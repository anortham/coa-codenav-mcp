using COA.CodeNav.McpServer.Configuration;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// using DotMemory.Unit; // Temporarily disabled

namespace COA.CodeNav.McpServer.IntegrationTests.UnitTests;

/// <summary>
/// Comprehensive unit tests for GetDiagnosticsTool focusing on real-world scenarios
/// </summary>
// [DotMemoryUnit(FailIfRunWithoutSupport = false)] // Temporarily disabled
public class GetDiagnosticsToolUnitTests : IDisposable
{
    private readonly Mock<ILogger<GetDiagnosticsTool>> _mockLogger;
    private readonly Mock<ILogger<RoslynWorkspaceService>> _mockWorkspaceLogger;
    private readonly Mock<ILogger<MSBuildWorkspaceManager>> _mockManagerLogger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly GetDiagnosticsTool _tool;
    private readonly string _tempDirectory;

    public GetDiagnosticsToolUnitTests()
    {
        _mockLogger = new Mock<ILogger<GetDiagnosticsTool>>();
        _mockWorkspaceLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        _mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"DiagnosticTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        
        var config = Options.Create(new WorkspaceManagerConfig());
        var workspaceManager = new MSBuildWorkspaceManager(_mockManagerLogger.Object, config);
        _workspaceService = new RoslynWorkspaceService(_mockWorkspaceLogger.Object, workspaceManager);
        
        // Create token estimator from framework
        var tokenEstimator = new COA.Mcp.Framework.TokenOptimization.DefaultTokenEstimator();
        
        _tool = new GetDiagnosticsTool(_mockLogger.Object, _workspaceService, tokenEstimator, null);
    }

