using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders;
using COA.Mcp.Framework.TokenOptimization;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace COA.CodeNav.McpServer.IntegrationTests.UnitTests;

/// <summary>
/// Unit tests for response builders focusing on basic functionality
/// </summary>
public class ResponseBuilderTests
{
    private readonly Mock<ILogger<ExtractInterfaceResponseBuilder>> _mockExtractLogger;
    private readonly Mock<ITokenEstimator> _mockTokenEstimator;

    public ResponseBuilderTests()
    {
        _mockExtractLogger = new Mock<ILogger<ExtractInterfaceResponseBuilder>>();
        _mockTokenEstimator = new Mock<ITokenEstimator>();
    }

    [Fact]
    public void ExtractInterfaceResponseBuilder_ShouldBeCreated()
    {
        // Arrange & Act
        var builder = new ExtractInterfaceResponseBuilder(_mockExtractLogger.Object, _mockTokenEstimator.Object);

        // Assert
        builder.Should().NotBeNull();
    }

    [Fact]
    public void MoveTypeResponseBuilder_ShouldBeCreated()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<MoveTypeResponseBuilder>>();
        
        // Act
        var builder = new MoveTypeResponseBuilder(mockLogger.Object, _mockTokenEstimator.Object);

        // Assert
        builder.Should().NotBeNull();
    }

    [Fact]
    public void InlineMethodResponseBuilder_ShouldBeCreated()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<InlineMethodResponseBuilder>>();
        
        // Act
        var builder = new InlineMethodResponseBuilder(mockLogger.Object, _mockTokenEstimator.Object);

        // Assert
        builder.Should().NotBeNull();
    }

    [Fact]
    public void InlineVariableResponseBuilder_ShouldBeCreated()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<InlineVariableResponseBuilder>>();
        
        // Act
        var builder = new InlineVariableResponseBuilder(mockLogger.Object, _mockTokenEstimator.Object);

        // Assert
        builder.Should().NotBeNull();
    }

    [Fact]
    public void ExtractInterfaceResult_ShouldHaveCorrectOperationName()
    {
        // Arrange & Act
        var result = new ExtractInterfaceResult();

        // Assert
        result.Operation.Should().Be("csharp_extract_interface");
    }

    [Fact]
    public void MoveTypeResult_ShouldHaveCorrectOperationName()
    {
        // Arrange & Act
        var result = new MoveTypeResult();

        // Assert
        result.Operation.Should().Be("csharp_move_type");
    }

    [Fact]
    public void InlineMethodResult_ShouldHaveCorrectOperationName()
    {
        // Arrange & Act
        var result = new InlineMethodResult();

        // Assert
        result.Operation.Should().Be("csharp_inline_method");
    }

    [Fact]
    public void InlineVariableResult_ShouldHaveCorrectOperationName()
    {
        // Arrange & Act
        var result = new InlineVariableResult();

        // Assert
        result.Operation.Should().Be("csharp_inline_variable");
    }

    [Fact]
    public void ExtractedMemberInfo_ShouldSetRequiredProperties()
    {
        // Arrange & Act
        var memberInfo = new ExtractedMemberInfo
        {
            Name = "TestMethod",
            Kind = "Method",
            Signature = "public void TestMethod()"
        };

        // Assert
        memberInfo.Name.Should().Be("TestMethod");
        memberInfo.Kind.Should().Be("Method");
        memberInfo.Signature.Should().Be("public void TestMethod()");
    }

    [Fact]
    public void ExtractInterfaceResult_ShouldAllowSettingExtractedMembers()
    {
        // Arrange
        var result = new ExtractInterfaceResult();
        var members = new List<ExtractedMemberInfo>
        {
            new ExtractedMemberInfo { Name = "Method1", Kind = "Method", Signature = "void Method1()" },
            new ExtractedMemberInfo { Name = "Property1", Kind = "Property", Signature = "string Property1 { get; set; }" }
        };

        // Act
        result.ExtractedMembers = members;

        // Assert
        result.ExtractedMembers.Should().HaveCount(2);
        result.ExtractedMembers.Should().Contain(m => m.Name == "Method1");
        result.ExtractedMembers.Should().Contain(m => m.Name == "Property1");
    }
}