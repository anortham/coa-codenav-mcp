using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Protocol;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

[McpServerToolType]
public class LoadProjectTool
{
    private readonly ILogger<LoadProjectTool> _logger;
    private readonly MSBuildWorkspaceManager _workspaceManager;
    private readonly RoslynWorkspaceService _workspaceService;

    public LoadProjectTool(
        ILogger<LoadProjectTool> logger,
        MSBuildWorkspaceManager workspaceManager,
        RoslynWorkspaceService workspaceService)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _workspaceService = workspaceService;
    }

    [McpServerTool(Name = "csharp_load_project")]
    [Description("Load a C# project file into the Roslyn workspace")]
    public async Task<LoadProjectResult> ExecuteAsync(LoadProjectParams parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Loading project: {ProjectPath}", parameters.ProjectPath);

            // Validate the project file exists
            if (!File.Exists(parameters.ProjectPath))
            {
                return new LoadProjectResult
                {
                    Success = false,
                    Message = $"Project file not found: {parameters.ProjectPath}"
                };
            }

            // Load the project
            var project = await _workspaceManager.LoadProjectAsync(
                parameters.ProjectPath, 
                parameters.WorkspaceId);

            if (project == null)
            {
                return new LoadProjectResult
                {
                    Success = false,
                    Message = "Failed to load project"
                };
            }

            // Register with workspace service
            var workspaceInfo = await _workspaceService.LoadProjectAsync(parameters.ProjectPath);
            
            if (workspaceInfo == null)
            {
                return new LoadProjectResult
                {
                    Success = false,
                    Message = "Failed to register project in workspace service"
                };
            }

            // Get project info
            var documentCount = project.Documents.Count();
            var references = project.MetadataReferences.Count();

            _logger.LogInformation("Successfully loaded project '{ProjectName}' with {DocumentCount} documents", 
                project.Name, documentCount);

            return new LoadProjectResult
            {
                Success = true,
                WorkspaceId = workspaceInfo.Id,
                Message = $"Successfully loaded project '{project.Name}'",
                ProjectName = project.Name,
                DocumentCount = documentCount,
                ReferenceCount = references
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading project: {ProjectPath}", parameters.ProjectPath);
            return new LoadProjectResult
            {
                Success = false,
                Message = $"Error loading project: {ex.Message}"
            };
        }
    }
}

public class LoadProjectParams
{
    [JsonPropertyName("projectPath")]
    [Description("Path to the .csproj file to load")]
    public required string ProjectPath { get; set; }

    [JsonPropertyName("workspaceId")]
    [Description("Optional workspace ID to use")]
    public string? WorkspaceId { get; set; }
}

public class LoadProjectResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("projectName")]
    public string? ProjectName { get; set; }

    [JsonPropertyName("documentCount")]
    public int DocumentCount { get; set; }

    [JsonPropertyName("referenceCount")]
    public int ReferenceCount { get; set; }
}