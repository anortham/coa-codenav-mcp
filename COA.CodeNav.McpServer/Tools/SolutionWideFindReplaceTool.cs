using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.Interfaces;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using DataAnnotations = System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that performs solution-wide find and replace operations
/// </summary>
public class SolutionWideFindReplaceTool : McpToolBase<SolutionWideFindReplaceParams, SolutionWideFindReplaceResult>
{
    private readonly ILogger<SolutionWideFindReplaceTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly SolutionWideFindReplaceResponseBuilder _responseBuilder;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => "csharp_solution_wide_find_replace";
    public override string Description => @"Find and replace text patterns across the entire solution with preview. Enables bulk changes like renaming patterns or updating deprecated APIs with regex support.";

    public SolutionWideFindReplaceTool(
        ILogger<SolutionWideFindReplaceTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        SolutionWideFindReplaceResponseBuilder responseBuilder,
        ITokenEstimator tokenEstimator,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _responseBuilder = responseBuilder;
        _tokenEstimator = tokenEstimator;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<SolutionWideFindReplaceResult> ExecuteInternalAsync(
        SolutionWideFindReplaceParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("SolutionWideFindReplace request: FindPattern={FindPattern}, Preview={Preview}", 
            parameters.FindPattern, parameters.Preview);

        var workspace = _workspaceService.GetActiveWorkspaces().FirstOrDefault();
        if (workspace == null)
        {
            return new SolutionWideFindReplaceResult
            {
                Success = false,
                Message = "No workspace loaded",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.WORKSPACE_NOT_LOADED,
                    Message = "No workspace loaded",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Load a solution or project first",
                            "Use csharp_load_solution or csharp_load_project"
                        },
                        SuggestedActions = new List<SuggestedAction>
                        {
                            new SuggestedAction
                            {
                                Tool = "csharp_load_solution",
                                Description = "Load a solution",
                                Parameters = new { solutionPath = "<path-to-your-solution.sln>" }
                            }
                        }
                    }
                }
            };
        }

        try
        {
            var solution = workspace.Solution;
            var matchedFiles = new List<FindReplaceMatch>();
            var totalMatches = 0;

            // Validate regex pattern if using regex
            Regex? regex = null;
            if (parameters.UseRegex)
            {
                try
                {
                    var options = RegexOptions.None;
                    if (!parameters.CaseSensitive) options |= RegexOptions.IgnoreCase;
                    if (parameters.Multiline) options |= RegexOptions.Multiline | RegexOptions.Singleline;
                    
                    regex = new Regex(parameters.FindPattern, options);
                }
                catch (ArgumentException ex)
                {
                    return new SolutionWideFindReplaceResult
                    {
                        Success = false,
                        Message = $"Invalid regex pattern: {ex.Message}",
                        Error = new ErrorInfo
                        {
                            Code = ErrorCodes.INTERNAL_ERROR,
                            Message = $"Invalid regex pattern: {ex.Message}",
                            Recovery = new RecoveryInfo
                            {
                                Steps = new[]
                                {
                                    "Check the regex pattern syntax",
                                    "Use literal search if complex patterns are not needed",
                                    "Test the pattern with a regex validator"
                                }
                            }
                        }
                    };
                }
            }

            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    if (!ShouldProcessDocument(document, parameters))
                        continue;

                    var sourceText = await document.GetTextAsync(cancellationToken);
                    var matches = FindMatches(sourceText, parameters, regex);
                    
                    if (matches.Any())
                    {
                        totalMatches += matches.Count;
                        
                        var fileMatch = new FindReplaceMatch
                        {
                            FilePath = document.FilePath ?? document.Name,
                            ProjectName = document.Project.Name,
                            MatchCount = matches.Count,
                            Matches = matches.Take(parameters.MaxFiles ?? 100).ToList()
                        };

                        if (!parameters.Preview)
                        {
                            // Apply changes
                            var newText = ApplyReplacements(sourceText, matches, parameters);
                            fileMatch.ModifiedContent = newText.ToString();
                        }

                        matchedFiles.Add(fileMatch);
                    }
                }
            }

            // Store all matched files before limiting
            var allMatchedFiles = matchedFiles.ToList();
            var wasTruncated = false;
            
            // Apply token optimization to prevent context overflow
            var estimatedTokens = _tokenEstimator.EstimateObject(matchedFiles);
            const int SAFETY_TOKEN_LIMIT = 10000;
            
            if (estimatedTokens > SAFETY_TOKEN_LIMIT)
            {
                // Use progressive reduction based on token estimation
                var originalCount = matchedFiles.Count;
                matchedFiles = _tokenEstimator.ApplyProgressiveReduction(
                    matchedFiles,
                    file => _tokenEstimator.EstimateObject(file),
                    SAFETY_TOKEN_LIMIT,
                    new[] { 50, 25, 10, 5 }
                );
                
                wasTruncated = matchedFiles.Count < originalCount;
                
                _logger.LogDebug("Applied token optimization: reduced from {Original} to {Reduced} files (estimated {EstimatedTokens} tokens)",
                    originalCount, matchedFiles.Count, estimatedTokens);
            }
            
            // Also respect MaxFiles parameter if provided and stricter than token limit
            if (parameters.MaxFiles.HasValue && matchedFiles.Count > parameters.MaxFiles.Value)
            {
                matchedFiles = matchedFiles.Take(parameters.MaxFiles.Value).ToList();
                wasTruncated = true;
            }

            // Store full results as resource if truncated
            string? resourceUri = null;
            if (wasTruncated && _resourceProvider != null)
            {
                var fullData = new
                {
                    findPattern = parameters.FindPattern,
                    replacePattern = parameters.ReplacePattern,
                    isPreview = parameters.Preview,
                    totalMatches = totalMatches,
                    totalFiles = allMatchedFiles.Count,
                    allMatchedFiles = allMatchedFiles,
                    executionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                };
                
                resourceUri = _resourceProvider.StoreAnalysisResult(
                    "solution-wide-find-replace",
                    fullData,
                    $"All {allMatchedFiles.Count} files with {totalMatches} matches for '{parameters.FindPattern}'"
                );
                
                _logger.LogDebug("Stored full find-replace results as resource: {ResourceUri}", resourceUri);
            }

            var insights = GenerateInsights(matchedFiles, totalMatches, parameters);
            
            // Add truncation warning if needed
            if (wasTruncated)
            {
                insights.Insert(0, $"⚠️ Showing {matchedFiles.Count} of {allMatchedFiles.Count} files. Full results available via resource URI.");
            }
            
            var actions = GenerateNextActions(matchedFiles, parameters, wasTruncated, allMatchedFiles.Count);

            var completeResult = new SolutionWideFindReplaceResult
            {
                Success = true,
                Message = parameters.Preview 
                    ? $"Found {totalMatches} matches in {allMatchedFiles.Count} files (preview mode, showing {matchedFiles.Count})"
                    : $"Replaced {totalMatches} matches in {allMatchedFiles.Count} files",
                Query = new QueryInfo
                {
                    TargetSymbol = parameters.FindPattern
                },
                Summary = new SummaryInfo
                {
                    TotalFound = totalMatches,
                    Returned = matchedFiles.Count,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                },
                MatchedFiles = matchedFiles,
                IsPreview = parameters.Preview,
                TotalMatches = totalMatches,
                ResourceUri = resourceUri,
                Insights = insights,
                Actions = actions,
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    Truncated = wasTruncated
                }
            };

            // Use ResponseBuilder for token optimization and AI-friendly formatting
            var context = new COA.Mcp.Framework.TokenOptimization.ResponseBuilders.ResponseContext
            {
                ResponseMode = "optimized",
                TokenLimit = 10000,
                ToolName = Name
            };

            return await _responseBuilder.BuildResponseAsync(completeResult, context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SolutionWideFindReplace");
            return new SolutionWideFindReplaceResult
            {
                Success = false,
                Message = $"Error: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Message = $"Error: {ex.Message}"
                },
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                }
            };
        }
    }

    private bool ShouldProcessDocument(Document document, SolutionWideFindReplaceParams parameters)
    {
        // Check file pattern
        if (!string.IsNullOrEmpty(parameters.FilePattern))
        {
            var fileName = Path.GetFileName(document.FilePath ?? document.Name);
            if (!IsFilePatternMatch(fileName, parameters.FilePattern))
                return false;
        }

        // Check exclude pattern
        if (!string.IsNullOrEmpty(parameters.ExcludePattern))
        {
            var fileName = Path.GetFileName(document.FilePath ?? document.Name);
            if (IsFilePatternMatch(fileName, parameters.ExcludePattern))
                return false;
        }

        // Only process C# files by default
        var extension = Path.GetExtension(document.Name);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsFilePatternMatch(string fileName, string pattern)
    {
        // Simple glob pattern matching
        var regexPattern = pattern
            .Replace("*", ".*")
            .Replace("?", ".");
        
        return Regex.IsMatch(fileName, "^" + regexPattern + "$", RegexOptions.IgnoreCase);
    }

    private List<TextMatch> FindMatches(SourceText sourceText, SolutionWideFindReplaceParams parameters, Regex? regex)
    {
        var matches = new List<TextMatch>();
        var content = sourceText.ToString();

        if (parameters.UseRegex && regex != null)
        {
            var regexMatches = regex.Matches(content);
            foreach (Match match in regexMatches)
            {
                var linePosition = sourceText.Lines.GetLinePosition(match.Index);
                matches.Add(new TextMatch
                {
                    StartIndex = match.Index,
                    Length = match.Length,
                    Line = linePosition.Line + 1,
                    Column = linePosition.Character + 1,
                    OriginalText = match.Value,
                    ReplacementText = regex.Replace(match.Value, parameters.ReplacePattern ?? "")
                });
            }
        }
        else
        {
            // Simple string search
            var comparison = parameters.CaseSensitive 
                ? StringComparison.Ordinal 
                : StringComparison.OrdinalIgnoreCase;

            var searchText = parameters.FindPattern;
            var replaceText = parameters.ReplacePattern ?? "";

            int index = 0;
            while ((index = content.IndexOf(searchText, index, comparison)) != -1)
            {
                // Check word boundaries if requested
                if (parameters.WholeWord && !IsWholeWordMatch(content, index, searchText.Length))
                {
                    index++;
                    continue;
                }

                var linePosition = sourceText.Lines.GetLinePosition(index);
                matches.Add(new TextMatch
                {
                    StartIndex = index,
                    Length = searchText.Length,
                    Line = linePosition.Line + 1,
                    Column = linePosition.Character + 1,
                    OriginalText = content.Substring(index, searchText.Length),
                    ReplacementText = replaceText
                });

                index += searchText.Length;
            }
        }

        return matches;
    }

    private bool IsWholeWordMatch(string content, int index, int length)
    {
        // Check start boundary
        if (index > 0 && char.IsLetterOrDigit(content[index - 1]))
            return false;

        // Check end boundary
        var endIndex = index + length;
        if (endIndex < content.Length && char.IsLetterOrDigit(content[endIndex]))
            return false;

        return true;
    }

    private SourceText ApplyReplacements(SourceText originalText, List<TextMatch> matches, SolutionWideFindReplaceParams parameters)
    {
        // Apply changes in reverse order to maintain indices
        var orderedMatches = matches.OrderByDescending(m => m.StartIndex).ToList();
        var currentText = originalText;

        foreach (var match in orderedMatches)
        {
            var textSpan = new Microsoft.CodeAnalysis.Text.TextSpan(match.StartIndex, match.Length);
            currentText = currentText.Replace(textSpan, match.ReplacementText);
        }

        return currentText;
    }

    private List<string> GenerateInsights(List<FindReplaceMatch> matchedFiles, int totalMatches, SolutionWideFindReplaceParams parameters)
    {
        var insights = new List<string>();

        if (totalMatches == 0)
        {
            insights.Add($"No matches found for pattern '{parameters.FindPattern}'");
            return insights;
        }

        insights.Add($"Found {totalMatches} matches across {matchedFiles.Count} files");

        if (parameters.UseRegex)
        {
            insights.Add("Using regex pattern matching - verify replacements carefully");
        }

        if (!parameters.Preview)
        {
            insights.Add("Changes have been applied - consider testing the modified code");
        }
        else
        {
            insights.Add("Preview mode - use preview=false to apply changes");
        }

        var avgMatchesPerFile = matchedFiles.Any() ? (double)totalMatches / matchedFiles.Count : 0;
        if (avgMatchesPerFile > 10)
        {
            insights.Add($"High match density ({avgMatchesPerFile:F1} per file) - consider more specific patterns");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(List<FindReplaceMatch> matchedFiles, SolutionWideFindReplaceParams parameters, bool wasTruncated, int totalFiles)
    {
        var actions = new List<AIAction>();

        // If truncated, offer to get more results
        if (wasTruncated)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_solution_wide_find_replace",
                Description = $"Get all {totalFiles} matched files",
                Parameters = new Dictionary<string, object>
                {
                    ["findPattern"] = parameters.FindPattern,
                    ["replacePattern"] = parameters.ReplacePattern ?? "",
                    ["preview"] = parameters.Preview,
                    ["useRegex"] = parameters.UseRegex,
                    ["caseSensitive"] = parameters.CaseSensitive,
                    ["wholeWord"] = parameters.WholeWord,
                    ["maxFiles"] = Math.Min(totalFiles, 500)
                },
                Priority = 95,
                Category = "pagination"
            });
        }

        if (parameters.Preview && matchedFiles.Any())
        {
            actions.Add(new AIAction
            {
                Action = "csharp_solution_wide_find_replace",
                Description = "Apply the replacements",
                Parameters = new Dictionary<string, object>
                {
                    ["findPattern"] = parameters.FindPattern,
                    ["replacePattern"] = parameters.ReplacePattern ?? "",
                    ["preview"] = false,
                    ["useRegex"] = parameters.UseRegex,
                    ["caseSensitive"] = parameters.CaseSensitive,
                    ["wholeWord"] = parameters.WholeWord
                },
                Priority = 90,
                Category = "execution"
            });
        }

        if (matchedFiles.Any())
        {
            var firstFile = matchedFiles.First();
            actions.Add(new AIAction
            {
                Action = "review_changes",
                Description = $"Review changes in {firstFile.FilePath}",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = firstFile.FilePath
                },
                Priority = 80,
                Category = "validation"
            });
        }

        return actions;
    }

}

