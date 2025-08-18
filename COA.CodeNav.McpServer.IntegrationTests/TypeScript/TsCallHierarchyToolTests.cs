using System;
using System.IO;
using System.Threading.Tasks;
using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services.TypeScript;
using COA.CodeNav.McpServer.Tools.TypeScript;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using FluentAssertions;

namespace COA.CodeNav.McpServer.IntegrationTests.TypeScript;

public class TsCallHierarchyToolTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<TsCallHierarchyTool> _logger;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly TypeScriptCompilerManager _compilerManager;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly TsCallHierarchyTool _tool;
    private readonly string _testProjectPath;

    public TsCallHierarchyToolTests(ITestOutputHelper output)
    {
        _output = output;
        
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new XunitLoggerProvider(output));
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        
        _logger = loggerFactory.CreateLogger<TsCallHierarchyTool>();
        _compilerManager = new TypeScriptCompilerManager(loggerFactory.CreateLogger<TypeScriptCompilerManager>());
        _workspaceService = new TypeScriptWorkspaceService(loggerFactory.CreateLogger<TypeScriptWorkspaceService>(), _compilerManager);
        _tokenEstimator = new DefaultTokenEstimator();
        _tool = new TsCallHierarchyTool(_logger, _workspaceService, _compilerManager, _tokenEstimator);
        
        _testProjectPath = @"C:\source\COA CodeNav MCP\ts-test-project";
    }

    [Fact(Skip = "TSP call hierarchy requires proper tsserver setup in CI")]
    public async Task CallHierarchy_ShouldFindIncomingAndOutgoingCalls()
    {
        // Arrange - Load the TypeScript project
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        var loadResult = await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        loadResult.Success.Should().BeTrue("Project should load successfully");
        
        var testFile = Path.Combine(_testProjectPath, "src", "index.ts");
        _output.WriteLine($"Test file: {testFile}");
        
        // Act - Get call hierarchy for UserService.getUser method
        // Target the method definition at the expected line/character
        var parameters = new TsCallHierarchyParams
        {
            FilePath = testFile,
            Line = 10,      // Method definition line (0-indexed)
            Character = 8,  // Position within the method name
            MaxDepth = 3
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        _output.WriteLine($"Success: {result.Success}");
        _output.WriteLine($"Message: {result.Message}");
        
        if (result.Root != null)
        {
            _output.WriteLine($"Root symbol: {result.Root.Name} in {result.Root.File}");
        }
        
        if (result.IncomingCalls != null)
        {
            _output.WriteLine($"Incoming calls count: {result.IncomingCalls.Count}");
            foreach (var call in result.IncomingCalls)
            {
                _output.WriteLine($"  From: {call.From?.Name} in {call.From?.File}");
            }
        }
        
        if (result.OutgoingCalls != null)
        {
            _output.WriteLine($"Outgoing calls count: {result.OutgoingCalls.Count}");
            foreach (var call in result.OutgoingCalls)
            {
                _output.WriteLine($"  To: {call.To?.Name} in {call.To?.File}");
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
        
        result.Success.Should().BeTrue("Should successfully analyze call hierarchy");
        result.Root.Should().NotBeNull("Should identify the root symbol");
        result.Query.Should().NotBeNull("Should include query information");
        result.Summary.Should().NotBeNull("Should include summary information");
    }

    [Fact(Skip = "TSP call hierarchy requires proper tsserver setup in CI")]
    public async Task CallHierarchy_WithMaxDepthLimit_ShouldRespectDepthConstraint()
    {
        // Arrange
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        var loadResult = await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        loadResult.Success.Should().BeTrue();
        
        var testFile = Path.Combine(_testProjectPath, "src", "index.ts");
        
        // Act - Test with limited depth
        var parameters = new TsCallHierarchyParams
        {
            FilePath = testFile,
            Line = 10,
            Character = 8,
            MaxDepth = 1  // Very limited depth
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        result.Success.Should().BeTrue("Should handle depth limits gracefully");
        
        if (result.CallTree != null)
        {
            _output.WriteLine($"Call tree depth: {result.CallTree.Depth}");
            result.CallTree.Depth.Should().BeLessThanOrEqualTo(parameters.MaxDepth, 
                "Call tree should respect max depth constraint");
        }
    }

    [Fact(Skip = "TSP call hierarchy requires proper tsserver setup in CI")]
    public async Task CallHierarchy_ForUnusedFunction_ShouldIndicateNoUsage()
    {
        // Arrange
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        var loadResult = await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        loadResult.Success.Should().BeTrue();
        
        var testFile = Path.Combine(_testProjectPath, "src", "index.ts");
        
        // Act - Target a potentially unused function
        var parameters = new TsCallHierarchyParams
        {
            FilePath = testFile,
            Line = 35,     // Line with an unused function (if it exists)
            Character = 10,
            MaxDepth = 5
        };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        _output.WriteLine($"Result: {result.Success}, Message: {result.Message}");
        
        if (result.Success)
        {
            var totalCalls = (result.IncomingCalls?.Count ?? 0) + (result.OutgoingCalls?.Count ?? 0);
            _output.WriteLine($"Total calls found: {totalCalls}");
            
            if (totalCalls == 0 && result.Insights != null)
            {
                result.Insights.Should().Contain(insight => 
                    insight.Contains("unused") || insight.Contains("no calls"),
                    "Should provide insight about unused symbols");
            }
        }
    }

    [Fact(Skip = "TSP call hierarchy requires proper tsserver setup in CI")]
    public async Task CallHierarchy_ShouldProvideHelpfulInsights()
    {
        // Arrange
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        var loadResult = await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        loadResult.Success.Should().BeTrue();
        
        var testFile = Path.Combine(_testProjectPath, "src", "index.ts");
        
        // Act
        var parameters = new TsCallHierarchyParams
        {
            FilePath = testFile,
            Line = 10,
            Character = 8,
            MaxDepth = 3,
                    };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        if (result.Success)
        {
            result.Insights.Should().NotBeNull("Should provide insights");
            result.Insights!.Count.Should().BeGreaterThan(0, "Should have at least one insight");
            
            result.Actions.Should().NotBeNull("Should provide suggested actions");
            result.Actions!.Count.Should().BeGreaterThan(0, "Should have at least one action");
            
            _output.WriteLine("Generated insights:");
            foreach (var insight in result.Insights)
            {
                _output.WriteLine($"  - {insight}");
            }
            
            _output.WriteLine("Generated actions:");
            foreach (var action in result.Actions)
            {
                _output.WriteLine($"  - {action.Action}: {action.Description}");
            }
        }
    }

    [Fact(Skip = "TSP call hierarchy requires proper tsserver setup in CI")]
    public async Task CallHierarchy_WithInvalidPosition_ShouldHandleGracefully()
    {
        // Arrange
        var tsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
        var loadResult = await _workspaceService.LoadTsConfigAsync(tsConfigPath, "test-workspace");
        loadResult.Success.Should().BeTrue();
        
        var testFile = Path.Combine(_testProjectPath, "src", "index.ts");
        
        // Act - Use an invalid position (empty line or comment)
        var parameters = new TsCallHierarchyParams
        {
            FilePath = testFile,
            Line = 1,      // Likely an import or empty line
            Character = 0,
            MaxDepth = 3,
                    };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        _output.WriteLine($"Result for invalid position: Success={result.Success}, Message={result.Message}");
        
        if (!result.Success)
        {
            result.Error.Should().NotBeNull("Should provide error information");
            result.Message.Should().NotBeNullOrEmpty("Should provide meaningful error message");
        }
        else
        {
            // If it succeeds, it should handle the case gracefully
            var totalCalls = (result.IncomingCalls?.Count ?? 0) + (result.OutgoingCalls?.Count ?? 0);
            _output.WriteLine($"Calls found at questionable position: {totalCalls}");
        }
    }

    [Fact]
    public async Task CallHierarchy_WithoutLoadedProject_ShouldReturnError()
    {
        // Arrange - Don't load any project
        var testFile = Path.Combine(_testProjectPath, "src", "index.ts");
        
        // Act
        var parameters = new TsCallHierarchyParams
        {
            FilePath = testFile,
            Line = 10,
            Character = 8,
            MaxDepth = 3,
                    };
        
        var result = await _tool.ExecuteAsync(parameters);
        
        // Assert
        result.Success.Should().BeFalse("Should fail without loaded project");
        result.Error.Should().NotBeNull("Should provide error information");
        result.Error!.Code.Should().NotBeNullOrEmpty("Should provide error code");
        result.Error.Recovery.Should().NotBeNull("Should provide recovery information");
        
        _output.WriteLine($"Expected error: {result.Message}");
        _output.WriteLine($"Error code: {result.Error.Code}");
    }

    public void Dispose()
    {
        _tool?.Dispose();
    }
}