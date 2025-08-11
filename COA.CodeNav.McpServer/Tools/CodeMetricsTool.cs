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
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using DataAnnotations = System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that calculates code metrics for methods, classes, and files
/// </summary>
public class CodeMetricsTool : McpToolBase<CodeMetricsParams, CodeMetricsResult>
{
    private readonly ILogger<CodeMetricsTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly CodeMetricsResponseBuilder _responseBuilder;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => "csharp_code_metrics";
    public override string Description => @"Calculate code metrics for methods, classes, and files.
Returns: Cyclomatic complexity, lines of code, maintainability index, and depth of inheritance.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if file is not found.
Use cases: Code quality assessment, identifying complex methods, refactoring candidates, technical debt analysis.
AI benefit: Provides quantitative metrics for prioritizing code improvements.";

    public CodeMetricsTool(
        ILogger<CodeMetricsTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        CodeMetricsResponseBuilder responseBuilder,
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

    protected override async Task<CodeMetricsResult> ExecuteInternalAsync(
        CodeMetricsParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("CodeMetrics request: FilePath={FilePath}, Scope={Scope}", 
            parameters.FilePath, parameters.Scope);

        var startTime = DateTime.UtcNow;

        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            return new CodeMetricsResult
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
                            "Verify the file path is correct and absolute",
                            "Ensure the solution or project containing this file is loaded",
                            "Use csharp_load_solution or csharp_load_project if needed"
                        },
                        SuggestedActions = new List<SuggestedAction>
                        {
                            new SuggestedAction
                            {
                                Tool = "csharp_load_solution",
                                Description = "Load the solution containing this file",
                                Parameters = new { solutionPath = "<path-to-your-solution.sln>" }
                            }
                        }
                    }
                }
            };
        }

        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (tree == null)
        {
            return new CodeMetricsResult
            {
                Success = false,
                Message = "Failed to get syntax tree",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Message = "Failed to get syntax tree",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Check if the file contains valid C# code",
                            "Try reloading the solution"
                        }
                    }
                }
            };
        }

        var root = await tree.GetRootAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        var metrics = new List<CodeMetricInfo>();

        // Calculate metrics based on scope
        switch (parameters.Scope?.ToLower() ?? "file")
        {
            case "method":
                if (parameters.Line.HasValue && parameters.Column.HasValue)
                {
                    var position = tree.GetText().Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
                        parameters.Line.Value - 1, parameters.Column.Value - 1));
                    var method = root.FindToken(position).Parent?.AncestorsAndSelf()
                        .OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    
                    if (method != null)
                    {
                        var metric = CalculateMethodMetrics(method, semanticModel);
                        metrics.Add(metric);
                    }
                }
                else
                {
                    // Calculate for all methods in file
                    var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
                    foreach (var method in methods)
                    {
                        var metric = CalculateMethodMetrics(method, semanticModel);
                        metrics.Add(metric);
                    }
                }
                break;

            case "class":
                var classes = root.DescendantNodes().OfType<TypeDeclarationSyntax>();
                foreach (var classDecl in classes)
                {
                    var metric = CalculateTypeMetrics(classDecl, semanticModel);
                    metrics.Add(metric);
                }
                break;

            default: // "file"
                var fileMetric = CalculateFileMetrics(root, parameters.FilePath, semanticModel);
                metrics.Add(fileMetric);
                break;
        }

        // Apply token optimization to prevent context overflow
        var estimatedTokens = _tokenEstimator.EstimateObject(metrics);
        var originalCount = metrics.Count;
        var wasOptimized = false;
        
        if (estimatedTokens > 8000) // Code metrics can include detailed information
        {
            metrics = _tokenEstimator.ApplyProgressiveReduction(
                metrics,
                metric => _tokenEstimator.EstimateObject(metric),
                8000,
                new[] { 50, 30, 20, 10 }
            );
            wasOptimized = metrics.Count < originalCount;
            
            _logger.LogDebug("Applied token optimization: reduced from {Original} to {Reduced} metrics (estimated {EstimatedTokens} tokens)",
                originalCount, metrics.Count, estimatedTokens);
        }

        // Generate insights and next actions
        var insights = GenerateInsights(metrics);
        var nextActions = GenerateNextActions(metrics, parameters);
        var analysis = GenerateAnalysis(metrics);

        var completeResult = new CodeMetricsResult
        {
            Success = true,
            Message = $"Code metrics calculated for {originalCount} item(s){(wasOptimized ? " (showing " + metrics.Count + " due to token optimization)" : "")}",
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = parameters.Line.HasValue && parameters.Column.HasValue 
                    ? new PositionInfo { Line = parameters.Line.Value, Column = parameters.Column.Value }
                    : null
            },
            Summary = new SummaryInfo
            {
                TotalFound = originalCount,
                Returned = metrics.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            },
            Metrics = metrics,
            Analysis = analysis,
            Insights = insights,
            Actions = nextActions,
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
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

    private CodeMetricInfo CalculateMethodMetrics(MethodDeclarationSyntax method, SemanticModel? semanticModel)
    {
        var methodSymbol = semanticModel?.GetDeclaredSymbol(method);
        var location = method.GetLocation();
        var lineSpan = location.GetLineSpan();

        var complexity = CalculateCyclomaticComplexity(method);
        var linesOfCode = CalculateLinesOfCode(method);
        var maintainabilityIndex = CalculateMaintainabilityIndex(complexity, linesOfCode);

        return new CodeMetricInfo
        {
            Name = method.Identifier.ValueText,
            FullName = methodSymbol?.ToDisplayString() ?? method.Identifier.ValueText,
            Kind = "Method",
            Location = new LocationInfo
            {
                FilePath = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                EndColumn = lineSpan.EndLinePosition.Character + 1
            },
            CyclomaticComplexity = complexity,
            LinesOfCode = linesOfCode,
            MaintainabilityIndex = maintainabilityIndex,
            DepthOfInheritance = GetDepthOfInheritance(methodSymbol?.ContainingType),
            CouplingBetweenObjects = CalculateCoupling(method, semanticModel),
            LackOfCohesion = 0 // Not applicable for methods
        };
    }

    private CodeMetricInfo CalculateTypeMetrics(TypeDeclarationSyntax type, SemanticModel? semanticModel)
    {
        var typeSymbol = semanticModel?.GetDeclaredSymbol(type) as INamedTypeSymbol;
        var location = type.GetLocation();
        var lineSpan = location.GetLineSpan();

        var methods = type.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var avgComplexity = methods.Any() ? (int)methods.Average(m => CalculateCyclomaticComplexity(m)) : 1;
        var totalLines = CalculateLinesOfCode(type);
        var maintainabilityIndex = CalculateMaintainabilityIndex(avgComplexity, totalLines);

        return new CodeMetricInfo
        {
            Name = type.Identifier.ValueText,
            FullName = typeSymbol?.ToDisplayString() ?? type.Identifier.ValueText,
            Kind = type.Keyword.ValueText, // class, interface, struct, etc.
            Location = new LocationInfo
            {
                FilePath = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                EndColumn = lineSpan.EndLinePosition.Character + 1
            },
            CyclomaticComplexity = avgComplexity,
            LinesOfCode = totalLines,
            MaintainabilityIndex = maintainabilityIndex,
            DepthOfInheritance = GetDepthOfInheritance(typeSymbol),
            CouplingBetweenObjects = CalculateTypeCoupling(type, semanticModel),
            LackOfCohesion = CalculateLackOfCohesion(type)
        };
    }

    private CodeMetricInfo CalculateFileMetrics(SyntaxNode root, string filePath, SemanticModel? semanticModel)
    {
        var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        
        var avgComplexity = methods.Any() ? (int)methods.Average(m => CalculateCyclomaticComplexity(m)) : 1;
        var totalLines = CalculateLinesOfCode(root);
        var maintainabilityIndex = CalculateMaintainabilityIndex(avgComplexity, totalLines);

        return new CodeMetricInfo
        {
            Name = Path.GetFileName(filePath),
            FullName = filePath,
            Kind = "File",
            Location = new LocationInfo
            {
                FilePath = filePath,
                Line = 1,
                Column = 1,
                EndLine = totalLines,
                EndColumn = 1
            },
            CyclomaticComplexity = avgComplexity,
            LinesOfCode = totalLines,
            MaintainabilityIndex = maintainabilityIndex,
            DepthOfInheritance = types.Any() ? types.Max(t => GetDepthOfInheritance(semanticModel?.GetDeclaredSymbol(t) as INamedTypeSymbol)) : 0,
            CouplingBetweenObjects = CalculateFileCoupling(root, semanticModel),
            LackOfCohesion = 0 // Not meaningful at file level
        };
    }

    private int CalculateCyclomaticComplexity(SyntaxNode node)
    {
        int complexity = 1; // Base complexity

        // Count decision points
        var decisionKinds = new[]
        {
            SyntaxKind.IfStatement,
            SyntaxKind.ElseClause,
            SyntaxKind.WhileStatement,
            SyntaxKind.ForStatement,
            SyntaxKind.ForEachStatement,
            SyntaxKind.DoStatement,
            SyntaxKind.SwitchSection,
            SyntaxKind.CaseSwitchLabel,
            SyntaxKind.CatchClause,
            SyntaxKind.ConditionalExpression,
            SyntaxKind.LogicalAndExpression,
            SyntaxKind.LogicalOrExpression
        };
        
        var decisionNodes = node.DescendantNodes().Where(n => decisionKinds.Contains(n.Kind()));

        complexity += decisionNodes.Count();

        return complexity;
    }

    private int CalculateLinesOfCode(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
    }

    private int CalculateMaintainabilityIndex(int cyclomaticComplexity, int linesOfCode)
    {
        // Simplified maintainability index calculation
        // Real formula uses Halstead Volume which is more complex
        var rawIndex = 171 - 5.2 * Math.Log(cyclomaticComplexity) - 0.23 * cyclomaticComplexity - 16.2 * Math.Log(linesOfCode);
        var normalizedIndex = Math.Max(0, Math.Min(100, rawIndex));
        return (int)normalizedIndex;
    }

    private int GetDepthOfInheritance(INamedTypeSymbol? typeSymbol)
    {
        if (typeSymbol == null) return 0;

        int depth = 0;
        var current = typeSymbol.BaseType;
        
        while (current != null && current.SpecialType != SpecialType.System_Object)
        {
            depth++;
            current = current.BaseType;
        }

        return depth;
    }

    private int CalculateCoupling(SyntaxNode node, SemanticModel? semanticModel)
    {
        if (semanticModel == null) return 0;

        var referencedTypes = new HashSet<string>();
        
        foreach (var identifierName in node.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(identifierName);
            if (symbolInfo.Symbol?.ContainingType != null)
            {
                referencedTypes.Add(symbolInfo.Symbol.ContainingType.ToDisplayString());
            }
        }

        return referencedTypes.Count;
    }

    private int CalculateTypeCoupling(TypeDeclarationSyntax type, SemanticModel? semanticModel)
    {
        return CalculateCoupling(type, semanticModel);
    }

    private int CalculateFileCoupling(SyntaxNode root, SemanticModel? semanticModel)
    {
        return CalculateCoupling(root, semanticModel);
    }

    private int CalculateLackOfCohesion(TypeDeclarationSyntax type)
    {
        // Simplified LCOM calculation
        var methods = type.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var fields = type.DescendantNodes().OfType<FieldDeclarationSyntax>().ToList();

        if (methods.Count <= 1) return 0;

        // This is a very simplified version - real LCOM is more complex
        return Math.Max(0, methods.Count - fields.Count);
    }

    private CodeMetricsAnalysis GenerateAnalysis(List<CodeMetricInfo> metrics)
    {
        if (!metrics.Any())
        {
            return new CodeMetricsAnalysis
            {
                TotalItems = 0,
                AverageComplexity = 0,
                HighComplexityItems = 0,
                LowMaintainabilityItems = 0,
                MaxDepthOfInheritance = 0
            };
        }

        return new CodeMetricsAnalysis
        {
            TotalItems = metrics.Count,
            AverageComplexity = metrics.Average(m => m.CyclomaticComplexity),
            HighComplexityItems = metrics.Count(m => m.CyclomaticComplexity > 10),
            LowMaintainabilityItems = metrics.Count(m => m.MaintainabilityIndex < 20),
            MaxDepthOfInheritance = metrics.Max(m => m.DepthOfInheritance),
            AverageLinesOfCode = metrics.Average(m => m.LinesOfCode),
            AverageMaintainabilityIndex = metrics.Average(m => m.MaintainabilityIndex)
        };
    }

    private List<string> GenerateInsights(List<CodeMetricInfo> metrics)
    {
        var insights = new List<string>();
        var analysis = GenerateAnalysis(metrics);

        if (analysis.HighComplexityItems > 0)
        {
            insights.Add($"{analysis.HighComplexityItems} item(s) have high cyclomatic complexity (>10) - consider refactoring");
        }

        if (analysis.LowMaintainabilityItems > 0)
        {
            insights.Add($"{analysis.LowMaintainabilityItems} item(s) have low maintainability index (<20) - technical debt");
        }

        if (analysis.MaxDepthOfInheritance > 5)
        {
            insights.Add($"Deep inheritance detected (depth: {analysis.MaxDepthOfInheritance}) - consider composition");
        }

        if (analysis.AverageComplexity > 7)
        {
            insights.Add($"High average complexity ({analysis.AverageComplexity:F1}) - code may be difficult to maintain");
        }

        if (analysis.AverageMaintainabilityIndex > 80)
        {
            insights.Add($"Good maintainability score ({analysis.AverageMaintainabilityIndex:F1}) - well-structured code");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(List<CodeMetricInfo> metrics, CodeMetricsParams parameters)
    {
        var actions = new List<AIAction>();
        var analysis = GenerateAnalysis(metrics);

        // Find most complex method for refactoring suggestion
        var mostComplex = metrics.OrderByDescending(m => m.CyclomaticComplexity).FirstOrDefault();
        if (mostComplex != null && mostComplex.CyclomaticComplexity > 10)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_extract_method",
                Description = $"Refactor complex {mostComplex.Kind.ToLower()} '{mostComplex.Name}' (complexity: {mostComplex.CyclomaticComplexity})",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = mostComplex.Location.FilePath,
                    ["startLine"] = mostComplex.Location.Line,
                    ["endLine"] = mostComplex.Location.EndLine
                },
                Priority = 90,
                Category = "refactoring"
            });
        }

        // Suggest finding unused code if maintainability is low
        if (analysis.LowMaintainabilityItems > 0)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_find_unused_code",
                Description = "Find unused code to improve maintainability",
                Parameters = new Dictionary<string, object>
                {
                    ["scope"] = "file",
                    ["filePath"] = parameters.FilePath
                },
                Priority = 70,
                Category = "cleanup"
            });
        }

        return actions;
    }

}

