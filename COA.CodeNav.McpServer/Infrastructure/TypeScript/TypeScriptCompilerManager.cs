using System.Diagnostics;
using System.Text;
using System.Text.Json;
using COA.CodeNav.McpServer.Constants;
using COA.Mcp.Framework.Models;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Infrastructure.TypeScript;

/// <summary>
/// Manages TypeScript compiler processes and communication
/// </summary>
public class TypeScriptCompilerManager : IDisposable
{
    private readonly ILogger<TypeScriptCompilerManager> _logger;
    private readonly Dictionary<string, TypeScriptServerInstance> _servers = new();
    private readonly SemaphoreSlim _serverLock = new(1, 1);
    private bool _typeScriptAvailable;
    private string? _typeScriptPath;
    private string? _typeScriptVersion;

    public bool IsTypeScriptAvailable => _typeScriptAvailable;
    public string? TypeScriptVersion => _typeScriptVersion;
    public string? TypeScriptPath => _typeScriptPath;

    public TypeScriptCompilerManager(ILogger<TypeScriptCompilerManager> logger)
    {
        _logger = logger;
        DetectTypeScript();
    }

    /// <summary>
    /// Detects if TypeScript is installed and available
    /// </summary>
    private void DetectTypeScript()
    {
        try
        {
            // Try to find tsc in the system
            var tscPath = FindTypeScriptCompiler();
            if (tscPath == null)
            {
                _logger.LogWarning("TypeScript compiler (tsc) not found in system PATH");
                _typeScriptAvailable = false;
                return;
            }

            _typeScriptPath = tscPath;

            // Get TypeScript version
            var versionProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = tscPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            versionProcess.Start();
            var version = versionProcess.StandardOutput.ReadToEnd().Trim();
            versionProcess.WaitForExit(5000);

            if (version.StartsWith("Version"))
            {
                _typeScriptVersion = version;
                _typeScriptAvailable = true;
                _logger.LogInformation("TypeScript detected: {Version} at {Path}", version, tscPath);
            }
            else
            {
                _typeScriptAvailable = false;
                _logger.LogWarning("Could not determine TypeScript version");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting TypeScript");
            _typeScriptAvailable = false;
        }
    }

    /// <summary>
    /// Finds the TypeScript compiler executable
    /// </summary>
    private string? FindTypeScriptCompiler()
    {
        // Check common locations and PATH
        var possiblePaths = new List<string>();

        // Add paths from environment PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            var paths = pathEnv.Split(Path.PathSeparator);
            foreach (var path in paths)
            {
                possiblePaths.Add(Path.Combine(path, "tsc.cmd"));
                possiblePaths.Add(Path.Combine(path, "tsc.exe"));
                possiblePaths.Add(Path.Combine(path, "tsc"));
            }
        }

        // Check npm global installation
        var npmPrefix = GetNpmPrefix();
        if (!string.IsNullOrEmpty(npmPrefix))
        {
            possiblePaths.Add(Path.Combine(npmPrefix, "tsc.cmd"));
            possiblePaths.Add(Path.Combine(npmPrefix, "tsc"));
            possiblePaths.Add(Path.Combine(npmPrefix, "node_modules", ".bin", "tsc.cmd"));
            possiblePaths.Add(Path.Combine(npmPrefix, "node_modules", ".bin", "tsc"));
        }

        // Check common Windows locations
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            possiblePaths.Add(Path.Combine(appData, "npm", "tsc.cmd"));
            possiblePaths.Add(Path.Combine(appData, "npm", "node_modules", ".bin", "tsc.cmd"));
        }

