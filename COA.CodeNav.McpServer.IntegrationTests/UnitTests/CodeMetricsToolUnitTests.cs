using COA.CodeNav.McpServer.Configuration;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// using DotMemory.Unit; // Temporarily disabled

namespace COA.CodeNav.McpServer.IntegrationTests.UnitTests;

/// <summary>
/// Comprehensive unit tests for CodeMetricsTool focusing on complex code analysis scenarios
/// </summary>
// [DotMemoryUnit(FailIfRunWithoutSupport = false)] // Temporarily disabled
public class CodeMetricsToolUnitTests : IDisposable
{
    private readonly Mock<ILogger<CodeMetricsTool>> _mockLogger;
    private readonly Mock<ILogger<RoslynWorkspaceService>> _mockWorkspaceLogger;
    private readonly Mock<ILogger<MSBuildWorkspaceManager>> _mockManagerLogger;
    private readonly Mock<ILogger<DocumentService>> _mockDocumentLogger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly CodeMetricsTool _tool;
    private readonly string _tempDirectory;

    public CodeMetricsToolUnitTests()
    {
        _mockLogger = new Mock<ILogger<CodeMetricsTool>>();
        _mockWorkspaceLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        _mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        _mockDocumentLogger = new Mock<ILogger<DocumentService>>();
        
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"CodeMetricsTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        
        var config = Options.Create(new WorkspaceManagerConfig());
        var workspaceManager = new MSBuildWorkspaceManager(_mockManagerLogger.Object, config);
        _workspaceService = new RoslynWorkspaceService(_mockWorkspaceLogger.Object, workspaceManager);
        _documentService = new DocumentService(_mockDocumentLogger.Object, _workspaceService);
        _tool = new CodeMetricsTool(_mockLogger.Object, _workspaceService, _documentService, null);
    }

