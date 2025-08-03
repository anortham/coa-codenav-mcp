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
/// MCP tool that analyzes dependencies and coupling between components
/// </summary>
[McpServerToolType]
public partial class DependencyAnalysisTool : ITool
{
    private readonly ILogger<DependencyAnalysisTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "csharp_dependency_analysis";
    public string Description => "Analyze dependencies and coupling between components";

    public DependencyAnalysisTool(
        ILogger<DependencyAnalysisTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "csharp_dependency_analysis")]
    [Description(@"Analyze dependencies and coupling between types, namespaces, and projects.
Returns: Dependency graph with coupling metrics and circular dependency detection.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if analysis fails.
Use cases: Architecture analysis, identifying tight coupling, finding circular dependencies.
AI benefit: Reveals architectural issues and coupling patterns that impact maintainability.")]
    public async Task<object> ExecuteAsync(DependencyAnalysisParams parameters, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("DependencyAnalysis request: Scope={Scope}, Level={Level}", 
            parameters.Scope, parameters.AnalysisLevel);
            
        try
        {
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
            _logger.LogInformation("Analyzing dependencies for {ProjectCount} projects", 
                solution.Projects.Count());

            // Determine scope
            var scopedProjects = await GetScopedProjectsAsync(solution, parameters, cancellationToken);
            if (!scopedProjects.Any())
            {
                return CreateErrorResult(
                    ErrorCodes.INVALID_PARAMETERS,
                    "No projects found matching the specified scope",
                    new List<string>
                    {
                        "Check the target path or namespace",
                        "Ensure the project/namespace exists",
                        "Try with a broader scope"
                    },
                    parameters,
                    startTime);
            }

            // Build dependency graph
            var dependencyGraph = await BuildDependencyGraphAsync(
                scopedProjects,
                parameters.AnalysisLevel,
                parameters.IncludeExternal,
                cancellationToken);

            // Find circular dependencies
            var circularDependencies = FindCircularDependencies(dependencyGraph);

            // Calculate metrics
            var metrics = CalculateMetrics(dependencyGraph);

            // Create analysis
            var analysis = new DependencyAnalysisInfo
            {
                AnalysisLevel = parameters.AnalysisLevel,
                NodeCount = dependencyGraph.Nodes.Count,
                EdgeCount = dependencyGraph.Edges.Count,
                NodesByType = dependencyGraph.Nodes.Values.GroupBy(n => n.Type).ToDictionary(g => g.Key, g => g.Count()),
                EdgesByType = dependencyGraph.Edges.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count()),
                MaxDependencyDepth = CalculateMaxDepth(dependencyGraph),
                ConnectedComponents = CountConnectedComponents(dependencyGraph)
            };

            // Apply token management
            var nodes = dependencyGraph.Nodes.Values.ToList();
            var response = TokenEstimator.CreateTokenAwareResponse(
                nodes,
                n => EstimateDependencyNodesTokens(n),
                requestedMax: parameters.MaxNodes ?? 100,
                safetyLimit: TokenEstimator.DEFAULT_SAFETY_LIMIT,
                toolName: "csharp_dependency_analysis"
            );

            // Create limited graph with only included nodes
            var limitedGraph = CreateLimitedGraph(dependencyGraph, response.Items);

            // Generate insights
            var insights = GenerateInsights(analysis, metrics, circularDependencies);
            if (response.WasTruncated)
            {
                insights.Insert(0, response.GetTruncationMessage());
            }

            // Generate next actions
            var nextActions = GenerateNextActions(parameters, analysis, metrics);
            if (response.WasTruncated)
            {
                nextActions.Insert(0, new NextAction
                {
                    Id = "get_full_graph",
                    Description = "Get complete dependency graph without truncation",
                    ToolName = "csharp_dependency_analysis",
                    Parameters = new
                    {
                        scope = parameters.Scope,
                        targetPath = parameters.TargetPath,
                        analysisLevel = parameters.AnalysisLevel,
                        maxNodes = dependencyGraph.Nodes.Count
                    },
                    Priority = "high"
                });
            }

            // Store full result if truncated
            string? resourceUri = null;
            if (response.WasTruncated && _resourceProvider != null)
            {
                resourceUri = _resourceProvider.StoreAnalysisResult(
                    "dependency-graph",
                    new { 
                        graph = dependencyGraph,
                        analysis = analysis,
                        metrics = metrics,
                        circularDependencies = circularDependencies
                    },
                    $"Complete dependency graph with {dependencyGraph.Nodes.Count} nodes");
            }

            var result = new DependencyAnalysisResult
            {
                Success = true,
                Message = GenerateResultMessage(analysis, response),
                Query = CreateQueryInfo(parameters),
                Summary = new SummaryInfo
                {
                    TotalFound = dependencyGraph.Nodes.Count,
                    Returned = response.ReturnedCount,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                },
                Graph = limitedGraph,
                Analysis = analysis,
                Metrics = metrics,
                CircularDependencies = circularDependencies.Take(10).ToList(), // Limit circular deps
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

            _logger.LogInformation("Dependency analysis completed: {NodeCount} nodes, {EdgeCount} edges", 
                dependencyGraph.Nodes.Count, dependencyGraph.Edges.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in DependencyAnalysis");
            return CreateErrorResult(
                ErrorCodes.INTERNAL_ERROR,
                $"Error: {ex.Message}",
                new List<string>
                {
                    "Check the server logs for detailed error information",
                    "Verify the solution is loaded correctly",
                    "Try with a smaller scope"
                },
                parameters,
                startTime);
        }
    }

    private async Task<List<Project>> GetScopedProjectsAsync(
        Solution solution,
        DependencyAnalysisParams parameters,
        CancellationToken cancellationToken)
    {
        var projects = new List<Project>();

        switch (parameters.Scope)
        {
            case "solution":
                projects.AddRange(solution.Projects);
                break;
                
            case "project":
                if (!string.IsNullOrEmpty(parameters.TargetPath))
                {
                    var project = solution.Projects.FirstOrDefault(p => 
                        p.Name.Equals(parameters.TargetPath, StringComparison.OrdinalIgnoreCase) ||
                        p.FilePath?.Contains(parameters.TargetPath, StringComparison.OrdinalIgnoreCase) == true);
                    
                    if (project != null)
                        projects.Add(project);
                }
                break;
                
            case "namespace":
                // Add projects containing the namespace
                foreach (var project in solution.Projects)
                {
                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation != null)
                    {
                        var hasNamespace = compilation.GlobalNamespace
                            .GetNamespaceMembers()
                            .Any(ns => IsNamespaceMatch(ns, parameters.TargetPath));
                        
                        if (hasNamespace)
                            projects.Add(project);
                    }
                }
                break;
                
            case "type":
                if (!string.IsNullOrEmpty(parameters.TargetPath))
                {
                    // Find document containing the type
                    var document = await _workspaceService.GetDocumentAsync(parameters.TargetPath);
                    if (document != null)
                        projects.Add(document.Project);
                }
                break;
        }

        return projects;
    }

    private bool IsNamespaceMatch(INamespaceSymbol ns, string? targetNamespace)
    {
        if (string.IsNullOrEmpty(targetNamespace))
            return false;
            
        var fullName = ns.ToDisplayString();
        if (fullName.StartsWith(targetNamespace, StringComparison.OrdinalIgnoreCase))
            return true;
            
        return ns.GetNamespaceMembers().Any(child => IsNamespaceMatch(child, targetNamespace));
    }

    private async Task<DependencyGraph> BuildDependencyGraphAsync(
        List<Project> projects,
        string analysisLevel,
        bool includeExternal,
        CancellationToken cancellationToken)
    {
        var graph = new DependencyGraph();
        
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            switch (analysisLevel)
            {
                case "type":
                    await AnalyzeTypeDependenciesAsync(project, compilation, graph, includeExternal, cancellationToken);
                    break;
                    
                case "namespace":
                    await AnalyzeNamespaceDependenciesAsync(project, compilation, graph, includeExternal, cancellationToken);
                    break;
                    
                case "project":
                    await AnalyzeProjectDependenciesAsync(project, projects, graph, cancellationToken);
                    break;
            }
        }

        return graph;
    }

    private async Task AnalyzeTypeDependenciesAsync(
        Project project,
        Compilation compilation,
        DependencyGraph graph,
        bool includeExternal,
        CancellationToken cancellationToken)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var root = await tree.GetRootAsync(cancellationToken);
            var model = compilation.GetSemanticModel(tree);

            // Find all type declarations
            var typeDeclarations = root.DescendantNodes()
                .Where(n => n is TypeDeclarationSyntax || n is EnumDeclarationSyntax || n is DelegateDeclarationSyntax);

            foreach (var typeDecl in typeDeclarations)
            {
                var typeSymbol = model.GetDeclaredSymbol(typeDecl);
                if (typeSymbol == null) continue;

                var typeNode = GetOrCreateNode(graph, typeSymbol, "Type", project.Name);

                // Analyze base types
                if (typeSymbol is INamedTypeSymbol namedType)
                {
                    if (namedType.BaseType != null && ShouldIncludeType(namedType.BaseType, includeExternal))
                    {
                        var baseNode = GetOrCreateNode(graph, namedType.BaseType, "Type", GetProjectName(namedType.BaseType));
                        AddEdge(graph, typeNode, baseNode, "Inherits");
                    }

                    // Analyze interfaces
                    foreach (var iface in namedType.Interfaces)
                    {
                        if (ShouldIncludeType(iface, includeExternal))
                        {
                            var ifaceNode = GetOrCreateNode(graph, iface, "Type", GetProjectName(iface));
                            AddEdge(graph, typeNode, ifaceNode, "Implements");
                        }
                    }
                }

                // Analyze member dependencies
                var memberRefs = typeDecl.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .Select(id => model.GetSymbolInfo(id).Symbol)
                    .Where(s => s != null)
                    .Select(s => s!.ContainingType)
                    .Where(t => t != null && !SymbolEqualityComparer.Default.Equals(t, typeSymbol))
                    .Distinct(SymbolEqualityComparer.Default);

                foreach (var refType in memberRefs)
                {
                    if (refType != null && refType is ITypeSymbol typeRef && ShouldIncludeType(typeRef, includeExternal))
                    {
                        var refNode = GetOrCreateNode(graph, typeRef, "Type", GetProjectName(typeRef));
                        AddEdge(graph, typeNode, refNode, "Uses");
                    }
                }
            }
        }
    }

    private async Task AnalyzeNamespaceDependenciesAsync(
        Project project,
        Compilation compilation,
        DependencyGraph graph,
        bool includeExternal,
        CancellationToken cancellationToken)
    {
        var namespaceUsages = new Dictionary<string, HashSet<string>>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var root = await tree.GetRootAsync(cancellationToken);
            var model = compilation.GetSemanticModel(tree);

            // Get namespace of current file
            var namespaceDecl = root.DescendantNodes()
                .OfType<NamespaceDeclarationSyntax>()
                .FirstOrDefault();
                
            var currentNamespace = namespaceDecl?.Name.ToString() ?? "<global>";

            // Track all type references
            var typeRefs = root.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Select(id => model.GetSymbolInfo(id).Symbol)
                .Where(s => s != null && s.ContainingNamespace != null)
                .Select(s => s!.ContainingNamespace.ToDisplayString())
                .Where(ns => ns != currentNamespace && (includeExternal || !IsExternalNamespace(ns)))
                .Distinct();

            if (!namespaceUsages.ContainsKey(currentNamespace))
                namespaceUsages[currentNamespace] = new HashSet<string>();

            foreach (var refNs in typeRefs)
            {
                namespaceUsages[currentNamespace].Add(refNs);
            }
        }

        // Build graph from namespace usages
        foreach (var kvp in namespaceUsages)
        {
            var sourceNode = GetOrCreateNode(graph, kvp.Key, "Namespace", project.Name);
            
            foreach (var targetNs in kvp.Value)
            {
                var targetNode = GetOrCreateNode(graph, targetNs, "Namespace", project.Name);
                AddEdge(graph, sourceNode, targetNode, "Uses");
            }
        }
    }

    private async Task AnalyzeProjectDependenciesAsync(
        Project project,
        List<Project> allProjects,
        DependencyGraph graph,
        CancellationToken cancellationToken)
    {
        var projectNode = GetOrCreateNode(graph, project.Name, "Project", project.Name);

        // Analyze project references
        foreach (var projectRef in project.ProjectReferences)
        {
            var refProject = allProjects.FirstOrDefault(p => p.Id == projectRef.ProjectId);
            if (refProject != null)
            {
                var refNode = GetOrCreateNode(graph, refProject.Name, "Project", refProject.Name);
                AddEdge(graph, projectNode, refNode, "References");
            }
        }

        // Analyze assembly references
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation != null)
        {
            foreach (var assemblyRef in compilation.ReferencedAssemblyNames)
            {
                if (!IsSystemAssembly(assemblyRef.Name))
                {
                    var assemblyNode = GetOrCreateNode(graph, assemblyRef.Name, "Assembly", "External");
                    AddEdge(graph, projectNode, assemblyNode, "References");
                }
            }
        }
    }

    private DependencyNode GetOrCreateNode(DependencyGraph graph, ISymbol symbol, string nodeType, string projectName)
    {
        var id = symbol.ToDisplayString();
        return GetOrCreateNode(graph, id, nodeType, projectName, symbol.Name);
    }

    private DependencyNode GetOrCreateNode(DependencyGraph graph, string id, string nodeType, string projectName, string? displayName = null)
    {
        if (!graph.Nodes.ContainsKey(id))
        {
            graph.Nodes[id] = new DependencyNode
            {
                Id = id,
                Name = displayName ?? id.Split('.').Last(),
                FullName = id,
                Type = nodeType,
                Project = projectName,
                IncomingEdges = new List<DependencyEdge>(),
                OutgoingEdges = new List<DependencyEdge>()
            };
        }
        return graph.Nodes[id];
    }

    private void AddEdge(DependencyGraph graph, DependencyNode source, DependencyNode target, string edgeType)
    {
        var edge = new DependencyEdge
        {
            SourceId = source.Id,
            TargetId = target.Id,
            Type = edgeType
        };

        graph.Edges.Add(edge);
        source.OutgoingEdges.Add(edge);
        target.IncomingEdges.Add(edge);
    }

    private bool ShouldIncludeType(ITypeSymbol type, bool includeExternal)
    {
        if (includeExternal)
            return true;
            
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        return !IsExternalNamespace(ns);
    }

    private bool IsExternalNamespace(string ns)
    {
        return ns.StartsWith("System.") || 
               ns.StartsWith("Microsoft.") ||
               ns.StartsWith("Newtonsoft.") ||
               string.IsNullOrEmpty(ns);
    }

    private bool IsSystemAssembly(string assemblyName)
    {
        return assemblyName.StartsWith("System.") ||
               assemblyName.StartsWith("Microsoft.") ||
               assemblyName == "mscorlib" ||
               assemblyName == "netstandard";
    }

    private string GetProjectName(ITypeSymbol type)
    {
        // Try to determine project from assembly name
        var assembly = type.ContainingAssembly?.Name;
        return string.IsNullOrEmpty(assembly) ? "Unknown" : assembly;
    }

    private List<CircularDependency> FindCircularDependencies(DependencyGraph graph)
    {
        var cycles = new List<CircularDependency>();
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();
        var currentPath = new Stack<string>();

        foreach (var node in graph.Nodes.Values)
        {
            if (!visited.Contains(node.Id))
            {
                FindCyclesUtil(node, graph, visited, recursionStack, currentPath, cycles);
            }
        }

        return cycles;
    }

    private void FindCyclesUtil(
        DependencyNode node,
        DependencyGraph graph,
        HashSet<string> visited,
        HashSet<string> recursionStack,
        Stack<string> currentPath,
        List<CircularDependency> cycles)
    {
        visited.Add(node.Id);
        recursionStack.Add(node.Id);
        currentPath.Push(node.Id);

        foreach (var edge in node.OutgoingEdges)
        {
            if (!visited.Contains(edge.TargetId))
            {
                var targetNode = graph.Nodes[edge.TargetId];
                FindCyclesUtil(targetNode, graph, visited, recursionStack, currentPath, cycles);
            }
            else if (recursionStack.Contains(edge.TargetId))
            {
                // Found a cycle
                var cycle = new List<string>();
                var found = false;
                
                foreach (var nodeId in currentPath.Reverse())
                {
                    if (nodeId == edge.TargetId)
                        found = true;
                    if (found)
                        cycle.Add(nodeId);
                }
                
                cycle.Reverse();
                cycle.Add(edge.TargetId); // Complete the cycle
                
                cycles.Add(new CircularDependency
                {
                    Nodes = cycle,
                    Severity = DetermineCycleSeverity(cycle, graph)
                });
            }
        }

        currentPath.Pop();
        recursionStack.Remove(node.Id);
    }

    private string DetermineCycleSeverity(List<string> cycle, DependencyGraph graph)
    {
        // Cycles between projects are most severe
        var nodeTypes = cycle.Select(id => graph.Nodes[id].Type).Distinct().ToList();
        
        if (nodeTypes.Contains("Project"))
            return "High";
        else if (nodeTypes.Contains("Namespace"))
            return "Medium";
        else
            return "Low";
    }

    private DependencyMetrics CalculateMetrics(DependencyGraph graph)
    {
        var metrics = new DependencyMetrics
        {
            TotalNodes = graph.Nodes.Count,
            TotalEdges = graph.Edges.Count,
            AverageOutgoingDependencies = graph.Nodes.Count > 0 
                ? (double)graph.Edges.Count / graph.Nodes.Count 
                : 0
        };

        // Calculate coupling metrics
        foreach (var node in graph.Nodes.Values)
        {
            var afferentCoupling = node.IncomingEdges.Count; // Ca
            var efferentCoupling = node.OutgoingEdges.Count; // Ce
            var totalCoupling = afferentCoupling + efferentCoupling;
            
            var stability = totalCoupling > 0 
                ? (double)efferentCoupling / totalCoupling 
                : 0;

            metrics.NodeMetrics.Add(new NodeMetric
            {
                NodeId = node.Id,
                AfferentCoupling = afferentCoupling,
                EfferentCoupling = efferentCoupling,
                Instability = stability
            });

            // Track highly coupled nodes
            if (totalCoupling > 10)
            {
                metrics.HighlyCoupledNodes.Add(node.Id);
            }

            // Track hub nodes (many outgoing)
            if (efferentCoupling > 7)
            {
                metrics.HubNodes.Add(node.Id);
            }

            // Track god nodes (many incoming)
            if (afferentCoupling > 10)
            {
                metrics.GodNodes.Add(node.Id);
            }
        }

        // Sort metrics by coupling
        metrics.NodeMetrics = metrics.NodeMetrics
            .OrderByDescending(m => m.AfferentCoupling + m.EfferentCoupling)
            .Take(50) // Limit to top 50
            .ToList();

        return metrics;
    }

    private DependencyGraph CreateLimitedGraph(DependencyGraph fullGraph, List<DependencyNode> includedNodes)
    {
        var limitedGraph = new DependencyGraph();
        var includedIds = new HashSet<string>(includedNodes.Select(n => n.Id));

        // Add included nodes
        foreach (var node in includedNodes)
        {
            limitedGraph.Nodes[node.Id] = new DependencyNode
            {
                Id = node.Id,
                Name = node.Name,
                FullName = node.FullName,
                Type = node.Type,
                Project = node.Project,
                IncomingEdges = new List<DependencyEdge>(),
                OutgoingEdges = new List<DependencyEdge>()
            };
        }

        // Add edges between included nodes
        foreach (var edge in fullGraph.Edges)
        {
            if (includedIds.Contains(edge.SourceId) && includedIds.Contains(edge.TargetId))
            {
                var newEdge = new DependencyEdge
                {
                    SourceId = edge.SourceId,
                    TargetId = edge.TargetId,
                    Type = edge.Type
                };
                
                limitedGraph.Edges.Add(newEdge);
                limitedGraph.Nodes[edge.SourceId].OutgoingEdges.Add(newEdge);
                limitedGraph.Nodes[edge.TargetId].IncomingEdges.Add(newEdge);
            }
        }

        return limitedGraph;
    }

    private int EstimateDependencyNodesTokens(List<DependencyNode> nodes)
    {
        return TokenEstimator.EstimateCollection(
            nodes,
            node => {
                var tokens = 150; // Base for node structure
                tokens += TokenEstimator.EstimateString(node.FullName);
                tokens += node.IncomingEdges.Count * 20;
                tokens += node.OutgoingEdges.Count * 20;
                return tokens;
            },
            baseTokens: TokenEstimator.BASE_RESPONSE_TOKENS
        );
    }

    private DependencyAnalysisInfo GenerateAnalysis(
        DependencyGraph graph,
        DependencyAnalysisParams parameters)
    {
        var nodesByType = graph.Nodes.Values.GroupBy(n => n.Type).ToDictionary(g => g.Key, g => g.Count());
        var edgesByType = graph.Edges.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());

        return new DependencyAnalysisInfo
        {
            AnalysisLevel = parameters.AnalysisLevel,
            NodeCount = graph.Nodes.Count,
            EdgeCount = graph.Edges.Count,
            NodesByType = nodesByType,
            EdgesByType = edgesByType,
            ConnectedComponents = CountConnectedComponents(graph),
            MaxDependencyDepth = CalculateMaxDepth(graph)
        };
    }

    private int CountConnectedComponents(DependencyGraph graph)
    {
        var visited = new HashSet<string>();
        var components = 0;

        foreach (var node in graph.Nodes.Values)
        {
            if (!visited.Contains(node.Id))
            {
                components++;
                DfsVisit(node, graph, visited);
            }
        }

        return components;
    }

    private void DfsVisit(DependencyNode node, DependencyGraph graph, HashSet<string> visited)
    {
        visited.Add(node.Id);
        
        foreach (var edge in node.OutgoingEdges.Concat(node.IncomingEdges))
        {
            var neighborId = edge.SourceId == node.Id ? edge.TargetId : edge.SourceId;
            if (!visited.Contains(neighborId) && graph.Nodes.ContainsKey(neighborId))
            {
                DfsVisit(graph.Nodes[neighborId], graph, visited);
            }
        }
    }

    private int CalculateMaxDepth(DependencyGraph graph)
    {
        var maxDepth = 0;
        var memo = new Dictionary<string, int>();

        foreach (var node in graph.Nodes.Values)
        {
            var depth = CalculateNodeDepth(node, graph, memo, new HashSet<string>());
            maxDepth = Math.Max(maxDepth, depth);
        }

        return maxDepth;
    }

    private int CalculateNodeDepth(
        DependencyNode node,
        DependencyGraph graph,
        Dictionary<string, int> memo,
        HashSet<string> currentPath)
    {
        if (memo.ContainsKey(node.Id))
            return memo[node.Id];

        if (currentPath.Contains(node.Id))
            return 0; // Cycle detected

        currentPath.Add(node.Id);
        var maxChildDepth = 0;

        foreach (var edge in node.OutgoingEdges)
        {
            if (graph.Nodes.ContainsKey(edge.TargetId))
            {
                var childDepth = CalculateNodeDepth(graph.Nodes[edge.TargetId], graph, memo, currentPath);
                maxChildDepth = Math.Max(maxChildDepth, childDepth);
            }
        }

        currentPath.Remove(node.Id);
        memo[node.Id] = maxChildDepth + 1;
        return maxChildDepth + 1;
    }

    private List<string> GenerateInsights(
        DependencyAnalysisInfo analysis,
        DependencyMetrics metrics,
        List<CircularDependency> circularDeps)
    {
        var insights = new List<string>();

        insights.Add($"Analyzed {analysis.NodeCount} components with {analysis.EdgeCount} dependencies");

        if (metrics.AverageOutgoingDependencies > 5)
        {
            insights.Add($"⚠️ High coupling detected: average {metrics.AverageOutgoingDependencies:F1} dependencies per component");
        }

        if (circularDeps.Any())
        {
            var highSeverity = circularDeps.Count(c => c.Severity == "High");
            if (highSeverity > 0)
            {
                insights.Add($"🔴 {highSeverity} high-severity circular dependencies found");
            }
            else
            {
                insights.Add($"⚠️ {circularDeps.Count} circular dependencies detected");
            }
        }

        if (metrics.GodNodes.Any())
        {
            insights.Add($"{metrics.GodNodes.Count} highly depended-upon components (god objects) identified");
        }

        if (metrics.HubNodes.Any())
        {
            insights.Add($"{metrics.HubNodes.Count} hub components with many outgoing dependencies");
        }

        if (analysis.ConnectedComponents > 1)
        {
            insights.Add($"Solution has {analysis.ConnectedComponents} disconnected component groups");
        }

        if (analysis.MaxDependencyDepth > 10)
        {
            insights.Add($"Deep dependency chains detected (max depth: {analysis.MaxDependencyDepth})");
        }

        return insights;
    }

    private List<NextAction> GenerateNextActions(
        DependencyAnalysisParams parameters,
        DependencyAnalysisInfo analysis,
        DependencyMetrics metrics)
    {
        var actions = new List<NextAction>();

        // Suggest analyzing highly coupled nodes
        if (metrics.HighlyCoupledNodes.Any())
        {
            var topCoupled = metrics.HighlyCoupledNodes.First();
            actions.Add(new NextAction
            {
                Id = "analyze_coupled_node",
                Description = $"Analyze highly coupled component '{topCoupled}'",
                ToolName = "csharp_dependency_analysis",
                Parameters = new
                {
                    scope = "type",
                    targetPath = topCoupled,
                    analysisLevel = "type"
                },
                Priority = "high"
            });
        }

        // Suggest different analysis levels
        if (parameters.AnalysisLevel != "namespace")
        {
            actions.Add(new NextAction
            {
                Id = "namespace_analysis",
                Description = "Analyze dependencies at namespace level",
                ToolName = "csharp_dependency_analysis",
                Parameters = new
                {
                    scope = parameters.Scope,
                    targetPath = parameters.TargetPath,
                    analysisLevel = "namespace"
                },
                Priority = "medium"
            });
        }

        // Suggest finding unused code if many disconnected components
        if (analysis.ConnectedComponents > 5)
        {
            actions.Add(new NextAction
            {
                Id = "find_unused",
                Description = "Find potentially unused code in disconnected components",
                ToolName = "csharp_find_unused_code",
                Parameters = new
                {
                    scope = "solution"
                },
                Priority = "medium"
            });
        }

        // Suggest refactoring for circular dependencies
        if (metrics.NodeMetrics.Any())
        {
            actions.Add(new NextAction
            {
                Id = "extract_interface",
                Description = "Extract interfaces to break circular dependencies",
                ToolName = "csharp_generate_code",
                Parameters = new
                {
                    generationType = "interface"
                },
                Priority = "high"
            });
        }

        return actions;
    }

    private string GenerateResultMessage(DependencyAnalysisInfo analysis, TokenAwareResponse<DependencyNode> response)
    {
        var message = $"Found {analysis.NodeCount} {analysis.AnalysisLevel}s with {analysis.EdgeCount} dependencies";
        
        if (response.WasTruncated)
        {
            message += $" (showing {response.ReturnedCount} nodes)";
        }

        return message;
    }

    private QueryInfo CreateQueryInfo(DependencyAnalysisParams parameters)
    {
        return new QueryInfo
        {
            AdditionalParams = new Dictionary<string, object>
            {
                ["scope"] = parameters.Scope,
                ["targetPath"] = parameters.TargetPath ?? "",
                ["analysisLevel"] = parameters.AnalysisLevel,
                ["includeExternal"] = parameters.IncludeExternal
            }
        };
    }

    private DependencyAnalysisResult CreateErrorResult(
        string errorCode,
        string message,
        List<string> recoverySteps,
        DependencyAnalysisParams parameters,
        DateTime startTime)
    {
        return new DependencyAnalysisResult
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

    private class DependencyGraph
    {
        public Dictionary<string, DependencyNode> Nodes { get; } = new();
        public List<DependencyEdge> Edges { get; } = new();
    }
}

