# Roslyn LSP Implementation Plan for COA CodeNav MCP

## Overview

This document provides a comprehensive, step-by-step implementation plan for integrating Roslyn-based Language Server Protocol (LSP) services into the COA CodeNav MCP server. The goal is to provide robust C# code analysis and navigation capabilities through the MCP protocol.

## Prerequisites

### Required NuGet Packages

```xml
<!-- Core Roslyn packages -->
<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" />
<PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="4.8.0" />
<PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="4.8.0" />
<PackageReference Include="Microsoft.Build.Locator" Version="1.6.10" />

<!-- For code fixes and refactorings -->
<PackageReference Include="Microsoft.CodeAnalysis.CSharp.Features" Version="4.8.0" />
<PackageReference Include="Microsoft.CodeAnalysis.Features" Version="4.8.0" />

<!-- For analyzers -->
<PackageReference Include="Microsoft.CodeAnalysis.Analyzers" Version="3.3.4" />
```

## Phase 1: Core Infrastructure Setup ✅ COMPLETED

### 1.1 MSBuild Integration ✅
- [x] Install MSBuildLocator and register MSBuild instance on startup
- [x] Create MSBuildWorkspaceManager class to handle workspace lifecycle
- [x] Implement proper MSBuild error handling and logging
- [x] Add configuration for MSBuild properties and global properties
- [x] Test with various project types (.NET Core, .NET Framework, .NET Standard)

### 1.2 Workspace Management ✅
- [x] Create RoslynWorkspaceService class as singleton service
- [x] Implement workspace creation and initialization
- [x] Add support for solution (.sln) loading
- [x] Add support for individual project (.csproj) loading
- [x] Implement workspace change notifications
- [x] Add document tracking (open, close, change events)
- [x] Implement proper disposal and cleanup

### 1.3 Document Management ✅
- [x] Create DocumentService for managing open documents
- [x] Implement in-memory document tracking with version control
- [x] Add text synchronization between client and server
- [x] Implement incremental sync for performance
- [x] Add document metadata caching
- [x] Handle file system watchers for external changes

### 1.4 Symbol Caching Infrastructure ✅
- [x] Design symbol cache architecture
- [x] Implement SymbolCache class with LRU eviction
- [x] Add project-level symbol indexing
- [x] Implement incremental cache updates
- [x] Add cache persistence options
- [x] Implement cache warming strategies

## Phase 2: Core Analysis Tools ✅ COMPLETED

### 2.1 Go to Definition ✅
- [x] Create GoToDefinitionTool MCP tool
- [x] Implement symbol resolution at position
- [x] Handle multiple definition scenarios
- [x] Add support for metadata definitions
- [x] Implement source link support
- [x] Handle partial classes and methods
- [x] Add telemetry for performance monitoring

### 2.2 Find All References ✅
- [x] Create FindReferencesTool MCP tool
- [x] Implement reference finding across solution
- [x] Add filtering options (read/write, scopes)
- [x] Implement streaming results for large result sets
- [x] Add progress reporting
- [x] Handle renamed symbols gracefully
- [x] Optimize for performance with parallel processing

### 2.3 Hover Information ✅
- [x] Create HoverTool MCP tool
- [x] Implement quick info provider
- [x] Add XML documentation parsing
- [x] Include type information
- [x] Add parameter information
- [x] Format output in Markdown
- [x] Handle generic types properly

### 2.4 Document Symbols ✅
- [x] Create DocumentSymbolsTool MCP tool
- [x] Implement symbol hierarchy extraction
- [x] Add support for all C# constructs
- [x] Include symbol kinds and modifiers
- [x] Implement range calculation
- [x] Add filtering by symbol type
- [x] Support nested symbols

### 2.5 Workspace/Symbol Search ✅
- [x] Create SymbolSearchTool MCP tool
- [x] Implement fuzzy search algorithm
- [x] Add symbol indexing across projects
- [x] Implement result ranking
- [x] Add filtering options
- [x] Support regex patterns
- [x] Optimize for large codebases

## Phase 3: Advanced Analysis Tools ✅ COMPLETED

### 3.1 Rename Symbol ✅
- [x] Create RenameSymbolTool MCP tool
- [x] Implement symbol renaming across solution
- [x] Add preview mode
- [x] Handle conflict detection
- [x] Support rename in comments and strings
- [x] Add file rename support for types
- [x] Implement undo/redo support

### 3.2 Call Stack Tracing ✅
- [x] Create TraceCallStackTool MCP tool
- [x] Implement forward call tracing (what this method calls)
- [x] Implement backward call tracing (who calls this method)
- [x] Detect entry points (API endpoints, event handlers)
- [x] Extract conditions and control flow
- [x] Add insights about code paths
- [x] Support filtering framework methods

