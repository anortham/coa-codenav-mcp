using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using COA.CodeNav.McpServer.Services.TypeScript;
using COA.CodeNav.McpServer.Tools.TypeScript;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;
using FluentAssertions;

namespace COA.CodeNav.McpServer.IntegrationTests.TypeScript;

public class TsDocumentSymbolsToolTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<TsDocumentSymbolsTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly TsDocumentSymbolsTool _tool;
    private readonly string _testProjectPath;

    public TsDocumentSymbolsToolTests(ITestOutputHelper output)
    {
        _output = output;
        
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new XunitLoggerProvider(output));
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        
        _logger = loggerFactory.CreateLogger<TsDocumentSymbolsTool>();
        _compilerManager = new TypeScriptCompilerManager(loggerFactory.CreateLogger<TypeScriptCompilerManager>());
        _workspaceService = new TypeScriptWorkspaceService(loggerFactory.CreateLogger<TypeScriptWorkspaceService>(), _compilerManager);
        _tool = new TsDocumentSymbolsTool(TestServiceProvider.Create(), _logger, _workspaceService, _compilerManager);
        
        // Use the test data project
        _testProjectPath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "TypeScriptProject");
    }

    [Fact(Skip = "TSP navtree command requires proper tsserver setup in CI")]
    public async Task DocumentSymbols_ShouldExtractClassesAndMethods()
    {
        // Arrange - Load the TypeScript project
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        var loadResult = await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        loadResult.Success.Should().BeTrue("Project should load successfully");
        
        var testFile = Path.Combine(_testProjectPath, "src", "userService.ts");
        _output.WriteLine($"Test file: {testFile}");
        
        // Act - Get document symbols
        var parameters = new TsDocumentSymbolsParams
        {
            FilePath = testFile,
            MaxResults = 100
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        _output.WriteLine($"Success: {result.Success}");
        _output.WriteLine($"Message: {result.Message}");
        
        result.Success.Should().BeTrue();
        result.Symbols.Should().NotBeNull();
        result.Symbols.Should().NotBeEmpty();
        
        // Should find the UserService class
        var userServiceClass = result.Symbols.FirstOrDefault(s => s.Name == "UserService" && s.Kind == "Class");
        userServiceClass.Should().NotBeNull("Should find UserService class");
        
        // Should find the User interface
        var userInterface = result.Symbols.FirstOrDefault(s => s.Name == "User" && s.Kind == "Interface");
        userInterface.Should().NotBeNull("Should find User interface");
        
        // Should find the UserRole enum
        var userRoleEnum = result.Symbols.FirstOrDefault(s => s.Name == "UserRole" && s.Kind == "Enum");
        userRoleEnum.Should().NotBeNull("Should find UserRole enum");
        
        // Should find methods within the class
        var addUserMethod = result.Symbols.FirstOrDefault(s => s.Name == "addUser" && s.Kind == "Method");
        addUserMethod.Should().NotBeNull("Should find addUser method");
        
        // Check that we have a reasonable number of symbols
        result.Symbols.Count.Should().BeGreaterThan(10, "Should find multiple symbols in the document");
        
        if (result.Symbols != null)
        {
            _output.WriteLine($"Found {result.Symbols.Count} symbols:");
            foreach (var symbol in result.Symbols.Take(20))
            {
                _output.WriteLine($"  - {symbol.Kind}: {symbol.Name} (Level: {symbol.Level})");
            }
        }
        
        if (result.Insights != null)
        {
            _output.WriteLine("Insights:");
            foreach (var insight in result.Insights)
            {
                _output.WriteLine($"  - {insight}");
            }
        }
    }

    [Fact(Skip = "TSP navtree command requires proper tsserver setup in CI")]
    public async Task DocumentSymbols_WithMaxResults_ShouldLimitResults()
    {
        // Arrange
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        
        var testFile = Path.Combine(_testProjectPath, "src", "userService.ts");
        
        // Act
        var parameters = new TsDocumentSymbolsParams
        {
            FilePath = testFile,
            MaxResults = 5
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        result.Success.Should().BeTrue();
        result.Symbols.Should().NotBeNull();
        result.Symbols.Count.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task DocumentSymbols_WithoutLoadedProject_ShouldReturnError()
    {
        // Arrange - Don't load any project
        var testFile = Path.Combine(_testProjectPath, "src", "nonexistent.ts");
        
        // Act
        var parameters = new TsDocumentSymbolsParams
        {
            FilePath = testFile,
            MaxResults = 100
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error.Code.Should().Be("WORKSPACE_NOT_LOADED");
    }

    [Fact]
    public void DocumentSymbols_ShouldHaveCorrectToolName()
    {
        _tool.Name.Should().Be("ts_document_symbols");
    }

    [Fact]
    public void DocumentSymbols_ShouldHaveProperDescription()
    {
        _tool.Description.Should().NotBeNullOrWhiteSpace();
        _tool.Description.Should().Contain("file structure");
        _tool.Description.Should().Contain("TypeScript");
    }

    public void Dispose()
    {
        _tool?.Dispose();
        _workspaceService?.Dispose();
        _compilerManager?.Dispose();
    }
}