using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// Tool for finding all references to a symbol in the codebase
/// </summary>
public class FindAllReferencesTool : McpToolBase<FindAllReferencesParams, object>
{
    private readonly ILogger<FindAllReferencesTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;
    private readonly FindAllReferencesResponseBuilder _responseBuilder;

    public override string Name => "csharp_find_all_references";
    public override string Description => @"Find all references to a symbol at a given position in a file.
Returns: List of reference locations with file paths, line numbers, and context.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if symbol cannot be found.
Use cases: Impact analysis, refactoring preparation, understanding symbol usage.
Not for: Finding definitions (use csharp_goto_definition), searching by name (use csharp_symbol_search).";
    public override ToolCategory Category => ToolCategory.Query;

    public FindAllReferencesTool(
        ILogger<FindAllReferencesTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        FindAllReferencesResponseBuilder responseBuilder,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _responseBuilder = responseBuilder;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<object> ExecuteInternalAsync(
        FindAllReferencesParams parameters,
        CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        
        _logger.LogInformation("Finding all references at {FilePath}:{Line}:{Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);

        // Get the document
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            return CreateErrorResult(
                ErrorCodes.DOCUMENT_NOT_FOUND,
                $"Document not found in workspace: {parameters.FilePath}",
                new[]
                {
                    "Ensure the file path is correct and absolute",
                    "Verify the solution/project containing this file is loaded",
                    "Use csharp_load_solution or csharp_load_project to load the containing project"
                },
                parameters,
                startTime);
        }

        // Calculate position
        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines.GetPosition(new LinePosition(parameters.Line - 1, parameters.Column - 1));

        // Get semantic model
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return CreateErrorResult(
                ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE,
                "Failed to get semantic model",
                new[]
                {
                    "Ensure the project is fully loaded and compiled",
                    "Check for compilation errors in the project",
                    "Try reloading the solution"
                },
                parameters,
                startTime);
        }

        // Find symbol at position
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            semanticModel, position, document.Project.Solution.Workspace, cancellationToken);
        
        if (symbol == null)
        {
            return CreateErrorResult(
                ErrorCodes.NO_SYMBOL_AT_POSITION,
                "No symbol found at the specified position",
                new[]
                {
                    "Verify the line and column numbers are correct (1-based)",
                    "Ensure the cursor is on a symbol (class, method, property, etc.)",
                    "Try adjusting the column position to the start of the symbol name"
                },
                parameters,
                startTime);
        }

        _logger.LogDebug("Found symbol: {SymbolName} ({SymbolKind})", symbol.Name, symbol.Kind);

        // Find all references
        var references = await SymbolFinder.FindReferencesAsync(
            symbol, 
            document.Project.Solution, 
            cancellationToken);

        var locations = new List<Models.ReferenceLocation>();

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                var refDoc = location.Document;
                var span = location.Location.SourceSpan;
                var lineSpan = (await refDoc.GetTextAsync(cancellationToken)).Lines.GetLinePositionSpan(span);

                locations.Add(new Models.ReferenceLocation
                {
                    FilePath = refDoc.FilePath ?? "<unknown>",
                    Line = lineSpan.Start.Line + 1,
                    Column = lineSpan.Start.Character + 1,
                    EndLine = lineSpan.End.Line + 1,
                    EndColumn = lineSpan.End.Character + 1,
                    Kind = location.Location.IsInSource ? "reference" : "metadata",
                    Text = (await refDoc.GetTextAsync(cancellationToken)).GetSubText(span).ToString()
                });
            }
        }

        _logger.LogInformation("Found {Count} references to {SymbolName}", locations.Count, symbol.Name);

        // Sort locations for consistent results
        var sortedLocations = locations.OrderBy(l => l.FilePath).ThenBy(l => l.Line).ToList();
        
        // Store full results as a resource if large
        string? resourceUri = null;
        if (sortedLocations.Count > 100 && _resourceProvider != null)
        {
            var fullData = new
            {
                symbol = symbol.ToDisplayString(),
                symbolKind = symbol.Kind.ToString(),
                totalReferences = sortedLocations.Count,
                allLocations = sortedLocations,
                searchedFrom = new { parameters.FilePath, parameters.Line, parameters.Column }
            };
            
            resourceUri = _resourceProvider.StoreAnalysisResult(
                "find-all-references",
                fullData,
                $"All {sortedLocations.Count} references to {symbol.Name}"
            );
            
            _logger.LogDebug("Stored full reference data as resource: {ResourceUri}", resourceUri);
        }
        
        // Prepare data for ResponseBuilder
        var data = new COA.CodeNav.McpServer.ResponseBuilders.FindAllReferencesData
        {
            Symbol = symbol,
            Locations = sortedLocations,
            SearchLocation = (parameters.FilePath, parameters.Line, parameters.Column),
            ResourceUri = resourceUri
        };
        
        // Build response using framework's token optimization
        var context = new COA.Mcp.Framework.TokenOptimization.ResponseBuilders.ResponseContext
        {
            ResponseMode = "optimized",
            TokenLimit = parameters.MaxResults.HasValue ? parameters.MaxResults * 200 : 10000,
            ToolName = Name
        };
        
        return await _responseBuilder.BuildResponseAsync(data, context);
    }

    private object CreateErrorResult(
        string errorCode,
        string message,
        string[] recoverySteps,
        FindAllReferencesParams parameters,
        DateTime startTime)
    {
        // Return a simple error response - framework doesn't need the full structure
        return new
        {
            Success = false,
            Message = message,
            Error = new ErrorInfo
            {
                Code = errorCode,
                Message = message,
                Recovery = new RecoveryInfo
                {
                    Steps = recoverySteps
                }
            },
            Query = new
            {
                FilePath = parameters.FilePath,
                Line = parameters.Line,
                Column = parameters.Column
            },
            ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
        };
    }
    
}

public class FindAllReferencesParams
{
    [JsonPropertyName("filePath")]
    [System.ComponentModel.DataAnnotations.Required]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)]
    [COA.Mcp.Framework.Attributes.Description("Line number (1-based) where the symbol appears")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)]
    [COA.Mcp.Framework.Attributes.Description("Column number (1-based) where the symbol appears")]
    public int Column { get; set; }

    [JsonPropertyName("maxResults")]
    [System.ComponentModel.DataAnnotations.Range(1, 500)]
    [COA.Mcp.Framework.Attributes.Description("Maximum number of references to return (default: 50, max: 500)")]
    public int? MaxResults { get; set; }
}