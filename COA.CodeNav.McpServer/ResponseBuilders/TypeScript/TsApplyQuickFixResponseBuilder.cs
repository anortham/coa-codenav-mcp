using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders.TypeScript;

/// <summary>
/// Response builder for TypeScript apply quick fix with token optimization
/// </summary>
public class TsApplyQuickFixResponseBuilder : BaseResponseBuilder<TsApplyQuickFixResult, TsApplyQuickFixResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public TsApplyQuickFixResponseBuilder(
        ILogger<TsApplyQuickFixResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }

    public override Task<TsApplyQuickFixResult> BuildResponseAsync(
        TsApplyQuickFixResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Apply progressive reduction to available fixes and content
        var wasReduced = false;
        
        if (data.AvailableFixes != null && data.AvailableFixes.Count > 0)
        {
            // Don't reduce small fix sets (<=10 fixes)
            if (data.AvailableFixes.Count <= 10)
            {
                _logger?.LogDebug("TsApplyQuickFixResponseBuilder: Small fix set ({Count} fixes), keeping all", 
                    data.AvailableFixes.Count);
                wasReduced = false;
            }
            else
            {
                // Fix token budget if it's 0 or negative
                if (tokenBudget <= 0)
                {
                    tokenBudget = context.TokenLimit ?? 8000;
                    _logger?.LogDebug("TsApplyQuickFixResponseBuilder: Fixed token budget from 0 to {Budget}", tokenBudget);
                }
                
                var originalTokens = _tokenEstimator.EstimateObject(data.AvailableFixes);
                
                _logger?.LogDebug("TsApplyQuickFixResponseBuilder: Original fixes: {Count}, Estimated tokens: {Tokens}, Budget: {Budget}", 
                    data.AvailableFixes.Count, originalTokens, tokenBudget);
                
                // Apply token optimization for large fix sets
                if (originalTokens > tokenBudget * 0.6)
                {
                    var fixBudget = (int)(tokenBudget * 0.6);
                    data.AvailableFixes = ReduceFixes(data.AvailableFixes, fixBudget);
                    wasReduced = data.AvailableFixes?.Count < data.Summary?.AvailableFixesCount;
                    
                    _logger?.LogDebug("TsApplyQuickFixResponseBuilder: Reduced from {Original} to {Reduced} fixes", 
                        data.Summary?.AvailableFixesCount ?? 0, data.AvailableFixes?.Count ?? 0);
                }
            }
        }
        
        // Reduce changes list if necessary
        if (data.Changes != null && data.Changes.Count > 20)
        {
            data.Changes = data.Changes.Take(20).ToList();
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
        
        // Generate insights based on fixes and changes
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
        TsApplyQuickFixResult data,
        string? responseMode)
    {
        var insights = new List<string>();
        
        // Applied fix insights
        if (data.AppliedFix != null)
        {
            insights.Add($"✅ Successfully applied quick fix: {data.AppliedFix.Description}");
            
            if (data.Changes?.Count > 0)
            {
                insights.Add($"Made {data.Changes.Count} change{(data.Changes.Count > 1 ? "s" : "")} to the file");
            }
        }
        else if (data.AvailableFixes?.Any() == true)
        {
            var fixCount = data.AvailableFixes.Count;
            insights.Add($"🔧 Found {fixCount} available quick fix{(fixCount > 1 ? "es" : "")} at this location");
            
            // Analyze available fix types
            var importFixes = data.AvailableFixes.Count(f => f.Description?.Contains("import") == true);
            var syntaxFixes = data.AvailableFixes.Count(f => f.Description?.Contains("syntax") == true || 
                                                          f.Description?.Contains("missing") == true);
            var refactorFixes = data.AvailableFixes.Count(f => f.Description?.Contains("refactor") == true);
            
            if (importFixes > 0)
                insights.Add($"Import-related fixes available: {importFixes}");
            if (syntaxFixes > 0)
                insights.Add($"Syntax/error fixes available: {syntaxFixes}");
            if (refactorFixes > 0)
                insights.Add($"Refactoring actions available: {refactorFixes}");
        }
        else
        {
            insights.Add("ℹ️ No quick fixes available at this location");
            insights.Add("Position may not have TypeScript errors or the error type may not have automatic fixes");
        }
        
        // Preview vs applied insights
        if (data.Query?.Preview == true)
        {
            insights.Add("Preview mode - no changes were written to disk");
        }
        else if (data.AppliedFix != null)
        {
            insights.Add("Changes have been applied to the file");
            insights.Add("Run ts_get_diagnostics to verify the fix resolved the issue");
        }
        
        if (data.Meta?.Truncated == true)
        {
            insights.Add($"Results truncated - showing subset of available fixes");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        TsApplyQuickFixResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.AppliedFix != null)
        {
            // Fix was applied - suggest verification
            actions.Add(new AIAction
            {
                Action = "ts_get_diagnostics",
                Description = "Verify the quick fix resolved the TypeScript error",
                Category = "verification",
                Priority = 9
            });
            
            // If imports were likely modified, suggest organization
            if (data.AppliedFix.Description?.Contains("import") == true)
            {
                actions.Add(new AIAction
                {
                    Action = "ts_organize_imports",
                    Description = "Organize imports after applying import-related fix",
                    Category = "cleanup",
                    Priority = 7
                });
            }
        }
        else if (data.AvailableFixes?.Any() == true)
        {
            // Fixes available but none applied - suggest applying them
            var mostRelevantFix = data.AvailableFixes.FirstOrDefault();
            if (mostRelevantFix != null)
            {
                actions.Add(new AIAction
                {
                    Action = "ts_apply_quick_fix",
                    Description = $"Apply the fix: {mostRelevantFix.Description}",
                    Category = "fix",
                    Priority = 10
                });
            }
            
            // Suggest looking at all available options
            if (data.AvailableFixes.Count > 1)
            {
                actions.Add(new AIAction
                {
                    Action = "review_available_fixes",
                    Description = $"Review all {data.AvailableFixes.Count} available quick fixes",
                    Category = "analyze",
                    Priority = 8
                });
            }
        }
        else
        {
            // No fixes available - suggest other approaches
            actions.Add(new AIAction
            {
                Action = "ts_get_diagnostics",
                Description = "Check for TypeScript errors that might have quick fixes",
                Category = "analyze",
                Priority = 6
            });
            
            actions.Add(new AIAction
            {
                Action = "ts_goto_definition",
                Description = "Navigate to symbol definition for manual inspection",
                Category = "navigate",
                Priority = 5
            });
        }
        
        return actions;
    }
    
    private List<QuickFixInfo>? ReduceFixes(List<QuickFixInfo> fixes, int tokenBudget)
    {
        var result = new List<QuickFixInfo>();
        var currentTokens = 0;
        
        // Prioritize fixes by type: syntax errors > import fixes > refactoring
        var prioritized = fixes
            .OrderBy(f => GetFixPriority(f.Description ?? ""))
            .ThenBy(f => f.Description);
        
        foreach (var fix in prioritized)
        {
            var fixTokens = _tokenEstimator.EstimateObject(fix);
            
            if (currentTokens + fixTokens <= tokenBudget)
            {
                result.Add(fix);
                currentTokens += fixTokens;
            }
            else
            {
                // Always include at least one fix if available
                if (result.Count == 0)
                {
                    result.Add(fix);
                }
                break;
            }
        }
        
        return result;
    }
    
    private int GetFixPriority(string description)
    {
        var lower = description.ToLowerInvariant();
        
        if (lower.Contains("error") || lower.Contains("syntax") || lower.Contains("missing"))
            return 0; // Highest priority - error fixes
        if (lower.Contains("import") || lower.Contains("add"))
            return 1; // Medium priority - import fixes
        if (lower.Contains("refactor") || lower.Contains("extract"))
            return 2; // Lower priority - refactoring
        
        return 3; // Lowest priority - other fixes
    }
    
    private string BuildSummary(TsApplyQuickFixResult data)
    {
        if (data.AppliedFix != null)
        {
            var changeCount = data.Changes?.Count ?? 0;
            var summary = $"Applied quick fix: {data.AppliedFix.Description}";
            
            if (changeCount > 0)
            {
                summary += $" ({changeCount} change{(changeCount > 1 ? "s" : "")})";
            }
            
            return summary;
        }
        
        if (data.AvailableFixes?.Any() == true)
        {
            var displayedCount = data.AvailableFixes.Count;
            var totalCount = data.Summary?.AvailableFixesCount ?? displayedCount;
            
            var summary = $"Found {displayedCount} available quick fix{(displayedCount > 1 ? "es" : "")}";
            
            if (displayedCount < totalCount)
            {
                summary += $" (showing {displayedCount} of {totalCount})";
            }
            
            return summary;
        }
        
        return "No quick fixes available at this location";
    }
}