# COA CodeNav MCP Server

A powerful MCP (Model Context Protocol) server providing comprehensive **C# and TypeScript** code analysis and navigation tools for AI assistants. Built on Microsoft's Roslyn compiler platform and TypeScript Server Protocol, it brings Visual Studio's IntelliSense and advanced code navigation capabilities to AI, enabling deep understanding and manipulation of codebases.

## 🚀 Features

### Auto-Loading System ⚡
- **Zero Configuration** - Solutions and TypeScript projects load automatically at startup
- **Multi-Language Support** - Simultaneous C# and TypeScript workspace initialization
- **Smart Discovery** - Automatically finds .sln, .csproj, and tsconfig.json files
- **Background Loading** - Non-blocking startup with parallel workspace preparation
- **Graceful Fallbacks** - Falls back to manual loading if auto-detection fails

### C# Analysis (Roslyn)
- **Complete C# Code Analysis** - Full Roslyn compiler integration for accurate code understanding
- **31 Powerful Tools** - Comprehensive suite covering navigation, analysis, refactoring, and code generation
- **Advanced Refactoring** - Extract methods/interfaces, inline code, move types, rename symbols, and more
- **Deep Analysis** - Code metrics, dependency analysis, clone detection, and call hierarchies

### TypeScript Analysis (TSP)
- **TypeScript Server Protocol** - Native TSP integration for accurate TypeScript analysis
- **14 Comprehensive Tools** - Full suite covering navigation, analysis, refactoring, and workspace management
- **Advanced Features** - Imports management, quick fixes, workspace loading, and symbol hierarchies
- **Project Management** - Load tsconfig.json projects with full monorepo workspace support
- **Real-time Diagnostics** - Compilation error detection with intelligent fixes

### AI-Optimized Experience
- **AI-First Design** - Structured outputs with insights, next actions, and error recovery
- **Smart Token Management** - Automatic response truncation to prevent context overflow
- **Progressive Disclosure** - Automatic response summarization for large results
- **Intelligent Hooks** - Claude Code integration with smart type verification suggestions
- **Cross-platform Support** - Windows, macOS, and Linux compatibility

## 📦 Installation

### Prerequisites

- .NET 9.0 SDK or later
- Windows, macOS, or Linux
- AI assistant with MCP support (Claude Desktop, etc.)
- **For TypeScript**: TypeScript installed globally (`npm install -g typescript`)

### Quick Install (Recommended)

#### Via dotnet global tool

```bash
# Install the global tool from NuGet
dotnet tool install --global COA.CodeNav.McpServer

# Add to Claude Desktop configuration
# The tool will be available as 'coa-codenav' command
```

#### Manual Claude Desktop Configuration

Add to your Claude configuration file:

**Windows:** `%APPDATA%\Claude\claude_desktop_config.json`
**macOS/Linux:** `~/.config/Claude/claude_desktop_config.json`

```json
{
  "mcpServers": {
    "coa-codenav": {
      "command": "coa-codenav"
    }
  }
}
```

### Alternative: Build from Source

```bash
# Clone and build
git clone https://github.com/anortham/coa-codenav-mcp.git
cd coa-codenav-mcp
dotnet build -c Release

# Add to Claude Desktop configuration
# Windows
"command": "C:\\path\\to\\coa-codenav-mcp\\COA.CodeNav.McpServer\\bin\\Release\\net9.0\\COA.CodeNav.McpServer.exe"

# macOS/Linux
"command": "/path/to/coa-codenav-mcp/COA.CodeNav.McpServer/bin/Release/net9.0/COA.CodeNav.McpServer"
```

### Update Tool

```bash
# Update to latest version
dotnet tool update --global COA.CodeNav.McpServer
```

### Uninstall Tool

```bash
# Remove global tool
dotnet tool uninstall --global COA.CodeNav.McpServer
```


## 🛠️ Available Tools

### Quick Reference

> 💡 **New**: With auto-loading enabled, solutions and TypeScript projects load automatically - no manual loading required!

#### C# Tools (31 tools)

