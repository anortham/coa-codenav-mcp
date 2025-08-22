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
/// MCP tool that finds all references to a TypeScript symbol using TSP
/// </summary>
public class TsFindAllReferencesTool : McpToolBase<TsFindAllReferencesParams, TsFindAllReferencesResult>, IDisposable
{
    private readonly ILogger<TsFindAllReferencesTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly Dictionary<string, TsServerProtocolHandler> _serverHandlers = new();
    private readonly SemaphoreSlim _serverLock = new(1, 1);
    private const int DEFAULT_MAX_RESULTS = 500;
    private const int TOKEN_SAFETY_LIMIT = 10000;

    public override string Name => ToolNames.TsFindAllReferences;
    
    public override string Description => @"**BEFORE CHANGING ANY TYPESCRIPT INTERFACE OR PUBLIC FUNCTION** - find every place it's used to prevent breaking changes. This is your safety net against accidentally breaking the TypeScript codebase.

**CRITICAL CHANGE PROTECTION:**
- About to modify an interface property? STOP - Find all uses first
- Changing a function signature or parameter types? STOP - See who depends on it  
- Refactoring or renaming TypeScript code? STOP - Check impact across the project
- User wants to 'change' or 'update' any TypeScript? Find references first

**Essential before:**
- Changing interface properties, method signatures, or type definitions
- Modifying exported functions, classes, or types
- Deleting or moving any exported TypeScript members
- Any refactoring that affects public APIs or shared types

**What you'll prevent:**
- Breaking dozens of function calls across TypeScript files
- Type errors in unexpected places throughout the project  
- Components failing due to changed interface properties
- Other developers' code breaking when they pull changes

**Impact assessment:**
- See exactly how many places will break with your changes
- Understand which files and components are affected
- Plan changes to minimize disruption across the codebase
- Decide if the change is worth the widespread impact

**The TypeScript safety rule:** If it's exported and you're changing it, find references first. No exceptions.

**Use cases:** Impact analysis, refactoring preparation, understanding TypeScript dependencies, change planning.

Prerequisites: Call ts_load_tsconfig first to load the TypeScript project.
See also: ts_rename_symbol for safe renaming, ts_goto_definition for understanding the target.";

