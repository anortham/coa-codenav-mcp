using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that calculates code metrics for methods, classes, and files
/// </summary>
[McpServerToolType]
public class CodeMetricsTool : ITool
{
    private readonly ILogger<CodeMetricsTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "csharp_code_metrics";
    public string Description => "Calculate code metrics including cyclomatic complexity, lines of code, and maintainability index";

    public CodeMetricsTool(
        ILogger<CodeMetricsTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "csharp_code_metrics")]
    [Description(@"Calculate code metrics for methods, classes, and files.
Returns: Cyclomatic complexity, lines of code, maintainability index, and depth of inheritance.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if file is not found.
Use cases: Code quality assessment, identifying complex methods, refactoring candidates, technical debt analysis.
AI benefit: Provides quantitative metrics for prioritizing code improvements.")]
    public async Task<object> ExecuteAsync(CodeMetricsParams parameters, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("CodeMetrics request: FilePath={FilePath}, Scope={Scope}", 
            parameters.FilePath, parameters.Scope);

        try
        {
            var document = await _documentService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                return new CodeMetricsResult
                {
                    Success = false,
                    Message = $"Document not found: {parameters.FilePath}",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.DOCUMENT_NOT_FOUND,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Verify the file path is correct and absolute",
                                "Ensure the solution or project containing this file is loaded",
                                "Use csharp_load_solution or csharp_load_project if needed"
                            }
                        }
                    },
                    Query = new QueryInfo { FilePath = parameters.FilePath },
                    Meta = new ToolMetadata 
                    { 
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
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
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Check if the file contains valid C# code",
                                "Try reloading the solution"
                            }
                        }
                    },
                    Query = new QueryInfo { FilePath = parameters.FilePath },
                    Meta = new ToolMetadata 
                    { 
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                    }
                };
            }

            var root = await tree.GetRootAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

            var metrics = new List<CodeMetricInfo>();
            var insights = new List<string>();
            var actions = new List<NextAction>();

            // Calculate metrics based on scope
            switch (parameters.Scope?.ToLower())
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

                default: // "file" or unspecified
                    var fileMetric = CalculateFileMetrics(root, semanticModel, document.FilePath ?? "Unknown");
                    metrics.Add(fileMetric);
                    
                    // Also include method-level metrics for insights
                    var fileMethods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
                    foreach (var method in fileMethods)
                    {
                        var metric = CalculateMethodMetrics(method, semanticModel);
                        metrics.Add(metric);
                    }
                    break;
            }

            // Pre-estimate tokens
            var estimatedTokens = EstimateResponseTokens(metrics);
            var shouldTruncate = estimatedTokens > 10000;
            
            if (shouldTruncate && metrics.Count > 50)
            {
                // Sort by complexity and take top 50
                metrics = metrics.OrderByDescending(m => m.CyclomaticComplexity).Take(50).ToList();
                insights.Insert(0, $"⚠️ Response size limit applied. Showing top 50 most complex items of {metrics.Count} total.");
            }

            // Generate insights
            GenerateInsights(metrics, insights);

            // Generate next actions
            GenerateNextActions(metrics, actions, parameters);

            var result = new CodeMetricsResult
            {
                Success = true,
                Message = $"Calculated metrics for {metrics.Count} items in {parameters.FilePath}",
                Query = new QueryInfo 
                { 
                    FilePath = parameters.FilePath,
                    Position = (parameters.Line.HasValue && parameters.Column.HasValue) 
                        ? new PositionInfo { Line = parameters.Line.Value, Column = parameters.Column.Value }
                        : null,
                    AdditionalParams = new Dictionary<string, object> { ["scope"] = parameters.Scope ?? "file" }
                },
                Metrics = metrics,
                Summary = new CodeMetricsSummary
                {
                    TotalItems = metrics.Count,
                    AverageComplexity = metrics.Any() ? metrics.Average(m => m.CyclomaticComplexity) : 0,
                    MaxComplexity = metrics.Any() ? metrics.Max(m => m.CyclomaticComplexity) : 0,
                    TotalLinesOfCode = metrics.Sum(m => m.LinesOfCode),
                    AverageMaintainabilityIndex = metrics.Any(m => m.MaintainabilityIndex.HasValue) 
                        ? metrics.Where(m => m.MaintainabilityIndex.HasValue).Average(m => m.MaintainabilityIndex!.Value) 
                        : null
                },
                Distribution = new MetricsDistribution
                {
                    ComplexityRanges = new Dictionary<string, int>
                    {
                        ["Low (1-5)"] = metrics.Count(m => m.CyclomaticComplexity <= 5),
                        ["Medium (6-10)"] = metrics.Count(m => m.CyclomaticComplexity > 5 && m.CyclomaticComplexity <= 10),
                        ["High (11-20)"] = metrics.Count(m => m.CyclomaticComplexity > 10 && m.CyclomaticComplexity <= 20),
                        ["Very High (>20)"] = metrics.Count(m => m.CyclomaticComplexity > 20)
                    },
                    MaintainabilityRanges = new Dictionary<string, int>
                    {
                        ["Good (>70)"] = metrics.Count(m => m.MaintainabilityIndex > 70),
                        ["Moderate (50-70)"] = metrics.Count(m => m.MaintainabilityIndex >= 50 && m.MaintainabilityIndex <= 70),
                        ["Poor (<50)"] = metrics.Count(m => m.MaintainabilityIndex < 50)
                    }
                },
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
                    "code_metrics",
                    result,
                    document.FilePath ?? "unknown");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating code metrics");
            return new CodeMetricsResult
            {
                Success = false,
                Message = $"Error calculating code metrics: {ex.Message}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.INTERNAL_ERROR,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new List<string>
                        {
                            "Check the server logs for detailed error information",
                            "Verify the file contains valid C# code",
                            "Try reloading the solution"
                        }
                    }
                },
                Query = new QueryInfo { FilePath = parameters.FilePath },
                Meta = new ToolMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                }
            };
        }
    }

    private CodeMetricInfo CalculateMethodMetrics(MethodDeclarationSyntax method, SemanticModel? semanticModel)
    {
        var complexity = CalculateCyclomaticComplexity(method);
        var loc = CalculateLinesOfCode(method);
        var mi = CalculateMaintainabilityIndex(complexity, loc, GetHalsteadVolume(method));
        
        return new CodeMetricInfo
        {
            Name = method.Identifier.Text,
            Type = "Method",
            CyclomaticComplexity = complexity,
            LinesOfCode = loc,
            MaintainabilityIndex = mi,
            Location = new MetricLocationInfo
            {
                StartLine = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                EndLine = method.GetLocation().GetLineSpan().EndLinePosition.Line + 1
            }
        };
    }

    private CodeMetricInfo CalculateTypeMetrics(TypeDeclarationSyntax type, SemanticModel? semanticModel)
    {
        var methods = type.DescendantNodes().OfType<MethodDeclarationSyntax>();
        var totalComplexity = methods.Sum(m => CalculateCyclomaticComplexity(m));
        var loc = CalculateLinesOfCode(type);
        var methodCount = methods.Count();
        var avgComplexity = methodCount > 0 ? totalComplexity / (double)methodCount : 1;
        
        // Calculate depth of inheritance
        var depthOfInheritance = 0;
        if (semanticModel != null)
        {
            var symbol = semanticModel.GetDeclaredSymbol(type) as INamedTypeSymbol;
            depthOfInheritance = CalculateDepthOfInheritance(symbol);
        }
        
        return new CodeMetricInfo
        {
            Name = type.Identifier.Text,
            Type = type is ClassDeclarationSyntax ? "Class" : 
                  type is InterfaceDeclarationSyntax ? "Interface" : 
                  type is StructDeclarationSyntax ? "Struct" : "Type",
            CyclomaticComplexity = (int)Math.Ceiling(avgComplexity),
            LinesOfCode = loc,
            DepthOfInheritance = depthOfInheritance,
            MethodCount = methodCount,
            Location = new MetricLocationInfo
            {
                StartLine = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                EndLine = type.GetLocation().GetLineSpan().EndLinePosition.Line + 1
            }
        };
    }

    private CodeMetricInfo CalculateFileMetrics(SyntaxNode root, SemanticModel? semanticModel, string filePath)
    {
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
        var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>();
        
        var totalComplexity = methods.Sum(m => CalculateCyclomaticComplexity(m));
        var loc = CalculateLinesOfCode(root);
        var methodCount = methods.Count();
        var avgComplexity = methodCount > 0 ? totalComplexity / (double)methodCount : 1;
        
        return new CodeMetricInfo
        {
            Name = Path.GetFileName(filePath),
            Type = "File",
            CyclomaticComplexity = (int)Math.Ceiling(avgComplexity),
            LinesOfCode = loc,
            MethodCount = methodCount,
            TypeCount = types.Count()
        };
    }

    private int CalculateCyclomaticComplexity(SyntaxNode node)
    {
        var complexity = 1; // Base complexity

        // Add complexity for control flow statements
        complexity += node.DescendantNodes().Count(n => n is IfStatementSyntax);
        complexity += node.DescendantNodes().Count(n => n is WhileStatementSyntax);
        complexity += node.DescendantNodes().Count(n => n is ForStatementSyntax);
        complexity += node.DescendantNodes().Count(n => n is ForEachStatementSyntax);
        complexity += node.DescendantNodes().Count(n => n is DoStatementSyntax);
        complexity += node.DescendantNodes().Count(n => n is SwitchStatementSyntax);
        complexity += node.DescendantNodes().OfType<SwitchExpressionArmSyntax>().Count();
        complexity += node.DescendantNodes().Count(n => n is CatchClauseSyntax);
        complexity += node.DescendantNodes().Count(n => n is ConditionalExpressionSyntax);
        complexity += node.DescendantNodes().Count(n => n is BinaryExpressionSyntax binaryExpr &&
            (binaryExpr.IsKind(SyntaxKind.LogicalAndExpression) || binaryExpr.IsKind(SyntaxKind.LogicalOrExpression)));

        // Add complexity for case labels in switch statements
        complexity += node.DescendantNodes().OfType<CaseSwitchLabelSyntax>().Count();

        return complexity;
    }

    private int CalculateLinesOfCode(SyntaxNode node)
    {
        var text = node.GetText();
        var lines = text.Lines.Count;
        
        // Subtract blank lines and comment-only lines
        var codeLines = 0;
        foreach (var line in text.Lines)
        {
            var lineText = line.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(lineText) && 
                !lineText.StartsWith("//") && 
                !lineText.StartsWith("/*") && 
                !lineText.StartsWith("*"))
            {
                codeLines++;
            }
        }
        
        return codeLines;
    }

    private double? CalculateMaintainabilityIndex(int cyclomaticComplexity, int linesOfCode, double halsteadVolume)
    {
        if (linesOfCode == 0) return 100.0;
        
        // Microsoft's formula for Maintainability Index
        // MI = MAX(0, (171 - 5.2 * ln(Halstead Volume) - 0.23 * (Cyclomatic Complexity) - 16.2 * ln(Lines of Code)) * 100 / 171)
        var mi = Math.Max(0, (171 - 5.2 * Math.Log(halsteadVolume) - 0.23 * cyclomaticComplexity - 16.2 * Math.Log(linesOfCode)) * 100 / 171);
        
        return Math.Round(mi, 2);
    }

    private double GetHalsteadVolume(SyntaxNode node)
    {
        // Simplified Halstead Volume calculation
        // In a real implementation, this would count unique operators and operands
        var operators = node.DescendantTokens().Count(t => t.IsKind(SyntaxKind.PlusToken) || 
            t.IsKind(SyntaxKind.MinusToken) || t.IsKind(SyntaxKind.AsteriskToken) || 
            t.IsKind(SyntaxKind.SlashToken) || t.IsKind(SyntaxKind.EqualsToken));
        
        var operands = node.DescendantTokens().Count(t => t.IsKind(SyntaxKind.IdentifierToken) || 
            t.IsKind(SyntaxKind.NumericLiteralToken) || t.IsKind(SyntaxKind.StringLiteralToken));
        
        var vocabulary = Math.Max(operators + operands, 1);
        var length = Math.Max(operators + operands, 1);
        
        return length * Math.Log2(vocabulary);
    }

    private int CalculateDepthOfInheritance(INamedTypeSymbol? typeSymbol)
    {
        if (typeSymbol == null) return 0;
        
        var depth = 0;
        var current = typeSymbol.BaseType;
        
        while (current != null && current.SpecialType != SpecialType.System_Object)
        {
            depth++;
            current = current.BaseType;
        }
        
        return depth;
    }

    private int EstimateResponseTokens(List<CodeMetricInfo> metrics)
    {
        var baseTokens = 800; // Base response structure
        var perMetricTokens = 150; // Per metric item
        
        return baseTokens + (metrics.Count * perMetricTokens);
    }

    private void GenerateInsights(List<CodeMetricInfo> metrics, List<string> insights)
    {
        if (!metrics.Any()) return;

        // Complexity insights
        var highComplexity = metrics.Where(m => m.CyclomaticComplexity > 10).ToList();
        if (highComplexity.Any())
        {
            insights.Add($"🔴 Found {highComplexity.Count} items with high complexity (>10): {string.Join(", ", highComplexity.Take(3).Select(m => m.Name))}");
        }

        // Maintainability insights
        var poorMaintainability = metrics.Where(m => m.MaintainabilityIndex < 50).ToList();
        if (poorMaintainability.Any())
        {
            insights.Add($"⚠️ {poorMaintainability.Count} items have poor maintainability (<50): Consider refactoring");
        }

        // Size insights
        var largeMethods = metrics.Where(m => m.Type == "Method" && m.LinesOfCode > 50).ToList();
        if (largeMethods.Any())
        {
            insights.Add($"📏 {largeMethods.Count} methods exceed 50 lines: {string.Join(", ", largeMethods.Take(3).Select(m => m.Name))}");
        }

        // Positive insights
        var wellMaintained = metrics.Where(m => m.MaintainabilityIndex > 70).ToList();
        if (wellMaintained.Any())
        {
            insights.Add($"✅ {wellMaintained.Count} items have good maintainability (>70)");
        }

        // Summary insight
        var avgComplexity = metrics.Average(m => m.CyclomaticComplexity);
        insights.Add($"📊 Average complexity: {avgComplexity:F1} - {(avgComplexity <= 5 ? "Good" : avgComplexity <= 10 ? "Moderate" : "High")}");
    }

    private void GenerateNextActions(List<CodeMetricInfo> metrics, List<NextAction> actions, CodeMetricsParams parameters)
    {
        // Suggest refactoring for high complexity items
        var mostComplex = metrics.OrderByDescending(m => m.CyclomaticComplexity).FirstOrDefault();
        if (mostComplex != null && mostComplex.CyclomaticComplexity > 10)
        {
            actions.Add(new NextAction
            {
                Id = "refactor_complex",
                Description = $"Extract methods from '{mostComplex.Name}' (complexity: {mostComplex.CyclomaticComplexity})",
                ToolName = "csharp_extract_method",
                Parameters = new
                {
                    filePath = parameters.FilePath,
                    startLine = mostComplex.Location?.StartLine,
                    endLine = mostComplex.Location?.EndLine
                },
                Priority = "high"
            });
        }

        // Suggest documentation for complex methods
        if (metrics.Any(m => m.Type == "Method" && m.CyclomaticComplexity > 7))
        {
            actions.Add(new NextAction
            {
                Id = "add_documentation",
                Description = "Add documentation to complex methods",
                ToolName = "documentation_guardian",
                Parameters = new
                {
                    filePath = parameters.FilePath,
                    focusOn = "complex_methods"
                },
                Priority = "medium"
            });
        }

        // Suggest finding similar patterns
        if (parameters.Scope == "method" && metrics.Count == 1)
        {
            actions.Add(new NextAction
            {
                Id = "find_similar_complexity",
                Description = "Find other methods with similar complexity",
                ToolName = "csharp_symbol_search",
                Parameters = new
                {
                    query = "*",
                    symbolKinds = new[] { "Method" },
                    projectFilter = Path.GetFileNameWithoutExtension(parameters.FilePath)
                },
                Priority = "low"
            });
        }
    }
}

