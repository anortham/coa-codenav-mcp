using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Models;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

[McpServerToolType]
public class GetWorkspaceStatisticsTool
{
    private readonly ILogger<GetWorkspaceStatisticsTool> _logger;
    private readonly MSBuildWorkspaceManager _workspaceManager;

    public GetWorkspaceStatisticsTool(
        ILogger<GetWorkspaceStatisticsTool> logger,
        MSBuildWorkspaceManager workspaceManager)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
    }

    [McpServerTool(Name = "csharp_get_workspace_statistics")]
    [Description(@"Get statistics about currently loaded workspaces and resource usage.
Returns: Workspace count, memory usage, idle times, and access patterns.
Use cases: Monitoring resource usage, debugging workspace issues, understanding cache behavior.")]
    public Task<object> ExecuteAsync(GetWorkspaceStatisticsParams parameters, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        
        try
        {
            _logger.LogInformation("Getting workspace statistics");
            
            var stats = _workspaceManager.GetStatistics();
            var process = System.Diagnostics.Process.GetCurrentProcess();
            
            var memoryMB = process.WorkingSet64 / (1024 * 1024);
            
            return Task.FromResult<object>(new GetWorkspaceStatisticsResult
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
                Insights = GenerateInsights(stats, process),
                Actions = GenerateNextActions(stats, memoryMB),
                Meta = new ToolMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting workspace statistics");
            return Task.FromResult<object>(new GetWorkspaceStatisticsResult
            {
                Success = false,
                Message = $"Error getting statistics: {ex.Message}",
                Query = new QueryInfo
                {
                    // No specific query parameters for this tool
                },
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new List<string>
                        {
                            "Check server logs for detailed error information",
                            "Verify the server is running correctly"
                        }
                    }
                },
                Meta = new ToolMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                }
            });
        }
    }

    private List<string> GenerateInsights(WorkspaceStatistics stats, System.Diagnostics.Process process)
    {
        var insights = new List<string>();
        
        // Workspace usage
        var usage = (double)stats.TotalWorkspaces / stats.MaxWorkspaces * 100;
        insights.Add($"Workspace capacity at {usage:F0}% ({stats.TotalWorkspaces}/{stats.MaxWorkspaces})");
        
        // Memory usage
        var memoryMB = process.WorkingSet64 / (1024 * 1024);
        insights.Add($"Process memory usage: {memoryMB}MB");
        
        // Idle workspaces
        var idleCount = stats.WorkspaceDetails.Count(w => w.IdleTime > TimeSpan.FromMinutes(15));
        if (idleCount > 0)
        {
            insights.Add($"{idleCount} workspace(s) idle for more than 15 minutes");
        }
        
        // Access patterns
        if (stats.TotalAccessCount > 0)
        {
            var avgAccess = (double)stats.TotalAccessCount / stats.TotalWorkspaces;
            insights.Add($"Average {avgAccess:F1} accesses per workspace");
        }
        
        // Recommendations
        if (usage > 80)
        {
            insights.Add("Consider closing unused workspaces to free resources");
        }
        
        if (memoryMB > 1500)
        {
            insights.Add("High memory usage detected - idle workspaces may be evicted");
        }
        
        return insights;
    }

    private List<NextAction> GenerateNextActions(WorkspaceStatistics stats, long memoryMB)
    {
        var actions = new List<NextAction>();

        // If there are idle workspaces, suggest cleaning them up
        var idleCount = stats.WorkspaceDetails.Count(w => w.IdleTime > TimeSpan.FromMinutes(15));
        if (idleCount > 0)
        {
            var oldestIdle = stats.WorkspaceDetails
                .Where(w => w.IdleTime > TimeSpan.FromMinutes(15))
                .OrderByDescending(w => w.IdleTime)
                .FirstOrDefault();

            if (oldestIdle != null)
            {
                actions.Add(new NextAction
                {
                    Id = "close_idle_workspace",
                    Description = $"Close idle workspace (idle for {oldestIdle.IdleTime})",
                    ToolName = "workspace_close", // Future tool
                    Parameters = new
                    {
                        workspaceId = oldestIdle.WorkspaceId
                    },
                    Priority = "medium"
                });
            }
        }

        // If no workspaces are loaded, suggest loading one
        if (stats.TotalWorkspaces == 0)
        {
            actions.Add(new NextAction
            {
                Id = "load_solution",
                Description = "Load a solution to start working",
                ToolName = "csharp_load_solution",
                Parameters = new
                {
                    solutionPath = "<path-to-your-solution.sln>"
                },
                Priority = "high"
            });
        }

        // If memory usage is high, suggest garbage collection
        if (memoryMB > 1500)
        {
            actions.Add(new NextAction
            {
                Id = "force_gc",
                Description = "Force garbage collection to free memory",
                ToolName = "system_gc", // Future tool
                Parameters = new { },
                Priority = "low"
            });
        }

        // Always suggest refreshing stats
        actions.Add(new NextAction
        {
            Id = "refresh_stats",
            Description = "Refresh workspace statistics",
            ToolName = "csharp_get_workspace_statistics",
            Parameters = new { },
            Priority = "low"
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