| Tool                         | Purpose                | Example Usage                       |
| ---------------------------- | ---------------------- | ----------------------------------- |
| `csharp_load_solution`       | Load VS solution*      | "Load MyApp.sln"                    |
| `csharp_goto_definition`     | Jump to definition     | "Go to UserService definition"      |
| `csharp_find_all_references` | Find usages            | "Where is ProcessOrder used?"       |
| `csharp_symbol_search`       | Search symbols         | "Find all \*Service classes"        |
| `csharp_get_diagnostics`     | Get errors/warnings    | "Show me all errors"                |
| `csharp_rename_symbol`       | Rename across solution | "Rename UserService to UserManager" |
| `csharp_extract_interface`   | Extract interface      | "Extract IUserService interface"     |
| `csharp_move_type`           | Move type to new file  | "Move User class to User.cs"        |
| `csharp_inline_method`       | Inline method calls    | "Inline the helper method"          |
| `csharp_call_hierarchy`      | View call graph        | "Show who calls ProcessOrder"       |
| `csharp_code_clone_detection`| Find duplicate code    | "Find duplicated code blocks"       |

*Only needed if auto-loading fails or for additional solutions

#### TypeScript Tools (14 tools)

| Tool                         | Purpose                    | Example Usage                       |
| ---------------------------- | -------------------------- | ----------------------------------- |
| `ts_load_tsconfig`          | Load TypeScript project*   | "Load tsconfig.json"                |
| `ts_load_workspace`         | Load multi-project workspace* | "Load TypeScript monorepo"        |
| `ts_goto_definition`        | Navigate to definitions    | "Go to UserService definition"      |
| `ts_find_all_references`    | Find symbol references     | "Where is processOrder used?"       |
| `ts_find_implementations`   | Find interface implementations | "Find all User implementations"   |
| `ts_get_diagnostics`        | Get TypeScript errors      | "Check for TypeScript errors"       |
| `ts_hover`                  | Get symbol information     | "What does this function do?"       |
| `ts_document_symbols`       | Extract file structure     | "Show all classes and methods"      |
| `ts_symbol_search`          | Search for symbols         | "Find all *Service classes"         |
| `ts_rename_symbol`          | Rename across files        | "Rename UserService to UserManager" |
| `ts_call_hierarchy`         | Analyze call relationships | "Show call hierarchy for function"  |
| `ts_add_missing_imports`    | Auto-add import statements | "Add missing imports to file"       |
| `ts_organize_imports`       | Sort and organize imports  | "Clean up import statements"        |
| `ts_apply_quick_fix`        | Apply TypeScript fixes     | "Fix this TypeScript error"         |

*Only needed if auto-loading fails or for additional projects

### 🔄 Auto-Loading System

The MCP server now features an intelligent auto-loading system that automatically discovers and loads your projects at startup, eliminating the need for manual workspace setup in most cases.

#### How Auto-Loading Works

1. **Startup Detection**: When the MCP server starts, it scans the current directory and subdirectories
2. **Multi-Language Discovery**: Simultaneously searches for:
   - C# solutions (`.sln` files)
   - C# projects (`.csproj` files) 
   - TypeScript configs (`tsconfig.json` files)
3. **Smart Prioritization**: Prefers solutions over individual projects, respects configuration preferences
4. **Background Loading**: Loads workspaces in parallel without blocking the server startup
5. **Graceful Fallbacks**: Falls back to manual loading if auto-detection fails

#### Configuration (appsettings.json)

```json
{
  "Startup": {
    "AutoLoadSolution": true,              // Enable auto-loading
    "SolutionPath": "MyApp.sln",          // Preferred solution name
    "MaxSearchDepth": 5,                   // Directory search depth
    "PreferredSolutionName": "MyApp",      // Preferred solution prefix
    "RequireSolution": false               // Allow project-only loading
  }
}
```

#### Auto-Loading Status

Use `csharp_get_workspace_statistics` to check if auto-loading succeeded:

```json
{
  "totalWorkspaces": 1,
  "workspaceDetails": [{
    "workspaceId": "C:\\Projects\\MyApp\\MyApp.sln",
    "loadedPath": "C:\\Projects\\MyApp\\MyApp.sln",
    "createdAt": "2025-01-20T10:30:00Z",
    "lastAccessedAt": "2025-01-20T10:35:00Z"
  }]
}
```

#### When Manual Loading is Still Needed

- **Additional Solutions**: Loading secondary solutions not in the startup directory
- **Specific Projects**: Loading individual projects when solution auto-loading fails
- **Remote Paths**: Loading solutions from network locations or different drives
- **Multi-Workspace**: Working with multiple unrelated solutions simultaneously

#### Benefits of Auto-Loading

