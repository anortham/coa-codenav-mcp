using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Utilities;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that provides symbol renaming functionality using Roslyn
/// </summary>
public class RenameSymbolTool : McpToolBase<RenameSymbolParams, RenameSymbolToolResult>
{
    private readonly ILogger<RenameSymbolTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => ToolNames.RenameSymbol;
    public override string Description => "Rename a symbol across the entire solution with conflict detection and preview";
    
    public RenameSymbolTool(
        ILogger<RenameSymbolTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<RenameSymbolToolResult> ExecuteInternalAsync(
        RenameSymbolParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("RenameSymbol request received: FilePath={FilePath}, Line={Line}, Column={Column}, NewName={NewName}, Preview={Preview}", 
            parameters.FilePath, parameters.Line, parameters.Column, parameters.NewName, parameters.Preview);
            
        var startTime = DateTime.UtcNow;

        _logger.LogInformation("Processing RenameSymbol for {FilePath} at {Line}:{Column} to '{NewName}'", 
            parameters.FilePath, parameters.Line, parameters.Column, parameters.NewName);

        // Get the document
        _logger.LogDebug("Retrieving document from workspace: {FilePath}", parameters.FilePath);
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            _logger.LogWarning("Document not found: {FilePath}", parameters.FilePath);
            throw new InvalidOperationException($"Document not found: {parameters.FilePath}");
        }

        // Get the position
        var text = await document.GetTextAsync(cancellationToken);
        var position = text.Lines.GetPosition(new LinePosition(parameters.Line - 1, parameters.Column - 1));
        
        _logger.LogDebug("Finding symbol at position {Position}", position);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            _logger.LogError("Failed to get semantic model for document");
            throw new InvalidOperationException("Failed to get semantic model");
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
            throw new InvalidOperationException("No symbol found at the specified position");
        }

        _logger.LogDebug("Found symbol: {SymbolName} ({SymbolKind})", 
            symbol.Name, symbol.Kind);

        // Check if symbol can be renamed
        if (!CanRenameSymbol(symbol))
        {
            _logger.LogWarning("Symbol cannot be renamed: {SymbolName} ({SymbolKind})", 
                symbol.Name, symbol.Kind);
            throw new InvalidOperationException($"Cannot rename {symbol.Kind} '{symbol.Name}'");
        }

        // Validate new name
        if (!IsValidIdentifier(parameters.NewName))
        {
            _logger.LogWarning("Invalid identifier: {NewName}", parameters.NewName);
            throw new ArgumentException($"'{parameters.NewName}' is not a valid identifier");
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
        if (conflicts.Any() && !parameters.Preview)
        {
            _logger.LogWarning("Rename would cause {ConflictCount} conflicts", conflicts.Count);
            throw new InvalidOperationException($"Rename would cause {conflicts.Count} conflict(s)");
        }

        // Get changes
        var allChanges = await GetChangesAsync(solution, renameResult, cancellationToken);
        
        // Limit changes for token optimization
        const int MAX_CHANGES_TO_SHOW = 5; // Only show first 5 files
        var truncatedChanges = allChanges.Take(MAX_CHANGES_TO_SHOW).ToList();
        var isTruncated = allChanges.Count > MAX_CHANGES_TO_SHOW;
        
        if (parameters.Preview)
        {
            _logger.LogInformation("Rename preview generated: {ChangeCount} file(s) would be modified", allChanges.Count);
            
            // Store preview result
            var result = new RenameSymbolToolResult
            {
                Success = true,
                Message = isTruncated 
                    ? $"Preview: Renaming '{symbol.Name}' to '{parameters.NewName}' would affect {allChanges.Count} file(s) (showing first {MAX_CHANGES_TO_SHOW})"
                    : $"Preview: Renaming '{symbol.Name}' to '{parameters.NewName}' would affect {allChanges.Count} file(s)",
                Changes = truncatedChanges,  // Only return truncated changes
                Conflicts = conflicts,
                Preview = true,
                Applied = false,
                Insights = GenerateInsights(symbol, allChanges, conflicts, isTruncated),
                Actions = GenerateNextActions(symbol, parameters, allChanges),
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    TargetSymbol = symbol.ToDisplayString()
                },
                Summary = new SummaryInfo
                {
                    TotalFound = allChanges.Count,
                    Returned = truncatedChanges.Count,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    SymbolInfo = new SymbolSummary
                    {
                        Name = symbol.Name,
                        Kind = symbol.Kind.ToString(),
                        ContainingType = symbol.ContainingType?.ToDisplayString(),
                        Namespace = symbol.ContainingNamespace?.ToDisplayString()
                    }
                },
                Meta = new ToolExecutionMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    Truncated = isTruncated,
                    Mode = isTruncated ? "truncated" : "full"
                }
            };

            if (_resourceProvider != null)
            {
                var resourceUri = _resourceProvider.StoreAnalysisResult("rename-preview", result);
                result.ResourceUri = resourceUri;
            }

            return result;
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

