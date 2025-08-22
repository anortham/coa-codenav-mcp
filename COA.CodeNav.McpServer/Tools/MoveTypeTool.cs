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
/// MCP tool that provides move type/class refactoring functionality using Roslyn
/// </summary>
public class MoveTypeTool : McpToolBase<MoveTypeParams, MoveTypeResult>
{
    private readonly ILogger<MoveTypeTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;
    private readonly MoveTypeResponseBuilder _responseBuilder;

    public override string Name => ToolNames.MoveType;
    public override string Description => @"Move classes, interfaces, or types to different files or namespaces. Improves code organization and updates using statements automatically.";
    public override ToolCategory Category => ToolCategory.Refactoring;
    
    public MoveTypeTool(
        ILogger<MoveTypeTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        ITokenEstimator tokenEstimator,
        MoveTypeResponseBuilder responseBuilder,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(logger)
    {
        _logger = logger;
        _tokenEstimator = tokenEstimator;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
        _responseBuilder = responseBuilder;
    }

    protected override async Task<MoveTypeResult> ExecuteInternalAsync(
        MoveTypeParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("MoveType request received: FilePath={FilePath}, TypeName={TypeName}, TargetFile={TargetFile}", 
            parameters.FilePath, parameters.TypeName, parameters.TargetFilePath);
        
        var startTime = DateTime.UtcNow;

        // Get the source document
        var sourceDocument = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (sourceDocument == null)
        {
            return new MoveTypeResult
            {
                Success = false,
                Message = $"Source document not found: {parameters.FilePath}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.DOCUMENT_NOT_FOUND,
                    Message = $"Source document not found: {parameters.FilePath}",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new string[]
                        {
                            "Ensure the source file path is correct and absolute",
                            "Verify the file exists in the loaded solution/project",
                            "Load a solution using csharp_load_solution or project using csharp_load_project"
                        }
                    }
                }
            };
        }

        // Get semantic model and syntax tree
        var semanticModel = await sourceDocument.GetSemanticModelAsync(cancellationToken);
        var syntaxTree = await sourceDocument.GetSyntaxTreeAsync(cancellationToken);
        
        if (semanticModel == null || syntaxTree == null)
        {
            return new MoveTypeResult
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

        // Find the type to move (class, interface, enum, struct, record)
        var root = syntaxTree.GetRoot(cancellationToken);
        var typeDeclaration = root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.ValueText == parameters.TypeName);

