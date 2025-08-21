using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders.TypeScript;

/// <summary>
/// Response builder for TypeScript add missing imports with token optimization
/// </summary>
public class TsAddMissingImportsResponseBuilder : BaseResponseBuilder<TsAddMissingImportsResult, TsAddMissingImportsResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public TsAddMissingImportsResponseBuilder(
        ILogger<TsAddMissingImportsResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }

    public override Task<TsAddMissingImportsResult> BuildResponseAsync(
        TsAddMissingImportsResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Apply progressive reduction to changes and content
        var wasReduced = false;
        
        if (data.Changes != null && data.Changes.Count > 0)
        {
            // Don't reduce small change sets (<=15 changes)
            if (data.Changes.Count <= 15)
            {
                _logger?.LogDebug("TsAddMissingImportsResponseBuilder: Small change set ({Count} changes), keeping all", 
                    data.Changes.Count);
                wasReduced = false;
            }
            else
            {
                // Fix token budget if it's 0 or negative
                if (tokenBudget <= 0)
                {
                    tokenBudget = context.TokenLimit ?? 8000;
                    _logger?.LogDebug("TsAddMissingImportsResponseBuilder: Fixed token budget from 0 to {Budget}", tokenBudget);
                }
                
                var originalTokens = _tokenEstimator.EstimateObject(data.Changes);
                
                _logger?.LogDebug("TsAddMissingImportsResponseBuilder: Original changes: {Count}, Estimated tokens: {Tokens}, Budget: {Budget}", 
                    data.Changes.Count, originalTokens, tokenBudget);
                
                // Apply token optimization for large change sets
                if (originalTokens > tokenBudget * 0.6)
                {
                    var changeBudget = (int)(tokenBudget * 0.6);
                    data.Changes = ReduceChanges(data.Changes, changeBudget);
                    wasReduced = data.Changes?.Count < data.Summary?.TotalImportsAdded;
                    
                    _logger?.LogDebug("TsAddMissingImportsResponseBuilder: Reduced from {Original} to {Reduced} changes", 
                        data.Summary?.TotalImportsAdded ?? 0, data.Changes?.Count ?? 0);
                }
            }
        }
        
        // Reduce missing imports list if necessary
        if (data.MissingImports != null && data.MissingImports.Count > 20)
        {
            data.MissingImports = data.MissingImports.Take(20).ToList();
            wasReduced = true;
        }
        
        // Reduce content size if still over budget (for preview mode)
        if (data.UpdatedContent != null && _tokenEstimator.EstimateString(data.UpdatedContent) > tokenBudget * 0.4)
        {
            var maxContentLength = Math.Max(1500, (int)(tokenBudget * 0.3));
            if (data.UpdatedContent.Length > maxContentLength)
            {
                data.UpdatedContent = data.UpdatedContent[..maxContentLength] + "\n... (content truncated)";
                wasReduced = true;
            }
        }
        
        if (data.OriginalContent != null && _tokenEstimator.EstimateString(data.OriginalContent) > tokenBudget * 0.3)
        {
            var maxContentLength = Math.Max(1000, (int)(tokenBudget * 0.2));
            if (data.OriginalContent.Length > maxContentLength)
            {
                data.OriginalContent = data.OriginalContent[..maxContentLength] + "\n... (content truncated)";
                wasReduced = true;
            }
        }
        
        // Generate insights based on changes
        var insights = GenerateInsights(data, context.ResponseMode);
        
        // Generate actions for next steps
        var actions = GenerateActions(data, (int)(tokenBudget * 0.15));
        
        // Update insights and actions with token-aware reductions
        data.Insights = ReduceInsights(insights, (int)(tokenBudget * 0.1));
        data.Actions = ReduceActions(actions, (int)(tokenBudget * 0.15));
        
        // Update metadata to reflect the optimization
        data.Meta = new ToolExecutionMetadata
        {
            Mode = context.ResponseMode ?? "optimized",
            Truncated = wasReduced,
            Tokens = _tokenEstimator.EstimateObject(data),
            ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
        };
        
        data.Success = true;
        data.Message = BuildSummary(data);
        
        return Task.FromResult(data);
    }
    
    protected override List<string> GenerateInsights(
        TsAddMissingImportsResult data,
        string? responseMode)
    {
        var insights = new List<string>();
        
        if (data.Changes == null || data.Changes.Count == 0)
        {
            if (data.MissingImports?.Any() == true)
            {
                insights.Add($"⚠️ Found {data.MissingImports.Count} missing import error(s) but no automatic fixes available");
                insights.Add("Manual import resolution may be required for custom modules or uninstalled packages");
            }
            else
            {
                insights.Add("✅ No missing imports detected - all symbols are properly imported");
            }
            return insights;
        }
        
        var changeCount = data.Changes.Count;
        var errorCount = data.Summary?.ErrorsFixed ?? 0;
        
        insights.Add($"✅ Successfully added {changeCount} missing import{(changeCount > 1 ? "s" : "")}");
        
        if (errorCount > 0)
        {
            insights.Add($"🐛 Fixed {errorCount} TypeScript error{(errorCount > 1 ? "s" : "")} by adding imports");
        }
        
        // Analyze import patterns
        var importStatements = data.Changes?.Count(c => c.NewText?.StartsWith("import ") == true) ?? 0;
        if (importStatements > 0)
        {
            insights.Add($"Added {importStatements} new import statement{(importStatements > 1 ? "s" : "")} to the file");
        }
        
        // Check for remaining missing imports
        var totalMissing = data.MissingImports?.Count ?? 0;
        var errorsFixed = data.Summary?.ErrorsFixed ?? 0;
        if (totalMissing > errorsFixed)
        {
            insights.Add($"⚠️ {totalMissing - errorsFixed} missing import(s) may require manual resolution");
            insights.Add("Check if required packages are installed or if symbols exist in available modules");
        }
        
        insights.Add("Run ts_organize_imports to organize the newly added imports");
        
        if (data.Meta?.Truncated == true)
        {
            insights.Add($"Results truncated - showing subset of changes and missing imports");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        TsAddMissingImportsResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Changes?.Any() == true)
        {
            // Suggest organizing imports after adding them
            actions.Add(new AIAction
            {
                Action = "ts_organize_imports",
                Description = "Organize the newly added imports for better structure",
                Category = "cleanup",
                Priority = 9
            });
            
            // Suggest running diagnostics to verify fixes
            actions.Add(new AIAction
            {
                Action = "ts_get_diagnostics",
                Description = "Verify all TypeScript errors are resolved",
                Category = "verification",
                Priority = 8
            });
        }
        
        // If there are unresolved missing imports, suggest analysis
        if (data.MissingImports?.Count > (data.Summary?.ErrorsFixed ?? 0))
        {
            actions.Add(new AIAction
            {
                Action = "ts_symbol_search",
                Description = "Search for unresolved symbols in the project",
                Category = "analyze",
                Priority = 7
            });
            
            actions.Add(new AIAction
            {
                Action = "package_analysis",
                Description = "Analyze package.json for missing dependencies",
                Category = "analyze",
                Priority = 6
            });
        }
        
        return actions;
    }
    
    private List<TsFileChange>? ReduceChanges(List<TsFileChange> changes, int tokenBudget)
    {
        var result = new List<TsFileChange>();
        var currentTokens = 0;
        
        // Prioritize actual import additions over other changes
        var prioritized = changes
            .OrderBy(c => c.NewText?.StartsWith("import ") == true ? 0 : 1)
            .ThenBy(c => c.StartPosition);
        
        foreach (var change in prioritized)
        {
            var changeTokens = _tokenEstimator.EstimateObject(change);
            
            if (currentTokens + changeTokens <= tokenBudget)
            {
                result.Add(change);
                currentTokens += changeTokens;
            }
            else
            {
                // Always include at least one import statement if available
                if (result.Count == 0 && change.NewText?.StartsWith("import ") == true)
                {
                    result.Add(change);
                }
                break;
            }
        }
        
        return result;
    }
    
    private string BuildSummary(TsAddMissingImportsResult data)
    {
        if (data.Changes == null || data.Changes.Count == 0)
        {
            if (data.MissingImports?.Any() == true)
            {
                return $"Found {data.MissingImports.Count} missing import(s) but no automatic fixes available";
            }
            return "No missing imports detected";
        }
        
        var displayedCount = data.Changes.Count;
        var totalCount = data.Summary?.TotalImportsAdded ?? displayedCount;
        var errorCount = data.Summary?.ErrorsFixed ?? 0;
        
        var summary = $"Added {displayedCount} missing import{(displayedCount > 1 ? "s" : "")}";
        
        if (errorCount > 0)
        {
            summary += $" (fixed {errorCount} error{(errorCount > 1 ? "s" : "")})";
        }
        
        if (displayedCount < totalCount)
        {
            summary += $" (showing {displayedCount} of {totalCount})";
        }
        
        return summary;
    }
}