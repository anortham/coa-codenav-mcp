using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using DataAnnotations = System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that generates type hierarchy information including inheritance chains and interface implementations
/// </summary>
public class TypeHierarchyTool : McpToolBase<TypeHierarchyParams, TypeHierarchyResult>
{
    private readonly ILogger<TypeHierarchyTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => "csharp_type_hierarchy";
    public override string Description => @"View the complete type hierarchy including base classes, derived types, and interface implementations.
Returns: Hierarchical view of type relationships with inheritance chains and implementations.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if type is not found.
Use cases: Understanding inheritance relationships, finding all implementations, exploring type hierarchies.
AI benefit: Provides complete view of type relationships for better code understanding.";

    public TypeHierarchyTool(
        ILogger<TypeHierarchyTool> logger,
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

    protected override async Task<TypeHierarchyResult> ExecuteInternalAsync(
        TypeHierarchyParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("TypeHierarchy request: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);

        var startTime = DateTime.UtcNow;

        var document = await _documentService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            return new TypeHierarchyResult
            {
                Success = false,
                Message = $"Document not found: {parameters.FilePath}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.DOCUMENT_NOT_FOUND,
                    Message = $"Document not found: {parameters.FilePath}",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Verify the file path is correct and absolute",
                            "Ensure the solution or project containing this file is loaded",
                            "Use csharp_load_solution or csharp_load_project if needed"
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

        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (tree == null)
        {
            return new TypeHierarchyResult
            {
                Success = false,
                Message = "Failed to get syntax tree",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Message = "Failed to get syntax tree",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Check if the file contains valid C# code",
                            "Try reloading the solution"
                        }
                    }
                }
            };
        }

        var position = tree.GetText().Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
            parameters.Line - 1, parameters.Column - 1));
        
        var root = await tree.GetRootAsync(cancellationToken);
        var token = root.FindToken(position);
        
        // Find the type declaration
        var typeDeclaration = token.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDeclaration == null)
        {
            return new TypeHierarchyResult
            {
                Success = false,
                Message = "No type found at the specified position",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.NO_SYMBOL_AT_POSITION,
                    Message = "No type found at the specified position",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Ensure the cursor is positioned on a class, struct, or interface declaration",
                            "Try adjusting the column position to the type name"
                        }
                    }
                }
            };
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return new TypeHierarchyResult
            {
                Success = false,
                Message = "Failed to get semantic model",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE,
                    Message = "Failed to get semantic model",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Ensure the project compiles without errors",
                            "Try reloading the solution"
                        }
                    }
                }
            };
        }

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            return new TypeHierarchyResult
            {
                Success = false,
                Message = "Failed to resolve type symbol",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SYMBOL_NOT_FOUND,
                    Message = "Failed to resolve type symbol",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Ensure the type declaration is valid",
                            "Check if there are compilation errors"
                        }
                    }
                }
            };
        }

        var workspace = _workspaceService.GetActiveWorkspaces().FirstOrDefault();
        if (workspace == null)
        {
            return new TypeHierarchyResult
            {
                Success = false,
                Message = "No workspace loaded",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.WORKSPACE_NOT_LOADED,
                    Message = "No workspace loaded",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "This should not happen as document was found",
                            "Try reloading the solution"
                        }
                    }
                }
            };
        }

        var solution = workspace.Solution;

        // Build the hierarchy
        var hierarchy = new TypeHierarchyInfo
        {
            Type = CreateTypeInfo(typeSymbol),
            BaseTypes = await GetBaseTypesAsync(typeSymbol, solution, parameters.MaxDepth, cancellationToken),
            DerivedTypes = await GetDerivedTypesAsync(typeSymbol, solution, parameters.IncludeDerived, cancellationToken),
            ImplementedInterfaces = GetImplementedInterfaces(typeSymbol, parameters.MaxDepth),
            ImplementingTypes = await GetImplementingTypesAsync(typeSymbol, solution, parameters.IncludeImplementations, cancellationToken)
        };

        // Generate insights and next actions
        var insights = GenerateInsights(hierarchy, typeSymbol);
        var nextActions = GenerateNextActions(hierarchy, typeSymbol, parameters);

        var summary = new TypeHierarchySummary
        {
            BaseTypeCount = CountBaseTypes(hierarchy.BaseTypes),
            DerivedTypeCount = hierarchy.DerivedTypes?.Count ?? 0,
            InterfaceCount = hierarchy.ImplementedInterfaces?.Count ?? 0,
            ImplementingTypeCount = hierarchy.ImplementingTypes?.Count ?? 0,
            TotalRelatedTypes = CountAllRelatedTypes(hierarchy)
        };

        return new TypeHierarchyResult
        {
            Success = true,
            Message = $"Type hierarchy for '{typeSymbol.Name}'",
            Query = new QueryInfo 
            { 
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                TargetSymbol = typeSymbol.ToDisplayString()
            },
            Summary = new SummaryInfo
            {
                TotalFound = summary.TotalRelatedTypes,
                Returned = summary.TotalRelatedTypes,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                SymbolInfo = new SymbolSummary
                {
                    Name = typeSymbol.Name,
                    Kind = typeSymbol.TypeKind.ToString(),
                    ContainingType = typeSymbol.ContainingType?.ToDisplayString(),
                    Namespace = typeSymbol.ContainingNamespace?.ToDisplayString()
                }
            },
            Hierarchy = hierarchy,
            TypeSummary = summary,
            Insights = insights,
            Actions = nextActions,
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
    }

    private Task<List<TypeReference>> GetBaseTypesAsync(INamedTypeSymbol typeSymbol, Solution solution, 
        int maxDepth, CancellationToken cancellationToken)
    {
        var baseTypes = new List<TypeReference>();
        var current = typeSymbol.BaseType;
        var depth = 0;

        while (current != null && current.SpecialType != SpecialType.System_Object && depth < maxDepth)
        {
            var baseType = CreateTypeInfo(current);
            
            // Try to find location
            var locations = current.Locations.Where(l => l.IsInSource).ToList();
            if (locations.Any())
            {
                var location = locations.First();
                var lineSpan = location.GetLineSpan();
                baseType.Location = new LocationInfo
                {
                    FilePath = lineSpan.Path,
                    Line = lineSpan.StartLinePosition.Line + 1,
                    Column = lineSpan.StartLinePosition.Character + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1,
                    EndColumn = lineSpan.EndLinePosition.Character + 1
                };
            }

            baseTypes.Add(baseType);
            current = current.BaseType;
            depth++;
        }

        return Task.FromResult(baseTypes);
    }

    private async Task<List<TypeReference>> GetDerivedTypesAsync(INamedTypeSymbol typeSymbol, Solution solution, 
        bool includeDerived, CancellationToken cancellationToken)
    {
        if (!includeDerived)
            return new List<TypeReference>();

        var derivedTypes = new List<TypeReference>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
                continue;

            var types = GetAllTypes(compilation.GlobalNamespace);

            foreach (var type in types)
            {
                if (type.BaseType != null && SymbolEqualityComparer.Default.Equals(type.BaseType, typeSymbol))
                {
                    var derivedType = CreateTypeInfo(type);
                    
                    var location = type.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location != null)
                    {
                        var lineSpan = location.GetLineSpan();
                        derivedType.Location = new LocationInfo
                        {
                            FilePath = lineSpan.Path,
                            Line = lineSpan.StartLinePosition.Line + 1,
                            Column = lineSpan.StartLinePosition.Character + 1,
                            EndLine = lineSpan.EndLinePosition.Line + 1,
                            EndColumn = lineSpan.EndLinePosition.Character + 1
                        };
                    }

                    derivedTypes.Add(derivedType);
                }
            }
        }

        return derivedTypes;
    }

    private List<TypeReference> GetImplementedInterfaces(INamedTypeSymbol typeSymbol, int maxDepth)
    {
        var interfaces = new List<TypeReference>();
        var allInterfaces = typeSymbol.AllInterfaces;

        foreach (var interfaceType in allInterfaces)
        {
            var interfaceInfo = CreateTypeInfo(interfaceType);
            
            var location = interfaceType.Locations.FirstOrDefault(l => l.IsInSource);
            if (location != null)
            {
                var lineSpan = location.GetLineSpan();
                interfaceInfo.Location = new LocationInfo
                {
                    FilePath = lineSpan.Path,
                    Line = lineSpan.StartLinePosition.Line + 1,
                    Column = lineSpan.StartLinePosition.Character + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1,
                    EndColumn = lineSpan.EndLinePosition.Character + 1
                };
            }

            interfaceInfo.IsDirect = typeSymbol.Interfaces.Contains(interfaceType, SymbolEqualityComparer.Default);
            interfaces.Add(interfaceInfo);
        }

        return interfaces;
    }

    private async Task<List<TypeReference>> GetImplementingTypesAsync(INamedTypeSymbol typeSymbol, Solution solution, 
        bool includeImplementations, CancellationToken cancellationToken)
    {
        if (!includeImplementations || typeSymbol.TypeKind != TypeKind.Interface)
            return new List<TypeReference>();

        var implementingTypes = new List<TypeReference>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
                continue;

            var types = GetAllTypes(compilation.GlobalNamespace);

            foreach (var type in types)
            {
                if (type.AllInterfaces.Contains(typeSymbol, SymbolEqualityComparer.Default))
                {
                    var implementingType = CreateTypeInfo(type);
                    
                    var location = type.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location != null)
                    {
                        var lineSpan = location.GetLineSpan();
                        implementingType.Location = new LocationInfo
                        {
                            FilePath = lineSpan.Path,
                            Line = lineSpan.StartLinePosition.Line + 1,
                            Column = lineSpan.StartLinePosition.Character + 1,
                            EndLine = lineSpan.EndLinePosition.Line + 1,
                            EndColumn = lineSpan.EndLinePosition.Character + 1
                        };
                    }

                    implementingTypes.Add(implementingType);
                }
            }
        }

        return implementingTypes;
    }

    private IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol namespaceSymbol)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            yield return type;
            
            foreach (var nestedType in GetNestedTypes(type))
            {
                yield return nestedType;
            }
        }

        foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in GetAllTypes(nestedNamespace))
            {
                yield return type;
            }
        }
    }

    private IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol typeSymbol)
    {
        foreach (var nestedType in typeSymbol.GetTypeMembers())
        {
            yield return nestedType;
            
            foreach (var deeperNestedType in GetNestedTypes(nestedType))
            {
                yield return deeperNestedType;
            }
        }
    }

    private TypeReference CreateTypeInfo(INamedTypeSymbol typeSymbol)
    {
        return new TypeReference
        {
            Name = typeSymbol.Name,
            FullName = typeSymbol.ToDisplayString(),
            Kind = typeSymbol.TypeKind.ToString(),
            IsAbstract = typeSymbol.IsAbstract,
            IsSealed = typeSymbol.IsSealed,
            IsStatic = typeSymbol.IsStatic,
            IsPartial = typeSymbol.Locations.Length > 1,
            Accessibility = typeSymbol.DeclaredAccessibility.ToString(),
            ContainingNamespace = typeSymbol.ContainingNamespace?.ToDisplayString(),
            GenericArity = typeSymbol.Arity,
            IsGeneric = typeSymbol.IsGenericType
        };
    }

    private int CountBaseTypes(List<TypeReference>? baseTypes)
    {
        return baseTypes?.Count ?? 0;
    }

    private int CountAllRelatedTypes(TypeHierarchyInfo hierarchy)
    {
        return (hierarchy.BaseTypes?.Count ?? 0) +
               (hierarchy.DerivedTypes?.Count ?? 0) +
               (hierarchy.ImplementedInterfaces?.Count ?? 0) +
               (hierarchy.ImplementingTypes?.Count ?? 0);
    }

    private List<string> GenerateInsights(TypeHierarchyInfo hierarchy, INamedTypeSymbol typeSymbol)
    {
        var insights = new List<string>();

        // Type characteristics
        if (typeSymbol.IsAbstract)
            insights.Add($"Abstract {typeSymbol.TypeKind.ToString().ToLower()} - cannot be instantiated directly");
        else if (typeSymbol.IsSealed)
            insights.Add($"Sealed {typeSymbol.TypeKind.ToString().ToLower()} - cannot be inherited");
        else if (typeSymbol.IsStatic)
            insights.Add($"Static {typeSymbol.TypeKind.ToString().ToLower()} - cannot be instantiated or inherited");

        // Inheritance depth
        var inheritanceDepth = hierarchy.BaseTypes?.Count ?? 0;
        if (inheritanceDepth > 3)
            insights.Add($"Deep inheritance hierarchy ({inheritanceDepth} levels) - consider composition");
        else if (inheritanceDepth == 0 && typeSymbol.TypeKind == TypeKind.Class)
            insights.Add("Inherits directly from System.Object");

        // Interface implementation
        var interfaceCount = hierarchy.ImplementedInterfaces?.Count ?? 0;
        if (interfaceCount > 0)
        {
            var directCount = hierarchy.ImplementedInterfaces?.Count(i => i.IsDirect) ?? 0;
            insights.Add($"Implements {interfaceCount} interface(s) ({directCount} directly)");
        }

        // Derived types
        var derivedCount = hierarchy.DerivedTypes?.Count ?? 0;
        if (derivedCount > 0)
            insights.Add($"Has {derivedCount} derived type(s) - changes may impact them");

        // If interface, implementing types
        if (typeSymbol.TypeKind == TypeKind.Interface)
        {
            var implementingCount = hierarchy.ImplementingTypes?.Count ?? 0;
            if (implementingCount > 0)
                insights.Add($"Implemented by {implementingCount} type(s)");
            else
                insights.Add("No implementations found in the solution");
        }

        // Generic type info
        if (typeSymbol.IsGenericType)
            insights.Add($"Generic type with {typeSymbol.Arity} type parameter(s)");

        return insights;
    }

    private List<AIAction> GenerateNextActions(TypeHierarchyInfo hierarchy, INamedTypeSymbol typeSymbol, 
        TypeHierarchyParams parameters)
    {
        var actions = new List<AIAction>();

        // Suggest finding all references
        actions.Add(new AIAction
        {
            Action = "csharp_find_all_references",
            Description = $"Find all references to '{typeSymbol.Name}'",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = parameters.FilePath,
                ["line"] = parameters.Line,
                ["column"] = parameters.Column
            },
            Priority = 90,
            Category = "navigation"
        });

        // If has derived types, suggest analyzing them
        if (hierarchy.DerivedTypes?.Any() == true)
        {
            var firstDerived = hierarchy.DerivedTypes.First();
            if (firstDerived.Location != null)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_type_hierarchy",
                    Description = $"Analyze derived type '{firstDerived.Name}'",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = firstDerived.Location.FilePath,
                        ["line"] = firstDerived.Location.Line,
                        ["column"] = firstDerived.Location.Column
                    },
                    Priority = 70,
                    Category = "exploration"
                });
            }
        }

        // If implements interfaces, suggest exploring them
        if (hierarchy.ImplementedInterfaces?.Any(i => i.IsDirect && i.Location != null) == true)
        {
            var firstInterface = hierarchy.ImplementedInterfaces.First(i => i.IsDirect && i.Location != null);
            actions.Add(new AIAction
            {
                Action = "csharp_get_type_members",
                Description = $"Explore interface '{firstInterface.Name}'",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = firstInterface.Location!.FilePath,
                    ["line"] = firstInterface.Location.Line,
                    ["column"] = firstInterface.Location.Column
                },
                Priority = 60,
                Category = "exploration"
            });
        }

        return actions;
    }

}

