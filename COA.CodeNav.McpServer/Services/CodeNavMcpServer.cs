using COA.CodeNav.McpServer.Exceptions;
using COA.CodeNav.McpServer.Infrastructure;
using COA.Mcp.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Services;

/// <summary>
/// Main MCP server for CodeNav that provides Roslyn-based LSP services
/// </summary>
public class CodeNavMcpServer : BackgroundService
{
    private readonly ILogger<CodeNavMcpServer> _logger;
    private readonly RoslynWorkspaceService _workspaceService;
    private readonly DocumentService _documentService;
    private readonly MSBuildWorkspaceManager _msbuildManager;
    private readonly ToolRegistry _toolRegistry;
    private readonly AttributeBasedToolDiscovery _toolDiscovery;
    private readonly IResourceRegistry _resourceRegistry;
    private readonly AnalysisResultResourceProvider _analysisResultProvider;
    private readonly JsonSerializerOptions _jsonOptions;
    private StreamWriter? _writer;

    public CodeNavMcpServer(
        ILogger<CodeNavMcpServer> logger,
        RoslynWorkspaceService workspaceService,
        DocumentService documentService,
        MSBuildWorkspaceManager msbuildManager,
        ToolRegistry toolRegistry,
        AttributeBasedToolDiscovery toolDiscovery,
        IResourceRegistry resourceRegistry,
        AnalysisResultResourceProvider analysisResultProvider)
    {
        _logger = logger;
        _workspaceService = workspaceService;
        _documentService = documentService;
        _msbuildManager = msbuildManager;
        _toolRegistry = toolRegistry;
        _toolDiscovery = toolDiscovery;
        _resourceRegistry = resourceRegistry;
        _analysisResultProvider = analysisResultProvider;

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("COA CodeNav MCP Server starting...");
        
        // Initialize MSBuild
        _logger.LogInformation("Initializing MSBuild...");
        _msbuildManager.EnsureMSBuildRegistered();
        
        // Discover and register tools using attribute-based discovery
        _logger.LogInformation("Discovering attribute-based tools...");
        _toolDiscovery.DiscoverAndRegisterTools(_toolRegistry);
        
        // Register resource providers
        _logger.LogInformation("Registering resource providers...");
        RegisterResourceProviders();

        using var reader = new StreamReader(Console.OpenStandardInput());
        _writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var line = await reader.ReadLineAsync(stoppingToken);
                if (line == null) 
                {
                    _logger.LogInformation("Input stream closed, shutting down");
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonRpcRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<JsonRpcRequest>(line, _jsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to parse JSON-RPC request: {Line}", line);
                    var errorResponse = new JsonRpcResponse
                    {
                        Id = null!,
                        Error = new JsonRpcError
                        {
                            Code = JsonRpcErrorCodes.ParseError,
                            Message = "Parse error",
                            Data = "Invalid JSON was received by the server"
                        }
                    };
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(errorResponse, _jsonOptions));
                    continue;
                }

                if (request == null) continue;

                var response = await HandleRequestAsync(request, stoppingToken);
                if (response != null)
                {
                    var responseJson = JsonSerializer.Serialize(response, _jsonOptions);
                    await _writer.WriteLineAsync(responseJson);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Server shutdown requested");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in server loop");
                // Don't write errors to stdout - it corrupts the protocol
            }
        }

        _logger.LogInformation("COA CodeNav MCP Server stopped");
    }

    private async Task<JsonRpcResponse?> HandleRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            object? result = request.Method switch
            {
                "initialize" => HandleInitialize(request),
                "notifications/initialized" => null, // Notification, no response
                "tools/list" => HandleToolsList(),
                "tools/call" => await HandleToolCallAsync(request, cancellationToken),
                "resources/list" => await HandleResourcesListAsync(cancellationToken),
                "resources/read" => await HandleResourcesReadAsync(request, cancellationToken),
                _ => throw new JsonRpcException(JsonRpcErrorCodes.MethodNotFound, $"Method '{request.Method}' not found")
            };

            if (result == null && request.Id == null)
            {
                // This was a notification, no response needed
                return null;
            }

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = result
            };
        }
        catch (JsonRpcException ex)
        {
            _logger.LogError(ex, "JSON-RPC error handling request: {Method}", request.Method);
            return new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError
                {
                    Code = ex.Code,
                    Message = ex.Message,
                    Data = ex.Data
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling request: {Method}", request.Method);
            return new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError
                {
                    Code = JsonRpcErrorCodes.InternalError,
                    Message = "Internal error",
                    Data = ex.Message
                }
            };
        }
    }

    private InitializeResult HandleInitialize(JsonRpcRequest request)
    {
        _logger.LogInformation("Received initialize request");
        
        return new InitializeResult
        {
            ProtocolVersion = "2024-11-05",
            ServerInfo = new Implementation
            {
                Name = "COA CodeNav MCP Server",
                Version = "1.0.0"
            },
            Capabilities = new ServerCapabilities
            {
                Tools = new { }, // Empty object indicates tools support
                Resources = new ResourceCapabilities { Subscribe = true, ListChanged = true }
            }
        };
    }

    private ListToolsResult HandleToolsList()
    {
        _logger.LogDebug("Listing tools");
        
        var tools = _toolRegistry.GetTools();
        _logger.LogInformation("Returning {Count} tools", tools.Count);
        
        return new ListToolsResult { Tools = tools };
    }

    private async Task<ListResourcesResult> HandleResourcesListAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Listing resources");
        
        var resources = await _resourceRegistry.ListResourcesAsync(cancellationToken);
        _logger.LogInformation("Returning {Count} resources", resources.Count);
        
        return new ListResourcesResult { Resources = resources };
    }
    
    private async Task<ReadResourceResult> HandleResourcesReadAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (request.Params == null)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Missing params for resource read");
        }

        var paramsJson = JsonSerializer.Serialize(request.Params);
        var readParams = JsonSerializer.Deserialize<ReadResourceRequest>(paramsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (readParams == null || string.IsNullOrEmpty(readParams.Uri))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Invalid resource read parameters");
        }

        _logger.LogInformation("Reading resource: {Uri}", readParams.Uri);
        _logger.LogDebug("Available providers: {Count}", _resourceRegistry.GetProviders().Count());
        
        try
        {
            return await _resourceRegistry.ReadResourceAsync(readParams.Uri, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Failed to read resource: {Uri}", readParams.Uri);
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, ex.Message);
        }
    }

    private async Task<object> HandleToolCallAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (request.Params == null)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Missing params for tool call");
        }

        var paramsJson = JsonSerializer.Serialize(request.Params);
        var callParams = JsonSerializer.Deserialize<CallToolRequest>(paramsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (callParams == null || string.IsNullOrEmpty(callParams.Name))
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, "Invalid tool call parameters");
        }

        _logger.LogInformation("Calling tool: {ToolName}", callParams.Name);
        
        try
        {
            // Convert Arguments object to JsonElement
            JsonElement? arguments = null;
            if (callParams.Arguments != null)
            {
                var argumentsJson = JsonSerializer.Serialize(callParams.Arguments);
                arguments = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            }
            
            return await _toolRegistry.CallToolAsync(callParams.Name, arguments, cancellationToken);
        }
        catch (InvalidParametersException ex)
        {
            throw new JsonRpcException(JsonRpcErrorCodes.InvalidParams, ex.Message);
        }
        catch (ToolExecutionException ex)
        {
            _logger.LogError(ex, "Tool execution failed: {ToolName}", callParams.Name);
            throw new JsonRpcException(JsonRpcErrorCodes.InternalError, ex.Message);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping COA CodeNav MCP Server...");
        
        // Clean up resources
        _documentService?.Dispose();
        _workspaceService?.Dispose();
        
        await base.StopAsync(cancellationToken);
    }
    
    private void RegisterResourceProviders()
    {
        _logger.LogDebug("Starting resource provider registration");
        
        // Register all resource providers
        _logger.LogDebug("Registering AnalysisResultResourceProvider - Scheme: {Scheme}", _analysisResultProvider.Scheme);
        _resourceRegistry.RegisterProvider(_analysisResultProvider);
        
        var providers = _resourceRegistry.GetProviders().ToList();
        _logger.LogInformation("Registered {Count} resource provider(s)", providers.Count);
        
        foreach (var provider in providers)
        {
            _logger.LogDebug("  - Provider: {Name} (Scheme: {Scheme})", provider.Name, provider.Scheme);
        }
    }
}