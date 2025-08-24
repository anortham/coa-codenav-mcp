using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that provides extract interface refactoring functionality using Roslyn
/// </summary>
public class ExtractInterfaceTool : McpToolBase<ExtractInterfaceParams, ExtractInterfaceResult>
{
    private readonly ILogger<ExtractInterfaceTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;
    private readonly ExtractInterfaceResponseBuilder _responseBuilder;

    public override string Name => ToolNames.ExtractInterface;
    public override string Description => "Extract an interface from a class to improve testability and dependency inversion. Creates clean contracts from existing implementations.";
    public override ToolCategory Category => ToolCategory.Refactoring;
    
    public ExtractInterfaceTool(
        IServiceProvider serviceProvider,
        ILogger<ExtractInterfaceTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        ITokenEstimator tokenEstimator,
        ExtractInterfaceResponseBuilder responseBuilder,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(serviceProvider, logger)
    {
        _logger = logger;
        _tokenEstimator = tokenEstimator;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
        _responseBuilder = responseBuilder;
    }

    protected override async Task<ExtractInterfaceResult> ExecuteInternalAsync(
        ExtractInterfaceParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("ExtractInterface request received: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);
        
        var startTime = DateTime.UtcNow;

        // Get the document
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            return new ExtractInterfaceResult
            {
                Success = false,
                Message = $"Document not found: {parameters.FilePath}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.DOCUMENT_NOT_FOUND,
                    Message = $"Document not found: {parameters.FilePath}",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new string[]
                        {
                            "Ensure the file path is correct and absolute",
                            "Verify the file exists in the loaded solution/project",
                            "Load a solution using csharp_load_solution or project using csharp_load_project"
                        }
                    }
                }
            };
        }

        // Get semantic model and syntax tree
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
        