/// <summary>
/// Parameters for SolutionWideFindReplace tool
/// </summary>
public class SolutionWideFindReplaceParams
{
    [DataAnnotations.Required(ErrorMessage = "FindPattern is required")]
    [JsonPropertyName("findPattern")]
    [COA.Mcp.Framework.Attributes.Description("The pattern to search for (literal text or regex)")]
    public string FindPattern { get; set; } = string.Empty;

    [JsonPropertyName("replacePattern")]
    [COA.Mcp.Framework.Attributes.Description("The replacement pattern (supports regex backreferences like $1)")]
    public string? ReplacePattern { get; set; }

    [JsonPropertyName("preview")]
    [COA.Mcp.Framework.Attributes.Description("Preview changes without applying them (default: true)")]
    public bool Preview { get; set; } = true;

    [JsonPropertyName("useRegex")]
    [COA.Mcp.Framework.Attributes.Description("Use regular expressions for pattern matching (default: false)")]
    public bool UseRegex { get; set; } = false;

    [JsonPropertyName("caseSensitive")]
    [COA.Mcp.Framework.Attributes.Description("Case-sensitive matching (default: true)")]
    public bool CaseSensitive { get; set; } = true;

    [JsonPropertyName("wholeWord")]
    [COA.Mcp.Framework.Attributes.Description("Match whole words only (default: false)")]
    public bool WholeWord { get; set; } = false;

