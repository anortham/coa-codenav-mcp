using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that extracts symbol hierarchy from a document using Framework v1.1.0
/// </summary>
public class DocumentSymbolsTool : McpToolBase<DocumentSymbolsParams, DocumentSymbolsToolResult>
{
    private readonly ILogger<DocumentSymbolsTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => ToolNames.DocumentSymbols;
    public override string Description => @"Extract the symbol hierarchy from a C# document.
Returns: Hierarchical list of classes, methods, properties, and fields with their locations.
Prerequisites: Call csharp_load_solution or csharp_load_project first.
Error handling: Returns specific error codes with recovery steps if document is not found.
Use cases: Code outline, document structure analysis, quick navigation within files.
Not for: Solution-wide search (use csharp_symbol_search), cross-references (use csharp_find_all_references).";

    public DocumentSymbolsTool(
        ILogger<DocumentSymbolsTool> logger,
        RoslynWorkspaceService workspaceService,
        ITokenEstimator tokenEstimator,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _tokenEstimator = tokenEstimator;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<DocumentSymbolsToolResult> ExecuteInternalAsync(
        DocumentSymbolsParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("DocumentSymbols request received: FilePath={FilePath}", parameters.FilePath);

        var startTime = DateTime.UtcNow;

        // Get the document
        _logger.LogDebug("Retrieving document from workspace: {FilePath}", parameters.FilePath);
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
            return new DocumentSymbolsToolResult
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

        // Get the syntax tree
        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (syntaxTree == null)
        {
            _logger.LogError("Failed to get syntax tree for document: {FilePath}", parameters.FilePath);
            return new DocumentSymbolsToolResult
            {
                Success = false,
                Message = "Could not parse document syntax",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.SEMANTIC_MODEL_UNAVAILABLE,
                    Message = "Could not parse document syntax",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new[]
                        {
                            "Check for syntax errors in the file",
                            "Ensure the file is a valid C# source file",
                            "Try reloading the project"
                        }
                    }
                }
            };
        }

        // Get semantic model
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        // Get the root node
        var root = await syntaxTree.GetRootAsync(cancellationToken);

        // Extract all symbols
        var allSymbols = new List<DocumentSymbol>();
        ExtractSymbols(root, allSymbols, semanticModel, parameters.IncludePrivate);

        // Apply filters
        if (parameters.SymbolKinds?.Any() == true)
        {
            allSymbols = FilterSymbolsByKind(allSymbols, parameters.SymbolKinds);
        }

        // Apply max results limit with token optimization
        var totalSymbolCount = CountSymbols(allSymbols);
        var requestedMaxResults = parameters.MaxResults;
        List<DocumentSymbol> returnedSymbols;
        bool wasLimited;

        // Check if we need token optimization
        var estimatedTokens = _tokenEstimator.EstimateObject(allSymbols);
        if (estimatedTokens > 10000)
        {
            // Use framework's progressive reduction
            returnedSymbols = _tokenEstimator.ApplyProgressiveReduction(
                allSymbols,
                symbol => _tokenEstimator.EstimateObject(symbol),
                10000,
                new[] { 100, 75, 50, 25, 10 }
            );
            var effectiveLimit = Math.Min(returnedSymbols.Count, requestedMaxResults);
            returnedSymbols = returnedSymbols.Take(effectiveLimit).ToList();
            wasLimited = true;
            
            _logger.LogWarning("Token optimization applied: reducing symbols from {Total} to {Safe}", 
                allSymbols.Count, returnedSymbols.Count);
        }
        else if (allSymbols.Count > requestedMaxResults)
        {
            returnedSymbols = allSymbols.Take(requestedMaxResults).ToList();
            wasLimited = true;
        }
        else
        {
            returnedSymbols = allSymbols;
            wasLimited = false;
        }

        // Generate insights
        var insights = GenerateInsights(allSymbols, totalSymbolCount);

        // Generate next actions
        var actions = GenerateNextActions(parameters.FilePath, allSymbols);

