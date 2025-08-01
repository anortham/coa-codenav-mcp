using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that provides Go to Definition functionality using Roslyn
/// </summary>
[McpServerToolType]
public class GoToDefinitionTool : ITool
{
    private readonly ILogger<GoToDefinitionTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "roslyn_goto_definition";
    public string Description => "Navigate to the definition of a symbol at a given position in a file";

    public GoToDefinitionTool(
        ILogger<GoToDefinitionTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "roslyn_goto_definition")]
    [Description(@"Navigate to the definition of a symbol at a given position in a file.
Returns: Symbol location with file path, line, and column numbers; includes insights and next actions.
Prerequisites: Call roslyn_load_solution or roslyn_load_project first.
Error handling: Returns specific error codes (DOCUMENT_NOT_FOUND, NO_SYMBOL_AT_POSITION) with recovery steps.
Use cases: Jump to class/method definitions, explore type declarations, navigate to property definitions.
Not for: Finding usages (use roslyn_find_all_references), searching by name (use future symbol search tool).")]
    public async Task<object> ExecuteAsync(GoToDefinitionParams parameters, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("GoToDefinition request received: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);
            
        try
        {
            _logger.LogInformation("Processing GoToDefinition for {FilePath} at {Line}:{Column}", 
                parameters.FilePath, parameters.Line, parameters.Column);

            // Get the document
            _logger.LogDebug("Retrieving document from workspace: {FilePath}", parameters.FilePath);
            var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
                return new GoToDefinitionResult
                {
                    Found = false,
                    Message = $"Document not found in workspace: {parameters.FilePath}",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.DOCUMENT_NOT_FOUND,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the file path is correct and absolute",
                                "Verify the solution/project containing this file is loaded",
                                "Use roslyn_load_solution or roslyn_load_project to load the containing project"
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
                    }
                };
            }

            // Get the source text
            var sourceText = await document.GetTextAsync(cancellationToken);
            
            // Convert line/column to position (adjusting for 0-based indexing)
            var position = sourceText.Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
                parameters.Line - 1, 
                parameters.Column - 1));

            // Get semantic model
            _logger.LogDebug("Getting semantic model for document");
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                _logger.LogError("Failed to get semantic model for document: {FilePath}", parameters.FilePath);
                return new GoToDefinitionResult
                {
                    Found = false,
                    Message = "Could not get semantic model for document",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the project is fully loaded and compiled",
                                "Check for compilation errors in the project",
                                "Try reloading the solution"
                            }
                        }
                    }
                };
            }

            // Find symbol at position
            _logger.LogDebug("Searching for symbol at position {Position}", position);
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
                semanticModel, 
                position, 
                document.Project.Solution.Workspace, 
                cancellationToken);

            if (symbol == null)
            {
                _logger.LogDebug("No symbol found at position {Line}:{Column} in {FilePath}", 
                    parameters.Line, parameters.Column, parameters.FilePath);
                return new GoToDefinitionResult
                {
                    Found = false,
                    Message = "No symbol found at the specified position",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.NO_SYMBOL_AT_POSITION,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Verify the line and column numbers are correct (1-based)",
                                "Ensure the cursor is on a symbol (class, method, property, etc.)",
                                "Try adjusting the column position to the start of the symbol name"
                            }
                        }
                    }
                };
            }

            // Find definition locations
            _logger.LogDebug("Found symbol '{SymbolName}' of kind {SymbolKind}, searching for definition locations", 
                symbol.ToDisplayString(), symbol.Kind);
                
            var locations = new List<LocationInfo>();

            // Check if symbol has source locations
            foreach (var location in symbol.Locations)
            {
                if (location.IsInSource)
                {
                    var lineSpan = location.GetLineSpan();
                    locations.Add(new LocationInfo
                    {
                        FilePath = lineSpan.Path,
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1,
                        EndLine = lineSpan.EndLinePosition.Line + 1,
                        EndColumn = lineSpan.EndLinePosition.Character + 1
                    });
                }
            }

            // If symbol is from metadata, try to find the metadata location
            if (locations.Count == 0 && symbol.Locations.Any(l => l.IsInMetadata))
            {
                return new GoToDefinitionResult
                {
                    Found = true,
                    SymbolName = symbol.ToDisplayString(),
                    SymbolKind = symbol.Kind.ToString(),
                    IsMetadata = true,
                    Message = $"Symbol '{symbol.Name}' is defined in metadata (external assembly)"
                };
            }

            if (locations.Count == 0)
            {
                return new GoToDefinitionResult
                {
                    Found = false,
                    SymbolName = symbol.ToDisplayString(),
                    SymbolKind = symbol.Kind.ToString(),
                    Message = "Symbol definition location not found"
                };
            }

            var nextActions = GenerateNextActions(symbol, locations);
            var insights = GenerateInsights(symbol, locations);

            var resourceUri = _resourceProvider?.StoreAnalysisResult("goto-definition", 
                new { symbol = symbol.ToDisplayString(), locations }, 
                $"Definition of {symbol.Name}");
                
            if (resourceUri != null)
            {
                _logger.LogDebug("Stored analysis result with URI: {ResourceUri}", resourceUri);
            }
            else if (_resourceProvider != null)
            {
                _logger.LogWarning("Resource provider is available but failed to store result");
            }
            else
            {
                _logger.LogDebug("No resource provider available - result will not be persisted");
            }
            
            var result = new GoToDefinitionResult
            {
                Found = true,
                SymbolName = symbol.ToDisplayString(),
                SymbolKind = symbol.Kind.ToString(),
                Locations = locations,
                Message = locations.Count == 1 
                    ? "Found definition" 
                    : $"Found {locations.Count} definitions (partial classes/methods)",
                NextActions = nextActions,
                Insights = insights,
                ResourceUri = resourceUri
            };

            _logger.LogInformation("GoToDefinition completed successfully: Found {LocationCount} location(s) for '{SymbolName}'", 
                locations.Count, symbol.ToDisplayString());
                
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Go to Definition");
            return new GoToDefinitionResult
            {
                Found = false,
                Message = $"Error: {ex.Message}",
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
                }
            };
        }
    }

    private List<NextAction> GenerateNextActions(ISymbol symbol, List<LocationInfo> locations)
    {
        var actions = new List<NextAction>();

        // Suggest finding all references
        if (locations.Any())
        {
            var firstLoc = locations.First();
            actions.Add(new NextAction
            {
                Id = "find_references",
                Description = $"Find all references to '{symbol.Name}'",
                ToolName = "roslyn_find_all_references",
                Parameters = new
                {
                    filePath = firstLoc.FilePath,
                    line = firstLoc.Line,
                    column = firstLoc.Column
                },
                Priority = "high"
            });
        }

        // If it's a method, suggest finding callers
        if (symbol.Kind == SymbolKind.Method)
        {
            actions.Add(new NextAction
            {
                Id = "find_callers",
                Description = "Find callers of this method",
                ToolName = "roslyn_find_callers", // Future tool
                Parameters = new
                {
                    methodName = symbol.ToDisplayString()
                },
                Priority = "medium"
            });
        }

        // If it's a type, suggest exploring members
        if (symbol.Kind == SymbolKind.NamedType && locations.Any())
        {
            var firstLoc = locations.First();
            actions.Add(new NextAction
            {
                Id = "document_symbols",
                Description = "Explore members of this type",
                ToolName = "roslyn_document_symbols",
                Parameters = new
                {
                    filePath = firstLoc.FilePath
                },
                Priority = "medium"
            });
        }

        // Always suggest hover for documentation
        if (locations.Any())
        {
            var firstLoc = locations.First();
            actions.Add(new NextAction
            {
                Id = "hover_info",
                Description = "Get documentation and signature",
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

    private List<string> GenerateInsights(ISymbol symbol, List<LocationInfo> locations)
    {
        var insights = new List<string>();

        // Symbol type insight
        insights.Add($"Symbol '{symbol.Name}' is a {GetFriendlySymbolKind(symbol)}");

        // Containing type insight
        if (symbol.ContainingType != null)
        {
            insights.Add($"Member of {symbol.ContainingType.TypeKind.ToString().ToLower()} '{symbol.ContainingType.Name}'");
        }

        // Namespace insight
        if (symbol.ContainingNamespace != null && !symbol.ContainingNamespace.IsGlobalNamespace)
        {
            insights.Add($"Located in namespace '{symbol.ContainingNamespace.ToDisplayString()}'");
        }

        // Accessibility insight
        if (symbol.DeclaredAccessibility != Accessibility.NotApplicable)
        {
            insights.Add($"Has {symbol.DeclaredAccessibility.ToString().ToLower()} accessibility");
        }

        // Multiple locations insight
        if (locations.Count > 1)
        {
            insights.Add($"Partial definition spans {locations.Count} files");
        }

        // Interface implementation insight
        if (symbol is IMethodSymbol method && method.ExplicitInterfaceImplementations.Length > 0)
        {
            var interfaces = string.Join(", ", method.ExplicitInterfaceImplementations.Select(i => i.ContainingType.Name));
            insights.Add($"Explicitly implements interface(s): {interfaces}");
        }

        return insights;
    }

    private string GetFriendlySymbolKind(ISymbol symbol)
    {
        return symbol.Kind switch
        {
            SymbolKind.Method => symbol is IMethodSymbol m && m.MethodKind == MethodKind.Constructor ? "constructor" : "method",
            SymbolKind.Property => "property",
            SymbolKind.Field => "field",
            SymbolKind.Event => "event",
            SymbolKind.NamedType => symbol is INamedTypeSymbol t ? t.TypeKind.ToString().ToLower() : "type",
            SymbolKind.Namespace => "namespace",
            SymbolKind.Parameter => "parameter",
            SymbolKind.Local => "local variable",
            _ => symbol.Kind.ToString().ToLower()
        };
    }
}

public class GoToDefinitionParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file (e.g., 'C:\\Project\\src\\Program.cs' on Windows, '/home/user/project/src/Program.cs' on Unix)")]
    public required string FilePath { get; set; }

    [JsonPropertyName("line")]
    [Description("Line number (1-based) where the symbol appears")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    [Description("Column number (1-based) of the symbol start position")]
    public int Column { get; set; }
}

public class GoToDefinitionResult
{
    [JsonPropertyName("found")]
    public bool Found { get; set; }
    
    [JsonPropertyName("symbolName")]
    public string? SymbolName { get; set; }
    
    [JsonPropertyName("symbolKind")]
    public string? SymbolKind { get; set; }
    
    [JsonPropertyName("locations")]
    public List<LocationInfo>? Locations { get; set; }
    
    [JsonPropertyName("isMetadata")]
    public bool IsMetadata { get; set; }
    
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("nextActions")]
    public List<NextAction>? NextActions { get; set; }
    
    [JsonPropertyName("insights")]
    public List<string>? Insights { get; set; }
    
    [JsonPropertyName("error")]
    public ErrorInfo? Error { get; set; }
    
    [JsonPropertyName("resourceUri")]
    public string? ResourceUri { get; set; }
}

public class LocationInfo
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
}