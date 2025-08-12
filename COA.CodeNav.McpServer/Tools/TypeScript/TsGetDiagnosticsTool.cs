using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders.TypeScript;
using COA.CodeNav.McpServer.Services.TypeScript;
using COA.Mcp.Framework;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using COA.Mcp.Framework.TokenOptimization.Models;
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools.TypeScript;

/// <summary>
/// Tool for getting TypeScript compilation diagnostics (errors, warnings, etc.)
/// </summary>
public class TsGetDiagnosticsTool : McpToolBase<TsGetDiagnosticsParams, TsGetDiagnosticsResult>
{
    private readonly ILogger<TsGetDiagnosticsTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptLanguageService _languageService;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly TsDiagnosticsResponseBuilder? _responseBuilder;

    public override string Name => ToolNames.TsGetDiagnostics;
    
    public override string Description => @"Get TypeScript compilation diagnostics (errors, warnings, info).
Returns: List of diagnostics with severity, location, and error messages.
Prerequisites: TypeScript project must be loaded via ts_load_tsconfig.
Error handling: Returns specific error codes if TypeScript is not installed or project not loaded.
Use cases: Finding compilation errors, checking code quality, identifying TypeScript issues.
Not for: Runtime errors, ESLint warnings (use different tools), JavaScript-only projects.";

    public override ToolCategory Category => ToolCategory.Diagnostics;

    public TsGetDiagnosticsTool(
        ILogger<TsGetDiagnosticsTool> logger,
        TypeScriptWorkspaceService workspaceService,
        TypeScriptLanguageService languageService,
        ITokenEstimator tokenEstimator,
        TsDiagnosticsResponseBuilder? responseBuilder = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _languageService = languageService;
        _tokenEstimator = tokenEstimator;
        _responseBuilder = responseBuilder;
    }