/// <summary>
/// Parameters for TypeHierarchy tool
/// </summary>
public class TypeHierarchyParams
{
    [DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file")]
    public string FilePath { get; set; } = string.Empty;

    [DataAnnotations.Required]
    [DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Line must be positive")]
    [JsonPropertyName("line")]
    [COA.Mcp.Framework.Attributes.Description("Line number (1-based) where the type is declared")]
    public int Line { get; set; }

    [DataAnnotations.Required]
    [DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Column must be positive")]
    [JsonPropertyName("column")]
    [COA.Mcp.Framework.Attributes.Description("Column number (1-based) where the type is declared")]
    public int Column { get; set; }

    [JsonPropertyName("includeDerived")]
    [COA.Mcp.Framework.Attributes.Description("Include types that derive from this type (default: true)")]
    public bool IncludeDerived { get; set; } = true;

    [JsonPropertyName("includeImplementations")]
    [COA.Mcp.Framework.Attributes.Description("Include types that implement this interface (default: true)")]
    public bool IncludeImplementations { get; set; } = true;

    [JsonPropertyName("maxDepth")]
    [COA.Mcp.Framework.Attributes.Description("Maximum depth for base type hierarchy (default: 10)")]
    public int MaxDepth { get; set; } = 10;
}

public class TypeHierarchyResult : ToolResultBase
{
    public override string Operation => "csharp_type_hierarchy";

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }
    
    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("hierarchy")]
    public TypeHierarchyInfo? Hierarchy { get; set; }

    [JsonPropertyName("typeSummary")]
    public TypeHierarchySummary? TypeSummary { get; set; }
}

public class TypeHierarchyInfo
{
    [JsonPropertyName("type")]
    public required TypeReference Type { get; set; }

