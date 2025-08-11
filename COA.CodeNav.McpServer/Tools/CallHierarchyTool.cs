using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.Interfaces;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using DataAnnotations = System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that provides a complete call hierarchy view (incoming and outgoing) at once
/// </summary>
public class CallHierarchyTool : McpToolBase<CallHierarchyParams, CallHierarchyResult>
{
    private readonly ILogger<CallHierarchyTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly CallHierarchyResponseBuilder _responseBuilder;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => "csharp_call_hierarchy";
    public override string Description => @"Get complete call hierarchy (incoming and outgoing calls) for a method at once.
Returns: Bidirectional call tree with incoming (callers) and outgoing (callees) in a single view.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if method cannot be found.
Use cases: Understanding method dependencies, impact analysis, refactoring planning, debugging entry points.
AI benefit: Provides complete context that agents can't easily piece together from separate tools.";

    public CallHierarchyTool(
        ILogger<CallHierarchyTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        CallHierarchyResponseBuilder responseBuilder,
        ITokenEstimator tokenEstimator,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _responseBuilder = responseBuilder;
        _tokenEstimator = tokenEstimator;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<CallHierarchyResult> ExecuteInternalAsync(
        CallHierarchyParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("CallHierarchy request: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);
            
        var startTime = DateTime.UtcNow;

        // Get the document
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
            return new CallHierarchyResult
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
                }
            };
        }

