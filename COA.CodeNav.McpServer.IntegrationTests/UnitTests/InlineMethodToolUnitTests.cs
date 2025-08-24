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
using Microsoft.Extensions.Options;
using Moq;

namespace COA.CodeNav.McpServer.IntegrationTests.UnitTests;

/// <summary>
/// Unit tests for InlineMethodTool focusing on method inlining scenarios
/// </summary>
public class InlineMethodToolUnitTests : IDisposable
{
    private readonly Mock<ILogger<InlineMethodTool>> _mockLogger;
    private readonly Mock<ILogger<RoslynWorkspaceService>> _mockWorkspaceLogger;
    private readonly Mock<ILogger<MSBuildWorkspaceManager>> _mockManagerLogger;
    private readonly Mock<ILogger<DocumentService>> _mockDocumentLogger;
    private readonly Mock<ILogger<InlineMethodResponseBuilder>> _mockResponseBuilderLogger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly InlineMethodTool _tool;
    private readonly string _tempDirectory;

    public InlineMethodToolUnitTests()
    {
        _mockLogger = new Mock<ILogger<InlineMethodTool>>();
        _mockWorkspaceLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        _mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        _mockDocumentLogger = new Mock<ILogger<DocumentService>>();
        _mockResponseBuilderLogger = new Mock<ILogger<InlineMethodResponseBuilder>>();
        
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"InlineMethodTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        
        var config = Options.Create(new WorkspaceManagerConfig());
        var workspaceManager = new MSBuildWorkspaceManager(_mockManagerLogger.Object, config);
        _workspaceService = new RoslynWorkspaceService(_mockWorkspaceLogger.Object, workspaceManager);
        _documentService = new DocumentService(_mockDocumentLogger.Object, _workspaceService);
        
        // Create dependencies from framework
        var tokenEstimator = new COA.Mcp.Framework.TokenOptimization.DefaultTokenEstimator();
        var responseBuilder = new InlineMethodResponseBuilder(_mockResponseBuilderLogger.Object, tokenEstimator);
        
        _tool = new InlineMethodTool(
            TestServiceProvider.Create(),
            _mockLogger.Object,
            _workspaceService,
            _documentService,
            tokenEstimator,
            responseBuilder,
            null);
    }

