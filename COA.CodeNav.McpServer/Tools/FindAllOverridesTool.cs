using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using DataAnnotations = System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that finds all overrides of virtual/abstract methods and properties
/// </summary>
public class FindAllOverridesTool : McpToolBase<FindAllOverridesParams, FindAllOverridesResult>
{
    private readonly ILogger<FindAllOverridesTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => "csharp_find_all_overrides";
    public override string Description => @"Find all overrides of virtual/abstract methods and properties, including interface implementations.
Returns: Complete inheritance tree showing all overrides, implementations, and their relationships.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if symbol is not overridable.
Use cases: Impact analysis before changes, understanding inheritance hierarchy, finding all implementations.
AI benefit: Provides complete override information that's difficult to piece together from other tools.";

    public FindAllOverridesTool(
        ILogger<FindAllOverridesTool> logger,
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

    protected override async Task<FindAllOverridesResult> ExecuteInternalAsync(
        FindAllOverridesParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("FindAllOverrides request: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);
            
        var startTime = DateTime.UtcNow;

        // Get the document
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
            return new FindAllOverridesResult
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

        // Get the symbol at position
        var sourceText = await document.GetTextAsync(cancellationToken);
        var position = sourceText.Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(
            parameters.Line - 1, 
            parameters.Column - 1));

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return new FindAllOverridesResult
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

        // Find the symbol at position
        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            semanticModel, 
            position, 
            document.Project.Solution.Workspace, 
            cancellationToken);

        if (symbol == null)
        {
            return new FindAllOverridesResult
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
                            "Ensure the cursor is on a method or property name",
                            "Verify the line and column numbers are correct (1-based)",
                            "Try positioning on the method/property declaration"
                        }
                    }
                }
            };
        }

        // Check if the symbol is overridable
        var overridableInfo = GetOverridableInfo(symbol);
        if (!overridableInfo.IsOverridable)
        {
            return new FindAllOverridesResult
            {
                Success = false,
                Message = $"The symbol '{symbol.Name}' is not overridable (not virtual, abstract, or part of an interface)",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SYMBOL_NOT_FOUND,
                    Message = $"The symbol '{symbol.Name}' is not overridable (not virtual, abstract, or part of an interface)",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "This tool only works with virtual, abstract, or interface members",
                            "For finding all references, use csharp_find_all_references",
                            "For finding implementations of types, use csharp_find_implementations"
                        }
                    }
                }
            };
        }

        _logger.LogInformation("Finding overrides for {SymbolType} '{SymbolName}'", 
            overridableInfo.SymbolType, symbol.ToDisplayString());

        // Build the override hierarchy
        var hierarchy = await BuildOverrideHierarchyAsync(
            symbol, 
            document.Project.Solution,
            parameters.IncludeInterfaces,
            parameters.IncludeBase,
            cancellationToken);

        // Flatten hierarchy for counting
        var allOverrides = FlattenHierarchy(hierarchy);
        
        // Apply limit if specified
        var limitedOverrides = allOverrides;
        if (parameters.MaxResults.HasValue && allOverrides.Count > parameters.MaxResults.Value)
        {
            limitedOverrides = allOverrides.Take(parameters.MaxResults.Value).ToList();
        }

        // Generate insights and analysis
        var insights = GenerateInsights(allOverrides, symbol, overridableInfo);
        var analysis = GenerateAnalysis(allOverrides, symbol, overridableInfo);
        var distribution = GenerateDistribution(allOverrides);
        var nextActions = GenerateNextActions(symbol, parameters, overridableInfo);

        _logger.LogInformation("Override search completed: Found {Count} overrides", allOverrides.Count);

        return new FindAllOverridesResult
        {
            Success = true,
            Message = $"Found {allOverrides.Count} overrides for '{symbol.Name}'",
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo { Line = parameters.Line, Column = parameters.Column },
                TargetSymbol = symbol.ToDisplayString()
            },
            Summary = new SummaryInfo
            {
                TotalFound = allOverrides.Count,
                Returned = limitedOverrides.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                SymbolInfo = new SymbolSummary
                {
                    Name = symbol.Name,
                    Kind = symbol.Kind.ToString(),
                    ContainingType = symbol.ContainingType?.ToDisplayString(),
                    Namespace = symbol.ContainingNamespace?.ToDisplayString()
                }
            },
            BaseSymbol = CreateSymbolInfo(symbol, overridableInfo),
            Hierarchy = CreateLimitedHierarchy(hierarchy, limitedOverrides),
            Overrides = limitedOverrides,
            Analysis = analysis,
            Distribution = distribution,
            Insights = insights,
            Actions = nextActions,
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
    }

    private async Task<OverrideHierarchy> BuildOverrideHierarchyAsync(
        ISymbol symbol,
        Solution solution,
        bool includeInterfaces,
        bool includeBase,
        CancellationToken cancellationToken)
    {
        var hierarchy = new OverrideHierarchy
        {
            Symbol = symbol.ToDisplayString(),
            SymbolName = symbol.Name,
            Location = GetSymbolLocation(symbol),
            IsVirtual = IsVirtual(symbol),
            IsAbstract = IsAbstract(symbol),
            IsInterface = symbol.ContainingType?.TypeKind == TypeKind.Interface,
            Overrides = new List<OverrideInfo>(),
            BaseChain = new List<OverrideInfo>()
        };

        // Find all implementations/overrides
        var implementations = await SymbolFinder.FindImplementationsAsync(
            symbol, 
            solution, 
            cancellationToken: cancellationToken);

        var implementationsList = implementations.ToList();
        _logger.LogInformation("FindImplementationsAsync found {Count} implementations for {Symbol}", 
            implementationsList.Count, symbol.ToDisplayString());

        // For abstract properties/methods in abstract classes, FindImplementationsAsync may not find overrides
        if (implementationsList.Count == 0 && symbol.IsAbstract && symbol.ContainingType?.TypeKind == TypeKind.Class)
        {
            _logger.LogInformation("Looking for overrides in derived types for abstract {SymbolType} '{Symbol}'", 
                symbol.Kind, symbol.ToDisplayString());
                
            var derivedTypes = await SymbolFinder.FindDerivedClassesAsync(
                (INamedTypeSymbol)symbol.ContainingType,
                solution,
                cancellationToken: cancellationToken);
                
            _logger.LogInformation("Found {Count} derived types", derivedTypes.Count());
                
            foreach (var derivedType in derivedTypes)
            {
                var overridingMember = derivedType.GetMembers(symbol.Name)
                    .FirstOrDefault(m => IsOverrideOf(m, symbol));
                    
                if (overridingMember != null)
                {
                    _logger.LogInformation("Found override in {DerivedType}", derivedType.ToDisplayString());
                    implementationsList.Add(overridingMember);
                }
            }
        }
        
        implementations = implementationsList;

        foreach (var impl in implementations)
        {
            // Skip the original symbol
            if (SymbolEqualityComparer.Default.Equals(impl, symbol))
                continue;

            // Handle interface implementations if requested
            if (!includeInterfaces && impl.ContainingType?.TypeKind == TypeKind.Interface)
                continue;

            var overrideInfo = CreateOverrideInfo(impl, symbol);
            if (overrideInfo != null)
            {
                hierarchy.Overrides.Add(overrideInfo);
            }
        }

        // Find base chain if requested
        if (includeBase)
        {
            await BuildBaseChainAsync(symbol, hierarchy.BaseChain, solution, cancellationToken);
        }

        return hierarchy;
    }

    private Task BuildBaseChainAsync(
        ISymbol symbol,
        List<OverrideInfo> baseChain,
        Solution solution,
        CancellationToken cancellationToken)
    {
        ISymbol? currentSymbol = symbol;
        
        while (currentSymbol != null)
        {
            ISymbol? baseSymbol = null;
            
            if (currentSymbol is IMethodSymbol method && method.IsOverride)
            {
                baseSymbol = method.OverriddenMethod;
            }
            else if (currentSymbol is IPropertySymbol property && property.IsOverride)
            {
                baseSymbol = property.OverriddenProperty;
            }
            
            if (baseSymbol != null)
            {
                var baseInfo = CreateOverrideInfo(baseSymbol, null);
                if (baseInfo != null)
                {
                    baseChain.Add(baseInfo);
                }
                currentSymbol = baseSymbol;
            }
            else
            {
                break;
            }
        }
        
        return Task.CompletedTask;
    }

    private OverrideInfo? CreateOverrideInfo(ISymbol symbol, ISymbol? originalSymbol)
    {
        if (symbol.Locations.IsEmpty || !symbol.Locations.Any(l => l.IsInSource))
            return null;

        var location = symbol.Locations.First(l => l.IsInSource);
        var lineSpan = location.GetLineSpan();

        return new OverrideInfo
        {
            Symbol = symbol.ToDisplayString(),
            SymbolName = symbol.Name,
            ContainingType = symbol.ContainingType?.ToDisplayString() ?? "<unknown>",
            Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? "<global>",
            Location = new LocationInfo
            {
                FilePath = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                EndColumn = lineSpan.EndLinePosition.Character + 1
            },
            IsDirectOverride = IsDirectOverride(symbol, originalSymbol),
            IsExplicitImplementation = IsExplicitImplementation(symbol),
            IsSealed = IsSealed(symbol),
            IsPartial = IsPartial(symbol),
            OverrideType = DetermineOverrideType(symbol, originalSymbol),
            Documentation = GetDocumentation(symbol)
        };
    }

    private BaseSymbolInfo CreateSymbolInfo(ISymbol symbol, OverridableInfo info)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        LocationInfo? locationInfo = null;
        
        if (location != null)
        {
            var lineSpan = location.GetLineSpan();
            locationInfo = new LocationInfo
            {
                FilePath = lineSpan.Path,
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                EndColumn = lineSpan.EndLinePosition.Character + 1
            };
        }

        return new BaseSymbolInfo
        {
            Symbol = symbol.ToDisplayString(),
            SymbolName = symbol.Name,
            SymbolKind = symbol.Kind.ToString(),
            ContainingType = symbol.ContainingType?.ToDisplayString(),
            Namespace = symbol.ContainingNamespace?.ToDisplayString(),
            Location = locationInfo,
            IsVirtual = info.IsVirtual,
            IsAbstract = info.IsAbstract,
            IsInterface = info.IsInterface,
            Documentation = GetDocumentation(symbol)
        };
    }

    private OverridableInfo GetOverridableInfo(ISymbol symbol)
    {
        var info = new OverridableInfo
        {
            SymbolType = symbol.Kind.ToString()
        };

        switch (symbol)
        {
            case IMethodSymbol method:
                info.IsVirtual = method.IsVirtual;
                info.IsAbstract = method.IsAbstract;
                info.IsInterface = method.ContainingType?.TypeKind == TypeKind.Interface;
                info.IsOverridable = method.IsVirtual || method.IsAbstract || info.IsInterface;
                break;
                
            case IPropertySymbol property:
                info.IsVirtual = property.IsVirtual;
                info.IsAbstract = property.IsAbstract;
                info.IsInterface = property.ContainingType?.TypeKind == TypeKind.Interface;
                info.IsOverridable = property.IsVirtual || property.IsAbstract || info.IsInterface;
                break;
                
            case IEventSymbol eventSymbol:
                info.IsVirtual = eventSymbol.IsVirtual;
                info.IsAbstract = eventSymbol.IsAbstract;
                info.IsInterface = eventSymbol.ContainingType?.TypeKind == TypeKind.Interface;
                info.IsOverridable = eventSymbol.IsVirtual || eventSymbol.IsAbstract || info.IsInterface;
                break;
                
            default:
                info.IsOverridable = false;
                break;
        }

        return info;
    }

    private List<OverrideInfo> FlattenHierarchy(OverrideHierarchy hierarchy)
    {
        var result = new List<OverrideInfo>();
        result.AddRange(hierarchy.Overrides);
        result.AddRange(hierarchy.BaseChain);
        return result;
    }

    private OverrideHierarchy CreateLimitedHierarchy(OverrideHierarchy original, List<OverrideInfo> includedOverrides)
    {
        var includedSymbols = new HashSet<string>(includedOverrides.Select(o => o.Symbol));
        
        return new OverrideHierarchy
        {
            Symbol = original.Symbol,
            SymbolName = original.SymbolName,
            Location = original.Location,
            IsVirtual = original.IsVirtual,
            IsAbstract = original.IsAbstract,
            IsInterface = original.IsInterface,
            Overrides = original.Overrides.Where(o => includedSymbols.Contains(o.Symbol)).ToList(),
            BaseChain = original.BaseChain.Where(o => includedSymbols.Contains(o.Symbol)).ToList()
        };
    }

    private OverrideAnalysis GenerateAnalysis(List<OverrideInfo> overrides, ISymbol symbol, OverridableInfo info)
    {
        var analysis = new OverrideAnalysis
        {
            TotalOverrides = overrides.Count,
            DirectOverrides = overrides.Count(o => o.IsDirectOverride),
            IndirectOverrides = overrides.Count(o => !o.IsDirectOverride),
            SealedOverrides = overrides.Count(o => o.IsSealed),
            ExplicitImplementations = overrides.Count(o => o.IsExplicitImplementation),
            UniqueNamespaces = overrides.Select(o => o.Namespace).Distinct().Count(),
            UniqueProjects = overrides.Select(o => GetProjectFromPath(o.Location.FilePath)).Distinct().Count()
        };

        analysis.HasSealedImplementations = analysis.SealedOverrides > 0;
        analysis.HasExplicitImplementations = analysis.ExplicitImplementations > 0;
        analysis.IsWidelyImplemented = analysis.UniqueNamespaces > 5;

        return analysis;
    }

    private OverrideDistribution GenerateDistribution(List<OverrideInfo> overrides)
    {
        return new OverrideDistribution
        {
            ByNamespace = overrides
                .GroupBy(o => o.Namespace)
                .ToDictionary(g => g.Key, g => g.Count()),
            ByProject = overrides
                .GroupBy(o => GetProjectFromPath(o.Location.FilePath))
                .ToDictionary(g => g.Key, g => g.Count()),
            ByType = overrides
                .GroupBy(o => o.OverrideType)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private List<string> GenerateInsights(List<OverrideInfo> overrides, ISymbol symbol, OverridableInfo info)
    {
        var insights = new List<string>();
        var analysis = GenerateAnalysis(overrides, symbol, info);

        if (overrides.Count == 0)
        {
            insights.Add($"No overrides found - this {info.SymbolType.ToLower()} is not overridden anywhere");
        }
        else if (overrides.Count == 1)
        {
            insights.Add($"Only one override found - consider if virtual/abstract is necessary");
        }
        else if (overrides.Count > 10)
        {
            insights.Add($"Heavily overridden {info.SymbolType.ToLower()} with {overrides.Count} implementations");
        }

        if (analysis.HasSealedImplementations)
            insights.Add($"{analysis.SealedOverrides} sealed overrides prevent further inheritance");

        if (analysis.HasExplicitImplementations)
            insights.Add($"{analysis.ExplicitImplementations} explicit interface implementations found");

        if (analysis.IsWidelyImplemented)
            insights.Add($"Implemented across {analysis.UniqueNamespaces} namespaces - core abstraction");

        if (info.IsInterface)
            insights.Add("Interface member - all implementations are contractual");

        return insights;
    }

    private List<AIAction> GenerateNextActions(ISymbol symbol, FindAllOverridesParams parameters, OverridableInfo info)
    {
        var actions = new List<AIAction>();

        actions.Add(new AIAction
        {
            Action = "csharp_call_hierarchy",
            Description = $"View complete call hierarchy for '{symbol.Name}'",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = parameters.FilePath,
                ["line"] = parameters.Line,
                ["column"] = parameters.Column,
                ["includeOverrides"] = true
            },
            Priority = 90,
            Category = "analysis"
        });

        actions.Add(new AIAction
        {
            Action = "csharp_rename_symbol",
            Description = $"Rename '{symbol.Name}' including all overrides",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = parameters.FilePath,
                ["line"] = parameters.Line,
                ["column"] = parameters.Column,
                ["preview"] = true,
                ["renameOverloads"] = true
            },
            Priority = 70,
            Category = "refactoring"
        });

        actions.Add(new AIAction
        {
            Action = "csharp_find_all_references",
            Description = $"Find all references to '{symbol.Name}'",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = parameters.FilePath,
                ["line"] = parameters.Line,
                ["column"] = parameters.Column
            },
            Priority = 60,
            Category = "navigation"
        });

        return actions;
    }

    // Helper methods
    private bool IsVirtual(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.IsVirtual,
        IPropertySymbol property => property.IsVirtual,
        IEventSymbol evt => evt.IsVirtual,
        _ => false
    };

    private bool IsAbstract(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.IsAbstract,
        IPropertySymbol property => property.IsAbstract,
        IEventSymbol evt => evt.IsAbstract,
        _ => false
    };

    private bool IsSealed(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.IsSealed,
        IPropertySymbol property => property.IsSealed,
        IEventSymbol evt => evt.IsSealed,
        _ => false
    };

    private bool IsPartial(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.PartialDefinitionPart != null || method.PartialImplementationPart != null,
        _ => false
    };

    private bool IsDirectOverride(ISymbol symbol, ISymbol? originalSymbol)
    {
        if (originalSymbol == null) return false;

        return symbol switch
        {
            IMethodSymbol method => SymbolEqualityComparer.Default.Equals(method.OverriddenMethod, originalSymbol),
            IPropertySymbol property => SymbolEqualityComparer.Default.Equals(property.OverriddenProperty, originalSymbol),
            IEventSymbol evt => SymbolEqualityComparer.Default.Equals(evt.OverriddenEvent, originalSymbol),
            _ => false
        };
    }

    private bool IsExplicitImplementation(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.ExplicitInterfaceImplementations.Any(),
        IPropertySymbol property => property.ExplicitInterfaceImplementations.Any(),
        IEventSymbol evt => evt.ExplicitInterfaceImplementations.Any(),
        _ => false
    };

    private string DetermineOverrideType(ISymbol symbol, ISymbol? originalSymbol)
    {
        if (symbol.ContainingType?.TypeKind == TypeKind.Interface)
            return "Interface Implementation";
        
        if (IsExplicitImplementation(symbol))
            return "Explicit Implementation";
        
        if (IsDirectOverride(symbol, originalSymbol))
            return "Direct Override";
        
        if (symbol is IMethodSymbol { IsOverride: true } or IPropertySymbol { IsOverride: true })
            return "Indirect Override";
        
        return "Implementation";
    }

    private string GetSymbolLocation(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location != null)
        {
            var lineSpan = location.GetLineSpan();
            return $"{lineSpan.Path}:{lineSpan.StartLinePosition.Line + 1}";
        }
        return "<unknown>";
    }

    private string GetProjectFromPath(string filePath)
    {
        var parts = filePath.Split('\\', '/');
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i].EndsWith(".csproj") || (i > 0 && parts[i - 1].EndsWith(".csproj")))
            {
                return parts[i].Replace(".csproj", "");
            }
        }
        return "Unknown";
    }

    private string? GetDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        var startTag = "<summary>";
        var endTag = "</summary>";
        var start = xml.IndexOf(startTag);
        var end = xml.IndexOf(endTag);
        
        if (start >= 0 && end > start)
        {
            var summary = xml.Substring(start + startTag.Length, end - start - startTag.Length)
                .Trim()
                .Replace("\n", " ")
                .Replace("  ", " ");
            return summary;
        }

        return null;
    }

    private bool IsOverrideOf(ISymbol member, ISymbol baseMember)
    {
        return member switch
        {
            IMethodSymbol method => baseMember is IMethodSymbol baseMethod && 
                                   method.OverriddenMethod != null &&
                                   SymbolEqualityComparer.Default.Equals(method.OverriddenMethod.OriginalDefinition, baseMethod.OriginalDefinition),
                                   
            IPropertySymbol property => baseMember is IPropertySymbol baseProperty && 
                                       property.OverriddenProperty != null &&
                                       SymbolEqualityComparer.Default.Equals(property.OverriddenProperty.OriginalDefinition, baseProperty.OriginalDefinition),
                                       
            IEventSymbol eventSymbol => baseMember is IEventSymbol baseEvent && 
                                       eventSymbol.OverriddenEvent != null &&
                                       SymbolEqualityComparer.Default.Equals(eventSymbol.OverriddenEvent.OriginalDefinition, baseEvent.OriginalDefinition),
                                       
            _ => false
        };
    }

    private class OverridableInfo
    {
        public bool IsOverridable { get; set; }
        public bool IsVirtual { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsInterface { get; set; }
        public string SymbolType { get; set; } = "";
    }

    protected override int EstimateTokenUsage()
    {
        // Estimate for typical find all overrides response
        return 4000;
    }
}

