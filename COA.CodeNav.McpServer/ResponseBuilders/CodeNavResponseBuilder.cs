using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using COA.Mcp.Framework.TokenOptimization.Reduction;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Base response builder for CodeNav tools using framework's token optimization
/// </summary>
public abstract class CodeNavResponseBuilder<TData> : BaseResponseBuilder<TData>
{
    protected readonly ITokenEstimator _tokenEstimator;
    
    protected const int DEFAULT_TOKEN_LIMIT = 10000;
    protected const int AGGRESSIVE_TOKEN_LIMIT = 15000;
    
    protected CodeNavResponseBuilder(
        ILogger logger,
        ITokenEstimator tokenEstimator) 
        : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }
    
    /// <summary>
    /// Calculate safe token budget using framework's safety modes
    /// </summary>
    protected int CalculateSafeTokenBudget(ResponseContext context)
    {
        var baseLimit = context.TokenLimit ?? DEFAULT_TOKEN_LIMIT;
        var safetyMode = context.TokenLimit.HasValue 
            ? TokenSafetyMode.Default 
            : TokenSafetyMode.Conservative;
            
        return TokenEstimator.CalculateTokenBudget(
            baseLimit,
            0, // No tokens used yet
            safetyMode);
    }
    
    /// <summary>
    /// Reduce collection to fit token budget using framework's reduction engine
    /// </summary>
    protected List<T> ReduceCollection<T>(
        IList<T> items,
        int tokenBudget,
        string strategy = "standard",
        Func<T, int>? priorityFunction = null)
    {
        var reductionContext = priorityFunction != null
            ? new ReductionContext { PriorityFunction = item => priorityFunction((T)item) }
            : null;
            
        var result = _reductionEngine.Reduce(
            items,
            item => _tokenEstimator.EstimateObject(item),
            tokenBudget,
            strategy,
            reductionContext);
            
        _logger?.LogDebug("Reduced {Original} items to {Reduced} items (estimated {Tokens} tokens)",
            result.OriginalCount, result.Items.Count, result.EstimatedTokens);
            
        return result.Items;
    }
    
    /// <summary>
    /// Build AI-optimized response with automatic token management
    /// </summary>
    protected AIOptimizedResponse BuildAIOptimizedResponse(
        string summary,
        object data,
        List<string> insights,
        List<AIAction> actions,
        ResponseContext context,
        DateTime startTime,
        bool wasTruncated = false)
    {
        var tokenBudget = CalculateSafeTokenBudget(context);
        
        // Allocate token budget across response components
        var dataBudget = (int)(tokenBudget * 0.7);
        var insightsBudget = (int)(tokenBudget * 0.15);
        var actionsBudget = (int)(tokenBudget * 0.15);
        
        // Reduce insights and actions to fit budget
        var reducedInsights = ReduceInsights(insights, insightsBudget);
        var reducedActions = ReduceActions(actions, actionsBudget);
        
        var response = new AIOptimizedResponse
        {
            Format = "ai-optimized",
            Data = new AIResponseData
            {
                Summary = summary,
                Results = data,
                Count = GetResultCount(data)
            },
            Insights = reducedInsights,
            Actions = reducedActions,
            Meta = new AIResponseMeta
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                Truncated = wasTruncated,
                TokenInfo = new TokenInfo
                {
                    Estimated = 0, // Will be calculated below
                    Limit = context.TokenLimit ?? DEFAULT_TOKEN_LIMIT
                }
            }
        };
        
        // Calculate actual token usage
        response.Meta.TokenInfo.Estimated = _tokenEstimator.EstimateObject(response);
        
        return response;
    }
    
    private int GetResultCount(object data)
    {
        if (data is System.Collections.ICollection collection)
            return collection.Count;
        if (data is Array array)
            return array.Length;
        return 1;
    }
}