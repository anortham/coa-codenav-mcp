using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Constants;
using COA.Mcp.Framework.Models;

namespace COA.CodeNav.McpServer.Models;

/// <summary>
/// TypeScript-specific models for tool results and data structures
/// </summary>

#region Tool Results

/// <summary>
/// Result for loading TypeScript configuration
/// </summary>
public class TsLoadConfigResult : ToolResultBase
{
    public override string Operation => ToolNames.TsLoadTsConfig;

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("projectPath")]
    public string? ProjectPath { get; set; }

    [JsonPropertyName("tsConfigPath")]
    public string? TsConfigPath { get; set; }

    [JsonPropertyName("compilerOptions")]
    public TsCompilerOptions? CompilerOptions { get; set; }

    [JsonPropertyName("files")]
    public List<string>? Files { get; set; }

    [JsonPropertyName("include")]
    public List<string>? Include { get; set; }

    [JsonPropertyName("exclude")]
    public List<string>? Exclude { get; set; }

    [JsonPropertyName("references")]
    public List<TsProjectReference>? References { get; set; }
}

/// <summary>
/// Result for TypeScript GoToDefinition
/// </summary>
public class TsGoToDefinitionResult : ToolResultBase
{
    public override string Operation => ToolNames.TsGoToDefinition;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("locations")]
    public List<LocationInfo>? Locations { get; set; }

    [JsonPropertyName("isDeclaration")]
    public bool IsDeclaration { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }
}

/// <summary>
/// Result for TypeScript FindAllReferences
/// </summary>
public class TsFindAllReferencesResult : ToolResultBase
{
    public override string Operation => ToolNames.TsFindAllReferences;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("locations")]
    public List<ReferenceLocation>? Locations { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }

    [JsonPropertyName("distribution")]
    public ReferenceDistribution? Distribution { get; set; }
}

/// <summary>
/// Result for TypeScript Symbol Search
/// </summary>
public class TsSymbolSearchResult : ToolResultBase
{
    public override string Operation => ToolNames.TsSymbolSearch;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("symbols")]
    public List<SymbolSearchItem>? Symbols { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }
}

/// <summary>
/// Result for TypeScript Find Implementations
/// </summary>
public class TsFindImplementationsResult : ToolResultBase
{
    public override string Operation => ToolNames.TsFindImplementations;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("implementations")]
    public List<ImplementationInfo>? Implementations { get; set; }

    [JsonPropertyName("interfaceName")]
    public string? InterfaceName { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }
}

/// <summary>
/// Result for TypeScript Diagnostics
/// </summary>
public class TsGetDiagnosticsResult : ToolResultBase
{
    public override string Operation => ToolNames.TsGetDiagnostics;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public DiagnosticsSummary? Summary { get; set; }

    [JsonPropertyName("diagnostics")]
    public List<TsDiagnostic>? Diagnostics { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }

    [JsonPropertyName("distribution")]
    public DiagnosticsDistribution? Distribution { get; set; }
}

/// <summary>
/// Result for TypeScript Document Symbols
/// </summary>
public class TsDocumentSymbolsResult : ToolResultBase
{
    public override string Operation => ToolNames.TsDocumentSymbols;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("symbols")]
    public List<SymbolInfo>? Symbols { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }
}

/// <summary>
/// Result for TypeScript Hover
/// </summary>
public class TsHoverResult : ToolResultBase
{
    public override string Operation => ToolNames.TsHover;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("hoverInfo")]
    public TsHoverInfo? HoverInfo { get; set; }

    [JsonPropertyName("symbolDetails")]
    public TsSymbolDetails? SymbolDetails { get; set; }
}

#endregion

#region Data Structures

