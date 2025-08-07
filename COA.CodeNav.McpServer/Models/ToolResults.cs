using System.Text.Json.Serialization;
using COA.CodeNav.McpServer.Constants;
using COA.Mcp.Framework.Models;

namespace COA.CodeNav.McpServer.Models;

/// <summary>
/// Result for GoToDefinition tool
/// </summary>
public class GoToDefinitionToolResult : ToolResultBase
{
    public override string Operation => ToolNames.GoToDefinition;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("locations")]
    public List<LocationInfo>? Locations { get; set; }

    [JsonPropertyName("isMetadata")]
    public bool IsMetadata { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }
}

/// <summary>
/// Result for DocumentSymbols tool
/// </summary>
public class DocumentSymbolsToolResult : ToolResultBase
{
    public override string Operation => ToolNames.DocumentSymbols;

    [JsonPropertyName("query")]
    public DocumentSymbolsQuery? Query { get; set; }

    [JsonPropertyName("summary")]
    public DocumentSymbolsSummary? Summary { get; set; }

    [JsonPropertyName("symbols")]
    public List<DocumentSymbol>? Symbols { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }

    [JsonPropertyName("distribution")]
    public SymbolDistribution? Distribution { get; set; }
}

/// <summary>
/// Document symbols specific query
/// </summary>
public class DocumentSymbolsQuery : QueryInfo
{
    [JsonPropertyName("symbolKinds")]
    public List<string>? SymbolKinds { get; set; }

    [JsonPropertyName("includePrivate")]
    public bool IncludePrivate { get; set; }

    [JsonPropertyName("maxResults")]
    public int MaxResults { get; set; }
}

/// <summary>
/// Document symbols summary
/// </summary>
public class DocumentSymbolsSummary : SummaryInfo
{
    [JsonPropertyName("totalSymbols")]
    public int TotalSymbols { get; set; }

    [JsonPropertyName("hierarchical")]
    public bool Hierarchical { get; set; }
}


/// <summary>
/// Result for FindAllReferences tool
/// </summary>
public class FindAllReferencesToolResult : ToolResultBase
{
    public override string Operation => ToolNames.FindAllReferences;

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
/// Distribution information for references
/// </summary>
public class ReferenceDistribution
{
    [JsonPropertyName("byFile")]
    public Dictionary<string, int>? ByFile { get; set; }

    [JsonPropertyName("byKind")]
    public Dictionary<string, int>? ByKind { get; set; }
}

/// <summary>
/// Result for Hover tool
/// </summary>
public class HoverToolResult : ToolResultBase
{
    public override string Operation => ToolNames.Hover;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("hoverInfo")]
    public HoverInfo? HoverInfo { get; set; }

    [JsonPropertyName("symbolDetails")]
    public SymbolDetails? SymbolDetails { get; set; }
}

/// <summary>
/// Result for RenameSymbol tool
/// </summary>
public class RenameSymbolToolResult : ToolResultBase
{
    public override string Operation => ToolNames.RenameSymbol;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("preview")]
    public bool Preview { get; set; }

    [JsonPropertyName("applied")]
    public bool Applied { get; set; }

    [JsonPropertyName("changes")]
    public List<FileChange>? Changes { get; set; }

    [JsonPropertyName("conflicts")]
    public List<RenameConflict>? Conflicts { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }
}

/// <summary>
/// Result for SymbolSearch tool
/// </summary>
public class SymbolSearchToolResult : ToolResultBase
{
    public override string Operation => ToolNames.SymbolSearch;

    [JsonPropertyName("query")]
    public SymbolSearchQuery? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("symbols")]
    public List<SymbolInfo>? Symbols { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }

    [JsonPropertyName("distribution")]
    public SymbolDistribution? Distribution { get; set; }
}

/// <summary>
/// Symbol search specific query
/// </summary>
public class SymbolSearchQuery : QueryInfo
{
    [JsonPropertyName("searchPattern")]
    public string? SearchPattern { get; set; }

    [JsonPropertyName("searchType")]
    public string? SearchType { get; set; }

    [JsonPropertyName("symbolKinds")]
    public List<string>? SymbolKinds { get; set; }
}

/// <summary>
/// Symbol distribution information
/// </summary>
public class SymbolDistribution
{
    [JsonPropertyName("byKind")]
    public Dictionary<string, int>? ByKind { get; set; }

    [JsonPropertyName("byNamespace")]
    public Dictionary<string, int>? ByNamespace { get; set; }

    [JsonPropertyName("byProject")]
    public Dictionary<string, int>? ByProject { get; set; }

    [JsonPropertyName("byAccessibility")]
    public Dictionary<string, int>? ByAccessibility { get; set; }
}

