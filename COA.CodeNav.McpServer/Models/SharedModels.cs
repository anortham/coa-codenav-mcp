using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Models;

/// <summary>
/// Represents a suggested next action for AI agents
/// </summary>
public class NextAction
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("description")]
    public required string Description { get; set; }

    [JsonPropertyName("toolName")]
    public required string ToolName { get; set; }

    [JsonPropertyName("parameters")]
    public object? Parameters { get; set; }

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "medium";
}

