using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Services.TypeScript;
using COA.CodeNav.McpServer.Configuration;
using System.Diagnostics;

namespace COA.CodeNav.McpServer.Services;

/// <summary>
/// Background service that automatically loads C# solutions and TypeScript workspaces
/// during server startup to improve user experience.
/// </summary>
public class StartupWorkspaceLoader : BackgroundService
{
    private readonly ILogger<StartupWorkspaceLoader> _logger;
    private readonly MSBuildWorkspaceManager _workspaceManager;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly TypeScriptWorkspaceService _typeScriptWorkspaceService;
    private readonly StartupConfiguration _config;
    private readonly string _workingDirectory;

    public StartupWorkspaceLoader(
        ILogger<StartupWorkspaceLoader> logger,
        MSBuildWorkspaceManager workspaceManager,
        RoslynWorkspaceService workspaceService,
        TypeScriptWorkspaceService typeScriptWorkspaceService,
        IOptions<StartupConfiguration> config)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
        _workspaceService = workspaceService;
        _typeScriptWorkspaceService = typeScriptWorkspaceService;
        _config = config.Value;
        _workingDirectory = Directory.GetCurrentDirectory();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Check if auto-loading is enabled
            if (!_config.AutoLoadSolution)
            {
                _logger.LogDebug("AutoLoadSolution is disabled in configuration");
                return;
            }

            _logger.LogInformation("Starting background workspace loading from {WorkingDirectory}", _workingDirectory);
            
            var stopwatch = Stopwatch.StartNew();
            
            // Detect and load C# solutions/projects
            var csharpLoaded = await LoadCSharpSolutionAsync(stoppingToken);
            
            // Detect and load TypeScript projects
            var typescriptLoaded = await LoadTypeScriptProjectAsync(stoppingToken);
            
            stopwatch.Stop();
            
