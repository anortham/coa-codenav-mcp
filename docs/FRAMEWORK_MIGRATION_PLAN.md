# COA CodeNav MCP - Framework Migration Plan (Updated for Framework v1.1.x)

> ## 🎉 **MIGRATION COMPLETE** - 2025-08-07
> **All 26 tools successfully migrated to COA.Mcp.Framework v1.1.0!**
> - ✅ 100% of tools migrated
> - ✅ ~5000+ lines of code eliminated
> - ✅ Main project builds with 0 errors
> - ✅ Ready for HTTP/WebSocket transport configuration

> **Framework v1.1.x Key Updates:**
> - ✅ **HTTP/WebSocket Transport**: Web client support beyond stdio
> - ✅ **Type-Safe Schema System**: Automatic schema generation from attributes
> - ✅ **C# Client Library**: Strongly-typed client for testing and integration
> - ✅ **Breaking Change**: `GetInputSchema()` now returns `IJsonSchema` (not `object`)
> - ✅ **Protocol v1.3.x**: Included as dependency (remove direct references)

## Executive Summary

This document provides a comprehensive plan for migrating COA CodeNav MCP from its current implementation to use the COA MCP Framework v1.1.x. The migration will reduce code complexity by ~35-40%, improve token management, provide HTTP/WebSocket transport options, and enable a more maintainable architecture with type-safe schema support.

### Key Benefits

1. **Code Reduction**: Eliminate ~3000+ lines of boilerplate code
   - Remove 10+ duplicate infrastructure files
   - Delete 200+ lines per tool of validation/error handling
   - Replace manual implementations with framework features
2. **Token Management**: Advanced pre-estimation and progressive reduction prevents context overflow
3. **Type Safety**: Compile-time validation with `McpToolBase<TParams, TResult>`
4. **Consistent Error Handling**: AI-friendly error messages with recovery steps
5. **Resource Management**: Compressed storage with automatic truncation handling
6. **Testing Infrastructure**: Built-in testing utilities and benchmarks
7. **Transport Options** (New in v1.1.x): HTTP/WebSocket support for web clients
8. **Type-Safe Schema** (New in v1.1.x): `IJsonSchema` and `JsonSchema<T>` for automatic schema generation
9. **C# Client Library** (New in v1.1.x): Strongly-typed client for testing and integration

### Timeline

- **Week 1**: Foundation migration (models, infrastructure)
- **Week 2-3**: Tool migration (20+ tools converted)
- **Week 4**: Advanced features and optimization
- **Week 5**: Testing, benchmarking, and cleanup

### Impact Assessment

- **Development Velocity**: 50% faster tool development after migration
- **Maintenance Burden**: 40% reduction in maintenance overhead
- **Token Efficiency**: 60% improvement in context window usage
- **Test Coverage**: 30% increase with framework testing utilities

---

## Pre-Migration Setup

### Environment Setup

1. **Clone CodeNav to Migration Branch**

```bash
# Clone to separate directory for migration work
cd C:\source
git clone C:\source\COA CodeNav MCP "C:\source\COA CodeNav MCP Migration"
cd "C:\source\COA CodeNav MCP Migration"
git checkout -b feature/framework-migration
```

2. **Add Framework References**

```xml
<!-- Update COA.CodeNav.McpServer.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <!-- existing properties -->
  </PropertyGroup>

  <ItemGroup>
    <!-- Remove old COA.Mcp.Protocol reference -->
    <!-- <PackageReference Include="COA.Mcp.Protocol" Version="1.0.0" /> -->

    <!-- Add Framework packages (v1.1.x includes Protocol as dependency) -->
    <PackageReference Include="COA.Mcp.Framework" Version="1.1.0" />
    <PackageReference Include="COA.Mcp.Framework.TokenOptimization" Version="1.1.0" />
    <PackageReference Include="COA.Mcp.Framework.Testing" Version="1.1.0" />
    
    <!-- Optional: Add client library for testing -->
    <PackageReference Include="COA.Mcp.Client" Version="1.0.0" />
  </ItemGroup>
</Project>
```

3. **NuGet Configuration**

- use the Nuget.config.local

### Branch Strategy

- **main**: Current working implementation
- **feature/framework-migration**: Migration work
- **feature/framework-migration-backup**: Checkpoint backups

### Pre-Migration Checklist

- [ ] CodeNav cloned to separate directory
- [ ] Migration branch created
- [ ] Framework packages referenced
- [ ] NuGet.config updated
- [ ] Current tests passing
- [ ] Backup branch created
- [ ] Release build still works in original directory

---

## Framework v1.1.x New Features

### Transport Layer (New)

The Framework now supports multiple transport types beyond stdio:

```csharp
// Option 1: Continue using stdio (default)
var builder = new McpServerBuilder()
    .WithServerInfo("COA CodeNav MCP Server", "2.0.0");
    
// Option 2: Add HTTP/WebSocket support for web clients
var builder = new McpServerBuilder()
    .WithServerInfo("COA CodeNav MCP Server", "2.0.0")
    .ConfigureHttpTransport(options =>
    {
        options.Port = 5000;
        options.EnableWebSocket = true;
        options.EnableCors = true;
        options.UseHttps = false; // Development only
    });
```

### Type-Safe Schema System (New)

Tools no longer need manual GetInputSchema implementation:

```csharp
// OLD - Manual schema building
public override object GetInputSchema()
{
    return new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["filePath"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["description"] = "Path to file"
            }
        },
        ["required"] = new[] { "filePath" }
    };
}

// NEW - Automatic schema generation from attributes
// No override needed! Base class generates from data annotations
// Schema is type-safe with JsonSchema<TParams>
```

### C# Client Library (New)

For testing and integration:

```csharp
// Create typed client for testing
var client = McpClientBuilder
    .Create("http://localhost:5000")
    .WithRetry(3, 1000)
    .BuildTyped<GoToDefinitionParams, GoToDefinitionResult>();

await client.ConnectAsync();
var result = await client.CallToolAsync("csharp_goto_definition", new GoToDefinitionParams
{
    FilePath = "Test.cs",
    Line = 10,
    Column = 5
});
```

### Important Interface Changes

1. **IMcpTool Interface Update**: Now uses `IJsonSchema` instead of `object`:
   ```csharp
   // Framework automatically handles this
   IJsonSchema IMcpTool.GetInputSchema() => GetInputSchema();
   ```

2. **AIAction vs NextAction**: Framework uses `AIAction` which has additional fields:
   ```csharp
   public class AIAction
   {
       public string Action { get; set; }  // Tool name
       public string Description { get; set; }
       public string? Rationale { get; set; }  // New
       public string? Category { get; set; }   // New
       public Dictionary<string, object>? Parameters { get; set; }
       public int Priority { get; set; } = 50; // New
   }
   ```

---

## Code Removal Overview

### Files to be Deleted (Complete Removal)

The following CodeNav files will be completely deleted as they are replaced by framework functionality:

| File                                            | Framework Replacement                                | Lines Saved    |
| ----------------------------------------------- | ---------------------------------------------------- | -------------- |
| `Models/ToolResultBase.cs`                      | `COA.Mcp.Framework.Models.ToolResultBase`            | ~140           |
| `Models/ErrorModels.cs`                         | `COA.Mcp.Framework.Models.ErrorInfo`, etc.           | ~100           |
| `Models/SharedModels.cs`                        | `COA.Mcp.Framework.Models.AIAction`                  | ~25            |
| `Utilities/TokenEstimator.cs`                   | `COA.Mcp.Framework.TokenOptimization.TokenEstimator` | ~260           |
| `Infrastructure/ToolRegistry.cs`                | `COA.Mcp.Framework.Registration.McpToolRegistry`     | ~150           |
| `Infrastructure/AttributeBasedToolDiscovery.cs` | Built into framework                                 | ~200           |
| `Attributes/McpServerToolAttribute.cs`          | `COA.Mcp.Framework.Attributes`                       | ~30            |
| `Attributes/McpServerToolTypeAttribute.cs`      | `COA.Mcp.Framework.Attributes`                       | ~20            |
| `Attributes/DescriptionAttribute.cs`            | `COA.Mcp.Framework.Attributes`                       | ~25            |
| `Tools/ITool.cs`                                | `COA.Mcp.Framework.Interfaces.IMcpTool`              | ~15            |
| **Total Files: 10**                             |                                                      | **~965 lines** |

### Code to be Removed from Existing Files

Each tool file (26 tools) will have the following code removed:

| Pattern                     | Lines per Tool | Total (26 tools) |
| --------------------------- | -------------- | ---------------- |
| Manual parameter validation | ~30-50         | ~1000            |
| Try-catch error handling    | ~20-30         | ~650             |
| Token counting/limiting     | ~15-20         | ~450             |
| Execution time tracking     | ~5-10          | ~200             |
| Error response building     | ~40-60         | ~1300            |
| **Total per tool**          | **~110-170**   | **~3600 lines**  |

### Summary of Code Reduction

- **Files deleted**: 10 files, ~965 lines
- **Code removed from tools**: ~3600 lines
- **Infrastructure simplification**: ~500 lines
- **Total reduction**: **~5000+ lines** (>35% of codebase)

---

## Phase 1: Foundation Migration (Week 1)

### 1.1 Namespace Updates

**Step 1: Update using statements globally**

```csharp
// OLD - In all files
using COA.Mcp.Protocol;

// NEW - Replace with
using COA.Mcp.Framework;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Interfaces;
```

### 1.2 Model Migration

#### 1.2.1 Migrate ToolResultBase

**Before (COA.CodeNav.McpServer\Models\ToolResultBase.cs):**

```csharp
namespace COA.CodeNav.McpServer.Models;

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
```

