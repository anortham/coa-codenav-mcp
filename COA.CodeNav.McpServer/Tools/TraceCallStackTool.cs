using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that traces execution paths through code, showing complete call chains
/// </summary>
[McpServerToolType]
public class TraceCallStackTool : ITool
{
    private readonly ILogger<TraceCallStackTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "roslyn_trace_call_stack";
    public string Description => "Trace execution paths through code from entry points to implementations";

    public TraceCallStackTool(
        ILogger<TraceCallStackTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "roslyn_trace_call_stack")]
    [Description(@"Trace execution paths through code from entry points to implementations.
Returns: Complete call chains with conditions, branches, and insights about code flow.
Prerequisites: Call roslyn_load_solution or roslyn_load_project first.
Error handling: Returns specific error codes with recovery steps if starting point cannot be found.
Use cases: Understanding API flows, tracing event handlers, debugging call chains, analyzing code paths.
Not for: Static analysis (use other tools), finding implementations (use roslyn_find_implementations).")]
    public async Task<object> ExecuteAsync(TraceCallStackParams parameters, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("TraceCallStack request: FilePath={FilePath}, Line={Line}, Column={Column}, Direction={Direction}", 
            parameters.FilePath, parameters.Line, parameters.Column, parameters.Direction);
            
        try
        {
            _logger.LogInformation("Processing call stack trace for {FilePath} at {Line}:{Column}", 
                parameters.FilePath, parameters.Line, parameters.Column);

            // Get the document
            var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
                return new TraceCallStackResult
                {
                    Success = false,
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
                            }
                        }
                    }
                };
            }

            // Get the starting symbol
            var sourceText = await document.GetTextAsync(cancellationToken);
            var position = sourceText.Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
                parameters.Line - 1, 
                parameters.Column - 1));

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                _logger.LogError("Failed to get semantic model for document: {FilePath}", parameters.FilePath);
                return new TraceCallStackResult
                {
                    Success = false,
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

            // Find the method at the position
            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
            var root = await syntaxTree!.GetRootAsync(cancellationToken);
            var token = root.FindToken(position);
            
            // Find the containing method
            var methodSyntax = token.Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (methodSyntax == null)
            {
                _logger.LogDebug("No method found at position {Line}:{Column}", parameters.Line, parameters.Column);
                return new TraceCallStackResult
                {
                    Success = false,
                    Message = "No method found at the specified position",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.NO_SYMBOL_AT_POSITION,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the cursor is inside a method body",
                                "Try positioning at the method name",
                                "Verify the line and column numbers are correct (1-based)"
                            }
                        }
                    }
                };
            }

            var methodSymbol = semanticModel.GetDeclaredSymbol(methodSyntax) as IMethodSymbol;
            if (methodSymbol == null)
            {
                return new TraceCallStackResult
                {
                    Success = false,
                    Message = "Could not resolve method symbol"
                };
            }

            _logger.LogDebug("Found method '{MethodName}', starting trace in {Direction} direction", 
                methodSymbol.ToDisplayString(), parameters.Direction);

            // Trace the call stack
            var allPaths = new List<CallPath>();
            var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            
            if (parameters.Direction == "forward")
            {
                // Trace calls made by this method
                var path = await TraceForwardAsync(methodSymbol, document, parameters.MaxDepth ?? 10, visited, cancellationToken);
                if (path != null)
                    allPaths.Add(path);
            }
            else // backward
            {
                // Find all callers of this method
                var callers = await FindCallersAsync(methodSymbol, document.Project.Solution, cancellationToken);
                foreach (var caller in callers.Take(20)) // Get more initially, then apply token limit
                {
                    var path = await TraceBackwardAsync(caller, methodSymbol, document.Project.Solution, parameters.MaxDepth ?? 10, visited, cancellationToken);
                    if (path != null)
                        allPaths.Add(path);
                }
            }
            
            // Apply token management
            var response = TokenEstimator.CreateTokenAwareResponse(
                allPaths,
                paths => EstimateCallPathsTokens(paths, parameters.IncludeFramework),
                requestedMax: parameters.MaxPaths ?? 10, // Default to 10 paths
                safetyLimit: TokenEstimator.DEFAULT_SAFETY_LIMIT,
                toolName: "roslyn_trace_call_stack"
            );

            // Generate insights (use all paths for accurate insights)
            var insights = GenerateInsights(allPaths, methodSymbol);
            
            // Add truncation message if needed
            if (response.WasTruncated)
            {
                insights.Insert(0, response.GetTruncationMessage());
            }
            
            var keyFindings = GenerateKeyFindings(response.Items, methodSymbol);
            var nextActions = GenerateNextActions(methodSymbol, parameters);
            
            // Add action to get more results if truncated
            if (response.WasTruncated)
            {
                nextActions.Insert(0, new NextAction
                {
                    Id = "get_more_paths",
                    Description = "Get additional call paths",
                    ToolName = "roslyn_trace_call_stack",
                    Parameters = new
                    {
                        filePath = parameters.FilePath,
                        line = parameters.Line,
                        column = parameters.Column,
                        direction = parameters.Direction,
                        maxPaths = Math.Min(allPaths.Count, 50),
                        maxDepth = parameters.MaxDepth
                    },
                    Priority = "high"
                });
            }

            // Store full result if truncated
            string? resourceUri = null;
            if (response.WasTruncated && _resourceProvider != null)
            {
                resourceUri = _resourceProvider.StoreAnalysisResult("call-stack-trace", 
                    new { 
                        method = methodSymbol.ToDisplayString(), 
                        paths = allPaths, 
                        totalPaths = allPaths.Count,
                        direction = parameters.Direction 
                    }, 
                    $"All {allPaths.Count} call paths for {methodSymbol.Name}");
            }

            var result = new TraceCallStackResult
            {
                Success = true,
                StartMethod = methodSymbol.ToDisplayString(),
                Direction = parameters.Direction,
                Paths = response.Items,
                Insights = insights,
                KeyFindings = keyFindings,
                NextActions = nextActions,
                ResourceUri = resourceUri,
                Message = response.WasTruncated 
                    ? $"Traced {allPaths.Count} path(s) from {methodSymbol.Name} (showing {response.ReturnedCount})"
                    : $"Traced {allPaths.Count} path(s) from {methodSymbol.Name}",
                Meta = new ToolMetadata
                {
                    ExecutionTime = "0ms", // TODO: Add timing
                    Truncated = response.WasTruncated,
                    Tokens = response.EstimatedTokens
                }
            };

            _logger.LogInformation("Call stack trace completed: Found {PathCount} paths", allPaths.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in TraceCallStack");
            return new TraceCallStackResult
            {
                Success = false,
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

    private async Task<CallPath?> TraceForwardAsync(
        IMethodSymbol startMethod, 
        Document document,
        int maxDepth, 
        HashSet<IMethodSymbol> visited,
        CancellationToken cancellationToken)
    {
        if (maxDepth <= 0 || visited.Contains(startMethod))
            return null;

        visited.Add(startMethod);
        
        var path = new CallPath
        {
            StartMethod = startMethod.ToDisplayString(),
            PathType = DeterminePathType(startMethod),
            Steps = new List<CallStep>()
        };

        // Get the method body
        var references = startMethod.DeclaringSyntaxReferences;
        if (!references.Any())
            return path;

        var syntaxRef = references.First();
        var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken) as MethodDeclarationSyntax;
        if (syntax?.Body == null && syntax?.ExpressionBody == null)
            return path;

        // Get the correct document for this syntax tree
        var syntaxTree = syntax.SyntaxTree;
        var solution = document.Project.Solution;
        var methodDocument = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.GetSyntaxTreeAsync(cancellationToken).Result == syntaxTree);
            
        if (methodDocument == null)
        {
            _logger.LogWarning("Could not find document for method {Method}", startMethod.ToDisplayString());
            return path;
        }

        var semanticModel = await methodDocument.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
            return path;

        // Find all method invocations in the body
        var invocations = syntax.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .ToList();

        var step = new CallStep
        {
            Method = startMethod.ToDisplayString(),
            File = $"{methodDocument.FilePath}:{syntax.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1}",
            Calls = new List<string>(),
            Conditions = ExtractConditions(syntax),
            Insights = new List<string>()
        };

        // Add insights about the method
        if (startMethod.IsAsync)
            step.Insights.Add("Async method");
        if (IsApiEndpoint(startMethod))
            step.Insights.Add("API endpoint");
        if (IsEventHandler(startMethod))
            step.Insights.Add("Event handler");

        foreach (var invocation in invocations)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            if (symbolInfo.Symbol is IMethodSymbol calledMethod)
            {
                step.Calls.Add(calledMethod.ToDisplayString());
                
                // Skip framework methods unless requested
                if (!IsFrameworkMethod(calledMethod))
                {
                    // Recursively trace this call
                    var nestedPath = await TraceForwardAsync(calledMethod, methodDocument, maxDepth - 1, visited, cancellationToken);
                    if (nestedPath != null && nestedPath.Steps.Any())
                    {
                        path.Steps.AddRange(nestedPath.Steps);
                    }
                }
            }
        }

        path.Steps.Insert(0, step);
        path.EndMethod = path.Steps.LastOrDefault()?.Method ?? startMethod.ToDisplayString();
        path.IsComplete = invocations.Count == 0 || maxDepth == 1;

        return path;
    }

    private Task<CallPath?> TraceBackwardAsync(
        IMethodSymbol callerMethod,
        IMethodSymbol targetMethod,
        Solution solution,
        int maxDepth,
        HashSet<IMethodSymbol> visited,
        CancellationToken cancellationToken)
    {
        var path = new CallPath
        {
            StartMethod = callerMethod.ToDisplayString(),
            EndMethod = targetMethod.ToDisplayString(),
            PathType = DeterminePathType(callerMethod),
            Steps = new List<CallStep>(),
            IsComplete = true
        };

        var currentMethod = callerMethod;
        var depth = 0;

        while (currentMethod != null && depth < maxDepth && !visited.Contains(currentMethod))
        {
            visited.Add(currentMethod);

            var step = new CallStep
            {
                Method = currentMethod.ToDisplayString(),
                File = GetMethodLocation(currentMethod),
                Calls = new List<string>(),
                Conditions = new List<string>(),
                Insights = new List<string>()
            };

            // Add insights
            if (currentMethod.IsAsync)
                step.Insights.Add("Async method");
            if (IsApiEndpoint(currentMethod))
            {
                step.Insights.Add("API endpoint - possible entry point");
                path.Steps.Add(step);
                break; // Found an entry point
            }
            if (IsEventHandler(currentMethod))
            {
                step.Insights.Add("Event handler - possible entry point");
                path.Steps.Add(step);
                break; // Found an entry point
            }

            path.Steps.Add(step);

            // Find callers of current method - need to get solution context
            // For now, we can't find callers without a solution context
            // This would need to be passed down from the original call
            var callers = new List<IMethodSymbol>();
            if (callers.Any())
            {
                // Pick the most likely caller (e.g., in same namespace)
                currentMethod = callers.FirstOrDefault(c => 
                    c.ContainingNamespace.Equals(currentMethod.ContainingNamespace, SymbolEqualityComparer.Default))
                    ?? callers.First();
            }
            else
            {
                break; // No more callers
            }

            depth++;
        }

        // Reverse the path since we traced backward
        path.Steps.Reverse();
        return Task.FromResult<CallPath?>(path);
    }

    private async Task<List<IMethodSymbol>> FindCallersAsync(IMethodSymbol method, Solution solution, CancellationToken cancellationToken)
    {
        var callers = new List<IMethodSymbol>();
        var references = await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken);
        
        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
            {
                var document = solution.GetDocument(location.Document.Id);
                if (document != null)
                {
                    var root = await location.Document.GetSyntaxRootAsync(cancellationToken);
                    var node = root?.FindNode(location.Location.SourceSpan);
                    
                    // Find containing method
                    var containingMethod = node?.AncestorsAndSelf()
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault();
                        
                    if (containingMethod != null)
                    {
                        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                        var methodSymbol = semanticModel?.GetDeclaredSymbol(containingMethod) as IMethodSymbol;
                        if (methodSymbol != null && !callers.Any(c => SymbolEqualityComparer.Default.Equals(c, methodSymbol)))
                        {
                            callers.Add(methodSymbol);
                        }
                    }
                }
            }
        }
        
        return callers;
    }

    private List<string> ExtractConditions(MethodDeclarationSyntax method)
    {
        var conditions = new List<string>();
        
        var ifStatements = method.DescendantNodes().OfType<IfStatementSyntax>();
        foreach (var ifStmt in ifStatements)
        {
            conditions.Add($"if ({ifStmt.Condition})");
        }
        
        var switchStatements = method.DescendantNodes().OfType<SwitchStatementSyntax>();
        foreach (var switchStmt in switchStatements)
        {
            conditions.Add($"switch ({switchStmt.Expression})");
        }
        
        return conditions;
    }

    private string DeterminePathType(IMethodSymbol method)
    {
        if (IsApiEndpoint(method))
            return "API Flow";
        if (IsEventHandler(method))
            return "Event Flow";
        if (method.IsAsync)
            return "Async Flow";
        if (method.DeclaredAccessibility == Accessibility.Private)
            return "Internal Flow";
        return "Standard Flow";
    }

    private bool IsApiEndpoint(IMethodSymbol method)
    {
        // Check for common API attributes
        var attributes = method.GetAttributes();
        return attributes.Any(a => 
            a.AttributeClass?.Name.Contains("HttpGet") == true ||
            a.AttributeClass?.Name.Contains("HttpPost") == true ||
            a.AttributeClass?.Name.Contains("HttpPut") == true ||
            a.AttributeClass?.Name.Contains("HttpDelete") == true ||
            a.AttributeClass?.Name.Contains("Route") == true);
    }

    private bool IsEventHandler(IMethodSymbol method)
    {
        // Check if method signature matches event handler pattern
        return method.Parameters.Length == 2 &&
               method.Parameters[0].Type.Name == "Object" &&
               method.Parameters[1].Type.Name.Contains("EventArgs");
    }

    private bool IsFrameworkMethod(IMethodSymbol method)
    {
        var ns = method.ContainingNamespace?.ToDisplayString() ?? "";
        return ns.StartsWith("System.") || 
               ns.StartsWith("Microsoft.") ||
               ns.StartsWith("Newtonsoft.") ||
               ns.StartsWith("EntityFramework");
    }

    private string GetMethodLocation(IMethodSymbol method)
    {
        var location = method.Locations.FirstOrDefault();
        if (location != null && location.IsInSource)
        {
            var lineSpan = location.GetLineSpan();
            return $"{lineSpan.Path}:{lineSpan.StartLinePosition.Line + 1}";
        }
        return "<unknown>";
    }

    private List<string> GenerateInsights(List<CallPath> paths, IMethodSymbol startMethod)
    {
        var insights = new List<string>();
        
        if (paths.Any(p => p.PathType == "API Flow"))
            insights.Add("This appears to be an API endpoint flow");
            
        if (paths.Any(p => p.Steps.Any(s => s.Insights.Contains("Async method"))))
            insights.Add("Uses async/await pattern throughout the call chain");
            
        if (paths.Any() && paths.Any(p => p.Steps.Any()))
        {
            var maxDepth = paths.Max(p => p.Steps.Count);
            insights.Add($"Maximum call depth: {maxDepth} levels");
            
            var uniqueMethods = paths.SelectMany(p => p.Steps.Select(s => s.Method)).Distinct().Count();
            insights.Add($"Touches {uniqueMethods} unique methods");
        }
        
        return insights;
    }

    private Dictionary<string, string> GenerateKeyFindings(List<CallPath> paths, IMethodSymbol startMethod)
    {
        var findings = new Dictionary<string, string>();
        
        // Find entry points
        var entryPoints = paths.SelectMany(p => p.Steps)
            .Where(s => s.Insights.Any(i => i.Contains("entry point")))
            .Select(s => s.Method)
            .Distinct()
            .ToList();
            
        if (entryPoints.Any())
            findings["entryPoints"] = string.Join(", ", entryPoints);
            
        // Detect patterns
        var hasDatabase = paths.Any(p => p.Steps.Any(s => 
            s.Calls.Any(c => c.Contains("DbContext") || c.Contains("Repository"))));
        if (hasDatabase)
            findings["dataAccess"] = "Includes database operations";
            
        var hasValidation = paths.Any(p => p.Steps.Any(s => 
            s.Calls.Any(c => c.Contains("Validate") || c.Contains("Validator"))));
        if (hasValidation)
            findings["validation"] = "Includes validation logic";
            
        return findings;
    }

    private int EstimateCallPathsTokens(List<CallPath> paths, bool includeFramework)
    {
        return TokenEstimator.EstimateCollection(
            paths,
            path => {
                var pathTokens = 100; // Base for path metadata
                pathTokens += path.Steps.Sum(step => {
                    var stepTokens = TokenEstimator.Roslyn.EstimateCallFrame(step, includeFramework);
                    // Add extra for calls list
                    stepTokens += step.Calls.Count * 50;
                    // Add extra for conditions
                    stepTokens += step.Conditions.Sum(c => TokenEstimator.EstimateString(c));
                    return stepTokens;
                });
                return pathTokens;
            },
            baseTokens: TokenEstimator.BASE_RESPONSE_TOKENS
        );
    }
    
    private List<NextAction> GenerateNextActions(IMethodSymbol method, TraceCallStackParams parameters)
    {
        var actions = new List<NextAction>();
        
        // Suggest opposite direction
        actions.Add(new NextAction
        {
            Id = "trace_opposite",
            Description = parameters.Direction == "forward" 
                ? "Trace who calls this method" 
                : "Trace what this method calls",
            ToolName = "roslyn_trace_call_stack",
            Parameters = new
            {
                filePath = parameters.FilePath,
                line = parameters.Line,
                column = parameters.Column,
                direction = parameters.Direction == "forward" ? "backward" : "forward",
                maxDepth = parameters.MaxDepth
            },
            Priority = "high"
        });
        
        // Suggest finding references
        actions.Add(new NextAction
        {
            Id = "find_references",
            Description = $"Find all references to '{method.Name}'",
            ToolName = "roslyn_find_all_references",
            Parameters = new
            {
                filePath = parameters.FilePath,
                line = parameters.Line,
                column = parameters.Column
            },
            Priority = "medium"
        });
        
        return actions;
    }
}

