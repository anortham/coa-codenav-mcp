using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders.TypeScript;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Services.TypeScript;
using COA.Mcp.Framework;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;
using AIAction = COA.Mcp.Framework.Models.AIAction;
using ToolExecutionMetadata = COA.Mcp.Framework.Models.ToolExecutionMetadata;

namespace COA.CodeNav.McpServer.Tools.TypeScript;

/// <summary>
/// MCP tool that organizes import statements in TypeScript files using TSP
/// </summary>
public class TsOrganizeImportsTool : McpToolBase<TsOrganizeImportsParams, TsOrganizeImportsResult>, IDisposable
{
    private readonly ILogger<TsOrganizeImportsTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;
    private readonly TsOrganizeImportsResponseBuilder? _responseBuilder;
    private readonly Dictionary<string, TsServerProtocolHandler> _serverHandlers = new();
    private readonly SemaphoreSlim _serverLock = new(1, 1);
    
    private const int SAFETY_TOKEN_LIMIT = 8000;

    public override string Name => ToolNames.TsOrganizeImports;
    public override ToolCategory Category => ToolCategory.Refactoring;
    
    public override string Description => @"Organize and sort TypeScript import statements automatically. Groups imports, removes unused ones, and applies consistent formatting.

Critical: Use after adding imports or before committing code. Ensures professional import organization and removes import clutter.

Prerequisites: Call ts_load_tsconfig first to load the TypeScript project.
Use cases: Cleaning up imports, preparing for code review, maintaining consistent formatting.";

    public TsOrganizeImportsTool(
        ILogger<TsOrganizeImportsTool> logger,
        TypeScriptWorkspaceService workspaceService,
        TypeScriptCompilerManager compilerManager,
        ITokenEstimator tokenEstimator,
        AnalysisResultResourceProvider? resourceProvider = null,
        TsOrganizeImportsResponseBuilder? responseBuilder = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _compilerManager = compilerManager;
        _tokenEstimator = tokenEstimator;
        _resourceProvider = resourceProvider;
        _responseBuilder = responseBuilder;
    }

