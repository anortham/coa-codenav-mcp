# CLAUDE.md

## Project Overview

COA.CodeNav.McpServer provides Roslyn-based C# code analysis and navigation tools via MCP (Model Context Protocol). Built on COA.Mcp.Framework v1.1.6.

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
├── Tools/                  # 26 MCP tool implementations
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

## Development Notes

1. Tools should provide `Insights` and `Actions` for AI agents
2. Always track execution time in `Meta.ExecutionTime`
3. Use `_resourceProvider` for large results that exceed token limits
4. Follow the established naming pattern: `csharp_` prefix for all tools
5. Keep files small - one type per file for better performance

## Testing

1. Load a solution: `csharp_load_solution`
2. Navigate code: `csharp_goto_definition`, `csharp_find_all_references`
3. Analyze: `csharp_call_hierarchy`, `csharp_code_metrics`
4. Refactor: `csharp_rename_symbol`, `csharp_extract_method`

## Reference

- Parent project: `C:\source\COA CodeSearch MCP` (search functionality)
- Framework docs: COA.Mcp.Framework v1.1.6 documentation