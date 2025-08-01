using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Models;

/// <summary>
/// AI-friendly error information with actionable recovery steps
/// </summary>
public class ErrorInfo
{
    [JsonPropertyName("code")]
    public required string Code { get; set; }
    
    [JsonPropertyName("recovery")]
    public RecoveryInfo? Recovery { get; set; }
}

/// <summary>
/// Recovery information with steps and suggested actions
/// </summary>
public class RecoveryInfo
{
    [JsonPropertyName("steps")]
    public List<string> Steps { get; set; } = new();
    
    [JsonPropertyName("suggestedActions")]
    public List<SuggestedAction> SuggestedActions { get; set; } = new();
}

/// <summary>
/// Suggested action for error recovery
/// </summary>
public class SuggestedAction
{
    [JsonPropertyName("tool")]
    public required string Tool { get; set; }
    
    [JsonPropertyName("description")]
    public required string Description { get; set; }
    
    [JsonPropertyName("parameters")]
    public object? Parameters { get; set; }
}

/// <summary>
/// Standard error codes for CodeNav
/// </summary>
public static class ErrorCodes
{
    public const string WORKSPACE_NOT_LOADED = "WORKSPACE_NOT_LOADED";
    public const string DOCUMENT_NOT_FOUND = "DOCUMENT_NOT_FOUND";
    public const string SYMBOL_NOT_FOUND = "SYMBOL_NOT_FOUND";
    public const string NO_SYMBOL_AT_POSITION = "NO_SYMBOL_AT_POSITION";
    public const string COMPILATION_ERROR = "COMPILATION_ERROR";
    public const string SEMANTIC_MODEL_UNAVAILABLE = "SEMANTIC_MODEL_UNAVAILABLE";
    public const string INTERNAL_ERROR = "INTERNAL_ERROR";
    public const string INVALID_PARAMETERS = "INVALID_PARAMETERS";
}