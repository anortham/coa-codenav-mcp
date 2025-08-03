using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that extracts symbol hierarchy from a document
/// </summary>
[McpServerToolType]
public class DocumentSymbolsTool : ITool
{
    private readonly ILogger<DocumentSymbolsTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public string ToolName => "roslyn_document_symbols";
    public string Description => "Extract symbol hierarchy from a document";

    public DocumentSymbolsTool(
        ILogger<DocumentSymbolsTool> logger,
        RoslynWorkspaceService workspaceService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "roslyn_document_symbols")]
    [Description(@"Extract the symbol hierarchy from a C# document.
Returns: Hierarchical list of symbols with their locations, kinds, and modifiers.
Prerequisites: Call roslyn_load_solution or roslyn_load_project first.
Error handling: Returns specific error codes with recovery steps if document is not found.
Use cases: Document outline, navigation, understanding file structure, finding symbols in a file.
Not for: Cross-file symbol search (use roslyn_symbol_search), finding references (use roslyn_find_all_references).")]
    public async Task<object> ExecuteAsync(DocumentSymbolsParams parameters, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("DocumentSymbols request received: FilePath={FilePath}", parameters.FilePath);
            
        try
        {
            _logger.LogInformation("Processing DocumentSymbols for {FilePath}", parameters.FilePath);

            // Get the document
            _logger.LogDebug("Retrieving document from workspace: {FilePath}", parameters.FilePath);
            var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                _logger.LogWarning("Document not found in workspace: {FilePath}", parameters.FilePath);
                return new DocumentSymbolsResult
                {
                    Found = false,
                    FilePath = parameters.FilePath,
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
                            },
                            SuggestedActions = new List<SuggestedAction>
                            {
                                new SuggestedAction
                                {
                                    Tool = "roslyn_load_solution",
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
                return new DocumentSymbolsResult
                {
                    Found = false,
                    FilePath = parameters.FilePath,
                    Message = "Could not parse document syntax",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.COMPILATION_ERROR,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new List<string>
                            {
                                "Check for syntax errors in the file",
                                "Ensure the file is a valid C# source file",
                                "Try reloading the project"
                            }
                        }
                    }
                };
            }

            // Get semantic model if available
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

            // Get the root node
            var root = await syntaxTree.GetRootAsync(cancellationToken);

            // Extract all symbols
            var allSymbols = new List<DocumentSymbol>();
            ExtractSymbols(root, allSymbols, semanticModel, parameters.IncludePrivate ?? false);

            // Apply filters
            if (parameters.SymbolKinds?.Any() == true)
            {
                allSymbols = FilterSymbolsByKind(allSymbols, parameters.SymbolKinds);
            }

            // Apply token management
            var totalSymbolCount = CountSymbols(allSymbols);
            
            // Create token-aware response
            var response = TokenEstimator.CreateTokenAwareResponse(
                allSymbols,
                symbols => EstimateDocumentSymbolsTokens(symbols),
                requestedMax: parameters.MaxResults ?? 100, // Default to 100 symbols
                safetyLimit: TokenEstimator.DEFAULT_SAFETY_LIMIT,
                toolName: "roslyn_document_symbols"
            );

            // Generate insights (use all symbols for accurate insights)
            var insights = GenerateInsights(allSymbols);
            
            // Add truncation message if needed
            if (response.WasTruncated)
            {
                insights.Insert(0, response.GetTruncationMessage());
                if (response.SafetyLimitApplied)
                {
                    // When safety limit is applied, we might need to flatten the hierarchy
                    response.Items = FlattenSymbolHierarchy(response.Items, response.ReturnedCount);
                }
            }

            // Generate next actions
            var nextActions = GenerateNextActions(allSymbols, parameters.FilePath);
            
            // Add action to get more results if truncated
            if (response.WasTruncated)
            {
                nextActions.Insert(0, new NextAction
                {
                    Id = "get_more_symbols",
                    Description = "Get additional symbols",
                    ToolName = "roslyn_document_symbols",
                    Parameters = new
                    {
                        filePath = parameters.FilePath,
                        maxResults = Math.Min(totalSymbolCount, 500),
                        includePrivate = parameters.IncludePrivate
                    },
                    Priority = "high"
                });
            }

            // Store full result if truncated
            string? resourceUri = null;
            if (response.WasTruncated && _resourceProvider != null)
            {
                resourceUri = _resourceProvider.StoreAnalysisResult("document-symbols",
                    new { filePath = parameters.FilePath, symbols = allSymbols, totalCount = totalSymbolCount },
                    $"All {totalSymbolCount} symbols for {Path.GetFileName(parameters.FilePath)}");
            }

            return new DocumentSymbolsResult
            {
                Found = true,
                FilePath = parameters.FilePath,
                TotalSymbols = totalSymbolCount,
                Symbols = response.Items,
                Message = response.WasTruncated 
                    ? $"Found {totalSymbolCount} symbols - showing {response.ReturnedCount}"
                    : $"Found {totalSymbolCount} symbols in {Path.GetFileName(parameters.FilePath)}",
                Insights = insights,
                NextActions = nextActions,
                ResourceUri = resourceUri,
                Meta = new ToolMetadata
                {
                    ExecutionTime = "0ms", // TODO: Add timing
                    Truncated = response.WasTruncated,
                    Tokens = response.EstimatedTokens
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Document Symbols");
            return new DocumentSymbolsResult
            {
                Found = false,
                FilePath = parameters.FilePath,
                Message = $"Error: {ex.Message}",
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
                }
            };
        }
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

    private List<string> GenerateInsights(List<DocumentSymbol> symbols)
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

    private int EstimateDocumentSymbolsTokens(List<DocumentSymbol> symbols)
    {
        return TokenEstimator.EstimateCollection(
            symbols,
            symbol => EstimateSymbolTokens(symbol, includeChildren: true),
            baseTokens: TokenEstimator.BASE_RESPONSE_TOKENS
        );
    }
    
    private int EstimateSymbolTokens(DocumentSymbol symbol, bool includeChildren)
    {
        var tokens = TokenEstimator.Roslyn.EstimateDocumentSymbol(symbol, recursive: false);
        
        if (includeChildren && symbol.Children.Any())
        {
            tokens += symbol.Children.Sum(child => EstimateSymbolTokens(child, true));
        }
        
        return tokens;
    }
    
    private List<DocumentSymbol> FlattenSymbolHierarchy(List<DocumentSymbol> symbols, int maxCount)
    {
        var flattened = new List<DocumentSymbol>();
        var queue = new Queue<(DocumentSymbol symbol, int depth)>(symbols.Select(s => (s, 0)));
        
        while (queue.Count > 0 && flattened.Count < maxCount)
        {
            var (symbol, depth) = queue.Dequeue();
            
            // Create a copy without children for flattened view
            var flatSymbol = new DocumentSymbol
            {
                Name = depth > 0 ? new string(' ', depth * 2) + symbol.Name : symbol.Name,
                Kind = symbol.Kind,
                Location = symbol.Location,
                Modifiers = symbol.Modifiers,
                TypeParameters = symbol.TypeParameters,
                Parameters = symbol.Parameters,
                ReturnType = symbol.ReturnType,
                Children = new List<DocumentSymbol>() // Empty children in flattened view
            };
            
            flattened.Add(flatSymbol);
            
            // Add children to queue for processing
            foreach (var child in symbol.Children)
            {
                queue.Enqueue((child, depth + 1));
            }
        }
        
        return flattened;
    }

    private List<NextAction> GenerateNextActions(List<DocumentSymbol> symbols, string filePath)
    {
        var actions = new List<NextAction>();

        // Suggest go to definition for key types
        var publicTypes = GetAllSymbolsOfKind(symbols, "Class")
            .Concat(GetAllSymbolsOfKind(symbols, "Interface"))
            .Where(s => s.Modifiers?.Contains("public") == true)
            .Take(3);

        foreach (var type in publicTypes)
        {
            if (type.Location != null)
            {
                actions.Add(new NextAction
                {
                    Id = $"goto_{type.Name.ToLower()}",
                    Description = $"Go to {type.Name} definition",
                    ToolName = "roslyn_goto_definition",
                    Parameters = new
                    {
                        filePath = type.Location.FilePath,
                        line = type.Location.Line,
                        column = type.Location.Column
                    },
                    Priority = "medium"
                });

                actions.Add(new NextAction
                {
                    Id = $"find_refs_{type.Name.ToLower()}",
                    Description = $"Find references to {type.Name}",
                    ToolName = "roslyn_find_all_references",
                    Parameters = new
                    {
                        filePath = type.Location.FilePath,
                        line = type.Location.Line,
                        column = type.Location.Column
                    },
                    Priority = "low"
                });
            }
        }

        // Suggest symbol search for complex files
        if (CountSymbols(symbols) > 50)
        {
            actions.Add(new NextAction
            {
                Id = "search_symbols",
                Description = "Search for specific symbols in solution",
                ToolName = "roslyn_symbol_search",
                Parameters = new
                {
                    query = "*",
                    namespaceFilter = symbols.FirstOrDefault(s => s.Kind == "Namespace")?.Name
                },
                Priority = "low"
            });
        }

        return actions;
    }
}

public class DocumentSymbolsParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file")]
    public required string FilePath { get; set; }

    [JsonPropertyName("symbolKinds")]
    [Description("Filter by symbol kinds: 'Class', 'Interface', 'Method', 'Property', 'Field', 'Event', 'Namespace', 'Struct', 'Enum', 'Delegate'")]
    public string[]? SymbolKinds { get; set; }

    [JsonPropertyName("includePrivate")]
    [Description("Include private symbols (default: false)")]
    public bool? IncludePrivate { get; set; }
    
    [JsonPropertyName("maxResults")]
    [Description("Maximum number of symbols to return (default: 100, max: 500)")]
    public int? MaxResults { get; set; }
}

public class DocumentSymbolsResult
{
    public bool Found { get; set; }
    public required string FilePath { get; set; }
    public int TotalSymbols { get; set; }
    public List<DocumentSymbol>? Symbols { get; set; }
    public string? Message { get; set; }
    public List<string>? Insights { get; set; }
    public List<NextAction>? NextActions { get; set; }
    public ErrorInfo? Error { get; set; }
    public string? ResourceUri { get; set; }
    public ToolMetadata? Meta { get; set; }
}

public class DocumentSymbol
{
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public LocationInfo? Location { get; set; }
    public List<string>? Modifiers { get; set; }
    public List<string>? TypeParameters { get; set; }
    public List<string>? Parameters { get; set; }
    public string? ReturnType { get; set; }
    public List<DocumentSymbol> Children { get; set; } = new();
}