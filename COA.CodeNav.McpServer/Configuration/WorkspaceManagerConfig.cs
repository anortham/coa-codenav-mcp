namespace COA.CodeNav.McpServer.Configuration;

/// <summary>
/// Configuration for workspace resource management and limits
/// </summary>
public class WorkspaceManagerConfig
{
    /// <summary>
    /// Maximum number of workspaces that can be loaded concurrently
    /// </summary>
    public int MaxConcurrentWorkspaces { get; set; } = 10;

    /// <summary>
    /// Time after which an idle workspace will be unloaded
    /// </summary>
    public TimeSpan WorkspaceIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maximum number of documents to keep in cache
    /// </summary>
    public int MaxCachedDocuments { get; set; } = 1000;

    /// <summary>
    /// Interval at which to check for idle workspaces
    /// </summary>
    public TimeSpan IdleCheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether to enable workspace eviction based on memory pressure
    /// </summary>
    public bool EnableMemoryPressureEviction { get; set; } = true;

    /// <summary>
    /// Memory threshold (in MB) at which to start evicting workspaces
    /// </summary>
    public long MemoryPressureThresholdMB { get; set; } = 2048; // 2GB
}