public class DependencyAnalysisParams
{
    [JsonPropertyName("scope")]
    [Description("Analysis scope: 'solution', 'project', 'namespace', or 'type' (default: 'solution')")]
    public string Scope { get; set; } = "solution";

    [JsonPropertyName("targetPath")]
    [Description("Target path/name for scoped analysis (project name, namespace, or file path)")]
    public string? TargetPath { get; set; }
    
    [JsonPropertyName("analysisLevel")]
    [Description("Level of analysis: 'type', 'namespace', or 'project' (default: 'namespace')")]
    public string AnalysisLevel { get; set; } = "namespace";
    
    [JsonPropertyName("includeExternal")]
    [Description("Include external dependencies (default: false)")]
    public bool IncludeExternal { get; set; } = false;
    
    [JsonPropertyName("maxNodes")]
    [Description("Maximum number of nodes to return (default: 100)")]
    public int? MaxNodes { get; set; }
}

public class DependencyAnalysisResult : ToolResultBase
{
    public override string Operation => "csharp_dependency_analysis";
    
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }
    
    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }
    
    [JsonPropertyName("graph")]
    public object? Graph { get; set; } // Simplified for JSON serialization
    
    [JsonPropertyName("analysis")]
    public DependencyAnalysisInfo? Analysis { get; set; }
    
    [JsonPropertyName("metrics")]
    public DependencyMetrics? Metrics { get; set; }
    
    [JsonPropertyName("circularDependencies")]
    public List<CircularDependency>? CircularDependencies { get; set; }
}

