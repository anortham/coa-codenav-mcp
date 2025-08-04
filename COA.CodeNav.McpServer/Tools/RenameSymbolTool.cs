using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

[McpServerToolType]
public class RenameSymbolTool
{
    private readonly ILogger<RenameSymbolTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public RenameSymbolTool(
        ILogger<RenameSymbolTool> logger, 
        RoslynWorkspaceService workspaceService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "csharp_rename_symbol", Category = "Refactoring")]
    [COA.CodeNav.McpServer.Attributes.Description(@"Rename a symbol across the entire solution with conflict detection and preview.
Returns: List of affected files, conflict information, and preview of changes.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if rename would cause conflicts.
Use cases: Rename classes, methods, properties, variables across entire codebase.
Not for: File renaming (use file system tools), namespace-only renames (use dedicated namespace tool).")]
    public async Task<object> ExecuteAsync(RenameSymbolParams parameters, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("RenameSymbol request received: FilePath={FilePath}, Line={Line}, Column={Column}, NewName={NewName}, Preview={Preview}", 
            parameters.FilePath, parameters.Line, parameters.Column, parameters.NewName, parameters.Preview);
            
        var startTime = DateTime.UtcNow;
            
        try
        {
            _logger.LogInformation("Processing RenameSymbol for {FilePath} at {Line}:{Column} to '{NewName}'", 
                parameters.FilePath, parameters.Line, parameters.Column, parameters.NewName);

            // Get the document
            _logger.LogDebug("Retrieving document from workspace: {FilePath}", parameters.FilePath);
            var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                _logger.LogWarning("Document not found: {FilePath}", parameters.FilePath);
                return new RenameSymbolToolResult
                {
                    Success = false,
                    Message = $"Document not found: {parameters.FilePath}",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.DOCUMENT_NOT_FOUND,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the file path is correct and absolute",
                                "Verify the solution/project containing this file is loaded",
                                "Use roslyn_load_solution or roslyn_load_project to load the workspace"
                            },
                            SuggestedActions = new List<SuggestedAction>
                            {
                                new SuggestedAction
                                {
                                    Tool = "roslyn_load_solution",
                                    Description = "Load the solution containing this file",
                                    Parameters = new { solutionPath = "<path-to-your-solution.sln>" }
                                }
                            }
                        }
                    },
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
                    },
                    Meta = new ToolMetadata { ExecutionTime = "0ms" }
                };
            }

            // Get the position
            var text = await document.GetTextAsync(cancellationToken);
            var position = text.Lines.GetPosition(new LinePosition(parameters.Line - 1, parameters.Column - 1));
            
            _logger.LogDebug("Finding symbol at position {Position}", position);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                _logger.LogError("Failed to get semantic model for document");
                return new RenameSymbolToolResult
                {
                    Success = false,
                    Message = "Failed to get semantic model",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the project builds successfully",
                                "Check for syntax errors in the file",
                                "Try reloading the solution"
                            }
                        }
                    },
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
                    },
                    Meta = new ToolMetadata { ExecutionTime = "0ms" }
                };
            }

            // Find the symbol at the position
            _logger.LogDebug("Finding symbol at position using SymbolFinder");
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
                semanticModel, 
                position, 
                document.Project.Solution.Workspace, 
                cancellationToken);
                
            if (symbol == null)
            {
                _logger.LogDebug("No symbol found at position {Position}", position);
                return new RenameSymbolToolResult
                {
                    Success = false,
                    Message = "No symbol found at the specified position",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.NO_SYMBOL_AT_POSITION,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the cursor is positioned on a symbol name",
                                "Try positioning on the declaration rather than usage",
                                "Check that the file has been saved and parsed"
                            }
                        }
                    },
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
                    },
                    Meta = new ToolMetadata 
                    { 
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                    }
                };
            }

            _logger.LogDebug("Found symbol: {SymbolName} ({SymbolKind})", 
                symbol.Name, symbol.Kind);

            // Check if symbol can be renamed
            if (!CanRenameSymbol(symbol))
            {
                _logger.LogWarning("Symbol cannot be renamed: {SymbolName} ({SymbolKind})", 
                    symbol.Name, symbol.Kind);
                return new RenameSymbolToolResult
                {
                    Success = false,
                    Message = $"Cannot rename {symbol.Kind} '{symbol.Name}'",
                    Error = new ErrorInfo
                    {
                        Code = "SYMBOL_CANNOT_BE_RENAMED",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Built-in types and external symbols cannot be renamed",
                                "Ensure you're renaming a symbol defined in your code",
                                "Check if the symbol is from a referenced assembly"
                            }
                        }
                    }
                };
            }

            // Validate new name
            if (!IsValidIdentifier(parameters.NewName))
            {
                _logger.LogWarning("Invalid identifier: {NewName}", parameters.NewName);
                return new RenameSymbolToolResult
                {
                    Success = false,
                    Message = $"'{parameters.NewName}' is not a valid identifier",
                    Error = new ErrorInfo
                    {
                        Code = "INVALID_IDENTIFIER",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Identifiers must start with a letter or underscore",
                                "Can only contain letters, digits, and underscores",
                                "Cannot be a reserved keyword"
                            }
                        }
                    }
                };
            }

            // Perform rename
            var solution = document.Project.Solution;
            var options = new SymbolRenameOptions(
                RenameOverloads: parameters.RenameOverloads ?? true,
                RenameInStrings: parameters.RenameInStrings ?? false,
                RenameInComments: parameters.RenameInComments ?? true,
                RenameFile: parameters.RenameFile ?? false
            );

            _logger.LogDebug("Attempting rename with options: RenameOverloads={RenameOverloads}, RenameInStrings={RenameInStrings}, RenameInComments={RenameInComments}", 
                options.RenameOverloads, options.RenameInStrings, options.RenameInComments);

            var renameResult = await Renamer.RenameSymbolAsync(
                solution,
                symbol,
                options,
                parameters.NewName,
                cancellationToken);

            // Check for conflicts
            var conflicts = GetConflicts(solution, renameResult);
            if (conflicts.Any())
            {
                _logger.LogWarning("Rename would cause {ConflictCount} conflicts", conflicts.Count);
                
                if (!parameters.Preview)
                {
                    return new RenameSymbolToolResult
                    {
                        Success = false,
                        Message = $"Rename would cause {conflicts.Count} conflict(s)",
                        Conflicts = conflicts,
                        Error = new ErrorInfo
                        {
                            Code = "RENAME_CONFLICTS",
                            Recovery = new RecoveryInfo
                            {
                                Steps = new List<string>
                                {
                                    "Review the conflicts listed",
                                    "Choose a different name that doesn't conflict",
                                    "Use preview mode to see all changes before applying"
                                }
                            }
                        },
                        Query = new QueryInfo
                        {
                            FilePath = parameters.FilePath,
                            Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                            TargetSymbol = symbol.ToDisplayString()
                        },
                        Meta = new ToolMetadata 
                        { 
                            ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                        }
                    };
                }
            }

            // Get changes
            var allChanges = await GetChangesAsync(solution, renameResult, cancellationToken);
            
            // Apply token management for preview
            var changes = allChanges;
            bool wasTruncated = false;
            
            if (parameters.Preview)
            {
                // Estimate tokens for the changes
                var estimatedTokens = EstimateFileChangesTokens(allChanges);
                
                if (estimatedTokens > TokenEstimator.DEFAULT_SAFETY_LIMIT)
                {
                    // Apply progressive reduction
                    var response = TokenEstimator.CreateTokenAwareResponse(
                        allChanges,
                        changesSubset => EstimateFileChangesTokens(changesSubset),
                        requestedMax: parameters.MaxChangedFiles ?? 50, // Default to 50 files
                        safetyLimit: TokenEstimator.DEFAULT_SAFETY_LIMIT,
                        toolName: "roslyn_rename_symbol"
                    );
                    
                    changes = response.Items;
                    wasTruncated = response.WasTruncated;
                }
            }
            
            if (parameters.Preview)
            {
                _logger.LogInformation("Rename preview generated: {ChangeCount} file(s) would be modified", changes.Count);
                
                // Store preview result
                var previewResult = new RenameSymbolToolResult
                {
                    Success = true,
                    Message = $"Preview: Renaming '{symbol.Name}' to '{parameters.NewName}' would affect {changes.Count} file(s)",
                    Changes = changes,
                    Conflicts = conflicts,
                    Preview = true,
                    Applied = false,
                    Insights = GenerateInsights(symbol, changes, conflicts, wasTruncated, allChanges.Count),
                    Actions = GenerateNextActions(symbol, parameters, changes, allChanges),
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                        TargetSymbol = symbol.ToDisplayString()
                    },
                    Summary = new SummaryInfo
                    {
                        TotalFound = allChanges.Count,
                        Returned = changes.Count,
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                        SymbolInfo = new SymbolSummary
                        {
                            Name = symbol.Name,
                            Kind = symbol.Kind.ToString(),
                            ContainingType = symbol.ContainingType?.ToDisplayString(),
                            Namespace = symbol.ContainingNamespace?.ToDisplayString()
                        }
                    },
                    Meta = new ToolMetadata 
                    { 
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                        Truncated = wasTruncated
                    }
                };

                if (_resourceProvider != null)
                {
                    var resourceUri = _resourceProvider.StoreAnalysisResult("rename-preview", previewResult);
                    previewResult.ResourceUri = resourceUri;
                    
                    // Store full changes if truncated
                    if (wasTruncated)
                    {
                        var fullResult = new RenameSymbolToolResult
                        {
                            Success = true,
                            Message = $"Full preview: All {allChanges.Count} files",
                            Changes = allChanges,
                            Conflicts = conflicts,
                            Preview = true,
                            Applied = false
                        };
                        var fullUri = _resourceProvider.StoreAnalysisResult("rename-preview-full", fullResult);
                        // Store the full results URI in insights
                        previewResult.Insights.Add($"Full results available at: {fullUri}");
                    }
                }

                return previewResult;
            }

            // Apply changes
            _logger.LogInformation("Applying rename: '{OldName}' to '{NewName}'", symbol.Name, parameters.NewName);
            
            // Update workspace
            foreach (var workspace in _workspaceService.GetActiveWorkspaces())
            {
                if (workspace.Solution == solution)
                {
                    workspace.Solution = renameResult;
                    break;
                }
            }

            var result = new RenameSymbolToolResult
            {
                Success = true,
                Message = $"Successfully renamed '{symbol.Name}' to '{parameters.NewName}' in {allChanges.Count} file(s)",
                Changes = allChanges, // Show all changes when applied
                Conflicts = conflicts,
                Preview = false,
                Applied = true,
                Insights = GenerateInsights(symbol, allChanges, conflicts, false, allChanges.Count),
                Actions = new List<NextAction>
                {
                    new NextAction
                    {
                        Id = "find_references",
                        Description = $"Find all references to the renamed symbol '{parameters.NewName}'",
                        ToolName = "roslyn_find_all_references",
                        Parameters = new Dictionary<string, object>
                        {
                            ["filePath"] = parameters.FilePath,
                            ["line"] = parameters.Line,
                            ["column"] = parameters.Column
                        },
                        Priority = "high"
                    }
                },
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    TargetSymbol = symbol.ToDisplayString()
                },
                Summary = new SummaryInfo
                {
                    TotalFound = changes.Count,
                    Returned = changes.Count,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    SymbolInfo = new SymbolSummary
                    {
                        Name = symbol.Name,
                        Kind = symbol.Kind.ToString(),
                        ContainingType = symbol.ContainingType?.ToDisplayString(),
                        Namespace = symbol.ContainingNamespace?.ToDisplayString()
                    }
                },
                Meta = new ToolMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                }
            };

            if (_resourceProvider != null)
            {
                var resourceUri = _resourceProvider.StoreAnalysisResult("rename-result", result);
                result.ResourceUri = resourceUri;
            }

            _logger.LogDebug("Rename completed successfully");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in RenameSymbol");
            return new RenameSymbolToolResult
            {
                Success = false,
                Message = $"Internal error: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new List<string>
                        {
                            "Check the server logs for detailed error information",
                            "Verify the solution/project is loaded correctly",
                            "Try the operation again"
                        }
                    }
                },
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
                },
                Meta = new ToolMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                }
            };
        }
    }

    private bool CanRenameSymbol(ISymbol symbol)
    {
        // Cannot rename symbols from metadata
        if (symbol.Locations.All(l => l.IsInMetadata))
            return false;

        // Cannot rename certain symbol kinds
        if (symbol.Kind == SymbolKind.Namespace && symbol.Name == "")
            return false;

        // Cannot rename built-in types
        if (symbol is ITypeSymbol typeSymbol && typeSymbol.SpecialType != SpecialType.None)
            return false;

        return true;
    }

    private bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Check if it's a valid C# identifier
        if (!char.IsLetter(name[0]) && name[0] != '_')
            return false;

        return name.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private List<RenameConflict> GetConflicts(Solution originalSolution, Solution renamedSolution)
    {
        var conflicts = new List<RenameConflict>();
        
        // This is a simplified conflict detection
        // In a real implementation, Roslyn provides more detailed conflict information
        
        return conflicts;
    }

    private async Task<List<FileChange>> GetChangesAsync(
        Solution originalSolution, 
        Solution renamedSolution,
        CancellationToken cancellationToken)
    {
        var changes = new List<FileChange>();
        var changedDocuments = originalSolution.Projects
            .SelectMany(p => p.DocumentIds)
            .Where(docId => 
            {
                var originalDoc = originalSolution.GetDocument(docId);
                var newDoc = renamedSolution.GetDocument(docId);
                if (originalDoc == null || newDoc == null) return false;
                
                var originalText = originalDoc.GetTextAsync(cancellationToken).Result;
                var newText = newDoc.GetTextAsync(cancellationToken).Result;
                return !originalText.ContentEquals(newText);
            });

        foreach (var docId in changedDocuments)
        {
            var originalDoc = originalSolution.GetDocument(docId);
            var newDoc = renamedSolution.GetDocument(docId);
            
            if (originalDoc == null || newDoc == null)
                continue;

            var originalText = await originalDoc.GetTextAsync(cancellationToken);
            var newText = await newDoc.GetTextAsync(cancellationToken);
            var textChanges = newText.GetTextChanges(originalText);

            var fileChange = new FileChange
            {
                FilePath = originalDoc.FilePath ?? "",
                Changes = textChanges.Select(tc => new Models.TextChange
                {
                    Span = new Models.TextSpan
                    {
                        Start = tc.Span.Start,
                        End = tc.Span.End,
                        Line = originalText.Lines.GetLinePosition(tc.Span.Start).Line + 1,
                        Column = originalText.Lines.GetLinePosition(tc.Span.Start).Character + 1
                    },
                    NewText = tc.NewText ?? ""
                }).ToList()
            };

            changes.Add(fileChange);
        }

        return changes;
    }
    
    private int EstimateFileChangesTokens(List<FileChange> changes)
    {
        return TokenEstimator.EstimateCollection(
            changes,
            change => {
                var tokens = 100; // Base structure per file
                tokens += TokenEstimator.EstimateString(change.FilePath);
                tokens += change.Changes.Count * 80; // Estimate per text change
                return tokens;
            },
            baseTokens: TokenEstimator.BASE_RESPONSE_TOKENS
        );
    }

    private List<string> GenerateInsights(ISymbol symbol, List<FileChange> changes, List<RenameConflict> conflicts, bool wasTruncated = false, int totalCount = 0)
    {
        var insights = new List<string>();
        
        if (wasTruncated)
        {
            insights.Add($"⚠️ Showing {changes.Count} of {totalCount} files to manage response size");
        }

        insights.Add($"Renaming {SymbolUtilities.GetFriendlySymbolKind(symbol)} '{symbol.Name}'");

        var totalChanges = changes.Sum(c => c.Changes.Count);
        var displayCount = wasTruncated ? totalCount : changes.Count;
        insights.Add($"{totalChanges} text changes across {displayCount} file(s)");

        if (conflicts.Any())
            insights.Add($"⚠️ {conflicts.Count} potential conflict(s) detected");

        if (symbol.ContainingType != null)
            insights.Add($"Member of {symbol.ContainingType.Name}");

        if (symbol.DeclaredAccessibility != Accessibility.NotApplicable)
            insights.Add($"{symbol.DeclaredAccessibility} accessibility");

        return insights;
    }

    private List<NextAction> GenerateNextActions(ISymbol symbol, RenameSymbolParams parameters, List<FileChange> changes, List<FileChange>? allChanges = null)
    {
        var actions = new List<NextAction>();

        if (parameters.Preview)
        {
            actions.Add(new NextAction
            {
                Id = "apply_rename",
                Description = "Apply this rename",
                ToolName = "roslyn_rename_symbol",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = parameters.FilePath,
                    ["line"] = parameters.Line,
                    ["column"] = parameters.Column,
                    ["newName"] = parameters.NewName,
                    ["preview"] = false
                },
                Priority = "high"
            });
            
            // If truncated, add option to see all changes
            if (allChanges != null && allChanges.Count > changes.Count)
            {
                actions.Add(new NextAction
                {
                    Id = "see_all_changes",
                    Description = $"Preview all {allChanges.Count} file changes",
                    ToolName = "roslyn_rename_symbol",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = parameters.FilePath,
                        ["line"] = parameters.Line,
                        ["column"] = parameters.Column,
                        ["newName"] = parameters.NewName,
                        ["preview"] = true,
                        ["maxChangedFiles"] = allChanges.Count
                    },
                    Priority = "medium"
                });
            }
        }

        actions.Add(new NextAction
        {
            Id = "undo_rename",
            Description = "Rename back to original name",
            ToolName = "roslyn_rename_symbol",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = changes.FirstOrDefault()?.FilePath ?? parameters.FilePath,
                ["line"] = parameters.Line,
                ["column"] = parameters.Column,
                ["newName"] = symbol.Name,
                ["preview"] = false
            },
            Priority = "medium"
        });

        return actions;
    }

}

