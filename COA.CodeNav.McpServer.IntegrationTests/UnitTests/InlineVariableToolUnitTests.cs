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
/// Unit tests for InlineVariableTool focusing on variable inlining scenarios
/// </summary>
public class InlineVariableToolUnitTests : IDisposable
{
    private readonly Mock<ILogger<InlineVariableTool>> _mockLogger;
    private readonly Mock<ILogger<RoslynWorkspaceService>> _mockWorkspaceLogger;
    private readonly Mock<ILogger<MSBuildWorkspaceManager>> _mockManagerLogger;
    private readonly Mock<ILogger<DocumentService>> _mockDocumentLogger;
    private readonly Mock<ILogger<InlineVariableResponseBuilder>> _mockResponseBuilderLogger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly InlineVariableTool _tool;
    private readonly string _tempDirectory;

    public InlineVariableToolUnitTests()
    {
        _mockLogger = new Mock<ILogger<InlineVariableTool>>();
        _mockWorkspaceLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        _mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        _mockDocumentLogger = new Mock<ILogger<DocumentService>>();
        _mockResponseBuilderLogger = new Mock<ILogger<InlineVariableResponseBuilder>>();
        
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"InlineVariableTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        
        var config = Options.Create(new WorkspaceManagerConfig());
        var workspaceManager = new MSBuildWorkspaceManager(_mockManagerLogger.Object, config);
        _workspaceService = new RoslynWorkspaceService(_mockWorkspaceLogger.Object, workspaceManager);
        _documentService = new DocumentService(_mockDocumentLogger.Object, _workspaceService);
        
        // Create dependencies from framework
        var tokenEstimator = new COA.Mcp.Framework.TokenOptimization.DefaultTokenEstimator();
        var responseBuilder = new InlineVariableResponseBuilder(_mockResponseBuilderLogger.Object, tokenEstimator);
        
        _tool = new InlineVariableTool(
            _mockLogger.Object,
            _workspaceService,
            _documentService,
            tokenEstimator,
            responseBuilder,
            null);
    }