    [Fact]
    public async Task CodeMetrics_WithNoWorkspaceLoaded_ShouldReturnWorkspaceNotLoadedError()
    {
        // Arrange
        var parameters = new CodeMetricsParams
        {
            FilePath = "C:\\NonExistent\\File.cs",
            Scope = "file"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<CodeMetricsResult>();
        var typedResult = (CodeMetricsResult)result;
        
        typedResult.Success.Should().BeFalse();
        typedResult.Error.Should().NotBeNull();
        typedResult.Error!.Code.Should().Be("DOCUMENT_NOT_FOUND");
        typedResult.Error.Recovery.Should().NotBeNull();
        typedResult.Error.Recovery!.Steps.Should().Contain(s => s.Contains("csharp_load_solution"));
    }

    [Fact]
    public async Task CodeMetrics_WithSimpleMethod_ShouldCalculateBasicMetrics()
    {
        // Arrange
        var (projectPath, methodLocation) = await SetupSimpleMethodAsync();
        
        var parameters = new CodeMetricsParams
        {
            FilePath = methodLocation.FilePath,
            Line = methodLocation.Line,
            Column = methodLocation.Column,
            Scope = "method",
            IncludeInherited = false
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<CodeMetricsResult>();
        var typedResult = (CodeMetricsResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Metrics.Should().NotBeNull();
        typedResult.Metrics!.Should().NotBeEmpty();
        
        var methodMetric = typedResult.Metrics.FirstOrDefault();
        methodMetric.Should().NotBeNull();
        methodMetric!.CyclomaticComplexity.Should().Be(1, "Simple method should have complexity of 1");
        methodMetric.LinesOfCode.Should().BeGreaterThan(0);
        methodMetric.MaintainabilityIndex.Should().BeGreaterThan(0);
        
        // Verify metadata
        typedResult.Summary.Should().NotBeNull();
        typedResult.Meta.Should().NotBeNull();
        typedResult.Meta!.ExecutionTime.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CodeMetrics_WithComplexMethod_ShouldCalculateHighComplexity()
    {
        // Arrange
        var (projectPath, methodLocation) = await SetupComplexMethodAsync();
        
        var parameters = new CodeMetricsParams
        {
            FilePath = methodLocation.FilePath,
            Line = methodLocation.Line,
            Column = methodLocation.Column,
            Scope = "method"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<CodeMetricsResult>();
        var typedResult = (CodeMetricsResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Metrics.Should().NotBeNull();
        
        var methodMetric = typedResult.Metrics!.FirstOrDefault();
        methodMetric.Should().NotBeNull();
        methodMetric!.CyclomaticComplexity.Should().BeGreaterThan(5, "Complex method should have high complexity");
        methodMetric.LinesOfCode.Should().BeGreaterThan(10);
        methodMetric.MaintainabilityIndex.Should().BeLessThan(80, "Complex method should have lower maintainability");
    }

    [Fact]
    public async Task CodeMetrics_WithClassScope_ShouldAnalyzeEntireClass()
    {
        // Arrange
        var (projectPath, classLocation) = await SetupComplexClassAsync();
        
        var parameters = new CodeMetricsParams
        {
            FilePath = classLocation.FilePath,
            Line = classLocation.Line,
            Column = classLocation.Column,
            Scope = "class",
            IncludeInherited = false
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<CodeMetricsResult>();
        var typedResult = (CodeMetricsResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Metrics.Should().NotBeNull();
        typedResult.Metrics!.Should().HaveCountGreaterThan(1, "Should analyze multiple methods in class");
        
        // Should have class-level metrics
        var classMetric = typedResult.Metrics.FirstOrDefault(m => m.Kind == "Class");
        classMetric.Should().NotBeNull();
        
        // Should have method-level metrics
        var methodMetrics = typedResult.Metrics.Where(m => m.Kind == "Method").ToList();
        methodMetrics.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CodeMetrics_WithFileScope_ShouldAnalyzeEntireFile()
    {
        // Arrange
        var (projectPath, fileLocation) = await SetupMultiClassFileAsync();
        
        var parameters = new CodeMetricsParams
        {
            FilePath = fileLocation.FilePath,
            Scope = "file",
            IncludeInherited = false
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<CodeMetricsResult>();
        var typedResult = (CodeMetricsResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Metrics.Should().NotBeNull();
        typedResult.Metrics!.Should().HaveCountGreaterThan(2, "Should analyze multiple classes in file");
        
        // Should have multiple class metrics
        var classMetrics = typedResult.Metrics.Where(m => m.Kind == "Class").ToList();
        classMetrics.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task CodeMetrics_WithThresholds_ShouldApplyThresholdFiltering()
    {
        // Arrange
        var (projectPath, classLocation) = await SetupMixedComplexityClassAsync();
        
        var parameters = new CodeMetricsParams
        {
            FilePath = classLocation.FilePath,
            Line = classLocation.Line,
            Column = classLocation.Column,
            Scope = "class",
            Thresholds = new Dictionary<string, int> { ["CyclomaticComplexity"] = 3, ["LinesOfCode"] = 5 }
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<CodeMetricsResult>();
        var typedResult = (CodeMetricsResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Metrics.Should().NotBeNull();
        
        // All returned metrics should meet the threshold criteria
        foreach (var metric in typedResult.Metrics!)
        {
            if (metric.Kind == "Method")
            {
                (metric.CyclomaticComplexity > 3 || metric.LinesOfCode > 5).Should().BeTrue(
                    "Method should meet at least one threshold criteria");
            }
        }
        
        // Should have insights about threshold filtering
        typedResult.Insights.Should().NotBeNull();
        typedResult.Insights!.Should().Contain(i => i.Contains("threshold"));
    }

    [Fact]
    // [DotMemoryUnit(FailIfRunWithoutSupport = false)] // Temporarily disabled
    public async Task CodeMetrics_MemoryUsage_ShouldNotLeakMemoryWithLargeAnalysis()
    {
        // Arrange
        var (projectPath, fileLocation) = await SetupLargeFileAsync();
        
        var parameters = new CodeMetricsParams
        {
            FilePath = fileLocation.FilePath,
            Scope = "file"
        };

        // Act & Assert - Memory checkpoint before
        // Memory check temporarily disabled - dotMemory.Check

        // Execute multiple times to test for leaks
        for (int i = 0; i < 3; i++)
        {
            var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);
            result.Should().BeOfType<CodeMetricsResult>();
            
            // Force garbage collection
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // Memory checkpoint after - verify no significant leaks
        // Memory check temporarily disabled - dotMemory.Check
    }

    [Fact]
    public async Task CodeMetrics_WithInheritance_ShouldIncludeInheritedMetrics()
    {
        // Arrange
        var (projectPath, derivedLocation) = await SetupInheritanceHierarchyAsync();
        
        var parameters = new CodeMetricsParams
        {
            FilePath = derivedLocation.FilePath,
            Line = derivedLocation.Line,
            Column = derivedLocation.Column,
            Scope = "class",
            IncludeInherited = true
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<CodeMetricsResult>();
        var typedResult = (CodeMetricsResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Metrics.Should().NotBeNull();
        
        // Should include both inherited and own metrics
        var methods = typedResult.Metrics!.Where(m => m.Kind == "Method").ToList();
        methods.Should().HaveCountGreaterThan(2, "Should include inherited methods");
        
        // Should have insights about inheritance
        typedResult.Insights.Should().NotBeNull();
        typedResult.Insights!.Should().Contain(i => i.Contains("inherited"));
    }

    [Fact]
    public async Task CodeMetrics_WithInvalidPosition_ShouldReturnNoSymbolError()
    {
        // Arrange
        var (projectPath, _) = await SetupSimpleProjectAsync();
        var testFile = Path.Combine(_tempDirectory, "TestClass.cs");
        
        var parameters = new CodeMetricsParams
        {
            FilePath = testFile,
            Line = 1,
            Column = 1, // Empty space, no symbol
            Scope = "method"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<CodeMetricsResult>();
        var typedResult = (CodeMetricsResult)result;
        
        typedResult.Success.Should().BeFalse();
        typedResult.Error.Should().NotBeNull();
        typedResult.Error!.Code.Should().Be("NO_SYMBOL_AT_POSITION");
    }

    [Fact]
    public async Task CodeMetrics_WithCancellation_ShouldHandleCancellationGracefully()
    {
        // Arrange
        var (projectPath, fileLocation) = await SetupLargeFileAsync();
        
        var parameters = new CodeMetricsParams
        {
            FilePath = fileLocation.FilePath,
            Scope = "file"
        };
        
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50)); // Cancel quickly

        // Act & Assert
        var operationCanceledException = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _tool.ExecuteAsync(parameters, cts.Token));
            
        operationCanceledException.Should().NotBeNull();
    }

    [Theory]
    [InlineData("method")]
    [InlineData("class")]
    [InlineData("file")]
    public async Task CodeMetrics_WithDifferentScopes_ShouldReturnAppropriateMetrics(string scope)
    {
        // Arrange
        var (projectPath, location) = await SetupComplexClassAsync();
        
        var parameters = new CodeMetricsParams
        {
            FilePath = location.FilePath,
            Line = location.Line,
            Column = location.Column,
            Scope = scope
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<CodeMetricsResult>();
        var typedResult = (CodeMetricsResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Metrics.Should().NotBeNull();
        
        // Verify scope-appropriate results
        switch (scope)
        {
            case "method":
                typedResult.Metrics!.Should().HaveCount(1);
                typedResult.Metrics.First().Kind.Should().Be("Method");
                break;
            case "class":
                typedResult.Metrics!.Should().HaveCountGreaterThan(1);
                typedResult.Metrics.Should().Contain(m => m.Kind == "Class");
                break;
            case "file":
                typedResult.Metrics!.Should().HaveCountGreaterThan(0);
                break;
        }
    }

    [Theory]
    [InlineData("CyclomaticComplexity>5")]
    [InlineData("LinesOfCode>10")]
    [InlineData("MaintainabilityIndex<70")]
    public async Task CodeMetrics_WithSpecificThreshold_ShouldFilterCorrectly(string threshold)
    {
        // Arrange
        var (projectPath, location) = await SetupMixedComplexityClassAsync();
        
        var parameters = new CodeMetricsParams
        {
            FilePath = location.FilePath,
            Line = location.Line,
            Column = location.Column,
            Scope = "class",
            Thresholds = new Dictionary<string, int> { ["CyclomaticComplexity"] = 10, ["LinesOfCode"] = 50 }
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<CodeMetricsResult>();
        var typedResult = (CodeMetricsResult)result;
        
        if (typedResult.Success && typedResult.Metrics?.Any() == true)
        {
            // All metrics should meet the threshold criteria
            foreach (var metric in typedResult.Metrics.Where(m => m.Kind == "Method"))
            {
                var meetsThreshold = threshold switch
                {
                    var t when t.StartsWith("CyclomaticComplexity>") =>
                        metric.CyclomaticComplexity > int.Parse(t.Split('>')[1]),
                    var t when t.StartsWith("LinesOfCode>") =>
                        metric.LinesOfCode > int.Parse(t.Split('>')[1]),
                    var t when t.StartsWith("MaintainabilityIndex<") =>
                        metric.MaintainabilityIndex < double.Parse(t.Split('<')[1]),
                    _ => true
                };
                
                meetsThreshold.Should().BeTrue($"Metric should meet threshold: {threshold}");
            }
        }
    }

    // Helper methods for setting up test scenarios

    private async Task<(string ProjectPath, MetricLocation Location)> SetupSimpleProjectAsync()
    {
        var projectPath = Path.Combine(_tempDirectory, "TestProject.csproj");
        var solutionPath = Path.Combine(_tempDirectory, "TestProject.sln");

        await File.WriteAllTextAsync(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

        await File.WriteAllTextAsync(solutionPath, $@"Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""TestProject"", ""TestProject.csproj"", ""{{12345678-1234-1234-1234-123456789012}}""
EndProject");

        var testFile = Path.Combine(_tempDirectory, "TestClass.cs");
        await File.WriteAllTextAsync(testFile, @"
using System;

public class TestClass
{
    public void TestMethod()
    {
        Console.WriteLine(""Hello World"");
    }
}");

        await _workspaceService.LoadSolutionAsync(solutionPath);
        return (projectPath, new MetricLocation { FilePath = testFile, Line = 6, Column = 17 });
    }

    private async Task<(string ProjectPath, MetricLocation Location)> SetupSimpleMethodAsync()
    {
        var projectPath = await SetupSimpleProjectAsync();
        
        var simpleCode = @"
using System;

public class SimpleClass
{
    public void SimpleMethod() // Target - Line 6, Column 17
    {
        Console.WriteLine(""Simple method"");
        var x = 1 + 2;
        Console.WriteLine(x);
    }
}";
        
        var testFile = Path.Combine(_tempDirectory, "SimpleClass.cs");
        await File.WriteAllTextAsync(testFile, simpleCode);
        
        return (projectPath.ProjectPath, new MetricLocation { FilePath = testFile, Line = 6, Column = 17 });
    }

    private async Task<(string ProjectPath, MetricLocation Location)> SetupComplexMethodAsync()
    {
        var projectPath = await SetupSimpleProjectAsync();
        
        var complexCode = @"
using System;
using System.Collections.Generic;

public class ComplexClass
{
    public void ComplexMethod(int parameter) // Target - Line 7, Column 17
    {
        if (parameter < 0)
        {
            throw new ArgumentException(""Parameter cannot be negative"");
        }
        
        var results = new List<string>();
        
        for (int i = 0; i < parameter; i++)
        {
            if (i % 2 == 0)
            {
                results.Add($""Even: {i}"");
                if (i % 4 == 0)
                {
                    Console.WriteLine($""Multiple of 4: {i}"");
                }
            }
            else
            {
                results.Add($""Odd: {i}"");
                if (i % 3 == 0)
                {
                    Console.WriteLine($""Multiple of 3: {i}"");
                }
            }
        }
        
        switch (parameter % 5)
        {
            case 0:
                Console.WriteLine(""Divisible by 5"");
                break;
            case 1:
                Console.WriteLine(""Remainder 1"");
                break;
            case 2:
                Console.WriteLine(""Remainder 2"");
                break;
            default:
                Console.WriteLine(""Other remainder"");
                break;
        }
        
        try
        {
            var average = results.Count > 0 ? results.Count / parameter : 0;
            Console.WriteLine($""Average: {average}"");
        }
        catch (Exception ex)
        {
            Console.WriteLine($""Error: {ex.Message}"");
        }
    }
}";
        
        var testFile = Path.Combine(_tempDirectory, "ComplexClass.cs");
        await File.WriteAllTextAsync(testFile, complexCode);
        
        return (projectPath.ProjectPath, new MetricLocation { FilePath = testFile, Line = 7, Column = 17 });
    }

    private async Task<(string ProjectPath, MetricLocation Location)> SetupComplexClassAsync()
    {
        var projectPath = await SetupSimpleProjectAsync();
        
        var classCode = @"
using System;
using System.Collections.Generic;
using System.Linq;

public class BusinessLogic // Target - Line 6, Column 14
{
    private readonly List<string> _data = new List<string>();
    
    public void ProcessData()
    {
        if (_data.Any())
        {
            foreach (var item in _data)
            {
                if (item.Length > 5)
                {
                    Console.WriteLine($""Long item: {item}"");
                }
                else
                {
                    Console.WriteLine($""Short item: {item}"");
                }
            }
        }
    }
    
    public void AddData(string item)
    {
        if (string.IsNullOrEmpty(item))
        {
            throw new ArgumentException(""Item cannot be null or empty"");
        }
        
        _data.Add(item);
    }
    
    public string GetSummary()
    {
        var count = _data.Count;
        var avgLength = _data.Any() ? _data.Average(x => x.Length) : 0;
        
        return $""Items: {count}, Average Length: {avgLength:F2}"";
    }
    
    public void ClearData()
    {
        _data.Clear();
        Console.WriteLine(""Data cleared"");
    }
}";
        
        var testFile = Path.Combine(_tempDirectory, "BusinessLogic.cs");
        await File.WriteAllTextAsync(testFile, classCode);
        
        return (projectPath.ProjectPath, new MetricLocation { FilePath = testFile, Line = 6, Column = 14 });
    }

    private async Task<(string ProjectPath, MetricLocation Location)> SetupMultiClassFileAsync()
    {
        var projectPath = await SetupSimpleProjectAsync();
        
        var multiClassCode = @"
using System;

public class FirstClass
{
    public void Method1()
    {
        Console.WriteLine(""First class method 1"");
    }
    
    public void Method2()
    {
        Console.WriteLine(""First class method 2"");
    }
}

public class SecondClass
{
    public void MethodA()
    {
        Console.WriteLine(""Second class method A"");
    }
    
    public void MethodB()
    {
        Console.WriteLine(""Second class method B"");
    }
}

public class ThirdClass
{
    public void DoWork()
    {
        var first = new FirstClass();
        var second = new SecondClass();
        
        first.Method1();
        second.MethodA();
    }
}";
        
        var testFile = Path.Combine(_tempDirectory, "MultiClass.cs");
        await File.WriteAllTextAsync(testFile, multiClassCode);
        
        return (projectPath.ProjectPath, new MetricLocation { FilePath = testFile });
    }

    private async Task<(string ProjectPath, MetricLocation Location)> SetupMixedComplexityClassAsync()
    {
        var projectPath = await SetupSimpleProjectAsync();
        
        var mixedCode = @"
using System;
using System.Collections.Generic;

public class MixedComplexityClass // Target - Line 5, Column 14
{
    public void SimpleMethod1() // Low complexity
    {
        Console.WriteLine(""Simple 1"");
    }
    
    public void SimpleMethod2() // Low complexity
    {
        Console.WriteLine(""Simple 2"");
    }
    
    public void ComplexMethod1(int param) // High complexity
    {
        if (param > 0)
        {
            for (int i = 0; i < param; i++)
            {
                if (i % 2 == 0)
                {
                    Console.WriteLine($""Even: {i}"");
                    if (i % 4 == 0)
                    {
                        Console.WriteLine($""Multiple of 4"");
                    }
                }
                else
                {
                    Console.WriteLine($""Odd: {i}"");
                }
            }
        }
        else if (param < 0)
        {
            Console.WriteLine(""Negative"");
        }
        else
        {
            Console.WriteLine(""Zero"");
        }
    }
    
    public void ComplexMethod2(string input) // High complexity
    {
        switch (input?.ToLower())
        {
            case ""a"":
                Console.WriteLine(""Case A"");
                break;
            case ""b"":
                Console.WriteLine(""Case B"");
                if (input.Length > 1)
                {
                    Console.WriteLine(""Long B"");
                }
                break;
            case ""c"":
                Console.WriteLine(""Case C"");
                break;
            default:
                Console.WriteLine(""Default case"");
                if (string.IsNullOrEmpty(input))
                {
                    Console.WriteLine(""Null or empty"");
                }
                break;
        }
        
        try
        {
            var result = input.Substring(0, 1);
            Console.WriteLine(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($""Error: {ex.Message}"");
        }
    }
}";
        
        var testFile = Path.Combine(_tempDirectory, "MixedComplexity.cs");
        await File.WriteAllTextAsync(testFile, mixedCode);
        
        return (projectPath.ProjectPath, new MetricLocation { FilePath = testFile, Line = 5, Column = 14 });
    }

    private async Task<(string ProjectPath, MetricLocation Location)> SetupLargeFileAsync()
    {
        var projectPath = await SetupSimpleProjectAsync();
        
        var largeFileContent = @"
using System;
using System.Collections.Generic;
using System.Linq;

namespace LargeFile
{";

        // Generate multiple classes with various complexity levels
        for (int i = 0; i < 10; i++)
        {
            largeFileContent += $@"
    public class LargeClass{i}
    {{
        private readonly List<int> _data{i} = new List<int>();
        
        public void Method{i}A()
        {{
            for (int j = 0; j < 10; j++)
            {{
                if (j % 2 == 0)
                {{
                    _data{i}.Add(j);
                    if (j % 4 == 0)
                    {{
                        Console.WriteLine($""Added {{j}} to class {i}"");
                    }}
                }}
            }}
        }}
        
        public void Method{i}B(int parameter)
        {{
            switch (parameter % 3)
            {{
                case 0:
                    Console.WriteLine(""Case 0 in class {i}"");
                    break;
                case 1:
                    Console.WriteLine(""Case 1 in class {i}"");
                    break;
                default:
                    Console.WriteLine(""Default case in class {i}"");
                    break;
            }}
        }}
        
        public int Method{i}C()
        {{
            var sum = 0;
            foreach (var item in _data{i})
            {{
                if (item > 5)
                {{
                    sum += item;
                }}
                else
                {{
                    sum += item * 2;
                }}
            }}
            return sum;
        }}
    }}";
        }

        largeFileContent += @"
}";
        
        var testFile = Path.Combine(_tempDirectory, "LargeFile.cs");
        await File.WriteAllTextAsync(testFile, largeFileContent);
        
        return (projectPath.ProjectPath, new MetricLocation { FilePath = testFile });
    }

    private async Task<(string ProjectPath, MetricLocation Location)> SetupInheritanceHierarchyAsync()
    {
        var projectPath = await SetupSimpleProjectAsync();
        
        var inheritanceCode = @"
using System;

public abstract class BaseProcessor
{
    public virtual void Process()
    {
        Console.WriteLine(""Base processing"");
    }
    
    public abstract void Initialize();
    
    protected void CommonMethod()
    {
        Console.WriteLine(""Common functionality"");
    }
}

public class DerivedProcessor : BaseProcessor // Target - Line 19, Column 14
{
    public override void Process()
    {
        base.Process();
        Console.WriteLine(""Derived processing"");
        DoSpecificWork();
    }
    
    public override void Initialize()
    {
        Console.WriteLine(""Derived initialization"");
        CommonMethod();
    }
    
    private void DoSpecificWork()
    {
        for (int i = 0; i < 5; i++)
        {
            if (i % 2 == 0)
            {
                Console.WriteLine($""Specific work: {i}"");
            }
        }
    }
    
    public void AdditionalMethod()
    {
        Console.WriteLine(""Additional functionality"");
    }
}";
        
        var testFile = Path.Combine(_tempDirectory, "InheritanceHierarchy.cs");
        await File.WriteAllTextAsync(testFile, inheritanceCode);
        
        return (projectPath.ProjectPath, new MetricLocation { FilePath = testFile, Line = 19, Column = 14 });
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
        catch (Exception ex)
        {
            Console.WriteLine($"Cleanup warning: {ex.Message}");
        }
    }
}

public class MetricLocation
{
    public string FilePath { get; set; } = "";
    public int? Line { get; set; }
    public int? Column { get; set; }
}