### 3.3 Find Implementations ✅
- [x] Create FindImplementationsTool MCP tool
- [x] Find all implementations of interfaces
- [x] Find all overrides of virtual/abstract methods
- [x] Handle explicit interface implementations
- [x] Support generic type implementations
- [x] Add filtering by project/namespace
- [x] Include derived classes

### 3.4 Get Type Members ✅
- [x] Create GetTypeMembersTool MCP tool
- [x] List all members of a type
- [x] Include inherited members option
- [x] Show member signatures
- [x] Include documentation
- [x] Support filtering by member type
- [x] Show accessibility levels

## Phase 4: Diagnostics and Code Quality ✅ COMPLETED

### 4.1 Get Diagnostics ✅
- [x] Create GetDiagnosticsTool MCP tool
- [x] Get compilation errors and warnings
- [x] Include analyzer diagnostics
- [x] Support severity filtering
- [x] Add suppression information
- [x] Include code fixes where available
- [x] Support workspace-wide diagnostics

### 4.2 Code Metrics ✅ COMPLETED
- [x] Create CodeMetricsTool MCP tool
- [x] Calculate cyclomatic complexity
- [x] Count lines of code
- [x] Calculate maintainability index
- [x] Identify complexity hotspots
- [x] Support file, class, and method-level metrics
- [ ] Generate quality reports

## Phase 5: MCP Integration ✅ COMPLETED

### 5.1 Tool Registration ✅
- [x] Register all Roslyn tools with MCP server
- [x] Implement tool discovery mechanism (attribute-based)
- [x] Add tool versioning
- [x] Create tool documentation
- [x] Implement tool validation
- [x] Add capability negotiation
- [x] Handle tool dependencies

### 5.1.1 Refactoring Infrastructure ✅ COMPLETED (August 2, 2025)
- [x] Create BaseRoslynTool<TParams, TResult> base class
- [x] Extract IDocumentAnalysisService for common operations
- [x] Implement tool interfaces (IExecutableTool, INavigationTool, etc.)
- [x] Complete GoToDefinitionToolRefactored as proof of concept
- [x] Preserve attribute-based discovery with adapter pattern
- [ ] Migrate remaining tools to new pattern

### 5.2 Resource Providers ✅
- [x] Create AnalysisResultResourceProvider
- [x] Implement resource URI scheme
- [x] Add resource caching
- [x] Support resource expiration
- [x] Handle resource updates
- [x] Implement resource listing
- [x] Add resource metadata

### 5.3 AI Optimization Features ✅
- [x] Add insights generation for all tools
- [x] Implement next actions suggestions
- [x] Add error recovery guidance
- [x] Include contextual help
- [x] Support progressive disclosure
- [x] Add smart defaults
- [x] Implement result summarization

### 5.4 Workspace Management Tools ✅
- [x] Create GetWorkspaceStatisticsTool MCP tool
- [x] Track loaded workspaces and memory usage
- [x] Monitor idle times and access patterns
- [x] Provide resource usage insights

### 5.5 Automatic Solution Loading ✅ COMPLETED (August 3, 2025)
- [x] Implement SolutionFinder utility for discovering .sln files
- [x] Add StartupConfiguration for configurable auto-loading behavior
- [x] Integrate auto-loading into CodeNavMcpServer startup
- [x] Support graceful handling when no solution exists
- [x] Add configuration options in appsettings.json
- [x] Log helpful information about loaded projects

#### Configuration
```json
"Startup": {
  "AutoLoadSolution": true,
  "SolutionPath": null,
  "MaxSearchDepth": 5,
  "PreferredSolutionName": null,
  "RequireSolution": false
}
```

## Current Status (August 3, 2025)

### Automatic Solution Loading
**New Feature**: The MCP server now automatically loads solutions on startup, eliminating the most common friction point for AI agents.

#### Benefits for AI Agents
- **No manual loading required**: Roslyn tools work immediately without `roslyn_load_solution` calls
- **Prevents inefficient patterns**: No more failing with DOCUMENT_NOT_FOUND then falling back to text tools
- **Graceful handling**: Works correctly even in early project stages without .sln files
- **Flexible configuration**: Can specify exact solution or let it auto-discover

#### How It Works
1. On startup, searches for .sln files up to 5 directories upward
2. Loads the first (or preferred) solution found
3. If no solution exists, logs warning and continues (tools still available for manual loading)
4. Manual `roslyn_load_solution` and `roslyn_load_project` remain available for flexibility

