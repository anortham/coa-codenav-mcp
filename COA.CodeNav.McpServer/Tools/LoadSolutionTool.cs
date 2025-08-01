using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Protocol;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

[McpServerToolType]
public class LoadSolutionTool
{
    private readonly ILogger<LoadSolutionTool> _logger;
    private readonly MSBuildWorkspaceManager _workspaceManager;
    private readonly RoslynWorkspaceService _workspaceService;

    public LoadSolutionTool(
        ILogger<LoadSolutionTool> logger,
        MSBuildWorkspaceManager workspaceManager,
        RoslynWorkspaceService workspaceService)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _workspaceService = workspaceService;
    }

    [McpServerTool(Name = "roslyn_load_solution")]
    [Description("Load a C# solution file into the Roslyn workspace")]
    public async Task<LoadSolutionResult> ExecuteAsync(LoadSolutionParams parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Loading solution: {SolutionPath}", parameters.SolutionPath);

            // Validate the solution file exists
            if (!File.Exists(parameters.SolutionPath))
            {
                return new LoadSolutionResult
                {
                    Success = false,
                    Message = $"Solution file not found: {parameters.SolutionPath}"
                };
            }

            // Load the solution
            var solution = await _workspaceManager.LoadSolutionAsync(
                parameters.SolutionPath, 
                parameters.WorkspaceId);

            if (solution == null)
            {
                return new LoadSolutionResult
                {
                    Success = false,
                    Message = "Failed to load solution"
                };
            }

            // Register with workspace service
            var workspaceInfo = await _workspaceService.LoadSolutionAsync(parameters.SolutionPath);
            
            if (workspaceInfo == null)
            {
                return new LoadSolutionResult
                {
                    Success = false,
                    Message = "Failed to register solution in workspace service"
                };
            }

            // Get solution info
            var projectCount = solution.Projects.Count();
            var projectNames = solution.Projects.Select(p => p.Name).ToList();

            _logger.LogInformation("Successfully loaded solution with {ProjectCount} projects", projectCount);

            return new LoadSolutionResult
            {
                Success = true,
                WorkspaceId = workspaceInfo.Id,
                Message = $"Successfully loaded solution with {projectCount} project(s)",
                ProjectCount = projectCount,
                ProjectNames = projectNames
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading solution: {SolutionPath}", parameters.SolutionPath);
            return new LoadSolutionResult
            {
                Success = false,
                Message = $"Error loading solution: {ex.Message}"
            };
        }
    }
}

public class LoadSolutionParams
{
    [JsonPropertyName("solutionPath")]
    [Description("Path to the .sln file to load")]
    public required string SolutionPath { get; set; }

    [JsonPropertyName("workspaceId")]
    [Description("Optional workspace ID to use")]
    public string? WorkspaceId { get; set; }
}

public class LoadSolutionResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("projectCount")]
    public int ProjectCount { get; set; }

    [JsonPropertyName("projectNames")]
    public List<string>? ProjectNames { get; set; }
}