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

#endregion