### Infrastructure Refactoring
Based on senior code review findings, we've implemented a refactoring strategy to eliminate code duplication:
- **Base infrastructure completed**: BaseRoslynTool, IDocumentAnalysisService, tool interfaces
- **Proof of concept completed**: GoToDefinitionToolRefactored demonstrates pattern
- **Decision made**: Keep attribute-based discovery while using inheritance for code reuse
- **See**: [Refactoring Strategy](./refactoring-strategy.md) for implementation details

### Tool Migration Status
- ✅ GoToDefinition - Refactored version completed
- ⏳ FindAllReferences - Next priority for refactoring
- ⏳ Hover - High priority for refactoring
- ⏳ SymbolSearch - High priority for refactoring
- ⏳ Remaining tools - Medium priority

### Token Management Implementation ✅ COMPLETED (August 2, 2025)
**Critical Issue Resolved**: Tools were throwing MCP token overflow errors instead of handling limits internally.

#### Implementation Summary
- **Pattern established**: Pre-estimation of response size BEFORE building responses
- **Safety limit**: 10K tokens max (5% of context window) to preserve usability
- **Progressive reduction**: Dynamically reduce results when over limit
- **Resource storage**: Full results stored with URIs for pagination
- **NextActions**: Guide agents on getting more results

#### Tools Updated with Token Management
1. **GetDiagnosticsTool** - Enhanced existing implementation
2. **DocumentSymbolsTool** - Added token estimation and hierarchy flattening
3. **GetTypeMembersTool** - Added with documentation control tip
4. **FindAllReferencesTool** - Replaced hard limit with dynamic estimation
5. **SymbolSearchTool** - Replaced hard limit with token-aware response
6. **TraceCallStackTool** - Added maxPaths parameter and token estimation
7. **RenameSymbolTool** - Added preview mode token management
8. **ApplyCodeFixTool** - Added preview mode token management

#### Shared Infrastructure
- **TokenEstimator utility**: Provides consistent estimation across all tools
- **Documentation**: Critical pattern documented in CLAUDE.md
- **Testing**: All implementations tested with large result sets

## Remaining High-Priority Tools

~~1. **Symbol Search Tool**~~ ✅ COMPLETED
   - Essential for AI agents to find symbols by name/pattern
   - Supports fuzzy matching and filtering
   - Enables discovery without exact location

~~2. **Find Implementations Tool**~~ ✅ COMPLETED  
   - Critical for understanding inheritance hierarchies
   - Helps AI agents navigate polymorphic code
   - Essential for refactoring tasks

~~3. **Get Type Members Tool**~~ ✅ COMPLETED
   - Useful for exploring type structure
   - Helps with code generation
   - Supports understanding APIs

~~4. **Get Diagnostics Tool**~~ ✅ COMPLETED
   - Important for code quality checks
   - Enables automated error detection
   - Supports fix suggestions

~~5. **Document Symbols Tool**~~ ✅ COMPLETED
   - Provides document structure overview
   - Useful for navigation
   - Helps understand file organization

## Implementation Status Summary

### ✅ Completed (27 items total)
- Infrastructure (6): MSBuildWorkspaceManager, RoslynWorkspaceService, DocumentService, SymbolCache, TokenEstimator, SolutionFinder
- Tools (21): LoadSolution, LoadProject, GoToDefinition, FindAllReferences, Hover, RenameSymbol, TraceCallStack, SymbolSearch, FindImplementations, DocumentSymbols, GetTypeMembers, GetDiagnostics, ApplyCodeFix, GetWorkspaceStatistics, GenerateCode, AddMissingUsings, FormatDocument, ExtractMethod, CodeMetrics, FindUnusedCode, TypeHierarchy
- Features: Resource providers, AI optimizations, attribute-based discovery, token management across all tools, automatic solution loading on startup

### 🔄 In Progress (0 tools)
- None

### 📋 TODO (3 tools from Phase 7)
- Get Code Completions Tool
- Get Signature Help Tool
- Generate Unit Test Tool

## Phase 6: Code Modification Tools (High Priority for AI Agents) ✅ COMPLETED

### 6.1 Apply Code Fix Tool 🔧 ✅ COMPLETED (August 2, 2025)
**Priority: CRITICAL** - Enables AI to automatically fix compilation errors

#### Tool Specification
```csharp
[McpServerTool(Name = "roslyn_apply_code_fix")]
[Description(@"Apply a code fix for a diagnostic at a specific location.
Returns: Modified code with the fix applied, affected files list.
Prerequisites: Call roslyn_get_diagnostics first to get available fixes.
Use cases: Fix compilation errors, apply analyzer suggestions, resolve warnings.
AI benefit: Enables automatic error resolution without manual intervention.")]
```

