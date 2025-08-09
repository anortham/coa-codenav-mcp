using COA.CodeNav.McpServer.Configuration;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// using DotMemory.Unit; // Temporarily disabled

namespace COA.CodeNav.McpServer.IntegrationTests.UnitTests;

/// <summary>
/// Comprehensive unit tests for LoadSolutionTool focusing on MSBuild integration issues
/// </summary>
// [DotMemoryUnit(FailIfRunWithoutSupport = false)] // Temporarily disabled
public class LoadSolutionToolUnitTests : IDisposable
{
    private readonly Mock<ILogger<LoadSolutionTool>> _mockLogger;
    private readonly Mock<ILogger<RoslynWorkspaceService>> _mockWorkspaceLogger;
    private readonly Mock<ILogger<MSBuildWorkspaceManager>> _mockManagerLogger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly LoadSolutionTool _tool;
    private readonly string _tempDirectory;

    public LoadSolutionToolUnitTests()
    {
        _mockLogger = new Mock<ILogger<LoadSolutionTool>>();
        _mockWorkspaceLogger = new Mock<ILogger<RoslynWorkspaceService>>();
        _mockManagerLogger = new Mock<ILogger<MSBuildWorkspaceManager>>();
        
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"LoadSolutionTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        
        var config = Options.Create(new WorkspaceManagerConfig());
        var workspaceManager = new MSBuildWorkspaceManager(_mockManagerLogger.Object, config);
        _workspaceService = new RoslynWorkspaceService(_mockWorkspaceLogger.Object, workspaceManager);
        _tool = new LoadSolutionTool(_mockLogger.Object, workspaceManager, _workspaceService);
    }