    public TsFindAllReferencesTool(
        ILogger<TsFindAllReferencesTool> logger,
        TypeScriptWorkspaceService workspaceService,
        TypeScriptCompilerManager compilerManager)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _compilerManager = compilerManager;
    }

    protected override async Task<TsFindAllReferencesResult> ExecuteInternalAsync(
        TsFindAllReferencesParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogDebug("TsFindAllReferences request: File={File}, Line={Line}, Character={Character}",
            parameters.FilePath, parameters.Line, parameters.Character);

        try
        {
            // Validate TypeScript availability
            var tsError = _compilerManager.ValidateTypeScriptAvailability();
            if (tsError != null)
            {
                return CreateErrorResult("TypeScript is not available", tsError);
            }

            // Find workspace for file
            var workspace = _workspaceService.FindWorkspaceForFile(parameters.FilePath);
            if (workspace == null)
            {
                return CreateWorkspaceNotLoadedResult(parameters.FilePath);
            }

            // Get or create server handler
            var handler = await GetOrCreateServerHandlerAsync(
                workspace.WorkspaceId, 
                workspace.ProjectPath, 
                cancellationToken);
                
            if (handler == null)
            {
                return CreateErrorResult("Failed to start TypeScript language server", 
                    new ErrorInfo { Code = ErrorCodes.INTERNAL_ERROR, Message = "Could not initialize TypeScript language server" });
            }

            // Normalize file path
            var normalizedPath = Path.GetFullPath(parameters.FilePath).Replace('\\', '/');

            // Convert 0-based to 1-based for TSP
            var tspLine = parameters.Line + 1;
            var tspOffset = parameters.Character + 1;

            // Get references from TypeScript server
            var referencesResponse = await handler.GetReferencesAsync(
                normalizedPath, 
                tspLine, 
                tspOffset, 
                cancellationToken);

            _logger.LogInformation("References response received: {Response}", referencesResponse?.ToString() ?? "null");

            if (referencesResponse == null)
            {
                return CreateNoReferencesFoundResult(parameters, startTime);
            }

            // Parse the response
            var references = ParseReferencesResponse(referencesResponse.Value);
            
            // Apply max results limit
            var totalReferences = references.Count;
            var truncated = false;
            
            if (parameters.MaxResults > 0 && references.Count > parameters.MaxResults)
            {
                references = references.Take(parameters.MaxResults).ToList();
                truncated = true;
            }

            // Group references by file for distribution analysis
            var distribution = AnalyzeReferenceDistribution(references);

            // Get symbol information from quick info
            var quickInfoResponse = await handler.GetQuickInfoAsync(
                normalizedPath,
                tspLine,
                tspOffset,
                cancellationToken);

            var symbolName = ExtractSymbolName(quickInfoResponse);

            var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            return new TsFindAllReferencesResult
            {
                Success = true,
                Message = $"Found {totalReferences} reference(s) to '{symbolName ?? "symbol"}'",
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character }
                },
                Summary = new SummaryInfo
                {
                    TotalFound = totalReferences,
                    Returned = references.Count,
                    ExecutionTime = $"{executionTime:F2}ms"
                },
                Locations = references,
                ResultsSummary = new ResultsSummary
                {
                    Total = totalReferences,
                    Included = references.Count,
                    HasMore = truncated
                },
                Distribution = distribution,
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{executionTime:F2}ms"
                },
                Insights = GenerateInsights(references, symbolName, truncated),
                Actions = GenerateActions(references, symbolName)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find TypeScript references");
            return CreateErrorResult($"Error finding references: {ex.Message}", 
                new ErrorInfo { Code = ErrorCodes.INTERNAL_ERROR, Message = ex.Message });
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

    private List<ReferenceLocation> ParseReferencesResponse(JsonElement response)
    {
        var references = new List<ReferenceLocation>();

        if (!response.TryGetProperty("refs", out var refsArray) || refsArray.ValueKind != JsonValueKind.Array)
        {
            _logger.LogWarning("Unexpected references response format");
            return references;
        }

        foreach (var reference in refsArray.EnumerateArray())
        {
            try
            {
                if (!reference.TryGetProperty("file", out var fileProp))
                    continue;

                var filePath = fileProp.GetString();
                if (string.IsNullOrEmpty(filePath))
                    continue;

                var location = new ReferenceLocation
                {
                    FilePath = filePath
                };

                // Parse start position
                if (reference.TryGetProperty("start", out var start))
                {
                    location.Line = start.TryGetProperty("line", out var line) ? line.GetInt32() - 1 : 0;
                    location.Column = start.TryGetProperty("offset", out var offset) ? offset.GetInt32() - 1 : 0;
                }

                // Parse end position
                if (reference.TryGetProperty("end", out var end))
                {
                    location.EndLine = end.TryGetProperty("line", out var endLine) ? endLine.GetInt32() - 1 : location.Line;
                    location.EndColumn = end.TryGetProperty("offset", out var endOffset) ? endOffset.GetInt32() - 1 : location.Column;
                }

                // Determine reference kind
                if (reference.TryGetProperty("isWriteAccess", out var isWrite) && isWrite.GetBoolean())
                {
                    location.Kind = "Write";
                }
                else if (reference.TryGetProperty("isDefinition", out var isDef) && isDef.GetBoolean())
                {
                    location.Kind = "Definition";
                }
                else
                {
                    location.Kind = "Read";
                }

                // Get line text for context if available
                if (reference.TryGetProperty("lineText", out var lineTextProp))
                {
                    var lineText = lineTextProp.GetString() ?? "";
                    // Could create preview here if ReferenceLocation had Preview property
                }

                references.Add(location);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse reference location");
            }
        }

        // Sort references by file and line
        return references
            .OrderBy(r => r.FilePath)
            .ThenBy(r => r.Line)
            .ThenBy(r => r.Column)
            .ToList();
    }

    private string CreatePreviewWithHighlight(string lineText, int startCol, int endCol)
    {
        if (string.IsNullOrEmpty(lineText))
            return "";

        // Trim the line and adjust positions
        var trimmedLine = lineText.Trim();
        var trimOffset = lineText.Length - lineText.TrimStart().Length;
        
        var adjustedStart = Math.Max(0, startCol - trimOffset);
        var adjustedEnd = Math.Min(trimmedLine.Length, endCol - trimOffset);

        if (adjustedStart >= trimmedLine.Length || adjustedEnd <= adjustedStart)
            return trimmedLine;

        // Add highlighting markers
        var before = trimmedLine.Substring(0, adjustedStart);
        var highlighted = trimmedLine.Substring(adjustedStart, adjustedEnd - adjustedStart);
        var after = adjustedEnd < trimmedLine.Length ? trimmedLine.Substring(adjustedEnd) : "";

        return $"{before}[{highlighted}]{after}";
    }

    private ReferenceDistribution AnalyzeReferenceDistribution(List<ReferenceLocation> references)
    {
        var distribution = new ReferenceDistribution
        {
            ByFile = new Dictionary<string, int>(),
            ByKind = new Dictionary<string, int>()
        };

        foreach (var reference in references)
        {
            // By file
            var fileName = Path.GetFileName(reference.FilePath);
            distribution.ByFile[fileName] = distribution.ByFile.GetValueOrDefault(fileName, 0) + 1;

            // By kind
            var kind = reference.Kind ?? "Unknown";
            distribution.ByKind[kind] = distribution.ByKind.GetValueOrDefault(kind, 0) + 1;

            // By module (directory)
            var directory = Path.GetDirectoryName(reference.FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                var moduleName = Path.GetFileName(directory);
                // Store by file instead of module
                distribution.ByFile[reference.FilePath] = distribution.ByFile.GetValueOrDefault(reference.FilePath, 0) + 1;
            }
        }

        return distribution;
    }

    private string? ExtractSymbolName(JsonElement? quickInfoResponse)
    {
        if (quickInfoResponse == null || !quickInfoResponse.HasValue)
            return null;

        try
        {
            if (quickInfoResponse.Value.TryGetProperty("displayString", out var displayString))
            {
                var display = displayString.GetString();
                if (!string.IsNullOrEmpty(display))
                {
                    // Extract the symbol name from the display string
                    // Display string format is usually like: "(property) SymbolName: type"
                    var parts = display.Split(' ', ':');
                    if (parts.Length > 1)
                    {
                        // Find the part that looks like a symbol name
                        foreach (var part in parts)
                        {
                            var cleaned = part.Trim('(', ')', '[', ']');
                            if (!string.IsNullOrEmpty(cleaned) && 
                                !cleaned.Equals("property", StringComparison.OrdinalIgnoreCase) &&
                                !cleaned.Equals("method", StringComparison.OrdinalIgnoreCase) &&
                                !cleaned.Equals("function", StringComparison.OrdinalIgnoreCase) &&
                                !cleaned.Equals("class", StringComparison.OrdinalIgnoreCase) &&
                                !cleaned.Equals("interface", StringComparison.OrdinalIgnoreCase))
                            {
                                return cleaned;
                            }
                        }
                    }
                    return display;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to extract symbol name from quick info");
        }

        return null;
    }

    private List<string> GenerateInsights(List<ReferenceLocation> references, string? symbolName, bool truncated)
    {
        var insights = new List<string>();

        if (references.Count == 0)
        {
            insights.Add("No references found - symbol might be unused");
            return insights;
        }

        // Basic statistics
        var fileCount = references.Select(r => r.FilePath).Distinct().Count();
        insights.Add($"Found in {fileCount} file{(fileCount != 1 ? "s" : "")}");

        // Reference types
        var writes = references.Count(r => r.Kind == "Write");
        var reads = references.Count(r => r.Kind == "Read");
        var definitions = references.Count(r => r.Kind == "Definition");

        if (writes > 0)
            insights.Add($"{writes} write access{(writes != 1 ? "es" : "")}");
        if (reads > 0)
            insights.Add($"{reads} read access{(reads != 1 ? "es" : "")}");
        if (definitions > 0)
            insights.Add($"{definitions} definition{(definitions != 1 ? "s" : "")}");

        // Usage patterns
        if (writes == 0 && reads > 0)
        {
            insights.Add("Symbol is read-only (never modified after initialization)");
        }
        else if (writes > reads)
        {
            insights.Add("Symbol is written more than read - might indicate unusual usage");
        }

        // File concentration
        if (fileCount == 1)
        {
            insights.Add("All references in a single file - consider if symbol should be private");
        }
        else if (fileCount > 10)
        {
            insights.Add("Widely used across the codebase - changes will have broad impact");
        }

        if (truncated)
        {
            insights.Add("Results truncated to limit response size");
        }

        return insights;
    }

    private List<AIAction> GenerateActions(List<ReferenceLocation> references, string? symbolName)
    {
        var actions = new List<AIAction>();

        if (references.Count > 0)
        {
            actions.Add(new AIAction
            {
                Action = ToolNames.TsRenameSymbol,
                Description = $"Rename '{symbolName ?? "symbol"}' across all references",
                Category = "refactor"
            });

            if (references.Count == 1)
            {
                actions.Add(new AIAction
                {
                    Action = "remove_unused",
                    Description = "Symbol appears unused - consider removing",
                    Category = "cleanup"
                });
            }

            actions.Add(new AIAction
            {
                Action = ToolNames.TsGoToDefinition,
                Description = "Navigate to symbol definition",
                Category = "navigate"
            });

            var hasWrites = references.Any(r => r.Kind == "Write");
            if (hasWrites)
            {
                actions.Add(new AIAction
                {
                    Action = "analyze_mutations",
                    Description = "Analyze where this symbol is modified",
                    Category = "analyze"
                });
            }
        }

        return actions;
    }

    private TsFindAllReferencesResult CreateErrorResult(string message, ErrorInfo error)
    {
        return new TsFindAllReferencesResult
        {
            Success = false,
            Message = message,
            Error = error
        };
    }

    private TsFindAllReferencesResult CreateWorkspaceNotLoadedResult(string filePath)
    {
        return new TsFindAllReferencesResult
        {
            Success = false,
            Message = $"No TypeScript project loaded for file: {filePath}",
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

    private TsFindAllReferencesResult CreateNoReferencesFoundResult(TsFindAllReferencesParams parameters, DateTime startTime)
    {
        return new TsFindAllReferencesResult
        {
            Success = true,
            Message = "No references found",
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character }
            },
            Locations = new List<ReferenceLocation>(),
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            },
            Insights = new List<string>
            {
                "No references found for the symbol at this position",
                "The symbol might be unused or the position might not be on a referenceable symbol"
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
/// Parameters for finding TypeScript references
/// </summary>
public class TsFindAllReferencesParams
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
    [Range(1, 1000)]
    public int MaxResults { get; set; } = 500;
}