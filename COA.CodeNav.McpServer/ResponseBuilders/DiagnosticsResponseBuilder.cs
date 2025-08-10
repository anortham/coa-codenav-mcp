using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Response builder for GetDiagnosticsTool that implements intelligent diagnostic reduction
/// </summary>
public class DiagnosticsResponseBuilder : BaseResponseBuilder<GetDiagnosticsToolResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public DiagnosticsResponseBuilder(
        ILogger<DiagnosticsResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }
    
    public override async Task<object> BuildResponseAsync(
        GetDiagnosticsToolResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Apply intelligent reduction to diagnostics
        var reducedDiagnostics = data.Diagnostics;
        var wasReduced = false;
        
        if (data.Diagnostics != null)
        {
            var originalTokens = _tokenEstimator.EstimateObject(data.Diagnostics);
            
            if (originalTokens > tokenBudget * 0.7)
            {
                var diagnosticBudget = (int)(tokenBudget * 0.7);
                reducedDiagnostics = ReduceDiagnostics(data.Diagnostics, diagnosticBudget);
                wasReduced = true;
            }
        }
        
        // Generate insights based on diagnostics
        var insights = GenerateInsights(data, context.ResponseMode);
        
        // Generate actions for fixing issues
        var actions = GenerateActions(data, (int)(tokenBudget * 0.2));
        
        // Build AI-optimized response
        var response = new AIOptimizedResponse
        {
            Format = "ai-optimized",
            Data = new AIResponseData
            {
                Summary = BuildSummary(data, reducedDiagnostics?.Count ?? 0),
                Results = reducedDiagnostics,
                Count = reducedDiagnostics?.Count ?? 0,
                ExtensionData = new Dictionary<string, object>
                {
                    ["byCategory"] = GroupByCategory(reducedDiagnostics),
                    ["bySeverity"] = GroupBySeverity(reducedDiagnostics),
                    ["fixableCount"] = reducedDiagnostics?.Count(d => d.HasCodeFix) ?? 0
                }
            },
            Insights = ReduceInsights(insights, (int)(tokenBudget * 0.1)),
            Actions = ReduceActions(actions, (int)(tokenBudget * 0.2)),
            Meta = CreateMetadata(startTime, wasReduced)
        };
        
        // Update token metadata
        response.Meta.TokenInfo = new TokenInfo
        {
            Estimated = _tokenEstimator.EstimateObject(response),
            Limit = context.TokenLimit ?? 10000,
            ReductionStrategy = wasReduced ? "severity-based" : null
        };
        
        return response;
    }
    
    protected override List<string> GenerateInsights(
        GetDiagnosticsToolResult data,
        string responseMode)
    {
        var insights = new List<string>();
        
        if (data.Summary?.TotalFound == 0)
        {
            insights.Add("✅ No diagnostics found - code is clean!");
            return insights;
        }
        
        // Error insights
        if (data.Summary?.ErrorCount > 0)
        {
            insights.Add($"⚠️ {data.Summary.ErrorCount} errors must be fixed before compilation");
            
            if (data.Summary.ErrorCount > 10)
            {
                insights.Add("High error count - consider fixing one file at a time");
            }
        }
        
        // Warning insights
        if (data.Summary?.WarningCount > 20)
        {
            insights.Add($"Found {data.Summary.WarningCount} warnings - consider enabling 'warnings as errors' to improve code quality");
        }
        
        // Analyzer insights
        if (data.Diagnostics?.Any(d => d.Category?.StartsWith("StyleCop") == true) == true)
        {
            var styleCount = data.Diagnostics.Count(d => d.Category?.StartsWith("StyleCop") == true);
            insights.Add($"StyleCop reported {styleCount} style issues - run code formatting to fix most automatically");
        }
        
        // Code fix insights
        var fixableCount = data.Diagnostics?.Count(d => d.HasCodeFix) ?? 0;
        if (fixableCount > 0)
        {
            insights.Add($"🔧 {fixableCount} issues have automatic code fixes available - use csharp_apply_code_fix");
        }
        
        // Pattern detection
        if (data.Diagnostics?.GroupBy(d => d.Id).Any(g => g.Count() > 5) == true)
        {
            var repeatedIssue = data.Diagnostics.GroupBy(d => d.Id)
                .OrderByDescending(g => g.Count())
                .First();
            insights.Add($"'{repeatedIssue.Key}' appears {repeatedIssue.Count()} times - consider bulk fixing with csharp_solution_wide_find_replace");
        }
        
        // File concentration
        if (data.Diagnostics?.GroupBy(d => d.Location?.FilePath).Any(g => g.Count() > 10) == true)
        {
            var problematicFile = data.Diagnostics
                .GroupBy(d => d.Location?.FilePath)
                .OrderByDescending(g => g.Count())
                .First();
            insights.Add($"File '{Path.GetFileName(problematicFile.Key)}' has {problematicFile.Count()} issues - needs focused attention");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        GetDiagnosticsToolResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Diagnostics?.Any() != true)
            return actions;
        
        // Fix actions
        if (data.Diagnostics.Any(d => d.HasCodeFix))
        {
            actions.Add(new AIAction
            {
                Action = "csharp_apply_code_fix",
                Description = "Apply available code fixes with csharp_apply_code_fix",
                Category = "fix",
                Priority = 10,
                Parameters = new Dictionary<string, object>
                {
                    ["fixableCount"] = data.Diagnostics.Count(d => d.HasCodeFix)
                }
            });
        }
        
        // Format action
        if (data.Diagnostics.Any(d => d.Category?.Contains("Formatting") == true))
        {
            actions.Add(new AIAction
            {
                Action = "csharp_format_document",
                Description = "Format documents to fix style issues with csharp_format_document",
                Category = "format",
                Priority = 9
            });
        }
        
        // Add missing usings
        if (data.Diagnostics.Any(d => d.Id == "CS0246" || d.Id == "CS0103"))
        {
            actions.Add(new AIAction
            {
                Action = "csharp_add_missing_usings",
                Description = "Add missing using directives with csharp_add_missing_usings",
                Category = "fix",
                Priority = 9
            });
        }
        
        // Bulk operations
        if (data.Summary?.TotalFound > 20)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_solution_wide_find_replace",
                Description = "Use csharp_solution_wide_find_replace for repeated issues",
                Category = "refactor",
                Priority = 8
            });
        }
        
        // Analysis actions
        if (data.Summary?.ErrorCount > 0)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_get_diagnostics",
                Description = "Filter to errors only: scope='file', severities=['Error']",
                Category = "filter",
                Priority = 8,
                Parameters = new Dictionary<string, object>
                {
                    ["scope"] = "file",
                    ["severities"] = new[] { "Error" }
                }
            });
        }
        
        // Refresh action
        if (data.Meta?.Truncated == true)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_refresh_workspace",
                Description = "Refresh workspace with csharp_refresh_workspace to get latest diagnostics",
                Category = "refresh",
                Priority = 6
            });
        }
        
        return actions;
    }
    
    private List<DiagnosticInfo>? ReduceDiagnostics(List<DiagnosticInfo> diagnostics, int tokenBudget)
    {
        var result = new List<DiagnosticInfo>();
        var currentTokens = 0;
        
        // Prioritize diagnostics by severity and fixability
        var prioritizedDiagnostics = diagnostics
            .OrderByDescending(d => GetDiagnosticPriority(d))
            .ThenBy(d => d.Location?.FilePath)
            .ThenBy(d => d.Location?.Line);
        
        foreach (var diagnostic in prioritizedDiagnostics)
        {
            var diagnosticTokens = _tokenEstimator.EstimateObject(diagnostic);
            if (currentTokens + diagnosticTokens <= tokenBudget)
            {
                result.Add(diagnostic);
                currentTokens += diagnosticTokens;
            }
            else
            {
                break;
            }
        }
        
        return result;
    }
    
    private int GetDiagnosticPriority(DiagnosticInfo diagnostic)
    {
        // Prioritize by severity
        var priority = diagnostic.Severity switch
        {
            "Error" => 1000,
            "Warning" => 100,
            "Info" => 10,
            "Hidden" => 1,
            _ => 5
        };
        
        // Boost priority for fixable issues
        if (diagnostic.HasCodeFix)
        {
            priority += 50;
        }
        
        // Boost priority for compilation errors
        if (diagnostic.Category == "Compiler")
        {
            priority += 25;
        }
        
        return priority;
    }
    
    private Dictionary<string, int> GroupByCategory(List<DiagnosticInfo>? diagnostics)
    {
        if (diagnostics == null) return new Dictionary<string, int>();
        
        return diagnostics
            .GroupBy(d => d.Category ?? "Unknown")
            .ToDictionary(g => g.Key, g => g.Count());
    }
    
    private Dictionary<string, int> GroupBySeverity(List<DiagnosticInfo>? diagnostics)
    {
        if (diagnostics == null) return new Dictionary<string, int>();
        
        return diagnostics
            .GroupBy(d => d.Severity ?? "Unknown")
            .ToDictionary(g => g.Key, g => g.Count());
    }
    
    private string BuildSummary(GetDiagnosticsToolResult data, int displayedCount)
    {
        var total = data.Summary?.TotalFound ?? 0;
        
        if (total == 0)
        {
            return "No diagnostics found - code is clean";
        }
        
        var parts = new List<string>();
        
        if (data.Summary?.ErrorCount > 0)
            parts.Add($"{data.Summary.ErrorCount} errors");
        if (data.Summary?.WarningCount > 0)
            parts.Add($"{data.Summary.WarningCount} warnings");
        if (data.Summary?.InfoCount > 0)
            parts.Add($"{data.Summary.InfoCount} info");
        
        var summary = $"Found {string.Join(", ", parts)}";
        
        if (displayedCount < total)
        {
            summary += $" (showing {displayedCount} most important)";
        }
        
        return summary;
    }
}