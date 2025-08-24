using System.Text;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using NextAction = COA.Mcp.Framework.Models.AIAction;
using COA.CodeNav.McpServer.Utilities;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that provides hover information (quick info) for symbols using Roslyn
/// </summary>
public class HoverTool : McpToolBase<HoverParams, HoverToolResult>
{
    private readonly ILogger<HoverTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => ToolNames.Hover;
    public override string Description => "Get detailed information about a symbol at a specific position. Shows method signatures, parameter types, return values, and documentation.";

    public HoverTool(
        IServiceProvider serviceProvider,
        ILogger<HoverTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        ITokenEstimator tokenEstimator,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(serviceProvider, logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _tokenEstimator = tokenEstimator;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<HoverToolResult> ExecuteInternalAsync(
        HoverParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Hover request received: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);
        
        var startTime = DateTime.UtcNow;

        _logger.LogInformation("Processing hover for {FilePath} at {Line}:{Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);

        // Get the document
        _logger.LogDebug("Retrieving document from workspace: {FilePath}", parameters.FilePath);
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
            return new HoverToolResult
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
                },
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
                },
                Meta = new ToolExecutionMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
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
            return new HoverToolResult
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
                },
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
                },
                Meta = new ToolExecutionMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                }
            };
        }

        // Find symbol at position using the robust approach from other tools
        _logger.LogDebug("Searching for symbol at position {Position}", position);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            semanticModel, position, document.Project.Solution.Workspace, cancellationToken);

        if (symbol == null)
        {
            // Get diagnostic information about what's at the position
            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
            var root = await syntaxTree!.GetRootAsync(cancellationToken);
            var token = root.FindToken(position);
            var tokenText = token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None) ? "<none>" : token.ToString();
            
            _logger.LogDebug("No symbol found at position {Line}:{Column} in {FilePath}. Token at position: '{TokenText}' ({TokenKind})", 
                parameters.Line, parameters.Column, parameters.FilePath, tokenText, token.RawKind);
                
            return new HoverToolResult
            {
                Success = false,
                Message = $"No symbol found at the specified position. Found token: '{tokenText}' ({token.RawKind})",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.NO_SYMBOL_AT_POSITION,
                    Message = $"No symbol found at the specified position. Found token: '{tokenText}' ({token.RawKind})",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Verify the line and column numbers are correct (1-based)",
                            "Ensure the cursor is on a symbol (class, method, property, etc.)",
                            "Try adjusting the column position to the start of the symbol name",
                            $"Current position shows token: '{tokenText}' of type {token.RawKind}"
                        }
                    }
                },
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
                },
                Meta = new ToolExecutionMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                }
            };
        }

        _logger.LogDebug("Found symbol '{SymbolName}' of kind {SymbolKind}, building hover info", 
            symbol.ToDisplayString(), symbol.Kind);

        // Build hover information
        var hoverInfo = BuildHoverInfo(symbol, null!, semanticModel);
        var nextActions = GenerateNextActions(symbol, parameters);
        var insights = GenerateInsights(symbol);

        // Store result if resource provider is available
        var resourceUri = _resourceProvider?.StoreAnalysisResult("hover", 
            new { symbol = symbol.ToDisplayString(), hoverInfo }, 
            $"Hover info for {symbol.Name}");
            
        if (resourceUri != null)
        {
            _logger.LogDebug("Stored hover result with URI: {ResourceUri}", resourceUri);
        }

        var result = new HoverToolResult
        {
            Success = true,
            HoverInfo = hoverInfo,
            SymbolDetails = new SymbolDetails
            {
                FullName = symbol.ToDisplayString(),
                Kind = symbol.Kind.ToString(),
                TypeInfo = GetTypeInfoSummary(symbol),
                Parameters = hoverInfo.Parameters,
                ReturnType = hoverInfo.ReturnType,
                Modifiers = GetModifiers(symbol)
            },
            Message = "Found hover information",
            Actions = nextActions,
            Insights = insights,
            ResourceUri = resourceUri,
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                TargetSymbol = symbol.ToDisplayString()
            },
            Summary = new SummaryInfo
            {
                TotalFound = 1,
                Returned = 1,
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

        _logger.LogInformation("Hover completed successfully for '{SymbolName}'", symbol.ToDisplayString());
        return result;
    }


    private HoverInfo BuildHoverInfo(ISymbol symbol, SyntaxNode node, SemanticModel semanticModel)
    {
        const int MAX_DOCUMENTATION_LENGTH = 2000; // Limit documentation to avoid token overflow
        
        var info = new HoverInfo
        {
            Signature = GetSignature(symbol),
            Documentation = TruncateDocumentation(GetDocumentation(symbol), MAX_DOCUMENTATION_LENGTH),
            TypeInfo = GetTypeInfo(symbol),
            DeclarationInfo = GetDeclarationInfo(symbol)
        };

        // Add parameter info for methods
        if (symbol is IMethodSymbol method)
        {
            info.Parameters = method.Parameters.Select(p => new COA.CodeNav.McpServer.Models.ParameterInfo
            {
                Name = p.Name,
                Type = p.Type.ToDisplayString(),
                IsOptional = p.IsOptional,
                HasDefaultValue = p.HasExplicitDefaultValue,
                DefaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() : null
            }).ToList();
            
            info.ReturnType = method.ReturnType.ToDisplayString();
        }

        // Add property info
        if (symbol is IPropertySymbol property)
        {
            info.PropertyType = property.Type.ToDisplayString();
            info.IsReadOnly = property.IsReadOnly;
            info.IsWriteOnly = property.IsWriteOnly;
        }

        // Add field info
        if (symbol is IFieldSymbol field)
        {
            info.FieldType = field.Type.ToDisplayString();
            info.IsConst = field.IsConst;
            info.IsReadOnly = field.IsReadOnly;
            if (field.IsConst && field.HasConstantValue)
            {
                info.ConstValue = field.ConstantValue?.ToString();
            }
        }

        return info;
    }

    private string GetSignature(ISymbol symbol)
    {
        // Get the display string with appropriate format
        return symbol.ToDisplayString(new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | 
                          SymbolDisplayMemberOptions.IncludeType |
                          SymbolDisplayMemberOptions.IncludeAccessibility |
                          SymbolDisplayMemberOptions.IncludeModifiers,
            parameterOptions: SymbolDisplayParameterOptions.IncludeName |
                             SymbolDisplayParameterOptions.IncludeType |
                             SymbolDisplayParameterOptions.IncludeDefaultValue |
                             SymbolDisplayParameterOptions.IncludeParamsRefOut,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes));
    }

    private string? GetDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        // Simple XML parsing - in production, use proper XML parsing
        var summary = ExtractXmlTag(xml, "summary");
        var remarks = ExtractXmlTag(xml, "remarks");
        var returns = ExtractXmlTag(xml, "returns");

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(summary))
            sb.AppendLine(summary.Trim());
        if (!string.IsNullOrWhiteSpace(remarks))
            sb.AppendLine($"\nRemarks: {remarks.Trim()}");
        if (!string.IsNullOrWhiteSpace(returns))
            sb.AppendLine($"\nReturns: {returns.Trim()}");

        return sb.Length > 0 ? sb.ToString().Trim() : null;
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
        
        return xml.Substring(startIndex, endIndex - startIndex).Trim();
    }

    private TypeInfoDetails GetTypeInfo(ISymbol symbol)
    {
        var typeInfo = new TypeInfoDetails();

        if (symbol is INamedTypeSymbol namedType)
        {
            typeInfo.IsClass = namedType.TypeKind == TypeKind.Class;
            typeInfo.IsInterface = namedType.TypeKind == TypeKind.Interface;
            typeInfo.IsStruct = namedType.TypeKind == TypeKind.Struct;
            typeInfo.IsEnum = namedType.TypeKind == TypeKind.Enum;
            typeInfo.IsDelegate = namedType.TypeKind == TypeKind.Delegate;
            typeInfo.IsGeneric = namedType.IsGenericType;
            typeInfo.BaseType = namedType.BaseType?.ToDisplayString();
            typeInfo.Interfaces = namedType.Interfaces.Select(i => i.ToDisplayString()).ToList();
        }

        return typeInfo;
    }

    private DeclarationInfo GetDeclarationInfo(ISymbol symbol)
    {
        var info = new DeclarationInfo
        {
            Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
            IsStatic = symbol.IsStatic,
            IsAbstract = symbol.IsAbstract,
            IsVirtual = symbol.IsVirtual,
            IsOverride = symbol.IsOverride,
            IsSealed = symbol.IsSealed,
            IsExtern = symbol.IsExtern
        };

        if (symbol.ContainingType != null)
        {
            info.ContainingType = symbol.ContainingType.ToDisplayString();
        }

        if (symbol.ContainingNamespace != null && !symbol.ContainingNamespace.IsGlobalNamespace)
        {
            info.Namespace = symbol.ContainingNamespace.ToDisplayString();
        }

        return info;
    }

    private List<NextAction> GenerateNextActions(ISymbol symbol, HoverParams parameters)
    {
        var actions = new List<NextAction>();

        // Suggest go to definition
        actions.Add(NextActionExtensions.CreateNextAction(
            "goto_definition",
            $"Go to definition of '{symbol.Name}'",
            ToolNames.GoToDefinition,
            new
            {
                filePath = parameters.FilePath,
                line = parameters.Line,
                column = parameters.Column
            },
            "high"
        ));

        // Suggest finding references
        actions.Add(NextActionExtensions.CreateNextAction(
            "find_references",
            $"Find all references to '{symbol.Name}'",
            ToolNames.FindAllReferences,
            new
            {
                filePath = parameters.FilePath,
                line = parameters.Line,
                column = parameters.Column
            },
            "medium"
        ));

        return actions;
    }

    private List<string> GenerateInsights(ISymbol symbol)
    {
        var insights = new List<string>();

        // Basic symbol info
        insights.Add($"'{symbol.Name}' is a {SymbolUtilities.GetFriendlySymbolKind(symbol)}");

        // Accessibility
        if (symbol.DeclaredAccessibility != Accessibility.NotApplicable)
        {
            insights.Add($"Has {symbol.DeclaredAccessibility.ToString().ToLower()} accessibility");
        }

        // Method-specific insights
        if (symbol is IMethodSymbol method)
        {
            if (method.IsExtensionMethod)
                insights.Add("This is an extension method");
            if (method.IsAsync)
                insights.Add("This is an async method");
            if (method.Parameters.Length > 0)
                insights.Add($"Takes {method.Parameters.Length} parameter(s)");
            if (method.IsGenericMethod)
                insights.Add($"Generic method with {method.TypeParameters.Length} type parameter(s)");
        }

        // Property-specific insights
        if (symbol is IPropertySymbol property)
        {
            if (property.IsIndexer)
                insights.Add("This is an indexer");
            if (property.IsReadOnly)
                insights.Add("Read-only property");
            else if (property.IsWriteOnly)
                insights.Add("Write-only property");
        }

        // Type-specific insights
        if (symbol is INamedTypeSymbol type)
        {
            if (type.IsAbstract)
                insights.Add("Abstract type - cannot be instantiated directly");
            if (type.IsSealed)
                insights.Add("Sealed type - cannot be inherited");
            if (type.IsGenericType)
                insights.Add($"Generic type with {type.TypeParameters.Length} type parameter(s)");
        }

        return insights;
    }

    
    private string? TruncateDocumentation(string? documentation, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(documentation))
            return documentation;
            
        if (documentation.Length <= maxLength)
            return documentation;
            
        // Truncate and add ellipsis
        return documentation.Substring(0, maxLength - 3) + "...";
    }
    
    private List<string> GetModifiers(ISymbol symbol)
    {
        var modifiers = new List<string>();
        
        if (symbol.IsStatic) modifiers.Add("static");
        if (symbol.IsVirtual) modifiers.Add("virtual");
        if (symbol.IsOverride) modifiers.Add("override");
        if (symbol.IsAbstract) modifiers.Add("abstract");
        if (symbol.IsSealed) modifiers.Add("sealed");
        if (symbol.IsExtern) modifiers.Add("extern");
        
        if (symbol is IMethodSymbol method)
        {
            if (method.IsAsync) modifiers.Add("async");
            if (method.IsExtensionMethod) modifiers.Add("extension");
        }
        
        return modifiers;
    }
    
    private string? GetTypeInfoSummary(ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol namedType)
        {
            var parts = new List<string>();
            
            if (namedType.TypeKind == TypeKind.Class) parts.Add("class");
            else if (namedType.TypeKind == TypeKind.Interface) parts.Add("interface");
            else if (namedType.TypeKind == TypeKind.Struct) parts.Add("struct");
            else if (namedType.TypeKind == TypeKind.Enum) parts.Add("enum");
            else if (namedType.TypeKind == TypeKind.Delegate) parts.Add("delegate");
            
            if (namedType.IsGenericType) parts.Add("generic");
            if (namedType.BaseType != null && namedType.BaseType.SpecialType != SpecialType.System_Object)
                parts.Add($"inherits {namedType.BaseType.Name}");
            
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }
        
        return null;
    }
}

public class HoverParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [System.ComponentModel.Description("Path to the source file")]
    public required string FilePath { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Line must be positive")]
    [JsonPropertyName("line")]
    [System.ComponentModel.Description("Line number (1-based) where the symbol appears")]
    public int Line { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Column must be positive")]
    [JsonPropertyName("column")]
    [System.ComponentModel.Description("Column number (1-based) of the symbol position")]
    public int Column { get; set; }
}

// Result class is defined in Models/ToolResults.cs