        if (semanticModel == null || syntaxTree == null)
        {
            return new ExtractInterfaceResult
            {
                Success = false,
                Message = "Failed to get semantic model or syntax tree",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.COMPILATION_ERROR,
                    Message = "Failed to get semantic model or syntax tree"
                }
            };
        }

        // Find the type at the specified position
        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines[parameters.Line - 1].Start + (parameters.Column - 1);
        
        var node = syntaxTree.GetRoot(cancellationToken).FindToken(position).Parent;
        var typeDeclaration = node?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        
        if (typeDeclaration == null)
        {
            return new ExtractInterfaceResult
            {
                Success = false,
                Message = "No type declaration found at the specified position",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SYMBOL_NOT_FOUND,
                    Message = "No type declaration found at the specified position"
                }
            };
        }

        // Get the type symbol
        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            return new ExtractInterfaceResult
            {
                Success = false,
                Message = "Could not resolve type symbol",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SYMBOL_NOT_FOUND,
                    Message = "Could not resolve type symbol"
                }
            };
        }

        // Generate interface name if not provided
        var interfaceName = parameters.InterfaceName ?? GenerateInterfaceName(typeSymbol.Name);
        
        // Get extractable members
        var extractableMembers = GetExtractableMembers(typeSymbol, parameters.MemberNames);
        
        if (!extractableMembers.Any())
        {
            return new ExtractInterfaceResult
            {
                Success = false,
                Message = "No suitable members found for interface extraction",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INVALID_OPERATION,
                    Message = "No suitable members found for interface extraction. Only public methods, properties, and events can be extracted to an interface."
                }
            };
        }

        try
        {
            // Generate the interface code
            var interfaceCode = GenerateInterfaceCode(interfaceName, extractableMembers, typeSymbol.ContainingNamespace?.ToDisplayString(), typeSymbol);
            
            // Generate the updated class code (if requested)
            string? updatedClassCode = null;
            if (parameters.UpdateClass != false) // default to true
            {
                updatedClassCode = await GenerateUpdatedClassCode(document, typeDeclaration, interfaceName, typeSymbol, cancellationToken);
            }

            var result = new ExtractInterfaceResult
            {
                Success = true,
                Message = $"Successfully extracted interface '{interfaceName}' with {extractableMembers.Count} members",
                InterfaceName = interfaceName,
                InterfaceCode = interfaceCode,
                UpdatedClassCode = updatedClassCode,
                ExtractedMembers = extractableMembers.Select(m => new ExtractedMemberInfo
                {
                    Name = m.Name,
                    Kind = m.Kind.ToString(),
                    Signature = GetMemberSignature(m)
                }).ToList(),
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
                },
                Summary = new SummaryInfo
                {
                    TotalFound = extractableMembers.Count,
                    Returned = extractableMembers.Count,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                },
                Meta = new ToolExecutionMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                }
            };

            // Use ResponseBuilder for token optimization and AI-friendly formatting
            var context = new COA.Mcp.Framework.TokenOptimization.ResponseBuilders.ResponseContext
            {
                ResponseMode = "optimized",
                TokenLimit = 10000, // Fixed token limit for consistent optimization
                ToolName = Name
            };

            return await _responseBuilder.BuildResponseAsync(result, context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during interface extraction");
            return new ExtractInterfaceResult
            {
                Success = false,
                Message = $"Error during interface extraction: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Message = ex.Message
                }
            };
        }
    }

    private string GenerateInterfaceName(string className)
    {
        // Remove common class suffixes and add I prefix
        var name = className;
        var suffixes = new[] { "Service", "Manager", "Provider", "Handler", "Controller", "Repository" };
        
        foreach (var suffix in suffixes)
        {
            if (name.EndsWith(suffix) && name.Length > suffix.Length)
            {
                name = name.Substring(0, name.Length - suffix.Length);
                break;
            }
        }
        
        return "I" + name;
    }

    private List<ISymbol> GetExtractableMembers(INamedTypeSymbol typeSymbol, string[]? memberNames)
    {
        var allMembers = typeSymbol.GetMembers()
            .Where(m => m.DeclaredAccessibility == Accessibility.Public && 
                       !m.IsStatic && 
                       (m.Kind == SymbolKind.Method || 
                        m.Kind == SymbolKind.Property || 
                        m.Kind == SymbolKind.Event))
            .Where(m => m.Kind != SymbolKind.Method || 
                       (!((IMethodSymbol)m).IsImplicitlyDeclared && 
                        !m.Name.StartsWith("get_") && 
                        !m.Name.StartsWith("set_") &&
                        !m.Name.StartsWith("add_") && 
                        !m.Name.StartsWith("remove_")))
            .ToList();

        if (memberNames?.Any() == true)
        {
            return allMembers.Where(m => memberNames.Contains(m.Name)).ToList();
        }

        return allMembers;
    }

    private string GenerateInterfaceCode(string interfaceName, List<ISymbol> members, string? namespaceName, INamedTypeSymbol typeSymbol)
    {
        var sb = new StringBuilder();
        
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.AppendLine($"namespace {namespaceName};");
            sb.AppendLine();
        }

        // Generate interface declaration with generic parameters and constraints
        var interfaceDeclaration = GenerateInterfaceDeclaration(interfaceName, typeSymbol);
        sb.AppendLine(interfaceDeclaration);
        sb.AppendLine("{");

        foreach (var member in members.OrderBy(m => m.Kind).ThenBy(m => m.Name))
        {
            var signature = GetInterfaceMemberSignature(member);
            sb.AppendLine($"    {signature};");
        }

        sb.AppendLine("}");
        
        return sb.ToString();
    }

    private string GenerateInterfaceDeclaration(string interfaceName, INamedTypeSymbol typeSymbol)
    {
        var sb = new StringBuilder();
        sb.Append($"public interface {interfaceName}");

        // Add generic type parameters
        if (typeSymbol.TypeParameters.Any())
        {
            sb.Append("<");
            sb.Append(string.Join(", ", typeSymbol.TypeParameters.Select(tp => tp.Name)));
            sb.Append(">");
        }

        // Add type parameter constraints
        if (typeSymbol.TypeParameters.Any())
        {
            foreach (var typeParam in typeSymbol.TypeParameters)
            {
                if (typeParam.HasReferenceTypeConstraint || typeParam.HasValueTypeConstraint || typeParam.HasUnmanagedTypeConstraint || typeParam.ConstraintTypes.Any())
                {
                    sb.Append($" where {typeParam.Name} : ");
                    
                    var constraints = new List<string>();
                    
                    if (typeParam.HasReferenceTypeConstraint)
                        constraints.Add("class");
                    else if (typeParam.HasValueTypeConstraint)
                        constraints.Add("struct");
                    else if (typeParam.HasUnmanagedTypeConstraint)
                        constraints.Add("unmanaged");
                    
                    foreach (var constraintType in typeParam.ConstraintTypes)
                    {
                        constraints.Add(constraintType.ToDisplayString());
                    }
                    
                    if (typeParam.HasConstructorConstraint)
                        constraints.Add("new()");
                    
                    sb.Append(string.Join(", ", constraints));
                }
            }
        }

        return sb.ToString();
    }

    private string GenerateFullInterfaceName(string interfaceName, INamedTypeSymbol typeSymbol)
    {
        if (!typeSymbol.TypeParameters.Any())
        {
            return interfaceName;
        }

        return $"{interfaceName}<{string.Join(", ", typeSymbol.TypeParameters.Select(tp => tp.Name))}>";
    }

    private async Task<string> GenerateUpdatedClassCode(Document document, TypeDeclarationSyntax typeDeclaration, string interfaceName, INamedTypeSymbol typeSymbol, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "";

        // Add interface to base list
        var updatedType = typeDeclaration;
        
        // Generate full interface name with generic parameters
        var fullInterfaceName = GenerateFullInterfaceName(interfaceName, typeSymbol);
        
        if (updatedType.BaseList == null)
        {
            var newBaseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(fullInterfaceName));
            updatedType = updatedType.WithBaseList(
                SyntaxFactory.BaseList(
                    SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(newBaseType)));
        }
        else
        {
            var newBaseType = SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(fullInterfaceName));
            updatedType = updatedType.WithBaseList(
                updatedType.BaseList.AddTypes(newBaseType));
        }

        var newRoot = root.ReplaceNode(typeDeclaration, updatedType);
        
        // Apply formatting but fix the inheritance line manually
        var formatted = Formatter.Format(newRoot, document.Project.Solution.Workspace);
        var result = formatted.ToFullString();
        
        // Post-process to fix inheritance formatting - move inheritance to same line as class declaration
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(public class \w+)\s*\n\s*:\s*(\w+)",
            "$1 : $2",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        
        return result;
    }

    private string GetMemberSignature(ISymbol member)
    {
        return member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    private string GetInterfaceMemberSignature(ISymbol member)
    {
        switch (member.Kind)
        {
            case SymbolKind.Method:
                var method = (IMethodSymbol)member;
                var parameters = string.Join(", ", method.Parameters.Select(p => 
                    $"{(p.RefKind != RefKind.None ? p.RefKind.ToString().ToLower() + " " : "")}{p.Type.ToDisplayString()} {p.Name}"));
                return $"{method.ReturnType.ToDisplayString()} {method.Name}({parameters})";
                
            case SymbolKind.Property:
                var property = (IPropertySymbol)member;
                var accessors = new List<string>();
                if (property.GetMethod != null && property.GetMethod.DeclaredAccessibility == Accessibility.Public)
                    accessors.Add("get");
                if (property.SetMethod != null && property.SetMethod.DeclaredAccessibility == Accessibility.Public)
                    accessors.Add("set");
                return $"{property.Type.ToDisplayString()} {property.Name} {{ {string.Join("; ", accessors)}; }}";
                
            case SymbolKind.Event:
                var eventSymbol = (IEventSymbol)member;
                return $"event {eventSymbol.Type.ToDisplayString()} {eventSymbol.Name}";
                
            default:
                return member.ToDisplayString();
        }
    }
}

public class ExtractInterfaceParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [Description("Path to the source file containing the class")]
    public string FilePath { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Line must be positive")]
    [JsonPropertyName("line")]
    [Description("Line number where the class is located (1-based)")]
    public int Line { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Column must be positive")]
    [JsonPropertyName("column")]
    [Description("Column number where the class is located (1-based)")]
    public int Column { get; set; }

    [JsonPropertyName("interfaceName")]
    [Description("Name for the extracted interface (optional, will be generated if not provided)")]
    public string? InterfaceName { get; set; }

    [JsonPropertyName("memberNames")]
    [Description("Specific member names to include in the interface (optional, all public members if not specified)")]
    public string[]? MemberNames { get; set; }

    [JsonPropertyName("updateClass")]
    [Description("Whether to update the class to implement the new interface (default: true)")]
    public bool? UpdateClass { get; set; }
}