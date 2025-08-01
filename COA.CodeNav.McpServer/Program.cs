using COA.CodeNav.McpServer.Caching;
using COA.CodeNav.McpServer.Infrastructure;
using COA.CodeNav.McpServer.Services;
using COA.CodeNav.McpServer.Tools;
using COA.Mcp.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

// Create host builder
var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("CODENAV_");
    })
    .UseSerilog((context, services, configuration) =>
    {
        // Get path resolution service to determine log directory
        var pathResolution = new PathResolutionService(context.Configuration);
        var logDirectory = pathResolution.GetLogsPath();
        
        // Ensure log directory exists
        pathResolution.EnsureDirectoryExists(logDirectory);
        
        configuration
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
            .Enrich.FromLogContext();
    }, preserveStaticLogger: false)
    .ConfigureLogging((context, logging) =>
    {
        logging.ClearProviders();
        // MCP protocol requires that only stderr is used for logging
        // stdout is reserved for JSON-RPC communication
        
        // Redirect console output to stderr
        Console.SetOut(Console.Error);
        
        logging.AddSimpleConsole(options =>
        {
            options.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Disabled;
            options.IncludeScopes = false;
            options.TimestampFormat = "HH:mm:ss ";
        });
        
        // Configure logging from appsettings.json which includes namespace-specific levels
        logging.AddConfiguration(context.Configuration.GetSection("Logging"));
    })
    .ConfigureServices((context, services) =>
    {
        // Register core services
        services.AddSingleton<IPathResolutionService, PathResolutionService>();
        
        // Register infrastructure services
        services.AddSingleton<MSBuildWorkspaceManager>();
        services.AddSingleton<RoslynWorkspaceService>();
        services.AddSingleton<DocumentService>();
        services.AddSingleton<SymbolCache>();

        // Register resource services
        services.AddSingleton<IResourceRegistry, ResourceRegistry>();
        services.AddSingleton<AnalysisResultResourceProvider>();
        
        // Register tool infrastructure
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<AttributeBasedToolDiscovery>();

        // Register Roslyn tools
        services.AddScoped<GoToDefinitionTool>();
        services.AddScoped<LoadSolutionTool>();
        services.AddScoped<LoadProjectTool>();
        services.AddScoped<FindAllReferencesTool>();
        services.AddScoped<HoverTool>();
        services.AddScoped<TraceCallStackTool>();
        services.AddScoped<RenameSymbolTool>();
        services.AddScoped<SymbolSearchTool>();
        services.AddScoped<FindImplementationsTool>();
        services.AddScoped<DocumentSymbolsTool>();
        services.AddScoped<GetTypeMembersTool>();
        services.AddScoped<GetDiagnosticsTool>();

        // Register MCP server
        services.AddSingleton<CodeNavMcpServer>();
        services.AddHostedService<CodeNavMcpServer>(provider => provider.GetRequiredService<CodeNavMcpServer>());
    })
    .Build();

// Run the host
try
{
    Log.Information("Starting COA CodeNav MCP Server...");
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
