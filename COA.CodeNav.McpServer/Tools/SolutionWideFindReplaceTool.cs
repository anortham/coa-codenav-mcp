using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that performs solution-wide find and replace operations
/// </summary>
[McpServerToolType]
public class SolutionWideFindReplaceTool : ITool
{
    private readonly ILogger<SolutionWideFindReplaceTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "csharp_solution_wide_find_replace";
    public string Description => "Perform find and replace operations across the entire solution";

    public SolutionWideFindReplaceTool(
        ILogger<SolutionWideFindReplaceTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "csharp_solution_wide_find_replace")]
    [Description(@"Perform find and replace operations across the entire solution with preview and filtering.
Returns: List of files that would be modified, with before/after snippets and change counts.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if pattern is invalid.
Use cases: Bulk renaming, pattern replacement, code modernization, fixing deprecated APIs.
AI benefit: Enables large-scale refactoring that would be tedious to do file by file.")]
    public async Task<object> ExecuteAsync(SolutionWideFindReplaceParams parameters, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("SolutionWideFindReplace request: Pattern={Pattern}, Replacement={Replacement}, Preview={Preview}", 
            parameters.FindPattern, parameters.ReplacePattern, parameters.Preview);
            
        try
        {
            // Validate parameters
            if (string.IsNullOrEmpty(parameters.FindPattern))
            {
                return CreateErrorResult(
                    ErrorCodes.INVALID_PARAMETERS,
                    "Find pattern cannot be empty",
                    new List<string>
                    {
                        "Provide a non-empty find pattern",
                        "For regex patterns, ensure they are properly escaped",
                        "Use literal search for exact text matching"
                    },
                    parameters,
                    startTime);
            }

            // Get the solution
            var workspaces = _workspaceService.GetActiveWorkspaces();
            var workspace = workspaces.FirstOrDefault();
            if (workspace == null)
            {
                return CreateErrorResult(
                    ErrorCodes.WORKSPACE_NOT_LOADED,
                    "No workspace loaded",
                    new List<string>
                    {
                        "Use csharp_load_solution to load a solution first",
                        "Use csharp_load_project to load a project",
                        "Verify the solution path is correct"
                    },
                    parameters,
                    startTime);
            }

            var solution = workspace.Solution;
            _logger.LogInformation("Performing find/replace across solution with {ProjectCount} projects", 
                solution.Projects.Count());

            // Compile regex if needed
            Regex? regex = null;
            if (parameters.UseRegex)
            {
                try
                {
                    var options = RegexOptions.Compiled;
                    if (!parameters.CaseSensitive)
                        options |= RegexOptions.IgnoreCase;
                    if (parameters.Multiline)
                        options |= RegexOptions.Multiline;
                    
                    regex = new Regex(parameters.FindPattern, options);
                }
                catch (ArgumentException ex)
                {
                    return CreateErrorResult(
                        ErrorCodes.INVALID_PARAMETERS,
                        $"Invalid regex pattern: {ex.Message}",
                        new List<string>
                        {
                            "Check your regex syntax",
                            "Escape special characters properly",
                            "Test your regex pattern in a regex tester",
                            "Consider using literal search instead"
                        },
                        parameters,
                        startTime);
                }
            }

            // Find all matches
            var allMatches = await FindMatchesAsync(
                solution,
                parameters.FindPattern,
                regex,
                parameters.CaseSensitive,
                parameters.WholeWord,
                parameters.FilePattern,
                parameters.ExcludePattern,
                cancellationToken);

            if (!allMatches.Any())
            {
                return new SolutionWideFindReplaceResult
                {
                    Success = true,
                    Message = $"No matches found for pattern '{parameters.FindPattern}'",
                    Query = CreateQueryInfo(parameters),
                    Summary = new SummaryInfo
                    {
                        TotalFound = 0,
                        Returned = 0,
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                    },
                    Changes = new List<FileChangePreview>(),
                    Analysis = new FindReplaceAnalysis
                    {
                        TotalFiles = 0,
                        TotalMatches = 0,
                        TotalReplacements = 0
                    },
                    Insights = new List<string> { "No matches found - verify your search pattern and filters" },
                    Actions = GenerateNoMatchesActions(parameters),
                    Meta = new ToolMetadata
                    {
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                    }
                };
            }

            // Generate replacements
            var changes = GenerateChanges(allMatches, parameters.ReplacePattern, regex);

            // Apply token management
            var response = TokenEstimator.CreateTokenAwareResponse(
                changes,
                c => EstimateChangesTokens(c),
                requestedMax: parameters.MaxFiles ?? 100,
                safetyLimit: TokenEstimator.DEFAULT_SAFETY_LIMIT,
                toolName: "csharp_solution_wide_find_replace"
            );

            // Apply changes if not preview mode
            bool applied = false;
            if (!parameters.Preview)
            {
                applied = await ApplyChangesAsync(response.Items, workspace, cancellationToken);
            }

            // Generate analysis
            var analysis = GenerateAnalysis(allMatches, changes);

            // Generate insights
            var insights = GenerateInsights(analysis, parameters);
            if (response.WasTruncated)
            {
                insights.Insert(0, response.GetTruncationMessage());
            }

            // Generate next actions
            var nextActions = GenerateNextActions(parameters, analysis, applied);
            if (response.WasTruncated && parameters.Preview)
            {
                nextActions.Insert(0, new NextAction
                {
                    Id = "see_all_changes",
                    Description = "See all files that would be modified",
                    ToolName = "csharp_solution_wide_find_replace",
                    Parameters = new
                    {
                        findPattern = parameters.FindPattern,
                        replacePattern = parameters.ReplacePattern,
                        useRegex = parameters.UseRegex,
                        caseSensitive = parameters.CaseSensitive,
                        filePattern = parameters.FilePattern,
                        maxFiles = changes.Count,
                        preview = true
                    },
                    Priority = "high"
                });
            }

            // Store full result if truncated
            string? resourceUri = null;
            if (response.WasTruncated && _resourceProvider != null)
            {
                resourceUri = _resourceProvider.StoreAnalysisResult(
                    "find-replace-changes",
                    new { 
                        findPattern = parameters.FindPattern,
                        replacePattern = parameters.ReplacePattern,
                        changes = changes,
                        totalFiles = changes.Count,
                        totalMatches = analysis.TotalMatches
                    },
                    $"All {changes.Count} files with matches for '{parameters.FindPattern}'");
            }

            var result = new SolutionWideFindReplaceResult
            {
                Success = true,
                Message = GenerateResultMessage(parameters, analysis, applied, response.WasTruncated),
                Query = CreateQueryInfo(parameters),
                Summary = new SummaryInfo
                {
                    TotalFound = changes.Count,
                    Returned = response.ReturnedCount,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                },
                Applied = applied,
                Changes = response.Items,
                Analysis = analysis,
                Insights = insights,
                Actions = nextActions,
                ResourceUri = resourceUri,
                Meta = new ToolMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    Truncated = response.WasTruncated,
                    Tokens = response.EstimatedTokens
                }
            };

            _logger.LogInformation("Find/replace completed: {TotalMatches} matches in {TotalFiles} files",
                analysis.TotalMatches, analysis.TotalFiles);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SolutionWideFindReplace");
            return CreateErrorResult(
                ErrorCodes.INTERNAL_ERROR,
                $"Error: {ex.Message}",
                new List<string>
                {
                    "Check the server logs for detailed error information",
                    "Verify the solution is loaded correctly",
                    "Try the operation again with a simpler pattern"
                },
                parameters,
                startTime);
        }
    }

    private async Task<List<FileMatches>> FindMatchesAsync(
        Solution solution,
        string findPattern,
        Regex? regex,
        bool caseSensitive,
        bool wholeWord,
        string? filePattern,
        string? excludePattern,
        CancellationToken cancellationToken)
    {
        var matches = new List<FileMatches>();
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                // Skip if doesn't match file pattern
                if (!MatchesFilePattern(document.FilePath, filePattern, excludePattern))
                    continue;

                var text = await document.GetTextAsync(cancellationToken);
                var content = text.ToString();
                var documentMatches = new List<Match>();

                if (regex != null)
                {
                    // Regex search
                    var regexMatches = regex.Matches(content);
                    foreach (System.Text.RegularExpressions.Match match in regexMatches)
                    {
                        documentMatches.Add(new Match
                        {
                            Start = match.Index,
                            Length = match.Length,
                            Value = match.Value,
                            Line = text.Lines.GetLineFromPosition(match.Index).LineNumber + 1
                        });
                    }
                }
                else
                {
                    // Literal search
                    var searchText = findPattern;
                    var index = 0;
                    
                    while ((index = content.IndexOf(searchText, index, comparison)) != -1)
                    {
                        // Check whole word if needed
                        if (wholeWord && !IsWholeWord(content, index, searchText.Length))
                        {
                            index++;
                            continue;
                        }

                        documentMatches.Add(new Match
                        {
                            Start = index,
                            Length = searchText.Length,
                            Value = searchText,
                            Line = text.Lines.GetLineFromPosition(index).LineNumber + 1
                        });
                        
                        index += searchText.Length;
                    }
                }

                if (documentMatches.Any())
                {
                    matches.Add(new FileMatches
                    {
                        FilePath = document.FilePath ?? "<unknown>",
                        ProjectName = project.Name,
                        Matches = documentMatches,
                        OriginalText = text
                    });
                }
            }
        }

        return matches;
    }

    private List<FileChangePreview> GenerateChanges(
        List<FileMatches> allMatches,
        string replacePattern,
        Regex? regex)
    {
        var changes = new List<FileChangePreview>();

        foreach (var fileMatch in allMatches)
        {
            var newText = fileMatch.OriginalText.ToString();
            var replacements = new List<ReplacementInfo>();

            // Sort matches in reverse order to avoid offset issues
            var sortedMatches = fileMatch.Matches.OrderByDescending(m => m.Start).ToList();

            foreach (var match in sortedMatches)
            {
                var replacement = regex != null
                    ? regex.Replace(match.Value, replacePattern)
                    : replacePattern;

                // Calculate line context
                var line = fileMatch.OriginalText.Lines.GetLineFromPosition(match.Start);
                var lineText = line.ToString();
                var columnStart = match.Start - line.Start + 1;

                replacements.Insert(0, new ReplacementInfo
                {
                    Line = match.Line,
                    Column = columnStart,
                    OldText = match.Value,
                    NewText = replacement,
                    LineBefore = lineText,
                    LineAfter = lineText.Substring(0, columnStart - 1) + 
                               replacement + 
                               lineText.Substring(columnStart - 1 + match.Length)
                });

                // Apply replacement to generate new text
                newText = newText.Remove(match.Start, match.Length).Insert(match.Start, replacement);
            }

            changes.Add(new FileChangePreview
            {
                FilePath = fileMatch.FilePath,
                ProjectName = fileMatch.ProjectName,
                MatchCount = fileMatch.Matches.Count,
                Replacements = replacements,
                NewContent = newText
            });
        }

        return changes;
    }

    private Task<bool> ApplyChangesAsync(
        List<FileChangePreview> changes,
        WorkspaceInfo workspaceInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            var solution = workspaceInfo.Solution;
            var workspace = solution.Workspace;

            foreach (var change in changes)
            {
                var documents = solution.Projects
                    .SelectMany(p => p.Documents)
                    .Where(d => d.FilePath == change.FilePath);

                foreach (var document in documents)
                {
                    var newText = SourceText.From(change.NewContent);
                    solution = solution.WithDocumentText(document.Id, newText);
                }
            }

            var success = workspace.TryApplyChanges(solution);
            if (!success)
            {
                _logger.LogWarning("Failed to apply some changes to workspace");
            }

            return Task.FromResult(success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying changes");
            return Task.FromResult(false);
        }
    }

    private bool MatchesFilePattern(string? filePath, string? includePattern, string? excludePattern)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        // Check exclude pattern first
        if (!string.IsNullOrEmpty(excludePattern))
        {
            var excludeRegex = new Regex(GlobToRegex(excludePattern), RegexOptions.IgnoreCase);
            if (excludeRegex.IsMatch(filePath))
                return false;
        }

        // Check include pattern
        if (!string.IsNullOrEmpty(includePattern))
        {
            var includeRegex = new Regex(GlobToRegex(includePattern), RegexOptions.IgnoreCase);
            return includeRegex.IsMatch(filePath);
        }

        return true;
    }

    private string GlobToRegex(string glob)
    {
        // Simple glob to regex conversion
        return "^" + Regex.Escape(glob)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/\\\\]*")
            .Replace("\\?", ".") + "$";
    }

    private bool IsWholeWord(string content, int index, int length)
    {
        // Check if match is a whole word
        bool startOk = index == 0 || !char.IsLetterOrDigit(content[index - 1]);
        bool endOk = index + length >= content.Length || !char.IsLetterOrDigit(content[index + length]);
        return startOk && endOk;
    }

    private int EstimateChangesTokens(List<FileChangePreview> changes)
    {
        return TokenEstimator.EstimateCollection(
            changes,
            change => {
                var tokens = 100; // Base for file structure
                tokens += TokenEstimator.EstimateString(change.FilePath);
                tokens += change.Replacements.Sum(r => 
                    TokenEstimator.EstimateString(r.LineBefore) +
                    TokenEstimator.EstimateString(r.LineAfter) +
                    50 // Metadata
                );
                return tokens;
            },
            baseTokens: TokenEstimator.BASE_RESPONSE_TOKENS
        );
    }

    private FindReplaceAnalysis GenerateAnalysis(List<FileMatches> matches, List<FileChangePreview> changes)
    {
        var analysis = new FindReplaceAnalysis
        {
            TotalFiles = matches.Count,
            TotalMatches = matches.Sum(m => m.Matches.Count),
            TotalReplacements = changes.Sum(c => c.Replacements.Count),
            FilesByProject = matches
                .GroupBy(m => m.ProjectName)
                .ToDictionary(g => g.Key, g => g.Count()),
            MatchesByProject = matches
                .GroupBy(m => m.ProjectName)
                .ToDictionary(g => g.Key, g => g.Sum(m => m.Matches.Count)),
            AverageMatchesPerFile = matches.Any() 
                ? (double)matches.Sum(m => m.Matches.Count) / matches.Count 
                : 0
        };

        // Determine if it's a high-impact change
        analysis.IsHighImpact = analysis.TotalMatches > 50 || analysis.TotalFiles > 20;
        
        // Check if it affects test files
        analysis.AffectsTests = matches.Any(m => 
            m.FilePath.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
            m.FilePath.Contains("Spec", StringComparison.OrdinalIgnoreCase));

        return analysis;
    }

    private List<string> GenerateInsights(FindReplaceAnalysis analysis, SolutionWideFindReplaceParams parameters)
    {
        var insights = new List<string>();

        if (analysis.TotalMatches == 0)
        {
            insights.Add("No matches found - check your pattern and filters");
        }
        else if (analysis.TotalMatches == 1)
        {
            insights.Add("Only one match found - consider using single-file editing instead");
        }
        else
        {
            insights.Add($"Found {analysis.TotalMatches} matches across {analysis.TotalFiles} files");
        }

        if (analysis.IsHighImpact)
        {
            insights.Add("⚠️ High-impact change affecting many files - review carefully");
        }

        if (analysis.AffectsTests)
        {
            insights.Add("Changes affect test files - ensure tests still pass");
        }

        if (analysis.FilesByProject != null && analysis.FilesByProject.Count > 1)
        {
            insights.Add($"Changes span {analysis.FilesByProject.Count} projects");
        }

        if (parameters.UseRegex)
        {
            insights.Add("Using regex pattern - verify replacements are as expected");
        }

        if (analysis.AverageMatchesPerFile > 5)
        {
            insights.Add($"High density of matches ({analysis.AverageMatchesPerFile:F1} per file)");
        }

        return insights;
    }

    private List<NextAction> GenerateNextActions(
        SolutionWideFindReplaceParams parameters, 
        FindReplaceAnalysis analysis,
        bool applied)
    {
        var actions = new List<NextAction>();

        if (parameters.Preview && analysis.TotalMatches > 0)
        {
            // Suggest applying the changes
            actions.Add(new NextAction
            {
                Id = "apply_changes",
                Description = "Apply these changes to the solution",
                ToolName = "csharp_solution_wide_find_replace",
                Parameters = new
                {
                    findPattern = parameters.FindPattern,
                    replacePattern = parameters.ReplacePattern,
                    useRegex = parameters.UseRegex,
                    caseSensitive = parameters.CaseSensitive,
                    wholeWord = parameters.WholeWord,
                    filePattern = parameters.FilePattern,
                    preview = false
                },
                Priority = "high"
            });
        }

        if (applied)
        {
            // Suggest running tests
            actions.Add(new NextAction
            {
                Id = "run_tests",
                Description = "Run tests to verify changes didn't break anything",
                ToolName = "bash",
                Parameters = new
                {
                    command = "dotnet test"
                },
                Priority = "high"
            });

            // Suggest reviewing changes
            actions.Add(new NextAction
            {
                Id = "review_changes",
                Description = "Review the changes with git diff",
                ToolName = "bash",
                Parameters = new
                {
                    command = "git diff"
                },
                Priority = "high"
            });
        }

        // Suggest finding remaining occurrences with different pattern
        if (analysis.TotalMatches > 0)
        {
            actions.Add(new NextAction
            {
                Id = "find_variations",
                Description = "Search for variations of this pattern",
                ToolName = "csharp_solution_wide_find_replace",
                Parameters = new
                {
                    findPattern = parameters.FindPattern,
                    replacePattern = parameters.ReplacePattern,
                    useRegex = true,
                    caseSensitive = !parameters.CaseSensitive,
                    preview = true
                },
                Priority = "medium"
            });
        }

        return actions;
    }

    private List<NextAction> GenerateNoMatchesActions(SolutionWideFindReplaceParams parameters)
    {
        var actions = new List<NextAction>();

        // Suggest case-insensitive search
        if (parameters.CaseSensitive)
        {
            actions.Add(new NextAction
            {
                Id = "try_case_insensitive",
                Description = "Try case-insensitive search",
                ToolName = "csharp_solution_wide_find_replace",
                Parameters = new
                {
                    findPattern = parameters.FindPattern,
                    replacePattern = parameters.ReplacePattern,
                    caseSensitive = false,
                    preview = true
                },
                Priority = "high"
            });
        }

        // Suggest removing file pattern filter
        if (!string.IsNullOrEmpty(parameters.FilePattern))
        {
            actions.Add(new NextAction
            {
                Id = "remove_file_filter",
                Description = "Search without file pattern filter",
                ToolName = "csharp_solution_wide_find_replace",
                Parameters = new
                {
                    findPattern = parameters.FindPattern,
                    replacePattern = parameters.ReplacePattern,
                    preview = true
                },
                Priority = "medium"
            });
        }

        return actions;
    }

    private string GenerateResultMessage(
        SolutionWideFindReplaceParams parameters,
        FindReplaceAnalysis analysis,
        bool applied,
        bool wasTruncated)
    {
        if (analysis.TotalMatches == 0)
        {
            return $"No matches found for '{parameters.FindPattern}'";
        }

        var action = applied ? "Replaced" : "Found";
        var suffix = wasTruncated ? $" (showing {analysis.TotalFiles} files)" : "";
        
        return $"{action} {analysis.TotalMatches} occurrences in {analysis.TotalFiles} files{suffix}";
    }

    private QueryInfo CreateQueryInfo(SolutionWideFindReplaceParams parameters)
    {
        return new QueryInfo
        {
            AdditionalParams = new Dictionary<string, object>
            {
                ["findPattern"] = parameters.FindPattern,
                ["replacePattern"] = parameters.ReplacePattern,
                ["useRegex"] = parameters.UseRegex,
                ["caseSensitive"] = parameters.CaseSensitive,
                ["preview"] = parameters.Preview
            }
        };
    }

    private SolutionWideFindReplaceResult CreateErrorResult(
        string errorCode,
        string message,
        List<string> recoverySteps,
        SolutionWideFindReplaceParams parameters,
        DateTime startTime)
    {
        return new SolutionWideFindReplaceResult
        {
            Success = false,
            Message = message,
            Error = new ErrorInfo
            {
                Code = errorCode,
                Recovery = new RecoveryInfo
                {
                    Steps = recoverySteps
                }
            },
            Query = CreateQueryInfo(parameters),
            Meta = new ToolMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
    }

    private class FileMatches
    {
        public string FilePath { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public List<Match> Matches { get; set; } = new();
        public SourceText OriginalText { get; set; } = null!;
    }

    private class Match
    {
        public int Start { get; set; }
        public int Length { get; set; }
        public string Value { get; set; } = "";
        public int Line { get; set; }
    }
}

public class SolutionWideFindReplaceParams
{
    [JsonPropertyName("findPattern")]
    [Description("The pattern to search for (literal text or regex)")]
    public required string FindPattern { get; set; }

    [JsonPropertyName("replacePattern")]
    [Description("The replacement pattern (supports regex backreferences like $1)")]
    public required string ReplacePattern { get; set; }
    
    [JsonPropertyName("useRegex")]
    [Description("Use regular expressions for pattern matching (default: false)")]
    public bool UseRegex { get; set; } = false;
    
    [JsonPropertyName("caseSensitive")]
    [Description("Case-sensitive matching (default: true)")]
    public bool CaseSensitive { get; set; } = true;
    
    [JsonPropertyName("wholeWord")]
    [Description("Match whole words only (default: false)")]
    public bool WholeWord { get; set; } = false;
    
    [JsonPropertyName("multiline")]
    [Description("Enable multiline mode for regex (default: false)")]
    public bool Multiline { get; set; } = false;
    
    [JsonPropertyName("filePattern")]
    [Description("Glob pattern to include files (e.g., '*.cs', 'src/**/*.cs')")]
    public string? FilePattern { get; set; }
    
    [JsonPropertyName("excludePattern")]
    [Description("Glob pattern to exclude files (e.g., '*.Designer.cs', '**/bin/**')")]
    public string? ExcludePattern { get; set; }
    
    [JsonPropertyName("preview")]
    [Description("Preview changes without applying them (default: true)")]
    public bool Preview { get; set; } = true;
    
    [JsonPropertyName("maxFiles")]
    [Description("Maximum number of files to return in preview (default: 100)")]
    public int? MaxFiles { get; set; }
}

public class SolutionWideFindReplaceResult : ToolResultBase
{
    public override string Operation => "csharp_solution_wide_find_replace";
    
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }
    
    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }
    
    [JsonPropertyName("applied")]
    public bool Applied { get; set; }
    
    [JsonPropertyName("changes")]
    public List<FileChangePreview>? Changes { get; set; }
    
    [JsonPropertyName("analysis")]
    public FindReplaceAnalysis? Analysis { get; set; }
}

