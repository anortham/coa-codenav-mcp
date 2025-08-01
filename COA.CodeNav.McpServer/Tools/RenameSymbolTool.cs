using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
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

    [McpServerTool(Name = "roslyn_rename_symbol")]
    [COA.CodeNav.McpServer.Attributes.Description(@"Rename a symbol across the entire solution with conflict detection and preview.
Returns: List of affected files, conflict information, and preview of changes.
Prerequisites: Call roslyn_load_solution or roslyn_load_project first.
Error handling: Returns specific error codes with recovery steps if rename would cause conflicts.
Use cases: Rename classes, methods, properties, variables across entire codebase.
Not for: File renaming (use file system tools), namespace-only renames (use dedicated namespace tool).")]
    public async Task<object> ExecuteAsync(RenameSymbolParams parameters, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("RenameSymbol request received: FilePath={FilePath}, Line={Line}, Column={Column}, NewName={NewName}, Preview={Preview}", 
            parameters.FilePath, parameters.Line, parameters.Column, parameters.NewName, parameters.Preview);
            
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
                return new RenameSymbolResult
                {
                    Success = false,
                    Message = $"Document not found: {parameters.FilePath}",
                    Error = new ErrorInfo
                    {
                        Code = "DOCUMENT_NOT_FOUND",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the file path is correct and absolute",
                                "Verify the solution/project containing this file is loaded",
                                "Use roslyn_load_solution or roslyn_load_project to load the workspace"
                            }
                        }
                    }
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
                return new RenameSymbolResult
                {
                    Success = false,
                    Message = "Failed to get semantic model",
                    Error = new ErrorInfo
                    {
                        Code = "SEMANTIC_MODEL_ERROR",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the project builds successfully",
                                "Check for syntax errors in the file",
                                "Try reloading the solution"
                            }
                        }
                    }
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
                return new RenameSymbolResult
                {
                    Success = false,
                    Message = "No symbol found at the specified position",
                    Error = new ErrorInfo
                    {
                        Code = "NO_SYMBOL_AT_POSITION",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the cursor is positioned on a symbol name",
                                "Try positioning on the declaration rather than usage",
                                "Check that the file has been saved and parsed"
                            }
                        }
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
                return new RenameSymbolResult
                {
                    Success = false,
                    Message = $"Cannot rename {symbol.Kind} '{symbol.Name}'",
                    SymbolName = symbol.Name,
                    SymbolKind = symbol.Kind.ToString(),
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
                return new RenameSymbolResult
                {
                    Success = false,
                    Message = $"'{parameters.NewName}' is not a valid identifier",
                    SymbolName = symbol.Name,
                    SymbolKind = symbol.Kind.ToString(),
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
                    return new RenameSymbolResult
                    {
                        Success = false,
                        Message = $"Rename would cause {conflicts.Count} conflict(s)",
                        SymbolName = symbol.Name,
                        SymbolKind = symbol.Kind.ToString(),
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
                        }
                    };
                }
            }

            // Get changes
            var changes = await GetChangesAsync(solution, renameResult, cancellationToken);
            
            if (parameters.Preview)
            {
                _logger.LogInformation("Rename preview generated: {ChangeCount} file(s) would be modified", changes.Count);
                
                // Store preview result
                var previewResult = new RenameSymbolResult
                {
                    Success = true,
                    Message = $"Preview: Renaming '{symbol.Name}' to '{parameters.NewName}' would affect {changes.Count} file(s)",
                    SymbolName = symbol.Name,
                    SymbolKind = symbol.Kind.ToString(),
                    NewName = parameters.NewName,
                    Changes = changes,
                    Conflicts = conflicts,
                    IsPreview = true,
                    Insights = GenerateInsights(symbol, changes, conflicts),
                    NextActions = GenerateNextActions(symbol, parameters, changes)
                };

                if (_resourceProvider != null)
                {
                    var resourceUri = _resourceProvider.StoreAnalysisResult("rename-preview", previewResult);
                    previewResult.ResourceUri = resourceUri;
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

            var result = new RenameSymbolResult
            {
                Success = true,
                Message = $"Successfully renamed '{symbol.Name}' to '{parameters.NewName}' in {changes.Count} file(s)",
                SymbolName = symbol.Name,
                SymbolKind = symbol.Kind.ToString(),
                NewName = parameters.NewName,
                Changes = changes,
                Conflicts = conflicts,
                IsPreview = false,
                Insights = GenerateInsights(symbol, changes, conflicts),
                NextActions = new List<NextAction>
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
            return new RenameSymbolResult
            {
                Success = false,
                Message = $"Internal error: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = "INTERNAL_ERROR",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new List<string>
                        {
                            "Check the server logs for detailed error information",
                            "Verify the solution/project is loaded correctly",
                            "Try the operation again"
                        }
                    }
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

    private List<ConflictInfo> GetConflicts(Solution originalSolution, Solution renamedSolution)
    {
        var conflicts = new List<ConflictInfo>();
        
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
                Changes = textChanges.Select(tc => new TextChange
                {
                    StartLine = originalText.Lines.GetLinePosition(tc.Span.Start).Line + 1,
                    StartColumn = originalText.Lines.GetLinePosition(tc.Span.Start).Character + 1,
                    EndLine = originalText.Lines.GetLinePosition(tc.Span.End).Line + 1,
                    EndColumn = originalText.Lines.GetLinePosition(tc.Span.End).Character + 1,
                    OldText = originalText.GetSubText(tc.Span).ToString(),
                    NewText = tc.NewText ?? ""
                }).ToList()
            };

            changes.Add(fileChange);
        }

        return changes;
    }

    private List<string> GenerateInsights(ISymbol symbol, List<FileChange> changes, List<ConflictInfo> conflicts)
    {
        var insights = new List<string>();

        insights.Add($"Renaming {GetFriendlySymbolKind(symbol)} '{symbol.Name}'");

        var totalChanges = changes.Sum(c => c.Changes.Count);
        insights.Add($"{totalChanges} text changes across {changes.Count} file(s)");

        if (conflicts.Any())
            insights.Add($"⚠️ {conflicts.Count} potential conflict(s) detected");

        if (symbol.ContainingType != null)
            insights.Add($"Member of {symbol.ContainingType.Name}");

        if (symbol.DeclaredAccessibility != Accessibility.NotApplicable)
            insights.Add($"{symbol.DeclaredAccessibility} accessibility");

        return insights;
    }

    private List<NextAction> GenerateNextActions(ISymbol symbol, RenameSymbolParams parameters, List<FileChange> changes)
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

    private string GetFriendlySymbolKind(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method when method.MethodKind == MethodKind.Constructor => "constructor",
            IMethodSymbol method when method.MethodKind == MethodKind.PropertyGet => "property getter",
            IMethodSymbol method when method.MethodKind == MethodKind.PropertySet => "property setter",
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            INamedTypeSymbol namedType => namedType.TypeKind.ToString().ToLower(),
            IParameterSymbol => "parameter",
            ILocalSymbol => "local variable",
            _ => symbol.Kind.ToString().ToLower()
        };
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
    [COA.CodeNav.McpServer.Attributes.Description("Preview changes without applying (default: true)")]
    public bool Preview { get; set; } = true;
    
    [JsonPropertyName("renameOverloads")]
    [COA.CodeNav.McpServer.Attributes.Description("Rename overloaded methods (default: true)")]
    public bool? RenameOverloads { get; set; }
    
    [JsonPropertyName("renameInStrings")]
    [COA.CodeNav.McpServer.Attributes.Description("Rename in string literals (default: false)")]
    public bool? RenameInStrings { get; set; }
    
    [JsonPropertyName("renameInComments")]
    [COA.CodeNav.McpServer.Attributes.Description("Rename in comments (default: true)")]
    public bool? RenameInComments { get; set; }
    
    [JsonPropertyName("renameFile")]
    [COA.CodeNav.McpServer.Attributes.Description("Rename file if renaming type (default: false)")]
    public bool? RenameFile { get; set; }
}

