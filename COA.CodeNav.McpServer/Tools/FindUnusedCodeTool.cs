using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that finds unused code elements in a project or solution
/// </summary>
[McpServerToolType]
public class FindUnusedCodeTool : ITool
{
    private readonly ILogger<FindUnusedCodeTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "csharp_find_unused_code";
    public string Description => "Find unused classes, methods, properties, and fields in the codebase";

    public FindUnusedCodeTool(
        ILogger<FindUnusedCodeTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "csharp_find_unused_code")]
    [Description(@"Find unused code elements in the codebase including classes, methods, properties, and fields.
Returns: List of potentially unused code elements with their locations and types.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if workspace is not loaded.
Use cases: Code cleanup, reducing technical debt, identifying dead code, improving maintainability.
AI benefit: Helps identify code that can be safely removed to reduce complexity.")]
    public async Task<object> ExecuteAsync(FindUnusedCodeParams parameters, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("FindUnusedCode request: Scope={Scope}, IncludePrivate={IncludePrivate}", 
            parameters.Scope, parameters.IncludePrivate);

        try
        {
            var workspaces = _workspaceService.GetActiveWorkspaces();
            if (!workspaces.Any())
            {
                return new FindUnusedCodeResult
                {
                    Success = false,
                    Message = "No workspace loaded",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.WORKSPACE_NOT_LOADED,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Load a solution using csharp_load_solution",
                                "Or load a project using csharp_load_project"
                            }
                        }
                    },
                    Query = new QueryInfo { AdditionalParams = new Dictionary<string, object> { ["scope"] = parameters.Scope ?? "solution" } },
                    Meta = new ToolMetadata 
                    { 
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                    }
                };
            }

            var workspace = workspaces.First();
            var solution = workspace.Solution;
            var unusedSymbols = new List<UnusedCodeInfo>();
            var insights = new List<string>();
            var actions = new List<NextAction>();

            // Determine projects to analyze
            var projects = parameters.Scope?.ToLower() switch
            {
                "project" when !string.IsNullOrEmpty(parameters.ProjectName) => 
                    solution.Projects.Where(p => p.Name.Equals(parameters.ProjectName, StringComparison.OrdinalIgnoreCase)),
                "file" when !string.IsNullOrEmpty(parameters.FilePath) => 
                    GetProjectsContainingFile(solution, parameters.FilePath),
                _ => solution.Projects
            };

            var totalSymbolsChecked = 0;
            var projectCount = 0;

            foreach (var project in projects)
            {
                projectCount++;
                if (!project.SupportsCompilation)
                    continue;

                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation == null)
                    continue;

                // Get all symbols in the project
                var symbols = await GetSymbolsToCheck(project, compilation, parameters, cancellationToken);
                totalSymbolsChecked += symbols.Count;

                // Check each symbol for usage
                foreach (var symbol in symbols)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Skip symbols that are likely entry points or framework-required
                    if (IsLikelyUsed(symbol))
                        continue;

                    // Find all references to this symbol
                    var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
                    var referenceCount = references.Sum(r => r.Locations.Count());

                    // If no references found (except its own declaration), it's unused
                    if (referenceCount == 0)
                    {
                        var location = symbol.Locations.FirstOrDefault();
                        if (location != null && location.IsInSource)
                        {
                            var lineSpan = location.GetLineSpan();
                            unusedSymbols.Add(new UnusedCodeInfo
                            {
                                Name = symbol.Name,
                                FullName = symbol.ToDisplayString(),
                                Kind = GetSymbolKindString(symbol),
                                Accessibility = symbol.DeclaredAccessibility.ToString(),
                                ContainingType = symbol.ContainingType?.Name,
                                Location = new LocationInfo
                                {
                                    FilePath = lineSpan.Path,
                                    Line = lineSpan.StartLinePosition.Line + 1,
                                    Column = lineSpan.StartLinePosition.Character + 1,
                                    EndLine = lineSpan.EndLinePosition.Line + 1,
                                    EndColumn = lineSpan.EndLinePosition.Character + 1
                                },
                                Reason = DetermineUnusedReason(symbol),
                                SafeToRemove = IsSafeToRemove(symbol)
                            });
                        }
                    }
                }
            }

            // Pre-estimate tokens for response management
            var estimatedTokens = EstimateResponseTokens(unusedSymbols);
            var shouldTruncate = estimatedTokens > 10000;
            
            if (shouldTruncate && unusedSymbols.Count > 50)
            {
                // Sort by safety and take top 50
                unusedSymbols = unusedSymbols
                    .OrderByDescending(u => u.SafeToRemove)
                    .ThenBy(u => u.Kind)
                    .Take(50)
                    .ToList();
                insights.Insert(0, $"⚠️ Response size limit applied. Showing 50 of {unusedSymbols.Count} unused items.");
            }

            // Generate insights
            GenerateInsights(unusedSymbols, totalSymbolsChecked, projectCount, insights);

            // Generate next actions
            GenerateNextActions(unusedSymbols, actions);

            // Group by file for better organization
            var distribution = unusedSymbols
                .GroupBy(u => u.Location?.FilePath ?? "Unknown")
                .ToDictionary(
                    g => g.Key,
                    g => new UnusedCodeFileInfo
                    {
                        FilePath = g.Key,
                        UnusedCount = g.Count(),
                        Kinds = g.GroupBy(u => u.Kind).ToDictionary(k => k.Key, k => k.Count())
                    }
                );

            var result = new FindUnusedCodeResult
            {
                Success = true,
                Message = $"Found {unusedSymbols.Count} potentially unused code elements",
                Query = new QueryInfo 
                { 
                    AdditionalParams = new Dictionary<string, object> 
                    { 
                        ["scope"] = parameters.Scope ?? "solution",
                        ["includePrivate"] = parameters.IncludePrivate
                    }
                },
                UnusedSymbols = unusedSymbols,
                Summary = new UnusedCodeSummary
                {
                    TotalUnused = unusedSymbols.Count,
                    TotalChecked = totalSymbolsChecked,
                    ProjectsAnalyzed = projectCount,
                    ByKind = unusedSymbols.GroupBy(u => u.Kind).ToDictionary(g => g.Key, g => g.Count()),
                    SafeToRemove = unusedSymbols.Count(u => u.SafeToRemove)
                },
                Distribution = distribution,
                Insights = insights,
                Actions = actions,
                Meta = new ToolMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    Truncated = shouldTruncate,
                    Tokens = estimatedTokens
                }
            };

            // Store full results if truncated
            if (shouldTruncate && _resourceProvider != null)
            {
                result.ResourceUri = _resourceProvider.StoreAnalysisResult(
                    "unused_code",
                    result,
                    "unused_code_analysis");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding unused code");
            return new FindUnusedCodeResult
            {
                Success = false,
                Message = $"Error finding unused code: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new List<string>
                        {
                            "Check the server logs for detailed error information",
                            "Verify the solution is fully loaded and compiled",
                            "Try analyzing a smaller scope (single project or file)"
                        }
                    }
                },
                Query = new QueryInfo { AdditionalParams = new Dictionary<string, object> { ["scope"] = parameters.Scope ?? "solution" } },
                Meta = new ToolMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                }
            };
        }
    }

    private async Task<List<ISymbol>> GetSymbolsToCheck(Project project, Compilation compilation, 
        FindUnusedCodeParams parameters, CancellationToken cancellationToken)
    {
        var symbols = new List<ISymbol>();
        
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (parameters.Scope?.ToLower() == "file" && !string.IsNullOrEmpty(parameters.FilePath))
            {
                var normalizedFilePath = Path.GetFullPath(parameters.FilePath);
                var treeFilePath = Path.GetFullPath(tree.FilePath);
                if (!normalizedFilePath.Equals(treeFilePath, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var semanticModel = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(cancellationToken);

            // Find all type declarations
            var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();
            foreach (var typeDecl in typeDeclarations)
            {
                var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl);
                if (typeSymbol != null && ShouldCheckSymbol(typeSymbol, parameters))
                {
                    symbols.Add(typeSymbol);
                    
                    // Add members
                    foreach (var member in typeSymbol.GetMembers())
                    {
                        if (ShouldCheckSymbol(member, parameters))
                        {
                            symbols.Add(member);
                        }
                    }
                }
            }

            // Find global methods and properties (in top-level programs)
            var members = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                .Where(m => m.Parent is CompilationUnitSyntax);
            
            foreach (var member in members)
            {
                var symbol = semanticModel.GetDeclaredSymbol(member);
                if (symbol != null && ShouldCheckSymbol(symbol, parameters))
                {
                    symbols.Add(symbol);
                }
            }
        }

        return symbols;
    }

    private bool ShouldCheckSymbol(ISymbol symbol, FindUnusedCodeParams parameters)
    {
        // Skip compiler-generated symbols
        if (symbol.IsImplicitlyDeclared || symbol.Name.StartsWith("<"))
            return false;

        // Skip if not including private and symbol is private
        if (!parameters.IncludePrivate && symbol.DeclaredAccessibility == Accessibility.Private)
            return false;

        // Filter by symbol kinds
        if (parameters.SymbolKinds?.Any() == true)
        {
            var symbolKind = GetSymbolKindString(symbol);
            if (!parameters.SymbolKinds.Contains(symbolKind, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        // Include types, methods, properties, fields, and events
        return symbol.Kind is SymbolKind.NamedType or SymbolKind.Method or 
               SymbolKind.Property or SymbolKind.Field or SymbolKind.Event;
    }

    private bool IsLikelyUsed(ISymbol symbol)
    {
        // Main method
        if (symbol is IMethodSymbol method && method.Name == "Main" && method.IsStatic)
            return true;

        // Program class in top-level programs
        if (symbol is INamedTypeSymbol type && type.Name == "Program" && type.IsImplicitClass)
            return true;

        // Has specific attributes that indicate usage
        var attributeNames = symbol.GetAttributes().Select(a => a.AttributeClass?.Name).Where(n => n != null);
        var usageAttributes = new[] { "TestMethod", "Test", "Fact", "Theory", "TestFixture", "TestClass",
                                     "Controller", "ApiController", "Route", "HttpGet", "HttpPost",
                                     "DllImport", "ComVisible", "Serializable" };
        
        if (attributeNames.Any(name => usageAttributes.Contains(name)))
            return true;

        // Interface implementations
        if (symbol.IsOverride || symbol.IsVirtual || symbol.IsAbstract)
            return true;

        // Event handlers
        if (symbol is IMethodSymbol eventHandler && 
            eventHandler.Parameters.Length == 2 &&
            eventHandler.Parameters[0].Type.Name == "Object" &&
            eventHandler.Parameters[1].Type.Name.EndsWith("EventArgs"))
            return true;

        return false;
    }

    private string DetermineUnusedReason(ISymbol symbol)
    {
        if (symbol.DeclaredAccessibility == Accessibility.Private)
            return "Private member with no references";
        
        if (symbol.DeclaredAccessibility == Accessibility.Internal)
            return "Internal member with no references within assembly";
        
        if (symbol.DeclaredAccessibility == Accessibility.Public)
            return "Public member with no references (may be used externally)";
        
        return "No references found";
    }

    private bool IsSafeToRemove(ISymbol symbol)
    {
        // Private symbols are generally safe to remove
        if (symbol.DeclaredAccessibility == Accessibility.Private)
            return true;

        // Internal symbols in non-library projects are relatively safe
        if (symbol.DeclaredAccessibility == Accessibility.Internal)
        {
            // Check if this is a library project (simplified check)
            var isLibrary = symbol.ContainingAssembly?.Name.EndsWith(".dll") == true;
            return !isLibrary;
        }

        // Public symbols are not safe to remove without further analysis
        return false;
    }

    private string GetSymbolKindString(ISymbol symbol)
    {
        return symbol.Kind switch
        {
            SymbolKind.NamedType when symbol is INamedTypeSymbol namedType => namedType.TypeKind switch
            {
                TypeKind.Class => "Class",
                TypeKind.Interface => "Interface",
                TypeKind.Struct => "Struct",
                TypeKind.Enum => "Enum",
                TypeKind.Delegate => "Delegate",
                _ => "Type"
            },
            SymbolKind.Method when symbol is IMethodSymbol methodSymbol => methodSymbol.MethodKind switch
            {
                MethodKind.Constructor => "Constructor",
                MethodKind.PropertyGet => "PropertyGetter",
                MethodKind.PropertySet => "PropertySetter",
                _ => "Method"
            },
            SymbolKind.Property => "Property",
            SymbolKind.Field => "Field",
            SymbolKind.Event => "Event",
            _ => symbol.Kind.ToString()
        };
    }

    private IEnumerable<Project> GetProjectsContainingFile(Solution solution, string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath != null && 
                    Path.GetFullPath(document.FilePath).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    yield return project;
                    break;
                }
            }
        }
    }

    private int EstimateResponseTokens(List<UnusedCodeInfo> symbols)
    {
        var baseTokens = 800;
        var perSymbolTokens = 120;
        return baseTokens + (symbols.Count * perSymbolTokens);
    }

    private void GenerateInsights(List<UnusedCodeInfo> symbols, int totalChecked, int projectCount, List<string> insights)
    {
        if (!symbols.Any())
        {
            insights.Add("✨ No unused code found! Your codebase is well-maintained.");
            return;
        }

        // Overall statistics
        var percentageUnused = totalChecked > 0 ? (symbols.Count * 100.0 / totalChecked) : 0;
        insights.Add($"📊 Found {symbols.Count} unused items out of {totalChecked} checked ({percentageUnused:F1}%)");

        // By accessibility
        var byAccessibility = symbols.GroupBy(s => s.Accessibility).OrderByDescending(g => g.Count());
        var topAccessibility = byAccessibility.FirstOrDefault();
        if (topAccessibility != null)
        {
            insights.Add($"🔒 Most unused code is {topAccessibility.Key.ToLower()} ({topAccessibility.Count()} items)");
        }

        // Safe to remove
        var safeToRemove = symbols.Count(s => s.SafeToRemove);
        if (safeToRemove > 0)
        {
            insights.Add($"✅ {safeToRemove} items are safe to remove (private members)");
        }

        // By kind
        var byKind = symbols.GroupBy(s => s.Kind).OrderByDescending(g => g.Count()).Take(3);
        var kinds = string.Join(", ", byKind.Select(g => $"{g.Key}s ({g.Count()})"));
        insights.Add($"📋 Most common unused: {kinds}");

        // Files with most unused code
        var fileWithMostUnused = symbols.GroupBy(s => Path.GetFileName(s.Location?.FilePath ?? "Unknown"))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (fileWithMostUnused != null && fileWithMostUnused.Count() > 3)
        {
            insights.Add($"📁 '{fileWithMostUnused.Key}' has the most unused code ({fileWithMostUnused.Count()} items)");
        }
    }

    private void GenerateNextActions(List<UnusedCodeInfo> symbols, List<NextAction> actions)
    {
        if (!symbols.Any())
            return;

        // Suggest removing safe items
        var safeItems = symbols.Where(s => s.SafeToRemove).Take(5).ToList();
        if (safeItems.Any())
        {
            var firstSafe = safeItems.First();
            actions.Add(new NextAction
            {
                Id = "remove_safe_unused",
                Description = $"Remove unused private {firstSafe.Kind.ToLower()} '{firstSafe.Name}'",
                ToolName = "editor_remove_symbol",
                Parameters = new
                {
                    filePath = firstSafe.Location?.FilePath,
                    line = firstSafe.Location?.Line,
                    symbolName = firstSafe.Name
                },
                Priority = "high"
            });
        }

        // Suggest analyzing specific file with most unused code
        var fileWithMost = symbols.GroupBy(s => s.Location?.FilePath)
            .Where(g => g.Key != null)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        
        if (fileWithMost != null && fileWithMost.Count() > 3)
        {
            actions.Add(new NextAction
            {
                Id = "analyze_file_metrics",
                Description = $"Analyze code metrics for file with {fileWithMost.Count()} unused items",
                ToolName = "csharp_code_metrics",
                Parameters = new
                {
                    filePath = fileWithMost.Key,
                    scope = "file"
                },
                Priority = "medium"
            });
        }

        // Suggest finding references for public symbols
        var publicUnused = symbols.FirstOrDefault(s => s.Accessibility == "Public");
        if (publicUnused != null)
        {
            actions.Add(new NextAction
            {
                Id = "verify_public_usage",
                Description = $"Verify external usage of public {publicUnused.Kind.ToLower()} '{publicUnused.Name}'",
                ToolName = "csharp_find_all_references",
                Parameters = new
                {
                    filePath = publicUnused.Location?.FilePath,
                    line = publicUnused.Location?.Line,
                    column = publicUnused.Location?.Column
                },
                Priority = "medium"
            });
        }
    }
}

