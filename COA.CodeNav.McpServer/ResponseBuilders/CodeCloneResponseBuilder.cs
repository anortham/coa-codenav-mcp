using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Tools;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Response builder for CodeCloneDetectionTool that implements token-aware response building with strong typing
/// </summary>
public class CodeCloneResponseBuilder : BaseResponseBuilder<CodeCloneDetectionResult, CodeCloneDetectionResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public CodeCloneResponseBuilder(
        ILogger<CodeCloneResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }
    
    public override Task<CodeCloneDetectionResult> BuildResponseAsync(
        CodeCloneDetectionResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Apply progressive reduction to clone groups
        var reducedGroups = data.CloneGroups;
        var originalCount = data.CloneGroups?.Count ?? 0;
        var wasReduced = false;
        
        if (data.CloneGroups != null && data.CloneGroups.Count > 0)
        {
            var originalTokens = _tokenEstimator.EstimateObject(data.CloneGroups);
            
            if (originalTokens > tokenBudget * 0.7) // Reserve 30% for metadata
            {
                var groupBudget = (int)(tokenBudget * 0.7);
                reducedGroups = ReduceCloneGroups(data.CloneGroups, groupBudget);
                wasReduced = true;
            }
        }
        
        // Generate insights based on clone detection results
        var insights = GenerateInsights(data, context.ResponseMode);
        
        // Generate actions for next steps
        var actions = GenerateActions(data, (int)(tokenBudget * 0.15));
        
        // Update the input data with optimized/reduced content
        data.CloneGroups = reducedGroups;
        
        // Update insights and actions with token-aware reductions
        data.Insights = ReduceInsights(insights, (int)(tokenBudget * 0.1));
        data.Actions = ReduceActions(actions, (int)(tokenBudget * 0.15));
        
        // Update metadata to reflect the optimization
        if (data.Summary != null)
        {
            data.Summary.Returned = reducedGroups?.Sum(g => g.Clones?.Count ?? 0) ?? 0;
        }
        
        // Add truncation message if needed
        if (wasReduced && data.Insights != null)
        {
            data.Insights.Insert(0, $"⚠️ Token optimization applied. Showing {reducedGroups?.Count ?? 0} of {originalCount} clone groups to fit context window.");
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
        data.Message = BuildSummary(data, reducedGroups?.Count ?? 0, originalCount);
        
        return Task.FromResult(data);
    }
    
    protected override List<string> GenerateInsights(
        CodeCloneDetectionResult data,
        string responseMode)
    {
        var insights = new List<string>();
        
        if (data.CloneGroups == null || data.CloneGroups.Count == 0)
        {
            insights.Add("No code clones detected - code appears to have minimal duplication");
        }
        else
        {
            var totalClones = data.CloneGroups.Sum(g => g.Clones?.Count ?? 0);
            
            if (totalClones > 50)
            {
                insights.Add($"High duplication detected ({totalClones} clones) - significant refactoring opportunities exist");
            }
            else if (totalClones > 20)
            {
                insights.Add($"Moderate duplication detected ({totalClones} clones) - consider extracting common functionality");
            }
            else
            {
                insights.Add($"Low duplication detected ({totalClones} clones) - codebase is reasonably DRY");
            }
            
            // Analyze clone types
            var highSimilarityGroups = data.CloneGroups.Where(g => g.SimilarityScore > 0.9).ToList();
            if (highSimilarityGroups.Any())
            {
                insights.Add($"Found {highSimilarityGroups.Count} groups with >90% similarity - these are prime refactoring candidates");
            }
            
            // Check for large clone groups
            var largeGroups = data.CloneGroups.Where(g => (g.Clones?.Count ?? 0) > 5).ToList();
            if (largeGroups.Any())
            {
                insights.Add($"{largeGroups.Count} groups have 5+ instances - consider creating shared utilities or base classes");
            }
        }
        
        if (data.Analysis != null)
        {
            if (data.Analysis.LargestGroupSize > 10)
            {
                insights.Add($"Largest clone group has {data.Analysis.LargestGroupSize} instances - critical refactoring needed");
            }
            
            // Note: TotalLinesOfDuplication not available in CloneAnalysis
            var totalLines = data.CloneGroups?.Sum(g => g.Clones?.Sum(c => c.LineCount) ?? 0) ?? 0;
            if (totalLines > 1000)
            {
                insights.Add($"{totalLines} total lines duplicated - significant technical debt");
            }
        }
        
        if (responseMode == "summary")
        {
            insights.Add("Showing summary view - use 'detailed' mode for complete clone information");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        CodeCloneDetectionResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.CloneGroups?.Any() == true)
        {
            // Refactoring actions
            actions.Add(new AIAction
            {
                Action = "csharp_extract_method",
                Description = "Extract duplicated code into reusable methods",
                Category = "refactor",
                Priority = 10
            });
            
            actions.Add(new AIAction
            {
                Action = "csharp_goto_definition",
                Description = "Navigate to clone locations to understand context",
                Category = "navigate",
                Priority = 9
            });
            
            // Analysis actions
            actions.Add(new AIAction
            {
                Action = "csharp_code_metrics",
                Description = "Analyze complexity metrics for duplicated code",
                Category = "analyze",
                Priority = 8
            });
            
            // If there are many clones, suggest creating base classes
            if (data.CloneGroups.Any(g => (g.Clones?.Count ?? 0) > 3))
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_generate_code",
                    Description = "Generate base class or interface for common functionality",
                    Category = "generate",
                    Priority = 8
                });
            }
            
            // Suggest find and replace for simple duplications
            if (data.CloneGroups.Any(g => g.SimilarityScore > 0.95))
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_solution_wide_find_replace",
                    Description = "Use find-replace to standardize nearly identical code",
                    Category = "refactor",
                    Priority = 7
                });
            }
        }
        
        // Filter actions
        if (data.Analysis?.TotalGroups > 10)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_code_clone_detection",
                Description = "Re-run with higher similarity threshold to focus on exact duplicates",
                Category = "filter",
                Priority = 6,
                Parameters = new Dictionary<string, object>
                {
                    ["similarityThreshold"] = 0.95
                }
            });
            
            actions.Add(new AIAction
            {
                Action = "csharp_code_clone_detection",
                Description = "Re-run with larger minimum lines to find significant duplications",
                Category = "filter",
                Priority = 6,
                Parameters = new Dictionary<string, object>
                {
                    ["minLines"] = 20
                }
            });
        }
        
        return actions;
    }
    
    private List<CloneGroup>? ReduceCloneGroups(List<CloneGroup> groups, int tokenBudget)
    {
        var result = new List<CloneGroup>();
        var currentTokens = 0;
        
        // Prioritize groups by importance
        var prioritizedGroups = groups
            .OrderByDescending(g => GetCloneGroupPriority(g))
            .ThenByDescending(g => g.SimilarityScore);
        
        foreach (var group in prioritizedGroups)
        {
            var groupTokens = _tokenEstimator.EstimateObject(group);
            
            if (currentTokens + groupTokens <= tokenBudget)
            {
                result.Add(group);
                currentTokens += groupTokens;
            }
            else if (result.Count == 0 && groupTokens > tokenBudget)
            {
                // If no groups fit and this one is too large, include it anyway
                // but truncate the clones within it
                var truncatedGroup = new CloneGroup
                {
                    Id = group.Id,
                    SimilarityScore = group.SimilarityScore,
                    Clones = group.Clones?.Take(2).ToList() // Show just first 2 clones
                };
                result.Add(truncatedGroup);
                break;
            }
            else
            {
                break;
            }
        }
        
        return result;
    }
    
    private int GetCloneGroupPriority(CloneGroup group)
    {
        var priority = 0;
        
        // Higher priority for more instances
        priority += (group.Clones?.Count ?? 0) * 10;
        
        // Higher priority for higher similarity
        priority += (int)(group.SimilarityScore * 100);
        
        // Higher priority for larger code blocks
        if (group.Clones?.FirstOrDefault() != null)
        {
            var firstClone = group.Clones.First();
            var lines = (firstClone.Location.EndLine - firstClone.Location.Line + 1);
            priority += lines * 2;
        }
        
        return priority;
    }
    
    private string BuildSummary(CodeCloneDetectionResult data, int displayedCount, int totalCount)
    {
        if (totalCount == 0)
        {
            return "No code clones detected in the solution";
        }
        
        var totalClones = data.CloneGroups?.Sum(g => g.Clones?.Count ?? 0) ?? 0;
        
        if (displayedCount < totalCount)
        {
            return $"Found {totalCount} clone groups with {data.Analysis?.TotalClones ?? totalClones} total clones, showing {displayedCount} most significant groups";
        }
        
        return $"Found {totalCount} clone groups with {totalClones} total clones";
    }
}