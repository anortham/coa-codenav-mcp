using COA.CodeNav.McpServer.Attributes;
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
    public void AllTools_ShouldHaveProperAttributes()
    {
        // Get all tool classes
        var assembly = Assembly.GetAssembly(typeof(ToolResultBase))!;
        var toolTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .ToList();

        toolTypes.Should().NotBeEmpty();

        foreach (var toolType in toolTypes)
        {
            // Each tool type should have at least one method with McpServerTool attribute
            var toolMethods = toolType.GetMethods()
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                .ToList();
            
            toolMethods.Should().NotBeEmpty($"{toolType.Name} should have at least one tool method");
            
            foreach (var method in toolMethods)
            {
                // Tool method should have Description attribute
                var description = method.GetCustomAttribute<DescriptionAttribute>();
                description.Should().NotBeNull($"{toolType.Name}.{method.Name} should have Description attribute");
                description!.Description.Should().NotBeNullOrEmpty();
                
                // Tool method should return Task<> (either Task<object> or Task<ConcreteType>)
                method.ReturnType.Should().Match(t => 
                    t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>),
                    $"{toolType.Name}.{method.Name} should return Task<T>");
                
                // Tool method should have proper parameters
                var parameters = method.GetParameters();
                parameters.Length.Should().BeGreaterThanOrEqualTo(1); // At least the tool params
                parameters.Last().ParameterType.Should().Be(typeof(CancellationToken));
            }
        }
    }

    [Fact]
    public void AllToolResults_ShouldInheritFromToolResultBase()
    {
        // This test verifies that all tool result types properly inherit from ToolResultBase
        var assembly = typeof(ToolResultBase).Assembly;
        var toolResultTypes = assembly.GetTypes()
            .Where(t => t.Name.EndsWith("ToolResult") && !t.IsAbstract && t != typeof(ToolResultBase))
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
        // Get all tool classes
        var assembly = Assembly.GetAssembly(typeof(ToolResultBase))!;
        var toolTypes = assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .ToList();

        foreach (var toolType in toolTypes)
        {
            var toolMethods = toolType.GetMethods()
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                .ToList();
            
            foreach (var method in toolMethods)
            {
                var toolAttribute = method.GetCustomAttribute<McpServerToolAttribute>();
                var toolName = toolAttribute!.Name;
                
                // Tool names should start with "csharp_"
                toolName.Should().StartWith("csharp_", $"Tool {toolName} should follow naming convention");
                
                // Tool names should be lowercase with underscores
                toolName.Should().MatchRegex("^[a-z_]+$", $"Tool {toolName} should use lowercase and underscores only");
            }
        }
    }
}