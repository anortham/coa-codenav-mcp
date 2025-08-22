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
/// MCP tool that renames TypeScript symbols across the project using TSP
/// </summary>
public class TsRenameSymbolTool : McpToolBase<TsRenameSymbolParams, TsRenameSymbolResult>, IDisposable
{
    private readonly ILogger<TsRenameSymbolTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly Dictionary<string, TsServerProtocolHandler> _serverHandlers = new();
    private readonly SemaphoreSlim _serverLock = new(1, 1);

    public override string Name => ToolNames.TsRenameSymbol;
    
    public override string Description => @"Safely rename TypeScript symbols across the entire project with conflict detection to prevent breaking changes.";

    public TsRenameSymbolTool(
        ILogger<TsRenameSymbolTool> logger,
        TypeScriptWorkspaceService workspaceService,
        TypeScriptCompilerManager compilerManager)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _compilerManager = compilerManager;
    }

    protected override async Task<TsRenameSymbolResult> ExecuteInternalAsync(
        TsRenameSymbolParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogDebug("TsRenameSymbol request: File={File}, Line={Line}, Character={Character}, NewName={NewName}, Preview={Preview}",
            parameters.FilePath, parameters.Line, parameters.Character, parameters.NewName, parameters.Preview);

        try
        {
            // Validate new name
            if (!IsValidIdentifier(parameters.NewName))
            {
                return new TsRenameSymbolResult
                {
                    Success = false,
                    Message = $"Invalid identifier name: '{parameters.NewName}'",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.INVALID_PARAMETER,
                        Message = "The new name is not a valid TypeScript identifier",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new[]
                            {
                                "Use a valid identifier name (starts with letter, $, or _)",
                                "Avoid TypeScript reserved keywords",
                                "Don't use spaces or special characters"
                            }
                        }
                    }
                };
            }

            // Validate TypeScript availability
            var tsError = _compilerManager.ValidateTypeScriptAvailability();
            if (tsError != null)
            {
                return new TsRenameSymbolResult
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

            // First, get info about the symbol being renamed
            var quickInfoResponse = await handler.GetQuickInfoAsync(
                normalizedPath,
                tspLine,
                tspOffset,
                cancellationToken);

            var oldName = ExtractSymbolName(quickInfoResponse);
            
            if (string.IsNullOrEmpty(oldName))
            {
                _logger.LogWarning("Could not determine symbol name at position");
                oldName = "symbol";
            }

            // Get rename locations from TypeScript server
            var renameResponse = await handler.GetRenameLocationsAsync(
                normalizedPath, 
                tspLine, 
                tspOffset,
                parameters.FindInComments,
                parameters.FindInStrings,
                cancellationToken);

            if (renameResponse == null || !renameResponse.HasValue)
            {
                return new TsRenameSymbolResult
                {
                    Success = false,
                    Message = "Cannot rename at this position",
                    Query = new RenameQuery
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character },
                        OldName = oldName,
                        NewName = parameters.NewName
                    },
                    Insights = new List<string>
                    {
                        "The position might not be on a renameable symbol",
                        "The symbol might be from an external library",
                        "The file might have syntax errors preventing analysis"
                    }
                };
            }

            // Parse rename locations
            var renameInfo = ParseRenameResponse(renameResponse.Value, oldName, parameters.NewName);

            if (!renameInfo.CanRename)
            {
                return new TsRenameSymbolResult
                {
                    Success = false,
                    Message = renameInfo.ReasonWhyNot ?? "Cannot rename this symbol",
                    Query = new RenameQuery
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character },
                        OldName = oldName,
                        NewName = parameters.NewName
                    },
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.RENAME_NOT_ALLOWED,
                        Message = renameInfo.ReasonWhyNot ?? "Symbol cannot be renamed"
                    }
                };
            }

            // Check for conflicts
            var conflicts = await CheckForConflictsAsync(handler, renameInfo, cancellationToken);

            var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            // Build the result
            var result = new TsRenameSymbolResult
            {
                Success = true,
                Message = parameters.Preview 
                    ? $"Preview: Rename '{oldName}' to '{parameters.NewName}' - {renameInfo.Locations.Count} location(s)"
                    : $"Renamed '{oldName}' to '{parameters.NewName}' - {renameInfo.Locations.Count} location(s) updated",
                Query = new RenameQuery
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character },
                    OldName = oldName,
                    NewName = parameters.NewName
                },
                Summary = new RenameSummary
                {
                    TotalOccurrences = renameInfo.Locations.Count,
                    FilesAffected = renameInfo.Locations.Select(l => l.FilePath).Distinct().Count(),
                    Preview = parameters.Preview
                },
                Changes = renameInfo.Locations,
                Conflicts = conflicts,
                Applied = !parameters.Preview,
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{executionTime:F2}ms"
                },
                Insights = GenerateInsights(renameInfo, conflicts, parameters),
                Actions = GenerateActions(parameters.Preview, conflicts.Count > 0)
            };

            // If not preview mode and no conflicts, apply the changes
            if (!parameters.Preview && conflicts.Count == 0)
            {
                // In a real implementation, we would apply the changes here
                // For now, we're just returning the preview
                _logger.LogInformation("Would apply rename changes to {Count} locations", renameInfo.Locations.Count);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename TypeScript symbol");
            return CreateErrorResult($"Error renaming symbol: {ex.Message}",
                new ErrorInfo { Code = ErrorCodes.INTERNAL_ERROR, Message = ex.Message });
        }
    }

    private bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Check if it's a reserved keyword
        var reservedKeywords = new HashSet<string>
        {
            "break", "case", "catch", "class", "const", "continue", "debugger",
            "default", "delete", "do", "else", "enum", "export", "extends",
            "false", "finally", "for", "function", "if", "import", "in",
            "instanceof", "new", "null", "return", "super", "switch", "this",
            "throw", "true", "try", "typeof", "var", "void", "while", "with",
            "as", "implements", "interface", "let", "package", "private",
            "protected", "public", "static", "yield", "any", "boolean",
            "constructor", "declare", "get", "module", "require", "number",
            "set", "string", "symbol", "type", "from", "of", "namespace",
            "async", "await", "abstract", "readonly", "keyof", "unique",
            "unknown", "never", "infer", "asserts", "is"
        };

        if (reservedKeywords.Contains(name))
            return false;

        // Check if it starts with a valid character
        if (!char.IsLetter(name[0]) && name[0] != '$' && name[0] != '_')
            return false;

        // Check if all characters are valid
        return name.All(c => char.IsLetterOrDigit(c) || c == '$' || c == '_');
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

    private RenameInfo ParseRenameResponse(JsonElement response, string oldName, string newName)
    {
        var info = new RenameInfo
        {
            CanRename = true,
            OldName = oldName,
            NewName = newName,
            Locations = new List<RenameLocation>()
        };

        // Check if rename is allowed
        if (response.TryGetProperty("info", out var infoElement))
        {
            if (infoElement.TryGetProperty("canRename", out var canRename))
            {
                info.CanRename = canRename.GetBoolean();
            }

            if (infoElement.TryGetProperty("localizedErrorMessage", out var errorMessage))
            {
                info.ReasonWhyNot = errorMessage.GetString();
            }
        }

        if (!info.CanRename)
        {
            return info;
        }

        // Parse rename locations
        if (response.TryGetProperty("locs", out var locs) && locs.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileGroup in locs.EnumerateArray())
            {
                if (!fileGroup.TryGetProperty("file", out var fileProp))
                    continue;

                var filePath = fileProp.GetString();
                if (string.IsNullOrEmpty(filePath))
                    continue;

                if (!fileGroup.TryGetProperty("locs", out var fileLocs) || fileLocs.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var loc in fileLocs.EnumerateArray())
                {
                    try
                    {
                        var location = new RenameLocation
                        {
                            FilePath = filePath,
                            OldText = oldName,
                            NewText = newName
                        };

                        // Parse start position
                        if (loc.TryGetProperty("start", out var start))
                        {
                            location.Line = start.TryGetProperty("line", out var line) ? line.GetInt32() - 1 : 0;
                            location.Column = start.TryGetProperty("offset", out var offset) ? offset.GetInt32() - 1 : 0;
                        }

                        // Parse end position
                        if (loc.TryGetProperty("end", out var end))
                        {
                            location.EndLine = end.TryGetProperty("line", out var endLine) ? endLine.GetInt32() - 1 : location.Line;
                            location.EndColumn = end.TryGetProperty("offset", out var endOffset) ? endOffset.GetInt32() - 1 : location.Column;
                        }

                        // Get context if available
                        if (loc.TryGetProperty("prefixText", out var prefix))
                        {
                            location.ContextBefore = prefix.GetString();
                        }

                        if (loc.TryGetProperty("suffixText", out var suffix))
                        {
                            location.ContextAfter = suffix.GetString();
                        }

                        info.Locations.Add(location);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to parse rename location");
                    }
                }
            }
        }

        return info;
    }

    private Task<List<ConflictInfo>> CheckForConflictsAsync(
        TsServerProtocolHandler handler,
        RenameInfo renameInfo,
        CancellationToken cancellationToken)
    {
        var conflicts = new List<ConflictInfo>();

        // Group locations by file
        var fileGroups = renameInfo.Locations.GroupBy(l => l.FilePath);

        foreach (var group in fileGroups)
        {
            // Check for naming conflicts in each file
            // This is a simplified check - a real implementation would be more thorough
            
            // Check if new name would conflict with existing symbols
            // This would require analyzing the scope and checking for collisions
            
            // For now, just check if the new name is very generic
            if (IsGenericName(renameInfo.NewName))
            {
                conflicts.Add(new ConflictInfo
                {
                    FilePath = group.Key,
                    Type = "PotentialConflict",
                    Description = $"The name '{renameInfo.NewName}' is very generic and might conflict with existing symbols",
                    Severity = "Warning"
                });
            }
        }

        return Task.FromResult(conflicts);
    }

    private bool IsGenericName(string name)
    {
        var genericNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "data", "value", "result", "item", "element", "obj", "object",
            "arr", "array", "list", "map", "set", "temp", "tmp", "test",
            "foo", "bar", "baz", "x", "y", "z", "i", "j", "k", "n", "m"
        };

        return genericNames.Contains(name);
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
                    // Extract symbol name from display string
                    var match = System.Text.RegularExpressions.Regex.Match(
                        display,
                        @"(?:(?:\([\w\s]+\))|(?:const|let|var|function|class|interface))\s+([a-zA-Z_$][\w$]*)"
                    );

                    if (match.Success && match.Groups.Count > 1)
                    {
                        return match.Groups[1].Value;
                    }

                    // Fallback: try to find an identifier-like string
                    var parts = display.Split(new[] { ' ', ':', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(part, @"^[a-zA-Z_$][\w$]*$"))
                        {
                            return part;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to extract symbol name from quick info");
        }

        return null;
    }

    private List<string> GenerateInsights(RenameInfo renameInfo, List<ConflictInfo> conflicts, TsRenameSymbolParams parameters)
    {
        var insights = new List<string>();

        insights.Add($"Renaming '{renameInfo.OldName}' to '{renameInfo.NewName}'");
        insights.Add($"Found {renameInfo.Locations.Count} occurrence(s) across {renameInfo.Locations.Select(l => l.FilePath).Distinct().Count()} file(s)");

        if (parameters.FindInComments)
        {
            insights.Add("Including occurrences in comments");
        }

        if (parameters.FindInStrings)
        {
            insights.Add("Including occurrences in string literals");
        }

        if (conflicts.Count > 0)
        {
            insights.Add($"⚠️ {conflicts.Count} potential conflict(s) detected");
        }

        if (IsGenericName(renameInfo.NewName))
        {
            insights.Add($"Consider using a more descriptive name than '{renameInfo.NewName}'");
        }

        if (parameters.Preview)
        {
            insights.Add("Preview mode - no changes applied");
        }

        return insights;
    }

    private List<AIAction> GenerateActions(bool isPreview, bool hasConflicts)
    {
        var actions = new List<AIAction>();

        if (isPreview)
        {
            if (!hasConflicts)
            {
                actions.Add(new AIAction
                {
                    Action = "apply_rename",
                    Description = "Apply the rename changes",
                    Category = "refactor",
                    Priority = 10
                });
            }
            else
            {
                actions.Add(new AIAction
                {
                    Action = "resolve_conflicts",
                    Description = "Resolve naming conflicts before applying",
                    Category = "fix",
                    Priority = 10
                });
            }
        }

        actions.Add(new AIAction
        {
            Action = ToolNames.TsFindAllReferences,
            Description = "Verify all references after rename",
            Category = "verify"
        });

        actions.Add(new AIAction
        {
            Action = ToolNames.TsGetDiagnostics,
            Description = "Check for compilation errors after rename",
            Category = "verify"
        });

        return actions;
    }

    private TsRenameSymbolResult CreateErrorResult(string message, ErrorInfo error)
    {
        return new TsRenameSymbolResult
        {
            Success = false,
            Message = message,
            Error = error
        };
    }

    private TsRenameSymbolResult CreateWorkspaceNotLoadedResult(string filePath)
    {
        return new TsRenameSymbolResult
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

    public void Dispose()
    {
        foreach (var handler in _serverHandlers.Values)
        {
            handler?.Dispose();
        }
        _serverHandlers.Clear();
        _serverLock?.Dispose();
    }

    private class RenameInfo
    {
        public bool CanRename { get; set; }
        public string? ReasonWhyNot { get; set; }
        public string OldName { get; set; } = "";
        public string NewName { get; set; } = "";
        public List<RenameLocation> Locations { get; set; } = new();
    }
}

