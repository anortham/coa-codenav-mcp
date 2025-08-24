using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// Tool for loading C# solution files into the Roslyn workspace
/// </summary>
public class LoadSolutionTool : McpToolBase<LoadSolutionParams, LoadSolutionResult>
{
    private readonly ILogger<LoadSolutionTool> _logger;
    private readonly MSBuildWorkspaceManager _workspaceManager;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly ITokenEstimator _tokenEstimator;

    public override string Name => "csharp_load_solution";
    public override string Description => "Load a C# solution file into the Roslyn workspace";
    public override ToolCategory Category => ToolCategory.Resources;

    public LoadSolutionTool(
        IServiceProvider serviceProvider,
        ILogger<LoadSolutionTool> logger,
        MSBuildWorkspaceManager workspaceManager,
        RoslynWorkspaceService workspaceService,
        ITokenEstimator tokenEstimator)
        : base(serviceProvider, logger)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _workspaceService = workspaceService;
        _tokenEstimator = tokenEstimator;
    }

    protected override async Task<LoadSolutionResult> ExecuteInternalAsync(
        LoadSolutionParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogInformation("Loading solution: {SolutionPath}", parameters.SolutionPath);

        // Validate the solution file exists
        if (!File.Exists(parameters.SolutionPath))
        {
            return new LoadSolutionResult
            {
                Success = false,
                Message = $"Solution file not found: {parameters.SolutionPath}",
                Error = new ErrorInfo
                {
                    Code = "SOLUTION_NOT_FOUND",
                    Message = $"Solution file not found: {parameters.SolutionPath}",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Verify the solution file path is correct and absolute",
                            "Check that the file has a .sln extension",
                            "Ensure you have read permissions for the file"
                        }
                    }
                }
            };
        }

        try
        {
            // Load the solution
            var solution = await _workspaceManager.LoadSolutionAsync(
                parameters.SolutionPath, 
                parameters.WorkspaceId);

            if (solution == null)
            {
                return new LoadSolutionResult
                {
                    Success = false,
                    Message = "Failed to load solution",
                    Error = new ErrorInfo
                    {
                        Code = "SOLUTION_LOAD_FAILED",
                        Message = "Failed to load solution into MSBuild workspace",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new[]
                            {
                                "Check that the solution file is valid",
                                "Ensure all required MSBuild tools are installed",
                                "Verify .NET SDK is properly configured"
                            }
                        }
                    }
                };
            }

            // Register with workspace service
            var workspaceInfo = await _workspaceService.LoadSolutionAsync(parameters.SolutionPath);
            
            if (workspaceInfo == null)
            {
                return new LoadSolutionResult
                {
                    Success = false,
                    Message = "Failed to register solution in workspace service",
                    Error = new ErrorInfo
                    {
                        Code = "WORKSPACE_REGISTRATION_FAILED",
                        Message = "Failed to register solution in workspace service"
                    }
                };
            }

            // Get solution info
            var projectCount = solution.Projects.Count();
            var projectNames = solution.Projects.Select(p => p.Name).ToList();

            _logger.LogInformation("Successfully loaded solution with {ProjectCount} projects", projectCount);

            // Generate insights
            var insights = new List<string>
            {
                $"Loaded {projectCount} project(s) from {Path.GetFileName(parameters.SolutionPath)}",
                $"Workspace ready for code navigation and analysis"
            };
            
            if (projectCount > 10)
                insights.Add("Large solution loaded - operations may take longer");
            
            // Generate next actions
            var actions = new List<AIAction>
            {
                new AIAction
                {
                    Action = "csharp_get_workspace_statistics",
                    Description = "View detailed workspace statistics",
                    Priority = 80
                },
                new AIAction
                {
                    Action = "csharp_symbol_search",
                    Description = "Search for symbols across the solution",
                    Parameters = new Dictionary<string, object> { ["query"] = "*" },
                    Priority = 60
                },
                new AIAction
                {
                    Action = "csharp_get_diagnostics",
                    Description = "Check for compilation errors and warnings",
                    Priority = 70
                }
            };

            return new LoadSolutionResult
            {
                Success = true,
                WorkspaceId = workspaceInfo.Id,
                Message = $"Successfully loaded solution with {projectCount} project(s)",
                ProjectCount = projectCount,
                ProjectNames = projectNames,
                Insights = insights,
                Actions = actions,
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading solution: {SolutionPath}", parameters.SolutionPath);
            return new LoadSolutionResult
            {
                Success = false,
                Message = $"Error loading solution: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = "INTERNAL_ERROR",
                    Message = ex.Message,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Check the server logs for detailed error information",
                            "Verify the solution file is not corrupted",
                            "Try closing other instances of Visual Studio or IDEs"
                        }
                    }
                }
            };
        }
    }

}

public class LoadSolutionParams
{
    [JsonPropertyName("solutionPath")]
    [System.ComponentModel.DataAnnotations.Required]
    [COA.Mcp.Framework.Attributes.Description("Path to the .sln file to load")]
    public string SolutionPath { get; set; } = string.Empty;

    [JsonPropertyName("workspaceId")]
    [COA.Mcp.Framework.Attributes.Description("Optional workspace ID to use")]
    public string? WorkspaceId { get; set; }
}

public class LoadSolutionResult : ToolResultBase
{
    public override string Operation => "csharp_load_solution";

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("projectCount")]
    public int ProjectCount { get; set; }

    [JsonPropertyName("projectNames")]
    public List<string>? ProjectNames { get; set; }
}