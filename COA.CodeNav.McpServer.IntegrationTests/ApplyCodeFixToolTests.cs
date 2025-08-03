using COA.CodeNav.McpServer.Attributes;
using COA.CodeNav.McpServer.Configuration;
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
            s.Contains("roslyn_load_solution") || s.Contains("roslyn_load_project"));
    }

    [Fact]
    public void ApplyCodeFixTool_ShouldHaveProperAttributes()
    {
        // Verify the tool has proper MCP attributes
        var toolType = typeof(ApplyCodeFixTool);
        
        // Should have McpServerToolType attribute
        var toolTypeAttr = toolType.GetCustomAttributes(typeof(McpServerToolTypeAttribute), false);
        toolTypeAttr.Should().HaveCount(1);
        
        // Should have ExecuteAsync method with proper attributes
        var executeMethod = toolType.GetMethod("ExecuteAsync");
        executeMethod.Should().NotBeNull();
        
        var toolAttr = executeMethod!.GetCustomAttributes(typeof(McpServerToolAttribute), false);
        toolAttr.Should().HaveCount(1);
        
        var mcpToolAttr = (McpServerToolAttribute)toolAttr[0];
        mcpToolAttr.Name.Should().Be("roslyn_apply_code_fix");
        
        // Should have Description attribute
        var descAttr = executeMethod.GetCustomAttributes(typeof(DescriptionAttribute), false);
        descAttr.Should().HaveCount(1);
        
        var description = ((DescriptionAttribute)descAttr[0]).Description;
        description.Should().Contain("Apply a code fix");
        description.Should().Contain("Prerequisites:");
        description.Should().Contain("Use cases:");
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
            Actions = new List<NextAction>(),
            Meta = new ToolMetadata
            {
                ExecutionTime = "10ms",
                Truncated = false
            }
        };

        // Assert - verify all required fields are present
        result.Success.Should().BeTrue();
        result.Message.Should().NotBeNullOrEmpty();
        result.Operation.Should().Be("roslyn_apply_code_fix");
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
        filePathProp.GetCustomAttributes(typeof(DescriptionAttribute), false).Should().HaveCount(1);
        
        var lineProp = paramType.GetProperty("Line");
        lineProp.Should().NotBeNull();
        lineProp!.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false).Should().HaveCount(1);
        lineProp.GetCustomAttributes(typeof(DescriptionAttribute), false).Should().HaveCount(1);
        
        var columnProp = paramType.GetProperty("Column");
        columnProp.Should().NotBeNull();
        columnProp!.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false).Should().HaveCount(1);
        columnProp.GetCustomAttributes(typeof(DescriptionAttribute), false).Should().HaveCount(1);
    }
}