    [JsonPropertyName("multiline")]
    [COA.Mcp.Framework.Attributes.Description("Enable multiline mode for regex (default: false)")]
    public bool Multiline { get; set; } = false;

    [JsonPropertyName("filePattern")]
    [COA.Mcp.Framework.Attributes.Description("Glob pattern to include files (e.g., '*.cs', 'src/**/*.cs')")]
    public string? FilePattern { get; set; }

    [JsonPropertyName("excludePattern")]
    [COA.Mcp.Framework.Attributes.Description("Glob pattern to exclude files (e.g., '*.Designer.cs', '**/bin/**')")]
    public string? ExcludePattern { get; set; }

    [JsonPropertyName("maxFiles")]
    [COA.Mcp.Framework.Attributes.Description("Maximum number of files to return in preview (default: 100)")]
    public int? MaxFiles { get; set; }
}

public class SolutionWideFindReplaceResult : ToolResultBase
{
    public override string Operation => "csharp_solution_wide_find_replace";
    
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }
    
    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("matchedFiles")]
    public List<FindReplaceMatch>? MatchedFiles { get; set; }

    [JsonPropertyName("isPreview")]
    public bool IsPreview { get; set; }

    [JsonPropertyName("totalMatches")]
    public int TotalMatches { get; set; }
}

public class FindReplaceMatch
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }

    [JsonPropertyName("projectName")]
    public required string ProjectName { get; set; }

    [JsonPropertyName("matchCount")]
    public int MatchCount { get; set; }

    [JsonPropertyName("matches")]
    public List<TextMatch> Matches { get; set; } = new();

    [JsonPropertyName("modifiedContent")]
    public string? ModifiedContent { get; set; }
}

public class TextMatch
{
    [JsonPropertyName("startIndex")]
    public int StartIndex { get; set; }

    [JsonPropertyName("length")]
    public int Length { get; set; }

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }

    [JsonPropertyName("originalText")]
    public required string OriginalText { get; set; }

    [JsonPropertyName("replacementText")]
    public required string ReplacementText { get; set; }
}