/// <summary>
/// Parameters for CodeMetrics tool
/// </summary>
public class CodeMetricsParams
{
    [DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file (e.g., 'C:\\Project\\src\\Program.cs' on Windows, '/home/user/project/src/Program.cs' on Unix)")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    [COA.Mcp.Framework.Attributes.Description("Scope of analysis: 'file', 'project', or 'file'")]
    public string? Scope { get; set; } = "file";

    [JsonPropertyName("line")]
    [COA.Mcp.Framework.Attributes.Description("Line number (1-based) inside the type declaration where code should be generated")]
    public int? Line { get; set; }

    [JsonPropertyName("column")]
    [COA.Mcp.Framework.Attributes.Description("Column number (1-based) inside the type declaration")]
    public int? Column { get; set; }

    [JsonPropertyName("includeInherited")]
    [COA.Mcp.Framework.Attributes.Description("Include inherited members when generating code. true = include base class members, false = current class only (default)")]
    public bool IncludeInherited { get; set; } = false;

    [JsonPropertyName("thresholds")]
    [COA.Mcp.Framework.Attributes.Description("Custom thresholds for highlighting issues")]
    public Dictionary<string, int>? Thresholds { get; set; }
}

public class CodeMetricsResult : ToolResultBase
{
    public override string Operation => "csharp_code_metrics";
    
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }
    
    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("metrics")]
    public List<CodeMetricInfo>? Metrics { get; set; }

    [JsonPropertyName("analysis")]
    public CodeMetricsAnalysis? Analysis { get; set; }
}

