using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that lists all members of a type with documentation using COA.Mcp.Framework v1.1.0
/// </summary>
public class GetTypeMembersTool : McpToolBase<GetTypeMembersParams, GetTypeMembersToolResult>
{
    private readonly ILogger<GetTypeMembersTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => ToolNames.GetTypeMembers;
    public override string Description => @"Verify type members BEFORE writing code. Shows exact property names, method signatures, and accessibility to prevent compilation errors.

Critical: When user mentions 'implement X' or 'use this class', verify the type FIRST. Prevents wrong assumptions about property names (fullName vs firstName), method signatures (async vs sync), and missing members.

Prerequisites: Call csharp_load_solution or csharp_load_project first.
Use cases: Before implementing interfaces, calling methods, or using any unfamiliar type.";

    public GetTypeMembersTool(
        ILogger<GetTypeMembersTool> logger,
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

    protected override async Task<GetTypeMembersToolResult> ExecuteInternalAsync(
        GetTypeMembersParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        _logger.LogDebug("GetTypeMembers request received: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);

        // Get the document
        _logger.LogDebug("Retrieving document from workspace: {FilePath}", parameters.FilePath);
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
            return new GetTypeMembersToolResult
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
            return new GetTypeMembersToolResult
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
            return new GetTypeMembersToolResult
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
            return new GetTypeMembersToolResult
            {
                Success = false,
                Message = $"Symbol '{symbol.Name}' is not a type or is not contained in a type",
                Insights = new List<string>
                {
                    "This tool works with classes, interfaces, structs, records, and enums",
                    "For namespace members, use Document Symbols tool"
                }
            };
        }

        // Get all members
        var allMembers = new List<TypeMemberInfo>();
        var allSymbolMembers = typeSymbol.GetMembers();

        foreach (var member in allSymbolMembers)
        {
            // Skip compiler-generated members unless requested
            if (!parameters.IncludeCompilerGenerated && IsCompilerGenerated(member))
                continue;

            // Apply filters
            if (!ShouldIncludeMember(member, parameters))
                continue;

            var memberInfo = CreateMemberInfo(member, parameters.IncludeDocumentation);
            if (memberInfo != null)
            {
                allMembers.Add(memberInfo);
            }
        }

        // Include inherited members if requested
        if (parameters.IncludeInherited)
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

                    var memberInfo = CreateMemberInfo(member, parameters.IncludeDocumentation);
                    if (memberInfo != null)
                    {
                        memberInfo.IsInherited = true;
                        memberInfo.DeclaringType = baseType.ToDisplayString();
                        allMembers.Add(memberInfo);
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

                    var memberInfo = CreateMemberInfo(member, parameters.IncludeDocumentation);
                    if (memberInfo != null)
                    {
                        memberInfo.IsInherited = true;
                        memberInfo.DeclaringType = iface.ToDisplayString();
                        memberInfo.IsInterfaceMember = true;
                        allMembers.Add(memberInfo);
                    }
                }
            }
        }

        // Sort members
        allMembers = SortMembers(allMembers, parameters.SortBy ?? "Kind");
        
        // Apply framework token optimization before max results limit
        var requestedMaxResults = parameters.MaxResults;
        List<TypeMemberInfo> returnedMembers;
        var tokenLimitApplied = false;
        
        // Use framework's token estimation
        var estimatedTokens = _tokenEstimator.EstimateObject(allMembers);
        if (estimatedTokens > 10000)
        {
            // Use framework's progressive reduction
            returnedMembers = _tokenEstimator.ApplyProgressiveReduction(
                allMembers,
                member => _tokenEstimator.EstimateObject(member),
                10000,
                new[] { 100, 75, 50, 25, 10 } // Progressive reduction steps
            );
            tokenLimitApplied = true;
            
            _logger.LogWarning("Token optimization applied: reducing members from {Total} to {Safe}", 
                allMembers.Count, returnedMembers.Count);
        }
        else
        {
            returnedMembers = allMembers;
        }
        
        // Then apply max results limit
        var effectiveMaxResults = Math.Min(requestedMaxResults, returnedMembers.Count);
        if (returnedMembers.Count > effectiveMaxResults)
        {
            returnedMembers = returnedMembers.Take(effectiveMaxResults).ToList();
        }
        
        var wasTruncated = returnedMembers.Count < allMembers.Count;

        // Generate insights
        var insights = GenerateInsights(typeSymbol, allMembers, wasTruncated, parameters.IncludeDocumentation);
        