/// <summary>
/// Parameters for FindAllOverrides tool
/// </summary>
public class FindAllOverridesParams
{
    [DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file containing the virtual/abstract member")]
    public string FilePath { get; set; } = string.Empty;

    [DataAnnotations.Required]
    [DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Line must be positive")]
    [JsonPropertyName("line")]
    [COA.Mcp.Framework.Attributes.Description("Line number (1-based) where the member is declared")]
    public int Line { get; set; }

    [DataAnnotations.Required]
    [DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Column must be positive")]
    [JsonPropertyName("column")]
    [COA.Mcp.Framework.Attributes.Description("Column number (1-based) where the member is declared")]
    public int Column { get; set; }
    
    [JsonPropertyName("includeInterfaces")]
    [COA.Mcp.Framework.Attributes.Description("Include interface implementations. true = include interface implementations (default), false = only overrides")]
    public bool IncludeInterfaces { get; set; } = true;
    
    [JsonPropertyName("includeBase")]
    [COA.Mcp.Framework.Attributes.Description("Include base method chain for overrides. true = show full inheritance chain (default), false = direct overrides only")]
    public bool IncludeBase { get; set; } = true;
    
    [JsonPropertyName("maxResults")]
    [COA.Mcp.Framework.Attributes.Description("Maximum number of overrides to return (default: 200, max: 500)")]
    public int? MaxResults { get; set; }
}

public class FindAllOverridesResult : ToolResultBase
{
    public override string Operation => "csharp_find_all_overrides";
    
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }
    
    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }
    
    [JsonPropertyName("baseSymbol")]
    public BaseSymbolInfo? BaseSymbol { get; set; }
    
    [JsonPropertyName("hierarchy")]
    public OverrideHierarchy? Hierarchy { get; set; }
    
    [JsonPropertyName("overrides")]
    public List<OverrideInfo>? Overrides { get; set; }
    
    [JsonPropertyName("analysis")]
    public OverrideAnalysis? Analysis { get; set; }
    
    [JsonPropertyName("distribution")]
    public OverrideDistribution? Distribution { get; set; }
}

public class BaseSymbolInfo
{
    [JsonPropertyName("symbol")]
    public required string Symbol { get; set; }
    
