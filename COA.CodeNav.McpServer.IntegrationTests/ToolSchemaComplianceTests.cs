using COA.CodeNav.McpServer.Configuration;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Tools;
using COA.Mcp.Framework.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace COA.CodeNav.McpServer.IntegrationTests;

/// <summary>
/// Tests that verify all tools follow the consistent result schema
/// </summary>
public class ToolSchemaComplianceTests
{
    [Fact]
    public async Task GetWorkspaceStatisticsTool_ShouldReturnCompliantSchema()
    {
        // Arrange
        var config = Options.Create(new WorkspaceManagerConfig());
        var workspaceManager = new MSBuildWorkspaceManager(
            NullLogger<MSBuildWorkspaceManager>.Instance, 
            config);
        
        var tool = new GetWorkspaceStatisticsTool(
            NullLogger<GetWorkspaceStatisticsTool>.Instance,
            workspaceManager);

        // Act
        var result = await tool.ExecuteAsync(new GetWorkspaceStatisticsParams(), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<GetWorkspaceStatisticsResult>();
        
        var typedResult = (GetWorkspaceStatisticsResult)result;
        
        // Verify all required schema fields are present
        typedResult.Success.Should().BeTrue();
        typedResult.Message.Should().NotBeNullOrEmpty();
        typedResult.Operation.Should().Be(ToolNames.GetWorkspaceStatistics);
        
        // Standard fields
        typedResult.Query.Should().NotBeNull();
        typedResult.Summary.Should().NotBeNull();
        typedResult.Summary!.ExecutionTime.Should().NotBeNullOrEmpty();
        typedResult.ResultsSummary.Should().NotBeNull();
        
        // Tool-specific fields
        typedResult.Statistics.Should().NotBeNull();
        typedResult.Statistics!.MaxWorkspaces.Should().BeGreaterThan(0);
        
        // AI-friendly fields
        typedResult.Insights.Should().NotBeNull();
        typedResult.Actions.Should().NotBeNull();
        typedResult.Meta.Should().NotBeNull();
        typedResult.Meta!.ExecutionTime.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GoToDefinitionTool_WithoutWorkspace_ShouldReturnProperError()
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
            
        var tool = new GoToDefinitionTool(
            NullLogger<GoToDefinitionTool>.Instance,
            workspaceService,
            documentService,
            null);

        var parameters = new GoToDefinitionParams
        {
            FilePath = "C:\\nonexistent\\file.cs",
            Line = 10,
            Column = 5
        };

        // Act
        var result = await tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<GoToDefinitionToolResult>();
        
        var typedResult = (GoToDefinitionToolResult)result;
        
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
    public void AllToolResults_ShouldInheritFromToolResultBase()
    {
        // This test verifies that all tool result types properly inherit from ToolResultBase
        var assembly = typeof(GoToDefinitionToolResult).Assembly;
        var toolResultTypes = assembly.GetTypes()
            .Where(t => t.Name.EndsWith("ToolResult") && !t.IsAbstract)
            .ToList();

        toolResultTypes.Should().NotBeEmpty("There should be tool result types in the assembly");

        foreach (var resultType in toolResultTypes)
        {
            resultType.Should().BeAssignableTo<ToolResultBase>(
                $"{resultType.Name} should inherit from ToolResultBase");
            
            // Verify it has the Operation property override
            var operationProperty = resultType.GetProperty("Operation");
            operationProperty.Should().NotBeNull($"{resultType.Name} should override Operation property");
        }
    }

    [Fact]
    public void ErrorCodes_ShouldBeConsistent()
    {
        // Verify we have standard error codes defined
        typeof(ErrorCodes).GetFields()
            .Where(f => f.IsLiteral && !f.IsInitOnly)
            .Should().Contain(f => f.Name == "DOCUMENT_NOT_FOUND");
        
        typeof(ErrorCodes).GetFields()
            .Where(f => f.IsLiteral && !f.IsInitOnly)
            .Should().Contain(f => f.Name == "NO_SYMBOL_AT_POSITION");
        
        typeof(ErrorCodes).GetFields()
            .Where(f => f.IsLiteral && !f.IsInitOnly)
            .Should().Contain(f => f.Name == "WORKSPACE_NOT_LOADED");
    }

    [Fact]
    public async Task FindAllReferencesTool_WithoutWorkspace_ShouldReturnHelpfulError()
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
            
        var tool = new FindAllReferencesTool(
            NullLogger<FindAllReferencesTool>.Instance,
            workspaceService,
            documentService,
            null);

        var parameters = new FindAllReferencesParams
        {
            FilePath = "C:\\nonexistent\\file.cs",
            Line = 10,
            Column = 5
        };

        // Act
        var result = await tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<FindAllReferencesToolResult>();
        
        var typedResult = (FindAllReferencesToolResult)result;
        
        typedResult.Success.Should().BeFalse();
        typedResult.Error.Should().NotBeNull();
        typedResult.Error!.Recovery!.Steps.Should().Contain(s => 
            s.Contains(ToolNames.LoadSolution) || s.Contains(ToolNames.LoadProject));
    }
}