- ✅ **Zero Configuration** - Works out of the box for most projects
- ✅ **Faster Startup** - Parallel loading reduces time to first usable state  
- ✅ **Multi-Language** - Handles C# and TypeScript projects simultaneously
- ✅ **Intelligence Ready** - Type verification and navigation tools available immediately
- ✅ **Backward Compatible** - Manual loading still available when needed

### Workspace Management

#### `csharp_load_solution`

Load a complete Visual Studio solution for analysis (typically only needed if auto-loading fails or for additional solutions).

**When to use:**

- "Load the MyApp.sln solution"
- "Open the solution file in C:\Projects\MyApp"
- "I want to analyze this C# solution"

**Example:**

```json
{
  "solutionPath": "C:\\Projects\\MyApp\\MyApp.sln",
  "workspaceId": "optional-custom-id"
}
```

#### `csharp_load_project`

Load a single C# project file.

**When to use:**

- "Load just the MyApp.Core project"
- "Open the csproj file"
- "I only need to analyze this one project"

**Example:**

```json
{
  "projectPath": "C:\\Projects\\MyApp\\MyApp.Core\\MyApp.Core.csproj"
}
```

#### `csharp_get_workspace_statistics`

Get statistics about loaded workspaces and resource usage.

**When to use:**

- "Show workspace memory usage"
- "How many workspaces are loaded?"
- "Check workspace performance"

### Code Navigation

#### `csharp_goto_definition`

Navigate to the definition of a symbol at a specific position.

**When to use:**

- "Where is UserService defined?"
- "Show me the definition of ProcessOrder method"
- "Jump to where this class is declared"

**Example:**

```json
{
  "filePath": "Program.cs",
  "line": 42,
  "column": 25
}
```

**Response includes:**

- Exact location of definition
- Symbol type and signature
- Next actions (find references, implementations, etc.)

#### `csharp_find_all_references`

Find all references to a symbol across the entire codebase.

**When to use:**

- "Where is UserService used?"
- "Find all calls to ProcessOrder"
- "Show me all references to this variable"

**Example:**

```json
{
  "filePath": "Services/UserService.cs",
  "line": 15,
  "column": 20,
  "maxResults": 100
}
```

**Features:**

- Groups results by file
- Shows usage context
- Handles large result sets with pagination

#### `csharp_find_implementations`

Find all implementations of interfaces and overrides of virtual/abstract methods.

**When to use:**

- "What classes implement IRepository?"
- "Show me all implementations of this interface"
- "What overrides ProcessOrder?"

#### `csharp_hover`

Get detailed information about a symbol including signature, documentation, and type info.

**When to use:**

- "What does this method do?"
- "Show me the documentation for ProcessOrder"
- "What parameters does this function take?"

### Code Search & Discovery

#### `csharp_symbol_search`

Search for symbols by name or pattern across the entire solution.

**When to use:**

- "Find all classes with 'Service' in the name"
- "Search for methods starting with 'Process'"
- "Find the UserController class"
- "Show me all interfaces in the Data namespace"

**Search types:**

- `contains` - Partial match anywhere in name (default)
- `exact` - Exact name match
- `startswith` - Name starts with query
- `endswith` - Name ends with query
- `wildcard` - Support \* and ? wildcards
- `regex` - Full regex patterns
- `fuzzy` - Fuzzy matching for typos

**Example:**

```json
{
  "query": "User*Service",
  "searchType": "wildcard",
  "symbolKinds": ["Class", "Interface"],
  "namespaceFilter": "MyApp.Services",
  "maxResults": 50
}
```

#### `csharp_document_symbols`

Extract the complete symbol hierarchy from a file.

**When to use:**

- "Show me the structure of this file"
- "What methods are in UserService.cs?"
- "Give me an outline of this class"

#### `csharp_get_type_members`

List all members of a type including methods, properties, fields, and events.

**When to use:**

- "What methods does UserService have?"
- "Show me all properties of Order class"
- "List members including inherited ones"

### Code Analysis

#### `csharp_get_diagnostics`

Get compilation errors, warnings, and analyzer diagnostics.

**When to use:**

- "Show me all errors in the solution"
- "What warnings do I have?"
- "Check for nullable reference warnings"
- "Find code quality issues"

**Example:**

```json
{
  "scope": "solution",
  "severities": ["Error", "Warning"],
  "includeAnalyzers": true,
  "idFilter": "CS8", // Filter for specific diagnostic IDs
  "maxResults": 50
}
```

#### `csharp_code_metrics`

Calculate code complexity and maintainability metrics.

**When to use:**

