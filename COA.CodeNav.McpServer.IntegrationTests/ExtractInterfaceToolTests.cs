using COA.CodeNav.McpServer.Configuration;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace COA.CodeNav.McpServer.IntegrationTests;

/// <summary>
/// Integration tests for ExtractInterfaceTool
/// </summary>
public class ExtractInterfaceToolTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly ExtractInterfaceTool _tool;

    public ExtractInterfaceToolTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"ExtractInterfaceIntegrationTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

        var config = Options.Create(new WorkspaceManagerConfig());
        var workspaceManager = new MSBuildWorkspaceManager(NullLogger<MSBuildWorkspaceManager>.Instance, config);
        _workspaceService = new RoslynWorkspaceService(NullLogger<RoslynWorkspaceService>.Instance, workspaceManager);
        var documentService = new DocumentService(NullLogger<DocumentService>.Instance, _workspaceService);

        // Create framework dependencies
        var tokenEstimator = new COA.Mcp.Framework.TokenOptimization.DefaultTokenEstimator();
        var responseBuilder = new ExtractInterfaceResponseBuilder(NullLogger<ExtractInterfaceResponseBuilder>.Instance, tokenEstimator);

        _tool = new ExtractInterfaceTool(
            NullLogger<ExtractInterfaceTool>.Instance,
            _workspaceService,
            documentService,
            tokenEstimator,
            responseBuilder,
            null);
    }

    [Fact]
    public async Task ExtractInterface_EndToEnd_ShouldWork()
    {
        // Arrange
        var sourceCode = @"
using System;
using System.Collections.Generic;

namespace TestApp.Services
{
    /// <summary>
    /// Service for managing users
    /// </summary>
    public class UserService
    {
        private readonly List<string> _users = new();

        /// <summary>
        /// Creates a new user
        /// </summary>
        /// <param name=""userName"">The user name</param>
        public void CreateUser(string userName)
        {
            if (!string.IsNullOrEmpty(userName))
            {
                _users.Add(userName);
            }
        }

        /// <summary>
        /// Gets all users
        /// </summary>
        /// <returns>List of user names</returns>
        public IEnumerable<string> GetUsers()
        {
            return _users;
        }

        /// <summary>
        /// Deletes a user
        /// </summary>
        /// <param name=""userName"">The user to delete</param>
        /// <returns>True if deleted, false if not found</returns>
        public bool DeleteUser(string userName)
        {
            return _users.Remove(userName);
        }

        /// <summary>
        /// Gets user count
        /// </summary>
        public int UserCount => _users.Count;

        // This private method should not be included
        private void LogAction(string action)
        {
            Console.WriteLine($""UserService: {action}"");
        }

        // This internal method should not be included
        internal void ResetUsers()
        {
            _users.Clear();
        }
    }
}";

        var (projectPath, sourceFilePath) = await CreateTestProject("UserServiceProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.ExtractInterfaceParams
        {
            FilePath = sourceFilePath,
            Line = 9, // UserService class declaration line
            Column = 18, // Inside "UserService"
            InterfaceName = "IUserService",
            UpdateClass = true
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Successfully extracted interface 'IUserService'");

        // Verify interface generation
        result.InterfaceName.Should().Be("IUserService");
        result.InterfaceCode.Should().NotBeNullOrEmpty();
        result.InterfaceCode.Should().Contain("namespace TestApp.Services;");
        result.InterfaceCode.Should().Contain("public interface IUserService");
        result.InterfaceCode.Should().Contain("void CreateUser(string userName);");
        result.InterfaceCode.Should().Contain("IEnumerable<string> GetUsers();");
        result.InterfaceCode.Should().Contain("bool DeleteUser(string userName);");
        result.InterfaceCode.Should().Contain("int UserCount { get; }");

        // Verify interface does NOT contain private/internal members
        result.InterfaceCode.Should().NotContain("LogAction");
        result.InterfaceCode.Should().NotContain("ResetUsers");

        // Verify class update
        result.UpdatedClassCode.Should().NotBeNullOrEmpty();
        result.UpdatedClassCode.Should().Contain("public class UserService : IUserService");

        // Verify extracted members info
        result.ExtractedMembers.Should().NotBeNull();
        result.ExtractedMembers!.Should().HaveCount(4); // CreateUser, GetUsers, DeleteUser, UserCount
        result.ExtractedMembers.Should().Contain(m => m.Name == "CreateUser" && m.Kind == "Method");
        result.ExtractedMembers.Should().Contain(m => m.Name == "GetUsers" && m.Kind == "Method");
        result.ExtractedMembers.Should().Contain(m => m.Name == "DeleteUser" && m.Kind == "Method");
        result.ExtractedMembers.Should().Contain(m => m.Name == "UserCount" && m.Kind == "Property");

        // Verify metadata
        result.Query.Should().NotBeNull();
        result.Query!.FilePath.Should().Be(sourceFilePath);
        result.Query.Position.Should().NotBeNull();
        result.Query.Position!.Line.Should().Be(9);

        result.Summary.Should().NotBeNull();
        result.Summary!.TotalFound.Should().Be(4);
        result.Summary.Returned.Should().Be(4);

        result.Meta.Should().NotBeNull();
        result.Meta!.ExecutionTime.Should().NotBeNullOrEmpty();
        result.Meta.Mode.Should().Be("optimized");

        // Verify tool properties
        result.Operation.Should().Be("csharp_extract_interface");
    }

    [Fact]
    public async Task ExtractInterface_WithResponseBuilder_ShouldOptimizeForTokens()
    {
        // Arrange - Create a class with many members to trigger token optimization
        var sourceCode = @"
using System;

namespace TestApp
{
    public class LargeService
    {" + 
        // Generate 50 methods to potentially trigger token optimization
        string.Join("\n", Enumerable.Range(1, 50).Select(i => $"        public void Method{i}() {{ }}")) + @"
    }
}";

        var (projectPath, sourceFilePath) = await CreateTestProject("LargeServiceProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.ExtractInterfaceParams
        {
            FilePath = sourceFilePath,
            Line = 6, // LargeService class declaration line
            Column = 18, // Inside "LargeService"
            InterfaceName = "ILargeService",
            UpdateClass = true
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.InterfaceName.Should().Be("ILargeService");
        
        // Verify that the response builder was used
        result.Meta.Should().NotBeNull();
        result.Meta!.Mode.Should().Be("optimized");
        result.Meta.Tokens.Should().BeGreaterThan(0);

        // Should have extracted all 50 methods (unless truncated by response builder)
        result.ExtractedMembers.Should().NotBeNull();
        result.ExtractedMembers!.Count.Should().BeGreaterThan(0);
        result.ExtractedMembers.Should().AllSatisfy(m => m.Kind.Should().Be("Method"));

        // Verify the interface contains the methods
        result.InterfaceCode.Should().Contain("public interface ILargeService");
        result.InterfaceCode.Should().Contain("void Method1();");
    }

    [Fact]
    public async Task ExtractInterface_WithGenerics_ShouldHandleGenericTypes()
    {
        // Arrange
        var sourceCode = @"
using System;
using System.Collections.Generic;

namespace TestApp
{
    public class GenericRepository<T> where T : class
    {
        private readonly List<T> _items = new();

        public void Add(T item)
        {
            _items.Add(item);
        }

        public T? GetById(int id)
        {
            return id < _items.Count ? _items[id] : null;
        }

        public IEnumerable<T> GetAll()
        {
            return _items;
        }

        public bool Remove(T item)
        {
            return _items.Remove(item);
        }
    }
}";

        var (projectPath, sourceFilePath) = await CreateTestProject("GenericProject", sourceCode);
        await _workspaceService.LoadProjectAsync(projectPath);

        var parameters = new COA.CodeNav.McpServer.Tools.ExtractInterfaceParams
        {
            FilePath = sourceFilePath,
            Line = 7, // GenericRepository class declaration line
            Column = 18, // Inside "GenericRepository"
            InterfaceName = "IRepository",
            UpdateClass = true
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.InterfaceName.Should().Be("IRepository");
        
        // Verify generic interface generation
        result.InterfaceCode.Should().Contain("public interface IRepository<T> where T : class");
        result.InterfaceCode.Should().Contain("void Add(T item);");
        result.InterfaceCode.Should().Contain("T? GetById(int id);");
        result.InterfaceCode.Should().Contain("IEnumerable<T> GetAll();");
        result.InterfaceCode.Should().Contain("bool Remove(T item);");

        // Verify class update with generic constraint
        result.UpdatedClassCode.Should().Contain("public class GenericRepository<T> : IRepository<T> where T : class");
    }

    private async Task<(string projectPath, string sourceFilePath)> CreateTestProject(string projectName, string sourceCode)
    {
        var projectDir = Path.Combine(_tempDirectory, projectName);
        Directory.CreateDirectory(projectDir);

        // Create project file
        var projectPath = Path.Combine(projectDir, $"{projectName}.csproj");
        var projectContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>";

        await File.WriteAllTextAsync(projectPath, projectContent);

        // Create source file
        var sourceFilePath = Path.Combine(projectDir, "Service.cs");
        await File.WriteAllTextAsync(sourceFilePath, sourceCode);

        return (projectPath, sourceFilePath);
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

