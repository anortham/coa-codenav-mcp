using COA.Mcp.Protocol;

namespace COA.CodeNav.McpServer.Services;

/// <summary>
/// Registry for managing resource providers and routing resource requests
/// </summary>
public interface IResourceRegistry
{
    /// <summary>
    /// Lists all available resources from all registered providers
    /// </summary>
    Task<List<Resource>> ListResourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a resource by URI, routing to the appropriate provider
    /// </summary>
    Task<ReadResourceResult> ReadResourceAsync(string uri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a resource provider
    /// </summary>
    void RegisterProvider(IResourceProvider provider);

    /// <summary>
    /// Gets all registered providers
    /// </summary>
    IEnumerable<IResourceProvider> GetProviders();
}