- "Calculate complexity of this method"
- "Find methods that are too complex"
- "Show maintainability index for this class"
- "Identify refactoring candidates"

**Metrics provided:**

- **Cyclomatic Complexity** - Number of code paths
- **Lines of Code** - Logical lines of code
- **Maintainability Index** - 0-100 score (higher is better)
- **Depth of Inheritance** - Inheritance hierarchy depth
- **Class Coupling** - Number of coupled classes

#### `csharp_find_unused_code`

Find potentially unused code elements including classes, methods, properties, and fields.

**When to use:**

- "Find dead code in the project"
- "Show me unused private methods"
- "Clean up unused classes"
- "Identify code that can be removed"

#### `csharp_type_hierarchy`

View the complete type hierarchy including base classes, derived types, and interface implementations.

**When to use:**

- "Show inheritance hierarchy of UserService"
- "What classes derive from BaseController?"
- "View the complete type hierarchy"
- "What interfaces does this class implement?"

### Advanced Analysis

#### `csharp_call_hierarchy`

View bidirectional call graph showing incoming and outgoing calls.

**When to use:**
- "Show all callers of this method"
- "What methods does this function call?"
- "View complete call hierarchy"
- "Understand method dependencies"

#### `csharp_find_all_overrides`

Find all overrides of virtual/abstract methods and properties.

**When to use:**
- "What overrides this virtual method?"
- "Find all implementations of abstract method"
- "Show override hierarchy"

#### `csharp_solution_wide_find_replace`

Perform find and replace operations across the entire solution.

**When to use:**
- "Replace all TODO comments"
- "Update deprecated API usage"
- "Bulk text replacement"
- "Find patterns across solution"

**Example:**
```json
{
  "findPattern": "// TODO:",
  "replacePattern": "// TASK:",
  "preview": true,
  "useRegex": false,
  "wholeWord": false
}
```

#### `csharp_code_clone_detection`

Detect duplicate code patterns for refactoring opportunities.

**When to use:**
- "Find duplicate code"
- "Identify copy-paste code"
- "Find refactoring opportunities"
- "Detect code clones"

**Features:**
- Configurable similarity threshold
- Timeout parameter for large codebases (30-300 seconds)
- Type 1 (exact), Type 2 (renamed), Type 3 (modified) clone detection

**Example:**
```json
{
  "minLines": 6,
  "minTokens": 50,
  "similarityThreshold": 0.8,
  "timeoutSeconds": 120
}
```

#### `csharp_dependency_analysis`

Analyze dependencies and coupling between types, namespaces, and projects.

**When to use:**
- "Analyze project dependencies"
- "Find circular dependencies"
- "Check coupling between namespaces"
- "Understand architecture"

**Analysis levels:**
- `project` - Project-level dependencies
- `namespace` - Namespace dependencies
- `type` - Type-level dependencies

### Code Flow Analysis

#### `csharp_trace_call_stack`

Trace execution paths through code from entry points to implementations.

**When to use:**

- "Show me how ProcessOrder gets called"
- "Trace execution from Main to this method"
- "What calls ValidateUser?"
- "Follow the call chain backwards"

**Directions:**

- `forward` - Follow calls made by the method
- `backward` - Find callers of the method

**Example:**

```json
{
  "filePath": "Services/OrderService.cs",
  "line": 45,
  "column": 20,
  "direction": "backward",
  "maxDepth": 10
}
```

### Code Refactoring & Generation

#### `csharp_rename_symbol`

Rename symbols across the entire solution with conflict detection and preview.

**When to use:**

- "Rename UserService to UserManager"
- "Change this variable name everywhere"
- "Refactor: rename ProcessOrder to ExecuteOrder"

**Features:**

- Preview changes before applying
- Conflict detection
- Optional file renaming for types
- Rename in comments/strings
- Rename overloads together

**Example:**

```json
{
  "filePath": "UserService.cs",
  "line": 10,
  "column": 15,
  "newName": "UserManager",
  "preview": true,
  "renameFile": true,
  "renameInComments": true
}
```

#### `csharp_apply_code_fix`

Apply available code fixes for diagnostics at a specific location.

**When to use:**

- "Fix this compilation error"
- "Apply the suggested fix for CS0219"
- "Resolve this nullable warning"

#### `csharp_generate_code`

Generate code for common patterns and boilerplate.

**When to use:**

- "Generate a constructor from fields"
- "Create properties for these fields"
- "Implement this interface"
- "Generate equals and hash code"