/// <summary>
/// TypeScript compiler options from tsconfig.json
/// </summary>
public class TsCompilerOptions
{
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("module")]
    public string? Module { get; set; }

    [JsonPropertyName("lib")]
    public List<string>? Lib { get; set; }

    [JsonPropertyName("jsx")]
    public string? Jsx { get; set; }

    [JsonPropertyName("strict")]
    public bool? Strict { get; set; }

    [JsonPropertyName("esModuleInterop")]
    public bool? EsModuleInterop { get; set; }

    [JsonPropertyName("skipLibCheck")]
    public bool? SkipLibCheck { get; set; }

    [JsonPropertyName("forceConsistentCasingInFileNames")]
    public bool? ForceConsistentCasingInFileNames { get; set; }

    [JsonPropertyName("declaration")]
    public bool? Declaration { get; set; }

    [JsonPropertyName("declarationMap")]
    public bool? DeclarationMap { get; set; }

    [JsonPropertyName("sourceMap")]
    public bool? SourceMap { get; set; }

    [JsonPropertyName("outDir")]
    public string? OutDir { get; set; }

    [JsonPropertyName("rootDir")]
    public string? RootDir { get; set; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("paths")]
    public Dictionary<string, List<string>>? Paths { get; set; }
}

/// <summary>
/// TypeScript project reference
/// </summary>
public class TsProjectReference
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("prepend")]
    public bool? Prepend { get; set; }
}

/// <summary>
/// TypeScript symbol information
/// </summary>
public class TsSymbolInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("kindModifiers")]
    public string? KindModifiers { get; set; }

    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }

    [JsonPropertyName("span")]
    public TextSpan? Span { get; set; }

    [JsonPropertyName("containerName")]
    public string? ContainerName { get; set; }

    [JsonPropertyName("isExported")]
    public bool IsExported { get; set; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }
}

/// <summary>
/// TypeScript symbol query parameters
/// </summary>
public class TsSymbolQuery : QueryInfo
{
    [JsonPropertyName("searchPattern")]
    public string? SearchPattern { get; set; }

    [JsonPropertyName("searchType")]
    public string? SearchType { get; set; }

    [JsonPropertyName("symbolKinds")]
    public List<string>? SymbolKinds { get; set; }

    [JsonPropertyName("includeExternal")]
    public bool IncludeExternal { get; set; }
}

/// <summary>
/// TypeScript diagnostic information
/// </summary>
public class TsDiagnostic
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("category")]
    public required string Category { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("start")]
    public Position? Start { get; set; }

    [JsonPropertyName("end")]
    public Position? End { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

/// <summary>
/// TypeScript document symbol
/// </summary>
public class TsDocumentSymbol
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("range")]
    public Range? Range { get; set; }

    [JsonPropertyName("selectionRange")]
    public Range? SelectionRange { get; set; }

    [JsonPropertyName("children")]
    public List<TsDocumentSymbol>? Children { get; set; }
}

/// <summary>
/// TypeScript hover information
/// </summary>
public class TsHoverInfo
{
    [JsonPropertyName("displayString")]
    public string? DisplayString { get; set; }

    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }

    [JsonPropertyName("tags")]
    public List<TsJsDocTag>? Tags { get; set; }

    [JsonPropertyName("typeString")]
    public string? TypeString { get; set; }
}

/// <summary>
/// TypeScript symbol details
/// </summary>
public class TsSymbolDetails
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("modifiers")]
    public List<string>? Modifiers { get; set; }
}

/// <summary>
/// TypeScript JSDoc tag
/// </summary>
public class TsJsDocTag
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

/// <summary>
/// Text span for TypeScript locations (using existing TextSpan from CodeElementModels)
/// </summary>

/// <summary>
/// Range for TypeScript locations
/// </summary>
public class Range
{
    [JsonPropertyName("start")]
    public required Position Start { get; set; }

    [JsonPropertyName("end")]
    public required Position End { get; set; }
}

/// <summary>
/// Position in a TypeScript file
/// </summary>
public class Position
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("character")]
    public int Character { get; set; }
}

/// <summary>
/// Symbol search result item
/// </summary>
public class SymbolSearchItem : SymbolInfo
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("containerKind")]
    public string? ContainerKind { get; set; }

    [JsonPropertyName("matchKind")]
    public string? MatchKind { get; set; }

    [JsonPropertyName("modifiers")]
    public new List<string>? Modifiers { get; set; }
}

#endregion

#region Parameters

/// <summary>
/// Parameters for loading TypeScript configuration
/// </summary>
public class TsLoadConfigParams
{
    [JsonPropertyName("tsConfigPath")]
    public required string TsConfigPath { get; set; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }
}

/// <summary>
/// Parameters for TypeScript file operations
/// </summary>
public class TsFileOperationParams
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("character")]
    public int Character { get; set; }
}

