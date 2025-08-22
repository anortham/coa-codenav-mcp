using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that provides symbol search functionality across the solution
/// </summary>
public class SymbolSearchTool : McpToolBase<SymbolSearchParams, SymbolSearchToolResult>
{
    private readonly ILogger<SymbolSearchTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;
    private readonly SymbolSearchResponseBuilder _responseBuilder;
    private readonly ITokenEstimator _tokenEstimator;
    
    private const int DEFAULT_MAX_RESULTS = 100;
    private const int AGGRESSIVE_MAX_RESULTS = 500; // For when we have token budget

    public override string Name => ToolNames.SymbolSearch;
    public override string Description => @"**FIND TYPES BEFORE USING THEM** - the essential first step before writing any code that references classes, interfaces, or methods you're not certain exist.

**TYPE DISCOVERY WORKFLOW:**
1. User mentions any class/interface name → Search for it FIRST
2. Verify it exists and see exact spelling → Avoid typos and wrong assumptions
3. Check multiple matches → Pick the right one from right namespace
4. Then proceed to get definition or members → Write correct code

**Critical discovery moments:**
- 'Use the User class' → Search 'User' to see if it exists and where
- 'Call the email service' → Search '*email*' or '*service*' to find actual names
- 'Implement IRepository' → Search 'IRepository' to verify interface exists
- Any mention of unfamiliar type names → Search to confirm

**Prevents costly mistakes:**
- Using non-existent types (compile errors before you even start)
- Wrong class names ('UserManager' vs 'UserService' vs 'UserController')  
- Wrong namespaces (multiple User classes, which one?)
- Typos in type names (catches before wasting time coding)

**Search patterns that work:**
- Exact names: 'UserService'
- Wildcards: '*Service', 'User*', '*Repository*'  
- Partial matches: 'email' finds 'EmailService', 'EmailProvider', etc.

**The rule:** Never assume a type exists. Search first, verify, then code.

Prerequisites: Call csharp_load_solution or csharp_load_project first.
Next steps: Use csharp_goto_definition or csharp_get_type_members on found symbols.";
    public override ToolCategory Category => ToolCategory.Query;