        // Add insight about token optimization if applied
        if (tokenLimitApplied)
        {
            insights.Insert(0, $"⚠️ Token optimization applied. Showing {returnedMembers.Count} of {allMembers.Count} members.");
        }
        
        // Generate next actions
        var actions = GenerateNextActions(typeSymbol, returnedMembers, wasTruncated, parameters);

        // Create distribution
        var distribution = CreateDistribution(allMembers);

        // Store full result if truncated
        string? resourceUri = null;
        if (wasTruncated && _resourceProvider != null)
        {
            resourceUri = _resourceProvider.StoreAnalysisResult("type-members",
                new { type = typeSymbol.ToDisplayString(), members = allMembers, totalCount = allMembers.Count },
                $"All {allMembers.Count} members of {typeSymbol.Name}");
        }

        return new GetTypeMembersToolResult
        {
            Success = true,
            Message = wasTruncated 
                ? $"Found {allMembers.Count} members - showing {returnedMembers.Count}"
                : $"Found {allMembers.Count} members in '{typeSymbol.Name}'",
            Query = new GetTypeMembersQuery
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                TargetSymbol = typeSymbol.ToDisplayString(),
                IncludeInherited = parameters.IncludeInherited,
                IncludePrivate = parameters.IncludePrivate,
                IncludeDocumentation = parameters.IncludeDocumentation,
                MemberKinds = parameters.MemberKinds?.ToList(),
                SortBy = parameters.SortBy,
                MaxResults = parameters.MaxResults
            },
            Summary = new GetTypeMembersSummary
            {
                TypeName = typeSymbol.ToDisplayString(),
                TypeKind = typeSymbol.TypeKind.ToString(),
                TotalMembers = allMembers.Count,
                PublicMembers = allMembers.Count(m => m.Accessibility == "Public"),
                VirtualMembers = allMembers.Count(m => m.IsVirtual || m.IsAbstract),
                InheritedMembers = allMembers.Count(m => m.IsInherited),
                Returned = returnedMembers.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            },
            Members = returnedMembers,
            ResultsSummary = new ResultsSummary
            {
                Total = allMembers.Count,
                Included = returnedMembers.Count,
                HasMore = wasTruncated
            },
            Distribution = distribution,
            ResourceUri = resourceUri,
            Insights = insights,
            Actions = actions,
            Meta = new ToolExecutionMetadata 
            { 
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
            }
        };
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
        if (!parameters.IncludePrivate && member.DeclaredAccessibility == Accessibility.Private)
            return false;

        if (!parameters.IncludeProtected && member.DeclaredAccessibility == Accessibility.Protected)
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

    private TypeMemberInfo? CreateMemberInfo(ISymbol member, bool includeDocumentation = true)
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
            Documentation = includeDocumentation ? GetDocumentation(member) : null
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

    private List<string> GenerateInsights(INamedTypeSymbol typeSymbol, List<TypeMemberInfo> members, bool wasTruncated, bool includeDocumentation)
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
        if (includeDocumentation)
        {
            var documented = members.Count(m => !string.IsNullOrEmpty(m.Documentation));
            if (documented < members.Count)
            {
                var percentage = (documented * 100) / members.Count;
                insights.Add($"Documentation coverage: {percentage}% ({documented}/{members.Count})");
            }
        }

        if (wasTruncated)
        {
            insights.Add($"Response truncated to stay within token limits");
            if (includeDocumentation)
            {
                insights.Add("Set includeDocumentation=false to see more members within token limits");
            }
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(INamedTypeSymbol typeSymbol, List<TypeMemberInfo> members, bool wasTruncated, GetTypeMembersParams originalParams)
    {
        var actions = new List<AIAction>();

        // Suggest getting more results if truncated
        if (wasTruncated)
        {
            actions.Add(new AIAction
            {
                Action = ToolNames.GetTypeMembers,
                Description = "Get additional members without documentation",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = originalParams.FilePath,
                    ["line"] = originalParams.Line,
                    ["column"] = originalParams.Column,
                    ["maxResults"] = Math.Min(originalParams.MaxResults * 2, 500),
                    ["includeDocumentation"] = false
                },
                Priority = 95,
                Category = "pagination"
            });
        }

        // Suggest finding implementations if abstract or interface
        if (typeSymbol.IsAbstract || typeSymbol.TypeKind == TypeKind.Interface)
        {
            var location = typeSymbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (location != null)
            {
                var lineSpan = location.GetLineSpan();
                actions.Add(new AIAction
                {
                    Action = ToolNames.FindImplementations,
                    Description = $"Find implementations of {typeSymbol.Name}",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = lineSpan.Path,
                        ["line"] = lineSpan.StartLinePosition.Line + 1,
                        ["column"] = lineSpan.StartLinePosition.Character + 1
                    },
                    Priority = 90,
                    Category = "navigation"
                });
            }
        }

        // Suggest exploring key methods
        var publicMethods = members
            .Where(m => m.Kind == "Method" && m.Accessibility == "Public" && m.Location != null)
            .Take(2);

        foreach (var method in publicMethods)
        {
            actions.Add(new AIAction
            {
                Action = ToolNames.TraceCallStack,
                Description = $"Trace calls to {method.Name}",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = method.Location!.FilePath,
                    ["line"] = method.Location.Line,
                    ["column"] = method.Location.Column,
                    ["direction"] = "backward"
                },
                Priority = 70,
                Category = "analysis"
            });
        }

        // Suggest finding references for the type
        var typeLocation = typeSymbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (typeLocation != null)
        {
            var lineSpan = typeLocation.GetLineSpan();
            actions.Add(new AIAction
            {
                Action = ToolNames.FindAllReferences,
                Description = $"Find where {typeSymbol.Name} is used",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = lineSpan.Path,
                    ["line"] = lineSpan.StartLinePosition.Line + 1,
                    ["column"] = lineSpan.StartLinePosition.Character + 1
                },
                Priority = 60,
                Category = "navigation"
            });
        }

        return actions;
    }

    private TypeMembersDistribution CreateDistribution(List<TypeMemberInfo> members)
    {
        return new TypeMembersDistribution
        {
            ByKind = members.GroupBy(m => m.Kind)
                           .ToDictionary(g => g.Key, g => g.Count()),
            ByAccessibility = members.GroupBy(m => m.Accessibility)
                                   .ToDictionary(g => g.Key, g => g.Count()),
            BySource = members.GroupBy(m => m.IsInherited ? "Inherited" : "Own")
                             .ToDictionary(g => g.Key, g => g.Count())
        };
    }

}