#### Implementation Requirements
- [x] Create ApplyCodeFixTool MCP tool
- [x] Integrate with Roslyn's CodeFixProvider infrastructure (using MEF)
- [x] Support preview mode before applying
- [x] Handle multi-file fixes (e.g., adding using statements)
- [x] Implement fix selection when multiple fixes available
- [x] Create CodeFixService with MEF-based provider discovery
- [x] Add comprehensive error handling with recovery steps
- [x] Include available fixes in error responses
- [x] Add integration tests to verify code fix functionality
- [x] Add rollback/undo support (handled by workspace.TryApplyChanges)
- [x] Ensure transactional application (all-or-nothing)

#### Result Schema
```csharp
public class ApplyCodeFixToolResult : ToolResultBase
{
    public List<FileChange> AppliedChanges { get; set; }
    public string FixTitle { get; set; }
    public string DiagnosticId { get; set; }
    public bool AllFilesSucceeded { get; set; }
}
```

### 6.2 Generate Code Tool 🏗️ ✅ COMPLETED (August 2, 2025)
**Priority: HIGH** - Reduces boilerplate, speeds development

#### Tool Specification
```csharp
[McpServerTool(Name = "roslyn_generate_code")]
[Description(@"Generate code for common patterns (constructors, properties, interface implementations).
Returns: Generated code with insertion points.
Prerequisites: Position must be inside a type declaration.
Use cases: Generate constructors from fields, properties from fields, interface implementations.
AI benefit: Quickly scaffold code following project conventions.")]
```

#### Implementation Requirements
- [x] Create GenerateCodeTool MCP tool
- [x] Support generation types:
  - [x] Constructor from fields/properties
  - [x] Properties from fields
  - [x] Interface implementation stubs
  - [x] Override methods
  - [x] Equality members (Equals, GetHashCode)
  - [x] IDisposable pattern
- [x] Detect and follow project code style
- [x] Handle partial classes correctly
- [x] Generate XML documentation stubs

### 6.3 Extract Method Tool 📦 ✅ COMPLETED (August 2, 2025)
**Priority: HIGH** - Core refactoring operation

#### Tool Specification
```csharp
[McpServerTool(Name = "roslyn_extract_method")]
[Description(@"Extract selected code into a new method.
Returns: Refactored code with new method and updated call site.
Prerequisites: Valid code selection that can be extracted.
Use cases: Refactor long methods, extract reusable logic, improve code organization.
AI benefit: Helps maintain clean code architecture.")]
```

#### Implementation Requirements
- [x] Create ExtractMethodTool MCP tool
- [x] Implement code flow analysis
- [x] Detect required parameters and return values
- [x] Generate meaningful method names
- [x] Handle variable scoping correctly
- [x] Support async method extraction
- [x] Preserve code formatting

### 6.4 Add Missing Usings Tool 📥 ✅ COMPLETED (August 2, 2025)
**Priority: HIGH** - Common fix for copy-pasted code

#### Tool Specification
```csharp
[McpServerTool(Name = "roslyn_add_missing_usings")]
[Description(@"Add missing using directives for unresolved types.
Returns: Updated file with required using statements.
Prerequisites: File must have unresolved type references.
Use cases: Fix missing imports, resolve type references after paste.
AI benefit: Quickly fix common compilation errors.")]
```

#### Implementation Requirements
- [x] Create AddMissingUsingsTool MCP tool
- [x] Search for types in referenced assemblies
- [x] Suggest fully qualified names as alternative
- [x] Handle ambiguous type names
- [x] Respect existing using organization
- [x] Support global usings (.NET 6+)
- [x] Add extension method usings

### 6.5 Format Document Tool 🎨 ✅ COMPLETED (August 2, 2025)
**Priority: HIGH** - Maintains code quality

#### Tool Specification
```csharp
[McpServerTool(Name = "roslyn_format_document")]
[Description(@"Format code according to project settings and .editorconfig.
Returns: Formatted code following project conventions.
Prerequisites: Valid C# document.
Use cases: Clean up code formatting, fix indentation, organize usings.
AI benefit: Ensures generated/modified code matches project style.")]
```

#### Implementation Requirements
- [x] Create FormatDocumentTool MCP tool
- [x] Apply .editorconfig settings
- [x] Format indentation and whitespace
- [x] Organize using statements
- [x] Sort members by accessibility/type
- [x] Handle region formatting
- [x] Support format selection

## Phase 7: Code Intelligence Tools (Medium Priority)

