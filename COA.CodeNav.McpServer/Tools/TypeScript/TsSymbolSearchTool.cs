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
/// MCP tool that provides symbol search functionality for TypeScript using TSP navto command
/// </summary>
public class TsSymbolSearchTool : McpToolBase<TsSymbolSearchParams, TsSymbolSearchResult>, IDisposable
{
    private readonly ILogger<TsSymbolSearchTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly Dictionary<string, TsServerProtocolHandler> _serverHandlers = new();
    private readonly SemaphoreSlim _serverLock = new(1, 1);

    public override string Name => ToolNames.TsSymbolSearch;
    
    public override string Description => @"**FIND TYPES BEFORE USING THEM** - When the user mentions any TypeScript type, interface, class, or function name, search for it FIRST to verify it exists and get the exact name.

**CRITICAL TYPE DISCOVERY WORKFLOW:**
1. User mentions 'use UserProfile' or any type name → Search for it immediately
2. Find exact matches → Note precise naming (UserProfile vs userProfile)
3. See where it's defined → Navigate to the source for full details
4. Then use the verified type → No more 'Cannot find name' errors

**Prevents common TypeScript frustrations:**
- Implementing non-existent types or interfaces
- Wrong capitalization (UserService vs userService)
- Using types from wrong modules or files
- Guessing at generic type parameters

**Essential before coding when:**
- User mentions any type name you haven't seen
- You get 'Cannot find name' TypeScript errors  
- Need to import types but don't know exact names
- Working with large codebases with many similar types

**Search strategies:**
- Partial names: 'User' finds UserProfile, UserService, etc.
- Patterns: 'Service' finds all service classes
- Exact matches: 'AuthProvider' finds the precise type

**The discovery rule:** If you don't know exactly where a TypeScript type is defined, search for it. No assumptions.

Prerequisites: Call ts_load_tsconfig first to load the TypeScript project.
Follow up: Use ts_goto_definition to see full type definition, ts_hover for quick signature check.";