/// <summary>
/// Parameters for GetTypeMembers tool
/// </summary>
public class GetTypeMembersParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file")]
    public string FilePath { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Line must be positive")]
    [JsonPropertyName("line")]
    [COA.Mcp.Framework.Attributes.Description("Line number (1-based) where the type is defined")]
    public int Line { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Column must be positive")]
    [JsonPropertyName("column")]
    [COA.Mcp.Framework.Attributes.Description("Column number (1-based) where the type is defined")]
    public int Column { get; set; }

    [JsonPropertyName("includeInherited")]
    [COA.Mcp.Framework.Attributes.Description("Include inherited members from base classes and interfaces (default: false)")]
    public bool IncludeInherited { get; set; }

    [JsonPropertyName("includePrivate")]
    [COA.Mcp.Framework.Attributes.Description("Include private members (default: false)")]
    public bool IncludePrivate { get; set; }

    [JsonPropertyName("includeProtected")]
    [COA.Mcp.Framework.Attributes.Description("Include protected members (default: true)")]
    public bool IncludeProtected { get; set; } = true;

    [JsonPropertyName("includeCompilerGenerated")]
    [COA.Mcp.Framework.Attributes.Description("Include compiler-generated members (default: false)")]
    public bool IncludeCompilerGenerated { get; set; }
    
    [JsonPropertyName("includeDocumentation")]
    [COA.Mcp.Framework.Attributes.Description("Include XML documentation (default: true). Set to false to see more members within token limits.")]
    public bool IncludeDocumentation { get; set; } = true;

    [JsonPropertyName("memberKinds")]
    [COA.Mcp.Framework.Attributes.Description("Filter by member kinds: 'Method', 'Property', 'Field', 'Event', 'Constructor', 'NestedType'")]
    public string[]? MemberKinds { get; set; }

    [JsonPropertyName("sortBy")]
    [COA.Mcp.Framework.Attributes.Description("Sort members by: 'Name', 'Kind', 'Accessibility' (default: 'Kind')")]
    public string? SortBy { get; set; }
    
    [System.ComponentModel.DataAnnotations.Range(1, 500, ErrorMessage = "MaxResults must be between 1 and 500")]
    [JsonPropertyName("maxResults")]
    [COA.Mcp.Framework.Attributes.Description("Maximum number of members to return (default: 100, max: 500)")]
    public int MaxResults { get; set; } = 100;
}