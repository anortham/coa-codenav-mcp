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
    
    public override string Description => @"Load TypeScript project configuration BEFORE using other TypeScript tools. Reads tsconfig.json to enable intelligent analysis and navigation.

Critical: Run this FIRST when starting TypeScript work. Without loading tsconfig, other TypeScript tools can't provide accurate results.

Prerequisites: TypeScript must be installed (npm install -g typescript).
Use cases: Starting TypeScript projects, enabling intelligent analysis, loading compiler settings.";

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