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
/// MCP tool that applies TypeScript compiler quick fixes and code actions using TSP
/// </summary>
public class TsApplyQuickFixTool : McpToolBase<TsApplyQuickFixParams, TsApplyQuickFixResult>, IDisposable
{
    private const int SAFETY_TOKEN_LIMIT = 8000;
    private readonly ILogger<TsApplyQuickFixTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;
    private readonly TsApplyQuickFixResponseBuilder? _responseBuilder;
    private readonly Dictionary<string, TsServerProtocolHandler> _serverHandlers = new();
    private readonly SemaphoreSlim _serverLock = new(1, 1);

    public override string Name => ToolNames.TsApplyQuickFix;
    public override ToolCategory Category => ToolCategory.Refactoring;
    
    public override string Description => @"Apply TypeScript compiler quick fixes instantly. One-click solutions for suggested fixes like adding missing declarations or imports.

Parameters use 1-based indexing:
- line: 1-based line number (first line is 1)  
- character: 1-based character position (first character is 1)";

    public TsApplyQuickFixTool(
        IServiceProvider serviceProvider,
        ILogger<TsApplyQuickFixTool> logger,
        TypeScriptWorkspaceService workspaceService,
        TypeScriptCompilerManager compilerManager,
        ITokenEstimator tokenEstimator,
        AnalysisResultResourceProvider? resourceProvider = null,
        TsApplyQuickFixResponseBuilder? responseBuilder = null)
        : base(serviceProvider, logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _compilerManager = compilerManager;
        _tokenEstimator = tokenEstimator;
        _resourceProvider = resourceProvider;
        _responseBuilder = responseBuilder;
    }