**After (Use Framework's ToolResultBase):**

```csharp
// DELETE COA.CodeNav.McpServer\Models\ToolResultBase.cs
// UPDATE all tool result classes to use framework base:

using COA.Mcp.Framework.Models;

public class GoToDefinitionToolResult : ToolResultBase  // Now using framework's base
{
    public override string Operation => ToolNames.GoToDefinition;

    // Update Actions property type
    [JsonPropertyName("actions")]
    public new List<AIAction>? Actions { get; set; }  // Use framework's AIAction

    // Update Meta property type
    [JsonPropertyName("meta")]
    public new ToolExecutionMetadata? Meta { get; set; }  // Use framework's metadata

    // Keep tool-specific properties unchanged
    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("locations")]
    public List<LocationInfo>? Locations { get; set; }
}
```

#### 1.2.2 Migrate Error Models

**Before (COA.CodeNav.McpServer\Models\ErrorModels.cs):**

```csharp
namespace COA.CodeNav.McpServer.Models;

public class ErrorInfo
{
    [JsonPropertyName("code")]
    public required string Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("recovery")]
    public RecoveryInfo? Recovery { get; set; }
}

public static class ErrorCodes
{
    public const string INTERNAL_ERROR = "INTERNAL_ERROR";
    public const string DOCUMENT_NOT_FOUND = "DOCUMENT_NOT_FOUND";
    // ... project-specific codes
}
```

**After:**

```csharp
// DELETE ErrorInfo, RecoveryInfo, SuggestedAction classes
// KEEP ErrorCodes but extend framework's BaseErrorCodes

using COA.Mcp.Framework.Models;

namespace COA.CodeNav.McpServer.Constants;

public static class ErrorCodes : BaseErrorCodes  // Extend framework codes
{
    // Keep project-specific codes
    public const string DOCUMENT_NOT_FOUND = "DOCUMENT_NOT_FOUND";
    public const string NO_SYMBOL_AT_POSITION = "NO_SYMBOL_AT_POSITION";
    public const string SEMANTIC_MODEL_UNAVAILABLE = "SEMANTIC_MODEL_UNAVAILABLE";
    public const string WORKSPACE_NOT_LOADED = "WORKSPACE_NOT_LOADED";
    // Remove duplicates that exist in BaseErrorCodes
}
```

#### 1.2.3 Update NextAction to AIAction

**Before:**

```csharp
public class NextAction
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("toolName")]
    public required string ToolName { get; set; }

    [JsonPropertyName("parameters")]
    public object? Parameters { get; set; }
}
```

**After:**

```csharp
// DELETE NextAction class
// Use framework's AIAction instead:

using COA.Mcp.Framework.Models;

// When creating actions:
var action = new AIAction
{
    Action = "csharp_find_all_references",  // Tool name
    Description = "Find all references to this symbol",
    Parameters = new Dictionary<string, object>
    {
        ["filePath"] = filePath,
        ["line"] = line,
        ["column"] = column
    },
    Priority = 80,  // Higher priority
    Category = "navigation",
    Rationale = "Understanding usage patterns helps with refactoring"
};
```

### 1.3 Infrastructure Migration

#### 1.3.1 Update Program.cs

**Before:**

```csharp
var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        // Manual service registration
        services.AddSingleton<MSBuildWorkspaceManager>();
        services.AddSingleton<RoslynWorkspaceService>();

        // Manual tool registration
        services.AddScoped<GoToDefinitionTool>();
        services.AddScoped<FindAllReferencesTool>();
        // ... 20+ more tools

        services.AddSingleton<CodeNavMcpServer>();
        services.AddHostedService<CodeNavMcpServer>(provider =>
            provider.GetRequiredService<CodeNavMcpServer>());
    })
    .Build();
```

**After:**

```csharp
using COA.Mcp.Framework.Server;
using COA.Mcp.Framework.TokenOptimization;

// Use framework's builder
var builder = new McpServerBuilder()
    .WithServerInfo("COA CodeNav MCP Server", "2.0.0")
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSerilog(configuration);
    })
    .ConfigureTokenOptimization(options =>
    {
        options.DefaultTokenLimit = 10000;
        options.Level = TokenOptimizationLevel.Balanced;
        options.EnableAdaptiveLearning = true;
        options.EnableResourceStorage = true;

        // Per-tool limits
        options.ToolTokenLimits = new Dictionary<string, int>
        {
            ["csharp_find_all_references"] = 15000,
            ["csharp_symbol_search"] = 20000,
            ["csharp_get_diagnostics"] = 8000
        };
    });

// Register infrastructure services
builder.Services.AddSingleton<MSBuildWorkspaceManager>();
builder.Services.AddSingleton<RoslynWorkspaceService>();
builder.Services.AddSingleton<DocumentService>();
builder.Services.AddSingleton<SymbolCache>();

// Register resource providers
builder.Services.AddSingleton<IResourceProvider, AnalysisResultResourceProvider>();
builder.Services.AddSingleton<IResourceStorageService, CompressedResourceStorage>();

// Register tools (will be migrated to auto-discovery later)
builder.RegisterToolType<GoToDefinitionTool>();
builder.RegisterToolType<FindAllReferencesTool>();
// ... other tools

// Alternative: Auto-discovery
// builder.DiscoverTools(typeof(Program).Assembly);

await builder.RunAsync();
```

### Phase 1 Checklist

#### Model Migration

- [ ] ToolResultBase deleted, using framework version
- [ ] All tool result classes updated to use framework base
- [ ] ErrorInfo, RecoveryInfo, SuggestedAction deleted
- [ ] ErrorCodes updated to extend BaseErrorCodes
- [ ] NextAction replaced with AIAction
- [ ] All imports updated to framework namespaces

#### Infrastructure

- [ ] Program.cs using McpServerBuilder
- [ ] Token optimization configured
- [ ] Resource storage configured
- [ ] Logging integrated with framework
- [ ] Service registration updated

#### Files to Delete in Phase 1

- [ ] `Models/ToolResultBase.cs` - replaced by framework's version
- [ ] `Models/ErrorModels.cs` - replaced by framework's ErrorInfo, RecoveryInfo, SuggestedAction
- [ ] `Models/SharedModels.cs` - NextAction replaced by framework's AIAction
- [ ] `Utilities/TokenEstimator.cs` - replaced by framework's TokenEstimator (delete after updating references)

#### Verification

- [ ] Project compiles without errors
- [ ] Unit tests updated and passing
- [ ] One tool manually tested end-to-end

---

## Phase 2: Tool Migration (Week 2-3)

### 2.1 Tool Migration Pattern

Each tool migration follows this pattern:

1. Convert to inherit from `McpToolBase<TParams, TResult>`
2. Remove manual validation code
3. Use framework's error handling
4. Implement token management
5. Update result building

### 2.2 Example: Migrate GoToDefinitionTool

#### Code to Remove During Migration

**Remove these patterns from EVERY tool:**

```csharp
// 1. REMOVE Manual Parameter Validation (30-50 lines per tool)
if (string.IsNullOrEmpty(parameters.FilePath))
{
    return new ToolResult
    {
        Success = false,
        Message = "FilePath is required",
        Error = new ErrorInfo
        {
            Code = ErrorCodes.INVALID_PARAMETERS,
            Recovery = new RecoveryInfo
            {
                Steps = new[] { "Provide a valid file path" }
            }
        }
    };
}

if (parameters.Line < 1 || parameters.Column < 1)
{
    return new ToolResult
    {
        Success = false,
        Message = "Invalid line or column",
        // ... error building
    };
}

// 2. REMOVE Try-Catch Wrapper (20-30 lines per tool)
try
{
    // tool logic
}
catch (OperationCanceledException)
{
    _logger.LogWarning("Operation cancelled");
    return new ToolResult { Success = false, Message = "Cancelled" };
}
catch (Exception ex)
{
    _logger.LogError(ex, "Tool failed");
    return new ToolResult
    {
        Success = false,
        Message = ex.Message,
        Error = new ErrorInfo { /* ... */ }
    };
}

// 3. REMOVE Manual Token Management (15-20 lines per tool)
var estimatedTokens = results.Count * 100 + 500;
if (estimatedTokens > 10000)
{
    _logger.LogWarning("Response too large, truncating");
    results = results.Take(50).ToList();
    wasTruncated = true;
}

// 4. REMOVE Manual Execution Time Tracking (5-10 lines per tool)
var startTime = DateTime.UtcNow;
// ... tool logic ...
var executionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms";
result.Meta = new ToolMetadata { ExecutionTime = executionTime };

// 5. REMOVE Error Response Builders (40-60 lines per tool)
private ToolResult CreateErrorResult(string code, string message, string[] steps)
{
    return new ToolResult
    {
        Success = false,
        Message = message,
        Error = new ErrorInfo
        {
            Code = code,
            Message = message,
            Recovery = new RecoveryInfo
            {
                Steps = steps,
                SuggestedActions = BuildSuggestedActions(code)
            }
        },
        Meta = new ToolMetadata
        {
            ExecutionTime = CalculateExecutionTime()
        }
    };
}
```

#### Before (Complete Tool):

```csharp
[McpServerToolType]
public class GoToDefinitionTool : ITool
{
    private readonly ILogger<GoToDefinitionTool> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;

    public string ToolName => "csharp_goto_definition";

    [McpServerTool(Name = "csharp_goto_definition")]
    public async Task<object> ExecuteAsync(
        GoToDefinitionParams parameters,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            // Manual parameter validation
            if (string.IsNullOrEmpty(parameters.FilePath))
            {
                return new GoToDefinitionToolResult
                {
                    Success = false,
                    Message = "FilePath is required",
                    Error = new ErrorInfo
                    {
                        Code = ErrorCodes.INVALID_PARAMETERS,
                        Recovery = new RecoveryInfo
                        {
                            Steps = new[] { "Provide a valid file path" }
                        }
                    }
                };
            }

            // Get document
            var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
            if (document == null)
            {
                // Manual error response building
                return new GoToDefinitionToolResult
                {
                    Success = false,
                    Message = $"Document not found: {parameters.FilePath}",
                    Error = new ErrorInfo { /* ... */ },
                    Meta = new ToolMetadata
                    {
                        ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                    }
                };
            }

            // Tool logic...
            var locations = await FindDefinitionLocations(document, position);

            // Manual token management
            var estimatedTokens = locations.Count * 100;
            if (estimatedTokens > 10000)
            {
                locations = locations.Take(50).ToList();
            }

            // Manual response building
            return new GoToDefinitionToolResult
            {
                Success = true,
                Locations = locations,
                Meta = new ToolMetadata
                {
                    ExecutionTime = $"{(DateTime.UtcNow - startTime).TotalMilliseconds:F2}ms"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GoToDefinition");
            return new GoToDefinitionToolResult
            {
                Success = false,
                Message = ex.Message,
                Error = new ErrorInfo { /* ... */ }
            };
        }
    }
}
```

#### After (Migrated Tool with Framework v1.1.x):

```csharp
using COA.Mcp.Framework.Base;
using COA.Mcp.Framework.Models;
using COA.Mcp.Framework.TokenOptimization;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel; // For Description attribute

public class GoToDefinitionTool : McpToolBase<GoToDefinitionParams, GoToDefinitionResult>
{
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly IResourceStorageService _resourceStorage;

    public override string Name => "csharp_goto_definition";
    public override string Description => "Navigate to the definition of a symbol";
    public override ToolCategory Category => ToolCategory.Navigation;
    
    // No need to override GetInputSchema() - Framework generates it automatically!

    public GoToDefinitionTool(
        ILogger<GoToDefinitionTool> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        IResourceStorageService resourceStorage)
        : base(logger)  // Pass logger to base
    {
        _workspaceService = workspaceService;
        _documentService = documentService;
        _resourceStorage = resourceStorage;
    }

    protected override async Task<GoToDefinitionResult> ExecuteInternalAsync(
        GoToDefinitionParams parameters,
        CancellationToken cancellationToken)
    {
        // Parameters are already validated by framework!

        // Get document
        var document = await _workspaceService.GetDocumentAsync(parameters.FilePath);
        if (document == null)
        {
            // Use framework's error result helper
            return CreateErrorResult(
                ErrorCodes.DOCUMENT_NOT_FOUND,
                $"Document not found: {parameters.FilePath}",
                new[]
                {
                    "Ensure the file path is correct and absolute",
                    "Verify the solution/project is loaded",
                    "Use csharp_load_solution to load the project"
                },
                new[]
                {
                    new SuggestedAction
                    {
                        Tool = "csharp_load_solution",
                        Description = "Load the solution containing this file",
                        Parameters = new { solutionPath = "<path-to-solution>" }
                    }
                });
        }

        // Tool logic
        var allLocations = await FindDefinitionLocations(document, position);

        // Use framework's token management
        var tokenAware = await ExecuteWithTokenManagement(async () =>
        {
            var response = TokenEstimator.CreateTokenAwareResponse(
                allLocations,
                locations => TokenEstimator.EstimateCollection(
                    locations,
                    loc => TokenEstimator.Roslyn.EstimateLocation(loc)),
                parameters.MaxResults ?? 100,
                TokenEstimator.DEFAULT_SAFETY_LIMIT);

            // Store full results if truncated
            string? resourceUri = null;
            if (response.WasTruncated)
            {
                resourceUri = await _resourceStorage.StoreResultsAsync(
                    allLocations,
                    TimeSpan.FromMinutes(30));
            }

            return new GoToDefinitionResult
            {
                Success = true,
                Operation = Name,
                Locations = response.Items,
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
                    TotalFound = response.TotalCount,
                    Returned = response.ReturnedCount
                },
                Insights = GenerateInsights(response),
                Actions = GenerateActions(response, parameters),
                Meta = new ToolExecutionMetadata
                {
                    Truncated = response.WasTruncated,
                    ResourceUri = resourceUri
                },
                ResultsSummary = new ResultsSummary
                {
                    Total = response.TotalCount,
                    Included = response.ReturnedCount,
                    HasMore = response.WasTruncated
                }
            };
        });

        return tokenAware;
    }

    protected override int EstimateTokenUsage()
    {
        // Provide accurate estimation for this tool
        return 2000; // Base estimate for typical response
    }

    private List<string> GenerateInsights(TokenAwareResponse<LocationInfo> response)
    {
        var insights = new List<string>();

        if (response.WasTruncated)
        {
            insights.Add(response.GetTruncationMessage());
        }

        if (response.Items.Any(l => l.IsMetadata))
        {
            insights.Add("📚 Symbol defined in metadata/external assembly");
        }

        if (response.Items.Count > 1)
        {
            insights.Add($"🔀 Multiple definitions found (partial classes or overloads)");
        }

        return insights;
    }

    private List<AIAction> GenerateActions(
        TokenAwareResponse<LocationInfo> response,
        GoToDefinitionParams originalParams)
    {
        var actions = new List<AIAction>();

        if (response.Items.Any())
        {
            var firstLocation = response.Items.First();

            actions.Add(new AIAction
            {
                Action = "csharp_find_all_references",
                Description = "Find all references to this symbol",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = firstLocation.FilePath,
                    ["line"] = firstLocation.Line,
                    ["column"] = firstLocation.Column
                },
                Priority = 90,
                Category = "navigation"
            });

            actions.Add(new AIAction
            {
                Action = "csharp_find_implementations",
                Description = "Find implementations if this is an interface/abstract",
                Parameters = new Dictionary<string, object>
                {
                    ["filePath"] = firstLocation.FilePath,
                    ["line"] = firstLocation.Line,
                    ["column"] = firstLocation.Column
                },
                Priority = 80,
                Category = "navigation"
            });
        }

        return actions;
    }
}

// Update parameter class with validation and description attributes
public class GoToDefinitionParams
{
    [Required(ErrorMessage = "FilePath is required")]
    [Description("Path to the source file")]
    [FileExists]  // Custom validation attribute
    public string FilePath { get; set; }

    [Required]
    [Range(1, int.MaxValue, ErrorMessage = "Line must be positive")]
    [Description("Line number (1-based) where the symbol appears")]
    public int Line { get; set; }

    [Required]
    [Range(1, int.MaxValue, ErrorMessage = "Column must be positive")]
    [Description("Column number (1-based) where the symbol appears")]
    public int Column { get; set; }

    [Range(1, 500)]
    [Description("Maximum number of results to return")]
    public int? MaxResults { get; set; }
}

// Update result class to extend framework base
public class GoToDefinitionResult : ToolResultBase
{
    public override string Operation => "csharp_goto_definition";

    // Tool-specific properties
    [JsonPropertyName("locations")]
    public List<LocationInfo> Locations { get; set; } = new();

    [JsonPropertyName("query")]
    public QueryInfo? Query { get; set; }

    [JsonPropertyName("summary")]
    public SummaryInfo? Summary { get; set; }

    [JsonPropertyName("resultsSummary")]
    public ResultsSummary? ResultsSummary { get; set; }
}
```

### 2.3 Tool Migration Checklist Template

For each tool, complete this checklist:

#### Tool: [TOOL_NAME]

- [ ] Inherits from McpToolBase<TParams, TResult>
- [ ] Parameter class has validation attributes
- [ ] Result class extends ToolResultBase
- [ ] Manual validation code removed
- [ ] Try-catch removed (handled by base)
- [ ] ExecuteInternalAsync implemented
- [ ] EstimateTokenUsage overridden
- [ ] Token management applied to results
- [ ] Resource storage for large results
- [ ] Insights generation implemented
- [ ] Actions generation implemented
- [ ] Error handling uses CreateErrorResult
- [ ] Unit tests updated
- [ ] Integration test passes

### 2.4 Migration Order

Migrate tools in this order for maximum impact:

#### High Priority (Most Used)

1. - [x] GoToDefinitionTool (Completed 2025-08-06)
2. - [x] FindAllReferencesTool (Completed 2025-08-06)
3. - [x] SymbolSearchTool (Completed 2025-08-07)
4. - [x] HoverTool (Completed 2025-08-07)
5. - [x] RenameSymbolTool (Completed 2025-08-07)

#### Medium Priority

6. - [x] GetDiagnosticsTool (Completed 2025-08-07)
7. - [x] FindImplementationsTool (Completed 2025-08-07)
8. - [x] DocumentSymbolsTool (Completed 2025-08-07)
9. - [x] GetTypeMembersTool (Completed 2025-08-07)
10. - [x] TraceCallStackTool (Completed 2025-08-07)

#### Lower Priority

11. - [x] ApplyCodeFixTool (Completed 2025-08-07)
12. - [x] GenerateCodeTool (Completed 2025-08-07)
13. - [x] FormatDocumentTool (Completed 2025-08-07)
14. - [x] ExtractMethodTool (Completed 2025-08-07)
15. - [x] AddMissingUsingsTool (Completed 2025-08-07)

#### Infrastructure Tools

16. - [x] LoadSolutionTool (Completed 2025-08-06)
17. - [x] LoadProjectTool (Completed 2025-08-06)
18. - [x] GetWorkspaceStatisticsTool (Completed 2025-08-06)

#### Advanced Analysis

19. - [x] CallHierarchyTool (Completed 2025-08-07)
20. - [x] TypeHierarchyTool (Completed 2025-08-07)
21. - [x] FindAllOverridesTool (Completed 2025-08-07)
22. - [x] CodeMetricsTool (Completed 2025-08-07)
23. - [x] FindUnusedCodeTool (Completed 2025-08-07)
24. - [x] SolutionWideFindReplaceTool (Completed 2025-08-07)
25. - [x] CodeCloneDetectionTool (Completed 2025-08-07)
26. - [x] DependencyAnalysisTool (Completed 2025-08-07)

---

## Phase 3: Advanced Features (Week 4)

### 3.1 Token Optimization Configuration

#### 3.1.1 Create Custom Token Estimators

```csharp
// Create TokenEstimators/RoslynEstimators.cs
using COA.Mcp.Framework.TokenOptimization;

namespace COA.CodeNav.McpServer.TokenEstimators;

public static class RoslynEstimators
{
    public static int EstimateLocation(LocationInfo location)
    {
        var tokens = 50; // Base structure
        tokens += TokenEstimator.EstimateString(location.FilePath) / 2;
        tokens += TokenEstimator.EstimateString(location.Text);
        tokens += 20; // Line, column, span
        return tokens;
    }

    public static int EstimateSymbol(SymbolInfo symbol)
    {
        var tokens = 80;
        tokens += TokenEstimator.EstimateString(symbol.Name);
        tokens += TokenEstimator.EstimateString(symbol.FullName);
        tokens += TokenEstimator.EstimateString(symbol.Documentation) / 3;
        tokens += (symbol.Parameters?.Count ?? 0) * 30;
        return tokens;
    }

    public static int EstimateDiagnostic(DiagnosticInfo diagnostic)
    {
        var tokens = 60;
        tokens += TokenEstimator.EstimateString(diagnostic.Message);
        tokens += TokenEstimator.EstimateString(diagnostic.FilePath) / 2;
        tokens += (diagnostic.Tags?.Count ?? 0) * 5;
        tokens += (diagnostic.Properties?.Count ?? 0) * 20;
        return tokens;
    }
}
```

#### 3.1.2 Implement Priority-Based Reduction

```csharp
// Create Reduction/RoslynPriorityCalculators.cs
using COA.Mcp.Framework.TokenOptimization.Reduction;

namespace COA.CodeNav.McpServer.Reduction;

public static class RoslynPriorityCalculators
{
    public static double CalculateReferencePriority(ReferenceLocation reference)
    {
        var priority = 50.0;

        // Boost definitions
        if (reference.Kind == "Definition")
            priority += 100;

        // Boost writes over reads
        if (reference.Kind == "Write")
            priority += 50;

        // Boost public API
        if (reference.IsPublic)
            priority += 30;

        // Penalize test code
        if (reference.FilePath.Contains("Test"))
            priority -= 20;

        return priority;
    }

    public static double CalculateDiagnosticPriority(DiagnosticInfo diagnostic)
    {
        return diagnostic.Severity switch
        {
            "Error" => 1000,
            "Warning" => 100,
            "Info" => 10,
            _ => 1
        };
    }

    public static double CalculateSymbolPriority(SymbolInfo symbol)
    {
        var priority = 50.0;

        if (symbol.Accessibility == "Public")
            priority += 100;
        else if (symbol.Accessibility == "Protected")
            priority += 50;
        else if (symbol.Accessibility == "Internal")
            priority += 20;

        // Boost commonly used types
        if (symbol.Kind == "Class" || symbol.Kind == "Interface")
            priority += 30;

        return priority;
    }
}
```

### 3.2 Response Builders

#### 3.2.1 Create Custom Response Builder

```csharp
// Create ResponseBuilders/RoslynResponseBuilder.cs
using COA.Mcp.Framework.TokenOptimization.ResponseBuilders;
using COA.Mcp.Framework.TokenOptimization.Models;

namespace COA.CodeNav.McpServer.ResponseBuilders;

public class RoslynResponseBuilder<TData> : BaseResponseBuilder<TData>
    where TData : class
{
    private readonly IInsightGenerator _insightGenerator;
    private readonly IActionGenerator _actionGenerator;

    public RoslynResponseBuilder(
        ILogger<RoslynResponseBuilder<TData>> logger,
        IInsightGenerator insightGenerator,
        IActionGenerator actionGenerator)
        : base(logger)
    {
        _insightGenerator = insightGenerator;
        _actionGenerator = actionGenerator;
    }

    public override async Task<object> BuildResponseAsync(
        TData data,
        ResponseContext context)
    {
        var startTime = DateTime.UtcNow;
        var tokenBudget = CalculateTokenBudget(context);

        // Apply token-aware reduction
        var optimizedData = ApplyTokenReduction(data, tokenBudget);

        // Generate insights and actions
        var insights = GenerateInsights(optimizedData, context.ResponseMode);
        var actions = GenerateActions(optimizedData, tokenBudget / 10);

        // Build response
        return new AIOptimizedResponse
        {
            Format = "ai-optimized",
            Data = optimizedData,
            Insights = ReduceInsights(insights, tokenBudget / 20),
            Actions = ReduceActions(actions, tokenBudget / 10),
            Meta = CreateMetadata(startTime, WasTruncated(data, optimizedData))
        };
    }

    protected override List<string> GenerateInsights(TData data, string responseMode)
    {
        // Use framework's insight generator with custom templates
        return _insightGenerator.Generate(data, new InsightContext
        {
            ResponseMode = responseMode,
            Domain = "roslyn-code-analysis",
            MaxInsights = 5
        });
    }

    protected override List<AIAction> GenerateActions(TData data, int tokenBudget)
    {
        // Use framework's action generator
        return _actionGenerator.Generate(data, new ActionContext
        {
            TokenBudget = tokenBudget,
            Domain = "roslyn-navigation",
            MaxActions = 5
        });
    }
}
```

### 3.3 Caching Implementation

```csharp
// Update appsettings.json
{
  "TokenOptimization": {
    "Caching": {
      "Enabled": true,
      "DefaultExpiration": "00:30:00",
      "MaxSize": 1000,
      "EvictionPolicy": "LRU"
    }
  }
}

// Use caching in tools
public class SymbolSearchTool : McpToolBase<SymbolSearchParams, SymbolSearchResult>
{
    private readonly IResponseCacheService _cache;

    protected override async Task<SymbolSearchResult> ExecuteInternalAsync(
        SymbolSearchParams parameters,
        CancellationToken cancellationToken)
    {
        // Generate cache key
        var cacheKey = CacheKeyGenerator.Generate(Name, parameters);

        // Try cache first
        var cached = await _cache.GetAsync<SymbolSearchResult>(cacheKey);
        if (cached != null)
        {
            cached.Meta.Cached = "hit";
            return cached;
        }

        // Execute search
        var result = await PerformSearch(parameters);

        // Cache result
        await _cache.SetAsync(cacheKey, result, TimeSpan.FromMinutes(15));

        result.Meta.Cached = "miss";
        return result;
    }
}
```

### Phase 3 Checklist

#### Token Optimization

- [ ] Custom estimators created for Roslyn types
- [ ] Priority calculators implemented
- [ ] Token limits configured per tool
- [ ] Progressive reduction tested
- [ ] Resource storage working

#### Response Building

- [ ] Custom response builder created
- [ ] Insight generation configured
- [ ] Action generation configured
- [ ] Response modes (summary/full) working
- [ ] Truncation messages clear

#### Caching

- [ ] Cache service configured
- [ ] Cache keys generated correctly
- [ ] Eviction policy set
- [ ] Cache hits/misses tracked
- [ ] Performance improved

---

## Phase 4: Testing & Optimization (Week 5)

### 4.1 Testing Strategy

#### 4.1.1 Unit Tests

```csharp
// Tests/Tools/GoToDefinitionToolTests.cs
using COA.Mcp.Framework.Testing.Base;
using COA.Mcp.Framework.Testing.Assertions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

[TestFixture]
public class GoToDefinitionToolTests : ToolTestBase<GoToDefinitionTool>
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        // Register dependencies for the tool
        services.AddSingleton<RoslynWorkspaceService>();
        services.AddSingleton<DocumentService>();
        services.AddSingleton<IResourceStorageService, InMemoryResourceStorage>();
    }
    
    [Test]
    public async Task GoToDefinition_ValidSymbol_ReturnsLocation()
    {
        // Arrange
        var parameters = new GoToDefinitionParams
        {
            FilePath = TestFiles.SampleClass,
            Line = 10,
            Column = 15
        };

        // Act
        var result = await Tool.ExecuteAsync(parameters);

        // Assert
        result.Should().BeSuccessful();
        result.Locations.Should().NotBeEmpty();
        result.Should().HaveValidInsights();
        result.Should().HaveNextActions();
        result.Should().StayWithinTokenLimit(10000);
    }

    [Test]
    public async Task GoToDefinition_InvalidFile_ReturnsError()
    {
        // Arrange
        var parameters = new GoToDefinitionParams
        {
            FilePath = "nonexistent.cs",
            Line = 1,
            Column = 1
        };

        // Act
        var result = await Tool.ExecuteAsync(parameters);

        // Assert
        result.Should().BeFailure();
        result.Error.Should().HaveRecoverySteps();
        result.Error.Code.Should().Be(ErrorCodes.DOCUMENT_NOT_FOUND);
    }
}
```

#### 4.1.2 Integration Tests with HTTP Client (New in v1.1.x)

```csharp
// Tests/Integration/HttpClientIntegrationTests.cs
using COA.Mcp.Client;
using COA.Mcp.Framework.Testing.Base;

[TestFixture]
public class HttpClientIntegrationTests : IntegrationTestBase
{
    private McpHttpClient _client;
    private TestServer _server;
    
    [SetUp]
    public async Task Setup()
    {
        // Start test server with HTTP transport
        _server = await StartTestServerAsync(builder =>
        {
            builder.ConfigureHttpTransport(options =>
            {
                options.Port = 0; // Random port
                options.EnableWebSocket = true;
            });
        });
        
        // Create typed client
        _client = McpClientBuilder
            .Create($"http://localhost:{_server.Port}")
            .WithTimeout(TimeSpan.FromSeconds(30))
            .Build();
            
        await _client.ConnectAsync();
        await _client.InitializeAsync();
    }
    
    [Test]
    public async Task NavigationWorkflow_ViaHttp_CompleteScenario()
    {
        // Use typed client for type-safe calls
        var typedClient = _client.AsTyped<GoToDefinitionParams, GoToDefinitionResult>();
        
        var result = await typedClient.CallToolAsync("csharp_goto_definition", new GoToDefinitionParams
        {
            FilePath = TestFiles.MainClass,
            Line = 25,
            Column = 20
        });
        
        result.Should().BeSuccessful();
        result.Locations.Should().NotBeEmpty();
    }
}
```

#### 4.1.3 Standard Integration Tests

```csharp
// Tests/Integration/ToolIntegrationTests.cs
using COA.Mcp.Framework.Testing.Base;

[TestFixture]
public class ToolIntegrationTests : IntegrationTestBase
{
    [Test]
    public async Task NavigationWorkflow_CompleteScenario()
    {
        // Load solution
        var loadResult = await ExecuteTool<LoadSolutionTool>(
            new LoadSolutionParams { SolutionPath = TestSolution });
        loadResult.Should().BeSuccessful();

        // Go to definition
        var gotoResult = await ExecuteTool<GoToDefinitionTool>(
            new GoToDefinitionParams
            {
                FilePath = TestFiles.MainClass,
                Line = 25,
                Column = 20
            });
        gotoResult.Should().BeSuccessful();

        // Find references using location from goto
        var location = gotoResult.Locations.First();
        var refsResult = await ExecuteTool<FindAllReferencesTool>(
            new FindAllReferencesParams
            {
                FilePath = location.FilePath,
                Line = location.Line,
                Column = location.Column
            });
        refsResult.Should().BeSuccessful();
        refsResult.Locations.Should().HaveCountGreaterThan(1);
    }
}
```

#### 4.1.3 Performance Tests

```csharp
// Tests/Performance/TokenBenchmarks.cs
using BenchmarkDotNet.Attributes;
using COA.Mcp.Framework.Testing.Performance;

[MemoryDiagnoser]
public class TokenBenchmarks
{
    private SymbolSearchTool _tool;
    private SymbolSearchParams _params;

    [GlobalSetup]
    public void Setup()
    {
        _tool = CreateTool<SymbolSearchTool>();
        _params = new SymbolSearchParams { Query = "Test*" };
    }

    [Benchmark]
    public async Task SearchWithTokenLimit()
    {
        _params.MaxResults = 100;
        var result = await _tool.ExecuteAsync(_params);
    }

    [Benchmark]
    public async Task SearchWithProgresiveReduction()
    {
        _params.MaxResults = 1000;
        var result = await _tool.ExecuteAsync(_params);
        // Should auto-reduce to stay within limits
    }
}
```

### 4.2 Optimization Tasks

#### 4.2.1 Complete Code Removal List

**Files to Delete Completely:**

```bash
# Models - replaced by framework equivalents
rm COA.CodeNav.McpServer/Models/ToolResultBase.cs         # Use COA.Mcp.Framework.Models.ToolResultBase
rm COA.CodeNav.McpServer/Models/ErrorModels.cs            # Use COA.Mcp.Framework.Models.ErrorInfo, etc.
rm COA.CodeNav.McpServer/Models/SharedModels.cs           # NextAction -> COA.Mcp.Framework.Models.AIAction

# Utilities - replaced by framework
rm COA.CodeNav.McpServer/Utilities/TokenEstimator.cs      # Use COA.Mcp.Framework.TokenOptimization.TokenEstimator

# Infrastructure - replaced by framework
rm COA.CodeNav.McpServer/Infrastructure/ToolRegistry.cs   # Use COA.Mcp.Framework.Registration.McpToolRegistry
rm COA.CodeNav.McpServer/Infrastructure/AttributeBasedToolDiscovery.cs  # Built into framework

# Attributes - replaced by framework
rm COA.CodeNav.McpServer/Attributes/McpServerToolAttribute.cs      # Use COA.Mcp.Framework.Attributes
rm COA.CodeNav.McpServer/Attributes/McpServerToolTypeAttribute.cs  # Use COA.Mcp.Framework.Attributes
rm COA.CodeNav.McpServer/Attributes/DescriptionAttribute.cs       # Use COA.Mcp.Framework.Attributes

# Base classes/interfaces
rm COA.CodeNav.McpServer/Tools/ITool.cs                   # Use COA.Mcp.Framework.Interfaces.IMcpTool
```

**Code to Remove from Existing Files:**

```csharp
// In each tool file, REMOVE:
- Manual parameter validation blocks
- Try-catch wrappers in ExecuteAsync
- Manual token counting/limiting code
- Manual execution time tracking
- Manual error response building
- CreateErrorResponse helper methods

// In CodeNavMcpServer.cs, REMOVE:
- Manual tool registration code
- Tool discovery logic
- Protocol handling (now in framework)

// In result classes, REMOVE:
- Duplicate base properties (Success, Message, Error, etc.)
- Manual JSON serialization attributes for base properties
```

#### 4.2.2 Update Tool Discovery

```csharp
// Program.cs - Switch to auto-discovery
var builder = new McpServerBuilder()
    .WithServerInfo("COA CodeNav MCP Server", "2.0.0");

// Remove individual registrations
// builder.RegisterToolType<GoToDefinitionTool>();
// builder.RegisterToolType<FindAllReferencesTool>();
// ... 20+ more

// Use auto-discovery instead
builder.DiscoverTools(typeof(Program).Assembly, options =>
{
    options.ToolNamespace = "COA.CodeNav.McpServer.Tools";
    options.ExcludeObsolete = true;
    options.RequireAttribute = true;
});
```

### 4.3 Performance Metrics

Track these metrics before and after migration:

| Metric                | Before  | After     | Improvement   |
| --------------------- | ------- | --------- | ------------- |
| Average response time | 250ms   | 180ms     | 28% faster    |
| Token usage (avg)     | 8500    | 3200      | 62% reduction |
| Memory usage (peak)   | 450MB   | 320MB     | 29% reduction |
| Context overflow rate | 15%     | <1%       | 93% reduction |
| Tool development time | 4 hours | 1.5 hours | 63% faster    |
| Test coverage         | 65%     | 85%       | 31% increase  |
| Code lines            | 8500    | 5100      | 40% reduction |

### Phase 4 Checklist

#### Testing

- [ ] All unit tests migrated
- [ ] Integration tests passing
- [ ] Performance benchmarks run
- [ ] Token usage verified
- [ ] Error handling tested
- [ ] Resource cleanup verified

#### Optimization & Code Removal

- [ ] All 10 duplicate infrastructure files deleted
- [ ] Manual validation removed from all 26 tools
- [ ] Try-catch wrappers removed from all tools
- [ ] Token management code removed from all tools
- [ ] Error builders removed from all tools
- [ ] Auto-discovery enabled (remove manual registrations)
- [ ] Unused dependencies removed from .csproj
- [ ] Build warnings resolved
- [ ] Code analysis passing

#### Documentation

- [ ] README updated
- [ ] Tool descriptions updated
- [ ] Migration notes documented
- [ ] Performance metrics recorded

### Code Removal Verification

Run this script to verify all obsolete code has been removed:

```bash
# Check for old patterns that should be removed
echo "Checking for code that should be deleted..."

# Should return 0 results
grep -r "class ToolResultBase" COA.CodeNav.McpServer/
grep -r "class ErrorInfo" COA.CodeNav.McpServer/
grep -r "class NextAction" COA.CodeNav.McpServer/
grep -r "class TokenEstimator" COA.CodeNav.McpServer/
grep -r "ITool" COA.CodeNav.McpServer/Tools/

# Check for manual validation patterns (should be minimal)
grep -r "string.IsNullOrEmpty(parameters" COA.CodeNav.McpServer/Tools/
grep -r "return new.*Success = false.*INVALID_PARAMETERS" COA.CodeNav.McpServer/Tools/

# Check for manual error handling (should be gone)
grep -r "catch (Exception ex)" COA.CodeNav.McpServer/Tools/
grep -r "DateTime.UtcNow.*startTime" COA.CodeNav.McpServer/Tools/

echo "If any results appear above, those files need updating!"
```

---

## Appendices

### Appendix A: Common Migration Patterns

#### Pattern 1: Simple Query Tool

```csharp
// Before: Manual everything
public class SimpleQueryTool : ITool
{
    public async Task<object> ExecuteAsync(params, cancellation)
    {
        try
        {
            // Validate
            if (params.Query == null) return error;

            // Execute
            var results = await Query(params);

            // Limit results
            if (results.Count > 100)
                results = results.Take(100).ToList();

            // Build response
            return new Result { Success = true, Data = results };
        }
        catch (Exception ex)
        {
            return new Result { Success = false, Error = ex.Message };
        }
    }
}

// After: Framework handles boilerplate
public class SimpleQueryTool : McpToolBase<QueryParams, QueryResult>
{
    protected override async Task<QueryResult> ExecuteInternalAsync(
        QueryParams parameters,
        CancellationToken cancellationToken)
    {
        var results = await Query(parameters);

        var tokenAware = TokenEstimator.CreateTokenAwareResponse(
            results, EstimateItem, parameters.MaxResults ?? 100);

        return new QueryResult
        {
            Success = true,
            Data = tokenAware.Items,
            Meta = new ToolExecutionMetadata
            {
                Truncated = tokenAware.WasTruncated
            }
        };
    }
}
```

#### Pattern 2: Command Tool

```csharp
// Before: Manual validation and error handling
public class CommandTool : ITool
{
    public async Task<object> ExecuteAsync(params, cancellation)
    {
        // Lots of validation
        if (string.IsNullOrEmpty(params.Command))
            return BuildError("Command required");
        if (!IsValidCommand(params.Command))
            return BuildError("Invalid command");

        try
        {
            await ExecuteCommand(params);
            return new Result { Success = true };
        }
        catch (Exception ex)
        {
            return BuildError(ex.Message);
        }
    }
}

// After: Declarative validation
public class CommandParams
{
    [Required]
    [RegularExpression("^(start|stop|restart)$")]
    public string Command { get; set; }
}

public class CommandTool : McpToolBase<CommandParams, CommandResult>
{
    protected override async Task<CommandResult> ExecuteInternalAsync(
        CommandParams parameters,
        CancellationToken cancellationToken)
    {
        // Validation already done!
        await ExecuteCommand(parameters);
        return new CommandResult { Success = true };
    }
}
```

### Appendix B: Framework v1.1.x Breaking Changes & Gotchas

#### Breaking Change: GetInputSchema Return Type

**Issue**: Tools implementing `GetInputSchema()` returning `object` will fail to compile.

**Solution**: Remove the override entirely - base class handles it:
```csharp
// REMOVE this method completely
// public override object GetInputSchema() { ... }

// Framework generates schema from attributes automatically
```

#### Breaking Change: IMcpTool Interface

**Issue**: Custom implementations of `IMcpTool` must return `IJsonSchema` not `object`.

**Solution**: Use framework's base class or implement properly:
```csharp
// Implement both interfaces
public class MyTool : IMcpTool<TParams, TResult>, IMcpTool
{
    // Generic interface
    public JsonSchema<TParams> GetInputSchema() => new JsonSchema<TParams>();
    
    // Non-generic interface (for framework)
    IJsonSchema IMcpTool.GetInputSchema() => GetInputSchema();
}
```

#### Gotcha: Description Attributes

**Issue**: Multiple `Description` attributes exist - wrong one causes schema issues.

**Solution**: Use both for proper schema generation:
```csharp
using System.ComponentModel; // For parameter descriptions
using COA.Mcp.Framework.Attributes; // For tool descriptions

public class MyParams
{
    [System.ComponentModel.Description("Parameter description")]
    public string Value { get; set; }
}

[COA.Mcp.Framework.Attributes.Description("Tool description")]
public class MyTool : McpToolBase<MyParams, MyResult> { }
```

#### Gotcha: Protocol Version

**Issue**: Framework v1.1.x includes Protocol v1.3.x as dependency.

**Solution**: Remove direct Protocol reference:
```xml
<!-- REMOVE -->
<PackageReference Include="COA.Mcp.Protocol" Version="1.0.0" />

<!-- Framework includes it -->
<PackageReference Include="COA.Mcp.Framework" Version="1.1.0" />
```

### Appendix C: Troubleshooting Guide

#### Issue: Compilation Errors After Migration

**Symptom:** CS0234: The type or namespace 'NextAction' does not exist

**Solution:**

```csharp
// Replace all NextAction with AIAction
// Update namespaces:
using COA.Mcp.Framework.Models;
```

#### Issue: Token Limits Not Working

**Symptom:** Responses still too large

**Solution:**

```csharp
// Ensure token optimization is configured
builder.ConfigureTokenOptimization(options =>
{
    options.DefaultTokenLimit = 10000;
    options.Level = TokenOptimizationLevel.Balanced;
});

// Override EstimateTokenUsage in tool
protected override int EstimateTokenUsage()
{
    return 5000; // Tool-specific estimate
}
```

#### Issue: Tests Failing After Migration

**Symptom:** "Success" property not found

**Solution:**

```csharp
// Update test assertions
// Before:
Assert.IsTrue(result.Success);

// After (using framework testing):
result.Should().BeSuccessful();
```

### Appendix C: Rollback Procedures

If migration needs to be rolled back:

1. **Immediate Rollback (< 1 day)**

```bash
git checkout main
git branch -D feature/framework-migration
```

2. **Partial Rollback (keep some changes)**

```bash
# Cherry-pick good commits
git checkout main
git cherry-pick <commit-hash>
```

3. **Gradual Rollback**

```bash
# Keep framework but revert tools
git revert <tool-migration-commits>
# Fix compilation
# Test thoroughly
```

### Appendix D: Migration Validation

Final validation before merging:

#### Functional Testing

- [ ] All tools respond correctly
- [ ] Error messages are helpful
- [ ] Token limits enforced
- [ ] Resource URIs work
- [ ] Insights generated
- [ ] Actions suggested

#### Performance Testing

- [ ] Response times acceptable
- [ ] Memory usage stable
- [ ] No memory leaks
- [ ] Cache working
- [ ] Token estimation accurate

#### Code Quality

- [ ] No compiler warnings
- [ ] Code analysis passing
- [ ] Test coverage > 80%
- [ ] Documentation updated
- [ ] No TODOs remaining

#### Integration Testing

- [ ] Works with Claude Desktop
- [ ] Works with test clients
- [ ] Logging functional
- [ ] Metrics collected

---

## Conclusion

This migration plan provides a systematic approach to modernizing CodeNav MCP with the COA MCP Framework v1.1.x. The migration will:

1. **Reduce code by 40%** through framework features
2. **Prevent token overflow** with advanced token management
3. **Improve maintainability** with consistent patterns
4. **Accelerate development** of new features
5. **Enhance reliability** with better error handling
6. **Enable web clients** through HTTP/WebSocket transport (New in v1.1.x)
7. **Provide type-safe schemas** with automatic generation (New in v1.1.x)
8. **Simplify testing** with strongly-typed C# client (New in v1.1.x)
9. **Support multiple transports** for flexible deployment (New in v1.1.x)

Follow the checklists carefully, test thoroughly at each phase, and maintain the ability to rollback if needed. The investment in migration will pay dividends in reduced maintenance burden and improved capability delivery.

### Success Criteria

Migration is complete when:

- All 26 tools migrated to framework
- All tests passing (unit, integration, performance)
- Token usage reduced by >50%
- Code reduction of >35% achieved
- No regressions in functionality
- Documentation fully updated

### Next Steps After Migration

1. **Implement Advanced Features**

   - Adaptive learning for token limits
   - Clustering-based reduction
   - Custom insight templates

2. **Add New Tools**

   - Leverage framework for rapid development
   - 75% less code per tool

3. **Performance Optimization**

   - Fine-tune token limits
   - Optimize caching strategies
   - Implement predictive pre-caching

4. **Share Learnings**
   - Document patterns discovered
   - Contribute improvements to framework
   - Help other MCP projects migrate
