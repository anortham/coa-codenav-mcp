using System;
using System.IO;
using System.Threading.Tasks;
using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using COA.CodeNav.McpServer.Services.TypeScript;
using COA.CodeNav.McpServer.Tools.TypeScript;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using FluentAssertions;

namespace COA.CodeNav.McpServer.IntegrationTests.TypeScript;

public class TsGoToDefinitionToolTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<TsGoToDefinitionTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly TsGoToDefinitionTool _tool;
    private readonly string _testProjectPath;

    public TsGoToDefinitionToolTests(ITestOutputHelper output)
    {
        _output = output;
        
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new XunitLoggerProvider(output));
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        
        _logger = loggerFactory.CreateLogger<TsGoToDefinitionTool>();
        _compilerManager = new TypeScriptCompilerManager(loggerFactory.CreateLogger<TypeScriptCompilerManager>());
        _workspaceService = new TypeScriptWorkspaceService(loggerFactory.CreateLogger<TypeScriptWorkspaceService>(), _compilerManager);
        _tool = new TsGoToDefinitionTool(_logger, _workspaceService, _compilerManager);
        
        _testProjectPath = @"C:\source\COA CodeNav MCP\ts-test-project";
    }

    [Fact(Skip = "TSP gotoDefinition command requires proper tsserver setup in CI")]
    public async Task GoToDefinition_ShouldFindClassDefinition()
    {
        // Arrange - Load the TypeScript project
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        var loadResult = await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        loadResult.Success.Should().BeTrue("Project should load successfully");
        
        var testFile = Path.Combine(_testProjectPath, "src", "index.ts");
        _output.WriteLine($"Test file: {testFile}");
        
        // Act - Get definition for UserService at line 28 (0-indexed), character 25
        // The text is: const userService = new UserService();
        //                                        ^
        var parameters = new TsGoToDefinitionParams
        {
            FilePath = testFile,
            Line = 28,     // Line 29 in 1-based editor
            Character = 24 // Position of 'U' in UserService (0-indexed)
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        _output.WriteLine($"Success: {result.Success}");
        _output.WriteLine($"Message: {result.Message}");
        
        if (result.Locations != null)
        {
            _output.WriteLine($"Locations count: {result.Locations.Count}");
            foreach (var loc in result.Locations)
            {
                _output.WriteLine($"  - {loc.FilePath}:{loc.Line}:{loc.Column}");
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
        
        result.Success.Should().BeTrue("Should successfully find definition");
        result.Locations.Should().NotBeNull();
        result.Locations!.Count.Should().BeGreaterThan(0, "Should find at least one definition");
        
        // The definition should be at line 7 (0-indexed) in the same file
        var definition = result.Locations[0];
        definition.FilePath.Should().EndWith("index.ts");
        definition.Line.Should().Be(7); // Line 8 in 1-based editor where class UserService is defined
    }

    public void Dispose()
    {
        _tool?.Dispose();
    }
}