public class CodeMetricInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("fullName")]
    public required string FullName { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("location")]
    public required LocationInfo Location { get; set; }

    [JsonPropertyName("cyclomaticComplexity")]
    public int CyclomaticComplexity { get; set; }

    [JsonPropertyName("linesOfCode")]
    public int LinesOfCode { get; set; }

    [JsonPropertyName("maintainabilityIndex")]
    public int MaintainabilityIndex { get; set; }

    [JsonPropertyName("depthOfInheritance")]
    public int DepthOfInheritance { get; set; }

    [JsonPropertyName("couplingBetweenObjects")]
    public int CouplingBetweenObjects { get; set; }

    [JsonPropertyName("lackOfCohesion")]
    public int LackOfCohesion { get; set; }
}

public class CodeMetricsAnalysis
{
    [JsonPropertyName("totalItems")]
    public int TotalItems { get; set; }

    [JsonPropertyName("averageComplexity")]
    public double AverageComplexity { get; set; }

    [JsonPropertyName("highComplexityItems")]
    public int HighComplexityItems { get; set; }

    [JsonPropertyName("lowMaintainabilityItems")]
    public int LowMaintainabilityItems { get; set; }

    [JsonPropertyName("maxDepthOfInheritance")]
    public int MaxDepthOfInheritance { get; set; }

    [JsonPropertyName("averageLinesOfCode")]
    public double AverageLinesOfCode { get; set; }

    [JsonPropertyName("averageMaintainabilityIndex")]
    public double AverageMaintainabilityIndex { get; set; }
}