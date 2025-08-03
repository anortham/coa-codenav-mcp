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
/// MCP tool that provides a complete call hierarchy view (incoming and outgoing) at once
/// </summary>
[McpServerToolType]
public class CallHierarchyTool : ITool
{
    private readonly ILogger<CallHierarchyTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "csharp_call_hierarchy";
    public string Description => "Get complete call hierarchy (incoming and outgoing calls) for a method";

    public CallHierarchyTool(
        ILogger<CallHierarchyTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "csharp_call_hierarchy")]
    [Description(@"Get complete call hierarchy (incoming and outgoing calls) for a method at once.
Returns: Bidirectional call tree with incoming (callers) and outgoing (callees) in a single view.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if method cannot be found.
Use cases: Understanding method dependencies, impact analysis, refactoring planning, debugging entry points.
AI benefit: Provides complete context that agents can't easily piece together from separate tools.")]
    public async Task<object> ExecuteAsync(CallHierarchyParams parameters, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("CallHierarchy request: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);
            
        try
        {
            // Get the document
            var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
                return CreateErrorResult(
                    ErrorCodes.DOCUMENT_NOT_FOUND,
                    $"Document not found in workspace: {parameters.FilePath}",
                    new List<string>
                    {
                        "Ensure the file path is correct and absolute",
                        "Verify the solution/project containing this file is loaded",
                        "Use csharp_load_solution or csharp_load_project to load the containing project"
                    },
                    parameters,
                    startTime);
            }

            // Get the method symbol at position
            var sourceText = await document.GetTextAsync(cancellationToken);
            var position = sourceText.Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
                parameters.Line - 1, 
                parameters.Column - 1));

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                return CreateErrorResult(
                    ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE,
                    "Could not get semantic model for document",
                    new List<string>
                    {
                        "Ensure the project is fully loaded and compiled",
                        "Check for compilation errors in the project",
                        "Try reloading the solution"
                    },
                    parameters,
                    startTime);
            }

            // Find the method at position
            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
            var root = await syntaxTree!.GetRootAsync(cancellationToken);
            var token = root.FindToken(position);
            
            var methodSyntax = token.Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (methodSyntax == null)
            {
                return CreateErrorResult(
                    ErrorCodes.NO_SYMBOL_AT_POSITION,
                    "No method found at the specified position",
                    new List<string>
                    {
                        "Ensure the cursor is inside a method body or on the method name",
                        "Verify the line and column numbers are correct (1-based)",
                        "This tool works only with methods, not properties or fields"
                    },
                    parameters,
                    startTime);
            }

            var methodSymbol = semanticModel.GetDeclaredSymbol(methodSyntax) as IMethodSymbol;
            if (methodSymbol == null)
            {
                return CreateErrorResult(
                    ErrorCodes.SYMBOL_NOT_FOUND,
                    "Could not resolve method symbol",
                    new List<string>
                    {
                        "Verify the method is properly declared",
                        "Check for compilation errors in the file",
                        "Try using csharp_get_diagnostics to identify issues"
                    },
                    parameters,
                    startTime);
            }

            _logger.LogInformation("Building call hierarchy for method '{MethodName}'", methodSymbol.ToDisplayString());

            // Build the hierarchy
            var hierarchy = await BuildCallHierarchyAsync(
                methodSymbol, 
                document.Project.Solution,
                parameters.MaxDepth ?? 3,
                parameters.IncludeOverrides,
                cancellationToken);

            // Pre-estimate tokens
            var allNodes = GetAllNodes(hierarchy);
            var response = TokenEstimator.CreateTokenAwareResponse(
                allNodes,
                nodes => EstimateHierarchyTokens(nodes),
                requestedMax: parameters.MaxNodes ?? 100,
                safetyLimit: TokenEstimator.DEFAULT_SAFETY_LIMIT,
                toolName: "csharp_call_hierarchy"
            );

            // Create limited hierarchy with only included nodes
            var limitedHierarchy = CreateLimitedHierarchy(hierarchy, response.Items);

            // Generate insights
            var insights = GenerateInsights(hierarchy, methodSymbol);
            if (response.WasTruncated)
            {
                insights.Insert(0, response.GetTruncationMessage());
            }

            // Generate next actions
            var nextActions = GenerateNextActions(methodSymbol, parameters);
            if (response.WasTruncated)
            {
                nextActions.Insert(0, new NextAction
                {
                    Id = "get_full_hierarchy",
                    Description = "Get complete hierarchy without truncation",
                    ToolName = "csharp_call_hierarchy",
                    Parameters = new
                    {
                        filePath = parameters.FilePath,
                        line = parameters.Line,
                        column = parameters.Column,
                        maxNodes = allNodes.Count,
                        maxDepth = parameters.MaxDepth
                    },
                    Priority = "high"
                });
            }

            // Store full result if truncated
            string? resourceUri = null;
            if (response.WasTruncated && _resourceProvider != null)
            {
                resourceUri = _resourceProvider.StoreAnalysisResult(
                    "call-hierarchy",
                    new { 
                        method = methodSymbol.ToDisplayString(),
                        hierarchy = hierarchy,
                        totalNodes = allNodes.Count
                    },
                    $"Complete call hierarchy for {methodSymbol.Name}");
            }

            var result = new CallHierarchyResult
            {
                Success = true,
                Message = response.WasTruncated 
                    ? $"Call hierarchy for '{methodSymbol.Name}' (showing {response.ReturnedCount} of {allNodes.Count} nodes)"
                    : $"Call hierarchy for '{methodSymbol.Name}'",
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    TargetSymbol = methodSymbol.ToDisplayString()
                },
                Summary = new SummaryInfo
                {
                    TotalFound = allNodes.Count,
                    Returned = response.ReturnedCount,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    SymbolInfo = new SymbolSummary
                    {
                        Name = methodSymbol.Name,
                        Kind = methodSymbol.Kind.ToString(),
                        ContainingType = methodSymbol.ContainingType?.ToDisplayString(),
                        Namespace = methodSymbol.ContainingNamespace?.ToDisplayString()
                    }
                },
                Hierarchy = limitedHierarchy,
                Analysis = GenerateAnalysis(hierarchy, methodSymbol),
                Insights = insights,
                Actions = nextActions,
                ResourceUri = resourceUri,
                Meta = new ToolMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    Truncated = response.WasTruncated,
                    Tokens = response.EstimatedTokens
                }
            };

            _logger.LogInformation("Call hierarchy completed: {TotalNodes} nodes found", allNodes.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in CallHierarchy");
            return CreateErrorResult(
                ErrorCodes.INTERNAL_ERROR,
                $"Error: {ex.Message}",
                new List<string>
                {
                    "Check the server logs for detailed error information",
                    "Verify the solution/project is loaded correctly",
                    "Try the operation again"
                },
                parameters,
                startTime);
        }
    }

    private async Task<CallHierarchyNode> BuildCallHierarchyAsync(
        IMethodSymbol method,
        Solution solution,
        int maxDepth,
        bool includeOverrides,
        CancellationToken cancellationToken)
    {
        var node = new CallHierarchyNode
        {
            Method = method.ToDisplayString(),
            MethodName = method.Name,
            Location = GetMethodLocation(method),
            IsVirtual = method.IsVirtual,
            IsAbstract = method.IsAbstract,
            IsOverride = method.IsOverride,
            Incoming = new List<CallHierarchyNode>(),
            Outgoing = new List<CallHierarchyNode>()
        };

        if (maxDepth <= 0)
        {
            node.IsTruncated = true;
            return node;
        }

        var visitedIncoming = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var visitedOutgoing = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        // Build incoming calls (callers)
        await BuildIncomingCallsAsync(method, solution, node, maxDepth, visitedIncoming, includeOverrides, cancellationToken);

        // Build outgoing calls (callees)
        await BuildOutgoingCallsAsync(method, solution, node, maxDepth, visitedOutgoing, cancellationToken);

        return node;
    }

    private async Task BuildIncomingCallsAsync(
        IMethodSymbol method,
        Solution solution,
        CallHierarchyNode parentNode,
        int remainingDepth,
        HashSet<IMethodSymbol> visited,
        bool includeOverrides,
        CancellationToken cancellationToken)
    {
        if (remainingDepth <= 0 || visited.Contains(method))
            return;

        visited.Add(method);

        // Find all references to this method
        var references = await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken);
        
        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
            {
                var document = solution.GetDocument(location.Document.Id);
                if (document == null) continue;

                var root = await location.Document.GetSyntaxRootAsync(cancellationToken);
                var node = root?.FindNode(location.Location.SourceSpan);
                
                // Find containing method
                var containingMethod = node?.AncestorsAndSelf()
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault();
                    
                if (containingMethod != null)
                {
                    var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                    var callerSymbol = semanticModel?.GetDeclaredSymbol(containingMethod) as IMethodSymbol;
                    
                    if (callerSymbol != null && !parentNode.Incoming.Any(n => n.Method == callerSymbol.ToDisplayString()))
                    {
                        var callerNode = new CallHierarchyNode
                        {
                            Method = callerSymbol.ToDisplayString(),
                            MethodName = callerSymbol.Name,
                            Location = GetMethodLocation(callerSymbol),
                            IsVirtual = callerSymbol.IsVirtual,
                            IsAbstract = callerSymbol.IsAbstract,
                            IsOverride = callerSymbol.IsOverride,
                            Incoming = new List<CallHierarchyNode>(),
                            Outgoing = new List<CallHierarchyNode>()
                        };

                        parentNode.Incoming.Add(callerNode);

                        // Recursively build callers of this caller
                        await BuildIncomingCallsAsync(
                            callerSymbol, solution, callerNode, 
                            remainingDepth - 1, visited, includeOverrides, cancellationToken);
                    }
                }
            }
        }

        // Include overrides if requested
        if (includeOverrides && (method.IsVirtual || method.IsAbstract))
        {
            // For virtual/abstract methods, find implementations
            var implementations = await SymbolFinder.FindImplementationsAsync(
                method, 
                solution, 
                cancellationToken: cancellationToken);
                
            foreach (var implementation in implementations.OfType<IMethodSymbol>())
            {
                if (!parentNode.Incoming.Any(n => n.Method == implementation.ToDisplayString()))
                {
                    var overrideNode = new CallHierarchyNode
                    {
                        Method = implementation.ToDisplayString(),
                        MethodName = implementation.Name,
                        Location = GetMethodLocation(implementation),
                        IsVirtual = implementation.IsVirtual,
                        IsAbstract = implementation.IsAbstract,
                        IsOverride = implementation.IsOverride,
                        IsOverrideRelation = true,
                        Incoming = new List<CallHierarchyNode>(),
                        Outgoing = new List<CallHierarchyNode>()
                    };

                    parentNode.Incoming.Add(overrideNode);
                }
            }
        }
        else if (includeOverrides && method.IsOverride)
        {
            // For override methods, find the base method
            var baseMethod = method.OverriddenMethod;
            if (baseMethod != null && !parentNode.Incoming.Any(n => n.Method == baseMethod.ToDisplayString()))
            {
                var baseNode = new CallHierarchyNode
                {
                    Method = baseMethod.ToDisplayString(),
                    MethodName = baseMethod.Name,
                    Location = GetMethodLocation(baseMethod),
                    IsVirtual = baseMethod.IsVirtual,
                    IsAbstract = baseMethod.IsAbstract,
                    IsOverride = baseMethod.IsOverride,
                    IsOverrideRelation = true,
                    Incoming = new List<CallHierarchyNode>(),
                    Outgoing = new List<CallHierarchyNode>()
                };

                parentNode.Incoming.Add(baseNode);
            }
        }
    }

    private async Task BuildOutgoingCallsAsync(
        IMethodSymbol method,
        Solution solution,
        CallHierarchyNode parentNode,
        int remainingDepth,
        HashSet<IMethodSymbol> visited,
        CancellationToken cancellationToken)
    {
        if (remainingDepth <= 0 || visited.Contains(method))
            return;

        visited.Add(method);

        // Get method body
        var references = method.DeclaringSyntaxReferences;
        if (!references.Any()) return;

        var syntaxRef = references.First();
        var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken) as MethodDeclarationSyntax;
        if (syntax?.Body == null && syntax?.ExpressionBody == null) return;

        // Get the document for this syntax
        var syntaxTree = syntax.SyntaxTree;
        var document = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.GetSyntaxTreeAsync(cancellationToken).Result == syntaxTree);
            
        if (document == null) return;

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null) return;

        // Find all method invocations
        var invocations = syntax.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .ToList();

        foreach (var invocation in invocations)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            if (symbolInfo.Symbol is IMethodSymbol calledMethod)
            {
                // Skip framework methods unless they're important
                if (IsFrameworkMethod(calledMethod) && !IsImportantFrameworkMethod(calledMethod))
                    continue;

                if (!parentNode.Outgoing.Any(n => n.Method == calledMethod.ToDisplayString()))
                {
                    var calleeNode = new CallHierarchyNode
                    {
                        Method = calledMethod.ToDisplayString(),
                        MethodName = calledMethod.Name,
                        Location = GetMethodLocation(calledMethod),
                        IsVirtual = calledMethod.IsVirtual,
                        IsAbstract = calledMethod.IsAbstract,
                        IsOverride = calledMethod.IsOverride,
                        IsFramework = IsFrameworkMethod(calledMethod),
                        Incoming = new List<CallHierarchyNode>(),
                        Outgoing = new List<CallHierarchyNode>()
                    };

                    parentNode.Outgoing.Add(calleeNode);

                    // Recursively build callees (only for non-framework methods)
                    if (!calleeNode.IsFramework)
                    {
                        await BuildOutgoingCallsAsync(
                            calledMethod, solution, calleeNode,
                            remainingDepth - 1, visited, cancellationToken);
                    }
                }
            }
        }
    }

    private bool IsFrameworkMethod(IMethodSymbol method)
    {
        var ns = method.ContainingNamespace?.ToDisplayString() ?? "";
        return ns.StartsWith("System.") || 
               ns.StartsWith("Microsoft.") ||
               ns.StartsWith("Newtonsoft.") ||
               ns.StartsWith("EntityFramework");
    }

    private bool IsImportantFrameworkMethod(IMethodSymbol method)
    {
        var name = method.Name;
        // Include important framework methods like database operations, HTTP calls, etc.
        return name.Contains("ExecuteAsync") ||
               name.Contains("SaveChanges") ||
               name.Contains("HttpClient") ||
               name.Contains("SendAsync");
    }

    private string GetMethodLocation(IMethodSymbol method)
    {
        var location = method.Locations.FirstOrDefault();
        if (location != null && location.IsInSource)
        {
            var lineSpan = location.GetLineSpan();
            return $"{lineSpan.Path}:{lineSpan.StartLinePosition.Line + 1}";
        }
        return "<external>";
    }

    private List<CallHierarchyNode> GetAllNodes(CallHierarchyNode root)
    {
        var nodes = new List<CallHierarchyNode> { root };
        var queue = new Queue<CallHierarchyNode>();
        queue.Enqueue(root);

        var visited = new HashSet<string>();
        visited.Add(root.Method);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            
            foreach (var child in current.Incoming.Concat(current.Outgoing))
            {
                if (!visited.Contains(child.Method))
                {
                    visited.Add(child.Method);
                    nodes.Add(child);
                    queue.Enqueue(child);
                }
            }
        }

        return nodes;
    }

    private CallHierarchyNode CreateLimitedHierarchy(CallHierarchyNode original, List<CallHierarchyNode> includedNodes)
    {
        var includedMethods = new HashSet<string>(includedNodes.Select(n => n.Method));
        return CreateLimitedNode(original, includedMethods, new HashSet<string>());
    }

    private CallHierarchyNode CreateLimitedNode(CallHierarchyNode node, HashSet<string> includedMethods, HashSet<string> processed)
    {
        if (!includedMethods.Contains(node.Method) || processed.Contains(node.Method))
            return null!;

        processed.Add(node.Method);

        var limitedNode = new CallHierarchyNode
        {
            Method = node.Method,
            MethodName = node.MethodName,
            Location = node.Location,
            IsVirtual = node.IsVirtual,
            IsAbstract = node.IsAbstract,
            IsOverride = node.IsOverride,
            IsFramework = node.IsFramework,
            IsOverrideRelation = node.IsOverrideRelation,
            IsTruncated = node.IsTruncated,
            Incoming = new List<CallHierarchyNode>(),
            Outgoing = new List<CallHierarchyNode>()
        };

        foreach (var child in node.Incoming)
        {
            var limitedChild = CreateLimitedNode(child, includedMethods, processed);
            if (limitedChild != null)
                limitedNode.Incoming.Add(limitedChild);
        }

        foreach (var child in node.Outgoing)
        {
            var limitedChild = CreateLimitedNode(child, includedMethods, processed);
            if (limitedChild != null)
                limitedNode.Outgoing.Add(limitedChild);
        }

        return limitedNode;
    }

    private int EstimateHierarchyTokens(List<CallHierarchyNode> nodes)
    {
        return TokenEstimator.EstimateCollection(
            nodes,
            node => {
                var tokens = 100; // Base for node structure
                tokens += TokenEstimator.EstimateString(node.Method);
                tokens += TokenEstimator.EstimateString(node.Location);
                tokens += 50; // Boolean flags and metadata
                return tokens;
            },
            baseTokens: TokenEstimator.BASE_RESPONSE_TOKENS
        );
    }

    private CallHierarchyAnalysis GenerateAnalysis(CallHierarchyNode hierarchy, IMethodSymbol method)
    {
        var allNodes = GetAllNodes(hierarchy);
        var incomingCount = CountNodes(hierarchy, true);
        var outgoingCount = CountNodes(hierarchy, false);

        return new CallHierarchyAnalysis
        {
            TotalNodes = allNodes.Count,
            IncomingCallsCount = incomingCount,
            OutgoingCallsCount = outgoingCount,
            MaxIncomingDepth = GetMaxDepth(hierarchy, true),
            MaxOutgoingDepth = GetMaxDepth(hierarchy, false),
            IsEntryPoint = incomingCount == 0,
            IsLeafMethod = outgoingCount == 0,
            HasOverrides = allNodes.Any(n => n.IsOverrideRelation),
            CallsFramework = allNodes.Any(n => n.IsFramework)
        };
    }

    private int CountNodes(CallHierarchyNode node, bool incoming, HashSet<string>? visited = null)
    {
        visited = visited ?? new HashSet<string>();
        if (visited.Contains(node.Method))
            return 0;
        
        visited.Add(node.Method);
        
        var count = 0;
        var children = incoming ? node.Incoming : node.Outgoing;
        
        foreach (var child in children)
        {
            count++;
            count += CountNodes(child, incoming, visited);
        }
        
        return count;
    }

    private int GetMaxDepth(CallHierarchyNode node, bool incoming, int currentDepth = 0, HashSet<string>? visited = null)
    {
        visited = visited ?? new HashSet<string>();
        if (visited.Contains(node.Method))
            return currentDepth;
        
        visited.Add(node.Method);
        
        var children = incoming ? node.Incoming : node.Outgoing;
        if (!children.Any())
            return currentDepth;
        
        var maxChildDepth = 0;
        foreach (var child in children)
        {
            var childDepth = GetMaxDepth(child, incoming, currentDepth + 1, visited);
            maxChildDepth = Math.Max(maxChildDepth, childDepth);
        }
        
        return maxChildDepth;
    }

    private List<string> GenerateInsights(CallHierarchyNode hierarchy, IMethodSymbol method)
    {
        var insights = new List<string>();
        var analysis = GenerateAnalysis(hierarchy, method);

        if (analysis.IsEntryPoint)
            insights.Add("This appears to be an entry point with no incoming calls");
        
        if (analysis.IsLeafMethod)
            insights.Add("This is a leaf method that doesn't call other methods");
        
        if (analysis.HasOverrides)
            insights.Add("This method participates in inheritance/override relationships");
        
        if (analysis.IncomingCallsCount > 10)
            insights.Add($"Highly referenced method with {analysis.IncomingCallsCount} incoming calls");
        
        if (analysis.OutgoingCallsCount > 15)
            insights.Add($"Complex method with {analysis.OutgoingCallsCount} outgoing calls - consider refactoring");
        
        if (method.IsAsync)
            insights.Add("Async method - check for proper async/await usage in call chain");

        return insights;
    }

    private List<NextAction> GenerateNextActions(IMethodSymbol method, CallHierarchyParams parameters)
    {
        var actions = new List<NextAction>();

        // Suggest finding all overrides if virtual/abstract
        if (method.IsVirtual || method.IsAbstract)
        {
            actions.Add(new NextAction
            {
                Id = "find_overrides",
                Description = "Find all overrides of this virtual/abstract method",
                ToolName = "csharp_find_all_overrides",
                Parameters = new
                {
                    filePath = parameters.FilePath,
                    line = parameters.Line,
                    column = parameters.Column
                },
                Priority = "high"
            });
        }

        // Suggest rename if many references
        actions.Add(new NextAction
        {
            Id = "rename_method",
            Description = $"Rename '{method.Name}' across the solution",
            ToolName = "csharp_rename_symbol",
            Parameters = new
            {
                filePath = parameters.FilePath,
                line = parameters.Line,
                column = parameters.Column,
                preview = true
            },
            Priority = "medium"
        });

        // Suggest trace call stack for detailed path analysis
        actions.Add(new NextAction
        {
            Id = "trace_paths",
            Description = "Trace specific execution paths through this method",
            ToolName = "csharp_trace_call_stack",
            Parameters = new
            {
                filePath = parameters.FilePath,
                line = parameters.Line,
                column = parameters.Column,
                direction = "backward"
            },
            Priority = "medium"
        });

        return actions;
    }

    private CallHierarchyResult CreateErrorResult(
        string errorCode,
        string message,
        List<string> recoverySteps,
        CallHierarchyParams parameters,
        DateTime startTime)
    {
        return new CallHierarchyResult
        {
            Success = false,
            Message = message,
            Error = new ErrorInfo
            {
                Code = errorCode,
                Recovery = new RecoveryInfo
                {
                    Steps = recoverySteps
                }
            },
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
            },
            Meta = new ToolMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
    }
}

