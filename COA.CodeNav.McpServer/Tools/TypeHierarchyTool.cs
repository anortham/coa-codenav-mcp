using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that generates type hierarchy information including inheritance chains and interface implementations
/// </summary>
[McpServerToolType]
public class TypeHierarchyTool : ITool
{
    private readonly ILogger<TypeHierarchyTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "csharp_type_hierarchy";
    public string Description => "View inheritance hierarchy and interface implementations for types";

    public TypeHierarchyTool(
        ILogger<TypeHierarchyTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "csharp_type_hierarchy")]
    [Description(@"View the complete type hierarchy including base classes, derived types, and interface implementations.
Returns: Hierarchical view of type relationships with inheritance chains and implementations.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if type is not found.
Use cases: Understanding inheritance relationships, finding all implementations, exploring type hierarchies.
AI benefit: Provides complete view of type relationships for better code understanding.")]
    public async Task<object> ExecuteAsync(TypeHierarchyParams parameters, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("TypeHierarchy request: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);

        try
        {
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
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Verify the file path is correct and absolute",
                                "Ensure the solution or project containing this file is loaded",
                                "Use csharp_load_solution or csharp_load_project if needed"
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
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Check if the file contains valid C# code",
                                "Try reloading the solution"
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
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the cursor is positioned on a class, struct, or interface declaration",
                                "Try adjusting the column position to the type name"
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

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                return new TypeHierarchyResult
                {
                    Success = false,
                    Message = "Failed to get semantic model",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.INTERNAL_ERROR,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the project compiles without errors",
                                "Try reloading the solution"
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
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the type declaration is valid",
                                "Check if there are compilation errors"
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
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "This should not happen as document was found",
                                "Try reloading the solution"
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

            var solution = workspace.Solution;
            var insights = new List<string>();
            var actions = new List<NextAction>();

            // Build the hierarchy
            var hierarchy = new TypeHierarchyInfo
            {
                Type = CreateTypeInfo(typeSymbol),
                BaseTypes = await GetBaseTypesAsync(typeSymbol, solution, parameters.MaxDepth, cancellationToken),
                DerivedTypes = await GetDerivedTypesAsync(typeSymbol, solution, parameters.IncludeDerived, cancellationToken),
                ImplementedInterfaces = GetImplementedInterfaces(typeSymbol, parameters.MaxDepth),
                ImplementingTypes = await GetImplementingTypesAsync(typeSymbol, solution, parameters.IncludeImplementations, cancellationToken)
            };

            // Generate insights
            GenerateInsights(hierarchy, typeSymbol, insights);

            // Generate next actions
            GenerateNextActions(hierarchy, typeSymbol, parameters, actions);

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
                Hierarchy = hierarchy,
                Summary = new TypeHierarchySummary
                {
                    BaseTypeCount = CountBaseTypes(hierarchy.BaseTypes),
                    DerivedTypeCount = hierarchy.DerivedTypes?.Count ?? 0,
                    InterfaceCount = hierarchy.ImplementedInterfaces?.Count ?? 0,
                    ImplementingTypeCount = hierarchy.ImplementingTypes?.Count ?? 0,
                    TotalRelatedTypes = CountAllRelatedTypes(hierarchy)
                },
                Insights = insights,
                Actions = actions,
                Meta = new ToolMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating type hierarchy");
            return new TypeHierarchyResult
            {
                Success = false,
                Message = $"Error generating type hierarchy: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new List<string>
                        {
                            "Check the server logs for detailed error information",
                            "Verify the file contains valid C# code",
                            "Try reloading the solution"
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

        // Search all projects in the solution
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
                continue;

            // Get all types in the project
            var types = GetAllTypes(compilation.GlobalNamespace);

            foreach (var type in types)
            {
                if (type.BaseType != null && SymbolEqualityComparer.Default.Equals(type.BaseType, typeSymbol))
                {
                    var derivedType = CreateTypeInfo(type);
                    
                    // Add location if available
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
            
            // Add location if available
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

            // Mark if directly implemented vs inherited
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

        // Search all projects in the solution
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
                continue;

            // Get all types in the project
            var types = GetAllTypes(compilation.GlobalNamespace);

            foreach (var type in types)
            {
                if (type.AllInterfaces.Contains(typeSymbol, SymbolEqualityComparer.Default))
                {
                    var implementingType = CreateTypeInfo(type);
                    
                    // Add location if available
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
            
            // Recursively get nested types
            foreach (var nestedType in GetNestedTypes(type))
            {
                yield return nestedType;
            }
        }

        // Recursively process nested namespaces
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
            
            // Recursively get nested types
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

    private void GenerateInsights(TypeHierarchyInfo hierarchy, INamedTypeSymbol typeSymbol, List<string> insights)
    {
        // Type characteristics
        if (typeSymbol.IsAbstract)
            insights.Add($"🔷 Abstract {typeSymbol.TypeKind.ToString().ToLower()} - cannot be instantiated directly");
        else if (typeSymbol.IsSealed)
            insights.Add($"🔒 Sealed {typeSymbol.TypeKind.ToString().ToLower()} - cannot be inherited");
        else if (typeSymbol.IsStatic)
            insights.Add($"📊 Static {typeSymbol.TypeKind.ToString().ToLower()} - cannot be instantiated or inherited");

        // Inheritance depth
        var inheritanceDepth = hierarchy.BaseTypes?.Count ?? 0;
        if (inheritanceDepth > 3)
            insights.Add($"⚠️ Deep inheritance hierarchy ({inheritanceDepth} levels) - consider composition");
        else if (inheritanceDepth == 0 && typeSymbol.TypeKind == TypeKind.Class)
            insights.Add("📌 Inherits directly from System.Object");

        // Interface implementation
        var interfaceCount = hierarchy.ImplementedInterfaces?.Count ?? 0;
        if (interfaceCount > 0)
        {
            var directCount = hierarchy.ImplementedInterfaces?.Count(i => i.IsDirect) ?? 0;
            insights.Add($"🔌 Implements {interfaceCount} interface(s) ({directCount} directly)");
        }

        // Derived types
        var derivedCount = hierarchy.DerivedTypes?.Count ?? 0;
        if (derivedCount > 0)
            insights.Add($"🌳 Has {derivedCount} derived type(s) - changes may impact them");

        // If interface, implementing types
        if (typeSymbol.TypeKind == TypeKind.Interface)
        {
            var implementingCount = hierarchy.ImplementingTypes?.Count ?? 0;
            if (implementingCount > 0)
                insights.Add($"✅ Implemented by {implementingCount} type(s)");
            else
                insights.Add("⚠️ No implementations found in the solution");
        }

        // Generic type info
        if (typeSymbol.IsGenericType)
            insights.Add($"🔤 Generic type with {typeSymbol.Arity} type parameter(s)");
    }

    private void GenerateNextActions(TypeHierarchyInfo hierarchy, INamedTypeSymbol typeSymbol, 
        TypeHierarchyParams parameters, List<NextAction> actions)
    {
        // Suggest finding all references
        actions.Add(new NextAction
        {
            Id = "find_type_references",
            Description = $"Find all references to '{typeSymbol.Name}'",
            ToolName = "csharp_find_all_references",
            Parameters = new
            {
                filePath = parameters.FilePath,
                line = parameters.Line,
                column = parameters.Column
            },
            Priority = "high"
        });

        // If has derived types, suggest analyzing them
        if (hierarchy.DerivedTypes?.Any() == true)
        {
            var firstDerived = hierarchy.DerivedTypes.First();
            if (firstDerived.Location != null)
            {
                actions.Add(new NextAction
                {
                    Id = "analyze_derived",
                    Description = $"Analyze derived type '{firstDerived.Name}'",
                    ToolName = "csharp_type_hierarchy",
                    Parameters = new
                    {
                        filePath = firstDerived.Location.FilePath,
                        line = firstDerived.Location.Line,
                        column = firstDerived.Location.Column
                    },
                    Priority = "medium"
                });
            }
        }

        // If implements interfaces, suggest exploring them
        if (hierarchy.ImplementedInterfaces?.Any(i => i.IsDirect && i.Location != null) == true)
        {
            var firstInterface = hierarchy.ImplementedInterfaces.First(i => i.IsDirect && i.Location != null);
            actions.Add(new NextAction
            {
                Id = "explore_interface",
                Description = $"Explore interface '{firstInterface.Name}'",
                ToolName = ToolNames.GetTypeMembers,
                Parameters = new
                {
                    filePath = firstInterface.Location!.FilePath,
                    line = firstInterface.Location.Line,
                    column = firstInterface.Location.Column
                },
                Priority = "medium"
            });
        }

        // Suggest finding unused code if sealed with no derived types
        if (typeSymbol.IsSealed && (hierarchy.DerivedTypes?.Count ?? 0) == 0)
        {
            actions.Add(new NextAction
            {
                Id = "check_usage",
                Description = "Check if this sealed type is unused",
                ToolName = "csharp_find_unused_code",
                Parameters = new
                {
                    scope = "file",
                    filePath = parameters.FilePath,
                    symbolKinds = new[] { "Class" }
                },
                Priority = "low"
            });
        }
    }
}

public class TypeHierarchyParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file")]
    public required string FilePath { get; set; }

    [JsonPropertyName("line")]
    [Description("Line number (1-based) where the type is declared")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    [Description("Column number (1-based) where the type is declared")]
    public int Column { get; set; }

    [JsonPropertyName("includeDerived")]
    [Description("Include types that derive from this type (default: true)")]
    public bool IncludeDerived { get; set; } = true;

    [JsonPropertyName("includeImplementations")]
    [Description("Include types that implement this interface (default: true)")]
    public bool IncludeImplementations { get; set; } = true;

    [JsonPropertyName("maxDepth")]
    [Description("Maximum depth for base type hierarchy (default: 10)")]
    public int MaxDepth { get; set; } = 10;
}

public class TypeHierarchyResult : ToolResultBase
{
    public override string Operation => ToolNames.TypeHierarchy;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("hierarchy")]
    public TypeHierarchyInfo? Hierarchy { get; set; }

    [JsonPropertyName("summary")]
    public TypeHierarchySummary? Summary { get; set; }
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