        // Get the method symbol at position
        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
            parameters.Line - 1, 
            parameters.Column - 1));

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return new CallHierarchyResult
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
                }
            };
        }

        // Find the method at position
        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
        var root = await syntaxTree!.GetRootAsync(cancellationToken);
        var token = root.FindToken(position);
        
        var methodSyntax = token.Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodSyntax == null)
        {
            return new CallHierarchyResult
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
                            "Ensure the cursor is inside a method body or on the method name",
                            "Verify the line and column numbers are correct (1-based)",
                            "This tool works only with methods, not properties or fields"
                        }
                    }
                }
            };
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(methodSyntax) as IMethodSymbol;
        if (methodSymbol == null)
        {
            return new CallHierarchyResult
            {
                Success = false,
                Message = "Could not resolve method symbol",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SYMBOL_NOT_FOUND,
                    Message = "Could not resolve method symbol",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Verify the method is properly declared",
                            "Check for compilation errors in the file",
                            "Try using csharp_get_diagnostics to identify issues"
                        }
                    }
                }
            };
        }

        _logger.LogInformation("Building call hierarchy for method '{MethodName}'", methodSymbol.ToDisplayString());

        // Build the hierarchy
        var hierarchy = await BuildCallHierarchyAsync(
            methodSymbol, 
            document.Project.Solution,
            parameters.MaxDepth ?? 3,
            parameters.IncludeOverrides,
            cancellationToken);

        // Apply framework token optimization to hierarchy
        var optimizedHierarchy = hierarchy;
        var tokenLimitApplied = false;
        
        // Use framework's token estimation on the hierarchy
        var estimatedTokens = _tokenEstimator.EstimateObject(hierarchy);
        if (estimatedTokens > 12000) // Call hierarchies can be large
        {
            // For call hierarchies, we need to truncate the depth rather than individual nodes
            // Start by limiting the number of incoming/outgoing calls at each level
            optimizedHierarchy = TruncateHierarchyForTokens(hierarchy, 12000);
            tokenLimitApplied = true;
            
            _logger.LogWarning("Token optimization applied to call hierarchy: estimated {EstimatedTokens} tokens, applied truncation", estimatedTokens);
        }

        // Generate insights
        var insights = GenerateInsights(optimizedHierarchy, methodSymbol);
        var analysis = GenerateAnalysis(optimizedHierarchy, methodSymbol);
        
        // Add insight about token optimization if applied
        if (tokenLimitApplied)
        {
            insights.Insert(0, $"⚠️ Token optimization applied to call hierarchy. Some deep call paths may be truncated.");
        }

        // Generate next actions
        var nextActions = GenerateNextActions(methodSymbol, parameters);

        _logger.LogInformation("Call hierarchy completed for '{Method}'", methodSymbol.ToDisplayString());

        var completeResult = new CallHierarchyResult
        {
            Success = true,
            Message = $"Call hierarchy for '{methodSymbol.Name}'",
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                TargetSymbol = methodSymbol.ToDisplayString()
            },
            Summary = new SummaryInfo
            {
                TotalFound = analysis.TotalNodes,
                Returned = analysis.TotalNodes,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                SymbolInfo = new SymbolSummary
                {
                    Name = methodSymbol.Name,
                    Kind = methodSymbol.Kind.ToString(),
                    ContainingType = methodSymbol.ContainingType?.ToDisplayString(),
                    Namespace = methodSymbol.ContainingNamespace?.ToDisplayString()
                }
            },
            Hierarchy = optimizedHierarchy,
            Analysis = analysis,
            Insights = insights,
            Actions = nextActions,
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };

        // Use ResponseBuilder for token optimization and AI-friendly formatting
        var context = new COA.Mcp.Framework.TokenOptimization.ResponseBuilders.ResponseContext
        {
            ResponseMode = "optimized",
            TokenLimit = 10000,
            ToolName = Name
        };

        return await _responseBuilder.BuildResponseAsync(completeResult, context);
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

        var references = method.DeclaringSyntaxReferences;
        if (!references.Any()) return;

        var syntaxRef = references.First();
        var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken) as MethodDeclarationSyntax;
        if (syntax?.Body == null && syntax?.ExpressionBody == null) return;

        var syntaxTree = syntax.SyntaxTree;
        var document = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.GetSyntaxTreeAsync(cancellationToken).Result == syntaxTree);
            
        if (document == null) return;

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null) return;

        var invocations = syntax.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .ToList();

        foreach (var invocation in invocations)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            if (symbolInfo.Symbol is IMethodSymbol calledMethod)
            {
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

    private List<AIAction> GenerateNextActions(IMethodSymbol method, CallHierarchyParams parameters)
    {
        var actions = new List<AIAction>();

        if (method.IsVirtual || method.IsAbstract)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_find_all_overrides",
                Description = "Find all overrides of this virtual/abstract method",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = parameters.FilePath,
                    ["line"] = parameters.Line,
                    ["column"] = parameters.Column
                },
                Priority = 90,
                Category = "navigation"
            });
        }

        actions.Add(new AIAction
        {
            Action = "csharp_rename_symbol",
            Description = $"Rename '{method.Name}' across the solution",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = parameters.FilePath,
                ["line"] = parameters.Line,
                ["column"] = parameters.Column,
                ["preview"] = true
            },
            Priority = 70,
            Category = "refactoring"
        });

        actions.Add(new AIAction
        {
            Action = "csharp_trace_call_stack",
            Description = "Trace specific execution paths through this method",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = parameters.FilePath,
                ["line"] = parameters.Line,
                ["column"] = parameters.Column,
                ["direction"] = "backward"
            },
            Priority = 60,
            Category = "analysis"
        });

        return actions;
    }
    
    private CallHierarchyNode TruncateHierarchyForTokens(CallHierarchyNode originalHierarchy, int tokenLimit)
    {
        // Create a copy of the root node
        var truncatedHierarchy = new CallHierarchyNode
        {
            Method = originalHierarchy.Method,
            MethodName = originalHierarchy.MethodName,
            Location = originalHierarchy.Location,
            IsVirtual = originalHierarchy.IsVirtual,
            IsAbstract = originalHierarchy.IsAbstract,
            IsOverride = originalHierarchy.IsOverride,
            IsOverrideRelation = originalHierarchy.IsOverrideRelation,
            IsFramework = originalHierarchy.IsFramework,
            IsTruncated = originalHierarchy.IsTruncated,
            Incoming = new List<CallHierarchyNode>(),
            Outgoing = new List<CallHierarchyNode>()
        };
        
        // Limit incoming calls to top 5 most important
        var incomingCount = Math.Min(5, originalHierarchy.Incoming.Count);
        truncatedHierarchy.Incoming.AddRange(originalHierarchy.Incoming.Take(incomingCount));
        
        // Limit outgoing calls to top 8 most important  
        var outgoingCount = Math.Min(8, originalHierarchy.Outgoing.Count);
        truncatedHierarchy.Outgoing.AddRange(originalHierarchy.Outgoing.Take(outgoingCount));
        
        // Mark as truncated if we reduced the hierarchy
        if (incomingCount < originalHierarchy.Incoming.Count || outgoingCount < originalHierarchy.Outgoing.Count)
        {
            truncatedHierarchy.IsTruncated = true;
        }
        
        // For each child, recursively truncate but with more aggressive limits
        foreach (var child in truncatedHierarchy.Incoming)
        {
            if (child.Incoming.Count > 3) 
            {
                child.Incoming = child.Incoming.Take(3).ToList();
                child.IsTruncated = true;
            }
            if (child.Outgoing.Count > 3)
            {
                child.Outgoing = child.Outgoing.Take(3).ToList();
                child.IsTruncated = true;
            }
        }
        
        foreach (var child in truncatedHierarchy.Outgoing)
        {
            if (child.Incoming.Count > 3)
            {
                child.Incoming = child.Incoming.Take(3).ToList();
                child.IsTruncated = true;
            }
            if (child.Outgoing.Count > 3)
            {
                child.Outgoing = child.Outgoing.Take(3).ToList();
                child.IsTruncated = true;
            }
        }
        
        return truncatedHierarchy;
    }

}

/// <summary>
/// Parameters for CallHierarchy tool
/// </summary>
public class CallHierarchyParams
{
    [DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file containing the method")]
    public string FilePath { get; set; } = string.Empty;

    [DataAnnotations.Required]
    [DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Line must be positive")]
    [JsonPropertyName("line")]
    [COA.Mcp.Framework.Attributes.Description("Line number (1-based) where the method is declared")]
    public int Line { get; set; }

    [DataAnnotations.Required]
    [DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Column must be positive")]
    [JsonPropertyName("column")]
    [COA.Mcp.Framework.Attributes.Description("Column number (1-based) where the method is declared")]
    public int Column { get; set; }
    
    [JsonPropertyName("maxDepth")]
    [COA.Mcp.Framework.Attributes.Description("Maximum depth to traverse in each direction (default: 3)")]
    public int? MaxDepth { get; set; }
    
    [JsonPropertyName("includeOverrides")]
    [COA.Mcp.Framework.Attributes.Description("Include method overrides in the hierarchy (default: true)")]
    public bool IncludeOverrides { get; set; } = true;
    
    [JsonPropertyName("maxNodes")]
    [COA.Mcp.Framework.Attributes.Description("Maximum number of nodes to return (default: 100, max: 500)")]
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