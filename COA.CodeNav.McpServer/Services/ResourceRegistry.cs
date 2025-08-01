using COA.Mcp.Protocol;
using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Services;

/// <summary>
/// Central registry for managing resource providers and routing resource requests.
/// Coordinates between different resource providers to offer a unified resource API.
/// </summary>
public class ResourceRegistry : IResourceRegistry
{
    private readonly ILogger<ResourceRegistry> _logger;
    private readonly List<IResourceProvider> _providers = new();

    public ResourceRegistry(ILogger<ResourceRegistry> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<Resource>> ListResourcesAsync(CancellationToken cancellationToken = default)
    {
        var allResources = new List<Resource>();

        foreach (var provider in _providers)
        {
            try
            {
                var resources = await provider.ListResourcesAsync(cancellationToken);
                allResources.AddRange(resources);
                _logger.LogDebug("Provider {ProviderName} contributed {Count} resources", 
                    provider.Name, resources.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing resources from provider {ProviderName}", provider.Name);
                // Continue with other providers
            }
        }

        _logger.LogInformation("Listed {TotalCount} resources from {ProviderCount} providers", 
            allResources.Count, _providers.Count);

        return allResources;
    }

    /// <inheritdoc />
    public async Task<ReadResourceResult> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new ArgumentException("URI cannot be null or empty", nameof(uri));
        }

        _logger.LogDebug("Attempting to read resource: {Uri}", uri);
        _logger.LogDebug("Available providers: {Count}", _providers.Count);
        
        foreach (var provider in _providers)
        {
            _logger.LogDebug("Checking provider {ProviderName} (scheme: {Scheme}) for URI: {Uri}", 
                provider.Name, provider.Scheme, uri);
                
            try
            {
                var canHandle = provider.CanHandle(uri);
                _logger.LogDebug("Provider {ProviderName} CanHandle({Uri}) = {CanHandle}", 
                    provider.Name, uri, canHandle);
                    
                if (canHandle)
                {
                    _logger.LogDebug("Provider {ProviderName} can handle URI, attempting to read", provider.Name);
                    var result = await provider.ReadResourceAsync(uri, cancellationToken);
                    if (result != null)
                    {
                        _logger.LogDebug("Provider {ProviderName} successfully read resource {Uri}", 
                            provider.Name, uri);
                        return result;
                    }
                    else
                    {
                        _logger.LogWarning("Provider {ProviderName} returned null for resource {Uri}", 
                            provider.Name, uri);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading resource {Uri} from provider {ProviderName}", 
                    uri, provider.Name);
                // Continue with other providers
            }
        }

        _logger.LogWarning("No provider could handle resource URI: {Uri}. Total providers checked: {Count}", 
            uri, _providers.Count);
        throw new InvalidOperationException($"No provider found for resource URI: {uri}");
    }

    /// <inheritdoc />
    public void RegisterProvider(IResourceProvider provider)
    {
        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        // Check for duplicate schemes
        var existingProvider = _providers.FirstOrDefault(p => p.Scheme == provider.Scheme);
        if (existingProvider != null)
        {
            _logger.LogWarning("Replacing existing provider for scheme '{Scheme}'. " +
                             "Old: {OldProvider}, New: {NewProvider}", 
                             provider.Scheme, existingProvider.Name, provider.Name);
            _providers.Remove(existingProvider);
        }

        _providers.Add(provider);
        _logger.LogInformation("Registered resource provider: {ProviderName} (scheme: {Scheme})", 
            provider.Name, provider.Scheme);
    }

    /// <inheritdoc />
    public IEnumerable<IResourceProvider> GetProviders()
    {
        return _providers.AsReadOnly();
    }
}