using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Response builder for ExtractInterfaceTool that implements token-aware response building
/// </summary>
public class ExtractInterfaceResponseBuilder : BaseResponseBuilder<ExtractInterfaceResult, ExtractInterfaceResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public ExtractInterfaceResponseBuilder(
        ILogger<ExtractInterfaceResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }
    
    public override Task<ExtractInterfaceResult> BuildResponseAsync(
        ExtractInterfaceResult data,
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
            // Reduce interface code if it's very large
            if (!string.IsNullOrEmpty(data.InterfaceCode) && data.InterfaceCode.Length > 2000)
            {
                data.InterfaceCode = TruncateCode(data.InterfaceCode, 1500);
                wasReduced = true;
            }
            
            // Reduce updated class code if it's very large
            if (!string.IsNullOrEmpty(data.UpdatedClassCode) && data.UpdatedClassCode.Length > 3000)
            {
                data.UpdatedClassCode = TruncateCode(data.UpdatedClassCode, 2000);
                wasReduced = true;
            }
            
            // Reduce extracted members if there are many
            if (data.ExtractedMembers?.Count > 20)
            {
                data.ExtractedMembers = data.ExtractedMembers.Take(20).ToList();
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
        ExtractInterfaceResult data,
        string responseMode)
    {
        var insights = new List<string>();
        
        if (data.Success)
        {
            insights.Add($"Successfully extracted interface '{data.InterfaceName}' with {data.ExtractedMembers?.Count ?? 0} members");
            
            if (data.ExtractedMembers?.Any() == true)
            {
                var membersByKind = data.ExtractedMembers.GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count());
                var kindSummary = string.Join(", ", membersByKind.Select(kv => $"{kv.Value} {kv.Key.ToLower()}{(kv.Value > 1 ? "s" : "")}"));
                insights.Add($"Interface includes: {kindSummary}");
            }
            
            if (!string.IsNullOrEmpty(data.UpdatedClassCode))
            {
                insights.Add("Class updated to implement the new interface");
            }
            
            insights.Add("Interface can be saved to a new file and class changes can be applied");
        }
        else
        {
            insights.Add("Interface extraction failed - check error details for resolution steps");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        ExtractInterfaceResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Success && data.Query?.FilePath != null)
        {
            // Action to check compilation after changes
            actions.Add(new AIAction
            {
                Action = "csharp_get_diagnostics",
                Description = "Check for compilation errors after interface extraction",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.Query.FilePath,
                    ["scope"] = "file"
                },
                Priority = 90,
                Category = "validation"
            });
            
            // Action to format the updated class
            actions.Add(new AIAction
            {
                Action = "csharp_format_document",
                Description = "Format the updated class code",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = data.Query.FilePath
                },
                Priority = 70,
                Category = "formatting"
            });
            
            // Action to find implementations of the new interface
            if (!string.IsNullOrEmpty(data.InterfaceName))
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_find_implementations",
                    Description = $"Find implementations of interface {data.InterfaceName}",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = data.Query.FilePath,
                        ["line"] = data.Query.Position?.Line ?? 1,
                        ["column"] = data.Query.Position?.Column ?? 1
                    },
                    Priority = 60,
                    Category = "exploration"
                });
            }
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