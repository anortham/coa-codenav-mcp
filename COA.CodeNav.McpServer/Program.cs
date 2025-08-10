using COA.CodeNav.McpServer.Caching;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Tools;
using COA.Mcp.Framework.Interfaces;
using COA.Mcp.Framework.Resources;
using COA.Mcp.Framework.Server;
using COA.Mcp.Framework.TokenOptimization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

// Configure Serilog first
var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables("CODENAV_")
    .Build();

// Setup logging
var pathResolution = new PathResolutionService(configuration);
var logDirectory = pathResolution.GetLogsPath();
pathResolution.EnsureDirectoryExists(logDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.CodeAnalysis", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Build", LogEventLevel.Warning)
    .MinimumLevel.Override("COA.CodeNav.McpServer", LogEventLevel.Debug)
    .WriteTo.File(
        path: Path.Combine(logDirectory, $"codenav-{DateTime.Now:yyyyMMdd-HHmmss}.log"),
        rollingInterval: RollingInterval.Infinite,
        fileSizeLimitBytes: 10_485_760, // 10MB per session
        retainedFileCountLimit: null, // Keep all session logs
        shared: true,
        flushToDiskInterval: TimeSpan.FromSeconds(1),
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"
    )
    .Enrich.FromLogContext()
    .CreateLogger();

// Use framework's builder
var builder = new McpServerBuilder()
    .WithServerInfo("COA CodeNav MCP Server", "2.0.0")
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSerilog(Log.Logger);
        
        // Framework handles stdio automatically - don't redirect Console.Out
        // Console.SetOut(Console.Error);
        
        // Configure logging from appsettings.json
        logging.AddConfiguration(configuration.GetSection("Logging"));
    });
    // TODO: Enable token optimization when Framework exposes the configuration API
    // The framework doesn't expose ConfigureTokenOptimization yet in v1.4.2
    // We'll implement manual token optimization service instead

// Register configuration
builder.Services.Configure<COA.CodeNav.McpServer.Configuration.WorkspaceManagerConfig>(
    configuration.GetSection("WorkspaceManager"));
builder.Services.Configure<COA.CodeNav.McpServer.Configuration.StartupConfiguration>(
    configuration.GetSection("Startup"));

// Register core services
builder.Services.AddSingleton<COA.CodeNav.McpServer.Utilities.SolutionFinder>();
builder.Services.AddSingleton<IPathResolutionService, PathResolutionService>();

// Register Token Optimization services from framework
builder.Services.AddSingleton<ITokenEstimator, DefaultTokenEstimator>();
builder.Services.AddSingleton<COA.Mcp.Framework.TokenOptimization.Intelligence.IInsightGenerator, COA.Mcp.Framework.TokenOptimization.Intelligence.InsightGenerator>();
builder.Services.AddSingleton<COA.Mcp.Framework.TokenOptimization.Actions.IActionGenerator, COA.Mcp.Framework.TokenOptimization.Actions.ActionGenerator>();
builder.Services.AddSingleton<COA.Mcp.Framework.TokenOptimization.Caching.IResponseCacheService, COA.Mcp.Framework.TokenOptimization.Caching.ResponseCacheService>();
builder.Services.AddSingleton<COA.Mcp.Framework.TokenOptimization.Storage.IResourceStorageService, COA.Mcp.Framework.TokenOptimization.Storage.ResourceStorageService>();

// Register Response Builders
builder.Services.AddSingleton<COA.CodeNav.McpServer.ResponseBuilders.SymbolSearchResponseBuilder>();
builder.Services.AddSingleton<COA.CodeNav.McpServer.ResponseBuilders.DiagnosticsResponseBuilder>();
builder.Services.AddSingleton<COA.CodeNav.McpServer.ResponseBuilders.FindAllReferencesResponseBuilder>();

// Register infrastructure services
builder.Services.AddSingleton<MSBuildWorkspaceManager>();
builder.Services.AddSingleton<RoslynWorkspaceService>();
builder.Services.AddSingleton<DocumentService>();
builder.Services.AddSingleton<SymbolCache>();
builder.Services.AddSingleton<CodeFixService>();

// Configure automatic resource caching (Framework v1.4.8+)
builder.Services.Configure<COA.Mcp.Framework.Resources.ResourceCacheOptions>(options =>
{
    options.DefaultExpiration = TimeSpan.FromMinutes(15);  // Cache for 15 minutes
    options.SlidingExpiration = TimeSpan.FromMinutes(5);   // Sliding window of 5 minutes
    options.MaxSizeBytes = 500 * 1024 * 1024;              // 500 MB max cache size for large code analysis results
});

// Register resource services
builder.Services.AddSingleton<IResourceRegistry, ResourceRegistry>();
// Register AnalysisResultResourceProvider as both itself and IResourceProvider in one go
builder.Services.AddSingleton<AnalysisResultResourceProvider>();
builder.Services.AddSingleton<IResourceProvider>(provider => provider.GetRequiredService<AnalysisResultResourceProvider>());

// Note: CodeNavMcpServer is not needed with the Framework - it handles the MCP protocol

// IMPORTANT: Tools must be registered in DI first because they have constructor dependencies
// The Framework's DiscoverTools doesn't handle DI registration - it expects tools to already be in the container
// This is NOT redundant - it's a two-step process:
// 1. Register tools in DI so they can be instantiated with their dependencies
// 2. DiscoverTools finds them and registers them as MCP tools

// Core workspace tools
builder.Services.AddScoped<LoadSolutionTool>();
builder.Services.AddScoped<LoadProjectTool>();
builder.Services.AddScoped<GetWorkspaceStatisticsTool>();

// Navigation tools
builder.Services.AddScoped<GoToDefinitionTool>();
builder.Services.AddScoped<FindAllReferencesTool>();
builder.Services.AddScoped<FindImplementationsTool>();
builder.Services.AddScoped<HoverTool>();
builder.Services.AddScoped<TraceCallStackTool>();
builder.Services.AddScoped<SymbolSearchTool>();
builder.Services.AddScoped<DocumentSymbolsTool>();
builder.Services.AddScoped<GetTypeMembersTool>();

// Refactoring tools
builder.Services.AddScoped<RenameSymbolTool>();
builder.Services.AddScoped<ExtractMethodTool>();
builder.Services.AddScoped<AddMissingUsingsTool>();
builder.Services.AddScoped<FormatDocumentTool>();

// Diagnostics and fixes
builder.Services.AddScoped<GetDiagnosticsTool>();
builder.Services.AddScoped<ApplyCodeFixTool>();
builder.Services.AddScoped<GenerateCodeTool>();

// Advanced analysis tools
builder.Services.AddScoped<CodeMetricsTool>();
builder.Services.AddScoped<FindUnusedCodeTool>();
builder.Services.AddScoped<TypeHierarchyTool>();
builder.Services.AddScoped<CallHierarchyTool>();
builder.Services.AddScoped<FindAllOverridesTool>();
builder.Services.AddScoped<SolutionWideFindReplaceTool>();
builder.Services.AddScoped<CodeCloneDetectionTool>();
builder.Services.AddScoped<DependencyAnalysisTool>();
builder.Services.AddScoped<RefreshWorkspaceTool>();

// Now discover and register all tools that inherit from McpToolBase
// This finds the tools we registered above and sets them up as MCP tools
builder.DiscoverTools(typeof(Program).Assembly);

// Run the server
try
{
    Log.Information("Starting COA CodeNav MCP Server with Framework v1.1...");
    
    // The builder handles both building and running
    await builder.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Server terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}