using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using COA.CodeNav.McpServer.Configuration;

namespace COA.CodeNav.McpServer.Infrastructure;

public class MSBuildWorkspaceManager : IDisposable
{
    private readonly ILogger<MSBuildWorkspaceManager> _logger;
    private readonly WorkspaceManagerConfig _config;
    private readonly ConcurrentDictionary<string, WorkspaceTrackingInfo> _workspaces = new();
    private static bool _msbuildRegistered = false;
    private static readonly object _registrationLock = new();
    private readonly Timer _idleCheckTimer;
    private bool _disposed;

    public MSBuildWorkspaceManager(
        ILogger<MSBuildWorkspaceManager> logger,
        IOptions<WorkspaceManagerConfig> config)
    {
        _logger = logger;
        _config = config.Value;
        // Defer MSBuild registration until first use to avoid blocking initialization
        // EnsureMSBuildRegistered();
        
        // Start idle workspace cleanup timer
        _idleCheckTimer = new Timer(
            CheckIdleWorkspaces,
            null,
            _config.IdleCheckInterval,
            _config.IdleCheckInterval);
    }

    public void EnsureMSBuildRegistered()
    {
        lock (_registrationLock)
        {
            if (!_msbuildRegistered)
            {
                try
                {
                    // Register the latest MSBuild instance
                    var instances = MSBuildLocator.QueryVisualStudioInstances()
                        .OrderByDescending(x => x.Version)
                        .ToList();

                    if (instances.Any())
                    {
                        var selectedInstance = instances.First();
                        _logger.LogInformation("Registering MSBuild from: {Name} {Version} at {Path}", 
                            selectedInstance.Name, 
                            selectedInstance.Version, 
                            selectedInstance.MSBuildPath);
                        
                        MSBuildLocator.RegisterInstance(selectedInstance);
                        _msbuildRegistered = true;
                    }
                    else
                    {
                        // Try to register defaults if no VS instances found
                        MSBuildLocator.RegisterDefaults();
                        _msbuildRegistered = true;
                        _logger.LogWarning("No Visual Studio instances found. Registered default MSBuild.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to register MSBuild");
                    throw;
                }
            }
        }
    }

    public async Task<MSBuildWorkspace> GetOrCreateWorkspaceAsync(string workspaceId, Dictionary<string, string>? properties = null)
    {
        // Ensure MSBuild is registered before creating any workspace
        EnsureMSBuildRegistered();
        return await Task.Run(() =>
        {
            // Check if workspace already exists
            if (_workspaces.TryGetValue(workspaceId, out var existingInfo))
            {
                existingInfo.RecordAccess();
                return existingInfo.Workspace;
            }

            // Check workspace limit
            if (_workspaces.Count >= _config.MaxConcurrentWorkspaces)
            {
                _logger.LogWarning("Maximum concurrent workspaces ({Max}) reached. Attempting to evict idle workspace.", 
                    _config.MaxConcurrentWorkspaces);
                
                if (!TryEvictIdleWorkspace())
                {
                    throw new InvalidOperationException(
                        $"Cannot create new workspace. Maximum limit of {_config.MaxConcurrentWorkspaces} workspaces reached and no idle workspaces to evict.");
                }
            }

            // Check memory pressure if enabled
            if (_config.EnableMemoryPressureEviction && IsUnderMemoryPressure())
            {
                _logger.LogWarning("System under memory pressure. Attempting to evict idle workspace.");
                TryEvictIdleWorkspace();
            }

            // Create new workspace
            var info = _workspaces.GetOrAdd(workspaceId, id =>
            {
                _logger.LogInformation("Creating new MSBuild workspace: {WorkspaceId}", id);
                
                var workspace = MSBuildWorkspace.Create(properties ?? new Dictionary<string, string>());
                
                // Configure workspace
                workspace.WorkspaceFailed += (sender, args) =>
                {
                    _logger.LogWarning("Workspace diagnostic: {Message}", args.Diagnostic.Message);
                };
                
                workspace.WorkspaceChanged += (sender, args) =>
                {
                    _logger.LogDebug("Workspace changed: {Kind} - {ProjectId}", 
                        args.Kind, 
                        args.ProjectId?.Id);
                };
                
                return new WorkspaceTrackingInfo(id, workspace);
            });

            info.RecordAccess();
            return info.Workspace;
        });
    }

    public async Task<Solution?> LoadSolutionAsync(string solutionPath, string? workspaceId = null, IProgress<Microsoft.CodeAnalysis.MSBuild.ProjectLoadProgress>? progress = null)
    {
        try
        {
            if (!File.Exists(solutionPath))
            {
                _logger.LogError("Solution file not found: {Path}", solutionPath);
                return null;
            }

            workspaceId ??= solutionPath;
            var workspace = await GetOrCreateWorkspaceAsync(workspaceId);
            
            _logger.LogInformation("Loading solution: {Path}", solutionPath);
            var solution = await workspace.OpenSolutionAsync(solutionPath, progress);
            
            // Track the loaded path
            if (_workspaces.TryGetValue(workspaceId, out var info))
            {
                info.LoadedPath = solutionPath;
            }
            
            _logger.LogInformation("Solution loaded successfully. Projects: {Count}", solution.ProjectIds.Count);
            foreach (var project in solution.Projects)
            {
                _logger.LogDebug("  - {ProjectName} ({Language})", project.Name, project.Language);
            }
            
            return solution;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load solution: {Path}", solutionPath);
            return null;
        }
    }

    public async Task<Project?> LoadProjectAsync(string projectPath, string? workspaceId = null, IProgress<Microsoft.CodeAnalysis.MSBuild.ProjectLoadProgress>? progress = null)
    {
        try
        {
            if (!File.Exists(projectPath))
            {
                _logger.LogError("Project file not found: {Path}", projectPath);
                return null;
            }

            workspaceId ??= projectPath;
            var workspace = await GetOrCreateWorkspaceAsync(workspaceId);
            
            _logger.LogInformation("Loading project: {Path}", projectPath);
            var project = await workspace.OpenProjectAsync(projectPath, progress);
            
            // Track the loaded path
            if (_workspaces.TryGetValue(workspaceId, out var info))
            {
                info.LoadedPath = projectPath;
            }
            
            _logger.LogInformation("Project loaded successfully: {Name}", project.Name);
            return project;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load project: {Path}", projectPath);
            return null;
        }
    }

    public bool TryGetWorkspace(string workspaceId, out MSBuildWorkspace? workspace)
    {
        if (_workspaces.TryGetValue(workspaceId, out var info))
        {
            info.RecordAccess();
            workspace = info.Workspace;
            return true;
        }
        
        workspace = null;
        return false;
    }

    public void CloseWorkspace(string workspaceId)
    {
        if (_workspaces.TryRemove(workspaceId, out var info))
        {
            _logger.LogInformation("Closing workspace: {WorkspaceId} (Path: {Path}, IdleTime: {IdleTime})", 
                workspaceId, info.LoadedPath, info.IdleTime);
            info.Workspace.CloseSolution();
            info.Workspace.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _idleCheckTimer?.Dispose();
        
        foreach (var kvp in _workspaces)
        {
            try
            {
                kvp.Value.Workspace.CloseSolution();
                kvp.Value.Workspace.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing workspace: {WorkspaceId}", kvp.Key);
            }
        }
        _workspaces.Clear();
    }

    private void CheckIdleWorkspaces(object? state)
    {
        if (_disposed) return;
        
        try
        {
            var idleWorkspaces = _workspaces
                .Where(kvp => kvp.Value.IdleTime > _config.WorkspaceIdleTimeout)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var workspaceId in idleWorkspaces)
            {
                _logger.LogInformation("Evicting idle workspace: {WorkspaceId}", workspaceId);
                CloseWorkspace(workspaceId);
            }

            if (idleWorkspaces.Any())
            {
                _logger.LogInformation("Evicted {Count} idle workspaces", idleWorkspaces.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking idle workspaces");
        }
    }

    private bool TryEvictIdleWorkspace()
    {
        var oldestIdle = _workspaces
            .OrderBy(kvp => kvp.Value.LastAccessedAt)
            .FirstOrDefault();

        if (oldestIdle.Key != null)
        {
            _logger.LogInformation("Evicting least recently used workspace: {WorkspaceId} (idle for {IdleTime})",
                oldestIdle.Key, oldestIdle.Value.IdleTime);
            CloseWorkspace(oldestIdle.Key);
            return true;
        }

        return false;
    }

    private bool IsUnderMemoryPressure()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var memoryMB = process.WorkingSet64 / (1024 * 1024);
        
        if (memoryMB > _config.MemoryPressureThresholdMB)
        {
            _logger.LogWarning("Memory pressure detected: {CurrentMB}MB > {ThresholdMB}MB",
                memoryMB, _config.MemoryPressureThresholdMB);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets statistics about currently loaded workspaces
    /// </summary>
    public WorkspaceStatistics GetStatistics()
    {
        return new WorkspaceStatistics
        {
            TotalWorkspaces = _workspaces.Count,
            MaxWorkspaces = _config.MaxConcurrentWorkspaces,
            OldestIdleTime = _workspaces.Any() 
                ? _workspaces.Max(kvp => kvp.Value.IdleTime) 
                : TimeSpan.Zero,
            TotalAccessCount = _workspaces.Sum(kvp => kvp.Value.AccessCount),
            WorkspaceDetails = _workspaces.Select(kvp => new WorkspaceDetail
            {
                WorkspaceId = kvp.Key,
                LoadedPath = kvp.Value.LoadedPath,
                CreatedAt = kvp.Value.CreatedAt,
                LastAccessedAt = kvp.Value.LastAccessedAt,
                AccessCount = kvp.Value.AccessCount,
                IdleTime = kvp.Value.IdleTime
            }).ToList()
        };
    }
}

public class WorkspaceStatistics
{
    public int TotalWorkspaces { get; set; }
    public int MaxWorkspaces { get; set; }
    public TimeSpan OldestIdleTime { get; set; }
    public int TotalAccessCount { get; set; }
    public List<WorkspaceDetail> WorkspaceDetails { get; set; } = new();
}

public class WorkspaceDetail
{
    public required string WorkspaceId { get; set; }
    public string? LoadedPath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastAccessedAt { get; set; }
    public int AccessCount { get; set; }
    public TimeSpan IdleTime { get; set; }
}