namespace COA.CodeNav.McpServer.Services;

/// <summary>
/// Interface for centralized path resolution
/// </summary>
public interface IPathResolutionService
{
    /// <summary>
    /// Gets the base directory path
    /// </summary>
    /// <returns>The full path to the base directory (e.g., ".codenav")</returns>
    string GetBasePath();
    
    /// <summary>
    /// Gets the logs directory path
    /// </summary>
    /// <returns>The full path to the logs directory (e.g., ".codenav/logs")</returns>
    string GetLogsPath();
    
    /// <summary>
    /// Gets the cache directory path
    /// </summary>
    /// <returns>The full path to the cache directory (e.g., ".codenav/cache")</returns>
    string GetCachePath();
    
    /// <summary>
    /// Gets the workspace metadata directory path
    /// </summary>
    /// <returns>The full path to the metadata directory (e.g., ".codenav/metadata")</returns>
    string GetWorkspaceMetadataPath();
    
    /// <summary>
    /// Safely checks if a directory exists
    /// </summary>
    bool DirectoryExists(string path);
    
    /// <summary>
    /// Safely checks if a file exists
    /// </summary>
    bool FileExists(string path);
    
    /// <summary>
    /// Safely gets the full path
    /// </summary>
    string GetFullPath(string path);
    
    /// <summary>
    /// Ensures a directory exists, creating it if necessary
    /// </summary>
    void EnsureDirectoryExists(string path);
}