public class CodeMetricsParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file")]
    public required string FilePath { get; set; }

    [JsonPropertyName("scope")]
    [Description("Scope of analysis: 'file' (default), 'class', or 'method'")]
    public string? Scope { get; set; }

    [JsonPropertyName("line")]
    [Description("Line number for method-specific analysis (1-based)")]
    public int? Line { get; set; }

    [JsonPropertyName("column")]
    [Description("Column number for method-specific analysis (1-based)")]
    public int? Column { get; set; }

    [JsonPropertyName("includeInherited")]
    [Description("Include metrics for inherited members (default: false)")]
    public bool IncludeInherited { get; set; }

    [JsonPropertyName("thresholds")]
    [Description("Custom thresholds for highlighting issues")]
    public MetricsThresholds? Thresholds { get; set; }
}

public class MetricsThresholds
{
    [JsonPropertyName("complexityWarning")]
    public int ComplexityWarning { get; set; } = 10;

    [JsonPropertyName("complexityError")]
    public int ComplexityError { get; set; } = 20;

    [JsonPropertyName("maintainabilityWarning")]
    public int MaintainabilityWarning { get; set; } = 50;

    [JsonPropertyName("locWarning")]
    public int LocWarning { get; set; } = 50;
}

public class CodeMetricsResult : ToolResultBase
{
    public override string Operation => "csharp_code_metrics";

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("metrics")]
    public List<CodeMetricInfo>? Metrics { get; set; }

    [JsonPropertyName("summary")]
    public CodeMetricsSummary? Summary { get; set; }

    [JsonPropertyName("distribution")]
    public MetricsDistribution? Distribution { get; set; }
}

