using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that detects duplicate code patterns across the solution
/// </summary>
[McpServerToolType]
public class CodeCloneDetectionTool : ITool
{
    private readonly ILogger<CodeCloneDetectionTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "csharp_code_clone_detection";
    public string Description => "Detect duplicate code patterns across the solution";

    public CodeCloneDetectionTool(
        ILogger<CodeCloneDetectionTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "csharp_code_clone_detection")]
    [Description(@"Detect duplicate code patterns across the solution for refactoring opportunities.
Returns: Groups of similar code blocks with similarity scores and locations.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if analysis fails.
Use cases: Identifying refactoring opportunities, reducing code duplication, improving maintainability.
AI benefit: Reveals hidden duplication patterns that are hard to spot manually.")]
    public async Task<object> ExecuteAsync(CodeCloneDetectionParams parameters, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("CodeCloneDetection request: MinLines={MinLines}, MinTokens={MinTokens}, Similarity={Similarity}", 
            parameters.MinLines, parameters.MinTokens, parameters.SimilarityThreshold);
            
        try
        {
            // Get the solution
            var workspaces = _workspaceService.GetActiveWorkspaces();
            var workspace = workspaces.FirstOrDefault();
            if (workspace == null)
            {
                return CreateErrorResult(
                    ErrorCodes.WORKSPACE_NOT_LOADED,
                    "No workspace loaded",
                    new List<string>
                    {
                        "Use csharp_load_solution to load a solution first",
                        "Use csharp_load_project to load a project",
                        "Verify the solution path is correct"
                    },
                    parameters,
                    startTime);
            }

            var solution = workspace.Solution;
            _logger.LogInformation("Analyzing {ProjectCount} projects for code clones", 
                solution.Projects.Count());

            // Collect all code blocks
            var allCodeBlocks = await CollectCodeBlocksAsync(
                solution,
                parameters.MinLines,
                parameters.FilePattern,
                parameters.ExcludePattern,
                cancellationToken);

            if (!allCodeBlocks.Any())
            {
                return new CodeCloneDetectionResult
                {
                    Success = true,
                    Message = "No code blocks found matching the criteria",
                    Query = CreateQueryInfo(parameters),
                    Summary = new SummaryInfo
                    {
                        TotalFound = 0,
                        Returned = 0,
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                    },
                    CloneGroups = new List<CloneGroup>(),
                    Analysis = new CloneAnalysis { TotalCloneGroups = 0, TotalDuplicateBlocks = 0 },
                    Insights = new List<string> { "No code blocks found - check your filters and minimum line count" },
                    Actions = new List<NextAction>(),
                    Meta = new ToolMetadata
                    {
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                    }
                };
            }

            _logger.LogDebug("Collected {BlockCount} code blocks for analysis", allCodeBlocks.Count);

            // Find clones
            var cloneGroups = FindClones(
                allCodeBlocks,
                parameters.SimilarityThreshold,
                parameters.MinTokens,
                parameters.CloneType);

            // Apply token management
            var response = TokenEstimator.CreateTokenAwareResponse(
                cloneGroups,
                groups => EstimateCloneGroupsTokens(groups),
                requestedMax: parameters.MaxGroups ?? 50,
                safetyLimit: TokenEstimator.DEFAULT_SAFETY_LIMIT,
                toolName: "csharp_code_clone_detection"
            );

            // Generate analysis
            var analysis = GenerateAnalysis(cloneGroups, allCodeBlocks.Count);

            // Generate insights
            var insights = GenerateInsights(analysis, parameters);
            if (response.WasTruncated)
            {
                insights.Insert(0, response.GetTruncationMessage());
            }

            // Generate next actions
            var nextActions = GenerateNextActions(parameters, analysis);
            if (response.WasTruncated)
            {
                nextActions.Insert(0, new NextAction
                {
                    Id = "get_all_clones",
                    Description = "Get all clone groups without truncation",
                    ToolName = "csharp_code_clone_detection",
                    Parameters = new
                    {
                        minLines = parameters.MinLines,
                        minTokens = parameters.MinTokens,
                        similarityThreshold = parameters.SimilarityThreshold,
                        maxGroups = cloneGroups.Count
                    },
                    Priority = "high"
                });
            }

            // Store full result if truncated
            string? resourceUri = null;
            if (response.WasTruncated && _resourceProvider != null)
            {
                resourceUri = _resourceProvider.StoreAnalysisResult(
                    "code-clones",
                    new { 
                        parameters = parameters,
                        cloneGroups = cloneGroups,
                        totalGroups = cloneGroups.Count,
                        analysis = analysis
                    },
                    $"All {cloneGroups.Count} clone groups found");
            }

            var result = new CodeCloneDetectionResult
            {
                Success = true,
                Message = response.WasTruncated 
                    ? $"Found {cloneGroups.Count} clone groups (showing {response.ReturnedCount})"
                    : $"Found {cloneGroups.Count} clone groups",
                Query = CreateQueryInfo(parameters),
                Summary = new SummaryInfo
                {
                    TotalFound = cloneGroups.Count,
                    Returned = response.ReturnedCount,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                },
                CloneGroups = response.Items,
                Analysis = analysis,
                Insights = insights,
                Actions = nextActions,
                ResourceUri = resourceUri,
                Meta = new ToolMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    Truncated = response.WasTruncated,
                    Tokens = response.EstimatedTokens
                }
            };

            _logger.LogInformation("Clone detection completed: Found {GroupCount} clone groups", 
                cloneGroups.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in CodeCloneDetection");
            return CreateErrorResult(
                ErrorCodes.INTERNAL_ERROR,
                $"Error: {ex.Message}",
                new List<string>
                {
                    "Check the server logs for detailed error information",
                    "Verify the solution is loaded correctly",
                    "Try with a smaller scope or higher thresholds"
                },
                parameters,
                startTime);
        }
    }

    private async Task<List<CodeBlock>> CollectCodeBlocksAsync(
        Solution solution,
        int minLines,
        string? filePattern,
        string? excludePattern,
        CancellationToken cancellationToken)
    {
        var codeBlocks = new List<CodeBlock>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                // Skip if doesn't match file pattern
                if (!MatchesFilePattern(document.FilePath, filePattern, excludePattern))
                    continue;

                try
                {
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    if (root == null) continue;

                    var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                    if (semanticModel == null) continue;

                    // Extract methods
                    var methods = root.DescendantNodes()
                        .OfType<MethodDeclarationSyntax>()
                        .Where(m => GetLineCount(m) >= minLines);

                    foreach (var method in methods)
                    {
                        var symbol = semanticModel.GetDeclaredSymbol(method);
                        if (symbol == null) continue;

                        codeBlocks.Add(new CodeBlock
                        {
                            Id = Guid.NewGuid().ToString(),
                            FilePath = document.FilePath ?? "<unknown>",
                            ProjectName = project.Name,
                            StartLine = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            EndLine = method.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                            LineCount = GetLineCount(method),
                            TokenCount = GetTokenCount(method),
                            MethodName = symbol.ToDisplayString(),
                            Code = method.ToFullString(),
                            NormalizedCode = NormalizeCode(method),
                            Hash = ComputeHash(NormalizeCode(method)),
                            SyntaxKind = "Method",
                            Complexity = CalculateComplexity(method)
                        });
                    }

                    // Extract constructors
                    var constructors = root.DescendantNodes()
                        .OfType<ConstructorDeclarationSyntax>()
                        .Where(c => GetLineCount(c) >= minLines);

                    foreach (var constructor in constructors)
                    {
                        var symbol = semanticModel.GetDeclaredSymbol(constructor);
                        if (symbol == null) continue;

                        codeBlocks.Add(new CodeBlock
                        {
                            Id = Guid.NewGuid().ToString(),
                            FilePath = document.FilePath ?? "<unknown>",
                            ProjectName = project.Name,
                            StartLine = constructor.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            EndLine = constructor.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                            LineCount = GetLineCount(constructor),
                            TokenCount = GetTokenCount(constructor),
                            MethodName = symbol.ToDisplayString(),
                            Code = constructor.ToFullString(),
                            NormalizedCode = NormalizeCode(constructor),
                            Hash = ComputeHash(NormalizeCode(constructor)),
                            SyntaxKind = "Constructor",
                            Complexity = CalculateComplexity(constructor)
                        });
                    }

                    // Extract property getters/setters if substantial
                    var properties = root.DescendantNodes()
                        .OfType<PropertyDeclarationSyntax>()
                        .Where(p => p.AccessorList != null);

                    foreach (var property in properties)
                    {
                        foreach (var accessor in property.AccessorList!.Accessors)
                        {
                            if (accessor.Body != null && GetLineCount(accessor) >= minLines)
                            {
                                var symbol = semanticModel.GetDeclaredSymbol(property);
                                if (symbol == null) continue;

                                codeBlocks.Add(new CodeBlock
                                {
                                    Id = Guid.NewGuid().ToString(),
                                    FilePath = document.FilePath ?? "<unknown>",
                                    ProjectName = project.Name,
                                    StartLine = accessor.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                                    EndLine = accessor.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                                    LineCount = GetLineCount(accessor),
                                    TokenCount = GetTokenCount(accessor),
                                    MethodName = $"{symbol.ToDisplayString()}.{accessor.Keyword.Text}",
                                    Code = accessor.ToFullString(),
                                    NormalizedCode = NormalizeCode(accessor),
                                    Hash = ComputeHash(NormalizeCode(accessor)),
                                    SyntaxKind = $"Property{accessor.Keyword.Text.Capitalize()}",
                                    Complexity = CalculateComplexity(accessor)
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing document {FilePath}", document.FilePath);
                }
            }
        }

        return codeBlocks;
    }

    private List<CloneGroup> FindClones(
        List<CodeBlock> codeBlocks,
        double similarityThreshold,
        int minTokens,
        string cloneType)
    {
        var cloneGroups = new List<CloneGroup>();
        var processed = new HashSet<string>();

        // Group by hash for Type-1 clones (exact duplicates)
        if (cloneType == "type1" || cloneType == "all")
        {
            var exactGroups = codeBlocks
                .Where(b => b.TokenCount >= minTokens)
                .GroupBy(b => b.Hash)
                .Where(g => g.Count() > 1);

            foreach (var group in exactGroups)
            {
                var blocks = group.ToList();
                var cloneGroup = new CloneGroup
                {
                    Id = Guid.NewGuid().ToString(),
                    CloneType = "Type-1 (Exact)",
                    Blocks = blocks.Select(b => CreateCloneInstance(b, 1.0)).ToList(),
                    AverageSimilarity = 1.0,
                    TotalLines = blocks.Sum(b => b.LineCount),
                    PotentialSavings = (blocks.Count - 1) * blocks.First().LineCount
                };
                
                cloneGroups.Add(cloneGroup);
                blocks.ForEach(b => processed.Add(b.Id));
            }
        }

        // Find Type-2 and Type-3 clones (similar with variations)
        if (cloneType == "type2" || cloneType == "type3" || cloneType == "all")
        {
            var unprocessed = codeBlocks
                .Where(b => !processed.Contains(b.Id) && b.TokenCount >= minTokens)
                .ToList();

            for (int i = 0; i < unprocessed.Count; i++)
            {
                if (processed.Contains(unprocessed[i].Id)) continue;

                var similar = new List<(CodeBlock block, double similarity)>
                {
                    (unprocessed[i], 1.0)
                };

                for (int j = i + 1; j < unprocessed.Count; j++)
                {
                    if (processed.Contains(unprocessed[j].Id)) continue;

                    var similarity = CalculateSimilarity(
                        unprocessed[i].NormalizedCode,
                        unprocessed[j].NormalizedCode,
                        cloneType == "type3");

                    if (similarity >= similarityThreshold)
                    {
                        similar.Add((unprocessed[j], similarity));
                    }
                }

                if (similar.Count > 1)
                {
                    var cloneGroup = new CloneGroup
                    {
                        Id = Guid.NewGuid().ToString(),
                        CloneType = DetermineCloneType(similar.Average(s => s.similarity)),
                        Blocks = similar.Select(s => CreateCloneInstance(s.block, s.similarity)).ToList(),
                        AverageSimilarity = similar.Average(s => s.similarity),
                        TotalLines = similar.Sum(s => s.block.LineCount),
                        PotentialSavings = (similar.Count - 1) * similar[0].block.LineCount
                    };

                    cloneGroups.Add(cloneGroup);
                    similar.ForEach(s => processed.Add(s.block.Id));
                }
            }
        }

        // Sort by potential savings
        return cloneGroups.OrderByDescending(g => g.PotentialSavings).ToList();
    }

    private CloneInstance CreateCloneInstance(CodeBlock block, double similarity)
    {
        return new CloneInstance
        {
            FilePath = block.FilePath,
            ProjectName = block.ProjectName,
            MethodName = block.MethodName,
            Location = new LocationInfo
            {
                FilePath = block.FilePath,
                Line = block.StartLine,
                Column = 1,
                EndLine = block.EndLine,
                EndColumn = 1
            },
            LineCount = block.LineCount,
            TokenCount = block.TokenCount,
            Similarity = similarity,
            CodeSnippet = GetCodeSnippet(block.Code, 5),
            Complexity = block.Complexity
        };
    }

    private string GetCodeSnippet(string code, int maxLines)
    {
        var lines = code.Split('\n').Take(maxLines);
        var snippet = string.Join('\n', lines);
        if (code.Split('\n').Length > maxLines)
        {
            snippet += "\n...";
        }
        return snippet.Trim();
    }

    private double CalculateSimilarity(string code1, string code2, bool allowGaps)
    {
        if (code1 == code2) return 1.0;

        // Tokenize
        var tokens1 = TokenizeCode(code1);
        var tokens2 = TokenizeCode(code2);

        if (!tokens1.Any() || !tokens2.Any()) return 0.0;

        // Calculate token-based similarity
        if (allowGaps)
        {
            // Type-3: Allow gaps/modifications
            return CalculateLCS(tokens1, tokens2) / (double)Math.Max(tokens1.Count, tokens2.Count);
        }
        else
        {
            // Type-2: Exact structure, different identifiers
            return CalculateTokenSimilarity(tokens1, tokens2);
        }
    }

    private List<string> TokenizeCode(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = tree.GetRoot();
        
        return root.DescendantTokens()
            .Where(t => !t.IsKind(SyntaxKind.EndOfFileToken))
            .Select(t => t.Kind().ToString())
            .ToList();
    }

    private double CalculateTokenSimilarity(List<string> tokens1, List<string> tokens2)
    {
        if (tokens1.Count != tokens2.Count) return 0.0;

        int matches = 0;
        for (int i = 0; i < tokens1.Count; i++)
        {
            if (tokens1[i] == tokens2[i])
                matches++;
        }

        return (double)matches / tokens1.Count;
    }

    private int CalculateLCS(List<string> tokens1, List<string> tokens2)
    {
        int[,] lcs = new int[tokens1.Count + 1, tokens2.Count + 1];

        for (int i = 1; i <= tokens1.Count; i++)
        {
            for (int j = 1; j <= tokens2.Count; j++)
            {
                if (tokens1[i - 1] == tokens2[j - 1])
                    lcs[i, j] = lcs[i - 1, j - 1] + 1;
                else
                    lcs[i, j] = Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
            }
        }

        return lcs[tokens1.Count, tokens2.Count];
    }

    private string NormalizeCode(SyntaxNode node)
    {
        // Remove whitespace and normalize identifiers for clone detection
        var normalizer = new CodeNormalizer();
        var normalized = normalizer.Visit(node);
        return normalized?.ToFullString() ?? "";
    }

    private class CodeNormalizer : CSharpSyntaxRewriter
    {
        private int _varCounter = 0;
        private readonly Dictionary<string, string> _identifierMap = new();

        public override SyntaxToken VisitToken(SyntaxToken token)
        {
            // Normalize identifiers
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                var text = token.Text;
                if (!_identifierMap.ContainsKey(text))
                {
                    _identifierMap[text] = $"var{_varCounter++}";
                }
                return SyntaxFactory.Identifier(_identifierMap[text]);
            }

            // Remove trivia (whitespace, comments)
            return token.WithoutTrivia();
        }
    }

    private string ComputeHash(string text)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    private int GetLineCount(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
    }

    private int GetTokenCount(SyntaxNode node)
    {
        return node.DescendantTokens()
            .Count(t => !t.IsKind(SyntaxKind.EndOfFileToken));
    }

    private int CalculateComplexity(SyntaxNode node)
    {
        // Simple cyclomatic complexity calculation
        int complexity = 1;

        complexity += node.DescendantNodes().Count(n => n is IfStatementSyntax);
        complexity += node.DescendantNodes().Count(n => n is WhileStatementSyntax);
        complexity += node.DescendantNodes().Count(n => n is ForStatementSyntax);
        complexity += node.DescendantNodes().Count(n => n is ForEachStatementSyntax);
        complexity += node.DescendantNodes().Count(n => n is CaseSwitchLabelSyntax);
        complexity += node.DescendantNodes().Count(n => n is ConditionalExpressionSyntax);
        complexity += node.DescendantNodes().Count(n => n is BinaryExpressionSyntax be && 
            (be.IsKind(SyntaxKind.LogicalAndExpression) || be.IsKind(SyntaxKind.LogicalOrExpression)));

        return complexity;
    }

    private string DetermineCloneType(double similarity)
    {
        if (similarity >= 1.0) return "Type-1 (Exact)";
        if (similarity >= 0.95) return "Type-2 (Renamed)";
        if (similarity >= 0.70) return "Type-3 (Modified)";
        return "Type-4 (Semantic)";
    }

    private bool MatchesFilePattern(string? filePath, string? includePattern, string? excludePattern)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        // Skip test files by default unless explicitly included
        if (filePath.Contains("Test", StringComparison.OrdinalIgnoreCase) && 
            (includePattern == null || !includePattern.Contains("Test", StringComparison.OrdinalIgnoreCase)))
            return false;

        return true;
    }

    private int EstimateCloneGroupsTokens(List<CloneGroup> groups)
    {
        return TokenEstimator.EstimateCollection(
            groups,
            group => {
                var tokens = 200; // Base for group structure
                tokens += group.Blocks.Sum(b => 
                    TokenEstimator.EstimateString(b.FilePath) +
                    TokenEstimator.EstimateString(b.CodeSnippet) +
                    100 // Metadata
                );
                return tokens;
            },
            baseTokens: TokenEstimator.BASE_RESPONSE_TOKENS
        );
    }

    private CloneAnalysis GenerateAnalysis(List<CloneGroup> cloneGroups, int totalBlocks)
    {
        var analysis = new CloneAnalysis
        {
            TotalCloneGroups = cloneGroups.Count,
            TotalDuplicateBlocks = cloneGroups.Sum(g => g.Blocks.Count),
            TotalDuplicateLines = cloneGroups.Sum(g => g.TotalLines),
            AverageSimilarity = cloneGroups.Any() ? cloneGroups.Average(g => g.AverageSimilarity) : 0,
            LargestCloneGroup = cloneGroups.OrderByDescending(g => g.Blocks.Count).FirstOrDefault()?.Blocks.Count ?? 0,
            TotalPotentialSavings = cloneGroups.Sum(g => g.PotentialSavings),
            CloneDistribution = cloneGroups
                .GroupBy(g => g.CloneType)
                .ToDictionary(g => g.Key, g => g.Count()),
            ProjectDistribution = cloneGroups
                .SelectMany(g => g.Blocks.Select(b => b.ProjectName))
                .GroupBy(p => p)
                .ToDictionary(g => g.Key, g => g.Count())
        };

        // Calculate duplication percentage
        analysis.DuplicationPercentage = totalBlocks > 0 
            ? (double)analysis.TotalDuplicateBlocks / totalBlocks * 100 
            : 0;

        return analysis;
    }

    private List<string> GenerateInsights(CloneAnalysis analysis, CodeCloneDetectionParams parameters)
    {
        var insights = new List<string>();

        if (analysis.TotalCloneGroups == 0)
        {
            insights.Add("No code clones detected - codebase has good uniqueness");
        }
        else
        {
            insights.Add($"Found {analysis.TotalCloneGroups} groups of duplicate code affecting {analysis.TotalDuplicateBlocks} blocks");
        }

        if (analysis.DuplicationPercentage > 20)
        {
            insights.Add($"⚠️ High duplication rate: {analysis.DuplicationPercentage:F1}% of code blocks are duplicates");
        }

        if (analysis.TotalPotentialSavings > 100)
        {
            insights.Add($"Potential to save {analysis.TotalPotentialSavings} lines through refactoring");
        }

        if (analysis.LargestCloneGroup > 5)
        {
            insights.Add($"Largest clone group has {analysis.LargestCloneGroup} instances - strong candidate for extraction");
        }

        var type1Count = analysis.CloneDistribution?.GetValueOrDefault("Type-1 (Exact)") ?? 0;
        if (type1Count > 0)
        {
            insights.Add($"{type1Count} exact duplicates found - these are easiest to refactor");
        }

        if (analysis.ProjectDistribution?.Count > 1)
        {
            insights.Add($"Clones span {analysis.ProjectDistribution.Count} projects - consider shared library");
        }

        return insights;
    }

    private List<NextAction> GenerateNextActions(CodeCloneDetectionParams parameters, CloneAnalysis analysis)
    {
        var actions = new List<NextAction>();

        if (analysis.TotalCloneGroups > 0)
        {
            // Suggest extracting the most duplicated code
            actions.Add(new NextAction
            {
                Id = "extract_common_code",
                Description = "Extract the most duplicated code into shared methods",
                ToolName = "csharp_extract_method",
                Parameters = new { },
                Priority = "high"
            });

            // Suggest finding references to understand usage
            actions.Add(new NextAction
            {
                Id = "analyze_usage",
                Description = "Find all references to understand clone usage patterns",
                ToolName = "csharp_find_all_references",
                Parameters = new { },
                Priority = "medium"
            });
        }

        // Suggest different detection parameters
        if (parameters.SimilarityThreshold > 0.7)
        {
            actions.Add(new NextAction
            {
                Id = "find_more_clones",
                Description = "Find more clones with lower similarity threshold",
                ToolName = "csharp_code_clone_detection",
                Parameters = new
                {
                    minLines = parameters.MinLines,
                    similarityThreshold = 0.7,
                    cloneType = "all"
                },
                Priority = "low"
            });
        }

        // Suggest analyzing specific high-duplication projects
        if (analysis.ProjectDistribution?.Any(p => p.Value > 10) == true)
        {
            var highDupProject = analysis.ProjectDistribution.OrderByDescending(p => p.Value).First();
            actions.Add(new NextAction
            {
                Id = "analyze_project",
                Description = $"Analyze '{highDupProject.Key}' project specifically (high duplication)",
                ToolName = "csharp_code_clone_detection",
                Parameters = new
                {
                    filePattern = $"**/{highDupProject.Key}/**/*.cs",
                    minLines = parameters.MinLines
                },
                Priority = "medium"
            });
        }

        return actions;
    }

    private QueryInfo CreateQueryInfo(CodeCloneDetectionParams parameters)
    {
        return new QueryInfo
        {
            AdditionalParams = new Dictionary<string, object>
            {
                ["minLines"] = parameters.MinLines,
                ["minTokens"] = parameters.MinTokens,
                ["similarityThreshold"] = parameters.SimilarityThreshold,
                ["cloneType"] = parameters.CloneType
            }
        };
    }

    private CodeCloneDetectionResult CreateErrorResult(
        string errorCode,
        string message,
        List<string> recoverySteps,
        CodeCloneDetectionParams parameters,
        DateTime startTime)
    {
        return new CodeCloneDetectionResult
        {
            Success = false,
            Message = message,
            Error = new ErrorInfo
            {
                Code = errorCode,
                Recovery = new RecoveryInfo
                {
                    Steps = recoverySteps
                }
            },
            Query = CreateQueryInfo(parameters),
            Meta = new ToolMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
    }

    private class CodeBlock
    {
        public string Id { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public int LineCount { get; set; }
        public int TokenCount { get; set; }
        public string MethodName { get; set; } = "";
        public string Code { get; set; } = "";
        public string NormalizedCode { get; set; } = "";
        public string Hash { get; set; } = "";
        public string SyntaxKind { get; set; } = "";
        public int Complexity { get; set; }
    }
}

public class CodeCloneDetectionParams
{
    [JsonPropertyName("minLines")]
    [Description("Minimum number of lines for a code block to be considered (default: 6)")]
    public int MinLines { get; set; } = 6;

    [JsonPropertyName("minTokens")]
    [Description("Minimum number of tokens for a code block to be considered (default: 50)")]
    public int MinTokens { get; set; } = 50;
    
    [JsonPropertyName("similarityThreshold")]
    [Description("Minimum similarity score to consider as clone (0.0-1.0, default: 0.8)")]
    public double SimilarityThreshold { get; set; } = 0.8;
    
    [JsonPropertyName("cloneType")]
    [Description("Type of clones to detect: 'type1' (exact), 'type2' (renamed), 'type3' (modified), 'all' (default: 'all')")]
    public string CloneType { get; set; } = "all";
    
    [JsonPropertyName("filePattern")]
    [Description("Glob pattern to include files (e.g., '*.cs', 'src/**/*.cs')")]
    public string? FilePattern { get; set; }
    
    [JsonPropertyName("excludePattern")]
    [Description("Glob pattern to exclude files (e.g., '*.Designer.cs', '**/bin/**')")]
    public string? ExcludePattern { get; set; }
    
    [JsonPropertyName("maxGroups")]
    [Description("Maximum number of clone groups to return (default: 50)")]
    public int? MaxGroups { get; set; }
}

public class CodeCloneDetectionResult : ToolResultBase
{
    public override string Operation => "csharp_code_clone_detection";
    
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }
    
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
    
    [JsonPropertyName("cloneType")]
    public required string CloneType { get; set; }
    
    [JsonPropertyName("blocks")]
    public List<CloneInstance> Blocks { get; set; } = new();
    
    [JsonPropertyName("averageSimilarity")]
    public double AverageSimilarity { get; set; }
    
    [JsonPropertyName("totalLines")]
    public int TotalLines { get; set; }
    
    [JsonPropertyName("potentialSavings")]
    public int PotentialSavings { get; set; }
}

public class CloneInstance
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }
    
    [JsonPropertyName("projectName")]
    public required string ProjectName { get; set; }
    
    [JsonPropertyName("methodName")]
    public required string MethodName { get; set; }
    
    [JsonPropertyName("location")]
    public required LocationInfo Location { get; set; }
    
    [JsonPropertyName("lineCount")]
    public int LineCount { get; set; }
    
    [JsonPropertyName("tokenCount")]
    public int TokenCount { get; set; }
    
    [JsonPropertyName("similarity")]
    public double Similarity { get; set; }
    
    [JsonPropertyName("codeSnippet")]
    public required string CodeSnippet { get; set; }
    
    [JsonPropertyName("complexity")]
    public int Complexity { get; set; }
}

public class CloneAnalysis
{
    [JsonPropertyName("totalCloneGroups")]
    public int TotalCloneGroups { get; set; }
    
    [JsonPropertyName("totalDuplicateBlocks")]
    public int TotalDuplicateBlocks { get; set; }
    
    [JsonPropertyName("totalDuplicateLines")]
    public int TotalDuplicateLines { get; set; }
    
    [JsonPropertyName("averageSimilarity")]
    public double AverageSimilarity { get; set; }
    
    [JsonPropertyName("largestCloneGroup")]
    public int LargestCloneGroup { get; set; }
    
    [JsonPropertyName("totalPotentialSavings")]
    public int TotalPotentialSavings { get; set; }
    
    [JsonPropertyName("duplicationPercentage")]
    public double DuplicationPercentage { get; set; }
    
    [JsonPropertyName("cloneDistribution")]
    public Dictionary<string, int>? CloneDistribution { get; set; }
    
    [JsonPropertyName("projectDistribution")]
    public Dictionary<string, int>? ProjectDistribution { get; set; }
}

// Extension method
public static class StringExtensions
{
    public static string Capitalize(this string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;
        return char.ToUpper(input[0]) + input.Substring(1).ToLower();
    }
}