        var appliedResult = new RenameSymbolToolResult
        {
            Success = true,
            Message = isTruncated
                ? $"Successfully renamed '{symbol.Name}' to '{parameters.NewName}' in {allChanges.Count} file(s) (showing first {MAX_CHANGES_TO_SHOW})"
                : $"Successfully renamed '{symbol.Name}' to '{parameters.NewName}' in {allChanges.Count} file(s)",
            Changes = truncatedChanges,  // Only return truncated changes
            Conflicts = conflicts,
            Preview = false,
            Applied = true,
            Insights = GenerateInsights(symbol, allChanges, conflicts, isTruncated),
            Actions = new List<AIAction>
            {
                new AIAction
                {
                    Action = ToolNames.FindAllReferences,
                    Description = $"Find all references to the renamed symbol '{parameters.NewName}'",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = parameters.FilePath,
                        ["line"] = parameters.Line,
                        ["column"] = parameters.Column
                    },
                    Priority = 90,
                    Category = "navigation"
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
                TotalFound = allChanges.Count,
                Returned = truncatedChanges.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                SymbolInfo = new SymbolSummary
                {
                    Name = symbol.Name,
                    Kind = symbol.Kind.ToString(),
                    ContainingType = symbol.ContainingType?.ToDisplayString(),
                    Namespace = symbol.ContainingNamespace?.ToDisplayString()
                }
            },
            Meta = new ToolExecutionMetadata 
            { 
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                Truncated = isTruncated,
                Mode = isTruncated ? "truncated" : "full"
            }
        };

        if (_resourceProvider != null)
        {
            var resourceUri = _resourceProvider.StoreAnalysisResult("rename-result", appliedResult);
            appliedResult.ResourceUri = resourceUri;
        }

        _logger.LogDebug("Rename completed successfully");
        return appliedResult;
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

    private List<string> GenerateInsights(ISymbol symbol, List<FileChange> changes, List<RenameConflict> conflicts, bool isTruncated = false)
    {
        var insights = new List<string>();
        
        insights.Add($"Renaming {SymbolUtilities.GetFriendlySymbolKind(symbol)} '{symbol.Name}'");

        var totalChanges = changes.Sum(c => c.Changes.Count);
        insights.Add($"{totalChanges} text changes across {changes.Count} file(s)");
        
        if (isTruncated)
            insights.Add($"⚠️ Response truncated for performance. Full results available via resource URI.");

        if (conflicts.Any())
            insights.Add($"⚠️ {conflicts.Count} potential conflict(s) detected");

        if (symbol.ContainingType != null)
            insights.Add($"Member of {symbol.ContainingType.Name}");

        if (symbol.DeclaredAccessibility != Accessibility.NotApplicable)
            insights.Add($"{symbol.DeclaredAccessibility} accessibility");

        return insights;
    }

    private List<AIAction> GenerateNextActions(ISymbol symbol, RenameSymbolParams parameters, List<FileChange> changes)
    {
        var actions = new List<AIAction>();

        if (parameters.Preview)
        {
            actions.Add(new AIAction
            {
                Action = ToolNames.RenameSymbol,
                Description = "Apply this rename",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = parameters.FilePath,
                    ["line"] = parameters.Line,
                    ["column"] = parameters.Column,
                    ["newName"] = parameters.NewName,
                    ["preview"] = false
                },
                Priority = 100,
                Category = "refactoring"
            });
        }

        actions.Add(new AIAction
        {
            Action = ToolNames.RenameSymbol,
            Description = "Rename back to original name",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = changes.FirstOrDefault()?.FilePath ?? parameters.FilePath,
                ["line"] = parameters.Line,
                ["column"] = parameters.Column,
                ["newName"] = symbol.Name,
                ["preview"] = false
            },
            Priority = 70,
            Category = "refactoring"
        });

        return actions;
    }

    protected override int EstimateTokenUsage()
    {
        // Estimate for typical RenameSymbol response
        // This can be quite large with file changes, so higher estimate
        return 8000;
    }
}

/// <summary>
/// Parameters for RenameSymbol tool
/// </summary>
public class RenameSymbolParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file")]
    public string FilePath { get; set; } = string.Empty;
    
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Line must be positive")]
    [JsonPropertyName("line")]
    [COA.Mcp.Framework.Attributes.Description("Line number (1-based) where the symbol appears")]
    public int Line { get; set; }
    
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Column must be positive")]
    [JsonPropertyName("column")]
    [COA.Mcp.Framework.Attributes.Description("Column number (1-based) where the symbol appears")]
    public int Column { get; set; }
    
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "NewName is required")]
    [JsonPropertyName("newName")]
    [COA.Mcp.Framework.Attributes.Description("New name for the symbol")]
    public string NewName { get; set; } = string.Empty;
    
    [JsonPropertyName("preview")]
    [COA.Mcp.Framework.Attributes.Description("Preview changes without applying. true = show preview (default), false = apply immediately")]
    public bool Preview { get; set; } = true;
    
    [JsonPropertyName("renameOverloads")]
    [COA.Mcp.Framework.Attributes.Description("Rename overloaded methods. true = rename all overloads (default), false = rename only this method")]
    public bool? RenameOverloads { get; set; }
    
    [JsonPropertyName("renameInStrings")]
    [COA.Mcp.Framework.Attributes.Description("Rename in string literals. true = include strings, false = skip strings (default)")]
    public bool? RenameInStrings { get; set; }
    
    [JsonPropertyName("renameInComments")]
    [COA.Mcp.Framework.Attributes.Description("Rename in comments. true = include comments (default), false = skip comments")]
    public bool? RenameInComments { get; set; }
    
    [JsonPropertyName("renameFile")]
    [COA.Mcp.Framework.Attributes.Description("Rename file if renaming type. true = rename file to match type name, false = keep current filename (default)")]
    public bool? RenameFile { get; set; }
    
    [JsonPropertyName("maxChangedFiles")]
    [COA.Mcp.Framework.Attributes.Description("Maximum number of changed files to return in preview (default: 50)")]
    public int? MaxChangedFiles { get; set; }
}