**Generation types:**

- `constructor` - Generate constructor from fields/properties
- `properties` - Generate properties from fields
- `interface` - Implement interface members
- `equals` - Generate Equals and GetHashCode
- `disposable` - Implement IDisposable pattern

#### `csharp_extract_method`

Extract selected code into a new method.

**When to use:**

- "Extract this code into a method"
- "Refactor: extract method called 'ValidateInput'"
- "Create a method from lines 50-75"

**Example:**

```json
{
  "filePath": "OrderService.cs",
  "startLine": 50,
  "endLine": 75,
  "methodName": "ValidateInput",
  "makeStatic": false
}
```

#### `csharp_add_missing_usings`

Add missing using directives for unresolved types.

**When to use:**

- "Fix missing imports"
- "Add required using statements"
- "Resolve unresolved type references"

#### `csharp_format_document`

Format code according to project settings and .editorconfig.

**When to use:**

- "Format this file"
- "Fix indentation"
- "Clean up code formatting"
- "Organize using statements"

## 💡 Real-World Examples

### Example 1: Understanding a Codebase

```
User: "I need to understand how user authentication works in this codebase"

AI: I'll help you understand the authentication system. Let me search for authentication-related code.

*Uses csharp_symbol_search with query "auth*" and wildcard search*
*Finds AuthenticationService, IAuthenticationProvider, AuthController*

Found several authentication components. Let me examine the main service structure.

*Uses csharp_get_type_members on AuthenticationService*
*Uses csharp_trace_call_stack to understand the flow*

The authentication system consists of:
1. `AuthenticationService` - Main service handling login/logout
2. `IAuthenticationProvider` - Interface for different auth providers
3. `JwtAuthProvider` - JWT token implementation
4. `AuthController` - REST API endpoints

The flow is: AuthController → AuthenticationService → IAuthenticationProvider
```

### Example 2: Refactoring Complex Code

```
User: "This method is too complex, can you help refactor it?"

AI: Let me analyze the complexity of this method first.

*Uses csharp_code_metrics to measure complexity*

This method has a cyclomatic complexity of 15, which is quite high. Let me help refactor it.

*Uses csharp_extract_method to extract logical blocks*
*Uses csharp_rename_symbol to give meaningful names*

I've extracted 3 logical blocks into separate methods:
1. ValidateInput() - Input validation logic
2. ProcessBusinessRules() - Core business logic
3. PrepareResponse() - Response formatting

The complexity is now reduced to 5, making it much more maintainable.
```

### Example 3: Finding and Fixing Issues

```
User: "The build is failing, can you help?"

AI: Let me check for compilation errors.

*Uses csharp_get_diagnostics with scope "solution"*

Found 3 errors:
1. CS0246: Type 'IUserRepository' not found in OrderService.cs:15
2. CS1061: 'User' does not contain 'EmailAddress' in UserValidator.cs:42
3. CS0103: Name 'logger' does not exist in ProcessingService.cs:78

Let me fix these:

*Uses csharp_add_missing_usings for the first error*
*Uses csharp_hover to understand the User type*
*Uses csharp_find_all_references to check logger usage*

Fixed:
1. Added missing using for IUserRepository
2. The property is 'Email' not 'EmailAddress'
3. Need to inject ILogger in the constructor
```

### Example 4: Code Review and Quality Check

```
User: "Review this service class for code quality issues"

AI: I'll perform a comprehensive code review. Let me analyze various aspects.

*Uses csharp_code_metrics to check complexity*
*Uses csharp_find_unused_code to identify dead code*
*Uses csharp_get_diagnostics to check for warnings*

Code Review Results:

1. **Complexity**: Method 'ProcessOrder' has high complexity (12)
   - Recommend extracting validation logic

2. **Unused Code**: Found 2 unused private methods
   - CalculateDiscount() - line 145
   - ValidateStock() - line 203

3. **Code Warnings**:
   - CS8602: Possible null reference in line 87
   - CS1998: Async method lacks await in SaveOrder()

4. **Suggestions**:
   - Extract complex logic from ProcessOrder
   - Remove or implement unused methods
   - Add null checks or use nullable annotations
   - Make SaveOrder synchronous or add async operations
```

## 📚 Complete Tool List

The server provides 26 tools organized into these categories:

### Workspace Management (3 tools)
- `csharp_load_solution` - Load .sln files
- `csharp_load_project` - Load .csproj files
- `csharp_get_workspace_statistics` - View workspace info and memory usage

