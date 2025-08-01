# COA CodeNav MCP Server

A powerful MCP (Model Context Protocol) server providing Roslyn-based C# code analysis and navigation tools for AI assistants.

## Overview

COA CodeNav MCP Server brings the power of Visual Studio's code navigation features to AI assistants. Built on Microsoft's Roslyn compiler platform, it provides deep understanding of C# code structure, enabling AI to navigate, analyze, and understand codebases like a senior developer.

## Features

- **Complete C# Code Analysis** - Full Roslyn compiler integration for accurate code understanding
- **AI-Optimized Responses** - Structured outputs with insights, next actions, and error recovery
- **Workspace Management** - Load and analyze entire solutions or individual projects
- **Symbol Caching** - Fast repeated lookups with intelligent caching
- **Progressive Disclosure** - Automatic response summarization for large results

## Available Tools

### 🚀 Workspace Management

#### Load Solution
**AI tool name**: `roslyn_load_solution`  
**When to use**: 
- "Load the MyApp.sln solution"
- "Open the solution file in C:\Projects\MyApp"
- "I want to analyze this C# solution"

```bash
# Loads a complete Visual Studio solution
roslyn_load_solution --solutionPath "C:\Projects\MyApp\MyApp.sln"
```

#### Load Project
**AI tool name**: `roslyn_load_project`  
**When to use**:
- "Load just the MyApp.Core project"
- "Open the csproj file"
- "I only need to analyze this one project"

```bash
# Loads a single C# project
roslyn_load_project --projectPath "C:\Projects\MyApp\MyApp.Core\MyApp.Core.csproj"
```

### 📍 Code Navigation

#### Go to Definition
**AI tool name**: `roslyn_goto_definition`  
**When to use**:
- "Where is UserService defined?"
- "Show me the definition of the ProcessOrder method"
- "Jump to where this class is declared"
- "What file contains the IRepository interface?"

```bash
# Navigate to symbol definition
roslyn_goto_definition --filePath "Program.cs" --line 42 --column 25
```

#### Find All References
**AI tool name**: `roslyn_find_all_references`  
**When to use**:
- "Where is UserService used in the codebase?"
- "Find all calls to ProcessOrder"
- "Show me all references to this variable"
- "What code uses the IRepository interface?"

```bash
# Find all usages of a symbol
roslyn_find_all_references --filePath "Services/UserService.cs" --line 15 --column 20
```

#### Hover Information
**AI tool name**: `roslyn_hover`  
**When to use**:
- "What does this method do?"
- "Show me the documentation for ProcessOrder"
- "What parameters does this function take?"
- "What type is this variable?"

```bash
# Get quick info about a symbol
roslyn_hover --filePath "Program.cs" --line 42 --column 25
```

### 🔍 Code Search & Discovery

#### Symbol Search
**AI tool name**: `roslyn_symbol_search`  
**When to use**:
- "Find all classes with 'Service' in the name"
- "Search for methods that start with 'Process'"
- "Show me all interfaces in the project"
- "Find the UserController class (I might have misspelled it)"
- "Look for anything related to authentication"

```bash
# Search with various patterns
roslyn_symbol_search --query "Service" --searchType "contains"
roslyn_symbol_search --query "IUser*" --searchType "wildcard"  
roslyn_symbol_search --query "Usr~" --searchType "fuzzy"
roslyn_symbol_search --query "Process.*" --searchType "regex"
```

#### Find Implementations
**AI tool name**: `roslyn_find_implementations`  
**When to use**:
- "What classes implement IRepository?"
- "Show me all implementations of this interface"
- "Find concrete classes for IUserService"
- "What overrides the ProcessOrder method?"

```bash
# Find implementations of interfaces or abstract methods
roslyn_find_implementations --filePath "Interfaces/IRepository.cs" --line 10 --column 18
```

### 📊 Code Analysis

#### Document Symbols
**AI tool name**: `roslyn_document_symbols`  
**When to use**:
- "Show me the structure of this file"
- "What methods are in UserService.cs?"
- "Give me an outline of this class"
- "List all members in this file"

```bash
# Extract symbol hierarchy from a document
roslyn_document_symbols --filePath "Services/UserService.cs" --includePrivate true
```

#### Get Type Members
**AI tool name**: `roslyn_get_type_members`  
**When to use**:
- "What methods does UserService have?"
- "Show me all properties of the Order class"
- "List the members of IRepository including inherited ones"
- "What can I call on this object?"

