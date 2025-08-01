using Microsoft.Extensions.Configuration;

namespace COA.CodeNav.McpServer.Services;

/// <summary>
/// Centralized path resolution service for all .codenav directory operations
/// </summary>
public class PathResolutionService : IPathResolutionService
{
    private readonly IConfiguration _configuration;
    private readonly string _basePath;
    
    public PathResolutionService(IConfiguration configuration)
    {
        _configuration = configuration;
        _basePath = InitializeBasePath();
    }
    
    private string InitializeBasePath()
    {
        // Allow override via configuration
        var basePath = _configuration["CodeNav:BasePath"] ?? PathConstants.BaseDirectoryName;
        
        if (!Path.IsPathRooted(basePath))
        {
            basePath = Path.Combine(Directory.GetCurrentDirectory(), basePath);
        }
        
        return Path.GetFullPath(basePath);
    }
    
    public string GetBasePath()
    {
        return _basePath;
    }
    
    public string GetLogsPath()
    {
        var logsPath = Path.Combine(_basePath, PathConstants.LogsDirectoryName);
        return logsPath;
    }
    
    public string GetCachePath()
    {
        var cachePath = Path.Combine(_basePath, PathConstants.CacheDirectoryName);
        return cachePath;
    }
    
    public string GetWorkspaceMetadataPath()
    {
        var metadataPath = Path.Combine(_basePath, PathConstants.WorkspaceMetadataDirectoryName);
        return metadataPath;
    }
    
    // Safe file system operations implementation
    
    public bool DirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }
    
    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
    
    public string GetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
    
    public void EnsureDirectoryExists(string path)
    {
        if (!DirectoryExists(path))
        {
            Directory.CreateDirectory(path);
        }
    }
}