    protected override async Task<TsOrganizeImportsResult> ExecuteInternalAsync(
        TsOrganizeImportsParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogDebug("TsOrganizeImports request: File={File}, Preview={Preview}",
            parameters.FilePath, parameters.Preview);

        try
        {
            // Validate TypeScript availability
            var tsError = _compilerManager.ValidateTypeScriptAvailability();
            if (tsError != null)
            {
                return new TsOrganizeImportsResult
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

            // Get current file content
            var originalContent = await File.ReadAllTextAsync(parameters.FilePath, cancellationToken);

            // Organize imports using TypeScript server
            var organizeResponse = await handler.OrganizeImportsAsync(parameters.FilePath, cancellationToken);

            if (organizeResponse == null)
            {
                return new TsOrganizeImportsResult
                {
                    Success = false,
                    Message = "Failed to organize imports",
                    Query = new OrganizeImportsQuery
                    {
                        FilePath = parameters.FilePath,
                        Preview = parameters.Preview
                    },
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.ORGANIZE_IMPORTS_FAILED,
                        Message = "TypeScript server could not organize imports for this file",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new[]
                            {
                                "Check if the file has valid TypeScript syntax",
                                "Ensure the file is included in the TypeScript project",
                                "Verify imports are not causing circular dependencies"
                            }
                        }
                    }
                };
            }

            // Parse the response and apply changes
            var changes = ParseOrganizeImportsResponse(organizeResponse.Value, originalContent);

            if (changes == null || changes.Count == 0)
            {
                return new TsOrganizeImportsResult
                {
                    Success = true,
                    Message = "No import organization needed - imports are already properly organized",
                    Query = new OrganizeImportsQuery
                    {
                        FilePath = parameters.FilePath,
                        Preview = parameters.Preview
                    },
                    Summary = new OrganizeImportsSummary
                    {
                        TotalChanges = 0,
                        OrganizedImports = 0,
                        RemovedDuplicates = 0
                    },
                    Meta = new ToolExecutionMetadata
                    {
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                    }
                };
            }

            var updatedContent = ApplyChanges(originalContent, changes);

            // If not preview mode, write the changes to disk
            if (!parameters.Preview)
            {
                await File.WriteAllTextAsync(parameters.FilePath, updatedContent, cancellationToken);
                
                // Update the TypeScript server with the new content
                await handler.UpdateFileAsync(parameters.FilePath, updatedContent, cancellationToken);
            }

            var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            // Apply token optimization to prevent context overflow
            var estimatedTokens = _tokenEstimator.EstimateObject(changes);
            var optimizedChanges = changes;
            var wasTruncated = false;
            
            if (estimatedTokens > SAFETY_TOKEN_LIMIT)
            {
                // Apply progressive reduction
                optimizedChanges = changes.Take(20).ToList();
                wasTruncated = true;
                _logger.LogDebug("Token optimization applied: reduced from {Original} to {Reduced} changes",
                    changes.Count, optimizedChanges.Count);
                    
                // Store full results in resource provider if available
                if (_resourceProvider != null && changes.Count > optimizedChanges.Count)
                {
                    var resourceUri = _resourceProvider.StoreAnalysisResult(
                        "ts-organize-imports-full", 
                        new { AllChanges = changes, FilePath = parameters.FilePath });
                }
            }
            
            // Build the result
            var result = new TsOrganizeImportsResult
            {
                Success = true,
                Message = parameters.Preview 
                    ? $"Preview: Organize imports in '{Path.GetFileName(parameters.FilePath)}' - {changes.Count} change(s)"
                    : $"Organized imports in '{Path.GetFileName(parameters.FilePath)}' - {changes.Count} change(s) applied",
                Query = new OrganizeImportsQuery
                {
                    FilePath = parameters.FilePath,
                    Preview = parameters.Preview
                },
                Summary = new OrganizeImportsSummary
                {
                    TotalChanges = changes.Count,
                    OrganizedImports = CountImportStatements(updatedContent),
                    RemovedDuplicates = CountDuplicatesRemoved(originalContent, updatedContent)
                },
                Changes = optimizedChanges,
                OriginalContent = parameters.Preview ? originalContent : null,
                UpdatedContent = parameters.Preview ? updatedContent : null,
                Insights = GenerateInsights(changes, wasTruncated),
                Actions = GenerateActions(parameters.FilePath, changes.Count),
                Meta = new ToolExecutionMetadata
                {
                    Mode = wasTruncated ? "truncated" : "full",
                    Truncated = wasTruncated,
                    Tokens = _tokenEstimator.EstimateObject(optimizedChanges),
                    ExecutionTime = $"{executionTime:F2}ms"
                }
            };

            // Apply response builder optimization if available
            if (_responseBuilder != null)
            {
                var context = new ResponseContext
                {
                    ResponseMode = "optimized",
                    TokenLimit = SAFETY_TOKEN_LIMIT,
                    ToolName = Name
                };
                
                result = await _responseBuilder.BuildResponseAsync(result, context);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error organizing imports for file: {FilePath}", parameters.FilePath);
            return CreateErrorResult($"Failed to organize imports: {ex.Message}",
                new ErrorInfo { Code = ErrorCodes.INTERNAL_ERROR, Message = ex.Message });
        }
    }

    #region Helper Methods

