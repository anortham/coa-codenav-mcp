using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services.TypeScript;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools.TypeScript;

/// <summary>
/// MCP tool that provides Go to Definition functionality for TypeScript using TSP
/// </summary>
public class TsGoToDefinitionTool : McpToolBase<TsGoToDefinitionParams, TsGoToDefinitionResult>, IDisposable
{
    private readonly ILogger<TsGoToDefinitionTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly Dictionary<string, TsServerProtocolHandler> _serverHandlers = new();
    private readonly SemaphoreSlim _serverLock = new(1, 1);

    public override string Name => ToolNames.TsGoToDefinition;
    
    public override string Description => @"Navigate to TypeScript type definitions showing actual implementation structure and properties. Understand interfaces and types before using them.";

    public TsGoToDefinitionTool(
        ILogger<TsGoToDefinitionTool> logger,
        TypeScriptWorkspaceService workspaceService,
        TypeScriptCompilerManager compilerManager)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _compilerManager = compilerManager;
    }

    protected override async Task<TsGoToDefinitionResult> ExecuteInternalAsync(
        TsGoToDefinitionParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogDebug("TsGoToDefinition request: File={File}, Line={Line}, Character={Character}",
            parameters.FilePath, parameters.Line, parameters.Character);

        try
        {
            // Validate TypeScript availability
            var tsError = _compilerManager.ValidateTypeScriptAvailability();
            if (tsError != null)
            {
                return new TsGoToDefinitionResult
                {
                    Success = false,
                    Message = "TypeScript is not available",
                    Error = tsError
                };
            }

            // Find workspace for file
            var workspace = _workspaceService.FindWorkspaceForFile(parameters.FilePath);
            if (workspace == null)
            {
                _logger.LogWarning("No TypeScript workspace found for file: {FilePath}", parameters.FilePath);
                return new TsGoToDefinitionResult
                {
                    Success = false,
                    Message = $"No TypeScript project loaded for file: {parameters.FilePath}",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.WORKSPACE_NOT_LOADED,
                        Message = "TypeScript project not loaded for this file",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new[]
                            {
                                "Load the TypeScript project containing this file",
                                "Use ts_load_tsconfig to load the project's tsconfig.json",
                                "Ensure the file is included in the TypeScript project configuration"
                            },
                            SuggestedActions = new List<SuggestedAction>
                            {
                                new()
                                {
                                    Tool = ToolNames.TsLoadTsConfig,
                                    Description = "Load the TypeScript project",
                                    Parameters = new { tsConfigPath = "path/to/tsconfig.json" }
                                }
                            }
                        }
                    }
                };
            }

            // Get or create server handler
            var handler = await GetOrCreateServerHandlerAsync(workspace.WorkspaceId, workspace.ProjectPath, cancellationToken);
            if (handler == null)
            {
                return new TsGoToDefinitionResult
                {
                    Success = false,
                    Message = "Failed to start TypeScript language server",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.INTERNAL_ERROR,
                        Message = "Could not initialize TypeScript language server"
                    }
                };
            }

            // Normalize file path
            var normalizedPath = Path.GetFullPath(parameters.FilePath).Replace('\\', '/');

            // Convert 0-based to 1-based for TSP (TSP uses 1-based line/offset)
            var tspLine = parameters.Line + 1;
            var tspOffset = parameters.Character + 1;

            // Get definition from TypeScript server
            var definitionResponse = await handler.GetDefinitionAsync(
                normalizedPath, 
                tspLine, 
                tspOffset, 
                cancellationToken);

            if (definitionResponse == null)
            {
                return new TsGoToDefinitionResult
                {
                    Success = false,
                    Message = "No definition found for symbol at the specified position",
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character }
                    },
                    Insights = new List<string>
                    {
                        "The symbol might be defined in an external module",
                        "The position might not be on a symbol",
                        "The file might have syntax errors preventing analysis"
                    }
                };
            }

            // Parse the response
            var locations = ParseDefinitionResponse(definitionResponse.Value);
            
            if (locations.Count == 0)
            {
                return new TsGoToDefinitionResult
                {
                    Success = true,
                    Message = "No definition found",
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character }
                    },
                    Locations = new List<LocationInfo>()
                };
            }

            // Get additional information about the symbol
            var quickInfoResponse = await handler.GetQuickInfoAsync(
                normalizedPath,
                tspLine,
                tspOffset,
                cancellationToken);

            string? symbolType = null;
            string? documentation = null;
            
            if (quickInfoResponse != null)
            {
                ParseQuickInfo(quickInfoResponse.Value, out symbolType, out documentation);
            }

            var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            return new TsGoToDefinitionResult
            {
                Success = true,
                Message = $"Found {locations.Count} definition(s)",
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character }
                },
                Summary = new SummaryInfo
                {
                    TotalFound = locations.Count,
                    Returned = locations.Count,
                    ExecutionTime = $"{executionTime:F2}ms"
                },
                Locations = locations,
                IsDeclaration = IsDeclarationFile(locations.FirstOrDefault()?.FilePath),
                ResultsSummary = new ResultsSummary
                {
                    Total = locations.Count,
                    Included = locations.Count,
                    HasMore = false
                },
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{executionTime:F2}ms"
                },
                Insights = GenerateInsights(locations, symbolType, documentation),
                Actions = GenerateActions(locations)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get TypeScript definition");
            return new TsGoToDefinitionResult
            {
                Success = false,
                Message = $"Error getting definition: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Message = ex.Message
                }
            };
        }
    }

    private async Task<TsServerProtocolHandler?> GetOrCreateServerHandlerAsync(
        string workspaceId, 
        string projectPath,
        CancellationToken cancellationToken)
    {
        await _serverLock.WaitAsync(cancellationToken);
        try
        {
            if (_serverHandlers.TryGetValue(workspaceId, out var existing) && existing.IsRunning)
            {
                return existing;
            }

            // Create new handler
            // Create a logger for TsServerProtocolHandler
            var handlerLogger = _logger as ILogger<TsServerProtocolHandler> 
                ?? new LoggerFactory().CreateLogger<TsServerProtocolHandler>();
            var handler = await TsServerProtocolHandler.CreateAsync(
                handlerLogger,
                projectPath,
                cancellationToken);

            if (handler != null)
            {
                _serverHandlers[workspaceId] = handler;
            }

            return handler;
        }
        finally
        {
            _serverLock.Release();
        }
    }

    private List<LocationInfo> ParseDefinitionResponse(JsonElement response)
    {
        var locations = new List<LocationInfo>();

        if (response.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Unexpected definition response format");
            return locations;
        }

        foreach (var item in response.EnumerateArray())
        {
            try
            {
                var location = new LocationInfo
                {
                    FilePath = item.GetProperty("file").GetString() ?? "",
                    Line = item.GetProperty("start").GetProperty("line").GetInt32() - 1, // Convert to 0-based
                    Column = item.GetProperty("start").GetProperty("offset").GetInt32() - 1,
                    EndLine = item.GetProperty("end").GetProperty("line").GetInt32() - 1,
                    EndColumn = item.GetProperty("end").GetProperty("offset").GetInt32() - 1
                };

                // Try to get context text if available
                if (item.TryGetProperty("contextStart", out var contextStart) &&
                    item.TryGetProperty("contextEnd", out var contextEnd))
                {
                    // Context might be available in some responses
                    // Preview not available on LocationInfo - would need to read file
                }

                locations.Add(location);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse definition location");
            }
        }

        return locations;
    }

    private void ParseQuickInfo(JsonElement response, out string? symbolType, out string? documentation)
    {
        symbolType = null;
        documentation = null;

        try
        {
            if (response.TryGetProperty("displayString", out var displayString))
            {
                symbolType = displayString.GetString();
            }

            if (response.TryGetProperty("documentation", out var docs))
            {
                documentation = docs.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse quick info");
        }
    }

    private string? ExtractSymbolName(LocationInfo? location)
    {
        if (location == null)
            return null;

        // In a real implementation, we'd read the file and extract the symbol name
        // For now, return a placeholder
        return Path.GetFileNameWithoutExtension(location.FilePath);
    }

    private bool IsDeclarationFile(string? filePath)
    {
        return !string.IsNullOrEmpty(filePath) && filePath.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase);
    }

    private List<string> GenerateInsights(List<LocationInfo> locations, string? symbolType, string? documentation)
    {
        var insights = new List<string>();

        if (locations.Count > 0)
        {
            var firstLocation = locations.First();
            insights.Add($"Definition found in: {Path.GetFileName(firstLocation.FilePath)}");

            if (IsDeclarationFile(firstLocation.FilePath))
            {
                insights.Add("Symbol is defined in a TypeScript declaration file");
            }

            if (!string.IsNullOrEmpty(symbolType))
            {
                insights.Add($"Symbol type: {symbolType}");
            }

            if (!string.IsNullOrEmpty(documentation))
            {
                insights.Add("Documentation available");
            }

            if (locations.Count > 1)
            {
                insights.Add($"Multiple definitions found ({locations.Count} total)");
            }
        }

        return insights;
    }

    private List<AIAction> GenerateActions(List<LocationInfo> locations)
    {
        var actions = new List<AIAction>();

        if (locations.Count > 0)
        {
            var firstLocation = locations.First();
            
            actions.Add(new AIAction
            {
                Action = "read_definition",
                Description = $"Read the definition at {Path.GetFileName(firstLocation.FilePath)}:{firstLocation.Line + 1}",
                Category = "navigate"
            });

            actions.Add(new AIAction
            {
                Action = ToolNames.TsFindAllReferences,
                Description = "Find all references to this symbol",
                Category = "analyze"
            });

            actions.Add(new AIAction
            {
                Action = ToolNames.TsHover,
                Description = "Get detailed type information",
                Category = "analyze"
            });
        }

        return actions;
    }

    public void Dispose()
    {
        foreach (var handler in _serverHandlers.Values)
        {
            handler?.Dispose();
        }
        _serverHandlers.Clear();
        _serverLock?.Dispose();
    }
}

/// <summary>
/// Parameters for TypeScript Go to Definition
/// </summary>
public class TsGoToDefinitionParams
{
    [Required]
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }

    [Required]
    [JsonPropertyName("line")]
    [Range(0, int.MaxValue)]
    public int Line { get; set; }

    [Required]
    [JsonPropertyName("character")]
    [Range(0, int.MaxValue)]
    public int Character { get; set; }
}