public class CallHierarchyParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file containing the method")]
    public required string FilePath { get; set; }

    [JsonPropertyName("line")]
    [Description("Line number (1-based) where the method is declared")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    [Description("Column number (1-based) where the method is declared")]
    public int Column { get; set; }
    
    [JsonPropertyName("maxDepth")]
    [Description("Maximum depth to traverse in each direction (default: 3)")]
    public int? MaxDepth { get; set; }
    
    [JsonPropertyName("includeOverrides")]
    [Description("Include method overrides in the hierarchy (default: true)")]
    public bool IncludeOverrides { get; set; } = true;
    
    [JsonPropertyName("maxNodes")]
    [Description("Maximum number of nodes to return (default: 100, max: 500)")]
    public int? MaxNodes { get; set; }
}

public class CallHierarchyResult : ToolResultBase
{
    public override string Operation => "csharp_call_hierarchy";
    
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }
    
    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }
    
    [JsonPropertyName("hierarchy")]
    public CallHierarchyNode? Hierarchy { get; set; }
    
    [JsonPropertyName("analysis")]
    public CallHierarchyAnalysis? Analysis { get; set; }
}

public class CallHierarchyNode
{
    [JsonPropertyName("method")]
    public required string Method { get; set; }
    
    [JsonPropertyName("methodName")]
    public required string MethodName { get; set; }
    
