# AI-First Tools Implementation Plan

This document outlines the implementation plan for five high-value tools that will significantly enhance AI agents' ability to understand and manipulate C# codebases.

## Overview

These tools are prioritized based on their value to AI workflows, focusing on capabilities that provide information AI cannot easily derive through existing tools.

### Tools to Implement

1. **Call Hierarchy Tool** - Bidirectional call graph navigation
2. **Find All Overrides Tool** - Complete virtual/abstract member tracking
3. **Solution-Wide Find/Replace Tool** - Bulk text operations with preview
4. **Code Clone Detection Tool** - Identify duplicate/similar code blocks
5. **Dependency Analysis Tool** - Show type/namespace dependencies

## Critical Implementation Requirements

### 1. Registration Pattern (MANDATORY)

Every tool MUST follow this pattern:

```csharp
using COA.CodeNav.McpServer.Attributes;

[McpServerToolType]
public class MyNewTool
{
    private readonly ILogger<MyNewTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    
    public MyNewTool(
        ILogger<MyNewTool> logger,
        RoslynWorkspaceService workspaceService)
    {
        _logger = logger;
        _workspaceService = workspaceService;
    }

    [McpServerTool(Name = "csharp_tool_name")]
    [Description(@"Tool description here.
    Returns: What the tool returns.
    Prerequisites: Call csharp_load_solution or csharp_load_project first.
    Error handling: Returns specific error codes with recovery steps.
    Use cases: When to use this tool.
    Not for: What this tool doesn't do.")]
    public async Task<object> ExecuteAsync(
        MyToolParams parameters, 
        CancellationToken cancellationToken = default)
    {
        // Implementation
    }
}
```

**Registration in Program.cs:**
```csharp
services.AddScoped<MyNewTool>();
```

### 2. Consistent Result Schema (MANDATORY)

All tools MUST return results inheriting from `ToolResultBase`:

```csharp
public class MyToolResult : ToolResultBase
{
    public override string Operation => "csharp_my_tool";
    
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }
    
    [JsonPropertyName("summary")] 
    public SummaryInfo? Summary { get; set; }
    
    // Tool-specific fields...
    
    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }
}
```

### 3. Token Management Pattern (CRITICAL)

```csharp
// CRITICAL: Pre-estimate response size BEFORE building
private int EstimateResponseTokens(List<CallHierarchyItem> items)
{
    var baseTokens = 500; // Response structure
    var perItemTokens = 150; // Adjust per tool
    
    if (items.Any())
    {
        var sample = items.Take(5).ToList();
        perItemTokens = sample.Sum(EstimateItemTokens) / sample.Count;
    }
    
    return baseTokens + (items.Count * perItemTokens);
}

// In ExecuteAsync:
const int SAFETY_TOKEN_LIMIT = 10000;

var preEstimatedTokens = EstimateResponseTokens(candidates);
if (preEstimatedTokens > SAFETY_TOKEN_LIMIT)
{
    // Progressive reduction
    for (int count = 50; count >= 10; count -= 10)
    {
        var test = candidates.Take(count).ToList();
        if (EstimateResponseTokens(test) <= SAFETY_TOKEN_LIMIT)
        {
            candidates = test;
            insights.Insert(0, $"⚠️ Response limit applied. Showing {count} of {total} results.");
            break;
        }
    }
}
```

### 4. Error Handling Pattern

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
                "Step 1: Try this first",
                "Step 2: If that fails, try this",
                "Step 3: Contact support if still failing"
            },
            SuggestedActions = new List<SuggestedAction>
            {
                new SuggestedAction
                {
                    Tool = "csharp_load_solution",
                    Description = "Load the solution first",
                    Parameters = new { solutionPath = "path/to/solution.sln" }
                }
            }
        }
    },
    Meta = new ToolMetadata { ExecutionTime = $"{sw.ElapsedMilliseconds:F2}ms" }
};
```

## Tool Implementation Details

### 1. Call Hierarchy Tool

**Tool Name:** `csharp_call_hierarchy`

**Purpose:** Provide complete call graph navigation in both directions (callers and callees).

**Parameters:**
```csharp
public class CallHierarchyParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file")]
    public required string FilePath { get; set; }
    
    [JsonPropertyName("line")]
    [Description("Line number (1-based)")]
    public required int Line { get; set; }
    
    [JsonPropertyName("column")]
    [Description("Column number (1-based)")]
    public required int Column { get; set; }
    
    [JsonPropertyName("direction")]
    [Description("Direction: 'incoming' (who calls this) or 'outgoing' (what this calls)")]
    public string Direction { get; set; } = "incoming";
    
    [JsonPropertyName("maxDepth")]
    [Description("Maximum depth to traverse (default: 5, max: 10)")]
    public int MaxDepth { get; set; } = 5;
    
    [JsonPropertyName("includeTests")]
    [Description("Include test methods in results (default: false)")]
    public bool IncludeTests { get; set; } = false;
}
```

**Implementation Steps:**
1. Use Roslyn's `SymbolFinder.FindCallersAsync` for incoming
2. Parse method body for outgoing calls using `SyntaxWalker`
3. Build hierarchical tree structure with deduplication
4. Apply token limits progressively by depth
5. Generate insights about call patterns

**Result Structure:**
```csharp
public class CallHierarchyResult : ToolResultBase
{
    [JsonPropertyName("hierarchy")]
    public CallNode? RootNode { get; set; }
    
