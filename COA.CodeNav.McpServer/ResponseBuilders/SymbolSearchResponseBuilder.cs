using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Response builder for SymbolSearchTool that implements token-aware response building
/// </summary>
public class SymbolSearchResponseBuilder : BaseResponseBuilder<SymbolSearchToolResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public SymbolSearchResponseBuilder(
        ILogger<SymbolSearchResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }
    
    public override async Task<object> BuildResponseAsync(
        SymbolSearchToolResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Apply progressive reduction to symbols
        var reducedSymbols = data.Symbols;
        var wasReduced = false;
        
        if (data.Symbols != null)
        {
            var originalTokens = _tokenEstimator.EstimateObject(data.Symbols);
            
            if (originalTokens > tokenBudget * 0.7) // Reserve 30% for metadata
            {
                var symbolBudget = (int)(tokenBudget * 0.7);
                reducedSymbols = ReduceSymbols(data.Symbols, symbolBudget);
                wasReduced = true;
            }
        }
        
        // Generate insights based on search results
        var insights = GenerateInsights(data, context.ResponseMode);
        
        // Generate actions for next steps
        var actions = GenerateActions(data, (int)(tokenBudget * 0.15));
        
        // Build AI-optimized response
        var response = new AIOptimizedResponse
        {
            Format = "ai-optimized",
            Data = new AIResponseData
            {
                Summary = BuildSummary(data, reducedSymbols?.Count ?? 0),
                Results = reducedSymbols,
                Count = reducedSymbols?.Count ?? 0,
                ExtensionData = new Dictionary<string, object>
                {
                    ["totalFound"] = data.Summary?.TotalFound ?? 0,
                    ["hasMore"] = data.ResultsSummary?.HasMore ?? false
                }
            },
            Insights = ReduceInsights(insights, (int)(tokenBudget * 0.1)),
            Actions = ReduceActions(actions, (int)(tokenBudget * 0.15)),
            Meta = CreateMetadata(startTime, wasReduced)
        };
        
        // Update token metadata
        response.Meta.TokenInfo = new TokenInfo
        {
            Estimated = _tokenEstimator.EstimateObject(response),
            Limit = context.TokenLimit ?? 10000,
            ReductionStrategy = wasReduced ? "progressive" : null
        };
        
        return response;
    }
    
    protected override List<string> GenerateInsights(
        SymbolSearchToolResult data,
        string responseMode)
    {
        var insights = new List<string>();
        
        if (data.Summary?.TotalFound == 0)
        {
            insights.Add("No symbols found - try broadening search criteria or using wildcard (*) patterns");
        }
        else if (data.Summary?.TotalFound > 100)
        {
            insights.Add($"Large result set ({data.Summary.TotalFound} symbols) - consider using filters like SymbolKinds or NamespaceFilter");
        }
        
        if (data.ResultsSummary?.HasMore == true)
        {
            insights.Add("Results truncated due to limits - refine search or increase MaxResults parameter");
        }
        
        if (data.Query?.SearchType == "wildcard" && data.Query?.SearchPattern?.Contains("*") == true)
        {
            insights.Add("Using wildcard search - exact or contains search may be faster for specific symbols");
        }
        
        if (responseMode == "summary")
        {
            insights.Add("Showing summary view - use 'detailed' mode for complete symbol metadata");
        }
        
        // Add pattern-specific insights
        if (data.Symbols?.Any(s => s.Kind == "Interface") == true)
        {
            insights.Add($"Found {data.Symbols.Count(s => s.Kind == "Interface")} interfaces - use csharp_find_implementations to find concrete types");
        }
        
        if (data.Symbols?.Any(s => s.Accessibility == "Private") == true)
        {
            insights.Add("Including private symbols - set IncludePrivate to false to see only public API");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        SymbolSearchToolResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Symbols?.Any() == true)
        {
            // Navigation actions
            actions.Add(new AIAction
            {
                Action = "csharp_goto_definition",
                Description = "Navigate to symbol definition using csharp_goto_definition",
                Category = "navigate",
                Priority = 10
            });
            
            actions.Add(new AIAction
            {
                Action = "csharp_find_all_references",
                Description = "Find all references to a symbol using csharp_find_all_references",
                Category = "analyze",
                Priority = 9
            });
            
            // Analysis actions
            if (data.Symbols.Any(s => s.Kind == "Method"))
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_call_hierarchy",
                    Description = "Analyze call hierarchy with csharp_call_hierarchy",
                    Category = "analyze",
                    Priority = 8
                });
            }
            
            if (data.Symbols.Any(s => s.Kind == "NamedType"))
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_type_hierarchy",
                    Description = "View type hierarchy with csharp_type_hierarchy",
                    Category = "analyze",
                    Priority = 8
                });
            }
            
            // Refactoring actions
            actions.Add(new AIAction
            {
                Action = "csharp_rename_symbol",
                Description = "Rename symbol across solution with csharp_rename_symbol",
                Category = "refactor",
                Priority = 7
            });
        }
        
        // Filter actions
        if (data.Summary?.TotalFound > 10)
        {
            if (data.Query?.SymbolKinds == null || data.Query.SymbolKinds.Count == 0)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_symbol_search",
                    Description = "Filter by symbol kind (Method, Property, Class, etc.)",
                    Category = "filter",
                    Priority = 6,
                    Parameters = new Dictionary<string, object>
                    {
                        ["symbolKinds"] = new[] { "Method", "Property", "Class" }
                    }
                });
            }
            
            if (data.Summary?.TotalFound > 10)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_symbol_search",
                    Description = "Filter results by namespace",
                    Category = "filter",
                    Priority = 6,
                    Parameters = new Dictionary<string, object>
                    {
                        ["namespaceFilter"] = "YourNamespace"
                    }
                });
            }
        }
        
        return actions;
    }
    
    private List<SymbolInfo>? ReduceSymbols(List<SymbolInfo> symbols, int tokenBudget)
    {
        var result = new List<SymbolInfo>();
        var currentTokens = 0;
        
        // Prioritize symbols by importance
        var prioritizedSymbols = symbols
            .OrderByDescending(s => GetSymbolPriority(s))
            .ThenBy(s => s.Name);
        
        foreach (var symbol in prioritizedSymbols)
        {
            var symbolTokens = _tokenEstimator.EstimateObject(symbol);
            if (currentTokens + symbolTokens <= tokenBudget)
            {
                result.Add(symbol);
                currentTokens += symbolTokens;
            }
            else
            {
                break;
            }
        }
        
        return result;
    }
    
    private int GetSymbolPriority(SymbolInfo symbol)
    {
        // Higher priority for public API
        var priority = symbol.Accessibility switch
        {
            "Public" => 100,
            "Protected" => 80,
            "Internal" => 60,
            "Private" => 40,
            _ => 50
        };
        
        // Boost priority for important symbol kinds
        priority += symbol.Kind switch
        {
            "NamedType" => 20,
            "Interface" => 20,
            "Method" => 15,
            "Property" => 10,
            "Event" => 10,
            "Field" => 5,
            _ => 0
        };
        
        return priority;
    }
    
    private string BuildSummary(SymbolSearchToolResult data, int displayedCount)
    {
        var totalFound = data.Summary?.TotalFound ?? displayedCount;
        
        if (totalFound == 0)
        {
            return $"No symbols found matching '{data.Query?.SearchPattern}'";
        }
        
        if (displayedCount < totalFound)
        {
            return $"Found {totalFound} symbols, showing {displayedCount} most relevant";
        }
        
        return $"Found {totalFound} symbols matching '{data.Query?.SearchPattern}'";
    }
}