            if (csharpLoaded || typescriptLoaded)
            {
                var loadedTypes = new List<string>();
                if (csharpLoaded) loadedTypes.Add("C#");
                if (typescriptLoaded) loadedTypes.Add("TypeScript");
                
                _logger.LogInformation("Background workspace loading completed in {ElapsedMs}ms. Loaded: {LoadedTypes}", 
                    stopwatch.ElapsedMilliseconds, string.Join(", ", loadedTypes));
            }
            else
            {
                var message = "No C# solutions or TypeScript projects found for background loading";
                if (_config.RequireSolution)
                {
                    _logger.LogError(message);
                    throw new InvalidOperationException($"{message} and RequireSolution is enabled");
                }
                else
                {
                    _logger.LogDebug(message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Background workspace loading cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background workspace loading failed, but server will continue normally");
        }
    }

    private async Task<bool> LoadCSharpSolutionAsync(CancellationToken cancellationToken)
    {
        try
        {
            string? primarySolution = null;

            // Check if specific solution path is configured
            if (!string.IsNullOrEmpty(_config.SolutionPath))
            {
                var configuredPath = Path.IsPathRooted(_config.SolutionPath) 
                    ? _config.SolutionPath 
                    : Path.Combine(_workingDirectory, _config.SolutionPath);

                if (File.Exists(configuredPath))
                {
                    primarySolution = configuredPath;
                    _logger.LogDebug("Using configured solution path: {SolutionPath}", primarySolution);
                }
                else
                {
                    _logger.LogWarning("Configured solution path not found: {SolutionPath}", configuredPath);
                }
            }

            // If no configured path or file not found, search for solutions
            if (primarySolution == null)
            {
                var solutionFiles = FindSolutionFiles();
                
                if (solutionFiles.Length == 0)
                {
                    _logger.LogDebug("No .sln files found, trying individual projects");
                    return await LoadCSharpProjectAsync(cancellationToken);
                }

                primarySolution = SelectBestSolution(solutionFiles);
            }

            _logger.LogInformation("Auto-loading primary solution: {SolutionPath}", primarySolution);

            // Load the solution using the workspace manager directly
            var solution = await _workspaceManager.LoadSolutionAsync(primarySolution, null, null);

            if (solution == null)
            {
                _logger.LogWarning("Failed to load solution into MSBuild workspace: {SolutionPath}", primarySolution);
                return false;
            }

            // CRITICAL: Register with workspace service so tools can find it
            var workspaceInfo = await _workspaceService.LoadSolutionAsync(primarySolution);
            
            if (workspaceInfo == null)
            {
                _logger.LogWarning("Failed to register solution in workspace service: {SolutionPath}", primarySolution);
                return false;
            }

            var projectCount = solution.Projects.Count();
            var projectNames = solution.Projects.Select(p => p.Name).ToList();
            
            _logger.LogInformation("Successfully loaded solution with {ProjectCount} projects: {ProjectNames}", 
                projectCount, string.Join(", ", projectNames));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception during C# solution auto-loading");
            return false;
        }
    }

    private string[] FindSolutionFiles()
    {
        var solutionFiles = new List<string>();
        var searchOptions = SearchOption.TopDirectoryOnly;

        // Search current directory and up to MaxSearchDepth parent directories
        var currentDir = new DirectoryInfo(_workingDirectory);
        var depth = 0;

        while (currentDir != null && depth <= _config.MaxSearchDepth)
        {
            var solutions = Directory.GetFiles(currentDir.FullName, "*.sln", searchOptions);
            solutionFiles.AddRange(solutions);

            currentDir = currentDir.Parent;
            depth++;
        }

        return solutionFiles.ToArray();
    }

    private string SelectBestSolution(string[] solutionFiles)
    {
        // If preferred solution name is specified, look for it first
        if (!string.IsNullOrEmpty(_config.PreferredSolutionName))
        {
            var preferred = solutionFiles.FirstOrDefault(f => 
                Path.GetFileNameWithoutExtension(f).Equals(_config.PreferredSolutionName, StringComparison.OrdinalIgnoreCase));
            
            if (preferred != null)
            {
                _logger.LogDebug("Selected preferred solution: {SolutionName}", Path.GetFileName(preferred));
                return preferred;
            }
        }

        // Otherwise, pick the best solution (prefer root level, then shortest path)
        var selected = solutionFiles
            .OrderBy(f => Path.GetDirectoryName(f) == _workingDirectory ? 0 : 1) // Root level first
            .ThenBy(f => f.Length) // Then shortest path
            .First();

        _logger.LogDebug("Selected solution: {SolutionName}", Path.GetFileName(selected));
        return selected;
    }

    private async Task<bool> LoadCSharpProjectAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Find project files in current directory
            var projectFiles = Directory.GetFiles(_workingDirectory, "*.csproj", SearchOption.TopDirectoryOnly);
            
            if (projectFiles.Length == 0)
            {
                _logger.LogDebug("No .csproj files found in {WorkingDirectory}", _workingDirectory);
                return false;
            }

            // Pick the first project (or could add logic to select best one)
            var primaryProject = projectFiles.First();
            
            _logger.LogInformation("Auto-loading primary project: {ProjectPath}", primaryProject);

            // Load the project using workspace service directly (simpler than solution loading)
            var workspaceInfo = await _workspaceService.LoadProjectAsync(primaryProject);
            
            if (workspaceInfo == null)
            {
                _logger.LogWarning("Failed to load project into workspace service: {ProjectPath}", primaryProject);
                return false;
            }

            _logger.LogInformation("Successfully loaded project: {ProjectName}", Path.GetFileNameWithoutExtension(primaryProject));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception during C# project auto-loading");
            return false;
        }
    }

    private async Task<bool> LoadTypeScriptProjectAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Find tsconfig.json files in current directory and subdirectories
            var tsConfigFiles = Directory.GetFiles(_workingDirectory, "tsconfig.json", SearchOption.AllDirectories);
            
            if (tsConfigFiles.Length == 0)
            {
                _logger.LogDebug("No tsconfig.json files found in {WorkingDirectory}", _workingDirectory);
                return false;
            }

            // Pick the root-level tsconfig.json if available, otherwise the first one
            var primaryTsConfig = tsConfigFiles
                .OrderBy(f => Path.GetDirectoryName(f) == _workingDirectory ? 0 : 1) // Root level first
                .ThenBy(f => f.Length) // Then shortest path
                .First();
            
            _logger.LogInformation("Auto-loading TypeScript project: {TsConfigPath}", primaryTsConfig);

            // Load the TypeScript project using workspace service
            var result = await _typeScriptWorkspaceService.LoadTsConfigAsync(primaryTsConfig);
            
            if (result == null || !result.Success)
            {
                _logger.LogWarning("Failed to load TypeScript project: {TsConfigPath}", primaryTsConfig);
                return false;
            }

            _logger.LogInformation("Successfully loaded TypeScript project: {ProjectName}", 
                Path.GetFileNameWithoutExtension(Path.GetDirectoryName(primaryTsConfig)));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception during TypeScript project auto-loading");
            return false;
        }
    }
}