    [JsonPropertyName("symbolName")]
    public required string SymbolName { get; set; }
    
    [JsonPropertyName("symbolKind")]
    public required string SymbolKind { get; set; }
    
    [JsonPropertyName("containingType")]
    public string? ContainingType { get; set; }
    
    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }
    
    [JsonPropertyName("location")]
    public LocationInfo? Location { get; set; }
    
    [JsonPropertyName("isVirtual")]
    public bool IsVirtual { get; set; }
    
    [JsonPropertyName("isAbstract")]
    public bool IsAbstract { get; set; }
    
    [JsonPropertyName("isInterface")]
    public bool IsInterface { get; set; }
    
    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }
}

public class OverrideHierarchy
{
    [JsonPropertyName("symbol")]
    public required string Symbol { get; set; }
    
    [JsonPropertyName("symbolName")]
    public required string SymbolName { get; set; }
    
    [JsonPropertyName("location")]
    public required string Location { get; set; }
    
    [JsonPropertyName("isVirtual")]
    public bool IsVirtual { get; set; }
    
    [JsonPropertyName("isAbstract")]
    public bool IsAbstract { get; set; }
    
    [JsonPropertyName("isInterface")]
    public bool IsInterface { get; set; }
    
    [JsonPropertyName("overrides")]
    public List<OverrideInfo> Overrides { get; set; } = new();
    
