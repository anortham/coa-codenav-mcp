using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Tools;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Response builder for DocumentSymbolsTool that implements token-aware response building with strong typing
/// </summary>
public class DocumentSymbolsResponseBuilder : BaseResponseBuilder<DocumentSymbolsToolResult, DocumentSymbolsToolResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public DocumentSymbolsResponseBuilder(
        ILogger<DocumentSymbolsResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }
    
    public override Task<DocumentSymbolsToolResult> BuildResponseAsync(
        DocumentSymbolsToolResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Apply progressive reduction to symbols
        var reducedSymbols = data.Symbols;
        var originalCount = data.Symbols?.Count ?? 0;
        var wasReduced = false;
        
        if (data.Symbols != null && data.Symbols.Count > 0)
        {
            var originalTokens = _tokenEstimator.EstimateObject(data.Symbols);
            
            if (originalTokens > tokenBudget * 0.7) // Reserve 30% for metadata
            {
                var symbolBudget = (int)(tokenBudget * 0.7);
                reducedSymbols = ReduceDocumentSymbols(data.Symbols, symbolBudget);
                wasReduced = true;
            }
        }
        
        // Generate insights based on document structure
        var insights = GenerateInsights(data, context.ResponseMode);
        
        // Generate actions for next steps
        var actions = GenerateActions(data, (int)(tokenBudget * 0.15));
        
        // Update the input data with optimized/reduced content
        data.Symbols = reducedSymbols;
        
        // Update insights and actions with token-aware reductions
        data.Insights = ReduceInsights(insights, (int)(tokenBudget * 0.1));
        data.Actions = ReduceActions(actions, (int)(tokenBudget * 0.15));
        
        // Update metadata to reflect the optimization
        if (data.Summary != null)
        {
            data.Summary.Returned = reducedSymbols?.Count ?? 0;
        }
        
        // Add truncation message if needed
        if (wasReduced && data.Insights != null)
        {
            data.Insights.Insert(0, $"⚠️ Token optimization applied. Showing {reducedSymbols?.Count ?? 0} of {originalCount} symbols.");
        }
        
        // Update execution metadata
        data.Meta = new ToolExecutionMetadata
        {
            Mode = context.ResponseMode ?? "optimized",
            Truncated = wasReduced,
            Tokens = _tokenEstimator.EstimateObject(data),
            ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
        };
        
        data.Success = true;
        data.Message = BuildSummary(data, reducedSymbols?.Count ?? 0, originalCount);
        
        return Task.FromResult(data);
    }
    
    protected override List<string> GenerateInsights(
        DocumentSymbolsToolResult data,
        string responseMode)
    {
        var insights = new List<string>();
        
        if (data.Symbols == null || data.Symbols.Count == 0)
        {
            insights.Add("No symbols found in document - file may be empty or not a valid C# file");
        }
        else
        {
            // Count symbol types
            var classes = data.Symbols.Count(s => s.Kind == "Class");
            var interfaces = data.Symbols.Count(s => s.Kind == "Interface");
            var methods = CountNestedSymbols(data.Symbols, "Method");
            var properties = CountNestedSymbols(data.Symbols, "Property");
            
            // Document structure insights
            if (classes + interfaces > 5)
            {
                insights.Add($"Large file detected ({classes} classes, {interfaces} interfaces) - consider splitting into multiple files");
            }
            else if (classes + interfaces == 1)
            {
                insights.Add("Single type per file - follows best practices");
            }
            
            // Method complexity
            if (methods > 30)
            {
                insights.Add($"High method count ({methods}) - class may have too many responsibilities");
            }
            else if (methods > 15)
            {
                insights.Add($"Moderate method count ({methods}) - consider if class follows Single Responsibility Principle");
            }
            
            // Property analysis
            if (properties > methods && methods > 0)
            {
                insights.Add("More properties than methods - appears to be a data-heavy class");
            }
            
            // Check for test classes
            if (data.Symbols.Any(s => s.Name?.Contains("Test") == true))
            {
                var testMethods = CountNestedSymbols(data.Symbols.Where(s => s.Name?.Contains("Test") == true).ToList(), "Method");
                insights.Add($"Test class detected with {testMethods} test methods");
            }
            
            // Check for nested types
            var nestedTypes = data.Symbols.SelectMany(s => s.Children ?? new List<DocumentSymbol>())
                .Count(c => c.Kind == "Class" || c.Kind == "Interface" || c.Kind == "Struct");
            if (nestedTypes > 0)
            {
                insights.Add($"Contains {nestedTypes} nested types - consider if they should be top-level");
            }
        }
        
        // Distribution statistics
        if (data.Distribution != null)
        {
            var publicCount = data.Distribution.ByAccessibility?.GetValueOrDefault("public", 0) ?? 0;
            if (publicCount > 20)
            {
                insights.Add($"Large public API surface ({publicCount} public members)");
            }
            
            var totalSymbols = data.Distribution.ByKind?.Values.Sum() ?? 0;
            if (totalSymbols > 100)
            {
                insights.Add($"Large file ({totalSymbols} symbols) - consider refactoring");
            }
        }
        
        if (responseMode == "summary")
        {
            insights.Add("Showing summary view - use 'detailed' mode for complete symbol information");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        DocumentSymbolsToolResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Symbols?.Any() == true)
        {
            // Navigation actions
            actions.Add(new AIAction
            {
                Action = "csharp_goto_definition",
                Description = "Navigate to specific symbol in the document",
                Category = "navigate",
                Priority = 10
            });
            
            // Analysis actions
            var methodCount = CountNestedSymbols(data.Symbols, "Method");
            if (methodCount > 0)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_code_metrics",
                    Description = "Analyze code metrics for this document",
                    Category = "analyze",
                    Priority = 9
                });
                
                actions.Add(new AIAction
                {
                    Action = "csharp_find_unused_code",
                    Description = "Find unused symbols in this document",
                    Category = "analyze",
                    Priority = 8
                });
            }
            
            // Refactoring actions for large files
            if (data.Symbols.Count > 10 || data.Statistics?.TotalLines > 300)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_extract_method",
                    Description = "Extract methods to reduce complexity",
                    Category = "refactor",
                    Priority = 8
                });
            }
            
            // Type hierarchy for classes/interfaces
            if (data.Symbols.Any(s => s.Kind == "Class" || s.Kind == "Interface"))
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_type_hierarchy",
                    Description = "View type hierarchy for classes/interfaces",
                    Category = "analyze",
                    Priority = 7
                });
                
                actions.Add(new AIAction
                {
                    Action = "csharp_find_implementations",
                    Description = "Find implementations of interfaces",
                    Category = "search",
                    Priority = 7
                });
            }
            
            // Documentation actions
            actions.Add(new AIAction
            {
                Action = "csharp_hover",
                Description = "Get detailed information about symbols",
                Category = "information",
                Priority = 6
            });
            
            // Search actions
            actions.Add(new AIAction
            {
                Action = "csharp_symbol_search",
                Description = "Search for similar symbols across solution",
                Category = "search",
                Priority = 6
            });
        }
        
        return actions;
    }
    
    private List<DocumentSymbol>? ReduceDocumentSymbols(List<DocumentSymbol> symbols, int tokenBudget)
    {
        var result = new List<DocumentSymbol>();
        var currentTokens = 0;
        
        // Prioritize symbols by importance
        var prioritizedSymbols = symbols
            .OrderByDescending(s => GetSymbolPriority(s))
            .ThenBy(s => s.Location?.StartLine ?? 0);
        
        foreach (var symbol in prioritizedSymbols)
        {
            // Create a reduced version of the symbol (with limited children)
            var reducedSymbol = ReduceSymbol(symbol, tokenBudget - currentTokens);
            var symbolTokens = _tokenEstimator.EstimateObject(reducedSymbol);
            
            if (currentTokens + symbolTokens <= tokenBudget)
            {
                result.Add(reducedSymbol);
                currentTokens += symbolTokens;
            }
            else if (result.Count == 0 && symbolTokens > tokenBudget)
            {
                // If no symbols fit and this one is too large, include a minimal version
                var minimalSymbol = new DocumentSymbol
                {
                    Name = symbol.Name,
                    Kind = symbol.Kind,
                    Range = symbol.Range,
                    Children = null // No children to save space
                };
                result.Add(minimalSymbol);
                break;
            }
            else
            {
                break;
            }
        }
        
        return result;
    }
    
    private DocumentSymbol ReduceSymbol(DocumentSymbol symbol, int tokenBudget)
    {
        var reduced = new DocumentSymbol
        {
            Name = symbol.Name,
            Kind = symbol.Kind,
            FullName = symbol.FullName,
            ContainerName = symbol.ContainerName,
            ContainerType = symbol.ContainerType,
            Location = symbol.Location,
            Accessibility = symbol.Accessibility,
            Namespace = symbol.Namespace,
            ProjectName = symbol.ProjectName,
            IsStatic = symbol.IsStatic,
            Modifiers = symbol.Modifiers,
            TypeParameters = symbol.TypeParameters,
            Parameters = symbol.Parameters,
            ReturnType = symbol.ReturnType,
            Children = null
        };
        
        if (symbol.Children != null && symbol.Children.Count > 0)
        {
            var baseTokens = _tokenEstimator.EstimateObject(reduced);
            var remainingBudget = tokenBudget - baseTokens;
            
            if (remainingBudget > 100) // Only include children if we have reasonable space
            {
                var childBudget = remainingBudget / Math.Max(1, symbol.Children.Count);
                var reducedChildren = new List<DocumentSymbol>();
                
                // Prioritize most important children
                var prioritizedChildren = symbol.Children
                    .OrderByDescending(c => GetSymbolPriority(c))
                    .Take(10); // Limit children to top 10
                
                foreach (var child in prioritizedChildren)
                {
                    var childTokens = _tokenEstimator.EstimateObject(child);
                    if (childTokens <= childBudget)
                    {
                        reducedChildren.Add(child);
                    }
                }
                
                if (reducedChildren.Count > 0)
                {
                    reduced.Children = reducedChildren;
                }
            }
        }
        
        return reduced;
    }
    
    private int GetSymbolPriority(DocumentSymbol symbol)
    {
        // Higher priority for types
        var priority = symbol.Kind switch
        {
            "Class" => 100,
            "Interface" => 100,
            "Struct" => 90,
            "Enum" => 80,
            "Method" => 70,
            "Constructor" => 70,
            "Property" => 60,
            "Field" => 50,
            "Event" => 50,
            _ => 30
        };
        
        // Boost priority for public members
        if (symbol.Accessibility == "public")
        {
            priority += 20;
        }
        
        // Boost priority for static members
        if (symbol.IsStatic)
        {
            priority += 10;
        }
        
        return priority;
    }
    
    private int CountNestedSymbols(List<DocumentSymbol> symbols, string kind)
    {
        var count = symbols.Count(s => s.Kind == kind);
        
        foreach (var symbol in symbols)
        {
            if (symbol.Children != null)
            {
                count += CountNestedSymbols(symbol.Children, kind);
            }
        }
        
        return count;
    }
    
    private string BuildSummary(DocumentSymbolsToolResult data, int displayedCount, int totalCount)
    {
        if (totalCount == 0)
        {
            return "No symbols found in the document";
        }
        
        var symbolTypes = new List<string>();
        
        if (data.Symbols != null)
        {
            var classes = data.Symbols.Count(s => s.Kind == "Class");
            var interfaces = data.Symbols.Count(s => s.Kind == "Interface");
            var methods = CountNestedSymbols(data.Symbols, "Method");
            
            if (classes > 0) symbolTypes.Add($"{classes} class(es)");
            if (interfaces > 0) symbolTypes.Add($"{interfaces} interface(s)");
            if (methods > 0) symbolTypes.Add($"{methods} method(s)");
        }
        
        var typeSummary = symbolTypes.Any() ? $" ({string.Join(", ", symbolTypes)})" : "";
        
        if (displayedCount < totalCount)
        {
            return $"Found {totalCount} symbols{typeSummary}, showing {displayedCount} most important";
        }
        
        return $"Found {totalCount} symbols{typeSummary}";
    }
}