using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.ResponseBuilders;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Tools;
using COA.CodeNav.McpServer.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using FluentAssertions;
using COA.CodeNav.McpServer.Configuration;

namespace COA.CodeNav.McpServer.IntegrationTests.UnitTests;

/// <summary>
/// Tests specifically for token optimization in SymbolSearchTool
/// </summary>
public class SymbolSearchTokenOptimizationTests : IDisposable
{
    private readonly Mock<ILogger<SymbolSearchTool>> _mockLogger;
    private readonly Mock<ILogger<RoslynWorkspaceService>> _mockWorkspaceLogger;
    private readonly Mock<ILogger<MSBuildWorkspaceManager>> _mockManagerLogger;
    private readonly Mock<ILogger<SymbolSearchResponseBuilder>> _mockResponseBuilderLogger;
    private readonly SymbolSearchTool _tool;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly COA.Mcp.Framework.TokenOptimization.ITokenEstimator _tokenEstimator;
    private readonly string _tempDirectory;

    public SymbolSearchTokenOptimizationTests()
    {
        _mockLogger = new Mock<ILogger<SymbolSearchTool>>();
        _mockWorkspaceLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        _mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        _mockResponseBuilderLogger = new Mock<ILogger<SymbolSearchResponseBuilder>>();
        
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"TokenOptTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        
        var config = Options.Create(new WorkspaceManagerConfig());
        var workspaceManager = new MSBuildWorkspaceManager(_mockManagerLogger.Object, config);
        _workspaceService = new RoslynWorkspaceService(_mockWorkspaceLogger.Object, workspaceManager);
        _tokenEstimator = new COA.Mcp.Framework.TokenOptimization.DefaultTokenEstimator();
        
        var responseBuilder = new SymbolSearchResponseBuilder(_mockResponseBuilderLogger.Object, _tokenEstimator);
        _tool = new SymbolSearchTool(_mockLogger.Object, _workspaceService, responseBuilder, _tokenEstimator, null);
    }
    
    [Fact]
    public async Task SymbolSearch_WithLargeResultSet_ShouldTruncateBasedOnTokens()
    {
        // Arrange - Create a project with many symbols
        var projectPath = await SetupLargeProjectAsync();
        
        var parameters = new SymbolSearchParams
        {
            Query = "*", // Match everything
            SearchType = "wildcard",
            MaxResults = 500 // Max allowed by validation
        };
        
        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);
        
        // Assert
        result.Should().BeOfType<SymbolSearchToolResult>();
        var typedResult = (SymbolSearchToolResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Symbols.Should().NotBeNull();
        
        // With token optimization, should truncate to stay within limits
        // At ~150 tokens per symbol, 10000 token limit / 150 = ~66 symbols max
        typedResult.Symbols!.Count.Should().BeLessThanOrEqualTo(70, 
            "Token optimization should limit results to stay within token budget");
        
        // Should indicate truncation
        typedResult.ResultsSummary!.HasMore.Should().BeTrue();
        typedResult.Meta!.Truncated.Should().BeTrue();
        
        // Should log the truncation
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Truncating symbol results")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
    
    [Fact]
    public async Task SymbolSearch_WithSmallResultSet_ShouldNotTruncate()
    {
        // Arrange - Create a project with few symbols
        var projectPath = await SetupSmallProjectAsync();
        
        var parameters = new SymbolSearchParams
        {
            Query = "TestClass",
            SearchType = "exact",
            MaxResults = 100
        };
        
        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);
        
        // Assert
        result.Should().BeOfType<SymbolSearchToolResult>();
        var typedResult = (SymbolSearchToolResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Symbols.Should().NotBeNull();
        
        // Small result set should not be truncated by token limits
        typedResult.ResultsSummary!.HasMore.Should().BeFalse();
        typedResult.Meta!.Truncated.Should().BeFalse();
        
        // Should NOT log truncation
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Truncating symbol results")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }
    
    private async Task<string> SetupLargeProjectAsync()
    {
        var projectPath = Path.Combine(_tempDirectory, "LargeProject.csproj");
        var solutionPath = Path.Combine(_tempDirectory, "LargeProject.sln");
        
        // Create project file
        await File.WriteAllTextAsync(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Create solution file
        await File.WriteAllTextAsync(solutionPath, $@"Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""LargeProject"", ""LargeProject.csproj"", ""{{12345678-1234-1234-1234-123456789012}}""
EndProject");

        // Generate many classes to exceed token limits
        for (int i = 0; i < 200; i++)
        {
            var className = $"GeneratedClass{i:D3}";
            var filePath = Path.Combine(_tempDirectory, $"{className}.cs");
            await File.WriteAllTextAsync(filePath, $@"
namespace TestNamespace
{{
    public class {className}
    {{
        public int Property{i} {{ get; set; }}
        public void Method{i}() {{ }}
        public string Field{i};
        
        public interface IInterface{i} {{ }}
        public enum Enum{i} {{ Value1, Value2 }}
        
        private class NestedClass{i} {{ }}
    }}
}}");
        }
        
        await _workspaceService.LoadSolutionAsync(solutionPath);
        await Task.Delay(500); // Give time for workspace to load
        
        return projectPath;
    }
    
    private async Task<string> SetupSmallProjectAsync()
    {
        var projectPath = Path.Combine(_tempDirectory, "SmallProject.csproj");
        var solutionPath = Path.Combine(_tempDirectory, "SmallProject.sln");
        
        // Create project file
        await File.WriteAllTextAsync(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Create solution file
        await File.WriteAllTextAsync(solutionPath, $@"Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""SmallProject"", ""SmallProject.csproj"", ""{{12345678-1234-1234-1234-123456789012}}""
EndProject");

        // Create just one test file
        var testFile = Path.Combine(_tempDirectory, "TestClass.cs");
        await File.WriteAllTextAsync(testFile, @"
namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod() { }
    }
}");
        
        await _workspaceService.LoadSolutionAsync(solutionPath);
        await Task.Delay(500); // Give time for workspace to load
        
        return projectPath;
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
        catch { /* Cleanup errors are non-critical */ }
    }
}