        if (typeDeclaration == null)
        {
            return new MoveTypeResult
            {
                Success = false,
                Message = $"Type '{parameters.TypeName}' not found in source file",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SYMBOL_NOT_FOUND,
                    Message = $"Type '{parameters.TypeName}' not found in source file"
                }
            };
        }

        // Get the type symbol
        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            return new MoveTypeResult
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

        try
        {
            // Determine target namespace
            var targetNamespace = parameters.TargetNamespace ?? typeSymbol.ContainingNamespace?.ToDisplayString();
            
            // Check if target file exists, create if needed
            Document? targetDocument = null;
            bool isNewFile = false;
            
            if (!string.IsNullOrEmpty(parameters.TargetFilePath))
            {
                targetDocument = await _workspaceService.GetDocumentAsync(parameters.TargetFilePath);
                if (targetDocument == null)
                {
                    // Create new file
                    isNewFile = true;
                    targetDocument = await CreateNewDocument(parameters.TargetFilePath, targetNamespace, sourceDocument.Project);
                }
            }
            else
            {
                // Generate target file path based on type name
                var sourceDirectory = Path.GetDirectoryName(parameters.FilePath) ?? "";
                var targetFileName = $"{parameters.TypeName}.cs";
                var targetFilePath = Path.Combine(sourceDirectory, targetFileName);
                
                targetDocument = await _workspaceService.GetDocumentAsync(targetFilePath);
                if (targetDocument == null)
                {
                    isNewFile = true;
                    targetDocument = await CreateNewDocument(targetFilePath, targetNamespace, sourceDocument.Project);
                }
                parameters.TargetFilePath = targetFilePath;
            }

            if (targetDocument == null)
            {
                return new MoveTypeResult
                {
                    Success = false,
                    Message = "Failed to create or access target document",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.INTERNAL_ERROR,
                        Message = "Failed to create or access target document"
                    }
                };
            }

            // Generate the moved type code
            var movedTypeCode = await GenerateMovedTypeCode(typeDeclaration, targetNamespace, sourceDocument, cancellationToken);
            
            // Generate updated source code (with type removed)
            var updatedSourceCode = await GenerateUpdatedSourceCode(sourceDocument, typeDeclaration, cancellationToken);
            
            // Generate updated target code (with type added)
            var updatedTargetCode = await GenerateUpdatedTargetCode(targetDocument, movedTypeCode, isNewFile, cancellationToken);

            // Generate insights
            var insights = GenerateInsights(parameters, typeSymbol, isNewFile);
            
            // Generate next actions
            var actions = GenerateNextActions(parameters, typeSymbol);

            var result = new MoveTypeResult
            {
                Success = true,
                Message = $"Successfully moved type '{parameters.TypeName}' to '{parameters.TargetFilePath}'",
                TypeName = parameters.TypeName,
                SourceFilePath = parameters.FilePath,
                TargetFilePath = parameters.TargetFilePath,
                UpdatedSourceCode = updatedSourceCode,
                UpdatedTargetCode = updatedTargetCode,
                MovedTypeCode = movedTypeCode,
                WasNewFileCreated = isNewFile,
                Insights = insights,
                Actions = actions,
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    TargetSymbol = parameters.TypeName
                },
                Summary = new SummaryInfo
                {
                    TotalFound = 1,
                    Returned = 1,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    SymbolInfo = new SymbolSummary
                    {
                        Name = typeSymbol.Name,
                        Kind = typeSymbol.TypeKind.ToString(),
                        ContainingType = typeSymbol.ContainingType?.ToDisplayString(),
                        Namespace = typeSymbol.ContainingNamespace?.ToDisplayString()
                    }
                },
                Meta = new ToolExecutionMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                }
            };

            // Store result as resource for large responses
            if (_resourceProvider != null)
            {
                var resourceUri = _resourceProvider.StoreAnalysisResult("move-type-result", result);
                result.ResourceUri = resourceUri;
            }

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
            _logger.LogError(ex, "Error during type move");
            return new MoveTypeResult
            {
                Success = false,
                Message = $"Error during type move: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Message = ex.Message
                }
            };
        }
    }

    private Task<Document> CreateNewDocument(string filePath, string? namespaceName, Project project)
    {
        var fileName = Path.GetFileName(filePath);
        var content = GenerateNewFileContent(namespaceName);
        
        // Add the document to the project
        var document = project.AddDocument(fileName, content, filePath: filePath);
        
        return Task.FromResult(document);
    }

    private string GenerateNewFileContent(string? namespaceName)
    {
        var sb = new StringBuilder();
        
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.AppendLine($"namespace {namespaceName};");
            sb.AppendLine();
        }
        
        sb.AppendLine("// Type will be added here");
        
        return sb.ToString();
    }

    private async Task<string> GenerateMovedTypeCode(BaseTypeDeclarationSyntax typeDeclaration, string? targetNamespace, Document sourceDocument, CancellationToken cancellationToken)
    {
        var root = await sourceDocument.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "";

        // Extract the type with its usings
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().ToList();
        var namespaceDeclaration = typeDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();

        var sb = new StringBuilder();

        // Add usings
        foreach (var usingDirective in usings)
        {
            sb.AppendLine(usingDirective.ToString());
        }

        if (usings.Any())
        {
            sb.AppendLine();
        }

        // Add namespace if different from current
        if (!string.IsNullOrEmpty(targetNamespace))
        {
            sb.AppendLine($"namespace {targetNamespace};");
            sb.AppendLine();
        }

        // Add the type declaration
        var formattedType = Formatter.Format(typeDeclaration, sourceDocument.Project.Solution.Workspace);
        sb.AppendLine(formattedType.ToFullString().TrimEnd());

        return sb.ToString();
    }

    private async Task<string> GenerateUpdatedSourceCode(Document sourceDocument, BaseTypeDeclarationSyntax typeToRemove, CancellationToken cancellationToken)
    {
        var root = await sourceDocument.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "";

        // Remove the type declaration
        var newRoot = root.RemoveNode(typeToRemove, SyntaxRemoveOptions.KeepNoTrivia);
        if (newRoot == null) return "";

        var formatted = Formatter.Format(newRoot, sourceDocument.Project.Solution.Workspace);
        return formatted.ToFullString();
    }

    private async Task<string> GenerateUpdatedTargetCode(Document targetDocument, string movedTypeCode, bool isNewFile, CancellationToken cancellationToken)
    {
        if (isNewFile)
        {
            return movedTypeCode;
        }

        var root = await targetDocument.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return movedTypeCode;

        // Parse the moved type to add it to the existing document
        var movedTypeSyntax = CSharpSyntaxTree.ParseText(movedTypeCode);
        var movedTypeRoot = movedTypeSyntax.GetRoot();
        
        // Find the type declaration in the moved code
        var typeDeclaration = movedTypeRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        if (typeDeclaration == null) return movedTypeCode;

        // Add the type to the existing document
        var newRoot = root;
        
        // Find where to insert (after usings, in namespace if it exists)
        var namespaceDeclaration = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (namespaceDeclaration != null)
        {
            var updatedNamespace = namespaceDeclaration.AddMembers(typeDeclaration);
            newRoot = root.ReplaceNode(namespaceDeclaration, updatedNamespace);
        }
        else
        {
            // Add to compilation unit
            if (root is CompilationUnitSyntax compilationUnit)
            {
                newRoot = compilationUnit.AddMembers(typeDeclaration);
            }
        }

        var formatted = Formatter.Format(newRoot, targetDocument.Project.Solution.Workspace);
        return formatted.ToFullString();
    }

    private List<string> GenerateInsights(MoveTypeParams parameters, INamedTypeSymbol typeSymbol, bool isNewFile)
    {
        var insights = new List<string>();

        insights.Add($"Moved {typeSymbol.TypeKind.ToString().ToLower()} '{typeSymbol.Name}' from '{parameters.FilePath}' to '{parameters.TargetFilePath}'");
        
        if (isNewFile)
        {
            insights.Add("Created new file for the moved type");
        }
        else
        {
            insights.Add("Added type to existing file");
        }

        var memberCount = typeSymbol.GetMembers().Count(m => !m.IsImplicitlyDeclared);
        if (memberCount > 0)
        {
            insights.Add($"Type contains {memberCount} members that were moved");
        }

        if (typeSymbol.Interfaces.Any())
        {
            insights.Add($"Type implements {typeSymbol.Interfaces.Length} interface(s)");
        }

        if (typeSymbol.BaseType != null && typeSymbol.BaseType.SpecialType != SpecialType.System_Object)
        {
            insights.Add($"Type inherits from {typeSymbol.BaseType.ToDisplayString()}");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(MoveTypeParams parameters, INamedTypeSymbol typeSymbol)
    {
        var actions = new List<AIAction>();

        // Action to find references to ensure no broken references
        actions.Add(new AIAction
        {
            Action = ToolNames.SymbolSearch,
            Description = $"Search for references to '{typeSymbol.Name}' in the solution",
            Parameters = new Dictionary<string, object>
            {
                ["query"] = typeSymbol.Name,
                ["searchType"] = "exact"
            },
            Priority = 90,
            Category = "validation"
        });

        // Action to view the moved type in its new location
        actions.Add(new AIAction
        {
            Action = ToolNames.DocumentSymbols,
            Description = $"View symbols in the new file '{parameters.TargetFilePath}'",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = parameters.TargetFilePath ?? ""
            },
            Priority = 70,
            Category = "navigation"
        });

        // Action to check for compilation errors after the move
        actions.Add(new AIAction
        {
            Action = ToolNames.GetDiagnostics,
            Description = "Check for compilation errors after the move",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = parameters.TargetFilePath ?? "",
                ["scope"] = "file"
            },
            Priority = 85,
            Category = "validation"
        });

        return actions;
    }
}

public class MoveTypeParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [Description("Path to the source file containing the type to move")]
    public string FilePath { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "TypeName is required")]
    [JsonPropertyName("typeName")]
    [Description("Name of the type (class, interface, enum, struct) to move")]
    public string TypeName { get; set; } = string.Empty;

    [JsonPropertyName("targetFilePath")]
    [Description("Path to the target file (optional, will be generated based on type name if not provided)")]
    public string? TargetFilePath { get; set; }

    [JsonPropertyName("targetNamespace")]
    [Description("Target namespace (optional, will use current namespace if not provided)")]
    public string? TargetNamespace { get; set; }

    [JsonPropertyName("createNewFile")]
    [Description("Whether to create a new file if target doesn't exist (default: true)")]
    public bool? CreateNewFile { get; set; } = true;
}