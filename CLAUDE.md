# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

COA.CodeNav.McpServer is a .NET 9.0 MCP (Model Context Protocol) server focused on providing Language Server Protocol (LSP) services for code analysis and navigation. This project is a spin-off from the COA CodeSearch MCP server, which originally included LSP services (Roslyn, TypeScript, and Razor) that were removed to focus on core search functionality.

### Background

- **Parent Project**: COA CodeSearch MCP (located at `C:\source\COA CodeSearch MCP`) - A stable, working MCP server focused on code search functionality
- **Purpose**: Resurrect and properly implement the LSP services that were removed from CodeSearch
- **Initial Focus**: Roslyn-based C# code analysis, ensuring rock-solid implementation before expanding to other language servers
- **Shared Foundation**: Uses the same proven COA.Mcp.Protocol implementation as CodeSearch

## Key Dependencies

- **COA.Mcp.Protocol** (v1.0.0) - Core MCP protocol implementation

## Build Commands

```bash
# Build the project
dotnet build

# Run the application
dotnet run --project COA.CodeNav.McpServer/COA.CodeNav.McpServer.csproj

# Clean build artifacts
dotnet clean

# Restore NuGet packages
dotnet restore
```

## Project Structure

- `COA.CodeNav.McpServer/` - Main project directory
  - `Program.cs` - Entry point (currently contains minimal "Hello, World!" implementation)
  - `COA.CodeNav.McpServer.csproj` - Project file targeting .NET 9.0
- `COA.CodeNav.McpServer.sln` - Solution file
- `.claude/settings.local.json` - Claude Code permissions configuration for MCP tools

## Development Guidelines

1. This project uses .NET 9.0 with nullable reference types enabled
2. Implicit usings are enabled for common namespaces
3. The project references COA.Mcp.Protocol for MCP functionality
4. Standard Visual Studio .gitignore is in place
5. Working CodeSearch MCP code with COA.Mcp.Protocol is available for reference at "c:\source\COA CodeSearch MCP"
6. Don't let files get too large, this slows down read performance. Prefer a file per type.

## Architecture Goals

1. **Separation of Concerns**: Keep LSP services cleanly separated from MCP protocol handling
2. **Extensibility**: Design for easy addition of new language servers (TypeScript, Razor, etc.) in the future
3. **Performance**: Ensure LSP operations don't block MCP communication
4. **Reliability**: Implement proper error handling and recovery mechanisms

## Implementation Roadmap

### Phase 1: Roslyn LSP Implementation (Current Focus)

- Set up Roslyn workspace and compiler infrastructure
- Implement core code analysis tools (hover, go-to-definition, find references)
- Add code navigation features (symbol search, outline)
- Implement diagnostics and code fixes
- Ensure proper caching and performance optimization

### Phase 2: Future Expansions (After Roslyn is stable)

- TypeScript LSP integration
- Razor LSP support
- Additional language servers as needed

## Related Documentation

- `/docs/roslyn-lsp-implementation-plan.md` - Detailed step-by-step checklist for Roslyn LSP implementation

## Development Workflow - Important

### Dogfooding During Development
While developing this project, we are dogfooding it by running the release build in the current Claude session. This means:

1. **Always build in Debug mode** when checking compilation: `dotnet build --configuration Debug`
2. **Never build Release mode** while the session is active (it's being used by Claude)
3. **To test new changes in Claude**:
   - User must exit the current session
   - Build release: `dotnet build --configuration Release`
   - Start a new session to load the updated release build

### Build Commands
```bash
# For compilation checks during development (safe to run anytime)
dotnet build --configuration Debug

# For testing in Claude (requires session restart)
dotnet build --configuration Release
```

## Current Status

### Infrastructure Complete
- [x] MSBuild workspace manager
- [x] Document tracking system  
- [x] Roslyn workspace service
- [x] Symbol caching layer
- [x] MCP server foundation
- [x] Attribute-based tool discovery
- [x] Tool registry system
- [x] Resource infrastructure
- [x] AI-optimized tool patterns

### Phase 1 Goals
- [x] Go to Definition (implemented with AI optimizations)
- [x] Find All References (implemented with NextActions)
- [x] Hover information
- [x] Trace Call Stack (forward and backward)
- [x] Rename Symbol (with preview and conflict detection)
- [ ] Symbol Search
- [ ] Find Implementations
- [ ] Document symbols

### Creating New Tools

To create a new MCP tool, use the attribute-based pattern:

```csharp
using COA.CodeNav.McpServer.Attributes;

[McpServerToolType]
public class MyNewTool
{
    private readonly ILogger<MyNewTool> _logger;
    // ... other dependencies

    [McpServerTool(Name = "tool_name")]
    [Description(@"Tool description here.
    Returns: What the tool returns.
    Prerequisites: Any requirements.
    Use cases: When to use this tool.")]
    public async Task<object> ExecuteAsync(MyToolParams parameters, CancellationToken cancellationToken = default)
    {
        // Tool implementation
    }
}

public class MyToolParams
{
    [JsonPropertyName("param1")]
    [Description("Description of parameter 1")]
    public required string Param1 { get; set; }
    
    // ... other parameters
}
```

Key points:
- Class must have `[McpServerToolType]` attribute
- Method must have `[McpServerTool]` attribute with Name
- Method must have `[Description]` attribute from COA.CodeNav.McpServer.Attributes
- Parameters should use `[JsonPropertyName]` for JSON serialization
- Parameters should use `[Description]` from COA.CodeNav.McpServer.Attributes (not System.ComponentModel)
- Tool class must be registered in Program.cs with DI: `services.AddScoped<MyNewTool>();`

### Testing
To test the server:
1. Build the project: `dotnet build --configuration Debug`
2. Run the server: `dotnet run --project COA.CodeNav.McpServer/COA.CodeNav.McpServer.csproj`
3. Connect with an MCP client (like Claude Desktop or a test client)
4. Load a C# solution using workspace tools
5. Test tools with file paths and positions
