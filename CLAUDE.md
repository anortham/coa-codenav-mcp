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

### Tool Result Schema Consistency

**CRITICAL**: All tools MUST follow the consistent result schema pattern established by CodeSearch MCP. This ensures a uniform API surface for AI agents and maintainability.

#### Required Result Structure

Every tool must return a result class that inherits from `ToolResultBase` or create a specific result class in `Models/ToolResults.cs`:

```csharp
public class MyToolResult : ToolResultBase
{
    public override string Operation => "roslyn_my_tool";
    
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }
    
    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }
    
    // Tool-specific result fields...
    
    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }
    
    [JsonPropertyName("distribution")]
    public MyDistribution? Distribution { get; set; }  // Optional
}
```

#### Standard Fields (from ToolResultBase)

1. **Success** (bool) - ALWAYS set to indicate success/failure
2. **Operation** (string) - Tool name for tracking
3. **Message** (string) - Human-readable result message
4. **Error** (ErrorInfo) - Structured error with recovery steps
5. **Insights** (List<string>) - AI-friendly insights about results
6. **Actions** (List<NextAction>) - Suggested next tool calls
7. **Meta** (ToolMetadata) - Execution time, truncation info
8. **ResourceUri** (string) - URI for full results if truncated

#### Error Handling Pattern

```csharp
return new MyToolResult
{
    Success = false,
    Message = "Descriptive error message",
    Error = new ErrorInfo
    {
        Code = ErrorCodes.SPECIFIC_ERROR_CODE,
        Recovery = new RecoveryInfo
        {
            Steps = new List<string>
            {
                "Step 1 to recover",
                "Step 2 to recover"
            },
            SuggestedActions = new List<SuggestedAction>
            {
                new SuggestedAction
                {
                    Tool = "suggested_tool",
                    Description = "What this will do",
                    Parameters = new { /* params */ }
                }
            }
        }
    },
    Query = new QueryInfo { /* capture original query */ },
    Meta = new ToolMetadata 
    { 
        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
    }
};
```

#### Token Management - CRITICAL PATTERN

**IMPORTANT**: Tools MUST implement pre-estimation and progressive reduction to prevent token overflow. We spent extensive time fine-tuning this in CodeSearch and it's equally critical here. If a tool uses 10% of the context window per call, users need to clear/compact every few minutes, seriously degrading usefulness.

**Key principles:**
1. **Pre-estimate response size** BEFORE building the response
2. **Apply safety limits** (typically 5K-10K tokens max)
3. **Progressive reduction** when over limit
4. **Lightweight results** with pagination hints
5. **NextActions** to guide agents on getting more results

**Implementation pattern from CodeSearch:**

```csharp
// CRITICAL: Pre-estimate BEFORE building response
private int EstimateResponseTokens(List<ResultType> results)
{
    var baseTokens = 500; // Response structure overhead
    var perItemTokens = 100; // Adjust based on your data
    
    // Sample first few items for accurate estimation
    if (results.Any())
    {
        var sample = results.Take(Math.Min(5, results.Count)).ToList();
        perItemTokens = sample.Sum(item => EstimateItemTokens(item)) / sample.Count;
    }
    
    return baseTokens + (results.Count * perItemTokens) + analysisOverhead;
}

// In ExecuteAsync:
const int SAFETY_TOKEN_LIMIT = 10000; // Max 10K tokens per response

// First determine candidates
var candidateResults = results.Take(requestedMax).ToList();

// Pre-estimate tokens
var preEstimatedTokens = EstimateResponseTokens(candidateResults);

// Apply progressive reduction if needed
if (preEstimatedTokens > SAFETY_TOKEN_LIMIT)
{
    // Try progressively smaller counts
    for (int count = 50; count >= 10; count -= 10)
    {
        var testResults = results.Take(count).ToList();
        if (EstimateResponseTokens(testResults) <= SAFETY_TOKEN_LIMIT)
        {
            candidateResults = testResults;
            break;
        }
    }
    
    // Add warning to insights
    insights.Insert(0, $"⚠️ Response size limit applied. Showing {candidateResults.Count} of {totalResults} results.");
}

// ALWAYS provide pagination hints
if (results.Count > candidateResults.Count)
{
    // Store full results in resource provider
    var resourceUri = _resourceProvider.StoreAnalysisResult(...);
    
    // Add NextAction for getting more results
    actions.Add(new NextAction
    {
        Id = "get_more_results",
        Description = "Get additional results",
        ToolName = "tool_name",
        Parameters = new { maxResults = 500 },
        Priority = "high"
    });
}
```

This pattern ensures:
- Tools never blow up the context window
- Agents get useful results on first try
- Clear guidance on how to get more data if needed
- Graceful degradation rather than errors

#### AI-Optimized Features

1. **Insights**: Generate 3-5 contextual insights about the results
2. **NextActions**: Suggest logical follow-up tool calls with pre-filled parameters
3. **Progressive Disclosure**: Use summary mode for large results
4. **Execution Time**: Always track and report execution time
5. **Query Context**: Always capture the original query parameters

#### Example Implementation Pattern

```csharp
public async Task<object> ExecuteAsync(MyToolParams parameters, CancellationToken cancellationToken = default)
{
    var startTime = DateTime.UtcNow;
    
    try
    {
        // Tool logic here...
        
        return new MyToolResult
        {
            Success = true,
            Message = "Clear success message",
            // Tool-specific fields...
            Query = new QueryInfo
            {
                FilePath = parameters.FilePath,
                Position = new PositionInfo 
                { 
                    Line = parameters.Line, 
                    Column = parameters.Column 
                }
            },
            Summary = new SummaryInfo
            {
                TotalFound = results.Count,
                Returned = returnedResults.Count,
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
            },
            Insights = GenerateInsights(results),
            Actions = GenerateNextActions(results),
            Meta = new ToolMetadata
            {
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms",
                Truncated = shouldTruncate
            }
        };
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error in tool execution");
        return new MyToolResult
        {
            Success = false,
            Message = $"Error: {ex.Message}",
            Error = new ErrorInfo
            {
                Code = ErrorCodes.INTERNAL_ERROR,
                Recovery = new RecoveryInfo
                {
                    Steps = new List<string>
                    {
                        "Check the server logs for detailed error information",
                        "Verify the solution/project is loaded correctly",
                        "Try the operation again"
                    }
                }
            },
            Query = /* capture original query */,
            Meta = new ToolMetadata 
            { 
                ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms" 
            }
        };
    }
}
```

### Testing
To test the server:
1. Build the project: `dotnet build --configuration Debug`
2. Run the server: `dotnet run --project COA.CodeNav.McpServer/COA.CodeNav.McpServer.csproj`
3. Connect with an MCP client (like Claude Desktop or a test client)
4. Load a C# solution using workspace tools
5. Test tools with file paths and positions