    protected override async Task<TsGetDiagnosticsResult> ExecuteInternalAsync(
        TsGetDiagnosticsParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        try
        {
            // Check if TypeScript workspace is loaded
            var workspace = parameters.WorkspaceId != null 
                ? _workspaceService.GetWorkspace(parameters.WorkspaceId)
                : _workspaceService.GetActiveWorkspace();

            if (workspace == null)
            {
                return new TsGetDiagnosticsResult
                {
                    Success = false,
                    Message = "No TypeScript workspace loaded",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.TS_PROJECT_NOT_LOADED,
                        Message = "No TypeScript project is currently loaded",
                        Recovery = new RecoveryInfo
                        {
                            Steps = new[]
                            {
                                "Load a TypeScript project using ts_load_tsconfig",
                                "Ensure the tsconfig.json file exists",
                                "Check that TypeScript is installed"
                            },
                            SuggestedActions = new List<SuggestedAction>
                            {
                                new()
                                {
                                    Tool = ToolNames.TsLoadTsConfig,
                                    Description = "Load a TypeScript project",
                                    Parameters = new Dictionary<string, object>
                                    {
                                        ["tsConfigPath"] = "<path-to-tsconfig.json>"
                                    }
                                }
                            }
                        }
                    },
                    Meta = CreateMetadata(startTime, false)
                };
            }

            // Get diagnostics
            var diagnostics = await _languageService.GetDiagnosticsAsync(
                parameters.FilePath,
                parameters.WorkspaceId,
                cancellationToken);

            // Filter by severity if requested
            if (parameters.Severities != null && parameters.Severities.Count > 0)
            {
                var severitySet = new HashSet<string>(parameters.Severities, StringComparer.OrdinalIgnoreCase);
                diagnostics = diagnostics.Where(d => severitySet.Contains(d.Category)).ToList();
            }

            // Filter by category if requested
            if (!string.IsNullOrEmpty(parameters.CategoryFilter))
            {
                diagnostics = diagnostics.Where(d => 
                    d.Category.Contains(parameters.CategoryFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Sort diagnostics
            diagnostics = SortDiagnostics(diagnostics, parameters.SortBy ?? "file");
            
            // Track total before limiting
            var totalDiagnostics = diagnostics.Count;
            var hasMore = false;
            
            // Apply MaxResults limit before creating result
            if (parameters.MaxResults > 0 && diagnostics.Count > parameters.MaxResults)
            {
                diagnostics = diagnostics.Take(parameters.MaxResults).ToList();
                hasMore = true;
            }

            // Create initial result
            var result = new TsGetDiagnosticsResult
            {
                Success = true,
                Query = new QueryInfo
                {
                    Workspace = workspace.WorkspaceId,
                    FilePath = parameters.FilePath,
                    Position = null,
                    TargetSymbol = null
                },
                Summary = new DiagnosticsSummary
                {
                    TotalFound = totalDiagnostics,
                    Returned = diagnostics.Count,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    ErrorCount = 0,
                    WarningCount = 0,
                    InfoCount = 0
                },
                Diagnostics = diagnostics,
                Distribution = _languageService.CategorizeDiagnostics(diagnostics),
                ResultsSummary = new ResultsSummary
                {
                    Included = diagnostics.Count,
                    Total = totalDiagnostics,
                    HasMore = hasMore
                }
            };

            // Update summary counts
            foreach (var diagnostic in diagnostics)
            {
                switch (diagnostic.Category.ToLowerInvariant())
                {
                    case "error":
                        result.Summary.ErrorCount++;
                        break;
                    case "warning":
                        result.Summary.WarningCount++;
                        break;
                    case "info":
                    case "information":
                        result.Summary.InfoCount++;
                        break;
                }
            }

            // Apply token optimization if response builder is available
            if (_responseBuilder != null)
            {
                var context = new ResponseContext
                {
                    TokenLimit = parameters.MaxResults > 0 ? parameters.MaxResults * 100 : 10000,
                    ResponseMode = "optimized"
                };
                
                result = await _responseBuilder.BuildResponseAsync(result, context);
            }

            // Generate insights
            result.Insights = GenerateInsights(result);

            // Generate actions
            result.Actions = GenerateActions(result);

            // Set final metadata (only if not already set by response builder)
            if (result.Meta == null)
            {
                result.Meta = CreateMetadata(startTime, result.ResultsSummary?.HasMore ?? false);
            }
            result.Message = BuildSummaryMessage(result);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get TypeScript diagnostics");
            
            return new TsGetDiagnosticsResult
            {
                Success = false,
                Message = $"Failed to get TypeScript diagnostics: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Message = ex.Message
                },
                Meta = CreateMetadata(startTime, false)
            };
        }
    }

    private List<TsDiagnostic> SortDiagnostics(List<TsDiagnostic> diagnostics, string sortBy)
    {
        return sortBy.ToLowerInvariant() switch
        {
            "severity" => diagnostics.OrderBy(d => GetSeverityOrder(d.Category))
                                    .ThenBy(d => d.FilePath)
                                    .ThenBy(d => d.Start?.Line ?? 0)
                                    .ToList(),
            "code" => diagnostics.OrderBy(d => d.Code)
                                .ThenBy(d => d.FilePath)
                                .ThenBy(d => d.Start?.Line ?? 0)
                                .ToList(),
            _ => diagnostics.OrderBy(d => d.FilePath)
                          .ThenBy(d => d.Start?.Line ?? 0)
                          .ThenBy(d => d.Start?.Character ?? 0)
                          .ToList()
        };
    }

    private int GetSeverityOrder(string category)
    {
        return category.ToLowerInvariant() switch
        {
            "error" => 0,
            "warning" => 1,
            "info" => 2,
            "hint" => 3,
            _ => 4
        };
    }

    private List<string> GenerateInsights(TsGetDiagnosticsResult result)
    {
        var insights = new List<string>();

        if (result.Diagnostics == null || result.Diagnostics.Count == 0)
        {
            insights.Add("✅ No TypeScript compilation issues found");
            return insights;
        }

        var errorCount = result.Summary?.ErrorCount ?? 0;
        var warningCount = result.Summary?.WarningCount ?? 0;

        if (errorCount > 0)
        {
            insights.Add($"❌ {errorCount} error{(errorCount > 1 ? "s" : "")} preventing compilation");
        }

        if (warningCount > 0)
        {
            insights.Add($"⚠️ {warningCount} warning{(warningCount > 1 ? "s" : "")} to address");
        }

        // Analyze common error patterns
        var commonIssues = AnalyzeCommonIssues(result.Diagnostics);
        insights.AddRange(commonIssues);

        if (result.ResultsSummary?.HasMore == true)
        {
            insights.Add($"Results truncated - showing {result.ResultsSummary.Included} of {result.ResultsSummary.Total} diagnostics");
        }

        return insights;
    }

    private List<string> AnalyzeCommonIssues(List<TsDiagnostic> diagnostics)
    {
        var insights = new List<string>();
        
        // Count common error types
        var typeMismatches = diagnostics.Count(d => d.Code >= 2300 && d.Code < 2400);
        var missingImports = diagnostics.Count(d => d.Code == 2304 || d.Code == 2305);
        var unusedVars = diagnostics.Count(d => d.Code == 6133);
        
        if (typeMismatches > 3)
        {
            insights.Add($"Multiple type mismatches ({typeMismatches}) - check type definitions");
        }
        
        if (missingImports > 0)
        {
            insights.Add($"Missing imports or undefined names ({missingImports}) - add required imports");
        }
        
        if (unusedVars > 0)
        {
            insights.Add($"Unused variables/parameters ({unusedVars}) - consider removing unused code");
        }

        return insights;
    }

    private List<AIAction> GenerateActions(TsGetDiagnosticsResult result)
    {
        var actions = new List<AIAction>();

        if (result.Diagnostics?.Any(d => d.Category == "error") == true)
        {
            actions.Add(new AIAction
            {
                Action = "ts_apply_quick_fix",
                Description = "Apply TypeScript quick fixes for errors",
                Category = "fix",
                Priority = 10
            });
        }

        if (result.Diagnostics?.Any(d => d.Code == 6133) == true) // Unused variables
        {
            actions.Add(new AIAction
            {
                Action = "ts_remove_unused",
                Description = "Remove unused variables and imports",
                Category = "cleanup",
                Priority = 8
            });
        }

        if (result.Diagnostics?.Any(d => d.Code == 2304 || d.Code == 2305) == true) // Missing imports
        {
            actions.Add(new AIAction
            {
                Action = "ts_add_missing_imports",
                Description = "Add missing import statements",
                Category = "fix",
                Priority = 9
            });
        }

        actions.Add(new AIAction
        {
            Action = "ts_organize_imports",
            Description = "Organize and sort import statements",
            Category = "cleanup",
            Priority = 5
        });

        return actions;
    }

    private string BuildSummaryMessage(TsGetDiagnosticsResult result)
    {
        if (result.Diagnostics == null || result.Diagnostics.Count == 0)
        {
            return "No TypeScript compilation issues found";
        }

        var parts = new List<string>();
        
        if (result.Summary != null)
        {
            if (result.Summary.ErrorCount > 0)
                parts.Add($"{result.Summary.ErrorCount} error{(result.Summary.ErrorCount > 1 ? "s" : "")}");
            if (result.Summary.WarningCount > 0)
                parts.Add($"{result.Summary.WarningCount} warning{(result.Summary.WarningCount > 1 ? "s" : "")}");
            if (result.Summary.InfoCount > 0)
                parts.Add($"{result.Summary.InfoCount} info{(result.Summary.InfoCount > 1 ? "s" : "")}");
        }

        return $"Found {string.Join(", ", parts)}";
    }

    private ToolExecutionMetadata CreateMetadata(DateTime startTime, bool truncated)
    {
        return new ToolExecutionMetadata
        {
            Mode = "full",
            Truncated = truncated,
            ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
        };
    }
}

/// <summary>
/// Parameters for getting TypeScript diagnostics
/// </summary>
public class TsGetDiagnosticsParams
{
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("severities")]
    public List<string>? Severities { get; set; }

    [JsonPropertyName("categoryFilter")]
    public string? CategoryFilter { get; set; }

    [JsonPropertyName("includeSemanticDiagnostics")]
    public bool IncludeSemanticDiagnostics { get; set; } = true;

    [JsonPropertyName("includeSyntaxDiagnostics")]
    public bool IncludeSyntaxDiagnostics { get; set; } = true;

    [JsonPropertyName("maxResults")]
    public int MaxResults { get; set; } = 100;

    [JsonPropertyName("sortBy")]
    public string? SortBy { get; set; } = "file";
}