public class FileChangePreview
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }
    
    [JsonPropertyName("projectName")]
    public required string ProjectName { get; set; }
    
    [JsonPropertyName("matchCount")]
    public int MatchCount { get; set; }
    
    [JsonPropertyName("replacements")]
    public List<ReplacementInfo> Replacements { get; set; } = new();
    
    [JsonPropertyName("newContent")]
    [JsonIgnore] // Don't serialize full content to save tokens
    public string NewContent { get; set; } = "";
}

public class ReplacementInfo
{
    [JsonPropertyName("line")]
    public int Line { get; set; }
    
    [JsonPropertyName("column")]
    public int Column { get; set; }
    
    [JsonPropertyName("oldText")]
    public required string OldText { get; set; }
    
    [JsonPropertyName("newText")]
    public required string NewText { get; set; }
    
    [JsonPropertyName("lineBefore")]
    public required string LineBefore { get; set; }
    
    [JsonPropertyName("lineAfter")]
    public required string LineAfter { get; set; }
}

public class FindReplaceAnalysis
{
    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; set; }
    
    [JsonPropertyName("totalMatches")]
    public int TotalMatches { get; set; }
    
    [JsonPropertyName("totalReplacements")]
    public int TotalReplacements { get; set; }
    
    [JsonPropertyName("filesByProject")]
    public Dictionary<string, int>? FilesByProject { get; set; }
    
    [JsonPropertyName("matchesByProject")]
    public Dictionary<string, int>? MatchesByProject { get; set; }
    
    [JsonPropertyName("averageMatchesPerFile")]
    public double AverageMatchesPerFile { get; set; }
    
    [JsonPropertyName("isHighImpact")]
    public bool IsHighImpact { get; set; }
    
    [JsonPropertyName("affectsTests")]
    public bool AffectsTests { get; set; }
}