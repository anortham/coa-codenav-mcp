using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that generates code for common patterns using Roslyn
/// </summary>
[McpServerToolType]
public class GenerateCodeTool : ITool
{
    private readonly ILogger<GenerateCodeTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "csharp_generate_code";
    public string Description => "Generate code for common patterns (constructors, properties, interface implementations)";

    public GenerateCodeTool(
        ILogger<GenerateCodeTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "csharp_generate_code")]
    [Description(@"Generate code for common patterns (constructors, properties, interface implementations).
Returns: Generated code with insertion points.
Prerequisites: Position must be inside a type declaration. Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if generation fails.
Use cases: Generate constructors from fields, properties from fields, interface implementations.
Not for: Complex refactorings (use dedicated refactoring tools), code fixes (use csharp_apply_code_fix).")]
    public async Task<object> ExecuteAsync(GenerateCodeParams parameters, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("GenerateCode request received: FilePath={FilePath}, Line={Line}, Column={Column}, GenerationType={GenerationType}", 
            parameters.FilePath, parameters.Line, parameters.Column, parameters.GenerationType);
        
        var startTime = DateTime.UtcNow;

        try
        {
            // Get document directly from workspace service
            var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                return CreateErrorResult(parameters, ErrorCodes.DOCUMENT_NOT_FOUND, startTime);
            }

            // Get syntax tree and semantic model
            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (syntaxTree == null)
            {
                return CreateErrorResult(parameters, ErrorCodes.COMPILATION_ERROR, startTime);
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                return CreateErrorResult(parameters, ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE, startTime);
            }

            // Find the containing type at the specified position
            var sourceText = await document.GetTextAsync(cancellationToken);
            var position = sourceText.Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
                parameters.Line - 1, 
                parameters.Column - 1));
            var containingType = await GetContainingTypeAsync(document, position, cancellationToken);
            
            if (containingType == null)
            {
                return new GenerateCodeToolResult
                {
                    Success = false,
                    Message = "No type declaration found at the specified position",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.NO_TYPE_AT_POSITION,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the cursor is inside a class, struct, or interface declaration",
                                "Use csharp_document_symbols to find type declarations in the file",
                                "Position the cursor inside the type body, not on the declaration line"
                            },
                            SuggestedActions = new List<SuggestedAction>
                            {
                                new SuggestedAction
                                {
                                    Tool = "csharp_document_symbols",
                                    Description = "Find type declarations in this file",
                                    Parameters = new { filePath = parameters.FilePath, symbolKinds = new[] { "Class", "Interface", "Struct" } }
                                }
                            }
                        }
                    },
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                        GenerationType = parameters.GenerationType
                    },
                    Meta = new ToolMetadata { ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" }
                };
            }

            // Generate code based on the requested type
            var result = parameters.GenerationType?.ToLowerInvariant() switch
            {
                "constructor" => await GenerateConstructorAsync(document, containingType, parameters, cancellationToken),
                "properties" => await GeneratePropertiesAsync(document, containingType, parameters, cancellationToken),
                "interface" => await GenerateInterfaceImplementationAsync(document, containingType, parameters, cancellationToken),
                "equals" => await GenerateEqualityMembersAsync(document, containingType, parameters, cancellationToken),
                "disposable" => await GenerateDisposablePatternAsync(document, containingType, parameters, cancellationToken),
                _ => CreateUnsupportedGenerationTypeResult(parameters, startTime)
            };

            // Add execution time
            result.Meta = new ToolMetadata 
            { 
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                Truncated = false
            };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating code");
            return new GenerateCodeToolResult
            {
                Success = false,
                Message = $"Error generating code: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new List<string>
                        {
                            "Check the server logs for detailed error information",
                            "Verify the solution/project is loaded correctly",
                            "Ensure the file is part of the loaded solution"
                        }
                    }
                },
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    GenerationType = parameters.GenerationType
                },
                Meta = new ToolMetadata { ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" }
            };
        }
    }

    private async Task<TypeDeclarationSyntax?> GetContainingTypeAsync(Document document, int position, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return null;

        var token = root.FindToken(position);
        return token.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
    }

    private async Task<GenerateCodeToolResult> GenerateConstructorAsync(
        Document document, 
        TypeDeclarationSyntax typeDeclaration, 
        GenerateCodeParams parameters,
        CancellationToken cancellationToken)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return CreateErrorResult(parameters, ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE, DateTime.UtcNow);
        }

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration);
        if (typeSymbol == null)
        {
            return CreateErrorResult(parameters, ErrorCodes.SYMBOL_NOT_FOUND, DateTime.UtcNow);
        }

        // Get fields and properties that can be initialized
        var fieldsAndProperties = GetInitializableMembers(typeSymbol, parameters.IncludeInherited ?? false);
        
        if (!fieldsAndProperties.Any())
        {
            return new GenerateCodeToolResult
            {
                Success = false,
                Message = "No fields or properties found to initialize in constructor",
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    GenerationType = parameters.GenerationType
                },
                Insights = new List<string>
                {
                    "The type has no fields or properties that need initialization",
                    "Consider adding fields or properties first",
                    "Use readonly fields for immutable types"
                },
                Actions = new List<NextAction>
                {
                    new NextAction
                    {
                        Id = "add_field",
                        Description = "Add a field to the type first",
                        ToolName = "csharp_generate_code",
                        Parameters = new { /* would need field generation */ },
                        Priority = "high"
                    }
                }
            };
        }

        // Generate constructor code
        var constructorCode = GenerateConstructorCode(typeSymbol, fieldsAndProperties);
        
        // Find insertion point (after last constructor or at beginning of type)
        var insertionPoint = FindConstructorInsertionPoint(typeDeclaration);
        
        return new GenerateCodeToolResult
        {
            Success = true,
            Message = $"Generated constructor with {fieldsAndProperties.Count} parameters",
            GeneratedCode = new GeneratedCode
            {
                Code = constructorCode,
                Language = "csharp",
                InsertionPoint = new InsertionPoint
                {
                    Line = insertionPoint.Line,
                    Column = insertionPoint.Column,
                    Description = insertionPoint.Description
                }
            },
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                GenerationType = parameters.GenerationType
            },
            Summary = new SummaryInfo
            {
                TotalFound = fieldsAndProperties.Count,
                Returned = fieldsAndProperties.Count
            },
            Insights = GenerateConstructorInsights(typeSymbol, fieldsAndProperties),
            Actions = GenerateConstructorNextActions(parameters, typeSymbol)
        };
    }

    private async Task<GenerateCodeToolResult> GeneratePropertiesAsync(
        Document document,
        TypeDeclarationSyntax typeDeclaration,
        GenerateCodeParams parameters,
        CancellationToken cancellationToken)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return CreateErrorResult(parameters, ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE, DateTime.UtcNow);
        }

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration);
        if (typeSymbol == null)
        {
            return CreateErrorResult(parameters, ErrorCodes.SYMBOL_NOT_FOUND, DateTime.UtcNow);
        }

        // Get private fields that don't have corresponding properties
        var fields = GetFieldsWithoutProperties(typeSymbol);
        
        if (!fields.Any())
        {
            return new GenerateCodeToolResult
            {
                Success = false,
                Message = "No private fields found without corresponding properties",
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    GenerationType = parameters.GenerationType
                },
                Insights = new List<string>
                {
                    "All fields already have corresponding properties",
                    "Consider adding new fields if you need more properties",
                    "Use auto-properties for simple get/set scenarios"
                }
            };
        }

        // Generate property code for each field
        var propertiesCode = GeneratePropertiesCode(fields, parameters.PropertyStyle ?? "auto");
        
        // Find insertion point (after last property or after fields)
        var insertionPoint = FindPropertyInsertionPoint(typeDeclaration);
        
        return new GenerateCodeToolResult
        {
            Success = true,
            Message = $"Generated {fields.Count} properties from fields",
            GeneratedCode = new GeneratedCode
            {
                Code = propertiesCode,
                Language = "csharp",
                InsertionPoint = new InsertionPoint
                {
                    Line = insertionPoint.Line,
                    Column = insertionPoint.Column,
                    Description = insertionPoint.Description
                }
            },
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                GenerationType = parameters.GenerationType
            },
            Summary = new SummaryInfo
            {
                TotalFound = fields.Count,
                Returned = fields.Count
            },
            Insights = GeneratePropertyInsights(fields),
            Actions = GeneratePropertyNextActions(parameters, typeSymbol)
        };
    }

    private async Task<GenerateCodeToolResult> GenerateInterfaceImplementationAsync(
        Document document,
        TypeDeclarationSyntax typeDeclaration,
        GenerateCodeParams parameters,
        CancellationToken cancellationToken)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return CreateErrorResult(parameters, ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE, DateTime.UtcNow);
        }

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration);
        if (typeSymbol == null)
        {
            return CreateErrorResult(parameters, ErrorCodes.SYMBOL_NOT_FOUND, DateTime.UtcNow);
        }

        // Get unimplemented interface members
        var unimplementedMembers = GetUnimplementedInterfaceMembers(typeSymbol);
        
        if (!unimplementedMembers.Any())
        {
            return new GenerateCodeToolResult
            {
                Success = false,
                Message = "No unimplemented interface members found",
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    GenerationType = parameters.GenerationType
                },
                Insights = new List<string>
                {
                    "All interface members are already implemented",
                    "Check if the type implements any interfaces",
                    "Use 'Find Implementations' to see existing implementations"
                },
                Actions = new List<NextAction>
                {
                    new NextAction
                    {
                        Id = "find_interfaces",
                        Description = "Find interfaces this type implements",
                        ToolName = "csharp_get_type_members",
                        Parameters = new { filePath = parameters.FilePath, line = parameters.Line, column = parameters.Column },
                        Priority = "medium"
                    }
                }
            };
        }

        // Generate implementation stubs
        var implementationCode = GenerateInterfaceImplementations(unimplementedMembers, parameters.ImplementationStyle ?? "throw");
        
        // Find insertion point
        var insertionPoint = FindMemberInsertionPoint(typeDeclaration);
        
        // Pre-estimate token count
        var estimatedTokens = EstimateCodeTokens(implementationCode);
        var truncated = false;
        
        if (estimatedTokens > 8000)
        {
            // Truncate to most important members
            var importantMembers = unimplementedMembers.Take(20).ToList();
            implementationCode = GenerateInterfaceImplementations(importantMembers, parameters.ImplementationStyle ?? "throw");
            truncated = true;
        }
        
        return new GenerateCodeToolResult
        {
            Success = true,
            Message = $"Generated {unimplementedMembers.Count} interface member implementations",
            GeneratedCode = new GeneratedCode
            {
                Code = implementationCode,
                Language = "csharp",
                InsertionPoint = new InsertionPoint
                {
                    Line = insertionPoint.Line,
                    Column = insertionPoint.Column,
                    Description = insertionPoint.Description
                }
            },
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                GenerationType = parameters.GenerationType
            },
            Summary = new SummaryInfo
            {
                TotalFound = unimplementedMembers.Count,
                Returned = truncated ? 20 : unimplementedMembers.Count
            },
            Insights = GenerateInterfaceInsights(typeSymbol, unimplementedMembers),
            Actions = GenerateInterfaceNextActions(parameters, typeSymbol),
            Meta = new ToolMetadata { Truncated = truncated }
        };
    }

    private async Task<GenerateCodeToolResult> GenerateEqualityMembersAsync(
        Document document,
        TypeDeclarationSyntax typeDeclaration,
        GenerateCodeParams parameters,
        CancellationToken cancellationToken)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return CreateErrorResult(parameters, ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE, DateTime.UtcNow);
        }

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration);
        if (typeSymbol == null)
        {
            return CreateErrorResult(parameters, ErrorCodes.SYMBOL_NOT_FOUND, DateTime.UtcNow);
        }

        // Check if equality members already exist
        var hasEquals = typeSymbol.GetMembers("Equals").Any(m => m is IMethodSymbol);
        var hasGetHashCode = typeSymbol.GetMembers("GetHashCode").Any(m => m is IMethodSymbol);
        
        if (hasEquals && hasGetHashCode)
        {
            return new GenerateCodeToolResult
            {
                Success = false,
                Message = "Equality members (Equals and GetHashCode) already exist",
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    GenerationType = parameters.GenerationType
                },
                Insights = new List<string>
                {
                    "Both Equals and GetHashCode are already implemented",
                    "Consider using record types for value equality",
                    "Ensure GetHashCode is consistent with Equals"
                }
            };
        }

        // Get members to include in equality comparison
        var equalityMembers = GetEqualityMembers(typeSymbol, parameters.IncludeInherited ?? false);
        
        // Generate equality code
        var equalityCode = GenerateEqualityMembersCode(typeSymbol, equalityMembers, !hasEquals, !hasGetHashCode);
        
        // Find insertion point
        var insertionPoint = FindMemberInsertionPoint(typeDeclaration);
        
        return new GenerateCodeToolResult
        {
            Success = true,
            Message = $"Generated equality members using {equalityMembers.Count} fields/properties",
            GeneratedCode = new GeneratedCode
            {
                Code = equalityCode,
                Language = "csharp",
                InsertionPoint = new InsertionPoint
                {
                    Line = insertionPoint.Line,
                    Column = insertionPoint.Column,
                    Description = insertionPoint.Description
                }
            },
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                GenerationType = parameters.GenerationType
            },
            Summary = new SummaryInfo
            {
                TotalFound = equalityMembers.Count,
                Returned = 2
            },
            Insights = GenerateEqualityInsights(typeSymbol, equalityMembers),
            Actions = GenerateEqualityNextActions(parameters, typeSymbol)
        };
    }

    private async Task<GenerateCodeToolResult> GenerateDisposablePatternAsync(
        Document document,
        TypeDeclarationSyntax typeDeclaration,
        GenerateCodeParams parameters,
        CancellationToken cancellationToken)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return CreateErrorResult(parameters, ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE, DateTime.UtcNow);
        }

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration);
        if (typeSymbol == null)
        {
            return CreateErrorResult(parameters, ErrorCodes.SYMBOL_NOT_FOUND, DateTime.UtcNow);
        }

        // Check if already implements IDisposable
        var implementsIDisposable = typeSymbol.AllInterfaces.Any(i => i.Name == "IDisposable");
        
        // Generate appropriate pattern
        var disposableCode = GenerateDisposablePatternCode(typeSymbol, implementsIDisposable, parameters.DisposableStyle ?? "standard");
        
        // Find insertion point
        var insertionPoint = FindMemberInsertionPoint(typeDeclaration);
        
        return new GenerateCodeToolResult
        {
            Success = true,
            Message = $"Generated {parameters.DisposableStyle ?? "standard"} disposable pattern",
            GeneratedCode = new GeneratedCode
            {
                Code = disposableCode,
                Language = "csharp",
                InsertionPoint = new InsertionPoint
                {
                    Line = insertionPoint.Line,
                    Column = insertionPoint.Column,
                    Description = insertionPoint.Description
                }
            },
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                GenerationType = parameters.GenerationType
            },
            Summary = new SummaryInfo
            {
                TotalFound = 1,
                Returned = 1
            },
            Insights = GenerateDisposableInsights(typeSymbol, implementsIDisposable),
            Actions = GenerateDisposableNextActions(parameters, typeSymbol)
        };
    }

    // Helper methods for code generation

    private List<ISymbol> GetInitializableMembers(INamedTypeSymbol typeSymbol, bool includeInherited)
    {
        var members = new List<ISymbol>();
        
        var symbols = includeInherited ? 
            typeSymbol.GetMembers().Concat(typeSymbol.BaseType?.GetMembers() ?? Enumerable.Empty<ISymbol>()) :
            typeSymbol.GetMembers();
        
        foreach (var member in symbols)
        {
            if (member is IFieldSymbol field && !field.IsStatic && !field.IsConst && !field.IsReadOnly)
            {
                members.Add(field);
            }
            else if (member is IPropertySymbol property && !property.IsStatic && property.SetMethod != null)
            {
                members.Add(property);
            }
        }
        
        return members;
    }

    private List<IFieldSymbol> GetFieldsWithoutProperties(INamedTypeSymbol typeSymbol)
    {
        var fields = typeSymbol.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic && !f.IsConst && f.DeclaredAccessibility == Accessibility.Private)
            .ToList();
        
        var properties = typeSymbol.GetMembers()
            .OfType<IPropertySymbol>()
            .Select(p => p.Name.ToLowerInvariant())
            .ToHashSet();
        
        return fields.Where(f => !properties.Contains(f.Name.TrimStart('_').ToLowerInvariant())).ToList();
    }

    private List<ISymbol> GetUnimplementedInterfaceMembers(INamedTypeSymbol typeSymbol)
    {
        var unimplemented = new List<ISymbol>();
        
        foreach (var @interface in typeSymbol.AllInterfaces)
        {
            foreach (var member in @interface.GetMembers())
            {
                if (member is IMethodSymbol or IPropertySymbol or IEventSymbol)
                {
                    var implementation = typeSymbol.FindImplementationForInterfaceMember(member);
                    if (implementation == null)
                    {
                        unimplemented.Add(member);
                    }
                }
            }
        }
        
        return unimplemented;
    }

    private List<ISymbol> GetEqualityMembers(INamedTypeSymbol typeSymbol, bool includeInherited)
    {
        var members = new List<ISymbol>();
        
        var symbols = includeInherited ?
            typeSymbol.GetMembers().Concat(typeSymbol.BaseType?.GetMembers() ?? Enumerable.Empty<ISymbol>()) :
            typeSymbol.GetMembers();
        
        foreach (var member in symbols)
        {
            if (member is IFieldSymbol field && !field.IsStatic && !field.IsConst)
            {
                members.Add(field);
            }
            else if (member is IPropertySymbol property && !property.IsStatic && property.GetMethod != null)
            {
                members.Add(property);
            }
        }
        
        return members;
    }

    private string GenerateConstructorCode(INamedTypeSymbol typeSymbol, List<ISymbol> members)
    {
        var sb = new StringBuilder();
        var parameters = new List<string>();
        var assignments = new List<string>();
        
        foreach (var member in members)
        {
            var paramName = ToCamelCase(member.Name);
            var typeName = GetTypeName(member);
            
            parameters.Add($"{typeName} {paramName}");
            assignments.Add($"        {member.Name} = {paramName};");
        }
        
        sb.AppendLine($"    public {typeSymbol.Name}({string.Join(", ", parameters)})");
        sb.AppendLine("    {");
        sb.AppendLine(string.Join("\n", assignments));
        sb.AppendLine("    }");
        
        return sb.ToString();
    }

    private string GeneratePropertiesCode(List<IFieldSymbol> fields, string style)
    {
        var sb = new StringBuilder();
        
        foreach (var field in fields)
        {
            var propertyName = ToPropertyName(field.Name);
            var typeName = field.Type.ToDisplayString();
            
            if (style == "auto")
            {
                sb.AppendLine($"    public {typeName} {propertyName} {{ get; set; }}");
            }
            else
            {
                sb.AppendLine($"    public {typeName} {propertyName}");
                sb.AppendLine("    {");
                sb.AppendLine($"        get => {field.Name};");
                sb.AppendLine($"        set => {field.Name} = value;");
                sb.AppendLine("    }");
            }
            
            if (!SymbolEqualityComparer.Default.Equals(field, fields.Last()))
            {
                sb.AppendLine();
            }
        }
        
        return sb.ToString();
    }

    private string GenerateInterfaceImplementations(List<ISymbol> members, string style)
    {
        var sb = new StringBuilder();
        
        foreach (var member in members)
        {
            if (member is IMethodSymbol method)
            {
                sb.AppendLine(GenerateMethodImplementation(method, style));
            }
            else if (member is IPropertySymbol property)
            {
                sb.AppendLine(GeneratePropertyImplementation(property, style));
            }
            else if (member is IEventSymbol @event)
            {
                sb.AppendLine(GenerateEventImplementation(@event, style));
            }
            
            if (!SymbolEqualityComparer.Default.Equals(member, members.Last()))
            {
                sb.AppendLine();
            }
        }
        
        return sb.ToString();
    }

    private string GenerateMethodImplementation(IMethodSymbol method, string style)
    {
        var sb = new StringBuilder();
        var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
        var returnType = method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString();
        
        sb.AppendLine($"    public {returnType} {method.Name}({parameters})");
        sb.AppendLine("    {");
        
        if (style == "throw")
        {
            sb.AppendLine($"        throw new NotImplementedException();");
        }
        else if (!method.ReturnsVoid)
        {
            sb.AppendLine($"        return default({returnType});");
        }
        
        sb.Append("    }");
        
        return sb.ToString();
    }

    private string GeneratePropertyImplementation(IPropertySymbol property, string style)
    {
        var sb = new StringBuilder();
        var typeName = property.Type.ToDisplayString();
        
        if (style == "auto")
        {
            sb.Append($"    public {typeName} {property.Name} {{ get; set; }}");
        }
        else
        {
            sb.AppendLine($"    public {typeName} {property.Name}");
            sb.AppendLine("    {");
            
            if (property.GetMethod != null)
            {
                sb.AppendLine("        get { throw new NotImplementedException(); }");
            }
            
            if (property.SetMethod != null)
            {
                sb.AppendLine("        set { throw new NotImplementedException(); }");
            }
            
            sb.Append("    }");
        }
        
        return sb.ToString();
    }

    private string GenerateEventImplementation(IEventSymbol @event, string style)
    {
        var typeName = @event.Type.ToDisplayString();
        return $"    public event {typeName} {@event.Name};";
    }

    private string GenerateEqualityMembersCode(INamedTypeSymbol typeSymbol, List<ISymbol> members, bool generateEquals, bool generateGetHashCode)
    {
        var sb = new StringBuilder();
        
        if (generateEquals)
        {
            sb.AppendLine($"    public override bool Equals(object obj)");
            sb.AppendLine("    {");
            sb.AppendLine($"        return Equals(obj as {typeSymbol.Name});");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    public bool Equals({typeSymbol.Name} other)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (other is null) return false;");
            sb.AppendLine("        if (ReferenceEquals(this, other)) return true;");
            
            if (members.Any())
            {
                var comparisons = members.Select(m => $"EqualityComparer<{GetTypeName(m)}>.Default.Equals({m.Name}, other.{m.Name})");
                sb.AppendLine($"        return {string.Join(" &&\n               ", comparisons)};");
            }
            else
            {
                sb.AppendLine("        return true;");
            }
            
            sb.AppendLine("    }");
        }
        
        if (generateEquals && generateGetHashCode)
        {
            sb.AppendLine();
        }
        
        if (generateGetHashCode)
        {
            sb.AppendLine("    public override int GetHashCode()");
            sb.AppendLine("    {");
            
            if (members.Any())
            {
                sb.AppendLine("        var hash = new HashCode();");
                foreach (var member in members)
                {
                    sb.AppendLine($"        hash.Add({member.Name});");
                }
                sb.AppendLine("        return hash.ToHashCode();");
            }
            else
            {
                sb.AppendLine("        return 0;");
            }
            
            sb.Append("    }");
        }
        
        return sb.ToString();
    }

    private string GenerateDisposablePatternCode(INamedTypeSymbol typeSymbol, bool implementsIDisposable, string style)
    {
        var sb = new StringBuilder();
        
        if (!implementsIDisposable)
        {
            // Need to add interface implementation
            sb.AppendLine($"    // TODO: Add 'IDisposable' to the class declaration");
            sb.AppendLine();
        }
        
        sb.AppendLine("    private bool disposed = false;");
        sb.AppendLine();
        
        if (style == "async")
        {
            sb.AppendLine("    public async ValueTask DisposeAsync()");
            sb.AppendLine("    {");
            sb.AppendLine("        await DisposeAsyncCore();");
            sb.AppendLine("        Dispose(false);");
            sb.AppendLine("        GC.SuppressFinalize(this);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    protected virtual async ValueTask DisposeAsyncCore()");
            sb.AppendLine("    {");
            sb.AppendLine("        // TODO: Dispose async resources");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        
        sb.AppendLine("    public void Dispose()");
        sb.AppendLine("    {");
        sb.AppendLine("        Dispose(true);");
        sb.AppendLine("        GC.SuppressFinalize(this);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    protected virtual void Dispose(bool disposing)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (!disposed)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (disposing)");
        sb.AppendLine("            {");
        sb.AppendLine("                // TODO: Dispose managed resources");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            // TODO: Free unmanaged resources");
        sb.AppendLine();
        sb.AppendLine("            disposed = true;");
        sb.AppendLine("        }");
        sb.Append("    }");
        
        return sb.ToString();
    }

    private InsertionPoint FindConstructorInsertionPoint(TypeDeclarationSyntax typeDeclaration)
    {
        var lastConstructor = typeDeclaration.Members.OfType<ConstructorDeclarationSyntax>().LastOrDefault();
        if (lastConstructor != null)
        {
            var lineSpan = lastConstructor.GetLocation().GetLineSpan();
            return new InsertionPoint
            {
                Line = lineSpan.EndLinePosition.Line + 2,
                Column = 1,
                Description = "After last constructor"
            };
        }
        
        // Insert at beginning of type
        var openBrace = typeDeclaration.OpenBraceToken;
        var lineSpan2 = openBrace.GetLocation().GetLineSpan();
        return new InsertionPoint
        {
            Line = lineSpan2.EndLinePosition.Line + 2,
            Column = 1,
            Description = "At beginning of type"
        };
    }

    private InsertionPoint FindPropertyInsertionPoint(TypeDeclarationSyntax typeDeclaration)
    {
        var lastProperty = typeDeclaration.Members.OfType<PropertyDeclarationSyntax>().LastOrDefault();
        if (lastProperty != null)
        {
            var lineSpan = lastProperty.GetLocation().GetLineSpan();
            return new InsertionPoint
            {
                Line = lineSpan.EndLinePosition.Line + 2,
                Column = 1,
                Description = "After last property"
            };
        }
        
        var lastField = typeDeclaration.Members.OfType<FieldDeclarationSyntax>().LastOrDefault();
        if (lastField != null)
        {
            var lineSpan = lastField.GetLocation().GetLineSpan();
            return new InsertionPoint
            {
                Line = lineSpan.EndLinePosition.Line + 2,
                Column = 1,
                Description = "After fields"
            };
        }
        
        return FindConstructorInsertionPoint(typeDeclaration);
    }

    private InsertionPoint FindMemberInsertionPoint(TypeDeclarationSyntax typeDeclaration)
    {
        var lastMember = typeDeclaration.Members.LastOrDefault();
        if (lastMember != null)
        {
            var lineSpan = lastMember.GetLocation().GetLineSpan();
            return new InsertionPoint
            {
                Line = lineSpan.EndLinePosition.Line + 2,
                Column = 1,
                Description = "After last member"
            };
        }
        
        return FindConstructorInsertionPoint(typeDeclaration);
    }

    private string GetTypeName(ISymbol symbol)
    {
        return symbol switch
        {
            IFieldSymbol field => field.Type.ToDisplayString(),
            IPropertySymbol property => property.Type.ToDisplayString(),
            _ => "object"
        };
    }

    private string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        
        name = name.TrimStart('_');
        if (name.Length == 0) return name;
        
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private string ToPropertyName(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return fieldName;
        
        fieldName = fieldName.TrimStart('_');
        if (fieldName.Length == 0) return fieldName;
        
        return char.ToUpperInvariant(fieldName[0]) + fieldName.Substring(1);
    }

    private List<string> GenerateConstructorInsights(INamedTypeSymbol typeSymbol, List<ISymbol> members)
    {
        var insights = new List<string>();
        
        if (members.Any(m => m is IFieldSymbol f && f.IsReadOnly))
        {
            insights.Add("💡 Constructor initializes readonly fields for immutability");
        }
        
        if (members.Count > 5)
        {
            insights.Add($"⚠️ Constructor has {members.Count} parameters - consider using a builder pattern");
        }
        
        if (typeSymbol.IsRecord)
        {
            insights.Add("💡 Consider using record primary constructor syntax for cleaner code");
        }
        
        insights.Add($"✅ All {members.Count} fields/properties will be initialized");
        
        return insights;
    }

    private List<NextAction> GenerateConstructorNextActions(GenerateCodeParams parameters, INamedTypeSymbol typeSymbol)
    {
        var actions = new List<NextAction>();
        
        actions.Add(new NextAction
        {
            Id = "generate_properties",
            Description = "Generate properties for private fields",
            ToolName = "csharp_generate_code",
            Parameters = new 
            { 
                filePath = parameters.FilePath,
                line = parameters.Line,
                column = parameters.Column,
                generationType = "properties"
            },
            Priority = "medium"
        });
        
        if (!typeSymbol.GetMembers("Equals").Any())
        {
            actions.Add(new NextAction
            {
                Id = "generate_equals",
                Description = "Generate equality members",
                ToolName = "csharp_generate_code",
                Parameters = new
                {
                    filePath = parameters.FilePath,
                    line = parameters.Line,
                    column = parameters.Column,
                    generationType = "equals"
                },
                Priority = "low"
            });
        }
        
        return actions;
    }

    private List<string> GeneratePropertyInsights(List<IFieldSymbol> fields)
    {
        var insights = new List<string>
        {
            $"📝 Generated {fields.Count} properties from private fields"
        };
        
        if (fields.Any(f => f.Name.StartsWith("_")))
        {
            insights.Add("✅ Following underscore prefix convention for private fields");
        }
        
        insights.Add("💡 Consider using auto-properties if backing fields aren't needed");
        
        return insights;
    }

    private List<NextAction> GeneratePropertyNextActions(GenerateCodeParams parameters, INamedTypeSymbol typeSymbol)
    {
        var actions = new List<NextAction>();
        
        if (!typeSymbol.Constructors.Any(c => c.Parameters.Length > 0))
        {
            actions.Add(new NextAction
            {
                Id = "generate_constructor",
                Description = "Generate constructor to initialize properties",
                ToolName = "csharp_generate_code",
                Parameters = new
                {
                    filePath = parameters.FilePath,
                    line = parameters.Line,
                    column = parameters.Column,
                    generationType = "constructor"
                },
                Priority = "high"
            });
        }
        
        return actions;
    }

    private List<string> GenerateInterfaceInsights(INamedTypeSymbol typeSymbol, List<ISymbol> unimplementedMembers)
    {
        var insights = new List<string>();
        
        var byInterface = unimplementedMembers
            .GroupBy(m => m.ContainingType.Name)
            .Select(g => $"{g.Key} ({g.Count()} members)")
            .ToList();
        
        insights.Add($"🔧 Implementing {unimplementedMembers.Count} members from: {string.Join(", ", byInterface)}");
        
        if (unimplementedMembers.Any(m => m is IMethodSymbol method && method.IsAsync))
        {
            insights.Add("⚡ Contains async methods - remember to implement async logic");
        }
        
        insights.Add("💡 Replace NotImplementedException with actual implementation");
        
        return insights;
    }

    private List<NextAction> GenerateInterfaceNextActions(GenerateCodeParams parameters, INamedTypeSymbol typeSymbol)
    {
        var actions = new List<NextAction>
        {
            new NextAction
            {
                Id = "find_implementations",
                Description = "Find other implementations for reference",
                ToolName = "csharp_find_implementations",
                Parameters = new
                {
                    filePath = parameters.FilePath,
                    line = parameters.Line,
                    column = parameters.Column
                },
                Priority = "medium"
            }
        };
        
        return actions;
    }

    private List<string> GenerateEqualityInsights(INamedTypeSymbol typeSymbol, List<ISymbol> members)
    {
        var insights = new List<string>
        {
            $"🔀 Equality based on {members.Count} fields/properties"
        };
        
        if (typeSymbol.IsReferenceType)
        {
            insights.Add("📌 Reference type - consider implementing IEquatable<T> for better performance");
        }
        
        insights.Add("⚠️ Remember to override == and != operators for consistency");
        
        return insights;
    }

    private List<NextAction> GenerateEqualityNextActions(GenerateCodeParams parameters, INamedTypeSymbol typeSymbol)
    {
        return new List<NextAction>();
    }

    private List<string> GenerateDisposableInsights(INamedTypeSymbol typeSymbol, bool implementsIDisposable)
    {
        var insights = new List<string>();
        
        if (!implementsIDisposable)
        {
            insights.Add("⚠️ Remember to add IDisposable to the class declaration");
        }
        
        insights.Add("🧹 Implement resource cleanup in the TODO sections");
        insights.Add("💡 Consider IAsyncDisposable for async resource cleanup");
        
        return insights;
    }

    private List<NextAction> GenerateDisposableNextActions(GenerateCodeParams parameters, INamedTypeSymbol typeSymbol)
    {
        return new List<NextAction>();
    }

    private GenerateCodeToolResult CreateErrorResult(GenerateCodeParams parameters, string errorCode, DateTime startTime)
    {
        var recovery = errorCode switch
        {
            ErrorCodes.WORKSPACE_NOT_LOADED => new RecoveryInfo
            {
                Steps = new List<string>
                {
                    "Load a solution or project first using csharp_load_solution or csharp_load_project",
                    "Verify the workspace was loaded successfully",
                    "Try the operation again"
                },
                SuggestedActions = new List<SuggestedAction>
                {
                    new SuggestedAction
                    {
                        Tool = "csharp_load_solution",
                        Description = "Load a C# solution",
                        Parameters = new { solutionPath = "path/to/solution.sln" }
                    }
                }
            },
            ErrorCodes.DOCUMENT_NOT_FOUND => new RecoveryInfo
            {
                Steps = new List<string>
                {
                    "Verify the file path is correct and absolute",
                    "Ensure the file is part of the loaded solution/project",
                    "Check if the file exists on disk"
                }
            },
            _ => new RecoveryInfo
            {
                Steps = new List<string>
                {
                    "Check the server logs for more details",
                    "Verify the code is syntactically correct",
                    "Try reloading the solution"
                }
            }
        };

        return new GenerateCodeToolResult
        {
            Success = false,
            Message = $"Error: {errorCode}",
            Error = new ErrorInfo
            {
                Code = errorCode,
                Recovery = recovery
            },
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                GenerationType = parameters.GenerationType
            },
            Meta = new ToolMetadata { ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" }
        };
    }

    private int EstimateCodeTokens(string code)
    {
        // Simple estimation based on character count
        // Roughly 4 characters per token
        return (code.Length / 4) + 100; // Add base overhead
    }

    private GenerateCodeToolResult CreateUnsupportedGenerationTypeResult(GenerateCodeParams parameters, DateTime startTime)
    {
        return new GenerateCodeToolResult
        {
            Success = false,
            Message = $"Unsupported generation type: {parameters.GenerationType}",
            Error = new ErrorInfo
            {
                Code = ErrorCodes.INVALID_PARAMETERS,
                Recovery = new RecoveryInfo
                {
                    Steps = new List<string>
                    {
                        "Use one of the supported generation types:",
                        "- 'constructor': Generate constructor from fields/properties",
                        "- 'properties': Generate properties from private fields",
                        "- 'interface': Generate interface implementation stubs",
                        "- 'equals': Generate Equals and GetHashCode methods",
                        "- 'disposable': Generate IDisposable pattern"
                    }
                }
            },
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                GenerationType = parameters.GenerationType
            },
            Meta = new ToolMetadata { ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" }
        };
    }
}