```bash
# List all members of a type
roslyn_get_type_members --filePath "Models/Order.cs" --line 15 --column 14 --includeInherited true
```

#### Get Diagnostics
**AI tool name**: `roslyn_get_diagnostics`  
**When to use**:
- "Show me all errors in the solution"
- "What warnings do I have?"
- "Check for compilation errors"
- "Find code quality issues"
- "Are there any nullable reference warnings?"

```bash
# Get compilation diagnostics
roslyn_get_diagnostics --scope "solution" --severities ["Error", "Warning"]
roslyn_get_diagnostics --scope "file" --filePath "Program.cs"
```

### 🔄 Code Flow Analysis

#### Trace Call Stack
**AI tool name**: `roslyn_trace_call_stack`  
**When to use**:
- "Show me how ProcessOrder gets called"
- "Trace the execution path to this method"
- "What calls ValidateUser?"
- "Follow the call chain from Main to this function"
- "Show me the code flow from the API endpoint"

```bash
# Trace execution paths
roslyn_trace_call_stack --filePath "Services/OrderService.cs" --line 45 --column 20 --direction "backward"
roslyn_trace_call_stack --filePath "Program.cs" --line 10 --column 15 --direction "forward"
```

### ✏️ Code Refactoring

#### Rename Symbol
**AI tool name**: `roslyn_rename_symbol`  
**When to use**:
- "Rename UserService to UserManager everywhere"
- "Change this variable name throughout the codebase"
- "Refactor: rename ProcessOrder to ProcessCustomerOrder"
- "Update this method name across all files"

```bash
# Rename symbols with conflict detection
roslyn_rename_symbol --filePath "Services/UserService.cs" --line 10 --column 14 --newName "UserManager" --preview true
```

## Example Conversations

### Finding and Understanding Code

```
User: "I need to understand how user authentication works in this codebase"

AI: I'll help you understand the authentication system. Let me search for authentication-related code.

*Uses roslyn_symbol_search with query "auth*" and wildcard search*
*Finds AuthenticationService, IAuthenticationProvider, AuthController*

Found several authentication components. Let me examine the main authentication service.

*Uses roslyn_document_symbols on AuthenticationService.cs*
*Uses roslyn_get_type_members on AuthenticationService*

The authentication system consists of:
1. `AuthenticationService` - Main service handling login/logout
2. `IAuthenticationProvider` - Interface for different auth providers
3. `JwtAuthProvider` - JWT token-based implementation
...
```

### Tracing Code Execution

```
User: "How does a user request end up calling the database?"

AI: I'll trace the execution path from your API endpoint to the database. Let me start with a typical controller.

*Uses roslyn_find_all_references on a controller action*
*Uses roslyn_trace_call_stack with direction "forward"*
*Shows the complete call chain: Controller → Service → Repository → DbContext*
```

### Refactoring Code

```
User: "This OrderService class is poorly named, it should be OrderProcessor"

AI: I'll rename OrderService to OrderProcessor throughout your codebase. Let me first preview the changes.

*Uses roslyn_rename_symbol with preview=true*
*Shows all 47 locations that will be updated*
*Identifies no naming conflicts*

This will update 47 references across 12 files. Shall I proceed with the rename?
```

## Installation

1. Ensure you have .NET 9.0 SDK installed
2. Clone this repository
3. Build the project:
   ```bash
   dotnet build --configuration Release
   ```
4. Configure your AI assistant to use the MCP server

## Configuration

Add to your Claude configuration file:

```json
{
  "mcpServers": {
    "codenav": {
      "command": "C:\\source\\COA CodeNav MCP\\COA.CodeNav.McpServer\\bin\\Release\\net9.0\\COA.CodeNav.McpServer.exe"
    }
  }
}
```

## Requirements

- .NET 9.0 or later
- Windows, macOS, or Linux
- AI assistant with MCP support (Claude Desktop, etc.)

## Architecture

The server is built with:
- **Roslyn Compiler Platform** - Microsoft's C# compiler APIs
- **MSBuild Workspaces** - Solution and project loading
- **MCP Protocol** - Standardized AI tool communication
- **Symbol Caching** - Performance optimization for repeated queries

## Contributing

Contributions are welcome! Please read our contributing guidelines before submitting PRs.

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Acknowledgments

- Built on Microsoft's Roslyn compiler platform
- Uses the COA.Mcp.Protocol library for MCP communication
- Inspired by Visual Studio's code navigation features