    [Fact]
    public async Task InlineVariable_WithoutWorkspace_ShouldReturnProperError()
    {
        // Arrange
        var parameters = new COA.CodeNav.McpServer.Tools.InlineVariableParams
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
    public async Task InlineVariable_WithSimpleVariable_ShouldInlineSuccessfully()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class Calculator
    {
        public int Calculate()
        {
            int multiplier = 5;
            int value = 10;
            int result = value * multiplier;
            Console.WriteLine(multiplier);
            return result + multiplier;
        }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("InlineVariableProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.InlineVariableParams
        {
            FilePath = filePath,
            Line = 10, // Line with "int multiplier = 5;"
            Column = 17, // Inside "multiplier"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.VariableName.Should().Be("multiplier");
        result.InlinedUsages.Should().Be(3); // Three usages of multiplier
        result.InitializationValue.Should().Be("5");
        
        result.UpdatedCode.Should().NotBeNullOrEmpty();
        result.UpdatedCode.Should().NotContain("int multiplier = 5;"); // Declaration should be removed
        result.UpdatedCode.Should().Contain("int result = value * 5;"); // First usage inlined
        result.UpdatedCode.Should().Contain("Console.WriteLine(5);"); // Second usage inlined
        result.UpdatedCode.Should().Contain("return result + 5;"); // Third usage inlined
        
        result.Query.Should().NotBeNull();
        result.Summary.Should().NotBeNull();
        result.Summary!.TotalFound.Should().Be(4); // Declaration + 3 usages
        result.Summary.Returned.Should().Be(3); // 3 usages inlined
        result.Meta.Should().NotBeNull();
        result.Meta!.ExecutionTime.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task InlineVariable_WithStringVariable_ShouldInlineSuccessfully()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class StringProcessor
    {
        public string Process()
        {
            string greeting = ""Hello"";
            string message = greeting + "" World"";
            Console.WriteLine(greeting);
            return message + "" - "" + greeting;
        }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("StringVariableProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.InlineVariableParams
        {
            FilePath = filePath,
            Line = 10, // Line with "string greeting = "Hello";"
            Column = 20, // Inside "greeting"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.VariableName.Should().Be("greeting");
        result.InitializationValue.Should().Be("\"Hello\"");
        
        result.UpdatedCode.Should().NotContain("string greeting = \"Hello\";");
        result.UpdatedCode.Should().Contain("string message = \"Hello\" + \" World\";");
        result.UpdatedCode.Should().Contain("Console.WriteLine(\"Hello\");");
        result.UpdatedCode.Should().Contain("return message + \" - \" + \"Hello\";");
    }

    [Fact]
    public async Task InlineVariable_WithComplexExpression_ShouldInlineSuccessfully()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class Calculator
    {
        public int Calculate()
        {
            int baseValue = (10 + 5) * 2;
            int result = baseValue + 1;
            return baseValue * 3;
        }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("ComplexExpressionProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.InlineVariableParams
        {
            FilePath = filePath,
            Line = 10, // Line with complex expression
            Column = 17, // Inside "baseValue"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.VariableName.Should().Be("baseValue");
        result.InitializationValue.Should().Be("(10 + 5) * 2");
        
        result.UpdatedCode.Should().NotContain("int baseValue = (10 + 5) * 2;");
        result.UpdatedCode.Should().Contain("int result = (10 + 5) * 2 + 1;");
        result.UpdatedCode.Should().Contain("return (10 + 5) * 2 * 3;");
    }

    [Fact]
    public async Task InlineVariable_WithUnusedVariable_ShouldRemoveDeclaration()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class Calculator
    {
        public int Calculate()
        {
            int unusedVariable = 42;
            int value = 10;
            return value + 5;
        }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("UnusedVariableProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.InlineVariableParams
        {
            FilePath = filePath,
            Line = 10, // Line with unused variable
            Column = 17, // Inside "unusedVariable"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.VariableName.Should().Be("unusedVariable");
        result.InlinedUsages.Should().Be(0); // No usages
        result.InitializationValue.Should().Be("42");
        
        result.UpdatedCode.Should().NotContain("int unusedVariable = 42;"); // Declaration removed
        result.UpdatedCode.Should().Contain("int value = 10;"); // Other variables remain
        result.UpdatedCode.Should().Contain("return value + 5;"); // Other code remains
    }

    [Fact]
    public async Task InlineVariable_WithVariableWithoutInitialization_ShouldReturnError()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class Calculator
    {
        public int Calculate()
        {
            int uninitializedVariable;
            uninitializedVariable = 10;
            return uninitializedVariable + 5;
        }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("UninitializedProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.InlineVariableParams
        {
            FilePath = filePath,
            Line = 10, // Line with uninitialized variable
            Column = 17, // Inside "uninitializedVariable"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("no initialization value");
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("INVALID_OPERATION");
    }

    [Fact]
    public async Task InlineVariable_WithConstVariable_ShouldReturnError()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class Calculator
    {
        public int Calculate()
        {
            const int constantValue = 10;
            return constantValue + 5;
        }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("ConstProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.InlineVariableParams
        {
            FilePath = filePath,
            Line = 10, // Line with const variable
            Column = 23, // Inside "constantValue"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Cannot inline const variables");
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("INVALID_OPERATION");
    }

    [Fact]
    public async Task InlineVariable_WithInvalidPosition_ShouldReturnError()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class Calculator
    {
        public int Calculate()
        {
            int value = 10;
            return value + 5;
        }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("InvalidPositionProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.InlineVariableParams
        {
            FilePath = filePath,
            Line = 2, // Line with using statement - not on a variable
            Column = 5,
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("No variable declaration found");
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("SYMBOL_NOT_FOUND");
    }

    [Fact]
    public void InlineVariable_ShouldHaveCorrectToolCategory()
    {
        // Act & Assert
        _tool.Category.Should().Be(COA.Mcp.Framework.ToolCategory.Refactoring);
    }

    [Fact]
    public void InlineVariable_ShouldHaveCorrectToolName()
    {
        // Act & Assert
        _tool.Name.Should().Be("csharp_inline_variable");
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

