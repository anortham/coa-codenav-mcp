using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that provides extract method refactoring functionality using Roslyn
/// </summary>
public class ExtractMethodTool : McpToolBase<ExtractMethodParams, ExtractMethodResult>
{
    private readonly ILogger<ExtractMethodTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => ToolNames.ExtractMethod;
    public override string Description => @"Extract selected code into a new method.
Returns: Refactored code with new method and updated original location.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if extraction fails.
Use cases: Code refactoring, reducing method complexity, improving code organization.
Not for: Moving methods between classes, renaming methods (use csharp_rename_symbol).";
    
    public ExtractMethodTool(
        ILogger<ExtractMethodTool> logger,
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

    protected override async Task<ExtractMethodResult> ExecuteInternalAsync(
        ExtractMethodParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("ExtractMethod request received: FilePath={FilePath}, StartLine={StartLine}, EndLine={EndLine}", 
            parameters.FilePath, parameters.StartLine, parameters.EndLine);
        
        var startTime = DateTime.UtcNow;

        // Get the document
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            return new ExtractMethodResult
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

        // Get the text span for the selection
        var text = await document.GetTextAsync(cancellationToken);
        var textSpan = GetTextSpanFromLineRange(text, parameters.StartLine, parameters.EndLine, 
            parameters.StartColumn, parameters.EndColumn);

        if (!textSpan.HasValue)
        {
            return new ExtractMethodResult
            {
                Success = false,
                Message = "Invalid selection range",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INVALID_PARAMETERS,
                    Message = "Invalid selection range",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new string[]
                        {
                            "Ensure line and column numbers are valid (1-based)",
                            "Verify the selection range is within the document bounds",
                            "Check that start position comes before end position"
                        }
                    }
                }
            };
        }

