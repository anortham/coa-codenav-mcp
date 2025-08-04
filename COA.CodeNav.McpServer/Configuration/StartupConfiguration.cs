namespace COA.CodeNav.McpServer.Configuration;

/// <summary>
/// Configuration for MCP server startup behavior
/// </summary>
public class StartupConfiguration
{
    /// <summary>
    /// Whether to automatically load a solution on startup
    /// </summary>
    public bool AutoLoadSolution { get; set; } = false;

    /// <summary>
    /// Path to the solution file to load on startup.
    /// If null, will search for .sln files in the current directory and parent directories.
    /// </summary>
    public string? SolutionPath { get; set; }

    /// <summary>
    /// Maximum depth to search for solution files (default: 5 levels up)
    /// </summary>
    public int MaxSearchDepth { get; set; } = 5;

    /// <summary>
    /// Preferred solution name when multiple .sln files are found
    /// </summary>
    public string? PreferredSolutionName { get; set; }

    /// <summary>
    /// Whether to fail startup if no solution is found (default: false, just log warning)
    /// </summary>
    public bool RequireSolution { get; set; } = false;
}