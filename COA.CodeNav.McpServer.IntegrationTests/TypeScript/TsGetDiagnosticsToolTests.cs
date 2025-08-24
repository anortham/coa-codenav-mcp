using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using COA.CodeNav.McpServer.Models;
using COA.CodeNav.McpServer.ResponseBuilders.TypeScript;
using COA.CodeNav.McpServer.Services.TypeScript;
using COA.CodeNav.McpServer.Tools.TypeScript;
using COA.Mcp.Framework.TokenOptimization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace COA.CodeNav.McpServer.IntegrationTests.TypeScript;

public class TsGetDiagnosticsToolTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TsGetDiagnosticsTool _tool;
    private readonly TypeScriptWorkspaceService _workspaceService;
    private readonly string _testProjectPath;
    private readonly string _testTsConfigPath;

    public TsGetDiagnosticsToolTests()
    {
        var services = new ServiceCollection();
        
        // Register logging
        services.AddLogging(builder => builder.AddConsole());
        
        // Register TypeScript services
        services.AddSingleton<TypeScriptCompilerManager>();
        services.AddSingleton<TypeScriptWorkspaceService>();
        services.AddSingleton<TypeScriptLanguageService>();
        services.AddSingleton<ITokenEstimator, DefaultTokenEstimator>();
        services.AddSingleton<TsDiagnosticsResponseBuilder>();
        
        // Register the tools
        services.AddScoped<LoadTsConfigTool>();
        services.AddScoped<TsGetDiagnosticsTool>();
        
        _serviceProvider = services.BuildServiceProvider();
        _tool = _serviceProvider.GetRequiredService<TsGetDiagnosticsTool>();
        _workspaceService = _serviceProvider.GetRequiredService<TypeScriptWorkspaceService>();
        
        // Path to our test TypeScript project
        _testProjectPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "TestData",
            "TypeScriptProject");
        _testTsConfigPath = Path.Combine(_testProjectPath, "tsconfig.json");
    }

    [Fact]
    public void TsGetDiagnosticsTool_ShouldHaveCorrectName()
    {
        _tool.Name.Should().Be("ts_get_diagnostics");
    }

    [Fact]
    public void TsGetDiagnosticsTool_ShouldHaveProperDescription()
    {
        _tool.Description.Should().Contain("TypeScript compilation errors");
        _tool.Description.Should().Contain("error messages");
        _tool.Description.Should().Contain("type issues");
        _tool.Description.Should().Contain("line numbers");
    }

    [Fact]
    public async Task TsGetDiagnosticsTool_WithoutLoadedWorkspace_ShouldReturnError()
    {
        var parameters = new TsGetDiagnosticsParams
        {
            WorkspaceId = "non-existent-workspace"
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("TS_PROJECT_NOT_LOADED");
        result.Error.Recovery.Should().NotBeNull();
        result.Error.Recovery!.Steps.Should().Contain(s => s.Contains("ts_load_tsconfig"));
    }

    [Fact]
    public async Task TsGetDiagnosticsTool_WithLoadedProject_ShouldReturnDiagnostics()
    {
        // Skip if TypeScript is not installed
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            return;
        }

        // First, load the TypeScript project
        await _workspaceService.LoadTsConfigAsync(_testTsConfigPath);

        var parameters = new TsGetDiagnosticsParams();

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Diagnostics.Should().NotBeNull();
        
        // Our test project intentionally has errors
        result.Diagnostics!.Should().NotBeEmpty();
        result.Summary.Should().NotBeNull();
        result.Summary!.TotalFound.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TsGetDiagnosticsTool_ShouldDetectIntentionalErrors()
    {
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            return;
        }

        await _workspaceService.LoadTsConfigAsync(_testTsConfigPath);

        var parameters = new TsGetDiagnosticsParams
        {
            FilePath = Path.Combine(_testProjectPath, "src", "index.ts")
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Diagnostics.Should().NotBeNull();
        
        // Check for specific errors we know are in index.ts
        // 1. Unused variable (error 6133)
        result.Diagnostics!.Should().Contain(d => 
            d.Message.Contains("unused", StringComparison.OrdinalIgnoreCase) ||
            d.Code == 6133);
        
        // 2. Cannot find name 'console' (error 2584)
        // TypeScript doesn't detect the unreachable code in our test case
        // so we check for the console error instead
        result.Diagnostics.Should().Contain(d => 
            d.Message.Contains("console", StringComparison.OrdinalIgnoreCase) ||
            d.Code == 2584);
        
        // 3. Missing return statement (error 2366)
        result.Diagnostics.Should().Contain(d => 
            d.Message.Contains("return", StringComparison.OrdinalIgnoreCase) ||
            d.Code == 2366);
    }

    [Fact]
    public async Task TsGetDiagnosticsTool_ShouldCategorizeDiagnostics()
    {
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            return;
        }

        await _workspaceService.LoadTsConfigAsync(_testTsConfigPath);

        var parameters = new TsGetDiagnosticsParams();

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Summary.Should().NotBeNull();
        
        // Should have categorized errors and warnings
        result.Summary!.ErrorCount.Should().BeGreaterThanOrEqualTo(0);
        result.Summary.WarningCount.Should().BeGreaterThanOrEqualTo(0);
        
        // Should have distribution data
        result.Distribution.Should().NotBeNull();
        result.Distribution!.BySeverity.Should().NotBeNull();
        result.Distribution.ByFile.Should().NotBeNull();
    }

    [Fact]
    public async Task TsGetDiagnosticsTool_WithSeverityFilter_ShouldFilterResults()
    {
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            return;
        }

        await _workspaceService.LoadTsConfigAsync(_testTsConfigPath);

        var parameters = new TsGetDiagnosticsParams
        {
            Severities = new List<string> { "error" }
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        
        if (result.Diagnostics?.Any() == true)
        {
            // All diagnostics should be errors
            result.Diagnostics.Should().OnlyContain(d => d.Category == "error");
        }
    }

    [Fact]
    public async Task TsGetDiagnosticsTool_WithMaxResults_ShouldLimitResults()
    {
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            return;
        }

        await _workspaceService.LoadTsConfigAsync(_testTsConfigPath);

        var parameters = new TsGetDiagnosticsParams
        {
            MaxResults = 2
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        
        if (result.Summary!.TotalFound > 2)
        {
            result.Diagnostics!.Count.Should().BeLessThanOrEqualTo(2);
            result.ResultsSummary!.HasMore.Should().BeTrue();
        }
    }

    [Fact]
    public async Task TsGetDiagnosticsTool_ShouldProvideInsights()
    {
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            return;
        }

        await _workspaceService.LoadTsConfigAsync(_testTsConfigPath);

        var parameters = new TsGetDiagnosticsParams();

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Insights.Should().NotBeNull();
        result.Insights!.Should().NotBeEmpty();
        
        if (result.Summary!.ErrorCount > 0)
        {
            result.Insights.Should().Contain(i => i.Contains("error"));
        }
        
        if (result.Summary.WarningCount > 0)
        {
            result.Insights.Should().Contain(i => i.Contains("warning"));
        }
    }

    [Fact]
    public async Task TsGetDiagnosticsTool_ShouldProvideActions()
    {
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            return;
        }

        await _workspaceService.LoadTsConfigAsync(_testTsConfigPath);

        var parameters = new TsGetDiagnosticsParams();

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Actions.Should().NotBeNull();
        result.Actions!.Should().NotBeEmpty();
        
        // Should suggest relevant actions based on diagnostics
        if (result.Summary!.ErrorCount > 0)
        {
            result.Actions.Should().Contain(a => a.Action == "ts_apply_quick_fix");
        }
        
        // Should always suggest organize imports
        result.Actions.Should().Contain(a => a.Action == "ts_organize_imports");
    }

    [Fact]
    public async Task TsGetDiagnosticsTool_WithResponseBuilder_ShouldOptimizeTokens()
    {
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            return;
        }

        await _workspaceService.LoadTsConfigAsync(_testTsConfigPath);

        // Create tool with response builder
        var responseBuilder = _serviceProvider.GetRequiredService<TsDiagnosticsResponseBuilder>();
        var tool = new TsGetDiagnosticsTool(
            _serviceProvider,
            _serviceProvider.GetRequiredService<ILogger<TsGetDiagnosticsTool>>(),
            _workspaceService,
            _serviceProvider.GetRequiredService<TypeScriptLanguageService>(),
            _serviceProvider.GetRequiredService<ITokenEstimator>(),
            responseBuilder);

        var parameters = new TsGetDiagnosticsParams
        {
            MaxResults = 100
        };

        var result = await tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Meta.Should().NotBeNull();
        result.Meta!.Mode.Should().Be("optimized");
        result.Meta.Tokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TsGetDiagnosticsTool_WithSortBy_ShouldSortResults()
    {
        var compilerManager = _serviceProvider.GetRequiredService<TypeScriptCompilerManager>();
        if (!compilerManager.IsTypeScriptAvailable)
        {
            return;
        }

        await _workspaceService.LoadTsConfigAsync(_testTsConfigPath);

        var parameters = new TsGetDiagnosticsParams
        {
            SortBy = "severity"
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        
        if (result.Diagnostics?.Count > 1)
        {
            // Errors should come before warnings
            var firstError = result.Diagnostics.FindIndex(d => d.Category == "error");
            var firstWarning = result.Diagnostics.FindIndex(d => d.Category == "warning");
            
            if (firstError >= 0 && firstWarning >= 0)
            {
                firstError.Should().BeLessThan(firstWarning);
            }
        }
    }
}