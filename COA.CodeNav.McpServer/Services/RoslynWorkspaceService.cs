using COA.CodeNav.McpServer.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace COA.CodeNav.McpServer.Services;

public class RoslynWorkspaceService : IDisposable
{
    private readonly ILogger<RoslynWorkspaceService> _logger;
    private readonly MSBuildWorkspaceManager _workspaceManager;
    private readonly ConcurrentDictionary<string, WorkspaceInfo> _activeWorkspaces = new();
    private readonly ConcurrentDictionary<string, Document> _openDocuments = new();

    public RoslynWorkspaceService(ILogger<RoslynWorkspaceService> logger, MSBuildWorkspaceManager workspaceManager)
    {
        _logger = logger;
        _workspaceManager = workspaceManager;
    }

    public async Task<WorkspaceInfo?> LoadSolutionAsync(string solutionPath)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(solutionPath);
            
            // Check if already loaded
            if (_activeWorkspaces.TryGetValue(normalizedPath, out var existingInfo))
            {
                _logger.LogInformation("Solution already loaded: {Path}", normalizedPath);
                return existingInfo;
            }

            var solution = await _workspaceManager.LoadSolutionAsync(normalizedPath);
            if (solution == null)
            {
                return null;
            }

            var info = new WorkspaceInfo
            {
                Id = normalizedPath,
                Type = WorkspaceType.Solution,
                Path = normalizedPath,
                Solution = solution,
                LoadedAt = DateTime.UtcNow
            };

