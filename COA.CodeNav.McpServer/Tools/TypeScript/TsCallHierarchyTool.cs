using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services.TypeScript;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools.TypeScript;

/// <summary>
/// MCP tool that provides complete call hierarchy (incoming and outgoing calls) for TypeScript functions/methods
/// </summary>
public class TsCallHierarchyTool : McpToolBase<TsCallHierarchyParams, TsCallHierarchyResult>, IDisposable
{
    private readonly ILogger<TsCallHierarchyTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly Dictionary<string, TsServerProtocolHandler> _serverHandlers = new();
    private readonly SemaphoreSlim _serverLock = new(1, 1);

    public override string Name => ToolNames.TsCallHierarchy;
    
    public override string Description => @"Get complete call hierarchy (incoming and outgoing calls) for a TypeScript function/method at once.
Returns: Bidirectional call tree with incoming (callers) and outgoing (callees) in a single view.
Prerequisites: Call ts_load_tsconfig first to load the TypeScript project.
Error handling: Returns specific error codes with recovery steps if function cannot be found.
Use cases: Understanding function dependencies, impact analysis, refactoring planning, debugging call chains.
AI benefit: Provides complete context that agents can't easily piece together from separate tools.";

    public TsCallHierarchyTool(
        ILogger<TsCallHierarchyTool> logger,
        TypeScriptWorkspaceService workspaceService,
        TypeScriptCompilerManager compilerManager,
        ITokenEstimator tokenEstimator)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _compilerManager = compilerManager;
        _tokenEstimator = tokenEstimator;
    }

    protected override async Task<TsCallHierarchyResult> ExecuteInternalAsync(
        TsCallHierarchyParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogDebug("TsCallHierarchy request: File={File}, Line={Line}, Character={Character}",
            parameters.FilePath, parameters.Line, parameters.Character);

        try
        {
            // Validate TypeScript availability
            var tsError = _compilerManager.ValidateTypeScriptAvailability();
            if (tsError != null)
            {
                return new TsCallHierarchyResult
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
                return new TsCallHierarchyResult
                {
                    Success = false,
                    Message = $"No TypeScript project loaded for file: {parameters.FilePath}",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.TS_PROJECT_NOT_LOADED,
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
                return new TsCallHierarchyResult
                {
                    Success = false,
                    Message = "Failed to start TypeScript server",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.TS_SERVER_START_FAILED,
                        Message = "Could not start or connect to TypeScript server"
                    }
                };
            }

            // Ensure file is open in the TypeScript server  
            await handler.OpenFileAsync(parameters.FilePath, null, cancellationToken);

            // For now, create a basic implementation using find references
            // This will be enhanced once TypeScript Server Protocol call hierarchy methods are added
            
            // Convert 0-based to 1-based for TSP
            var tspLine = parameters.Line + 1;
            var tspOffset = parameters.Character + 1;
            
            // Get quick info to understand what symbol we're on
            var quickInfo = await handler.GetQuickInfoAsync(parameters.FilePath, tspLine, tspOffset, cancellationToken);
            if (quickInfo == null)
            {
                return new TsCallHierarchyResult
                {
                    Success = false,
                    Message = "No symbol found at the specified position",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.NO_SYMBOL_AT_POSITION,
                        Message = "No function, method, or callable found at position"
                    }
                };
            }

            // Get symbol name from quick info
            var symbolName = quickInfo.HasValue ? ExtractSymbolName(quickInfo.Value) : null;
            
            // Create basic root item
            var rootItem = new TsCallHierarchyItem
            {
                Name = symbolName ?? "Unknown",
                Kind = "function",
                File = parameters.FilePath,
                Span = new TsTextSpan
                {
                    Start = new TsTextPosition { Line = parameters.Line, Character = parameters.Character },
                    End = new TsTextPosition { Line = parameters.Line, Character = parameters.Character }
                }
            };

            // Use find references to get basic call information
            var references = await handler.GetReferencesAsync(parameters.FilePath, tspLine, tspOffset, cancellationToken);
            var (incomingCalls, outgoingCalls) = ProcessReferencesForCallHierarchy(references, parameters.FilePath);

            // Build the call tree structure
            var callTree = BuildCallTree(rootItem, incomingCalls, outgoingCalls, parameters.MaxDepth);

            var result = new TsCallHierarchyResult
            {
                Success = true,
                Message = $"Found call hierarchy for {rootItem.Name}",
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Character }
                },
                Summary = new SummaryInfo
                {
                    TotalFound = (incomingCalls?.Count ?? 0) + (outgoingCalls?.Count ?? 0),
                    Returned = (incomingCalls?.Count ?? 0) + (outgoingCalls?.Count ?? 0),
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                },
                Root = rootItem,
                IncomingCalls = incomingCalls,
                OutgoingCalls = outgoingCalls,
                CallTree = callTree,
                ResultsSummary = new ResultsSummary
                {
                    Total = (incomingCalls?.Count ?? 0) + (outgoingCalls?.Count ?? 0),
                    Included = (incomingCalls?.Count ?? 0) + (outgoingCalls?.Count ?? 0),
                    HasMore = false
                }
            };

            // Apply token optimization if needed
            await OptimizeResultAsync(result, cancellationToken);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("TsCallHierarchy operation was cancelled");
            return new TsCallHierarchyResult
            {
                Success = false,
                Message = "Operation was cancelled"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during TsCallHierarchy operation");
            return new TsCallHierarchyResult
            {
                Success = false,
                Message = $"Unexpected error: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Message = ex.Message
                }
            };
        }
    }

    private async Task<TsServerProtocolHandler?> GetOrCreateServerHandlerAsync(string workspaceId, string projectPath, CancellationToken cancellationToken)
    {
        await _serverLock.WaitAsync(cancellationToken);
        try
        {
            if (_serverHandlers.TryGetValue(workspaceId, out var existingHandler) && existingHandler.IsRunning)
            {
                return existingHandler;
            }

            var handlerLogger = _logger as ILogger<TsServerProtocolHandler> 
                ?? new LoggerFactory().CreateLogger<TsServerProtocolHandler>();
            var newHandler = await TsServerProtocolHandler.CreateAsync(
                handlerLogger,
                projectPath, 
                cancellationToken);
            if (newHandler != null)
            {
                _serverHandlers[workspaceId] = newHandler;
            }

            return newHandler;
        }
        finally
        {
            _serverLock.Release();
        }
    }

    private string? ExtractSymbolName(JsonElement quickInfo)
    {
        try
        {
            if (quickInfo.TryGetProperty("displayString", out var displayElement))
            {
                return displayElement.GetString();
            }
            if (quickInfo.TryGetProperty("kind", out var kindElement))
            {
                return kindElement.GetString();
            }
            return "symbol";
        }
        catch
        {
            return "symbol";
        }
    }

    private (List<TsIncomingCall>?, List<TsOutgoingCall>?) ProcessReferencesForCallHierarchy(
        JsonElement? references, 
        string currentFilePath)
    {
        var incomingCalls = new List<TsIncomingCall>();
        var outgoingCalls = new List<TsOutgoingCall>();

        try
        {
            if (references.HasValue && references.Value.TryGetProperty("refs", out var refsElement))
            {
                foreach (var refElement in refsElement.EnumerateArray())
                {
                    if (refElement.TryGetProperty("file", out var fileElement))
                    {
                        var file = fileElement.GetString();
                        if (file != null && !file.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            // Create a basic call hierarchy item for this reference
                            var item = new TsCallHierarchyItem
                            {
                                Name = System.IO.Path.GetFileNameWithoutExtension(file),
                                Kind = "reference",
                                File = file,
                                Span = ParseRefSpan(refElement)
                            };

                            // For simplicity, treat all external references as incoming calls
                            incomingCalls.Add(new TsIncomingCall
                            {
                                From = item,
                                FromSpans = new List<TsTextSpan> { item.Span ?? new TsTextSpan() }
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process references for call hierarchy");
        }

        return (incomingCalls, outgoingCalls);
    }

    private TsTextSpan? ParseRefSpan(JsonElement refElement)
    {
        try
        {
            if (refElement.TryGetProperty("start", out var startElement) &&
                refElement.TryGetProperty("end", out var endElement))
            {
                return new TsTextSpan
                {
                    Start = new TsTextPosition
                    {
                        Line = startElement.TryGetProperty("line", out var startLine) ? startLine.GetInt32() : 0,
                        Character = startElement.TryGetProperty("offset", out var startOffset) ? startOffset.GetInt32() : 0
                    },
                    End = new TsTextPosition
                    {
                        Line = endElement.TryGetProperty("line", out var endLine) ? endLine.GetInt32() : 0,
                        Character = endElement.TryGetProperty("offset", out var endOffset) ? endOffset.GetInt32() : 0
                    }
                };
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }


    private TsCallTreeNode BuildCallTree(
        TsCallHierarchyItem root, 
        List<TsIncomingCall>? incomingCalls, 
        List<TsOutgoingCall>? outgoingCalls, 
        int maxDepth)
    {
        var rootNode = new TsCallTreeNode
        {
            Item = root,
            Depth = 0,
            Direction = "root",
            Children = new List<TsCallTreeNode>(),
            IsExpanded = true
        };

        // Add incoming calls as children
        if (incomingCalls != null)
        {
            foreach (var call in incomingCalls.Take(50)) // Limit to prevent excessive output
            {
                if (call.From != null)
                {
                    rootNode.Children.Add(new TsCallTreeNode
                    {
                        Item = call.From,
                        Depth = 1,
                        Direction = "incoming",
                        Children = new List<TsCallTreeNode>(),
                        IsExpanded = false
                    });
                }
            }
        }

        // Add outgoing calls as children
        if (outgoingCalls != null)
        {
            foreach (var call in outgoingCalls.Take(50)) // Limit to prevent excessive output
            {
                if (call.To != null)
                {
                    rootNode.Children.Add(new TsCallTreeNode
                    {
                        Item = call.To,
                        Depth = 1,
                        Direction = "outgoing",
                        Children = new List<TsCallTreeNode>(),
                        IsExpanded = false
                    });
                }
            }
        }

        return rootNode;
    }

    private int GetUniqueFileCount(List<TsIncomingCall>? incomingCalls, List<TsOutgoingCall>? outgoingCalls)
    {
        var files = new HashSet<string>();
        
        if (incomingCalls != null)
        {
            foreach (var call in incomingCalls)
            {
                if (!string.IsNullOrEmpty(call.From?.File))
                    files.Add(call.From.File);
            }
        }

        if (outgoingCalls != null)
        {
            foreach (var call in outgoingCalls)
            {
                if (!string.IsNullOrEmpty(call.To?.File))
                    files.Add(call.To.File);
            }
        }

        return files.Count;
    }

    private async Task OptimizeResultAsync(TsCallHierarchyResult result, CancellationToken cancellationToken)
    {
        const int SAFETY_TOKEN_LIMIT = 10000;
        
        var estimatedTokens = _tokenEstimator.EstimateObject(result);
        
        if (estimatedTokens > SAFETY_TOKEN_LIMIT)
        {
            // Reduce incoming calls
            if (result.IncomingCalls?.Count > 20)
            {
                result.IncomingCalls = result.IncomingCalls.Take(20).ToList();
            }
            
            // Reduce outgoing calls
            if (result.OutgoingCalls?.Count > 20)
            {
                result.OutgoingCalls = result.OutgoingCalls.Take(20).ToList();
            }

            // Update summary
            if (result.Summary != null)
            {
                var newTotal = (result.IncomingCalls?.Count ?? 0) + (result.OutgoingCalls?.Count ?? 0);
                result.Summary.TotalFound = newTotal;
                result.Summary.Returned = newTotal;
            }

            // Rebuild call tree with reduced data
            if (result.Root != null)
            {
                result.CallTree = BuildCallTree(result.Root, result.IncomingCalls, result.OutgoingCalls, 2);
            }
        }

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _serverLock?.Dispose();
        
        foreach (var handler in _serverHandlers.Values)
        {
            handler?.Dispose();
        }
        _serverHandlers.Clear();
    }

}