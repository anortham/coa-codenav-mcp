using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace COA.CodeNav.McpServer.Services;

public class DocumentService
{
    private readonly ILogger<DocumentService> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly ConcurrentDictionary<string, DocumentState> _documentStates = new();
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _fileWatchers = new();

    public DocumentService(ILogger<DocumentService> logger, RoslynWorkspaceService workspaceService)
    {
        _logger = logger;
        _workspaceService = workspaceService;
    }

    public async Task<DocumentState?> OpenDocumentAsync(string filePath, string? initialText = null, int? version = null)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        
        // Get or create document state
        var state = _documentStates.GetOrAdd(normalizedPath, path => new DocumentState
        {
            FilePath = path,
            Version = version ?? 0,
            IsOpen = true,
            OpenedAt = DateTime.UtcNow
        });

        // Update state
        state.IsOpen = true;
        state.Version = version ?? state.Version;

        // Load or update document in workspace
        var document = await _workspaceService.OpenDocumentAsync(normalizedPath, initialText);
        if (document == null)
        {
            _documentStates.TryRemove(normalizedPath, out _);
            return null;
        }

        state.DocumentId = document.Id;
        
        // Set up file watcher if not already watching
        SetupFileWatcher(normalizedPath);
        
        _logger.LogInformation("Document opened: {Path} (Version: {Version})", filePath, state.Version);
        return state;
    }

    public async Task<DocumentState?> UpdateDocumentAsync(string filePath, string text, int? version = null)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        
        if (!_documentStates.TryGetValue(normalizedPath, out var state))
        {
            _logger.LogWarning("Cannot update document - not tracked: {Path}", filePath);
            return null;
        }

        // Check version for conflict detection
        if (version.HasValue && state.Version != version.Value - 1)
        {
            _logger.LogWarning("Version mismatch for document {Path}. Expected: {Expected}, Got: {Got}", 
                filePath, state.Version + 1, version.Value);
            // Continue anyway, but log the mismatch
        }

        // Update document in workspace
        var document = await _workspaceService.UpdateDocumentAsync(normalizedPath, text);
        if (document == null)
        {
            return null;
        }

        // Update state
        state.Version = version ?? (state.Version + 1);
        state.LastModified = DateTime.UtcNow;
        state.IsDirty = true;
        
        _logger.LogDebug("Document updated: {Path} (Version: {Version})", filePath, state.Version);
        return state;
    }

    public async Task<Document?> GetDocumentAsync(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        
        // First try to get from workspace service regardless of open state
        // Documents don't need to be "open" to be accessed for analysis
        var document = await _workspaceService.GetDocumentAsync(normalizedPath);
        if (document != null)
        {
            return document;
        }
        
        // If not found and we have state, log for debugging
        if (_documentStates.TryGetValue(normalizedPath, out var state))
        {
            _logger.LogDebug("Document state exists but document not found in workspace: {Path}, IsOpen: {IsOpen}", 
                normalizedPath, state.IsOpen);
        }
        
        return null;
    }

    public DocumentState? GetDocumentState(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        return _documentStates.TryGetValue(normalizedPath, out var state) ? state : null;
    }

    public void CloseDocument(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        
        if (_documentStates.TryGetValue(normalizedPath, out var state))
        {
            state.IsOpen = false;
            state.ClosedAt = DateTime.UtcNow;
            
            // Remove file watcher
            if (_fileWatchers.TryRemove(normalizedPath, out var watcher))
            {
                watcher.Dispose();
            }

            _workspaceService.CloseDocument(normalizedPath);
            _logger.LogInformation("Document closed: {Path}", filePath);
        }
    }

    public IEnumerable<DocumentState> GetOpenDocuments()
    {
        return _documentStates.Values.Where(d => d.IsOpen);
    }

    public Task<TextChange[]> GetTextChangesAsync(string filePath, string oldText, string newText)
    {
        var oldSourceText = SourceText.From(oldText);
        var newSourceText = SourceText.From(newText);
        
        var changes = newSourceText.GetTextChanges(oldSourceText);
        return Task.FromResult(changes.ToArray());
    }

    private void SetupFileWatcher(string filePath)
    {
        if (_fileWatchers.ContainsKey(filePath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            var fileName = Path.GetFileName(filePath);
            
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            {
                return;
            }

            var watcher = new FileSystemWatcher(directory)
            {
                Filter = fileName,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
            };

            watcher.Changed += async (sender, e) =>
            {
                if (e.FullPath == filePath && _documentStates.TryGetValue(filePath, out var state))
                {
                    state.HasExternalChanges = true;
                    _logger.LogInformation("External changes detected for document: {Path}", filePath);
                    
                    // Optionally reload the document
                    if (!state.IsDirty)
                    {
                        try
                        {
                            var text = await File.ReadAllTextAsync(filePath);
                            await UpdateDocumentAsync(filePath, text);
                            state.HasExternalChanges = false;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to reload document after external change: {Path}", filePath);
                        }
                    }
                }
            };

            watcher.EnableRaisingEvents = true;
            _fileWatchers[filePath] = watcher;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to setup file watcher for: {Path}", filePath);
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _fileWatchers.Values)
        {
            watcher.Dispose();
        }
        _fileWatchers.Clear();
        _documentStates.Clear();
    }
}

public class DocumentState
{
    public required string FilePath { get; set; }
    public DocumentId? DocumentId { get; set; }
    public int Version { get; set; }
    public bool IsOpen { get; set; }
    public bool IsDirty { get; set; }
    public bool HasExternalChanges { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime? LastModified { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}