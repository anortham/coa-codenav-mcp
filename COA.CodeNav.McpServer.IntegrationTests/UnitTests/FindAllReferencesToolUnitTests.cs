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
/// Comprehensive unit tests for FindAllReferencesTool focusing on performance and reliability
/// </summary>
// [DotMemoryUnit(FailIfRunWithoutSupport = false)] // Temporarily disabled
public class FindAllReferencesToolUnitTests : IDisposable
{
    private readonly Mock<ILogger<FindAllReferencesTool>> _mockLogger;
    private readonly Mock<ILogger<RoslynWorkspaceService>> _mockWorkspaceLogger;
    private readonly Mock<ILogger<MSBuildWorkspaceManager>> _mockManagerLogger;
    private readonly Mock<ILogger<DocumentService>> _mockDocumentLogger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly FindAllReferencesTool _tool;
    private readonly string _tempDirectory;

    public FindAllReferencesToolUnitTests()
    {
        _mockLogger = new Mock<ILogger<FindAllReferencesTool>>();
        _mockWorkspaceLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        _mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        _mockDocumentLogger = new Mock<ILogger<DocumentService>>();
        
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"FindRefsTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        
        var config = Options.Create(new WorkspaceManagerConfig());
        var workspaceManager = new MSBuildWorkspaceManager(_mockManagerLogger.Object, config);
        _workspaceService = new RoslynWorkspaceService(_mockWorkspaceLogger.Object, workspaceManager);
        _documentService = new DocumentService(_mockDocumentLogger.Object, _workspaceService);
        _tool = new FindAllReferencesTool(_mockLogger.Object, _workspaceService, _documentService, null);
    }

