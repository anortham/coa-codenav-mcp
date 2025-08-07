using COA.Mcp.Framework.Attributes;
using COA.CodeNav.McpServer.Configuration;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.IntegrationTests;

/// <summary>
/// Tests for the Apply Code Fix Tool
/// </summary>
public class ApplyCodeFixToolTests
{
    [Fact]
    public async Task ApplyCodeFixTool_WithoutWorkspace_ShouldReturnProperError()
    {
        // Arrange
        var workspaceService = new RoslynWorkspaceService(
            NullLogger<RoslynWorkspaceService>.Instance,
            new MSBuildWorkspaceManager(
                NullLogger<MSBuildWorkspaceManager>.Instance,
                Options.Create(new WorkspaceManagerConfig())));
        
        var documentService = new DocumentService(
            NullLogger<DocumentService>.Instance,
            workspaceService);
        
        var codeFixService = new CodeFixService(
            NullLogger<CodeFixService>.Instance);
            
        var tool = new ApplyCodeFixTool(
            NullLogger<ApplyCodeFixTool>.Instance,
            workspaceService,
            documentService,
            codeFixService,
            null);

        var parameters = new ApplyCodeFixParams
        {
            FilePath = "C:\\nonexistent\\file.cs",
            Line = 10,
            Column = 5
        };

        // Act
        var result = await tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<ApplyCodeFixToolResult>();
        
        var typedResult = (ApplyCodeFixToolResult)result;
        
        typedResult.Success.Should().BeFalse();
        typedResult.Message.Should().Contain("Document not found");
        
        // Error structure
        typedResult.Error.Should().NotBeNull();
        typedResult.Error!.Code.Should().Be(ErrorCodes.DOCUMENT_NOT_FOUND);
        typedResult.Error.Recovery.Should().NotBeNull();
        typedResult.Error.Recovery!.Steps.Should().NotBeNullOrEmpty();
        
        // Should include hint to load workspace
        typedResult.Error.Recovery.Steps.Should().Contain(s => 
            s.Contains(ToolNames.LoadSolution) || s.Contains(ToolNames.LoadProject));
    }

    [Fact]
    public void ApplyCodeFixTool_ShouldHaveProperAttributes()
    {
        // Verify the tool has proper MCP attributes
        var toolType = typeof(ApplyCodeFixTool);
        
        // Tool should inherit from McpToolBase
        toolType.Should().BeAssignableTo(typeof(COA.Mcp.Framework.Base.McpToolBase<ApplyCodeFixParams, ApplyCodeFixToolResult>));
        
        // Should have Name property
        var nameProp = toolType.GetProperty("Name");
        nameProp.Should().NotBeNull();
        
        // Should have Description property
        var descProp = toolType.GetProperty("Description");
        descProp.Should().NotBeNull();
    }

    [Fact]
    public void ApplyCodeFixToolResult_ShouldFollowSchema()
    {
        // Arrange
        var result = new ApplyCodeFixToolResult
        {
            Success = true,
            Message = "Test message",
            FixTitle = "Remove unused variable",
            DiagnosticId = "CS0219",
            AllFilesSucceeded = true,
            AppliedChanges = new List<FileChange>
            {
                new FileChange
                {
                    FilePath = "test.cs",
                    Changes = new List<TextChange>()
                }
            },
            Insights = new List<string> { "Applied fix" },
            Actions = new List<COA.Mcp.Framework.Models.AIAction>(),
            Meta = new COA.Mcp.Framework.Models.ToolExecutionMetadata
            {
                ExecutionTime = "10ms",
                Truncated = false
            }
        };

        // Assert - verify all required fields are present
        result.Success.Should().BeTrue();
        result.Message.Should().NotBeNullOrEmpty();
        result.Operation.Should().Be(ToolNames.ApplyCodeFix);
        result.FixTitle.Should().NotBeNullOrEmpty();
        result.DiagnosticId.Should().NotBeNullOrEmpty();
        result.AllFilesSucceeded.Should().BeTrue();
        result.AppliedChanges.Should().NotBeNull();
        result.Insights.Should().NotBeNull();
        result.Actions.Should().NotBeNull();
        result.Meta.Should().NotBeNull();
    }

    [Fact]
    public void ApplyCodeFixParams_ShouldHaveRequiredProperties()
    {
        // Verify all required parameters are properly decorated
        var paramType = typeof(ApplyCodeFixParams);
        
        var filePathProp = paramType.GetProperty("FilePath");
        filePathProp.Should().NotBeNull();
        filePathProp!.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false).Should().HaveCount(1);
        filePathProp.GetCustomAttributes(typeof(COA.Mcp.Framework.Attributes.DescriptionAttribute), false).Should().HaveCount(1);
        
        var lineProp = paramType.GetProperty("Line");
        lineProp.Should().NotBeNull();
        lineProp!.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false).Should().HaveCount(1);
        lineProp.GetCustomAttributes(typeof(COA.Mcp.Framework.Attributes.DescriptionAttribute), false).Should().HaveCount(1);
        
        var columnProp = paramType.GetProperty("Column");
        columnProp.Should().NotBeNull();
        columnProp!.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false).Should().HaveCount(1);
        columnProp.GetCustomAttributes(typeof(COA.Mcp.Framework.Attributes.DescriptionAttribute), false).Should().HaveCount(1);
    }
}