public class CodeMetricInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("cyclomaticComplexity")]
    public int CyclomaticComplexity { get; set; }

    [JsonPropertyName("linesOfCode")]
    public int LinesOfCode { get; set; }

    [JsonPropertyName("maintainabilityIndex")]
    public double? MaintainabilityIndex { get; set; }

    [JsonPropertyName("depthOfInheritance")]
    public int? DepthOfInheritance { get; set; }

    [JsonPropertyName("methodCount")]
    public int? MethodCount { get; set; }

    [JsonPropertyName("typeCount")]
    public int? TypeCount { get; set; }

    [JsonPropertyName("location")]
    public MetricLocationInfo? Location { get; set; }
}

public class MetricLocationInfo
{
    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }
}

public class CodeMetricsSummary
{
    [JsonPropertyName("totalItems")]
    public int TotalItems { get; set; }

    [JsonPropertyName("averageComplexity")]
    public double AverageComplexity { get; set; }

    [JsonPropertyName("maxComplexity")]
    public int MaxComplexity { get; set; }

    [JsonPropertyName("totalLinesOfCode")]
    public int TotalLinesOfCode { get; set; }

    [JsonPropertyName("averageMaintainabilityIndex")]
    public double? AverageMaintainabilityIndex { get; set; }
}

public class MetricsDistribution
{
    [JsonPropertyName("complexityRanges")]
    public Dictionary<string, int>? ComplexityRanges { get; set; }

    [JsonPropertyName("maintainabilityRanges")]
    public Dictionary<string, int>? MaintainabilityRanges { get; set; }
}