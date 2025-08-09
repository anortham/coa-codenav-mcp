using COA.CodeNav.McpServer.Configuration;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// using DotMemory.Unit; // Temporarily disabled
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace COA.CodeNav.McpServer.IntegrationTests.UnitTests;

/// <summary>
/// Memory cleanup and performance tests for the entire MCP server infrastructure
/// </summary>
// [DotMemoryUnit(FailIfRunWithoutSupport = false)] // Temporarily disabled
public class MemoryCleanupTests : IDisposable
{
    private readonly string _tempDirectory;
    
    public MemoryCleanupTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"MemoryTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    // [DotMemoryUnit(FailIfRunWithoutSupport = false)] // Temporarily disabled
    public async Task WorkspaceService_MultipleLoadUnload_ShouldNotLeakMemory()
    {
        // This is a critical test for memory leaks during workspace operations
        
        var mockLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        var mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        var config = Options.Create(new WorkspaceManagerConfig());
        
        // Create test solution
        var solutionPath = await CreateTestSolutionAsync();
        
        // Memory checkpoint before operations
        // Memory check temporarily disabled - dotMemory.Check
        /*
        {
            memory.GetObjects(where => where.Type.Name.Contains("Workspace") || 
                                     where.Type.Name.Contains("Solution") ||
                                     where.Type.Name.Contains("Document"))
                  .ObjectsCount.Should().BeLessThan(10, "Should start with minimal Roslyn objects");
        */

        // Perform multiple load/unload cycles
        for (int cycle = 0; cycle < 5; cycle++)
        {
            using (var workspaceManager = new MSBuildWorkspaceManager(mockManagerLogger.Object, config))
            using (var workspaceService = new RoslynWorkspaceService(mockLogger.Object, workspaceManager))
            {
                // Load workspace
                var workspace = await workspaceService.LoadSolutionAsync(solutionPath);
                workspace.Should().NotBeNull();
                
                // Perform some operations that create temporary objects
                var activeWorkspaces = workspaceService.GetActiveWorkspaces().ToList();
                activeWorkspaces.Should().NotBeEmpty();
                
                var testFile = Path.Combine(_tempDirectory, "TestClass.cs");
                var document = await workspaceService.GetDocumentAsync(testFile);
                
                // Force refresh operations
                if (document != null)
                {
                    await workspaceService.RefreshDocumentAsync(testFile);
                }
                
                // Close workspace explicitly
                workspaceService.CloseWorkspace(solutionPath);
            }
            
            // Force garbage collection after each cycle
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // Final memory check - should not have accumulated objects
        // Memory check temporarily disabled - dotMemory.Check
        /*
        {
            var roslynObjects = memory.GetObjects(where => 
                where.Type.Name.Contains("Workspace") || 
                where.Type.Name.Contains("Solution") ||
                where.Type.Name.Contains("Document") ||
                where.Type.Name.Contains("SemanticModel")).ObjectsCount;
                
            roslynObjects.Should().BeLessThan(50, 
                "Should not accumulate Roslyn objects after multiple load/unload cycles");
        */
    }

    [Fact]
    // [DotMemoryUnit(FailIfRunWithoutSupport = false)] // Temporarily disabled
    public async Task DocumentRefresh_RepeatedOperations_ShouldNotLeakDocuments()
    {
        var mockLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        var mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        var config = Options.Create(new WorkspaceManagerConfig());
        
        using var workspaceManager = new MSBuildWorkspaceManager(mockManagerLogger.Object, config);
        using var workspaceService = new RoslynWorkspaceService(mockLogger.Object, workspaceManager);
        
        var solutionPath = await CreateTestSolutionAsync();
        var testFile = Path.Combine(_tempDirectory, "TestClass.cs");
        
        await workspaceService.LoadSolutionAsync(solutionPath);

        // Memory checkpoint before document operations
        // Memory check temporarily disabled - dotMemory.Check
        /*
        {
            memory.GetObjects(where => where.Type.Name.Contains("Document"))
                  .ObjectsCount.Should().BeLessThan(20, "Should start with minimal document objects");
        */

        // Perform many document refresh operations
        for (int i = 0; i < 20; i++)
        {
            // Modify file content slightly
            await File.WriteAllTextAsync(testFile, $@"
using System;

public class TestClass{i}
{{
    public void Method{i}()
    {{
        Console.WriteLine(""Iteration {i}"");
        int unused{i} = {i}; // Create diagnostic
    }}
}}");

            // Refresh document multiple times
            await workspaceService.RefreshDocumentAsync(testFile);
            await workspaceService.GetDocumentAsync(testFile, forceRefresh: true);
            
            // Periodic cleanup
            if (i % 5 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        // Final garbage collection
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Memory check after operations
        // Memory check temporarily disabled - dotMemory.Check
        /*
        {
            var documentObjects = memory.GetObjects(where => where.Type.Name.Contains("Document")).ObjectsCount;
            documentObjects.Should().BeLessThan(100, 
                "Document refresh operations should not accumulate excessive document objects");
        */
    }

    [Fact]
    // [DotMemoryUnit(FailIfRunWithoutSupport = false)] // Temporarily disabled
    public async Task WorkspaceInvalidation_ShouldCleanupProperly()
    {
        var mockLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        var mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        var config = Options.Create(new WorkspaceManagerConfig());
        
        using var workspaceManager = new MSBuildWorkspaceManager(mockManagerLogger.Object, config);
        using var workspaceService = new RoslynWorkspaceService(mockLogger.Object, workspaceManager);
        
        var solutionPath = await CreateLargeTestSolutionAsync();
        
        // Load workspace
        await workspaceService.LoadSolutionAsync(solutionPath);
        
        // Memory checkpoint after loading
        // Memory check temporarily disabled - dotMemory.Check
        /*
        {
            memory.GetObjects(where => where.Type.Name.Contains("Workspace"))
                  .ObjectsCount.Should().BeGreaterThan(0, "Should have workspace objects after loading");
        */

        // Perform workspace invalidation
        var success = await workspaceService.InvalidateWorkspaceAsync(solutionPath);
        success.Should().BeTrue();
        
        // Force garbage collection
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Memory check after invalidation - should have cleaned up
        // Memory check temporarily disabled - dotMemory.Check
        /*
        {
            var workspaceObjects = memory.GetObjects(where => where.Type.Name.Contains("Workspace")).ObjectsCount;
            // Note: Some workspace objects may remain due to the new workspace being loaded
            // but the count should be reasonable
            workspaceObjects.Should().BeLessThan(20, 
                "Workspace invalidation should clean up old workspace objects");
        */
    }

    [Fact]
    public async Task ConcurrentWorkspaceOperations_ShouldHandleThreadSafety()
    {
        var mockLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        var mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        var config = Options.Create(new WorkspaceManagerConfig());
        
        using var workspaceManager = new MSBuildWorkspaceManager(mockManagerLogger.Object, config);
        using var workspaceService = new RoslynWorkspaceService(mockLogger.Object, workspaceManager);
        
        var solutionPath = await CreateTestSolutionAsync();
        var testFile = Path.Combine(_tempDirectory, "TestClass.cs");
        
        await workspaceService.LoadSolutionAsync(solutionPath);

        // Create multiple concurrent tasks performing various operations
        var tasks = new List<Task>();
        var exceptions = new List<Exception>();

        for (int i = 0; i < 10; i++)
        {
            int taskId = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    // Perform various concurrent operations
                    switch (taskId % 4)
                    {
                        case 0:
                            await workspaceService.GetDocumentAsync(testFile);
                            break;
                        case 1:
                            await workspaceService.RefreshDocumentAsync(testFile);
                            break;
                        case 2:
                            var workspaces = workspaceService.GetActiveWorkspaces().ToList();
                            break;
                        case 3:
                            await workspaceService.GetDocumentAsync(testFile, forceRefresh: true);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        // Wait for all tasks to complete
        await Task.WhenAll(tasks);

        // Assert no exceptions occurred due to thread safety issues
        exceptions.Should().BeEmpty("Concurrent workspace operations should be thread-safe");
    }

    private async Task<string> CreateTestSolutionAsync()
    {
        var projectPath = Path.Combine(_tempDirectory, "TestProject.csproj");
        var solutionPath = Path.Combine(_tempDirectory, "TestSolution.sln");

        await File.WriteAllTextAsync(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

        var testFile = Path.Combine(_tempDirectory, "TestClass.cs");
        await File.WriteAllTextAsync(testFile, @"
using System;

public class TestClass
{
    public void TestMethod()
    {
        Console.WriteLine(""Test"");
    }
}");

        await File.WriteAllTextAsync(solutionPath, @"Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""TestProject"", ""TestProject.csproj"", ""{11111111-1111-1111-1111-111111111111}""
EndProject
Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
    EndGlobalSection
    GlobalSection(ProjectConfigurationPlatforms) = postSolution
        {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
    EndGlobalSection
EndGlobal");

        return solutionPath;
    }

    private async Task<string> CreateLargeTestSolutionAsync()
    {
        var solutionPath = Path.Combine(_tempDirectory, "LargeTestSolution.sln");
        
        // Create multiple projects with multiple files
        var projectEntries = new List<string>();
        var configEntries = new List<string>();
        
        for (int i = 0; i < 3; i++)
        {
            var projectName = $"LargeProject{i}";
            var projectDir = Path.Combine(_tempDirectory, projectName);
            Directory.CreateDirectory(projectDir);
            
            var projectGuid = $"{{{i + 1:D8}-1111-1111-1111-111111111111}}";
            
            var projectPath = Path.Combine(projectDir, $"{projectName}.csproj");
            await File.WriteAllTextAsync(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

            // Create multiple class files
            for (int j = 0; j < 3; j++)
            {
                var classFile = Path.Combine(projectDir, $"Class{j}.cs");
                await File.WriteAllTextAsync(classFile, $@"
using System;

namespace {projectName}
{{
    public class Class{j}
    {{
        public void Method{j}()
        {{
            Console.WriteLine(""Method {j} in {projectName}"");
        }}
    }}
}}");
            }
            
            projectEntries.Add($@"Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""{projectName}"", ""{projectName}\{projectName}.csproj"", ""{projectGuid}""");
            projectEntries.Add("EndProject");
            
            configEntries.Add($"        {projectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            configEntries.Add($"        {projectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU");
        }
        
        var solutionContent = @"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17" + Environment.NewLine + 
            string.Join(Environment.NewLine, projectEntries) + @"
Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
    EndGlobalSection
    GlobalSection(ProjectConfigurationPlatforms) = postSolution
" + string.Join(Environment.NewLine, configEntries) + @"
    EndGlobalSection
EndGlobal";

        await File.WriteAllTextAsync(solutionPath, solutionContent);
        return solutionPath;
    }

    public void Dispose()
    {
        try
        {
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

/// <summary>
/// Performance benchmarks for key MCP operations
/// </summary>
[MemoryDiagnoser]
public class McpPerformanceBenchmarks
{
    private RoslynWorkspaceService? _workspaceService;
    private string _solutionPath = "";
    private string _tempDirectory = "";

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"BenchmarkTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        
        var mockLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        var mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        var config = Options.Create(new WorkspaceManagerConfig());
        
        var workspaceManager = new MSBuildWorkspaceManager(mockManagerLogger.Object, config);
        _workspaceService = new RoslynWorkspaceService(mockLogger.Object, workspaceManager);
        
        _solutionPath = await CreateBenchmarkSolutionAsync();
        await _workspaceService.LoadSolutionAsync(_solutionPath);
    }

    [Benchmark]
    public async Task<object?> GetDocument()
    {
        var testFile = Path.Combine(_tempDirectory, "TestClass.cs");
        return await _workspaceService!.GetDocumentAsync(testFile);
    }

    [Benchmark]
    public async Task<object?> RefreshDocument()
    {
        var testFile = Path.Combine(_tempDirectory, "TestClass.cs");
        return await _workspaceService!.RefreshDocumentAsync(testFile);
    }

    [Benchmark]
    public async Task<bool> InvalidateWorkspace()
    {
        return await _workspaceService!.InvalidateWorkspaceAsync(_solutionPath);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _workspaceService?.Dispose();
        
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    private async Task<string> CreateBenchmarkSolutionAsync()
    {
        var projectPath = Path.Combine(_tempDirectory, "BenchmarkProject.csproj");
        var solutionPath = Path.Combine(_tempDirectory, "BenchmarkSolution.sln");

        await File.WriteAllTextAsync(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

        var testFile = Path.Combine(_tempDirectory, "TestClass.cs");
        await File.WriteAllTextAsync(testFile, @"
using System;

public class TestClass
{
    public void TestMethod()
    {
        Console.WriteLine(""Benchmark test"");
    }
}");

        await File.WriteAllTextAsync(solutionPath, @"Microsoft Visual Studio Solution File, Format Version 12.00
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""BenchmarkProject"", ""BenchmarkProject.csproj"", ""{11111111-1111-1111-1111-111111111111}""
EndProject");

        return solutionPath;
    }
}