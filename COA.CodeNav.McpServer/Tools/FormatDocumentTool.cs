using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// MCP tool that provides document formatting functionality using Roslyn
/// </summary>
public class FormatDocumentTool : McpToolBase<FormatDocumentParams, FormatDocumentResult>
{
    private readonly ILogger<FormatDocumentTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly AnalysisResultResourceProvider? _resourceProvider;

    public override string Name => ToolNames.FormatDocument;
    public override string Description => "Format code according to project settings and .editorconfig.";
    
    public FormatDocumentTool(
        ILogger<FormatDocumentTool> logger,
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

    protected override async Task<FormatDocumentResult> ExecuteInternalAsync(
        FormatDocumentParams parameters,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("FormatDocument request received: FilePath={FilePath}, OrganizeUsings={OrganizeUsings}", 
            parameters.FilePath, parameters.OrganizeUsings);
        
        var startTime = DateTime.UtcNow;

        // Get the document
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            return new FormatDocumentResult
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

        // Track changes made
        var changesMade = new List<string>();
        var formattedDocument = document;

        // Step 1: Organize usings if requested
        if (parameters.OrganizeUsings ?? true)
        {
            var beforeUsings = await GetUsingsInfo(formattedDocument, cancellationToken);
            
            // Sort usings
            var root = await formattedDocument.GetSyntaxRootAsync(cancellationToken);
            if (root != null)
            {
                var newRoot = OrganizeUsings(root);
                if (newRoot != root)
                {
                    formattedDocument = formattedDocument.WithSyntaxRoot(newRoot);
                    changesMade.Add("Organized and sorted using directives");
                }
            }
            
            var afterUsings = await GetUsingsInfo(formattedDocument, cancellationToken);
            if (beforeUsings.Count != afterUsings.Count)
            {
                changesMade.Add($"Removed {beforeUsings.Count - afterUsings.Count} unused using directives");
            }
        }

        // Step 2: Format the document
        var options = await GetFormattingOptionsAsync(formattedDocument, cancellationToken);
        
        // Apply formatting
        formattedDocument = await Formatter.FormatAsync(formattedDocument, options, cancellationToken);
        
        // Check if formatting made changes
        var originalText = await document.GetTextAsync(cancellationToken);
        var formattedText = await formattedDocument.GetTextAsync(cancellationToken);
        
        if (!originalText.ContentEquals(formattedText))
        {
            changesMade.Add("Applied code formatting (indentation, spacing, line breaks)");
        }

        // Step 3: Format selection if specified
        if (parameters.StartLine.HasValue && parameters.EndLine.HasValue)
        {
            var textSpan = await GetTextSpanFromLines(
                formattedDocument,
                parameters.StartLine.Value,
                parameters.EndLine.Value,
                cancellationToken);
            
            if (textSpan.HasValue)
            {
                formattedDocument = await Formatter.FormatAsync(
                    formattedDocument,
                    textSpan.Value,
                    options,
                    cancellationToken);
                
                changesMade.Add($"Formatted selection from line {parameters.StartLine} to {parameters.EndLine}");
            }
        }

        // Get the final formatted code
        var finalText = await formattedDocument.GetTextAsync(cancellationToken);
        var code = finalText.ToString();

        // Calculate statistics
        var stats = await CalculateFormattingStats(document, formattedDocument, cancellationToken);

        // Generate insights
        var insights = GenerateInsights(changesMade, stats);

        // Generate next actions
        var actions = GenerateNextActions(parameters);

        return new FormatDocumentResult
        {
            Success = true,
            Message = changesMade.Any() 
                ? $"Formatted document with {changesMade.Count} change types" 
                : "Document is already properly formatted",
            Query = CreateQueryInfo(parameters),
            Summary = new SummaryInfo
            {
                TotalFound = changesMade.Count,
                Returned = changesMade.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            },
            Code = code,
            ChangesMade = changesMade,
            FormattingStats = stats,
            Insights = insights,
            Actions = actions,
            Meta = new ToolExecutionMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            }
        };
    }

