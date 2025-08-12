using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services.TypeScript;
using COA.Mcp.Framework;
using COA.Mcp.Framework.Base;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools.TypeScript;

/// <summary>
/// Tool for loading TypeScript configuration files (tsconfig.json)
/// </summary>
public class LoadTsConfigTool : McpToolBase<TsLoadConfigParams, TsLoadConfigResult>
{
    private readonly ILogger<LoadTsConfigTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;

    public override string Name => ToolNames.TsLoadTsConfig;
    
    public override string Description => @"Load a TypeScript project via tsconfig.json file.
Returns: Project configuration including compiler options, files, and references.
Prerequisites: TypeScript must be installed (npm install -g typescript).
Error handling: Returns specific error codes if TypeScript is not installed or tsconfig.json is not found.
Use cases: Loading TypeScript projects, initializing TypeScript workspace, preparing for TypeScript analysis.
Not for: Loading JavaScript-only projects, loading package.json (use different tool).";

    public override ToolCategory Category => ToolCategory.Resources;

    public LoadTsConfigTool(
        ILogger<LoadTsConfigTool> logger,
        TypeScriptWorkspaceService workspaceService)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
    }

    protected override async Task<TsLoadConfigResult> ExecuteInternalAsync(
        TsLoadConfigParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogInformation("Loading TypeScript configuration: {TsConfigPath}", parameters.TsConfigPath);

        try
        {
            // Load the TypeScript configuration
            var result = await _workspaceService.LoadTsConfigAsync(
                parameters.TsConfigPath,
                parameters.WorkspaceId);

            // Add execution metadata
            result.Meta = new Mcp.Framework.Models.ToolExecutionMetadata
            {
                Mode = "full",
                Truncated = false,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load TypeScript configuration from {TsConfigPath}", parameters.TsConfigPath);
            
            return new TsLoadConfigResult
            {
                Success = false,
                Message = $"Failed to load TypeScript configuration: {ex.Message}",
                Error = new Mcp.Framework.Models.ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Message = ex.Message,
                    Recovery = new Mcp.Framework.Models.RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Check the tsconfig.json file path",
                            "Ensure the file is valid JSON",
                            "Check for syntax errors in tsconfig.json"
                        }
                    }
                },
                Meta = new Mcp.Framework.Models.ToolExecutionMetadata
                {
                    Mode = "error",
                    Truncated = false,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                }
            };
        }
    }
}