# COA CodeNav MCP Server

A powerful MCP (Model Context Protocol) server providing Roslyn-based C# code analysis and navigation tools for AI assistants. Built on Microsoft's Roslyn compiler platform, it brings Visual Studio's IntelliSense and code navigation capabilities to AI, enabling deep understanding and manipulation of C# codebases.

## 🚀 Features

- **Complete C# Code Analysis** - Full Roslyn compiler integration for accurate code understanding
- **21 Powerful Tools** - Comprehensive suite covering navigation, analysis, refactoring, and code generation
- **AI-Optimized Responses** - Structured outputs with insights, next actions, and error recovery
- **Smart Token Management** - Automatic response summarization to prevent context overflow
- **Workspace Management** - Load and analyze entire solutions or individual projects
- **Symbol Caching** - Fast repeated lookups with intelligent caching
- **Progressive Disclosure** - Automatic response summarization for large results
- **Rich Error Recovery** - Detailed error information with actionable recovery steps

## 📦 Installation

### Prerequisites

- .NET 9.0 SDK or later
- Windows, macOS, or Linux
- AI assistant with MCP support (Claude Desktop, etc.)

### Building from Source

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

Add to your Claude configuration file (`%APPDATA%\Claude\claude_desktop_config.json` on Windows):

```json
{
  "mcpServers": {
    "codenav": {
      "command": "C:\\source\\COA CodeNav MCP\\COA.CodeNav.McpServer\\bin\\Release\\net9.0\\COA.CodeNav.McpServer.exe"
    }
  }
}
```

## 🛠️ Available Tools (21 Total)

### Workspace Management (3 tools)

#### `roslyn_load_solution`
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

#### `roslyn_load_project`
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

#### `roslyn_get_workspace_statistics`
Get statistics about loaded workspaces and resource usage.

**When to use:**
- "Show workspace memory usage"
- "How many workspaces are loaded?"
- "Check workspace performance"

### 📍 Code Navigation (4 tools)

#### `roslyn_goto_definition`
Navigate to the definition of a symbol.

**When to use:**
- "Where is UserService defined?"
- "Show me the definition of ProcessOrder"
- "Jump to where this class is declared"

**Example:**
```json
{
  "filePath": "Program.cs",
  "line": 42,
  "column": 25
}
```

#### `roslyn_find_all_references`
Find all references to a symbol across the codebase.

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

#### `roslyn_find_implementations`
Find all implementations of interfaces and overrides.

**When to use:**
- "What classes implement IRepository?"
- "Show me all implementations of this interface"
- "What overrides ProcessOrder?"

#### `roslyn_hover`
Get detailed information about a symbol (signature, documentation, type info).

**When to use:**
- "What does this method do?"
- "Show me the documentation for ProcessOrder"
- "What parameters does this function take?"

### 🔍 Code Search & Discovery (3 tools)

#### `roslyn_symbol_search`
Search for symbols by name or pattern across the solution.

**When to use:**
- "Find all classes with 'Service' in the name"
- "Search for methods starting with 'Process'"
- "Find the UserController class"
- "Show me all interfaces"

**Search types:**
- `contains` - Partial match anywhere in name
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
  "maxResults": 50
}
```

#### `roslyn_document_symbols`
Extract the symbol hierarchy from a file.

**When to use:**
- "Show me the structure of this file"
- "What methods are in UserService.cs?"
- "Give me an outline of this class"

#### `roslyn_get_type_members`
List all members of a type with documentation.

**When to use:**
- "What methods does UserService have?"
- "Show me all properties of Order class"
- "List members including inherited ones"

### 📊 Code Analysis (4 tools)

#### `roslyn_get_diagnostics`
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
  "maxResults": 50
}
```

#### `roslyn_code_metrics`
Calculate code complexity and maintainability metrics.

**When to use:**
- "Calculate complexity of this method"
- "Find methods that are too complex"
- "Show maintainability index"
- "Identify refactoring candidates"

**Metrics provided:**
- Cyclomatic Complexity
- Lines of Code
- Maintainability Index
- Depth of Inheritance
- Class Coupling

#### `roslyn_find_unused_code`
Find potentially unused code elements.

**When to use:**
- "Find dead code in the project"
- "Show me unused private methods"
- "Clean up unused classes"
- "Identify code that can be removed"

#### `roslyn_type_hierarchy`
View complete type hierarchy including base classes and derived types.

**When to use:**
- "Show inheritance hierarchy of UserService"
- "What classes derive from BaseController?"
- "View the complete type hierarchy"

### 🔄 Code Flow Analysis (1 tool)

#### `roslyn_trace_call_stack`
Trace execution paths through code.

**When to use:**
- "Show me how ProcessOrder gets called"
- "Trace execution to this method"
- "What calls ValidateUser?"
- "Follow the call chain from Main"

**Directions:**
- `forward` - Follow calls made by the method
- `backward` - Find callers of the method

### ✏️ Code Refactoring & Generation (6 tools)

#### `roslyn_rename_symbol`
Rename symbols across the entire solution with conflict detection.

**When to use:**
- "Rename UserService to UserManager"
- "Change this variable name everywhere"
- "Refactor: rename ProcessOrder"