public class TraceCallStackParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file containing the method to trace")]
    public required string FilePath { get; set; }

    [JsonPropertyName("line")]
    [Description("Line number (1-based) inside the method to trace from")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    [Description("Column number (1-based) inside the method to trace from")]
    public int Column { get; set; }
    
    [JsonPropertyName("direction")]
    [Description("Trace direction: 'forward' (follow calls made by this method) or 'backward' (find callers of this method)")]
    public string Direction { get; set; } = "forward";
    
    [JsonPropertyName("maxDepth")]
    [Description("Maximum depth to trace (default: 10)")]
    public int? MaxDepth { get; set; }
    
    [JsonPropertyName("includeFramework")]
    [Description("Include framework method calls (default: false)")]
    public bool IncludeFramework { get; set; } = false;
    
    [JsonPropertyName("maxPaths")]
    [Description("Maximum number of paths to return (default: 10, max: 50)")]
    public int? MaxPaths { get; set; }
}

public class TraceCallStackResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("startMethod")]
    public string? StartMethod { get; set; }
    
    [JsonPropertyName("direction")]
    public string? Direction { get; set; }
    
    [JsonPropertyName("paths")]
    public List<CallPath>? Paths { get; set; }
    
    [JsonPropertyName("insights")]
    public List<string>? Insights { get; set; }
    
    [JsonPropertyName("keyFindings")]
    public Dictionary<string, string>? KeyFindings { get; set; }
    
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("nextActions")]
    public List<NextAction>? NextActions { get; set; }
    
    [JsonPropertyName("error")]
    public ErrorInfo? Error { get; set; }
    
    [JsonPropertyName("resourceUri")]
    public string? ResourceUri { get; set; }
    
    [JsonPropertyName("meta")]
    public ToolMetadata? Meta { get; set; }
}

public class CallPath
{
    [JsonPropertyName("startMethod")]
    public required string StartMethod { get; set; }
    
    [JsonPropertyName("endMethod")]
    public string? EndMethod { get; set; }
    
    [JsonPropertyName("pathType")]
    public required string PathType { get; set; }
    
    [JsonPropertyName("steps")]
    public required List<CallStep> Steps { get; set; }
    
    [JsonPropertyName("isComplete")]
    public bool IsComplete { get; set; }
}

public class CallStep
{
    [JsonPropertyName("method")]
    public required string Method { get; set; }
    
    [JsonPropertyName("file")]
    public required string File { get; set; }
    
    [JsonPropertyName("calls")]
    public required List<string> Calls { get; set; }
    
    [JsonPropertyName("conditions")]
    public List<string> Conditions { get; set; } = new();
    
    [JsonPropertyName("insights")]
    public List<string> Insights { get; set; } = new();
}