using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that provides symbol search functionality across the solution
/// </summary>
[McpServerToolType]
public class SymbolSearchTool : ITool
{
    private readonly ILogger<SymbolSearchTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;
    
    // Removed hard limit - now using dynamic token estimation

    public string ToolName => "csharp_symbol_search";
    public string Description => "Search for symbols by name or pattern across the solution";

    public SymbolSearchTool(
        ILogger<SymbolSearchTool> logger,
        RoslynWorkspaceService workspaceService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "csharp_symbol_search")]
    [Description(@"Search for symbols by name or pattern across the entire solution.
Returns: List of matching symbols with their locations, types, and metadata.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if no workspace is loaded.
Use cases: Finding symbols by name/pattern, discovering types, locating methods, exploring namespaces.
Not for: Finding references to a symbol (use csharp_find_all_references), navigating to definition (use csharp_goto_definition).")]
    public async Task<object> ExecuteAsync(SymbolSearchParams parameters, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("SymbolSearch request received: Query={Query}, SearchType={SearchType}, SymbolKinds={SymbolKinds}", 
            parameters.Query, parameters.SearchType, string.Join(",", parameters.SymbolKinds ?? Array.Empty<string>()));
            
        var startTime = DateTime.UtcNow;
            
        try
        {
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
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
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
                    Meta = new ToolMetadata { ExecutionTime = "0ms" }
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
                
            // Apply token management
            var response = TokenEstimator.CreateTokenAwareResponse(
                allSymbols,
                syms => EstimateSymbolTokens(syms),
                requestedMax: parameters.MaxResults ?? 100, // Default to 100 symbols
                safetyLimit: TokenEstimator.DEFAULT_SAFETY_LIMIT,
                toolName: "csharp_symbol_search"
            );

            if (!response.Items.Any())
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
                    Meta = new ToolMetadata 
                    { 
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                    }
                };
            }

            // Generate insights (use all symbols for accurate insights)
            var insights = GenerateInsights(allSymbols, response.Items, parameters);
            
            // Add truncation message if needed
            if (response.WasTruncated)
            {
                insights.Insert(0, response.GetTruncationMessage());
            }
            
            // Generate next actions
            var nextActions = GenerateNextActions(response.Items);
            
            // Add action to get more results if truncated
            if (response.WasTruncated)
            {
                nextActions.Insert(0, new NextAction
                {
                    Id = "get_more_symbols",
                    Description = "Get additional symbols",
                    ToolName = "csharp_symbol_search",
                    Parameters = new
                    {
                        query = parameters.Query,
                        searchType = parameters.SearchType,
                        maxResults = Math.Min(allSymbols.Count, 500)
                    },
                    Priority = "high"
                });
            }
            
            // Store full results if truncated
            string? resourceUri = null;
            if (response.WasTruncated && _resourceProvider != null)
            {
                resourceUri = _resourceProvider.StoreAnalysisResult("symbol-search",
                    new { query = parameters.Query, results = allSymbols, totalCount = allSymbols.Count },
                    $"All {allSymbols.Count} symbols matching: {parameters.Query}");
            }
            
            return new SymbolSearchToolResult
            {
                Success = true,
                Message = response.WasTruncated 
                    ? $"Found {allSymbols.Count} symbols matching '{parameters.Query}' (showing {response.ReturnedCount})"
                    : $"Found {allSymbols.Count} symbols matching '{parameters.Query}'",
                Symbols = response.Items,
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
                    Returned = response.ReturnedCount,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                },
                ResultsSummary = new ResultsSummary
                {
                    Included = response.ReturnedCount,
                    Total = allSymbols.Count,
                    HasMore = response.WasTruncated
                },
                Meta = new ToolMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    Truncated = response.WasTruncated,
                    Tokens = response.EstimatedTokens
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Symbol Search");
            return new SymbolSearchToolResult
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
                },
                Query = new SymbolSearchQuery 
                { 
                    SearchPattern = parameters.Query,
                    SearchType = parameters.SearchType ?? "contains",
                    SymbolKinds = parameters.SymbolKinds?.ToList()
                },
                Meta = new ToolMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                }
            };
        }
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
            foreach (var member in typeSymbol.GetMembers())
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

    private int EstimateSymbolTokens(List<Models.SymbolInfo> symbols)
    {
        return TokenEstimator.EstimateCollection(
            symbols,
            symbol => TokenEstimator.Roslyn.EstimateSymbol(symbol),
            baseTokens: TokenEstimator.BASE_RESPONSE_TOKENS
        );
    }
    
    private List<string> GenerateInsights(List<Models.SymbolInfo> allSymbols, List<Models.SymbolInfo> returnedSymbols, SymbolSearchParams parameters)
    {
        var insights = new List<string>();

        // Truncation is now handled by TokenEstimator

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
            var pattern = parameters.Query.Replace("*", "");
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

    private List<NextAction> GenerateNextActions(List<Models.SymbolInfo> symbols)
    {
        var actions = new List<NextAction>();

        // Take first few symbols for next actions
        foreach (var symbol in symbols.Take(3))
        {
            if (symbol.Location != null)
            {
                actions.Add(new NextAction
                {
                    Id = $"goto_{symbol.Name.ToLower()}",
                    Description = $"Go to definition of '{symbol.Name}'",
                    ToolName = "csharp_goto_definition",
                    Parameters = new
                    {
                        filePath = symbol.Location.FilePath,
                        line = symbol.Location.Line,
                        column = symbol.Location.Column
                    },
                    Priority = "medium"
                });

                actions.Add(new NextAction
                {
                    Id = $"refs_{symbol.Name.ToLower()}",
                    Description = $"Find references to '{symbol.Name}'",
                    ToolName = "csharp_find_all_references",
                    Parameters = new
                    {
                        filePath = symbol.Location.FilePath,
                        line = symbol.Location.Line,
                        column = symbol.Location.Column
                    },
                    Priority = "medium"
                });
            }
        }

        return actions;
    }
}

public class SymbolSearchParams
{
    [JsonPropertyName("query")]
    [Description("Symbol name or pattern to search for (e.g., 'UserService', 'User*', 'IUser.*')")]
    public required string Query { get; set; }

    [JsonPropertyName("searchType")]
    [Description("Search type: 'contains' (default), 'exact', 'startswith', 'endswith', 'wildcard', 'regex', 'fuzzy'")]
    public string? SearchType { get; set; }

    [JsonPropertyName("symbolKinds")]
    [Description("Filter by symbol kinds: 'Class', 'Interface', 'Method', 'Property', 'Field', 'Event', 'Namespace', 'Struct', 'Enum', 'Delegate'")]
    public string[]? SymbolKinds { get; set; }

    [JsonPropertyName("includePrivate")]
    [Description("Include private symbols (default: false)")]
    public bool? IncludePrivate { get; set; }

    [JsonPropertyName("namespaceFilter")]
    [Description("Filter by namespace (contains match)")]
    public string? NamespaceFilter { get; set; }

    [JsonPropertyName("projectFilter")]
    [Description("Filter by project name (contains match)")]
    public string? ProjectFilter { get; set; }

    [JsonPropertyName("maxResults")]
    [Description("Maximum number of results to return (default: 100, max: 500)")]
    public int? MaxResults { get; set; }
}

// Result classes have been moved to COA.CodeNav.McpServer.Models namespace
// Note: SymbolInfo for SymbolSearchTool remains in Models namespace but with enhanced properties