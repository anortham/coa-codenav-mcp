using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using COA.Mcp.Framework.TokenOptimization.Reduction;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Base response builder for CodeNav tools using framework's token optimization with strong typing
/// </summary>
/// <typeparam name="TData">The type of input data to process</typeparam>
/// <typeparam name="TResult">The type of result to return</typeparam>
public abstract class CodeNavResponseBuilder<TData, TResult> : BaseResponseBuilder<TData, TResult>
    where TResult : new()
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
    
}