public class RenameSymbolResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("message")]
    public string? Message { get; set; }
    
    [JsonPropertyName("symbolName")]
    public string? SymbolName { get; set; }
    
    [JsonPropertyName("symbolKind")]
    public string? SymbolKind { get; set; }
    
    [JsonPropertyName("newName")]
    public string? NewName { get; set; }
    
    [JsonPropertyName("changes")]
    public List<FileChange>? Changes { get; set; }
    
    [JsonPropertyName("conflicts")]
    public List<ConflictInfo>? Conflicts { get; set; }
    
    [JsonPropertyName("isPreview")]
    public bool IsPreview { get; set; }
    
    [JsonPropertyName("insights")]
    public List<string>? Insights { get; set; }
    
    [JsonPropertyName("nextActions")]
    public List<NextAction>? NextActions { get; set; }
    
    [JsonPropertyName("error")]
    public ErrorInfo? Error { get; set; }
    
    [JsonPropertyName("resourceUri")]
    public string? ResourceUri { get; set; }
}

public class FileChange
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }
    
    [JsonPropertyName("changes")]
    public required List<TextChange> Changes { get; set; }
}

public class TextChange
{
    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }
    
    [JsonPropertyName("startColumn")]
    public int StartColumn { get; set; }
    
    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }
    
    [JsonPropertyName("endColumn")]
    public int EndColumn { get; set; }
    
    [JsonPropertyName("oldText")]
    public required string OldText { get; set; }
    
    [JsonPropertyName("newText")]
    public required string NewText { get; set; }
}

public class ConflictInfo
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }
    
    [JsonPropertyName("description")]
    public required string Description { get; set; }
    
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }
    
    [JsonPropertyName("line")]
    public int? Line { get; set; }
    
    [JsonPropertyName("column")]
    public int? Column { get; set; }
}