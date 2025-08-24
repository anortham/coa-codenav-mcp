using Microsoft.Extensions.DependencyInjection;

namespace COA.CodeNav.McpServer.IntegrationTests;

/// <summary>
/// Utility for creating test service providers with required framework dependencies
/// </summary>
public static class TestServiceProvider
{
    /// <summary>
    /// Creates a minimal service provider with required framework dependencies for testing
    /// </summary>
    /// <returns>Service provider configured for testing</returns>
    public static IServiceProvider Create()
    {
        var services = new ServiceCollection();
        
        // Add required framework dependencies
        services.AddScoped<IEnumerable<COA.Mcp.Framework.Pipeline.ISimpleMiddleware>>(
            sp => new List<COA.Mcp.Framework.Pipeline.ISimpleMiddleware>());
        
        return services.BuildServiceProvider();
    }
}