/// <summary>
/// Parameters for TypeScript symbol search
/// </summary>
public class TsSymbolSearchParams
{
    [JsonPropertyName("query")]
    public required string Query { get; set; }

    [JsonPropertyName("searchType")]
    public string? SearchType { get; set; } = "contains";

    [JsonPropertyName("symbolKinds")]
    public List<string>? SymbolKinds { get; set; }

    [JsonPropertyName("maxResults")]
    public int MaxResults { get; set; } = 100;

    [JsonPropertyName("includeExternal")]
    public bool IncludeExternal { get; set; } = false;
}

/// <summary>
/// Parameters for TypeScript Call Hierarchy
/// </summary>
public class TsCallHierarchyParams
{
    [Required]
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [Required]
    [JsonPropertyName("character")]
    public int Character { get; set; }

    [JsonPropertyName("maxDepth")]
    public int MaxDepth { get; set; } = 3;

    [JsonPropertyName("includeOverrides")]
    public bool IncludeOverrides { get; set; } = true;

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; set; }
}

/// <summary>
/// Result for TypeScript Call Hierarchy
/// </summary>
public class TsCallHierarchyResult : ToolResultBase
{
    public override string Operation => ToolNames.TsCallHierarchy;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("root")]
    public TsCallHierarchyItem? Root { get; set; }

    [JsonPropertyName("incomingCalls")]
    public List<TsIncomingCall>? IncomingCalls { get; set; }

    [JsonPropertyName("outgoingCalls")]
    public List<TsOutgoingCall>? OutgoingCalls { get; set; }

    [JsonPropertyName("callTree")]
    public TsCallTreeNode? CallTree { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }
}

/// <summary>
/// TypeScript Call Hierarchy Item
/// </summary>
public class TsCallHierarchyItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("span")]
    public TsTextSpan? Span { get; set; }

    [JsonPropertyName("selectionSpan")]
    public TsTextSpan? SelectionSpan { get; set; }

    [JsonPropertyName("containerName")]
    public string? ContainerName { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

/// <summary>
/// TypeScript Incoming Call
/// </summary>
public class TsIncomingCall
{
    [JsonPropertyName("from")]
    public TsCallHierarchyItem? From { get; set; }

    [JsonPropertyName("fromSpans")]
    public List<TsTextSpan>? FromSpans { get; set; }
}

/// <summary>
/// TypeScript Outgoing Call
/// </summary>
public class TsOutgoingCall
{
    [JsonPropertyName("to")]
    public TsCallHierarchyItem? To { get; set; }

    [JsonPropertyName("fromSpans")]
    public List<TsTextSpan>? FromSpans { get; set; }
}

/// <summary>
/// TypeScript Call Tree Node for hierarchical display
/// </summary>
public class TsCallTreeNode
{
    [JsonPropertyName("item")]
    public TsCallHierarchyItem? Item { get; set; }

    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = string.Empty; // "incoming" or "outgoing"

    [JsonPropertyName("children")]
    public List<TsCallTreeNode>? Children { get; set; }

    [JsonPropertyName("isExpanded")]
    public bool IsExpanded { get; set; } = false;
}

/// <summary>
/// TypeScript Text Span
/// </summary>
public class TsTextSpan
{
    [JsonPropertyName("start")]
    public TsTextPosition? Start { get; set; }

    [JsonPropertyName("end")]
    public TsTextPosition? End { get; set; }
}

/// <summary>
/// TypeScript Text Position
/// </summary>
public class TsTextPosition
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("character")]
    public int Character { get; set; }
}

// TypeScript-specific models

/// <summary>
/// TypeScript file change information
/// </summary>
public class TsFileChange
{
    [JsonPropertyName("startPosition")]
    public int StartPosition { get; set; }

    [JsonPropertyName("length")]
    public int Length { get; set; }