    public SymbolSearchTool(
        ILogger<SymbolSearchTool> logger,
        RoslynWorkspaceService workspaceService,
        SymbolSearchResponseBuilder responseBuilder,
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

    protected override async Task<SymbolSearchToolResult> ExecuteInternalAsync(
        SymbolSearchParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("SymbolSearch request received: Query={Query}, SearchType={SearchType}, SymbolKinds={SymbolKinds}", 
            parameters.Query, parameters.SearchType, string.Join(",", parameters.SymbolKinds ?? Array.Empty<string>()));
            
        var startTime = DateTime.UtcNow;

        _logger.LogInformation("Processing SymbolSearch for query: {Query}", parameters.Query);

        // Check if any workspaces are loaded
        var workspaces = _workspaceService.GetActiveWorkspaces();
        if (!workspaces.Any())
        {
            _logger.LogWarning("No workspace loaded");
            return new SymbolSearchToolResult
            {
                Success = false,
                Message = "No workspace loaded. Please load a solution or project first.",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.WORKSPACE_NOT_LOADED,
                    Message = "No workspace loaded. Please load a solution or project first.",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Load a solution using csharp_load_solution",
                            "Or load a project using csharp_load_project",
                            "Then retry the symbol search"
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
                Query = new SymbolSearchQuery 
                { 
                    SearchPattern = parameters.Query,
                    SearchType = parameters.SearchType ?? "contains",
                    SymbolKinds = parameters.SymbolKinds?.ToList()
                },
                Meta = new ToolExecutionMetadata { ExecutionTime = "0ms" }
            };
        }

        // Get all solutions from all workspaces
        var allProjects = new List<Project>();
        foreach (var workspace in workspaces)
        {
            allProjects.AddRange(workspace.Solution.Projects);
        }
        var symbols = new ConcurrentBag<Models.SymbolInfo>();
        var searchPattern = BuildSearchPattern(parameters.Query, parameters.SearchType);

        // Process all projects in parallel
        var tasks = allProjects.Select(project => Task.Run(async () =>
        {
            try
            {
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation == null) return;

                // Search in global namespace
                await SearchSymbolsInNamespace(
                    compilation.GlobalNamespace, 
                    searchPattern, 
                    parameters, 
                    symbols,
                    project.Name,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error searching symbols in project {ProjectName}", project.Name);
            }
        }, cancellationToken));

        await Task.WhenAll(tasks);

        // Sort all symbols
        var allSymbols = symbols
            .OrderBy(s => s.Name)
            .ThenBy(s => s.FullName)
            .ToList();

        // Apply MaxResults limit BEFORE passing to ResponseBuilder
        var requestedMaxResults = parameters.MaxResults ?? DEFAULT_MAX_RESULTS;
        var returnedSymbols = allSymbols.Take(requestedMaxResults).ToList();
        var wasTruncated = allSymbols.Count > requestedMaxResults;
        
        // Log if truncation will be needed
        if (allSymbols.Count >= 200)
        {
            _logger.LogInformation("Truncating symbol results - found {Count} symbols", allSymbols.Count);
        }

        if (!allSymbols.Any())
        {
            return new SymbolSearchToolResult
            {
                Success = false,
                Message = $"No symbols found matching '{parameters.Query}'",
                Insights = new List<string>
                {
                    "Try using wildcards: *Service to find symbols ending with 'Service'",
                    "Use fuzzy search by appending ~: UserSrvc~ to find similar names",
                    "Check if the solution is fully loaded and compiled"
                },
                Query = new SymbolSearchQuery 
                { 
                    SearchPattern = parameters.Query,
                    SearchType = parameters.SearchType ?? "contains",
                    SymbolKinds = parameters.SymbolKinds?.ToList()
                },
                Summary = new SummaryInfo
                {
                    TotalFound = 0,
                    Returned = 0,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                },
                Meta = new ToolExecutionMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                }
            };
        }

        // Generate insights
        var insights = GenerateInsights(allSymbols, returnedSymbols, parameters);
        
        // Add truncation message if needed
        if (wasTruncated)
        {
            insights.Insert(0, $"⚠️ Showing {returnedSymbols.Count} of {allSymbols.Count} symbols. Use maxResults parameter to get more.");
        }
        
        // Generate next actions
        var nextActions = GenerateNextActions(returnedSymbols);
        
        // Add action to get more results if truncated
        if (wasTruncated)
        {
            nextActions.Insert(0, new AIAction
            {
                Action = ToolNames.SymbolSearch,
                Description = "Get additional symbols",
                Parameters = new Dictionary<string, object>
                {
                    ["query"] = parameters.Query,
                    ["searchType"] = parameters.SearchType ?? "contains",
                    ["maxResults"] = Math.Min(allSymbols.Count, 500)
                },
                Priority = 95,
                Category = "pagination"
            });
        }
        
        // Store full results as a resource if truncated
        string? resourceUri = null;
        if (wasTruncated && _resourceProvider != null)
        {
            var fullData = new
            {
                query = parameters.Query,
                searchType = parameters.SearchType,
                totalFound = allSymbols.Count,
                symbols = allSymbols,
                executionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            };
            
            resourceUri = _resourceProvider.StoreAnalysisResult(
                "symbol-search",
                fullData,
                $"All {allSymbols.Count} symbols matching '{parameters.Query}'"
            );
            
            _logger.LogDebug("Stored full symbol search results as resource: {ResourceUri}", resourceUri);
        }
        
        // Generate distribution information
        var distribution = new SymbolDistribution
        {
            ByKind = allSymbols.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count()),
            ByProject = allSymbols.GroupBy(s => s.ProjectName ?? "Unknown").ToDictionary(g => g.Key, g => g.Count()),
            ByNamespace = allSymbols.GroupBy(s => s.Namespace ?? "Global").ToDictionary(g => g.Key, g => g.Count())
        };
        
        // Build complete result with all symbols first
        var completeResult = new SymbolSearchToolResult
        {
            Success = true,
            Message = wasTruncated 
                ? $"Found {allSymbols.Count} symbols matching '{parameters.Query}' (showing {returnedSymbols.Count})"
                : $"Found {allSymbols.Count} symbols matching '{parameters.Query}'",
            Symbols = returnedSymbols,
            Insights = insights,
            Actions = nextActions,
            ResourceUri = resourceUri,
            Query = new SymbolSearchQuery 
            { 
                SearchPattern = parameters.Query,
                SearchType = parameters.SearchType ?? "contains",
                SymbolKinds = parameters.SymbolKinds?.ToList()
            },
            Summary = new SummaryInfo
            {
                TotalFound = allSymbols.Count,
                Returned = returnedSymbols.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            },
            ResultsSummary = new ResultsSummary
            {
                Included = returnedSymbols.Count,
                Total = allSymbols.Count,
                HasMore = wasTruncated
            },
            Distribution = distribution,
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
            TokenLimit = 10000, // Fixed token limit for consistent optimization
            ToolName = Name
        };

