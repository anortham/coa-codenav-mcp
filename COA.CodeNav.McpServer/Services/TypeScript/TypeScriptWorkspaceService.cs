using System.Collections.Concurrent;
using System.Text.Json;
using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using COA.CodeNav.McpServer.Models;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Services.TypeScript;

/// <summary>
/// Service for managing TypeScript workspaces and projects
/// </summary>
public class TypeScriptWorkspaceService : IDisposable
{
    private readonly ILogger<TypeScriptWorkspaceService> _logger;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly ConcurrentDictionary<string, TypeScriptWorkspaceInfo> _workspaces = new();

    public TypeScriptWorkspaceService(
        ILogger<TypeScriptWorkspaceService> logger,
        TypeScriptCompilerManager compilerManager)
    {
        _logger = logger;
        _compilerManager = compilerManager;
    }

    /// <summary>
    /// Loads a TypeScript project from tsconfig.json
    /// </summary>
    public async Task<TsLoadConfigResult> LoadTsConfigAsync(string tsConfigPath, string? workspaceId = null)
    {
        try
        {
            // Validate file exists
            if (!File.Exists(tsConfigPath))
            {
                return new TsLoadConfigResult
                {
                    Success = false,
                    Message = $"tsconfig.json not found: {tsConfigPath}",
                    Error = new Mcp.Framework.Models.ErrorInfo
                    {
                        Code = Constants.ErrorCodes.TSCONFIG_NOT_FOUND,
                        Message = $"TypeScript configuration file not found: {tsConfigPath}",
                        Recovery = new Mcp.Framework.Models.RecoveryInfo
                        {
                            Steps = new[]
                            {
                                "Ensure the tsconfig.json file exists",
                                "Check the file path is correct",
                                "Run 'tsc --init' to create a new tsconfig.json"
                            }
                        }
                    }
                };
            }

            // Check TypeScript availability
            var tsError = _compilerManager.ValidateTypeScriptAvailability();
            if (tsError != null)
            {
                return new TsLoadConfigResult
                {
                    Success = false,
                    Message = "TypeScript is not available",
                    Error = tsError
                };
            }

            var normalizedPath = Path.GetFullPath(tsConfigPath);
            var projectPath = Path.GetDirectoryName(normalizedPath) ?? Path.GetDirectoryName(tsConfigPath)!;
            workspaceId ??= normalizedPath;

            // Check if already loaded
            if (_workspaces.TryGetValue(workspaceId, out var existingWorkspace))
            {
                _logger.LogInformation("TypeScript workspace already loaded: {WorkspaceId}", workspaceId);
                return ConvertWorkspaceInfoToResult(existingWorkspace);
            }

            // Parse tsconfig.json
            var tsConfigContent = await File.ReadAllTextAsync(tsConfigPath);
            var tsConfig = ParseTsConfig(tsConfigContent);

            // Note: We're not using a language server for now, just running tsc directly
            // This is simpler and more reliable for basic diagnostics

            // Create workspace info
            var workspaceInfo = new TypeScriptWorkspaceInfo
            {
                WorkspaceId = workspaceId,
                ProjectPath = projectPath,
                TsConfigPath = normalizedPath,
                CompilerOptions = tsConfig.CompilerOptions,
                Files = tsConfig.Files,
                Include = tsConfig.Include,
                Exclude = tsConfig.Exclude,
                References = tsConfig.References,
                Server = null, // Not using server for now, just running tsc directly
                LoadedAt = DateTime.UtcNow
            };

            // Register workspace
            _workspaces[workspaceId] = workspaceInfo;
            _logger.LogInformation("TypeScript workspace loaded: {WorkspaceId} from {TsConfigPath}", workspaceId, normalizedPath);

            return ConvertWorkspaceInfoToResult(workspaceInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load TypeScript configuration from {TsConfigPath}", tsConfigPath);
            return new TsLoadConfigResult
            {
                Success = false,
                Message = $"Failed to load TypeScript configuration: {ex.Message}",
                Error = new Mcp.Framework.Models.ErrorInfo
                {
                    Code = Constants.ErrorCodes.INTERNAL_ERROR,
                    Message = ex.Message
                }
            };
        }
    }

    /// <summary>
    /// Gets a loaded TypeScript workspace
    /// </summary>
    public TypeScriptWorkspaceInfo? GetWorkspace(string workspaceId)
    {
        return _workspaces.TryGetValue(workspaceId, out var workspace) ? workspace : null;
    }

    /// <summary>
    /// Gets the active workspace (if only one is loaded)
    /// </summary>
    public TypeScriptWorkspaceInfo? GetActiveWorkspace()
    {
        if (_workspaces.Count == 1)
        {
            return _workspaces.Values.First();
        }

        // Return the most recently loaded workspace
        return _workspaces.Values.OrderByDescending(w => w.LoadedAt).FirstOrDefault();
    }

    /// <summary>
    /// Finds the workspace that contains a specific file
    /// </summary>
    public TypeScriptWorkspaceInfo? FindWorkspaceForFile(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        
        foreach (var workspace in _workspaces.Values)
        {
            // Check if file is under the project path
            if (normalizedPath.StartsWith(workspace.ProjectPath, StringComparison.OrdinalIgnoreCase))
            {
                // Check if file matches include patterns and not excluded
                if (IsFileIncluded(normalizedPath, workspace))
                {
                    return workspace;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a file is included in the TypeScript project
    /// </summary>
    private bool IsFileIncluded(string filePath, TypeScriptWorkspaceInfo workspace)
    {
        // Check if it's a TypeScript file
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext != ".ts" && ext != ".tsx" && ext != ".d.ts")
        {
            return false;
        }

        // If explicit files list exists, check if file is in it
        if (workspace.Files != null && workspace.Files.Count > 0)
        {
            var relativePath = Path.GetRelativePath(workspace.ProjectPath, filePath);
            return workspace.Files.Contains(relativePath, StringComparer.OrdinalIgnoreCase);
        }

        // Check exclude patterns
        if (workspace.Exclude != null)
        {
            foreach (var pattern in workspace.Exclude)
            {
                if (MatchesGlobPattern(filePath, pattern, workspace.ProjectPath))
                {
                    return false;
                }
            }
        }

        // Check include patterns
        if (workspace.Include != null && workspace.Include.Count > 0)
        {
            foreach (var pattern in workspace.Include)
            {
                if (MatchesGlobPattern(filePath, pattern, workspace.ProjectPath))
                {
                    return true;
                }
            }
            return false; // If include patterns exist, file must match one
        }

        // Default: include all TypeScript files
        return true;
    }

    /// <summary>
    /// Simple glob pattern matching (basic implementation)
    /// </summary>
    private bool MatchesGlobPattern(string filePath, string pattern, string basePath)
    {
        // This is a simplified implementation
        // In production, use a proper glob matching library
        var relativePath = Path.GetRelativePath(basePath, filePath).Replace('\\', '/');
        
        // Handle ** pattern
        if (pattern.Contains("**"))
        {
            var parts = pattern.Split("**");
            if (parts.Length == 2)
            {
                var prefix = parts[0].TrimEnd('/');
                var suffix = parts[1].TrimStart('/');
                
                if (!string.IsNullOrEmpty(prefix) && !relativePath.StartsWith(prefix))
                    return false;
                    
                if (!string.IsNullOrEmpty(suffix))
                {
                    if (suffix.StartsWith("*."))
                    {
                        var ext = suffix.Substring(1);
                        return relativePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
                    }
                }
                
                return true;
            }
        }
        
        // Handle simple patterns
        if (pattern.StartsWith("*."))
        {
            var ext = pattern.Substring(1);
            return relativePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
        }
        
        return relativePath.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses tsconfig.json content
    /// </summary>
    private TsConfigData ParseTsConfig(string content)
    {
        try
        {
            // Remove comments (simple approach - doesn't handle all cases)
            var lines = content.Split('\n');
            var cleanedLines = lines
                .Select(line =>
                {
                    var commentIndex = line.IndexOf("//");
                    return commentIndex >= 0 ? line.Substring(0, commentIndex) : line;
                })
                .ToList();
            var cleanedContent = string.Join('\n', cleanedLines);

            using var doc = JsonDocument.Parse(cleanedContent);
            var root = doc.RootElement;

            var tsConfig = new TsConfigData();

            // Parse compiler options
            if (root.TryGetProperty("compilerOptions", out var compilerOptionsElement))
            {
                tsConfig.CompilerOptions = ParseCompilerOptions(compilerOptionsElement);
            }

            // Parse files array
            if (root.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Array)
            {
                tsConfig.Files = filesElement.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => s != null)
                    .Cast<string>()
                    .ToList();
            }

            // Parse include array
            if (root.TryGetProperty("include", out var includeElement) && includeElement.ValueKind == JsonValueKind.Array)
            {
                tsConfig.Include = includeElement.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => s != null)
                    .Cast<string>()
                    .ToList();
            }

            // Parse exclude array
            if (root.TryGetProperty("exclude", out var excludeElement) && excludeElement.ValueKind == JsonValueKind.Array)
            {
                tsConfig.Exclude = excludeElement.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => s != null)
                    .Cast<string>()
                    .ToList();
            }

            // Parse references
            if (root.TryGetProperty("references", out var referencesElement) && referencesElement.ValueKind == JsonValueKind.Array)
            {
                tsConfig.References = referencesElement.EnumerateArray()
                    .Select(e =>
                    {
                        if (e.TryGetProperty("path", out var pathElement))
                        {
                            return new TsProjectReference
                            {
                                Path = pathElement.GetString() ?? "",
                                Prepend = e.TryGetProperty("prepend", out var prependElement) ? prependElement.GetBoolean() : null
                            };
                        }
                        return null;
                    })
                    .Where(r => r != null)
                    .Cast<TsProjectReference>()
                    .ToList();
            }

            return tsConfig;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse tsconfig.json");
            return new TsConfigData();
        }
    }

    /// <summary>
    /// Parses compiler options from JSON
    /// </summary>
    private TsCompilerOptions ParseCompilerOptions(JsonElement element)
    {
        var options = new TsCompilerOptions();

        if (element.TryGetProperty("target", out var target))
            options.Target = target.GetString();

        if (element.TryGetProperty("module", out var module))
            options.Module = module.GetString();

        if (element.TryGetProperty("lib", out var lib) && lib.ValueKind == JsonValueKind.Array)
            options.Lib = lib.EnumerateArray().Select(e => e.GetString()).Where(s => s != null).Cast<string>().ToList();

        if (element.TryGetProperty("jsx", out var jsx))
            options.Jsx = jsx.GetString();

        if (element.TryGetProperty("strict", out var strict))
            options.Strict = strict.GetBoolean();

        if (element.TryGetProperty("esModuleInterop", out var esModuleInterop))
            options.EsModuleInterop = esModuleInterop.GetBoolean();

        if (element.TryGetProperty("skipLibCheck", out var skipLibCheck))
            options.SkipLibCheck = skipLibCheck.GetBoolean();

        if (element.TryGetProperty("forceConsistentCasingInFileNames", out var forceConsistentCasing))
            options.ForceConsistentCasingInFileNames = forceConsistentCasing.GetBoolean();

        if (element.TryGetProperty("declaration", out var declaration))
            options.Declaration = declaration.GetBoolean();

        if (element.TryGetProperty("declarationMap", out var declarationMap))
            options.DeclarationMap = declarationMap.GetBoolean();

        if (element.TryGetProperty("sourceMap", out var sourceMap))
            options.SourceMap = sourceMap.GetBoolean();

        if (element.TryGetProperty("outDir", out var outDir))
            options.OutDir = outDir.GetString();

        if (element.TryGetProperty("rootDir", out var rootDir))
            options.RootDir = rootDir.GetString();

        if (element.TryGetProperty("baseUrl", out var baseUrl))
            options.BaseUrl = baseUrl.GetString();

        if (element.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
        {
            options.Paths = new Dictionary<string, List<string>>();
            foreach (var pathEntry in paths.EnumerateObject())
            {
                if (pathEntry.Value.ValueKind == JsonValueKind.Array)
                {
                    var pathList = pathEntry.Value.EnumerateArray()
                        .Select(e => e.GetString())
                        .Where(s => s != null)
                        .Cast<string>()
                        .ToList();
                    options.Paths[pathEntry.Name] = pathList;
                }
            }
        }

        return options;
    }

    /// <summary>
    /// Converts workspace info to result
    /// </summary>
    private TsLoadConfigResult ConvertWorkspaceInfoToResult(TypeScriptWorkspaceInfo workspace)
    {
        return new TsLoadConfigResult
        {
            Success = true,
            Message = $"TypeScript workspace loaded successfully",
            WorkspaceId = workspace.WorkspaceId,
            ProjectPath = workspace.ProjectPath,
            TsConfigPath = workspace.TsConfigPath,
            CompilerOptions = workspace.CompilerOptions,
            Files = workspace.Files,
            Include = workspace.Include,
            Exclude = workspace.Exclude,
            References = workspace.References,
            Insights = new List<string?>
            {
                $"TypeScript version: {_compilerManager.TypeScriptVersion}",
                $"Project path: {workspace.ProjectPath}",
                workspace.CompilerOptions?.Strict == true ? "Strict mode enabled" : "Strict mode disabled",
                workspace.References?.Count > 0 ? $"Project has {workspace.References.Count} references" : null
            }.Where(i => i != null).Select(i => i!).ToList(),
            Actions = new List<Mcp.Framework.Models.AIAction>
            {
                new()
                {
                    Action = "ts_get_diagnostics",
                    Description = "Check for TypeScript compilation errors",
                    Category = "analyze"
                },
                new()
                {
                    Action = "ts_symbol_search",
                    Description = "Search for TypeScript symbols",
                    Category = "search"
                }
            }
        };
    }

    /// <summary>
    /// Unloads a TypeScript workspace
    /// </summary>
    public async Task UnloadWorkspaceAsync(string workspaceId)
    {
        if (_workspaces.TryRemove(workspaceId, out var workspace))
        {
            await _compilerManager.StopServerAsync(workspaceId);
            _logger.LogInformation("TypeScript workspace unloaded: {WorkspaceId}", workspaceId);
        }
    }

    public void Dispose()
    {
        foreach (var workspaceId in _workspaces.Keys)
        {
            UnloadWorkspaceAsync(workspaceId).Wait(5000);
        }
        _workspaces.Clear();
    }
}

/// <summary>
/// Information about a loaded TypeScript workspace
/// </summary>
public class TypeScriptWorkspaceInfo
{
    public required string WorkspaceId { get; set; }
    public required string ProjectPath { get; set; }
    public required string TsConfigPath { get; set; }
    public TsCompilerOptions? CompilerOptions { get; set; }
    public List<string>? Files { get; set; }
    public List<string>? Include { get; set; }
    public List<string>? Exclude { get; set; }
    public List<TsProjectReference>? References { get; set; }
    public TypeScriptServerInstance? Server { get; set; }
    public DateTime LoadedAt { get; set; }
}

/// <summary>
/// Internal tsconfig.json data structure
/// </summary>
internal class TsConfigData
{
    public TsCompilerOptions? CompilerOptions { get; set; }
    public List<string>? Files { get; set; }
    public List<string>? Include { get; set; }
    public List<string>? Exclude { get; set; }
    public List<TsProjectReference>? References { get; set; }
}