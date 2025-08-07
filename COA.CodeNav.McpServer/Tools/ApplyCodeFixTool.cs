using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that provides code fix functionality using Roslyn
/// </summary>
public class ApplyCodeFixTool : McpToolBase<ApplyCodeFixParams, ApplyCodeFixToolResult>
{
    private readonly ILogger<ApplyCodeFixTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly CodeFixService _codeFixService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => ToolNames.ApplyCodeFix;
    public override string Description => "Apply a code fix for a diagnostic at a specific location.";
    
    public ApplyCodeFixTool(
        ILogger<ApplyCodeFixTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        CodeFixService codeFixService,
        AnalysisResultResourceProvider? resourceProvider = null)
        : base(logger)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _codeFixService = codeFixService;
        _resourceProvider = resourceProvider;
    }

    protected override async Task<ApplyCodeFixToolResult> ExecuteInternalAsync(
        ApplyCodeFixParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("ApplyCodeFix request received: FilePath={FilePath}, Line={Line}, Column={Column}", 
            parameters.FilePath, parameters.Line, parameters.Column);
        
        var startTime = DateTime.UtcNow;

        // Get the document
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            return new ApplyCodeFixToolResult
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

        // Get semantic model
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return new ApplyCodeFixToolResult
            {
                Success = false,
                Message = "Failed to get semantic model",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.ANALYSIS_FAILED,
                    Message = "Failed to get semantic model",
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

        // Convert line/column to position
        var text = await document.GetTextAsync(cancellationToken);
        var position = text.Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(parameters.Line - 1, parameters.Column - 1));
        var span = new Microsoft.CodeAnalysis.Text.TextSpan(position, 0);

        // Get diagnostics at this position
        var diagnostics = await GetDiagnosticsAtSpanAsync(document, span, cancellationToken);
        
        // Filter to specific diagnostic if provided
        if (!string.IsNullOrEmpty(parameters.DiagnosticId))
        {
            var filtered = diagnostics.Where(d => d.Id == parameters.DiagnosticId).ToList();
            if (!filtered.Any())
            {
                return new ApplyCodeFixToolResult
                {
                    Success = false,
                    Message = $"No diagnostic with ID '{parameters.DiagnosticId}' found at the specified location",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.DIAGNOSTIC_NOT_FOUND,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new string[]
                            {
                                "Run csharp_get_diagnostics to see available diagnostics",
                                "Verify the diagnostic ID is correct",
                                "Check if the diagnostic still exists at this location"
                            }
                        }
                    },
                    AvailableDiagnostics = diagnostics.Select(d => new CodeFixDiagnosticInfo
                    {
                        Id = d.Id,
                        Message = d.GetMessage(),
                        Severity = d.Severity.ToString()
                    }).ToList()
                };
            }
            diagnostics = filtered;
        }

        if (!diagnostics.Any())
        {
            return new ApplyCodeFixToolResult
            {
                Success = false,
                Message = "No diagnostics found at the specified location",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.DIAGNOSTIC_NOT_FOUND,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new string[]
                        {
                            "Run csharp_get_diagnostics to find diagnostics in the file",
                            "Check if the line and column are correct",
                            "Ensure there are actually issues to fix at this location"
                        }
                    }
                }
            };
        }

        // Get code fixes for the diagnostics
        var fixes = await _codeFixService.GetCodeFixesAsync(document, diagnostics, cancellationToken);
        if (!fixes.Any())
        {
            return new ApplyCodeFixToolResult
            {
                Success = false,
                Message = "No code fixes available for the diagnostic(s) at this location",
                Error = new ErrorInfo
                {
                    Code = ErrorCodes.NO_CODE_FIXES_AVAILABLE,
                    Recovery = new RecoveryInfo
                    {
                        Steps = new string[]
                        {
                            "Some diagnostics don't have automatic fixes",
                            "Try fixing the issue manually",
                            "Check if a different diagnostic at this location has fixes"
                        }
                    }
                }
            };
        }

        // Select the fix to apply
        CodeAction? selectedFix = null;
        string diagnosticId = "";
        
        if (!string.IsNullOrEmpty(parameters.FixTitle))
        {
            var match = fixes.FirstOrDefault(f => f.action.Title == parameters.FixTitle);
            if (match.action == null)
            {
                return new ApplyCodeFixToolResult
                {
                    Success = false,
                    Message = $"No fix found with title '{parameters.FixTitle}'",
                    AvailableFixes = fixes.Select(f => new CodeFixInfo
                    {
                        Title = f.action.Title,
                        DiagnosticId = f.diagnosticId,
                        Category = f.action.EquivalenceKey
                    }).ToList(),
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.FIX_NOT_FOUND,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new string[]
                            {
                                "Choose one of the available fixes listed",
                                "Or omit fixTitle to apply the first available fix"
                            }
                        }
                    }
                };
            }
            selectedFix = match.action;
            diagnosticId = match.diagnosticId;
        }
        else
        {
            // Use first fix if not specified
            var first = fixes.First();
            selectedFix = first.action;
            diagnosticId = first.diagnosticId;
        }

        // Apply the fix
        var operations = await selectedFix.GetOperationsAsync(cancellationToken);
        var workspace = document.Project.Solution.Workspace;
        var solution = document.Project.Solution;
        var changes = new List<FileChange>();

        foreach (var operation in operations)
        {
            if (operation is ApplyChangesOperation applyChangesOp)
            {
                var changedSolution = applyChangesOp.ChangedSolution;
                var solutionChanges = changedSolution.GetChanges(solution);

                foreach (var projectChanges in solutionChanges.GetProjectChanges())
                {
                    foreach (var changedDocId in projectChanges.GetChangedDocuments())
                    {
                        var oldDoc = solution.GetDocument(changedDocId);
                        var newDoc = changedSolution.GetDocument(changedDocId);
                        
                        if (oldDoc != null && newDoc != null)
                        {
                            var oldText = await oldDoc.GetTextAsync(cancellationToken);
                            var newText = await newDoc.GetTextAsync(cancellationToken);
                            var textChanges = newText.GetTextChanges(oldText);

                            if (textChanges.Any())
                            {
                                changes.Add(new FileChange
                                {
                                    FilePath = oldDoc.FilePath ?? "",
                                    Changes = textChanges.Select(tc => new Models.TextChange
                                    {
                                        Span = new Models.TextSpan
                                        {
                                            Start = tc.Span.Start,
                                            End = tc.Span.End
                                        },
                                        NewText = tc.NewText ?? ""
                                    }).ToList()
                                });
                            }
                        }
                    }
                }

                // Apply changes if not in preview mode
                if (!parameters.Preview)
                {
                    workspace.TryApplyChanges(changedSolution);
                }
            }
        }

        // Generate insights
        var insights = GenerateInsights(selectedFix, changes);

        // Generate next actions
        var actions = GenerateNextActions(parameters, changes);

        return new ApplyCodeFixToolResult
        {
            Success = true,
            Message = parameters.Preview 
                ? $"Preview: '{selectedFix.Title}' would be applied"
                : $"Successfully applied fix: '{selectedFix.Title}'",
            FixTitle = selectedFix.Title,
            DiagnosticId = diagnosticId,
            AppliedChanges = changes,
            AllFilesSucceeded = true,
            Insights = insights,
            Actions = actions,
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
    }

    private async Task<List<Diagnostic>> GetDiagnosticsAtSpanAsync(
        Document document, 
        Microsoft.CodeAnalysis.Text.TextSpan span, 
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();

        // Get compiler diagnostics
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel != null)
        {
            var compilationDiagnostics = semanticModel.GetDiagnostics(span, cancellationToken);
            diagnostics.AddRange(compilationDiagnostics);
        }

        // Get syntax diagnostics
        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (syntaxTree != null)
        {
            var syntaxDiagnostics = syntaxTree.GetDiagnostics(cancellationToken);
            diagnostics.AddRange(syntaxDiagnostics.Where(d => d.Location.SourceSpan.IntersectsWith(span)));
        }

        // Note: Analyzer diagnostics require additional setup that's complex for this migration
        // For now, we'll rely on compiler and syntax diagnostics

        return diagnostics;
    }

    private List<string> GenerateInsights(CodeAction codeAction, List<FileChange> changes)
    {
        var insights = new List<string>();
        
        insights.Add($"Applied fix: '{codeAction.Title}'");

        var totalChanges = changes.Sum(c => c.Changes.Count);
        insights.Add($"Made {totalChanges} text changes across {changes.Count} file(s)");

        if (changes.Count > 1)
        {
            insights.Add("This fix affected multiple files in the solution");
        }

        if (codeAction.EquivalenceKey != null)
        {
            insights.Add($"Fix category: {codeAction.EquivalenceKey}");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(ApplyCodeFixParams parameters, List<FileChange> changes)
    {
        var actions = new List<AIAction>();
        
        // Re-run diagnostics to see if there are more issues
        actions.Add(new AIAction
        {
            Action = ToolNames.GetDiagnostics,
            Description = "Check for remaining diagnostics in the file",
            Parameters = new Dictionary<string, object>
            {
                ["filePath"] = parameters.FilePath,
                ["scope"] = "file"
            },
            Priority = 90,
            Category = "validation"
        });

        // Format the document after applying fixes
        if (changes.Any())
        {
            actions.Add(new AIAction
            {
                Action = ToolNames.FormatDocument,
                Description = "Format the modified file(s)",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = parameters.FilePath
                },
                Priority = 70,
                Category = "formatting"
            });
        }

        return actions;
    }
    
    protected override int EstimateTokenUsage()
    {
        // Estimate for typical code fix response
        return 3000;
    }
}

