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
    
    public override string Description => @"**ESSENTIAL FIRST STEP FOR ALL TYPESCRIPT WORK** - Before using any TypeScript tools, load your project configuration. This is the foundation that enables intelligent TypeScript analysis and navigation.

**PROJECT INITIALIZATION:**
- Starting work on any TypeScript project? Run this FIRST before anything else
- Opens and analyzes your tsconfig.json to understand project structure
- Loads compiler options, path mappings, and project references
- Enables all other TypeScript tools to work with full context

**TYPESCRIPT INTELLIGENCE FOUNDATION:**
- Provides the compiler settings that drive intelligent analysis
- Understands your custom path mappings and module resolution
- Loads project references for monorepo and multi-project setups
- Establishes the TypeScript version and strict mode settings

**CRITICAL FOR ACCURACY:**
- Without loading tsconfig first, other tools can't provide accurate results
- Ensures type checking follows your exact project configuration
- Respects your include/exclude patterns and file organization
- Uses your specific compiler options for diagnostics and navigation

**ONE-TIME SETUP PER SESSION:**
- Load once per TypeScript project to enable all features
- Handles complex project configurations automatically
- Works with modern TypeScript setups including workspaces
- Validates TypeScript installation and project structure

**THE TYPESCRIPT WORKFLOW:**
1. **ALWAYS start here** - Load your tsconfig.json
2. Then use any other TypeScript tools with full intelligence
3. All navigation, diagnostics, and refactoring now work perfectly

**SUCCESS GUARANTEE:** This tool makes every other TypeScript tool work better by providing complete project context.

Prerequisites: TypeScript must be installed (npm install -g typescript).
See also: All other ts_ tools require this to be run first for optimal results.";

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