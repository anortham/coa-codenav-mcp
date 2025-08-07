using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// Tool for finding all references to a symbol in the codebase
/// </summary>
public class FindAllReferencesTool : McpToolBase<FindAllReferencesParams, FindAllReferencesToolResult>
{
    private readonly ILogger<FindAllReferencesTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;
    
    private const int DEFAULT_MAX_RESULTS = 50;
    private const int TOKEN_SAFETY_LIMIT = 10000;
    private const int TOKENS_PER_REFERENCE = 100; // Estimate

    public override string Name => "csharp_find_all_references";
    public override string Description => "Find all references to a symbol at a given position in a file";

    public FindAllReferencesTool(
        ILogger<FindAllReferencesTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<FindAllReferencesToolResult> ExecuteInternalAsync(
        FindAllReferencesParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogInformation("Finding all references at {FilePath}:{Line}:{Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);

        // Get the document
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            return CreateErrorResult(
                ErrorCodes.DOCUMENT_NOT_FOUND,
                $"Document not found in workspace: {parameters.FilePath}",
                new[]
                {
                    "Ensure the file path is correct and absolute",
                    "Verify the solution/project containing this file is loaded",
                    "Use csharp_load_solution or csharp_load_project to load the containing project"
                },
                parameters,
                startTime);
        }

        // Calculate position
        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines.GetPosition(new LinePosition(parameters.Line - 1, parameters.Column - 1));

        // Get semantic model
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return CreateErrorResult(
                ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE,
                "Failed to get semantic model",
                new[]
                {
                    "Ensure the project is fully loaded and compiled",
                    "Check for compilation errors in the project",
                    "Try reloading the solution"
                },
                parameters,
                startTime);
        }

        // Find symbol at position
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            semanticModel, position, document.Project.Solution.Workspace, cancellationToken);
        
        if (symbol == null)
        {
            return CreateErrorResult(
                ErrorCodes.NO_SYMBOL_AT_POSITION,
                "No symbol found at the specified position",
                new[]
                {
                    "Verify the line and column numbers are correct (1-based)",
                    "Ensure the cursor is on a symbol (class, method, property, etc.)",
                    "Try adjusting the column position to the start of the symbol name"
                },
                parameters,
                startTime);
        }

        _logger.LogDebug("Found symbol: {SymbolName} ({SymbolKind})", symbol.Name, symbol.Kind);

        // Find all references
        var references = await SymbolFinder.FindReferencesAsync(
            symbol, 
            document.Project.Solution, 
            cancellationToken);

        var locations = new List<Models.ReferenceLocation>();

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                var refDoc = location.Document;
                var span = location.Location.SourceSpan;
                var lineSpan = (await refDoc.GetTextAsync(cancellationToken)).Lines.GetLinePositionSpan(span);