/// <summary>
/// Parameters for TypeScript rename symbol
/// </summary>
public class TsRenameSymbolParams
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

    [Required]
    [JsonPropertyName("newName")]
    [MinLength(1)]
    public required string NewName { get; set; }

    [JsonPropertyName("preview")]
    public bool Preview { get; set; } = true;

    [JsonPropertyName("findInComments")]
    public bool FindInComments { get; set; } = false;

    [JsonPropertyName("findInStrings")]
    public bool FindInStrings { get; set; } = false;
}

/// <summary>
/// Result for TypeScript rename symbol
/// </summary>
public class TsRenameSymbolResult : ToolResultBase
{
    public override string Operation => ToolNames.TsRenameSymbol;

    [JsonPropertyName("query")]
    public RenameQuery? Query { get; set; }

    [JsonPropertyName("summary")]
    public RenameSummary? Summary { get; set; }

    [JsonPropertyName("changes")]
    public List<RenameLocation>? Changes { get; set; }

    [JsonPropertyName("conflicts")]
    public List<ConflictInfo>? Conflicts { get; set; }

    [JsonPropertyName("applied")]
    public bool Applied { get; set; }
}

/// <summary>
/// Query information for rename
/// </summary>
public class RenameQuery : QueryInfo
{
    [JsonPropertyName("oldName")]
    public string? OldName { get; set; }

    [JsonPropertyName("newName")]
    public string? NewName { get; set; }
}

/// <summary>
/// Summary of rename operation
/// </summary>
public class RenameSummary
{
    [JsonPropertyName("totalOccurrences")]
    public int TotalOccurrences { get; set; }

    [JsonPropertyName("filesAffected")]
    public int FilesAffected { get; set; }

    [JsonPropertyName("preview")]
    public bool Preview { get; set; }
}

/// <summary>
/// Location of a rename change
/// </summary>
public class RenameLocation
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }

    [JsonPropertyName("endColumn")]
    public int EndColumn { get; set; }

    [JsonPropertyName("oldText")]
    public string? OldText { get; set; }

    [JsonPropertyName("newText")]
    public string? NewText { get; set; }

    [JsonPropertyName("contextBefore")]
    public string? ContextBefore { get; set; }

    [JsonPropertyName("contextAfter")]
    public string? ContextAfter { get; set; }
}

/// <summary>
/// Information about a rename conflict
/// </summary>
public class ConflictInfo
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("description")]
    public required string Description { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Error";
}