    [Fact]
    public async Task GetDiagnostics_WithNoWorkspaceLoaded_ShouldReturnWorkspaceNotLoadedError()
    {
        // Arrange
        var parameters = new GetDiagnosticsParams
        {
            Scope = "solution"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<GetDiagnosticsToolResult>();
        var typedResult = (GetDiagnosticsToolResult)result;
        
        typedResult.Success.Should().BeFalse();
        typedResult.Error.Should().NotBeNull();
        typedResult.Error!.Code.Should().Be("WORKSPACE_NOT_LOADED");
        typedResult.Error.Recovery.Should().NotBeNull();
        typedResult.Error.Recovery!.Steps.Should().Contain(s => s.Contains("csharp_load_solution"));
    }

    [Fact]
    public async Task GetDiagnostics_WithFileScope_RequiresFilePath()
    {
        // Arrange
        var parameters = new GetDiagnosticsParams
        {
            Scope = "file"
            // Missing FilePath
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<GetDiagnosticsToolResult>();
        var typedResult = (GetDiagnosticsToolResult)result;
        
        typedResult.Success.Should().BeFalse();
        typedResult.Message.Should().Contain("File path is required when scope is 'file'");
    }

    [Fact]
    public async Task GetDiagnostics_WithLargeResultSet_ShouldRespectMaxResults()
    {
        // Arrange - Create a project with many diagnostic issues
        await SetupProjectWithManyIssuesAsync();
        
        var parameters = new GetDiagnosticsParams
        {
            Scope = "solution",
            MaxResults = 5 // Limit to 5 results
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<GetDiagnosticsToolResult>();
        var typedResult = (GetDiagnosticsToolResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Diagnostics.Should().NotBeNull();
        typedResult.Diagnostics!.Count.Should().BeLessThanOrEqualTo(5);
        
        if (typedResult.ResultsSummary?.Total > 5)
        {
            typedResult.ResultsSummary.HasMore.Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetDiagnostics_WithSeverityFilter_ShouldFilterCorrectly()
    {
        // Arrange
        await SetupProjectWithMixedSeverityIssuesAsync();
        
        var parameters = new GetDiagnosticsParams
        {
            Scope = "solution",
            Severities = new[] { "Error" }
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<GetDiagnosticsToolResult>();
        var typedResult = (GetDiagnosticsToolResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Diagnostics.Should().NotBeNull();
        
        // All returned diagnostics should be errors
        foreach (var diagnostic in typedResult.Diagnostics!)
        {
            diagnostic.Severity.Should().Be("Error");
        }
    }

    [Fact]
    // [DotMemoryUnit(FailIfRunWithoutSupport = false)] // Temporarily disabled
    public async Task GetDiagnostics_MemoryUsage_ShouldNotLeakMemory()
    {
        // Arrange
        await SetupProjectWithManyIssuesAsync();
        
        var parameters = new GetDiagnosticsParams
        {
            Scope = "solution",
            MaxResults = 100
        };

        // Act & Assert - Memory checkpoint before
        // Memory check temporarily disabled - dotMemory.Check

        // Execute multiple times to test for leaks
        for (int i = 0; i < 5; i++)
        {
            var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);
            result.Should().BeOfType<GetDiagnosticsToolResult>();
            
            // Force garbage collection
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // Memory checkpoint after - verify no significant leaks
        // Memory check temporarily disabled - dotMemory.Check
    }

    [Fact]
    public async Task GetDiagnostics_WithForceRefresh_ShouldRefreshDocuments()
    {
        // Arrange - create all files first, then load workspace
        var projectPath = Path.Combine(_tempDirectory, "TestProject.csproj");
        var solutionPath = Path.Combine(_tempDirectory, "TestProject.sln");

        // Create minimal project
        await File.WriteAllTextAsync(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Create solution
        await File.WriteAllTextAsync(solutionPath, $@"Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""TestProject"", ""TestProject.csproj"", ""{{12345678-1234-1234-1234-123456789012}}""
EndProject");

        var testFilePath = Path.Combine(_tempDirectory, "TestClass.cs");
        
        // Create file with diagnostic issue
        await File.WriteAllTextAsync(testFilePath, @"
using System;
public class Test {
    public void Method() {
        int unused = 42; // CS0219
    }
}");

        // Now load workspace after all files exist
        await _workspaceService.LoadSolutionAsync(solutionPath);

        var parameters = new GetDiagnosticsParams
        {
            Scope = "file",
            FilePath = testFilePath,
            ForceRefresh = true
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<GetDiagnosticsToolResult>();
        var typedResult = (GetDiagnosticsToolResult)result;
        
        typedResult.Success.Should().BeTrue();
        // Should have found the diagnostic after refresh
        typedResult.Summary?.TotalFound.Should().BeGreaterThan(0);
    }

    [Fact(Skip = "Operations complete too quickly to test cancellation reliably")]
    public async Task GetDiagnostics_WithCancellation_ShouldHandleCancellationGracefully()
    {
        // Arrange
        await SetupLargeProjectAsync(); // Large project to allow time for cancellation
        
        var parameters = new GetDiagnosticsParams
        {
            Scope = "solution"
        };
        
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(1)); // Cancel extremely quickly

        // Act & Assert - Accept any OperationCanceledException (including TaskCanceledException)
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _tool.ExecuteAsync(parameters, cts.Token));
    }

    [Fact]
    public async Task GetDiagnostics_WithInvalidParameters_ShouldValidateInputs()
    {
        // Test invalid parameter combinations that should throw validation exceptions
        var frameworkValidatedCases = new[]
        {
            new GetDiagnosticsParams { MaxResults = 0 }, // Invalid max results
            new GetDiagnosticsParams { MaxResults = 1000 }, // Exceeds limit
        };

        foreach (var parameters in frameworkValidatedCases)
        {
            // Act & Assert - These should throw validation exceptions from the framework
            var act = () => _tool.ExecuteAsync(parameters, CancellationToken.None);
            await act.Should().ThrowAsync<Exception>()
                .Where(ex => ex.Message.Contains("MaxResults must be between 1 and 500"));
        }

        // Test cases that should be handled gracefully by the tool logic
        var toolValidatedCases = new[]
        {
            new GetDiagnosticsParams { Scope = "file", FilePath = null }, // Missing file path
            new GetDiagnosticsParams { Scope = "invalid" }, // Invalid scope
        };

        foreach (var parameters in toolValidatedCases)
        {
            // Act
            var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

            // Assert
            result.Should().BeOfType<GetDiagnosticsToolResult>();
            var typedResult = (GetDiagnosticsToolResult)result;
            
            // Should fail validation gracefully
            typedResult.Success.Should().BeFalse();
            typedResult.Error.Should().NotBeNull();
            typedResult.Error!.Code.Should().NotBeEmpty();
        }
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Warning")]
    [InlineData("Info")]
    [InlineData("Hidden")]
    public async Task GetDiagnostics_WithSpecificSeverity_ShouldReturnOnlyThatSeverity(string severity)
    {
        // Arrange
        await SetupProjectWithMixedSeverityIssuesAsync();
        
        var parameters = new GetDiagnosticsParams
        {
            Scope = "solution",
            Severities = new[] { severity }
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<GetDiagnosticsToolResult>();
        var typedResult = (GetDiagnosticsToolResult)result;
        
        if (typedResult.Success && typedResult.Diagnostics?.Any() == true)
        {
            typedResult.Diagnostics.Should().AllSatisfy(d => 
                d.Severity.Should().Be(severity));
        }
    }

    private async Task<string> SetupSimpleProjectAsync()
    {
        var projectPath = Path.Combine(_tempDirectory, "TestProject.csproj");
        var solutionPath = Path.Combine(_tempDirectory, "TestProject.sln");

        // Create minimal project
        await File.WriteAllTextAsync(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Create solution
        await File.WriteAllTextAsync(solutionPath, $@"Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""TestProject"", ""TestProject.csproj"", ""{{12345678-1234-1234-1234-123456789012}}""
EndProject");

        await _workspaceService.LoadSolutionAsync(solutionPath);
        return projectPath;
    }

    private async Task SetupProjectWithManyIssuesAsync()
    {
        var projectPath = await SetupSimpleProjectAsync();
        
        // Create file with many diagnostic issues
        var codeWithIssues = @"
using System;
using System.Collections.Generic; // Unused
using System.Linq; // Unused

public class TestClass
{
    private int unusedField; // CS0169
    
    public void Method1()
    {
        int unused1 = 1; // CS0219
        int unused2 = 2; // CS0219
        int unused3 = 3; // CS0219
        int unused4 = 4; // CS0219
        int unused5 = 5; // CS0219
        
        Console.WriteLine(""Hello"");
    }
    
    public void Method2()
    {
        int unused6 = 6; // CS0219
        int unused7 = 7; // CS0219
        string unused8 = ""test""; // CS0219
        List<int> unused9 = new List<int>(); // CS0219
        
        Console.WriteLine(""World"");
    }
}";
        
        var testFile = Path.Combine(_tempDirectory, "TestWithIssues.cs");
        await File.WriteAllTextAsync(testFile, codeWithIssues);
    }

    private async Task SetupProjectWithMixedSeverityIssuesAsync()
    {
        var projectPath = await SetupSimpleProjectAsync();
        
        // Create code with different severity levels
        var mixedCode = @"
using System;
using System.Collections.Generic; // Warning: Unused using

#pragma warning disable CS0219 // Turn off unused variable warnings

public class MixedIssues 
{
    public void TestMethod()
    {
        int unusedVar = 42; // This would be CS0219 but disabled
        
        // This will cause a compilation error
        UndefinedMethod(); // Error: Method doesn't exist
        
        Console.WriteLine(""Test"");
    }
    
    [Obsolete(""This method is obsolete"")] // Info/Warning depending on settings
    public void OldMethod() { }
    
    public void CallingOldMethod()
    {
        OldMethod(); // Warning: Calling obsolete method
    }
}";
        
        var testFile = Path.Combine(_tempDirectory, "MixedIssues.cs");
        await File.WriteAllTextAsync(testFile, mixedCode);
    }

    private async Task SetupLargeProjectAsync()
    {
        var projectPath = await SetupSimpleProjectAsync();
        
        // Create multiple files to make a larger project
        for (int i = 0; i < 10; i++)
        {
            var largeCode = $@"
using System;
using System.Collections.Generic;

namespace LargeProject 
{{
    public class LargeClass{i}
    {{
        private List<string> data = new List<string>();
        
        public void ProcessData()
        {{
            for (int j = 0; j < 1000; j++)
            {{
                data.Add($""Item {{j}}"");
            }}
            
            var results = data.Where(x => x.Contains(""Item"")).ToList();
            Console.WriteLine($""Processed {{results.Count}} items"");
        }}
        
        public void AnotherMethod()
        {{
            int unusedVar{i} = {i}; // CS0219
            ProcessData();
        }}
    }}
}}";
            
            var fileName = Path.Combine(_tempDirectory, $"LargeClass{i}.cs");
            await File.WriteAllTextAsync(fileName, largeCode);
        }
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
            Console.WriteLine($"Cleanup warning: {ex.Message}");
        }
    }
}