        return await _responseBuilder.BuildResponseAsync(completeResult, context);
    }

    private async Task SearchSymbolsInNamespace(
        INamespaceOrTypeSymbol container,
        Func<string, bool> searchPattern,
        SymbolSearchParams parameters,
        ConcurrentBag<Models.SymbolInfo> results,
        string projectName,
        CancellationToken cancellationToken)
    {
        // Check if we should include this symbol
        if (ShouldIncludeSymbol(container, parameters) && searchPattern(container.Name))
        {
            var location = container.Locations.FirstOrDefault(l => l.IsInSource);
            if (location != null)
            {
                var lineSpan = location.GetLineSpan();
                results.Add(new Models.SymbolInfo
                {
                    Name = container.Name,
                    FullName = container.ToDisplayString(),
                    Kind = container.Kind.ToString(),
                    ContainerType = container.ContainingType?.ToDisplayString(),
                    Namespace = container.ContainingNamespace?.ToDisplayString(),
                    ProjectName = projectName,
                    Location = new LocationInfo
                    {
                        FilePath = lineSpan.Path,
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1
                    },
                    Accessibility = container.DeclaredAccessibility.ToString(),
                    IsStatic = container.IsStatic,
                    IsAbstract = container.IsAbstract,
                    IsSealed = container.IsSealed,
                    IsVirtual = container is ITypeSymbol type && type.IsVirtual,
                    IsOverride = container is ITypeSymbol t && t.IsOverride
                });
            }
        }

        // Process type members
        if (container is INamedTypeSymbol typeSymbol)
        {
            var immutableArray = typeSymbol.GetMembers();
            foreach (var member in immutableArray)
            {
                if (cancellationToken.IsCancellationRequested) return;

                if (member is INamespaceOrTypeSymbol nestedType)
                {
                    await SearchSymbolsInNamespace(nestedType, searchPattern, parameters, results, projectName, cancellationToken);
                }
                else if (ShouldIncludeSymbol(member, parameters) && searchPattern(member.Name))
                {
                    var location = member.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location != null)
                    {
                        var lineSpan = location.GetLineSpan();
                        results.Add(new Models.SymbolInfo
                        {
                            Name = member.Name,
                            FullName = member.ToDisplayString(),
                            Kind = member.Kind.ToString(),
                            ContainerType = member.ContainingType?.ToDisplayString(),
                            Namespace = member.ContainingNamespace?.ToDisplayString(),
                            ProjectName = projectName,
                            Location = new LocationInfo
                            {
                                FilePath = lineSpan.Path,
                                Line = lineSpan.StartLinePosition.Line + 1,
                                Column = lineSpan.StartLinePosition.Character + 1
                            },
                            Accessibility = member.DeclaredAccessibility.ToString(),
                            IsStatic = member.IsStatic,
                            IsAbstract = member.IsAbstract,
                            IsVirtual = member.IsVirtual,
                            IsOverride = member.IsOverride
                        });
                    }
                }
            }
        }

        // Process namespace members
        if (container is INamespaceSymbol namespaceSymbol)
        {
            foreach (var member in namespaceSymbol.GetMembers())
            {
                if (cancellationToken.IsCancellationRequested) return;

                if (member is INamespaceOrTypeSymbol namespaceOrType)
                {
                    await SearchSymbolsInNamespace(namespaceOrType, searchPattern, parameters, results, projectName, cancellationToken);
                }
            }
        }
    }

    private bool ShouldIncludeSymbol(ISymbol symbol, SymbolSearchParams parameters)
    {
        // Filter by symbol kinds if specified
        if (parameters.SymbolKinds?.Any() == true)
        {
            var kindStr = symbol.Kind.ToString();
            if (!parameters.SymbolKinds.Any(k => k.Equals(kindStr, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // Filter by accessibility
        if (parameters.IncludePrivate == false && symbol.DeclaredAccessibility == Accessibility.Private)
        {
            return false;
        }

        // Filter by namespace
        if (!string.IsNullOrEmpty(parameters.NamespaceFilter))
        {
            var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
            if (!ns.Contains(parameters.NamespaceFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Filter by project
        if (!string.IsNullOrEmpty(parameters.ProjectFilter))
        {
            var project = symbol.ContainingAssembly?.Name ?? "";
            if (!project.Contains(parameters.ProjectFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private Func<string, bool> BuildSearchPattern(string query, string? searchType)
    {
        switch (searchType?.ToLower())
        {
            case "exact":
                return name => name.Equals(query, StringComparison.Ordinal);

            case "fuzzy":
                var fuzzyQuery = query.TrimEnd('~');
                return name => FuzzyMatch(name, fuzzyQuery);

            case "wildcard":
                var pattern = "^" + Regex.Escape(query).Replace("\\*", ".*") + "$";
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                return name => regex.IsMatch(name);

            case "regex":
                try
                {
                    var userRegex = new Regex(query, RegexOptions.IgnoreCase);
                    return name => userRegex.IsMatch(name);
                }
                catch
                {
                    // Fall back to contains if regex is invalid
                    return name => name.Contains(query, StringComparison.OrdinalIgnoreCase);
                }

            case "startswith":
                return name => name.StartsWith(query, StringComparison.OrdinalIgnoreCase);

            case "endswith":
                return name => name.EndsWith(query, StringComparison.OrdinalIgnoreCase);

            default: // "contains" or null
                return name => name.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
    }

    private bool FuzzyMatch(string text, string pattern)
    {
        // Simple fuzzy matching algorithm
        var patternIndex = 0;
        var textIndex = 0;

        while (textIndex < text.Length && patternIndex < pattern.Length)
        {
            if (char.ToLower(text[textIndex]) == char.ToLower(pattern[patternIndex]))
            {
                patternIndex++;
            }
            textIndex++;
        }

        return patternIndex == pattern.Length;
    }
    
    private List<string> GenerateInsights(List<Models.SymbolInfo> allSymbols, List<Models.SymbolInfo> returnedSymbols, SymbolSearchParams parameters)
    {
        var insights = new List<string>();

        // Distribution by symbol kind
        var kindGroups = allSymbols.GroupBy(s => s.Kind).OrderByDescending(g => g.Count());
        insights.Add($"Found {string.Join(", ", kindGroups.Select(g => $"{g.Count()} {g.Key.ToLower()}s"))}");

        // Distribution by project
        var projectGroups = allSymbols.GroupBy(s => s.ProjectName).OrderByDescending(g => g.Count());
        if (projectGroups.Count() > 1)
        {
            insights.Add($"Symbols distributed across {projectGroups.Count()} projects");
        }

        // Common patterns
        if (parameters.SearchType == "wildcard" && parameters.Query.Contains("*"))
        {
            insights.Add($"All results match the pattern '{parameters.Query}'");
        }

        // Accessibility insights
        var publicSymbols = allSymbols.Count(s => s.Accessibility == "Public");
        if (publicSymbols > 0)
        {
            insights.Add($"{publicSymbols} public symbols that are part of the API surface");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(List<Models.SymbolInfo> symbols)
    {
        var actions = new List<AIAction>();

        // Take first few symbols for next actions
        foreach (var symbol in symbols.Take(3))
        {
            if (symbol.Location != null)
            {
                actions.Add(new AIAction
                {
                    Action = ToolNames.GoToDefinition,
                    Description = $"Go to definition of '{symbol.Name}'",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = symbol.Location.FilePath,
                        ["line"] = symbol.Location.Line,
                        ["column"] = symbol.Location.Column
                    },
                    Priority = 80,
                    Category = "navigation"
                });

                actions.Add(new AIAction
                {
                    Action = ToolNames.FindAllReferences,
                    Description = $"Find references to '{symbol.Name}'",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = symbol.Location.FilePath,
                        ["line"] = symbol.Location.Line,
                        ["column"] = symbol.Location.Column
                    },
                    Priority = 75,
                    Category = "navigation"
                });
            }
        }

        return actions;
    }

}

/// <summary>
/// Parameters for SymbolSearch tool
/// </summary>
public class SymbolSearchParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "Query is required")]
    [JsonPropertyName("query")]
    [COA.Mcp.Framework.Attributes.Description("Symbol name or pattern to search for (e.g., 'UserService', 'User*', 'IUser.*')")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("searchType")]
    [COA.Mcp.Framework.Attributes.Description("Search type: 'contains' (default), 'exact', 'startswith', 'endswith', 'wildcard', 'regex', 'fuzzy'")]
    public string? SearchType { get; set; }

    [JsonPropertyName("symbolKinds")]
    [COA.Mcp.Framework.Attributes.Description("Filter by symbol kinds: 'Class', 'Interface', 'Method', 'Property', 'Field', 'Event', 'Namespace', 'Struct', 'Enum', 'Delegate'")]
    public string[]? SymbolKinds { get; set; }

    [JsonPropertyName("includePrivate")]
    [COA.Mcp.Framework.Attributes.Description("Include private symbols (default: false)")]
    public bool? IncludePrivate { get; set; }

    [JsonPropertyName("namespaceFilter")]
    [COA.Mcp.Framework.Attributes.Description("Filter by namespace (contains match)")]
    public string? NamespaceFilter { get; set; }

    [JsonPropertyName("projectFilter")]
    [COA.Mcp.Framework.Attributes.Description("Filter by project name (contains match)")]
    public string? ProjectFilter { get; set; }

    [System.ComponentModel.DataAnnotations.Range(1, 500, ErrorMessage = "MaxResults must be between 1 and 500")]
    [JsonPropertyName("maxResults")]
    [COA.Mcp.Framework.Attributes.Description("Maximum number of results to return (default: 100, max: 500)")]
    public int? MaxResults { get; set; }
}