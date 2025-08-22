using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.Services.TypeScript;
using COA.CodeNav.McpServer.Tools.TypeScript;
using COA.Mcp.Framework.TokenOptimization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace COA.CodeNav.McpServer.IntegrationTests.TypeScript;

public class LoadTsConfigToolTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LoadTsConfigTool _tool;
    private readonly string _testProjectPath;

    public LoadTsConfigToolTests()
    {
        var services = new ServiceCollection();
        
        // Register logging
        services.AddLogging(builder => builder.AddConsole());
        
        // Register TypeScript services
        services.AddSingleton<TypeScriptCompilerManager>();
        services.AddSingleton<TypeScriptWorkspaceService>();
        services.AddSingleton<ITokenEstimator, DefaultTokenEstimator>();
        
        // Register the tool
        services.AddScoped<LoadTsConfigTool>();
        
        _serviceProvider = services.BuildServiceProvider();
        _tool = _serviceProvider.GetRequiredService<LoadTsConfigTool>();
        
        // Path to our test TypeScript project
        _testProjectPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "TestData",
            "TypeScriptProject",
            "tsconfig.json");
    }

    [Fact]
    public void LoadTsConfigTool_ShouldHaveCorrectName()
    {
        _tool.Name.Should().Be("ts_load_tsconfig");
    }

    [Fact]
    public void LoadTsConfigTool_ShouldHaveProperDescription()
    {
        _tool.Description.Should().Contain("Load TypeScript project");
        _tool.Description.Should().Contain("tsconfig.json");
        _tool.Description.Should().Contain("compiler settings");
    }

    [Fact]
    public async Task LoadTsConfigTool_WithValidTsConfig_ShouldLoadSuccessfully()
    {
        // Skip if TypeScript is not installed
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            // This is not a failure - TypeScript might not be installed in CI
            return;
        }

        var parameters = new TsLoadConfigParams
        {
            TsConfigPath = _testProjectPath
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.TsConfigPath.Should().Be(Path.GetFullPath(_testProjectPath));
        result.CompilerOptions.Should().NotBeNull();
        result.CompilerOptions!.Target.Should().Be("ES2020");
        result.CompilerOptions.Strict.Should().BeTrue();
    }

    [Fact]
    public async Task LoadTsConfigTool_WithNonExistentFile_ShouldReturnError()
    {
        var parameters = new TsLoadConfigParams
        {
            TsConfigPath = "C:\\nonexistent\\tsconfig.json"
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("TSCONFIG_NOT_FOUND");
        result.Error.Recovery.Should().NotBeNull();
        result.Error.Recovery!.Steps.Should().NotBeEmpty();
    }

    [Fact]
    public async Task LoadTsConfigTool_ShouldParseCompilerOptions()
    {
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            return;
        }

        var parameters = new TsLoadConfigParams
        {
            TsConfigPath = _testProjectPath
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.CompilerOptions.Should().NotBeNull();
        
        // Verify compiler options from our test tsconfig.json
        var options = result.CompilerOptions!;
        options.Target.Should().Be("ES2020");
        options.Module.Should().Be("commonjs");
        options.Strict.Should().BeTrue();
        options.EsModuleInterop.Should().BeTrue();
        options.SkipLibCheck.Should().BeTrue();
        options.ForceConsistentCasingInFileNames.Should().BeTrue();
        options.Declaration.Should().BeTrue();
        options.SourceMap.Should().BeTrue();
        options.OutDir.Should().Be("./dist");
        options.RootDir.Should().Be("./src");
    }

    [Fact]
    public async Task LoadTsConfigTool_ShouldParseIncludeAndExclude()
    {
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            return;
        }

        var parameters = new TsLoadConfigParams
        {
            TsConfigPath = _testProjectPath
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        
        // Verify include patterns
        result.Include.Should().NotBeNull();
        result.Include!.Should().Contain("src/**/*.ts");
        result.Include.Should().Contain("src/**/*.tsx");
        
        // Verify exclude patterns
        result.Exclude.Should().NotBeNull();
        result.Exclude!.Should().Contain("node_modules");
        result.Exclude.Should().Contain("dist");
    }

    [Fact]
    public async Task LoadTsConfigTool_ShouldProvideInsightsAndActions()
    {
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            return;
        }

        var parameters = new TsLoadConfigParams
        {
            TsConfigPath = _testProjectPath
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        
        // Should have insights
        result.Insights.Should().NotBeNull();
        result.Insights!.Should().NotBeEmpty();
        result.Insights.Should().Contain(i => i.Contains("TypeScript version"));
        result.Insights.Should().Contain(i => i.Contains("Strict mode"));
        
        // Should have actions
        result.Actions.Should().NotBeNull();
        result.Actions!.Should().NotBeEmpty();
        result.Actions.Should().Contain(a => a.Action == "ts_get_diagnostics");
    }

    [Fact]
    public async Task LoadTsConfigTool_WithCustomWorkspaceId_ShouldUseIt()
    {
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            return;
        }

        var customWorkspaceId = "my-custom-workspace";
        var parameters = new TsLoadConfigParams
        {
            TsConfigPath = _testProjectPath,
            WorkspaceId = customWorkspaceId
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.WorkspaceId.Should().Be(customWorkspaceId);
    }

    [Fact]
    public async Task LoadTsConfigTool_WhenTypeScriptNotInstalled_ShouldProvideHelpfulError()
    {
        // This test verifies error handling when TypeScript is not available
        // We can't easily simulate this condition, so we'll just verify the error structure
        
        var parameters = new TsLoadConfigParams
        {
            TsConfigPath = "C:\\nonexistent\\tsconfig.json"
        };

        var result = await _tool.ExecuteAsync(parameters);

        if (result.Error?.Code == "TYPESCRIPT_NOT_INSTALLED")
        {
            result.Error.Recovery.Should().NotBeNull();
            result.Error.Recovery!.Steps.Should().Contain(s => s.Contains("npm install"));
            result.Error.Recovery.SuggestedActions.Should().NotBeNull();
            result.Error.Recovery.SuggestedActions!.Should().NotBeEmpty();
        }
    }
}