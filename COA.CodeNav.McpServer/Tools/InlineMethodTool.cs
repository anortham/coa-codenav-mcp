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
/// MCP tool that provides inline method refactoring functionality using Roslyn
/// </summary>
public class InlineMethodTool : McpToolBase<InlineMethodParams, InlineMethodResult>
{
    private readonly ILogger<InlineMethodTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;
    private readonly InlineMethodResponseBuilder _responseBuilder;

    public override string Name => ToolNames.InlineMethod;
    public override string Description => @"Inline a method by replacing all calls with the method body.
Returns: Updated code with method calls replaced by method body and method declaration removed.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if inlining fails.
Use cases: Simplifying code, removing unnecessary abstractions, improving performance.
Not for: Complex methods with multiple return points, recursive methods, virtual methods.";
    public override ToolCategory Category => ToolCategory.Refactoring;
    
    public InlineMethodTool(
        ILogger<InlineMethodTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        ITokenEstimator tokenEstimator,
        InlineMethodResponseBuilder responseBuilder,
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

    protected override async Task<InlineMethodResult> ExecuteInternalAsync(
        InlineMethodParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("InlineMethod request received: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);
        
        var startTime = DateTime.UtcNow;

        // Get the document
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            return new InlineMethodResult
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
            return new InlineMethodResult
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

        // Find the method at the specified position
        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines[parameters.Line - 1].Start + (parameters.Column - 1);
        
        var node = syntaxTree.GetRoot(cancellationToken).FindToken(position).Parent;
        var methodDeclaration = node?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        
        if (methodDeclaration == null)
        {
            return new InlineMethodResult
            {
                Success = false,
                Message = "No method declaration found at the specified position",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SYMBOL_NOT_FOUND,
                    Message = "No method declaration found at the specified position"
                }
            };
        }

        // Get the method symbol
        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDeclaration, cancellationToken) as IMethodSymbol;
        if (methodSymbol == null)
        {
            return new InlineMethodResult
            {
                Success = false,
                Message = "Could not resolve method symbol",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SYMBOL_NOT_FOUND,
                    Message = "Could not resolve method symbol"
                }
            };
        }