    [JsonPropertyName("baseChain")]
    public List<OverrideInfo> BaseChain { get; set; } = new();
}

public class OverrideInfo
{
    [JsonPropertyName("symbol")]
    public required string Symbol { get; set; }
    
    [JsonPropertyName("symbolName")]
    public required string SymbolName { get; set; }
    
    [JsonPropertyName("containingType")]
    public required string ContainingType { get; set; }
    
    [JsonPropertyName("namespace")]
    public required string Namespace { get; set; }
    
    [JsonPropertyName("location")]
    public required LocationInfo Location { get; set; }
    
    [JsonPropertyName("isDirectOverride")]
    public bool IsDirectOverride { get; set; }
    
    [JsonPropertyName("isExplicitImplementation")]
    public bool IsExplicitImplementation { get; set; }
    
    [JsonPropertyName("isSealed")]
    public bool IsSealed { get; set; }
    
    [JsonPropertyName("isPartial")]
    public bool IsPartial { get; set; }
    
    [JsonPropertyName("overrideType")]
    public required string OverrideType { get; set; }
    
    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }
}

public class OverrideAnalysis
{
    [JsonPropertyName("totalOverrides")]
    public int TotalOverrides { get; set; }
    
    [JsonPropertyName("directOverrides")]
    public int DirectOverrides { get; set; }
    
    [JsonPropertyName("indirectOverrides")]
    public int IndirectOverrides { get; set; }
    
    [JsonPropertyName("sealedOverrides")]
    public int SealedOverrides { get; set; }
    
    [JsonPropertyName("explicitImplementations")]
    public int ExplicitImplementations { get; set; }
    
    [JsonPropertyName("uniqueNamespaces")]
    public int UniqueNamespaces { get; set; }
    
    [JsonPropertyName("uniqueProjects")]
    public int UniqueProjects { get; set; }
    
    [JsonPropertyName("hasSealedImplementations")]
    public bool HasSealedImplementations { get; set; }
    
    [JsonPropertyName("hasExplicitImplementations")]
    public bool HasExplicitImplementations { get; set; }
    
    [JsonPropertyName("isWidelyImplemented")]
    public bool IsWidelyImplemented { get; set; }
}

public class OverrideDistribution
{
    [JsonPropertyName("byNamespace")]
    public Dictionary<string, int>? ByNamespace { get; set; }
    
    [JsonPropertyName("byProject")]
    public Dictionary<string, int>? ByProject { get; set; }
    
    [JsonPropertyName("byType")]
    public Dictionary<string, int>? ByType { get; set; }
}