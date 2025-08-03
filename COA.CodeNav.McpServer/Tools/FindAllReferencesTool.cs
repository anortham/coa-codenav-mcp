using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Utilities;
using COA.Mcp.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Tools;

using System.Linq;

[McpServerToolType]
public class FindAllReferencesTool
{
    private readonly ILogger<FindAllReferencesTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;
    
    // Removed hard limit - now using dynamic token estimation

    public FindAllReferencesTool(
        ILogger<FindAllReferencesTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "csharp_find_all_references")]
    [Description("Find all references to a symbol at a given position in a file")]
    public async Task<object> ExecuteAsync(FindAllReferencesParams parameters, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        
        try
        {
            _logger.LogInformation("Finding all references at {FilePath}:{Line}:{Column}", 
                parameters.FilePath, parameters.Line, parameters.Column);

            // Get the document
            var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                return new FindAllReferencesToolResult
                {
                    Success = false,
                    Message = $"Document not found in workspace: {parameters.FilePath}",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.DOCUMENT_NOT_FOUND,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the file path is correct and absolute",
                                "Verify the solution/project containing this file is loaded",
                                "Use roslyn_load_solution or roslyn_load_project to load the containing project"
                            }
                        }
                    },
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
                    },
                    Meta = new ToolMetadata 
                    { 
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                    }
                };
            }

            // Calculate position
            var sourceText = await document.GetTextAsync(cancellationToken);
            var position = sourceText.Lines.GetPosition(new LinePosition(parameters.Line - 1, parameters.Column - 1));

            // Get semantic model
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                return new FindAllReferencesToolResult
                {
                    Success = false,
                    Message = "Failed to get semantic model",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Ensure the project is fully loaded and compiled",
                                "Check for compilation errors in the project",
                                "Try reloading the solution"
                            }
                        }
                    },
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
                    },
                    Meta = new ToolMetadata 
                    { 
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                    }
                };
            }

            // Find symbol at position
            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, document.Project.Solution.Workspace, cancellationToken);
            if (symbol == null)
            {
                return new FindAllReferencesToolResult
                {
                    Success = false,
                    Message = "No symbol found at the specified position",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.NO_SYMBOL_AT_POSITION,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Verify the line and column numbers are correct (1-based)",
                                "Ensure the cursor is on a symbol (class, method, property, etc.)",
                                "Try adjusting the column position to the start of the symbol name"
                            }
                        }
                    },
                    Query = new QueryInfo
                    {
                        FilePath = parameters.FilePath,
                        Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
                    },
                    Meta = new ToolMetadata 
                    { 
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                    }
                };
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
            var totalCount = sortedLocations.Count;
            
            // Apply token management
            var response = TokenEstimator.CreateTokenAwareResponse(
                sortedLocations,
                locs => EstimateReferenceTokens(locs),
                requestedMax: parameters.MaxResults ?? 50, // Default to 50 references
                safetyLimit: TokenEstimator.DEFAULT_SAFETY_LIMIT,
                toolName: "roslyn_find_all_references"
            );
            
            // Generate insights (use all locations for accurate insights)
            var insights = GenerateInsights(symbol, sortedLocations);
            
            // Add truncation message if needed
            if (response.WasTruncated)
            {
                insights.Insert(0, response.GetTruncationMessage());
            }
            
            // Calculate distribution on all locations
            var distribution = new ReferenceDistribution
            {
                ByFile = sortedLocations.GroupBy(l => l.FilePath)
                    .ToDictionary(g => g.Key, g => g.Count()),
                ByKind = sortedLocations.GroupBy(l => l.Kind ?? "unknown")
                    .ToDictionary(g => g.Key, g => g.Count())
            };
            
            // Store full results as a resource if truncated
            string? resourceUri = null;
            if (response.WasTruncated && _resourceProvider != null)
            {
                var fullData = new
                {
                    symbol = symbol.ToDisplayString(),
                    symbolKind = symbol.Kind.ToString(),
                    totalReferences = totalCount,
                    allLocations = sortedLocations,
                    searchedFrom = new { parameters.FilePath, parameters.Line, parameters.Column }
                };
                
                resourceUri = _resourceProvider.StoreAnalysisResult(
                    "find-all-references",
                    fullData,
                    $"All {totalCount} references to {symbol.Name}"
                );
                
                _logger.LogDebug("Stored full reference data as resource: {ResourceUri}", resourceUri);
            }
            
            // Generate next actions
            var nextActions = GenerateNextActions(symbol, response.Items, response.WasTruncated, resourceUri);
            
            // Add action to get more results if truncated
            if (response.WasTruncated)
            {
                nextActions.Insert(0, new NextAction
                {
                    Id = "get_more_references",
                    Description = "Get additional references",
                    ToolName = "roslyn_find_all_references",
                    Parameters = new
                    {
                        filePath = parameters.FilePath,
                        line = parameters.Line,
                        column = parameters.Column,
                        maxResults = Math.Min(totalCount, 500)
                    },
                    Priority = "high"
                });
            }

            return new FindAllReferencesToolResult
            {
                Success = true,
                Locations = response.Items,
                Message = response.WasTruncated 
                    ? $"Found {totalCount} reference(s) - showing {response.ReturnedCount}"
                    : $"Found {totalCount} reference(s)",
                Actions = nextActions,
                Insights = insights,
                ResourceUri = resourceUri,
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                    TargetSymbol = symbol.ToDisplayString()
                },
                Summary = new SummaryInfo
                {
                    TotalFound = totalCount,
                    Returned = response.ReturnedCount,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    SymbolInfo = new SymbolSummary
                    {
                        Name = symbol.Name,
                        Kind = symbol.Kind.ToString(),
                        ContainingType = symbol.ContainingType?.ToDisplayString(),
                        Namespace = symbol.ContainingNamespace?.ToDisplayString()
                    }
                },
                ResultsSummary = new ResultsSummary
                {
                    Included = response.ReturnedCount,
                    Total = totalCount,
                    HasMore = response.WasTruncated
                },
                Distribution = distribution,
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
            _logger.LogError(ex, "Error finding references");
            return new FindAllReferencesToolResult
            {
                Success = false,
                Message = $"Error finding references: {ex.Message}",
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
                Query = new QueryInfo
                {
                    FilePath = parameters.FilePath,
                    Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column }
                },
                Meta = new ToolMetadata 
                { 
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                }
            };
        }
    }

    private int EstimateReferenceTokens(List<Models.ReferenceLocation> references)
    {
        return TokenEstimator.EstimateCollection(
            references,
            reference => TokenEstimator.Roslyn.EstimateReference(reference),
            baseTokens: TokenEstimator.BASE_RESPONSE_TOKENS
        );
    }
    
    private List<NextAction> GenerateNextActions(ISymbol symbol, List<Models.ReferenceLocation> locations, bool wasTruncated, string? resourceUri)
    {
        var actions = new List<NextAction>();
        
        // If results were truncated, provide action to get full results
        if (wasTruncated && !string.IsNullOrEmpty(resourceUri))
        {
            actions.Add(new NextAction
            {
                Id = "get_all_references",
                Description = $"Retrieve all references from stored resource",
                ToolName = "read_resource",
                Parameters = new
                {
                    uri = resourceUri
                },
                Priority = "high"
            });
        }

        // If this is a method or property, suggest going to its definition
        if (symbol.Kind == SymbolKind.Method || symbol.Kind == SymbolKind.Property || 
            symbol.Kind == SymbolKind.Field || symbol.Kind == SymbolKind.Event)
        {
            var containingType = symbol.ContainingType;
            if (containingType != null && containingType.Locations.Any(l => l.IsInSource))
            {
                var location = containingType.Locations.First(l => l.IsInSource);
                var lineSpan = location.GetLineSpan();
                
                actions.Add(new NextAction
                {
                    Id = "goto_containing_type",
                    Description = $"Go to containing type '{containingType.Name}'",
                    ToolName = "csharp_goto_definition",
                    Parameters = new
                    {
                        filePath = lineSpan.Path,
                        line = lineSpan.StartLinePosition.Line + 1,
                        column = lineSpan.StartLinePosition.Character + 1
                    },
                    Priority = "high"
                });
            }
        }

        // If we found references in multiple files, suggest exploring a specific file
        var filesWithReferences = locations.Select(l => l.FilePath).Distinct().ToList();
        if (filesWithReferences.Count > 3)
        {
            var topFile = locations.GroupBy(l => l.FilePath)
                .OrderByDescending(g => g.Count())
                .First();

            actions.Add(new NextAction
            {
                Id = "filter_to_file",
                Description = $"Focus on {Path.GetFileName(topFile.Key)} ({topFile.Count()} references)",
                ToolName = "roslyn_find_all_references",
                Parameters = new
                {
                    filePath = topFile.Key,
                    line = topFile.First().Line,
                    column = topFile.First().Column
                },
                Priority = "medium"
            });
        }

        // If this is a type, suggest finding derived types or implementations
        if (symbol.Kind == SymbolKind.NamedType)
        {
            var namedType = (INamedTypeSymbol)symbol;
            if (namedType.TypeKind == TypeKind.Interface)
            {
                actions.Add(new NextAction
                {
                    Id = "find_implementations",
                    Description = "Find implementations of this interface",
                    ToolName = "roslyn_find_implementations", // Future tool
                    Parameters = new
                    {
                        typeName = symbol.ToDisplayString()
                    },
                    Priority = "high"
                });
            }
            else if (namedType.TypeKind == TypeKind.Class && !namedType.IsSealed)
            {
                actions.Add(new NextAction
                {
                    Id = "find_derived",
                    Description = "Find derived classes",
                    ToolName = "roslyn_find_derived_types", // Future tool
                    Parameters = new
                    {
                        typeName = symbol.ToDisplayString()
                    },
                    Priority = "medium"
                });
            }
        }

        // Always suggest hover for more information
        if (locations.Any())
        {
            var firstLoc = locations.First();
            actions.Add(new NextAction
            {
                Id = "hover_info",
                Description = "Get hover information for this symbol",
                ToolName = "roslyn_hover",
                Parameters = new
                {
                    filePath = firstLoc.FilePath,
                    line = firstLoc.Line,
                    column = firstLoc.Column
                },
                Priority = "low"
            });
        }

        return actions;
    }
    
    private List<string> GenerateInsights(ISymbol symbol, List<Models.ReferenceLocation> locations)
    {
        var insights = new List<string>();
        
        // Basic symbol info
        insights.Add($"Symbol '{symbol.Name}' is a {SymbolUtilities.GetFriendlySymbolKind(symbol)}");
        
        // File distribution
        var fileCount = locations.Select(l => l.FilePath).Distinct().Count();
        if (fileCount == 1)
        {
            insights.Add($"All references are in a single file");
        }
        else
        {
            insights.Add($"References spread across {fileCount} files");
        }
        
        // Reference type distribution
        var sourceRefs = locations.Count(l => l.Kind == "reference");
        var metadataRefs = locations.Count(l => l.Kind == "metadata");
        if (metadataRefs > 0)
        {
            insights.Add($"{sourceRefs} source references, {metadataRefs} metadata references");
        }
        
        // Usage patterns
        if (symbol.Kind == SymbolKind.Method)
        {
            insights.Add($"Method is called {locations.Count} time(s)");
        }
        else if (symbol.Kind == SymbolKind.Property)
        {
            insights.Add($"Property is accessed {locations.Count} time(s)");
        }
        else if (symbol.Kind == SymbolKind.Field)
        {
            insights.Add($"Field is referenced {locations.Count} time(s)");
        }
        
        // Most referenced file
        if (fileCount > 1)
        {
            var topFile = locations.GroupBy(l => l.FilePath)
                .OrderByDescending(g => g.Count())
                .First();
            insights.Add($"Most references in {Path.GetFileName(topFile.Key)} ({topFile.Count()} references)");
        }
        
        return insights;
    }
    
}

public class FindAllReferencesParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file")]
    public required string FilePath { get; set; }

    [JsonPropertyName("line")]
    [Description("Line number (1-based)")]
    public required int Line { get; set; }

    [JsonPropertyName("column")]
    [Description("Column number (1-based)")]
    public required int Column { get; set; }
    
    [JsonPropertyName("maxResults")]
    [Description("Maximum number of references to return (default: 50, max: 500)")]
    public int? MaxResults { get; set; }
}

// Result classes have been moved to Models/ToolResults.cs and Models/CodeElementModels.cs