            _activeWorkspaces[normalizedPath] = info;
            _logger.LogInformation("Solution loaded and registered: {Path}", normalizedPath);
            
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load solution: {Path}", solutionPath);
            return null;
        }
    }

    public async Task<WorkspaceInfo?> LoadProjectAsync(string projectPath)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(projectPath);
            
            // Check if already loaded
            if (_activeWorkspaces.TryGetValue(normalizedPath, out var existingInfo))
            {
                _logger.LogInformation("Project already loaded: {Path}", normalizedPath);
                return existingInfo;
            }

            _logger.LogInformation("Attempting to load project through workspace manager: {Path}", normalizedPath);
            var project = await _workspaceManager.LoadProjectAsync(normalizedPath);
            if (project == null)
            {
                _logger.LogError("WorkspaceManager.LoadProjectAsync returned null for: {Path}", normalizedPath);
                return null;
            }

            _logger.LogInformation("Project loaded from workspace manager. Creating WorkspaceInfo for: {Name}", project.Name);
            
            var info = new WorkspaceInfo
            {
                Id = normalizedPath,
                Type = WorkspaceType.Project,
                Path = normalizedPath,
                Solution = project.Solution,
                LoadedAt = DateTime.UtcNow
            };

            _activeWorkspaces[normalizedPath] = info;
            _logger.LogInformation("Project loaded and registered: {Path} with {ProjectCount} projects in solution", 
                normalizedPath, project.Solution.Projects.Count());
            
            // Register documents from the project
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null)
                {
                    var docPath = Path.GetFullPath(doc.FilePath);
                    _openDocuments[docPath] = doc;
                    _logger.LogDebug("Registered document: {DocPath}", docPath);
                }
            }
            
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load project in RoslynWorkspaceService: {Path}. Exception: {ExType}", 
                projectPath, ex.GetType().Name);
            return null;
        }
    }

    public Task<Document?> GetDocumentAsync(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        
        // Check open documents first
        if (_openDocuments.TryGetValue(normalizedPath, out var openDoc))
        {
            return Task.FromResult<Document?>(openDoc);
        }

        // Search in all workspaces
        foreach (var workspace in _activeWorkspaces.Values)
        {
            var documents = workspace.Solution.Projects
                .SelectMany(p => p.Documents)
                .Where(d => d.FilePath != null && Path.GetFullPath(d.FilePath) == normalizedPath);

            var document = documents.FirstOrDefault();
            if (document != null)
            {
                return Task.FromResult<Document?>(document);
            }
        }

        _logger.LogWarning("Document not found in any workspace: {Path}", filePath);
        return Task.FromResult<Document?>(null);
    }

    public async Task<Document?> OpenDocumentAsync(string filePath, string? text = null)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        
        // Find document in workspaces
        var document = await GetDocumentAsync(normalizedPath);
        if (document == null)
        {
            _logger.LogWarning("Cannot open document - not found in workspace: {Path}", filePath);
            return null;
        }

        // Update text if provided
        if (text != null)
        {
            document = document.WithText(Microsoft.CodeAnalysis.Text.SourceText.From(text));
            
            // Update solution
            var workspace = GetWorkspaceForDocument(document);
            if (workspace != null)
            {
                workspace.Solution = document.Project.Solution;
            }
        }

        _openDocuments[normalizedPath] = document;
        _logger.LogInformation("Document opened: {Path}", filePath);
        
        return document;
    }

    public Task<Document?> UpdateDocumentAsync(string filePath, string text)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        
        if (!_openDocuments.TryGetValue(normalizedPath, out var document))
        {
            _logger.LogWarning("Cannot update document - not open: {Path}", filePath);
            return Task.FromResult<Document?>(null);
        }

        // Update document text
        var newDocument = document.WithText(Microsoft.CodeAnalysis.Text.SourceText.From(text));
        
        // Update in workspace
        var workspace = GetWorkspaceForDocument(document);
        if (workspace != null)
        {
            workspace.Solution = newDocument.Project.Solution;
        }

        _openDocuments[normalizedPath] = newDocument;
        _logger.LogDebug("Document updated: {Path}", filePath);
        
        return Task.FromResult<Document?>(newDocument);
    }

    public void CloseDocument(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        
        if (_openDocuments.TryRemove(normalizedPath, out _))
        {
            _logger.LogInformation("Document closed: {Path}", filePath);
        }
    }

    public IEnumerable<WorkspaceInfo> GetActiveWorkspaces()
    {
        return _activeWorkspaces.Values;
    }

    public WorkspaceInfo? GetWorkspaceForPath(string path)
    {
        var normalizedPath = Path.GetFullPath(path);
        
        // Direct match
        if (_activeWorkspaces.TryGetValue(normalizedPath, out var workspace))
        {
            return workspace;
        }

        // Check if path is under any workspace
        foreach (var ws in _activeWorkspaces.Values)
        {
            var wsDir = Path.GetDirectoryName(ws.Path);
            if (wsDir != null && normalizedPath.StartsWith(wsDir, StringComparison.OrdinalIgnoreCase))
            {
                return ws;
            }
        }

        return null;
    }

    private WorkspaceInfo? GetWorkspaceForDocument(Document document)
    {
        var solution = document.Project.Solution;
        return _activeWorkspaces.Values.FirstOrDefault(w => w.Solution == solution);
    }

    public void CloseWorkspace(string workspacePath)
    {
        var normalizedPath = Path.GetFullPath(workspacePath);
        
        if (_activeWorkspaces.TryRemove(normalizedPath, out var workspace))
        {
            // Remove all open documents from this workspace
            var docsToRemove = _openDocuments
                .Where(kvp => kvp.Value.Project.Solution == workspace.Solution)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var docPath in docsToRemove)
            {
                _openDocuments.TryRemove(docPath, out _);
            }

            _workspaceManager.CloseWorkspace(workspace.Id);
            _logger.LogInformation("Workspace closed: {Path}", workspacePath);
        }
    }

    public void Dispose()
    {
        _openDocuments.Clear();
        _activeWorkspaces.Clear();
        _workspaceManager.Dispose();
    }
}

public class WorkspaceInfo
{
    public required string Id { get; set; }
    public required WorkspaceType Type { get; set; }
    public required string Path { get; set; }
    public required Solution Solution { get; set; }
    public DateTime LoadedAt { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public enum WorkspaceType
{
    Solution,
    Project
}