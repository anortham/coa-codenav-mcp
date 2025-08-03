using Microsoft.CodeAnalysis.MSBuild;

namespace COA.CodeNav.McpServer.Infrastructure;

/// <summary>
/// Tracks information about a loaded workspace including usage statistics
/// </summary>
internal class WorkspaceTrackingInfo
{
    public string WorkspaceId { get; }
    public MSBuildWorkspace Workspace { get; }
    public DateTime CreatedAt { get; }
    public DateTime LastAccessedAt { get; set; }
    public int AccessCount { get; set; }
    public string? LoadedPath { get; set; }

    public WorkspaceTrackingInfo(string workspaceId, MSBuildWorkspace workspace)
    {
        WorkspaceId = workspaceId;
        Workspace = workspace;
        CreatedAt = DateTime.UtcNow;
        LastAccessedAt = DateTime.UtcNow;
        AccessCount = 1;
    }

    public void RecordAccess()
    {
        LastAccessedAt = DateTime.UtcNow;
        AccessCount++;
    }

    public TimeSpan IdleTime => DateTime.UtcNow - LastAccessedAt;
}