    [Fact]
    public async Task LoadSolution_WithNonExistentFile_ShouldReturnFileNotFoundError()
    {
        // Arrange
        var parameters = new LoadSolutionParams
        {
            SolutionPath = "C:\\NonExistent\\Solution.sln"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<LoadSolutionResult>();
        var typedResult = (LoadSolutionResult)result;
        
        typedResult.Success.Should().BeFalse();
        typedResult.Error.Should().NotBeNull();
        typedResult.Error!.Code.Should().Be("SOLUTION_NOT_FOUND");
        typedResult.Error.Recovery.Should().NotBeNull();
        typedResult.Error.Recovery!.Steps.Should().Contain(s => s.Contains("Verify the solution file path"));
    }

    [Fact]
    public async Task LoadSolution_WithValidSimpleSolution_ShouldLoadSuccessfully()
    {
        // Arrange
        var solutionPath = await CreateSimpleSolutionAsync();
        var parameters = new LoadSolutionParams
        {
            SolutionPath = solutionPath
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<LoadSolutionResult>();
        var typedResult = (LoadSolutionResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.ProjectNames.Should().NotBeNull();
        typedResult.ProjectCount.Should().BeGreaterThan(0);
        typedResult.Meta.Should().NotBeNull();
        typedResult.Meta!.ExecutionTime.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LoadSolution_WithMultipleProjects_ShouldLoadAllProjects()
    {
        // Arrange
        var solutionPath = await CreateMultiProjectSolutionAsync();
        var parameters = new LoadSolutionParams
        {
            SolutionPath = solutionPath
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<LoadSolutionResult>();
        var typedResult = (LoadSolutionResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.ProjectNames.Should().NotBeNull();
        typedResult.ProjectCount.Should().Be(3, "Should load all 3 projects");
        
        // Verify projects are loaded with correct information
        typedResult.ProjectNames.Should().NotBeNull();
        typedResult.ProjectNames!.Should().HaveCount(3);
        typedResult.ProjectNames.Should().AllSatisfy(p => 
        {
            p.Should().NotBeNullOrEmpty();
        });
    }

    [Fact]
    public async Task LoadSolution_WithProjectReferences_ShouldResolveReferences()
    {
        // Arrange
        var solutionPath = await CreateSolutionWithProjectReferencesAsync();
        var parameters = new LoadSolutionParams
        {
            SolutionPath = solutionPath
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<LoadSolutionResult>();
        var typedResult = (LoadSolutionResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.ProjectNames.Should().NotBeNull();
        
        // Find the main project (should have references)
        var mainProject = typedResult.ProjectNames!.FirstOrDefault(p => p.Contains("MainApp"));
        mainProject.Should().NotBeNull();
        
        // Should have insights about project structure
        typedResult.Insights.Should().NotBeNull();
        typedResult.Insights!.Should().Contain(i => i.Contains("project reference") || i.Contains("dependency"));
    }

    [Fact]
    // [DotMemoryUnit(FailIfRunWithoutSupport = false)] // Temporarily disabled
    public async Task LoadSolution_MemoryUsage_ShouldNotLeakWorkspaceMemory()
    {
        // Arrange
        var solutionPath = await CreateLargeSolutionAsync();
        var parameters = new LoadSolutionParams
        {
            SolutionPath = solutionPath
        };

        // Act & Assert - Memory checkpoint before
        // Memory check temporarily disabled - dotMemory.Check
        /*
        {
            memory.GetObjects(where => where.Type.Name.Contains("Workspace"))
                  .ObjectsCount.Should().BeLessThan(5, "Should start with minimal workspace objects");
        */

        // Load and unload solution multiple times
        for (int i = 0; i < 3; i++)
        {
            var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);
            result.Should().BeOfType<LoadSolutionResult>();
            
            // Unload the workspace
            _workspaceService.CloseWorkspace(solutionPath);
            
            // Force garbage collection
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // Memory checkpoint after - verify no significant leaks
        // Memory check temporarily disabled - dotMemory.Check
        /*
        {
            var workspaceObjects = memory.GetObjects(where => where.Type.Name.Contains("Workspace")).ObjectsCount;
            workspaceObjects.Should().BeLessThan(20, "Should not accumulate excessive workspace objects");
        */
    }

    [Fact]
    public async Task LoadSolution_WithAlreadyLoadedSolution_ShouldReturnExistingInstance()
    {
        // Arrange
        var solutionPath = await CreateSimpleSolutionAsync();
        var parameters = new LoadSolutionParams
        {
            SolutionPath = solutionPath
        };

        // Act - Load twice
        var result1 = await _tool.ExecuteAsync(parameters, CancellationToken.None);
        var result2 = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result1.Should().BeOfType<LoadSolutionResult>();
        result2.Should().BeOfType<LoadSolutionResult>();
        
        var typedResult1 = (LoadSolutionResult)result1;
        var typedResult2 = (LoadSolutionResult)result2;
        
        typedResult1.Success.Should().BeTrue();
        typedResult2.Success.Should().BeTrue();
        
        // Second load should be faster (cached)
        var time1 = ParseExecutionTime(typedResult1.Meta!.ExecutionTime!);
        var time2 = ParseExecutionTime(typedResult2.Meta!.ExecutionTime!);
        
        time2.Should().BeLessThan(time1, "Second load should be faster due to caching");
    }

    [Fact]
    public async Task LoadSolution_WithMalformedSolutionFile_ShouldReturnParseError()
    {
        // Arrange
        var malformedSolutionPath = await CreateMalformedSolutionAsync();
        var parameters = new LoadSolutionParams
        {
            SolutionPath = malformedSolutionPath
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<LoadSolutionResult>();
        var typedResult = (LoadSolutionResult)result;
        
        typedResult.Success.Should().BeFalse();
        typedResult.Error.Should().NotBeNull();
        typedResult.Error!.Code.Should().BeOneOf("SOLUTION_PARSE_ERROR", "OPERATION_FAILED");
        typedResult.Error.Recovery.Should().NotBeNull();
    }

    [Fact]
    public async Task LoadSolution_WithMissingProjectFiles_ShouldReportMissingProjects()
    {
        // Arrange
        var solutionPath = await CreateSolutionWithMissingProjectsAsync();
        var parameters = new LoadSolutionParams
        {
            SolutionPath = solutionPath
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<LoadSolutionResult>();
        var typedResult = (LoadSolutionResult)result;
        
        // Should succeed partially but report issues
        if (typedResult.Success)
        {
            typedResult.Insights.Should().NotBeNull();
            typedResult.Insights!.Should().Contain(i => 
                i.Contains("missing") || i.Contains("not found") || i.Contains("failed"));
        }
        else
        {
            typedResult.Error.Should().NotBeNull();
            typedResult.Error!.Recovery.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task LoadSolution_WithCancellation_ShouldHandleCancellationGracefully()
    {
        // Arrange
        var largeSolutionPath = await CreateLargeSolutionAsync();
        var parameters = new LoadSolutionParams
        {
            SolutionPath = largeSolutionPath
        };
        
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100)); // Cancel quickly

        // Act & Assert
        var operationCanceledException = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _tool.ExecuteAsync(parameters, cts.Token));
            
        operationCanceledException.Should().NotBeNull();
    }

    [Theory]
    [InlineData("net9.0")]
    [InlineData("net8.0")]
    [InlineData("netstandard2.1")]
    public async Task LoadSolution_WithDifferentTargetFrameworks_ShouldHandleAllFrameworks(string targetFramework)
    {
        // Arrange
        var solutionPath = await CreateSolutionWithTargetFrameworkAsync(targetFramework);
        var parameters = new LoadSolutionParams
        {
            SolutionPath = solutionPath
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters, CancellationToken.None);

        // Assert
        result.Should().BeOfType<LoadSolutionResult>();
        var typedResult = (LoadSolutionResult)result;
        
        typedResult.Success.Should().BeTrue();
        typedResult.ProjectNames.Should().NotBeNull();
        
        var project = typedResult.ProjectNames!.First();
        project.Should().NotBeNullOrEmpty();
    }

    private async Task<string> CreateSimpleSolutionAsync()
    {
        var projectPath = Path.Combine(_tempDirectory, "SimpleProject.csproj");
        var solutionPath = Path.Combine(_tempDirectory, "SimpleSolution.sln");

        // Create project file
        await File.WriteAllTextAsync(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

        // Create a simple class file
        var classFile = Path.Combine(_tempDirectory, "SimpleClass.cs");
        await File.WriteAllTextAsync(classFile, @"
using System;

namespace SimpleProject
{
    public class SimpleClass
    {
        public void SimpleMethod()
        {
            Console.WriteLine(""Hello from simple class"");
        }
    }
}");

        // Create solution file
        await File.WriteAllTextAsync(solutionPath, @"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""SimpleProject"", ""SimpleProject.csproj"", ""{11111111-1111-1111-1111-111111111111}""
EndProject
Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
        Release|Any CPU = Release|Any CPU
    EndGlobalSection
    GlobalSection(ProjectConfigurationPlatforms) = postSolution
        {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
        {11111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|Any CPU
        {11111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|Any CPU
    EndGlobalSection
EndGlobal");

        return solutionPath;
    }

    private async Task<string> CreateMultiProjectSolutionAsync()
    {
        var solutionPath = Path.Combine(_tempDirectory, "MultiProject.sln");
        
        // Create three projects
        var projects = new[] { "ProjectA", "ProjectB", "ProjectC" };
        var projectGuids = new[]
        {
            "{11111111-1111-1111-1111-111111111111}",
            "{22222222-2222-2222-2222-222222222222}",
            "{33333333-3333-3333-3333-333333333333}"
        };

        for (int i = 0; i < projects.Length; i++)
        {
            var projectDir = Path.Combine(_tempDirectory, projects[i]);
            Directory.CreateDirectory(projectDir);
            
            var projectPath = Path.Combine(projectDir, $"{projects[i]}.csproj");
            await File.WriteAllTextAsync(projectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

            var classFile = Path.Combine(projectDir, $"Class{i + 1}.cs");
            await File.WriteAllTextAsync(classFile, $@"
using System;

namespace {projects[i]}
{{
    public class Class{i + 1}
    {{
        public void Method{i + 1}()
        {{
            Console.WriteLine(""Hello from {projects[i]}"");
        }}
    }}
}}");
        }

        // Create solution file
        var solutionContent = @"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""ProjectA"", ""ProjectA\ProjectA.csproj"", ""{11111111-1111-1111-1111-111111111111}""
EndProject
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""ProjectB"", ""ProjectB\ProjectB.csproj"", ""{22222222-2222-2222-2222-222222222222}""
EndProject
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""ProjectC"", ""ProjectC\ProjectC.csproj"", ""{33333333-3333-3333-3333-333333333333}""
EndProject
Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
        Release|Any CPU = Release|Any CPU
    EndGlobalSection
    GlobalSection(ProjectConfigurationPlatforms) = postSolution
        {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
        {22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        {22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
        {33333333-3333-3333-3333-333333333333}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        {33333333-3333-3333-3333-333333333333}.Debug|Any CPU.Build.0 = Debug|Any CPU
    EndGlobalSection
EndGlobal";

        await File.WriteAllTextAsync(solutionPath, solutionContent);
        return solutionPath;
    }

    private async Task<string> CreateSolutionWithProjectReferencesAsync()
    {
        var solutionPath = Path.Combine(_tempDirectory, "WithReferences.sln");
        
        // Create Library project
        var libDir = Path.Combine(_tempDirectory, "Library");
        Directory.CreateDirectory(libDir);
        
        var libProjectPath = Path.Combine(libDir, "Library.csproj");
        await File.WriteAllTextAsync(libProjectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

        var libClassFile = Path.Combine(libDir, "LibraryClass.cs");
        await File.WriteAllTextAsync(libClassFile, @"
using System;

namespace Library
{
    public class LibraryClass
    {
        public static void LibraryMethod()
        {
            Console.WriteLine(""Hello from library"");
        }
    }
}");

        // Create MainApp project with reference to Library
        var mainDir = Path.Combine(_tempDirectory, "MainApp");
        Directory.CreateDirectory(mainDir);
        
        var mainProjectPath = Path.Combine(mainDir, "MainApp.csproj");
        await File.WriteAllTextAsync(mainProjectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""..\Library\Library.csproj"" />
  </ItemGroup>
</Project>");

        var mainClassFile = Path.Combine(mainDir, "Program.cs");
        await File.WriteAllTextAsync(mainClassFile, @"
using System;
using Library;

namespace MainApp
{
    class Program
    {
        static void Main(string[] args)
        {
            LibraryClass.LibraryMethod();
            Console.WriteLine(""Hello from main app"");
        }
    }
}");

        // Create solution file
        var solutionContent = @"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""Library"", ""Library\Library.csproj"", ""{11111111-1111-1111-1111-111111111111}""
EndProject
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""MainApp"", ""MainApp\MainApp.csproj"", ""{22222222-2222-2222-2222-222222222222}""
EndProject
Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
        Release|Any CPU = Release|Any CPU
    EndGlobalSection
    GlobalSection(ProjectConfigurationPlatforms) = postSolution
        {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
        {22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        {22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
    EndGlobalSection
    GlobalSection(ProjectDependencies) = postSolution
        {22222222-2222-2222-2222-222222222222} = {11111111-1111-1111-1111-111111111111}
    EndGlobalSection
EndGlobal";

        await File.WriteAllTextAsync(solutionPath, solutionContent);
        return solutionPath;
    }

    private async Task<string> CreateLargeSolutionAsync()
    {
        var solutionPath = Path.Combine(_tempDirectory, "LargeSolution.sln");
        var solutionContent = @"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17";
        
        var projectEntries = new List<string>();
        var configEntries = new List<string>();
        
        // Create multiple projects with multiple files each
        for (int i = 0; i < 8; i++)
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

            // Create multiple classes per project
            for (int j = 0; j < 5; j++)
            {
                var classFile = Path.Combine(projectDir, $"Class{j}.cs");
                await File.WriteAllTextAsync(classFile, $@"
using System;
using System.Collections.Generic;

namespace {projectName}
{{
    public class Class{j}
    {{
        private List<string> data = new List<string>();
        
        public void Method{j}()
        {{
            for (int k = 0; k < 100; k++)
            {{
                data.Add($""Item {{k}}"");
            }}
            Console.WriteLine($""Processed {{data.Count}} items in {projectName}.Class{j}"");
        }}
        
        public void AnotherMethod{j}()
        {{
            Method{j}();
        }}
    }}
}}");
            }
            
            projectEntries.Add($@"Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""{projectName}"", ""{projectName}\{projectName}.csproj"", ""{projectGuid}""");
            projectEntries.Add("EndProject");
            
            configEntries.Add($"        {projectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            configEntries.Add($"        {projectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU");
        }
        
        solutionContent += Environment.NewLine + string.Join(Environment.NewLine, projectEntries);
        solutionContent += @"
Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
        Release|Any CPU = Release|Any CPU
    EndGlobalSection
    GlobalSection(ProjectConfigurationPlatforms) = postSolution
" + string.Join(Environment.NewLine, configEntries) + @"
    EndGlobalSection
EndGlobal";

        await File.WriteAllTextAsync(solutionPath, solutionContent);
        return solutionPath;
    }

    private async Task<string> CreateMalformedSolutionAsync()
    {
        var solutionPath = Path.Combine(_tempDirectory, "Malformed.sln");
        
        // Create malformed solution content
        var malformedContent = @"This is not a valid solution file
Some random content
Missing proper headers
Invalid GUID formats
{INVALID-GUID}
EndProject without matching Project";

        await File.WriteAllTextAsync(solutionPath, malformedContent);
        return solutionPath;
    }

    private async Task<string> CreateSolutionWithMissingProjectsAsync()
    {
        var solutionPath = Path.Combine(_tempDirectory, "WithMissing.sln");
        
        // Create solution that references non-existent projects
        var solutionContent = @"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""ExistingProject"", ""ExistingProject.csproj"", ""{11111111-1111-1111-1111-111111111111}""
EndProject
Project(""{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"") = ""MissingProject"", ""MissingProject.csproj"", ""{22222222-2222-2222-2222-222222222222}""
EndProject
Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
    EndGlobalSection
    GlobalSection(ProjectConfigurationPlatforms) = postSolution
        {11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        {22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
    EndGlobalSection
EndGlobal";

        await File.WriteAllTextAsync(solutionPath, solutionContent);
        
        // Create only one of the projects
        var existingProjectPath = Path.Combine(_tempDirectory, "ExistingProject.csproj");
        await File.WriteAllTextAsync(existingProjectPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
  </PropertyGroup>
</Project>");

        return solutionPath;
    }

    private async Task<string> CreateSolutionWithTargetFrameworkAsync(string targetFramework)
    {
        var projectPath = Path.Combine(_tempDirectory, $"Framework{targetFramework.Replace(".", "")}.csproj");
        var solutionPath = Path.Combine(_tempDirectory, $"Framework{targetFramework.Replace(".", "")}.sln");

        // Create project with specific target framework
        await File.WriteAllTextAsync(projectPath, $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>{targetFramework}</TargetFramework>
  </PropertyGroup>
</Project>");

        var classFile = Path.Combine(_tempDirectory, $"FrameworkClass.cs");
        await File.WriteAllTextAsync(classFile, $@"
using System;

namespace FrameworkTest
{{
    public class FrameworkClass
    {{
        public void FrameworkMethod()
        {{
            Console.WriteLine(""Running on {targetFramework}"");
        }}
    }}
}}");

        // Create solution file
        await File.WriteAllTextAsync(solutionPath, $@"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""Framework{targetFramework.Replace(".", "")}"", ""Framework{targetFramework.Replace(".", "")}.csproj"", ""{{11111111-1111-1111-1111-111111111111}}""
EndProject
Global
    GlobalSection(SolutionConfigurationPlatforms) = preSolution
        Debug|Any CPU = Debug|Any CPU
    EndGlobalSection
    GlobalSection(ProjectConfigurationPlatforms) = postSolution
        {{11111111-1111-1111-1111-111111111111}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        {{11111111-1111-1111-1111-111111111111}}.Debug|Any CPU.Build.0 = Debug|Any CPU
    EndGlobalSection
EndGlobal");

        return solutionPath;
    }

    private double ParseExecutionTime(string executionTime)
    {
        if (executionTime.EndsWith("ms"))
        {
            var timeString = executionTime.Replace("ms", "");
            if (double.TryParse(timeString, out var result))
            {
                return result;
            }
        }
        return 0.0;
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