**Features:**
- Preview changes before applying
- Conflict detection
- Optional file renaming
- Rename in comments/strings

#### `roslyn_apply_code_fix`
Apply available code fixes for diagnostics.

**When to use:**
- "Fix this compilation error"
- "Apply the suggested fix"
- "Resolve this warning"

#### `roslyn_generate_code`
Generate code for common patterns.

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

#### `roslyn_extract_method`
Extract selected code into a new method.

**When to use:**
- "Extract this code into a method"
- "Refactor: extract method"
- "Create a method from this logic"

#### `roslyn_add_missing_usings`
Add missing using directives for unresolved types.

**When to use:**
- "Fix missing imports"
- "Add required using statements"
- "Resolve unresolved type references"

#### `roslyn_format_document`
Format code according to project settings and .editorconfig.

**When to use:**
- "Format this file"
- "Fix indentation"
- "Clean up code formatting"
- "Organize using statements"

## 💡 Example Conversations

### Understanding a Codebase

```
User: "I need to understand how user authentication works in this codebase"

AI: I'll help you understand the authentication system. Let me search for authentication-related code.

*Uses roslyn_symbol_search with query "auth*" and wildcard search*
*Finds AuthenticationService, IAuthenticationProvider, AuthController*

Found several authentication components. Let me examine the main service structure.

*Uses roslyn_get_type_members on AuthenticationService*
*Uses roslyn_trace_call_stack to understand the flow*

The authentication system consists of:
1. `AuthenticationService` - Main service handling login/logout
2. `IAuthenticationProvider` - Interface for different auth providers  
3. `JwtAuthProvider` - JWT token implementation
4. `AuthController` - REST API endpoints

The flow is: AuthController → AuthenticationService → IAuthenticationProvider
```

### Refactoring Code

```
User: "This method is too complex, can you help refactor it?"

AI: Let me analyze the complexity of this method first.

*Uses roslyn_code_metrics to measure complexity*

This method has a cyclomatic complexity of 15, which is quite high. Let me help refactor it.

*Uses roslyn_extract_method to extract logical blocks*
*Uses roslyn_rename_symbol to give meaningful names*

I've extracted 3 logical blocks into separate methods:
1. ValidateInput() - Input validation logic
2. ProcessBusinessRules() - Core business logic
3. PrepareResponse() - Response formatting

The complexity is now reduced to 5, making it much more maintainable.
```

### Finding and Fixing Issues

```
User: "The build is failing, can you help?"

AI: Let me check for compilation errors.

*Uses roslyn_get_diagnostics with scope "solution"*

Found 3 errors:
1. CS0246: Type 'IUserRepository' not found in OrderService.cs:15
2. CS1061: 'User' does not contain 'EmailAddress' in UserValidator.cs:42
3. CS0103: Name 'logger' does not exist in ProcessingService.cs:78

Let me fix these:

*Uses roslyn_add_missing_usings for the first error*
*Uses roslyn_hover to understand the User type*
*Uses roslyn_find_all_references to check logger usage*

Fixed:
1. Added missing using for IUserRepository
2. The property is 'Email' not 'EmailAddress' 
3. Need to inject ILogger in the constructor
```

## 🏗️ Architecture

### Core Components

- **MSBuildWorkspaceManager** - Manages Roslyn workspace lifecycle and caching
- **RoslynService** - Core service providing code analysis operations
- **Tool Classes** - Individual tool implementations with MCP protocol integration
- **Symbol Cache** - Performance optimization for repeated symbol lookups
- **Resource Provider** - Manages large results with progressive disclosure

### Design Principles

1. **AI-First Design** - Every response includes insights and suggested next actions
2. **Token Efficiency** - Automatic summarization prevents context overflow
3. **Error Recovery** - Detailed error information with actionable steps
4. **Performance** - Symbol caching and efficient workspace management
5. **Extensibility** - Easy to add new tools using attribute-based discovery

## 🔧 Troubleshooting

### Common Issues

**"Workspace not loaded" error**
- Ensure you've called `roslyn_load_solution` or `roslyn_load_project` first
- Check the solution/project path is correct
- Verify the solution builds successfully in Visual Studio

**"Symbol not found" errors**
- Make sure the file is part of the loaded solution/project
- Check that the line and column numbers are correct (1-based)
- Ensure the code compiles without errors

**Performance issues**
- Use `roslyn_get_workspace_statistics` to check memory usage
- Consider loading individual projects instead of large solutions
- Enable response summarization for large results

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

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- Built on [Microsoft's Roslyn](https://github.com/dotnet/roslyn) compiler platform
- Uses the COA.Mcp.Protocol library for MCP communication
- Inspired by Visual Studio's code navigation features
- Thanks to all contributors and users!

## 📊 Status

- ✅ 21 tools implemented and tested
- ✅ Full Roslyn integration
- ✅ AI-optimized responses
- ✅ Token management
- ✅ Error recovery
- 🚧 TypeScript support (planned)
- 🚧 Razor support (planned)

---

For more information, issues, or contributions, visit our [GitHub repository](https://github.com/your-org/coa-codenav-mcp).