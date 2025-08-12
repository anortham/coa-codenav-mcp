using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders.TypeScript;

/// <summary>
/// Response builder for TypeScript diagnostics with token optimization
/// </summary>
public class TsDiagnosticsResponseBuilder : BaseResponseBuilder<TsGetDiagnosticsResult, TsGetDiagnosticsResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public TsDiagnosticsResponseBuilder(
        ILogger<TsDiagnosticsResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }

    public override Task<TsGetDiagnosticsResult> BuildResponseAsync(
        TsGetDiagnosticsResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Apply progressive reduction to diagnostics
        var reducedDiagnostics = data.Diagnostics;
        var wasReduced = false;
        
        if (data.Diagnostics != null && data.Diagnostics.Count > 0)
        {
            // Don't reduce small result sets (<=20 diagnostics)
            if (data.Diagnostics.Count <= 20)
            {
                _logger?.LogDebug("TsDiagnosticsResponseBuilder: Small result set ({Count} diagnostics), keeping all", 
                    data.Diagnostics.Count);
                reducedDiagnostics = data.Diagnostics;
                wasReduced = false;
            }
            else
            {
                // Fix token budget if it's 0 or negative
                if (tokenBudget <= 0)
                {
                    tokenBudget = context.TokenLimit ?? 10000;
                    _logger?.LogDebug("TsDiagnosticsResponseBuilder: Fixed token budget from 0 to {Budget}", tokenBudget);
                }
                
                var originalTokens = _tokenEstimator.EstimateObject(data.Diagnostics);
                
                _logger?.LogDebug("TsDiagnosticsResponseBuilder: Original diagnostics: {Count}, Estimated tokens: {Tokens}, Budget: {Budget}", 
                    data.Diagnostics.Count, originalTokens, tokenBudget);
                
                // Apply token optimization for large result sets
                if (originalTokens > tokenBudget * 0.7)
                {
                    var diagnosticBudget = (int)(tokenBudget * 0.7);
                    reducedDiagnostics = ReduceDiagnostics(data.Diagnostics, diagnosticBudget);
                    wasReduced = reducedDiagnostics?.Count < data.Diagnostics.Count;
                    
                    _logger?.LogDebug("TsDiagnosticsResponseBuilder: Reduced from {Original} to {Reduced} diagnostics", 
                        data.Diagnostics.Count, reducedDiagnostics?.Count ?? 0);
                }
            }
        }
        
        // Generate insights based on diagnostics
        var insights = GenerateInsights(data, context.ResponseMode);
        
        // Generate actions for next steps
        var actions = GenerateActions(data, (int)(tokenBudget * 0.15));
        
        // Update the input data with optimized/reduced content
        data.Diagnostics = reducedDiagnostics;
        
        // Update insights and actions with token-aware reductions
        data.Insights = ReduceInsights(insights, (int)(tokenBudget * 0.1));
        data.Actions = ReduceActions(actions, (int)(tokenBudget * 0.15));
        
        // Update metadata to reflect the optimization
        if (data.ResultsSummary != null)
        {
            data.ResultsSummary.Included = reducedDiagnostics?.Count ?? 0;
            data.ResultsSummary.HasMore = wasReduced || data.ResultsSummary.HasMore;
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
        data.Message = BuildSummary(data, reducedDiagnostics?.Count ?? 0);
        
        return Task.FromResult(data);
    }
    
    protected override List<string> GenerateInsights(
        TsGetDiagnosticsResult data,
        string? responseMode)
    {
        var insights = new List<string>();
        
        if (data.Diagnostics == null || data.Diagnostics.Count == 0)
        {
            insights.Add("✅ No TypeScript compilation issues found - code is clean");
            return insights;
        }
        
        // Count by severity
        var errorCount = data.Diagnostics.Count(d => d.Category == "error");
        var warningCount = data.Diagnostics.Count(d => d.Category == "warning");
        var infoCount = data.Diagnostics.Count(d => d.Category == "info");
        
        if (errorCount > 0)
        {
            insights.Add($"❌ {errorCount} error{(errorCount > 1 ? "s" : "")} must be fixed for compilation to succeed");
        }
        
        if (warningCount > 0)
        {
            insights.Add($"⚠️ {warningCount} warning{(warningCount > 1 ? "s" : "")} should be addressed");
        }
        
        if (infoCount > 0)
        {
            insights.Add($"ℹ️ {infoCount} informational message{(infoCount > 1 ? "s" : "")}");
        }
        
        // Analyze common patterns
        var typeErrors = data.Diagnostics.Count(d => d.Code >= 2300 && d.Code < 2800);
        var syntaxErrors = data.Diagnostics.Count(d => d.Code >= 1000 && d.Code < 2000);
        
        if (typeErrors > 3)
        {
            insights.Add($"Type-related issues detected ({typeErrors}) - review type definitions and interfaces");
        }
        
        if (syntaxErrors > 0)
        {
            insights.Add($"Syntax errors found ({syntaxErrors}) - fix syntax before addressing other issues");
        }
        
        // File distribution insight
        if (data.Distribution?.ByFile?.Count > 1)
        {
            var topFile = data.Distribution.ByFile.OrderByDescending(kvp => kvp.Value).First();
            insights.Add($"Most issues in {topFile.Key} ({topFile.Value} diagnostics)");
        }
        
        if (data.ResultsSummary?.HasMore == true)
        {
            insights.Add($"Showing {data.ResultsSummary.Included} of {data.ResultsSummary.Total} diagnostics - increase MaxResults for complete list");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        TsGetDiagnosticsResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Diagnostics?.Any() == true)
        {
            // Quick fix actions
            if (data.Diagnostics.Any(d => d.Category == "error"))
            {
                actions.Add(new AIAction
                {
                    Action = "ts_apply_quick_fix",
                    Description = "Apply TypeScript quick fixes to resolve errors",
                    Category = "fix",
                    Priority = 10
                });
            }
            
            // Import-related actions
            if (data.Diagnostics.Any(d => d.Code == 2304 || d.Code == 2305))
            {
                actions.Add(new AIAction
                {
                    Action = "ts_add_missing_imports",
                    Description = "Add missing import statements",
                    Category = "fix",
                    Priority = 9
                });
            }
            
            // Unused code actions
            if (data.Diagnostics.Any(d => d.Code == 6133 || d.Code == 6138))
            {
                actions.Add(new AIAction
                {
                    Action = "ts_remove_unused",
                    Description = "Remove unused variables and imports",
                    Category = "cleanup",
                    Priority = 7
                });
            }
            
            // Organize imports
            actions.Add(new AIAction
            {
                Action = "ts_organize_imports",
                Description = "Organize and sort import statements",
                Category = "cleanup",
                Priority = 5
            });
            
            // Navigate to specific errors
            if (data.Diagnostics.Count > 0)
            {
                var firstError = data.Diagnostics.FirstOrDefault(d => d.Category == "error") ?? data.Diagnostics.First();
                if (firstError.FilePath != null && firstError.Start != null)
                {
                    actions.Add(new AIAction
                    {
                        Action = "navigate_to_error",
                        Description = $"Navigate to first error in {Path.GetFileName(firstError.FilePath)}",
                        Category = "navigate",
                        Priority = 8,
                        Parameters = new Dictionary<string, object>
                        {
                            ["filePath"] = firstError.FilePath,
                            ["line"] = firstError.Start.Line + 1,
                            ["character"] = firstError.Start.Character + 1
                        }
                    });
                }
            }
        }
        else
        {
            // No issues - suggest other analysis
            actions.Add(new AIAction
            {
                Action = "ts_symbol_search",
                Description = "Search for TypeScript symbols",
                Category = "search",
                Priority = 5
            });
            
            actions.Add(new AIAction
            {
                Action = "ts_document_symbols",
                Description = "Get document structure and symbols",
                Category = "analyze",
                Priority = 5
            });
        }
        
        return actions;
    }
    
    private List<TsDiagnostic>? ReduceDiagnostics(List<TsDiagnostic> diagnostics, int tokenBudget)
    {
        var result = new List<TsDiagnostic>();
        var currentTokens = 0;
        
        // Prioritize errors over warnings over info
        var prioritized = diagnostics
            .OrderBy(d => GetSeverityPriority(d.Category))
            .ThenBy(d => d.FilePath)
            .ThenBy(d => d.Start?.Line ?? 0);
        
        foreach (var diagnostic in prioritized)
        {
            var diagnosticTokens = _tokenEstimator.EstimateObject(diagnostic);
            
            if (currentTokens + diagnosticTokens <= tokenBudget)
            {
                result.Add(diagnostic);
                currentTokens += diagnosticTokens;
            }
            else
            {
                // If we haven't included any errors yet, include at least one
                if (!result.Any(d => d.Category == "error") && diagnostic.Category == "error")
                {
                    result.Add(diagnostic);
                }
                break;
            }
        }
        
        return result;
    }
    
    private int GetSeverityPriority(string category)
    {
        return category.ToLowerInvariant() switch
        {
            "error" => 0,
            "warning" => 1,
            "info" => 2,
            "hint" => 3,
            _ => 4
        };
    }
    
    private string BuildSummary(TsGetDiagnosticsResult data, int displayedCount)
    {
        var totalFound = data.Summary?.TotalFound ?? displayedCount;
        
        if (totalFound == 0)
        {
            return "No TypeScript compilation issues found";
        }
        
        var parts = new List<string>();
        if (data.Summary != null)
        {
            if (data.Summary.ErrorCount > 0)
                parts.Add($"{data.Summary.ErrorCount} error{(data.Summary.ErrorCount > 1 ? "s" : "")}");
            if (data.Summary.WarningCount > 0)
                parts.Add($"{data.Summary.WarningCount} warning{(data.Summary.WarningCount > 1 ? "s" : "")}");
            if (data.Summary.InfoCount > 0)
                parts.Add($"{data.Summary.InfoCount} info{(data.Summary.InfoCount > 1 ? "s" : "")}");
        }
        
        var summary = $"Found {string.Join(", ", parts)}";
        
        if (displayedCount < totalFound)
        {
            summary += $" (showing {displayedCount} of {totalFound})";
        }
        
        return summary;
    }
}