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
/// MCP tool that provides hover information (quick info) for TypeScript symbols using TSP
/// </summary>
public class TsHoverTool : McpToolBase<TsHoverParams, TsHoverResult>, IDisposable
{
    private readonly ILogger<TsHoverTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly Dictionary<string, TsServerProtocolHandler> _serverHandlers = new();
    private readonly SemaphoreSlim _serverLock = new(1, 1);

    public override string Name => ToolNames.TsHover;
    
    public override string Description => @"Check TypeScript method signatures and types BEFORE calling. Shows exact parameter types, return values, and JSDoc documentation to prevent type errors.

Critical: Before calling any unfamiliar TypeScript method, verify its signature first. Prevents wrong parameter types, missing optional properties, or using deprecated overloads.

Prerequisites: Call ts_load_tsconfig first to load the TypeScript project.
Use cases: Verifying function signatures, checking optional properties, understanding generic types.";

    public TsHoverTool(
        ILogger<TsHoverTool> logger,
        TypeScriptWorkspaceService workspaceService,
        TypeScriptCompilerManager compilerManager)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _compilerManager = compilerManager;
    }

    protected override async Task<TsHoverResult> ExecuteInternalAsync(
        TsHoverParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogDebug("TsHover request: File={File}, Line={Line}, Character={Character}",
            parameters.FilePath, parameters.Line, parameters.Character);

        try
        {
            // Validate TypeScript availability
            var tsError = _compilerManager.ValidateTypeScriptAvailability();
            if (tsError != null)
            {
                return new TsHoverResult
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
                return new TsHoverResult
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
            var handler = await GetOrCreateServerHandlerAsync(
                workspace.WorkspaceId, 
                workspace.ProjectPath, 
                cancellationToken);
                
            if (handler == null)
            {
                return new TsHoverResult
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

            // Convert 0-based to 1-based for TSP
            var tspLine = parameters.Line + 1;
            var tspOffset = parameters.Character + 1;

            // Get quick info from TypeScript server
            var quickInfoResponse = await handler.GetQuickInfoAsync(
                normalizedPath, 
                tspLine, 
                tspOffset, 
                cancellationToken);

            _logger.LogInformation("QuickInfo response received: {Response}", quickInfoResponse?.ToString() ?? "null");

            if (quickInfoResponse == null || !quickInfoResponse.HasValue)
            {
                return new TsHoverResult
                {
                    Success = true,
                    Message = "No hover information available at this position",
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character }
                    },
                    HoverInfo = null,
                    SymbolDetails = null,
                    Insights = new List<string>
                    {
                        "The position might not be on a symbol",
                        "The file might have syntax errors preventing analysis"
                    }
                };
            }

            // Parse the quick info response
            var (hoverInfo, symbolDetails) = ParseQuickInfoResponse(quickInfoResponse.Value);

            if (hoverInfo == null)
            {
                return new TsHoverResult
                {
                    Success = true,
                    Message = "Limited hover information available",
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character }
                    },
                    HoverInfo = null,
                    SymbolDetails = null
                };
            }

            var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            return new TsHoverResult
            {
                Success = true,
                Message = $"Hover information for '{symbolDetails?.Name ?? "symbol"}'",
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character }
                },
                HoverInfo = hoverInfo,
                SymbolDetails = symbolDetails,
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{executionTime:F2}ms"
                },
                Insights = GenerateInsights(hoverInfo, symbolDetails),
                Actions = GenerateActions(symbolDetails)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get TypeScript hover information");
            return new TsHoverResult
            {
                Success = false,
                Message = $"Error getting hover information: {ex.Message}",
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

    private (TsHoverInfo? hoverInfo, TsSymbolDetails? symbolDetails) ParseQuickInfoResponse(JsonElement response)
    {
        try
        {
            var hoverInfo = new TsHoverInfo();
            var symbolDetails = new TsSymbolDetails
            {
                Name = "unknown",
                Kind = "unknown"
            };

            // Parse display string (signature)
            if (response.TryGetProperty("displayString", out var displayString))
            {
                hoverInfo.DisplayString = displayString.GetString();
                
                // Extract symbol name and kind from display string
                ParseDisplayString(hoverInfo.DisplayString, symbolDetails);
            }

            // Parse documentation
            if (response.TryGetProperty("documentation", out var documentation))
            {
                hoverInfo.Documentation = documentation.GetString();
            }

            // Parse JSDoc tags
            if (response.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                hoverInfo.Tags = new List<TsJsDocTag>();
                foreach (var tag in tags.EnumerateArray())
                {
                    var jsDocTag = ParseJsDocTag(tag);
                    if (jsDocTag != null)
                    {
                        hoverInfo.Tags.Add(jsDocTag);
                    }
                }
            }

            // Parse kind string
            if (response.TryGetProperty("kind", out var kind))
            {
                symbolDetails.Kind = kind.GetString() ?? "unknown";
            }

            // Parse kind modifiers (e.g., "public", "static", "readonly")
            if (response.TryGetProperty("kindModifiers", out var kindModifiers))
            {
                var modifiers = kindModifiers.GetString();
                if (!string.IsNullOrEmpty(modifiers))
                {
                    symbolDetails.Modifiers = modifiers.Split(',')
                        .Select(m => m.Trim())
                        .Where(m => !string.IsNullOrEmpty(m))
                        .ToList();
                }
            }

            // Extract type information
            if (!string.IsNullOrEmpty(hoverInfo.DisplayString))
            {
                hoverInfo.TypeString = ExtractTypeFromDisplayString(hoverInfo.DisplayString);
                symbolDetails.Type = hoverInfo.TypeString;
            }

            return (hoverInfo, symbolDetails);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse quick info response");
            return (null, null);
        }
    }

    private void ParseDisplayString(string? displayString, TsSymbolDetails symbolDetails)
    {
        if (string.IsNullOrEmpty(displayString))
            return;

        // Display string formats:
        // "(property) SymbolName: type"
        // "(method) SymbolName(params): returnType"
        // "(class) ClassName"
        // "(interface) InterfaceName"
        // "function functionName(params): returnType"
        // "const variableName: type"

        var match = System.Text.RegularExpressions.Regex.Match(
            displayString,
            @"^\(?([\w\s]+)\)?\s+([a-zA-Z_$][\w$]*)"
        );

        if (match.Success)
        {
            var kindPart = match.Groups[1].Value.Trim('(', ')', ' ');
            var namePart = match.Groups[2].Value;

            if (!string.IsNullOrEmpty(kindPart))
            {
                symbolDetails.Kind = kindPart;
            }

            if (!string.IsNullOrEmpty(namePart))
            {
                symbolDetails.Name = namePart;
            }
        }
        else
        {
            // Try simpler parsing for const, let, var, function
            var parts = displayString.Split(new[] { ' ', ':', '(' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                symbolDetails.Kind = parts[0];
                symbolDetails.Name = parts[1];
            }
        }
    }

    private string? ExtractTypeFromDisplayString(string displayString)
    {
        // Extract type after colon
        var colonIndex = displayString.LastIndexOf(':');
        if (colonIndex > 0 && colonIndex < displayString.Length - 1)
        {
            return displayString.Substring(colonIndex + 1).Trim();
        }

        // For functions, extract return type after =>
        var arrowIndex = displayString.LastIndexOf("=>");
        if (arrowIndex > 0 && arrowIndex < displayString.Length - 2)
        {
            return displayString.Substring(arrowIndex + 2).Trim();
        }

        return null;
    }

    private TsJsDocTag? ParseJsDocTag(JsonElement tagElement)
    {
        try
        {
            var tag = new TsJsDocTag
            {
                Name = tagElement.TryGetProperty("name", out var name) ? name.GetString() ?? "" : ""
            };

            if (tagElement.TryGetProperty("text", out var text))
            {
                tag.Text = text.GetString();
            }

            return tag;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse JSDoc tag");
            return null;
        }
    }

    private List<string> GenerateInsights(TsHoverInfo? hoverInfo, TsSymbolDetails? symbolDetails)
    {
        var insights = new List<string>();

        if (symbolDetails != null)
        {
            insights.Add($"Symbol kind: {symbolDetails.Kind}");

            if (symbolDetails.Modifiers?.Count > 0)
            {
                insights.Add($"Modifiers: {string.Join(", ", symbolDetails.Modifiers)}");
            }

            if (!string.IsNullOrEmpty(symbolDetails.Type))
            {
                // Analyze type complexity
                if (symbolDetails.Type.Contains("=>") || symbolDetails.Type.Contains("("))
                {
                    insights.Add("Function type signature");
                }
                else if (symbolDetails.Type.Contains("|"))
                {
                    insights.Add("Union type");
                }
                else if (symbolDetails.Type.Contains("&"))
                {
                    insights.Add("Intersection type");
                }
                else if (symbolDetails.Type.Contains("<") && symbolDetails.Type.Contains(">"))
                {
                    insights.Add("Generic type");
                }
                else if (symbolDetails.Type == "any")
                {
                    insights.Add("Type is 'any' - consider using a more specific type");
                }
                else if (symbolDetails.Type == "unknown")
                {
                    insights.Add("Type is 'unknown' - requires type narrowing before use");
                }
            }
        }

        if (hoverInfo != null)
        {
            if (!string.IsNullOrEmpty(hoverInfo.Documentation))
            {
                insights.Add("Documentation available");
            }

            if (hoverInfo.Tags?.Count > 0)
            {
                var tagTypes = hoverInfo.Tags.Select(t => t.Name).Distinct().ToList();
                if (tagTypes.Count > 0)
                {
                    insights.Add($"JSDoc tags: {string.Join(", ", tagTypes)}");
                }

                // Check for specific important tags
                if (hoverInfo.Tags.Any(t => t.Name == "deprecated"))
                {
                    insights.Add("⚠️ This symbol is deprecated");
                }
                if (hoverInfo.Tags.Any(t => t.Name == "experimental"))
                {
                    insights.Add("🧪 This symbol is experimental");
                }
            }
        }

        return insights;
    }

    private List<AIAction> GenerateActions(TsSymbolDetails? symbolDetails)
    {
        var actions = new List<AIAction>();

        if (symbolDetails != null)
        {
            actions.Add(new AIAction
            {
                Action = ToolNames.TsGoToDefinition,
                Description = $"Go to definition of '{symbolDetails.Name}'",
                Category = "navigate"
            });

            actions.Add(new AIAction
            {
                Action = ToolNames.TsFindAllReferences,
                Description = $"Find all references to '{symbolDetails.Name}'",
                Category = "analyze"
            });

            if (symbolDetails.Kind == "class" || symbolDetails.Kind == "interface")
            {
                actions.Add(new AIAction
                {
                    Action = ToolNames.TsFindImplementations,
                    Description = $"Find implementations of '{symbolDetails.Name}'",
                    Category = "analyze"
                });
            }

            if (symbolDetails.Type == "any")
            {
                actions.Add(new AIAction
                {
                    Action = "improve_type",
                    Description = "Replace 'any' with a more specific type",
                    Category = "refactor"
                });
            }

            actions.Add(new AIAction
            {
                Action = ToolNames.TsRenameSymbol,
                Description = $"Rename '{symbolDetails.Name}'",
                Category = "refactor"
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
/// Parameters for TypeScript hover
/// </summary>
public class TsHoverParams
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