/// <summary>
/// Parameters for ApplyCodeFix tool
/// </summary>
public class ApplyCodeFixParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file")]
    public string FilePath { get; set; } = string.Empty;

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Line must be positive")]
    [JsonPropertyName("line")]
    [COA.Mcp.Framework.Attributes.Description("Line number (1-based) where the diagnostic is located")]
    public int Line { get; set; }

    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "Column must be positive")]
    [JsonPropertyName("column")]
    [COA.Mcp.Framework.Attributes.Description("Column number (1-based) where the diagnostic is located")]
    public int Column { get; set; }

    [JsonPropertyName("diagnosticId")]
    [COA.Mcp.Framework.Attributes.Description("Optional: Specific diagnostic ID to fix (e.g., 'CS0219'). If not provided, applies first available fix.")]
    public string? DiagnosticId { get; set; }

    [JsonPropertyName("fixTitle")]
    [COA.Mcp.Framework.Attributes.Description("Optional: Specific fix title to apply. If not provided, applies first available fix.")]
    public string? FixTitle { get; set; }

    [JsonPropertyName("preview")]
    [COA.Mcp.Framework.Attributes.Description("Preview changes without applying (default: false)")]
    public bool Preview { get; set; } = false;
    
    [JsonPropertyName("maxChangedFiles")]
    [COA.Mcp.Framework.Attributes.Description("Maximum number of changed files to return in preview (default: 50)")]
    public int? MaxChangedFiles { get; set; }
}

public class ApplyCodeFixToolResult : ToolResultBase
{
    public override string Operation => ToolNames.ApplyCodeFix;

    [JsonPropertyName("fixTitle")]
    public string? FixTitle { get; set; }

    [JsonPropertyName("diagnosticId")]
    public string? DiagnosticId { get; set; }

    [JsonPropertyName("appliedChanges")]
    public List<FileChange>? AppliedChanges { get; set; }

    [JsonPropertyName("allFilesSucceeded")]
    public bool AllFilesSucceeded { get; set; }

    [JsonPropertyName("availableFixes")]
    public List<CodeFixInfo>? AvailableFixes { get; set; }

    [JsonPropertyName("availableDiagnostics")]
    public List<CodeFixDiagnosticInfo>? AvailableDiagnostics { get; set; }
}

public class CodeFixInfo
{
    [JsonPropertyName("title")]
    public required string Title { get; set; }

    [JsonPropertyName("diagnosticId")]
    public required string DiagnosticId { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }
}

public class CodeFixDiagnosticInfo
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }

    [JsonPropertyName("severity")]
    public required string Severity { get; set; }
}