### Code Navigation (8 tools)
- `csharp_goto_definition` - Navigate to definitions
- `csharp_find_all_references` - Find all usages
- `csharp_find_implementations` - Find interface implementations
- `csharp_hover` - Get symbol information
- `csharp_trace_call_stack` - Trace execution paths
- `csharp_symbol_search` - Search symbols by pattern
- `csharp_document_symbols` - Get file structure
- `csharp_get_type_members` - List type members

### Refactoring (4 tools)
- `csharp_rename_symbol` - Rename across solution
- `csharp_extract_method` - Extract code to method
- `csharp_add_missing_usings` - Add using directives
- `csharp_format_document` - Format code

### Diagnostics & Fixes (3 tools)
- `csharp_get_diagnostics` - Get errors/warnings
- `csharp_apply_code_fix` - Apply code fixes
- `csharp_generate_code` - Generate boilerplate code

### Advanced Analysis (8 tools)
- `csharp_code_metrics` - Calculate complexity
- `csharp_find_unused_code` - Find dead code
- `csharp_type_hierarchy` - View inheritance
- `csharp_call_hierarchy` - Bidirectional call graph
- `csharp_find_all_overrides` - Find overrides
- `csharp_solution_wide_find_replace` - Bulk operations
- `csharp_code_clone_detection` - Find duplicates
- `csharp_dependency_analysis` - Analyze dependencies

## 🏗️ Architecture

### Core Components

- **MSBuildWorkspaceManager** - Manages Roslyn workspace lifecycle and caching
- **RoslynWorkspaceService** - Core service providing code analysis operations
- **DocumentService** - Handles document tracking and updates
- **Tool Classes** - Individual tool implementations with MCP protocol integration
- **Symbol Cache** - Performance optimization for repeated symbol lookups
- **Resource Provider** - Manages large results with progressive disclosure

### Design Principles

1. **AI-First Design** - Every response includes insights and suggested next actions
2. **Token Efficiency** - Automatic summarization prevents context overflow
3. **Error Recovery** - Detailed error information with actionable steps
4. **Performance** - Symbol caching and efficient workspace management
5. **Extensibility** - Easy to add new tools using attribute-based discovery
6. **Intelligent Integration** - Seamless Claude Code hooks for enhanced development workflow

### Token Management

All tools implement smart token management:

- Pre-estimate response size before building
- Apply safety limits (5K-10K tokens)
- Progressive reduction when over limit
- Store full results in resources
- Provide clear next actions for pagination

## 🎯 Claude Code Integration

### Intelligent Hooks System

COA CodeNav MCP includes sophisticated Claude Code hooks that enhance the development experience by providing contextual guidance and automatic workspace management.

#### Available Hooks

**Session Start Hook** (`session_start_codenav.py`)
- Triggered on session startup and resume
- Reports auto-loading status for detected projects
- Provides fallback guidance if auto-loading fails
- Shows immediate readiness status for type verification tools

**Pre-Edit Guard Rails** (`guard_rails_pre.py`)
- Triggered before Edit/Write/MultiEdit operations
- Analyzes code for type references and complexity
- Suggests type verification when working with custom types
- Accounts for auto-loaded solutions to avoid redundant suggestions

**Post-Tool Success Tracking** (`guard_rails_post.py`)
- Tracks successful CodeNav tool usage
- Builds session-local knowledge of verified types
- Provides concise feedback optimized for auto-loading environments
- Maintains verification statistics across the session

#### Hook Features

- **Auto-Loading Aware**: Hooks understand when solutions are already loaded via auto-loading
- **Reduced Noise**: Cleaner output since manual loading reminders are no longer needed
- **Type Verification Focus**: Emphasis shifted from "load solutions" to "verify types"
- **Session Memory**: Tracks verified types to avoid redundant suggestions
- **Backward Compatible**: Gracefully handles manual loading scenarios

#### Example Hook Output

**Session Start (Auto-Loading Active)**:
```
🔥 SESSION START HOOK TRIGGERED
🚀 CodeNav Auto-Loading Status
=============================================

📁 C# Project Detected
   ✅ Solution auto-loaded: COA.CodeNav.McpServer.sln
   🚀 C# type verification ready!

📁 TypeScript Project Detected
   ✅ TypeScript workspace auto-loaded
   🚀 TypeScript type verification ready!

✨ Auto-Loading Benefits:
   • Solutions/projects loaded automatically at startup
   • No manual loading steps required
   • Instant type verification tools available
   • Seamless hover tooltips and go-to-definition
```

