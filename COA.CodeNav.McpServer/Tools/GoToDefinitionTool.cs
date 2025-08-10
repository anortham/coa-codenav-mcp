using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that provides Go to Definition functionality using Roslyn
/// </summary>
public class GoToDefinitionTool : McpToolBase<GoToDefinitionParams, GoToDefinitionToolResult>
{
    private readonly ILogger<GoToDefinitionTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => "csharp_goto_definition";
    public override string Description => @"Navigate to the definition of a symbol at a given position in a file.
Returns: Symbol definition location with metadata and documentation.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if symbol cannot be resolved.
Use cases: Jumping to source code, understanding symbol origins, code navigation.
Not for: Finding references (use csharp_find_all_references), searching symbols (use csharp_symbol_search).";
    
    public GoToDefinitionTool(
        ILogger<GoToDefinitionTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<GoToDefinitionToolResult> ExecuteInternalAsync(
        GoToDefinitionParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("GoToDefinition request received: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);
        
        var startTime = DateTime.UtcNow;

        // Get the document
        _logger.LogDebug("Retrieving document from workspace: {FilePath}", parameters.FilePath);
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
            // Use Framework's error result pattern
            return new GoToDefinitionToolResult
            {
                Success = false,
                Message = $"Document not found in workspace: {parameters.FilePath}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.DOCUMENT_NOT_FOUND,
                    Message = $"Document not found in workspace: {parameters.FilePath}",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Ensure the file path is correct and absolute",
                            "Verify the solution/project containing this file is loaded",
                            "Use csharp_load_solution or csharp_load_project to load the containing project"
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

        // Get the source text
        var sourceText = await document.GetTextAsync(cancellationToken);
        
        // Convert line/column to position (adjusting for 0-based indexing)
        var position = sourceText.Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
            parameters.Line - 1, 
            parameters.Column - 1));

        // Get semantic model
        _logger.LogDebug("Getting semantic model for document");
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            _logger.LogError("Failed to get semantic model for document: {FilePath}", parameters.FilePath);
            return new GoToDefinitionToolResult
            {
                Success = false,
                Message = "Could not get semantic model for document",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE,
                    Message = "Could not get semantic model for document",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Ensure the project is fully loaded and compiled",
                            "Check for compilation errors in the project",
                            "Try reloading the solution"
                        }
                    }
                }
            };
        }

        // Find symbol at position
        _logger.LogDebug("Searching for symbol at position {Position}", position);
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            semanticModel, 
            position, 
            document.Project.Solution.Workspace, 
            cancellationToken);

        if (symbol == null)
        {
            _logger.LogDebug("No symbol found at position {Line}:{Column} in {FilePath}", 
                parameters.Line, parameters.Column, parameters.FilePath);
            return new GoToDefinitionToolResult
            {
                Success = false,
                Message = "No symbol found at the specified position",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.NO_SYMBOL_AT_POSITION,
                    Message = "No symbol found at the specified position",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Verify the line and column numbers are correct (1-based)",
                            "Ensure the cursor is on a symbol (class, method, property, etc.)",
                            "Try adjusting the column position to the start of the symbol name"
                        }
                    }
                }
            };
        }

        // Find definition locations
        _logger.LogDebug("Found symbol '{SymbolName}' of kind {SymbolKind}, searching for definition locations", 
            symbol.ToDisplayString(), symbol.Kind);
            
        var locations = new List<LocationInfo>();

        // Check if symbol has source locations
        foreach (var location in symbol.Locations)
        {
            if (location.IsInSource)
            {
                var lineSpan = location.GetLineSpan();
                locations.Add(new LocationInfo
                {
                    FilePath = lineSpan.Path,
                    Line = lineSpan.StartLinePosition.Line + 1,
                    Column = lineSpan.StartLinePosition.Character + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1,
                    EndColumn = lineSpan.EndLinePosition.Character + 1
                });
            }
        }

        // If symbol is from metadata, return metadata result
        if (locations.Count == 0 && symbol.Locations.Any(l => l.IsInMetadata))
        {
            return new GoToDefinitionToolResult
            {
                Success = true,
                IsMetadata = true,
                Message = $"Symbol '{symbol.Name}' is defined in metadata (external assembly)",
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    TargetSymbol = symbol.ToDisplayString()
                },
                Summary = new SummaryInfo
                {
                    TotalFound = 0,
                    Returned = 0,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    SymbolInfo = new SymbolSummary
                    {
                        Name = symbol.Name,
                        Kind = symbol.Kind.ToString(),
                        ContainingType = symbol.ContainingType?.ToDisplayString(),
                        Namespace = symbol.ContainingNamespace?.ToDisplayString()
                    }
                },
                Meta = new ToolExecutionMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    Tokens = 500 // Metadata-only response is small
                }
            };
        }

        if (locations.Count == 0)
        {
            return new GoToDefinitionToolResult
            {
                Success = false,
                Message = "Symbol definition location not found",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.NO_DEFINITION_FOUND,
                    Message = "Symbol definition location not found",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "The symbol may be defined in an external assembly",
                            "Check if the symbol is from a NuGet package or framework",
                            "Ensure all project references are loaded"
                        }
                    }
                }
            };
        }

        // Generate insights
        var insights = GenerateInsights(symbol, locations);

        // Generate next actions
        var actions = GenerateNextActions(parameters, locations.FirstOrDefault());

        _logger.LogInformation("GoToDefinition found {Count} location(s) for symbol '{Symbol}'", 
            locations.Count, symbol.ToDisplayString());

        return new GoToDefinitionToolResult
        {
            Success = true,
            Message = $"Found {locations.Count} definition location(s) for '{symbol.Name}'",
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                TargetSymbol = symbol.ToDisplayString()
            },
            Summary = new SummaryInfo
            {
                TotalFound = locations.Count,
                Returned = locations.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                SymbolInfo = new ExtendedSymbolSummary
                {
                    Name = symbol.Name,
                    Kind = symbol.Kind.ToString(),
                    ContainingType = symbol.ContainingType?.ToDisplayString(),
                    Namespace = symbol.ContainingNamespace?.ToDisplayString(),
                    Documentation = GetSymbolDocumentation(symbol)
                }
            },
            Locations = locations,
            ResultsSummary = new ResultsSummary
            {
                Total = locations.Count,
                Included = locations.Count,
                HasMore = false
            },
            Insights = insights,
            Actions = actions,
            Meta = new ToolExecutionMetadata 
            { 
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                Tokens = EstimateResponseTokens(locations)
            }
        };
    }


    private List<string> GenerateInsights(ISymbol symbol, List<LocationInfo> locations)
    {
        var insights = new List<string>();

        if (locations.Count > 1)
        {
            insights.Add($"🔀 Multiple definitions found - likely partial classes or method overloads");
        }

        if (symbol.IsVirtual || symbol.IsAbstract)
        {
            insights.Add($"🔄 This is a virtual/abstract member that can be overridden");
        }

        if (symbol.Kind == SymbolKind.Method && symbol is IMethodSymbol method)
        {
            if (method.IsExtensionMethod)
            {
                insights.Add($"🔌 This is an extension method");
            }
            if (method.IsAsync)
            {
                insights.Add($"⚡ This is an async method");
            }
        }

        if (symbol.DeclaredAccessibility == Accessibility.Public)
        {
            insights.Add($"🌐 Public API member - changes may affect external consumers");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(GoToDefinitionParams originalParams, LocationInfo? firstLocation)
    {
        var actions = new List<AIAction>();

        if (firstLocation != null)
        {
            actions.Add(new AIAction
            {
                Action = "csharp_find_all_references",
                Description = "Find all references to this symbol",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = firstLocation.FilePath,
                    ["line"] = firstLocation.Line,
                    ["column"] = firstLocation.Column
                },
                Priority = 90,
                Category = "navigation"
            });

            actions.Add(new AIAction
            {
                Action = "csharp_find_implementations",
                Description = "Find implementations if this is an interface/abstract",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = firstLocation.FilePath,
                    ["line"] = firstLocation.Line,
                    ["column"] = firstLocation.Column
                },
                Priority = 80,
                Category = "navigation"
            });

            actions.Add(new AIAction
            {
                Action = "csharp_hover",
                Description = "Get detailed information about this symbol",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = firstLocation.FilePath,
                    ["line"] = firstLocation.Line,
                    ["column"] = firstLocation.Column
                },
                Priority = 70,
                Category = "information"
            });
        }

        return actions;
    }


    private string? GetSymbolDocumentation(ISymbol symbol)
    {
        var xmlComment = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xmlComment))
            return null;

        // Simple extraction of summary tag
        var summaryStart = xmlComment.IndexOf("<summary>");
        var summaryEnd = xmlComment.IndexOf("</summary>");
        if (summaryStart >= 0 && summaryEnd > summaryStart)
        {
            var summary = xmlComment.Substring(summaryStart + 9, summaryEnd - summaryStart - 9);
            return summary.Trim();
        }

        return null;
    }

    private int EstimateResponseTokens(List<LocationInfo> locations)
    {
        // Base tokens for response structure
        var baseTokens = 500;
        
        // Each location adds approximately 150 tokens
        var locationTokens = locations.Count * 150;
        
        return baseTokens + locationTokens;
    }

}

/// <summary>
/// Parameters for GoToDefinition tool
/// </summary>
public class GoToDefinitionParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file")]
    public string FilePath { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Line must be positive")]
    [JsonPropertyName("line")]
    [COA.Mcp.Framework.Attributes.Description("Line number (1-based) where the symbol appears")]
    public int Line { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Column must be positive")]
    [JsonPropertyName("column")]
    [COA.Mcp.Framework.Attributes.Description("Column number (1-based) where the symbol appears")]
    public int Column { get; set; }
}