                locations.Add(new Models.ReferenceLocation
                {
                    FilePath = refDoc.FilePath ?? "<unknown>",
                    Line = lineSpan.Start.Line + 1,
                    Column = lineSpan.Start.Character + 1,
                    EndLine = lineSpan.End.Line + 1,
                    EndColumn = lineSpan.End.Character + 1,
                    Kind = location.Location.IsInSource ? "reference" : "metadata",
                    Text = (await refDoc.GetTextAsync(cancellationToken)).GetSubText(span).ToString()
                });
            }
        }

        _logger.LogInformation("Found {Count} references to {SymbolName}", locations.Count, symbol.Name);

        // Sort locations for consistent results
        var sortedLocations = locations.OrderBy(l => l.FilePath).ThenBy(l => l.Line).ToList();
        var totalCount = sortedLocations.Count;
        
        // Apply token management
        var maxResults = parameters.MaxResults ?? DEFAULT_MAX_RESULTS;
        var estimatedTokens = EstimateTokensForReferences(sortedLocations);
        var returnedLocations = sortedLocations;
        var wasTruncated = false;
        
        if (estimatedTokens > TOKEN_SAFETY_LIMIT)
        {
            // Calculate how many references we can safely return
            var safeCount = TOKEN_SAFETY_LIMIT / TOKENS_PER_REFERENCE;
            returnedLocations = sortedLocations.Take(Math.Min(safeCount, maxResults)).ToList();
            wasTruncated = true;
        }
        else if (sortedLocations.Count > maxResults)
        {
            returnedLocations = sortedLocations.Take(maxResults).ToList();
            wasTruncated = true;
        }
        
        // Generate insights
        var insights = GenerateInsights(symbol, sortedLocations, wasTruncated);
        
        // Calculate distribution
        var distribution = new ReferenceDistribution
        {
            ByFile = sortedLocations.GroupBy(l => l.FilePath)
                .ToDictionary(g => g.Key, g => g.Count()),
            ByKind = sortedLocations.GroupBy(l => l.Kind ?? "unknown")
                .ToDictionary(g => g.Key, g => g.Count())
        };
        
        // Store full results as a resource if truncated
        string? resourceUri = null;
        if (wasTruncated && _resourceProvider != null)
        {
            var fullData = new
            {
                symbol = symbol.ToDisplayString(),
                symbolKind = symbol.Kind.ToString(),
                totalReferences = totalCount,
                allLocations = sortedLocations,
                searchedFrom = new { parameters.FilePath, parameters.Line, parameters.Column }
            };
            
            resourceUri = _resourceProvider.StoreAnalysisResult(
                "find-all-references",
                fullData,
                $"All {totalCount} references to {symbol.Name}"
            );
            
            _logger.LogDebug("Stored full reference data as resource: {ResourceUri}", resourceUri);
        }
        
        // Generate next actions
        var actions = GenerateNextActions(parameters, symbol, returnedLocations.FirstOrDefault(), wasTruncated, totalCount);

        return new FindAllReferencesToolResult
        {
            Success = true,
            Locations = returnedLocations,
            Message = wasTruncated 
                ? $"Found {totalCount} reference(s) - showing {returnedLocations.Count}"
                : $"Found {totalCount} reference(s)",
            Actions = actions,
            Insights = insights,
            ResourceUri = resourceUri,
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                TargetSymbol = symbol.ToDisplayString()
            },
            Summary = new SummaryInfo
            {
                TotalFound = totalCount,
                Returned = returnedLocations.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                SymbolInfo = new SymbolSummary
                {
                    Name = symbol.Name,
                    Kind = symbol.Kind.ToString(),
                    ContainingType = symbol.ContainingType?.ToDisplayString(),
                    Namespace = symbol.ContainingNamespace?.ToDisplayString()
                }
            },
            ResultsSummary = new ResultsSummary
            {
                Included = returnedLocations.Count,
                Total = totalCount,
                HasMore = wasTruncated
            },
            Distribution = distribution,
            Meta = new ToolExecutionMetadata 
            { 
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                Truncated = wasTruncated,
                Tokens = EstimateResponseTokens(returnedLocations.Count, wasTruncated)
            }
        };
    }

    private FindAllReferencesToolResult CreateErrorResult(
        string errorCode,
        string message,
        string[] recoverySteps,
        FindAllReferencesParams parameters,
        DateTime startTime)
    {
        return new FindAllReferencesToolResult
        {
            Success = false,
            Message = message,
            Error = new ErrorInfo
            {
                Code = errorCode,
                Message = message,
                Recovery = new RecoveryInfo
                {
                    Steps = recoverySteps
                }
            },
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
            },
            Meta = new ToolExecutionMetadata 
            { 
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                Tokens = 800 // Error response
            }
        };
    }

    private int EstimateTokensForReferences(List<Models.ReferenceLocation> references)
    {
        // Base overhead for response structure
        var baseTokens = 500;
        
        // Estimate tokens per reference (file path + line info + text snippet)
        var tokensPerReference = TOKENS_PER_REFERENCE;
        
        return baseTokens + (references.Count * tokensPerReference);
    }

    private List<string> GenerateInsights(ISymbol symbol, List<Models.ReferenceLocation> locations, bool wasTruncated)
    {
        var insights = new List<string>();

        if (wasTruncated)
        {
            insights.Add($"⚠️ Response truncated for performance. Full results available via resource URI.");
        }

        if (locations.Count == 0)
        {
            insights.Add("📭 No references found - this symbol might be unused");
        }
        else if (locations.Count == 1)
        {
            insights.Add("⚡ Only one reference found - consider if this symbol is necessary");
        }
        else if (locations.Count > 100)
        {
            insights.Add($"🔥 High usage ({locations.Count} references) - changes will have wide impact");
        }

        // File distribution insight
        var fileCount = locations.Select(l => l.FilePath).Distinct().Count();
        if (fileCount > 10)
        {
            insights.Add($"📁 Referenced across {fileCount} files - consider the coupling");
        }

        // Symbol type specific insights
        if (symbol.Kind == SymbolKind.Method && symbol.DeclaredAccessibility == Accessibility.Public)
        {
            insights.Add("🌐 Public method - external callers may exist outside this solution");
        }

        if (symbol.IsVirtual || symbol.IsAbstract)
        {
            insights.Add("🔄 Virtual/abstract member - check for overrides in derived classes");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(
        FindAllReferencesParams originalParams,
        ISymbol symbol,
        Models.ReferenceLocation? firstReference,
        bool wasTruncated,
        int totalCount)
    {
        var actions = new List<AIAction>();

        if (wasTruncated)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_find_all_references",
                Description = $"Get all {totalCount} references",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = originalParams.FilePath,
                    ["line"] = originalParams.Line,
                    ["column"] = originalParams.Column,
                    ["maxResults"] = Math.Min(totalCount, 500)
                },
                Priority = 90
            });
        }

        if (firstReference != null)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_rename_symbol",
                Description = "Rename this symbol across the codebase",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = originalParams.FilePath,
                    ["line"] = originalParams.Line,
                    ["column"] = originalParams.Column,
                    ["newName"] = $"New{symbol.Name}"
                },
                Priority = 70
            });
        }

        actions.Add(new AIAction
        {
            Action = "csharp_find_implementations",
            Description = "Find implementations if this is an interface/abstract",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = originalParams.FilePath,
                ["line"] = originalParams.Line,
                ["column"] = originalParams.Column
            },
            Priority = 60
        });

        return actions;
    }

    private int EstimateResponseTokens(int referenceCount, bool truncated)
    {
        // Base tokens for response structure
        var baseTokens = 600;
        
        // Each reference adds approximately 200 tokens
        var referenceTokens = referenceCount * 200;
        
        // Add extra if truncated (for messages)
        if (truncated) baseTokens += 100;
        
        return baseTokens + referenceTokens;
    }

    protected override int EstimateTokenUsage()
    {
        // This can vary widely based on number of references
        return 5000;
    }
}

public class FindAllReferencesParams
{
    [JsonPropertyName("filePath")]
    [System.ComponentModel.DataAnnotations.Required]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)]
    [COA.Mcp.Framework.Attributes.Description("Line number (1-based) where the symbol appears")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)]
    [COA.Mcp.Framework.Attributes.Description("Column number (1-based) where the symbol appears")]
    public int Column { get; set; }

    [JsonPropertyName("maxResults")]
    [System.ComponentModel.DataAnnotations.Range(1, 500)]
    [COA.Mcp.Framework.Attributes.Description("Maximum number of references to return (default: 50, max: 500)")]
    public int? MaxResults { get; set; }
}