    private async Task<TsServerProtocolHandler?> GetOrCreateServerHandlerAsync(string workspaceId, string projectPath, CancellationToken cancellationToken)
    {
        await _serverLock.WaitAsync(cancellationToken);
        try
        {
            if (_serverHandlers.TryGetValue(workspaceId, out var existingHandler) && existingHandler.IsRunning)
            {
                return existingHandler;
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

    private TsOrganizeImportsResult CreateWorkspaceNotLoadedResult(string filePath)
    {
        return new TsOrganizeImportsResult
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
                            Parameters = new Dictionary<string, object> { ["tsConfigPath"] = "path/to/tsconfig.json" }
                        }
                    }
                }
            }
        };
    }

    private TsOrganizeImportsResult CreateErrorResult(string message, ErrorInfo error)
    {
        return new TsOrganizeImportsResult
        {
            Success = false,
            Message = message,
            Error = error
        };
    }

    private List<TsFileChange> ParseOrganizeImportsResponse(JsonElement response, string originalContent)
    {
        var changes = new List<TsFileChange>();

        try
        {
            if (response.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
            {
                foreach (var edit in edits.EnumerateArray())
                {
                    if (edit.TryGetProperty("textChanges", out var textChanges) && textChanges.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var textChange in textChanges.EnumerateArray())
                        {
                            var change = ParseTextChange(textChange);
                            if (change != null)
                            {
                                changes.Add(change);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse organize imports response");
        }

        return changes;
    }

    private TsFileChange? ParseTextChange(JsonElement textChange)
    {
        try
        {
            if (!textChange.TryGetProperty("span", out var span) ||
                !textChange.TryGetProperty("newText", out var newText))
                return null;

            if (!span.TryGetProperty("start", out var start) ||
                !span.TryGetProperty("length", out var length))
                return null;

            return new TsFileChange
            {
                StartPosition = start.GetInt32(),
                Length = length.GetInt32(),
                NewText = newText.GetString() ?? "",
                Description = "Organize imports"
            };
        }
        catch
        {
            return null;
        }
    }

    private string ApplyChanges(string originalContent, List<TsFileChange> changes)
    {
        // Sort changes by position descending to apply from end to start
        var sortedChanges = changes.OrderByDescending(c => c.StartPosition).ToList();
        
        var content = originalContent;
        foreach (var change in sortedChanges)
        {
            if (change.StartPosition >= 0 && change.StartPosition <= content.Length)
            {
                var endPos = Math.Min(change.StartPosition + change.Length, content.Length);
                content = content.Substring(0, change.StartPosition) + 
                         change.NewText + 
                         content.Substring(endPos);
            }
        }

        return content;
    }

    private int CountImportStatements(string content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        
        var lines = content.Split('\n');
        return lines.Count(line => 
        {
            var trimmed = line.Trim();
            return trimmed.StartsWith("import ") && (trimmed.Contains(" from ") || trimmed.EndsWith("';") || trimmed.EndsWith("\";"));
        });
    }

    private int CountDuplicatesRemoved(string originalContent, string updatedContent)
    {
        var originalImports = CountImportStatements(originalContent);
        var updatedImports = CountImportStatements(updatedContent);
        return Math.Max(0, originalImports - updatedImports);
    }

    private List<string> GenerateInsights(List<TsFileChange> changes, bool wasTruncated = false)
    {
        var insights = new List<string>();

        if (changes.Count > 0)
        {
            insights.Add($"Organized {changes.Count} import statement(s)");
            
            var hasNewlines = changes.Any(c => c.NewText.Contains('\n'));
            if (hasNewlines)
            {
                insights.Add("Import statements were reorganized with proper grouping");
            }
        }

        if (wasTruncated)
        {
            insights.Add("⚠️ Results truncated for optimal token usage");
        }
        
        insights.Add("Import organization follows TypeScript/TSLint standards");
        insights.Add("Run ts_add_missing_imports if you need to add missing dependencies");

        return insights;
    }

    private List<AIAction> GenerateActions(string filePath, int changeCount)
    {
        var actions = new List<AIAction>();

        if (changeCount > 0)
        {
            actions.Add(new AIAction
            {
                Action = ToolNames.TsAddMissingImports,
                Description = "Add any missing imports",
                Parameters = new Dictionary<string, object> { ["filePath"] = filePath }
            });

            actions.Add(new AIAction
            {
                Action = ToolNames.TsGetDiagnostics,
                Description = "Check for any remaining TypeScript errors",
                Parameters = new Dictionary<string, object> { ["filePath"] = filePath }
            });
        }

        return actions;
    }

    #endregion

    public void Dispose()
    {
        foreach (var handler in _serverHandlers.Values)
        {
            try
            {
                handler?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing TypeScript server handler");
            }
        }
        _serverHandlers.Clear();
        _serverLock?.Dispose();
    }
}

/// <summary>
/// Parameters for organizing imports in a TypeScript file
/// </summary>
public class TsOrganizeImportsParams
{
    [Required]
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }

    [JsonPropertyName("preview")]
    public bool Preview { get; set; } = false;
}


