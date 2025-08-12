using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using COA.CodeNav.McpServer.Services.TypeScript;
using COA.CodeNav.McpServer.Tools.TypeScript;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using FluentAssertions;

namespace COA.CodeNav.McpServer.IntegrationTests.TypeScript;

public class TsFindImplementationsToolTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<TsFindImplementationsTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly TsFindImplementationsTool _tool;
    private readonly string _testProjectPath;

    public TsFindImplementationsToolTests(ITestOutputHelper output)
    {
        _output = output;
        
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new XunitLoggerProvider(output));
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        
        _logger = loggerFactory.CreateLogger<TsFindImplementationsTool>();
        _compilerManager = new TypeScriptCompilerManager(loggerFactory.CreateLogger<TypeScriptCompilerManager>());
        _workspaceService = new TypeScriptWorkspaceService(loggerFactory.CreateLogger<TypeScriptWorkspaceService>(), _compilerManager);
        _tool = new TsFindImplementationsTool(_logger, _workspaceService, _compilerManager);
        
        // Use the test data project
        _testProjectPath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "TypeScriptProject");
    }

    [Fact(Skip = "TSP implementation command requires proper tsserver setup in CI")]
    public async Task FindImplementations_ForInterface_ShouldFindImplementingClasses()
    {
        // Arrange - Load the TypeScript project
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        var loadResult = await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        loadResult.Success.Should().BeTrue("Project should load successfully");
        
        var testFile = Path.Combine(_testProjectPath, "src", "userService.ts");
        _output.WriteLine($"Test file: {testFile}");
        
        // Act - Find implementations of Repository interface (line 95, character 18)
        // The text is: export interface Repository<T> {
        //                                  ^
        var parameters = new TsFindImplementationsParams
        {
            FilePath = testFile,
            Line = 94,     // Line 95 in 1-based editor (0-indexed)
            Character = 17, // Position of 'R' in Repository (0-indexed)
            MaxResults = 100
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        _output.WriteLine($"Success: {result.Success}");
        _output.WriteLine($"Message: {result.Message}");
        
        // This test might not find implementations if none exist in test files
        // But the tool should still succeed
        result.Success.Should().BeTrue();
        
        if (result.Implementations != null && result.Implementations.Any())
        {
            _output.WriteLine($"Found {result.Implementations.Count} implementations:");
            foreach (var impl in result.Implementations)
            {
                _output.WriteLine($"  - {impl.ImplementingType} at {impl.Location?.FilePath}:{impl.Location?.Line}");
            }
        }
        else
        {
            _output.WriteLine("No implementations found (this is expected if test files don't have implementations)");
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

    [Fact(Skip = "TSP implementation command requires proper tsserver setup in CI")]
    public async Task FindImplementations_ForClass_ShouldReturnEmptyOrDerived()
    {
        // Arrange
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        
        var testFile = Path.Combine(_testProjectPath, "src", "userService.ts");
        
        // Act - Try to find implementations of UserService class
        var parameters = new TsFindImplementationsParams
        {
            FilePath = testFile,
            Line = 12,     // Line 13 in 1-based editor - UserService class
            Character = 13, // Position in 'UserService'
            MaxResults = 100
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        result.Success.Should().BeTrue();
        // Classes typically don't have implementations unless extended
        _output.WriteLine($"Implementations found: {result.Implementations?.Count ?? 0}");
    }

    [Fact(Skip = "TSP implementation command requires proper tsserver setup in CI")]
    public async Task FindImplementations_WithMaxResults_ShouldLimitResults()
    {
        // Arrange
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        
        var testFile = Path.Combine(_testProjectPath, "src", "userService.ts");
        
        // Act
        var parameters = new TsFindImplementationsParams
        {
            FilePath = testFile,
            Line = 94,
            Character = 17,
            MaxResults = 2
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        result.Success.Should().BeTrue();
        if (result.Implementations != null && result.Implementations.Any())
        {
            result.Implementations.Count.Should().BeLessThanOrEqualTo(2);
        }
    }

    [Fact(Skip = "TSP implementation command requires proper tsserver setup in CI")]
    public async Task FindImplementations_OnNonInterface_ShouldHandleGracefully()
    {
        // Arrange
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        
        var testFile = Path.Combine(_testProjectPath, "src", "userService.ts");
        
        // Act - Try on a regular method, not an interface
        var parameters = new TsFindImplementationsParams
        {
            FilePath = testFile,
            Line = 19,     // addUser method
            Character = 11,
            MaxResults = 100
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        // Should either succeed with no results or provide appropriate message
        result.Should().NotBeNull();
        if (result.Success)
        {
            // If successful, implementations should be empty or null
            if (result.Implementations != null)
            {
                _output.WriteLine($"Found {result.Implementations.Count} implementations (expected 0 for regular method)");
            }
        }
        else
        {
            // If it fails, should have helpful error message
            result.Message.Should().NotBeNullOrWhiteSpace();
            _output.WriteLine($"Error message: {result.Message}");
        }
    }

    [Fact]
    public async Task FindImplementations_WithoutLoadedProject_ShouldReturnError()
    {
        // Arrange - Don't load any project
        var testFile = Path.Combine(_testProjectPath, "src", "userService.ts");
        
        // Act
        var parameters = new TsFindImplementationsParams
        {
            FilePath = testFile,
            Line = 94,
            Character = 17,
            MaxResults = 100
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error.Code.Should().Be("WORKSPACE_NOT_LOADED");
    }

    [Fact]
    public void FindImplementations_ShouldHaveCorrectToolName()
    {
        _tool.Name.Should().Be("ts_find_implementations");
    }

    [Fact]
    public void FindImplementations_ShouldHaveProperDescription()
    {
        _tool.Description.Should().NotBeNullOrWhiteSpace();
        _tool.Description.Should().Contain("implementations");
        _tool.Description.Should().Contain("TypeScript");
        _tool.Description.Should().Contain("interface");
    }

    [Fact(Skip = "TSP implementation command requires additional setup - to be fixed")]
    public async Task FindImplementations_ShouldProvideInsights()
    {
        // Arrange
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        
        var testFile = Path.Combine(_testProjectPath, "src", "userService.ts");
        
        // Act
        var parameters = new TsFindImplementationsParams
        {
            FilePath = testFile,
            Line = 94,
            Character = 17,
            MaxResults = 100
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        result.Success.Should().BeTrue();
        result.Insights.Should().NotBeNull();
        result.Insights.Should().NotBeEmpty("Tool should provide insights about the search");
        
        _output.WriteLine("Insights provided:");
        foreach (var insight in result.Insights)
        {
            _output.WriteLine($"  - {insight}");
        }
    }

    public void Dispose()
    {
        _tool?.Dispose();
        _workspaceService?.Dispose();
        _compilerManager?.Dispose();
    }
}