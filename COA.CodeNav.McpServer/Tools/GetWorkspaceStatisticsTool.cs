using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Models;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// Tool for getting workspace statistics and resource usage
/// </summary>
public class GetWorkspaceStatisticsTool : McpToolBase<GetWorkspaceStatisticsParams, GetWorkspaceStatisticsResult>
{
    private readonly ILogger<GetWorkspaceStatisticsTool> _logger;
    private readonly MSBuildWorkspaceManager _workspaceManager;
    private readonly ITokenEstimator _tokenEstimator;

    public override string Name => "csharp_get_workspace_statistics";
    public override string Description => "Get statistics about currently loaded workspaces and resource usage";

    public GetWorkspaceStatisticsTool(
        ILogger<GetWorkspaceStatisticsTool> logger,
        MSBuildWorkspaceManager workspaceManager,
        ITokenEstimator tokenEstimator)
        : base(logger)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _tokenEstimator = tokenEstimator;
    }

    protected override Task<GetWorkspaceStatisticsResult> ExecuteInternalAsync(
        GetWorkspaceStatisticsParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogInformation("Getting workspace statistics");
        
        var stats = _workspaceManager.GetStatistics();
        var process = System.Diagnostics.Process.GetCurrentProcess();
        
        var memoryMB = process.WorkingSet64 / (1024 * 1024);
        
        var result = new GetWorkspaceStatisticsResult
        {
            Success = true,
            Message = $"Found {stats.TotalWorkspaces} active workspace(s)",
            Query = new QueryInfo
            {
                // No specific query parameters for this tool
            },
            Summary = new SummaryInfo
            {
                TotalFound = stats.TotalWorkspaces,
                Returned = stats.TotalWorkspaces,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            },
            Statistics = new WorkspaceStatisticsInfo
            {
                TotalWorkspaces = stats.TotalWorkspaces,
                MaxWorkspaces = stats.MaxWorkspaces,
                AvailableSlots = stats.MaxWorkspaces - stats.TotalWorkspaces,
                OldestIdleTime = stats.OldestIdleTime.ToString(@"hh\:mm\:ss"),
                TotalAccessCount = stats.TotalAccessCount,
                MemoryUsageMB = memoryMB,
                GCMemoryMB = GC.GetTotalMemory(false) / (1024 * 1024),
                WorkspaceDetails = stats.WorkspaceDetails.Select(w => new WorkspaceDetailInfo
                {
                    WorkspaceId = w.WorkspaceId,
                    LoadedPath = w.LoadedPath ?? "N/A",
                    CreatedAt = w.CreatedAt,
                    LastAccessedAt = w.LastAccessedAt,
                    AccessCount = w.AccessCount,
                    IdleTime = w.IdleTime.ToString(@"hh\:mm\:ss"),
                    IsStale = w.IdleTime > TimeSpan.FromMinutes(15)
                }).ToList()
            },
            ResultsSummary = new ResultsSummary
            {
                Included = stats.TotalWorkspaces,
                Total = stats.TotalWorkspaces,
                HasMore = false
            },
            Insights = GenerateInsights(stats, memoryMB),
            Actions = GenerateNextActions(stats),
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
        
        return Task.FromResult(result);
    }

    private List<string> GenerateInsights(WorkspaceStatistics stats, long memoryMB)
    {
        var insights = new List<string>();
        
        // Workspace usage
        var usage = stats.TotalWorkspaces == 0 ? 0 : (double)stats.TotalWorkspaces / stats.MaxWorkspaces * 100;
        insights.Add($"📊 Workspace capacity at {usage:F0}% ({stats.TotalWorkspaces}/{stats.MaxWorkspaces})");
        
        // Memory usage
        insights.Add($"💾 Process memory usage: {memoryMB}MB");
        
        // Idle workspaces
        var idleCount = stats.WorkspaceDetails.Count(w => w.IdleTime > TimeSpan.FromMinutes(15));
        if (idleCount > 0)
        {
            insights.Add($"⏱️ {idleCount} workspace(s) idle for more than 15 minutes");
        }
        
        // Access patterns
        if (stats.TotalWorkspaces > 0 && stats.TotalAccessCount > 0)
        {
            var avgAccess = (double)stats.TotalAccessCount / stats.TotalWorkspaces;
            insights.Add($"📈 Average {avgAccess:F1} accesses per workspace");
        }
        
        // Recommendations
        if (usage > 80)
        {
            insights.Add("⚠️ Consider closing unused workspaces to free resources");
        }
        
        if (memoryMB > 1500)
        {
            insights.Add("⚠️ High memory usage detected - idle workspaces may be evicted");
        }
        
        return insights;
    }

    private List<AIAction> GenerateNextActions(WorkspaceStatistics stats)
    {
        var actions = new List<AIAction>();

        // If no workspaces are loaded, suggest loading one
        if (stats.TotalWorkspaces == 0)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_load_solution",
                Description = "Load a solution to start working",
                Parameters = new Dictionary<string, object>
                {
                    ["solutionPath"] = "<path-to-your-solution.sln>"
                },
                Priority = 90
            });
        }

        // Always suggest refreshing stats
        actions.Add(new AIAction
        {
            Action = "csharp_get_workspace_statistics",
            Description = "Refresh workspace statistics",
            Parameters = new Dictionary<string, object>(),
            Priority = 30
        });

        return actions;
    }

}

public class GetWorkspaceStatisticsParams
{
    // No parameters needed for this tool
}

public class GetWorkspaceStatisticsResult : ToolResultBase
{
    public override string Operation => ToolNames.GetWorkspaceStatistics;
    
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }
    
    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }
    
    [JsonPropertyName("statistics")]
    public WorkspaceStatisticsInfo? Statistics { get; set; }
    
    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }
}

public class WorkspaceStatisticsInfo
{
    [JsonPropertyName("totalWorkspaces")]
    public int TotalWorkspaces { get; set; }
    
    [JsonPropertyName("maxWorkspaces")]
    public int MaxWorkspaces { get; set; }
    
    [JsonPropertyName("availableSlots")]
    public int AvailableSlots { get; set; }
    
    [JsonPropertyName("oldestIdleTime")]
    public string OldestIdleTime { get; set; } = "";
    
    [JsonPropertyName("totalAccessCount")]
    public int TotalAccessCount { get; set; }
    
    [JsonPropertyName("memoryUsageMB")]
    public long MemoryUsageMB { get; set; }
    
    [JsonPropertyName("gcMemoryMB")]
    public long GCMemoryMB { get; set; }
    
    [JsonPropertyName("workspaceDetails")]
    public List<WorkspaceDetailInfo> WorkspaceDetails { get; set; } = new();
}

public class WorkspaceDetailInfo
{
    [JsonPropertyName("workspaceId")]
    public required string WorkspaceId { get; set; }
    
    [JsonPropertyName("loadedPath")]
    public required string LoadedPath { get; set; }
    
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
    
    [JsonPropertyName("lastAccessedAt")]
    public DateTime LastAccessedAt { get; set; }
    
    [JsonPropertyName("accessCount")]
    public int AccessCount { get; set; }
    
    [JsonPropertyName("idleTime")]
    public required string IdleTime { get; set; }
    
    [JsonPropertyName("isStale")]
    public bool IsStale { get; set; }
}