using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.ResponseBuilders;

/// <summary>
/// Response builder for MoveTypeTool that implements token-aware response building
/// </summary>
public class MoveTypeResponseBuilder : BaseResponseBuilder<MoveTypeResult, MoveTypeResult>
{
    private readonly ITokenEstimator _tokenEstimator;
    
    public MoveTypeResponseBuilder(
        ILogger<MoveTypeResponseBuilder> logger,
        ITokenEstimator tokenEstimator) : base(logger)
    {
        _tokenEstimator = tokenEstimator;
    }
    
    public override Task<MoveTypeResult> BuildResponseAsync(
        MoveTypeResult data,
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
            // Reduce source code if it's very large
            if (!string.IsNullOrEmpty(data.UpdatedSourceCode) && data.UpdatedSourceCode.Length > 3000)
            {
                data.UpdatedSourceCode = TruncateCode(data.UpdatedSourceCode, 2000);
                wasReduced = true;
            }
            
            // Reduce target code if it's very large
            if (!string.IsNullOrEmpty(data.UpdatedTargetCode) && data.UpdatedTargetCode.Length > 3000)
            {
                data.UpdatedTargetCode = TruncateCode(data.UpdatedTargetCode, 2000);
                wasReduced = true;
            }
            
            // No dependencies to reduce in the actual MoveTypeResult model
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
        MoveTypeResult data,
        string responseMode)
    {
        var insights = new List<string>();
        
        if (data.Success)
        {
            insights.Add($"Successfully moved type '{data.TypeName}' to {data.TargetFilePath}");
            
            if (data.WasNewFileCreated)
            {
                insights.Add("Created new file for the moved type");
            }
            
            insights.Add("Source and target files updated - verify compilation and update references as needed");
        }
        else
        {
            insights.Add("Type move failed - check error details for resolution steps");
        }
        
        return insights;
    }
    
    protected override List<AIAction> GenerateActions(
        MoveTypeResult data,
        int tokenBudget)
    {
        var actions = new List<AIAction>();
        
        if (data.Success)
        {
            // Action to check compilation in source file
            if (data.Query?.FilePath != null)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_get_diagnostics",
                    Description = "Check for compilation errors in source file",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = data.Query.FilePath,
                        ["scope"] = "file"
                    },
                    Priority = 95,
                    Category = "validation"
                });
            }
            
            // Action to check compilation in target file
            if (!string.IsNullOrEmpty(data.TargetFilePath))
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_get_diagnostics",
                    Description = "Check for compilation errors in target file",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = data.TargetFilePath,
                        ["scope"] = "file"
                    },
                    Priority = 95,
                    Category = "validation"
                });
            }
            
            // Action to add missing usings to target file
            if (!string.IsNullOrEmpty(data.TargetFilePath))
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_add_missing_usings",
                    Description = "Add missing using statements to target file",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = data.TargetFilePath,
                        ["preview"] = false
                    },
                    Priority = 85,
                    Category = "correction"
                });
            }
            
            // Action to find all references to the moved type
            if (!string.IsNullOrEmpty(data.TypeName) && !string.IsNullOrEmpty(data.TargetFilePath))
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_find_all_references",
                    Description = $"Find all references to moved type {data.TypeName}",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = data.TargetFilePath,
                        ["line"] = 1,
                        ["column"] = 1
                    },
                    Priority = 70,
                    Category = "exploration"
                });
            }
            
            // Action to format both files
            if (data.Query?.FilePath != null)
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_format_document",
                    Description = "Format source file after type removal",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = data.Query.FilePath
                    },
                    Priority = 60,
                    Category = "formatting"
                });
            }
            
            if (!string.IsNullOrEmpty(data.TargetFilePath))
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_format_document",
                    Description = "Format target file after type addition",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = data.TargetFilePath
                    },
                    Priority = 60,
                    Category = "formatting"
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