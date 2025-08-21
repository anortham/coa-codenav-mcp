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
/// Unit tests for ExtractInterfaceTool focusing on interface extraction scenarios
/// </summary>
public class ExtractInterfaceToolUnitTests : IDisposable
{
    private readonly Mock<ILogger<ExtractInterfaceTool>> _mockLogger;
    private readonly Mock<ILogger<RoslynWorkspaceService>> _mockWorkspaceLogger;
    private readonly Mock<ILogger<MSBuildWorkspaceManager>> _mockManagerLogger;
    private readonly Mock<ILogger<DocumentService>> _mockDocumentLogger;
    private readonly Mock<ILogger<ExtractInterfaceResponseBuilder>> _mockResponseBuilderLogger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly ExtractInterfaceTool _tool;
    private readonly string _tempDirectory;

    public ExtractInterfaceToolUnitTests()
    {
        _mockLogger = new Mock<ILogger<ExtractInterfaceTool>>();
        _mockWorkspaceLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        _mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        _mockDocumentLogger = new Mock<ILogger<DocumentService>>();
        _mockResponseBuilderLogger = new Mock<ILogger<ExtractInterfaceResponseBuilder>>();
        
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"ExtractInterfaceTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        
        var config = Options.Create(new WorkspaceManagerConfig());
        var workspaceManager = new MSBuildWorkspaceManager(_mockManagerLogger.Object, config);
        _workspaceService = new RoslynWorkspaceService(_mockWorkspaceLogger.Object, workspaceManager);
        _documentService = new DocumentService(_mockDocumentLogger.Object, _workspaceService);
        
        // Create dependencies from framework
        var tokenEstimator = new COA.Mcp.Framework.TokenOptimization.DefaultTokenEstimator();
        var responseBuilder = new ExtractInterfaceResponseBuilder(_mockResponseBuilderLogger.Object, tokenEstimator);
        
        _tool = new ExtractInterfaceTool(
            _mockLogger.Object,
            _workspaceService,
            _documentService,
            tokenEstimator,
            responseBuilder,
            null);
    }

    [Fact]
    public async Task ExtractInterface_WithoutWorkspace_ShouldReturnProperError()
    {
        // Arrange
        var parameters = new COA.CodeNav.McpServer.Tools.ExtractInterfaceParams
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
    public async Task ExtractInterface_WithValidClass_ShouldExtractInterface()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class TestService
    {
        public void DoSomething() { }
        public string GetData() { return ""data""; }
        public int Count { get; set; }
        private void PrivateMethod() { }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("TestProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.ExtractInterfaceParams
        {
            FilePath = filePath,
            Line = 6, // Line with class declaration
            Column = 18, // Inside "TestService"
            InterfaceName = "ITestService",
            UpdateClass = true
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.InterfaceName.Should().Be("ITestService");
        result.InterfaceCode.Should().NotBeNullOrEmpty();
        result.InterfaceCode.Should().Contain("public interface ITestService");
        result.InterfaceCode.Should().Contain("void DoSomething()");
        result.InterfaceCode.Should().Contain("string GetData()");
        result.InterfaceCode.Should().Contain("int Count { get; set; }");
        result.InterfaceCode.Should().NotContain("PrivateMethod"); // Should not include private methods
        
        result.UpdatedClassCode.Should().NotBeNullOrEmpty();
        result.UpdatedClassCode.Should().Contain("class TestService : ITestService");
        
        result.ExtractedMembers.Should().NotBeNull();
        result.ExtractedMembers!.Should().HaveCount(3); // DoSomething, GetData, Count
        result.ExtractedMembers.Should().Contain(m => m.Name == "DoSomething");
        result.ExtractedMembers.Should().Contain(m => m.Name == "GetData");
        result.ExtractedMembers.Should().Contain(m => m.Name == "Count");
        
        result.Query.Should().NotBeNull();
        result.Summary.Should().NotBeNull();
        result.Summary!.TotalFound.Should().Be(3);
        result.Meta.Should().NotBeNull();
        result.Meta!.ExecutionTime.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExtractInterface_WithSpecificMembers_ShouldExtractOnlySpecifiedMembers()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class Calculator
    {
        public int Add(int a, int b) { return a + b; }
        public int Subtract(int a, int b) { return a - b; }
        public int Multiply(int a, int b) { return a * b; }
        public int Divide(int a, int b) { return a / b; }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("CalculatorProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.ExtractInterfaceParams
        {
            FilePath = filePath,
            Line = 6, // Line with class declaration
            Column = 18, // Inside "Calculator"
            InterfaceName = "IBasicCalculator",
            MemberNames = new[] { "Add", "Subtract" }, // Only extract Add and Subtract
            UpdateClass = true
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.InterfaceName.Should().Be("IBasicCalculator");
        result.InterfaceCode.Should().Contain("int Add(int a, int b)");
        result.InterfaceCode.Should().Contain("int Subtract(int a, int b)");
        result.InterfaceCode.Should().NotContain("Multiply");
        result.InterfaceCode.Should().NotContain("Divide");
        
        result.ExtractedMembers.Should().HaveCount(2);
        result.ExtractedMembers.Should().Contain(m => m.Name == "Add");
        result.ExtractedMembers.Should().Contain(m => m.Name == "Subtract");
    }

    [Fact]
    public async Task ExtractInterface_WithGeneratedInterfaceName_ShouldGenerateProperName()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class UserService
    {
        public void CreateUser() { }
        public void DeleteUser() { }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("ServiceProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.ExtractInterfaceParams
        {
            FilePath = filePath,
            Line = 6, // Line with class declaration
            Column = 18, // Inside "UserService"
            // No InterfaceName specified - should auto-generate
            UpdateClass = true
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.InterfaceName.Should().Be("IUser"); // Should remove "Service" suffix and add "I" prefix
        result.InterfaceCode.Should().Contain("public interface IUser");
    }

    [Fact]
    public async Task ExtractInterface_WithNoPublicMembers_ShouldReturnError()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class EmptyClass
    {
        private void PrivateMethod() { }
        internal void InternalMethod() { }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("EmptyProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.ExtractInterfaceParams
        {
            FilePath = filePath,
            Line = 6, // Line with class declaration
            Column = 18, // Inside "EmptyClass"
            UpdateClass = true
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("No suitable members found");
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("INVALID_OPERATION");
    }

    [Fact]
    public async Task ExtractInterface_WithInvalidPosition_ShouldReturnError()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class TestClass
    {
        public void Method() { }
    }
}";

        var (projectPath, filePath) = await CreateTestProject("TestProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.ExtractInterfaceParams
        {
            FilePath = filePath,
            Line = 2, // Line with using statement - not on a type
            Column = 5,
            UpdateClass = true
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("No type declaration found");
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("SYMBOL_NOT_FOUND");
    }

    [Fact]
    public void ExtractInterface_ShouldHaveCorrectToolCategory()
    {
        // Act & Assert
        _tool.Category.Should().Be(COA.Mcp.Framework.ToolCategory.Refactoring);
    }

    [Fact]
    public void ExtractInterface_ShouldHaveCorrectToolName()
    {
        // Act & Assert
        _tool.Name.Should().Be("csharp_extract_interface");
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