/// <summary>
/// Parameters for GenerateCode tool
/// </summary>
public class GenerateCodeParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file (e.g., 'C:\\Project\\src\\Program.cs' on Windows, '/home/user/project/src/Program.cs' on Unix)")]
    public required string FilePath { get; set; }

    [JsonPropertyName("line")]
    [Description("Line number (1-based) inside the type declaration where code should be generated")]
    public required int Line { get; set; }

    [JsonPropertyName("column")]
    [Description("Column number (1-based) inside the type declaration")]
    public required int Column { get; set; }

    [JsonPropertyName("generationType")]
    [Description("Type of code to generate: 'constructor', 'properties', 'interface', 'equals', 'disposable'")]
    public required string GenerationType { get; set; }

    [JsonPropertyName("includeInherited")]
    [Description("Include inherited members when generating code. true = include base class members, false = current class only (default)")]
    public bool? IncludeInherited { get; set; }

    [JsonPropertyName("propertyStyle")]
    [Description("Style for property generation: 'auto' (default) or 'full' with backing field")]
    public string? PropertyStyle { get; set; }

    [JsonPropertyName("implementationStyle")]
    [Description("Style for interface implementations: 'throw' (default), 'default', or 'auto'")]
    public string? ImplementationStyle { get; set; }

    [JsonPropertyName("disposableStyle")]
    [Description("Style for IDisposable pattern: 'standard' (default) or 'async'")]
    public string? DisposableStyle { get; set; }
}

/// <summary>
/// Result for GenerateCode tool
/// </summary>
public class GenerateCodeToolResult : ToolResultBase
{
    public override string Operation => "csharp_generate_code";

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("generatedCode")]
    public GeneratedCode? GeneratedCode { get; set; }
}

/// <summary>
/// Information about generated code
/// </summary>
public class GeneratedCode
{
    [JsonPropertyName("code")]
    public required string Code { get; set; }

    [JsonPropertyName("language")]
    public required string Language { get; set; }

    [JsonPropertyName("insertionPoint")]
    public InsertionPoint? InsertionPoint { get; set; }
}

/// <summary>
/// Information about where to insert generated code
/// </summary>
public class InsertionPoint
{
    [JsonPropertyName("line")]
    public required int Line { get; set; }

    [JsonPropertyName("column")]
    public required int Column { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}