        // Validate that the method can be inlined
        var validationResult = ValidateMethodForInlining(methodDeclaration, methodSymbol);
        if (!validationResult.CanInline)
        {
            return new InlineMethodResult
            {
                Success = false,
                Message = $"Method cannot be inlined: {validationResult.Reason}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INVALID_OPERATION,
                    Message = validationResult.Reason
                }
            };
        }

        try
        {
            // Find all references to the method
            var references = await SymbolFinder.FindReferencesAsync(methodSymbol, document.Project.Solution, cancellationToken);
            var referenceLocations = references.SelectMany(r => r.Locations).Where(loc => !loc.IsImplicit).ToList();

            if (!referenceLocations.Any() && !parameters.ForceInline)
            {
                return new InlineMethodResult
                {
                    Success = false,
                    Message = "Method has no references found to inline. Use forceInline=true to remove the method anyway.",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.INVALID_OPERATION,
                        Message = "Method has no references found to inline"
                    }
                };
            }

            // Perform the inlining
            var inlineResult = await PerformInlining(document, methodDeclaration, methodSymbol, referenceLocations, cancellationToken);

            // Generate insights
            var insights = GenerateInsights(methodSymbol, referenceLocations.Count);
            
            // Generate next actions
            var actions = GenerateNextActions(parameters, methodSymbol.Name);

            var result = new InlineMethodResult
            {
                Success = true,
                Message = $"Successfully inlined method '{methodSymbol.Name}' ({referenceLocations.Count} call sites replaced)",
                MethodName = methodSymbol.Name,
                InlinedCallSites = referenceLocations.Count,
                UpdatedCode = inlineResult.UpdatedCode,
                MethodBody = inlineResult.MethodBody,
                Insights = insights,
                Actions = actions,
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    TargetSymbol = methodSymbol.ToDisplayString()
                },
                Summary = new SummaryInfo
                {
                    TotalFound = referenceLocations.Count + 1, // +1 for the method declaration
                    Returned = referenceLocations.Count,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    SymbolInfo = new SymbolSummary
                    {
                        Name = methodSymbol.Name,
                        Kind = methodSymbol.Kind.ToString(),
                        ContainingType = methodSymbol.ContainingType?.ToDisplayString(),
                        Namespace = methodSymbol.ContainingNamespace?.ToDisplayString()
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
                var resourceUri = _resourceProvider.StoreAnalysisResult("inline-method-result", result);
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
            _logger.LogError(ex, "Error during method inlining");
            return new InlineMethodResult
            {
                Success = false,
                Message = $"Error during method inlining: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Message = ex.Message
                }
            };
        }
    }

    private (bool CanInline, string Reason) ValidateMethodForInlining(MethodDeclarationSyntax methodDeclaration, IMethodSymbol methodSymbol)
    {
        // Check if method is virtual, abstract, or override
        if (methodSymbol.IsVirtual || methodSymbol.IsAbstract || methodSymbol.IsOverride)
        {
            return (false, "Cannot inline virtual, abstract, or override methods");
        }

        // Check if method is recursive
        if (IsRecursiveMethod(methodDeclaration, methodSymbol.Name))
        {
            return (false, "Cannot inline recursive methods");
        }

        // Check if method has multiple return statements
        var returnStatements = methodDeclaration.DescendantNodes().OfType<ReturnStatementSyntax>().ToList();
        if (returnStatements.Count > 1)
        {
            return (false, "Cannot inline methods with multiple return statements");
        }

        // Check if method has out or ref parameters
        if (methodSymbol.Parameters.Any(p => p.RefKind == RefKind.Out || p.RefKind == RefKind.Ref))
        {
            return (false, "Cannot inline methods with out or ref parameters");
        }

        // Check if method body is empty or too complex
        if (methodDeclaration.Body == null || !methodDeclaration.Body.Statements.Any())
        {
            return (false, "Cannot inline methods with empty or missing body");
        }

        return (true, "Method can be inlined");
    }

    private bool IsRecursiveMethod(MethodDeclarationSyntax methodDeclaration, string methodName)
    {
        var invocations = methodDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>();
        
        foreach (var invocation in invocations)
        {
            if (invocation.Expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == methodName)
            {
                return true;
            }
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess && 
                memberAccess.Name.Identifier.ValueText == methodName)
            {
                return true;
            }
        }
        
        return false;
    }

    private async Task<(string UpdatedCode, string MethodBody)> PerformInlining(
        Document document, 
        MethodDeclarationSyntax methodDeclaration, 
        IMethodSymbol methodSymbol,
        List<Microsoft.CodeAnalysis.FindSymbols.ReferenceLocation> referenceLocations,
        CancellationToken cancellationToken)
    {
        var workspace = document.Project.Solution.Workspace;
        var currentSolution = document.Project.Solution;

        // Get the method body for inlining
        var methodBody = GetMethodBodyForInlining(methodDeclaration);

        // Group references by document
        var referencesByDocument = referenceLocations.GroupBy(r => r.Document);

        foreach (var docGroup in referencesByDocument)
        {
            var targetDocument = currentSolution.GetDocument(docGroup.Key.Id);
            if (targetDocument == null) continue;

            var root = await targetDocument.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;

            var newRoot = root;

            // Process all invocations in this document
            // Collect replacements first to avoid stale node references
            var invocationsToReplace = new List<(InvocationExpressionSyntax invocation, SyntaxNode replacement)>();
            
            foreach (var reference in docGroup)
            {
                var referencePosition = reference.Location.SourceSpan.Start;
                var token = root.FindToken(referencePosition);
                var invocationNode = token.Parent?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();

                if (invocationNode != null)
                {
                    var replacement = CreateInlinedReplacement(invocationNode, methodBody, methodSymbol, root);
                    invocationsToReplace.Add((invocationNode, replacement));
                }
            }
            
            // Apply all replacements at once to avoid stale references
            if (invocationsToReplace.Any())
            {
                newRoot = newRoot.ReplaceNodes(
                    invocationsToReplace.Select(x => x.invocation),
                    (original, rewritten) =>
                    {
                        var replacementPair = invocationsToReplace.FirstOrDefault(x => x.invocation.IsEquivalentTo(original));
                        return replacementPair.replacement ?? rewritten;
                    });
            }

            currentSolution = currentSolution.WithDocumentSyntaxRoot(targetDocument.Id, newRoot);
        }

        // Remove the method declaration from the original document
        // Need to find the method again in the updated solution to avoid stale references
        var originalDocument = currentSolution.GetDocument(document.Id);
        if (originalDocument != null)
        {
            var originalRoot = await originalDocument.GetSyntaxRootAsync(cancellationToken);
            if (originalRoot != null)
            {
                // Find the method declaration again in the updated tree
                var updatedMethodDeclaration = originalRoot.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.ValueText == methodSymbol.Name 
                                      && m.ParameterList.Parameters.Count == methodSymbol.Parameters.Length);
                
                if (updatedMethodDeclaration != null)
                {
                    var newOriginalRoot = originalRoot.RemoveNode(updatedMethodDeclaration, SyntaxRemoveOptions.KeepNoTrivia);
                    if (newOriginalRoot != null)
                    {
                        currentSolution = currentSolution.WithDocumentSyntaxRoot(originalDocument.Id, newOriginalRoot);
                    }
                }
            }
        }

        // Format and return the updated code for the original document
        var finalDocument = currentSolution.GetDocument(document.Id);
        if (finalDocument != null)
        {
            var formattedDocument = await Formatter.FormatAsync(finalDocument, cancellationToken: cancellationToken);
            var finalText = await formattedDocument.GetTextAsync(cancellationToken);
            return (finalText.ToString(), methodBody);
        }

        return ("", methodBody);
    }

    private string GetMethodBodyForInlining(MethodDeclarationSyntax methodDeclaration)
    {
        if (methodDeclaration.Body == null || !methodDeclaration.Body.Statements.Any())
        {
            return "";
        }

        // For single statement methods, return the statement
        if (methodDeclaration.Body.Statements.Count == 1)
        {
            var statement = methodDeclaration.Body.Statements[0];
            
            // If it's a return statement, return just the expression
            if (statement is ReturnStatementSyntax returnStatement && returnStatement.Expression != null)
            {
                return returnStatement.Expression.ToString();
            }
            
            return statement.ToString();
        }

        // For multi-statement methods, return the block
        return methodDeclaration.Body.ToString();
    }

    private SyntaxNode CreateInlinedReplacement(InvocationExpressionSyntax invocation, string methodBody, IMethodSymbol methodSymbol, SyntaxNode root)
    {
        // Replace parameters with arguments in the method body
        var processedBody = ReplaceParametersWithArguments(methodBody, invocation, methodSymbol);
        
        // Check if this is a void method
        bool isVoidMethod = methodSymbol.ReturnType.SpecialType == SpecialType.System_Void;
        
        // Find the parent context to determine if we're in an expression or statement context
        var parent = invocation.Parent;
        bool isInExpressionStatement = parent is ExpressionStatementSyntax;
        
        // For simple method bodies (no braces, no semicolons)
        if (!processedBody.Contains("{") && !processedBody.Contains(";"))
        {
            try
            {
                return SyntaxFactory.ParseExpression(processedBody);
            }
            catch
            {
                return SyntaxFactory.ParseExpression($"({processedBody})");
            }
        }
        
        // For method bodies with braces - extract the inner content
        if (processedBody.StartsWith("{") && processedBody.EndsWith("}"))
        {
            var innerBody = processedBody.Substring(1, processedBody.Length - 2).Trim();
            
            // If it's a simple return statement, extract the expression
            var returnMatch = System.Text.RegularExpressions.Regex.Match(innerBody, @"^\s*return\s+([^;]+);?\s*$");
            if (returnMatch.Success && !isVoidMethod)
            {
                try
                {
                    return SyntaxFactory.ParseExpression(returnMatch.Groups[1].Value.Trim());
                }
                catch
                {
                    return SyntaxFactory.ParseExpression($"({returnMatch.Groups[1].Value.Trim()})");
                }
            }
            
            // For single statement methods (like Console.WriteLine), just extract the statement content
            var singleStatementMatch = System.Text.RegularExpressions.Regex.Match(innerBody, @"^\s*([^;]+);?\s*$");
            if (singleStatementMatch.Success && isVoidMethod && isInExpressionStatement)
            {
                try
                {
                    return SyntaxFactory.ParseExpression(singleStatementMatch.Groups[1].Value.Trim());
                }
                catch
                {
                    return SyntaxFactory.ParseExpression($"({singleStatementMatch.Groups[1].Value.Trim()})");
                }
            }
        }
        
        // If we can't simplify to an expression, fall back to a simple approach
        // Try to parse the processed body as an expression
        try
        {
            return SyntaxFactory.ParseExpression(processedBody);
        }
        catch
        {
            // Last resort - wrap in parentheses
            return SyntaxFactory.ParseExpression($"({processedBody})");
        }
    }
    
    private List<StatementSyntax> ParseStatementsFromString(string statementsText)
    {
        var statements = new List<StatementSyntax>();
        
        // Split by semicolons and parse each as a statement
        var parts = statementsText.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                try
                {
                    statements.Add(SyntaxFactory.ParseStatement(trimmed + ";"));
                }
                catch
                {
                    // If parsing fails, try as expression statement
                    try
                    {
                        statements.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression(trimmed)));
                    }
                    catch
                    {
                        // Skip invalid statements
                    }
                }
            }
        }
        
        return statements;
    }

    private string ReplaceParametersWithArguments(string methodBody, InvocationExpressionSyntax invocation, IMethodSymbol methodSymbol)
    {
        var result = methodBody;
        
        if (invocation.ArgumentList?.Arguments != null)
        {
            for (int i = 0; i < invocation.ArgumentList.Arguments.Count && i < methodSymbol.Parameters.Length; i++)
            {
                var parameter = methodSymbol.Parameters[i];
                var argument = invocation.ArgumentList.Arguments[i];
                
                result = result.Replace(parameter.Name, argument.Expression.ToString());
            }
        }
        
        return result;
    }

    private List<string> GenerateInsights(IMethodSymbol methodSymbol, int callSiteCount)
    {
        var insights = new List<string>();

        insights.Add($"Inlined method '{methodSymbol.Name}' with {methodSymbol.Parameters.Length} parameter(s)");
        
        if (callSiteCount > 0)
        {
            insights.Add($"Replaced {callSiteCount} call site(s) with method body");
        }

        if (methodSymbol.ReturnType.SpecialType != SpecialType.System_Void)
        {
            insights.Add($"Method returns {methodSymbol.ReturnType.ToDisplayString()}");
        }

        if (methodSymbol.IsStatic)
        {
            insights.Add("Inlined static method");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(InlineMethodParams parameters, string methodName)
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

        // Action to search for any remaining references (should be none)
        actions.Add(new AIAction
        {
            Action = ToolNames.SymbolSearch,
            Description = $"Verify that all references to '{methodName}' have been removed",
            Parameters = new Dictionary<string, object>
            {
                ["query"] = methodName,
                ["searchType"] = "exact"
            },
            Priority = 80,
            Category = "validation"
        });

        return actions;
    }
}

public class InlineMethodParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [Description("Path to the source file containing the method to inline")]
    public string FilePath { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Line must be positive")]
    [JsonPropertyName("line")]
    [Description("Line number where the method is located (1-based)")]
    public int Line { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Column must be positive")]
    [JsonPropertyName("column")]
    [Description("Column number where the method is located (1-based)")]
    public int Column { get; set; }

    [JsonPropertyName("forceInline")]
    [Description("Force inlining even if method has no references (default: false)")]
    public bool ForceInline { get; set; } = false;
}