    [JsonPropertyName("location")]
    public required string Location { get; set; }
    
    [JsonPropertyName("isVirtual")]
    public bool IsVirtual { get; set; }
    
    [JsonPropertyName("isAbstract")]
    public bool IsAbstract { get; set; }
    
    [JsonPropertyName("isOverride")]
    public bool IsOverride { get; set; }
    
    [JsonPropertyName("isFramework")]
    public bool IsFramework { get; set; }
    
    [JsonPropertyName("isOverrideRelation")]
    public bool IsOverrideRelation { get; set; }
    
    [JsonPropertyName("isTruncated")]
    public bool IsTruncated { get; set; }
    
    [JsonPropertyName("incoming")]
    public List<CallHierarchyNode> Incoming { get; set; } = new();
    
    [JsonPropertyName("outgoing")]
    public List<CallHierarchyNode> Outgoing { get; set; } = new();
}

public class CallHierarchyAnalysis
{
    [JsonPropertyName("totalNodes")]
    public int TotalNodes { get; set; }
    
    [JsonPropertyName("incomingCallsCount")]
    public int IncomingCallsCount { get; set; }
    
    [JsonPropertyName("outgoingCallsCount")]
    public int OutgoingCallsCount { get; set; }
    
    [JsonPropertyName("maxIncomingDepth")]
    public int MaxIncomingDepth { get; set; }
    
    [JsonPropertyName("maxOutgoingDepth")]
    public int MaxOutgoingDepth { get; set; }
    
    [JsonPropertyName("isEntryPoint")]
    public bool IsEntryPoint { get; set; }
    
    [JsonPropertyName("isLeafMethod")]
    public bool IsLeafMethod { get; set; }
    
    [JsonPropertyName("hasOverrides")]
    public bool HasOverrides { get; set; }
    
    [JsonPropertyName("callsFramework")]
    public bool CallsFramework { get; set; }
}