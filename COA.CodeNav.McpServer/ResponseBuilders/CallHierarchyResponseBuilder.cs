using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Tools;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Response builder for CallHierarchyTool that implements token-aware response building with strong typing
/// </summary>
public class CallHierarchyResponseBuilder : BaseResponseBuilder<CallHierarchyResult, CallHierarchyResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public CallHierarchyResponseBuilder(
        ILogger<CallHierarchyResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }
    
    public override Task<CallHierarchyResult> BuildResponseAsync(
        CallHierarchyResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Track original sizes for reporting
        var originalIncomingCount = data.Hierarchy != null ? CountNodes(data.Hierarchy.Incoming?.FirstOrDefault()) : 0;
        var originalOutgoingCount = data.Hierarchy != null ? CountNodes(data.Hierarchy.Outgoing?.FirstOrDefault()) : 0;
        var wasReduced = false;
        
        // Apply progressive reduction to hierarchy
        if (data.Hierarchy != null)
        {
            var originalTokens = _tokenEstimator.EstimateObject(data.Hierarchy);
            
            if (originalTokens > tokenBudget * 0.7) // Reserve 30% for metadata
            {
                var hierarchyBudget = (int)(tokenBudget * 0.7);
                data.Hierarchy = ReduceHierarchy(data.Hierarchy, hierarchyBudget);
                wasReduced = true;
            }
        }
        
        // Generate insights based on call hierarchy
        var insights = GenerateInsights(data, context.ResponseMode);
        
        // Generate actions for next steps
        var actions = GenerateActions(data, (int)(tokenBudget * 0.15));
        
        // Update insights and actions with token-aware reductions
        data.Insights = ReduceInsights(insights, (int)(tokenBudget * 0.1));
        data.Actions = ReduceActions(actions, (int)(tokenBudget * 0.15));
        
        // Update metadata to reflect the optimization
        if (data.Analysis != null && data.Hierarchy != null)
        {
            data.Analysis.IncomingCallsCount = CountNodes(data.Hierarchy.Incoming?.FirstOrDefault());
            data.Analysis.OutgoingCallsCount = CountNodes(data.Hierarchy.Outgoing?.FirstOrDefault());
        }
        
        // Add truncation message if needed
        if (wasReduced && data.Insights != null && data.Hierarchy != null)
        {
            var currentIncoming = CountNodes(data.Hierarchy.Incoming?.FirstOrDefault());
            var currentOutgoing = CountNodes(data.Hierarchy.Outgoing?.FirstOrDefault());
            data.Insights.Insert(0, $"⚠️ Token optimization applied. Showing {currentIncoming}/{originalIncomingCount} incoming and {currentOutgoing}/{originalOutgoingCount} outgoing calls.");
        }
        
        // Update execution metadata
        data.Meta = new ToolExecutionMetadata
        {
            Mode = context.ResponseMode ?? "optimized",
            Truncated = wasReduced,
            Tokens = _tokenEstimator.EstimateObject(data),
            ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
        };
        
        data.Success = true;
        data.Message = BuildSummary(data, wasReduced);
        
        return Task.FromResult(data);
    }
    
    protected override List<string> GenerateInsights(
        CallHierarchyResult data,
        string responseMode)
    {
        var insights = new List<string>();
        
        var incomingCount = data.Hierarchy != null ? CountNodes(data.Hierarchy.Incoming?.FirstOrDefault()) : 0;
        var outgoingCount = data.Hierarchy != null ? CountNodes(data.Hierarchy.Outgoing?.FirstOrDefault()) : 0;
        
        // Analyze incoming calls (callers)
        if (incomingCount == 0)
        {
            insights.Add("No incoming calls found - this method appears to be unused or is an entry point");
        }
        else if (incomingCount > 20)
        {
            insights.Add($"High fan-in detected ({incomingCount} callers) - this is a critical method with many dependencies");
        }
        else if (incomingCount > 10)
        {
            insights.Add($"Moderate fan-in ({incomingCount} callers) - consider if this method has too many responsibilities");
        }
        
        // Analyze outgoing calls (callees)
        if (outgoingCount == 0)
        {
            insights.Add("No outgoing calls - this is a leaf method with no dependencies");
        }
        else if (outgoingCount > 30)
        {
            insights.Add($"High fan-out detected ({outgoingCount} dependencies) - this method may be doing too much");
        }
        else if (outgoingCount > 15)
        {
            insights.Add($"Moderate fan-out ({outgoingCount} dependencies) - consider breaking down this method");
        }
        
        // Analyze call depth
        var maxIncomingDepth = data.Analysis?.MaxIncomingDepth ?? 0;
        var maxOutgoingDepth = data.Analysis?.MaxOutgoingDepth ?? 0;
        
        if (maxIncomingDepth > 5)
        {
            insights.Add($"Deep incoming call chain (depth: {maxIncomingDepth}) - complex dependency chain detected");
        }
        
        if (maxOutgoingDepth > 5)
        {
            insights.Add($"Deep outgoing call chain (depth: {maxOutgoingDepth}) - consider flattening the call hierarchy");
        }
        
        // Check for circular dependencies (heuristic based on override patterns)
        if (data.Hierarchy?.IsOverride == true && data.Analysis?.HasOverrides == true)
        {
            insights.Add("⚠️ Circular dependency detected - refactoring strongly recommended");
        }
        
        // Check for test coverage
        if (data.Hierarchy?.Incoming != null)
        {
            var testCallers = data.Hierarchy.Incoming.Count(n => CountTestMethods(n) > 0);
            if (testCallers == 0 && incomingCount > 0)
            {
                insights.Add("No test methods found in callers - consider adding test coverage");
            }
            else if (testCallers > 0)
            {
                insights.Add($"Found {testCallers} test method(s) calling this code");
            }
        }
        
        if (responseMode == "summary")
        {
            insights.Add("Showing summary view - use 'detailed' mode for complete call paths");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        CallHierarchyResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        // Navigation actions
        if (data.Hierarchy != null)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_goto_definition",
                Description = "Navigate to specific caller or callee",
                Category = "navigate",
                Priority = 10
            });
            
            actions.Add(new AIAction
            {
                Action = "csharp_find_all_references",
                Description = "Find all references to methods in the hierarchy",
                Category = "analyze",
                Priority = 9
            });
        }
        
        // Analysis actions
        var incomingCount = data.Hierarchy != null ? CountNodes(data.Hierarchy.Incoming?.FirstOrDefault()) : 0;
        var outgoingCount = data.Hierarchy != null ? CountNodes(data.Hierarchy.Outgoing?.FirstOrDefault()) : 0;
        
        if (incomingCount > 10 || outgoingCount > 15)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_code_metrics",
                Description = "Analyze complexity metrics for this method",
                Category = "analyze",
                Priority = 8
            });
            
            actions.Add(new AIAction
            {
                Action = "csharp_extract_method",
                Description = "Refactor to reduce method complexity",
                Category = "refactor",
                Priority = 8
            });
        }
        
        // Dependency analysis
        if (data.Analysis?.HasOverrides == true)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_dependency_analysis",
                Description = "Analyze circular dependencies in detail",
                Category = "analyze",
                Priority = 9
            });
        }
        
        // Test coverage actions
        if (data.Hierarchy?.Incoming != null && data.Hierarchy.Incoming.All(n => CountTestMethods(n) == 0) && incomingCount > 0)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_generate_code",
                Description = "Generate unit tests for this method",
                Category = "generate",
                Priority = 7,
                Parameters = new Dictionary<string, object>
                {
                    ["generationType"] = "UnitTest"
                }
            });
        }
        
        // Tracing actions
        if (data.Analysis != null && (data.Analysis.MaxIncomingDepth > 3 || data.Analysis.MaxOutgoingDepth > 3))
        {
            actions.Add(new AIAction
            {
                Action = "csharp_trace_call_stack",
                Description = "Trace specific execution paths",
                Category = "analyze",
                Priority = 7
            });
        }
        
        return actions;
    }
    
    private CallHierarchyNode? ReduceHierarchy(CallHierarchyNode node, int tokenBudget)
    {
        var nodeTokens = _tokenEstimator.EstimateObject(node);
        
        if (nodeTokens <= tokenBudget)
        {
            return node;
        }
        
        // Create a reduced version
        var reduced = new CallHierarchyNode
        {
            Method = node.Method,
            MethodName = node.MethodName,
            Location = node.Location,
            IsVirtual = node.IsVirtual,
            IsAbstract = node.IsAbstract,
            IsOverride = node.IsOverride,
            Incoming = null,
            Outgoing = null // Initially no children
        };
        
        // Handle both incoming and outgoing calls
        if ((node.Incoming != null && node.Incoming.Count > 0) || (node.Outgoing != null && node.Outgoing.Count > 0))
        {
            var remainingBudget = tokenBudget - _tokenEstimator.EstimateObject(reduced);
            var halfBudget = remainingBudget / 2;
            
            if (node.Incoming != null && node.Incoming.Count > 0)
            {
                var childBudget = halfBudget / Math.Max(1, node.Incoming.Count);
                var reducedIncoming = new List<CallHierarchyNode>();
                
                var prioritizedIncoming = node.Incoming
                    .OrderByDescending(c => GetCallPriority(c))
                    .Take(3); // Limit to top 3 incoming
                
                foreach (var call in prioritizedIncoming)
                {
                    var reducedCall = ReduceHierarchy(call, childBudget);
                    if (reducedCall != null)
                    {
                        reducedIncoming.Add(reducedCall);
                    }
                }
                
                reduced.Incoming = reducedIncoming;
            }
            
            if (node.Outgoing != null && node.Outgoing.Count > 0)
            {
                var childBudget = halfBudget / Math.Max(1, node.Outgoing.Count);
                var reducedOutgoing = new List<CallHierarchyNode>();
                
                var prioritizedOutgoing = node.Outgoing
                    .OrderByDescending(c => GetCallPriority(c))
                    .Take(3); // Limit to top 3 outgoing
                
                foreach (var call in prioritizedOutgoing)
                {
                    var reducedCall = ReduceHierarchy(call, childBudget);
                    if (reducedCall != null)
                    {
                        reducedOutgoing.Add(reducedCall);
                    }
                }
                
                reduced.Outgoing = reducedOutgoing;
            }
        }
        
        return reduced;
    }
    
    private int GetCallPriority(CallHierarchyNode node)
    {
        var priority = 0;
        
        // Higher priority for nodes with more children
        priority += ((node.Incoming?.Count ?? 0) + (node.Outgoing?.Count ?? 0)) * 10;
        
        // Higher priority for virtual/abstract methods
        if (node.IsVirtual || node.IsAbstract)
        {
            priority += 20;
        }
        
        // Lower priority for test methods
        if (node.MethodName?.Contains("Test") == true || node.Location?.Contains("Test") == true)
        {
            priority -= 10;
        }
        
        return priority;
    }
    
    private int CountNodes(CallHierarchyNode? node)
    {
        if (node == null) return 0;
        
        var count = 1;
        if (node.Incoming != null)
        {
            foreach (var child in node.Incoming)
            {
                count += CountNodes(child);
            }
        }
        
        if (node.Outgoing != null)
        {
            foreach (var child in node.Outgoing)
            {
                count += CountNodes(child);
            }
        }
        
        return count;
    }
    
    private int GetMaxDepth(CallHierarchyNode? node, int currentDepth = 0)
    {
        if (node == null) return currentDepth;
        
        var maxDepth = currentDepth + 1;
        
        if (node.Incoming != null)
        {
            foreach (var child in node.Incoming)
            {
                var childDepth = GetMaxDepth(child, currentDepth + 1);
                maxDepth = Math.Max(maxDepth, childDepth);
            }
        }
        
        if (node.Outgoing != null)
        {
            foreach (var child in node.Outgoing)
            {
                var childDepth = GetMaxDepth(child, currentDepth + 1);
                maxDepth = Math.Max(maxDepth, childDepth);
            }
        }
        
        return maxDepth;
    }
    
    private int CountTestMethods(CallHierarchyNode? node)
    {
        if (node == null) return 0;
        
        var count = 0;
        
        // Check if this is a test method
        if (node.MethodName?.Contains("Test") == true || 
            node.Location?.Contains("Test") == true ||
            node.Location?.Contains(".Tests") == true)
        {
            count = 1;
        }
        
        // Count test methods in children
        if (node.Incoming != null)
        {
            foreach (var child in node.Incoming)
            {
                count += CountTestMethods(child);
            }
        }
        
        if (node.Outgoing != null)
        {
            foreach (var child in node.Outgoing)
            {
                count += CountTestMethods(child);
            }
        }
        
        return count;
    }
    
    private string BuildSummary(CallHierarchyResult data, bool wasReduced)
    {
        var incomingCount = data.Hierarchy != null ? CountNodes(data.Hierarchy.Incoming?.FirstOrDefault()) : 0;
        var outgoingCount = data.Hierarchy != null ? CountNodes(data.Hierarchy.Outgoing?.FirstOrDefault()) : 0;
        
        if (incomingCount == 0 && outgoingCount == 0)
        {
            return "No call hierarchy found for the specified method";
        }
        
        var summary = $"Found {incomingCount} incoming and {outgoingCount} outgoing calls";
        
        if (data.Analysis != null)
        {
            var maxDepth = Math.Max(data.Analysis.MaxIncomingDepth, data.Analysis.MaxOutgoingDepth);
            if (maxDepth > 0)
            {
                summary += $" (max depth: {maxDepth})";
            }
        }
        
        if (wasReduced)
        {
            summary += " [reduced for context]";
        }
        
        return summary;
    }
}