    protected override async Task<TsApplyQuickFixResult> ExecuteInternalAsync(
        TsApplyQuickFixParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogDebug("TsApplyQuickFix request: File={File}, Line={Line}, Character={Character}, Action={Action}, Preview={Preview}",
            parameters.FilePath, parameters.Line, parameters.Character, parameters.ActionName, parameters.Preview);

        try
        {
            // Validate TypeScript availability
            var tsError = _compilerManager.ValidateTypeScriptAvailability();
            if (tsError != null)
            {
                return new TsApplyQuickFixResult
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

            // Normalize file path - parameters are already 1-based, TSP expects 1-based, so no conversion needed
            var normalizedPath = Path.GetFullPath(parameters.FilePath).Replace('\\', '/');
            var tspLine = parameters.Line;
            var tspOffset = parameters.Character;
            var endTspLine = parameters.EndLine ?? parameters.Line;
            var endTspOffset = parameters.EndCharacter ?? parameters.Character;

            // Get available code fixes at the position
            var codeFixesResponse = await handler.GetCodeFixesAsync(
                normalizedPath, 
                tspLine, 
                tspOffset,
                endTspLine,
                endTspOffset,
                parameters.ErrorCodes,
                cancellationToken);

            if (codeFixesResponse == null)
            {
                return new TsApplyQuickFixResult
                {
                    Success = false,
                    Message = "No code fixes available at this position",
                    Query = new ApplyQuickFixQuery
                    {
                        FilePath = parameters.FilePath,
                        Line = parameters.Line,
                        Character = parameters.Character,
                        ActionName = parameters.ActionName,
                        Preview = parameters.Preview
                    },
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.NO_CODE_FIXES_AVAILABLE,
                        Message = "TypeScript compiler has no quick fixes available at this position",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new[]
                            {
                                "Ensure there are compilation errors at this position",
                                "Check if the position is on a symbol that can be fixed",
                                "Run ts_get_diagnostics to see available errors"
                            }
                        }
                    }
                };
            }

            // Parse available code fixes
            var availableFixes = ParseAvailableCodeFixes(codeFixesResponse.Value);

            if (availableFixes.Count == 0)
            {
                return new TsApplyQuickFixResult
                {
                    Success = false,
                    Message = "No applicable code fixes found",
                    Query = new ApplyQuickFixQuery
                    {
                        FilePath = parameters.FilePath,
                        Line = parameters.Line,
                        Character = parameters.Character,
                        ActionName = parameters.ActionName,
                        Preview = parameters.Preview
                    },
                    AvailableFixes = new List<QuickFixInfo>(),
                    Insights = new List<string>
                    {
                        "No automatic fixes available at this position",
                        "This may require manual code changes",
                        "Check the TypeScript compilation errors for more context"
                    }
                };
            }

            // Find the specific fix to apply
            TsCodeFixInfo? selectedFix = null;
            if (!string.IsNullOrEmpty(parameters.ActionName))
            {
                selectedFix = availableFixes.FirstOrDefault(f => 
                    f.Description.Contains(parameters.ActionName, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // If no specific action requested, use the first available fix
                selectedFix = availableFixes.First();
            }

            if (selectedFix == null)
            {
                return new TsApplyQuickFixResult
                {
                    Success = false,
                    Message = $"No code fix found matching '{parameters.ActionName}'",
                    Query = new ApplyQuickFixQuery
                    {
                        FilePath = parameters.FilePath,
                        Line = parameters.Line,
                        Character = parameters.Character,
                        ActionName = parameters.ActionName,
                        Preview = parameters.Preview
                    },
                    AvailableFixes = availableFixes.Select(f => new QuickFixInfo
                    {
                        Name = ExtractActionName(f.Description),
                        Description = f.Description,
                        Category = "quickfix",
                        Priority = 1
                    }).ToList(),
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.FIX_NOT_FOUND,
                        Message = $"No fix found with action name '{parameters.ActionName}'"
                    }
                };
            }

            // Apply the selected fix
            var changes = selectedFix.Changes;
            var updatedContent = ApplyChanges(originalContent, changes);

            // If not preview mode, write the changes to disk
            if (!parameters.Preview)
            {
                await File.WriteAllTextAsync(parameters.FilePath, updatedContent, cancellationToken);
                
                // Update the TypeScript server with the new content
                await handler.UpdateFileAsync(parameters.FilePath, updatedContent, cancellationToken);
            }

            var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            // Build the result
            var result = new TsApplyQuickFixResult
            {
                Success = true,
                Message = parameters.Preview 
                    ? $"Preview: Apply quick fix '{selectedFix.Description}' - {changes.Count} change(s)"
                    : $"Applied quick fix '{selectedFix.Description}' - {changes.Count} change(s)",
                Query = new ApplyQuickFixQuery
                {
                    FilePath = parameters.FilePath,
                    Line = parameters.Line,
                    Character = parameters.Character,
                    ActionName = parameters.ActionName ?? selectedFix.Description,
                    Preview = parameters.Preview
                },
                Summary = new ApplyQuickFixSummary
                {
                    AppliedFixesCount = 1,
                    ChangesCount = changes.Count,
                    AvailableFixesCount = availableFixes.Count
                },
                Changes = changes,
                OriginalContent = parameters.Preview ? originalContent : null,
                UpdatedContent = parameters.Preview ? updatedContent : null,
                AvailableFixes = availableFixes.Select(f => new QuickFixInfo
                {
                    Description = f.Description,
                    Name = ExtractActionName(f.Description)
                }).ToList(),
                Insights = GenerateInsights(selectedFix, changes, availableFixes),
                Actions = GenerateActions(parameters.FilePath, changes.Count),
                Meta = new ToolExecutionMetadata
                {
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
            else
            {
                // Fallback to direct token optimization
                result = await ApplyTokenOptimization(result, executionTime);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying quick fix for file: {FilePath}", parameters.FilePath);
            return CreateErrorResult($"Failed to apply quick fix: {ex.Message}",
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

    private TsApplyQuickFixResult CreateWorkspaceNotLoadedResult(string filePath)
    {
        return new TsApplyQuickFixResult
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
                            Parameters = new Dictionary<string, object>
                            {
                                ["tsConfigPath"] = "path/to/tsconfig.json"
                            }
                        }
                    }
                }
            }
        };
    }

    private TsApplyQuickFixResult CreateErrorResult(string message, ErrorInfo error)
    {
        return new TsApplyQuickFixResult
        {
            Success = false,
            Message = message,
            Error = error
        };
    }

    private List<TsCodeFixInfo> ParseAvailableCodeFixes(JsonElement codeFixesResponse)
    {
        var fixes = new List<TsCodeFixInfo>();

        try
        {
            if (codeFixesResponse.ValueKind == JsonValueKind.Array)
            {
                foreach (var fix in codeFixesResponse.EnumerateArray())
                {
                    if (fix.TryGetProperty("description", out var description) &&
                        fix.TryGetProperty("changes", out var changes) &&
                        changes.ValueKind == JsonValueKind.Array)
                    {
                        var fileChanges = new List<TsFileChange>();
                        
                        foreach (var change in changes.EnumerateArray())
                        {
                            if (change.TryGetProperty("textChanges", out var textChanges) &&
                                textChanges.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var textChange in textChanges.EnumerateArray())
                                {
                                    var fileChange = ParseTextChange(textChange);
                                    if (fileChange != null)
                                    {
                                        fileChange.Description = description.GetString() ?? "Apply quick fix";
                                        fileChanges.Add(fileChange);
                                    }
                                }
                            }
                        }

                        if (fileChanges.Count > 0)
                        {
                            fixes.Add(new TsCodeFixInfo
                            {
                                Description = description.GetString() ?? "Unknown fix",
                                Changes = fileChanges
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse available code fixes");
        }

        return fixes;
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
                NewText = newText.GetString() ?? ""
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

    private string ExtractActionName(string description)
    {
        // Extract a simplified action name from the description
        // Examples: "Add missing function declaration" -> "add-missing-function"
        return description.ToLower()
            .Replace(" ", "-")
            .Replace("'", "")
            .Replace("\"", "");
    }

    private List<string> GenerateInsights(TsCodeFixInfo appliedFix, List<TsFileChange> changes, List<TsCodeFixInfo> availableFixes)
    {
        var insights = new List<string>();

        insights.Add($"Applied fix: {appliedFix.Description}");
        
        if (changes.Count > 0)
        {
            insights.Add($"Made {changes.Count} text change(s) to the file");
        }

        if (availableFixes.Count > 1)
        {
            insights.Add($"{availableFixes.Count - 1} other fix(es) were available at this position");
        }

        insights.Add("Run ts_get_diagnostics to check for remaining errors");
        insights.Add("Consider running ts_organize_imports if imports were modified");

        return insights;
    }

    private List<AIAction> GenerateActions(string filePath, int changeCount)
    {
        var actions = new List<AIAction>();

        if (changeCount > 0)
        {
            actions.Add(new AIAction
            {
                Action = ToolNames.TsGetDiagnostics,
                Description = "Check for remaining TypeScript errors",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = filePath
                }
            });

            actions.Add(new AIAction
            {
                Action = ToolNames.TsOrganizeImports,
                Description = "Organize imports if they were modified",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = filePath
                }
            });
        }

        return actions;
    }

    private Task<TsApplyQuickFixResult> ApplyTokenOptimization(TsApplyQuickFixResult result, double executionTime)
    {
        // Estimate current token usage
        var estimatedTokens = _tokenEstimator.EstimateObject(result);
        var wasOptimized = false;
        
        if (estimatedTokens > SAFETY_TOKEN_LIMIT)
        {
            _logger.LogDebug("TsApplyQuickFix: Token optimization needed. Estimated: {EstimatedTokens}, Limit: {Limit}", 
                estimatedTokens, SAFETY_TOKEN_LIMIT);
            
            // Progressive reduction strategy
            if (result.AvailableFixes?.Count > 15)
            {
                // Keep first 15 most relevant fixes (TSP returns them by relevance)
                result.AvailableFixes = result.AvailableFixes.Take(15).ToList();
                result.Insights?.Add($"⚠️ Showing first 15 of {result.Summary?.AvailableFixesCount} available fixes due to size limits");
                wasOptimized = true;
            }
            
            // Reduce content if still over limit and in preview mode
            if (_tokenEstimator.EstimateObject(result) > SAFETY_TOKEN_LIMIT && result.OriginalContent != null)
            {
                if (result.OriginalContent.Length > 2000)
                {
                    result.OriginalContent = result.OriginalContent[..2000] + "\n... (truncated)";
                }
                if (result.UpdatedContent != null && result.UpdatedContent.Length > 2000)
                {
                    result.UpdatedContent = result.UpdatedContent[..2000] + "\n... (truncated)";
                }
                wasOptimized = true;
            }
            
            // Store full result in resource provider if available
            if (wasOptimized && _resourceProvider != null)
            {
                try
                {
                    var resourceUri = _resourceProvider.StoreAnalysisResult("ts-apply-quick-fix", result);
                    result.ResourceUri = resourceUri;
                    result.Insights?.Add("💾 Complete results stored - use resource tools to access full data");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to store full result in resource provider");
                }
            }
        }
        
        // Update metadata
        result.Meta = new ToolExecutionMetadata
        {
            Mode = wasOptimized ? "optimized" : "full",
            Truncated = wasOptimized,
            Tokens = _tokenEstimator.EstimateObject(result),
            ExecutionTime = $"{executionTime:F2}ms"
        };
        
        return Task.FromResult(result);
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
/// Parameters for applying TypeScript quick fixes
/// </summary>
public class TsApplyQuickFixParams
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

    [JsonPropertyName("endLine")]
    [Range(0, int.MaxValue)]
    public int? EndLine { get; set; }

    [JsonPropertyName("endCharacter")]
    [Range(0, int.MaxValue)]
    public int? EndCharacter { get; set; }

    [JsonPropertyName("actionName")]
    public string? ActionName { get; set; }

    [JsonPropertyName("errorCodes")]
    public string[]? ErrorCodes { get; set; }

    [JsonPropertyName("preview")]
    public bool Preview { get; set; } = false;
}