    private async Task<OptionSet> GetFormattingOptionsAsync(Document document, CancellationToken cancellationToken)
    {
        // Get the workspace options
        var options = document.Project.Solution.Options;

        // Try to load .editorconfig settings if available
        var analyzerConfigOptions = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider;
        if (analyzerConfigOptions != null)
        {
            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (tree != null)
            {
                var configOptions = analyzerConfigOptions.GetOptions(tree);
                
                // Apply common .editorconfig settings
                if (configOptions.TryGetValue("indent_style", out var indentStyle))
                {
                    options = options.WithChangedOption(
                        FormattingOptions.UseTabs,
                        LanguageNames.CSharp,
                        indentStyle.Equals("tab", StringComparison.OrdinalIgnoreCase));
                }

                if (configOptions.TryGetValue("indent_size", out var indentSizeStr) && 
                    int.TryParse(indentSizeStr, out var indentSize))
                {
                    options = options.WithChangedOption(
                        FormattingOptions.IndentationSize,
                        LanguageNames.CSharp,
                        indentSize);
                }

                if (configOptions.TryGetValue("end_of_line", out var endOfLine))
                {
                    var newLine = endOfLine.ToLowerInvariant() switch
                    {
                        "lf" => "\n",
                        "crlf" => "\r\n",
                        "cr" => "\r",
                        _ => Environment.NewLine
                    };
                    
                    options = options.WithChangedOption(
                        FormattingOptions.NewLine,
                        LanguageNames.CSharp,
                        newLine);
                }
            }
        }

        return options;
    }

    private SyntaxNode OrganizeUsings(SyntaxNode root)
    {
        var compilation = root as CompilationUnitSyntax;
        if (compilation == null)
            return root;

        // Get all using directives
        var usings = compilation.Usings
            .OrderBy(u => u.Alias != null ? 1 : 0) // Aliases last
            .ThenBy(u => u.Name?.ToString().StartsWith("System") == true ? 0 : 1) // System namespaces first
            .ThenBy(u => u.Name?.ToString())
            .ToList();

        // Also handle usings inside namespace declarations
        var namespaceDeclarations = compilation.Members
            .OfType<NamespaceDeclarationSyntax>()
            .ToList();

        foreach (var ns in namespaceDeclarations)
        {
            var nsUsings = ns.Usings
                .OrderBy(u => u.Alias != null ? 1 : 0)
                .ThenBy(u => u.Name?.ToString().StartsWith("System") == true ? 0 : 1)
                .ThenBy(u => u.Name?.ToString())
                .ToList();

            if (!ns.Usings.SequenceEqual(nsUsings))
            {
                var newNs = ns.WithUsings(SyntaxFactory.List(nsUsings));
                root = root.ReplaceNode(ns, newNs);
            }
        }

        // Update root if usings changed
        if (!compilation.Usings.SequenceEqual(usings))
        {
            compilation = compilation.WithUsings(SyntaxFactory.List(usings));
            return compilation;
        }

        return root;
    }

    private async Task<List<string>> GetUsingsInfo(Document document, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
            return new List<string>();

        var compilation = root as CompilationUnitSyntax;
        if (compilation == null)
            return new List<string>();

        return compilation.Usings
            .Select(u => u.Name?.ToString() ?? string.Empty)
            .Where(u => !string.IsNullOrEmpty(u))
            .ToList();
    }

    private async Task<Microsoft.CodeAnalysis.Text.TextSpan?> GetTextSpanFromLines(
        Document document,
        int startLine,
        int endLine,
        CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken);
        var lines = text.Lines;

        if (startLine < 1 || startLine > lines.Count ||
            endLine < 1 || endLine > lines.Count ||
            startLine > endLine)
        {
            return null;
        }

        var startPosition = lines[startLine - 1].Start;
        var endPosition = lines[endLine - 1].End;

