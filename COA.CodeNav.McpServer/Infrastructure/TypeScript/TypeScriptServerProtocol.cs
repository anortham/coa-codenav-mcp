using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Infrastructure.TypeScript;

/// <summary>
/// Implements the TypeScript Server Protocol (TSP) for communication with tsserver.
/// This is NOT the Language Server Protocol (LSP), but TypeScript's specific protocol.
/// </summary>
public class TypeScriptServerProtocol : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _workspaceId;
    private readonly string _projectPath;
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private StreamReader? _stderr;
    private readonly ConcurrentDictionary<int, PendingRequest> _pendingRequests = new();
    private int _sequenceNumber;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _outputReaderTask;
    private Task? _errorReaderTask;
    private readonly SemaphoreSlim _requestSemaphore = new(1, 1);
    private bool _isInitialized;
    private readonly StringBuilder _messageBuffer = new();
    private readonly List<string> _openFiles = new();

    public bool IsRunning => _process != null && !_process.HasExited;
    public string WorkspaceId => _workspaceId;
    public string ProjectPath => _projectPath;

    public TypeScriptServerProtocol(string workspaceId, string projectPath, ILogger logger)
    {
        _workspaceId = workspaceId;
        _projectPath = projectPath;
        _logger = logger;
    }

    /// <summary>
    /// Starts the TypeScript language server process
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (IsRunning)
            return true;

        try
        {
            _cancellationTokenSource = new CancellationTokenSource();

            // Find tsserver.js - it's typically in node_modules/typescript/lib/
            var tsServerPath = FindTsServerPath();
            if (string.IsNullOrEmpty(tsServerPath))
            {
                _logger.LogError("Could not find tsserver.js. Ensure TypeScript is installed.");
                return false;
            }

            _logger.LogInformation("Starting tsserver from: {Path}", tsServerPath);

            // Start tsserver process with proper arguments
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"\"{tsServerPath}\" --cancellationPipeName=* --locale=en --disableAutomaticTypingAcquisition",
                    WorkingDirectory = _projectPath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    Environment =
                    {
                        ["TSS_LOG"] = "-level verbose -file tsserver.log", // Enable logging for debugging
                        ["NODE_NO_WARNINGS"] = "1"
                    }
                }
            };

            _process.Start();
            _stdin = _process.StandardInput;
            _stdout = _process.StandardOutput;
            _stderr = _process.StandardError;

            // Start reading output and error streams
            _outputReaderTask = Task.Run(() => ReadOutputAsync(_cancellationTokenSource.Token));
            _errorReaderTask = Task.Run(() => ReadErrorAsync(_cancellationTokenSource.Token));

            // Wait a bit for the server to start
            await Task.Delay(500);

            if (_process.HasExited)
            {
                _logger.LogError("tsserver process exited immediately. Exit code: {ExitCode}", _process.ExitCode);
                return false;
            }

            _logger.LogInformation("TypeScript server started for workspace {WorkspaceId}", _workspaceId);

            // Initialize the server with a configure request
            await ConfigureServerAsync();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start TypeScript server for workspace {WorkspaceId}", _workspaceId);
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// Finds the path to tsserver.js
    /// </summary>
    private string? FindTsServerPath()
    {
        var searchPaths = new List<string>();

        // Try to find TypeScript in common locations
        // 1. Local node_modules
        searchPaths.Add(Path.Combine(_projectPath, "node_modules", "typescript", "lib", "tsserver.js"));
        
        // 2. Parent directories (monorepo support)
        var currentDir = new DirectoryInfo(_projectPath);
        while (currentDir.Parent != null)
        {
            searchPaths.Add(Path.Combine(currentDir.Parent.FullName, "node_modules", "typescript", "lib", "tsserver.js"));
            currentDir = currentDir.Parent;
        }

        // 3. Global npm installation
        var npmPrefix = GetNpmPrefix();
        if (!string.IsNullOrEmpty(npmPrefix))
        {
            searchPaths.Add(Path.Combine(npmPrefix, "node_modules", "typescript", "lib", "tsserver.js"));
            searchPaths.Add(Path.Combine(npmPrefix, "lib", "node_modules", "typescript", "lib", "tsserver.js"));
        }

        // 4. Windows-specific global location
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            searchPaths.Add(Path.Combine(appData, "npm", "node_modules", "typescript", "lib", "tsserver.js"));
        }

        // 5. Common Unix locations
        searchPaths.Add("/usr/local/lib/node_modules/typescript/lib/tsserver.js");
        searchPaths.Add("/usr/lib/node_modules/typescript/lib/tsserver.js");

        // Find the first existing path
        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                _logger.LogDebug("Found tsserver at: {Path}", path);
                return path;
            }
        }

        _logger.LogWarning("Could not find tsserver.js. Searched paths: {Paths}", string.Join(", ", searchPaths));
        return null;
    }

    /// <summary>
    /// Gets the npm prefix directory
    /// </summary>
    private string? GetNpmPrefix()
    {
        try
        {
            var npmProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
                    Arguments = "prefix -g",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            npmProcess.Start();
            var prefix = npmProcess.StandardOutput.ReadToEnd().Trim();
            npmProcess.WaitForExit(5000);

            return string.IsNullOrEmpty(prefix) ? null : prefix;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not get npm prefix");
            return null;
        }
    }

    /// <summary>
    /// Configures the TypeScript server with initial settings
    /// </summary>
    private async Task ConfigureServerAsync()
    {
        // Send configure request
        var configureRequest = new TspRequest
        {
            Command = "configure",
            Arguments = new
            {
                hostInfo = "MCP Server",
                preferences = new
                {
                    includeCompletionsForModuleExports = true,
                    includeCompletionsWithInsertText = true,
                    allowTextChangesInNewFiles = true,
                    providePrefixAndSuffixTextForRename = true
                }
            }
        };

        await SendRequestAsync(configureRequest);
        _isInitialized = true;
    }

    /// <summary>
    /// Opens a file in the TypeScript server
    /// </summary>
    public async Task<TspResponse?> OpenFileAsync(string filePath, string? content = null)
    {
        if (!_isInitialized)
        {
            _logger.LogWarning("Server not initialized");
            return null;
        }

        var normalizedPath = Path.GetFullPath(filePath).Replace('\\', '/');
        
        if (_openFiles.Contains(normalizedPath))
        {
            _logger.LogDebug("File already open: {FilePath}", normalizedPath);
            return null;
        }

        var openRequest = new TspRequest
        {
            Command = "open",
            Arguments = new
            {
                file = normalizedPath,
                fileContent = content ?? await File.ReadAllTextAsync(filePath),
                projectRootPath = _projectPath
            }
        };

        var response = await SendRequestAsync(openRequest);
        if (response?.Success == true)
        {
            _openFiles.Add(normalizedPath);
        }

        return response;
    }

    /// <summary>
    /// Closes a file in the TypeScript server
    /// </summary>
    public async Task<TspResponse?> CloseFileAsync(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath).Replace('\\', '/');
        
        if (!_openFiles.Contains(normalizedPath))
        {
            return null;
        }

        var closeRequest = new TspRequest
        {
            Command = "close",
            Arguments = new
            {
                file = normalizedPath
            }
        };

        var response = await SendRequestAsync(closeRequest);
        if (response?.Success == true)
        {
            _openFiles.Remove(normalizedPath);
        }

        return response;
    }

    /// <summary>
    /// Gets semantic diagnostics for a file
    /// </summary>
    public async Task<TspResponse?> GetSemanticDiagnosticsAsync(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath).Replace('\\', '/');
        
        // Ensure file is open
        if (!_openFiles.Contains(normalizedPath))
        {
            await OpenFileAsync(filePath);
        }

        var request = new TspRequest
        {
            Command = "semanticDiagnosticsSync",
            Arguments = new
            {
                file = normalizedPath
            }
        };

        return await SendRequestAsync(request);
    }

    /// <summary>
    /// Gets syntax diagnostics for a file
    /// </summary>
    public async Task<TspResponse?> GetSyntacticDiagnosticsAsync(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath).Replace('\\', '/');
        
        // Ensure file is open
        if (!_openFiles.Contains(normalizedPath))
        {
            await OpenFileAsync(filePath);
        }

        var request = new TspRequest
        {
            Command = "syntacticDiagnosticsSync",
            Arguments = new
            {
                file = normalizedPath
            }
        };

        return await SendRequestAsync(request);
    }

    /// <summary>
    /// Gets definition location for a symbol
    /// </summary>
    public async Task<TspResponse?> GetDefinitionAsync(string filePath, int line, int offset)
    {
        var normalizedPath = Path.GetFullPath(filePath).Replace('\\', '/');
        
        // Ensure file is open
        if (!_openFiles.Contains(normalizedPath))
        {
            await OpenFileAsync(filePath);
        }

        var request = new TspRequest
        {
            Command = "definition",
            Arguments = new
            {
                file = normalizedPath,
                line = line,
                offset = offset
            }
        };

        return await SendRequestAsync(request);
    }

    /// <summary>
    /// Gets all references to a symbol
    /// </summary>
    public async Task<TspResponse?> GetReferencesAsync(string filePath, int line, int offset)
    {
        var normalizedPath = Path.GetFullPath(filePath).Replace('\\', '/');
        
        // Ensure file is open
        if (!_openFiles.Contains(normalizedPath))
        {
            await OpenFileAsync(filePath);
        }

        var request = new TspRequest
        {
            Command = "references",
            Arguments = new
            {
                file = normalizedPath,
                line = line,
                offset = offset
            }
        };

        return await SendRequestAsync(request);
    }

    /// <summary>
    /// Gets hover information for a position
    /// </summary>
    public async Task<TspResponse?> GetQuickInfoAsync(string filePath, int line, int offset)
    {
        var normalizedPath = Path.GetFullPath(filePath).Replace('\\', '/');
        
        // Ensure file is open
        if (!_openFiles.Contains(normalizedPath))
        {
            await OpenFileAsync(filePath);
        }

        var request = new TspRequest
        {
            Command = "quickinfo",
            Arguments = new
            {
                file = normalizedPath,
                line = line,
                offset = offset
            }
        };

        return await SendRequestAsync(request);
    }

    /// <summary>
    /// Performs a rename operation
    /// </summary>
    public async Task<TspResponse?> GetRenameLocationsAsync(string filePath, int line, int offset)
    {
        var normalizedPath = Path.GetFullPath(filePath).Replace('\\', '/');
        
        // Ensure file is open
        if (!_openFiles.Contains(normalizedPath))
        {
            await OpenFileAsync(filePath);
        }

        var request = new TspRequest
        {
            Command = "rename",
            Arguments = new
            {
                file = normalizedPath,
                line = line,
                offset = offset,
                findInComments = false,
                findInStrings = false
            }
        };

        return await SendRequestAsync(request);
    }

    /// <summary>
    /// Sends a request to the TypeScript server and waits for response
    /// </summary>
    private async Task<TspResponse?> SendRequestAsync(TspRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsRunning || _stdin == null)
        {
            _logger.LogWarning("Cannot send request - server not running");
            return null;
        }

        await _requestSemaphore.WaitAsync(cancellationToken);
        try
        {
            var seq = Interlocked.Increment(ref _sequenceNumber);
            request.Seq = seq;
            request.Type = "request";

            var tcs = new TaskCompletionSource<TspResponse>();
            var pendingRequest = new PendingRequest
            {
                Sequence = seq,
                Command = request.Command,
                CompletionSource = tcs,
                Timestamp = DateTime.UtcNow
            };

            _pendingRequests[seq] = pendingRequest;

            // Serialize request
            var requestJson = JsonSerializer.Serialize(request, TspJsonOptions.Default);
            
            // TSP uses newline-delimited JSON (NDJSON), NOT Content-Length headers like LSP
            _logger.LogDebug("Sending TSP request: {Request}", requestJson);
            
            await _stdin.WriteLineAsync(requestJson);
            await _stdin.FlushAsync();

            // Wait for response with timeout
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                
                try
                {
                    return await tcs.Task.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Request {Seq} timed out for command {Command}", seq, request.Command);
                    _pendingRequests.TryRemove(seq, out _);
                    return null;
                }
            }
        }
        finally
        {
            _requestSemaphore.Release();
        }
    }

    /// <summary>
    /// Reads output from the TypeScript server
    /// </summary>
    private async Task ReadOutputAsync(CancellationToken cancellationToken)
    {
        if (_stdout == null)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !_stdout.EndOfStream)
            {
                var line = await _stdout.ReadLineAsync();
                if (string.IsNullOrEmpty(line))
                    continue;

                _logger.LogTrace("TSP output: {Line}", line);

                try
                {
                    // TSP uses newline-delimited JSON
                    var response = JsonSerializer.Deserialize<TspResponse>(line, TspJsonOptions.Default);
                    if (response != null)
                    {
                        ProcessResponse(response);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug("Non-JSON output from tsserver: {Line}, Error: {Error}", line, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading TypeScript server output");
        }
    }

    /// <summary>
    /// Reads error output from the TypeScript server
    /// </summary>
    private async Task ReadErrorAsync(CancellationToken cancellationToken)
    {
        if (_stderr == null)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested && !_stderr.EndOfStream)
            {
                var line = await _stderr.ReadLineAsync();
                if (!string.IsNullOrEmpty(line))
                {
                    _logger.LogDebug("TSP stderr: {Line}", line);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading TypeScript server error output");
        }
    }

    /// <summary>
    /// Processes a response from the TypeScript server
    /// </summary>
    private void ProcessResponse(TspResponse response)
    {
        // Handle different response types
        switch (response.Type)
        {
            case "response":
                HandleCommandResponse(response);
                break;
                
            case "event":
                HandleEvent(response);
                break;
                
            default:
                _logger.LogDebug("Unknown response type: {Type}", response.Type);
                break;
        }
    }

    /// <summary>
    /// Handles a command response
    /// </summary>
    private void HandleCommandResponse(TspResponse response)
    {
        if (response.RequestSeq.HasValue && 
            _pendingRequests.TryRemove(response.RequestSeq.Value, out var pendingRequest))
        {
            response.Success = response.Success ?? true;
            pendingRequest.CompletionSource.TrySetResult(response);
            
            _logger.LogDebug("Received response for request {Seq}, Command: {Command}, Success: {Success}", 
                response.RequestSeq, response.Command, response.Success);
        }
        else
        {
            _logger.LogDebug("Received response without matching request: {Seq}", response.RequestSeq);
        }
    }

    /// <summary>
    /// Handles server events
    /// </summary>
    private void HandleEvent(TspResponse response)
    {
        _logger.LogDebug("Received event: {Event}", response.Event);
        
        // Handle specific events if needed
        switch (response.Event)
        {
            case "semanticDiag":
            case "syntaxDiag":
            case "suggestionDiag":
                // Diagnostic events - could be processed if needed
                break;
                
            case "projectLoadingStart":
            case "projectLoadingFinish":
                // Project loading events
                _logger.LogInformation("Project loading event: {Event}", response.Event);
                break;
        }
    }

    /// <summary>
    /// Stops the TypeScript server
    /// </summary>
    public async Task StopAsync()
    {
        if (!IsRunning)
            return;

        try
        {
            _cancellationTokenSource?.Cancel();

            // Close all open files
            foreach (var file in _openFiles.ToList())
            {
                await CloseFileAsync(file);
            }

            // Send exit command
            if (_stdin != null)
            {
                var exitRequest = new TspRequest
                {
                    Seq = Interlocked.Increment(ref _sequenceNumber),
                    Type = "request",
                    Command = "exit"
                };

                var exitJson = JsonSerializer.Serialize(exitRequest, TspJsonOptions.Default);
                await _stdin.WriteLineAsync(exitJson);
                await _stdin.FlushAsync();
            }

            // Wait for process to exit
            if (_process != null && !_process.WaitForExit(5000))
            {
                _logger.LogWarning("TypeScript server did not exit gracefully, killing process");
                _process.Kill();
            }

            // Wait for reader tasks
            if (_outputReaderTask != null)
            {
                await _outputReaderTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            if (_errorReaderTask != null)
            {
                await _errorReaderTask.WaitAsync(TimeSpan.FromSeconds(2));
            }

            _logger.LogInformation("TypeScript server stopped for workspace {WorkspaceId}", _workspaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping TypeScript server for workspace {WorkspaceId}", _workspaceId);
        }
        finally
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        // Cancel any pending requests
        foreach (var pending in _pendingRequests.Values)
        {
            pending.CompletionSource.TrySetCanceled();
        }
        _pendingRequests.Clear();

        _stdin?.Dispose();
        _stdout?.Dispose();
        _stderr?.Dispose();
        _process?.Dispose();
        _cancellationTokenSource?.Dispose();

        _stdin = null;
        _stdout = null;
        _stderr = null;
        _process = null;
        _cancellationTokenSource = null;
        _isInitialized = false;
        _openFiles.Clear();
    }

    public void Dispose()
    {
        StopAsync().Wait(5000);
        Cleanup();
        _requestSemaphore?.Dispose();
    }

    /// <summary>
    /// Pending request information
    /// </summary>
    private class PendingRequest
    {
        public int Sequence { get; set; }
        public string Command { get; set; } = "";
        public TaskCompletionSource<TspResponse> CompletionSource { get; set; } = new();
        public DateTime Timestamp { get; set; }
    }
}

/// <summary>
/// TypeScript Server Protocol request
/// </summary>
public class TspRequest
{
    [JsonPropertyName("seq")]
    public int Seq { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "request";

    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("arguments")]
    public object? Arguments { get; set; }
}

/// <summary>
/// TypeScript Server Protocol response
/// </summary>
public class TspResponse
{
    [JsonPropertyName("seq")]
    public int Seq { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("request_seq")]
    public int? RequestSeq { get; set; }

    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }

    [JsonPropertyName("event")]
    public string? Event { get; set; }
}

/// <summary>
/// JSON serialization options for TSP
/// </summary>
public static class TspJsonOptions
{
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}