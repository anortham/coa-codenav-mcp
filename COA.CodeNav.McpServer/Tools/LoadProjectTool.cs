using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// Tool for loading C# project files into the Roslyn workspace
/// </summary>
public class LoadProjectTool : McpToolBase<LoadProjectParams, LoadProjectResult>
{
    private readonly ILogger<LoadProjectTool> _logger;
    private readonly MSBuildWorkspaceManager _workspaceManager;
    private readonly RoslynWorkspaceService _workspaceService;

    public override string Name => "csharp_load_project";
    public override string Description => "Load a C# project file into the Roslyn workspace";

    public LoadProjectTool(
        ILogger<LoadProjectTool> logger,
        MSBuildWorkspaceManager workspaceManager,
        RoslynWorkspaceService workspaceService)
        : base(logger)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _workspaceService = workspaceService;
    }

    protected override async Task<LoadProjectResult> ExecuteInternalAsync(
        LoadProjectParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogInformation("Loading project: {ProjectPath}", parameters.ProjectPath);

        // Validate the project file exists
        if (!File.Exists(parameters.ProjectPath))
        {
            return new LoadProjectResult
            {
                Success = false,
                Message = $"Project file not found: {parameters.ProjectPath}",
                Error = new ErrorInfo
                {
                    Code = "PROJECT_NOT_FOUND",
                    Message = $"Project file not found: {parameters.ProjectPath}",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Verify the project path is correct and absolute",
                            "Check that the file has a .csproj extension",
                            "Ensure you have read permissions for the file"
                        }
                    }
                }
            };
        }

        try
        {
            // Load the project
            var project = await _workspaceManager.LoadProjectAsync(
                parameters.ProjectPath, 
                parameters.WorkspaceId);

            if (project == null)
            {
                return new LoadProjectResult
                {
                    Success = false,
                    Message = "Failed to load project",
                    Error = new ErrorInfo
                    {
                        Code = "PROJECT_LOAD_FAILED",
                        Message = "Failed to load project into MSBuild workspace",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new[]
                            {
                                "Check that the project file is valid",
                                "Ensure all required MSBuild tools are installed",
                                "Verify .NET SDK is properly configured",
                                "Try restoring NuGet packages first"
                            }
                        }
                    }
                };
            }

            // Register with workspace service
            var workspaceInfo = await _workspaceService.LoadProjectAsync(parameters.ProjectPath);
            
            if (workspaceInfo == null)
            {
                return new LoadProjectResult
                {
                    Success = false,
                    Message = "Failed to register project in workspace service",
                    Error = new ErrorInfo
                    {
                        Code = "WORKSPACE_REGISTRATION_FAILED",
                        Message = "Failed to register project in workspace service"
                    }
                };
            }

            // Get project info
            var documentCount = project.Documents.Count();
            var references = project.MetadataReferences.Count();

            _logger.LogInformation("Successfully loaded project '{ProjectName}' with {DocumentCount} documents", 
                project.Name, documentCount);

            // Generate insights
            var insights = new List<string>
            {
                $"Loaded project '{project.Name}' with {documentCount} documents",
                $"Project has {references} references"
            };
            
            if (documentCount > 100)
                insights.Add("Large project - analysis operations may take longer");
            
            // Generate next actions
            var actions = new List<AIAction>
            {
                new AIAction
                {
                    Action = "csharp_get_diagnostics",
                    Description = "Check for compilation errors and warnings",
                    Priority = 80
                },
                new AIAction
                {
                    Action = "csharp_document_symbols",
                    Description = "View symbols in project files",
                    Priority = 60
                }
            };

            return new LoadProjectResult
            {
                Success = true,
                WorkspaceId = workspaceInfo.Id,
                Message = $"Successfully loaded project '{project.Name}'",
                ProjectName = project.Name,
                DocumentCount = documentCount,
                ReferenceCount = references,
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
            _logger.LogError(ex, "Error loading project: {ProjectPath}", parameters.ProjectPath);
            return new LoadProjectResult
            {
                Success = false,
                Message = $"Error loading project: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = "INTERNAL_ERROR",
                    Message = ex.Message,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Check the server logs for detailed error information",
                            "Verify the project file is not corrupted",
                            "Try running 'dotnet restore' on the project first"
                        }
                    }
                }
            };
        }
    }

}

public class LoadProjectParams
{
    [JsonPropertyName("projectPath")]
    [System.ComponentModel.DataAnnotations.Required]
    [COA.Mcp.Framework.Attributes.Description("Path to the .csproj file to load")]
    public string ProjectPath { get; set; } = string.Empty;

    [JsonPropertyName("workspaceId")]
    [COA.Mcp.Framework.Attributes.Description("Optional workspace ID to use")]
    public string? WorkspaceId { get; set; }
}

public class LoadProjectResult : ToolResultBase
{
    public override string Operation => "csharp_load_project";

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("projectName")]
    public string? ProjectName { get; set; }

    [JsonPropertyName("documentCount")]
    public int DocumentCount { get; set; }

    [JsonPropertyName("referenceCount")]
    public int ReferenceCount { get; set; }
}