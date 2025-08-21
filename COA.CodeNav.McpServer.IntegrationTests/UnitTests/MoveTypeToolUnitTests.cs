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
/// Unit tests for MoveTypeTool focusing on type moving scenarios
/// </summary>
public class MoveTypeToolUnitTests : IDisposable
{
    private readonly Mock<ILogger<MoveTypeTool>> _mockLogger;
    private readonly Mock<ILogger<RoslynWorkspaceService>> _mockWorkspaceLogger;
    private readonly Mock<ILogger<MSBuildWorkspaceManager>> _mockManagerLogger;
    private readonly Mock<ILogger<DocumentService>> _mockDocumentLogger;
    private readonly Mock<ILogger<MoveTypeResponseBuilder>> _mockResponseBuilderLogger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly MoveTypeTool _tool;
    private readonly string _tempDirectory;

    public MoveTypeToolUnitTests()
    {
        _mockLogger = new Mock<ILogger<MoveTypeTool>>();
        _mockWorkspaceLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        _mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        _mockDocumentLogger = new Mock<ILogger<DocumentService>>();
        _mockResponseBuilderLogger = new Mock<ILogger<MoveTypeResponseBuilder>>();
        
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"MoveTypeTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        
        var config = Options.Create(new WorkspaceManagerConfig());
        var workspaceManager = new MSBuildWorkspaceManager(_mockManagerLogger.Object, config);
        _workspaceService = new RoslynWorkspaceService(_mockWorkspaceLogger.Object, workspaceManager);
        _documentService = new DocumentService(_mockDocumentLogger.Object, _workspaceService);
        
        // Create dependencies from framework
        var tokenEstimator = new COA.Mcp.Framework.TokenOptimization.DefaultTokenEstimator();
        var responseBuilder = new MoveTypeResponseBuilder(_mockResponseBuilderLogger.Object, tokenEstimator);
        
        _tool = new MoveTypeTool(
            _mockLogger.Object,
            _workspaceService,
            _documentService,
            tokenEstimator,
            responseBuilder,
            null);
    }

    [Fact]
    public async Task MoveType_WithoutWorkspace_ShouldReturnProperError()
    {
        // Arrange
        var parameters = new COA.CodeNav.McpServer.Tools.MoveTypeParams
        {
            FilePath = "C:\\nonexistent\\source.cs",
            TypeName = "TestClass",
            TargetFilePath = "C:\\nonexistent\\target.cs"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Source document not found");
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("DOCUMENT_NOT_FOUND");
        result.Error.Recovery.Should().NotBeNull();
        result.Error.Recovery!.Steps.Should().Contain(step => step.Contains("Load a solution"));
    }

    [Fact]
    public async Task MoveType_ToNewFile_ShouldCreateNewFileAndMoveType()
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

    public class OrderService
    {
        public void CreateOrder() { }
    }
}";

        var (projectPath, sourceFilePath) = await CreateTestProject("MoveTypeProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var targetFilePath = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "UserService.cs");

        var parameters = new COA.CodeNav.McpServer.Tools.MoveTypeParams
        {
            FilePath = sourceFilePath,
            TypeName = "UserService",
            TargetFilePath = targetFilePath,
            CreateNewFile = true
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.TypeName.Should().Be("UserService");
        result.TargetFilePath.Should().Be(targetFilePath);
        result.WasNewFileCreated.Should().BeTrue();
        
        result.UpdatedSourceCode.Should().NotBeNullOrEmpty();
        result.UpdatedSourceCode.Should().NotContain("class UserService"); // Should be removed from source
        result.UpdatedSourceCode.Should().Contain("class OrderService"); // Should remain in source
        
        result.UpdatedTargetCode.Should().NotBeNullOrEmpty();
        result.UpdatedTargetCode.Should().Contain("class UserService"); // Should be in target
        result.UpdatedTargetCode.Should().Contain("namespace TestNamespace");
        result.UpdatedTargetCode.Should().Contain("using System;");
        
        result.Query.Should().NotBeNull();
        result.Summary.Should().NotBeNull();
        result.Meta.Should().NotBeNull();
        result.Meta!.ExecutionTime.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task MoveType_ToExistingFile_ShouldAppendToExistingFile()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class UserService
    {
        public void CreateUser() { }
    }
}";

        var targetCode = @"
using System;

namespace TestNamespace
{
    public class BaseService
    {
        public void Initialize() { }
    }
}";

        var (projectPath, sourceFilePath) = await CreateTestProject("MoveTypeProject", sourceCode);
        var targetFilePath = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "Services.cs");
        await File.WriteAllTextAsync(targetFilePath, targetCode);
        
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.MoveTypeParams
        {
            FilePath = sourceFilePath,
            TypeName = "UserService",
            TargetFilePath = targetFilePath,
            CreateNewFile = false
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.WasNewFileCreated.Should().BeFalse();
        
        result.UpdatedTargetCode.Should().NotBeNullOrEmpty();
        result.UpdatedTargetCode.Should().Contain("class BaseService"); // Should keep existing content
        result.UpdatedTargetCode.Should().Contain("class UserService"); // Should add moved type
    }

