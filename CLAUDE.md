# CLAUDE.md

## Project Overview

COA.CodeNav.McpServer provides comprehensive C# and TypeScript code analysis and navigation tools via MCP (Model Context Protocol). Built on COA.Mcp.Framework v1.7.19.

## Build Commands

```bash
# Build from project directory (preferred)
cd COA.CodeNav.McpServer
dotnet build --configuration Debug

# Run the server
dotnet run --project COA.CodeNav.McpServer/COA.CodeNav.McpServer.csproj
```

## Project Structure

```
COA.CodeNav.McpServer/
├── Program.cs              # MCP server setup, tool registration
├── Tools/                  # 45 MCP tool implementations (31 C# + 14 TypeScript)
├── Services/              # RoslynWorkspaceService, DocumentService
├── Infrastructure/        # MSBuildWorkspaceManager
├── Models/               # Result types inheriting from ToolResultBase
└── Constants/            # Error codes, tool names
```

## Creating New Tools with COA.Mcp.Framework

Tools inherit from `McpToolBase<TParams, TResult>` and follow the framework pattern:

```csharp
public class MyTool : McpToolBase<MyParams, MyResult>
{
    public override string Name => "csharp_my_tool";
    public override string Description => @"Brief description.
Returns: What it returns.
Prerequisites: Requirements.
Use cases: When to use.";

    protected override async Task<MyResult> ExecuteInternalAsync(
        MyParams parameters, 
        CancellationToken cancellationToken)
    {
        // Implementation
        return new MyResult { Success = true, ... };
    }
}
```

Register in `Program.cs`:
```csharp
builder.Services.AddScoped<MyTool>();
```

## Result Schema Pattern

All results inherit from `ToolResultBase`:

```csharp
public class MyResult : ToolResultBase
{
    public override string Operation => "csharp_my_tool";
    
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }
    
    [JsonPropertyName("summary")] 
    public SummaryInfo? Summary { get; set; }
    
    // Tool-specific fields...
}
```

## Token Management

**Critical**: Prevent context window overflow:

```csharp
const int SAFETY_TOKEN_LIMIT = 10000;

// Pre-estimate response size
var estimatedTokens = EstimateResponseTokens(results);

// Progressive reduction if needed
if (estimatedTokens > SAFETY_TOKEN_LIMIT)
{
    results = results.Take(50).ToList();
    insights.Add($"⚠️ Showing {results.Count} of {total} results.");
}
```

## Error Handling

Return structured errors with recovery steps:

```csharp
return new MyResult
{
    Success = false,
    Error = new ErrorInfo
    {
        Code = ErrorCodes.DOCUMENT_NOT_FOUND,
        Recovery = new RecoveryInfo
        {
            Steps = new[] { "Load solution first", "Verify file path" },
            SuggestedActions = new List<SuggestedAction> { ... }
        }
    }
};
```

## Key Services

- **RoslynWorkspaceService**: Manages MSBuild workspaces and documents
- **DocumentService**: Handles document retrieval and semantic model access
- **MSBuildWorkspaceManager**: Loads solutions/projects with proper error handling

## AI Agent Enhancements

### Position Tolerance for Navigation Tools

Critical C# navigation tools now include position tolerance to handle AI agent positioning inaccuracies:

**Enhanced Tools:**
- `HoverTool` - Enhanced hover information with position tolerance
- `GoToDefinitionTool` - Robust symbol navigation with fallback positioning
- `FindAllReferencesTool` - Reference finding with positional flexibility

**Algorithm:**
```csharp
// Position tolerance tries multiple nearby positions when exact position fails:
// 1. Exact position first
// 2. Previous character (position - 1)
// 3. Next character (position + 1) 
// 4. Two positions back (position - 2)
// All attempts stay within the same line for safety
```

**Implementation:**
Each enhanced tool includes a `FindSymbolWithToleranceAsync()` method that uses `SymbolFinder.FindSymbolAtPositionAsync()` with fallback positioning, inspired by cclsp (C# Language Server Protocol) patterns.

### TypeScript Indexing Standardization

All TypeScript tools now use **1-based indexing** consistently with C# tools:

**Standardized Tools:**
- `ts_hover` - TypeScript hover information
- `ts_goto_definition` - Navigate to TypeScript definitions  
- `ts_find_all_references` - Find TypeScript symbol references
- `ts_find_implementations` - Find interface implementations
- `ts_rename_symbol` - Rename TypeScript symbols safely
- `ts_call_hierarchy` - Show TypeScript call hierarchies
- `ts_apply_quick_fix` - Apply compiler quick fixes

**Parameter Convention:**
```typescript
// All TypeScript tools now use 1-based indexing:
{
  "line": 1,        // First line is 1 (not 0)
  "character": 1    // First character is 1 (not 0)
}
```

**Documentation:** Each tool description explicitly specifies 1-based indexing to prevent AI agent confusion.

## Development Notes

1. Tools should provide `Insights` and `Actions` for AI agents
2. Always track execution time in `Meta.ExecutionTime`
3. Use `_resourceProvider` for large results that exceed token limits
4. Follow the established naming pattern: `csharp_` prefix for all tools
5. Keep files small - one type per file for better performance
6. **Position Tolerance**: Critical navigation tools include fallback positioning for AI agent reliability
7. **Consistent Indexing**: All tools use 1-based indexing (line=1, column=1) for unified behavior

## Testing

### C# Tools
1. Load a solution: `csharp_load_solution`
2. Navigate code: `csharp_goto_definition`, `csharp_find_all_references`, `csharp_hover`
3. Analyze: `csharp_call_hierarchy`, `csharp_code_metrics`
4. Refactor: `csharp_rename_symbol`, `csharp_extract_method`

### TypeScript Tools  
1. Load TypeScript project: `ts_load_tsconfig`
2. Navigate TypeScript: `ts_goto_definition`, `ts_find_all_references`, `ts_hover`
3. Analyze TypeScript: `ts_call_hierarchy`, `ts_find_implementations`
4. Refactor TypeScript: `ts_rename_symbol`, `ts_apply_quick_fix`, `ts_organize_imports`

**Note**: All tools use 1-based indexing. Position tolerance in C# navigation tools handles slight positioning inaccuracies.

## Reference

- Parent project: `C:\source\COA CodeSearch MCP` (search functionality)
- Framework docs: COA.Mcp.Framework v1.4.1 documentation