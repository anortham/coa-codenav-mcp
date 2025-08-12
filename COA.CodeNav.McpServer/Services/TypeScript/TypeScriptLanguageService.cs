using System.Diagnostics;
using System.Text;
using System.Text.Json;
using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using COA.CodeNav.McpServer.Models;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Services.TypeScript;

/// <summary>
/// Service for TypeScript language operations (diagnostics, symbols, etc.)
/// </summary>
public class TypeScriptLanguageService
{
    private readonly ILogger<TypeScriptLanguageService> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;

    public TypeScriptLanguageService(
        ILogger<TypeScriptLanguageService> logger,
        TypeScriptWorkspaceService workspaceService,
        TypeScriptCompilerManager compilerManager)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _compilerManager = compilerManager;
    }

    /// <summary>
    /// Gets diagnostics for a TypeScript project or file
    /// </summary>
    public async Task<List<TsDiagnostic>> GetDiagnosticsAsync(
        string? filePath = null,
        string? workspaceId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Find the appropriate workspace
            TypeScriptWorkspaceInfo? workspace = null;
            
            if (!string.IsNullOrEmpty(workspaceId))
            {
                workspace = _workspaceService.GetWorkspace(workspaceId);
            }
            else if (!string.IsNullOrEmpty(filePath))
            {
                workspace = _workspaceService.FindWorkspaceForFile(filePath);
            }
            else
            {
                workspace = _workspaceService.GetActiveWorkspace();
            }

            if (workspace == null)
            {
                _logger.LogWarning("No TypeScript workspace found for diagnostics");
                return new List<TsDiagnostic>();
            }

            // Use TypeScript compiler directly for now (simpler than full LSP)
            return await GetDiagnosticsUsingCompilerAsync(workspace, filePath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get TypeScript diagnostics");
            return new List<TsDiagnostic>();
        }
    }

    /// <summary>
    /// Gets diagnostics using the TypeScript compiler directly
    /// </summary>
    private async Task<List<TsDiagnostic>> GetDiagnosticsUsingCompilerAsync(
        TypeScriptWorkspaceInfo workspace,
        string? filePath,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<TsDiagnostic>();

        try
        {
            // Build the tsc command
            var tscArgs = new StringBuilder();
            tscArgs.Append("--noEmit "); // Don't emit files, just check
            tscArgs.Append("--pretty false "); // Disable pretty output for easier parsing
            
            // When using --project, you cannot specify individual files
            // If a specific file is requested, we'll use the tsconfig but let tsc check all files
            // and filter the results later
            tscArgs.Append($"--project \"{workspace.TsConfigPath}\"");

            // Run tsc
            var tscPath = _compilerManager.TypeScriptPath ?? "tsc";
            var startInfo = new ProcessStartInfo
            {
                FileName = tscPath,
                Arguments = tscArgs.ToString(),
                WorkingDirectory = workspace.ProjectPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo };
            
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    outputBuilder.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for completion with timeout
            var completed = process.WaitForExit(30000); // 30 second timeout
            
            if (!completed)
            {
                process.Kill();
                _logger.LogWarning("TypeScript compiler timed out");
                return diagnostics;
            }

            // Parse the output (TypeScript writes errors to stdout, not stderr!)
            var output = outputBuilder.ToString();
            var errors = errorBuilder.ToString();
            
            // TypeScript writes diagnostic messages to stdout, not stderr
            // Only use stdout for parsing diagnostics
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var diagnostic = ParseDiagnosticLine(line);
                if (diagnostic != null)
                {
                    // If a specific file was requested, filter to only that file
                    if (!string.IsNullOrEmpty(filePath))
                    {
                        // Normalize paths for comparison
                        var normalizedRequestedPath = Path.GetFullPath(filePath).Replace('\\', '/');
                        var normalizedDiagnosticPath = diagnostic.FilePath != null 
                            ? Path.GetFullPath(Path.Combine(workspace.ProjectPath, diagnostic.FilePath)).Replace('\\', '/')
                            : "";
                        
                        if (!string.Equals(normalizedRequestedPath, normalizedDiagnosticPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // Skip diagnostics from other files
                        }
                    }
                    
                    diagnostics.Add(diagnostic);
                }
            }

            _logger.LogInformation("Found {Count} TypeScript diagnostics", diagnostics.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run TypeScript compiler for diagnostics");
        }

        return diagnostics;
    }

    /// <summary>
    /// Parses a TypeScript compiler diagnostic line
    /// </summary>
    private TsDiagnostic? ParseDiagnosticLine(string line)
    {
        try
        {
            // TypeScript error format: file(line,col): error TS2304: Cannot find name 'xyz'.
            // Example: src/index.ts(10,5): error TS2304: Cannot find name 'console'.
            
            if (string.IsNullOrWhiteSpace(line))
                return null;

            // Check if this is a diagnostic line
            if (!line.Contains("): error TS") && !line.Contains("): warning TS") && !line.Contains("): info TS"))
                return null;

            // Use regex for more reliable parsing
            // Pattern: file(line,col): severity TScode: message
            var pattern = @"^(.+?)\((\d+),(\d+)\):\s+(error|warning|info)\s+TS(\d+):\s+(.+)$";
            var match = System.Text.RegularExpressions.Regex.Match(line, pattern);
            
            if (!match.Success)
            {
                return null;
            }

            var filePath = match.Groups[1].Value;
            var lineNumber = int.Parse(match.Groups[2].Value);
            var column = int.Parse(match.Groups[3].Value);
            var category = match.Groups[4].Value;
            var code = int.Parse(match.Groups[5].Value);
            var message = match.Groups[6].Value;
            
            return new TsDiagnostic
            {
                Code = code,
                Category = category.ToLowerInvariant(),
                Message = message,
                FilePath = filePath,
                Start = new Position { Line = lineNumber - 1, Character = column - 1 }, // Convert to 0-based
                End = new Position { Line = lineNumber - 1, Character = column }, // Approximate end
                Source = "TypeScript"
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse diagnostic line: {Line}", line);
        }

        return null;
    }

    /// <summary>
    /// Gets semantic diagnostics for a specific file using language server
    /// </summary>
    public async Task<List<TsDiagnostic>> GetSemanticDiagnosticsAsync(
        string filePath,
        TypeScriptWorkspaceInfo workspace,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<TsDiagnostic>();

        if (workspace.Server == null || !workspace.Server.IsRunning)
        {
            _logger.LogWarning("TypeScript server not running for workspace {WorkspaceId}", workspace.WorkspaceId);
            return diagnostics;
        }

        try
        {
            // Send semantic diagnostics request to language server
            var request = new
            {
                seq = 0, // Will be set by server instance
                type = "request",
                command = "semanticDiagnosticsSync",
                arguments = new
                {
                    file = filePath
                }
            };

            var response = await workspace.Server.SendRequestAsync(request, cancellationToken);
            if (response != null)
            {
                diagnostics = ParseLanguageServerDiagnostics(response);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get semantic diagnostics for {FilePath}", filePath);
        }

        return diagnostics;
    }

    /// <summary>
    /// Parses diagnostics from language server response
    /// </summary>
    private List<TsDiagnostic> ParseLanguageServerDiagnostics(JsonDocument response)
    {
        var diagnostics = new List<TsDiagnostic>();

        try
        {
            var root = response.RootElement;
            if (root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in body.EnumerateArray())
                {
                    var diagnostic = ParseLanguageServerDiagnostic(item);
                    if (diagnostic != null)
                    {
                        diagnostics.Add(diagnostic);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse language server diagnostics");
        }

        return diagnostics;
    }

    /// <summary>
    /// Parses a single diagnostic from language server
    /// </summary>
    private TsDiagnostic? ParseLanguageServerDiagnostic(JsonElement element)
    {
        try
        {
            var diagnostic = new TsDiagnostic
            {
                Category = "error", // Default
                Source = "TypeScript",
                Message = "" // Required property, will be set below
            };

            if (element.TryGetProperty("text", out var text))
                diagnostic.Message = text.GetString() ?? "";

            if (element.TryGetProperty("code", out var code))
                diagnostic.Code = code.GetInt32();

            if (element.TryGetProperty("category", out var category))
                diagnostic.Category = category.GetString() ?? "error";

            if (element.TryGetProperty("fileName", out var fileName))
                diagnostic.FilePath = fileName.GetString();

            if (element.TryGetProperty("start", out var start))
            {
                diagnostic.Start = new Position
                {
                    Line = start.TryGetProperty("line", out var line) ? line.GetInt32() - 1 : 0,
                    Character = start.TryGetProperty("offset", out var offset) ? offset.GetInt32() - 1 : 0
                };
            }

            if (element.TryGetProperty("end", out var end))
            {
                diagnostic.End = new Position
                {
                    Line = end.TryGetProperty("line", out var endLine) ? endLine.GetInt32() - 1 : diagnostic.Start?.Line ?? 0,
                    Character = end.TryGetProperty("offset", out var endOffset) ? endOffset.GetInt32() - 1 : diagnostic.Start?.Character ?? 0
                };
            }

            return diagnostic;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse language server diagnostic");
            return null;
        }
    }

    /// <summary>
    /// Categorizes diagnostics by severity
    /// </summary>
    public DiagnosticsDistribution CategorizeDiagnostics(List<TsDiagnostic> diagnostics)
    {
        var distribution = new DiagnosticsDistribution
        {
            BySeverity = new Dictionary<string, int>(),
            ByFile = new Dictionary<string, int>(),
            ByCategory = new Dictionary<string, int>()
        };

        foreach (var diagnostic in diagnostics)
        {
            // By severity (category in TypeScript)
            var severity = diagnostic.Category.ToLowerInvariant();
            distribution.BySeverity[severity] = distribution.BySeverity.GetValueOrDefault(severity, 0) + 1;

            // By file
            if (!string.IsNullOrEmpty(diagnostic.FilePath))
            {
                var fileName = Path.GetFileName(diagnostic.FilePath);
                distribution.ByFile[fileName] = distribution.ByFile.GetValueOrDefault(fileName, 0) + 1;
            }

            // By category (error code ranges)
            var category = GetDiagnosticCategory(diagnostic.Code);
            distribution.ByCategory[category] = distribution.ByCategory.GetValueOrDefault(category, 0) + 1;
        }

        return distribution;
    }

    /// <summary>
    /// Gets the category of a TypeScript diagnostic based on error code
    /// </summary>
    private string GetDiagnosticCategory(int code)
    {
        // TypeScript error code ranges
        return code switch
        {
            >= 1000 and < 2000 => "Syntax",
            >= 2000 and < 3000 => "Semantic",
            >= 4000 and < 5000 => "Declaration",
            >= 5000 and < 6000 => "Command Line",
            >= 6000 and < 7000 => "Compiler",
            >= 7000 and < 8000 => "Type Checking",
            _ => "Other"
        };
    }
}