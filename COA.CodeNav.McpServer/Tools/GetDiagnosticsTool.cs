using System.Collections.Immutable;
using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// Tool that gets compilation errors, warnings, and analyzer diagnostics
/// </summary>
public class GetDiagnosticsTool : McpToolBase<GetDiagnosticsParams, GetDiagnosticsToolResult>
{
    private readonly ILogger<GetDiagnosticsTool> _logger;
    
    public override ToolCategory Category => ToolCategory.Diagnostics;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;
    private readonly DiagnosticsResponseBuilder _responseBuilder;
    private readonly ITokenEstimator _tokenEstimator;

    // Result management constant
    private const int MAX_DIAGNOSTICS_PER_RESPONSE = 50;

    public override string Name => ToolNames.GetDiagnostics;
    public override string Description => @"Get compilation errors, warnings, and analyzer diagnostics for files, projects, or the entire solution.
Returns: List of diagnostics with severity, location, and suggested fixes.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if workspace is not loaded.
Use cases: Finding compilation errors, checking code quality, identifying warnings, running analyzers.
Not for: Code metrics (use future csharp_code_metrics), finding specific symbols (use csharp_symbol_search).";

    public GetDiagnosticsTool(
        ILogger<GetDiagnosticsTool> logger,
        RoslynWorkspaceService workspaceService,
        DiagnosticsResponseBuilder responseBuilder,
        ITokenEstimator tokenEstimator,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _responseBuilder = responseBuilder;
        _tokenEstimator = tokenEstimator;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<GetDiagnosticsToolResult> ExecuteInternalAsync(
        GetDiagnosticsParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        _logger.LogInformation("Processing GetDiagnostics with scope: {Scope}", parameters.Scope);

        // Validate parameters first
        if (parameters.Scope?.ToLower() == "file" && string.IsNullOrEmpty(parameters.FilePath))
        {
            return CreateInvalidParametersResult("File path is required when scope is 'file'", parameters, startTime);
        }

        // Check if any workspaces are loaded
        var workspaces = _workspaceService.GetActiveWorkspaces();
        if (!workspaces.Any())
        {
            return CreateWorkspaceNotLoadedResult(parameters, startTime);
        }

        var allDiagnostics = new List<DiagnosticInfo>();
        var projects = new List<Project>();

        // Determine which projects to analyze
        switch (parameters.Scope?.ToLower())
        {
            case "file":

                var document = await _workspaceService.GetDocumentAsync(parameters.FilePath!, parameters.ForceRefresh ?? false);
                if (document == null)
                {
                    return CreateDocumentNotFoundResult(parameters, startTime);
                }

                var fileDiagnostics = await GetDocumentDiagnosticsAsync(document, parameters, cancellationToken);
                allDiagnostics.AddRange(fileDiagnostics);
                break;

            case "project":
                if (!string.IsNullOrEmpty(parameters.FilePath))
                {
                    var projectDoc = await _workspaceService.GetDocumentAsync(parameters.FilePath, parameters.ForceRefresh ?? false);
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

        // Generate insights
        var insights = GenerateInsights(allDiagnostics);

        // Apply token management using Framework's patterns
        var totalDiagnostics = allDiagnostics.Count;
        var requestedMaxResults = parameters.MaxResults ?? MAX_DIAGNOSTICS_PER_RESPONSE;

        // First, determine the actual number to return based on request
        var maxResults = Math.Min(requestedMaxResults, 500); // Hard limit of 500
        List<DiagnosticInfo> returnedDiagnostics;
        var tokenLimitApplied = false;

        // Use framework's token optimization
        var estimatedTokens = _tokenEstimator.EstimateObject(allDiagnostics);
        if (estimatedTokens > 10000)
        {
            // Use framework's progressive reduction
            returnedDiagnostics = _tokenEstimator.ApplyProgressiveReduction(
                allDiagnostics,
                diagnostic => _tokenEstimator.EstimateObject(diagnostic),
                10000,
                new[] { 50, 40, 30, 20, 10 } // Progressive reduction steps
            );
            var effectiveLimit = Math.Min(returnedDiagnostics.Count, maxResults);
            returnedDiagnostics = returnedDiagnostics.Take(effectiveLimit).ToList();
            tokenLimitApplied = true;
            
            _logger.LogWarning("Token optimization applied: reducing diagnostics from {Total} to {Safe}", 
                allDiagnostics.Count, returnedDiagnostics.Count);
        }
        else if (totalDiagnostics > maxResults)
        {
            returnedDiagnostics = allDiagnostics.Take(maxResults).ToList();
        }
        else
        {
            returnedDiagnostics = allDiagnostics;
        }

        var shouldTruncate = totalDiagnostics > returnedDiagnostics.Count;

        // Store full results in resource provider for retrieval
        var resourceUri = _resourceProvider?.StoreAnalysisResult("diagnostics",
            new
            {
                diagnostics = allDiagnostics,
                query = parameters,
                timestamp = DateTime.UtcNow
            },
            $"Full diagnostics for {parameters.Scope ?? "solution"} ({totalDiagnostics} total)");

        // Count diagnostics by severity (from full set)
        var errorCount = allDiagnostics.Count(d => d.Severity == "Error");
        var warningCount = allDiagnostics.Count(d => d.Severity == "Warning");
        var infoCount = allDiagnostics.Count(d => d.Severity == "Info");

        // Add insight about truncation if applicable
        if (shouldTruncate)
        {
            if (tokenLimitApplied)
            {
                insights.Insert(0, $"⚠️ Token limit applied. Showing {returnedDiagnostics.Count} of {totalDiagnostics} diagnostics.");
            }
            else
            {
                insights.Insert(0, $"Showing {returnedDiagnostics.Count} of {totalDiagnostics} diagnostics (max results limit)");
            }
            if (resourceUri != null)
            {
                insights.Add($"Full results available at resource: {resourceUri}");
                var totalPages = (int)Math.Ceiling((double)totalDiagnostics / 100); // Resource provider uses 100 items per page
                if (totalPages > 1)
                {
                    insights.Add($"Resource contains {totalPages} pages (100 diagnostics per page). Access pages: {resourceUri}/page/1 to {resourceUri}/page/{totalPages}");
                }
            }
        }

        // Generate next actions
        var nextActions = GenerateNextActions(allDiagnostics, parameters, shouldTruncate, totalDiagnostics);

        // Build complete result first
        var completeResult = new GetDiagnosticsToolResult
        {
            Success = true,
            Message = FormatSummaryMessage(allDiagnostics),
            Diagnostics = returnedDiagnostics,
            Query = new DiagnosticsQuery
            {
                Scope = parameters.Scope ?? "solution",
                FilePath = parameters.FilePath,
                Severities = parameters.Severities?.ToList(),
                Categories = parameters.CategoryFilter != null ? new List<string> { parameters.CategoryFilter } : null
            },
            Summary = new DiagnosticsSummary
            {
                TotalFound = allDiagnostics.Count,
                Returned = returnedDiagnostics.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                ErrorCount = errorCount,
                WarningCount = warningCount,
                InfoCount = infoCount
            },
            ResultsSummary = new ResultsSummary
            {
                Included = returnedDiagnostics.Count,
                Total = totalDiagnostics,
                HasMore = shouldTruncate
            },
            Distribution = new DiagnosticsDistribution
            {
                BySeverity = allDiagnostics.GroupBy(d => d.Severity)
                    .ToDictionary(g => g.Key, g => g.Count()),
                ByCategory = allDiagnostics.GroupBy(d => d.Category ?? "General")
                    .ToDictionary(g => g.Key, g => g.Count()),
                ByFile = allDiagnostics.Where(d => d.Location != null)
                    .GroupBy(d => Path.GetFileName(d.Location!.FilePath))
                    .ToDictionary(g => g.Key, g => g.Count())
            },
            Insights = insights,
            Actions = nextActions,
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                Truncated = shouldTruncate
            },
            ResourceUri = resourceUri
        };

        // Use ResponseBuilder for token optimization and AI-friendly formatting
        var context = new COA.Mcp.Framework.TokenOptimization.ResponseBuilders.ResponseContext
        {
            ResponseMode = "optimized",
            TokenLimit = parameters.MaxResults.HasValue ? parameters.MaxResults * 200 : 12000,
            ToolName = Name
        };

        return (GetDiagnosticsToolResult)await _responseBuilder.BuildResponseAsync(completeResult, context);
    }

    private GetDiagnosticsToolResult CreateWorkspaceNotLoadedResult(GetDiagnosticsParams parameters, DateTime startTime)
    {
        return new GetDiagnosticsToolResult
        {
            Success = false,
            Message = "No workspace loaded. Please load a solution or project first.",
            Error = new ErrorInfo
            {
                Code = ErrorCodes.WORKSPACE_NOT_LOADED,
                Message = "No workspace loaded",
                Recovery = new RecoveryInfo
                {
                    Steps = new[]
                    {
                        "Load a solution using csharp_load_solution",
                        "Or load a project using csharp_load_project",
                        "Then retry getting diagnostics"
                    },
                    SuggestedActions = new List<SuggestedAction>
                    {
                        new SuggestedAction
                        {
                            Tool = "csharp_load_solution",
                            Description = "Load a solution file",
                            Parameters = new { solutionPath = "<path-to-your-solution.sln>" }
                        }
                    }
                }
            },
            Query = new DiagnosticsQuery
            {
                Scope = parameters.Scope ?? "solution",
                FilePath = parameters.FilePath
            },
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
    }

    private GetDiagnosticsToolResult CreateInvalidParametersResult(string message, GetDiagnosticsParams parameters, DateTime startTime)
    {
        return new GetDiagnosticsToolResult
        {
            Success = false,
            Message = message,
            Error = new ErrorInfo
            {
                Code = ErrorCodes.INVALID_PARAMETERS,
                Message = message,
                Recovery = new RecoveryInfo
                {
                    Steps = new[]
                    {
                        "Provide a file path when using 'file' scope",
                        "Or use 'project' or 'solution' scope"
                    }
                }
            },
            Query = new DiagnosticsQuery
            {
                Scope = parameters.Scope ?? "solution",
                FilePath = parameters.FilePath
            },
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
    }

    private GetDiagnosticsToolResult CreateDocumentNotFoundResult(GetDiagnosticsParams parameters, DateTime startTime)
    {
        return new GetDiagnosticsToolResult
        {
            Success = false,
            Message = $"Document not found: {parameters.FilePath}",
            Error = new ErrorInfo
            {
                Code = ErrorCodes.DOCUMENT_NOT_FOUND,
                Message = $"Document not found: {parameters.FilePath}",
                Recovery = new RecoveryInfo
                {
                    Steps = new[]
                    {
                        "Ensure the file path is correct and absolute",
                        "Verify the file is part of the loaded solution/project"
                    }
                }
            },
            Query = new DiagnosticsQuery
            {
                Scope = parameters.Scope ?? "solution",
                FilePath = parameters.FilePath
            },
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
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

    private List<AIAction> GenerateNextActions(List<DiagnosticInfo> diagnostics, GetDiagnosticsParams parameters, bool wasTruncated, int totalCount)
    {
        var actions = new List<AIAction>();

        if (wasTruncated)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_get_diagnostics",
                Description = $"Get more diagnostics (up to 500)",
                Parameters = new Dictionary<string, object>
                {
                    ["scope"] = parameters.Scope ?? "solution",
                    ["filePath"] = parameters.FilePath ?? "",
                    ["maxResults"] = Math.Min(totalCount, 500)
                },
                Priority = 90
            });
        }

        // Suggest fixing errors first
        var firstError = diagnostics.FirstOrDefault(d => d.Severity == "Error" && d.Location != null);
        if (firstError?.Location != null)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_goto_definition",
                Description = $"Go to first error: {firstError.Id}",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = firstError.Location.FilePath ?? "",
                    ["line"] = firstError.Location.Line,
                    ["column"] = firstError.Location.Column
                },
                Priority = 80
            });
        }

