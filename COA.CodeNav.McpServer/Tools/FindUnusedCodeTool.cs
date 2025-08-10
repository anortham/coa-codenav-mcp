using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.Interfaces;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using DataAnnotations = System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that finds unused code elements in a project or solution
/// </summary>
public class FindUnusedCodeTool : McpToolBase<FindUnusedCodeParams, FindUnusedCodeResult>
{
    private readonly ILogger<FindUnusedCodeTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => "csharp_find_unused_code";
    public override string Description => @"Find unused code elements in the codebase including classes, methods, properties, and fields.
Returns: List of potentially unused code elements with their locations and types.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if workspace is not loaded.
Use cases: Code cleanup, reducing technical debt, identifying dead code, improving maintainability.
AI benefit: Helps identify code that can be safely removed to reduce complexity.";

    public FindUnusedCodeTool(
        ILogger<FindUnusedCodeTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        ITokenEstimator tokenEstimator,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _tokenEstimator = tokenEstimator;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<FindUnusedCodeResult> ExecuteInternalAsync(
        FindUnusedCodeParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("FindUnusedCode request: Scope={Scope}, FilePath={FilePath}", 
            parameters.Scope, parameters.FilePath);

        var workspace = _workspaceService.GetActiveWorkspaces().FirstOrDefault();
        if (workspace == null)
        {
            return new FindUnusedCodeResult
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

        var solution = workspace.Solution;
        var unusedElements = new List<UnusedCodeElement>();

        try
        {
            switch (parameters.Scope?.ToLower() ?? "solution")
            {
                case "file":
                    if (string.IsNullOrEmpty(parameters.FilePath))
                    {
                        return CreateErrorResult("FilePath is required for file scope", parameters, startTime);
                    }
                    await AnalyzeFileAsync(parameters.FilePath, unusedElements, solution, parameters, cancellationToken);
                    break;

                case "project":
                    if (!string.IsNullOrEmpty(parameters.ProjectName))
                    {
                        var project = solution.Projects.FirstOrDefault(p => 
                            p.Name.Equals(parameters.ProjectName, StringComparison.OrdinalIgnoreCase));
                        if (project == null)
                        {
                            return CreateErrorResult($"Project '{parameters.ProjectName}' not found", parameters, startTime);
                        }
                        await AnalyzeProjectAsync(project, unusedElements, parameters, cancellationToken);
                    }
                    else if (!string.IsNullOrEmpty(parameters.FilePath))
                    {
                        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
                        if (document?.Project != null)
                        {
                            await AnalyzeProjectAsync(document.Project, unusedElements, parameters, cancellationToken);
                        }
                    }
                    else
                    {
                        return CreateErrorResult("ProjectName or FilePath is required for project scope", parameters, startTime);
                    }
                    break;

                default: // "solution"
                    await AnalyzeSolutionAsync(solution, unusedElements, parameters, cancellationToken);
                    break;
            }

            // Apply filters
            if (parameters.SymbolKinds?.Any() == true)
            {
                var allowedKinds = new HashSet<string>(parameters.SymbolKinds, StringComparer.OrdinalIgnoreCase);
                unusedElements = unusedElements.Where(e => allowedKinds.Contains(e.Kind)).ToList();
            }

            if (!parameters.IncludePrivate)
            {
                unusedElements = unusedElements.Where(e => e.Accessibility != "Private").ToList();
            }

            // Apply token optimization if needed
            var totalElements = unusedElements.Count;
            List<UnusedCodeElement> returnedElements;
            bool wasTruncated = false;

            var estimatedTokens = _tokenEstimator.EstimateObject(unusedElements);
            if (estimatedTokens > 10000)
            {
                // Use framework's progressive reduction
                returnedElements = _tokenEstimator.ApplyProgressiveReduction(
                    unusedElements,
                    element => _tokenEstimator.EstimateObject(element),
                    10000,
                    new[] { 100, 75, 50, 25, 10 }
                );
                wasTruncated = true;
                
                _logger.LogWarning("Token optimization applied: reducing unused elements from {Total} to {Safe}", 
                    totalElements, returnedElements.Count);
            }
            else
            {
                returnedElements = unusedElements;
            }

            // Generate insights and actions
            var insights = GenerateInsights(unusedElements);
            var actions = GenerateNextActions(unusedElements, parameters);
            var analysis = GenerateAnalysis(unusedElements);

            if (wasTruncated)
            {
                insights.Insert(0, $"⚠️ Token limit applied. Showing {returnedElements.Count} of {totalElements} unused elements.");
            }

            return new FindUnusedCodeResult
            {
                Success = true,
                Message = $"Found {unusedElements.Count} potentially unused code element(s)",
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath
                },
                Summary = new SummaryInfo
                {
                    TotalFound = unusedElements.Count,
                    Returned = returnedElements.Count,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                },
                UnusedElements = returnedElements,
                Analysis = analysis,
                Insights = insights,
                Actions = actions,
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding unused code");
            return CreateErrorResult($"Error: {ex.Message}", parameters, startTime);
        }
    }

    private async Task AnalyzeFileAsync(string filePath, List<UnusedCodeElement> unusedElements, 
        Solution solution, FindUnusedCodeParams parameters, CancellationToken cancellationToken)
    {
        var document = await _workspaceService.GetDocumentAsync(filePath);
        if (document == null) return;

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (semanticModel == null || root == null) return;

        await AnalyzeSymbolsInNode(root, semanticModel, solution, unusedElements, parameters, cancellationToken);
    }

    private async Task AnalyzeProjectAsync(Project project, List<UnusedCodeElement> unusedElements, 
        FindUnusedCodeParams parameters, CancellationToken cancellationToken)
    {
        foreach (var document in project.Documents)
        {
            if (!document.Name.EndsWith(".cs")) continue;

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (semanticModel == null || root == null) continue;

            await AnalyzeSymbolsInNode(root, semanticModel, project.Solution, unusedElements, parameters, cancellationToken);
        }
    }

    private async Task AnalyzeSolutionAsync(Solution solution, List<UnusedCodeElement> unusedElements, 
        FindUnusedCodeParams parameters, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            await AnalyzeProjectAsync(project, unusedElements, parameters, cancellationToken);
        }
    }

    private async Task AnalyzeSymbolsInNode(SyntaxNode root, SemanticModel semanticModel, Solution solution,
        List<UnusedCodeElement> unusedElements, FindUnusedCodeParams parameters, CancellationToken cancellationToken)
    {
        // Analyze classes, interfaces, structs
        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var symbol = semanticModel.GetDeclaredSymbol(typeDecl);
            if (symbol != null && await IsUnusedAsync(symbol, solution, cancellationToken))
            {
                unusedElements.Add(CreateUnusedElement(symbol, typeDecl.GetLocation()));
            }
        }

        // Analyze methods
        foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var symbol = semanticModel.GetDeclaredSymbol(methodDecl);
            if (symbol != null && await IsUnusedAsync(symbol, solution, cancellationToken))
            {
                // Skip special methods
                if (symbol is IMethodSymbol methodSym && IsSpecialMethod(methodSym)) continue;
                
                unusedElements.Add(CreateUnusedElement(symbol, methodDecl.GetLocation()));
            }
        }