        // Add action to get more results if limited
        if (wasLimited)
        {
            actions.Insert(0, new AIAction
            {
                Action = ToolNames.DocumentSymbols,
                Description = "Get additional symbols",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = parameters.FilePath,
                    ["maxResults"] = Math.Min(totalSymbolCount, 500),
                    ["includePrivate"] = parameters.IncludePrivate
                },
                Priority = 90,
                Category = "pagination"
            });
        }

        // Generate distribution
        var distribution = GenerateDistribution(allSymbols);

        _logger.LogInformation("DocumentSymbols found {Count} symbols for {FilePath}", 
            totalSymbolCount, Path.GetFileName(parameters.FilePath));

        return new DocumentSymbolsToolResult
        {
            Success = true,
            Message = wasLimited 
                ? $"Found {totalSymbolCount} symbols - showing {returnedSymbols.Count}"
                : $"Found {totalSymbolCount} symbols in {Path.GetFileName(parameters.FilePath)}",
            Query = new DocumentSymbolsQuery
            {
                FilePath = parameters.FilePath,
                SymbolKinds = parameters.SymbolKinds?.ToList(),
                IncludePrivate = parameters.IncludePrivate,
                MaxResults = parameters.MaxResults
            },
            Summary = new DocumentSymbolsSummary
            {
                TotalFound = totalSymbolCount,
                Returned = returnedSymbols.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                TotalSymbols = totalSymbolCount,
                Hierarchical = true
            },
            Symbols = returnedSymbols,
            ResultsSummary = new ResultsSummary
            {
                Total = totalSymbolCount,
                Included = returnedSymbols.Count,
                HasMore = wasLimited
            },
            Distribution = distribution,
            Insights = insights,
            Actions = actions,
            Meta = new ToolExecutionMetadata 
            { 
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
            }
        };
    }

    private void ExtractSymbols(SyntaxNode node, List<DocumentSymbol> symbols, SemanticModel? semanticModel, bool includePrivate)
    {
        switch (node)
        {
            case CompilationUnitSyntax compilationUnit:
                // Process usings
                foreach (var usingDirective in compilationUnit.Usings)
                {
                    symbols.Add(CreateSymbolFromUsing(usingDirective));
                }
                
                // Process members
                foreach (var member in compilationUnit.Members)
                {
                    ExtractSymbols(member, symbols, semanticModel, includePrivate);
                }
                break;

            case NamespaceDeclarationSyntax namespaceDecl:
                var namespaceSymbol = CreateSymbolFromNamespace(namespaceDecl, semanticModel);
                symbols.Add(namespaceSymbol);
                
                // Process namespace members
                foreach (var member in namespaceDecl.Members)
                {
                    ExtractSymbols(member, namespaceSymbol.Children, semanticModel, includePrivate);
                }
                break;

            case FileScopedNamespaceDeclarationSyntax fileScopedNamespace:
                var fileScopedNamespaceSymbol = CreateSymbolFromFileScopedNamespace(fileScopedNamespace, semanticModel);
                symbols.Add(fileScopedNamespaceSymbol);
                
                // Process namespace members
                foreach (var member in fileScopedNamespace.Members)
                {
                    ExtractSymbols(member, fileScopedNamespaceSymbol.Children, semanticModel, includePrivate);
                }
                break;

            case TypeDeclarationSyntax typeDecl:
                if (!ShouldIncludeType(typeDecl, includePrivate)) break;
                
                var typeSymbol = CreateSymbolFromType(typeDecl, semanticModel);
                symbols.Add(typeSymbol);
                
                // Process type members
                foreach (var member in typeDecl.Members)
                {
                    ExtractMemberSymbols(member, typeSymbol.Children, semanticModel, includePrivate);
                }
                break;

            case EnumDeclarationSyntax enumDecl:
                if (!ShouldIncludeType(enumDecl, includePrivate)) break;
                
                var enumSymbol = CreateSymbolFromEnum(enumDecl, semanticModel);
                symbols.Add(enumSymbol);
                
                // Process enum members
                foreach (var member in enumDecl.Members)
                {
                    enumSymbol.Children.Add(CreateSymbolFromEnumMember(member, semanticModel));
                }
                break;

            case DelegateDeclarationSyntax delegateDecl:
                if (!ShouldIncludeMember(delegateDecl.Modifiers, includePrivate)) break;
                
                symbols.Add(CreateSymbolFromDelegate(delegateDecl, semanticModel));
                break;
        }
    }

    private void ExtractMemberSymbols(MemberDeclarationSyntax member, List<DocumentSymbol> symbols, SemanticModel? semanticModel, bool includePrivate)
    {
        switch (member)
        {
            case MethodDeclarationSyntax method:
                if (ShouldIncludeMember(method.Modifiers, includePrivate))
                {
                    symbols.Add(CreateSymbolFromMethod(method, semanticModel));
                }
                break;

            case PropertyDeclarationSyntax property:
                if (ShouldIncludeMember(property.Modifiers, includePrivate))
                {
                    symbols.Add(CreateSymbolFromProperty(property, semanticModel));
                }
                break;

            case FieldDeclarationSyntax field:
                if (ShouldIncludeMember(field.Modifiers, includePrivate))
                {
                    foreach (var variable in field.Declaration.Variables)
                    {
                        symbols.Add(CreateSymbolFromField(field, variable, semanticModel));
                    }
                }
                break;

            case EventDeclarationSyntax eventDecl:
                if (ShouldIncludeMember(eventDecl.Modifiers, includePrivate))
                {
                    symbols.Add(CreateSymbolFromEvent(eventDecl, semanticModel));
                }
                break;

            case ConstructorDeclarationSyntax constructor:
                if (ShouldIncludeMember(constructor.Modifiers, includePrivate))
                {
                    symbols.Add(CreateSymbolFromConstructor(constructor, semanticModel));
                }
                break;

            case TypeDeclarationSyntax nestedType:
                ExtractSymbols(nestedType, symbols, semanticModel, includePrivate);
                break;

            case EnumDeclarationSyntax nestedEnum:
                ExtractSymbols(nestedEnum, symbols, semanticModel, includePrivate);
                break;

            case DelegateDeclarationSyntax nestedDelegate:
                ExtractSymbols(nestedDelegate, symbols, semanticModel, includePrivate);
                break;
        }
    }

    private bool ShouldIncludeType(BaseTypeDeclarationSyntax typeDecl, bool includePrivate)
    {
        return includePrivate || !typeDecl.Modifiers.Any(SyntaxKind.PrivateKeyword);
    }

    private bool ShouldIncludeMember(SyntaxTokenList modifiers, bool includePrivate)
    {
        return includePrivate || !modifiers.Any(SyntaxKind.PrivateKeyword);
    }

    private DocumentSymbol CreateSymbolFromUsing(UsingDirectiveSyntax usingDirective)
    {
        return new DocumentSymbol
        {
            Name = usingDirective.Name?.ToString() ?? "",
            Kind = "Using",
            Location = GetLocation(usingDirective),
            Children = new List<DocumentSymbol>()
        };
    }

    private DocumentSymbol CreateSymbolFromNamespace(NamespaceDeclarationSyntax namespaceDecl, SemanticModel? semanticModel)
    {
        return new DocumentSymbol
        {
            Name = namespaceDecl.Name.ToString(),
            Kind = "Namespace",
            Location = GetLocation(namespaceDecl),
            Children = new List<DocumentSymbol>()
        };
    }

    private DocumentSymbol CreateSymbolFromFileScopedNamespace(FileScopedNamespaceDeclarationSyntax namespaceDecl, SemanticModel? semanticModel)
    {
        return new DocumentSymbol
        {
            Name = namespaceDecl.Name.ToString(),
            Kind = "Namespace",
            Location = GetLocation(namespaceDecl),
            Children = new List<DocumentSymbol>()
        };
    }

    private DocumentSymbol CreateSymbolFromType(TypeDeclarationSyntax typeDecl, SemanticModel? semanticModel)
    {
        var kind = typeDecl switch
        {
            ClassDeclarationSyntax => "Class",
            InterfaceDeclarationSyntax => "Interface",
            StructDeclarationSyntax => "Struct",
            RecordDeclarationSyntax => "Record",
            _ => "Type"
        };

        return new DocumentSymbol
        {
            Name = typeDecl.Identifier.Text,
            Kind = kind,
            Location = GetLocation(typeDecl),
            Modifiers = GetModifiers(typeDecl.Modifiers),
            TypeParameters = typeDecl.TypeParameterList?.Parameters.Select(p => p.Identifier.Text).ToList(),
            Children = new List<DocumentSymbol>()
        };
    }

    private DocumentSymbol CreateSymbolFromEnum(EnumDeclarationSyntax enumDecl, SemanticModel? semanticModel)
    {
        return new DocumentSymbol
        {
            Name = enumDecl.Identifier.Text,
            Kind = "Enum",
            Location = GetLocation(enumDecl),
            Modifiers = GetModifiers(enumDecl.Modifiers),
            Children = new List<DocumentSymbol>()
        };
    }

    private DocumentSymbol CreateSymbolFromEnumMember(EnumMemberDeclarationSyntax enumMember, SemanticModel? semanticModel)
    {
        return new DocumentSymbol
        {
            Name = enumMember.Identifier.Text,
            Kind = "EnumMember",
            Location = GetLocation(enumMember),
            Children = new List<DocumentSymbol>()
        };
    }

    private DocumentSymbol CreateSymbolFromDelegate(DelegateDeclarationSyntax delegateDecl, SemanticModel? semanticModel)
    {
        return new DocumentSymbol
        {
            Name = delegateDecl.Identifier.Text,
            Kind = "Delegate",
            Location = GetLocation(delegateDecl),
            Modifiers = GetModifiers(delegateDecl.Modifiers),
            TypeParameters = delegateDecl.TypeParameterList?.Parameters.Select(p => p.Identifier.Text).ToList(),
            Children = new List<DocumentSymbol>()
        };
    }

    private DocumentSymbol CreateSymbolFromMethod(MethodDeclarationSyntax method, SemanticModel? semanticModel)
    {
        return new DocumentSymbol
        {
            Name = method.Identifier.Text,
            Kind = "Method",
            Location = GetLocation(method),
            Modifiers = GetModifiers(method.Modifiers),
            TypeParameters = method.TypeParameterList?.Parameters.Select(p => p.Identifier.Text).ToList(),
            Parameters = method.ParameterList.Parameters.Select(p => p.ToString()).ToList(),
            ReturnType = method.ReturnType.ToString(),
            Children = new List<DocumentSymbol>()
        };
    }

    private DocumentSymbol CreateSymbolFromProperty(PropertyDeclarationSyntax property, SemanticModel? semanticModel)
    {
        return new DocumentSymbol
        {
            Name = property.Identifier.Text,
            Kind = "Property",
            Location = GetLocation(property),
            Modifiers = GetModifiers(property.Modifiers),
            ReturnType = property.Type.ToString(),
            Children = new List<DocumentSymbol>()
        };
    }

    private DocumentSymbol CreateSymbolFromField(FieldDeclarationSyntax field, VariableDeclaratorSyntax variable, SemanticModel? semanticModel)
    {
        return new DocumentSymbol
        {
            Name = variable.Identifier.Text,
            Kind = "Field",
            Location = GetLocation(variable),
            Modifiers = GetModifiers(field.Modifiers),
            ReturnType = field.Declaration.Type.ToString(),
            Children = new List<DocumentSymbol>()
        };
    }

    private DocumentSymbol CreateSymbolFromEvent(EventDeclarationSyntax eventDecl, SemanticModel? semanticModel)
    {
        return new DocumentSymbol
        {
            Name = eventDecl.Identifier.Text,
            Kind = "Event",
            Location = GetLocation(eventDecl),
            Modifiers = GetModifiers(eventDecl.Modifiers),
            ReturnType = eventDecl.Type.ToString(),
            Children = new List<DocumentSymbol>()
        };
    }

    private DocumentSymbol CreateSymbolFromConstructor(ConstructorDeclarationSyntax constructor, SemanticModel? semanticModel)
    {
        return new DocumentSymbol
        {
            Name = constructor.Identifier.Text,
            Kind = "Constructor",
            Location = GetLocation(constructor),
            Modifiers = GetModifiers(constructor.Modifiers),
            Parameters = constructor.ParameterList.Parameters.Select(p => p.ToString()).ToList(),
            Children = new List<DocumentSymbol>()
        };
    }

    private LocationInfo GetLocation(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return new LocationInfo
        {
            FilePath = span.Path,
            Line = span.StartLinePosition.Line + 1,
            Column = span.StartLinePosition.Character + 1,
            EndLine = span.EndLinePosition.Line + 1,
            EndColumn = span.EndLinePosition.Character + 1
        };
    }

    private List<string> GetModifiers(SyntaxTokenList modifiers)
    {
        return modifiers.Select(m => m.ValueText).ToList();
    }

    private List<DocumentSymbol> FilterSymbolsByKind(List<DocumentSymbol> symbols, string[] kinds)
    {
        var filtered = new List<DocumentSymbol>();
        
        foreach (var symbol in symbols)
        {
            if (kinds.Contains(symbol.Kind, StringComparer.OrdinalIgnoreCase))
            {
                filtered.Add(symbol);
            }
            else if (symbol.Children.Any())
            {
                var filteredChildren = FilterSymbolsByKind(symbol.Children, kinds);
                if (filteredChildren.Any())
                {
                    var copy = new DocumentSymbol
                    {
                        Name = symbol.Name,
                        Kind = symbol.Kind,
                        Location = symbol.Location,
                        Modifiers = symbol.Modifiers,
                        TypeParameters = symbol.TypeParameters,
                        Parameters = symbol.Parameters,
                        ReturnType = symbol.ReturnType,
                        Children = filteredChildren
                    };
                    filtered.Add(copy);
                }
            }
        }
        
        return filtered;
    }

    private int CountSymbols(List<DocumentSymbol> symbols)
    {
        return symbols.Count + symbols.Sum(s => CountSymbols(s.Children));
    }

    private List<string> GenerateInsights(List<DocumentSymbol> symbols, int totalCount)
    {
        var insights = new List<string>();

        // Count by type
        var typeCounts = new Dictionary<string, int>();
        CountSymbolTypes(symbols, typeCounts);
        
        if (typeCounts.Any())
        {
            var topTypes = typeCounts.OrderByDescending(kvp => kvp.Value).Take(3);
            insights.Add($"Contains {string.Join(", ", topTypes.Select(kvp => $"{kvp.Value} {kvp.Key.ToLower()}s"))}");
        }

        // Namespace structure
        var namespaces = symbols.Where(s => s.Kind == "Namespace").ToList();
        if (namespaces.Count > 1)
        {
            insights.Add($"Multiple namespaces defined ({namespaces.Count})");
        }

        // Class structure insights
        var classes = GetAllSymbolsOfKind(symbols, "Class");
        if (classes.Any())
        {
            var publicClasses = classes.Count(c => c.Modifiers?.Contains("public") == true);
            if (publicClasses > 0)
            {
                insights.Add($"{publicClasses} public class(es) exposed as API");
            }
        }

        // Method insights
        var methods = GetAllSymbolsOfKind(symbols, "Method");
        if (methods.Count > 10)
        {
            insights.Add($"Complex file with {methods.Count} methods - consider refactoring");
        }

        return insights;
    }

    private void CountSymbolTypes(List<DocumentSymbol> symbols, Dictionary<string, int> counts)
    {
        foreach (var symbol in symbols)
        {
            if (!counts.ContainsKey(symbol.Kind))
            {
                counts[symbol.Kind] = 0;
            }
            counts[symbol.Kind]++;
            
            CountSymbolTypes(symbol.Children, counts);
        }
    }

    private List<DocumentSymbol> GetAllSymbolsOfKind(List<DocumentSymbol> symbols, string kind)
    {
        var result = new List<DocumentSymbol>();
        
        foreach (var symbol in symbols)
        {
            if (symbol.Kind == kind)
            {
                result.Add(symbol);
            }
            result.AddRange(GetAllSymbolsOfKind(symbol.Children, kind));
        }
        
        return result;
    }

    private SymbolDistribution GenerateDistribution(List<DocumentSymbol> symbols)
    {
        var byKind = new Dictionary<string, int>();
        var byAccessibility = new Dictionary<string, int>();

        CountDistribution(symbols, byKind, byAccessibility);

        return new SymbolDistribution
        {
            ByKind = byKind.Any() ? byKind : null,
            ByAccessibility = byAccessibility.Any() ? byAccessibility : null
        };
    }

    private void CountDistribution(List<DocumentSymbol> symbols, Dictionary<string, int> byKind, Dictionary<string, int> byAccessibility)
    {
        foreach (var symbol in symbols)
        {
            // Count by kind
            if (!byKind.ContainsKey(symbol.Kind))
                byKind[symbol.Kind] = 0;
            byKind[symbol.Kind]++;

            // Count by accessibility (based on modifiers)
            var accessibility = GetAccessibilityFromModifiers(symbol.Modifiers);
            if (!byAccessibility.ContainsKey(accessibility))
                byAccessibility[accessibility] = 0;
            byAccessibility[accessibility]++;

            // Recursively count children
            CountDistribution(symbol.Children, byKind, byAccessibility);
        }
    }

    private string GetAccessibilityFromModifiers(List<string>? modifiers)
    {
        if (modifiers == null) return "internal";

        if (modifiers.Contains("public")) return "public";
        if (modifiers.Contains("private")) return "private";
        if (modifiers.Contains("protected")) return "protected";
        if (modifiers.Contains("internal")) return "internal";

        return "internal"; // Default C# accessibility
    }

    private List<AIAction> GenerateNextActions(string filePath, List<DocumentSymbol> symbols)
    {
        var actions = new List<AIAction>();

        // Suggest go to definition for key types
        var publicTypes = GetAllSymbolsOfKind(symbols, "Class")
            .Concat(GetAllSymbolsOfKind(symbols, "Interface"))
            .Where(s => s.Modifiers?.Contains("public") == true)
            .Take(3);

        foreach (var type in publicTypes)
        {
            if (type.Location != null)
            {
                actions.Add(new AIAction
                {
                    Action = ToolNames.GoToDefinition,
                    Description = $"Go to {type.Name} definition",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = type.Location.FilePath,
                        ["line"] = type.Location.Line,
                        ["column"] = type.Location.Column
                    },
                    Priority = 80,
                    Category = "navigation"
                });

                actions.Add(new AIAction
                {
                    Action = ToolNames.FindAllReferences,
                    Description = $"Find references to {type.Name}",
                    Parameters = new Dictionary<string, object>
                    {
                        ["filePath"] = type.Location.FilePath,
                        ["line"] = type.Location.Line,
                        ["column"] = type.Location.Column
                    },
                    Priority = 70,
                    Category = "navigation"
                });
            }
        }

        // Suggest symbol search for complex files
        if (CountSymbols(symbols) > 50)
        {
            actions.Add(new AIAction
            {
                Action = ToolNames.SymbolSearch,
                Description = "Search for specific symbols in solution",
                Parameters = new Dictionary<string, object>
                {
                    ["query"] = "*",
                    ["namespaceFilter"] = symbols.FirstOrDefault(s => s.Kind == "Namespace")?.Name ?? ""
                },
                Priority = 60,
                Category = "search"
            });
        }

        return actions;
    }
}

/// <summary>
/// Parameters for DocumentSymbols tool
/// </summary>
public class DocumentSymbolsParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("symbolKinds")]
    [COA.Mcp.Framework.Attributes.Description("Filter by symbol kinds: 'Class', 'Interface', 'Method', 'Property', 'Field', 'Event', 'Namespace', 'Struct', 'Enum', 'Delegate'")]
    public string[]? SymbolKinds { get; set; }

    [JsonPropertyName("includePrivate")]
    [COA.Mcp.Framework.Attributes.Description("Include private symbols (default: false)")]
    public bool IncludePrivate { get; set; } = false;
    
    [System.ComponentModel.DataAnnotations.Range(1, 500, ErrorMessage = "MaxResults must be between 1 and 500")]
    [JsonPropertyName("maxResults")]
    [COA.Mcp.Framework.Attributes.Description("Maximum number of symbols to return (default: 100, max: 500)")]
    public int MaxResults { get; set; } = 100;
}