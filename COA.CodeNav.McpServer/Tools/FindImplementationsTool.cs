using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that finds all implementations of interfaces and overrides of virtual/abstract members
/// </summary>
[McpServerToolType]
public class FindImplementationsTool : ITool
{
    private readonly ILogger<FindImplementationsTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "roslyn_find_implementations";
    public string Description => "Find all implementations of an interface or abstract/virtual member";

    public FindImplementationsTool(
        ILogger<FindImplementationsTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "roslyn_find_implementations")]
    [Description(@"Find all implementations of interfaces and overrides of virtual/abstract methods.
Returns: List of implementing types/members with their locations and metadata.
Prerequisites: Call roslyn_load_solution or roslyn_load_project first.
Error handling: Returns specific error codes with recovery steps if symbol is not found.
Use cases: Finding concrete implementations, discovering derived classes, locating overrides.
Not for: Finding references (use roslyn_find_all_references), finding base types (use roslyn_goto_definition).")]
    public async Task<object> ExecuteAsync(FindImplementationsParams parameters, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("FindImplementations request received: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);
            
        try
        {
            _logger.LogInformation("Processing FindImplementations for {FilePath} at {Line}:{Column}", 
                parameters.FilePath, parameters.Line, parameters.Column);

            // Get the document
            _logger.LogDebug("Retrieving document from workspace: {FilePath}", parameters.FilePath);
            var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
                return new FindImplementationsResult
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
                return new FindImplementationsResult
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
                return new FindImplementationsResult
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
                                "Ensure the cursor is on a symbol that can have implementations",
                                "Try adjusting the column position to the start of the symbol name"
                            }
                        }
                    }
                };
            }

            // Check if symbol can have implementations
            if (!CanHaveImplementations(symbol))
            {
                return new FindImplementationsResult
                {
                    Found = false,
                    SymbolName = symbol.ToDisplayString(),
                    SymbolKind = symbol.Kind.ToString(),
                    Message = $"Symbol '{symbol.Name}' of kind '{symbol.Kind}' cannot have implementations",
                    Insights = new List<string>
                    {
                        "Only interfaces, abstract classes, abstract methods, and virtual methods can have implementations",
                        "For concrete types, use 'Find All References' to see where they are used"
                    }
                };
            }

            // Find implementations
            _logger.LogDebug("Finding implementations for symbol '{SymbolName}' of kind {SymbolKind}", 
                symbol.ToDisplayString(), symbol.Kind);

            var implementations = new List<ImplementationInfo>();
            
            // Use SymbolFinder.FindImplementationsAsync which works for all symbol types
            var foundImplementations = await SymbolFinder.FindImplementationsAsync(
                symbol, 
                document.Project.Solution, 
                cancellationToken: cancellationToken);

            foreach (var impl in foundImplementations)
            {
                if (impl.Locations.Any(l => l.IsInSource))
                {
                    var location = impl.Locations.First(l => l.IsInSource);
                    var lineSpan = location.GetLineSpan();
                    
                    var implementationInfo = new ImplementationInfo
                    {
                        ImplementingType = impl is INamedTypeSymbol ? impl.ToDisplayString() : impl.ContainingType?.ToDisplayString(),
                        ImplementingMember = impl is not INamedTypeSymbol ? impl.ToDisplayString() : null,
                        Location = new LocationInfo
                        {
                            FilePath = lineSpan.Path,
                            Line = lineSpan.StartLinePosition.Line + 1,
                            Column = lineSpan.StartLinePosition.Character + 1,
                            EndLine = lineSpan.EndLinePosition.Line + 1,
                            EndColumn = lineSpan.EndLinePosition.Character + 1
                        },
                        IsDirectImplementation = true,
                        IsExplicitImplementation = IsExplicitImplementation(impl, symbol),
                        ImplementationType = GetImplementationTypeDescription(impl, symbol)
                    };
                    
                    implementations.Add(implementationInfo);
                }
            }

            // For types, also find derived classes
            if (symbol is INamedTypeSymbol typeSymbol && typeSymbol.IsAbstract)
            {
                var derivedClasses = await SymbolFinder.FindDerivedClassesAsync(
                    typeSymbol, 
                    document.Project.Solution,
                    transitive: true,
                    cancellationToken: cancellationToken);
                
                foreach (var derived in derivedClasses)
                {
                    // Skip if already added
                    if (implementations.Any(i => i.ImplementingType == derived.ToDisplayString()))
                        continue;
                        
                    if (derived.Locations.Any(l => l.IsInSource))
                    {
                        var location = derived.Locations.First(l => l.IsInSource);
                        var lineSpan = location.GetLineSpan();
                        
                        implementations.Add(new ImplementationInfo
                        {
                            ImplementingType = derived.ToDisplayString(),
                            ImplementingMember = null,
                            Location = new LocationInfo
                            {
                                FilePath = lineSpan.Path,
                                Line = lineSpan.StartLinePosition.Line + 1,
                                Column = lineSpan.StartLinePosition.Character + 1,
                                EndLine = lineSpan.EndLinePosition.Line + 1,
                                EndColumn = lineSpan.EndLinePosition.Character + 1
                            },
                            IsDirectImplementation = SymbolEqualityComparer.Default.Equals(derived.BaseType, typeSymbol),
                            IsExplicitImplementation = false,
                            ImplementationType = "Derived Class"
                        });
                    }
                }
            }

            if (!implementations.Any())
            {
                return new FindImplementationsResult
                {
                    Found = false,
                    SymbolName = symbol.ToDisplayString(),
                    SymbolKind = symbol.Kind.ToString(),
                    TotalImplementations = 0,
                    Message = $"No implementations found for '{symbol.Name}'",
                    Insights = GenerateNoImplementationsInsights(symbol)
                };
            }

            var insights = GenerateInsights(symbol, implementations);
            var nextActions = GenerateNextActions(symbol, implementations);

            var resourceUri = _resourceProvider?.StoreAnalysisResult("find-implementations",
                new { symbol = symbol.ToDisplayString(), implementations },
                $"Implementations of {symbol.Name}");

            return new FindImplementationsResult
            {
                Found = true,
                SymbolName = symbol.ToDisplayString(),
                SymbolKind = symbol.Kind.ToString(),
                TotalImplementations = implementations.Count,
                Implementations = implementations,
                Message = $"Found {implementations.Count} implementation(s) of '{symbol.Name}'",
                Insights = insights,
                NextActions = nextActions,
                ResourceUri = resourceUri
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Find Implementations");
            return new FindImplementationsResult
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

    private bool CanHaveImplementations(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol typeSymbol => typeSymbol.TypeKind == TypeKind.Interface || typeSymbol.IsAbstract,
            IMethodSymbol methodSymbol => methodSymbol.IsAbstract || methodSymbol.IsVirtual || methodSymbol.IsOverride || 
                                         methodSymbol.ContainingType?.TypeKind == TypeKind.Interface,
            IPropertySymbol propertySymbol => propertySymbol.IsAbstract || propertySymbol.IsVirtual || propertySymbol.IsOverride ||
                                            propertySymbol.ContainingType?.TypeKind == TypeKind.Interface,
            IEventSymbol eventSymbol => eventSymbol.IsAbstract || eventSymbol.IsVirtual || eventSymbol.IsOverride ||
                                       eventSymbol.ContainingType?.TypeKind == TypeKind.Interface,
            _ => false
        };
    }

    private bool IsExplicitImplementation(ISymbol implementation, ISymbol interfaceSymbol)
    {
        return implementation switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations.Any(),
            IPropertySymbol property => property.ExplicitInterfaceImplementations.Any(),
            IEventSymbol @event => @event.ExplicitInterfaceImplementations.Any(),
            _ => false
        };
    }

    private string GetImplementationTypeDescription(ISymbol implementation, ISymbol baseSymbol)
    {
        if (baseSymbol is INamedTypeSymbol baseType)
        {
            if (baseType.TypeKind == TypeKind.Interface)
            {
                return "Interface Implementation";
            }
            else if (baseType.IsAbstract)
            {
                return "Abstract Class Implementation";
            }
        }
        
        return implementation switch
        {
            IMethodSymbol method when method.IsOverride => "Method Override",
            IMethodSymbol method when method.ContainingType?.TypeKind == TypeKind.Interface => "Interface Method Implementation",
            IPropertySymbol property when property.IsOverride => "Property Override", 
            IPropertySymbol property when property.ContainingType?.TypeKind == TypeKind.Interface => "Interface Property Implementation",
            IEventSymbol @event when @event.IsOverride => "Event Override",
            IEventSymbol @event when @event.ContainingType?.TypeKind == TypeKind.Interface => "Interface Event Implementation",
            INamedTypeSymbol => "Type Implementation",
            _ => "Implementation"
        };
    }

    private List<string> GenerateInsights(ISymbol symbol, List<ImplementationInfo> implementations)
    {
        var insights = new List<string>();

        // Group by implementation type
        var typeGroups = implementations.GroupBy(i => i.ImplementationType);
        insights.Add($"Found {string.Join(", ", typeGroups.Select(g => $"{g.Count()} {g.Key}"))}");

        // Check for explicit implementations
        var explicitCount = implementations.Count(i => i.IsExplicitImplementation);
        if (explicitCount > 0)
        {
            insights.Add($"{explicitCount} explicit interface implementation(s) found");
        }

        // Direct vs indirect implementations
        var directCount = implementations.Count(i => i.IsDirectImplementation);
        if (directCount < implementations.Count)
        {
            insights.Add($"{directCount} direct and {implementations.Count - directCount} indirect implementations");
        }

        // File distribution
        var fileCount = implementations.Select(i => i.Location?.FilePath).Distinct().Count();
        if (fileCount > 1)
        {
            insights.Add($"Implementations spread across {fileCount} files");
        }

        return insights;
    }

    private List<string> GenerateNoImplementationsInsights(ISymbol symbol)
    {
        var insights = new List<string>();

        if (symbol is INamedTypeSymbol typeSymbol)
        {
            if (typeSymbol.TypeKind == TypeKind.Interface)
            {
                insights.Add("This interface has no implementations in the current solution");
                insights.Add("Consider creating a concrete implementation or mock for testing");
            }
            else if (typeSymbol.IsAbstract)
            {
                insights.Add("This abstract class has no derived classes in the current solution");
                insights.Add("Consider creating a concrete implementation");
            }
        }
        else if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            insights.Add("This member has no overrides or implementations in the current solution");
            if (symbol.ContainingType?.TypeKind == TypeKind.Interface)
            {
                insights.Add("Check if the interface itself has implementations");
            }
        }

        return insights;
    }

    private List<NextAction> GenerateNextActions(ISymbol symbol, List<ImplementationInfo> implementations)
    {
        var actions = new List<NextAction>();

        // Take first few implementations for next actions
        foreach (var impl in implementations.Take(3))
        {
            if (impl.Location != null)
            {
                actions.Add(new NextAction
                {
                    Id = $"goto_impl_{impl.ImplementingType?.Replace(".", "_").ToLower()}",
                    Description = $"Go to {impl.ImplementingType}",
                    ToolName = "roslyn_goto_definition",
                    Parameters = new
                    {
                        filePath = impl.Location.FilePath,
                        line = impl.Location.Line,
                        column = impl.Location.Column
                    },
                    Priority = "high"
                });
            }
        }

        // Suggest finding all references to see usage
        if (implementations.Any())
        {
            var firstImpl = implementations.First();
            if (firstImpl.Location != null)
            {
                actions.Add(new NextAction
                {
                    Id = "find_usage",
                    Description = $"Find usages of implementations",
                    ToolName = "roslyn_find_all_references",
                    Parameters = new
                    {
                        filePath = firstImpl.Location.FilePath,
                        line = firstImpl.Location.Line,
                        column = firstImpl.Location.Column
                    },
                    Priority = "medium"
                });
            }
        }

        return actions;
    }
}

public class FindImplementationsParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file")]
    public required string FilePath { get; set; }

    [JsonPropertyName("line")]
    [Description("Line number (1-based) where the symbol appears")]
    public required int Line { get; set; }

    [JsonPropertyName("column")]
    [Description("Column number (1-based) where the symbol appears")]
    public required int Column { get; set; }
}

public class FindImplementationsResult
{
    public bool Found { get; set; }
    public string? SymbolName { get; set; }
    public string? SymbolKind { get; set; }
    public int TotalImplementations { get; set; }
    public List<ImplementationInfo>? Implementations { get; set; }
    public string? Message { get; set; }
    public List<string>? Insights { get; set; }
    public List<NextAction>? NextActions { get; set; }
    public ErrorInfo? Error { get; set; }
    public string? ResourceUri { get; set; }
}

public class ImplementationInfo
{
    public string? ImplementingType { get; set; }
    public string? ImplementingMember { get; set; }
    public LocationInfo? Location { get; set; }
    public bool IsDirectImplementation { get; set; }
    public bool IsExplicitImplementation { get; set; }
    public string? ImplementationType { get; set; }
}