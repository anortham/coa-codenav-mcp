namespace COA.CodeNav.McpServer.Tools;

/// <summary>
/// Base interface for all MCP tools
/// </summary>
public interface ITool
{
    /// <summary>
    /// Gets the name of the tool as exposed to MCP clients
    /// </summary>
    string ToolName { get; }
    
    /// <summary>
    /// Gets the human-readable description of what this tool does
    /// </summary>
    string Description { get; }
}