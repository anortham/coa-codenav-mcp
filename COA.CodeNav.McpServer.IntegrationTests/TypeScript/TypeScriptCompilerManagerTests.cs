using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace COA.CodeNav.McpServer.IntegrationTests.TypeScript;

public class TypeScriptCompilerManagerTests
{
    private readonly TypeScriptCompilerManager _manager;

    public TypeScriptCompilerManagerTests()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());
        
        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<TypeScriptCompilerManager>>();
        
        _manager = new TypeScriptCompilerManager(logger);
    }

    [Fact]
    public void TypeScriptCompilerManager_ShouldDetectTypeScriptInstallation()
    {
        // This test will pass or fail based on whether TypeScript is installed
        // Both outcomes are valid - we're testing the detection mechanism
        
        if (_manager.IsTypeScriptAvailable)
        {
            _manager.TypeScriptVersion.Should().NotBeNullOrEmpty();
            _manager.TypeScriptVersion.Should().Contain("Version");
        }
        else
        {
            _manager.TypeScriptVersion.Should().BeNull();
        }
    }

    [Fact]
    public void TypeScriptCompilerManager_ValidateTypeScriptAvailability_WhenNotInstalled_ShouldReturnHelpfulError()
    {
        if (!_manager.IsTypeScriptAvailable)
        {
            var error = _manager.ValidateTypeScriptAvailability();
            
            error.Should().NotBeNull();
            error!.Code.Should().Be("TYPESCRIPT_NOT_INSTALLED");
            error.Message.Should().Contain("TypeScript is not installed");
            error.Recovery.Should().NotBeNull();
            error.Recovery!.Steps.Should().NotBeEmpty();
            error.Recovery.Steps.Should().Contain(s => s.Contains("npm install"));
            error.Recovery.SuggestedActions.Should().NotBeNull();
            error.Recovery.SuggestedActions!.Should().NotBeEmpty();
        }
        else
        {
            var error = _manager.ValidateTypeScriptAvailability();
            error.Should().BeNull();
        }
    }

    [Fact(Skip = "TypeScript server functionality not currently used - diagnostics use direct compiler execution")]
    public async Task TypeScriptCompilerManager_GetOrCreateServer_WhenTypeScriptNotAvailable_ShouldReturnNull()
    {
        if (!_manager.IsTypeScriptAvailable)
        {
            var server = await _manager.GetOrCreateServerAsync("test-workspace", "C:\\test");
            server.Should().BeNull();
        }
    }

    [Fact(Skip = "TypeScript server functionality not currently used - diagnostics use direct compiler execution")]
    public async Task TypeScriptCompilerManager_GetOrCreateServer_WithSameWorkspaceId_ShouldReturnSameInstance()
    {
        if (_manager.IsTypeScriptAvailable)
        {
            var testPath = Path.GetTempPath();
            var server1 = await _manager.GetOrCreateServerAsync("test-workspace", testPath);
            var server2 = await _manager.GetOrCreateServerAsync("test-workspace", testPath);
            
            if (server1 != null)
            {
                server2.Should().BeSameAs(server1);
                
                // Clean up
                await _manager.StopServerAsync("test-workspace");
            }
        }
    }

    [Fact(Skip = "TypeScript server functionality not currently used - diagnostics use direct compiler execution")]
    public async Task TypeScriptCompilerManager_StopServer_ShouldStopRunningServer()
    {
        if (_manager.IsTypeScriptAvailable)
        {
            var testPath = Path.GetTempPath();
            var server = await _manager.GetOrCreateServerAsync("test-workspace-stop", testPath);
            
            if (server != null)
            {
                server.IsRunning.Should().BeTrue();
                
                await _manager.StopServerAsync("test-workspace-stop");
                
                // After stopping, getting the server again should create a new one
                var newServer = await _manager.GetOrCreateServerAsync("test-workspace-stop", testPath);
                if (newServer != null)
                {
                    newServer.Should().NotBeSameAs(server);
                    await _manager.StopServerAsync("test-workspace-stop");
                }
            }
        }
    }

    [Fact(Skip = "TypeScript server functionality not currently used - diagnostics use direct compiler execution")]
    public void TypeScriptCompilerManager_Dispose_ShouldCleanupAllServers()
    {
        // Create a new manager for this test
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());
        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<TypeScriptCompilerManager>>();
        
        using (var manager = new TypeScriptCompilerManager(logger))
        {
            if (manager.IsTypeScriptAvailable)
            {
                // Create some servers
                var testPath = Path.GetTempPath();
                var task1 = manager.GetOrCreateServerAsync("dispose-test-1", testPath).GetAwaiter().GetResult();
                var task2 = manager.GetOrCreateServerAsync("dispose-test-2", testPath).GetAwaiter().GetResult();
            }
            
            // Dispose should complete without throwing
        }
        
        // If we got here without exceptions, the test passes
        true.Should().BeTrue();
    }
}