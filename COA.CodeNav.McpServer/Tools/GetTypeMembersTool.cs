using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that lists all members of a type with documentation
/// </summary>
[McpServerToolType]
public class GetTypeMembersTool : ITool
{
    private readonly ILogger<GetTypeMembersTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "roslyn_get_type_members";
    public string Description => "List all members of a type with their signatures and documentation";

    public GetTypeMembersTool(
        ILogger<GetTypeMembersTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "roslyn_get_type_members")]
    [Description(@"List all members of a type including methods, properties, fields, and events with their documentation.
Returns: Detailed list of type members with signatures, documentation, and metadata.
Prerequisites: Call roslyn_load_solution or roslyn_load_project first.
Error handling: Returns specific error codes with recovery steps if type is not found.
Use cases: Exploring type APIs, understanding type structure, generating documentation, finding specific members.
Not for: Finding implementations (use roslyn_find_implementations), searching across types (use roslyn_symbol_search).")]
    public async Task<object> ExecuteAsync(GetTypeMembersParams parameters, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("GetTypeMembers request received: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);
            
        try
        {
            _logger.LogInformation("Processing GetTypeMembers for {FilePath} at {Line}:{Column}", 
                parameters.FilePath, parameters.Line, parameters.Column);

            // Get the document
            _logger.LogDebug("Retrieving document from workspace: {FilePath}", parameters.FilePath);
            var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
                return new GetTypeMembersResult
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
                return new GetTypeMembersResult
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
                return new GetTypeMembersResult
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
                                "Ensure the cursor is on a type symbol",
                                "Try adjusting the column position to the start of the type name"
                            }
                        }
                    }
                };
            }

            // Get the type symbol (if symbol is a member, get its containing type)
            INamedTypeSymbol? typeSymbol = symbol as INamedTypeSymbol;
            if (typeSymbol == null && symbol.ContainingType != null)
            {
                typeSymbol = symbol.ContainingType;
            }

            if (typeSymbol == null)
            {
                return new GetTypeMembersResult
                {
                    Found = false,
                    SymbolName = symbol.ToDisplayString(),
                    SymbolKind = symbol.Kind.ToString(),
                    Message = $"Symbol '{symbol.Name}' is not a type or is not contained in a type",
                    Insights = new List<string>
                    {
                        "This tool works with classes, interfaces, structs, records, and enums",
                        "For namespace members, use Document Symbols tool"
                    }
                };
            }

            // Get all members
            var members = new List<TypeMemberInfo>();
            var allMembers = typeSymbol.GetMembers();

            foreach (var member in allMembers)
            {
                // Skip compiler-generated members unless requested
                if (!parameters.IncludeCompilerGenerated.GetValueOrDefault() && IsCompilerGenerated(member))
                    continue;

                // Apply filters
                if (!ShouldIncludeMember(member, parameters))
                    continue;

                var memberInfo = CreateMemberInfo(member);
                if (memberInfo != null)
                {
                    members.Add(memberInfo);
                }
            }

            // Include inherited members if requested
            if (parameters.IncludeInherited.GetValueOrDefault())
            {
                var baseType = typeSymbol.BaseType;
                while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
                {
                    foreach (var member in baseType.GetMembers())
                    {
                        if (!ShouldIncludeMember(member, parameters))
                            continue;

                        if (IsOverriddenInDerived(member, typeSymbol))
                            continue;

                        var memberInfo = CreateMemberInfo(member);
                        if (memberInfo != null)
                        {
                            memberInfo.IsInherited = true;
                            memberInfo.DeclaringType = baseType.ToDisplayString();
                            members.Add(memberInfo);
                        }
                    }
                    baseType = baseType.BaseType;
                }

                // Include interface members
                foreach (var iface in typeSymbol.AllInterfaces)
                {
                    foreach (var member in iface.GetMembers())
                    {
                        if (!ShouldIncludeMember(member, parameters))
                            continue;

                        var memberInfo = CreateMemberInfo(member);
                        if (memberInfo != null)
                        {
                            memberInfo.IsInherited = true;
                            memberInfo.DeclaringType = iface.ToDisplayString();
                            memberInfo.IsInterfaceMember = true;
                            members.Add(memberInfo);
                        }
                    }
                }
            }

            // Sort members
            members = SortMembers(members, parameters.SortBy ?? "Kind");

            // Generate insights
            var insights = GenerateInsights(typeSymbol, members);

            // Generate next actions
            var nextActions = GenerateNextActions(typeSymbol, members);

            // Store result
            var resourceUri = _resourceProvider?.StoreAnalysisResult("type-members",
                new { type = typeSymbol.ToDisplayString(), members },
                $"Members of {typeSymbol.Name}");

            return new GetTypeMembersResult
            {
                Found = true,
                TypeName = typeSymbol.ToDisplayString(),
                TypeKind = typeSymbol.TypeKind.ToString(),
                TotalMembers = members.Count,
                Members = members,
                Message = $"Found {members.Count} members in '{typeSymbol.Name}'",
                Insights = insights,
                NextActions = nextActions,
                ResourceUri = resourceUri
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Get Type Members");
            return new GetTypeMembersResult
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

    private bool IsCompilerGenerated(ISymbol symbol)
    {
        return symbol.IsImplicitlyDeclared || 
               symbol.Name.StartsWith("<") ||
               symbol.GetAttributes().Any(a => a.AttributeClass?.Name == "CompilerGeneratedAttribute");
    }

    private bool ShouldIncludeMember(ISymbol member, GetTypeMembersParams parameters)
    {
        // Filter by accessibility
        if (!parameters.IncludePrivate.GetValueOrDefault() && member.DeclaredAccessibility == Accessibility.Private)
            return false;

        if (!parameters.IncludeProtected.GetValueOrDefault() && member.DeclaredAccessibility == Accessibility.Protected)
            return false;

        // Filter by member kinds
        if (parameters.MemberKinds?.Any() == true)
        {
            var memberKind = GetMemberKind(member);
            if (!parameters.MemberKinds.Contains(memberKind, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        // Skip special members unless requested
        if (member.Kind == SymbolKind.Method)
        {
            var method = (IMethodSymbol)member;
            if (method.MethodKind == MethodKind.Constructor && member.IsImplicitlyDeclared)
                return false;
        }

        return true;
    }

    private bool IsOverriddenInDerived(ISymbol baseMember, INamedTypeSymbol derivedType)
    {
        return derivedType.GetMembers()
            .Any(m => m.IsOverride && 
                      SymbolEqualityComparer.Default.Equals(GetOverriddenMember(m), baseMember));
    }

    private ISymbol? GetOverriddenMember(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol eventSymbol => eventSymbol.OverriddenEvent,
            _ => null
        };
    }

    private TypeMemberInfo? CreateMemberInfo(ISymbol member)
    {
        var info = new TypeMemberInfo
        {
            Name = member.Name,
            Kind = GetMemberKind(member),
            Accessibility = member.DeclaredAccessibility.ToString(),
            IsStatic = member.IsStatic,
            IsAbstract = member.IsAbstract,
            IsVirtual = member.IsVirtual,
            IsOverride = member.IsOverride,
            IsSealed = member.IsSealed,
            Documentation = GetDocumentation(member)
        };

        // Get location if available
        var location = member.Locations.FirstOrDefault(l => l.IsInSource);
        if (location != null)
        {
            var lineSpan = location.GetLineSpan();
            info.Location = new LocationInfo
            {
                FilePath = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                EndColumn = lineSpan.EndLinePosition.Character + 1
            };
        }

        // Get member-specific information
        switch (member)
        {
            case IMethodSymbol method:
                info.Signature = GetMethodSignature(method);
                info.ReturnType = method.ReturnType.ToDisplayString();
                info.Parameters = method.Parameters.Select(p => new MemberParameterInfo
                {
                    Name = p.Name,
                    Type = p.Type.ToDisplayString(),
                    IsOptional = p.IsOptional,
                    HasDefaultValue = p.HasExplicitDefaultValue,
                    DefaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() : null
                }).ToList();
                info.TypeParameters = method.TypeParameters.Select(tp => tp.Name).ToList();
                break;

            case IPropertySymbol property:
                info.Signature = GetPropertySignature(property);
                info.ReturnType = property.Type.ToDisplayString();
                info.HasGetter = property.GetMethod != null;
                info.HasSetter = property.SetMethod != null;
                info.IsReadOnly = property.IsReadOnly;
                info.IsWriteOnly = property.IsWriteOnly;
                break;

            case IFieldSymbol field:
                info.Signature = GetFieldSignature(field);
                info.ReturnType = field.Type.ToDisplayString();
                info.IsReadOnly = field.IsReadOnly;
                info.IsConst = field.IsConst;
                if (field.IsConst && field.HasConstantValue)
                {
                    info.ConstantValue = field.ConstantValue?.ToString();
                }
                break;

            case IEventSymbol eventSymbol:
                info.Signature = GetEventSignature(eventSymbol);
                info.ReturnType = eventSymbol.Type.ToDisplayString();
                break;

            case INamedTypeSymbol type:
                info.Signature = type.ToDisplayString();
                info.TypeParameters = type.TypeParameters.Select(tp => tp.Name).ToList();
                break;

            default:
                info.Signature = member.ToDisplayString();
                break;
        }

        return info;
    }

    private string GetMemberKind(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => method.MethodKind switch
            {
                MethodKind.Constructor => "Constructor",
                MethodKind.Destructor => "Destructor",
                MethodKind.UserDefinedOperator => "Operator",
                MethodKind.Conversion => "Conversion",
                _ => "Method"
            },
            IPropertySymbol => "Property",
            IFieldSymbol => "Field",
            IEventSymbol => "Event",
            INamedTypeSymbol => "NestedType",
            _ => member.Kind.ToString()
        };
    }

    private string GetMethodSignature(IMethodSymbol method)
    {
        var sb = new StringBuilder();
        
        // Modifiers
        if (method.IsStatic) sb.Append("static ");
        if (method.IsAbstract) sb.Append("abstract ");
        if (method.IsVirtual && !method.IsOverride) sb.Append("virtual ");
        if (method.IsOverride) sb.Append("override ");
        if (method.IsSealed) sb.Append("sealed ");
        if (method.IsAsync) sb.Append("async ");
        
        // Return type and name
        if (method.MethodKind != MethodKind.Constructor && method.MethodKind != MethodKind.Destructor)
        {
            sb.Append(method.ReturnType.ToDisplayString()).Append(" ");
        }
        
        sb.Append(method.Name);
        
        // Type parameters
        if (method.IsGenericMethod)
        {
            sb.Append("<");
            sb.Append(string.Join(", ", method.TypeParameters.Select(tp => tp.Name)));
            sb.Append(">");
        }
        
        // Parameters
        sb.Append("(");
        sb.Append(string.Join(", ", method.Parameters.Select(p => 
        {
            var param = p.Type.ToDisplayString() + " " + p.Name;
            if (p.HasExplicitDefaultValue)
            {
                param += " = " + (p.ExplicitDefaultValue?.ToString() ?? "null");
            }
            return param;
        })));
        sb.Append(")");
        
        return sb.ToString();
    }

    private string GetPropertySignature(IPropertySymbol property)
    {
        var sb = new StringBuilder();
        
        // Modifiers
        if (property.IsStatic) sb.Append("static ");
        if (property.IsAbstract) sb.Append("abstract ");
        if (property.IsVirtual && !property.IsOverride) sb.Append("virtual ");
        if (property.IsOverride) sb.Append("override ");
        if (property.IsSealed) sb.Append("sealed ");
        if (property.IsReadOnly) sb.Append("readonly ");
        
        // Type and name
        sb.Append(property.Type.ToDisplayString()).Append(" ");
        sb.Append(property.Name);
        
        // Accessors
        sb.Append(" { ");
        if (property.GetMethod != null)
        {
            if (property.GetMethod.DeclaredAccessibility != property.DeclaredAccessibility)
                sb.Append(property.GetMethod.DeclaredAccessibility.ToString().ToLower()).Append(" ");
            sb.Append("get; ");
        }
        if (property.SetMethod != null)
        {
            if (property.SetMethod.DeclaredAccessibility != property.DeclaredAccessibility)
                sb.Append(property.SetMethod.DeclaredAccessibility.ToString().ToLower()).Append(" ");
            sb.Append("set; ");
        }
        sb.Append("}");
        
        return sb.ToString();
    }

    private string GetFieldSignature(IFieldSymbol field)
    {
        var sb = new StringBuilder();
        
        // Modifiers
        if (field.IsStatic) sb.Append("static ");
        if (field.IsReadOnly) sb.Append("readonly ");
        if (field.IsConst) sb.Append("const ");
        if (field.IsVolatile) sb.Append("volatile ");
        
        // Type and name
        sb.Append(field.Type.ToDisplayString()).Append(" ");
        sb.Append(field.Name);
        
        // Const value
        if (field.IsConst && field.HasConstantValue)
        {
            sb.Append(" = ").Append(field.ConstantValue?.ToString() ?? "null");
        }
        
        return sb.ToString();
    }

    private string GetEventSignature(IEventSymbol eventSymbol)
    {
        var sb = new StringBuilder();
        
        // Modifiers
        if (eventSymbol.IsStatic) sb.Append("static ");
        if (eventSymbol.IsAbstract) sb.Append("abstract ");
        if (eventSymbol.IsVirtual && !eventSymbol.IsOverride) sb.Append("virtual ");
        if (eventSymbol.IsOverride) sb.Append("override ");
        if (eventSymbol.IsSealed) sb.Append("sealed ");
        
        // Event keyword, type and name
        sb.Append("event ");
        sb.Append(eventSymbol.Type.ToDisplayString()).Append(" ");
        sb.Append(eventSymbol.Name);
        
        return sb.ToString();
    }

    private string? GetDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        // Simple XML parsing - in production, use proper XML parsing
        var summary = ExtractXmlTag(xml, "summary");
        var returns = ExtractXmlTag(xml, "returns");
        var remarks = ExtractXmlTag(xml, "remarks");

        var doc = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            doc.AppendLine(summary.Trim());
        }
        if (!string.IsNullOrWhiteSpace(returns))
        {
            doc.AppendLine($"Returns: {returns.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(remarks))
        {
            doc.AppendLine($"Remarks: {remarks.Trim()}");
        }

        return doc.Length > 0 ? doc.ToString().Trim() : null;
    }

    private string? ExtractXmlTag(string xml, string tagName)
    {
        var startTag = $"<{tagName}>";
        var endTag = $"</{tagName}>";
        
        var startIndex = xml.IndexOf(startTag);
        if (startIndex < 0) return null;
        
        startIndex += startTag.Length;
        var endIndex = xml.IndexOf(endTag, startIndex);
        if (endIndex < 0) return null;
        
        return xml.Substring(startIndex, endIndex - startIndex)
            .Replace("\r\n", " ")
            .Replace("\n", " ")
            .Replace("  ", " ")
            .Trim();
    }

    private List<TypeMemberInfo> SortMembers(List<TypeMemberInfo> members, string sortBy)
    {
        return sortBy.ToLower() switch
        {
            "name" => members.OrderBy(m => m.Name).ToList(),
            "kind" => members.OrderBy(m => m.Kind).ThenBy(m => m.Name).ToList(),
            "accessibility" => members.OrderBy(m => GetAccessibilityOrder(m.Accessibility)).ThenBy(m => m.Name).ToList(),
            _ => members
        };
    }

    private int GetAccessibilityOrder(string accessibility)
    {
        return accessibility switch
        {
            "Public" => 0,
            "Protected" => 1,
            "Internal" => 2,
            "ProtectedOrInternal" => 3,
            "ProtectedAndInternal" => 4,
            "Private" => 5,
            _ => 6
        };
    }

    private List<string> GenerateInsights(INamedTypeSymbol typeSymbol, List<TypeMemberInfo> members)
    {
        var insights = new List<string>();

        // Type information
        if (typeSymbol.IsAbstract)
        {
            insights.Add("Abstract type - cannot be instantiated directly");
        }
        if (typeSymbol.IsSealed)
        {
            insights.Add("Sealed type - cannot be inherited");
        }
        if (typeSymbol.IsStatic)
        {
            insights.Add("Static type - contains only static members");
        }

        // Member statistics
        var membersByKind = members.GroupBy(m => m.Kind);
        var stats = string.Join(", ", membersByKind.Select(g => $"{g.Count()} {g.Key.ToLower()}s"));
        insights.Add($"Contains {stats}");

        // Public API surface
        var publicMembers = members.Count(m => m.Accessibility == "Public");
        if (publicMembers > 0)
        {
            insights.Add($"{publicMembers} public members exposed as API");
        }

        // Virtual members
        var virtualMembers = members.Count(m => m.IsVirtual || m.IsAbstract);
        if (virtualMembers > 0)
        {
            insights.Add($"{virtualMembers} virtual/abstract members can be overridden");
        }

        // Documentation coverage
        var documented = members.Count(m => !string.IsNullOrEmpty(m.Documentation));
        if (documented < members.Count)
        {
            var percentage = (documented * 100) / members.Count;
            insights.Add($"Documentation coverage: {percentage}% ({documented}/{members.Count})");
        }

        return insights;
    }

    private List<NextAction> GenerateNextActions(INamedTypeSymbol typeSymbol, List<TypeMemberInfo> members)
    {
        var actions = new List<NextAction>();

        // Suggest finding implementations if abstract or interface
        if (typeSymbol.IsAbstract || typeSymbol.TypeKind == TypeKind.Interface)
        {
            var location = typeSymbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (location != null)
            {
                var lineSpan = location.GetLineSpan();
                actions.Add(new NextAction
                {
                    Id = "find_implementations",
                    Description = $"Find implementations of {typeSymbol.Name}",
                    ToolName = "roslyn_find_implementations",
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

        // Suggest exploring key methods
        var publicMethods = members
            .Where(m => m.Kind == "Method" && m.Accessibility == "Public" && m.Location != null)
            .Take(2);

        foreach (var method in publicMethods)
        {
            actions.Add(new NextAction
            {
                Id = $"trace_{method.Name.ToLower()}",
                Description = $"Trace calls to {method.Name}",
                ToolName = "roslyn_trace_call_stack",
                Parameters = new
                {
                    filePath = method.Location!.FilePath,
                    line = method.Location.Line,
                    column = method.Location.Column,
                    direction = "backward"
                },
                Priority = "medium"
            });
        }

        // Suggest finding references for the type
        var typeLocation = typeSymbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (typeLocation != null)
        {
            var lineSpan = typeLocation.GetLineSpan();
            actions.Add(new NextAction
            {
                Id = "find_type_usage",
                Description = $"Find where {typeSymbol.Name} is used",
                ToolName = "roslyn_find_all_references",
                Parameters = new
                {
                    filePath = lineSpan.Path,
                    line = lineSpan.StartLinePosition.Line + 1,
                    column = lineSpan.StartLinePosition.Character + 1
                },
                Priority = "low"
            });
        }

        return actions;
    }
}

public class GetTypeMembersParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file")]
    public required string FilePath { get; set; }

    [JsonPropertyName("line")]
    [Description("Line number (1-based) where the type is defined")]
    public required int Line { get; set; }

    [JsonPropertyName("column")]
    [Description("Column number (1-based) where the type is defined")]
    public required int Column { get; set; }

    [JsonPropertyName("includeInherited")]
    [Description("Include inherited members from base classes and interfaces (default: false)")]
    public bool? IncludeInherited { get; set; }

    [JsonPropertyName("includePrivate")]
    [Description("Include private members (default: false)")]
    public bool? IncludePrivate { get; set; }

    [JsonPropertyName("includeProtected")]
    [Description("Include protected members (default: true)")]
    public bool? IncludeProtected { get; set; }

    [JsonPropertyName("includeCompilerGenerated")]
    [Description("Include compiler-generated members (default: false)")]
    public bool? IncludeCompilerGenerated { get; set; }

    [JsonPropertyName("memberKinds")]
    [Description("Filter by member kinds: 'Method', 'Property', 'Field', 'Event', 'Constructor', 'NestedType'")]
    public string[]? MemberKinds { get; set; }

    [JsonPropertyName("sortBy")]
    [Description("Sort members by: 'Name', 'Kind', 'Accessibility' (default: 'Kind')")]
    public string? SortBy { get; set; }
}

public class GetTypeMembersResult
{
    public bool Found { get; set; }
    public string? TypeName { get; set; }
    public string? TypeKind { get; set; }
    public string? SymbolName { get; set; }
    public string? SymbolKind { get; set; }
    public int TotalMembers { get; set; }
    public List<TypeMemberInfo>? Members { get; set; }
    public string? Message { get; set; }
    public List<string>? Insights { get; set; }
    public List<NextAction>? NextActions { get; set; }
    public ErrorInfo? Error { get; set; }
    public string? ResourceUri { get; set; }
}

public class TypeMemberInfo
{
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public string? Signature { get; set; }
    public required string Accessibility { get; set; }
    public LocationInfo? Location { get; set; }
    public string? Documentation { get; set; }
    public string? ReturnType { get; set; }
    public List<MemberParameterInfo>? Parameters { get; set; }
    public List<string>? TypeParameters { get; set; }
    public bool IsStatic { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsVirtual { get; set; }
    public bool IsOverride { get; set; }
    public bool IsSealed { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsWriteOnly { get; set; }
    public bool IsConst { get; set; }
    public string? ConstantValue { get; set; }
    public bool HasGetter { get; set; }
    public bool HasSetter { get; set; }
    public bool IsInherited { get; set; }
    public string? DeclaringType { get; set; }
    public bool IsInterfaceMember { get; set; }
}

public class MemberParameterInfo
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public bool IsOptional { get; set; }
    public bool HasDefaultValue { get; set; }
    public string? DefaultValue { get; set; }
}