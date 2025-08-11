using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Tools;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Response builder for CodeMetricsTool that implements token-aware response building with strong typing
/// </summary>
public class CodeMetricsResponseBuilder : BaseResponseBuilder<CodeMetricsResult, CodeMetricsResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public CodeMetricsResponseBuilder(
        ILogger<CodeMetricsResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }
    
    public override Task<CodeMetricsResult> BuildResponseAsync(
        CodeMetricsResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Apply progressive reduction to metrics
        var reducedMetrics = data.Metrics;
        var originalCount = data.Metrics?.Count ?? 0;
        var wasReduced = false;
        
        if (data.Metrics != null && data.Metrics.Count > 0)
        {
            var originalTokens = _tokenEstimator.EstimateObject(data.Metrics);
            
            if (originalTokens > tokenBudget * 0.7) // Reserve 30% for metadata
            {
                var metricsBudget = (int)(tokenBudget * 0.7);
                reducedMetrics = ReduceMetrics(data.Metrics, metricsBudget);
                wasReduced = true;
            }
        }
        
        // Generate insights based on metrics
        var insights = GenerateInsights(data, context.ResponseMode);
        
        // Generate actions for next steps
        var actions = GenerateActions(data, (int)(tokenBudget * 0.15));
        
        // Update the input data with optimized/reduced content
        data.Metrics = reducedMetrics;
        
        // Update insights and actions with token-aware reductions
        data.Insights = ReduceInsights(insights, (int)(tokenBudget * 0.1));
        data.Actions = ReduceActions(actions, (int)(tokenBudget * 0.15));
        
        // Add truncation message if needed
        if (wasReduced && data.Insights != null)
        {
            data.Insights.Insert(0, $"⚠️ Token optimization applied. Showing {reducedMetrics?.Count ?? 0} of {originalCount} metrics.");
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
        data.Message = BuildSummary(data, reducedMetrics?.Count ?? 0, originalCount);
        
        return Task.FromResult(data);
    }
    
    protected override List<string> GenerateInsights(
        CodeMetricsResult data,
        string responseMode)
    {
        var insights = new List<string>();
        
        if (data.Metrics == null || data.Metrics.Count == 0)
        {
            insights.Add("No metrics available - ensure valid code element was analyzed");
        }
        else
        {
            // Analyze complexity
            var highComplexityMethods = data.Metrics
                .Where(m => m.CyclomaticComplexity > 10)
                .ToList();
            
            if (highComplexityMethods.Any())
            {
                var maxComplexity = highComplexityMethods.Max(m => m.CyclomaticComplexity);
                insights.Add($"⚠️ High complexity detected: {highComplexityMethods.Count} method(s) with complexity > 10 (max: {maxComplexity})");
                
                if (maxComplexity > 20)
                {
                    insights.Add("🔴 Critical: Methods with complexity > 20 are very difficult to test and maintain");
                }
            }
            else
            {
                var avgComplexity = data.Metrics.Average(m => m.CyclomaticComplexity);
                if (avgComplexity < 5)
                {
                    insights.Add($"✅ Good complexity levels (avg: {avgComplexity:F1}) - code is maintainable");
                }
            }
            
            // Analyze maintainability
            var lowMaintainabilityMethods = data.Metrics
                .Where(m => m.MaintainabilityIndex < 50)
                .ToList();
            
            if (lowMaintainabilityMethods.Any())
            {
                insights.Add($"⚠️ {lowMaintainabilityMethods.Count} method(s) have low maintainability index (< 50)");
            }
            
            // Analyze lines of code
            var largeMethods = data.Metrics
                .Where(m => m.LinesOfCode > 50)
                .ToList();
            
            if (largeMethods.Any())
            {
                var largestMethod = largeMethods.OrderByDescending(m => m.LinesOfCode).First();
                insights.Add($"Long methods detected: {largeMethods.Count} method(s) > 50 lines (largest: {largestMethod.LinesOfCode} lines)");
            }
            
            // Analyze inheritance depth
            if (data.Analysis != null)
            {
                if (data.Analysis.MaxDepthOfInheritance > 5)
                {
                    insights.Add($"Deep inheritance hierarchy (max depth: {data.Analysis.MaxDepthOfInheritance}) - consider composition over inheritance");
                }
                
                // Overall maintainability assessment
                if (data.Analysis.AverageMaintainabilityIndex < 50)
                {
                    insights.Add($"🔴 Poor average maintainability ({data.Analysis.AverageMaintainabilityIndex:F1}) - significant refactoring needed");
                }
                else if (data.Analysis.AverageMaintainabilityIndex < 70)
                {
                    insights.Add($"⚠️ Fair average maintainability ({data.Analysis.AverageMaintainabilityIndex:F1}) - some refactoring recommended");
                }
                else
                {
                    insights.Add($"✅ Good average maintainability ({data.Analysis.AverageMaintainabilityIndex:F1})");
                }
            }
        }
        
        if (responseMode == "summary")
        {
            insights.Add("Showing summary view - use 'detailed' mode for complete metrics breakdown");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        CodeMetricsResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Metrics?.Any() == true)
        {
            // Refactoring actions for high complexity
            var highComplexityMethods = data.Metrics.Where(m => m.CyclomaticComplexity > 10).ToList();
            if (highComplexityMethods.Any())
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_extract_method",
                    Description = "Extract methods to reduce complexity",
                    Category = "refactor",
                    Priority = 10
                });
                
                actions.Add(new AIAction
                {
                    Action = "csharp_goto_definition",
                    Description = "Navigate to complex methods for review",
                    Category = "navigate",
                    Priority = 9
                });
            }
            
            // Code quality actions
            actions.Add(new AIAction
            {
                Action = "csharp_find_unused_code",
                Description = "Find and remove unused code to improve metrics",
                Category = "analyze",
                Priority = 8
            });
            
            // Testing actions for complex code
            if (data.Metrics.Any(m => m.CyclomaticComplexity > 7))
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_generate_code",
                    Description = "Generate unit tests for complex methods",
                    Category = "generate",
                    Priority = 8,
                    Parameters = new Dictionary<string, object>
                    {
                        ["generationType"] = "UnitTest"
                    }
                });
            }
            
            // Dependency analysis for high complexity
            if (data.Analysis?.AverageComplexity > 10)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_dependency_analysis",
                    Description = "Analyze dependencies to reduce coupling",
                    Category = "analyze",
                    Priority = 7
                });
            }
            
            // Documentation for complex code
            if (highComplexityMethods.Any())
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_hover",
                    Description = "Review documentation for complex methods",
                    Category = "information",
                    Priority = 6
                });
            }
            
            // Clone detection for large methods
            var largeMethods = data.Metrics.Where(m => m.LinesOfCode > 50).ToList();
            if (largeMethods.Any())
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_code_clone_detection",
                    Description = "Check for duplicate code in large methods",
                    Category = "analyze",
                    Priority = 7
                });
            }
            
            // Type hierarchy for deep inheritance
            if (data.Analysis?.MaxDepthOfInheritance > 3)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_type_hierarchy",
                    Description = "Review inheritance hierarchy",
                    Category = "analyze",
                    Priority = 6
                });
            }
        }
        
        return actions;
    }
    
    private List<CodeMetricInfo>? ReduceMetrics(List<CodeMetricInfo> metrics, int tokenBudget)
    {
        var result = new List<CodeMetricInfo>();
        var currentTokens = 0;
        
        // Prioritize metrics by importance (worst metrics first)
        var prioritizedMetrics = metrics
            .OrderByDescending(m => GetMetricPriority(m))
            .ThenBy(m => m.Name);
        
        foreach (var metric in prioritizedMetrics)
        {
            var metricTokens = _tokenEstimator.EstimateObject(metric);
            
            if (currentTokens + metricTokens <= tokenBudget)
            {
                result.Add(metric);
                currentTokens += metricTokens;
            }
            else if (result.Count == 0 && metricTokens > tokenBudget)
            {
                // If no metrics fit and this one is too large, include a simplified version
                var simplified = new CodeMetricInfo
                {
                    Name = metric.Name,
                    Type = metric.Type,
                    CyclomaticComplexity = metric.CyclomaticComplexity,
                    MaintainabilityIndex = metric.MaintainabilityIndex,
                    LinesOfCode = metric.LinesOfCode
                    // Omit other fields to save space
                };
                result.Add(simplified);
                break;
            }
            else
            {
                break;
            }
        }
        
        return result;
    }
    
    private int GetMetricPriority(CodeMetricInfo metric)
    {
        var priority = 0;
        
        // Higher priority for problematic metrics
        
        // Cyclomatic complexity
        if (metric.CyclomaticComplexity > 20)
        {
            priority += 100;
        }
        else if (metric.CyclomaticComplexity > 10)
        {
            priority += 50;
        }
        else if (metric.CyclomaticComplexity > 7)
        {
            priority += 20;
        }
        
        // Maintainability index (lower is worse)
        if (metric.MaintainabilityIndex < 30)
        {
            priority += 80;
        }
        else if (metric.MaintainabilityIndex < 50)
        {
            priority += 40;
        }
        else if (metric.MaintainabilityIndex < 70)
        {
            priority += 20;
        }
        
        // Lines of code
        if (metric.LinesOfCode > 100)
        {
            priority += 60;
        }
        else if (metric.LinesOfCode > 50)
        {
            priority += 30;
        }
        else if (metric.LinesOfCode > 30)
        {
            priority += 10;
        }
        
        // Coupling
        if (metric.ClassCoupling > 20)
        {
            priority += 40;
        }
        else if (metric.ClassCoupling > 10)
        {
            priority += 20;
        }
        
        // Type-based priority
        if (metric.Type == "Method")
        {
            priority += 10; // Methods are usually more actionable
        }
        
        return priority;
    }
    
    private string BuildSummary(CodeMetricsResult data, int displayedCount, int totalCount)
    {
        if (totalCount == 0)
        {
            return "No metrics calculated";
        }
        
        var summary = $"Calculated metrics for {totalCount} code element(s)";
        
        if (data.Analysis != null)
        {
            var healthIndicator = data.Analysis.AverageMaintainabilityIndex switch
            {
                >= 70 => "✅",
                >= 50 => "⚠️",
                _ => "🔴"
            };
            
            var health = data.Analysis.AverageMaintainabilityIndex switch
            {
                >= 70 => "Good",
                >= 50 => "Fair",
                _ => "Poor"
            };
            
            summary += $" - Overall health: {healthIndicator} {health}";
        }
        
        if (displayedCount < totalCount)
        {
            summary += $" (showing {displayedCount} most critical)";
        }
        
        return summary;
    }
}