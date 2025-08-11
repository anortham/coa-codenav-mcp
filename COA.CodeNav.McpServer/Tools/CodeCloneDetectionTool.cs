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
    public override string Description => @"Detect duplicate code patterns across the solution for refactoring opportunities.
Returns: Groups of similar code blocks with similarity scores and locations.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if analysis fails.
Use cases: Identifying refactoring opportunities, reducing code duplication, improving maintainability.
AI benefit: Reveals hidden duplication patterns that are hard to spot manually.";

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
            
            // Add truncation warning if needed
            if (wasTruncated)
            {
                if (tokenLimitApplied && parameters.MaxGroups.HasValue && cloneGroups.Count < parameters.MaxGroups.Value)
                {
                    insights.Insert(0, $"⚠️ Token optimization applied. Showing {cloneGroups.Count} of {allGroups.Count} clone groups to fit context window.");
                }
                else if (tokenLimitApplied)
                {
                    insights.Insert(0, $"⚠️ Token optimization applied. Showing {cloneGroups.Count} of {allGroups.Count} clone groups.");
                }
                else
                {
                    insights.Insert(0, $"⚠️ Showing {cloneGroups.Count} of {allGroups.Count} clone groups. Full results available via resource URI.");
                }
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
            insights.Add("No code clones detected - good code quality!");
            return insights;
        }

        insights.Add($"Found {groups.Count} clone groups from {totalBlocks} code blocks analyzed");

        var highSimilarity = groups.Count(g => g.SimilarityScore > 0.9);
        if (highSimilarity > 0)
        {
            insights.Add($"{highSimilarity} groups have very high similarity (>90%) - consider immediate refactoring");
        }

        var largestGroup = groups.OrderByDescending(g => g.Clones?.Count ?? 0).FirstOrDefault();
        if (largestGroup != null && largestGroup.Clones?.Count > 3)
        {
            insights.Add($"Largest clone group has {largestGroup.Clones.Count} instances - significant duplication");
        }

        var totalClones = groups.Sum(g => g.Clones?.Count ?? 0);
        var duplicatedLines = groups.Sum(g => (g.Clones?.Count ?? 0) * (g.Clones?.FirstOrDefault()?.LineCount ?? 0));
        insights.Add($"Estimated {duplicatedLines} duplicated lines of code across {totalClones} code blocks");

        return insights;
    }

    private List<AIAction> GenerateNextActions(List<CloneGroup> groups, CodeCloneDetectionParams parameters, bool wasTruncated, int totalGroups)
    {
        var actions = new List<AIAction>();

        // If truncated, offer to get all results
        if (wasTruncated)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_code_clone_detection",
                Description = $"Get all {totalGroups} clone groups",
                Parameters = new Dictionary<string, object>
                {
                    ["minLines"] = parameters.MinLines,
                    ["minTokens"] = parameters.MinTokens,
                    ["similarityThreshold"] = parameters.SimilarityThreshold,
                    ["maxGroups"] = Math.Min(totalGroups, 500)
                },
                Priority = 95,
                Category = "pagination"
            });
        }

        var topGroup = groups.FirstOrDefault();
        if (topGroup?.Clones?.Any() == true)
        {
            var firstClone = topGroup.Clones.First();
            actions.Add(new AIAction
            {
                Action = "csharp_extract_method",
                Description = $"Extract common logic from cloned code in '{firstClone.Name}'",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = firstClone.Location.FilePath,
                    ["startLine"] = firstClone.Location.Line,
                    ["endLine"] = firstClone.Location.EndLine
                },
                Priority = 90,
                Category = "refactoring"
            });

            // Suggest reviewing the clone group
            if (topGroup.Clones.Count > 1)
            {
                actions.Add(new AIAction
                {
                    Action = "review_clones",
                    Description = $"Review all {topGroup.Clones.Count} instances of this clone",
                    Parameters = new Dictionary<string, object>
                    {
                        ["cloneGroupId"] = topGroup.Id,
                        ["similarity"] = topGroup.SimilarityScore
                    },
                    Priority = 80,
                    Category = "analysis"
                });
            }
        }

        return actions;
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