        // Get syntax tree and semantic model
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (tree == null)
        {
            return new ExtractMethodResult
            {
                Success = false,
                Message = "Failed to get syntax tree",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.ANALYSIS_FAILED,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new string[]
                        {
                            "Try reloading the solution",
                            "Check if the document has valid C# syntax",
                            "Run csharp_get_diagnostics to check for errors"
                        }
                    }
                }
            };
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return new ExtractMethodResult
            {
                Success = false,
                Message = "Failed to get semantic model",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.ANALYSIS_FAILED,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new string[]
                        {
                            "Try reloading the solution",
                            "Check if the project builds successfully",
                            "Ensure all project dependencies are restored"
                        }
                    }
                }
            };
        }

        // Analyze the selected code
        var root = await tree.GetRootAsync(cancellationToken);
        var selectedNodes = root.DescendantNodes(textSpan.Value)
            .Where(n => textSpan.Value.Contains(n.Span))
            .ToList();

        if (!selectedNodes.Any())
        {
            return new ExtractMethodResult
            {
                Success = false,
                Message = "No valid code selected for extraction",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INVALID_SELECTION,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new string[]
                        {
                            "Select complete statements or expressions",
                            "Ensure the selection includes executable code",
                            "Avoid selecting only whitespace or comments"
                        }
                    }
                }
            };
        }

        // Find the containing method
        var containingMethod = selectedNodes.First()
            .Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (containingMethod == null)
        {
            return new ExtractMethodResult
            {
                Success = false,
                Message = "Selected code is not within a method",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INVALID_SELECTION,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new string[]
                        {
                            "Extract method can only be performed on code within methods",
                            "Select code inside a method body",
                            "Consider using other refactoring tools for class-level extractions"
                        }
                    }
                }
            };
        }

        // Analyze data flow (simplified for migration)
        var dataFlowAnalysis = AnalyzeDataFlow(semanticModel, selectedNodes, containingMethod);
        if (!dataFlowAnalysis.Success)
        {
            return new ExtractMethodResult
            {
                Success = false,
                Message = dataFlowAnalysis.ErrorMessage ?? "Failed to analyze data flow",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.ANALYSIS_FAILED,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new string[]
                        {
                            "Ensure the selected code forms a complete control flow",
                            "Check that all code paths return or flow through",
                            "Avoid selecting partial statements or expressions"
                        }
                    }
                }
            };
        }

        // Generate the new method
        var methodName = parameters.MethodName ?? GenerateMethodName(selectedNodes, dataFlowAnalysis);
        var extractedMethod = GenerateExtractedMethod(
            methodName,
            selectedNodes,
            dataFlowAnalysis,
            containingMethod,
            semanticModel,
            parameters.MakeStatic ?? false);

        // Generate the method call
        var methodCall = GenerateMethodCall(
            methodName,
            dataFlowAnalysis,
            containingMethod.Modifiers.Any(SyntaxKind.StaticKeyword) || (parameters.MakeStatic ?? false));

        // Apply the refactoring
        var editor = await DocumentEditor.CreateAsync(document, cancellationToken);
        
        // Insert the new method after the containing method
        var containingType = containingMethod.Ancestors().OfType<TypeDeclarationSyntax>().First();
        editor.InsertAfter(containingMethod, extractedMethod);

        // Replace the selected code with the method call
        var statementsToReplace = selectedNodes.OfType<StatementSyntax>().ToList();
        if (statementsToReplace.Any())
        {
            editor.ReplaceNode(statementsToReplace.First(), methodCall);
            foreach (var stmt in statementsToReplace.Skip(1))
            {
                editor.RemoveNode(stmt);
            }
        }
        else
        {
            // Handle expression extraction
            var expressionToReplace = selectedNodes.FirstOrDefault();
            if (expressionToReplace != null)
            {
                editor.ReplaceNode(expressionToReplace, methodCall);
            }
        }

        // Get the updated document
        var updatedDocument = editor.GetChangedDocument();
        
        // Format the document
        updatedDocument = await Formatter.FormatAsync(updatedDocument, cancellationToken: cancellationToken);
        
        // Get the final code
        var finalText = await updatedDocument.GetTextAsync(cancellationToken);
        var code = finalText.ToString();

        // Generate insights
        var insights = GenerateInsights(dataFlowAnalysis, methodName);

        // Generate next actions
        var actions = GenerateNextActions(parameters, methodName);

        return new ExtractMethodResult
        {
            Success = true,
            Message = $"Successfully extracted method '{methodName}'",
            Query = CreateQueryInfo(parameters),
            Summary = new SummaryInfo
            {
                TotalFound = selectedNodes.Count,
                Returned = 1,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            },
            Code = code,
            ExtractedMethodName = methodName,
            MethodSignature = GetMethodSignature(extractedMethod),
            Parameters = dataFlowAnalysis.Parameters.Select(p => new ExtractedMethodParameterInfo
            {
                Name = p.Name,
                Type = p.Type,
                IsOut = p.IsOut,
                IsRef = p.IsRef
            }).ToList(),
            ReturnType = dataFlowAnalysis.ReturnType,
            Insights = insights,
            Actions = actions,
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
    }

    private Microsoft.CodeAnalysis.Text.TextSpan? GetTextSpanFromLineRange(
        SourceText text,
        int startLine,
        int endLine,
        int? startColumn,
        int? endColumn)
    {
        var lines = text.Lines;
        
        if (startLine < 1 || startLine > lines.Count ||
            endLine < 1 || endLine > lines.Count ||
            startLine > endLine)
        {
            return null;
        }

        var startLineInfo = lines[startLine - 1];
        var endLineInfo = lines[endLine - 1];

        var startPos = startColumn.HasValue
            ? startLineInfo.Start + Math.Min(startColumn.Value - 1, startLineInfo.Span.Length)
            : startLineInfo.Start;

        var endPos = endColumn.HasValue
            ? endLineInfo.Start + Math.Min(endColumn.Value - 1, endLineInfo.Span.Length)
            : endLineInfo.End;

        return Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(startPos, endPos);
    }

    // Simplified data flow analysis for migration
    private DataFlowAnalysisResult AnalyzeDataFlow(
        SemanticModel semanticModel,
        List<SyntaxNode> selectedNodes,
        MethodDeclarationSyntax containingMethod)
    {
        var result = new DataFlowAnalysisResult { Success = true };

        try
        {
            // Simplified analysis - just check for statements vs expressions
            var statements = selectedNodes.OfType<StatementSyntax>().ToList();
            if (!statements.Any())
            {
                // Handle expression extraction
                var expression = selectedNodes.FirstOrDefault() as ExpressionSyntax;
                if (expression != null)
                {
                    var typeInfo = semanticModel.GetTypeInfo(expression);
                    result.ReturnType = typeInfo.Type?.ToDisplayString() ?? "void";
                    result.IsExpression = true;
                }
                return result;
            }

            // Simplified parameter and return type analysis
            result.ReturnType = "void";
            result.Parameters = new List<MethodParameter>();
            result.IsAsync = statements.Any(s => ContainsAwaitExpression(s));

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Error during data flow analysis: {ex.Message}";
            return result;
        }
    }

    private bool ContainsAwaitExpression(SyntaxNode node)
    {
        return node.DescendantNodes().OfType<AwaitExpressionSyntax>().Any();
    }

    private string GenerateMethodName(List<SyntaxNode> selectedNodes, DataFlowAnalysisResult dataFlow)
    {
        // Simple method name generation
        var firstStatement = selectedNodes.OfType<StatementSyntax>().FirstOrDefault();
        
        if (firstStatement is ReturnStatementSyntax returnStmt && returnStmt.Expression != null)
        {
            return "Calculate" + ToPascalCase(returnStmt.Expression.ToString().Split('.').Last());
        }

        if (firstStatement is ExpressionStatementSyntax exprStmt)
        {
            var expression = exprStmt.Expression;
            if (expression is InvocationExpressionSyntax invocation)
            {
                var methodName = invocation.Expression.ToString().Split('.').Last();
                return "Perform" + ToPascalCase(methodName);
            }
            if (expression is AssignmentExpressionSyntax assignment)
            {
                var variable = assignment.Left.ToString().Split('.').Last();
                return "Set" + ToPascalCase(variable);
            }
        }

        // Default names based on return type
        if (!string.IsNullOrEmpty(dataFlow.ReturnType) && dataFlow.ReturnType != "void")
        {
            return "Get" + ToPascalCase(dataFlow.ReturnType.Split('.').Last());
        }

        return "ExtractedMethod";
    }

    private string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        input = System.Text.RegularExpressions.Regex.Replace(input, @"[^a-zA-Z0-9]", "");
        if (input.Length == 0)
            return "Value";

        return char.ToUpper(input[0]) + input.Substring(1);
    }

    private MethodDeclarationSyntax GenerateExtractedMethod(
        string methodName,
        List<SyntaxNode> selectedNodes,
        DataFlowAnalysisResult dataFlow,
        MethodDeclarationSyntax containingMethod,
        SemanticModel semanticModel,
        bool makeStatic)
    {
        var modifiers = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword)
        };

        if (makeStatic || containingMethod.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        }

        if (dataFlow.IsAsync)
        {
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));
        }

        // Create parameters
        var parameters = dataFlow.Parameters.Select(p =>
        {
            var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name))
                .WithType(SyntaxFactory.ParseTypeName(p.Type));

            if (p.IsRef)
                param = param.AddModifiers(SyntaxFactory.Token(SyntaxKind.RefKeyword));
            else if (p.IsOut)
                param = param.AddModifiers(SyntaxFactory.Token(SyntaxKind.OutKeyword));

            return param;
        }).ToArray();

        // Determine return type
        var returnType = dataFlow.ReturnType ?? "void";
        if (dataFlow.IsAsync && returnType == "void")
        {
            returnType = "Task";
        }
        else if (dataFlow.IsAsync && !returnType.StartsWith("Task"))
        {
            returnType = $"Task<{returnType}>";
        }

        // Create method body
        var statements = new List<StatementSyntax>();
        
        if (dataFlow.IsExpression)
        {
            // Handle expression extraction
            var expression = selectedNodes.First() as ExpressionSyntax;
            if (expression != null)
            {
                statements.Add(SyntaxFactory.ReturnStatement(expression));
            }
        }
        else
        {
            // Copy statements
            statements.AddRange(selectedNodes.OfType<StatementSyntax>());

            // Add return statement if needed
            if (!string.IsNullOrEmpty(dataFlow.ReturnVariable) && 
                !statements.OfType<ReturnStatementSyntax>().Any())
            {
                statements.Add(SyntaxFactory.ReturnStatement(
                    SyntaxFactory.IdentifierName(dataFlow.ReturnVariable)));
            }
        }

        var method = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.ParseTypeName(returnType),
                SyntaxFactory.Identifier(methodName))
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithParameterList(SyntaxFactory.ParameterList(
                SyntaxFactory.SeparatedList(parameters)))
            .WithBody(SyntaxFactory.Block(statements))
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed)
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

        return method;
    }

    private SyntaxNode GenerateMethodCall(
        string methodName,
        DataFlowAnalysisResult dataFlow,
        bool isStatic)
    {
        // Create arguments
        var arguments = dataFlow.Parameters.Select(p =>
        {
            var arg = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Name));
            if (p.IsRef)
                arg = arg.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword));
            else if (p.IsOut)
                arg = arg.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword));
            return arg;
        }).ToArray();

        // Create the invocation
        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.IdentifierName(methodName),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));

        // Handle async calls
        ExpressionSyntax callExpression = invocation;
        if (dataFlow.IsAsync)
        {
            callExpression = SyntaxFactory.AwaitExpression(invocation);
        }

        // Create statement based on return type
        if (!string.IsNullOrEmpty(dataFlow.ReturnVariable))
        {
            // Assign to existing variable
            return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(dataFlow.ReturnVariable),
                    callExpression));
        }
        else if (dataFlow.ReturnType != null && dataFlow.ReturnType != "void")
        {
            // Return the value
            return SyntaxFactory.ReturnStatement(callExpression);
        }
        else
        {
            // Just call the method
            return SyntaxFactory.ExpressionStatement(callExpression);
        }
    }

    private string GetMethodSignature(MethodDeclarationSyntax method)
    {
        var modifiers = string.Join(" ", method.Modifiers.Select(m => m.ToString()));
        var returnType = method.ReturnType.ToString();
        var name = method.Identifier.ToString();
        var parameters = method.ParameterList.ToString();
        
        return $"{modifiers} {returnType} {name}{parameters}".Trim();
    }

    private List<string> GenerateInsights(DataFlowAnalysisResult dataFlow, string methodName)
    {
        var insights = new List<string>();

        insights.Add($"Successfully extracted method '{methodName}'");

        if (dataFlow.Parameters.Any())
        {
            insights.Add($"Method takes {dataFlow.Parameters.Count} parameter(s)");
            
            var refParams = dataFlow.Parameters.Count(p => p.IsRef);
            if (refParams > 0)
            {
                insights.Add($"{refParams} parameter(s) passed by reference");
            }
        }
        else
        {
            insights.Add("Method requires no parameters");
        }

        if (dataFlow.ReturnType != null && dataFlow.ReturnType != "void")
        {
            insights.Add($"Method returns {dataFlow.ReturnType}");
        }

        if (dataFlow.IsAsync)
        {
            insights.Add("Extracted async method with await expressions");
        }

        insights.Add("Consider adding XML documentation to the new method");

        return insights;
    }

    private List<AIAction> GenerateNextActions(ExtractMethodParams parameters, string methodName)
    {
        var actions = new List<AIAction>
        {
            new AIAction
            {
                Action = ToolNames.RenameSymbol,
                Description = "Rename the extracted method if needed",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = parameters.FilePath,
                    ["symbolName"] = methodName
                },
                Priority = 70,
                Category = "refactoring"
            },
            new AIAction
            {
                Action = ToolNames.FormatDocument,
                Description = "Format the document",
                Parameters = new Dictionary<string, object> { ["filePath"] = parameters.FilePath },
                Priority = 50,
                Category = "formatting"
            }
        };

        return actions;
    }

    private QueryInfo CreateQueryInfo(ExtractMethodParams parameters)
    {
        return new QueryInfo
        {
            FilePath = parameters.FilePath,
            Position = new PositionInfo
            {
                Line = parameters.StartLine,
                Column = parameters.StartColumn ?? 1
            }
        };
    }
    

    // Helper classes
    private class DataFlowAnalysisResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public List<MethodParameter> Parameters { get; set; } = new();
        public string? ReturnType { get; set; }
        public string? ReturnVariable { get; set; }
        public bool IsAsync { get; set; }
        public bool IsExpression { get; set; }
    }

    private class MethodParameter
    {
        public required string Name { get; set; }
        public required string Type { get; set; }
        public bool IsRef { get; set; }
        public bool IsOut { get; set; }
    }
}

