using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

using System.Linq;

[McpServerToolType]
public class FindAllReferencesTool
{
    private readonly ILogger<FindAllReferencesTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;

    public FindAllReferencesTool(
        ILogger<FindAllReferencesTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
    }

    [McpServerTool(Name = "roslyn_find_all_references")]
    [Description("Find all references to a symbol at a given position in a file")]
    public async Task<FindAllReferencesResult> ExecuteAsync(FindAllReferencesParams parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Finding all references at {FilePath}:{Line}:{Column}", 
                parameters.FilePath, parameters.Line, parameters.Column);

            // Get the document
            var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                return new FindAllReferencesResult
                {
                    Found = false,
                    Message = $"Document not found in workspace: {parameters.FilePath}"
                };
            }

            // Calculate position
            var sourceText = await document.GetTextAsync(cancellationToken);
            var position = sourceText.Lines.GetPosition(new LinePosition(parameters.Line - 1, parameters.Column - 1));

            // Get semantic model
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                return new FindAllReferencesResult
                {
                    Found = false,
                    Message = "Failed to get semantic model"
                };
            }

            // Find symbol at position
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, document.Project.Solution.Workspace, cancellationToken);
            if (symbol == null)
            {
                return new FindAllReferencesResult
                {
                    Found = false,
                    Message = "No symbol found at the specified position"
                };
            }

            _logger.LogDebug("Found symbol: {SymbolName} ({SymbolKind})", symbol.Name, symbol.Kind);

            // Find all references
            var references = await SymbolFinder.FindReferencesAsync(
                symbol, 
                document.Project.Solution, 
                cancellationToken);

            var locations = new List<ReferenceLocation>();

            foreach (var referencedSymbol in references)
            {
                foreach (var location in referencedSymbol.Locations)
                {
                    var refDoc = location.Document;
                    var span = location.Location.SourceSpan;
                    var lineSpan = (await refDoc.GetTextAsync(cancellationToken)).Lines.GetLinePositionSpan(span);

                    locations.Add(new ReferenceLocation
                    {
                        FilePath = refDoc.FilePath ?? "<unknown>",
                        Line = lineSpan.Start.Line + 1,
                        Column = lineSpan.Start.Character + 1,
                        EndLine = lineSpan.End.Line + 1,
                        EndColumn = lineSpan.End.Character + 1,
                        Kind = location.Location.IsInSource ? "reference" : "metadata",
                        Text = (await refDoc.GetTextAsync(cancellationToken)).GetSubText(span).ToString()
                    });
                }
            }

            _logger.LogInformation("Found {Count} references to {SymbolName}", locations.Count, symbol.Name);

            // Generate next actions
            var nextActions = GenerateNextActions(symbol, locations);

            return new FindAllReferencesResult
            {
                Found = true,
                SymbolName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                SymbolKind = symbol.Kind.ToString(),
                TotalCount = locations.Count,
                Locations = locations.OrderBy(l => l.FilePath).ThenBy(l => l.Line).ToList(),
                Message = $"Found {locations.Count} reference(s)",
                NextActions = nextActions
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding references");
            return new FindAllReferencesResult
            {
                Found = false,
                Message = $"Error finding references: {ex.Message}"
            };
        }
    }

    private List<NextAction> GenerateNextActions(ISymbol symbol, List<ReferenceLocation> locations)
    {
        var actions = new List<NextAction>();

        // If this is a method or property, suggest going to its definition
        if (symbol.Kind == SymbolKind.Method || symbol.Kind == SymbolKind.Property || 
            symbol.Kind == SymbolKind.Field || symbol.Kind == SymbolKind.Event)
        {
            var containingType = symbol.ContainingType;
            if (containingType != null && containingType.Locations.Any(l => l.IsInSource))
            {
                var location = containingType.Locations.First(l => l.IsInSource);
                var lineSpan = location.GetLineSpan();
                
                actions.Add(new NextAction
                {
                    Id = "goto_containing_type",
                    Description = $"Go to containing type '{containingType.Name}'",
                    ToolName = "roslyn_goto_definition",
                    Parameters = new
                    {
                        filePath = lineSpan.Path,
                        line = lineSpan.StartLinePosition.Line + 1,
                        column = lineSpan.StartLinePosition.Character + 1
                    },
                    Priority = "high"
                });
            }
        }

        // If we found references in multiple files, suggest exploring a specific file
        var filesWithReferences = locations.Select(l => l.FilePath).Distinct().ToList();
        if (filesWithReferences.Count > 3)
        {
            var topFile = locations.GroupBy(l => l.FilePath)
                .OrderByDescending(g => g.Count())
                .First();

            actions.Add(new NextAction
            {
                Id = "filter_to_file",
                Description = $"Focus on {Path.GetFileName(topFile.Key)} ({topFile.Count()} references)",
                ToolName = "roslyn_find_all_references",
                Parameters = new
                {
                    filePath = topFile.Key,
                    line = topFile.First().Line,
                    column = topFile.First().Column
                },
                Priority = "medium"
            });
        }

        // If this is a type, suggest finding derived types or implementations
        if (symbol.Kind == SymbolKind.NamedType)
        {
            var namedType = (INamedTypeSymbol)symbol;
            if (namedType.TypeKind == TypeKind.Interface)
            {
                actions.Add(new NextAction
                {
                    Id = "find_implementations",
                    Description = "Find implementations of this interface",
                    ToolName = "roslyn_find_implementations", // Future tool
                    Parameters = new
                    {
                        typeName = symbol.ToDisplayString()
                    },
                    Priority = "high"
                });
            }
            else if (namedType.TypeKind == TypeKind.Class && !namedType.IsSealed)
            {
                actions.Add(new NextAction
                {
                    Id = "find_derived",
                    Description = "Find derived classes",
                    ToolName = "roslyn_find_derived_types", // Future tool
                    Parameters = new
                    {
                        typeName = symbol.ToDisplayString()
                    },
                    Priority = "medium"
                });
            }
        }

        // Always suggest hover for more information
        if (locations.Any())
        {
            var firstLoc = locations.First();
            actions.Add(new NextAction
            {
                Id = "hover_info",
                Description = "Get hover information for this symbol",
                ToolName = "roslyn_hover",
                Parameters = new
                {
                    filePath = firstLoc.FilePath,
                    line = firstLoc.Line,
                    column = firstLoc.Column
                },
                Priority = "low"
            });
        }

        return actions;
    }
}

public class FindAllReferencesParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file")]
    public required string FilePath { get; set; }

    [JsonPropertyName("line")]
    [Description("Line number (1-based)")]
    public required int Line { get; set; }

    [JsonPropertyName("column")]
    [Description("Column number (1-based)")]
    public required int Column { get; set; }
}

public class FindAllReferencesResult
{
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("symbolName")]
    public string? SymbolName { get; set; }

    [JsonPropertyName("symbolKind")]
    public string? SymbolKind { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("locations")]
    public List<ReferenceLocation>? Locations { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("nextActions")]
    public List<NextAction>? NextActions { get; set; }
}

public class ReferenceLocation
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

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}