        // Find the first existing path
        return possiblePaths.FirstOrDefault(File.Exists);
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
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets or creates a TypeScript language server for a workspace
    /// </summary>
    public async Task<TypeScriptServerInstance?> GetOrCreateServerAsync(string workspaceId, string projectPath)
    {
        if (!_typeScriptAvailable)
        {
            _logger.LogWarning("TypeScript is not available");
            return null;
        }

        await _serverLock.WaitAsync();
        try
        {
            if (_servers.TryGetValue(workspaceId, out var existingServer) && existingServer.IsRunning)
            {
                return existingServer;
            }

            // Create new server instance
            var server = await CreateServerInstanceAsync(workspaceId, projectPath);
            if (server != null)
            {
                _servers[workspaceId] = server;
            }

            return server;
        }
        finally
        {
            _serverLock.Release();
        }
    }

    /// <summary>
    /// Creates a new TypeScript language server instance
    /// </summary>
    private async Task<TypeScriptServerInstance?> CreateServerInstanceAsync(string workspaceId, string projectPath)
    {
        try
        {
            var server = new TypeScriptServerInstance(workspaceId, projectPath, _typeScriptPath!, _logger);
            await server.StartAsync();
            return server;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create TypeScript server for workspace {WorkspaceId}", workspaceId);
            return null;
        }
    }

