using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders.TypeScript;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Services.TypeScript;
using COA.Mcp.Framework;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;
using AIAction = COA.Mcp.Framework.Models.AIAction;
using ToolExecutionMetadata = COA.Mcp.Framework.Models.ToolExecutionMetadata;

namespace COA.CodeNav.McpServer.Tools.TypeScript;

/// <summary>
/// MCP tool that loads multiple TypeScript projects in a workspace for comprehensive analysis
/// </summary>
public class TsLoadWorkspaceTool : McpToolBase<TsLoadWorkspaceParams, TsLoadWorkspaceResult>
{
    private const int SAFETY_TOKEN_LIMIT = 10000;
    private readonly ILogger<TsLoadWorkspaceTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;
    private readonly TsLoadWorkspaceResponseBuilder? _responseBuilder;

    public override string Name => ToolNames.TsLoadWorkspace;
    public override ToolCategory Category => ToolCategory.Resources;
    
    public override string Description => @"Load multiple TypeScript projects in a workspace for comprehensive cross-project analysis.
Returns: Summary of loaded projects with configuration details and cross-references.
Prerequisites: TypeScript must be installed globally (npm install -g typescript).
Error handling: Returns specific error codes with recovery steps if workspace loading fails.
Use cases: Loading monorepos, multi-project workspaces, analyzing project dependencies.
Not for: Single project loading (use ts_load_tsconfig), loading non-TypeScript projects.";

    public TsLoadWorkspaceTool(
        ILogger<TsLoadWorkspaceTool> logger,
        TypeScriptWorkspaceService workspaceService,
        TypeScriptCompilerManager compilerManager,
        ITokenEstimator tokenEstimator,
        AnalysisResultResourceProvider? resourceProvider = null,
        TsLoadWorkspaceResponseBuilder? responseBuilder = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _compilerManager = compilerManager;
        _tokenEstimator = tokenEstimator;
        _resourceProvider = resourceProvider;
        _responseBuilder = responseBuilder;
    }

    protected override async Task<TsLoadWorkspaceResult> ExecuteInternalAsync(
        TsLoadWorkspaceParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogDebug("TsLoadWorkspace request: WorkspacePath={WorkspacePath}, IncludeNodeModules={IncludeNodeModules}",
            parameters.WorkspacePath, parameters.IncludeNodeModules);

        try
        {
            // Validate TypeScript availability
            var tsError = _compilerManager.ValidateTypeScriptAvailability();
            if (tsError != null)
            {
                return new TsLoadWorkspaceResult
                {
                    Success = false,
                    Message = "TypeScript is not available",
                    Error = tsError
                };
            }

            // Validate workspace path
            if (!Directory.Exists(parameters.WorkspacePath))
            {
                return new TsLoadWorkspaceResult
                {
                    Success = false,
                    Message = $"Workspace directory does not exist: {parameters.WorkspacePath}",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.PROJECT_NOT_FOUND,
                        Message = "The specified workspace directory was not found",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new[]
                            {
                                "Check the workspace directory path",
                                "Ensure you have read permissions for the directory",
                                "Verify the directory contains TypeScript projects"
                            }
                        }
                    }
                };
            }

            // Discover TypeScript projects in the workspace
            var discoveredProjects = await DiscoverTypeScriptProjectsAsync(
                parameters.WorkspacePath,
                parameters.IncludeNodeModules,
                parameters.MaxDepth,
                cancellationToken);