**Pre-Edit Guidance**:
```
💡 Type Verification Suggestion:
   💡 Verify C# types: UserService, OrderProcessor, PaymentValidator
      mcp__codenav__csharp_hover <file> <line> <col> → UserService details
```

**Post-Tool Success**:
```
✅ Type verified: UserService
   Properties: Id, Name, Email (+2 more)
   Methods: GetUser, UpdateUser (+3 more)
   Session: 12 types verified
```

#### Installation

The hooks are automatically installed with the MCP server. To use them with Claude Code:

1. Place the hook files in `.claude/hooks/`
2. Configure `.claude/settings.json` with hook triggers
3. Hooks will activate automatically on session start and tool usage

#### Hook Configuration

Example `.claude/settings.json` configuration:

```json
{
  "hooks": {
    "SessionStart": [
      {
        "source": "startup",
        "hooks": [
          {
            "type": "command", 
            "command": "uv run .claude/hooks/session_start_codenav.py"
          }
        ]
      }
    ],
    "PreToolUse": [
      {
        "matcher": "Edit|Write|MultiEdit",
        "hooks": [
          {
            "type": "command",
            "command": "uv run .claude/hooks/guard_rails_pre.py"
          }
        ]
      }
    ],
    "PostToolUse": [
      {
        "matcher": "csharp_hover|csharp_goto_definition|ts_hover|ts_goto_definition",
        "hooks": [
          {
            "type": "command",
            "command": "uv run .claude/hooks/guard_rails_post.py"
          }
        ]
      }
    ]
  }
}
```

## 🔧 Troubleshooting

### Common Issues

**Auto-Loading Issues**

- **"No workspace loaded" despite auto-loading**: Check that solution/project files exist in the current directory or subdirectories
- **Auto-loading found wrong solution**: Use manual `csharp_load_solution` with the specific path you want
- **TypeScript not auto-loading**: Ensure `tsconfig.json` exists and TypeScript is installed globally (`npm install -g typescript`)
- **Multiple solutions detected**: Configure `PreferredSolutionName` in appsettings.json to specify which solution to prefer

**Workspace Errors**

- **"Workspace not loaded" error**: 
  - First, check `csharp_get_workspace_statistics` to see if auto-loading worked
  - If no workspaces loaded, manually call `csharp_load_solution` or `csharp_load_project`
  - Verify the solution builds successfully in Visual Studio

**Symbol Navigation Issues**

- **"Symbol not found" errors**:
  - Ensure the file is part of the loaded solution/project
  - Check that line and column numbers are correct (1-based)
  - Verify the code compiles without errors
  - Use `csharp_get_diagnostics` to check for compilation issues

**Performance Issues**

- **Slow response times**:
  - Use `csharp_get_workspace_statistics` to check memory usage
  - Consider loading individual projects instead of large solutions
  - Enable response summarization for large results
  - Check if multiple workspaces are loaded unnecessarily

**Hook-Related Issues**

- **Hooks not triggering**: Verify `.claude/settings.json` configuration and ensure Python/uv is available
- **Excessive hook output**: Hooks are optimized for auto-loading environments; consider disabling if not using auto-loading
- **Type verification suggestions not helpful**: Hooks learn from your session; suggestions improve as you verify more types

**Response Issues**

- **"Response truncated" messages**:
  - This is normal for large results to prevent context overflow
  - Use the provided next actions to get more results
  - Consider using more specific queries to reduce result size

### Auto-Loading Configuration

**Disable Auto-Loading**:
```json
{
  "Startup": {
    "AutoLoadSolution": false
  }
}
```

**Configure Search Behavior**:
```json
{
  "Startup": {
    "AutoLoadSolution": true,
    "MaxSearchDepth": 3,                    // Reduce for faster startup
    "PreferredSolutionName": "MyMainApp",   // Prefer specific solution
    "RequireSolution": true                 // Don't fallback to projects
  }
}
```

**Debug Auto-Loading**:
- Check logs in `%LOCALAPPDATA%\COA.CodeNav.McpServer\logs` (Windows) or `~/.local/share/COA.CodeNav.McpServer/logs` (Linux/macOS)
- Use `csharp_get_workspace_statistics` to see what was loaded
- Verify that solution/project files are accessible and not corrupted

### Logging

Logs are written to:

- Windows: `%LOCALAPPDATA%\COA.CodeNav.McpServer\logs`
- Linux/macOS: `~/.local/share/COA.CodeNav.McpServer/logs`

### Development Setup

1. Fork and clone the repository
2. Open in Visual Studio 2022 or VS Code
3. Build and run tests: `dotnet test`
4. Make changes and submit a PR

### Adding New Tools

The server uses the COA.Mcp.Framework v1.1.6. To add a new tool:

1. Create a new class in the `Tools` folder inheriting from `McpToolBase<TParams, TResult>`
2. Override the `Name` and `Description` properties
3. Implement `ExecuteInternalAsync` method
4. Register the tool in `Program.cs`
5. Follow the established result schema pattern

Example tool implementation:

```csharp
public class MyNewTool : McpToolBase<MyParams, MyResult>
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
builder.Services.AddScoped<MyNewTool>();
```

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- Built on [Microsoft's Roslyn](https://github.com/dotnet/roslyn) compiler platform
- Uses the COA.Mcp.Protocol library for MCP communication
- Inspired by Visual Studio's code navigation features
- Thanks to all contributors and users!

## 📊 Project Status

### Auto-Loading System (New! ⚡)
- ✅ **Intelligent workspace discovery** - Automatically finds and loads C# solutions and TypeScript projects
- ✅ **Multi-language support** - Simultaneous C# and TypeScript workspace initialization
- ✅ **Background loading** - Non-blocking startup with parallel workspace preparation
- ✅ **Smart fallbacks** - Graceful degradation to manual loading when needed
- ✅ **Claude Code integration** - Hooks system optimized for auto-loading workflow

### C# Analysis (Complete)
- ✅ **31 Roslyn tools** implemented and tested
- ✅ **Full MSBuild workspace** support with auto-loading and manual fallback
- ✅ **Advanced refactoring** - extract methods, rename symbols, generate code
- ✅ **Deep analysis** - metrics, dependencies, clone detection, call hierarchies
- ✅ **Symbol caching** for performance optimization

### TypeScript Analysis (Released)
- ✅ **14 TypeScript tools** implemented with TypeScript Server Protocol
- ✅ **Project management** with auto-loading tsconfig.json and workspace tracking  
- ✅ **Navigation tools** - GoToDefinition, FindReferences, Hover, CallHierarchy working correctly
- ✅ **Real-time diagnostics** via tsc compiler integration
- ✅ **Advanced analysis** - Call hierarchy with bidirectional call tracking
- ✅ **Imports management** - Auto-organize and fix missing imports

### Framework Integration
- ✅ **COA.Mcp.Framework v1.7.19** - Latest framework with enhanced token management
- ✅ **AI-optimized responses** with insights, next actions, and error recovery
- ✅ **Smart token management** with automatic response truncation
- ✅ **Cross-platform support** - Windows, macOS, and Linux
- ✅ **Global dotnet tool** packaging for easy installation
- ✅ **Claude Code hooks** - Intelligent session management and type verification guidance

### Planned Features
- 🚧 Python language support (architecture ready for extension)
- 🚧 Additional TypeScript tools (DocumentSymbols, SymbolSearch, FindImplementations)
- 🚧 Razor/Blazor support
- 🚧 JavaScript support via TypeScript infrastructure

## 🚀 Getting Started

### C# Projects (with Auto-Loading)
1. **Install**: `dotnet tool install --global COA.CodeNav.McpServer`
2. **Configure** your AI assistant (see Installation section)  
3. **Start session** in your project directory - solutions auto-load! ⚡
4. **Explore immediately**: "What does the UserService class do?"
5. **Navigate**: "Find all references to ProcessOrder"
6. **Refactor**: "Rename UserService to UserManager"

*Manual loading only needed for additional solutions: "Load the MyApp.sln solution"*

### TypeScript Projects (with Auto-Loading)
1. **Prerequisites**: Ensure TypeScript is installed: `npm install -g typescript`
2. **Start session** in your project directory - tsconfig.json auto-loads! ⚡
3. **Navigate immediately**: "Go to the definition of UserService"
4. **Analyze**: "Check for TypeScript compilation errors"
5. **Explore**: "Find all references to processOrder method"

*Manual loading only needed for additional projects: "Load the tsconfig.json file"*

### Quick Verification
After starting a session, check that auto-loading worked:
- **C#**: "Show workspace statistics" - should show loaded solution
- **TypeScript**: "Check for TypeScript errors" - should show project status

---