/// <summary>
/// Result for FindImplementations tool
/// </summary>
public class FindImplementationsToolResult : ToolResultBase
{
    public override string Operation => ToolNames.FindImplementations;

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("implementations")]
    public List<ImplementationInfo>? Implementations { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }

    [JsonPropertyName("distribution")]
    public ImplementationDistribution? Distribution { get; set; }
}

/// <summary>
/// Implementation distribution information
/// </summary>
public class ImplementationDistribution
{
    [JsonPropertyName("byType")]
    public Dictionary<string, int>? ByType { get; set; }

    [JsonPropertyName("byProject")]
    public Dictionary<string, int>? ByProject { get; set; }
}

/// <summary>
/// Result for GetDiagnostics tool
/// </summary>
public class GetDiagnosticsToolResult : ToolResultBase
{
    public override string Operation => ToolNames.GetDiagnostics;

    [JsonPropertyName("query")]
    public DiagnosticsQuery? Query { get; set; }

    [JsonPropertyName("summary")]
    public DiagnosticsSummary? Summary { get; set; }

    [JsonPropertyName("diagnostics")]
    public List<DiagnosticInfo>? Diagnostics { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }

    [JsonPropertyName("distribution")]
    public DiagnosticsDistribution? Distribution { get; set; }
}

/// <summary>
/// Diagnostics specific query
/// </summary>
public class DiagnosticsQuery : QueryInfo
{
    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("severities")]
    public List<string>? Severities { get; set; }

    [JsonPropertyName("categories")]
    public List<string>? Categories { get; set; }
}

/// <summary>
/// Diagnostics summary
/// </summary>
public class DiagnosticsSummary : SummaryInfo
{
    [JsonPropertyName("errorCount")]
    public int ErrorCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("infoCount")]
    public int InfoCount { get; set; }
}

/// <summary>
/// Diagnostics distribution
/// </summary>
public class DiagnosticsDistribution
{
    [JsonPropertyName("bySeverity")]
    public Dictionary<string, int>? BySeverity { get; set; }

    [JsonPropertyName("byCategory")]
    public Dictionary<string, int>? ByCategory { get; set; }

    [JsonPropertyName("byFile")]
    public Dictionary<string, int>? ByFile { get; set; }
}

/// <summary>
/// Result for GetTypeMembers tool
/// </summary>
public class GetTypeMembersToolResult : ToolResultBase
{
    public override string Operation => ToolNames.GetTypeMembers;

    [JsonPropertyName("query")]
    public GetTypeMembersQuery? Query { get; set; }

    [JsonPropertyName("summary")]
    public GetTypeMembersSummary? Summary { get; set; }

    [JsonPropertyName("members")]
    public List<TypeMemberInfo>? Members { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }

    [JsonPropertyName("distribution")]
    public TypeMembersDistribution? Distribution { get; set; }
}

/// <summary>
/// GetTypeMembers specific query
/// </summary>
public class GetTypeMembersQuery : QueryInfo
{
    [JsonPropertyName("includeInherited")]
    public bool IncludeInherited { get; set; }

    [JsonPropertyName("includePrivate")]
    public bool IncludePrivate { get; set; }

    [JsonPropertyName("includeDocumentation")]
    public bool IncludeDocumentation { get; set; }

    [JsonPropertyName("memberKinds")]
    public List<string>? MemberKinds { get; set; }

    [JsonPropertyName("sortBy")]
    public string? SortBy { get; set; }

    [JsonPropertyName("maxResults")]
    public int MaxResults { get; set; }
}

/// <summary>
/// GetTypeMembers summary
/// </summary>
public class GetTypeMembersSummary : SummaryInfo
{
    [JsonPropertyName("typeName")]
    public string? TypeName { get; set; }

    [JsonPropertyName("typeKind")]
    public string? TypeKind { get; set; }

    [JsonPropertyName("totalMembers")]
    public int TotalMembers { get; set; }

    [JsonPropertyName("publicMembers")]
    public int PublicMembers { get; set; }

    [JsonPropertyName("virtualMembers")]
    public int VirtualMembers { get; set; }

    [JsonPropertyName("inheritedMembers")]
    public int InheritedMembers { get; set; }
}

/// <summary>
/// Distribution information for type members
/// </summary>
public class TypeMembersDistribution
{
    [JsonPropertyName("byKind")]
    public Dictionary<string, int>? ByKind { get; set; }

    [JsonPropertyName("byAccessibility")]
    public Dictionary<string, int>? ByAccessibility { get; set; }

    [JsonPropertyName("bySource")]
    public Dictionary<string, int>? BySource { get; set; } // Own vs Inherited
}