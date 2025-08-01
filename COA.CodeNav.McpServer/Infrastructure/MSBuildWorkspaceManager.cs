using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace COA.CodeNav.McpServer.Infrastructure;

public class MSBuildWorkspaceManager : IDisposable
{
    private readonly ILogger<MSBuildWorkspaceManager> _logger;
    private readonly ConcurrentDictionary<string, MSBuildWorkspace> _workspaces = new();
    private static bool _msbuildRegistered = false;
    private static readonly object _registrationLock = new();

    public MSBuildWorkspaceManager(ILogger<MSBuildWorkspaceManager> logger)
    {
        _logger = logger;
        EnsureMSBuildRegistered();
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
        return await Task.Run(() =>
        {
            return _workspaces.GetOrAdd(workspaceId, id =>
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
                
                return workspace;
            });
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
        return _workspaces.TryGetValue(workspaceId, out workspace);
    }

    public void CloseWorkspace(string workspaceId)
    {
        if (_workspaces.TryRemove(workspaceId, out var workspace))
        {
            _logger.LogInformation("Closing workspace: {WorkspaceId}", workspaceId);
            workspace.CloseSolution();
            workspace.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _workspaces)
        {
            try
            {
                kvp.Value.CloseSolution();
                kvp.Value.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing workspace: {WorkspaceId}", kvp.Key);
            }
        }
        _workspaces.Clear();
    }
}