    [JsonPropertyName("totalNodes")]
    public int TotalNodes { get; set; }
    
    [JsonPropertyName("maxDepthReached")]
    public int MaxDepthReached { get; set; }
    
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }
}

public class CallNode
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; }
    
    [JsonPropertyName("location")]
    public LocationInfo Location { get; set; }
    
    [JsonPropertyName("children")]
    public List<CallNode> Children { get; set; }
}
```

### 2. Find All Overrides Tool

**Tool Name:** `csharp_find_all_overrides`

**Purpose:** Find all overrides of virtual/abstract members and interface implementations.

**Parameters:**
```csharp
public class FindAllOverridesParams
{
    [JsonPropertyName("filePath")]
    [Description("Path to the source file")]
    public required string FilePath { get; set; }
    
    [JsonPropertyName("line")]
    [Description("Line number (1-based) of the virtual/abstract member")]
    public required int Line { get; set; }
    
    [JsonPropertyName("column")]
    [Description("Column number (1-based)")]
    public required int Column { get; set; }
    
    [JsonPropertyName("includeInterfaces")]
    [Description("Include interface implementations (default: true)")]
    public bool IncludeInterfaces { get; set; } = true;
    
    [JsonPropertyName("searchScope")]
    [Description("Search scope: 'solution' (default) or 'project'")]
    public string SearchScope { get; set; } = "solution";
}
```

**Implementation Steps:**
1. Get symbol at position
2. Check if virtual/abstract/interface member
3. Use `SymbolFinder.FindOverridesAsync` for virtual/abstract
4. Use `SymbolFinder.FindImplementationsAsync` for interfaces
5. Group results by containing type
6. Apply token management for large inheritance hierarchies

**Result Structure:**
```csharp
public class FindAllOverridesResult : ToolResultBase
{
    [JsonPropertyName("baseSymbol")]
    public SymbolInfo BaseSymbol { get; set; }
    
    [JsonPropertyName("overrides")]
    public List<OverrideInfo> Overrides { get; set; }
    
    [JsonPropertyName("groupedByType")]
    public Dictionary<string, List<OverrideInfo>> ByType { get; set; }
}
```

### 3. Solution-Wide Find/Replace Tool

**Tool Name:** `csharp_find_replace_all`

**Purpose:** Perform text-based find/replace across entire solution with preview.

**Parameters:**
```csharp
public class FindReplaceAllParams
{
    [JsonPropertyName("findPattern")]
    [Description("Text or regex pattern to find")]
    public required string FindPattern { get; set; }
    
    [JsonPropertyName("replaceWith")]
    [Description("Replacement text (supports regex groups)")]
    public required string ReplaceWith { get; set; }
    
    [JsonPropertyName("useRegex")]
    [Description("Use regex matching (default: false)")]
    public bool UseRegex { get; set; } = false;
    
    [JsonPropertyName("caseSensitive")]
    [Description("Case sensitive search (default: true)")]
    public bool CaseSensitive { get; set; } = true;
    
    [JsonPropertyName("filePattern")]
    [Description("File pattern filter (e.g., '*.cs', default: all files)")]
    public string? FilePattern { get; set; }
    
    [JsonPropertyName("preview")]
    [Description("Preview changes without applying (default: true)")]
    public bool Preview { get; set; } = true;
    
    [JsonPropertyName("maxFiles")]
    [Description("Maximum files to process (default: 100)")]
    public int MaxFiles { get; set; } = 100;
}
```

**Implementation Steps:**
1. Get all documents matching file pattern
2. Search each document for pattern matches
3. Calculate replacements with context
4. If preview, show changes with diff-like format
5. If not preview, apply changes using workspace.TryApplyChanges
6. Handle token limits by file count and match density

**Result Structure:**
```csharp
public class FindReplaceAllResult : ToolResultBase
{
    [JsonPropertyName("matches")]
    public List<FileMatches> FileMatches { get; set; }
    
    [JsonPropertyName("totalMatches")]
    public int TotalMatches { get; set; }
    
    [JsonPropertyName("filesAffected")]
    public int FilesAffected { get; set; }
    
    [JsonPropertyName("applied")]
    public bool Applied { get; set; }
    
    [JsonPropertyName("preview")]
    public List<ChangePreview> Previews { get; set; }
}
```

### 4. Code Clone Detection Tool

**Tool Name:** `csharp_detect_clones`

**Purpose:** Find duplicate or similar code blocks across the solution.

**Parameters:**
```csharp
public class DetectClonesParams
{
    [JsonPropertyName("scope")]
    [Description("Search scope: 'solution', 'project', or 'file'")]
    public string Scope { get; set; } = "solution";
    