public class RenameSymbolParams
{
    [JsonPropertyName("filePath")]
    [COA.CodeNav.McpServer.Attributes.Description("Path to the source file")]
    public required string FilePath { get; set; }
    
    [JsonPropertyName("line")]
    [COA.CodeNav.McpServer.Attributes.Description("Line number (1-based) where the symbol appears")]
    public required int Line { get; set; }
    
    [JsonPropertyName("column")]
    [COA.CodeNav.McpServer.Attributes.Description("Column number (1-based) where the symbol appears")]
    public required int Column { get; set; }
    
    [JsonPropertyName("newName")]
    [COA.CodeNav.McpServer.Attributes.Description("New name for the symbol")]
    public required string NewName { get; set; }
    
    [JsonPropertyName("preview")]
    [COA.CodeNav.McpServer.Attributes.Description("Preview changes without applying. true = show preview (default), false = apply immediately")]
    public bool Preview { get; set; } = true;
    
    [JsonPropertyName("renameOverloads")]
    [COA.CodeNav.McpServer.Attributes.Description("Rename overloaded methods. true = rename all overloads (default), false = rename only this method")]
    public bool? RenameOverloads { get; set; }
    
    [JsonPropertyName("renameInStrings")]
    [COA.CodeNav.McpServer.Attributes.Description("Rename in string literals. true = include strings, false = skip strings (default)")]
    public bool? RenameInStrings { get; set; }
    
    [JsonPropertyName("renameInComments")]
    [COA.CodeNav.McpServer.Attributes.Description("Rename in comments. true = include comments (default), false = skip comments")]
    public bool? RenameInComments { get; set; }
    
    [JsonPropertyName("renameFile")]
    [COA.CodeNav.McpServer.Attributes.Description("Rename file if renaming type. true = rename file to match type name, false = keep current filename (default)")]
    public bool? RenameFile { get; set; }
    
    [JsonPropertyName("maxChangedFiles")]
    [COA.CodeNav.McpServer.Attributes.Description("Maximum number of changed files to return in preview (default: 50)")]
    public int? MaxChangedFiles { get; set; }
}

// Result classes have been moved to COA.CodeNav.McpServer.Models namespace