using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that gets compilation errors, warnings, and analyzer diagnostics
/// </summary>
[McpServerToolType]
public class GetDiagnosticsTool : ITool
{
    private readonly ILogger<GetDiagnosticsTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "roslyn_get_diagnostics";
    public string Description => "Get compilation errors, warnings, and analyzer diagnostics";

    public GetDiagnosticsTool(
        ILogger<GetDiagnosticsTool> logger,
        RoslynWorkspaceService workspaceService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "roslyn_get_diagnostics")]
    [Description(@"Get compilation errors, warnings, and analyzer diagnostics for files, projects, or the entire solution.
Returns: List of diagnostics with severity, location, and suggested fixes.
Prerequisites: Call roslyn_load_solution or roslyn_load_project first.
Error handling: Returns specific error codes with recovery steps if workspace is not loaded.
Use cases: Finding compilation errors, checking code quality, identifying warnings, running analyzers.
Not for: Code metrics (use future roslyn_code_metrics), finding specific symbols (use roslyn_symbol_search).")]
    public async Task<object> ExecuteAsync(GetDiagnosticsParams parameters, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("GetDiagnostics request received: Scope={Scope}, FilePath={FilePath}", 
            parameters.Scope, parameters.FilePath);
            
        try
        {
            _logger.LogInformation("Processing GetDiagnostics with scope: {Scope}", parameters.Scope);

            // Check if any workspaces are loaded
            var workspaces = _workspaceService.GetActiveWorkspaces();
            if (!workspaces.Any())
            {
                _logger.LogWarning("No workspace loaded");
                return new GetDiagnosticsResult
                {
                    Success = false,
                    Message = "No workspace loaded. Please load a solution or project first.",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.WORKSPACE_NOT_LOADED,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Load a solution using roslyn_load_solution",
                                "Or load a project using roslyn_load_project",
                                "Then retry getting diagnostics"
                            },
                            SuggestedActions = new List<SuggestedAction>
                            {
                                new SuggestedAction
                                {
                                    Tool = "roslyn_load_solution",
                                    Description = "Load a solution file",
                                    Parameters = new { solutionPath = "<path-to-your-solution.sln>" }
                                }
                            }
                        }
                    }
                };
            }

            var allDiagnostics = new List<DiagnosticInfo>();
            var projects = new List<Project>();

            // Determine which projects to analyze
            switch (parameters.Scope?.ToLower())
            {
                case "file":
                    if (string.IsNullOrEmpty(parameters.FilePath))
                    {
                        return new GetDiagnosticsResult
                        {
                            Success = false,
                            Message = "File path is required when scope is 'file'",
                            Error = new ErrorInfo
                            {
                                Code = ErrorCodes.INVALID_PARAMETERS,
                                Recovery = new RecoveryInfo
                                {
                                    Steps = new List<string>
                                    {
                                        "Provide a file path when using 'file' scope",
                                        "Or use 'project' or 'solution' scope"
                                    }
                                }
                            }
                        };
                    }

                    var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
                    if (document == null)
                    {
                        return new GetDiagnosticsResult
                        {
                            Success = false,
                            Message = $"Document not found: {parameters.FilePath}",
                            Error = new ErrorInfo
                            {
                                Code = ErrorCodes.DOCUMENT_NOT_FOUND,
                                Recovery = new RecoveryInfo
                                {
                                    Steps = new List<string>
                                    {
                                        "Ensure the file path is correct and absolute",
                                        "Verify the file is part of the loaded solution/project"
                                    }
                                }
                            }
                        };
                    }

                    var fileDiagnostics = await GetDocumentDiagnosticsAsync(document, parameters, cancellationToken);
                    allDiagnostics.AddRange(fileDiagnostics);
                    break;

                case "project":
                    if (!string.IsNullOrEmpty(parameters.FilePath))
                    {
                        var projectDoc = await _workspaceService.GetDocumentAsync(parameters.FilePath);
                        if (projectDoc != null)
                        {
                            projects.Add(projectDoc.Project);
                        }
                    }
                    else
                    {
                        // Use first project if no file specified
                        projects.AddRange(workspaces.SelectMany(w => w.Solution.Projects).Take(1));
                    }
                    break;

                case "solution":
                default:
                    // Get all projects from all workspaces
                    projects.AddRange(workspaces.SelectMany(w => w.Solution.Projects));
                    break;
            }

            // Get diagnostics for projects
            foreach (var project in projects)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var projectDiagnostics = await GetProjectDiagnosticsAsync(project, parameters, cancellationToken);
                allDiagnostics.AddRange(projectDiagnostics);
            }

            // Apply filters
            allDiagnostics = ApplyFilters(allDiagnostics, parameters);

            // Sort diagnostics
            allDiagnostics = SortDiagnostics(allDiagnostics, parameters.SortBy ?? "Severity");

            // Group diagnostics
            var groupedDiagnostics = GroupDiagnostics(allDiagnostics, parameters.GroupBy ?? "File");

            // Generate insights
            var insights = GenerateInsights(allDiagnostics);

            // Generate next actions
            var nextActions = GenerateNextActions(allDiagnostics);

            // Store result
            var resourceUri = _resourceProvider?.StoreAnalysisResult("diagnostics",
                new { diagnostics = allDiagnostics, grouped = groupedDiagnostics },
                $"Diagnostics for {parameters.Scope ?? "solution"}");

            return new GetDiagnosticsResult
            {
                Success = true,
                TotalDiagnostics = allDiagnostics.Count,
                Diagnostics = parameters.GroupBy != null ? null : allDiagnostics,
                GroupedDiagnostics = parameters.GroupBy != null ? groupedDiagnostics : null,
                Message = FormatSummaryMessage(allDiagnostics),
                Insights = insights,
                NextActions = nextActions,
                ResourceUri = resourceUri
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Get Diagnostics");
            return new GetDiagnosticsResult
            {
                Success = false,
                Message = $"Error: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new List<string>
                        {
                            "Check the server logs for detailed error information",
                            "Verify the solution/project is loaded correctly",
                            "Try the operation again"
                        }
                    }
                }
            };
        }
    }

    private async Task<List<DiagnosticInfo>> GetDocumentDiagnosticsAsync(
        Document document,
        GetDiagnosticsParams parameters,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<DiagnosticInfo>();

        try
        {
            // Get syntax diagnostics
            if (parameters.IncludeSyntax != false)
            {
                var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
                if (syntaxTree != null)
                {
                    var syntaxDiagnostics = syntaxTree.GetDiagnostics(cancellationToken);
                    diagnostics.AddRange(syntaxDiagnostics.Select(d => CreateDiagnosticInfo(d, document.FilePath, "Syntax")));
                }
            }

            // Get semantic diagnostics
            if (parameters.IncludeSemantic != false)
            {
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (semanticModel != null)
                {
                    var semanticDiagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken);
                    diagnostics.AddRange(semanticDiagnostics.Select(d => CreateDiagnosticInfo(d, document.FilePath, "Semantic")));
                }
            }

            // Get analyzer diagnostics
            if (parameters.IncludeAnalyzers ?? true)
            {
                var analyzerDiagnostics = await GetAnalyzerDiagnosticsAsync(document, cancellationToken);
                diagnostics.AddRange(analyzerDiagnostics.Select(d => CreateDiagnosticInfo(d, document.FilePath, "Analyzer")));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting diagnostics for document: {FilePath}", document.FilePath);
        }

        return diagnostics;
    }

    private async Task<List<DiagnosticInfo>> GetProjectDiagnosticsAsync(
        Project project,
        GetDiagnosticsParams parameters,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<DiagnosticInfo>();

        try
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) return diagnostics;

            // Get compilation diagnostics
            if (parameters.IncludeSemantic != false)
            {
                var compilationDiagnostics = compilation.GetDiagnostics(cancellationToken);
                foreach (var diagnostic in compilationDiagnostics)
                {
                    if (diagnostic.Location.IsInSource)
                    {
                        var filePath = diagnostic.Location.SourceTree?.FilePath;
                        diagnostics.Add(CreateDiagnosticInfo(diagnostic, filePath, "Compilation"));
                    }
                }
            }

            // Get analyzer diagnostics for the project
            if (parameters.IncludeAnalyzers ?? true)
            {
                var analyzerDiagnostics = await GetProjectAnalyzerDiagnosticsAsync(compilation, project, cancellationToken);
                foreach (var diagnostic in analyzerDiagnostics)
                {
                    if (diagnostic.Location.IsInSource)
                    {
                        var filePath = diagnostic.Location.SourceTree?.FilePath;
                        diagnostics.Add(CreateDiagnosticInfo(diagnostic, filePath, "Analyzer"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting diagnostics for project: {ProjectName}", project.Name);
        }

        return diagnostics;
    }

    private async Task<ImmutableArray<Diagnostic>> GetAnalyzerDiagnosticsAsync(
        Document document,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get analyzers from the project
            var analyzers = document.Project.AnalyzerReferences
                .SelectMany(r => r.GetAnalyzers(LanguageNames.CSharp))
                .ToImmutableArray();

            if (!analyzers.Any())
            {
                return ImmutableArray<Diagnostic>.Empty;
            }

            var compilation = await document.Project.GetCompilationAsync(cancellationToken);
            if (compilation == null) return ImmutableArray<Diagnostic>.Empty;

            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (tree == null) return ImmutableArray<Diagnostic>.Empty;

            var compilationWithAnalyzers = new CompilationWithAnalyzers(compilation, analyzers, options: null);
            var diagnostics = await compilationWithAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null) return ImmutableArray<Diagnostic>.Empty;
            
            var semanticDiagnostics = await compilationWithAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(semanticModel, null, cancellationToken);
            
            return diagnostics.AddRange(semanticDiagnostics);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting analyzer diagnostics");
            return ImmutableArray<Diagnostic>.Empty;
        }
    }

    private async Task<ImmutableArray<Diagnostic>> GetProjectAnalyzerDiagnosticsAsync(
        Compilation compilation,
        Project project,
        CancellationToken cancellationToken)
    {
        try
        {
            var analyzers = project.AnalyzerReferences
                .SelectMany(r => r.GetAnalyzers(LanguageNames.CSharp))
                .ToImmutableArray();

            if (!analyzers.Any())
            {
                return ImmutableArray<Diagnostic>.Empty;
            }

            var compilationWithAnalyzers = new CompilationWithAnalyzers(compilation, analyzers, options: null);
            return await compilationWithAnalyzers.GetAllDiagnosticsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting project analyzer diagnostics");
            return ImmutableArray<Diagnostic>.Empty;
        }
    }

    private DiagnosticInfo CreateDiagnosticInfo(Diagnostic diagnostic, string? filePath, string source)
    {
        var info = new DiagnosticInfo
        {
            Id = diagnostic.Id,
            Category = diagnostic.Descriptor.Category,
            Severity = diagnostic.Severity.ToString(),
            Message = diagnostic.GetMessage(),
            Source = source,
            FilePath = filePath,
            IsSuppressed = diagnostic.IsSuppressed,
            IsWarningAsError = diagnostic.IsWarningAsError,
            Tags = diagnostic.Descriptor.CustomTags.ToList()
        };

        // Get location information
        if (diagnostic.Location.IsInSource)
        {
            var lineSpan = diagnostic.Location.GetLineSpan();
            info.Location = new LocationInfo
            {
                FilePath = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                EndColumn = lineSpan.EndLinePosition.Character + 1
            };
        }

        // Get additional properties
        if (diagnostic.Properties.Any())
        {
            info.Properties = diagnostic.Properties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        // Check for code fixes
        info.HasCodeFix = diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary) ||
                         diagnostic.Id.StartsWith("IDE") ||
                         diagnostic.Id.StartsWith("CA");

        return info;
    }

    private List<DiagnosticInfo> ApplyFilters(List<DiagnosticInfo> diagnostics, GetDiagnosticsParams parameters)
    {
        var filtered = diagnostics.AsEnumerable();

        // Filter by severity
        if (parameters.Severities?.Any() == true)
        {
            filtered = filtered.Where(d => parameters.Severities.Contains(d.Severity, StringComparer.OrdinalIgnoreCase));
        }

        // Filter by category
        if (!string.IsNullOrEmpty(parameters.CategoryFilter))
        {
            filtered = filtered.Where(d => d.Category.Contains(parameters.CategoryFilter, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by ID pattern
        if (!string.IsNullOrEmpty(parameters.IdFilter))
        {
            filtered = filtered.Where(d => d.Id.Contains(parameters.IdFilter, StringComparison.OrdinalIgnoreCase));
        }

        // Include/exclude suppressed
        if (parameters.IncludeSuppressed == false)
        {
            filtered = filtered.Where(d => !d.IsSuppressed);
        }

        return filtered.ToList();
    }

    private List<DiagnosticInfo> SortDiagnostics(List<DiagnosticInfo> diagnostics, string sortBy)
    {
        return sortBy.ToLower() switch
        {
            "severity" => diagnostics.OrderBy(d => GetSeverityOrder(d.Severity))
                                   .ThenBy(d => d.FilePath)
                                   .ThenBy(d => d.Location?.Line ?? 0)
                                   .ToList(),
            "file" => diagnostics.OrderBy(d => d.FilePath)
                                .ThenBy(d => d.Location?.Line ?? 0)
                                .ThenBy(d => GetSeverityOrder(d.Severity))
                                .ToList(),
            "category" => diagnostics.OrderBy(d => d.Category)
                                    .ThenBy(d => GetSeverityOrder(d.Severity))
                                    .ThenBy(d => d.FilePath)
                                    .ToList(),
            _ => diagnostics
        };
    }

    private int GetSeverityOrder(string severity)
    {
        return severity switch
        {
            "Error" => 0,
            "Warning" => 1,
            "Info" => 2,
            "Hidden" => 3,
            _ => 4
        };
    }

    private Dictionary<string, List<DiagnosticInfo>> GroupDiagnostics(List<DiagnosticInfo> diagnostics, string groupBy)
    {
        return groupBy.ToLower() switch
        {
            "file" => diagnostics.GroupBy(d => d.FilePath ?? "Unknown")
                                .ToDictionary(g => g.Key, g => g.ToList()),
            "severity" => diagnostics.GroupBy(d => d.Severity)
                                    .ToDictionary(g => g.Key, g => g.ToList()),
            "category" => diagnostics.GroupBy(d => d.Category)
                                    .ToDictionary(g => g.Key, g => g.ToList()),
            "source" => diagnostics.GroupBy(d => d.Source)
                                  .ToDictionary(g => g.Key, g => g.ToList()),
            _ => new Dictionary<string, List<DiagnosticInfo>> { ["All"] = diagnostics }
        };
    }

    private string FormatSummaryMessage(List<DiagnosticInfo> diagnostics)
    {
        var errorCount = diagnostics.Count(d => d.Severity == "Error");
        var warningCount = diagnostics.Count(d => d.Severity == "Warning");
        var infoCount = diagnostics.Count(d => d.Severity == "Info");

        var parts = new List<string>();
        if (errorCount > 0) parts.Add($"{errorCount} error{(errorCount != 1 ? "s" : "")}");
        if (warningCount > 0) parts.Add($"{warningCount} warning{(warningCount != 1 ? "s" : "")}");
        if (infoCount > 0) parts.Add($"{infoCount} info message{(infoCount != 1 ? "s" : "")}");

        return parts.Any() 
            ? $"Found {string.Join(", ", parts)}" 
            : "No diagnostics found";
    }

    private List<string> GenerateInsights(List<DiagnosticInfo> diagnostics)
    {
        var insights = new List<string>();

        // Error insights
        var errors = diagnostics.Where(d => d.Severity == "Error").ToList();
        if (errors.Any())
        {
            insights.Add($"{errors.Count} compilation errors must be fixed");
            
            var commonErrors = errors.GroupBy(e => e.Id)
                                    .OrderByDescending(g => g.Count())
                                    .Take(3);
            foreach (var errorGroup in commonErrors)
            {
                insights.Add($"Most common error: {errorGroup.Key} ({errorGroup.Count()} occurrences)");
            }
        }

        // Warning insights
        var warnings = diagnostics.Where(d => d.Severity == "Warning").ToList();
        if (warnings.Any())
        {
            var nullableWarnings = warnings.Count(w => w.Id.StartsWith("CS8"));
            if (nullableWarnings > 0)
            {
                insights.Add($"{nullableWarnings} nullable reference warnings - consider addressing null safety");
            }

            var unusedWarnings = warnings.Count(w => w.Id == "CS0168" || w.Id == "CS0169" || w.Id == "CS0219");
            if (unusedWarnings > 0)
            {
                insights.Add($"{unusedWarnings} unused code warnings - consider removing dead code");
            }
        }

        // Analyzer insights
        var analyzerDiagnostics = diagnostics.Where(d => d.Source == "Analyzer").ToList();
        if (analyzerDiagnostics.Any())
        {
            insights.Add($"{analyzerDiagnostics.Count} analyzer diagnostics for code quality");
            
            var hasCodeFixes = analyzerDiagnostics.Count(d => d.HasCodeFix);
            if (hasCodeFixes > 0)
            {
                insights.Add($"{hasCodeFixes} diagnostics have available code fixes");
            }
        }

        // File concentration
        var fileGroups = diagnostics.Where(d => d.FilePath != null)
                                   .GroupBy(d => d.FilePath)
                                   .OrderByDescending(g => g.Count())
                                   .Take(3);
        foreach (var fileGroup in fileGroups)
        {
            if (fileGroup.Count() > 5)
            {
                var fileName = Path.GetFileName(fileGroup.Key!);
                insights.Add($"{fileName} has {fileGroup.Count()} diagnostics - needs attention");
            }
        }

        // Suppression insights
        var suppressed = diagnostics.Count(d => d.IsSuppressed);
        if (suppressed > 0)
        {
            insights.Add($"{suppressed} diagnostics are suppressed - review if suppressions are still needed");
        }

        return insights;
    }

    private List<NextAction> GenerateNextActions(List<DiagnosticInfo> diagnostics)
    {
        var actions = new List<NextAction>();

        // Suggest fixing errors first
        var firstError = diagnostics.FirstOrDefault(d => d.Severity == "Error" && d.Location != null);
        if (firstError?.Location != null)
        {
            actions.Add(new NextAction
            {
                Id = "goto_first_error",
                Description = $"Go to first error: {firstError.Id}",
                ToolName = "roslyn_goto_definition",
                Parameters = new
                {
                    filePath = firstError.Location.FilePath,
                    line = firstError.Location.Line,
                    column = firstError.Location.Column
                },
                Priority = "high"
            });
        }

        // Suggest viewing file with most diagnostics
        var worstFile = diagnostics.Where(d => d.FilePath != null)
                                  .GroupBy(d => d.FilePath)
                                  .OrderByDescending(g => g.Count())
                                  .FirstOrDefault();
        if (worstFile != null && worstFile.Count() > 3)
        {
            actions.Add(new NextAction
            {
                Id = "view_worst_file",
                Description = $"View file with most issues: {Path.GetFileName(worstFile.Key!)}",
                ToolName = "roslyn_document_symbols",
                Parameters = new
                {
                    filePath = worstFile.Key
                },
                Priority = "medium"
            });
        }

        // Suggest running specific analyzer categories
        var categories = diagnostics.Select(d => d.Category).Distinct().Take(3);
        foreach (var category in categories)
        {
            actions.Add(new NextAction
            {
                Id = $"filter_{category.ToLower()}",
                Description = $"Show only {category} diagnostics",
                ToolName = "roslyn_get_diagnostics",
                Parameters = new
                {
                    categoryFilter = category,
                    scope = "solution"
                },
                Priority = "low"
            });
        }

        return actions;
    }
}

public class GetDiagnosticsParams
{
    [JsonPropertyName("scope")]
    [Description("Scope of diagnostics: 'file', 'project', or 'solution' (default: 'solution')")]
    public string? Scope { get; set; }

    [JsonPropertyName("filePath")]
    [Description("File path when scope is 'file', or to identify project when scope is 'project'")]
    public string? FilePath { get; set; }

    [JsonPropertyName("severities")]
    [Description("Filter by severities: 'Error', 'Warning', 'Info', 'Hidden'")]
    public string[]? Severities { get; set; }

    [JsonPropertyName("includeSyntax")]
    [Description("Include syntax diagnostics (default: true)")]
    public bool? IncludeSyntax { get; set; }

    [JsonPropertyName("includeSemantic")]
    [Description("Include semantic/compilation diagnostics (default: true)")]
    public bool? IncludeSemantic { get; set; }

    [JsonPropertyName("includeAnalyzers")]
    [Description("Include analyzer diagnostics (default: true)")]
    public bool? IncludeAnalyzers { get; set; }

    [JsonPropertyName("includeSuppressed")]
    [Description("Include suppressed diagnostics (default: false)")]
    public bool? IncludeSuppressed { get; set; }

    [JsonPropertyName("categoryFilter")]
    [Description("Filter by category (contains match)")]
    public string? CategoryFilter { get; set; }

    [JsonPropertyName("idFilter")]
    [Description("Filter by diagnostic ID (contains match, e.g., 'CS8' for nullable warnings)")]
    public string? IdFilter { get; set; }

    [JsonPropertyName("sortBy")]
    [Description("Sort diagnostics by: 'Severity' (default), 'File', 'Category'")]
    public string? SortBy { get; set; }

    [JsonPropertyName("groupBy")]
    [Description("Group diagnostics by: 'File', 'Severity', 'Category', 'Source'")]
    public string? GroupBy { get; set; }
}

public class GetDiagnosticsResult
{
    public bool Success { get; set; }
    public int TotalDiagnostics { get; set; }
    public List<DiagnosticInfo>? Diagnostics { get; set; }
    public Dictionary<string, List<DiagnosticInfo>>? GroupedDiagnostics { get; set; }
    public string? Message { get; set; }
    public List<string>? Insights { get; set; }
    public List<NextAction>? NextActions { get; set; }
    public ErrorInfo? Error { get; set; }
    public string? ResourceUri { get; set; }
}

public class DiagnosticInfo
{
    public required string Id { get; set; }
    public required string Category { get; set; }
    public required string Severity { get; set; }
    public required string Message { get; set; }
    public required string Source { get; set; }
    public string? FilePath { get; set; }
    public LocationInfo? Location { get; set; }
    public bool IsSuppressed { get; set; }
    public bool IsWarningAsError { get; set; }
    public bool HasCodeFix { get; set; }
    public List<string>? Tags { get; set; }
    public Dictionary<string, string?>? Properties { get; set; }
}