public class DependencyNode
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }
    
    [JsonPropertyName("name")]
    public required string Name { get; set; }
    
    [JsonPropertyName("fullName")]
    public required string FullName { get; set; }
    
    [JsonPropertyName("type")]
    public required string Type { get; set; }
    
    [JsonPropertyName("project")]
    public required string Project { get; set; }
    
    [JsonPropertyName("incomingCount")]
    public int IncomingCount => IncomingEdges.Count;
    
    [JsonPropertyName("outgoingCount")]
    public int OutgoingCount => OutgoingEdges.Count;
    
    [JsonIgnore]
    public List<DependencyEdge> IncomingEdges { get; set; } = new();
    
    [JsonIgnore]
    public List<DependencyEdge> OutgoingEdges { get; set; } = new();
}

public class DependencyEdge
{
    [JsonPropertyName("sourceId")]
    public required string SourceId { get; set; }
    
    [JsonPropertyName("targetId")]
    public required string TargetId { get; set; }
    
    [JsonPropertyName("type")]
    public required string Type { get; set; }
}

public class DependencyAnalysisInfo
{
    [JsonPropertyName("analysisLevel")]
    public required string AnalysisLevel { get; set; }
    
    [JsonPropertyName("nodeCount")]
    public int NodeCount { get; set; }
    
