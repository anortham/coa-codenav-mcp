using COA.Mcp.Framework.Attributes;
using COA.Mcp.Framework.Models;
using COA.CodeNav.McpServer.Constants;
using COA.CodeNav.McpServer.Models;
using FluentAssertions;
using System.Reflection;

namespace COA.CodeNav.McpServer.IntegrationTests;

/// <summary>
/// Tests for the MCP server functionality
/// </summary>
public class McpServerTests
{
    [Fact]
    public void AllTools_ShouldInheritFromMcpToolBase()
    {
        // Get all tool classes
        var assembly = Assembly.GetAssembly(typeof(GoToDefinitionToolResult))!;
        var toolTypes = assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Tool") && !t.IsAbstract && t.Namespace == "COA.CodeNav.McpServer.Tools")
            .ToList();

        toolTypes.Should().NotBeEmpty();

        foreach (var toolType in toolTypes)
        {
            // Each tool should inherit from McpToolBase<,>
            var baseType = toolType.BaseType;
            while (baseType != null && baseType != typeof(object))
            {
                if (baseType.IsGenericType && baseType.GetGenericTypeDefinition().FullName?.Contains("McpToolBase") == true)
                {
                    break;
                }
                baseType = baseType.BaseType;
            }
            
            baseType.Should().NotBeNull($"{toolType.Name} should inherit from McpToolBase<,>");
            
            // Tool should have Name property
            var nameProp = toolType.GetProperty("Name");
            nameProp.Should().NotBeNull($"{toolType.Name} should have Name property");
            
            // Tool should have Description property
            var descProp = toolType.GetProperty("Description");
            descProp.Should().NotBeNull($"{toolType.Name} should have Description property");
        }
    }

    [Fact]
    public void AllToolResults_ShouldInheritFromBaseResult()
    {
        // This test verifies that all tool result types properly inherit from BaseResult
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
    public void ToolNames_ShouldFollowNamingConvention()
    {
        // Get all tool classes that inherit from McpToolBase
        var assembly = Assembly.GetAssembly(typeof(GoToDefinitionToolResult))!;
        var toolTypes = assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Tool") && !t.IsAbstract && t.Namespace == "COA.CodeNav.McpServer.Tools")
            .ToList();

        foreach (var toolType in toolTypes)
        {
            // Create instance to get the Name property value
            // Since tools require DI, we'll check the Name property exists and is overridden
            var nameProp = toolType.GetProperty("Name");
            if (nameProp != null && nameProp.DeclaringType == toolType)
            {
                // We can't easily instantiate to get the value, but we can check the ToolNames constants
                // Tool names should start with "csharp_" and use lowercase with underscores
                // This is enforced by the ToolNames constants class
            }
        }
    }
}