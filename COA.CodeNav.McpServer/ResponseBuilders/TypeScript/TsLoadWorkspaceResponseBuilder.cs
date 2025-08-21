using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders.TypeScript;

/// <summary>
/// Response builder for TypeScript load workspace with token optimization
/// </summary>
public class TsLoadWorkspaceResponseBuilder : BaseResponseBuilder<TsLoadWorkspaceResult, TsLoadWorkspaceResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public TsLoadWorkspaceResponseBuilder(
        ILogger<TsLoadWorkspaceResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }

    public override Task<TsLoadWorkspaceResult> BuildResponseAsync(
        TsLoadWorkspaceResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Apply progressive reduction to projects and content
        var wasReduced = false;
        
        if (data.Projects != null && data.Projects.Count > 0)
        {
            // Don't reduce small project sets (<=5 projects)
            if (data.Projects.Count <= 5)
            {
                _logger?.LogDebug("TsLoadWorkspaceResponseBuilder: Small project set ({Count} projects), keeping all", 
                    data.Projects.Count);
                wasReduced = false;
            }
            else
            {
                // Fix token budget if it's 0 or negative
                if (tokenBudget <= 0)
                {
                    tokenBudget = context.TokenLimit ?? 10000;
                    _logger?.LogDebug("TsLoadWorkspaceResponseBuilder: Fixed token budget from 0 to {Budget}", tokenBudget);
                }
                
                var originalTokens = _tokenEstimator.EstimateObject(data.Projects);
                
                _logger?.LogDebug("TsLoadWorkspaceResponseBuilder: Original projects: {Count}, Estimated tokens: {Tokens}, Budget: {Budget}", 
                    data.Projects.Count, originalTokens, tokenBudget);
                
                // Apply token optimization for large project sets
                if (originalTokens > tokenBudget * 0.7)
                {
                    var projectBudget = (int)(tokenBudget * 0.7);
                    var reducedProjects = ReduceProjects(data.Projects, projectBudget);
                    wasReduced = reducedProjects.Count < data.Projects.Count;
                    data.Projects = reducedProjects;
                    
                    _logger?.LogDebug("TsLoadWorkspaceResponseBuilder: Reduced from {Original} to {Reduced} projects", 
                        data.Summary?.ProjectCount ?? 0, data.Projects?.Count ?? 0);
                }
            }
        }
        
        // Reduce cross-references if necessary
        if (data.CrossReferences != null && data.CrossReferences.Count > 20)
        {
            data.CrossReferences = data.CrossReferences.Take(20).ToList();
            wasReduced = true;
        }
        
        // Generate insights based on workspace data
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
        TsLoadWorkspaceResult data,
        string? responseMode)
    {
        var insights = new List<string>();
        
        if (data.Projects == null || data.Projects.Count == 0)
        {
            insights.Add("❌ No TypeScript projects found in the specified workspace");
            insights.Add("Ensure the workspace contains valid tsconfig.json files");
            insights.Add("Check that TypeScript is properly installed and configured");
            return insights;
        }
        
        var projectCount = data.Projects.Count;
        var totalFiles = data.Projects.Sum(p => p.SourceFiles?.Count ?? 0);
        
        insights.Add($"✅ Successfully loaded {projectCount} TypeScript project{(projectCount > 1 ? "s" : "")}");
        insights.Add($"📁 Total source files: {totalFiles:N0}");
        
        // Analyze project configurations
        var strictProjects = data.Projects.Count(p => p.CompilerOptions?.ContainsKey("strict") == true &&
                                                      p.CompilerOptions["strict"].ToString() == "True");
        if (strictProjects > 0)
        {
            insights.Add($"🔒 {strictProjects} project{(strictProjects > 1 ? "s" : "")} using strict TypeScript mode");
        }
        
        // Analyze cross-references
        if (data.CrossReferences?.Any() == true)
        {
            insights.Add($"🔗 Found {data.CrossReferences.Count} cross-project reference{(data.CrossReferences.Count > 1 ? "s" : "")}");
            insights.Add("Cross-project analysis and refactoring is now available");
        }
        
        // Project type analysis
        var libProjects = data.Projects.Count(p => p.ProjectType?.Contains("library") == true);
        var appProjects = data.Projects.Count(p => p.ProjectType?.Contains("application") == true);
        
        if (libProjects > 0)
            insights.Add($"📚 {libProjects} library project{(libProjects > 1 ? "s" : "")} detected");
        if (appProjects > 0)
            insights.Add($"🚀 {appProjects} application project{(appProjects > 1 ? "s" : "")} detected");
        
        // Size analysis
        var largeProjects = data.Projects.Count(p => (p.SourceFiles?.Count ?? 0) > 100);
        if (largeProjects > 0)
        {
            insights.Add($"⚡ {largeProjects} large project{(largeProjects > 1 ? "s" : "")} (>100 files) - analysis may take longer");
        }
        
        if (data.Meta?.Truncated == true)
        {
            insights.Add($"Results truncated - showing subset of {data.Summary?.ProjectCount} total projects");
            insights.Add("Use resource tools to access complete workspace data");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        TsLoadWorkspaceResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Projects?.Any() == true)
        {
            // Suggest analyzing the workspace for issues
            actions.Add(new AIAction
            {
                Action = "ts_get_diagnostics",
                Description = "Check for TypeScript errors across all projects",
                Category = "analyze",
                Priority = 9
            });
            
            // Suggest symbol search across projects
            actions.Add(new AIAction
            {
                Action = "ts_symbol_search",
                Description = "Search for symbols across the entire workspace",
                Category = "search",
                Priority = 8
            });
            
            // If there are cross-references, suggest dependency analysis
            if (data.CrossReferences?.Any() == true)
            {
                actions.Add(new AIAction
                {
                    Action = "analyze_project_dependencies",
                    Description = "Analyze cross-project dependencies and relationships",
                    Category = "analyze",
                    Priority = 7
                });
            }
            
            // Suggest analyzing specific projects
            var firstProject = data.Projects.First();
            if (firstProject.SourceFiles?.Any() == true)
            {
                actions.Add(new AIAction
                {
                    Action = "ts_document_symbols",
                    Description = $"Analyze symbols in {Path.GetFileName(firstProject.ProjectPath)}",
                    Category = "analyze",
                    Priority = 6
                });
            }
            
            // Suggest workspace-wide operations
            actions.Add(new AIAction
            {
                Action = "ts_find_unused_exports",
                Description = "Find unused exports across the workspace",
                Category = "cleanup",
                Priority = 5
            });
        }
        else
        {
            // No projects loaded - suggest troubleshooting
            actions.Add(new AIAction
            {
                Action = "verify_typescript_installation",
                Description = "Verify TypeScript is properly installed",
                Category = "troubleshoot",
                Priority = 10
            });
            
            actions.Add(new AIAction
            {
                Action = "search_tsconfig_files",
                Description = "Search for tsconfig.json files in the workspace",
                Category = "search",
                Priority = 9
            });
        }
        
        return actions;
    }
    
    private List<TypeScriptProjectInfo> ReduceProjects(List<TypeScriptProjectInfo> projects, int tokenBudget)
    {
        var result = new List<TypeScriptProjectInfo>();
        var currentTokens = 0;
        
        // Prioritize projects by importance: applications first, then libraries, then test projects
        var prioritized = projects
            .OrderBy(p => GetProjectPriority(p))
            .ThenByDescending(p => p.SourceFiles?.Count ?? 0); // Larger projects first within same priority
        
        foreach (var project in prioritized)
        {
            var projectTokens = _tokenEstimator.EstimateObject(project);
            
            if (currentTokens + projectTokens <= tokenBudget)
            {
                result.Add(project);
                currentTokens += projectTokens;
            }
            else
            {
                // Try to reduce the project size instead of excluding it entirely
                var reducedProject = ReduceProjectDetails(project, tokenBudget - currentTokens);
                if (reducedProject != null)
                {
                    result.Add(reducedProject);
                    currentTokens += _tokenEstimator.EstimateObject(reducedProject);
                }
                break;
            }
        }
        
        // Always include at least one project if available
        if (result.Count == 0 && projects.Any())
        {
            result.Add(ReduceProjectDetails(projects.First(), tokenBudget) ?? projects.First());
        }
        
        return result;
    }
    
    private int GetProjectPriority(TypeScriptProjectInfo project)
    {
        var type = project.ProjectType?.ToLowerInvariant() ?? "";
        
        if (type.Contains("application"))
            return 0; // Highest priority
        if (type.Contains("library"))
            return 1; // Medium priority
        if (type.Contains("test"))
            return 2; // Lower priority
        
        return 3; // Lowest priority
    }
    
    private TypeScriptProjectInfo? ReduceProjectDetails(TypeScriptProjectInfo project, int availableTokens)
    {
        if (availableTokens < 200) return null; // Not enough space for meaningful project info
        
        var reduced = new TypeScriptProjectInfo
        {
            ProjectPath = project.ProjectPath,
            ProjectType = project.ProjectType,
            CompilerOptions = project.CompilerOptions?.Take(5).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            SourceFiles = project.SourceFiles?.Take(10).ToList(),
            Dependencies = project.Dependencies?.Take(5).ToList(),
            Notes = new List<string> { "Project details truncated due to size constraints" }
        };
        
        if (project.SourceFiles?.Count > 10)
        {
            reduced.Notes?.Add($"... and {project.SourceFiles.Count - 10} more source files");
        }
        
        return reduced;
    }
    
    private string BuildSummary(TsLoadWorkspaceResult data)
    {
        if (data.Projects == null || data.Projects.Count == 0)
        {
            return "No TypeScript projects found in workspace";
        }
        
        var displayedCount = data.Projects.Count;
        var totalCount = data.Summary?.ProjectCount ?? displayedCount;
        var totalFiles = data.Projects.Sum(p => p.SourceFiles?.Count ?? 0);
        
        var summary = $"Loaded {displayedCount} TypeScript project{(displayedCount > 1 ? "s" : "")} with {totalFiles:N0} source files";
        
        if (displayedCount < totalCount)
        {
            summary += $" (showing {displayedCount} of {totalCount} projects)";
        }
        
        return summary;
    }
}