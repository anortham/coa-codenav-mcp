using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Models;

/// <summary>
/// Base class for all tool results following the CodeSearch MCP pattern
/// </summary>
public abstract class ToolResultBase
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("operation")]
    public abstract string Operation { get; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public ErrorInfo? Error { get; set; }

    [JsonPropertyName("insights")]
    public List<string>? Insights { get; set; }

    [JsonPropertyName("actions")]
    public List<NextAction>? Actions { get; set; }

    [JsonPropertyName("meta")]
    public ToolMetadata? Meta { get; set; }

    [JsonPropertyName("resourceUri")]
    public string? ResourceUri { get; set; }
}

/// <summary>
/// Metadata about the tool execution
/// </summary>
public class ToolMetadata
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "full";

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("tokens")]
    public int? Tokens { get; set; }

    [JsonPropertyName("cached")]
    public string? Cached { get; set; }

    [JsonPropertyName("executionTime")]
    public string? ExecutionTime { get; set; }
}

/// <summary>
/// Standard query information
/// </summary>
public class QueryInfo
{
    [JsonPropertyName("workspace")]
    public string? Workspace { get; set; }

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("position")]
    public PositionInfo? Position { get; set; }

    [JsonPropertyName("targetSymbol")]
    public string? TargetSymbol { get; set; }

    [JsonPropertyName("additionalParams")]
    public Dictionary<string, object>? AdditionalParams { get; set; }
}

/// <summary>
/// Position information for location-based queries
/// </summary>
public class PositionInfo
{
    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }
}

/// <summary>
/// Standard summary information
/// </summary>
public class SummaryInfo
{
    [JsonPropertyName("totalFound")]
    public int TotalFound { get; set; }

    [JsonPropertyName("returned")]
    public int Returned { get; set; }

    [JsonPropertyName("executionTime")]
    public string? ExecutionTime { get; set; }

    [JsonPropertyName("symbolInfo")]
    public SymbolSummary? SymbolInfo { get; set; }
}

/// <summary>
/// Symbol summary information
/// </summary>
public class SymbolSummary
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("containingType")]
    public string? ContainingType { get; set; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }
}

/// <summary>
/// Standard results summary
/// </summary>
public class ResultsSummary
{
    [JsonPropertyName("included")]
    public int Included { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }
}