public class FindUnusedCodeParams
{
    [JsonPropertyName("scope")]
    [Description("Scope of analysis: 'solution' (default), 'project', or 'file'")]
    public string? Scope { get; set; }

    [JsonPropertyName("projectName")]
    [Description("Project name when scope is 'project'")]
    public string? ProjectName { get; set; }

    [JsonPropertyName("filePath")]
    [Description("File path when scope is 'file'")]
    public string? FilePath { get; set; }

    [JsonPropertyName("includePrivate")]
    [Description("Include private members in analysis (default: true)")]
    public bool IncludePrivate { get; set; } = true;

    [JsonPropertyName("symbolKinds")]
    [Description("Filter by symbol kinds: 'Class', 'Method', 'Property', 'Field', 'Event'")]
    public List<string>? SymbolKinds { get; set; }

    [JsonPropertyName("excludeTestCode")]
    [Description("Exclude test classes and methods (default: true)")]
    public bool ExcludeTestCode { get; set; } = true;
}

public class FindUnusedCodeResult : ToolResultBase
{
    public override string Operation => "csharp_find_unused_code";

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("unusedSymbols")]
    public List<UnusedCodeInfo>? UnusedSymbols { get; set; }

    [JsonPropertyName("summary")]
    public UnusedCodeSummary? Summary { get; set; }

    [JsonPropertyName("distribution")]
    public Dictionary<string, UnusedCodeFileInfo>? Distribution { get; set; }
}

public class UnusedCodeInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("fullName")]
    public required string FullName { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("accessibility")]
    public required string Accessibility { get; set; }

    [JsonPropertyName("containingType")]
    public string? ContainingType { get; set; }

    [JsonPropertyName("location")]
    public LocationInfo? Location { get; set; }

    [JsonPropertyName("reason")]
    public required string Reason { get; set; }

    [JsonPropertyName("safeToRemove")]
    public bool SafeToRemove { get; set; }
}

public class UnusedCodeSummary
{
    [JsonPropertyName("totalUnused")]
    public int TotalUnused { get; set; }

    [JsonPropertyName("totalChecked")]
    public int TotalChecked { get; set; }

    [JsonPropertyName("projectsAnalyzed")]
    public int ProjectsAnalyzed { get; set; }

    [JsonPropertyName("byKind")]
    public Dictionary<string, int>? ByKind { get; set; }

    [JsonPropertyName("safeToRemove")]
    public int SafeToRemove { get; set; }
}

public class UnusedCodeFileInfo
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }

    [JsonPropertyName("unusedCount")]
    public int UnusedCount { get; set; }

    [JsonPropertyName("kinds")]
    public Dictionary<string, int>? Kinds { get; set; }
}