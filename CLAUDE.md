# CLAUDE.md

## Project Overview

COA.CodeNav.McpServer provides comprehensive C# and TypeScript code analysis and navigation tools via MCP (Model Context Protocol). Built on COA.Mcp.Framework v1.7.19.

## Build Commands

```bash
# Build and run
cd COA.CodeNav.McpServer
dotnet build --configuration Debug
dotnet run --project COA.CodeNav.McpServer/COA.CodeNav.McpServer.csproj
```

## Key Development Patterns

- Tools inherit from `McpToolBase<TParams, TResult>` and register via DI
- Results inherit from `ToolResultBase` with structured error handling  
- Token limits enforced at 10K tokens with progressive reduction
- All tools use 1-based indexing (line=1, column=1) for consistency
- C# navigation tools include position tolerance for AI agent reliability

## Quick Testing

**C# Workflow:**
1. `csharp_load_solution` → `csharp_hover` → `csharp_goto_definition` → `csharp_find_all_references`

**TypeScript Workflow:**  
1. `ts_load_tsconfig` → `ts_hover` → `ts_goto_definition` → `ts_find_all_references`

**Enhanced Tools:** HoverTool, GoToDefinitionTool, FindAllReferencesTool include position tolerance for AI agents.

## Development Standards

- Use `_resourceProvider` for large results that exceed token limits
- Provide `Insights` and `Actions` for AI agents in all tool responses
- Follow naming pattern: `csharp_` prefix for C# tools, `ts_` for TypeScript
- Track execution time in `Meta.ExecutionTime`
- One type per file for better performance