    [JsonPropertyName("newText")]
    public string NewText { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

// New TypeScript Tool Result Models

/// <summary>
/// Result from organizing imports in a TypeScript file
/// </summary>
public class TsOrganizeImportsResult : ToolResultBase
{
    public override string Operation => ToolNames.TsOrganizeImports;

    [JsonPropertyName("query")]
    public OrganizeImportsQuery? Query { get; set; }

    [JsonPropertyName("summary")]
    public OrganizeImportsSummary? Summary { get; set; }

    [JsonPropertyName("changes")]
    public List<TsFileChange>? Changes { get; set; }

    [JsonPropertyName("originalContent")]
    public string? OriginalContent { get; set; }

    [JsonPropertyName("updatedContent")]
    public string? UpdatedContent { get; set; }
}

/// <summary>
/// Query information for organize imports operation
/// </summary>
public class OrganizeImportsQuery
{
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("preview")]
    public bool Preview { get; set; }
}

/// <summary>
/// Summary of organize imports operation
/// </summary>
public class OrganizeImportsSummary
{
    [JsonPropertyName("totalChanges")]
    public int TotalChanges { get; set; }

    [JsonPropertyName("organizedImports")]
    public int OrganizedImports { get; set; }

    [JsonPropertyName("removedDuplicates")]
    public int RemovedDuplicates { get; set; }
}

/// <summary>
/// Result from adding missing imports in a TypeScript file
/// </summary>
public class TsAddMissingImportsResult : ToolResultBase
{
    public override string Operation => ToolNames.TsAddMissingImports;

    [JsonPropertyName("query")]
    public AddMissingImportsQuery? Query { get; set; }

    [JsonPropertyName("summary")]
    public AddMissingImportsSummary? Summary { get; set; }

    [JsonPropertyName("changes")]
    public List<TsFileChange>? Changes { get; set; }

    [JsonPropertyName("originalContent")]
    public string? OriginalContent { get; set; }

    [JsonPropertyName("updatedContent")]
    public string? UpdatedContent { get; set; }

    [JsonPropertyName("missingImports")]
    public List<MissingImportInfo>? MissingImports { get; set; }
}

/// <summary>
/// Query information for add missing imports operation
/// </summary>
public class AddMissingImportsQuery
{
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("preview")]
    public bool Preview { get; set; }
}

/// <summary>
/// Summary of add missing imports operation
/// </summary>
public class AddMissingImportsSummary
{
    [JsonPropertyName("totalImportsAdded")]
    public int TotalImportsAdded { get; set; }

    [JsonPropertyName("errorsFixed")]
    public int ErrorsFixed { get; set; }
}

/// <summary>
/// Information about a missing import for the response
/// </summary>
public class MissingImportInfo
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("character")]
    public int Character { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Result from applying TypeScript quick fixes
/// </summary>
public class TsApplyQuickFixResult : ToolResultBase
{
    public override string Operation => ToolNames.TsApplyQuickFix;

    [JsonPropertyName("query")]
    public ApplyQuickFixQuery? Query { get; set; }

    [JsonPropertyName("summary")]
    public ApplyQuickFixSummary? Summary { get; set; }

    [JsonPropertyName("appliedFix")]
    public QuickFixInfo? AppliedFix { get; set; }

    [JsonPropertyName("availableFixes")]
    public List<QuickFixInfo>? AvailableFixes { get; set; }

    [JsonPropertyName("changes")]
    public List<TsFileChange>? Changes { get; set; }

    [JsonPropertyName("originalContent")]
    public string? OriginalContent { get; set; }

    [JsonPropertyName("updatedContent")]
    public string? UpdatedContent { get; set; }
}

/// <summary>
/// Query information for apply quick fix operation
/// </summary>
public class ApplyQuickFixQuery
{
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("character")]
    public int Character { get; set; }

    [JsonPropertyName("actionName")]
    public string? ActionName { get; set; }

    [JsonPropertyName("preview")]
    public bool Preview { get; set; }
}

/// <summary>
/// Summary of apply quick fix operation
/// </summary>
public class ApplyQuickFixSummary
{
    [JsonPropertyName("availableFixesCount")]
    public int AvailableFixesCount { get; set; }

    [JsonPropertyName("appliedFixesCount")]
    public int AppliedFixesCount { get; set; }

    [JsonPropertyName("changesCount")]
    public int ChangesCount { get; set; }
}

/// <summary>
/// Information about a TypeScript quick fix
/// </summary>
public class QuickFixInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("priority")]
    public int Priority { get; set; }
}

/// <summary>
/// Result from loading TypeScript workspace
/// </summary>
public class TsLoadWorkspaceResult : ToolResultBase
{
    public override string Operation => ToolNames.TsLoadWorkspace;

    [JsonPropertyName("query")]
    public LoadWorkspaceQuery? Query { get; set; }

