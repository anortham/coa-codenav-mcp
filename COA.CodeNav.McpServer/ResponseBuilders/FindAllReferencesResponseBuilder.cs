using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Response builder for FindAllReferences using framework's token optimization with strong typing
/// </summary>
public class FindAllReferencesResponseBuilder : BaseResponseBuilder<FindAllReferencesData, FindAllReferencesToolResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public FindAllReferencesResponseBuilder(
        ILogger<FindAllReferencesResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }
    
    public override async Task<FindAllReferencesToolResult> BuildResponseAsync(
        FindAllReferencesData data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Use framework's reduction engine to optimize locations
        var reducedLocations = _reductionEngine.Reduce(
            data.Locations,
            location => _tokenEstimator.EstimateObject(location),
            (int)(tokenBudget * 0.7), // 70% for data
            "standard").Items;
        
        var wasTruncated = reducedLocations.Count < data.Locations.Count;
        
        // Generate insights
        var insights = GenerateInsights(data, context.ResponseMode ?? "optimized");
        if (wasTruncated)
        {
            insights.Insert(0, $"⚠️ Showing {reducedLocations.Count} of {data.Locations.Count} references. Full results stored as resource.");
        }
        
        // Generate actions
        var actions = GenerateActions(data, (int)(tokenBudget * 0.15));
        
        // Build the response
        var response = new FindAllReferencesToolResult
        {
            Success = true,
            Query = new QueryInfo
            {
                FilePath = data.SearchLocation.FilePath,
                Position = new PositionInfo
                {
                    Line = data.SearchLocation.Line,
                    Column = data.SearchLocation.Column
                },
                TargetSymbol = data.Symbol.Name
            },
            Summary = new SummaryInfo
            {
                TotalFound = data.Locations.Count,
                Returned = reducedLocations.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                SymbolInfo = new SymbolSummary
                {
                    Name = data.Symbol.Name,
                    Kind = data.Symbol.Kind.ToString(),
                    ContainingType = data.Symbol.ContainingType?.ToDisplayString(),
                    Namespace = data.Symbol.ContainingNamespace?.ToDisplayString()
                }
            },
            Locations = reducedLocations,
            ResultsSummary = new ResultsSummary
            {
                Included = reducedLocations.Count,
                Total = data.Locations.Count,
                HasMore = wasTruncated
            },
            Insights = ReduceInsights(insights, (int)(tokenBudget * 0.1)),
            Actions = ReduceActions(actions, (int)(tokenBudget * 0.05)),
            Meta = new ToolExecutionMetadata
            {
                Mode = context.ResponseMode ?? "optimized",
                Truncated = wasTruncated,
                Tokens = 0, // Will be updated below
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            },
            ResourceUri = data.ResourceUri
        };
        
        // Update token estimate
        response.Meta.Tokens = _tokenEstimator.EstimateObject(response);
        
        return await Task.FromResult(response);
    }
    
    protected override List<string> GenerateInsights(FindAllReferencesData data, string responseMode)
    {
        var insights = new List<string>();
        var locations = data.Locations;
        
        if (locations.Count == 0)
        {
            insights.Add($"No references found for {data.Symbol.Name} - it may be unused");
            insights.Add("Consider removing unused code or verifying the symbol exists");
            return insights;
        }
        
        // File distribution insight
        var fileCount = locations.Select(l => l.FilePath).Distinct().Count();
        if (fileCount > 1)
        {
            insights.Add($"References spread across {fileCount} files");
        }
        else
        {
            insights.Add($"All references in single file: {locations.First().FilePath}");
        }
        
        // Usage pattern insights
        if (data.Symbol.Kind == SymbolKind.Method)
        {
            insights.Add($"Method '{data.Symbol.Name}' has {locations.Count} call sites");
            if (locations.Count > 50)
            {
                insights.Add("High usage - consider impact before modifying");
            }
        }
        else if (data.Symbol.Kind == SymbolKind.Property)
        {
            var reads = locations.Count(l => l.Kind == "read");
            var writes = locations.Count(l => l.Kind == "write");
            if (reads > 0 || writes > 0)
            {
                insights.Add($"Property accessed {reads} times, modified {writes} times");
            }
        }
        
        // Refactoring insights
        if (locations.Count == 1)
        {
            insights.Add("Single reference - safe to inline or remove if not needed");
        }
        else if (locations.Count > 100)
        {
            insights.Add("Extensive usage - consider creating facade or adapter pattern");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(FindAllReferencesData data, int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Locations.Count > 0)
        {
            // Navigate to first reference
            var firstRef = data.Locations.First();
            actions.Add(new AIAction
            {
                Action = "navigate_first",
                Tool = "csharp_goto_definition",
                Description = $"Navigate to first reference at {Path.GetFileName(firstRef.FilePath)}:{firstRef.Line}",
                Category = "navigate",
                Priority = 10,
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = firstRef.FilePath,
                    ["line"] = firstRef.Line,
                    ["column"] = firstRef.Column
                }
            });
            
            // Rename symbol
            actions.Add(new AIAction
            {
                Action = "rename",
                Tool = "csharp_rename_symbol",
                Description = $"Rename '{data.Symbol.Name}' across all {data.Locations.Count} references",
                Category = "refactor",
                Priority = 8,
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.SearchLocation.FilePath,
                    ["line"] = data.SearchLocation.Line,
                    ["column"] = data.SearchLocation.Column,
                    ["newName"] = $"{data.Symbol.Name}_renamed"
                }
            });
            
            // Extract to interface if it's a class
            if (data.Symbol.Kind == SymbolKind.NamedType)
            {
                actions.Add(new AIAction
                {
                    Action = "extract_interface",
                    Tool = "csharp_generate_code",
                    Description = "Extract interface from this class",
                    Category = "refactor",
                    Priority = 7
                });
            }
        }
        
        // Find implementations if interface/abstract
        if (data.Symbol.IsAbstract || data.Symbol.Kind == SymbolKind.Method)
        {
            actions.Add(new AIAction
            {
                Action = "find_implementations",
                Tool = "csharp_find_implementations",
                Description = "Find all implementations/overrides",
                Category = "analyze",
                Priority = 9,
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.SearchLocation.FilePath,
                    ["line"] = data.SearchLocation.Line,
                    ["column"] = data.SearchLocation.Column
                }
            });
        }
        
        return actions;
    }
}

/// <summary>
/// Data container for FindAllReferences operation
/// </summary>
public class FindAllReferencesData
{
    public required ISymbol Symbol { get; init; }
    public required List<ReferenceLocation> Locations { get; init; }
    public required (string FilePath, int Line, int Column) SearchLocation { get; init; }
    public string? ResourceUri { get; init; }
}