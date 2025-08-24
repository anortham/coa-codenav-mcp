using COA.CodeNav.McpServer.Configuration;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Tools;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

namespace COA.CodeNav.McpServer.IntegrationTests;

/// <summary>
/// Integration test that verifies code fixes work with a real workspace
/// </summary>
public class ApplyCodeFixIntegrationTest
{
    [Fact]
    public async Task ApplyCodeFix_WithUnusedVariable_ShouldOfferRemovalFix()
    {
        // This test creates a simple in-memory workspace to test code fixes
        
        // Arrange
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        
        // Create a project with a document containing an unused variable
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "TestProject",
            "TestProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest));

        var solution = workspace.CurrentSolution
            .AddProject(projectInfo)
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(Console).Assembly.Location));

        var sourceText = SourceText.From(@"
using System;

using Moq;

public class TestClass
{
    public void TestMethod()
    {
        int unusedVariable = 42; // CS0219: Variable is assigned but its value is never used
        Console.WriteLine(""Hello"");
    }
}");

        solution = solution.AddDocument(documentId, "Test.cs", sourceText, filePath: "C:\\Test\\Test.cs");
        workspace.TryApplyChanges(solution);

        // Create services
        var workspaceManager = new MSBuildWorkspaceManager(
            NullLogger<MSBuildWorkspaceManager>.Instance,
            Options.Create(new WorkspaceManagerConfig()));
            
        var workspaceService = new RoslynWorkspaceService(
            NullLogger<RoslynWorkspaceService>.Instance,
            workspaceManager);
            
        var documentService = new DocumentService(
            NullLogger<DocumentService>.Instance,
            workspaceService);
            
        var codeFixService = new CodeFixService(
            NullLogger<CodeFixService>.Instance);
            
        var tool = new ApplyCodeFixTool(
            TestServiceProvider.Create(),
            NullLogger<ApplyCodeFixTool>.Instance,
            workspaceService,
            documentService,
            codeFixService,
            null!);

        // We need to inject the test workspace into the services
        // This is a bit hacky but works for testing
        var document = workspace.CurrentSolution.GetDocument(documentId);
        
        // Get diagnostics from the document
        var semanticModel = await document!.GetSemanticModelAsync();
        var diagnostics = semanticModel!.GetDiagnostics();
        
        // Find the CS0219 diagnostic
        var unusedVarDiagnostic = diagnostics.FirstOrDefault(d => d.Id == "CS0219");
        unusedVarDiagnostic.Should().NotBeNull("Should find CS0219 diagnostic for unused variable");

        // Get code fixes for this diagnostic
        var fixes = await codeFixService.GetCodeFixesAsync(document, new[] { unusedVarDiagnostic! }, CancellationToken.None);
        
        // Assert
        fixes.Should().NotBeEmpty("Should find code fixes for unused variable");
        
        // Log what fixes we found for debugging
        foreach (var fix in fixes)
        {
            Console.WriteLine($"Found fix: {fix.action.Title} for {fix.diagnosticId}");
        }
        
        // Verify we have a fix to remove the unused variable
        fixes.Should().Contain(f => 
            f.action.Title.Contains("Remove", StringComparison.OrdinalIgnoreCase) ||
            f.action.Title.Contains("unused", StringComparison.OrdinalIgnoreCase),
            "Should have a fix to remove unused variable");
    }
}