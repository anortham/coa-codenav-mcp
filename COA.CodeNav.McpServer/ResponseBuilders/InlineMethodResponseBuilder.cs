using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Response builder for InlineMethodTool that implements token-aware response building
/// </summary>
public class InlineMethodResponseBuilder : BaseResponseBuilder<InlineMethodResult, InlineMethodResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public InlineMethodResponseBuilder(
        ILogger<InlineMethodResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }
    
    public override Task<InlineMethodResult> BuildResponseAsync(
        InlineMethodResult data,
        ResponseContext context)
    {
        var tokenBudget = CalculateTokenBudget(context);
        var startTime = DateTime.UtcNow;
        
        // Estimate current tokens
        var currentTokens = _tokenEstimator.EstimateObject(data);
        var wasReduced = false;
        
        // Apply progressive reduction if needed
        if (currentTokens > tokenBudget)
        {
            // Reduce updated code if it's very large
            if (!string.IsNullOrEmpty(data.UpdatedCode) && data.UpdatedCode.Length > 4000)
            {
                data.UpdatedCode = TruncateCode(data.UpdatedCode, 3000);
                wasReduced = true;
            }
            
            // Reduce method body if it's very large
            if (!string.IsNullOrEmpty(data.MethodBody) && data.MethodBody.Length > 1000)
            {
                data.MethodBody = TruncateCode(data.MethodBody, 800);
                wasReduced = true;
            }
        }
        
        // Generate insights
        var insights = GenerateInsights(data, context.ResponseMode);
        
        // Generate actions
        var actions = GenerateActions(data, (int)(tokenBudget * 0.15));
        
        // Apply token-aware reductions to insights and actions
        data.Insights = ReduceInsights(insights, (int)(tokenBudget * 0.1));
        data.Actions = ReduceActions(actions, (int)(tokenBudget * 0.15));
        
        // Update execution metadata
        data.Meta = new ToolExecutionMetadata
        {
            Mode = context.ResponseMode ?? "optimized",
            Truncated = wasReduced,
            Tokens = _tokenEstimator.EstimateObject(data),
            ExecutionTime = data.Meta?.ExecutionTime ?? $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
        };
        
        if (wasReduced)
        {
            data.Message += " (Response optimized for token budget)";
        }
        
        return Task.FromResult(data);
    }
    
    protected override List<string> GenerateInsights(
        InlineMethodResult data,
        string responseMode)
    {
        var insights = new List<string>();
        
        if (data.Success)
        {
            insights.Add($"Successfully inlined method '{data.MethodName}' ({data.InlinedCallSites} call sites replaced)");
            
            if (data.InlinedCallSites > 5)
            {
                insights.Add("Large number of call sites inlined - verify code correctness and consider if method was providing useful abstraction");
            }
            
            if (data.InlinedCallSites == 1)
            {
                insights.Add("Single call site inlined - good candidate for removal of unnecessary indirection");
            }
            
            if (!string.IsNullOrEmpty(data.MethodBody) && data.MethodBody.Contains("throw"))
            {
                insights.Add("Method contained exception throwing - verify error handling logic after inlining");
            }
            
            insights.Add("Method declaration removed and all call sites replaced with method body");
        }
        else
        {
            insights.Add("Method inlining failed - check error details for resolution steps");
            
            if (data.Message?.Contains("recursive") == true)
            {
                insights.Add("Recursive methods cannot be safely inlined");
            }
            
            if (data.Message?.Contains("multiple returns") == true)
            {
                insights.Add("Methods with multiple return statements require complex control flow analysis");
            }
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        InlineMethodResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Success && data.Query?.FilePath != null)
        {
            // Action to check compilation after inlining
            actions.Add(new AIAction
            {
                Action = "csharp_get_diagnostics",
                Description = "Check for compilation errors after method inlining",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.Query.FilePath,
                    ["scope"] = "file"
                },
                Priority = 95,
                Category = "validation"
            });
            
            // Action to format the document
            actions.Add(new AIAction
            {
                Action = "csharp_format_document",
                Description = "Format the document after inlining",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.Query.FilePath
                },
                Priority = 80,
                Category = "formatting"
            });
            
            // Action to check for unused variables after inlining
            actions.Add(new AIAction
            {
                Action = "csharp_find_unused_code",
                Description = "Check for unused variables after method inlining",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.Query.FilePath,
                    ["scope"] = "file",
                    ["includePrivate"] = true
                },
                Priority = 70,
                Category = "cleanup"
            });
            
            // Action to run code metrics to check if complexity improved
            actions.Add(new AIAction
            {
                Action = "csharp_code_metrics",
                Description = "Check code metrics after inlining to verify improvement",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.Query.FilePath,
                    ["scope"] = "file"
                },
                Priority = 60,
                Category = "analysis"
            });
        }
        
        return actions;
    }
    
    private string TruncateCode(string code, int maxLength)
    {
        if (code.Length <= maxLength) return code;
        
        var truncated = code.Substring(0, maxLength);
        var lastNewline = truncated.LastIndexOf('\n');
        if (lastNewline > maxLength * 0.8) // Keep most of the content
        {
            truncated = truncated.Substring(0, lastNewline);
        }
        
        return truncated + "\n// ... (truncated for token budget)";
    }
}