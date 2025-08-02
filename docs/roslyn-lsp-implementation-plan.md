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

## Phase 2: Core Analysis Tools (PARTIALLY COMPLETE)

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

## Phase 3: Advanced Analysis Tools

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

## Phase 4: Diagnostics and Code Quality

### 4.1 Get Diagnostics ✅
- [x] Create GetDiagnosticsTool MCP tool
- [x] Get compilation errors and warnings
- [x] Include analyzer diagnostics
- [x] Support severity filtering
- [x] Add suppression information
- [x] Include code fixes where available
- [x] Support workspace-wide diagnostics

### 4.2 Code Metrics 📋 TODO
- [ ] Create CodeMetricsTool MCP tool
- [ ] Calculate cyclomatic complexity
- [ ] Measure code coverage potential
- [ ] Count lines of code
- [ ] Calculate maintainability index
- [ ] Identify code smells
- [ ] Generate quality reports

## Phase 5: MCP Integration ✅ COMPLETED

### 5.1 Tool Registration ✅
- [x] Register all Roslyn tools with MCP server
- [x] Implement tool discovery mechanism
- [x] Add tool versioning
- [x] Create tool documentation
- [x] Implement tool validation
- [x] Add capability negotiation
- [x] Handle tool dependencies

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

### ✅ Completed (21 tools/features)
- Infrastructure: MSBuildWorkspaceManager, RoslynWorkspaceService, DocumentService, SymbolCache
- Tools: LoadSolution, LoadProject, GoToDefinition, FindAllReferences, Hover, RenameSymbol, TraceCallStack, SymbolSearch, FindImplementations, DocumentSymbols, GetTypeMembers, GetDiagnostics
- Features: Resource providers, AI optimizations, attribute-based discovery

### 🔄 In Progress (0 tools)
- None

### 📋 TODO (1 tool)
- Code Metrics Tool

## Phase 6: Code Modification Tools (High Priority for AI Agents)

### 6.1 Apply Code Fix Tool 🔧
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
- [ ] Create ApplyCodeFixTool MCP tool
- [ ] Integrate with Roslyn's CodeFixProvider infrastructure
- [ ] Support preview mode before applying
- [ ] Handle multi-file fixes (e.g., adding using statements)
- [ ] Implement fix selection when multiple fixes available
- [ ] Add rollback/undo support
- [ ] Ensure transactional application (all-or-nothing)

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

### 6.2 Generate Code Tool 🏗️
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
- [ ] Create GenerateCodeTool MCP tool
- [ ] Support generation types:
  - [ ] Constructor from fields/properties
  - [ ] Properties from fields
  - [ ] Interface implementation stubs
  - [ ] Override methods
  - [ ] Equality members (Equals, GetHashCode)
  - [ ] IDisposable pattern
- [ ] Detect and follow project code style
- [ ] Handle partial classes correctly
- [ ] Generate XML documentation stubs

### 6.3 Extract Method Tool 📦
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
- [ ] Create ExtractMethodTool MCP tool
- [ ] Implement code flow analysis
- [ ] Detect required parameters and return values
- [ ] Generate meaningful method names
- [ ] Handle variable scoping correctly
- [ ] Support async method extraction
- [ ] Preserve code formatting

### 6.4 Add Missing Usings Tool 📥
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
- [ ] Create AddMissingUsingsTool MCP tool
- [ ] Search for types in referenced assemblies
- [ ] Suggest fully qualified names as alternative
- [ ] Handle ambiguous type names
- [ ] Respect existing using organization
- [ ] Support global usings (.NET 6+)
- [ ] Add extension method usings

### 6.5 Format Document Tool 🎨
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
- [ ] Create FormatDocumentTool MCP tool
- [ ] Apply .editorconfig settings
- [ ] Format indentation and whitespace
- [ ] Organize using statements
- [ ] Sort members by accessibility/type
- [ ] Handle region formatting
- [ ] Support format selection

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

### 7.2 Find Type Hierarchy Tool 🌳
**Priority: MEDIUM** - Understanding type relationships

#### Implementation Requirements
- [ ] Show inheritance chain
- [ ] Find base classes and interfaces
- [ ] Find all derived types
- [ ] Support generic type relationships
- [ ] Include interface implementations

### 7.3 Get Signature Help Tool 📝
**Priority: MEDIUM** - Helps with correct API usage

#### Implementation Requirements
- [ ] Show active parameter
- [ ] Display all overloads
- [ ] Include parameter documentation
- [ ] Handle generic methods
- [ ] Support named parameters

### 7.4 Find Unused Code Tool 🧹
**Priority: MEDIUM** - Code cleanup

#### Implementation Requirements
- [ ] Detect unused:
  - [ ] Private methods
  - [ ] Fields
  - [ ] Properties
  - [ ] Classes
  - [ ] Using statements
- [ ] Consider reflection usage
- [ ] Handle conditional compilation

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

1. Implement Apply Code Fix Tool (highest priority - enables error fixing)
2. Implement Generate Code Tool (high value for code generation)
3. Implement Extract Method Tool (essential refactoring)
4. Implement Add Missing Usings Tool (common quick fix)
5. Implement Format Document Tool (code quality)
6. Evaluate performance and reliability metrics
7. Gather user feedback and iterate
8. Consider implementing Phase 7 tools based on usage patterns

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