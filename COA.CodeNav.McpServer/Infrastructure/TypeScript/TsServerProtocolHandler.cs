using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Infrastructure.TypeScript;

/// <summary>
/// Production-quality TypeScript Server Protocol handler.
/// Implements the EXACT protocol used by tsserver, not LSP.
/// 
/// Protocol Details:
/// - Uses newline-delimited JSON (NDJSON) format
/// - Each message is a single line of JSON followed by newline
/// - No Content-Length headers (that's LSP, not TSP)
/// - Requests have seq, type="request", command, and arguments
/// - Responses have seq, type="response", request_seq, success, command, and body
/// - Events have seq, type="event", event, and body
/// </summary>
public sealed class TsServerProtocolHandler : IDisposable
{
    private readonly ILogger<TsServerProtocolHandler> _logger;
    private readonly string _projectRoot;
    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly ConcurrentDictionary<int, PendingRequest> _pendingRequests = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _readTask;
    private int _sequenceNumber;
    private volatile bool _isDisposed;
    
    // Track server state
    private readonly HashSet<string> _openFiles = new();
    private readonly object _openFilesLock = new();
    private bool _serverReady;
    private readonly TaskCompletionSource<bool> _serverReadyTcs = new();

    public bool IsRunning => !_isDisposed && _process != null && !_process.HasExited;

    private TsServerProtocolHandler(
        ILogger<TsServerProtocolHandler> logger,
        string projectRoot,
        Process process)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _projectRoot = projectRoot ?? throw new ArgumentNullException(nameof(projectRoot));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        
        _writer = _process.StandardInput;
        _reader = _process.StandardOutput;
        
