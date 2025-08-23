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
        
        // Generate enhanced insights
        var insights = GenerateInsights(data, context.ResponseMode ?? "optimized");
        if (wasTruncated)
        {
            // Enhanced truncation messaging
            var fileGroups = data.Locations.GroupBy(l => l.FilePath).ToList();
            var returnedFileGroups = reducedLocations.GroupBy(l => l.FilePath).ToList();
            
            insights.Insert(0, $"🔍 Results truncated to prevent context overflow (showing {reducedLocations.Count} of {data.Locations.Count} references)");
            insights.Insert(1, $"📂 Coverage: {returnedFileGroups.Count}/{fileGroups.Count} files included");
            
            // Check if any files were completely excluded
            var excludedFiles = fileGroups.Count - returnedFileGroups.Count;
            if (excludedFiles > 0)
            {
                insights.Insert(2, $"⚠️ {excludedFiles} files with references were excluded - use file filtering for focused analysis");
            }
            
            insights.Add($"💾 Full results available via ReadMcpResourceTool");
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
            // Priority-based actions for truncated results
            var fileGroups = data.Locations.GroupBy(l => l.FilePath).OrderByDescending(g => g.Count()).ToList();
            var wasTruncated = data.Locations.Count > 50; // Assume truncation threshold
            
            if (wasTruncated && fileGroups.Count > 1)
            {
                // Focus on file with most references
                var topFile = fileGroups.First();
                var fileName = Path.GetFileName(topFile.Key);
                actions.Add(new AIAction
                {
                    Action = "csharp_find_all_references",
                    Description = $"📁 Focus on {fileName} ({topFile.Count()} references)",
                    Category = "filtering",
                    Priority = 95,
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = data.SearchLocation.FilePath,
                        ["line"] = data.SearchLocation.Line,
                        ["column"] = data.SearchLocation.Column,
                        ["maxResults"] = topFile.Count()
                    }
                });

                // Show second most referenced file if significant
                if (fileGroups.Count > 1 && fileGroups[1].Count() > 3)
                {
                    var secondFile = fileGroups[1];
                    var secondFileName = Path.GetFileName(secondFile.Key);
                    actions.Add(new AIAction
                    {
                        Action = "csharp_find_all_references",
                        Description = $"📁 Analyze {secondFileName} ({secondFile.Count()} references)",
                        Category = "filtering",
                        Priority = 85,
                        Parameters = new Dictionary<string, object>
                        {
                            ["filePath"] = data.SearchLocation.FilePath,
                            ["line"] = data.SearchLocation.Line,
                            ["column"] = data.SearchLocation.Column,
                            ["maxResults"] = 100
                        }
                    });
                }
            }

            // Navigate to first reference (always useful)
            var firstRef = data.Locations.First();
            actions.Add(new AIAction
            {
                Action = "csharp_goto_definition", 
                Description = $"🔍 View first usage: {Path.GetFileName(firstRef.FilePath)}:{firstRef.Line}",
                Category = "navigation",
                Priority = 80,
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = firstRef.FilePath,
                    ["line"] = firstRef.Line,
                    ["column"] = firstRef.Column
                }
            });
            
            // Symbol-specific actions based on usage patterns
            if (data.Symbol.Kind == SymbolKind.Method && data.Locations.Count > 10)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_rename_symbol",
                    Description = $"🔄 Safely rename '{data.Symbol.Name}' ({data.Locations.Count} usages)",
                    Category = "refactoring",
                    Priority = 75,
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = data.SearchLocation.FilePath,
                        ["line"] = data.SearchLocation.Line,
                        ["column"] = data.SearchLocation.Column,
                        ["preview"] = true
                    }
                });
            }
            
            if (data.Symbol.Kind == SymbolKind.NamedType)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_find_implementations",
                    Description = $"🔗 Find implementations/inheritances of '{data.Symbol.Name}'",
                    Category = "analysis",
                    Priority = 70,
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = data.SearchLocation.FilePath,
                        ["line"] = data.SearchLocation.Line,
                        ["column"] = data.SearchLocation.Column
                    }
                });
            }

            // Refactoring insights based on usage count
            if (data.Locations.Count == 1)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_inline_method",
                    Description = $"💡 Single usage - consider inlining '{data.Symbol.Name}'",
                    Category = "refactoring",
                    Priority = 65,
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = data.SearchLocation.FilePath,
                        ["line"] = data.SearchLocation.Line,
                        ["column"] = data.SearchLocation.Column,
                        ["preview"] = true
                    }
                });
            }
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