        return Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(startPosition, endPosition);
    }

    private async Task<FormattingStats> CalculateFormattingStats(
        Document originalDocument,
        Document formattedDocument,
        CancellationToken cancellationToken)
    {
        var originalText = await originalDocument.GetTextAsync(cancellationToken);
        var formattedText = await formattedDocument.GetTextAsync(cancellationToken);

        var stats = new FormattingStats
        {
            OriginalLineCount = originalText.Lines.Count,
            FormattedLineCount = formattedText.Lines.Count,
            CharactersChanged = 0
        };

        // Calculate character changes
        var changes = formattedText.GetTextChanges(originalText);
        stats.CharactersChanged = changes.Sum(c => Math.Abs(c.NewText?.Length ?? 0 - c.Span.Length));

        // Count indentation changes
        for (int i = 0; i < Math.Min(originalText.Lines.Count, formattedText.Lines.Count); i++)
        {
            var originalLine = originalText.Lines[i].ToString();
            var formattedLine = formattedText.Lines[i].ToString();

            var originalIndent = originalLine.TakeWhile(char.IsWhiteSpace).Count();
            var formattedIndent = formattedLine.TakeWhile(char.IsWhiteSpace).Count();

            if (originalIndent != formattedIndent)
            {
                stats.IndentationChanges++;
            }
        }

        return stats;
    }

    private List<string> GenerateInsights(List<string> changesMade, FormattingStats stats)
    {
        var insights = new List<string>();

        if (!changesMade.Any())
        {
            insights.Add("Document is already properly formatted");
            insights.Add("No formatting changes were needed");
        }
        else
        {
            insights.Add($"Applied {changesMade.Count} formatting improvements");

            if (stats.IndentationChanges > 0)
            {
                insights.Add($"Fixed indentation on {stats.IndentationChanges} lines");
            }

            if (stats.CharactersChanged > 100)
            {
                insights.Add($"Modified {stats.CharactersChanged:N0} characters for consistent formatting");
            }

            if (changesMade.Any(c => c.Contains("using")))
            {
                insights.Add("Organized and cleaned up using directives");
            }
        }

        insights.Add("Consider setting up .editorconfig for consistent team formatting");

        if (stats.FormattedLineCount != stats.OriginalLineCount)
        {
            var diff = stats.FormattedLineCount - stats.OriginalLineCount;
            insights.Add($"Line count changed by {Math.Abs(diff)} ({(diff > 0 ? "added" : "removed")} lines)");
        }

        return insights;
    }

    private List<AIAction> GenerateNextActions(FormatDocumentParams parameters)
    {
        var actions = new List<AIAction>
        {
            new AIAction
            {
                Action = ToolNames.GetDiagnostics,
                Description = "Check for any compilation errors",
                Parameters = new Dictionary<string, object> { ["filePath"] = parameters.FilePath },
                Priority = 90,
                Category = "validation"
            }
        };

        if (!(parameters.OrganizeUsings ?? true))
        {
            actions.Add(new AIAction
            {
                Action = ToolNames.FormatDocument,
                Description = "Format with using organization",
                Parameters = new Dictionary<string, object> { ["filePath"] = parameters.FilePath, ["organizeUsings"] = true },
                Priority = 70,
                Category = "formatting"
            });
        }

        actions.Add(new AIAction
        {
            Action = ToolNames.AddMissingUsings,
            Description = "Add any missing using directives",
            Parameters = new Dictionary<string, object> { ["filePath"] = parameters.FilePath },
            Priority = 70,
            Category = "refactoring"
        });

        return actions;
    }

    private QueryInfo CreateQueryInfo(FormatDocumentParams parameters)
    {
        return new QueryInfo
        {
            FilePath = parameters.FilePath,
            Position = parameters.StartLine.HasValue && parameters.EndLine.HasValue
                ? new PositionInfo { Line = parameters.StartLine.Value, Column = 1 }
                : null
        };
    }
    
    protected override int EstimateTokenUsage()
    {
        // Estimate for typical format document response
        return 2500;
    }
}

public class FormatDocumentParams
{
    [System.ComponentModel.DataAnnotations.Required(ErrorMessage = "FilePath is required")]
    [JsonPropertyName("filePath")]
    [COA.Mcp.Framework.Attributes.Description("Path to the source file")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("organizeUsings")]
    [COA.Mcp.Framework.Attributes.Description("Organize and remove unused usings (default: true)")]
    public bool? OrganizeUsings { get; set; }

    [JsonPropertyName("startLine")]
    [COA.Mcp.Framework.Attributes.Description("Start line for partial formatting (1-based)")]
    public int? StartLine { get; set; }

    [JsonPropertyName("endLine")]
    [COA.Mcp.Framework.Attributes.Description("End line for partial formatting (1-based)")]
    public int? EndLine { get; set; }
}

public class FormatDocumentResult : ToolResultBase
{
    public override string Operation => ToolNames.FormatDocument;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("changesMade")]
    public List<string>? ChangesMade { get; set; }

    [JsonPropertyName("formattingStats")]
    public FormattingStats? FormattingStats { get; set; }
}

public class FormattingStats
{
    [JsonPropertyName("originalLineCount")]
    public int OriginalLineCount { get; set; }

    [JsonPropertyName("formattedLineCount")]
    public int FormattedLineCount { get; set; }

    [JsonPropertyName("charactersChanged")]
    public int CharactersChanged { get; set; }

    [JsonPropertyName("indentationChanges")]
    public int IndentationChanges { get; set; }
}
