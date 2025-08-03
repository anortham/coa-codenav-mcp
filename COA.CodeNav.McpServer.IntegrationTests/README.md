# COA CodeNav MCP Server Integration Tests

This project contains integration tests for the COA CodeNav MCP Server, focusing on:

1. **MCP Protocol Compliance** - Verifying JSON-RPC communication
2. **Tool Response Schema** - Ensuring all tools follow the consistent result format
3. **Error Handling** - Testing error scenarios and recovery guidance
4. **Concurrent Operations** - Ensuring the server handles multiple requests

## Running the Tests

```bash
# Build the main project first
dotnet build ../COA.CodeNav.McpServer/COA.CodeNav.McpServer.csproj

# Run all tests
dotnet test

# Run with detailed output
dotnet test --logger "console;verbosity=detailed"

# Run specific test
dotnet test --filter "FullyQualifiedName~WorkspaceStatisticsTests"
```

## Test Structure

### McpTestBase
Base class that handles:
- Starting the MCP server process
- Managing stdin/stdout communication
- JSON-RPC protocol handling
- Server lifecycle management

### Test Categories

1. **WorkspaceStatisticsTests** - Tests the workspace management functionality
2. **ErrorHandlingTests** - Verifies error responses include proper recovery guidance
3. **McpProtocolTests** - Tests MCP protocol compliance (tools/list, resources/list, etc.)

## Key Testing Patterns

### Testing Tool Responses
```csharp
var result = await CallToolAsync<ToolResultType>("tool_name", parameters);
result.Should().NotBeNull();
result.Success.Should().BeTrue();
// Verify schema compliance...
```

### Testing Error Scenarios
```csharp
result.Success.Should().BeFalse();
result.Error.Should().NotBeNull();
result.Error.Code.Should().Be(ErrorCodes.EXPECTED_ERROR);
result.Error.Recovery.Steps.Should().Contain("helpful recovery step");
```

## Adding New Tests

1. Create a new test class inheriting from `McpTestBase`
2. Use `CallToolAsync<T>` to invoke MCP tools
3. Use FluentAssertions for readable assertions
4. Verify both success and error scenarios
5. Check schema compliance for all responses

## Troubleshooting

- If tests hang, check that the server process started correctly
- Server stderr is redirected to test output for debugging
- Use `Output.WriteLine()` for additional debugging info
- The server process is killed after each test class completes