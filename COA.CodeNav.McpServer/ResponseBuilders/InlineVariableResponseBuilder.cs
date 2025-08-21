using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Response builder for InlineVariableTool that implements token-aware response building
/// </summary>
public class InlineVariableResponseBuilder : BaseResponseBuilder<InlineVariableResult, InlineVariableResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public InlineVariableResponseBuilder(
        ILogger<InlineVariableResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }
    
    public override Task<InlineVariableResult> BuildResponseAsync(
        InlineVariableResult data,
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
            
            // Truncate very long initialization values
            if (!string.IsNullOrEmpty(data.InitializationValue) && data.InitializationValue.Length > 500)
            {
                data.InitializationValue = data.InitializationValue.Substring(0, 500) + "... (truncated)";
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
        InlineVariableResult data,
        string responseMode)
    {
        var insights = new List<string>();
        
        if (data.Success)
        {
            insights.Add($"Successfully inlined variable '{data.VariableName}' ({data.InlinedUsages} usages replaced)");
            
            if (data.InlinedUsages == 0)
            {
                insights.Add("Variable had no usages - declaration was simply removed");
            }
            else if (data.InlinedUsages == 1)
            {
                insights.Add("Single usage inlined - good candidate for removing unnecessary variable");
            }
            else if (data.InlinedUsages > 10)
            {
                insights.Add("Many usages inlined - verify that the variable wasn't providing useful semantic meaning");
            }
            
            if (!string.IsNullOrEmpty(data.InitializationValue))
            {
                if (data.InitializationValue.Length > 100)
                {
                    insights.Add("Variable had complex initialization expression - consider if readability was improved");
                }
                
                if (data.InitializationValue.Contains("new "))
                {
                    insights.Add("Variable contained object instantiation - verify performance implications of repeated creation");
                }
                
                if (data.InitializationValue.Contains("(") && data.InitializationValue.Contains(")"))
                {
                    insights.Add("Variable contained method call or expression - ensure side effects are appropriate");
                }
            }
            
            insights.Add("Variable declaration removed and all usages replaced with initialization value");
        }
        else
        {
            insights.Add("Variable inlining failed - check error details for resolution steps");
            
            if (data.Message?.Contains("const") == true)
            {
                insights.Add("Const variables are already inlined by the compiler");
            }
            
            if (data.Message?.Contains("initialization") == true)
            {
                insights.Add("Variables must have initialization values to be inlined");
            }
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        InlineVariableResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Success && data.Query?.FilePath != null)
        {
            // Action to check compilation after inlining
            actions.Add(new AIAction
            {
                Action = "csharp_get_diagnostics",
                Description = "Check for compilation errors after variable inlining",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.Query.FilePath,
                    ["scope"] = "file"
                },
                Priority = 90,
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
                Priority = 75,
                Category = "formatting"
            });
            
            // Action to check for other unused variables
            actions.Add(new AIAction
            {
                Action = "csharp_find_unused_code",
                Description = "Check for other unused variables in the same scope",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.Query.FilePath,
                    ["scope"] = "file",
                    ["symbolKinds"] = new[] { "Field", "LocalVariable" },
                    ["includePrivate"] = true
                },
                Priority = 65,
                Category = "cleanup"
            });
            
            // Action to check code metrics
            actions.Add(new AIAction
            {
                Action = "csharp_code_metrics",
                Description = "Check code metrics to verify simplification",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.Query.FilePath,
                    ["scope"] = "method"
                },
                Priority = 55,
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