        // Suggest viewing file with most diagnostics
        var worstFile = diagnostics.Where(d => d.FilePath != null)
                                  .GroupBy(d => d.FilePath)
                                  .OrderByDescending(g => g.Count())
                                  .FirstOrDefault();
        if (worstFile != null && worstFile.Count() > 3)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_document_symbols",
                Description = $"View file with most issues: {Path.GetFileName(worstFile.Key!)}",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = worstFile.Key!
                },
                Priority = 60
            });
        }

        // Suggest running specific analyzer categories
        var categories = diagnostics.Select(d => d.Category).Distinct().Take(3);
        foreach (var category in categories)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_get_diagnostics",
                Description = $"Show only {category} diagnostics",
                Parameters = new Dictionary<string, object>
                {
                    ["categoryFilter"] = category,
                    ["scope"] = "solution"
                },
                Priority = 40
            });
        }

        return actions;
    }

}

public class GetDiagnosticsParams
{
    [JsonPropertyName("scope")]
    [COA.Mcp.Framework.Attributes.Description("Scope of diagnostics: 'file', 'project', or 'solution' (default: 'solution')")]
    public string? Scope { get; set; }

    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("File path when scope is 'file', or to identify project when scope is 'project'")]
    public string? FilePath { get; set; }

    [JsonPropertyName("severities")]
    [COA.Mcp.Framework.Attributes.Description("Filter by severities: 'Error', 'Warning', 'Info', 'Hidden'")]
    public string[]? Severities { get; set; }

    [JsonPropertyName("includeSyntax")]
    [COA.Mcp.Framework.Attributes.Description("Include syntax diagnostics (default: true)")]
    public bool? IncludeSyntax { get; set; }

    [JsonPropertyName("includeSemantic")]
    [COA.Mcp.Framework.Attributes.Description("Include semantic/compilation diagnostics (default: true)")]
    public bool? IncludeSemantic { get; set; }

    [JsonPropertyName("includeAnalyzers")]
    [COA.Mcp.Framework.Attributes.Description("Include analyzer diagnostics (default: true)")]
    public bool? IncludeAnalyzers { get; set; }

    [JsonPropertyName("includeSuppressed")]
    [COA.Mcp.Framework.Attributes.Description("Include suppressed diagnostics (default: false)")]
    public bool? IncludeSuppressed { get; set; }

    [JsonPropertyName("categoryFilter")]
    [COA.Mcp.Framework.Attributes.Description("Filter by category (contains match)")]
    public string? CategoryFilter { get; set; }

    [JsonPropertyName("idFilter")]
    [COA.Mcp.Framework.Attributes.Description("Filter by diagnostic ID (contains match, e.g., 'CS8' for nullable warnings)")]
    public string? IdFilter { get; set; }

    [JsonPropertyName("sortBy")]
    [COA.Mcp.Framework.Attributes.Description("Sort diagnostics by: 'Severity' (default), 'File', 'Category'")]
    public string? SortBy { get; set; }

    [JsonPropertyName("groupBy")]
    [COA.Mcp.Framework.Attributes.Description("Group diagnostics by: 'File', 'Severity', 'Category', 'Source'")]
    public string? GroupBy { get; set; }

    [JsonPropertyName("maxResults")]
    [System.ComponentModel.DataAnnotations.Range(1, 500)]
    [COA.Mcp.Framework.Attributes.Description("Maximum number of diagnostics to return (default: 50, max: 500)")]
    public int? MaxResults { get; set; }

    [JsonPropertyName("forceRefresh")]
    [COA.Mcp.Framework.Attributes.Description("Force refresh documents from disk before analysis (default: false)")]
    public bool? ForceRefresh { get; set; }
}