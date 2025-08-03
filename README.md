# COA CodeNav MCP Server

A powerful MCP (Model Context Protocol) server providing advanced C# code analysis and navigation tools for AI assistants. Built on Microsoft's Roslyn compiler platform, it brings Visual Studio's IntelliSense and code navigation capabilities to AI, enabling deep understanding and manipulation of C# codebases.

## 🚀 Features

- **Complete C# Code Analysis** - Full Roslyn compiler integration for accurate code understanding
- **21 Powerful Tools** - Comprehensive suite covering navigation, analysis, refactoring, and code generation
- **AI-Optimized Responses** - Structured outputs with insights, next actions, and error recovery
- **Smart Token Management** - Automatic response truncation to prevent context overflow
- **Workspace Management** - Load and analyze entire solutions or individual projects
- **Symbol Caching** - Fast repeated lookups with intelligent caching
- **Progressive Disclosure** - Automatic response summarization for large results
- **Rich Error Recovery** - Detailed error information with actionable recovery steps

## 📦 Installation

### Prerequisites

- .NET 9.0 SDK or later
- Windows, macOS, or Linux
- AI assistant with MCP support (Claude Desktop, etc.)

### Quick Install

#### Option 1: Download Release (Recommended)

1. Download the latest release from [GitHub Releases](https://github.com/your-org/coa-codenav-mcp/releases)
2. Extract to your preferred location
3. Configure your AI assistant (see Configuration section)

#### Option 2: Build from Source

```bash
# Clone the repository
git clone https://github.com/your-org/coa-codenav-mcp.git
cd coa-codenav-mcp

# Build the project
dotnet build --configuration Release

# Run tests (optional)
dotnet test
```

### Configuration for Claude Desktop

Add to your Claude configuration file:

**Windows:** `%APPDATA%\Claude\claude_desktop_config.json`
```json
{
  "mcpServers": {
    "codenav": {
      "command": "C:\\path\\to\\COA.CodeNav.McpServer.exe"
    }
  }
}
```

**macOS/Linux:** `~/.config/Claude/claude_desktop_config.json`
```json
{
  "mcpServers": {
    "codenav": {
      "command": "/path/to/COA.CodeNav.McpServer"
    }
  }
}
```

## 🛠️ Available Tools

### Quick Reference

| Tool | Purpose | Example Usage |
|------|---------|---------------|
| `csharp_load_solution` | Load VS solution | "Load MyApp.sln" |
| `csharp_goto_definition` | Jump to definition | "Go to UserService definition" |
| `csharp_find_all_references` | Find usages | "Where is ProcessOrder used?" |
| `csharp_symbol_search` | Search symbols | "Find all *Service classes" |
| `csharp_get_diagnostics` | Get errors/warnings | "Show me all errors" |
| `csharp_rename_symbol` | Rename across solution | "Rename UserService to UserManager" |

### Workspace Management

#### `csharp_load_solution`
Load a complete Visual Studio solution for analysis.

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
- `wildcard` - Support * and ? wildcards
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
  "idFilter": "CS8",  // Filter for specific diagnostic IDs
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

### Token Management

All tools implement smart token management:
- Pre-estimate response size before building
- Apply safety limits (5K-10K tokens)
- Progressive reduction when over limit
- Store full results in resources
- Provide clear next actions for pagination

## 🔧 Troubleshooting

### Common Issues

**"Workspace not loaded" error**
- Ensure you've called `csharp_load_solution` or `csharp_load_project` first
- Check the solution/project path is correct
- Verify the solution builds successfully in Visual Studio

**"Symbol not found" errors**
- Make sure the file is part of the loaded solution/project
- Check that the line and column numbers are correct (1-based)
- Ensure the code compiles without errors

**Performance issues**
- Use `csharp_get_workspace_statistics` to check memory usage
- Consider loading individual projects instead of large solutions
- Enable response summarization for large results

**"Response truncated" messages**
- This is normal for large results
- Use the provided next actions to get more results
- Consider using more specific queries

### Logging

Logs are written to:
- Windows: `%LOCALAPPDATA%\COA.CodeNav.McpServer\logs`
- Linux/macOS: `~/.local/share/COA.CodeNav.McpServer/logs`

## 🤝 Contributing

We welcome contributions! Please see our [Contributing Guide](CONTRIBUTING.md) for details.

### Development Setup

1. Fork and clone the repository
2. Open in Visual Studio 2022 or VS Code
3. Build and run tests: `dotnet test`
4. Make changes and submit a PR

### Adding New Tools

1. Create a new class in the `Tools` folder
2. Add `[McpServerToolType]` attribute to the class
3. Add `[McpServerTool]` and `[Description]` attributes to the method
4. Register the tool in `Program.cs`
5. Follow the established result schema pattern

Example tool implementation:
```csharp
[McpServerToolType]
public class MyNewTool
{
    [McpServerTool(Name = "csharp_my_tool")]
    [Description(@"Brief description of what the tool does.
    Returns: What the tool returns.
    Prerequisites: Any requirements.
    Use cases: When to use this tool.")]
    public async Task<object> ExecuteAsync(MyToolParams parameters, CancellationToken cancellationToken)
    {
        // Implementation
    }
}
```

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- Built on [Microsoft's Roslyn](https://github.com/dotnet/roslyn) compiler platform
- Uses the [COA.Mcp.Protocol](https://github.com/your-org/coa-mcp-protocol) library for MCP communication
- Inspired by Visual Studio's code navigation features
- Thanks to all contributors and users!

## 📊 Project Status

- ✅ **21 tools** implemented and tested
- ✅ **Full Roslyn integration** with MSBuild workspace support
- ✅ **AI-optimized responses** with insights and next actions
- ✅ **Smart token management** with automatic truncation
- ✅ **Comprehensive error recovery** with actionable steps
- ✅ **Symbol caching** for performance
- ✅ **Resource management** for large results
- 🚧 TypeScript support (planned for v2.0)
- 🚧 Razor support (planned for v2.0)

## 🚀 Getting Started

1. **Install** the MCP server (see Installation section)
2. **Configure** your AI assistant
3. **Load** a solution: "Load the MyApp.sln solution"
4. **Explore**: "What does the UserService class do?"
5. **Navigate**: "Find all references to ProcessOrder"
6. **Refactor**: "Rename UserService to UserManager"

---

For more information, issues, or contributions, visit our [GitHub repository](https://github.com/your-org/coa-codenav-mcp).