    /// <summary>
    /// Validates TypeScript availability and returns appropriate error if not available
    /// </summary>
    public ErrorInfo? ValidateTypeScriptAvailability()
    {
        if (_typeScriptAvailable)
            return null;

        return new ErrorInfo
        {
            Code = ErrorCodes.TYPESCRIPT_NOT_INSTALLED,
            Message = "TypeScript is not installed or not found in PATH",
            Recovery = new RecoveryInfo
            {
                Steps = new[]
                {
                    "Install TypeScript globally: npm install -g typescript",
                    "Or install TypeScript locally in your project: npm install --save-dev typescript",
                    "Ensure TypeScript is in your PATH",
                    "Restart the MCP server after installation"
                },
                SuggestedActions = new List<SuggestedAction>
                {
                    new()
                    {
                        Tool = "bash",
                        Description = "Install TypeScript globally",
                        Parameters = new Dictionary<string, object>
                        {
                            ["command"] = "npm install -g typescript"
                        }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Stops a TypeScript server for a workspace
    /// </summary>
    public async Task StopServerAsync(string workspaceId)
    {
        await _serverLock.WaitAsync();
        try
        {
            if (_servers.TryGetValue(workspaceId, out var server))
            {
                await server.StopAsync();
                _servers.Remove(workspaceId);
            }
        }
        finally
        {
            _serverLock.Release();
        }
    }

    public void Dispose()
    {
        _serverLock.Wait();
        try
        {
            foreach (var server in _servers.Values)
            {
                server.Dispose();
            }
            _servers.Clear();
        }
        finally
        {
            _serverLock.Release();
            _serverLock.Dispose();
        }
    }
}

/// <summary>
/// Represents a running TypeScript language server instance
/// </summary>
public class TypeScriptServerInstance : IDisposable
{
    private readonly string _workspaceId;
    private readonly string _projectPath;
    private readonly string _tscPath;
    private readonly ILogger _logger;
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private StreamReader? _stderr;
    private int _requestId;
    private readonly Dictionary<int, TaskCompletionSource<JsonDocument>> _pendingRequests = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _outputReaderTask;

    public bool IsRunning => _process != null && !_process.HasExited;
    public string WorkspaceId => _workspaceId;
    public string ProjectPath => _projectPath;

    public TypeScriptServerInstance(string workspaceId, string projectPath, string tscPath, ILogger logger)
    {
        _workspaceId = workspaceId;
        _projectPath = projectPath;
        _tscPath = tscPath;
        _logger = logger;
    }

    /// <summary>
    /// Starts the TypeScript language server
    /// </summary>
    public async Task StartAsync()
    {
        if (IsRunning)
            return;

        try
        {
            _cancellationTokenSource = new CancellationTokenSource();

            // Start tsserver process
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    // Use tsserver from TypeScript installation
                    Arguments = GetTsServerPath(),
                    WorkingDirectory = _projectPath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            _process.Start();
            _stdin = _process.StandardInput;
            _stdout = _process.StandardOutput;
            _stderr = _process.StandardError;

            // Start reading output
            _outputReaderTask = Task.Run(() => ReadOutputAsync(_cancellationTokenSource.Token));

            _logger.LogInformation("TypeScript server started for workspace {WorkspaceId}", _workspaceId);

            // Send initial configuration
            await ConfigureServerAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start TypeScript server for workspace {WorkspaceId}", _workspaceId);
            throw;
        }
    }

    /// <summary>
    /// Gets the path to tsserver
    /// </summary>
    private string GetTsServerPath()
    {
        // Try to find tsserver relative to tsc
        var tscDir = Path.GetDirectoryName(_tscPath);
        if (tscDir != null)
        {
            var tsServerPath = Path.Combine(tscDir, "..", "lib", "node_modules", "typescript", "lib", "tsserver.js");
            if (File.Exists(tsServerPath))
                return tsServerPath;

            // Try alternative location
            tsServerPath = Path.Combine(tscDir, "..", "lib", "tsserver.js");
            if (File.Exists(tsServerPath))
                return tsServerPath;
        }

        // Fallback to requiring typescript module
        return "-e \"require('typescript/lib/tsserver')\"";
    }

    /// <summary>
    /// Configures the TypeScript server
    /// </summary>
    private async Task ConfigureServerAsync()
    {
        // Send open command for the project
        var openRequest = new
        {
            seq = GetNextRequestId(),
            type = "request",
            command = "open",
            arguments = new
            {
                file = Path.Combine(_projectPath, "dummy.ts"),
                projectRootPath = _projectPath
            }
        };

        await SendRequestAsync(openRequest);
    }

    /// <summary>
    /// Sends a request to the TypeScript server
    /// </summary>
    public async Task<JsonDocument?> SendRequestAsync(object request, CancellationToken cancellationToken = default)
    {
        if (!IsRunning || _stdin == null)
            return null;

        try
        {
            var requestId = GetNextRequestId();
            var requestJson = JsonSerializer.Serialize(request);
            
            var tcs = new TaskCompletionSource<JsonDocument>();
            _pendingRequests[requestId] = tcs;

            await _stdin.WriteLineAsync(requestJson);
            await _stdin.FlushAsync();

            using (cancellationToken.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending request to TypeScript server");
            return null;
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

                try
                {
                    var json = JsonDocument.Parse(line);
                    var root = json.RootElement;

                    if (root.TryGetProperty("type", out var typeElement))
                    {
                        var type = typeElement.GetString();
                        if (type == "response" && root.TryGetProperty("request_seq", out var seqElement))
                        {
                            var requestId = seqElement.GetInt32();
                            if (_pendingRequests.TryGetValue(requestId, out var tcs))
                            {
                                _pendingRequests.Remove(requestId);
                                tcs.TrySetResult(json);
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // Not JSON, might be plain text output
                    _logger.LogDebug("Non-JSON output from TypeScript server: {Line}", line);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading TypeScript server output");
        }
    }

    /// <summary>
    /// Gets the next request ID
    /// </summary>
    private int GetNextRequestId()
    {
        return Interlocked.Increment(ref _requestId);
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

            // Send exit command
            if (_stdin != null)
            {
                var exitRequest = new
                {
                    seq = GetNextRequestId(),
                    type = "request",
                    command = "exit"
                };

                await _stdin.WriteLineAsync(JsonSerializer.Serialize(exitRequest));
                await _stdin.FlushAsync();
            }

            // Wait for process to exit
            if (_process != null && !_process.WaitForExit(5000))
            {
                _process.Kill();
            }

            if (_outputReaderTask != null)
            {
                await _outputReaderTask;
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
    }

    public void Dispose()
    {
        StopAsync().Wait(5000);
        Cleanup();
    }
}