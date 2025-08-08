using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace COA.CodeNav.McpServer.IntegrationTests.Helpers
{
    /// <summary>
    /// Builder for creating test workspaces with predefined or custom code
    /// </summary>
    public class TestWorkspaceBuilder
    {
        private readonly AdhocWorkspace _workspace;
        private ProjectId _projectId;
        private Solution _solution = null!;
        private readonly List<DocumentInfo> _documents = new();
        private readonly string _projectName;

        public TestWorkspaceBuilder(string projectName = "TestProject")
        {
            _workspace = new AdhocWorkspace();
            _projectName = projectName;
            _projectId = ProjectId.CreateNewId();
            
            InitializeProject();
        }

        private void InitializeProject()
        {
            var projectInfo = ProjectInfo.Create(
                _projectId,
                VersionStamp.Create(),
                _projectName,
                _projectName,
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest));

            _solution = _workspace.CurrentSolution
                .AddProject(projectInfo)
                .AddMetadataReference(_projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddMetadataReference(_projectId, MetadataReference.CreateFromFile(typeof(Console).Assembly.Location))
                .AddMetadataReference(_projectId, MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location))
                .AddMetadataReference(_projectId, MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location));
        }

        /// <summary>
        /// Adds a document with the specified content
        /// </summary>
        public TestWorkspaceBuilder WithDocument(string name, string content, string? filePath = null)
        {
            var documentId = DocumentId.CreateNewId(_projectId);
            var sourceText = SourceText.From(content);
            filePath ??= Path.Combine("C:\\TestProject", name);

            _solution = _solution.AddDocument(documentId, name, sourceText, filePath: filePath);
            return this;
        }

        /// <summary>
        /// Adds the SampleService test fixture
        /// </summary>
        public TestWorkspaceBuilder WithSampleService()
        {
            var content = File.ReadAllText("TestFixtures\\SampleService.cs");
            return WithDocument("SampleService.cs", content);
        }

        /// <summary>
        /// Adds the Repository test fixture
        /// </summary>
        public TestWorkspaceBuilder WithRepository()
        {
            var content = File.ReadAllText("TestFixtures\\Repository.cs");
            return WithDocument("Repository.cs", content);
        }

        /// <summary>
        /// Adds a simple class with the specified name and content
        /// </summary>
        public TestWorkspaceBuilder WithClass(string className, string classContent = "")
        {
            var content = $@"
using System;

namespace TestProject
{{
    public class {className}
    {{
        {classContent}
    }}
}}";
            return WithDocument($"{className}.cs", content);
        }

        /// <summary>
        /// Adds a class with an unused variable (for diagnostic testing)
        /// </summary>
        public TestWorkspaceBuilder WithUnusedVariableClass()
        {
            var content = @"
using System;

namespace TestProject
{
    public class DiagnosticTest
    {
        public void MethodWithUnusedVariable()
        {
            int unusedVariable = 42; // CS0219
            Console.WriteLine(""Hello World"");
        }
    }
}";
            return WithDocument("DiagnosticTest.cs", content);
        }

        /// <summary>
        /// Builds and returns the workspace
        /// </summary>
        public AdhocWorkspace Build()
        {
            _workspace.TryApplyChanges(_solution);
            return _workspace;
        }

        /// <summary>
        /// Gets the project from the built workspace
        /// </summary>
        public Project GetProject()
        {
            var workspace = Build();
            return workspace.CurrentSolution.GetProject(_projectId)!;
        }

        /// <summary>
        /// Gets a document by name
        /// </summary>
        public Document GetDocument(string name)
        {
            var project = GetProject();
            return project.Documents.First(d => d.Name == name);
        }

        /// <summary>
        /// Gets the first document in the project
        /// </summary>
        public Document GetFirstDocument()
        {
            var project = GetProject();
            return project.Documents.First();
        }

        public void Dispose()
        {
            _workspace?.Dispose();
        }
    }
}