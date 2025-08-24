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

public class TsSymbolSearchToolTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<TsSymbolSearchTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly TsSymbolSearchTool _tool;
    private readonly string _testProjectPath;

    public TsSymbolSearchToolTests(ITestOutputHelper output)
    {
        _output = output;
        
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new XunitLoggerProvider(output));
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        
        _logger = loggerFactory.CreateLogger<TsSymbolSearchTool>();
        _compilerManager = new TypeScriptCompilerManager(loggerFactory.CreateLogger<TypeScriptCompilerManager>());
        _workspaceService = new TypeScriptWorkspaceService(loggerFactory.CreateLogger<TypeScriptWorkspaceService>(), _compilerManager);
        _tool = new TsSymbolSearchTool(TestServiceProvider.Create(), _logger, _workspaceService, _compilerManager);
        
        // Use the test data project
        _testProjectPath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "TypeScriptProject");
    }

    [Fact(Skip = "TSP navto command requires additional setup - to be fixed")]
    public async Task SymbolSearch_ShouldFindUserClass()
    {
        // Arrange - Load the TypeScript project
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        var loadResult = await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        loadResult.Success.Should().BeTrue("Project should load successfully");
        
        // Act - Search for "User" symbols
        var parameters = new TsSymbolSearchParams
        {
            Query = "User",
            MaxResults = 50
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        _output.WriteLine($"Success: {result.Success}");
        _output.WriteLine($"Message: {result.Message}");
        
        result.Success.Should().BeTrue();
        result.Symbols.Should().NotBeNull();
        result.Symbols.Should().NotBeEmpty();
        
        // Should find UserService class
        var userServiceClass = result.Symbols.FirstOrDefault(s => s.Name == "UserService" && s.Kind == "Class");
        userServiceClass.Should().NotBeNull("Should find UserService class");
        
        // Should find User interface
        var userInterface = result.Symbols.FirstOrDefault(s => s.Name == "User" && s.Kind == "Interface");
        userInterface.Should().NotBeNull("Should find User interface");
        
        // Should find UserRole enum
        var userRoleEnum = result.Symbols.FirstOrDefault(s => s.Name == "UserRole" && s.Kind == "Enum");
        userRoleEnum.Should().NotBeNull("Should find UserRole enum");
        
        if (result.Symbols != null)
        {
            _output.WriteLine($"Found {result.Symbols.Count} symbols matching 'User':");
            foreach (var symbol in result.Symbols)
            {
                _output.WriteLine($"  - {symbol.Kind}: {symbol.Name} in {Path.GetFileName(symbol.FilePath)}");
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

    [Fact(Skip = "TSP navto command requires additional setup - to be fixed")]
    public async Task SymbolSearch_WithPartialMatch_ShouldFindSymbols()
    {
        // Arrange
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        
        // Act - Search for partial match
        var parameters = new TsSymbolSearchParams
        {
            Query = "add",  // Should match addUser
            MaxResults = 50
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        result.Success.Should().BeTrue();
        result.Symbols.Should().NotBeNull();
        
        // Should find addUser method
        var addUserMethod = result.Symbols.FirstOrDefault(s => s.Name.Contains("add", StringComparison.OrdinalIgnoreCase));
        addUserMethod.Should().NotBeNull("Should find methods containing 'add'");
        
        _output.WriteLine($"Found {result.Symbols?.Count ?? 0} symbols containing 'add'");
    }

    [Fact(Skip = "TSP navto command requires additional setup - to be fixed")]
    public async Task SymbolSearch_WithSymbolKindFilter_ShouldFilterResults()
    {
        // Arrange
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        
        // Act - Search for only interfaces
        var parameters = new TsSymbolSearchParams
        {
            Query = "User",  // Search for User-related symbols
            SymbolKinds = new System.Collections.Generic.List<string> { "Interface" },
            MaxResults = 50
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        result.Success.Should().BeTrue();
        if (result.Symbols != null && result.Symbols.Any())
        {
            result.Symbols.All(s => s.Kind == "Interface").Should().BeTrue("All results should be interfaces");
            _output.WriteLine($"Found {result.Symbols.Count} interfaces");
        }
    }

    [Fact(Skip = "TSP navto command requires additional setup - to be fixed")]
    public async Task SymbolSearch_WithMaxResults_ShouldLimitResults()
    {
        // Arrange
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        
        // Act
        var parameters = new TsSymbolSearchParams
        {
            Query = "get",  // Search for "get" to find some symbols
            MaxResults = 3
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        result.Success.Should().BeTrue();
        result.Symbols.Should().NotBeNull();
        result.Symbols.Count.Should().BeLessThanOrEqualTo(3);
    }

    [Fact(Skip = "TSP navto command requires additional setup - to be fixed")]
    public async Task SymbolSearch_WithNoMatches_ShouldReturnEmptyList()
    {
        // Arrange
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        
        // Act
        var parameters = new TsSymbolSearchParams
        {
            Query = "NonExistentSymbolXYZ123",
            MaxResults = 50
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        result.Success.Should().BeTrue();
        result.Symbols.Should().NotBeNull();
        result.Symbols.Should().BeEmpty();
        result.Insights.Should().NotBeNull();
        result.Insights.Should().Contain(i => i.Contains("No symbols found"));
    }

    [Fact]
    public async Task SymbolSearch_WithoutLoadedProject_ShouldReturnError()
    {
        // Arrange - Don't load any project
        
        // Act
        var parameters = new TsSymbolSearchParams
        {
            Query = "User",
            MaxResults = 50
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error.Code.Should().Be("WORKSPACE_NOT_LOADED");
    }

    [Fact]
    public void SymbolSearch_ShouldHaveCorrectToolName()
    {
        _tool.Name.Should().Be("ts_symbol_search");
    }

    [Fact]
    public void SymbolSearch_ShouldHaveProperDescription()
    {
        _tool.Description.Should().NotBeNullOrWhiteSpace();
        _tool.Description.Should().Contain("Search");
        _tool.Description.Should().Contain("TypeScript");
        _tool.Description.Should().Contain("types");
    }

    public void Dispose()
    {
        _tool?.Dispose();
        _workspaceService?.Dispose();
        _compilerManager?.Dispose();
    }
}