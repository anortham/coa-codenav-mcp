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
/// MCP tool that provides document symbols functionality for TypeScript using TSP navtree command
/// </summary>
public class TsDocumentSymbolsTool : McpToolBase<TsDocumentSymbolsParams, TsDocumentSymbolsResult>, IDisposable
{
    private readonly ILogger<TsDocumentSymbolsTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly Dictionary<string, TsServerProtocolHandler> _serverHandlers = new();
    private readonly SemaphoreSlim _serverLock = new(1, 1);

    public override string Name => ToolNames.TsDocumentSymbols;
    
    public override string Description => @"Map TypeScript file structure BEFORE making changes. Shows all interfaces, classes, functions, and exports to understand what actually exists.

Critical: Before using anything from a TypeScript file, map its contents FIRST. Prevents import errors from non-existent types or wrong assumptions about file structure.

Prerequisites: Call ts_load_tsconfig first to load the TypeScript project.
Use cases: Understanding file organization, checking available exports, planning code changes.";

    public TsDocumentSymbolsTool(
        ILogger<TsDocumentSymbolsTool> logger,
        TypeScriptWorkspaceService workspaceService,
        TypeScriptCompilerManager compilerManager)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _compilerManager = compilerManager;
    }

    protected override async Task<TsDocumentSymbolsResult> ExecuteInternalAsync(
        TsDocumentSymbolsParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogDebug("TsDocumentSymbols request: File={File}, MaxResults={MaxResults}",
            parameters.FilePath, parameters.MaxResults);

        try
        {
            // Validate TypeScript availability
            var tsError = _compilerManager.ValidateTypeScriptAvailability();
            if (tsError != null)
            {
                return new TsDocumentSymbolsResult
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
                return new TsDocumentSymbolsResult
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
                return new TsDocumentSymbolsResult
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

            // Get navigation tree from TypeScript server
            var navTreeResponse = await handler.GetNavigationTreeAsync(normalizedPath, cancellationToken);

            if (navTreeResponse == null)
            {
                return new TsDocumentSymbolsResult
                {
                    Success = false,
                    Message = "Failed to get document symbols",
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath
                    }
                };
            }

            // Parse the navigation tree
            var symbols = ParseNavigationTree(navTreeResponse.Value, parameters.MaxResults);
            
            var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            return new TsDocumentSymbolsResult
            {
                Success = true,
                Message = $"Found {symbols.Count} symbols in document",
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath
                },
                Summary = new SummaryInfo
                {
                    TotalFound = symbols.Count,
                    Returned = symbols.Count,
                    ExecutionTime = $"{executionTime:F2}ms"
                },
                Symbols = symbols,
                ResultsSummary = new ResultsSummary
                {
                    Total = symbols.Count,
                    Included = symbols.Count,
                    HasMore = false
                },
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{executionTime:F2}ms"
                },
                Insights = GenerateInsights(symbols),
                Actions = GenerateActions(parameters.FilePath)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get TypeScript document symbols");
            return new TsDocumentSymbolsResult
            {
                Success = false,
                Message = $"Error getting document symbols: {ex.Message}",
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

    private List<SymbolInfo> ParseNavigationTree(JsonElement navTree, int maxResults)
    {
        var symbols = new List<SymbolInfo>();
        
        if (navTree.TryGetProperty("childItems", out var childItems) && childItems.ValueKind == JsonValueKind.Array)
        {
            ParseNavTreeItems(childItems, symbols, null, 0, maxResults);
        }
        else
        {
            // Sometimes the response is directly an array of items
            ParseNavTreeItems(navTree, symbols, null, 0, maxResults);
        }

        return symbols;
    }

    private void ParseNavTreeItems(JsonElement items, List<SymbolInfo> symbols, string? parentName, int level, int maxResults)
    {
        if (symbols.Count >= maxResults)
            return;

        if (items.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in items.EnumerateArray())
        {
            if (symbols.Count >= maxResults)
                break;

            try
            {
                var symbol = new SymbolInfo
                {
                    Name = item.GetProperty("text").GetString() ?? "",
                    Kind = MapKindToSymbolKind(item.GetProperty("kind").GetString()),
                    ParentName = parentName,
                    Level = level
                };

                // Parse location if available
                if (item.TryGetProperty("spans", out var spans) && spans.ValueKind == JsonValueKind.Array)
                {
                    foreach (var span in spans.EnumerateArray())
                    {
                        if (span.TryGetProperty("start", out var start))
                        {
                            symbol.Location = new LocationInfo
                            {
                                FilePath = "", // Will be filled by the calling context
                                Line = start.GetProperty("line").GetInt32() - 1, // Convert to 0-based
                                Column = start.GetProperty("offset").GetInt32() - 1
                            };

                            if (span.TryGetProperty("end", out var end))
                            {
                                symbol.Location.EndLine = end.GetProperty("line").GetInt32() - 1;
                                symbol.Location.EndColumn = end.GetProperty("offset").GetInt32() - 1;
                            }
                            break; // Use first span
                        }
                    }
                }

                // Add modifiers if present
                if (item.TryGetProperty("kindModifiers", out var modifiers))
                {
                    var modifierString = modifiers.GetString();
                    if (!string.IsNullOrEmpty(modifierString))
                    {
                        symbol.Modifiers = modifierString.Split(',').ToList();
                    }
                }

                symbols.Add(symbol);

                // Parse child items recursively
                if (item.TryGetProperty("childItems", out var childItems) && childItems.ValueKind == JsonValueKind.Array)
                {
                    ParseNavTreeItems(childItems, symbols, symbol.Name, level + 1, maxResults);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse navigation tree item");
            }
        }
    }

    private string MapKindToSymbolKind(string? tsKind)
    {
        return tsKind?.ToLower() switch
        {
            "class" => "Class",
            "interface" => "Interface",
            "enum" => "Enum",
            "module" => "Module",
            "namespace" => "Namespace",
            "function" => "Function",
            "method" => "Method",
            "property" => "Property",
            "variable" => "Variable",
            "const" => "Constant",
            "let" => "Variable",
            "constructor" => "Constructor",
            "type" => "TypeAlias",
            "alias" => "TypeAlias",
            _ => "Unknown"
        };
    }

    private List<string> GenerateInsights(List<SymbolInfo> symbols)
    {
        var insights = new List<string>();

        if (symbols.Count > 0)
        {
            // Count symbol types
            var symbolCounts = symbols.GroupBy(s => s.Kind)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count());

            insights.Add($"Document contains {symbols.Count} symbols");
            
            foreach (var kvp in symbolCounts.Take(3))
            {
                insights.Add($"{kvp.Value} {kvp.Key}(s)");
            }

            // Check for complexity indicators
            var maxNesting = symbols.Max(s => s.Level);
            if (maxNesting > 3)
            {
                insights.Add($"Maximum nesting level: {maxNesting}");
            }

            var classes = symbols.Where(s => s.Kind == "Class").ToList();
            if (classes.Count > 5)
            {
                insights.Add($"Contains {classes.Count} classes - consider splitting into multiple files");
            }
        }

        return insights;
    }

    private List<AIAction> GenerateActions(string filePath)
    {
        return new List<AIAction>
        {
            new()
            {
                Action = ToolNames.TsSymbolSearch,
                Description = "Search for specific symbols in the project",
                Category = "search"
            },
            new()
            {
                Action = ToolNames.TsGoToDefinition,
                Description = "Navigate to a specific symbol definition",
                Category = "navigate"
            },
            new()
            {
                Action = "analyze_complexity",
                Description = "Analyze code complexity metrics",
                Category = "analyze"
            }
        };
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
/// Parameters for TypeScript Document Symbols
/// </summary>
public class TsDocumentSymbolsParams
{
    [Required]
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }

    [JsonPropertyName("maxResults")]
    [Range(1, 500)]
    public int MaxResults { get; set; } = 100;
}