    [JsonPropertyName("baseTypes")]
    public List<TypeReference>? BaseTypes { get; set; }

    [JsonPropertyName("derivedTypes")]
    public List<TypeReference>? DerivedTypes { get; set; }

    [JsonPropertyName("implementedInterfaces")]
    public List<TypeReference>? ImplementedInterfaces { get; set; }

    [JsonPropertyName("implementingTypes")]
    public List<TypeReference>? ImplementingTypes { get; set; }
}

public class TypeReference
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("fullName")]
    public required string FullName { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("isAbstract")]
    public bool IsAbstract { get; set; }

    [JsonPropertyName("isSealed")]
    public bool IsSealed { get; set; }

    [JsonPropertyName("isStatic")]
    public bool IsStatic { get; set; }

    [JsonPropertyName("isPartial")]
    public bool IsPartial { get; set; }

    [JsonPropertyName("accessibility")]
    public required string Accessibility { get; set; }

    [JsonPropertyName("containingNamespace")]
    public string? ContainingNamespace { get; set; }

    [JsonPropertyName("location")]
    public LocationInfo? Location { get; set; }

    [JsonPropertyName("genericArity")]
    public int GenericArity { get; set; }

    [JsonPropertyName("isGeneric")]
    public bool IsGeneric { get; set; }

    [JsonPropertyName("isDirect")]
    public bool IsDirect { get; set; }
}

public class TypeHierarchySummary
{
    [JsonPropertyName("baseTypeCount")]
    public int BaseTypeCount { get; set; }

    [JsonPropertyName("derivedTypeCount")]
    public int DerivedTypeCount { get; set; }

    [JsonPropertyName("interfaceCount")]
    public int InterfaceCount { get; set; }

    [JsonPropertyName("implementingTypeCount")]
    public int ImplementingTypeCount { get; set; }

    [JsonPropertyName("totalRelatedTypes")]
    public int TotalRelatedTypes { get; set; }
}