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
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that provides inline variable refactoring functionality using Roslyn
/// </summary>
public class InlineVariableTool : McpToolBase<InlineVariableParams, InlineVariableResult>
{
    private readonly ILogger<InlineVariableTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;
    private readonly InlineVariableResponseBuilder _responseBuilder;

    public override string Name => ToolNames.InlineVariable;
    public override string Description => @"Inline variables by replacing usage with initialization value. Simplifies code by removing unnecessary temporary variables.

Critical: Before inlining, ensure the variable has a single assignment and simple value. Best for temporary variables that don't improve readability.

Prerequisites: Call csharp_load_solution or csharp_load_project first.
Use cases: Simplifying code, removing temporary variables, improving readability, reducing variable clutter.";
    public override ToolCategory Category => ToolCategory.Refactoring;
    
    public InlineVariableTool(
        ILogger<InlineVariableTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        ITokenEstimator tokenEstimator,
        InlineVariableResponseBuilder responseBuilder,
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

    protected override async Task<InlineVariableResult> ExecuteInternalAsync(
        InlineVariableParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("InlineVariable request received: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);
        
        var startTime = DateTime.UtcNow;

        // Get the document
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            return new InlineVariableResult
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
            return new InlineVariableResult
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

        // Find the variable at the specified position
        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines[parameters.Line - 1].Start + (parameters.Column - 1);
        
        var root = syntaxTree.GetRoot(cancellationToken);
        var node = root.FindToken(position).Parent;
        
        // Look for variable declaration
        var variableDeclarator = node?.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
        if (variableDeclarator == null)
        {
            return new InlineVariableResult
            {
                Success = false,
                Message = "No variable declaration found at the specified position",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SYMBOL_NOT_FOUND,
                    Message = "No variable declaration found at the specified position"
                }
            };
        }

        // Get the variable symbol
        var variableSymbol = semanticModel.GetDeclaredSymbol(variableDeclarator, cancellationToken) as ILocalSymbol;
        if (variableSymbol == null)
        {
            return new InlineVariableResult
            {
                Success = false,
                Message = "Could not resolve variable symbol",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SYMBOL_NOT_FOUND,
                    Message = "Could not resolve variable symbol"
                }
            };
        }

        // Validate that the variable can be inlined
        var validationResult = ValidateVariableForInlining(variableDeclarator, variableSymbol);
        if (!validationResult.CanInline)
        {
            return new InlineVariableResult
            {
                Success = false,
                Message = $"Variable cannot be inlined: {validationResult.Reason}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INVALID_OPERATION,
                    Message = validationResult.Reason
                }
            };
        }

