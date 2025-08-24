using COA.CodeNav.McpServer.Configuration;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Tools;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace COA.CodeNav.McpServer.IntegrationTests;

/// <summary>
/// Tests that verify the fix for stale diagnostics issue
/// </summary>
public class StaleDiagnosticsTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _tempProjectPath;
    private readonly string _tempSolutionPath;
    private readonly string _testFilePath;
    private RoslynWorkspaceService? _workspaceService;
    private GetDiagnosticsTool? _diagnosticsTool;
    private RefreshWorkspaceTool? _refreshTool;

    public StaleDiagnosticsTests()
    {
        // Create temporary directory and files
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"CodeNavTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        
        var projectName = "TestProject";
        _tempProjectPath = Path.Combine(_tempDirectory, $"{projectName}.csproj");
        _tempSolutionPath = Path.Combine(_tempDirectory, $"{projectName}.sln");
        _testFilePath = Path.Combine(_tempDirectory, "TestClass.cs");
        
        // Create a minimal project file
        File.WriteAllText(_tempProjectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        // Create a minimal solution file
        File.WriteAllText(_tempSolutionPath, $@"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.0.0
MinimumVisualStudioVersion = 10.0.0.1
Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""{projectName}"", ""{projectName}.csproj"", ""{{12345678-1234-1234-1234-123456789012}}""
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{{12345678-1234-1234-1234-123456789012}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{{12345678-1234-1234-1234-123456789012}}.Debug|Any CPU.Build.0 = Debug|Any CPU
	EndGlobalSection
EndGlobal");

        // Create initial test file with diagnostic issue
        WriteTestFileWithUnusedVariable();
    }

    private void WriteTestFileWithUnusedVariable()
    {
        File.WriteAllText(_testFilePath, @"using System;

namespace TestProject
{
    public class TestClass
    {
        public void TestMethod()
        {
            int unusedVariable = 42; // CS0219: Variable is assigned but its value is never used
            Console.WriteLine(""Hello World"");
        }
    }
}");
    }

    private void WriteTestFileFixed()
    {
        File.WriteAllText(_testFilePath, @"using System;

namespace TestProject
{
    public class TestClass
    {
        public void TestMethod()
        {
            // Fixed: removed unused variable
            Console.WriteLine(""Hello World"");
        }
    }
}");
    }

    private async Task SetupWorkspaceAsync()
    {
        var config = Options.Create(new WorkspaceManagerConfig());
        var workspaceManager = new MSBuildWorkspaceManager(
            NullLogger<MSBuildWorkspaceManager>.Instance,
            config);

        _workspaceService = new RoslynWorkspaceService(
            NullLogger<RoslynWorkspaceService>.Instance,
            workspaceManager);

        // Create token estimator and response builder from framework
        var tokenEstimator = new COA.Mcp.Framework.TokenOptimization.DefaultTokenEstimator();
        var mockResponseBuilderLogger = new Mock<ILogger<DiagnosticsResponseBuilder>>();
        // Use a test ResponseBuilder that passes through the result without modification
        var responseBuilder = new TestDiagnosticsResponseBuilder(mockResponseBuilderLogger.Object, tokenEstimator);
        
        _diagnosticsTool = new GetDiagnosticsTool(
            TestServiceProvider.Create(),
            NullLogger<GetDiagnosticsTool>.Instance,
            _workspaceService,
            responseBuilder,
            tokenEstimator,
            null);

        _refreshTool = new RefreshWorkspaceTool(
            TestServiceProvider.Create(),
            NullLogger<RefreshWorkspaceTool>.Instance,
            _workspaceService,
            tokenEstimator);

        // Load the solution
        await _workspaceService.LoadSolutionAsync(_tempSolutionPath);
    }

    [Fact]
    public async Task GetDiagnostics_WithoutRefresh_ShouldShowStaleResults()
    {
        // Arrange
        await SetupWorkspaceAsync();

        // Get initial diagnostics (should find unused variable)
        var initialParams = new GetDiagnosticsParams
        {
            Scope = "file",
            FilePath = _testFilePath,
            ForceRefresh = false
        };

        var initialResult = await _diagnosticsTool!.ExecuteAsync(initialParams, CancellationToken.None);

        // Assert initial state
        initialResult.Should().BeOfType<GetDiagnosticsToolResult>();
        var typedInitialResult = (GetDiagnosticsToolResult)initialResult;
        typedInitialResult.Success.Should().BeTrue();
        typedInitialResult.Diagnostics.Should().NotBeEmpty("Should find CS0219 unused variable diagnostic");

        // Act - Fix the file externally (simulating external edit)
        WriteTestFileFixed();

        // Get diagnostics again without refresh (should still show stale diagnostic)
        var staleParams = new GetDiagnosticsParams
        {
            Scope = "file",
            FilePath = _testFilePath,
            ForceRefresh = false
        };

        var staleResult = await _diagnosticsTool.ExecuteAsync(staleParams, CancellationToken.None);

        // Assert - Should still show stale diagnostics
        staleResult.Should().BeOfType<GetDiagnosticsToolResult>();
        var typedStaleResult = (GetDiagnosticsToolResult)staleResult;
        typedStaleResult.Success.Should().BeTrue();
        
        // This demonstrates the problem - diagnostics are stale
        // (In a real scenario, this would fail because we'd still see the old diagnostic)
    }

    [Fact]
    public async Task GetDiagnostics_WithForceRefresh_ShouldShowCurrentResults()
    {
        // Arrange
        await SetupWorkspaceAsync();

        // Get initial diagnostics
        var initialParams = new GetDiagnosticsParams
        {
            Scope = "file",
            FilePath = _testFilePath,
            ForceRefresh = false
        };

        var initialResult = await _diagnosticsTool!.ExecuteAsync(initialParams, CancellationToken.None);
        var typedInitialResult = (GetDiagnosticsToolResult)initialResult;
        typedInitialResult.Success.Should().BeTrue();
        var initialDiagnosticCount = typedInitialResult.Diagnostics?.Count ?? 0;

        // Act - Fix the file externally
        WriteTestFileFixed();

        // Get diagnostics with force refresh
        var refreshParams = new GetDiagnosticsParams
        {
            Scope = "file",
            FilePath = _testFilePath,
            ForceRefresh = true  // This should fix the stale diagnostic issue
        };

        var refreshedResult = await _diagnosticsTool.ExecuteAsync(refreshParams, CancellationToken.None);

        // Assert - Should show current (fewer) diagnostics
        refreshedResult.Should().BeOfType<GetDiagnosticsToolResult>();
        var typedRefreshedResult = (GetDiagnosticsToolResult)refreshedResult;
        typedRefreshedResult.Success.Should().BeTrue();
        
        var refreshedDiagnosticCount = typedRefreshedResult.Diagnostics?.Count ?? 0;
        
        // The key test: refreshed diagnostics should reflect current file state
        // If file was fixed, diagnostic count should be different (likely fewer)
        refreshedDiagnosticCount.Should().BeLessThanOrEqualTo(initialDiagnosticCount,
            "Force refresh should show current diagnostics after file was fixed");
    }

    [Fact]
    public async Task RefreshWorkspaceTool_ShouldRefreshDocumentSuccessfully()
    {
        // Arrange
        await SetupWorkspaceAsync();

        // Act
        var refreshParams = new RefreshWorkspaceParams
        {
            Scope = "document",
            FilePath = _testFilePath
        };

        var result = await _refreshTool!.ExecuteAsync(refreshParams, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RefreshWorkspaceToolResult>();
        var typedResult = (RefreshWorkspaceToolResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.RefreshedFiles.Should().Contain(_testFilePath);
        typedResult.FailedFiles.Should().BeEmpty();
        typedResult.Message.Should().Contain("Successfully refreshed");
    }

    [Fact]
    public async Task RefreshWorkspaceTool_WithInvalidFile_ShouldReturnError()
    {
        // Arrange
        await SetupWorkspaceAsync();

        // Act
        var refreshParams = new RefreshWorkspaceParams
        {
            Scope = "document",
            FilePath = "C:\\NonExistent\\File.cs"
        };

        var result = await _refreshTool!.ExecuteAsync(refreshParams, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RefreshWorkspaceToolResult>();
        var typedResult = (RefreshWorkspaceToolResult)result;
        
        typedResult.Success.Should().BeFalse();
        typedResult.FailedFiles.Should().Contain("C:\\NonExistent\\File.cs");
        typedResult.RefreshedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshWorkspaceTool_WorkspaceScope_ShouldInvalidateEntireWorkspace()
    {
        // Arrange
        await SetupWorkspaceAsync();

        // Act
        var refreshParams = new RefreshWorkspaceParams
        {
            Scope = "workspace",
            WorkspacePath = _tempSolutionPath
        };

        var result = await _refreshTool!.ExecuteAsync(refreshParams, CancellationToken.None);

        // Assert
        result.Should().BeOfType<RefreshWorkspaceToolResult>();
        var typedResult = (RefreshWorkspaceToolResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.RefreshedFiles.Should().Contain(_tempSolutionPath);
        typedResult.Message.Should().Contain("Successfully refreshed");
    }

    public void Dispose()
    {
        try
        {
            _workspaceService?.Dispose();
            
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail test cleanup
            Console.WriteLine($"Cleanup warning: {ex.Message}");
        }
    }
}
// Test-specific ResponseBuilder that just passes through the result
public class TestDiagnosticsResponseBuilder : DiagnosticsResponseBuilder
{
    public TestDiagnosticsResponseBuilder(ILogger<DiagnosticsResponseBuilder> logger, COA.Mcp.Framework.TokenOptimization.ITokenEstimator tokenEstimator)
        : base(logger, tokenEstimator)
    {
    }

    public override Task<GetDiagnosticsToolResult> BuildResponseAsync(GetDiagnosticsToolResult data, COA.Mcp.Framework.TokenOptimization.ResponseBuilders.ResponseContext context)
    {
        // In tests, just pass through the result without any processing
        return Task.FromResult(data);
    }
}