        // Analyze properties
        foreach (var propertyDecl in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            var symbol = semanticModel.GetDeclaredSymbol(propertyDecl);
            if (symbol != null && await IsUnusedAsync(symbol, solution, cancellationToken))
            {
                unusedElements.Add(CreateUnusedElement(symbol, propertyDecl.GetLocation()));
            }
        }

        // Analyze fields
        foreach (var fieldDecl in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            foreach (var variable in fieldDecl.Declaration.Variables)
            {
                var symbol = semanticModel.GetDeclaredSymbol(variable);
                if (symbol != null && await IsUnusedAsync(symbol, solution, cancellationToken))
                {
                    unusedElements.Add(CreateUnusedElement(symbol, variable.GetLocation()));
                }
            }
        }
    }

    private async Task<bool> IsUnusedAsync(ISymbol symbol, Solution solution, CancellationToken cancellationToken)
    {
        // Skip public API members unless requested
        if (symbol.DeclaredAccessibility == Accessibility.Public)
            return false;

        // Skip interface members
        if (symbol.ContainingType?.TypeKind == TypeKind.Interface)
            return false;

        // Skip override members
        if (symbol.IsOverride)
            return false;

        // Find references
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
        
        // Check if there are any references outside of the declaration
        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
            {
                if (!location.Location.IsInSource) continue;
                
                // Skip the declaration itself - simple location comparison
                if (symbol.Locations.Any(l => l.SourceTree?.FilePath == location.Location.SourceTree?.FilePath &&
                                               l.SourceSpan.Start == location.Location.SourceSpan.Start))
                    continue;

                // Found a usage reference
                return false;
            }
        }

        return true; // No references found
    }

    private bool IsSpecialMethod(IMethodSymbol method)
    {
        // Skip constructors, destructors, operators, etc.
        return method.MethodKind != MethodKind.Ordinary ||
               method.IsOverride ||
               method.IsVirtual ||
               method.IsAbstract ||
               method.ContainingType?.TypeKind == TypeKind.Interface;
    }

    private UnusedCodeElement CreateUnusedElement(ISymbol symbol, Location location)
    {
        var lineSpan = location.GetLineSpan();
        
        return new UnusedCodeElement
        {
            Name = symbol.Name,
            FullName = symbol.ToDisplayString(),
            Kind = symbol.Kind.ToString(),
            Accessibility = symbol.DeclaredAccessibility.ToString(),
            ContainingType = symbol.ContainingType?.Name,
            Namespace = symbol.ContainingNamespace?.ToDisplayString(),
            Location = new LocationInfo
            {
                FilePath = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                EndColumn = lineSpan.EndLinePosition.Character + 1
            },
            Reason = "No references found in the analyzed scope"
        };
    }

    private UnusedCodeAnalysis GenerateAnalysis(List<UnusedCodeElement> elements)
    {
        return new UnusedCodeAnalysis
        {
            TotalUnused = elements.Count,
            ByKind = elements.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count()),
            ByAccessibility = elements.GroupBy(e => e.Accessibility).ToDictionary(g => g.Key, g => g.Count()),
            ByNamespace = elements.GroupBy(e => e.Namespace ?? "").ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private List<string> GenerateInsights(List<UnusedCodeElement> elements)
    {
        var insights = new List<string>();
        var analysis = GenerateAnalysis(elements);

        if (elements.Count == 0)
        {
            insights.Add("No unused code elements found - good code hygiene!");
            return insights;
        }

        insights.Add($"Found {elements.Count} potentially unused code elements");

        var mostCommonKind = analysis.ByKind.OrderByDescending(kvp => kvp.Value).FirstOrDefault();
        if (mostCommonKind.Value > 0)
        {
            insights.Add($"Most unused element type: {mostCommonKind.Key} ({mostCommonKind.Value} items)");
        }

        var privateCount = analysis.ByAccessibility.GetValueOrDefault("Private", 0);
        if (privateCount > elements.Count * 0.8)
        {
            insights.Add($"Most unused elements are private ({privateCount}) - safe to remove");
        }

        if (analysis.ByNamespace.Count > 5)
        {
            insights.Add($"Unused code spread across {analysis.ByNamespace.Count} namespaces - systematic cleanup needed");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(List<UnusedCodeElement> elements, FindUnusedCodeParams parameters)
    {
        var actions = new List<AIAction>();

        if (elements.Any())
        {
            // Suggest refactoring the first unused method
            var firstMethod = elements.FirstOrDefault(e => e.Kind == "Method");
            if (firstMethod != null)
            {
                actions.Add(new AIAction
                {
                    Action = "review_and_remove",
                    Description = $"Review and consider removing unused {firstMethod.Kind.ToLower()} '{firstMethod.Name}'",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = firstMethod.Location.FilePath,
                        ["line"] = firstMethod.Location.Line,
                        ["column"] = firstMethod.Location.Column
                    },
                    Priority = 80,
                    Category = "cleanup"
                });
            }

            // Suggest broader analysis if file scope
            if (parameters.Scope?.ToLower() == "file")
            {
                actions.Add(new AIAction
                {
                    Action = "csharp_find_unused_code",
                    Description = "Analyze entire project for unused code",
                    Parameters = new Dictionary<string, object>
                    {
                        ["scope"] = "project",
                        ["filePath"] = parameters.FilePath ?? "",
                        ["includePrivate"] = true
                    },
                    Priority = 60,
                    Category = "analysis"
                });
            }
        }

        return actions;
    }

    private FindUnusedCodeResult CreateErrorResult(string message, FindUnusedCodeParams parameters, DateTime startTime)
    {
        return new FindUnusedCodeResult
        {
            Success = false,
            Message = message,
            Error = new ErrorInfo
            {
                Code = ErrorCodes.INTERNAL_ERROR,
                Message = message
            },
            Query = new QueryInfo { FilePath = parameters.FilePath },
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
    }
}

/// <summary>
/// Parameters for FindUnusedCode tool
/// </summary>
public class FindUnusedCodeParams
{
    [JsonPropertyName("scope")]
    [COA.Mcp.Framework.Attributes.Description("Scope of analysis: 'solution', 'project', or 'file'")]
    public string? Scope { get; set; } = "solution";

    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("File path when scope is 'file'")]
    public string? FilePath { get; set; }

    [JsonPropertyName("projectName")]
    [COA.Mcp.Framework.Attributes.Description("Project name when scope is 'project'")]
    public string? ProjectName { get; set; }

    [JsonPropertyName("symbolKinds")]
    [COA.Mcp.Framework.Attributes.Description("Filter by symbol kinds: 'Class', 'Method', 'Property', 'Field', 'Event'")]
    public string[]? SymbolKinds { get; set; }

    [JsonPropertyName("includePrivate")]
    [COA.Mcp.Framework.Attributes.Description("Include private members in analysis (default: true)")]
    public bool IncludePrivate { get; set; } = true;

    [JsonPropertyName("excludeTestCode")]
    [COA.Mcp.Framework.Attributes.Description("Exclude test classes and methods (default: true)")]
    public bool ExcludeTestCode { get; set; } = true;
}

public class FindUnusedCodeResult : ToolResultBase
{
    public override string Operation => "csharp_find_unused_code";
    
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }
    
    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("unusedElements")]
    public List<UnusedCodeElement>? UnusedElements { get; set; }

    [JsonPropertyName("analysis")]
    public UnusedCodeAnalysis? Analysis { get; set; }
}

public class UnusedCodeElement
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

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("location")]
    public required LocationInfo Location { get; set; }

    [JsonPropertyName("reason")]
    public required string Reason { get; set; }
}

public class UnusedCodeAnalysis
{
    [JsonPropertyName("totalUnused")]
    public int TotalUnused { get; set; }

    [JsonPropertyName("byKind")]
    public Dictionary<string, int> ByKind { get; set; } = new();

    [JsonPropertyName("byAccessibility")]
    public Dictionary<string, int> ByAccessibility { get; set; } = new();

    [JsonPropertyName("byNamespace")]
    public Dictionary<string, int> ByNamespace { get; set; } = new();
}