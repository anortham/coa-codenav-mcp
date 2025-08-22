using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that refreshes workspace or individual documents to resolve stale diagnostics
/// </summary>
public class RefreshWorkspaceTool : McpToolBase<RefreshWorkspaceParams, RefreshWorkspaceToolResult>
{
    private readonly ILogger<RefreshWorkspaceTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly ITokenEstimator _tokenEstimator;

    public override string Name => ToolNames.RefreshWorkspace;
    public override string Description => @"Refresh workspace to resolve stale diagnostics and sync with external file changes. Fixes cached analysis results that may be outdated.

Critical: Use when analysis results seem stale or after external tools modify files. Resolves caching issues that cause incorrect diagnostics.

Prerequisites: Call csharp_load_solution or csharp_load_project first.
Use cases: Fixing stale diagnostics, syncing with external changes, resolving cache issues, troubleshooting analysis.";

    public RefreshWorkspaceTool(
        ILogger<RefreshWorkspaceTool> logger,
        RoslynWorkspaceService workspaceService,
        ITokenEstimator tokenEstimator)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _tokenEstimator = tokenEstimator;
    }

    protected override async Task<RefreshWorkspaceToolResult> ExecuteInternalAsync(
        RefreshWorkspaceParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogInformation("Processing refresh request: Scope={Scope}, Path={Path}", 
            parameters.Scope, parameters.FilePath ?? parameters.WorkspacePath);

        var refreshedFiles = new List<string>();
        var failedFiles = new List<string>();

        try
        {
            switch (parameters.Scope?.ToLower())
            {
                case "document":
                case "file":
                    if (string.IsNullOrEmpty(parameters.FilePath))
                    {
                        return CreateErrorResult(
                            "File path is required when scope is 'document' or 'file'",
                            ErrorCodes.INVALID_PARAMETERS,
                            parameters,
                            startTime);
                    }

                    var refreshedDoc = await _workspaceService.RefreshDocumentAsync(parameters.FilePath);
                    if (refreshedDoc != null)
                    {
                        refreshedFiles.Add(parameters.FilePath);
                        _logger.LogInformation("Successfully refreshed document: {FilePath}", parameters.FilePath);
                    }
                    else
                    {
                        failedFiles.Add(parameters.FilePath);
                        _logger.LogWarning("Failed to refresh document: {FilePath}", parameters.FilePath);
                    }
                    break;

                case "workspace":
                default:
                    if (string.IsNullOrEmpty(parameters.WorkspacePath))
                    {
                        return CreateErrorResult(
                            "Workspace path is required when scope is 'workspace'",
                            ErrorCodes.INVALID_PARAMETERS,
                            parameters,
                            startTime);
                    }

                    var success = await _workspaceService.InvalidateWorkspaceAsync(parameters.WorkspacePath);
                    if (success)
                    {
                        refreshedFiles.Add(parameters.WorkspacePath);
                        _logger.LogInformation("Successfully refreshed workspace: {WorkspacePath}", parameters.WorkspacePath);
                    }
                    else
                    {
                        failedFiles.Add(parameters.WorkspacePath);
                        _logger.LogWarning("Failed to refresh workspace: {WorkspacePath}", parameters.WorkspacePath);
                    }
                    break;
            }

            // Generate insights
            var insights = GenerateInsights(refreshedFiles, failedFiles, parameters);

            // Generate next actions
            var actions = GenerateNextActions(refreshedFiles, parameters);

            return new RefreshWorkspaceToolResult
            {
                Success = failedFiles.Count == 0,
                Message = failedFiles.Count == 0
                    ? $"Successfully refreshed {refreshedFiles.Count} item(s)"
                    : $"Refreshed {refreshedFiles.Count} item(s), {failedFiles.Count} failed",
                RefreshedFiles = refreshedFiles,
                FailedFiles = failedFiles,
                Query = new RefreshQuery
                {
                    Scope = parameters.Scope ?? "workspace",
                    FilePath = parameters.FilePath,
                    WorkspacePath = parameters.WorkspacePath
                },
                Summary = new SummaryInfo
                {
                    TotalFound = refreshedFiles.Count + failedFiles.Count,
                    Returned = refreshedFiles.Count,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                },
                Insights = insights,
                Actions = actions,
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    Tokens = 800 + (refreshedFiles.Count * 50)
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during refresh operation");
            return CreateErrorResult(
                $"Refresh operation failed: {ex.Message}",
                ErrorCodes.OPERATION_FAILED,
                parameters,
                startTime);
        }
    }

    private List<string> GenerateInsights(List<string> refreshedFiles, List<string> failedFiles, RefreshWorkspaceParams parameters)
    {
        var insights = new List<string>();

        if (refreshedFiles.Any())
        {
            insights.Add($"✅ Successfully refreshed {refreshedFiles.Count} item(s) - diagnostics should now be current");
        }

        if (failedFiles.Any())
        {
            insights.Add($"⚠️ Failed to refresh {failedFiles.Count} item(s) - check file paths and workspace status");
        }

        if (parameters.Scope?.ToLower() == "workspace")
        {
            insights.Add("💡 Workspace refresh reloads all documents and clears caches - use for major sync issues");
        }
        else
        {
            insights.Add("💡 Document refresh is lightweight - use when specific files show stale diagnostics");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(List<string> refreshedFiles, RefreshWorkspaceParams parameters)
    {
        var actions = new List<AIAction>();

        if (refreshedFiles.Any())
        {
            // Suggest getting diagnostics to see refreshed results
            actions.Add(new AIAction
            {
                Action = ToolNames.GetDiagnostics,
                Description = "Get current diagnostics after refresh",
                Parameters = new Dictionary<string, object>
                {
                    ["scope"] = parameters.Scope?.ToLower() == "document" ? "file" : "solution",
                    ["filePath"] = parameters.FilePath ?? ""
                },
                Priority = 90,
                Category = "validation"
            });

            // If it was a document refresh, suggest workspace-level tools
            if (parameters.Scope?.ToLower() == "document" && !string.IsNullOrEmpty(parameters.FilePath))
            {
                actions.Add(new AIAction
                {
                    Action = ToolNames.GoToDefinition,
                    Description = "Test navigation with refreshed document",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = parameters.FilePath,
                        ["line"] = 1,
                        ["column"] = 1
                    },
                    Priority = 70,
                    Category = "testing"
                });
            }
        }

        return actions;
    }

    private RefreshWorkspaceToolResult CreateErrorResult(
        string message,
        string errorCode,
        RefreshWorkspaceParams parameters,
        DateTime startTime)
    {
        return new RefreshWorkspaceToolResult
        {
            Success = false,
            Message = message,
            RefreshedFiles = new List<string>(),
            FailedFiles = new List<string>(),
            Error = new ErrorInfo
            {
                Code = errorCode,
                Message = message,
                Recovery = new RecoveryInfo
                {
                    Steps = new[]
                    {
                        "Verify file paths are correct and absolute",
                        "Ensure workspace is loaded with csharp_load_solution or csharp_load_project",
                        "Check that files exist on disk"
                    }
                }
            },
            Query = new RefreshQuery
            {
                Scope = parameters.Scope ?? "workspace",
                FilePath = parameters.FilePath,
                WorkspacePath = parameters.WorkspacePath
            },
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                Tokens = 500
            }
        };
    }

}

/// <summary>
/// Parameters for RefreshWorkspace tool
/// </summary>
public class RefreshWorkspaceParams
{
    [JsonPropertyName("scope")]
    [COA.Mcp.Framework.Attributes.Description("Refresh scope: 'workspace' (full reload), 'document'/'file' (single file)")]
    public string? Scope { get; set; }

    [JsonPropertyName("workspacePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to solution or project file (required for workspace scope)")]
    public string? WorkspacePath { get; set; }

    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to specific file to refresh (required for document/file scope)")]
    public string? FilePath { get; set; }
}