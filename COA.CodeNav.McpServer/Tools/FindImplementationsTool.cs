using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.IO;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that finds all implementations of interfaces and overrides of virtual/abstract members
/// </summary>
public class FindImplementationsTool : McpToolBase<FindImplementationsParams, FindImplementationsToolResult>
{
    private readonly ILogger<FindImplementationsTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => ToolNames.FindImplementations;
    public override string Description => "Find all implementations of interfaces and abstract methods. Shows every concrete class that implements or overrides the selected symbol.";

    public FindImplementationsTool(
        ILogger<FindImplementationsTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        ITokenEstimator tokenEstimator,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _tokenEstimator = tokenEstimator;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<FindImplementationsToolResult> ExecuteInternalAsync(
        FindImplementationsParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("FindImplementations request received: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);
            
        var startTime = DateTime.UtcNow;

        // Get the document
        _logger.LogDebug("Retrieving document from workspace: {FilePath}", parameters.FilePath);
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
            return new FindImplementationsToolResult
            {
                Success = false,
                Message = $"Document not found in workspace: {parameters.FilePath}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.DOCUMENT_NOT_FOUND,
                    Message = $"Document not found in workspace: {parameters.FilePath}",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Ensure the file path is correct and absolute",
                            "Verify the solution/project containing this file is loaded",
                            "Use csharp_load_solution or csharp_load_project to load the containing project"
                        },
                        SuggestedActions = new List<SuggestedAction>
                        {
                            new SuggestedAction
                            {
                                Tool = "csharp_load_solution",
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
            return new FindImplementationsToolResult
            {
                Success = false,
                Message = "Could not get semantic model for document",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE,
                    Message = "Could not get semantic model for document",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
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
            return new FindImplementationsToolResult
            {
                Success = false,
                Message = "No symbol found at the specified position",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.NO_SYMBOL_AT_POSITION,
                    Message = "No symbol found at the specified position",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
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
            return new FindImplementationsToolResult
            {
                Success = false,
                Message = $"Symbol '{symbol.Name}' of kind '{symbol.Kind}' cannot have implementations",
                Insights = new List<string>
                {
                    "Only interfaces, abstract classes, abstract methods, and virtual methods can have implementations",
                    "For concrete types, use 'Find All References' to see where they are used"
                },
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    TargetSymbol = symbol.ToDisplayString()
                },
                Meta = new ToolExecutionMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
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
            return new FindImplementationsToolResult
            {
                Success = false,
                Message = $"No implementations found for '{symbol.Name}'",
                Insights = GenerateNoImplementationsInsights(symbol),
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    TargetSymbol = symbol.ToDisplayString()
                },
                Summary = new SummaryInfo
                {
                    TotalFound = 0,
                    Returned = 0,
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
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                }
            };
        }

        var insights = GenerateInsights(symbol, implementations);
        var actions = GenerateActions(symbol, implementations);

        var resourceUri = _resourceProvider?.StoreAnalysisResult("find-implementations",
            new { symbol = symbol.ToDisplayString(), implementations },
            $"Implementations of {symbol.Name}");

        return new FindImplementationsToolResult
        {
            Success = true,
            Message = $"Found {implementations.Count} implementation(s) of '{symbol.Name}'",
            Implementations = implementations,
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                TargetSymbol = symbol.ToDisplayString()
            },
            Summary = new SummaryInfo
            {
                TotalFound = implementations.Count,
                Returned = implementations.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                SymbolInfo = new SymbolSummary
                {
                    Name = symbol.Name,
                    Kind = symbol.Kind.ToString(),
                    ContainingType = symbol.ContainingType?.ToDisplayString(),
                    Namespace = symbol.ContainingNamespace?.ToDisplayString()
                }
            },
            ResultsSummary = new ResultsSummary
            {
                Included = implementations.Count,
                Total = implementations.Count,
                HasMore = false
            },
            Distribution = new ImplementationDistribution
            {
                ByType = implementations.GroupBy(i => i.ImplementationType ?? "Unknown")
                    .ToDictionary(g => g.Key, g => g.Count()),
                ByProject = implementations.Where(i => i.Location != null)
                    .GroupBy(i => Path.GetFileName(Path.GetDirectoryName(i.Location!.FilePath) ?? "Unknown"))
                    .ToDictionary(g => g.Key, g => g.Count())
            },
            Insights = insights,
            Actions = actions,
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            },
            ResourceUri = resourceUri
        };
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

    private List<AIAction> GenerateActions(ISymbol symbol, List<ImplementationInfo> implementations)
    {
        var actions = new List<AIAction>();

        // Take first few implementations for next actions
        foreach (var impl in implementations.Take(3))
        {
            if (impl.Location != null)
            {
                actions.Add(new AIAction
                {
                    Action = ToolNames.GoToDefinition,
                    Description = $"Go to {impl.ImplementingType}",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = impl.Location.FilePath,
                        ["line"] = impl.Location.Line,
                        ["column"] = impl.Location.Column
                    },
                    Priority = 90,
                    Category = "navigation"
                });
            }
        }

        // Suggest finding all references to see usage
        if (implementations.Any())
        {
            var firstImpl = implementations.First();
            if (firstImpl.Location != null)
            {
                actions.Add(new AIAction
                {
                    Action = ToolNames.FindAllReferences,
                    Description = $"Find usages of implementations",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = firstImpl.Location.FilePath,
                        ["line"] = firstImpl.Location.Line,
                        ["column"] = firstImpl.Location.Column
                    },
                    Priority = 80,
                    Category = "navigation"
                });
            }
        }

        return actions;
    }

}

/// <summary>
/// Parameters for FindImplementations tool
/// </summary>
public class FindImplementationsParams
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
}