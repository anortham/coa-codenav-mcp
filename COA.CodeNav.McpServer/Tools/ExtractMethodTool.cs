using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

[McpServerToolType]
public class ExtractMethodTool
{
    private readonly ILogger<ExtractMethodTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public ExtractMethodTool(
        ILogger<ExtractMethodTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "roslyn_extract_method")]
    [Description(@"Extract selected code into a new method.
Returns: Refactored code with new method and updated call site.
Prerequisites: Valid code selection that can be extracted.
Use cases: Refactor long methods, extract reusable logic, improve code organization.
AI benefit: Helps maintain clean code architecture.")]
    public async Task<object> ExecuteAsync(ExtractMethodParams parameters, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            // Get the document
            var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                return CreateErrorResult(
                    ErrorCodes.DOCUMENT_NOT_FOUND,
                    $"Document not found: {parameters.FilePath}",
                    new List<string>
                    {
                        "Ensure the file path is correct and absolute",
                        "Verify the file exists in the loaded solution/project",
                        "Load a solution using roslyn_load_solution or project using roslyn_load_project"
                    },
                    parameters,
                    startTime);
            }

            // Get the text span for the selection
            var text = await document.GetTextAsync(cancellationToken);
            var textSpan = GetTextSpanFromLineRange(text, parameters.StartLine, parameters.EndLine, 
                parameters.StartColumn, parameters.EndColumn);

            if (!textSpan.HasValue)
            {
                return CreateErrorResult(
                    ErrorCodes.INVALID_PARAMETERS,
                    "Invalid selection range",
                    new List<string>
                    {
                        "Ensure line and column numbers are valid (1-based)",
                        "Verify the selection range is within the document bounds",
                        "Check that start position comes before end position"
                    },
                    parameters,
                    startTime);
            }

            // Get syntax tree and semantic model
            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (tree == null)
            {
                return CreateErrorResult(
                    ErrorCodes.ANALYSIS_FAILED,
                    "Failed to get syntax tree",
                    new List<string>
                    {
                        "Try reloading the solution",
                        "Check if the document has valid C# syntax",
                        "Run roslyn_get_diagnostics to check for errors"
                    },
                    parameters,
                    startTime);
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                return CreateErrorResult(
                    ErrorCodes.ANALYSIS_FAILED,
                    "Failed to get semantic model",
                    new List<string>
                    {
                        "Try reloading the solution",
                        "Check if the project builds successfully",
                        "Ensure all project dependencies are restored"
                    },
                    parameters,
                    startTime);
            }

            // Analyze the selected code
            var root = await tree.GetRootAsync(cancellationToken);
            var selectedNodes = root.DescendantNodes(textSpan.Value)
                .Where(n => textSpan.Value.Contains(n.Span))
                .ToList();

            if (!selectedNodes.Any())
            {
                return CreateErrorResult(
                    ErrorCodes.INVALID_SELECTION,
                    "No valid code selected for extraction",
                    new List<string>
                    {
                        "Select complete statements or expressions",
                        "Ensure the selection includes executable code",
                        "Avoid selecting only whitespace or comments"
                    },
                    parameters,
                    startTime);
            }

            // Find the containing method
            var containingMethod = selectedNodes.First()
                .Ancestors()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault();

            if (containingMethod == null)
            {
                return CreateErrorResult(
                    ErrorCodes.INVALID_SELECTION,
                    "Selected code is not within a method",
                    new List<string>
                    {
                        "Extract method can only be performed on code within methods",
                        "Select code inside a method body",
                        "Consider using other refactoring tools for class-level extractions"
                    },
                    parameters,
                    startTime);
            }

            // Analyze data flow
            var dataFlowAnalysis = AnalyzeDataFlow(semanticModel, selectedNodes, containingMethod);
            if (!dataFlowAnalysis.Success)
            {
                return CreateErrorResult(
                    ErrorCodes.ANALYSIS_FAILED,
                    dataFlowAnalysis.ErrorMessage ?? "Failed to analyze data flow",
                    new List<string>
                    {
                        "Ensure the selected code forms a complete control flow",
                        "Check that all code paths return or flow through",
                        "Avoid selecting partial statements or expressions"
                    },
                    parameters,
                    startTime);
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
                Meta = new ToolMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    Truncated = false
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting method");
            return CreateErrorResult(
                ErrorCodes.INTERNAL_ERROR,
                $"Error extracting method: {ex.Message}",
                new List<string>
                {
                    "Check the server logs for detailed error information",
                    "Verify the selection contains valid extractable code",
                    "Ensure the code compiles without errors"
                },
                parameters,
                startTime);
        }
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

    private DataFlowAnalysisResult AnalyzeDataFlow(
        SemanticModel semanticModel,
        List<SyntaxNode> selectedNodes,
        MethodDeclarationSyntax containingMethod)
    {
        var result = new DataFlowAnalysisResult { Success = true };

        try
        {
            // Get statements for data flow analysis
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

            // Perform data flow analysis
            var firstStatement = statements.First();
            var lastStatement = statements.Last();
            var dataFlow = semanticModel.AnalyzeDataFlow(firstStatement, lastStatement);

            if (dataFlow == null)
            {
                result.Success = false;
                result.ErrorMessage = "Failed to analyze data flow";
                return result;
            }

            // Analyze variables
            var variablesIn = dataFlow.DataFlowsIn.ToList();
            var variablesOut = dataFlow.DataFlowsOut.ToList();
            var declaredInside = dataFlow.VariablesDeclared.ToList();

            // Determine parameters
            foreach (var variable in variablesIn)
            {
                if (!declaredInside.Contains(variable))
                {
                    ISymbol? symbol = variable as ILocalSymbol;
                    if (symbol == null)
                        symbol = variable as IParameterSymbol;
                    if (symbol != null)
                    {
                        var typeStr = "object"; // Default type
                        if (symbol is ILocalSymbol local)
                            typeStr = local.Type.ToDisplayString();
                        else if (symbol is IParameterSymbol param)
                            typeStr = param.Type.ToDisplayString();
                        
                        var methodParam = new MethodParameter
                        {
                            Name = symbol.Name,
                            Type = typeStr,
                            IsRef = variablesOut.Contains(variable),
                            IsOut = false
                        };
                        result.Parameters.Add(methodParam);
                    }
                }
            }

            // Determine return value
            var returnStatements = statements.OfType<ReturnStatementSyntax>().ToList();
            if (returnStatements.Any())
            {
                var returnExpression = returnStatements.First().Expression;
                if (returnExpression != null)
                {
                    var typeInfo = semanticModel.GetTypeInfo(returnExpression);
                    result.ReturnType = typeInfo.Type?.ToDisplayString() ?? "void";
                }
            }

            // Check for variables used after the selection
            var usedAfter = variablesOut.Where(v => declaredInside.Contains(v)).ToList();
            if (usedAfter.Count > 1)
            {
                result.Success = false;
                result.ErrorMessage = "Multiple variables are used after the selected code. Extract method can only return one value.";
                return result;
            }
            else if (usedAfter.Count == 1 && !returnStatements.Any())
            {
                var variable = usedAfter.First();
                var varSymbol = variable as ILocalSymbol;
                if (varSymbol != null)
                {
                    result.ReturnType = varSymbol.Type.ToDisplayString();
                }
                else
                {
                    result.ReturnType = "object";
                }
                result.ReturnVariable = variable.Name;
            }

            // Check if method should be async
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
        // Try to generate a meaningful name based on the code
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

        insights.Add($"✅ Successfully extracted method '{methodName}'");

        if (dataFlow.Parameters.Any())
        {
            insights.Add($"📥 Method takes {dataFlow.Parameters.Count} parameter(s)");
            
            var refParams = dataFlow.Parameters.Count(p => p.IsRef);
            if (refParams > 0)
            {
                insights.Add($"🔄 {refParams} parameter(s) passed by reference");
            }
        }
        else
        {
            insights.Add("📦 Method requires no parameters");
        }

        if (dataFlow.ReturnType != null && dataFlow.ReturnType != "void")
        {
            insights.Add($"📤 Method returns {dataFlow.ReturnType}");
        }

        if (dataFlow.IsAsync)
        {
            insights.Add("⚡ Extracted async method with await expressions");
        }

        insights.Add("💡 Consider adding XML documentation to the new method");

        return insights;
    }

    private List<NextAction> GenerateNextActions(ExtractMethodParams parameters, string methodName)
    {
        var actions = new List<NextAction>
        {
            new NextAction
            {
                Id = "add_documentation",
                Description = $"Add XML documentation to '{methodName}'",
                ToolName = "roslyn_generate_code",
                Parameters = new 
                { 
                    filePath = parameters.FilePath,
                    generationType = "documentation",
                    targetMethod = methodName
                },
                Priority = "high"
            },
            new NextAction
            {
                Id = "rename_method",
                Description = "Rename the extracted method if needed",
                ToolName = "roslyn_rename_symbol",
                Parameters = new
                {
                    filePath = parameters.FilePath,
                    symbolName = methodName
                },
                Priority = "medium"
            },
            new NextAction
            {
                Id = "find_similar",
                Description = "Find similar code that could use this method",
                ToolName = "roslyn_find_all_references",
                Parameters = new
                {
                    filePath = parameters.FilePath,
                    symbolName = methodName
                },
                Priority = "low"
            },
            new NextAction
            {
                Id = "format_document",
                Description = "Format the document",
                ToolName = "roslyn_format_document",
                Parameters = new { filePath = parameters.FilePath },
                Priority = "low"
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

    private object CreateErrorResult(
        string errorCode,
        string message,
        List<string> recoverySteps,
        ExtractMethodParams parameters,
        DateTime startTime)
    {
        return new ExtractMethodResult
        {
            Success = false,
            Message = message,
            Error = new ErrorInfo
            {
                Code = errorCode,
                Recovery = new RecoveryInfo
                {
                    Steps = recoverySteps,
                    SuggestedActions = new List<SuggestedAction>
                    {
                        new SuggestedAction
                        {
                            Tool = "roslyn_get_diagnostics",
                            Description = "Check for compilation errors",
                            Parameters = new { filePath = parameters.FilePath }
                        }
                    }
                }
            },
            Query = CreateQueryInfo(parameters),
            Meta = new ToolMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
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
    [JsonPropertyName("filePath")]
    [Description("Path to the source file")]
    public required string FilePath { get; set; }

    [JsonPropertyName("startLine")]
    [Description("Start line of selection (1-based)")]
    public required int StartLine { get; set; }

    [JsonPropertyName("endLine")]
    [Description("End line of selection (1-based)")]
    public required int EndLine { get; set; }

    [JsonPropertyName("startColumn")]
    [Description("Start column of selection (1-based, optional)")]
    public int? StartColumn { get; set; }

    [JsonPropertyName("endColumn")]
    [Description("End column of selection (1-based, optional)")]
    public int? EndColumn { get; set; }

    [JsonPropertyName("methodName")]
    [Description("Name for the extracted method (optional, will be generated if not provided)")]
    public string? MethodName { get; set; }

    [JsonPropertyName("makeStatic")]
    [Description("Make the extracted method static (default: false)")]
    public bool? MakeStatic { get; set; }
}

public class ExtractMethodResult : ToolResultBase
{
    public override string Operation => "roslyn_extract_method";

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