### 7.1 Get Code Completions Tool 💡
**Priority: MEDIUM** - Helps with API discovery

#### Tool Specification
```csharp
[McpServerTool(Name = "roslyn_get_completions")]
[Description(@"Get IntelliSense-like code completions at a position.
Returns: List of completion items with documentation.
Prerequisites: Valid position in code.
Use cases: Discover available methods/properties, complete partial code.
AI benefit: Helps explore APIs without documentation.")]
```

#### Implementation Requirements
- [ ] Return completion items with:
  - [ ] Symbol name and kind
  - [ ] Full signature
  - [ ] Documentation
  - [ ] Parameter information
- [ ] Support member access completions
- [ ] Include snippet completions
- [ ] Rank by relevance

### 7.2 Find Type Hierarchy Tool 🌳 ✅ COMPLETED (August 3, 2025)
**Priority: MEDIUM** - Understanding type relationships

#### Implementation Requirements
- [x] Show inheritance chain
- [x] Find base classes and interfaces
- [x] Find all derived types
- [x] Support generic type relationships
- [x] Include interface implementations

### 7.3 Get Signature Help Tool 📝
**Priority: MEDIUM** - Helps with correct API usage

#### Implementation Requirements
- [ ] Show active parameter
- [ ] Display all overloads
- [ ] Include parameter documentation
- [ ] Handle generic methods
- [ ] Support named parameters

### 7.4 Find Unused Code Tool 🧹 ✅ COMPLETED (August 3, 2025)
**Priority: MEDIUM** - Code cleanup

#### Implementation Requirements
- [x] Detect unused:
  - [x] Private methods
  - [x] Fields
  - [x] Properties
  - [x] Classes
  - [x] Using statements
- [x] Consider reflection usage
- [x] Handle conditional compilation

### 7.5 Generate Unit Test Tool 🧪
**Priority: MEDIUM** - Improves code quality

#### Implementation Requirements
- [ ] Detect test framework (xUnit, NUnit, MSTest)
- [ ] Generate test class structure
- [ ] Create test methods for public methods
- [ ] Generate meaningful test names
- [ ] Add basic assertions
- [ ] Mock dependencies

## Implementation Guidelines for All New Tools

### Consistent Result Schema
All new tools MUST follow the established pattern:

1. **Inherit from ToolResultBase** or create specific result class
2. **Include standard fields**: Success, Message, Error, Query, Summary, Meta
3. **Implement token management** for large results (MAX_RETURNED_* constants)
4. **Add AI-optimized features**: Insights, NextActions
5. **Track execution time** in all cases

### Error Handling
1. Use specific **ErrorCodes** from the enum
2. Provide **recovery steps** that are actionable
3. Include **suggested actions** with pre-filled parameters
4. Capture **original query** in error responses

### Code Quality Requirements
1. **Async all the way**: Use async/await properly
2. **Cancellation support**: Respect CancellationToken
3. **Logging**: Use ILogger with appropriate levels
4. **Null safety**: Use nullable reference types
5. **Resource cleanup**: Implement proper disposal

### Testing Requirements
1. **Unit tests** for core logic
2. **Integration tests** with real Roslyn workspaces
3. **Performance tests** for large codebases
4. **Error case coverage**
5. **AI scenario validation**

## Next Steps

1. ~~Implement Apply Code Fix Tool~~ ✅ COMPLETED
2. ~~Implement comprehensive token management~~ ✅ COMPLETED
3. ~~Implement Generate Code Tool~~ ✅ COMPLETED
4. ~~Implement Add Missing Usings Tool~~ ✅ COMPLETED
5. ~~Implement Format Document Tool~~ ✅ COMPLETED
6. ~~Implement Extract Method Tool~~ ✅ COMPLETED
7. Consider tool refactoring using BaseRoslynTool pattern
8. ~~Implement Code Metrics Tool (Phase 4)~~ ✅ COMPLETED
9. Evaluate performance and reliability metrics
10. Gather user feedback and iterate
11. Consider implementing Phase 7 tools based on usage patterns
12. ~~Implement automatic solution loading on startup~~ ✅ COMPLETED

## Success Metrics

### Performance Targets
- Go to Definition: < 100ms for same project, < 500ms cross-project ✅
- Find References: < 1s for small projects, < 5s for large solutions ✅
- Hover: < 50ms response time ✅
- Symbol Search: < 500ms for fuzzy search
- Rename: < 2s for preview generation ✅

### Reliability Targets
- 99.9% uptime for core features
- < 0.1% request failure rate
- Memory usage < 1GB for typical projects
- CPU usage < 25% during idle
- Graceful handling of all error cases ✅