        // Start the read loop
        _readTask = Task.Run(ReadLoopAsync, _shutdownCts.Token);
    }

    /// <summary>
    /// Factory method to create and initialize a new TSP handler
    /// </summary>
    public static async Task<TsServerProtocolHandler?> CreateAsync(
        ILogger<TsServerProtocolHandler> logger,
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var tsServerPath = FindTsServerExecutable(logger);
        if (string.IsNullOrEmpty(tsServerPath))
        {
            logger.LogError("Cannot find tsserver.js. Ensure TypeScript is installed (npm install -g typescript)");
            return null;
        }

        logger.LogInformation("Starting tsserver from: {Path}", tsServerPath);

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = BuildTsServerArguments(tsServerPath),
                    WorkingDirectory = projectRoot,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            // Set environment variables for better debugging
            process.StartInfo.Environment["TSS_LOG"] = "-level verbose -file " + Path.Combine(projectRoot, "tsserver.log");
            process.StartInfo.Environment["NODE_NO_WARNINGS"] = "1";

            if (!process.Start())
            {
                logger.LogError("Failed to start tsserver process");
                return null;
            }

            var handler = new TsServerProtocolHandler(logger, projectRoot, process);
            
            // Monitor stderr in background (for debugging)
            _ = Task.Run(async () => await handler.MonitorStderrAsync(process.StandardError));
            
            // Wait for server to be ready
            await handler.WaitForServerReadyAsync(cancellationToken);
            
            return handler;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create TypeScript server handler");
            return null;
        }
    }

    private static string BuildTsServerArguments(string tsServerPath)
    {
        var args = new StringBuilder();
        args.Append($"\"{tsServerPath}\"");
        args.Append(" --cancellationPipeName=*"); // Use auto-generated pipe name
        args.Append(" --locale=en");
        args.Append(" --disableAutomaticTypingAcquisition"); // Prevent automatic @types downloads
        args.Append(" --allowLocalPluginLoads"); // Allow local plugins
        return args.ToString();
    }

    private static string? FindTsServerExecutable(ILogger logger)
    {
        var searchPaths = new List<string>();

        // Check environment variable first
        var tsServerPathEnv = Environment.GetEnvironmentVariable("TSSERVER_PATH");
        if (!string.IsNullOrEmpty(tsServerPathEnv))
        {
            searchPaths.Add(tsServerPathEnv);
        }

        // Try global npm installation paths
        try
        {
            using var npmProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
                    Arguments = "root -g",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            
            npmProcess.Start();
            var npmRoot = npmProcess.StandardOutput.ReadToEnd().Trim();
            if (npmProcess.WaitForExit(5000) && !string.IsNullOrEmpty(npmRoot))
            {
                searchPaths.Add(Path.Combine(npmRoot, "typescript", "lib", "tsserver.js"));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not determine npm global root");
        }

        // Windows-specific paths
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            searchPaths.Add(Path.Combine(appData, "npm", "node_modules", "typescript", "lib", "tsserver.js"));
            
            // Check Program Files
            searchPaths.Add(@"C:\Program Files\nodejs\node_modules\typescript\lib\tsserver.js");
            searchPaths.Add(@"C:\Program Files (x86)\nodejs\node_modules\typescript\lib\tsserver.js");
        }

        // Unix-like paths
        searchPaths.Add("/usr/local/lib/node_modules/typescript/lib/tsserver.js");
        searchPaths.Add("/usr/lib/node_modules/typescript/lib/tsserver.js");
        searchPaths.Add("/opt/homebrew/lib/node_modules/typescript/lib/tsserver.js"); // macOS with Homebrew

        // Local node_modules (relative to current directory)
        searchPaths.Add(Path.Combine(Directory.GetCurrentDirectory(), "node_modules", "typescript", "lib", "tsserver.js"));

        foreach (var path in searchPaths.Where(p => !string.IsNullOrEmpty(p)))
        {
            logger.LogTrace("Checking for tsserver at: {Path}", path);
            if (File.Exists(path))
            {
                logger.LogDebug("Found tsserver at: {Path}", path);
                return path;
            }
        }

        logger.LogError("Could not find tsserver.js in any of the expected locations");
        return null;
    }

    private async Task WaitForServerReadyAsync(CancellationToken cancellationToken)
    {
        // Send a simple request to verify the server is responding
        var configureRequest = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "configure",
            arguments = new
            {
                hostInfo = "COA CodeNav MCP Server",
                preferences = new
                {
                    includeCompletionsForModuleExports = true,
                    includeCompletionsWithInsertText = true,
                    includePackageJsonAutoImports = "auto",
                    allowTextChangesInNewFiles = true,
                    providePrefixAndSuffixTextForRename = true,
                    allowRenameOfImportPath = true,
                    includeAutomaticOptionalChainCompletions = true,
                    generateReturnInDocTemplate = true,
                    includeCompletionsForImportStatements = true,
                    includeCompletionsWithSnippetText = true,
                    includeCompletionsWithClassMemberSnippets = true,
                    includeCompletionsWithObjectLiteralMethodSnippets = true,
                    displayPartsForJSDoc = true
                },
                watchOptions = new
                {
                    watchFile = "UseFsEvents",
                    watchDirectory = "UseFsEvents",
                    fallbackPolling = "DynamicPriority"
                }
            }
        };

        await SendRequestInternalAsync(configureRequest, cancellationToken);
        
        // Wait for ready signal
        await _serverReadyTcs.Task.WaitAsync(cancellationToken);
        
        _logger.LogInformation("TypeScript server is ready");
    }

    private async Task MonitorStderrAsync(StreamReader stderr)
    {
        try
        {
            while (!_shutdownCts.Token.IsCancellationRequested)
            {
                var line = await stderr.ReadLineAsync();
                if (!string.IsNullOrEmpty(line))
                {
                    // Log stderr output at debug level
                    _logger.LogDebug("[tsserver stderr] {Line}", line);
                    
                    // Check for critical errors
                    if (line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError("TypeScript server error: {Line}", line);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_shutdownCts.Token.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error monitoring tsserver stderr");
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_shutdownCts.Token.IsCancellationRequested && !_reader.EndOfStream)
            {
                var line = await _reader.ReadLineAsync();
                if (string.IsNullOrEmpty(line))
                    continue;

                // Check if this is a Content-Length header
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    // Parse content length
                    var lengthStr = line.Substring("Content-Length:".Length).Trim();
                    if (int.TryParse(lengthStr, out var contentLength))
                    {
                        // Read the empty line after the header
                        await _reader.ReadLineAsync();
                        
                        // Read the specified number of bytes
                        var buffer = new char[contentLength];
                        var totalRead = 0;
                        while (totalRead < contentLength)
                        {
                            var read = await _reader.ReadAsync(buffer, totalRead, contentLength - totalRead);
                            if (read == 0)
                                break;
                            totalRead += read;
                        }
                        
                        var jsonContent = new string(buffer, 0, totalRead);
                        _logger.LogTrace("[TSP] <- {Content}", jsonContent);
                        
                        try
                        {
                            using var doc = JsonDocument.Parse(jsonContent);
                            ProcessMessage(doc.RootElement);
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "Failed to parse TSP message: {Content}", jsonContent);
                        }
                    }
                }
                else
                {
                    // Try to parse as JSON directly (in case there are messages without Content-Length)
                    _logger.LogTrace("[TSP] <- {Line}", line);
                    
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        ProcessMessage(doc.RootElement);
                    }
                    catch (JsonException)
                    {
                        // Not JSON, might be other output
                        _logger.LogTrace("Non-JSON output from tsserver: {Line}", line);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_shutdownCts.Token.IsCancellationRequested)
            {
                _logger.LogError(ex, "Fatal error in TSP read loop");
            }
        }
        finally
        {
            // Complete all pending requests with cancellation
            foreach (var pending in _pendingRequests.Values)
            {
                pending.TaskCompletionSource.TrySetCanceled();
            }
        }
    }

    private void ProcessMessage(JsonElement message)
    {
        if (!message.TryGetProperty("type", out var typeProperty))
        {
            _logger.LogWarning("TSP message missing 'type' property");
            return;
        }

        var messageType = typeProperty.GetString();
        
        switch (messageType)
        {
            case "response":
                ProcessResponse(message);
                break;
                
            case "event":
                ProcessEvent(message);
                break;
                
            default:
                _logger.LogDebug("Unknown TSP message type: {Type}", messageType);
                break;
        }
    }

    private void ProcessResponse(JsonElement message)
    {
        if (!message.TryGetProperty("request_seq", out var requestSeqProperty))
        {
            _logger.LogWarning("Response missing request_seq");
            return;
        }

        var requestSeq = requestSeqProperty.GetInt32();
        
        if (_pendingRequests.TryRemove(requestSeq, out var pending))
        {
            pending.TaskCompletionSource.TrySetResult(message);
            
            var command = message.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() : "unknown";
            var success = message.TryGetProperty("success", out var successProp) && successProp.GetBoolean();
            
            _logger.LogDebug("Response received: seq={Seq}, command={Command}, success={Success}", 
                requestSeq, command, success);
            
            // If this was the initial configure request, mark server as ready
            if (command == "configure" && !_serverReady)
            {
                _serverReady = true;
                _serverReadyTcs.TrySetResult(true);
            }
        }
        else
        {
            _logger.LogDebug("Received response for unknown request: {Seq}", requestSeq);
        }
    }

    private void ProcessEvent(JsonElement message)
    {
        if (!message.TryGetProperty("event", out var eventProperty))
        {
            _logger.LogWarning("Event message missing 'event' property");
            return;
        }

        var eventName = eventProperty.GetString();
        _logger.LogTrace("Event received: {Event}", eventName);

        // Handle specific events
        switch (eventName)
        {
            case "requestCompleted":
                // A request has completed - this is informational
                break;
                
            case "projectLoadingStart":
            case "projectLoadingFinish":
                _logger.LogInformation("Project loading event: {Event}", eventName);
                break;
                
            case "semanticDiag":
            case "syntaxDiag":
            case "suggestionDiag":
                // Diagnostic events - could be captured if needed
                break;
                
            case "typingsInstallerPid":
                // Typings installer process ID - informational
                break;
                
            default:
                _logger.LogTrace("Unhandled event: {Event}", eventName);
                break;
        }
    }

    private int GetNextSequence() => Interlocked.Increment(ref _sequenceNumber);

    private async Task<JsonElement?> SendRequestInternalAsync(object request, CancellationToken cancellationToken)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(TsServerProtocolHandler));

        var requestJson = JsonSerializer.Serialize(request);
        _logger.LogTrace("[TSP] -> {Request}", requestJson);

        // Extract sequence number from the request
        using var doc = JsonDocument.Parse(requestJson);
        var seq = doc.RootElement.GetProperty("seq").GetInt32();

        var pendingRequest = new PendingRequest
        {
            Sequence = seq,
            TaskCompletionSource = new TaskCompletionSource<JsonElement>()
        };

        _pendingRequests[seq] = pendingRequest;

        try
        {
            await _writer.WriteLineAsync(requestJson);
            await _writer.FlushAsync();

            // Wait for response with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            return await pendingRequest.TaskCompletionSource.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _pendingRequests.TryRemove(seq, out _);
            _logger.LogWarning("Request {Seq} cancelled or timed out", seq);
            return null;
        }
        catch (Exception ex)
        {
            _pendingRequests.TryRemove(seq, out _);
            _logger.LogError(ex, "Failed to send request {Seq}", seq);
            throw;
        }
    }

    #region Public API Methods

    /// <summary>
    /// Opens a file in the TypeScript server
    /// </summary>
    public async Task<bool> OpenFileAsync(string filePath, string? content = null, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        lock (_openFilesLock)
        {
            if (_openFiles.Contains(normalizedPath))
            {
                _logger.LogTrace("File already open: {Path}", normalizedPath);
                return true;
            }
        }

        content ??= await File.ReadAllTextAsync(filePath, cancellationToken);

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "open",
            arguments = new
            {
                file = normalizedPath,
                fileContent = content,
                projectRootPath = _projectRoot
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        
        if (response != null)
        {
            var success = response.Value.TryGetProperty("success", out var successProp) && 
                         successProp.GetBoolean();
            
            if (success)
            {
                lock (_openFilesLock)
                {
                    _openFiles.Add(normalizedPath);
                }
            }
            
            return success;
        }

        return false;
    }

    /// <summary>
    /// Updates file content in the server
    /// </summary>
    public async Task<bool> UpdateFileAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        // Ensure file is open
        lock (_openFilesLock)
        {
            if (!_openFiles.Contains(normalizedPath))
            {
                _logger.LogWarning("Cannot update file that is not open: {Path}", normalizedPath);
                return false;
            }
        }

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "updateOpen",
            arguments = new
            {
                changedFiles = new[]
                {
                    new
                    {
                        fileName = normalizedPath,
                        textChanges = new[]
                        {
                            new
                            {
                                newText = content,
                                start = new { line = 1, offset = 1 },
                                end = new { line = int.MaxValue, offset = 1 }
                            }
                        }
                    }
                }
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response != null;
    }

    /// <summary>
    /// Gets semantic diagnostics for a file
    /// </summary>
    public async Task<JsonElement?> GetSemanticDiagnosticsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        // Ensure file is open
        if (!await EnsureFileOpenAsync(normalizedPath, cancellationToken))
            return null;

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "semanticDiagnosticsSync",
            arguments = new
            {
                file = normalizedPath,
                includeLinePosition = true
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response?.TryGetProperty("body", out var body) == true ? body : null;
    }

    /// <summary>
    /// Gets syntactic diagnostics for a file
    /// </summary>
    public async Task<JsonElement?> GetSyntacticDiagnosticsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        // Ensure file is open
        if (!await EnsureFileOpenAsync(normalizedPath, cancellationToken))
            return null;

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "syntacticDiagnosticsSync",
            arguments = new
            {
                file = normalizedPath,
                includeLinePosition = true
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response?.TryGetProperty("body", out var body) == true ? body : null;
    }

    /// <summary>
    /// Gets definition locations for a symbol
    /// </summary>
    public async Task<JsonElement?> GetDefinitionAsync(string filePath, int line, int offset, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        _logger.LogDebug("GetDefinitionAsync: file={File}, line={Line}, offset={Offset}", normalizedPath, line, offset);
        
        if (!await EnsureFileOpenAsync(normalizedPath, cancellationToken))
        {
            _logger.LogWarning("Failed to ensure file is open: {File}", normalizedPath);
            return null;
        }

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "definition",
            arguments = new
            {
                file = normalizedPath,
                line,
                offset
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        
        if (response?.TryGetProperty("body", out var body) == true)
        {
            _logger.LogInformation("GetDefinition response body: {Body}", body.ToString());
            return body;
        }
        
        _logger.LogWarning("GetDefinition returned no body. Response: {Response}", response?.ToString());
        return null;
    }

    /// <summary>
    /// Gets all references to a symbol
    /// </summary>
    public async Task<JsonElement?> GetReferencesAsync(string filePath, int line, int offset, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        if (!await EnsureFileOpenAsync(normalizedPath, cancellationToken))
            return null;

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "references",
            arguments = new
            {
                file = normalizedPath,
                line,
                offset
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response?.TryGetProperty("body", out var body) == true ? body : null;
    }

    /// <summary>
    /// Gets hover information (quick info) for a position
    /// </summary>
    public async Task<JsonElement?> GetQuickInfoAsync(string filePath, int line, int offset, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        if (!await EnsureFileOpenAsync(normalizedPath, cancellationToken))
            return null;

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "quickinfo",
            arguments = new
            {
                file = normalizedPath,
                line,
                offset
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        
        if (response != null)
        {
            // Check if the request was successful
            var success = response.Value.TryGetProperty("success", out var successProp) && 
                         successProp.GetBoolean();
            
            if (success && response.Value.TryGetProperty("body", out var body))
            {
                return body;
            }
        }
        
        return null;
    }

    /// <summary>
    /// Gets rename locations for a symbol
    /// </summary>
    public async Task<JsonElement?> GetRenameLocationsAsync(string filePath, int line, int offset, bool findInComments = false, bool findInStrings = false, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        if (!await EnsureFileOpenAsync(normalizedPath, cancellationToken))
            return null;

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "rename",
            arguments = new
            {
                file = normalizedPath,
                line,
                offset,
                findInComments,
                findInStrings
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response?.TryGetProperty("body", out var body) == true ? body : null;
    }

    /// <summary>
    /// Gets navigation tree for a file (outline)
    /// </summary>
    public async Task<JsonElement?> GetNavigationTreeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        if (!await EnsureFileOpenAsync(normalizedPath, cancellationToken))
            return null;

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "navtree",
            arguments = new
            {
                file = normalizedPath
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response?.TryGetProperty("body", out var body) == true ? body : null;
    }

    /// <summary>
    /// Gets navigation items matching a search pattern (navto command)
    /// </summary>
    public async Task<JsonElement?> GetNavToAsync(string searchValue, string? filePath = null, int maxResultCount = 256, CancellationToken cancellationToken = default)
    {
        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "navto",
            arguments = new
            {
                searchValue = searchValue,
                file = filePath != null ? NormalizePath(filePath) : null,
                maxResultCount = maxResultCount
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response?.TryGetProperty("body", out var body) == true ? body : null;
    }

    /// <summary>
    /// Gets implementation locations for a symbol at a given position
    /// </summary>
    public async Task<JsonElement?> GetImplementationAsync(string filePath, int line, int offset, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        if (!await EnsureFileOpenAsync(normalizedPath, cancellationToken))
            return null;

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "implementation",
            arguments = new
            {
                file = normalizedPath,
                line = line,
                offset = offset
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response?.TryGetProperty("body", out var body) == true ? body : null;
    }

    /// <summary>
    /// Reloads TypeScript projects
    /// </summary>
    public async Task<bool> ReloadProjectsAsync(CancellationToken cancellationToken = default)
    {
        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "reloadProjects"
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response != null;
    }

    /// <summary>
    /// Organizes imports for a file
    /// </summary>
    public async Task<JsonElement?> OrganizeImportsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        if (!await EnsureFileOpenAsync(normalizedPath, cancellationToken))
            return null;

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "organizeImports",
            arguments = new
            {
                scope = new
                {
                    type = "file",
                    args = new
                    {
                        file = normalizedPath
                    }
                }
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response?.TryGetProperty("body", out var body) == true ? body : null;
    }

    /// <summary>
    /// Gets code fixes available at a position
    /// </summary>
    public async Task<JsonElement?> GetCodeFixesAsync(string filePath, int line, int offset, int? endLine = null, int? endOffset = null, string[]? errorCodes = null, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        if (!await EnsureFileOpenAsync(normalizedPath, cancellationToken))
            return null;

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "getCodeFixes",
            arguments = new
            {
                file = normalizedPath,
                startLine = line,
                startOffset = offset,
                endLine = endLine ?? line,
                endOffset = endOffset ?? offset,
                errorCodes = errorCodes ?? new string[0]
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response?.TryGetProperty("body", out var body) == true ? body : null;
    }

    /// <summary>
    /// Gets applicable refactors at a position or selection
    /// </summary>
    public async Task<JsonElement?> GetApplicableRefactorsAsync(string filePath, int line, int offset, int? endLine = null, int? endOffset = null, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        if (!await EnsureFileOpenAsync(normalizedPath, cancellationToken))
            return null;

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "getApplicableRefactors",
            arguments = new
            {
                file = normalizedPath,
                startLine = line,
                startOffset = offset,
                endLine = endLine ?? line,
                endOffset = endOffset ?? offset
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response?.TryGetProperty("body", out var body) == true ? body : null;
    }

    /// <summary>
    /// Gets edits for a refactor
    /// </summary>
    public async Task<JsonElement?> GetEditsForRefactorAsync(string filePath, int line, int offset, int? endLine, int? endOffset, string refactor, string action, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(filePath);
        
        if (!await EnsureFileOpenAsync(normalizedPath, cancellationToken))
            return null;

        var request = new
        {
            seq = GetNextSequence(),
            type = "request",
            command = "getEditsForRefactor",
            arguments = new
            {
                file = normalizedPath,
                startLine = line,
                startOffset = offset,
                endLine = endLine ?? line,
                endOffset = endOffset ?? offset,
                refactor,
                action
            }
        };

        var response = await SendRequestInternalAsync(request, cancellationToken);
        return response?.TryGetProperty("body", out var body) == true ? body : null;
    }

    #endregion

    #region Helper Methods

    private async Task<bool> EnsureFileOpenAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        lock (_openFilesLock)
        {
            if (_openFiles.Contains(normalizedPath))
                return true;
        }

        return await OpenFileAsync(normalizedPath, null, cancellationToken);
    }

    private static string NormalizePath(string path)
    {
        // TypeScript server expects forward slashes
        return Path.GetFullPath(path).Replace('\\', '/');
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        try
        {
            // Send exit command
            if (IsRunning)
            {
                var exitRequest = new
                {
                    seq = GetNextSequence(),
                    type = "request",
                    command = "exit"
                };

                var json = JsonSerializer.Serialize(exitRequest);
                _writer.WriteLine(json);
                _writer.Flush();
            }

            // Signal shutdown
            _shutdownCts.Cancel();

            // Wait briefly for graceful shutdown
            if (!_process.WaitForExit(2000))
            {
                _logger.LogWarning("TypeScript server did not exit gracefully, forcing termination");
                _process.Kill();
            }

            // Wait for read task
            try
            {
                _readTask.Wait(1000);
            }
            catch { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during TypeScript server disposal");
        }
        finally
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _process?.Dispose();
            _shutdownCts?.Dispose();
            _serverReadyTcs?.TrySetCanceled();
        }
    }

    #endregion

    private class PendingRequest
    {
        public int Sequence { get; init; }
        public TaskCompletionSource<JsonElement> TaskCompletionSource { get; init; } = new();
    }
}