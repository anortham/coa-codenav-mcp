using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that detects duplicate code patterns across the solution
/// </summary>
public class CodeCloneDetectionTool : McpToolBase<CodeCloneDetectionParams, CodeCloneDetectionResult>
{
    private readonly ILogger<CodeCloneDetectionTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly CodeCloneResponseBuilder _responseBuilder;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => "csharp_code_clone_detection";
    public override string Description => "Detect duplicate code patterns across the solution to identify refactoring opportunities. Finds similar code blocks that could be consolidated to reduce technical debt.\n\nEffective usage strategies:\n\u2022 Start broad: Use default settings to get overview, then narrow down based on results\n\u2022 Focus on high-impact: Use similarityThreshold: 0.9 to find exact duplicates first\n\u2022 Size filtering: Adjust minLines (6-20) based on project needs - larger for significant methods\n\u2022 Scope control: Use filePattern: \"src/**/*.cs\" to limit analysis to source code\n\u2022 Performance: Results auto-truncated at 10,000 tokens - use filtering for large codebases\n\nTypical workflow: Run broad scan \u2192 Review high-similarity groups \u2192 Extract common patterns \u2192 Verify with targeted re-scan";

    public CodeCloneDetectionTool(
        ILogger<CodeCloneDetectionTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        CodeCloneResponseBuilder responseBuilder,
        ITokenEstimator tokenEstimator,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _responseBuilder = responseBuilder;
        _tokenEstimator = tokenEstimator;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<CodeCloneDetectionResult> ExecuteInternalAsync(
        CodeCloneDetectionParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("CodeCloneDetection request: MinLines={MinLines}, SimilarityThreshold={Threshold}", 
            parameters.MinLines, parameters.SimilarityThreshold);
        
        // Add timeout to prevent hanging
        var timeoutSeconds = parameters.TimeoutSeconds ?? 30; // Default to 30 seconds
        if (timeoutSeconds > 300) timeoutSeconds = 300; // Cap at 5 minutes
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var workspace = _workspaceService.GetActiveWorkspaces().FirstOrDefault();
        if (workspace == null)
        {
            return new CodeCloneDetectionResult
            {
                Success = false,
                Message = "No workspace loaded",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.WORKSPACE_NOT_LOADED,
                    Message = "No workspace loaded",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Load a solution or project first",
                            "Use csharp_load_solution or csharp_load_project"
                        }
                    }
                }
            };
        }

        try
        {
            var solution = workspace.Solution;
            var codeBlocks = new List<CodeBlock>();

            // Extract code blocks from all documents
            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    if (!ShouldProcessDocument(document, parameters))
                        continue;

                    var root = await document.GetSyntaxRootAsync(linkedCts.Token);
                    if (root == null) continue;

                    ExtractCodeBlocks(root, document, codeBlocks, parameters);
                }
            }

            // Find similar code blocks
            var cloneGroups = FindCloneGroups(codeBlocks, parameters, linkedCts.Token);
            
            // Store all groups before limiting
            var allGroups = cloneGroups.ToList();
            
            // Apply framework token optimization
            var tokenLimitApplied = false;
            var estimatedTokens = _tokenEstimator.EstimateObject(cloneGroups);
            if (estimatedTokens > 10000) // Clone detection can return massive results
            {
                // Use framework's progressive reduction
                var optimizedGroups = _tokenEstimator.ApplyProgressiveReduction(
                    cloneGroups.ToList(),
                    group => _tokenEstimator.EstimateObject(group),
                    10000,
                    new[] { 20, 15, 10, 5, 3 } // Progressive reduction steps for clone groups
                );
                cloneGroups = optimizedGroups;
                tokenLimitApplied = true;
                
                _logger.LogWarning("Token optimization applied to clone groups: reducing from {Total} to {Safe}", 
                    allGroups.Count, cloneGroups.Count);
            }
            
            var wasTruncated = tokenLimitApplied;
            
            // Apply additional MaxGroups limit if specified
            if (parameters.MaxGroups.HasValue && cloneGroups.Count > parameters.MaxGroups.Value)
            {
                cloneGroups = cloneGroups.Take(parameters.MaxGroups.Value).ToList();
                wasTruncated = true;
            }

            // Store full results as resource if truncated
            string? resourceUri = null;
            if (wasTruncated && _resourceProvider != null)
            {
                var fullData = new
                {
                    totalGroups = allGroups.Count,
                    totalClones = allGroups.Sum(g => g.Clones?.Count ?? 0),
                    totalBlocksAnalyzed = codeBlocks.Count,
                    averageSimilarity = allGroups.Any() ? allGroups.Average(g => g.SimilarityScore) : 0,
                    largestGroupSize = allGroups.Any() ? allGroups.Max(g => g.Clones?.Count ?? 0) : 0,
                    allCloneGroups = allGroups,
                    parameters = new { parameters.MinLines, parameters.MinTokens, parameters.SimilarityThreshold, parameters.CloneType },
                    executionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                };
                
                resourceUri = _resourceProvider.StoreAnalysisResult(
                    "code-clone-detection",
                    fullData,
                    $"All {allGroups.Count} clone groups with {allGroups.Sum(g => g.Clones?.Count ?? 0)} total clones"
                );
                
                _logger.LogDebug("Stored full clone detection results as resource: {ResourceUri}", resourceUri);
            }

            var insights = GenerateInsights(cloneGroups, codeBlocks.Count);
            
            // Add enhanced truncation messaging if needed
            if (wasTruncated)
            {
                insights.InsertRange(0, GenerateEnhancedTruncationInsights(
                    cloneGroups.Count, allGroups.Count, tokenLimitApplied, 
                    codeBlocks.Count, parameters));
            }
            
            var actions = GenerateNextActions(cloneGroups, parameters, wasTruncated, allGroups.Count);

            var completeResult = new CodeCloneDetectionResult
            {
                Success = true,
                Message = wasTruncated 
                    ? $"Found {allGroups.Count} clone group(s) from {codeBlocks.Count} code blocks analyzed (showing {cloneGroups.Count})"
                    : $"Found {cloneGroups.Count} clone group(s) from {codeBlocks.Count} code blocks analyzed",
                Summary = new SummaryInfo
                {
                    TotalFound = allGroups.Sum(g => g.Clones?.Count ?? 0),
                    Returned = cloneGroups.Sum(g => g.Clones?.Count ?? 0),
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                },
                CloneGroups = cloneGroups,
                Analysis = new CloneAnalysis
                {
                    TotalGroups = allGroups.Count,
                    TotalClones = allGroups.Sum(g => g.Clones?.Count ?? 0),
                    AverageSimilarity = allGroups.Any() ? allGroups.Average(g => g.SimilarityScore) : 0,
                    LargestGroupSize = allGroups.Any() ? allGroups.Max(g => g.Clones?.Count ?? 0) : 0
                },
                ResourceUri = resourceUri,
                Insights = insights,
                Actions = actions,
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    Truncated = wasTruncated
                }
            };

            // Use ResponseBuilder for token optimization and AI-friendly formatting
            var context = new COA.Mcp.Framework.TokenOptimization.ResponseBuilders.ResponseContext
            {
                ResponseMode = "optimized",
                TokenLimit = 10000,
                ToolName = Name
            };

            return await _responseBuilder.BuildResponseAsync(completeResult, context);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("CodeCloneDetection operation timed out or was cancelled");
            return new CodeCloneDetectionResult
            {
                Success = false,
                Message = $"Operation timed out after {parameters.TimeoutSeconds ?? 30} seconds. Try with smaller scope or fewer files, or increase timeoutSeconds parameter (max: 300).",
                Error = new ErrorInfo
                {
                    Code = "TIMEOUT",
                    Message = $"Operation timed out after {parameters.TimeoutSeconds ?? 30} seconds",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Try with a smaller minLines value",
                            "Use filePattern to limit scope",
                            "Reduce similarityThreshold for faster matching",
                            "Increase timeoutSeconds parameter (e.g., timeoutSeconds: 120 for 2 minutes, max: 300)"
                        }
                    }
                },
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in CodeCloneDetection");
            return new CodeCloneDetectionResult
            {
                Success = false,
                Message = $"Error: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Message = $"Error: {ex.Message}"
                },
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                }
            };
        }
    }

    private bool ShouldProcessDocument(Document document, CodeCloneDetectionParams parameters)
    {
        if (!string.IsNullOrEmpty(parameters.FilePattern))
        {
            var fileName = Path.GetFileName(document.FilePath ?? document.Name);
            if (!IsGlobMatch(fileName, parameters.FilePattern))
                return false;
        }

        if (!string.IsNullOrEmpty(parameters.ExcludePattern))
        {
            var fileName = Path.GetFileName(document.FilePath ?? document.Name);
            if (IsGlobMatch(fileName, parameters.ExcludePattern))
                return false;
        }

        return Path.GetExtension(document.Name).Equals(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsGlobMatch(string fileName, string pattern)
    {
        var regex = "^" + pattern.Replace("*", ".*").Replace("?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(fileName, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private void ExtractCodeBlocks(SyntaxNode root, Document document, List<CodeBlock> codeBlocks, CodeCloneDetectionParams parameters)
    {
        // Extract methods as code blocks
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var lineCount = GetLineCount(method);
            if (lineCount >= parameters.MinLines)
            {
                var location = method.GetLocation();
                var lineSpan = location.GetLineSpan();
                
                codeBlocks.Add(new CodeBlock
                {
                    Id = Guid.NewGuid().ToString(),
                    Content = method.ToString(),
                    NormalizedContent = NormalizeContent(method.ToString()),
                    Hash = ComputeHash(method.ToString()),
                    Location = new LocationInfo
                    {
                        FilePath = lineSpan.Path,
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1,
                        EndLine = lineSpan.EndLinePosition.Line + 1,
                        EndColumn = lineSpan.EndLinePosition.Character + 1
                    },
                    LineCount = lineCount,
                    TokenCount = method.DescendantTokens().Count(),
                    ContainingType = method.Parent is TypeDeclarationSyntax parent ? parent.Identifier.ValueText : "",
                    Name = method.Identifier.ValueText,
                    Kind = "Method"
                });
            }
        }
    }

    private int GetLineCount(SyntaxNode node)
    {
        var location = node.GetLocation();
        var lineSpan = location.GetLineSpan();
        return lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;
    }

    private string NormalizeContent(string content)
    {
        // Simple normalization: remove whitespace and standardize identifiers
        return System.Text.RegularExpressions.Regex.Replace(content, @"\s+", " ")
            .Replace("{", " { ")
            .Replace("}", " } ")
            .Trim();
    }

    private string ComputeHash(string content)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }

    private List<CloneGroup> FindCloneGroups(List<CodeBlock> codeBlocks, CodeCloneDetectionParams parameters, CancellationToken cancellationToken)
    {
        var groups = new List<CloneGroup>();
        var processed = new HashSet<string>();

        for (int i = 0; i < codeBlocks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (processed.Contains(codeBlocks[i].Id))
                continue;

            var group = new CloneGroup
            {
                Id = Guid.NewGuid().ToString(),
                Clones = new List<CodeBlock>(),
                SimilarityScore = 0
            };

            var baseBlock = codeBlocks[i];
            group.Clones.Add(baseBlock);
            processed.Add(baseBlock.Id);

            // Find similar blocks
            for (int j = i + 1; j < codeBlocks.Count; j++)
            {
                if (processed.Contains(codeBlocks[j].Id))
                    continue;

                var similarity = CalculateSimilarity(baseBlock, codeBlocks[j]);
                if (similarity >= parameters.SimilarityThreshold)
                {
                    group.Clones.Add(codeBlocks[j]);
                    processed.Add(codeBlocks[j].Id);
                    group.SimilarityScore = Math.Max(group.SimilarityScore, similarity);
                }
            }

            // Only include groups with multiple clones
            if (group.Clones.Count > 1)
            {
                groups.Add(group);
            }
        }

        return groups.OrderByDescending(g => g.SimilarityScore).ToList();
    }

    private double CalculateSimilarity(CodeBlock block1, CodeBlock block2)
    {
        // Simple similarity based on normalized content
        if (block1.Hash == block2.Hash)
            return 1.0; // Exact match

        // Levenshtein distance-based similarity (simplified)
        var content1 = block1.NormalizedContent;
        var content2 = block2.NormalizedContent;

        if (content1.Length == 0 && content2.Length == 0)
            return 1.0;

        if (content1.Length == 0 || content2.Length == 0)
            return 0.0;
        
        // Skip expensive calculation for very large strings
        const int MAX_LENGTH = 5000;
        if (content1.Length > MAX_LENGTH || content2.Length > MAX_LENGTH)
        {
            // Use simpler heuristic for large strings
            return ComputeQuickSimilarity(content1, content2);
        }

        var distance = ComputeLevenshteinDistance(content1, content2);
        var maxLength = Math.Max(content1.Length, content2.Length);
        
        return 1.0 - (double)distance / maxLength;
    }

    private int ComputeLevenshteinDistance(string s1, string s2)
    {
        var matrix = new int[s1.Length + 1, s2.Length + 1];

        for (int i = 0; i <= s1.Length; i++)
            matrix[i, 0] = i;

        for (int j = 0; j <= s2.Length; j++)
            matrix[0, j] = j;

        for (int i = 1; i <= s1.Length; i++)
        {
            for (int j = 1; j <= s2.Length; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[s1.Length, s2.Length];
    }
    
    private double ComputeQuickSimilarity(string s1, string s2)
    {
        // Quick similarity based on common tokens
        var tokens1 = s1.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tokens2 = s2.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        
        if (tokens1.Count == 0 && tokens2.Count == 0)
            return 1.0;
        
        if (tokens1.Count == 0 || tokens2.Count == 0)
            return 0.0;
        
        var intersection = tokens1.Intersect(tokens2).Count();
        var union = tokens1.Union(tokens2).Count();
        
        return (double)intersection / union; // Jaccard similarity
    }

    private List<string> GenerateInsights(List<CloneGroup> groups, int totalBlocks)
    {
        var insights = new List<string>();

        if (groups.Count == 0)
        {
            insights.Add("✨ No significant code clones detected - codebase follows DRY principles well");
            insights.Add($"💡 Analyzed {totalBlocks} code blocks with current similarity threshold");
            return insights;
        }

        // Priority-based insights with smart categorization
        var totalClones = groups.Sum(g => g.Clones?.Count ?? 0);
        var duplicatedLines = groups.Sum(g => (g.Clones?.Count ?? 0) * (g.Clones?.FirstOrDefault()?.LineCount ?? 0));
        
        // Clone severity categorization
        var criticalGroups = groups.Count(g => g.SimilarityScore > 0.95 && (g.Clones?.Count ?? 0) > 3);
        var highSimilarity = groups.Count(g => g.SimilarityScore > 0.9);
        var largeDuplication = groups.Count(g => (g.Clones?.Count ?? 0) > 5);
        
        insights.Add($"🔍 Found {groups.Count} clone groups from {totalBlocks} code blocks analyzed");
        insights.Add($"📊 Impact: {duplicatedLines} duplicated lines across {totalClones} code instances");
        
        if (criticalGroups > 0)
        {
            insights.Add($"🚨 Critical: {criticalGroups} groups have >95% similarity with 4+ instances - immediate refactoring candidates");
        }
        else if (highSimilarity > 0)
        {
            insights.Add($"⚠️ High priority: {highSimilarity} groups with >90% similarity - strong refactoring opportunities");
        }
        
        if (largeDuplication > 0)
        {
            insights.Add($"📈 Widespread: {largeDuplication} groups have 5+ instances - consider shared utilities or base classes");
        }
        
        var largestGroup = groups.OrderByDescending(g => g.Clones?.Count ?? 0).FirstOrDefault();
        if (largestGroup != null && largestGroup.Clones?.Count > 3)
        {
            var firstClone = largestGroup.Clones.FirstOrDefault();
            var context = firstClone != null ? $" in {firstClone.ContainingType}.{firstClone.Name}" : "";
            insights.Add($"🎯 Largest group: {largestGroup.Clones.Count} instances{context} - highest impact target");
        }
        
        // Technical debt estimation
        if (duplicatedLines > 500)
        {
            var estimatedHours = duplicatedLines / 100; // Rough estimate: 100 lines per hour of refactoring
            insights.Add($"💰 Technical debt: ~{estimatedHours} hours of refactoring opportunity");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(List<CloneGroup> groups, CodeCloneDetectionParams parameters, bool wasTruncated, int totalGroups)
    {
        var actions = new List<AIAction>();
        var totalClones = groups.Sum(g => g.Clones?.Count ?? 0);

        // Enhanced filtering strategies for large result sets
        if (wasTruncated && totalGroups > 20)
        {
            // Focus on critical clones first
            actions.Add(new AIAction
            {
                Action = "csharp_code_clone_detection",
                Description = $"🎯 Focus on critical clones (>95% similarity, 4+ instances)",
                Parameters = new Dictionary<string, object>
                {
                    ["similarityThreshold"] = 0.95,
                    ["minLines"] = Math.Max(parameters.MinLines, 10),
                    ["maxGroups"] = 25
                },
                Priority = 98,
                Category = "filtering"
            });
            
            // Focus on large duplications
            actions.Add(new AIAction
            {
                Action = "csharp_code_clone_detection",
                Description = $"📏 Focus on significant duplications (20+ lines)",
                Parameters = new Dictionary<string, object>
                {
                    ["minLines"] = 20,
                    ["similarityThreshold"] = parameters.SimilarityThreshold,
                    ["maxGroups"] = 30
                },
                Priority = 96,
                Category = "filtering"
            });
            
            // Scope-based filtering
            actions.Add(new AIAction
            {
                Action = "csharp_code_clone_detection",
                Description = $"📂 Analyze specific area (use filePattern: \"YourFolder/**/*.cs\")",
                Parameters = new Dictionary<string, object>
                {
                    ["filePattern"] = "src/**/*.cs",
                    ["similarityThreshold"] = parameters.SimilarityThreshold,
                    ["minLines"] = parameters.MinLines
                },
                Priority = 94,
                Category = "filtering"
            });
        }
        else if (wasTruncated)
        {
            actions.Add(new AIAction
            {
                Action = "ReadMcpResourceTool",
                Description = $"📋 View all {totalGroups} clone groups (full results)",
                Parameters = new Dictionary<string, object>
                {
                    ["server"] = "codenav"
                },
                Priority = 95,
                Category = "navigation"
            });
        }

        // Smart refactoring actions based on clone analysis
        var topGroup = groups.OrderByDescending(g => g.SimilarityScore * (g.Clones?.Count ?? 0)).FirstOrDefault();
        if (topGroup?.Clones?.Any() == true)
        {
            var firstClone = topGroup.Clones.First();
            var fileName = Path.GetFileName(firstClone.Location.FilePath);
            
            // Priority refactoring target
            actions.Add(new AIAction
            {
                Action = "csharp_goto_definition",
                Description = $"🔍 Examine highest-impact clone: {fileName}:{firstClone.Location.Line} ({topGroup.SimilarityScore:P0} similarity)",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = firstClone.Location.FilePath,
                    ["line"] = firstClone.Location.Line,
                    ["column"] = firstClone.Location.Column
                },
                Priority = 92,
                Category = "navigation"
            });
            
            // Refactoring actions based on clone characteristics
            if (topGroup.SimilarityScore > 0.95)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_extract_method",
                    Description = $"🔧 Extract duplicate method from {firstClone.ContainingType}.{firstClone.Name}",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = firstClone.Location.FilePath,
                        ["startLine"] = firstClone.Location.Line,
                        ["endLine"] = firstClone.Location.EndLine,
                        ["methodName"] = $"Extract{firstClone.Name}Common"
                    },
                    Priority = 90,
                    Category = "refactoring"
                });
            }
            
            if (topGroup.Clones.Count > 3)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_generate_code",
                    Description = $"🏗️ Generate base class/interface for {topGroup.Clones.Count} similar implementations",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = firstClone.Location.FilePath,
                        ["line"] = firstClone.Location.Line,
                        ["column"] = firstClone.Location.Column,
                        ["generationType"] = "interface"
                    },
                    Priority = 88,
                    Category = "refactoring"
                });
            }
        }
        
        // Pattern analysis for multiple groups
        if (groups.Count > 5)
        {
            var criticalGroups = groups.Count(g => g.SimilarityScore > 0.95);
            if (criticalGroups > 2)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_solution_wide_find_replace",
                    Description = $"🔄 Standardize {criticalGroups} exact duplicate patterns with find-replace",
                    Parameters = new Dictionary<string, object>
                    {
                        ["useRegex"] = true,
                        ["preview"] = true
                    },
                    Priority = 86,
                    Category = "refactoring"
                });
            }
        }
        
        // Code quality assessment
        if (totalClones > 20)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_code_metrics",
                Description = $"📈 Analyze complexity metrics for duplicated areas",
                Parameters = new Dictionary<string, object>
                {
                    ["scope"] = "solution",
                    ["includeInherited"] = false
                },
                Priority = 75,
                Category = "analysis"
            });
        }

        return actions.OrderByDescending(a => a.Priority).ToList();
    }
    
    private List<string> GenerateEnhancedTruncationInsights(
        int displayedGroups, int totalGroups, bool tokenLimitApplied, 
        int totalBlocksAnalyzed, CodeCloneDetectionParams parameters)
    {
        var insights = new List<string>();
        
        // Clear explanation of why truncation happened
        if (tokenLimitApplied)
        {
            insights.Add($"🔄 Results auto-truncated to prevent context overflow (10,000 token safety limit)");
            insights.Add($"📊 Showing {displayedGroups} of {totalGroups} clone groups prioritized by impact (similarity × instances)");
        }
        else
        {
            insights.Add($"📋 Showing {displayedGroups} of {totalGroups} clone groups (maxGroups limit applied)");
        }
        
        // Analysis scope context
        insights.Add($"🔍 Analysis scope: {totalBlocksAnalyzed} code blocks, {parameters.MinLines}+ lines, {parameters.SimilarityThreshold:P0}+ similarity");
        
        // Strategic guidance for large result sets
        if (totalGroups > 50)
        {
            insights.Add($"💡 Large codebase detected - use filtering strategies to focus analysis:");
            insights.Add($"   • Increase similarityThreshold to 0.9+ for exact duplicates");
            insights.Add($"   • Increase minLines to 15+ for significant methods only");
            insights.Add($"   • Use filePattern to analyze specific areas first");
        }
        else if (totalGroups > 20)
        {
            insights.Add($"🎯 Medium complexity - consider focusing on highest-similarity groups first");
        }
        
        insights.Add($"💾 Full results available via ReadMcpResourceTool");
        
        return insights;
    }

}