        try
        {
            // Find all references to the variable
            var references = await SymbolFinder.FindReferencesAsync(variableSymbol, document.Project.Solution, cancellationToken);
            var referenceLocations = references.SelectMany(r => r.Locations).Where(loc => !loc.IsImplicit).ToList();

            // Get the initialization value
            var initializationValue = variableDeclarator.Initializer?.Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(initializationValue))
            {
                return new InlineVariableResult
                {
                    Success = false,
                    Message = "Variable has no initialization value to inline",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.INVALID_OPERATION,
                        Message = "Variable has no initialization value to inline"
                    }
                };
            }

            // Perform the inlining
            var updatedCode = await PerformVariableInlining(document, variableDeclarator, referenceLocations, initializationValue, cancellationToken);

            // Generate insights
            var insights = GenerateInsights(variableSymbol, referenceLocations.Count, initializationValue);
            
            // Generate next actions
            var actions = GenerateNextActions(parameters, variableSymbol.Name);

            var result = new InlineVariableResult
            {
                Success = true,
                Message = $"Successfully inlined variable '{variableSymbol.Name}' ({referenceLocations.Count} usages replaced)",
                VariableName = variableSymbol.Name,
                InlinedUsages = referenceLocations.Count,
                InitializationValue = initializationValue,
                UpdatedCode = updatedCode,
                Insights = insights,
                Actions = actions,
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    TargetSymbol = variableSymbol.Name
                },
                Summary = new SummaryInfo
                {
                    TotalFound = referenceLocations.Count + 1, // +1 for the declaration
                    Returned = referenceLocations.Count,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    SymbolInfo = new SymbolSummary
                    {
                        Name = variableSymbol.Name,
                        Kind = variableSymbol.Kind.ToString(),
                        ContainingType = variableSymbol.ContainingSymbol?.ToDisplayString(),
                        Namespace = variableSymbol.ContainingNamespace?.ToDisplayString()
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
                var resourceUri = _resourceProvider.StoreAnalysisResult("inline-variable-result", result);
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
            _logger.LogError(ex, "Error during variable inlining");
            return new InlineVariableResult
            {
                Success = false,
                Message = $"Error during variable inlining: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Message = ex.Message
                }
            };
        }
    }

    private (bool CanInline, string Reason) ValidateVariableForInlining(VariableDeclaratorSyntax variableDeclarator, ILocalSymbol variableSymbol)
    {
        // Check if variable has initialization
        if (variableDeclarator.Initializer?.Value == null)
        {
            return (false, "Variable has no initialization value");
        }

        // Check if variable is const or readonly (these should be handled differently)
        if (variableSymbol.IsConst)
        {
            return (false, "Cannot inline const variables (they are already inlined by the compiler)");
        }

        // For now, allow most local variables to be inlined
        // More sophisticated analysis could check for:
        // - Multiple assignments
        // - Complex expressions that might have side effects
        // - Variables used in different scopes

        return (true, "Variable can be inlined");
    }

    private async Task<string> PerformVariableInlining(
        Document document,
        VariableDeclaratorSyntax variableDeclarator,
        List<Microsoft.CodeAnalysis.FindSymbols.ReferenceLocation> referenceLocations,
        string initializationValue,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "";

        var newRoot = root;

        // Replace all variable references with the initialization value
        var referenceNodes = referenceLocations
            .Where(r => r.Document.Id == document.Id)
            .Select(r => root.FindToken(r.Location.SourceSpan.Start).Parent)
            .OfType<IdentifierNameSyntax>()
            .ToList();

        // Replace all references at once to avoid stale node references
        if (referenceNodes.Any())
        {
            var replacement = SyntaxFactory.ParseExpression(initializationValue);
            newRoot = newRoot.ReplaceNodes(referenceNodes, (original, rewritten) => replacement);
        }

        // Remove the variable declaration after replacements
        // We need to find the declaration in the updated tree
        var updatedVariableDeclarator = newRoot.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.ValueText == variableDeclarator.Identifier.ValueText);
            
        if (updatedVariableDeclarator != null)
        {
            var variableDeclaration = updatedVariableDeclarator.Parent as VariableDeclarationSyntax;
            if (variableDeclaration?.Variables.Count == 1)
            {
                // Remove the entire declaration statement if this is the only variable
                var declarationStatement = variableDeclaration.Parent as LocalDeclarationStatementSyntax;
                if (declarationStatement != null)
                {
                    newRoot = newRoot.RemoveNode(declarationStatement, SyntaxRemoveOptions.KeepNoTrivia);
                }
            }
            else if (variableDeclaration?.Variables.Count > 1)
            {
                // Remove just this variable from the declaration
                var updatedDeclaration = variableDeclaration.RemoveNode(updatedVariableDeclarator, SyntaxRemoveOptions.KeepNoTrivia);
                if (updatedDeclaration != null)
                {
                    newRoot = newRoot.ReplaceNode(variableDeclaration, updatedDeclaration);
                }
            }
        }

        var formatted = Formatter.Format(newRoot, document.Project.Solution.Workspace);
        return formatted.ToFullString();
    }

    private List<string> GenerateInsights(ILocalSymbol variableSymbol, int usageCount, string initializationValue)
    {
        var insights = new List<string>();

        insights.Add($"Inlined local variable '{variableSymbol.Name}' of type {variableSymbol.Type.ToDisplayString()}");
        
        if (usageCount > 0)
        {
            insights.Add($"Replaced {usageCount} usage(s) with initialization value: {initializationValue}");
        }

        if (initializationValue.Length > 50)
        {
            insights.Add("Variable had a complex initialization expression");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(InlineVariableParams parameters, string variableName)
    {
        var actions = new List<AIAction>();

        // Action to check for compilation errors after inlining
        actions.Add(new AIAction
        {
            Action = ToolNames.GetDiagnostics,
            Description = "Check for compilation errors after inlining",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = parameters.FilePath,
                ["scope"] = "file"
            },
            Priority = 90,
            Category = "validation"
        });

        // Action to format the document
        actions.Add(new AIAction
        {
            Action = ToolNames.FormatDocument,
            Description = "Format the document after inlining",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = parameters.FilePath
            },
            Priority = 70,
            Category = "formatting"
        });

        return actions;
    }
}

public class InlineVariableParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [Description("Path to the source file containing the variable to inline")]
    public string FilePath { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Line must be positive")]
    [JsonPropertyName("line")]
    [Description("Line number where the variable is declared (1-based)")]
    public int Line { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Column must be positive")]
    [JsonPropertyName("column")]
    [Description("Column number where the variable is declared (1-based)")]
    public int Column { get; set; }
}