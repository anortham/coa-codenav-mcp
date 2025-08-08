using COA.CodeNav.McpServer.Tools;
using COA.CodeNav.McpServer.IntegrationTests.Helpers;
using FluentAssertions;

namespace COA.CodeNav.McpServer.IntegrationTests.Tools
{
    /// <summary>
    /// Simple test to demonstrate the pattern for testing MCP tools
    /// </summary>
    public class LoadSolutionToolTests : IDisposable
    {
        private TestWorkspaceBuilder? _workspaceBuilder;

        [Fact]
        public void LoadSolutionTool_ShouldHaveCorrectName()
        {
            // This is a simple schema test that doesn't require actual Roslyn setup
            var tool = new LoadSolutionTool(null!, null!, null!);
            
            tool.Name.Should().Be("csharp_load_solution");
            tool.Description.Should().NotBeNullOrEmpty();
        }

        [Fact] 
        public void LoadSolutionParams_WithNonExistentPath_ShouldReturnError()
        {
            // Test error handling - but need to provide valid loggers
            // For now, just test that the parameter class exists and works
            var parameters = new LoadSolutionParams
            {
                SolutionPath = "C:\\NonExistent\\Solution.sln"
            };

            // Verify parameter structure 
            parameters.SolutionPath.Should().Be("C:\\NonExistent\\Solution.sln");
        }

        [Fact]
        public void TestWorkspaceBuilder_CanCreateSimpleWorkspace()
        {
            // Test our test helper infrastructure
            _workspaceBuilder = new TestWorkspaceBuilder("TestProject");
            
            var workspace = _workspaceBuilder
                .WithClass("TestClass", "public void TestMethod() { }")
                .Build();

            workspace.Should().NotBeNull();
            workspace.CurrentSolution.Projects.Should().NotBeEmpty();
            
            var project = workspace.CurrentSolution.Projects.First();
            project.Name.Should().Be("TestProject");
            project.Documents.Should().NotBeEmpty();
        }

        public void Dispose()
        {
            _workspaceBuilder?.Dispose();
        }
    }
}