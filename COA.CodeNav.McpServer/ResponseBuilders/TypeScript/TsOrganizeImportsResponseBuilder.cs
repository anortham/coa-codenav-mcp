using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders.TypeScript;

/// <summary>
/// Response builder for TypeScript organize imports with token optimization
/// </summary>
public class TsOrganizeImportsResponseBuilder : BaseResponseBuilder<TsOrganizeImportsResult, TsOrganizeImportsResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public TsOrganizeImportsResponseBuilder(
        ILogger<TsOrganizeImportsResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }

    public override Task<TsOrganizeImportsResult> BuildResponseAsync(
        TsOrganizeImportsResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Apply progressive reduction to content
        var wasReduced = false;
        
        if (data.Changes != null && data.Changes.Count > 0)
        {
            // Don't reduce small change sets (<=10 changes)
            if (data.Changes.Count <= 10)
            {
                _logger?.LogDebug("TsOrganizeImportsResponseBuilder: Small change set ({Count} changes), keeping all", 
                    data.Changes.Count);
                wasReduced = false;
            }
            else
            {
                // Fix token budget if it's 0 or negative
                if (tokenBudget <= 0)
                {
                    tokenBudget = context.TokenLimit ?? 8000;
                    _logger?.LogDebug("TsOrganizeImportsResponseBuilder: Fixed token budget from 0 to {Budget}", tokenBudget);
                }
                
                var originalTokens = _tokenEstimator.EstimateObject(data.Changes);
                
                _logger?.LogDebug("TsOrganizeImportsResponseBuilder: Original changes: {Count}, Estimated tokens: {Tokens}, Budget: {Budget}", 
                    data.Changes.Count, originalTokens, tokenBudget);
                
                // Apply token optimization for large change sets
                if (originalTokens > tokenBudget * 0.7)
                {
                    var changeBudget = (int)(tokenBudget * 0.7);
                    data.Changes = ReduceChanges(data.Changes, changeBudget);
                    wasReduced = data.Changes?.Count < data.Summary?.TotalChanges;
                    
                    _logger?.LogDebug("TsOrganizeImportsResponseBuilder: Reduced from {Original} to {Reduced} changes", 
                        data.Summary?.TotalChanges ?? 0, data.Changes?.Count ?? 0);
                }
            }
        }
        
        // Reduce content size if still over budget
        if (data.UpdatedContent != null && _tokenEstimator.EstimateString(data.UpdatedContent) > tokenBudget * 0.5)
        {
            var maxContentLength = Math.Max(1000, (int)(tokenBudget * 0.3));
            if (data.UpdatedContent.Length > maxContentLength)
            {
                data.UpdatedContent = data.UpdatedContent[..maxContentLength] + "\n... (content truncated)";
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
        TsOrganizeImportsResult data,
        string? responseMode)
    {
        var insights = new List<string>();
        
        if (data.Changes == null || data.Changes.Count == 0)
        {
            insights.Add("✅ Imports are already well-organized - no changes needed");
            return insights;
        }
        
        var changeCount = data.Changes.Count;
        if (changeCount > 0)
        {
            insights.Add($"🔄 Organized {changeCount} import statement{(changeCount > 1 ? "s" : "")}");
        }
        
        // Analyze change patterns
        var importsAdded = data.Changes.Count(c => c.Description?.Contains("Add") == true);
        var importsRemoved = data.Changes.Count(c => c.Description?.Contains("Remove") == true);
        var importsReordered = data.Changes.Count(c => c.Description?.Contains("Reorder") == true);
        
        if (importsAdded > 0)
        {
            insights.Add($"Added {importsAdded} missing import{(importsAdded > 1 ? "s" : "")}");
        }
        
        if (importsRemoved > 0)
        {
            insights.Add($"Removed {importsRemoved} unused import{(importsRemoved > 1 ? "s" : "")}");
        }
        
        if (importsReordered > 0)
        {
            insights.Add($"Reordered {importsReordered} import{(importsReordered > 1 ? "s" : "")} for better organization");
        }
        
        insights.Add("Import organization improves code readability and maintainability");
        
        if (data.Meta?.Truncated == true)
        {
            insights.Add($"Results truncated - showing {data.Changes.Count} of {data.Summary?.TotalChanges} changes");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        TsOrganizeImportsResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Changes?.Any() == true)
        {
            // Suggest running diagnostics to verify no errors
            actions.Add(new AIAction
            {
                Action = "ts_get_diagnostics",
                Description = "Verify no TypeScript errors after import organization",
                Category = "verification",
                Priority = 8
            });
            
            // Suggest formatting if imports were changed significantly
            if (data.Changes.Count > 5)
            {
                actions.Add(new AIAction
                {
                    Action = "ts_format_document",
                    Description = "Format the document after import organization",
                    Category = "cleanup",
                    Priority = 6
                });
            }
        }
        else
        {
            // No changes made - suggest other analysis
            actions.Add(new AIAction
            {
                Action = "ts_document_symbols",
                Description = "Analyze document structure and symbols",
                Category = "analyze",
                Priority = 5
            });
        }
        
        return actions;
    }
    
    private List<TsFileChange>? ReduceChanges(List<TsFileChange> changes, int tokenBudget)
    {
        var result = new List<TsFileChange>();
        var currentTokens = 0;
        
        // Prioritize changes by importance: removals first, then additions, then reorderings
        var prioritized = changes
            .OrderBy(c => GetChangePriority(c.Description ?? ""))
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
                break;
            }
        }
        
        return result;
    }
    
    private int GetChangePriority(string description)
    {
        if (description.Contains("Remove"))
            return 0; // Highest priority - removing unused imports
        if (description.Contains("Add"))
            return 1; // Medium priority - adding missing imports
        if (description.Contains("Reorder"))
            return 2; // Lower priority - reordering for style
        return 3;
    }
    
    private string BuildSummary(TsOrganizeImportsResult data)
    {
        if (data.Changes == null || data.Changes.Count == 0)
        {
            return "Imports are already well-organized";
        }
        
        var displayedCount = data.Changes.Count;
        var totalCount = data.Summary?.TotalChanges ?? displayedCount;
        
        var summary = $"Organized imports with {displayedCount} change{(displayedCount > 1 ? "s" : "")}";
        
        if (displayedCount < totalCount)
        {
            summary += $" (showing {displayedCount} of {totalCount})";
        }
        
        return summary;
    }
}