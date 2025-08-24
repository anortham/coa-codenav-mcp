using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using NextAction = COA.Mcp.Framework.Models.AIAction;
using COA.CodeNav.McpServer.Utilities;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that traces execution paths through code, showing complete call chains
/// </summary>
public class TraceCallStackTool : McpToolBase<TraceCallStackParams, TraceCallStackToolResult>
{
    private readonly ILogger<TraceCallStackTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => ToolNames.TraceCallStack;
    
    public override string Description => @"Trace execution paths through code to understand complete call chains. Shows how methods connect from entry points to implementations for debugging and impact analysis.";

    public TraceCallStackTool(
        IServiceProvider serviceProvider,
        ILogger<TraceCallStackTool> logger,
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

    protected override async Task<TraceCallStackToolResult> ExecuteInternalAsync(
        TraceCallStackParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("TraceCallStack request: FilePath={FilePath}, Line={Line}, Column={Column}, Direction={Direction}", 
            parameters.FilePath, parameters.Line, parameters.Column, parameters.Direction);
            
        var startTime = DateTime.UtcNow;

        _logger.LogInformation("Processing call stack trace for {FilePath} at {Line}:{Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);

        // Get the document
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
            return new TraceCallStackToolResult
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

        // Get the starting symbol
        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
            parameters.Line - 1, 
            parameters.Column - 1));

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            _logger.LogError("Failed to get semantic model for document: {FilePath}", parameters.FilePath);
            return new TraceCallStackToolResult
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

        // Find the method at the position
        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
        var root = await syntaxTree!.GetRootAsync(cancellationToken);
        var token = root.FindToken(position);
        
        // Find the containing method
        var methodSyntax = token.Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodSyntax == null)
        {
            _logger.LogDebug("No method found at position {Line}:{Column}", parameters.Line, parameters.Column);
            return new TraceCallStackToolResult
            {
                Success = false,
                Message = "No method found at the specified position",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.NO_SYMBOL_AT_POSITION,
                    Message = "No method found at the specified position",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Ensure the cursor is inside a method body",
                            "Try positioning at the method name",
                            "Verify the line and column numbers are correct (1-based)"
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

        var methodSymbol = semanticModel.GetDeclaredSymbol(methodSyntax) as IMethodSymbol;
        if (methodSymbol == null)
        {
            return new TraceCallStackToolResult
            {
                Success = false,
                Message = "Could not resolve method symbol",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.NO_SYMBOL_AT_POSITION,
                    Message = "Could not resolve method symbol"
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
            foreach (var caller in callers.Take(parameters.MaxPaths ?? 10)) // Apply limit early
            {
                var path = await TraceBackwardAsync(caller, methodSymbol, document.Project.Solution, parameters.MaxDepth ?? 10, visited, cancellationToken);
                if (path != null)
                    allPaths.Add(path);
            }
        }
        
        // Apply token optimization to prevent context overflow
        var estimatedTokens = _tokenEstimator.EstimateObject(allPaths);
        const int SAFETY_TOKEN_LIMIT = 10000;
        
        var finalPaths = allPaths.ToList();
        var wasTokenOptimized = false;
        
        if (estimatedTokens > SAFETY_TOKEN_LIMIT)
        {
            // Use progressive reduction based on token estimation
            var originalCount = allPaths.Count;
            finalPaths = _tokenEstimator.ApplyProgressiveReduction(
                allPaths,
                path => _tokenEstimator.EstimateObject(path),
                SAFETY_TOKEN_LIMIT,
                new[] { 15, 10, 5 }
            );
            
            wasTokenOptimized = finalPaths.Count < originalCount;
            
            _logger.LogDebug("Applied token optimization: reduced from {Original} to {Reduced} paths (estimated {EstimatedTokens} tokens)",
                originalCount, finalPaths.Count, estimatedTokens);
        }
        
        // Also respect MaxPaths parameter if provided and stricter than token limit
        var maxPaths = parameters.MaxPaths ?? 10;
        if (finalPaths.Count > maxPaths)
        {
            finalPaths = finalPaths.Take(maxPaths).ToList();
        }
        
        // Generate insights
        var insights = GenerateInsights(allPaths, methodSymbol);
        var keyFindings = GenerateKeyFindings(finalPaths, methodSymbol);
        var nextActions = GenerateNextActions(methodSymbol, parameters);

        // Store full result if resource provider is available
        string? resourceUri = null;
        if (_resourceProvider != null && allPaths.Count > finalPaths.Count)
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

        var result = new TraceCallStackToolResult
        {
            Success = true,
            StartMethod = methodSymbol.ToDisplayString(),
            Direction = parameters.Direction,
            Paths = finalPaths,
            KeyFindings = keyFindings,
            Message = finalPaths.Count < allPaths.Count 
                ? $"Traced {allPaths.Count} path(s) from {methodSymbol.Name} (showing {finalPaths.Count}{(wasTokenOptimized ? " due to token optimization" : "")})"
                : $"Traced {allPaths.Count} path(s) from {methodSymbol.Name}",
            Actions = nextActions,
            Insights = insights,
            ResourceUri = resourceUri,
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                TargetSymbol = methodSymbol.ToDisplayString()
            },
            Summary = new SummaryInfo
            {
                TotalFound = allPaths.Count,
                Returned = finalPaths.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            },
            Meta = new ToolExecutionMetadata 
            { 
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
            }
        };

        _logger.LogInformation("Call stack trace completed: Found {PathCount} paths", allPaths.Count);
        return result;
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

            // For now, we can't find callers without more solution context
            // This would need more sophisticated caller analysis
            break;
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
    
    private List<NextAction> GenerateNextActions(IMethodSymbol method, TraceCallStackParams parameters)
    {
        var actions = new List<NextAction>();
        
        // Suggest opposite direction
        actions.Add(NextActionExtensions.CreateNextAction(
            "trace_opposite",
            parameters.Direction == "forward" 
                ? "Trace who calls this method" 
                : "Trace what this method calls",
            ToolNames.TraceCallStack,
            new
            {
                filePath = parameters.FilePath,
                line = parameters.Line,
                column = parameters.Column,
                direction = parameters.Direction == "forward" ? "backward" : "forward",
                maxDepth = parameters.MaxDepth
            },
            "high"
        ));
        
        // Suggest finding references
        actions.Add(NextActionExtensions.CreateNextAction(
            "find_references",
            $"Find all references to '{method.Name}'",
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
}

public class TraceCallStackParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [System.ComponentModel.Description("Path to the source file containing the method to trace")]
    public required string FilePath { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Line must be positive")]
    [JsonPropertyName("line")]
    [System.ComponentModel.Description("Line number (1-based) inside the method to trace from")]
    public int Line { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Column must be positive")]
    [JsonPropertyName("column")]
    [System.ComponentModel.Description("Column number (1-based) inside the method to trace from")]
    public int Column { get; set; }
    
    [JsonPropertyName("direction")]
    [System.ComponentModel.Description("Trace direction: 'forward' (follow calls made by this method) or 'backward' (find callers of this method)")]
    public string Direction { get; set; } = "forward";
    
    [JsonPropertyName("maxDepth")]
    [System.ComponentModel.Description("Maximum depth to trace (default: 10)")]
    public int? MaxDepth { get; set; }
    
    [JsonPropertyName("includeFramework")]
    [System.ComponentModel.Description("Include framework method calls. true = include .NET framework methods, false = user code only (default)")]
    public bool IncludeFramework { get; set; } = false;
    
    [JsonPropertyName("maxPaths")]
    [System.ComponentModel.Description("Maximum number of paths to return (default: 10, max: 50)")]
    public int? MaxPaths { get; set; }
}

public class TraceCallStackToolResult : ToolResultBase
{
    public override string Operation => ToolNames.TraceCallStack;
    
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }
    
    [JsonPropertyName("startMethod")]
    public string? StartMethod { get; set; }
    
    [JsonPropertyName("direction")]
    public string? Direction { get; set; }
    
    [JsonPropertyName("paths")]
    public List<CallPath>? Paths { get; set; }
    
    [JsonPropertyName("keyFindings")]
    public Dictionary<string, string>? KeyFindings { get; set; }
    
    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }
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