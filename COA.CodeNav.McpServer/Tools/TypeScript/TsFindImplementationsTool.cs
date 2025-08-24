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
/// MCP tool that finds implementations of interfaces and abstract methods in TypeScript
/// </summary>
public class TsFindImplementationsTool : McpToolBase<TsFindImplementationsParams, TsFindImplementationsResult>, IDisposable
{
    private readonly ILogger<TsFindImplementationsTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly Dictionary<string, TsServerProtocolHandler> _serverHandlers = new();
    private readonly SemaphoreSlim _serverLock = new(1, 1);

    public override string Name => ToolNames.TsFindImplementations;
    
    public override string Description => @"Find all TypeScript interface implementations and abstract method overrides to discover concrete classes that implement interfaces.

Parameters use 1-based indexing:
- line: 1-based line number (first line is 1)  
- character: 1-based character position (first character is 1)";

    public TsFindImplementationsTool(
        IServiceProvider serviceProvider,
        ILogger<TsFindImplementationsTool> logger,
        TypeScriptWorkspaceService workspaceService,
        TypeScriptCompilerManager compilerManager)
        : base(serviceProvider, logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _compilerManager = compilerManager;
    }

    protected override async Task<TsFindImplementationsResult> ExecuteInternalAsync(
        TsFindImplementationsParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogDebug("TsFindImplementations request: File={File}, Line={Line}, Character={Character}",
            parameters.FilePath, parameters.Line, parameters.Character);

        try
        {
            // Validate TypeScript availability
            var tsError = _compilerManager.ValidateTypeScriptAvailability();
            if (tsError != null)
            {
                return new TsFindImplementationsResult
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
                return new TsFindImplementationsResult
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
                return new TsFindImplementationsResult
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

            // Parameters are 1-based, TSP expects 1-based, so no conversion needed
            var tspLine = parameters.Line;
            var tspOffset = parameters.Character;

            // Get implementations from TypeScript server
            var implementationsResponse = await handler.GetImplementationAsync(
                normalizedPath, 
                tspLine, 
                tspOffset, 
                cancellationToken);

            if (implementationsResponse == null)
            {
                return new TsFindImplementationsResult
                {
                    Success = false,
                    Message = "No implementations found for symbol at the specified position",
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character }
                    },
                    Insights = new List<string>
                    {
                        "The symbol might not be an interface or abstract member",
                        "The position might not be on a symbol that can be implemented",
                        "No implementations might exist in the current project"
                    }
                };
            }

            // Parse the response
            var implementations = ParseImplementationsResponse(implementationsResponse.Value, parameters.MaxResults);
            
            if (implementations.Count == 0)
            {
                return new TsFindImplementationsResult
                {
                    Success = true,
                    Message = "No implementations found",
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character }
                    },
                    Implementations = new List<ImplementationInfo>()
                };
            }

            // Get additional information about the symbol
            var quickInfoResponse = await handler.GetQuickInfoAsync(
                normalizedPath,
                tspLine,
                tspOffset,
                cancellationToken);

            string? symbolType = null;
            string? symbolName = null;
            
            if (quickInfoResponse != null)
            {
                ParseQuickInfo(quickInfoResponse.Value, out symbolType, out symbolName);
            }

            var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            return new TsFindImplementationsResult
            {
                Success = true,
                Message = $"Found {implementations.Count} implementation(s)",
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character }
                },
                Summary = new SummaryInfo
                {
                    TotalFound = implementations.Count,
                    Returned = implementations.Count,
                    ExecutionTime = $"{executionTime:F2}ms"
                },
                Implementations = implementations,
                InterfaceName = symbolName,
                ResultsSummary = new ResultsSummary
                {
                    Total = implementations.Count,
                    Included = implementations.Count,
                    HasMore = false
                },
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{executionTime:F2}ms"
                },
                Insights = GenerateInsights(implementations, symbolName, symbolType),
                Actions = GenerateActions(implementations, parameters.FilePath)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find TypeScript implementations");
            return new TsFindImplementationsResult
            {
                Success = false,
                Message = $"Error finding implementations: {ex.Message}",
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

    private List<ImplementationInfo> ParseImplementationsResponse(JsonElement response, int maxResults)
    {
        var implementations = new List<ImplementationInfo>();

        if (response.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Unexpected implementation response format");
            return implementations;
        }

        foreach (var item in response.EnumerateArray())
        {
            if (implementations.Count >= maxResults)
                break;

            try
            {
                var implementation = new ImplementationInfo
                {
                    Location = new LocationInfo
                    {
                        FilePath = item.GetProperty("file").GetString() ?? "",
                        Line = item.GetProperty("start").GetProperty("line").GetInt32() - 1, // Convert to 0-based
                        Column = item.GetProperty("start").GetProperty("offset").GetInt32() - 1,
                        EndLine = item.GetProperty("end").GetProperty("line").GetInt32() - 1,
                        EndColumn = item.GetProperty("end").GetProperty("offset").GetInt32() - 1
                    }
                };

                // Try to get the implementing type name from context
                if (item.TryGetProperty("contextStart", out var contextStart) &&
                    item.TryGetProperty("contextEnd", out var contextEnd))
                {
                    // Context might contain the implementing class/type name
                    // This would require additional processing or file reading
                }

                // Extract implementing type from file path (heuristic)
                var fileName = Path.GetFileNameWithoutExtension(implementation.Location?.FilePath ?? "");
                implementation.ImplementingType = fileName;

                implementations.Add(implementation);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse implementation location");
            }
        }

        return implementations;
    }

    private void ParseQuickInfo(JsonElement response, out string? symbolType, out string? symbolName)
    {
        symbolType = null;
        symbolName = null;

        try
        {
            if (response.TryGetProperty("displayString", out var displayString))
            {
                var display = displayString.GetString();
                if (!string.IsNullOrEmpty(display))
                {
                    // Try to extract type and name from display string
                    // TypeScript format is usually something like "(interface) InterfaceName" or "(class) ClassName"
                    var parts = display.Split(' ', 2);
                    if (parts.Length > 1)
                    {
                        symbolType = parts[0].Trim('(', ')');
                        symbolName = parts[1];
                    }
                    else
                    {
                        symbolName = display;
                    }
                }
            }

            // Sometimes the symbol name is in a separate field
            if (symbolName == null && response.TryGetProperty("name", out var name))
            {
                symbolName = name.GetString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse quick info");
        }
    }

    private List<string> GenerateInsights(List<ImplementationInfo> implementations, string? symbolName, string? symbolType)
    {
        var insights = new List<string>();

        if (implementations.Count > 0)
        {
            if (!string.IsNullOrEmpty(symbolName))
            {
                insights.Add($"Found {implementations.Count} implementation(s) of {symbolName}");
            }
            else
            {
                insights.Add($"Found {implementations.Count} implementation(s)");
            }

            if (!string.IsNullOrEmpty(symbolType) && symbolType.ToLower() == "interface")
            {
                insights.Add("Symbol is an interface");
            }

            // Group by file
            var fileCount = implementations.Select(i => i.Location?.FilePath).Distinct().Count();
            if (fileCount > 1)
            {
                insights.Add($"Implementations spread across {fileCount} files");
            }

            // Check for test implementations
            var testImplementations = implementations.Where(i => 
                i.Location?.FilePath?.Contains("test", StringComparison.OrdinalIgnoreCase) == true ||
                i.Location?.FilePath?.Contains("spec", StringComparison.OrdinalIgnoreCase) == true).Count();
            
            if (testImplementations > 0)
            {
                insights.Add($"{testImplementations} test implementation(s) found");
            }

            // Check for mock implementations
            var mockImplementations = implementations.Where(i => 
                i.Location?.FilePath?.Contains("mock", StringComparison.OrdinalIgnoreCase) == true ||
                i.ImplementingType?.Contains("Mock", StringComparison.OrdinalIgnoreCase) == true).Count();
            
            if (mockImplementations > 0)
            {
                insights.Add($"{mockImplementations} mock implementation(s) found");
            }
        }
        else
        {
            insights.Add("No implementations found in the current project");
            insights.Add("The symbol might not be an interface or abstract member");
            insights.Add("Consider checking if this is the right symbol to search implementations for");
        }

        return insights;
    }

    private List<AIAction> GenerateActions(List<ImplementationInfo> implementations, string sourceFile)
    {
        var actions = new List<AIAction>();

        if (implementations.Count > 0)
        {
            var firstImpl = implementations.First();
            
            actions.Add(new AIAction
            {
                Action = "navigate_to_implementation",
                Description = $"Navigate to implementation in {Path.GetFileName(firstImpl.Location?.FilePath ?? "")}",
                Category = "navigate"
            });

            actions.Add(new AIAction
            {
                Action = ToolNames.TsFindAllReferences,
                Description = "Find all references to the interface",
                Category = "analyze"
            });

            actions.Add(new AIAction
            {
                Action = ToolNames.TsDocumentSymbols,
                Description = "View structure of implementing files",
                Category = "navigate"
            });

            if (implementations.Count > 3)
            {
                actions.Add(new AIAction
                {
                    Action = "analyze_implementations",
                    Description = "Analyze implementation patterns and consistency",
                    Category = "analyze"
                });
            }
        }
        else
        {
            actions.Add(new AIAction
            {
                Action = ToolNames.TsGoToDefinition,
                Description = "Navigate to the symbol definition",
                Category = "navigate"
            });

            actions.Add(new AIAction
            {
                Action = ToolNames.TsSymbolSearch,
                Description = "Search for related symbols",
                Category = "search"
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
/// Parameters for TypeScript Find Implementations
/// </summary>
public class TsFindImplementationsParams
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

    [JsonPropertyName("maxResults")]
    [Range(1, 500)]
    public int MaxResults { get; set; } = 100;
}