    [Fact]
    public async Task MoveType_WithNonExistentType_ShouldReturnError()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class ExistingClass
    {
        public void Method() { }
    }
}";

        var (projectPath, sourceFilePath) = await CreateTestProject("MoveTypeProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var targetFilePath = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "Target.cs");

        var parameters = new COA.CodeNav.McpServer.Tools.MoveTypeParams
        {
            FilePath = sourceFilePath,
            TypeName = "NonExistentClass", // This class doesn't exist
            TargetFilePath = targetFilePath
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Type 'NonExistentClass' not found");
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("SYMBOL_NOT_FOUND");
    }

    [Fact]
    public async Task MoveType_WithInterface_ShouldMoveInterface()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public interface IUserService
    {
        void CreateUser();
        void DeleteUser();
    }

    public class UserService : IUserService
    {
        public void CreateUser() { }
        public void DeleteUser() { }
    }
}";

        var (projectPath, sourceFilePath) = await CreateTestProject("InterfaceProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var targetFilePath = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "IUserService.cs");

        var parameters = new COA.CodeNav.McpServer.Tools.MoveTypeParams
        {
            FilePath = sourceFilePath,
            TypeName = "IUserService",
            TargetFilePath = targetFilePath,
            CreateNewFile = true
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.TypeName.Should().Be("IUserService");
        
        result.UpdatedSourceCode.Should().NotContain("interface IUserService");
        result.UpdatedSourceCode.Should().Contain("class UserService"); // Implementation should remain
        
        result.UpdatedTargetCode.Should().Contain("interface IUserService");
        result.UpdatedTargetCode.Should().NotContain("class UserService"); // Implementation should not be moved
    }

    [Fact]
    public async Task MoveType_WithEnum_ShouldMoveEnum()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public enum Status
    {
        Active,
        Inactive,
        Pending
    }

    public class UserService
    {
        public Status GetStatus() { return Status.Active; }
    }
}";

        var (projectPath, sourceFilePath) = await CreateTestProject("EnumProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var targetFilePath = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "Status.cs");

        var parameters = new COA.CodeNav.McpServer.Tools.MoveTypeParams
        {
            FilePath = sourceFilePath,
            TypeName = "Status",
            TargetFilePath = targetFilePath,
            CreateNewFile = true
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.TypeName.Should().Be("Status");
        
        result.UpdatedSourceCode.Should().NotContain("enum Status");
        result.UpdatedSourceCode.Should().Contain("class UserService");
        
        result.UpdatedTargetCode.Should().Contain("enum Status");
        result.UpdatedTargetCode.Should().Contain("Active");
        result.UpdatedTargetCode.Should().Contain("Inactive");
        result.UpdatedTargetCode.Should().Contain("Pending");
    }

    [Fact]
    public void MoveType_ShouldHaveCorrectToolCategory()
    {
        // Act & Assert
        _tool.Category.Should().Be(COA.Mcp.Framework.ToolCategory.Refactoring);
    }

    [Fact]
    public void MoveType_ShouldHaveCorrectToolName()
    {
        // Act & Assert
        _tool.Name.Should().Be("csharp_move_type");
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