    [JsonPropertyName("edgeCount")]
    public int EdgeCount { get; set; }
    
    [JsonPropertyName("nodesByType")]
    public Dictionary<string, int>? NodesByType { get; set; }
    
    [JsonPropertyName("edgesByType")]
    public Dictionary<string, int>? EdgesByType { get; set; }
    
    [JsonPropertyName("connectedComponents")]
    public int ConnectedComponents { get; set; }
    
    [JsonPropertyName("maxDependencyDepth")]
    public int MaxDependencyDepth { get; set; }
}

public class DependencyMetrics
{
    [JsonPropertyName("totalNodes")]
    public int TotalNodes { get; set; }
    
    [JsonPropertyName("totalEdges")]
    public int TotalEdges { get; set; }
    
    [JsonPropertyName("averageOutgoingDependencies")]
    public double AverageOutgoingDependencies { get; set; }
    
    [JsonPropertyName("nodeMetrics")]
    public List<NodeMetric> NodeMetrics { get; set; } = new();
    
    [JsonPropertyName("highlyCoupledNodes")]
    public List<string> HighlyCoupledNodes { get; set; } = new();
    
    [JsonPropertyName("hubNodes")]
    public List<string> HubNodes { get; set; } = new();
    
    [JsonPropertyName("godNodes")]
    public List<string> GodNodes { get; set; } = new();
}

public class NodeMetric
{
    [JsonPropertyName("nodeId")]
    public required string NodeId { get; set; }
    
    [JsonPropertyName("afferentCoupling")]
    public int AfferentCoupling { get; set; }
    
    [JsonPropertyName("efferentCoupling")]
    public int EfferentCoupling { get; set; }
    
    [JsonPropertyName("instability")]
    public double Instability { get; set; }
}

public class CircularDependency
{
    [JsonPropertyName("nodes")]
    public List<string> Nodes { get; set; } = new();
    
    [JsonPropertyName("severity")]
    public required string Severity { get; set; }
}