    [JsonPropertyName("summary")]
    public LoadWorkspaceSummary? Summary { get; set; }

    [JsonPropertyName("projects")]
    public List<TypeScriptProjectInfo>? Projects { get; set; }

    [JsonPropertyName("crossReferences")]
    public List<ProjectReference>? CrossReferences { get; set; }
}

/// <summary>
/// Query information for load workspace operation
/// </summary>
public class LoadWorkspaceQuery
{
    [JsonPropertyName("workspacePath")]
    public string? WorkspacePath { get; set; }

    [JsonPropertyName("includeNodeModules")]
    public bool IncludeNodeModules { get; set; }

    [JsonPropertyName("maxDepth")]
    public int MaxDepth { get; set; }
}

/// <summary>
/// Summary of load workspace operation
/// </summary>
public class LoadWorkspaceSummary
{
    [JsonPropertyName("projectCount")]
    public int ProjectCount { get; set; }

    [JsonPropertyName("totalSourceFiles")]
    public int TotalSourceFiles { get; set; }

    [JsonPropertyName("crossReferencesCount")]
    public int CrossReferencesCount { get; set; }
}

/// <summary>
/// Information about a TypeScript project in a workspace
/// </summary>
public class TypeScriptProjectInfo
{
    [JsonPropertyName("projectPath")]
    public string ProjectPath { get; set; } = string.Empty;

    [JsonPropertyName("projectType")]
    public string? ProjectType { get; set; }

    [JsonPropertyName("compilerOptions")]
    public Dictionary<string, object>? CompilerOptions { get; set; }

    [JsonPropertyName("sourceFiles")]
    public List<string>? SourceFiles { get; set; }

    [JsonPropertyName("dependencies")]
    public List<string>? Dependencies { get; set; }

    [JsonPropertyName("notes")]
    public List<string>? Notes { get; set; }
}

/// <summary>
/// Information about a cross-project reference
/// </summary>
public class ProjectReference
{
    [JsonPropertyName("fromProject")]
    public string FromProject { get; set; } = string.Empty;

    [JsonPropertyName("toProject")]
    public string ToProject { get; set; } = string.Empty;

    [JsonPropertyName("referenceType")]
    public string ReferenceType { get; set; } = string.Empty;
}

/// <summary>
/// Represents an error for a missing import
/// </summary>
public class MissingImportError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("character")]
    public int Character { get; set; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }

    [JsonPropertyName("endCharacter")]
    public int EndCharacter { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Information about a TypeScript project loaded in a workspace
/// </summary>
public class WorkspaceProjectInfo
{
    [JsonPropertyName("projectPath")]
    public string ProjectPath { get; set; } = string.Empty;

    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = string.Empty;

    [JsonPropertyName("compilerOptions")]
    public Dictionary<string, object>? CompilerOptions { get; set; }

    [JsonPropertyName("sourceFiles")]
    public List<string> SourceFiles { get; set; } = new();

    [JsonPropertyName("references")]
    public List<string> References { get; set; } = new();

    [JsonPropertyName("loadedAt")]
    public DateTime LoadedAt { get; set; }

    [JsonPropertyName("notes")]
    public List<string>? Notes { get; set; }
}

/// <summary>
/// Information about a failed project load
/// </summary>
public class FailedProjectInfo
{
    [JsonPropertyName("projectPath")]
    public string ProjectPath { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Information about dependencies between projects
/// </summary>
public class ProjectDependencyInfo
{
    [JsonPropertyName("fromProject")]
    public string FromProject { get; set; } = string.Empty;

    [JsonPropertyName("toProject")]
    public string ToProject { get; set; } = string.Empty;

    [JsonPropertyName("fromPath")]
    public string FromPath { get; set; } = string.Empty;

    [JsonPropertyName("toPath")]
    public string ToPath { get; set; } = string.Empty;

    [JsonPropertyName("dependencyType")]
    public string DependencyType { get; set; } = string.Empty;
}

/// <summary>
/// Information about a TypeScript code fix
/// </summary>
public class TsCodeFixInfo
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("changes")]
    public List<TsFileChange> Changes { get; set; } = new();
}

/// <summary>
/// Information about an available fix
/// </summary>
public class AvailableFixInfo
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("actionName")]
    public string ActionName { get; set; } = string.Empty;
}

#endregion