            if (discoveredProjects.Count == 0)
            {
                return new TsLoadWorkspaceResult
                {
                    Success = false,
                    Message = $"No TypeScript projects found in workspace: {parameters.WorkspacePath}",
                    Query = new LoadWorkspaceQuery
                    {
                        WorkspacePath = parameters.WorkspacePath,
                        IncludeNodeModules = parameters.IncludeNodeModules,
                        MaxDepth = parameters.MaxDepth
                    },
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.TSCONFIG_NOT_FOUND,
                        Message = "No tsconfig.json files found in the workspace",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new[]
                            {
                                "Ensure the workspace contains TypeScript projects",
                                "Check that tsconfig.json files exist in project directories",
                                "Try increasing the MaxDepth parameter if projects are nested deeply"
                            }
                        }
                    },
                    Insights = new List<string>
                    {
                        "No TypeScript configuration files were found",
                        "The workspace may contain only JavaScript projects",
                        "Consider initializing TypeScript with 'tsc --init'"
                    }
                };
            }

            // Load each discovered project
            var loadedProjects = new List<TypeScriptProjectInfo>();
            var failedProjects = new List<FailedProjectInfo>();

            foreach (var projectPath in discoveredProjects)
            {
                try
                {
                    _logger.LogDebug("Loading TypeScript project: {ProjectPath}", projectPath);
                    
                    var loadResult = await _workspaceService.LoadTsConfigAsync(
                        projectPath,
                        parameters.WorkspaceId);

                    if (loadResult.Success)
                    {
                        loadedProjects.Add(new TypeScriptProjectInfo
                        {
                            ProjectPath = projectPath,
                            ProjectType = ExtractProjectName(projectPath),
                            CompilerOptions = ConvertCompilerOptions(loadResult.CompilerOptions),
                            SourceFiles = loadResult.Files ?? new List<string>(),
                            Dependencies = loadResult.References?.Select(r => r.Path ?? "").ToList() ?? new List<string>()
                        });
                    }
                    else
                    {
                        failedProjects.Add(new FailedProjectInfo
                        {
                            ProjectPath = projectPath,
                            Error = loadResult.Error?.Message ?? "Unknown error",
                            Reason = "Failed to load TypeScript configuration"
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load TypeScript project: {ProjectPath}", projectPath);
                    failedProjects.Add(new FailedProjectInfo
                    {
                        ProjectPath = projectPath,
                        Error = ex.Message,
                        Reason = "Exception during project loading"
                    });
                }
            }

            var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            // Analyze cross-project dependencies  
            var crossReferences = AnalyzeCrossReferences(loadedProjects);

            // Build the result
            var result = new TsLoadWorkspaceResult
            {
                Success = loadedProjects.Count > 0,
                Message = loadedProjects.Count > 0
                    ? $"Loaded {loadedProjects.Count} TypeScript project(s) from workspace"
                    : "Failed to load any TypeScript projects from workspace",
                Query = new LoadWorkspaceQuery
                {
                    WorkspacePath = parameters.WorkspacePath,
                    IncludeNodeModules = parameters.IncludeNodeModules,
                    MaxDepth = parameters.MaxDepth
                },
                Summary = new LoadWorkspaceSummary
                {
                    ProjectCount = loadedProjects.Count,
                    TotalSourceFiles = loadedProjects.Sum(p => p.SourceFiles?.Count ?? 0),
                    CrossReferencesCount = crossReferences.Count
                },
                Projects = loadedProjects,
                CrossReferences = crossReferences,
                Insights = GenerateInsights(loadedProjects, failedProjects, discoveredProjects.Count),
                Actions = GenerateActions(loadedProjects, parameters.WorkspacePath),
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{executionTime:F2}ms"
                }
            };

            // Apply response builder optimization if available
            if (_responseBuilder != null)
            {
                var context = new ResponseContext
                {
                    ResponseMode = "optimized",
                    TokenLimit = SAFETY_TOKEN_LIMIT,
                    ToolName = Name
                };
                
                result = await _responseBuilder.BuildResponseAsync(result, context);
            }
            else
            {
                // Fallback to direct token optimization
                result = await ApplyTokenOptimization(result, executionTime);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading TypeScript workspace: {WorkspacePath}", parameters.WorkspacePath);
            return new TsLoadWorkspaceResult
            {
                Success = false,
                Message = $"Failed to load TypeScript workspace: {ex.Message}",
                Error = new ErrorInfo { Code = ErrorCodes.INTERNAL_ERROR, Message = ex.Message }
            };
        }
    }

    #region Helper Methods

    private async Task<List<string>> DiscoverTypeScriptProjectsAsync(
        string workspacePath,
        bool includeNodeModules,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        var projects = new List<string>();
        
        await DiscoverProjectsRecursiveAsync(
            workspacePath,
            projects,
            includeNodeModules,
            maxDepth,
            0,
            cancellationToken);

        return projects;
    }

    private async Task DiscoverProjectsRecursiveAsync(
        string directory,
        List<string> projects,
        bool includeNodeModules,
        int maxDepth,
        int currentDepth,
        CancellationToken cancellationToken)
    {
        if (currentDepth > maxDepth)
            return;

        try
        {
            var tsConfigPath = Path.Combine(directory, "tsconfig.json");
            if (File.Exists(tsConfigPath))
            {
                projects.Add(tsConfigPath);
            }

            // Search subdirectories
            var directories = Directory.GetDirectories(directory);
            foreach (var subDir in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dirName = Path.GetFileName(subDir);
                
                // Skip node_modules unless explicitly included
                if (!includeNodeModules && dirName == "node_modules")
                    continue;

                // Skip common non-project directories
                if (dirName.StartsWith(".") || 
                    dirName == "dist" || 
                    dirName == "build" || 
                    dirName == "coverage" ||
                    dirName == "lib")
                    continue;

                await DiscoverProjectsRecursiveAsync(
                    subDir,
                    projects,
                    includeNodeModules,
                    maxDepth,
                    currentDepth + 1,
                    cancellationToken);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error discovering projects in directory: {Directory}", directory);
        }
    }

    private string ExtractProjectName(string tsConfigPath)
    {
        var projectDir = Path.GetDirectoryName(tsConfigPath);
        if (string.IsNullOrEmpty(projectDir))
            return "Unknown";

        var packageJsonPath = Path.Combine(projectDir, "package.json");
        if (File.Exists(packageJsonPath))
        {
            try
            {
                var packageJson = File.ReadAllText(packageJsonPath);
                var packageInfo = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(packageJson);
                if (packageInfo?.TryGetValue("name", out var nameObj) == true && nameObj is System.Text.Json.JsonElement nameElement)
                {
                    return nameElement.GetString() ?? Path.GetFileName(projectDir);
                }
            }
            catch
            {
                // If we can't parse package.json, fall back to directory name
            }
        }

        return Path.GetFileName(projectDir) ?? "Unknown";
    }

    private Dictionary<string, object>? ConvertCompilerOptions(TsCompilerOptions? compilerOptions)
    {
        if (compilerOptions == null) return null;
        
        return new Dictionary<string, object>
        {
            ["target"] = compilerOptions.Target ?? "",
            ["module"] = compilerOptions.Module ?? "",
            ["strict"] = compilerOptions.Strict ?? false,
            ["outDir"] = compilerOptions.OutDir ?? "",
            ["rootDir"] = compilerOptions.RootDir ?? "",
            ["sourceMap"] = compilerOptions.SourceMap ?? false,
            ["declaration"] = compilerOptions.Declaration ?? false
        };
    }

    private List<ProjectReference> AnalyzeCrossReferences(List<TypeScriptProjectInfo> projects)
    {
        var dependencies = new List<ProjectReference>();

        foreach (var project in projects)
        {
            if (project.Dependencies != null)
            {
                foreach (var dependency in project.Dependencies)
                {
                    var referencedProject = projects.FirstOrDefault(p => 
                        Path.GetDirectoryName(p.ProjectPath)?.Equals(
                            Path.GetDirectoryName(dependency), 
                            StringComparison.OrdinalIgnoreCase) == true);

                    if (referencedProject != null)
                    {
                        dependencies.Add(new ProjectReference
                        {
                            FromProject = Path.GetFileNameWithoutExtension(project.ProjectPath),
                            ToProject = Path.GetFileNameWithoutExtension(referencedProject.ProjectPath),
                            ReferenceType = "ProjectReference"
                        });
                    }
                }
            }
        }

        return dependencies;
    }

    private List<string> GenerateInsights(
        List<TypeScriptProjectInfo> loadedProjects,
        List<FailedProjectInfo> failedProjects,
        int totalDiscovered)
    {
        var insights = new List<string>();

        if (loadedProjects.Count > 0)
        {
            insights.Add($"Successfully loaded {loadedProjects.Count} of {totalDiscovered} TypeScript project(s)");
            
            var totalFiles = loadedProjects.Sum(p => p.SourceFiles.Count);
            insights.Add($"Total source files across all projects: {totalFiles}");

            if (loadedProjects.Any(p => p.Dependencies?.Count > 0))
            {
                insights.Add("Found cross-project references - this is a multi-project workspace");
            }
        }

        if (failedProjects.Count > 0)
        {
            insights.Add($"{failedProjects.Count} project(s) failed to load - check project configurations");
        }

        insights.Add("All loaded projects are now available for TypeScript analysis");
        insights.Add("Use specific TypeScript tools (ts_goto_definition, ts_find_all_references) for code navigation");

        return insights;
    }

    private List<AIAction> GenerateActions(
        List<TypeScriptProjectInfo> loadedProjects,
        string workspacePath)
    {
        var actions = new List<AIAction>();

        if (loadedProjects.Count > 0)
        {
            // Suggest checking for errors in each project
            foreach (var project in loadedProjects.Take(3)) // Limit to first 3 projects
            {
                actions.Add(new AIAction
                {
                    Action = ToolNames.TsGetDiagnostics,
                    Description = $"Check diagnostics for {Path.GetFileNameWithoutExtension(project.ProjectPath)}",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = project.SourceFiles.FirstOrDefault() ?? project.ProjectPath
                    }
                });
            }

            if (loadedProjects.Count > 3)
            {
                actions.Add(new AIAction
                {
                    Action = ToolNames.TsGetDiagnostics,
                    Description = $"Check diagnostics for remaining {loadedProjects.Count - 3} project(s)",
                    Parameters = new Dictionary<string, object>
                    {
                        ["workspacePath"] = workspacePath
                    }
                });
            }
        }

        return actions;
    }

    private Task<TsLoadWorkspaceResult> ApplyTokenOptimization(TsLoadWorkspaceResult result, double executionTime)
    {
        // Estimate current token usage
        var estimatedTokens = _tokenEstimator.EstimateObject(result);
        var wasOptimized = false;
        
        if (estimatedTokens > SAFETY_TOKEN_LIMIT)
        {
            _logger.LogDebug("TsLoadWorkspace: Token optimization needed. Estimated: {EstimatedTokens}, Limit: {Limit}", 
                estimatedTokens, SAFETY_TOKEN_LIMIT);
            
            // Progressive reduction strategy for projects
            if (result.Projects?.Count > 10)
            {
                // Keep first 10 projects and summarize the rest
                var keptProjects = result.Projects.Take(10).ToList();
                var remainingCount = result.Projects.Count - 10;
                
                result.Projects = keptProjects;
                result.Insights?.Add($"⚠️ Showing first 10 of {result.Summary?.ProjectCount} projects due to size limits");
                result.Insights?.Add($"Use ts_load_tsconfig for specific projects if needed");
                wasOptimized = true;
            }
            
            // Reduce source file lists if still over limit
            if (_tokenEstimator.EstimateObject(result) > SAFETY_TOKEN_LIMIT && result.Projects != null)
            {
                foreach (var project in result.Projects)
                {
                    if (project.SourceFiles?.Count > 20)
                    {
                        var keepCount = Math.Min(20, project.SourceFiles.Count);
                        var removedCount = project.SourceFiles.Count - keepCount;
                        project.SourceFiles = project.SourceFiles.Take(keepCount).ToList();
                        if (project.Notes == null) project.Notes = new List<string>();
                        project.Notes.Add($"... and {removedCount} more source files (truncated)");
                        wasOptimized = true;
                    }
                }
            }
            
            // Store full result in resource provider if available
            if (wasOptimized && _resourceProvider != null)
            {
                try
                {
                    var resourceUri = _resourceProvider.StoreAnalysisResult("ts-load-workspace", result);
                    result.ResourceUri = resourceUri;
                    result.Insights?.Add("💾 Complete workspace details stored - use resource tools to access full data");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to store full result in resource provider");
                }
            }
        }
        
        // Update metadata
        result.Meta = new ToolExecutionMetadata
        {
            Mode = wasOptimized ? "optimized" : "full",
            Truncated = wasOptimized,
            Tokens = _tokenEstimator.EstimateObject(result),
            ExecutionTime = $"{executionTime:F2}ms"
        };
        
        return Task.FromResult(result);
    }

    #endregion
}

/// <summary>
/// Parameters for loading a TypeScript workspace
/// </summary>
public class TsLoadWorkspaceParams
{
    [Required]
    [JsonPropertyName("workspacePath")]
    public required string WorkspacePath { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("includeNodeModules")]
    public bool IncludeNodeModules { get; set; } = false;

    [JsonPropertyName("maxDepth")]
    public int MaxDepth { get; set; } = 5;
}
