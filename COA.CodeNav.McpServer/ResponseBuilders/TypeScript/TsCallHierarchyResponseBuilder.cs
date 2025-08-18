using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders.TypeScript;

/// <summary>
/// Response builder for TypeScript call hierarchy with token optimization
/// </summary>
public class TsCallHierarchyResponseBuilder : BaseResponseBuilder<TsCallHierarchyResult, TsCallHierarchyResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public TsCallHierarchyResponseBuilder(
        ILogger<TsCallHierarchyResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }

    public override Task<TsCallHierarchyResult> BuildResponseAsync(
        TsCallHierarchyResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Apply progressive reduction to call hierarchy data
        var wasReduced = false;
        var originalIncomingCount = data.IncomingCalls?.Count ?? 0;
        var originalOutgoingCount = data.OutgoingCalls?.Count ?? 0;
        var totalOriginal = originalIncomingCount + originalOutgoingCount;
        
        if (totalOriginal > 0)
        {
            // Don't reduce small result sets (<=20 calls total)
            if (totalOriginal <= 20)
            {
                _logger?.LogDebug("TsCallHierarchyResponseBuilder: Small result set ({Count} calls), keeping all", 
                    totalOriginal);
                wasReduced = false;
            }
            else
            {
                // Fix token budget if it's 0 or negative
                if (tokenBudget <= 0)
                {
                    tokenBudget = context.TokenLimit ?? 10000;
                    _logger?.LogDebug("TsCallHierarchyResponseBuilder: Fixed token budget from 0 to {Budget}", tokenBudget);
                }
                
                var originalTokens = _tokenEstimator.EstimateObject(data);
                
                _logger?.LogDebug("TsCallHierarchyResponseBuilder: Original calls: {Count}, Estimated tokens: {Tokens}, Budget: {Budget}", 
                    totalOriginal, originalTokens, tokenBudget);
                
                // Apply token optimization for large result sets
                if (originalTokens > tokenBudget * 0.7)
                {
                    var callBudget = (int)(tokenBudget * 0.7);
                    ReduceCallHierarchy(data, callBudget);
                    
                    var newIncomingCount = data.IncomingCalls?.Count ?? 0;
                    var newOutgoingCount = data.OutgoingCalls?.Count ?? 0;
                    var newTotal = newIncomingCount + newOutgoingCount;
                    
                    wasReduced = newTotal < totalOriginal;
                    
                    _logger?.LogDebug("TsCallHierarchyResponseBuilder: Reduced from {Original} to {Reduced} calls", 
                        totalOriginal, newTotal);
                }
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
        if (data.ResultsSummary != null)
        {
            var currentTotal = (data.IncomingCalls?.Count ?? 0) + (data.OutgoingCalls?.Count ?? 0);
            data.ResultsSummary.Included = currentTotal;
            data.ResultsSummary.HasMore = wasReduced || data.ResultsSummary.HasMore;
        }
        
        // Rebuild call tree with reduced data if needed
        if (data.Root != null && wasReduced)
        {
            data.CallTree = BuildOptimizedCallTree(data.Root, data.IncomingCalls, data.OutgoingCalls);
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
        data.Message = BuildSummary(data, originalIncomingCount, originalOutgoingCount);
        
        return Task.FromResult(data);
    }
    
    protected override List<string> GenerateInsights(
        TsCallHierarchyResult data,
        string? responseMode)
    {
        var insights = new List<string>();
        
        if (data.Root?.Name != null)
        {
            insights.Add($"📊 Call hierarchy for '{data.Root.Name}'");
        }
        
        var incomingCount = data.IncomingCalls?.Count ?? 0;
        var outgoingCount = data.OutgoingCalls?.Count ?? 0;
        var totalCalls = incomingCount + outgoingCount;
        
        if (totalCalls == 0)
        {
            insights.Add("🔍 No incoming or outgoing calls found - symbol might be unused or a leaf node");
            return insights;
        }
        
        // Analyze call patterns
        if (incomingCount > 0)
        {
            insights.Add($"📥 {incomingCount} incoming call{(incomingCount > 1 ? "s" : "")} - symbol is being used");
            
            if (incomingCount > 10)
            {
                insights.Add("⚠️ High number of incoming calls - consider if this symbol has too many responsibilities");
            }
        }
        
        if (outgoingCount > 0)
        {
            insights.Add($"📤 {outgoingCount} outgoing call{(outgoingCount > 1 ? "s" : "")} - symbol depends on other code");
            
            if (outgoingCount > 15)
            {
                insights.Add("🔗 Complex dependencies - consider breaking down this function");
            }
        }
        
        // File distribution analysis
        if (data.IncomingCalls != null && data.OutgoingCalls != null)
        {
            var allFiles = new HashSet<string>();
            foreach (var call in data.IncomingCalls)
            {
                if (call.From?.File != null) allFiles.Add(call.From.File);
            }
            foreach (var call in data.OutgoingCalls)
            {
                if (call.To?.File != null) allFiles.Add(call.To.File);
            }
            
            if (allFiles.Count > 1)
            {
                insights.Add($"📁 Calls span {allFiles.Count} file{(allFiles.Count > 1 ? "s" : "")} - good modularity");
            }
            else if (allFiles.Count == 1)
            {
                insights.Add("📄 All calls within single file - consider if symbol should be private");
            }
        }
        
        // Usage pattern insights
        if (incomingCount == 0 && outgoingCount > 0)
        {
            insights.Add("🌱 Entry point pattern - symbol is called externally but calls other code");
        }
        else if (incomingCount > 0 && outgoingCount == 0)
        {
            insights.Add("🎯 Leaf function pattern - symbol is called but doesn't call other code");
        }
        else if (incomingCount > 0 && outgoingCount > 0)
        {
            insights.Add("🔄 Intermediate function pattern - symbol is part of a call chain");
        }
        
        if (data.ResultsSummary?.HasMore == true)
        {
            insights.Add($"📋 Showing {data.ResultsSummary.Included} of {data.ResultsSummary.Total} calls - increase MaxNodes for complete hierarchy");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        TsCallHierarchyResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Root?.File != null)
        {
            // Navigate to definition
            actions.Add(new AIAction
            {
                Action = "ts_goto_definition",
                Description = $"Navigate to '{data.Root.Name}' definition",
                Category = "navigate",
                Priority = 8,
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.Root.File,
                    ["line"] = data.Root.Span?.Start?.Line ?? 0,
                    ["character"] = data.Root.Span?.Start?.Character ?? 0
                }
            });
            
            // Find all references
            actions.Add(new AIAction
            {
                Action = "ts_find_all_references",
                Description = $"Find all references to '{data.Root.Name}'",
                Category = "analyze",
                Priority = 7,
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.Root.File,
                    ["line"] = data.Root.Span?.Start?.Line ?? 0,
                    ["character"] = data.Root.Span?.Start?.Character ?? 0
                }
            });
        }
        
        // Refactoring suggestions based on call patterns
        var incomingCount = data.IncomingCalls?.Count ?? 0;
        var outgoingCount = data.OutgoingCalls?.Count ?? 0;
        
        if (incomingCount == 0 && outgoingCount > 0)
        {
            actions.Add(new AIAction
            {
                Action = "analyze_unused_symbol",
                Description = "Symbol appears unused - verify if it can be removed",
                Category = "cleanup",
                Priority = 6
            });
        }
        
        if (outgoingCount > 10)
        {
            actions.Add(new AIAction
            {
                Action = "ts_extract_method",
                Description = "Consider extracting methods to reduce complexity",
                Category = "refactor",
                Priority = 6
            });
        }
        
        if (incomingCount > 10)
        {
            actions.Add(new AIAction
            {
                Action = "analyze_coupling",
                Description = "High usage suggests reviewing single responsibility principle",
                Category = "analyze",
                Priority = 5
            });
        }
        
        // Navigate to specific callers/callees
        if (data.IncomingCalls?.Count > 0)
        {
            var firstCaller = data.IncomingCalls.First();
            if (firstCaller.From?.File != null)
            {
                actions.Add(new AIAction
                {
                    Action = "navigate_to_caller",
                    Description = $"Navigate to caller in {Path.GetFileName(firstCaller.From.File)}",
                    Category = "navigate",
                    Priority = 7,
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = firstCaller.From.File,
                        ["line"] = firstCaller.From.Span?.Start?.Line ?? 0,
                        ["character"] = firstCaller.From.Span?.Start?.Character ?? 0
                    }
                });
            }
        }
        
        if (data.OutgoingCalls?.Count > 0)
        {
            var firstCallee = data.OutgoingCalls.First();
            if (firstCallee.To?.File != null)
            {
                actions.Add(new AIAction
                {
                    Action = "navigate_to_callee",
                    Description = $"Navigate to called function in {Path.GetFileName(firstCallee.To.File)}",
                    Category = "navigate",
                    Priority = 7,
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = firstCallee.To.File,
                        ["line"] = firstCallee.To.Span?.Start?.Line ?? 0,
                        ["character"] = firstCallee.To.Span?.Start?.Character ?? 0
                    }
                });
            }
        }
        
        // Rename symbol if appropriate
        if (data.Root?.File != null && incomingCount > 0)
        {
            actions.Add(new AIAction
            {
                Action = "ts_rename_symbol",
                Description = $"Rename '{data.Root.Name}' across all references",
                Category = "refactor",
                Priority = 5,
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.Root.File,
                    ["line"] = data.Root.Span?.Start?.Line ?? 0,
                    ["character"] = data.Root.Span?.Start?.Character ?? 0
                }
            });
        }
        
        return actions;
    }
    
    private void ReduceCallHierarchy(TsCallHierarchyResult data, int tokenBudget)
    {
        var availableTokens = tokenBudget;
        
        // Reserve tokens for core structure
        var coreTokens = _tokenEstimator.EstimateObject(new { data.Root, data.Query, data.Summary });
        availableTokens -= coreTokens;
        
        if (availableTokens <= 0)
            return;
        
        // Allocate tokens between incoming and outgoing calls
        var incomingCount = data.IncomingCalls?.Count ?? 0;
        var outgoingCount = data.OutgoingCalls?.Count ?? 0;
        var totalCalls = incomingCount + outgoingCount;
        
        if (totalCalls == 0)
            return;
        
        // Split tokens proportionally
        var incomingTokens = (int)(availableTokens * 0.5);
        var outgoingTokens = availableTokens - incomingTokens;
        
        // Reduce incoming calls
        if (data.IncomingCalls != null && data.IncomingCalls.Count > 0)
        {
            data.IncomingCalls = ReduceCallList(data.IncomingCalls, incomingTokens);
        }
        
        // Reduce outgoing calls
        if (data.OutgoingCalls != null && data.OutgoingCalls.Count > 0)
        {
            data.OutgoingCalls = ReduceCallList(data.OutgoingCalls, outgoingTokens);
        }
    }
    
    private List<TsIncomingCall> ReduceCallList(List<TsIncomingCall> calls, int tokenBudget)
    {
        var result = new List<TsIncomingCall>();
        var currentTokens = 0;
        
        // Prioritize by file name (keep calls from different files for diversity)
        var prioritized = calls
            .GroupBy(c => c.From?.File ?? "unknown")
            .SelectMany(g => g.Take(2)) // Max 2 calls per file
            .OrderBy(c => c.From?.File ?? "")
            .ThenBy(c => c.From?.Name ?? "");
        
        foreach (var call in prioritized)
        {
            var callTokens = _tokenEstimator.EstimateObject(call);
            
            if (currentTokens + callTokens <= tokenBudget)
            {
                result.Add(call);
                currentTokens += callTokens;
            }
            else
            {
                break;
            }
        }
        
        return result;
    }
    
    private List<TsOutgoingCall> ReduceCallList(List<TsOutgoingCall> calls, int tokenBudget)
    {
        var result = new List<TsOutgoingCall>();
        var currentTokens = 0;
        
        // Prioritize by file name (keep calls to different files for diversity)
        var prioritized = calls
            .GroupBy(c => c.To?.File ?? "unknown")
            .SelectMany(g => g.Take(2)) // Max 2 calls per file
            .OrderBy(c => c.To?.File ?? "")
            .ThenBy(c => c.To?.Name ?? "");
        
        foreach (var call in prioritized)
        {
            var callTokens = _tokenEstimator.EstimateObject(call);
            
            if (currentTokens + callTokens <= tokenBudget)
            {
                result.Add(call);
                currentTokens += callTokens;
            }
            else
            {
                break;
            }
        }
        
        return result;
    }
    
    private TsCallTreeNode BuildOptimizedCallTree(
        TsCallHierarchyItem root, 
        List<TsIncomingCall>? incomingCalls, 
        List<TsOutgoingCall>? outgoingCalls)
    {
        var rootNode = new TsCallTreeNode
        {
            Item = root,
            Depth = 0,
            Direction = "root",
            Children = new List<TsCallTreeNode>(),
            IsExpanded = true
        };
        
        // Add incoming calls as children (limited for token optimization)
        if (incomingCalls != null)
        {
            foreach (var call in incomingCalls.Take(10))
            {
                if (call.From != null)
                {
                    rootNode.Children.Add(new TsCallTreeNode
                    {
                        Item = call.From,
                        Depth = 1,
                        Direction = "incoming",
                        Children = new List<TsCallTreeNode>(),
                        IsExpanded = false
                    });
                }
            }
        }
        
        // Add outgoing calls as children (limited for token optimization)
        if (outgoingCalls != null)
        {
            foreach (var call in outgoingCalls.Take(10))
            {
                if (call.To != null)
                {
                    rootNode.Children.Add(new TsCallTreeNode
                    {
                        Item = call.To,
                        Depth = 1,
                        Direction = "outgoing",
                        Children = new List<TsCallTreeNode>(),
                        IsExpanded = false
                    });
                }
            }
        }
        
        return rootNode;
    }
    
    private string BuildSummary(TsCallHierarchyResult data, int originalIncoming, int originalOutgoing)
    {
        var currentIncoming = data.IncomingCalls?.Count ?? 0;
        var currentOutgoing = data.OutgoingCalls?.Count ?? 0;
        var currentTotal = currentIncoming + currentOutgoing;
        var originalTotal = originalIncoming + originalOutgoing;
        
        if (originalTotal == 0)
        {
            return $"Call hierarchy for '{data.Root?.Name ?? "symbol"}' - no calls found";
        }
        
        var parts = new List<string>();
        if (currentIncoming > 0)
            parts.Add($"{currentIncoming} incoming");
        if (currentOutgoing > 0)
            parts.Add($"{currentOutgoing} outgoing");
        
        var summary = $"Call hierarchy for '{data.Root?.Name ?? "symbol"}' - {string.Join(", ", parts)}";
        
        if (currentTotal < originalTotal)
        {
            summary += $" (showing {currentTotal} of {originalTotal})";
        }
        
        return summary;
    }
}