/// <summary>
/// Parameters for CodeCloneDetection tool
/// </summary>
public class CodeCloneDetectionParams
{
    [JsonPropertyName("minLines")]
    [COA.Mcp.Framework.Attributes.Description("Minimum number of lines for a code block to be considered (default: 6)")]
    public int MinLines { get; set; } = 6;

    [JsonPropertyName("minTokens")]
    [COA.Mcp.Framework.Attributes.Description("Minimum number of tokens for a code block to be considered (default: 50)")]
    public int MinTokens { get; set; } = 50;

    [JsonPropertyName("similarityThreshold")]
    [COA.Mcp.Framework.Attributes.Description("Minimum similarity score to consider as clone (0.0-1.0, default: 0.8)")]
    public double SimilarityThreshold { get; set; } = 0.8;

    [JsonPropertyName("cloneType")]
    [COA.Mcp.Framework.Attributes.Description("Type of clones to detect: 'type1' (exact), 'type2' (renamed), 'type3' (modified), 'all' (default: 'all')")]
    public string CloneType { get; set; } = "all";

    [JsonPropertyName("filePattern")]
    [COA.Mcp.Framework.Attributes.Description("Glob pattern to include files (e.g., '*.cs', 'src/**/*.cs')")]
    public string? FilePattern { get; set; }

