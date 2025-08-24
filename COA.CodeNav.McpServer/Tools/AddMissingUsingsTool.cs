using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that provides add missing usings functionality using Roslyn
/// </summary>
public class AddMissingUsingsTool : McpToolBase<AddMissingUsingsParams, AddMissingUsingsResult>
{
    private readonly ILogger<AddMissingUsingsTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly CodeFixService _codeFixService;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => ToolNames.AddMissingUsings;
    public override string Description => "Automatically add missing using statements for unresolved types. Fixes 'type not found' errors by importing the correct namespaces.";
    
    public AddMissingUsingsTool(
        IServiceProvider serviceProvider,
        ILogger<AddMissingUsingsTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        CodeFixService codeFixService,
        ITokenEstimator tokenEstimator,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(serviceProvider, logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _tokenEstimator = tokenEstimator;
        _codeFixService = codeFixService;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<AddMissingUsingsResult> ExecuteInternalAsync(
        AddMissingUsingsParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("AddMissingUsings request received: FilePath={FilePath}, Preview={Preview}", 
            parameters.FilePath, parameters.Preview);
        
        var startTime = DateTime.UtcNow;

        // Get the document
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            return new AddMissingUsingsResult
            {
                Success = false,
                Message = $"Document not found: {parameters.FilePath}",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.DOCUMENT_NOT_FOUND,
                    Message = $"Document not found: {parameters.FilePath}",
                    Recovery = new RecoveryInfo
                    {
                        Steps = new string[]
                        {
                            "Ensure the file path is correct and absolute",
                            "Verify the file exists in the loaded solution/project",
                            "Load a solution using csharp_load_solution or project using csharp_load_project"
                        }
                    }
                }
            };
        }

        // Get diagnostics for the document
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (compilation == null)
        {
            return new AddMissingUsingsResult
            {
                Success = false,
                Message = "Failed to get compilation",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.ANALYSIS_FAILED,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new string[]
                        {
                            "Try reloading the solution",
                            "Check if the project builds successfully",
                            "Ensure all project dependencies are restored"
                        }
                    }
                }
            };
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return new AddMissingUsingsResult
            {
                Success = false,
                Message = "Failed to get semantic model",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.ANALYSIS_FAILED,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new string[]
                        {
                            "Try reloading the solution",
                            "Check if the document has compilation errors",
                            "Ensure the project builds successfully"
                        }
                    }
                }
            };
        }

        // Get unresolved type diagnostics
        var diagnostics = semanticModel.GetDiagnostics();
        var unresolvedTypeDiagnostics = diagnostics
            .Where(d => IsUnresolvedTypeDiagnostic(d))
            .ToList();

        if (!unresolvedTypeDiagnostics.Any())
        {
            return new AddMissingUsingsResult
            {
                Success = true,
                Message = "No unresolved types found in the document",
                Query = CreateQueryInfo(parameters),
                Summary = new SummaryInfo
                {
                    TotalFound = 0,
                    Returned = 0,
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                },
                AddedUsings = new List<string>(),
                UnresolvedTypes = new List<UnresolvedTypeInfo>(),
                Insights = new List<string>
                {
                    "All types are properly resolved",
                    "No using directives need to be added"
                },
                Actions = new List<AIAction>(),
                Meta = new ToolExecutionMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                }
            };
        }

        // Get the document with applied fixes
        var updatedDocument = document;
        var addedUsings = new List<string>();
        var unresolvedTypes = new List<UnresolvedTypeInfo>();
        var fixedDiagnostics = new HashSet<string>();

        // Group diagnostics by their error code
        var diagnosticGroups = unresolvedTypeDiagnostics
            .GroupBy(d => d.Id)
            .ToList();

        foreach (var group in diagnosticGroups)
        {
            foreach (var diagnostic in group)
            {
                // Skip if we've already fixed a diagnostic at this location
                var diagnosticKey = $"{diagnostic.Id}:{diagnostic.Location.SourceSpan.Start}";
                if (fixedDiagnostics.Contains(diagnosticKey))
                    continue;

                // Get code fixes for this diagnostic
                var fixes = await _codeFixService.GetCodeFixesAsync(
                    updatedDocument,
                    new[] { diagnostic },
                    cancellationToken);

                // Find "using" fixes
                var usingFix = fixes
                    .Where(f => IsAddUsingFix(f.action))
                    .Select(f => f.action)
                    .FirstOrDefault();

                if (usingFix != null)
                {
                    // Apply the fix if not in preview mode
                    if (!parameters.Preview)
                    {
                        var operations = await usingFix.GetOperationsAsync(cancellationToken);
                        var applyOperation = operations.OfType<ApplyChangesOperation>().FirstOrDefault();

                        if (applyOperation != null)
                        {
                            var changedSolution = applyOperation.ChangedSolution;
                            updatedDocument = changedSolution.GetDocument(updatedDocument.Id) ?? updatedDocument;
                        }
                    }

                    // Extract the using directive that was added
                    var addedUsing = ExtractAddedUsing(usingFix.Title);
                    if (!string.IsNullOrEmpty(addedUsing) && !addedUsings.Contains(addedUsing))
                    {
                        addedUsings.Add(addedUsing);
                    }

                    fixedDiagnostics.Add(diagnosticKey);
                }
                else
                {
                    // Track unresolved types that couldn't be fixed
                    var typeName = ExtractTypeNameFromDiagnostic(diagnostic, semanticModel);
                    if (!string.IsNullOrEmpty(typeName))
                    {
                        unresolvedTypes.Add(new UnresolvedTypeInfo
                        {
                            TypeName = typeName,
                            Line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1,
                            Column = diagnostic.Location.GetLineSpan().StartLinePosition.Character + 1,
                            DiagnosticId = diagnostic.Id,
                            Message = diagnostic.GetMessage()
                        });
                    }
                }
            }
        }

        // Get the updated text
        var updatedText = await updatedDocument.GetTextAsync(cancellationToken);
        var code = parameters.Preview ? null : updatedText.ToString();

        // Generate insights
        var insights = GenerateInsights(addedUsings, unresolvedTypes);

        // Generate next actions
        var actions = GenerateNextActions(parameters, unresolvedTypes);

        return new AddMissingUsingsResult
        {
            Success = true,
            Message = addedUsings.Any() 
                ? $"Added {addedUsings.Count} using directives" 
                : "No using directives could be added automatically",
            Query = CreateQueryInfo(parameters),
            Summary = new SummaryInfo
            {
                TotalFound = unresolvedTypeDiagnostics.Count,
                Returned = addedUsings.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            },
            Code = code,
            AddedUsings = addedUsings,
            UnresolvedTypes = unresolvedTypes,
            Insights = insights,
            Actions = actions,
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
    }

    private bool IsUnresolvedTypeDiagnostic(Diagnostic diagnostic)
    {
        // Common diagnostics for unresolved types
        return diagnostic.Id switch
        {
            "CS0246" => true, // The type or namespace name could not be found
            "CS0103" => true, // The name does not exist in the current context
            "CS0234" => true, // The type or namespace name does not exist in the namespace
            "CS1061" => false, // Type does not contain a definition for member (not a using issue)
            _ => false
        };
    }

    private bool IsAddUsingFix(CodeAction codeAction)
    {
        var title = codeAction.Title.ToLowerInvariant();
        return title.Contains("using") && 
               (title.Contains("add") || title.Contains("import") || title.StartsWith("using"));
    }

    private string ExtractAddedUsing(string codeActionTitle)
    {
        // Extract namespace from titles like:
        // "using System.Linq;"
        // "Add using System.Collections.Generic"
        // "using System.Threading.Tasks"
        
        if (codeActionTitle.StartsWith("using "))
        {
            return codeActionTitle.TrimEnd(';').Trim();
        }

        var parts = codeActionTitle.Split(' ');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("using", StringComparison.OrdinalIgnoreCase))
            {
                var namespaceName = string.Join(" ", parts.Skip(i + 1)).TrimEnd(';');
                return $"using {namespaceName}";
            }
        }

        return string.Empty;
    }

    private string ExtractTypeNameFromDiagnostic(Diagnostic diagnostic, SemanticModel semanticModel)
    {
        try
        {
            var root = semanticModel.SyntaxTree.GetRoot();
            var node = root.FindNode(diagnostic.Location.SourceSpan);

            // Try to find identifier name
            if (node is IdentifierNameSyntax identifier)
            {
                return identifier.Identifier.Text;
            }

            // Try to find qualified name
            if (node is QualifiedNameSyntax qualified)
            {
                return qualified.ToString();
            }

            // Extract from diagnostic message as fallback
            var message = diagnostic.GetMessage();
            var match = System.Text.RegularExpressions.Regex.Match(message, @"'([^']+)'");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        catch
        {
            // Fallback to empty if extraction fails
        }

        return string.Empty;
    }

    private List<string> GenerateInsights(List<string> addedUsings, List<UnresolvedTypeInfo> unresolvedTypes)
    {
        var insights = new List<string>();

        if (addedUsings.Any())
        {
            insights.Add($"Successfully added {addedUsings.Count} using directives");
            
            // Group by common namespaces
            var commonNamespaces = addedUsings
                .Where(u => u.StartsWith("using System."))
                .Count();
            
            if (commonNamespaces > 0)
            {
                insights.Add($"Added {commonNamespaces} System namespace imports");
            }

            var otherNamespaces = addedUsings.Count - commonNamespaces;
            if (otherNamespaces > 0)
            {
                insights.Add($"Added {otherNamespaces} project/package namespace imports");
            }
        }

        if (unresolvedTypes.Any())
        {
            insights.Add($"{unresolvedTypes.Count} types remain unresolved");
            
            var uniqueTypes = unresolvedTypes.Select(t => t.TypeName).Distinct().Count();
            if (uniqueTypes < unresolvedTypes.Count)
            {
                insights.Add($"{uniqueTypes} unique unresolved types found");
            }
        }

        if (!addedUsings.Any() && !unresolvedTypes.Any())
        {
            insights.Add("All types are properly resolved");
        }

        insights.Add("Consider using global usings for commonly used namespaces");

        return insights;
    }

    private List<AIAction> GenerateNextActions(AddMissingUsingsParams parameters, List<UnresolvedTypeInfo> unresolvedTypes)
    {
        var actions = new List<AIAction>();

        if (unresolvedTypes.Any())
        {
            var firstUnresolved = unresolvedTypes.First();
            actions.Add(new AIAction
            {
                Action = ToolNames.SymbolSearch,
                Description = $"Search for type '{firstUnresolved.TypeName}' in solution",
                Parameters = new Dictionary<string, object> { ["query"] = firstUnresolved.TypeName },
                Priority = 70,
                Category = "search"
            });
        }

        actions.Add(new AIAction
        {
            Action = ToolNames.FormatDocument,
            Description = "Format document to organize usings",
            Parameters = new Dictionary<string, object> { ["filePath"] = parameters.FilePath },
            Priority = 50,
            Category = "formatting"
        });

        actions.Add(new AIAction
        {
            Action = ToolNames.GetDiagnostics,
            Description = "Check for remaining compilation errors",
            Parameters = new Dictionary<string, object> { ["filePath"] = parameters.FilePath },
            Priority = 70,
            Category = "validation"
        });

        return actions;
    }

    private QueryInfo CreateQueryInfo(AddMissingUsingsParams parameters)
    {
        return new QueryInfo
        {
            FilePath = parameters.FilePath
        };
    }
    
}

public class AddMissingUsingsParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("preview")]
    [COA.Mcp.Framework.Attributes.Description("Preview changes without applying (default: false)")]
    public bool Preview { get; set; } = false;
}

public class AddMissingUsingsResult : ToolResultBase
{
    public override string Operation => ToolNames.AddMissingUsings;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("addedUsings")]
    public List<string>? AddedUsings { get; set; }

    [JsonPropertyName("unresolvedTypes")]
    public List<UnresolvedTypeInfo>? UnresolvedTypes { get; set; }
}

public class UnresolvedTypeInfo
{
    [JsonPropertyName("typeName")]
    public required string TypeName { get; set; }

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")] 
    public int Column { get; set; }

    [JsonPropertyName("diagnosticId")]
    public required string DiagnosticId { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }
}
