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
using Microsoft.Extensions.Logging;
using DataAnnotations = System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that analyzes dependencies and coupling between types, namespaces, and projects
/// </summary>
public class DependencyAnalysisTool : McpToolBase<DependencyAnalysisParams, DependencyAnalysisResult>
{
    private readonly ILogger<DependencyAnalysisTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => "csharp_dependency_analysis";
    public override string Description => @"Analyze dependencies and coupling between types, namespaces, and projects. Detects circular dependencies and tight coupling issues.

Critical: Use to identify architectural problems and refactoring opportunities. Circular dependencies and high coupling reduce maintainability and testability.

Prerequisites: Call csharp_load_solution or csharp_load_project first.
Use cases: Architecture analysis, identifying tight coupling, finding circular dependencies, planning refactoring.";

    public DependencyAnalysisTool(
        ILogger<DependencyAnalysisTool> logger,
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

    protected override async Task<DependencyAnalysisResult> ExecuteInternalAsync(
        DependencyAnalysisParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("DependencyAnalysis request: Level={Level}, Scope={Scope}", 
            parameters.AnalysisLevel, parameters.Scope);

        var workspace = _workspaceService.GetActiveWorkspaces().FirstOrDefault();
        if (workspace == null)
        {
            return new DependencyAnalysisResult
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
                        }
                    }
                }
            };
        }

        try
        {
            var solution = workspace.Solution;
            var dependencies = new List<DependencyRelation>();
            var analysisNodes = new Dictionary<string, AnalysisNode>();

            // Perform analysis based on level
            switch (parameters.AnalysisLevel?.ToLower() ?? "namespace")
            {
                case "project":
                    await AnalyzeProjectDependencies(solution, dependencies, analysisNodes, parameters, cancellationToken);
                    break;
                case "type":
                    await AnalyzeTypeDependencies(solution, dependencies, analysisNodes, parameters, cancellationToken);
                    break;
                default: // namespace
                    await AnalyzeNamespaceDependencies(solution, dependencies, analysisNodes, parameters, cancellationToken);
                    break;
            }

            // Limit results if requested
            if (parameters.MaxNodes.HasValue && analysisNodes.Count > parameters.MaxNodes.Value)
            {
                var topNodes = analysisNodes.Values
                    .OrderByDescending(n => n.IncomingCount + n.OutgoingCount)
                    .Take(parameters.MaxNodes.Value)
                    .ToList();

                var nodeNames = new HashSet<string>(topNodes.Select(n => n.Name));
                analysisNodes = topNodes.ToDictionary(n => n.Name);
                dependencies = dependencies.Where(d => nodeNames.Contains(d.From) && nodeNames.Contains(d.To)).ToList();
            }

            // Detect circular dependencies
            var cycles = DetectCircularDependencies(dependencies);

            // Generate insights and actions
            var insights = GenerateInsights(dependencies, analysisNodes.Values.ToList(), cycles);
            var actions = GenerateNextActions(dependencies, cycles, parameters);
            var analysis = GenerateDependencyAnalysis(dependencies, analysisNodes.Values.ToList(), cycles);

            return new DependencyAnalysisResult
            {
                Success = true,
                Message = $"Analyzed {analysisNodes.Count} nodes with {dependencies.Count} dependencies",
                Summary = new SummaryInfo
                {
                    TotalFound = dependencies.Count,
                    Returned = dependencies.Count,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                },
                Dependencies = dependencies,
                Nodes = analysisNodes.Values.ToList(),
                CircularDependencies = cycles,
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
            _logger.LogError(ex, "Error in DependencyAnalysis");
            return new DependencyAnalysisResult
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

    private Task AnalyzeProjectDependencies(Solution solution, List<DependencyRelation> dependencies,
        Dictionary<string, AnalysisNode> nodes, DependencyAnalysisParams parameters, CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            if (!nodes.ContainsKey(project.Name))
            {
                nodes[project.Name] = new AnalysisNode
                {
                    Name = project.Name,
                    FullName = project.FilePath ?? project.Name,
                    Kind = "Project",
                    IncomingCount = 0,
                    OutgoingCount = 0
                };
            }

            // Analyze project references
            foreach (var projectRef in project.ProjectReferences)
            {
                var referencedProject = solution.GetProject(projectRef.ProjectId);
                if (referencedProject != null)
                {
                    if (!nodes.ContainsKey(referencedProject.Name))
                    {
                        nodes[referencedProject.Name] = new AnalysisNode
                        {
                            Name = referencedProject.Name,
                            FullName = referencedProject.FilePath ?? referencedProject.Name,
                            Kind = "Project",
                            IncomingCount = 0,
                            OutgoingCount = 0
                        };
                    }

                    dependencies.Add(new DependencyRelation
                    {
                        From = project.Name,
                        To = referencedProject.Name,
                        Type = "ProjectReference",
                        Strength = 1.0
                    });

                    nodes[project.Name].OutgoingCount++;
                    nodes[referencedProject.Name].IncomingCount++;
                }
            }
        }

        return Task.CompletedTask;
    }

    private async Task AnalyzeNamespaceDependencies(Solution solution, List<DependencyRelation> dependencies,
        Dictionary<string, AnalysisNode> nodes, DependencyAnalysisParams parameters, CancellationToken cancellationToken)
    {
        var namespaceDeps = new Dictionary<string, HashSet<string>>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (!document.Name.EndsWith(".cs")) continue;

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (root == null || semanticModel == null) continue;

                await AnalyzeDocumentForNamespaces(root, semanticModel, namespaceDeps, cancellationToken);
            }
        }

        // Convert to dependency relations
        foreach (var kvp in namespaceDeps)
        {
            var fromNamespace = kvp.Key;
            if (!nodes.ContainsKey(fromNamespace))
            {
                nodes[fromNamespace] = new AnalysisNode
                {
                    Name = fromNamespace,
                    FullName = fromNamespace,
                    Kind = "Namespace",
                    IncomingCount = 0,
                    OutgoingCount = 0
                };
            }

            foreach (var toNamespace in kvp.Value)
            {
                if (fromNamespace == toNamespace) continue; // Skip self-references

                if (!nodes.ContainsKey(toNamespace))
                {
                    nodes[toNamespace] = new AnalysisNode
                    {
                        Name = toNamespace,
                        FullName = toNamespace,
                        Kind = "Namespace",
                        IncomingCount = 0,
                        OutgoingCount = 0
                    };
                }

                dependencies.Add(new DependencyRelation
                {
                    From = fromNamespace,
                    To = toNamespace,
                    Type = "NamespaceReference",
                    Strength = 1.0
                });

                nodes[fromNamespace].OutgoingCount++;
                nodes[toNamespace].IncomingCount++;
            }
        }
    }

    private async Task AnalyzeTypeDependencies(Solution solution, List<DependencyRelation> dependencies,
        Dictionary<string, AnalysisNode> nodes, DependencyAnalysisParams parameters, CancellationToken cancellationToken)
    {
        var typeDeps = new Dictionary<string, HashSet<string>>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (!document.Name.EndsWith(".cs")) continue;

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (root == null || semanticModel == null) continue;

                await AnalyzeDocumentForTypes(root, semanticModel, typeDeps, cancellationToken);
            }
        }

        // Convert to dependency relations
        foreach (var kvp in typeDeps)
        {
            var fromType = kvp.Key;
            if (!nodes.ContainsKey(fromType))
            {
                nodes[fromType] = new AnalysisNode
                {
                    Name = GetSimpleName(fromType),
                    FullName = fromType,
                    Kind = "Type",
                    IncomingCount = 0,
                    OutgoingCount = 0
                };
            }

            foreach (var toType in kvp.Value)
            {
                if (fromType == toType) continue;

                if (!nodes.ContainsKey(toType))
                {
                    nodes[toType] = new AnalysisNode
                    {
                        Name = GetSimpleName(toType),
                        FullName = toType,
                        Kind = "Type",
                        IncomingCount = 0,
                        OutgoingCount = 0
                    };
                }

                dependencies.Add(new DependencyRelation
                {
                    From = fromType,
                    To = toType,
                    Type = "TypeReference",
                    Strength = 1.0
                });

                nodes[fromType].OutgoingCount++;
                nodes[toType].IncomingCount++;
            }
        }
    }

    private Task AnalyzeDocumentForNamespaces(SyntaxNode root, SemanticModel semanticModel,
        Dictionary<string, HashSet<string>> namespaceDeps, CancellationToken cancellationToken)
    {
        var namespaces = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>();
        
        foreach (var ns in namespaces)
        {
            var nsName = ns.Name.ToString();
            if (!namespaceDeps.ContainsKey(nsName))
            {
                namespaceDeps[nsName] = new HashSet<string>();
            }

            // Find using statements
            foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                var usedNamespace = usingDirective.Name?.ToString();
                if (!string.IsNullOrEmpty(usedNamespace) && usedNamespace != nsName)
                {
                    namespaceDeps[nsName].Add(usedNamespace);
                }
            }

            // Find type references
            foreach (var identifier in ns.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var symbolInfo = semanticModel.GetSymbolInfo(identifier, cancellationToken);
                if (symbolInfo.Symbol?.ContainingNamespace != null)
                {
                    var referencedNamespace = symbolInfo.Symbol.ContainingNamespace.ToDisplayString();
                    if (referencedNamespace != nsName && !string.IsNullOrEmpty(referencedNamespace))
                    {
                        namespaceDeps[nsName].Add(referencedNamespace);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    private Task AnalyzeDocumentForTypes(SyntaxNode root, SemanticModel semanticModel,
        Dictionary<string, HashSet<string>> typeDeps, CancellationToken cancellationToken)
    {
        var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

        foreach (var type in types)
        {
            var typeSymbol = semanticModel.GetDeclaredSymbol(type);
            if (typeSymbol == null) continue;

            var typeName = typeSymbol.ToDisplayString();
            if (!typeDeps.ContainsKey(typeName))
            {
                typeDeps[typeName] = new HashSet<string>();
            }

            // Find base type dependencies
            if (type.BaseList != null)
            {
                foreach (var baseType in type.BaseList.Types)
                {
                    var baseSymbol = semanticModel.GetSymbolInfo(baseType.Type, cancellationToken).Symbol;
                    if (baseSymbol != null && !IsFrameworkType(baseSymbol))
                    {
                        typeDeps[typeName].Add(baseSymbol.ToDisplayString());
                    }
                }
            }

            // Find field/property type dependencies
            foreach (var member in type.Members)
            {
                if (member is FieldDeclarationSyntax field)
                {
                    var fieldSymbol = semanticModel.GetSymbolInfo(field.Declaration.Type, cancellationToken).Symbol;
                    if (fieldSymbol != null && !IsFrameworkType(fieldSymbol))
                    {
                        typeDeps[typeName].Add(fieldSymbol.ToDisplayString());
                    }
                }
                else if (member is PropertyDeclarationSyntax property)
                {
                    var propSymbol = semanticModel.GetSymbolInfo(property.Type, cancellationToken).Symbol;
                    if (propSymbol != null && !IsFrameworkType(propSymbol))
                    {
                        typeDeps[typeName].Add(propSymbol.ToDisplayString());
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    private bool IsFrameworkType(ISymbol symbol)
    {
        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        return ns.StartsWith("System") || ns.StartsWith("Microsoft");
    }

    private string GetSimpleName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 ? fullName.Substring(lastDot + 1) : fullName;
    }

    private List<CircularDependency> DetectCircularDependencies(List<DependencyRelation> dependencies)
    {
        var cycles = new List<CircularDependency>();
        var graph = dependencies.GroupBy(d => d.From).ToDictionary(g => g.Key, g => g.Select(d => d.To).ToList());
        
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();

        foreach (var node in graph.Keys)
        {
            if (!visited.Contains(node))
            {
                var path = new List<string>();
                DetectCycles(node, graph, visited, recursionStack, path, cycles);
            }
        }

        return cycles;
    }

    private bool DetectCycles(string node, Dictionary<string, List<string>> graph,
        HashSet<string> visited, HashSet<string> recursionStack, List<string> path, List<CircularDependency> cycles)
    {
        visited.Add(node);
        recursionStack.Add(node);
        path.Add(node);

        if (graph.ContainsKey(node))
        {
            foreach (var neighbor in graph[node])
            {
                if (!visited.Contains(neighbor))
                {
                    if (DetectCycles(neighbor, graph, visited, recursionStack, path, cycles))
                        return true;
                }
                else if (recursionStack.Contains(neighbor))
                {
                    // Found a cycle
                    var cycleStartIndex = path.IndexOf(neighbor);
                    var cyclePath = path.Skip(cycleStartIndex).ToList();
                    cyclePath.Add(neighbor); // Complete the cycle

                    cycles.Add(new CircularDependency
                    {
                        Nodes = cyclePath,
                        Length = cyclePath.Count - 1,
                        Severity = cyclePath.Count > 3 ? "High" : "Medium"
                    });
                    return true;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        recursionStack.Remove(node);
        return false;
    }

    private DependencyAnalysisInfo GenerateDependencyAnalysis(List<DependencyRelation> dependencies,
        List<AnalysisNode> nodes, List<CircularDependency> cycles)
    {
        return new DependencyAnalysisInfo
        {
            TotalNodes = nodes.Count,
            TotalDependencies = dependencies.Count,
            CircularDependencies = cycles.Count,
            MaxIncomingDependencies = nodes.Any() ? nodes.Max(n => n.IncomingCount) : 0,
            MaxOutgoingDependencies = nodes.Any() ? nodes.Max(n => n.OutgoingCount) : 0,
            AverageIncomingDependencies = nodes.Any() ? nodes.Average(n => n.IncomingCount) : 0,
            AverageOutgoingDependencies = nodes.Any() ? nodes.Average(n => n.OutgoingCount) : 0,
            HighlyCoupledNodes = nodes.Where(n => n.IncomingCount + n.OutgoingCount > 10).Count()
        };
    }

    private List<string> GenerateInsights(List<DependencyRelation> dependencies, List<AnalysisNode> nodes, List<CircularDependency> cycles)
    {
        var insights = new List<string>();

        if (dependencies.Count == 0)
        {
            insights.Add("No dependencies found in the analyzed scope");
            return insights;
        }

        insights.Add($"Analyzed {nodes.Count} nodes with {dependencies.Count} dependency relationships");

        if (cycles.Any())
        {
            insights.Add($"Found {cycles.Count} circular dependencies - these should be resolved");
        }
        else
        {
            insights.Add("No circular dependencies detected - good architecture!");
        }

        var highlyCoupled = nodes.Where(n => n.IncomingCount + n.OutgoingCount > 10).ToList();
        if (highlyCoupled.Any())
        {
            insights.Add($"{highlyCoupled.Count} nodes are highly coupled (>10 dependencies) - consider refactoring");
        }

        var averageCoupling = nodes.Any() ? nodes.Average(n => n.IncomingCount + n.OutgoingCount) : 0;
        if (averageCoupling > 5)
        {
            insights.Add($"High average coupling ({averageCoupling:F1}) - architecture may benefit from decoupling");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(List<DependencyRelation> dependencies, List<CircularDependency> cycles, DependencyAnalysisParams parameters)
    {
        var actions = new List<AIAction>();

        if (cycles.Any())
        {
            var firstCycle = cycles.First();
            actions.Add(new AIAction
            {
                Action = "resolve_circular_dependency",
                Description = $"Resolve circular dependency involving {string.Join(" -> ", firstCycle.Nodes)}",
                Parameters = new Dictionary<string, object>
                {
                    ["nodes"] = firstCycle.Nodes
                },
                Priority = 95,
                Category = "architecture"
            });
        }

        return actions;
    }

}

/// <summary>
/// Parameters for DependencyAnalysis tool
/// </summary>
public class DependencyAnalysisParams
{
    [JsonPropertyName("analysisLevel")]
    [COA.Mcp.Framework.Attributes.Description("Level of analysis: 'type', 'namespace', or 'project' (default: 'namespace')")]
    public string? AnalysisLevel { get; set; } = "namespace";

    [JsonPropertyName("scope")]
    [COA.Mcp.Framework.Attributes.Description("Analysis scope: 'solution', 'project', 'namespace', or 'type' (default: 'solution')")]
    public string? Scope { get; set; } = "solution";

    [JsonPropertyName("targetPath")]
    [COA.Mcp.Framework.Attributes.Description("Target path/name for scoped analysis (project name, namespace, or file path)")]
    public string? TargetPath { get; set; }

    [JsonPropertyName("includeExternal")]
    [COA.Mcp.Framework.Attributes.Description("Include external dependencies (default: false)")]
    public bool IncludeExternal { get; set; } = false;

    [JsonPropertyName("maxNodes")]
    [COA.Mcp.Framework.Attributes.Description("Maximum number of nodes to return (default: 100)")]
    public int? MaxNodes { get; set; }
}

public class DependencyAnalysisResult : ToolResultBase
{
    public override string Operation => "csharp_dependency_analysis";
    
    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("dependencies")]
    public List<DependencyRelation>? Dependencies { get; set; }

    [JsonPropertyName("nodes")]
    public List<AnalysisNode>? Nodes { get; set; }

    [JsonPropertyName("circularDependencies")]
    public List<CircularDependency>? CircularDependencies { get; set; }

    [JsonPropertyName("analysis")]
    public DependencyAnalysisInfo? Analysis { get; set; }
}

public class DependencyRelation
{
    [JsonPropertyName("from")]
    public required string From { get; set; }

    [JsonPropertyName("to")]
    public required string To { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("strength")]
    public double Strength { get; set; }
}

public class AnalysisNode
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("fullName")]
    public required string FullName { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("incomingCount")]
    public int IncomingCount { get; set; }

    [JsonPropertyName("outgoingCount")]
    public int OutgoingCount { get; set; }
}

public class CircularDependency
{
    [JsonPropertyName("nodes")]
    public List<string> Nodes { get; set; } = new();

    [JsonPropertyName("length")]
    public int Length { get; set; }

    [JsonPropertyName("severity")]
    public required string Severity { get; set; }
}

public class DependencyAnalysisInfo
{
    [JsonPropertyName("totalNodes")]
    public int TotalNodes { get; set; }

    [JsonPropertyName("totalDependencies")]
    public int TotalDependencies { get; set; }

    [JsonPropertyName("circularDependencies")]
    public int CircularDependencies { get; set; }

    [JsonPropertyName("maxIncomingDependencies")]
    public int MaxIncomingDependencies { get; set; }

    [JsonPropertyName("maxOutgoingDependencies")]
    public int MaxOutgoingDependencies { get; set; }

    [JsonPropertyName("averageIncomingDependencies")]
    public double AverageIncomingDependencies { get; set; }

    [JsonPropertyName("averageOutgoingDependencies")]
    public double AverageOutgoingDependencies { get; set; }

    [JsonPropertyName("highlyCoupledNodes")]
    public int HighlyCoupledNodes { get; set; }
}