    [JsonPropertyName("excludePattern")]
    [COA.Mcp.Framework.Attributes.Description("Glob pattern to exclude files (e.g., '*.Designer.cs', '**/bin/**')")]
    public string? ExcludePattern { get; set; }

    [JsonPropertyName("maxGroups")]
    [COA.Mcp.Framework.Attributes.Description("Maximum number of clone groups to return (default: 50)")]
    public int? MaxGroups { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    [COA.Mcp.Framework.Attributes.Description("Timeout in seconds for the operation (default: 30, max: 300)")]
    public int? TimeoutSeconds { get; set; }
}

public class CodeCloneDetectionResult : ToolResultBase
{
    public override string Operation => "csharp_code_clone_detection";
    
    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("cloneGroups")]
    public List<CloneGroup>? CloneGroups { get; set; }

    [JsonPropertyName("analysis")]
    public CloneAnalysis? Analysis { get; set; }
}

public class CloneGroup
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("clones")]
    public List<CodeBlock>? Clones { get; set; }

    [JsonPropertyName("similarityScore")]
    public double SimilarityScore { get; set; }
}

public class CodeBlock
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("content")]
    public required string Content { get; set; }

    [JsonPropertyName("normalizedContent")]
    public required string NormalizedContent { get; set; }

    [JsonPropertyName("hash")]
    public required string Hash { get; set; }

    [JsonPropertyName("location")]
    public required LocationInfo Location { get; set; }

    [JsonPropertyName("lineCount")]
    public int LineCount { get; set; }

    [JsonPropertyName("tokenCount")]
    public int TokenCount { get; set; }

    [JsonPropertyName("containingType")]
    public required string ContainingType { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }
}

public class CloneAnalysis
{
    [JsonPropertyName("totalGroups")]
    public int TotalGroups { get; set; }

    [JsonPropertyName("totalClones")]
    public int TotalClones { get; set; }

    [JsonPropertyName("averageSimilarity")]
    public double AverageSimilarity { get; set; }

    [JsonPropertyName("largestGroupSize")]
    public int LargestGroupSize { get; set; }
}