using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

[McpServerToolType]
public class ApplyCodeFixTool
{
    private readonly ILogger<ApplyCodeFixTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly CodeFixService _codeFixService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public ApplyCodeFixTool(
        ILogger<ApplyCodeFixTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        CodeFixService codeFixService,
        AnalysisResultResourceProvider? resourceProvider = null)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _codeFixService = codeFixService;
        _resourceProvider = resourceProvider;
    }

    [McpServerTool(Name = "csharp_apply_code_fix")]
    [Description(@"Apply a code fix for a diagnostic at a specific location.
Returns: Modified code with the fix applied, affected files list.
Prerequisites: Call csharp_get_diagnostics first to get available fixes.
Error handling: Returns specific error codes with recovery steps if fix cannot be applied.
Use cases: Fix compilation errors, apply analyzer suggestions, resolve warnings.
Not for: Manual code edits (use other tools), refactorings without diagnostics.")]
    public async Task<object> ExecuteAsync(ApplyCodeFixParams parameters, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            // Get the document
            var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                return CreateErrorResult(
                    ErrorCodes.DOCUMENT_NOT_FOUND, 
                    $"Document not found: {parameters.FilePath}",
                    new List<string>
                    {
                        "Ensure the file path is correct and absolute",
                        "Verify the file exists in the loaded solution/project",
                        "Load a solution using csharp_load_solution or project using csharp_load_project"
                    },
                    parameters,
                    startTime);
            }

            // Get semantic model
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                return CreateErrorResult(
                    ErrorCodes.ANALYSIS_FAILED,
                    "Failed to get semantic model",
                    new List<string>
                    {
                        "Try reloading the solution",
                        "Check if the document has compilation errors",
                        "Ensure the project builds successfully"
                    },
                    parameters,
                    startTime);
            }

            // Convert line/column to position
            var text = await document.GetTextAsync(cancellationToken);
            var position = text.Lines.GetPosition(new LinePosition(parameters.Line - 1, parameters.Column - 1));
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
                                Steps = new List<string>
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
                        }).ToList(),
                        Meta = new ToolMetadata 
                        { 
                            ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
                        }
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
                            Steps = new List<string>
                            {
                                "Run csharp_get_diagnostics to find diagnostics in the file",
                                "Check if the line and column are correct",
                                "Ensure there are actually issues to fix at this location"
                            }
                        }
                    },
                    Meta = new ToolMetadata 
                    { 
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
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
                            Steps = new List<string>
                            {
                                "Some diagnostics don't have automatic fixes",
                                "Try fixing the issue manually",
                                "Check if a different diagnostic at this location has fixes"
                            }
                        }
                    },
                    Meta = new ToolMetadata 
                    { 
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
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
                                Steps = new List<string>
                                {
                                    "Choose one of the available fixes listed",
                                    "Or omit fixTitle to apply the first available fix"
                                }
                            }
                        },
                        Meta = new ToolMetadata 
                        { 
                            ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
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

            // Apply token management for preview mode
            var displayChanges = changes;
            bool wasTruncated = false;
            
            if (parameters.Preview && changes.Count > 0)
            {
                var estimatedTokens = EstimateFileChangesTokens(changes);
                
                if (estimatedTokens > COA.CodeNav.McpServer.Utilities.TokenEstimator.DEFAULT_SAFETY_LIMIT)
                {
                    var response = COA.CodeNav.McpServer.Utilities.TokenEstimator.CreateTokenAwareResponse(
                        changes,
                        changesSubset => EstimateFileChangesTokens(changesSubset),
                        requestedMax: parameters.MaxChangedFiles ?? 50,
                        safetyLimit: COA.CodeNav.McpServer.Utilities.TokenEstimator.DEFAULT_SAFETY_LIMIT,
                        toolName: "csharp_apply_code_fix"
                    );
                    
                    displayChanges = response.Items;
                    wasTruncated = response.WasTruncated;
                }
            }
            
            var result = new ApplyCodeFixToolResult
            {
                Success = true,
                Message = parameters.Preview 
                    ? $"Preview: '{selectedFix.Title}' would be applied"
                    : $"Successfully applied fix: '{selectedFix.Title}'",
                FixTitle = selectedFix.Title,
                DiagnosticId = diagnosticId,
                AppliedChanges = parameters.Preview ? displayChanges : changes,
                AllFilesSucceeded = true,
                Insights = GenerateInsights(selectedFix, displayChanges, wasTruncated, changes.Count),
                Actions = GenerateNextActions(parameters, displayChanges, changes),
                Meta = new ToolMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                    Truncated = wasTruncated
                }
            };

            if (changes.Count > 10 && _resourceProvider != null)
            {
                var resourceUri = _resourceProvider.StoreAnalysisResult(
                    "apply-code-fix",
                    result,
                    $"Code fix results for {selectedFix.Title}"
                );
                result.ResourceUri = resourceUri;
                result.Meta.Truncated = true;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying code fix");
            return CreateErrorResult(
                ErrorCodes.INTERNAL_ERROR,
                $"Error applying code fix: {ex.Message}",
                new List<string>
                {
                    "Check the server logs for detailed error information",
                    "Verify the solution/project is loaded correctly",
                    "Try the operation again"
                },
                parameters,
                startTime);
        }
    }

    private ApplyCodeFixToolResult CreateErrorResult(
        string errorCode, 
        string message, 
        List<string> recoverySteps,
        ApplyCodeFixParams parameters,
        DateTime startTime)
    {
        return new ApplyCodeFixToolResult
        {
            Success = false,
            Message = message,
            Error = new ErrorInfo
            {
                Code = errorCode,
                Recovery = new RecoveryInfo
                {
                    Steps = recoverySteps
                }
            },
            Meta = new ToolMetadata 
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

        // Get analyzer diagnostics
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (compilation != null)
        {
            var analyzers = document.Project.AnalyzerReferences
                .SelectMany(r => r.GetAnalyzersForAllLanguages())
                .ToImmutableArray();

            if (analyzers.Length > 0)
            {
                var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, options: null);
                var analyzerDiagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);
                
                // Filter to the specific document and span
                var documentDiagnostics = analyzerDiagnostics
                    .Where(d => d.Location.SourceTree == syntaxTree && d.Location.SourceSpan.IntersectsWith(span));
                    
                diagnostics.AddRange(documentDiagnostics);
            }
        }

        return diagnostics;
    }


    private List<string> GenerateInsights(CodeAction codeAction, List<FileChange> changes, bool wasTruncated = false, int totalCount = 0)
    {
        var insights = new List<string>();
        
        if (wasTruncated)
        {
            insights.Add($"⚠️ Showing {changes.Count} of {totalCount} files to manage response size");
        }

        insights.Add($"Applied fix: '{codeAction.Title}'");

        var totalChanges = changes.Sum(c => c.Changes.Count);
        var displayCount = wasTruncated ? totalCount : changes.Count;
        insights.Add($"Made {totalChanges} text changes across {displayCount} file(s)");

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

    private List<NextAction> GenerateNextActions(ApplyCodeFixParams parameters, List<FileChange> changes, List<FileChange>? allChanges = null)
    {
        var actions = new List<NextAction>();

        // If truncated in preview mode, add action to see all changes
        if (allChanges != null && allChanges.Count > changes.Count && parameters.Preview)
        {
            actions.Add(new NextAction
            {
                Id = "see-all-changes",
                ToolName = "csharp_apply_code_fix",
                Description = $"Preview all {allChanges.Count} file changes",
                Parameters = new
                {
                    filePath = parameters.FilePath,
                    line = parameters.Line,
                    column = parameters.Column,
                    diagnosticId = parameters.DiagnosticId,
                    fixTitle = parameters.FixTitle,
                    preview = true,
                    maxChangedFiles = allChanges.Count
                },
                Priority = "high"
            });
        }
        
        // Re-run diagnostics to see if there are more issues
        actions.Add(new NextAction
        {
            Id = "check-diagnostics",
            ToolName = "csharp_get_diagnostics",
            Description = "Check for remaining diagnostics in the file",
            Parameters = new
            {
                filePath = parameters.FilePath,
                scope = "file"
            }
        });

        // Format the document after applying fixes
        if (changes.Any())
        {
            actions.Add(new NextAction
            {
                Id = "format-document",
                ToolName = "csharp_format_document",
                Description = "Format the modified file(s)",
                Parameters = new
                {
                    filePath = parameters.FilePath
                }
            });
        }

        return actions;
    }
    
    private int EstimateFileChangesTokens(List<FileChange> changes)
    {
        return COA.CodeNav.McpServer.Utilities.TokenEstimator.EstimateCollection(
            changes,
            change => {
                var tokens = 100; // Base structure per file
                tokens += COA.CodeNav.McpServer.Utilities.TokenEstimator.EstimateString(change.FilePath);
                tokens += change.Changes.Count * 80; // Estimate per text change
                return tokens;
            },
            baseTokens: COA.CodeNav.McpServer.Utilities.TokenEstimator.BASE_RESPONSE_TOKENS
        );
    }
}

public class ApplyCodeFixParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file")]
    public required string FilePath { get; set; }

    [JsonPropertyName("line")]
    [Description("Line number (1-based) where the diagnostic is located")]
    public required int Line { get; set; }

    [JsonPropertyName("column")]
    [Description("Column number (1-based) where the diagnostic is located")]
    public required int Column { get; set; }

    [JsonPropertyName("diagnosticId")]
    [Description("Optional: Specific diagnostic ID to fix (e.g., 'CS0219'). If not provided, applies first available fix.")]
    public string? DiagnosticId { get; set; }

    [JsonPropertyName("fixTitle")]
    [Description("Optional: Specific fix title to apply. If not provided, applies first available fix.")]
    public string? FixTitle { get; set; }

    [JsonPropertyName("preview")]
    [Description("Preview changes without applying (default: false)")]
    public bool Preview { get; set; } = false;
    
    [JsonPropertyName("maxChangedFiles")]
    [Description("Maximum number of changed files to return in preview (default: 50)")]
    public int? MaxChangedFiles { get; set; }
}

public class ApplyCodeFixToolResult : ToolResultBase
{
    public override string Operation => "csharp_apply_code_fix";

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