    public TsSymbolSearchTool(
        ILogger<TsSymbolSearchTool> logger,
        TypeScriptWorkspaceService workspaceService,
        TypeScriptCompilerManager compilerManager)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _compilerManager = compilerManager;
    }

    protected override async Task<TsSymbolSearchResult> ExecuteInternalAsync(
        TsSymbolSearchParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogDebug("TsSymbolSearch request: Query={Query}, MaxResults={MaxResults}",
            parameters.Query, parameters.MaxResults);

        try
        {
            // Validate TypeScript availability
            var tsError = _compilerManager.ValidateTypeScriptAvailability();
            if (tsError != null)
            {
                return new TsSymbolSearchResult
                {
                    Success = false,
                    Message = "TypeScript is not available",
                    Error = tsError
                };
            }

            // Get the appropriate workspace
            TypeScriptWorkspaceInfo? workspace = null;
            
            if (!string.IsNullOrEmpty(parameters.WorkspaceId))
            {
                workspace = _workspaceService.GetWorkspace(parameters.WorkspaceId);
            }
            else if (!string.IsNullOrEmpty(parameters.FilePath))
            {
                workspace = _workspaceService.FindWorkspaceForFile(parameters.FilePath);
            }
            else
            {
                // Get the first available workspace - we don't have GetAllWorkspaces method
                // workspace = null; // Already null
            }

            if (workspace == null)
            {
                _logger.LogWarning("No TypeScript workspace found");
                return new TsSymbolSearchResult
                {
                    Success = false,
                    Message = "No TypeScript project loaded",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.WORKSPACE_NOT_LOADED,
                        Message = "No TypeScript project is currently loaded",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new[]
                            {
                                "Load a TypeScript project first",
                                "Use ts_load_tsconfig to load the project's tsconfig.json"
                            },
                            SuggestedActions = new List<SuggestedAction>
                            {
                                new()
                                {
                                    Tool = ToolNames.TsLoadTsConfig,
                                    Description = "Load a TypeScript project",
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
                return new TsSymbolSearchResult
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

            // Perform the symbol search using navto command
            var navToResponse = await handler.GetNavToAsync(
                parameters.Query,
                parameters.FilePath,
                parameters.MaxResults,
                cancellationToken);

            if (navToResponse == null)
            {
                return new TsSymbolSearchResult
                {
                    Success = false,
                    Message = "Failed to search for symbols",
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath
                    }
                };
            }

            // Parse the navigation results
            var symbols = ParseNavToResponse(navToResponse.Value, parameters.MaxResults);
            
            // Filter by symbol kinds if specified
            if (parameters.SymbolKinds != null && parameters.SymbolKinds.Any())
            {
                var kindsSet = new HashSet<string>(parameters.SymbolKinds, StringComparer.OrdinalIgnoreCase);
                symbols = symbols.Where(s => kindsSet.Contains(s.Kind)).ToList();
            }

            var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            return new TsSymbolSearchResult
            {
                Success = true,
                Message = $"Found {symbols.Count} matching symbols",
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
                Insights = GenerateInsights(symbols, parameters.Query),
                Actions = GenerateActions(symbols)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search TypeScript symbols");
            return new TsSymbolSearchResult
            {
                Success = false,
                Message = $"Error searching symbols: {ex.Message}",
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

    private List<SymbolSearchItem> ParseNavToResponse(JsonElement response, int maxResults)
    {
        var symbols = new List<SymbolSearchItem>();

        if (response.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Unexpected navto response format");
            return symbols;
        }

        foreach (var item in response.EnumerateArray())
        {
            if (symbols.Count >= maxResults)
                break;

            try
            {
                var symbol = new SymbolSearchItem
                {
                    Name = item.GetProperty("name").GetString() ?? "",
                    Kind = MapKindToSymbolKind(item.GetProperty("kind").GetString()),
                    FilePath = item.GetProperty("file").GetString() ?? "",
                    ContainerName = item.TryGetProperty("containerName", out var container) 
                        ? container.GetString() : null
                };

                // Parse location
                if (item.TryGetProperty("start", out var start))
                {
                    symbol.Location = new LocationInfo
                    {
                        FilePath = symbol.FilePath,
                        Line = start.GetProperty("line").GetInt32() - 1, // Convert to 0-based
                        Column = start.GetProperty("offset").GetInt32() - 1
                    };

                    if (item.TryGetProperty("end", out var end))
                    {
                        symbol.Location.EndLine = end.GetProperty("line").GetInt32() - 1;
                        symbol.Location.EndColumn = end.GetProperty("offset").GetInt32() - 1;
                    }
                }

                // Parse match kind (exact, prefix, substring, pattern)
                if (item.TryGetProperty("matchKind", out var matchKind))
                {
                    symbol.MatchKind = matchKind.GetString();
                }

                // Parse container kind if available
                if (item.TryGetProperty("containerKind", out var containerKind))
                {
                    symbol.ContainerKind = MapKindToSymbolKind(containerKind.GetString());
                }

                // Parse modifiers if present
                if (item.TryGetProperty("kindModifiers", out var modifiers))
                {
                    var modifierString = modifiers.GetString();
                    if (!string.IsNullOrEmpty(modifierString))
                    {
                        symbol.Modifiers = modifierString.Split(',').ToList();
                    }
                }

                symbols.Add(symbol);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse navto result item");
            }
        }

        return symbols;
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
            "getter" => "Property",
            "setter" => "Property",
            "call" => "Function",
            "index" => "Property",
            "construct" => "Constructor",
            "parameter" => "Parameter",
            "type parameter" => "TypeParameter",
            "primitive type" => "Type",
            "label" => "Label",
            "script" => "Script",
            "directory" => "Directory",
            "external module name" => "Module",
            _ => "Unknown"
        };
    }

    private List<string> GenerateInsights(List<SymbolSearchItem> symbols, string query)
    {
        var insights = new List<string>();

        if (symbols.Count > 0)
        {
            insights.Add($"Found {symbols.Count} symbols matching '{query}'");

            // Group by symbol kind
            var symbolsByKind = symbols.GroupBy(s => s.Kind)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var kvp in symbolsByKind.Take(3))
            {
                insights.Add($"{kvp.Value} {kvp.Key}(s)");
            }

            // Group by file
            var fileCount = symbols.Select(s => s.FilePath).Distinct().Count();
            if (fileCount > 1)
            {
                insights.Add($"Symbols found across {fileCount} files");
            }

            // Check match quality
            var exactMatches = symbols.Where(s => s.MatchKind == "exact").Count();
            if (exactMatches > 0)
            {
                insights.Add($"{exactMatches} exact match(es)");
            }

            // Check for exported symbols
            var exportedSymbols = symbols.Where(s => s.Modifiers != null && s.Modifiers.Contains("export")).Count();
            if (exportedSymbols > 0)
            {
                insights.Add($"{exportedSymbols} exported symbol(s)");
            }
        }
        else
        {
            insights.Add($"No symbols found matching '{query}'");
            insights.Add("Try using wildcards or partial matches");
            insights.Add("Ensure TypeScript project is fully loaded");
        }

        return insights;
    }

    private List<AIAction> GenerateActions(List<SymbolSearchItem> symbols)
    {
        var actions = new List<AIAction>();

        if (symbols.Count > 0)
        {
            var firstSymbol = symbols.First();
            
            actions.Add(new AIAction
            {
                Action = ToolNames.TsGoToDefinition,
                Description = $"Navigate to {firstSymbol.Name} definition",
                Category = "navigate"
            });

            actions.Add(new AIAction
            {
                Action = ToolNames.TsFindAllReferences,
                Description = $"Find all references to {firstSymbol.Name}",
                Category = "analyze"
            });

            if (symbols.Any(s => s.Kind == "Interface"))
            {
                actions.Add(new AIAction
                {
                    Action = ToolNames.TsFindImplementations,
                    Description = "Find implementations of interfaces",
                    Category = "analyze"
                });
            }

            actions.Add(new AIAction
            {
                Action = ToolNames.TsDocumentSymbols,
                Description = "View document structure for files containing matches",
                Category = "navigate"
            });
        }
        else
        {
            actions.Add(new AIAction
            {
                Action = "refine_search",
                Description = "Try different search patterns or keywords",
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
/// Parameters for TypeScript Symbol Search
/// </summary>
public class TsSymbolSearchParams
{
    [Required]
    [JsonPropertyName("query")]
    public required string Query { get; set; }

    [JsonPropertyName("maxResults")]
    [Range(1, 500)]
    public int MaxResults { get; set; } = 50;

    [JsonPropertyName("symbolKinds")]
    public List<string>? SymbolKinds { get; set; }

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }
}