    [Fact]
    public async Task InlineMethod_WithoutWorkspace_ShouldReturnProperError()
    {
        // Arrange
        var parameters = new COA.CodeNav.McpServer.Tools.InlineMethodParams
        {
            FilePath = "C:\\nonexistent\\file.cs",
            Line = 10,
            Column = 5
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Document not found");
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("DOCUMENT_NOT_FOUND");
        result.Error.Recovery.Should().NotBeNull();
        result.Error.Recovery!.Steps.Should().Contain(step => step.Contains("Load a solution"));
    }

    [Fact]
    public async Task InlineMethod_WithSimpleMethod_ShouldInlineSuccessfully()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class Calculator
    {
        public int Add(int a, int b)
        {
            return AddImpl(a, b);
        }

        private int AddImpl(int a, int b)
        {
            return a + b;
        }

        public int Calculate()
        {
            return AddImpl(5, 3);
        }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("InlineProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.InlineMethodParams
        {
            FilePath = filePath,
            Line = 13, // Line with AddImpl method declaration
            Column = 21, // Inside "AddImpl"
            ForceInline = false
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.MethodName.Should().Be("AddImpl");
        result.InlinedCallSites.Should().Be(2); // Two calls to AddImpl
        result.MethodBody.Should().Contain("a + b");
        
        result.UpdatedCode.Should().NotBeNullOrEmpty();
        result.UpdatedCode.Should().NotContain("private int AddImpl"); // Method should be removed
        result.UpdatedCode.Should().Contain("return a + b;"); // Body should be inlined
        
        result.Query.Should().NotBeNull();
        result.Summary.Should().NotBeNull();
        result.Summary!.TotalFound.Should().Be(3); // Declaration + 2 call sites
        result.Summary.Returned.Should().Be(2); // 2 call sites inlined
        result.Meta.Should().NotBeNull();
        result.Meta!.ExecutionTime.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task InlineMethod_WithVoidMethod_ShouldInlineSuccessfully()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class Logger
    {
        public void Log(string message)
        {
            LogImpl(message);
        }

        private void LogImpl(string message)
        {
            Console.WriteLine($""[LOG] {message}"");
        }

        public void Test()
        {
            LogImpl(""test message"");
        }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("VoidMethodProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.InlineMethodParams
        {
            FilePath = filePath,
            Line = 13, // Line with LogImpl method declaration
            Column = 21, // Inside "LogImpl"
            ForceInline = false
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.MethodName.Should().Be("LogImpl");
        result.InlinedCallSites.Should().Be(2);
        
        result.UpdatedCode.Should().NotContain("private void LogImpl");
        result.UpdatedCode.Should().Contain("Console.WriteLine($\"[LOG] {message}\");");
    }

    [Fact]
    public async Task InlineMethod_WithMethodWithoutCalls_ShouldReturnError()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class Calculator
    {
        private int UnusedMethod()
        {
            return 42;
        }

        public int Add(int a, int b)
        {
            return a + b;
        }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("UnusedMethodProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.InlineMethodParams
        {
            FilePath = filePath,
            Line = 8, // Line with UnusedMethod declaration
            Column = 20, // Inside "UnusedMethod"
            ForceInline = false
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("no references found");
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("INVALID_OPERATION");
    }

    [Fact]
    public async Task InlineMethod_WithForceInline_ShouldInlineEvenWithoutCalls()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class Calculator
    {
        private int UnusedMethod()
        {
            return 42;
        }

        public int Add(int a, int b)
        {
            return a + b;
        }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("ForceInlineProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.InlineMethodParams
        {
            FilePath = filePath,
            Line = 8, // Line with UnusedMethod declaration
            Column = 20, // Inside "UnusedMethod"
            ForceInline = true // Force inlining even without references
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.MethodName.Should().Be("UnusedMethod");
        result.InlinedCallSites.Should().Be(0); // No call sites, but method should be removed
        
        result.UpdatedCode.Should().NotContain("private int UnusedMethod"); // Method should be removed
    }

    [Fact]
    public async Task InlineMethod_WithInvalidPosition_ShouldReturnError()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class Calculator
    {
        public int Add(int a, int b)
        {
            return a + b;
        }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("InvalidPositionProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.InlineMethodParams
        {
            FilePath = filePath,
            Line = 2, // Line with using statement - not on a method
            Column = 5,
            ForceInline = false
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("No method declaration found");
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("SYMBOL_NOT_FOUND");
    }

    [Fact]
    public async Task InlineMethod_WithMethodHavingParameters_ShouldReplaceParameters()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class StringHelper
    {
        public string Format(string template, string value)
        {
            return FormatImpl(template, value);
        }

        private string FormatImpl(string template, string value)
        {
            return template.Replace(""{value}"", value);
        }

        public string Test()
        {
            return FormatImpl(""Hello {value}!"", ""World"");
        }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("ParameterProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.InlineMethodParams
        {
            FilePath = filePath,
            Line = 13, // Line with FormatImpl method declaration
            Column = 24, // Inside "FormatImpl"
            ForceInline = false
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.MethodName.Should().Be("FormatImpl");
        
        result.UpdatedCode.Should().NotContain("private string FormatImpl");
        result.UpdatedCode.Should().Contain("template.Replace(\"{value}\", value)");
    }

    [Fact]
    public void InlineMethod_ShouldHaveCorrectToolCategory()
    {
        // Act & Assert
        _tool.Category.Should().Be(COA.Mcp.Framework.ToolCategory.Refactoring);
    }

    [Fact]
    public void InlineMethod_ShouldHaveCorrectToolName()
    {
        // Act & Assert
        _tool.Name.Should().Be("csharp_inline_method");
    }

    private async Task<(string projectPath, string filePath)> CreateTestProject(string projectName, string sourceCode)
    {
        var projectDir = Path.Combine(_tempDirectory, projectName);
        Directory.CreateDirectory(projectDir);

        var projectPath = Path.Combine(projectDir, $"{projectName}.csproj");
        var projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>";

        await File.WriteAllTextAsync(projectPath, projectContent);

        var filePath = Path.Combine(projectDir, "TestFile.cs");
        await File.WriteAllTextAsync(filePath, sourceCode);

        return (projectPath, filePath);
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
        catch
        {
            // Ignore cleanup errors in tests
        }
    }
}