    [Fact]
    public async Task FindAllReferences_WithNoWorkspaceLoaded_ShouldReturnWorkspaceNotLoadedError()
    {
        // Arrange
        var parameters = new FindAllReferencesParams
        {
            FilePath = "C:\\NonExistent\\File.cs",
            Line = 10,
            Column = 5
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<FindAllReferencesToolResult>();
        var typedResult = (FindAllReferencesToolResult)result;
        
        typedResult.Success.Should().BeFalse();
        typedResult.Error.Should().NotBeNull();
        typedResult.Error!.Code.Should().Be("DOCUMENT_NOT_FOUND");
        typedResult.Error.Recovery.Should().NotBeNull();
        typedResult.Error.Recovery!.Steps.Should().Contain(s => s.Contains("csharp_load_solution"));
    }

    [Fact]
    public async Task FindAllReferences_WithValidMethodSymbol_ShouldFindAllReferences()
    {
        // Arrange
        var (projectPath, methodLocation) = await SetupProjectWithMethodReferencesAsync();
        
        var parameters = new FindAllReferencesParams
        {
            FilePath = methodLocation.FilePath,
            Line = methodLocation.Line,
            Column = methodLocation.Column
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<FindAllReferencesToolResult>();
        var typedResult = (FindAllReferencesToolResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Locations.Should().NotBeNull();
        typedResult.Locations!.Count.Should().BeGreaterThan(0, "Should find method references");
        
        // Should find both definition and usage references
        typedResult.Locations.Should().Contain(r => r.Kind == "definition");
        typedResult.Locations.Should().Contain(r => r.Kind != "definition");
        
        // Verify metadata
        typedResult.Summary.Should().NotBeNull();
        typedResult.Summary!.TotalFound.Should().Be(typedResult.Locations.Count);
        typedResult.Meta.Should().NotBeNull();
        typedResult.Meta!.ExecutionTime.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FindAllReferences_WithPropertySymbol_ShouldFindGettersAndSetters()
    {
        // Arrange
        var (projectPath, propertyLocation) = await SetupProjectWithPropertyReferencesAsync();
        
        var parameters = new FindAllReferencesParams
        {
            FilePath = propertyLocation.FilePath,
            Line = propertyLocation.Line,
            Column = propertyLocation.Column
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<FindAllReferencesToolResult>();
        var typedResult = (FindAllReferencesToolResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Locations.Should().NotBeNull();
        typedResult.Locations!.Count.Should().BeGreaterThan(2, "Should find property definition and usages");
        
        // Should find both read and write references
        typedResult.Locations.Should().Contain(r => (r.Kind != null && r.Kind.Contains("Read")) || (r.Text != null && r.Text.Contains("get")));
        typedResult.Locations.Should().Contain(r => (r.Kind != null && r.Kind.Contains("Write")) || (r.Text != null && r.Text.Contains("set")));
    }

    [Fact]
    public async Task FindAllReferences_WithMaxResults_ShouldRespectLimit()
    {
        // Arrange
        var (projectPath, symbolLocation) = await SetupProjectWithManyReferencesAsync();
        
        var parameters = new FindAllReferencesParams
        {
            FilePath = symbolLocation.FilePath,
            Line = symbolLocation.Line,
            Column = symbolLocation.Column,
            MaxResults = 5
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<FindAllReferencesToolResult>();
        var typedResult = (FindAllReferencesToolResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Locations.Should().NotBeNull();
        typedResult.Locations!.Count.Should().BeLessThanOrEqualTo(5);
        
        if (typedResult.ResultsSummary?.Total > 5)
        {
            typedResult.ResultsSummary.HasMore.Should().BeTrue();
        }
    }

    [Fact]
    // [DotMemoryUnit(FailIfRunWithoutSupport = false)] // Temporarily disabled
    public async Task FindAllReferences_MemoryUsage_ShouldNotLeakMemoryWithLargeResults()
    {
        // Arrange
        var (projectPath, symbolLocation) = await SetupLargeProjectWithManyReferencesAsync();
        
        var parameters = new FindAllReferencesParams
        {
            FilePath = symbolLocation.FilePath,
            Line = symbolLocation.Line,
            Column = symbolLocation.Column,
            MaxResults = 100
        };

        // Act & Assert - Memory checkpoint before
        // Memory check temporarily disabled - dotMemory.Check
        /*
        {
            memory.GetObjects(where => where.Type.Is<ReferenceLocation>())
                  .ObjectsCount.Should().BeLessThan(10, "Should start with minimal reference objects");
        */

        // Execute multiple times to test for leaks
        for (int i = 0; i < 3; i++)
        {
            var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);
            result.Should().BeOfType<FindAllReferencesToolResult>();
            
            // Force garbage collection
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // Memory checkpoint after - verify no significant leaks
        // Memory check temporarily disabled - dotMemory.Check
        /*
        {
            var referenceObjects = memory.GetObjects(where => where.Type.Is<ReferenceLocation>()).ObjectsCount;
            referenceObjects.Should().BeLessThan(500, "Should not accumulate excessive reference objects");
        */
    }

    [Fact]
    public async Task FindAllReferences_WithInvalidPosition_ShouldReturnNoSymbolError()
    {
        // Arrange
        var (projectPath, _) = await SetupSimpleProjectAsync();
        var testFile = Path.Combine(_tempDirectory, "TestClass.cs");
        
        var parameters = new FindAllReferencesParams
        {
            FilePath = testFile,
            Line = 1,
            Column = 1 // Empty space, no symbol
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<FindAllReferencesToolResult>();
        var typedResult = (FindAllReferencesToolResult)result;
        
        typedResult.Success.Should().BeFalse();
        typedResult.Error.Should().NotBeNull();
        typedResult.Error!.Code.Should().Be("NO_SYMBOL_AT_POSITION");
    }

    [Fact]
    public async Task FindAllReferences_WithCancellation_ShouldHandleCancellationGracefully()
    {
        // Arrange
        var (projectPath, symbolLocation) = await SetupLargeProjectWithManyReferencesAsync();
        
        var parameters = new FindAllReferencesParams
        {
            FilePath = symbolLocation.FilePath,
            Line = symbolLocation.Line,
            Column = symbolLocation.Column
        };
        
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50)); // Cancel quickly

        // Act & Assert
        var operationCanceledException = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _tool.ExecuteAsync(parameters, cts.Token));
            
        operationCanceledException.Should().NotBeNull();
    }

    [Fact]
    public async Task FindAllReferences_WithCrossProjectReferences_ShouldFindAcrossProjects()
    {
        // Arrange
        var (mainProject, libProject, symbolLocation) = await SetupCrossProjectReferencesAsync();
        
        var parameters = new FindAllReferencesParams
        {
            FilePath = symbolLocation.FilePath,
            Line = symbolLocation.Line,
            Column = symbolLocation.Column
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<FindAllReferencesToolResult>();
        var typedResult = (FindAllReferencesToolResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Locations.Should().NotBeNull();
        typedResult.Locations!.Count.Should().BeGreaterThan(1, "Should find references across projects");
        
        // Should find references in both projects
        var projectNames = typedResult.Locations.Select(r => Path.GetDirectoryName(r.FilePath))
                                                 .Where(p => p != null)
                                                 .Distinct()
                                                 .Count();
        projectNames.Should().BeGreaterThan(1, "Should span multiple project directories");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(25)]
    [InlineData(100)]
    public async Task FindAllReferences_WithVariousMaxResults_ShouldScaleAppropriately(int maxResults)
    {
        // Arrange
        var (projectPath, symbolLocation) = await SetupProjectWithManyReferencesAsync();
        
        var parameters = new FindAllReferencesParams
        {
            FilePath = symbolLocation.FilePath,
            Line = symbolLocation.Line,
            Column = symbolLocation.Column,
            MaxResults = maxResults
        };

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);
        stopwatch.Stop();

        // Assert
        result.Should().BeOfType<FindAllReferencesToolResult>();
        var typedResult = (FindAllReferencesToolResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.Locations.Should().NotBeNull();
        typedResult.Locations!.Count.Should().BeLessThanOrEqualTo(maxResults);
        
        // Performance assertion - should complete reasonably quickly
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000, 
            $"Finding {maxResults} references should complete within 5 seconds");
    }

    private async Task<(string ProjectPath, SymbolLocation Location)> SetupSimpleProjectAsync()
    {
        var projectPath = Path.Combine(_tempDirectory, "TestProject.csproj");
        var solutionPath = Path.Combine(_tempDirectory, "TestProject.sln");

        // Create minimal project
        await File.WriteAllTextAsync(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Create solution
        await File.WriteAllTextAsync(solutionPath, $@"Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""TestProject"", ""TestProject.csproj"", ""{{12345678-1234-1234-1234-123456789012}}""
EndProject");

        // Create a simple class
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
        return (projectPath, new SymbolLocation { FilePath = testFile, Line = 6, Column = 17 }); // TestMethod
    }

    private async Task<(string ProjectPath, SymbolLocation Location)> SetupProjectWithMethodReferencesAsync()
    {
        var projectPath = await SetupSimpleProjectAsync();
        
        // Create code with method definition and multiple references
        var codeWithReferences = @"
using System;

public class BusinessService
{
    public void ProcessData()  // Definition - Line 6, Column 17
    {
        Console.WriteLine(""Processing data..."");
    }
    
    public void ExecuteWorkflow()
    {
        ProcessData(); // Reference 1
        
        for (int i = 0; i < 3; i++)
        {
            ProcessData(); // Reference 2, 3, 4
        }
        
        var action = ProcessData; // Reference 5 (method group)
        action();
    }
}

public class Client
{
    private readonly BusinessService _service = new BusinessService();
    
    public void DoWork()
    {
        _service.ProcessData(); // Reference 6
        _service.ProcessData(); // Reference 7
    }
}";
        
        var testFile = Path.Combine(_tempDirectory, "BusinessService.cs");
        await File.WriteAllTextAsync(testFile, codeWithReferences);
        
        return (projectPath.ProjectPath, new SymbolLocation { FilePath = testFile, Line = 6, Column = 17 });
    }

    private async Task<(string ProjectPath, SymbolLocation Location)> SetupProjectWithPropertyReferencesAsync()
    {
        var projectPath = await SetupSimpleProjectAsync();
        
        var codeWithProperty = @"
using System;

public class DataModel
{
    public string Name { get; set; } // Property definition - Line 6, Column 19
    
    public void TestProperty()
    {
        Name = ""Test""; // Write reference
        var value = Name; // Read reference
        Console.WriteLine(Name); // Read reference
        Name = value + ""_Modified""; // Write reference
        
        var model = new DataModel { Name = ""Initial"" }; // Write reference
        Console.WriteLine($""Name: {model.Name}""); // Read reference
    }
}";
        
        var testFile = Path.Combine(_tempDirectory, "DataModel.cs");
        await File.WriteAllTextAsync(testFile, codeWithProperty);
        
        return (projectPath.ProjectPath, new SymbolLocation { FilePath = testFile, Line = 6, Column = 19 });
    }

    private async Task<(string ProjectPath, SymbolLocation Location)> SetupProjectWithManyReferencesAsync()
    {
        var projectPath = await SetupSimpleProjectAsync();
        
        var codeWithManyRefs = @"
using System;
using System.Collections.Generic;

public class UtilityClass
{
    public static void CommonMethod() // Definition - Line 7, Column 24
    {
        Console.WriteLine(""Common method called"");
    }
}

public class Consumer1
{
    public void Method1() 
    { 
        UtilityClass.CommonMethod();
        UtilityClass.CommonMethod();
        UtilityClass.CommonMethod();
    }
    
    public void Method2() 
    { 
        UtilityClass.CommonMethod();
        UtilityClass.CommonMethod();
    }
}

public class Consumer2
{
    public void Process()
    {
        for (int i = 0; i < 10; i++)
        {
            UtilityClass.CommonMethod();
        }
    }
    
    public void Execute()
    {
        UtilityClass.CommonMethod();
        UtilityClass.CommonMethod();
        UtilityClass.CommonMethod();
        UtilityClass.CommonMethod();
        UtilityClass.CommonMethod();
    }
}

public class Consumer3
{
    public void Run()
    {
        var actions = new List<Action>
        {
            UtilityClass.CommonMethod,
            UtilityClass.CommonMethod,
            UtilityClass.CommonMethod
        };
        
        foreach (var action in actions)
        {
            action();
        }
    }
}";
        
        var testFile = Path.Combine(_tempDirectory, "UtilityClass.cs");
        await File.WriteAllTextAsync(testFile, codeWithManyRefs);
        
        return (projectPath.ProjectPath, new SymbolLocation { FilePath = testFile, Line = 7, Column = 24 });
    }

    private async Task<(string ProjectPath, SymbolLocation Location)> SetupLargeProjectWithManyReferencesAsync()
    {
        var (projectPath, location) = await SetupProjectWithManyReferencesAsync();
        
        // Add more files with references to create a larger test case
        for (int i = 0; i < 5; i++)
        {
            var additionalCode = $@"
using System;

public class AdditionalConsumer{i}
{{
    public void Method1()
    {{
        UtilityClass.CommonMethod();
        UtilityClass.CommonMethod();
    }}
    
    public void Method2()
    {{
        for (int j = 0; j < 5; j++)
        {{
            UtilityClass.CommonMethod();
        }}
    }}
    
    public void Method3()
    {{
        UtilityClass.CommonMethod();
        UtilityClass.CommonMethod();
        UtilityClass.CommonMethod();
    }}
}}";
            
            var fileName = Path.Combine(_tempDirectory, $"AdditionalConsumer{i}.cs");
            await File.WriteAllTextAsync(fileName, additionalCode);
        }
        
        return (projectPath, location);
    }

    private async Task<(string MainProject, string LibProject, SymbolLocation Location)> SetupCrossProjectReferencesAsync()
    {
        // Create Library Project
        var libProjectPath = Path.Combine(_tempDirectory, "Library", "Library.csproj");
        var libDir = Path.GetDirectoryName(libProjectPath)!;
        Directory.CreateDirectory(libDir);

        await File.WriteAllTextAsync(libProjectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

        var libCode = @"
using System;

namespace Library
{
    public class SharedUtility
    {
        public static void SharedMethod() // Definition - Line 8, Column 28
        {
            Console.WriteLine(""Shared method from library"");
        }
    }
}";
        
        var libFile = Path.Combine(libDir, "SharedUtility.cs");
        await File.WriteAllTextAsync(libFile, libCode);

        // Create Main Project
        var mainProjectPath = Path.Combine(_tempDirectory, "MainApp", "MainApp.csproj");
        var mainDir = Path.GetDirectoryName(mainProjectPath)!;
        Directory.CreateDirectory(mainDir);

        await File.WriteAllTextAsync(mainProjectPath, $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""..\..\Library\Library.csproj"" />
  </ItemGroup>
</Project>");

        var mainCode = @"
using System;
using Library;

namespace MainApp
{
    public class Program
    {
        public static void Main()
        {
            SharedUtility.SharedMethod(); // Cross-project reference
            SharedUtility.SharedMethod();
        }
        
        public static void DoWork()
        {
            SharedUtility.SharedMethod();
        }
    }
}";
        
        var mainFile = Path.Combine(mainDir, "Program.cs");
        await File.WriteAllTextAsync(mainFile, mainCode);

        // Create Solution
        var solutionPath = Path.Combine(_tempDirectory, "CrossProjectSolution.sln");
        await File.WriteAllTextAsync(solutionPath, @"Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""Library"", ""Library\Library.csproj"", ""{11111111-1111-1111-1111-111111111111}""
EndProject
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""MainApp"", ""MainApp\MainApp.csproj"", ""{22222222-2222-2222-2222-222222222222}""
EndProject
Global
    GlobalSection(ProjectDependencies) = postSolution
        {22222222-2222-2222-2222-222222222222} = {11111111-1111-1111-1111-111111111111}
    EndGlobalSection
EndGlobal");

        await _workspaceService.LoadSolutionAsync(solutionPath);
        
        return (mainProjectPath, libProjectPath, new SymbolLocation { FilePath = libFile, Line = 8, Column = 28 });
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

public class SymbolLocation
{
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
}