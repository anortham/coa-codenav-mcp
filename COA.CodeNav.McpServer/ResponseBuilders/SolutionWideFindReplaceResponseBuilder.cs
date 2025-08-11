using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Tools;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Response builder for SolutionWideFindReplaceTool that implements token-aware response building with strong typing
/// </summary>
public class SolutionWideFindReplaceResponseBuilder : BaseResponseBuilder<SolutionWideFindReplaceResult, SolutionWideFindReplaceResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public SolutionWideFindReplaceResponseBuilder(
        ILogger<SolutionWideFindReplaceResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }
    
    public override Task<SolutionWideFindReplaceResult> BuildResponseAsync(
        SolutionWideFindReplaceResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Apply progressive reduction to matched files
        var reducedFiles = data.MatchedFiles;
        var originalCount = data.MatchedFiles?.Count ?? 0;
        var wasReduced = false;
        
        if (data.MatchedFiles != null && data.MatchedFiles.Count > 0)
        {
            var originalTokens = _tokenEstimator.EstimateObject(data.MatchedFiles);
            
            if (originalTokens > tokenBudget * 0.7) // Reserve 30% for metadata
            {
                var changesBudget = (int)(tokenBudget * 0.7);
                reducedFiles = ReduceMatchedFiles(data.MatchedFiles, changesBudget);
                wasReduced = true;
            }
        }
        
        // Generate insights based on find/replace results
        var insights = GenerateInsights(data, context.ResponseMode);
        
        // Generate actions for next steps
        var actions = GenerateActions(data, (int)(tokenBudget * 0.15));
        
        // Update the input data with optimized/reduced content
        data.MatchedFiles = reducedFiles;
        
        // Update insights and actions with token-aware reductions
        data.Insights = ReduceInsights(insights, (int)(tokenBudget * 0.1));
        data.Actions = ReduceActions(actions, (int)(tokenBudget * 0.15));
        
        // Update metadata to reflect the optimization
        if (data.Summary != null)
        {
            data.Summary.Returned = reducedFiles?.Count ?? 0;
        }
        
        // Add truncation message if needed
        if (wasReduced && data.Insights != null)
        {
            var totalMatches = data.MatchedFiles?.Sum(f => f.Matches?.Count ?? 0) ?? 0;
            var originalMatches = data.TotalMatches;
            data.Insights.Insert(0, $"⚠️ Token optimization applied. Showing {reducedFiles?.Count ?? 0} of {originalCount} files with {totalMatches} of {originalMatches} matches.");
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
        data.Message = BuildSummary(data, reducedFiles?.Count ?? 0, originalCount);
        
        return Task.FromResult(data);
    }
    
    protected override List<string> GenerateInsights(
        SolutionWideFindReplaceResult data,
        string responseMode)
    {
        var insights = new List<string>();
        
        var totalFiles = data.MatchedFiles?.Count ?? 0;
        var totalMatches = data.TotalMatches;
        
        if (totalMatches == 0)
        {
            insights.Add("No matches found - pattern may be too specific or not present in codebase");
        }
        else
        {
            // Scale of changes
            if (totalMatches > 100)
            {
                insights.Add($"Large-scale change detected ({totalMatches} matches) - review carefully before applying");
            }
            else if (totalMatches > 50)
            {
                insights.Add($"Moderate number of changes ({totalMatches} matches) - consider testing after applying");
            }
            
            // File distribution
            if (totalFiles > 0)
            {
                var avgMatchesPerFile = totalMatches / (double)totalFiles;
                if (avgMatchesPerFile > 10)
                {
                    insights.Add($"High concentration of matches (avg {avgMatchesPerFile:F1} per file) - pattern may be in generated or duplicated code");
                }
                else if (totalFiles > 20)
                {
                    insights.Add($"Changes spread across {totalFiles} files - wide impact on codebase");
                }
            }
            
            // Pattern analysis - check if it looks like regex
            if (data.Query?.TargetSymbol?.Contains(".*") == true || 
                data.Query?.TargetSymbol?.Contains("\\w") == true ||
                data.Query?.TargetSymbol?.Contains("^") == true ||
                data.Query?.TargetSymbol?.Contains("$") == true)
            {
                insights.Add("Pattern appears to use regex - ensure pattern correctly matches intended targets");
            }
            
            // Risk assessment
            if (data.MatchedFiles != null)
            {
                var testFiles = data.MatchedFiles.Count(f => f.FilePath?.Contains("Test") == true);
                var srcFiles = totalFiles - testFiles;
                
                if (testFiles > 0 && srcFiles > 0)
                {
                    insights.Add($"Changes affect both source ({srcFiles}) and test ({testFiles}) files - ensure tests still pass");
                }
                else if (testFiles > 0)
                {
                    insights.Add("Changes only in test files - lower risk");
                }
                
                // Check for critical files
                var criticalFiles = data.MatchedFiles.Where(f => 
                    f.FilePath?.Contains("Program.cs") == true ||
                    f.FilePath?.Contains("Startup.cs") == true ||
                    f.FilePath?.Contains(".csproj") == true).ToList();
                
                if (criticalFiles.Any())
                {
                    insights.Add($"⚠️ Changes affect {criticalFiles.Count} critical file(s) - extra caution advised");
                }
            }
        }
        
        // Preview mode check
        if (data.IsPreview == true)
        {
            insights.Add("Preview mode - no changes applied. Review results and run with preview=false to apply");
        }
        else if (totalMatches > 0)
        {
            insights.Add("⚠️ Changes have been applied to files - remember to test and commit");
        }
        
        if (responseMode == "summary")
        {
            insights.Add("Showing summary view - use 'detailed' mode for complete match context");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        SolutionWideFindReplaceResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.MatchedFiles?.Any() == true)
        {
            // Review actions
            actions.Add(new AIAction
            {
                Action = "csharp_goto_definition",
                Description = "Navigate to specific changed locations",
                Category = "navigate",
                Priority = 10
            });
            
            // If in preview mode, suggest applying
            if (data.IsPreview == true)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_solution_wide_find_replace",
                    Description = "Apply these changes (run with preview=false)",
                    Category = "apply",
                    Priority = 9,
                    Parameters = new Dictionary<string, object>
                    {
                        ["preview"] = false,
                        ["findPattern"] = data.Query?.TargetSymbol ?? ""
                        // Note: ReplacePattern not available in the result
                    }
                });
            }
            
            // Testing actions
            actions.Add(new AIAction
            {
                Action = "csharp_get_diagnostics",
                Description = "Check for compilation errors after changes",
                Category = "verify",
                Priority = 9
            });
            
            // Rollback actions
            if (data.IsPreview == false)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_solution_wide_find_replace",
                    Description = "Review changes before finalizing",
                    Category = "review",
                    Priority = 8,
                    Parameters = new Dictionary<string, object>
                    {
                        // Note: Cannot suggest rollback without original replace pattern
                        ["preview"] = true
                    }
                });
            }
            
            // Refinement actions
            if (data.TotalMatches > 50)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_solution_wide_find_replace",
                    Description = "Refine search with file pattern filter",
                    Category = "filter",
                    Priority = 7,
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePattern"] = "*.cs",
                        ["excludePattern"] = "*Test*.cs"
                    }
                });
            }
            
            // Analysis actions
            actions.Add(new AIAction
            {
                Action = "csharp_code_clone_detection",
                Description = "Check if pattern indicates code duplication",
                Category = "analyze",
                Priority = 6
            });
        }
        else
        {
            // No matches found - suggest alternatives
            actions.Add(new AIAction
            {
                Action = "csharp_solution_wide_find_replace",
                Description = "Try with case-insensitive search",
                Category = "retry",
                Priority = 8,
                Parameters = new Dictionary<string, object>
                {
                    ["caseSensitive"] = false
                }
            });
            
            actions.Add(new AIAction
            {
                Action = "csharp_solution_wide_find_replace",
                Description = "Try with regex pattern for flexible matching",
                Category = "retry",
                Priority = 7,
                Parameters = new Dictionary<string, object>
                {
                    ["useRegex"] = true
                }
            });
        }
        
        return actions;
    }
    
    private List<FindReplaceMatch>? ReduceMatchedFiles(List<FindReplaceMatch> files, int tokenBudget)
    {
        var result = new List<FindReplaceMatch>();
        var currentTokens = 0;
        
        // Prioritize files by importance
        var prioritizedFiles = files
            .OrderByDescending(f => GetFilePriority(f))
            .ThenBy(f => f.FilePath);
        
        foreach (var file in prioritizedFiles)
        {
            // Create a reduced version of the matched file
            var reducedFile = ReduceMatchedFile(file, tokenBudget - currentTokens);
            var fileTokens = _tokenEstimator.EstimateObject(reducedFile);
            
            if (currentTokens + fileTokens <= tokenBudget)
            {
                result.Add(reducedFile);
                currentTokens += fileTokens;
            }
            else if (result.Count == 0 && fileTokens > tokenBudget)
            {
                // If no files fit and this one is too large, include a minimal version
                var minimalFile = new FindReplaceMatch
                {
                    FilePath = file.FilePath,
                    ProjectName = file.ProjectName,
                    MatchCount = file.MatchCount,
                    Matches = file.Matches?.Take(1).ToList() ?? new List<TextMatch>() // Just first match
                };
                result.Add(minimalFile);
                break;
            }
            else
            {
                break;
            }
        }
        
        return result;
    }
    
    private FindReplaceMatch ReduceMatchedFile(FindReplaceMatch file, int remainingBudget)
    {
        var reduced = new FindReplaceMatch
        {
            FilePath = file.FilePath,
            ProjectName = file.ProjectName,
            MatchCount = file.MatchCount,
            Matches = new List<TextMatch>()
        };
        
        if (file.Matches != null && file.Matches.Count > 0)
        {
            var baseTokens = _tokenEstimator.EstimateObject(reduced);
            var matchBudget = remainingBudget - baseTokens;
            
            if (matchBudget > 50) // Only include matches if we have reasonable space
            {
                var matchesPerFile = matchBudget / Math.Max(1, file.Matches.Count);
                var reducedMatches = new List<TextMatch>();
                
                // Prioritize first and last matches to show range
                var prioritizedMatches = file.Matches
                    .Take(5) // First 5 matches
                    .Concat(file.Matches.Skip(Math.Max(0, file.Matches.Count - 2))) // Last 2 matches
                    .Distinct()
                    .Take(7); // Maximum 7 matches per file
                
                foreach (var match in prioritizedMatches)
                {
                    reducedMatches.Add(match);
                }
                
                reduced.Matches = reducedMatches;
            }
        }
        
        return reduced;
    }
    
    private int GetFilePriority(FindReplaceMatch file)
    {
        var priority = 0;
        
        // Higher priority for files with more matches
        priority += file.MatchCount * 10;
        
        // Higher priority for source files over test files
        if (file.FilePath?.Contains("Test") == false)
        {
            priority += 50;
        }
        
        // Higher priority for critical files
        if (file.FilePath?.EndsWith("Program.cs") == true ||
            file.FilePath?.EndsWith("Startup.cs") == true)
        {
            priority += 100;
        }
        
        // Lower priority for generated files
        if (file.FilePath?.Contains(".g.cs") == true ||
            file.FilePath?.Contains(".Designer.cs") == true)
        {
            priority -= 30;
        }
        
        return priority;
    }
    
    private string BuildSummary(SolutionWideFindReplaceResult data, int displayedFiles, int totalFiles)
    {
        var totalMatches = data.TotalMatches;
        
        if (totalMatches == 0)
        {
            return $"No matches found for pattern '{data.Query?.TargetSymbol}'";
        }
        
        var actionText = data.IsPreview == true ? "Found" : "Replaced";
        
        if (displayedFiles < totalFiles)
        {
            return $"{actionText} {totalMatches} matches across {totalFiles} files, showing {displayedFiles} files";
        }
        
        return $"{actionText} {totalMatches} matches across {totalFiles} files";
    }
}