    [JsonPropertyName("filePath")]
    [Description("File path when scope is 'file' or 'project'")]
    public string? FilePath { get; set; }
    
    [JsonPropertyName("minTokens")]
    [Description("Minimum tokens for clone detection (default: 50)")]
    public int MinTokens { get; set; } = 50;
    
    [JsonPropertyName("similarity")]
    [Description("Similarity threshold 0.0-1.0 (default: 0.8)")]
    public double Similarity { get; set; } = 0.8;
    
    [JsonPropertyName("ignoreWhitespace")]
    [Description("Ignore whitespace differences (default: true)")]
    public bool IgnoreWhitespace { get; set; } = true;
    
    [JsonPropertyName("cloneType")]
    [Description("Clone type: 'exact', 'parameterized', 'near-miss' (default: all)")]
    public string? CloneType { get; set; }
}
```

**Implementation Steps:**
1. Extract all method bodies as syntax trees
2. Convert to normalized token sequences
3. Use suffix tree or hash-based detection
4. Group clones by similarity
5. Calculate consolidation opportunities
6. Limit results by token budget

**Result Structure:**
```csharp
public class DetectClonesResult : ToolResultBase
{
    [JsonPropertyName("cloneGroups")]
    public List<CloneGroup> CloneGroups { get; set; }
    
    [JsonPropertyName("totalClones")]
    public int TotalClones { get; set; }
    
    [JsonPropertyName("savingsEstimate")]
    public CodeSavings SavingsEstimate { get; set; }
}

public class CloneGroup
{
    [JsonPropertyName("instances")]
    public List<CloneInstance> Instances { get; set; }
    
    [JsonPropertyName("similarity")]
    public double Similarity { get; set; }
    
    [JsonPropertyName("tokenCount")]
    public int TokenCount { get; set; }
    
    [JsonPropertyName("consolidationHint")]
    public string ConsolidationHint { get; set; }
}
```

### 5. Dependency Analysis Tool

**Tool Name:** `csharp_analyze_dependencies`

**Purpose:** Analyze type and namespace dependencies to understand coupling.

**Parameters:**
```csharp
public class AnalyzeDependenciesParams
{
    [JsonPropertyName("targetPath")]
    [Description("File path or namespace to analyze")]
    public required string TargetPath { get; set; }
    
    [JsonPropertyName("direction")]
    [Description("Direction: 'outgoing' (what target depends on) or 'incoming' (what depends on target)")]
    public string Direction { get; set; } = "outgoing";
    
    [JsonPropertyName("level")]
    [Description("Analysis level: 'type', 'namespace', or 'assembly'")]
    public string Level { get; set; } = "type";
    
    [JsonPropertyName("includeFramework")]
    [Description("Include .NET framework dependencies (default: false)")]
    public bool IncludeFramework { get; set; } = false;
    
    [JsonPropertyName("maxDepth")]
    [Description("Maximum dependency depth (default: 3)")]
    public int MaxDepth { get; set; } = 3;
}
```

**Implementation Steps:**
1. Parse target to identify types/namespaces
2. Walk syntax trees to find type references
3. Build dependency graph
4. Calculate coupling metrics
5. Identify circular dependencies
6. Apply token limits by depth and node count

**Result Structure:**
```csharp
public class AnalyzeDependenciesResult : ToolResultBase
{
    [JsonPropertyName("dependencies")]
    public DependencyGraph Graph { get; set; }
    
    [JsonPropertyName("metrics")]
    public DependencyMetrics Metrics { get; set; }
    
    [JsonPropertyName("circularDependencies")]
    public List<CircularDependency> Circular { get; set; }
    
    [JsonPropertyName("suggestions")]
    public List<RefactoringSuggestion> Suggestions { get; set; }
}
```

## Implementation Priority & Timeline

### Phase 1 (Week 1-2)
1. **Call Hierarchy Tool** - Most requested, builds on existing TraceCallStack
2. **Find All Overrides Tool** - Extends FindImplementations

### Phase 2 (Week 3-4)
3. **Solution-Wide Find/Replace** - High impact for refactoring
4. **Dependency Analysis Tool** - Critical for understanding impact

### Phase 3 (Week 5-6)
5. **Code Clone Detection** - Most complex, requires new algorithms

## Testing Requirements

Each tool must include:
1. Unit tests for core logic
2. Integration tests with real C# projects
3. Token limit tests with large codebases
4. Error scenario tests
5. Performance benchmarks

## Success Criteria

1. **Consistent API** - All tools follow exact same patterns
2. **Token Safety** - No tool uses >10K tokens per call
3. **Performance** - Response time <5 seconds for typical operations
4. **Error Recovery** - Clear, actionable error messages
5. **AI Insights** - Each tool provides valuable insights and next actions

## Notes

- Always test with large, real-world codebases (Roslyn itself is good)
- Monitor actual token usage vs estimates and adjust
- Consider caching for expensive operations (dependency graphs)
- Ensure all tools work with partial/broken code
- Document limitations clearly in tool descriptions