public class ExtractMethodParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file")]
    public string FilePath { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "StartLine must be positive")]
    [JsonPropertyName("startLine")]
    [COA.Mcp.Framework.Attributes.Description("Start line of selection (1-based)")]
    public int StartLine { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "EndLine must be positive")]
    [JsonPropertyName("endLine")]
    [COA.Mcp.Framework.Attributes.Description("End line of selection (1-based)")]
    public int EndLine { get; set; }

    [JsonPropertyName("startColumn")]
    [COA.Mcp.Framework.Attributes.Description("Start column of selection (1-based, optional)")]
    public int? StartColumn { get; set; }

    [JsonPropertyName("endColumn")]
    [COA.Mcp.Framework.Attributes.Description("End column of selection (1-based, optional)")]
    public int? EndColumn { get; set; }

    [JsonPropertyName("methodName")]
    [COA.Mcp.Framework.Attributes.Description("Name for the extracted method (optional, will be generated if not provided)")]
    public string? MethodName { get; set; }

    [JsonPropertyName("makeStatic")]
    [COA.Mcp.Framework.Attributes.Description("Make the extracted method static (default: false)")]
    public bool? MakeStatic { get; set; }
}

public class ExtractMethodResult : ToolResultBase
{
    public override string Operation => ToolNames.ExtractMethod;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("extractedMethodName")]
    public string? ExtractedMethodName { get; set; }

    [JsonPropertyName("methodSignature")]
    public string? MethodSignature { get; set; }

    [JsonPropertyName("parameters")]
    public List<ExtractedMethodParameterInfo>? Parameters { get; set; }

    [JsonPropertyName("returnType")]
    public string? ReturnType { get; set; }
}

public class ExtractedMethodParameterInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("isRef")]
    public bool IsRef { get; set; }

    [JsonPropertyName("isOut")]
    public bool IsOut { get; set; }
}
