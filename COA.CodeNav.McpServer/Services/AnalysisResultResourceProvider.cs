using COA.Mcp.Protocol;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;

namespace COA.CodeNav.McpServer.Services;

/// <summary>
/// Resource provider that exposes code analysis results as persistent, shareable resources.
/// Allows AI agents to bookmark and reference analysis results across conversation turns.
/// </summary>
public class AnalysisResultResourceProvider : IResourceProvider
{
    private readonly ILogger<AnalysisResultResourceProvider> _logger;
    private readonly ConcurrentDictionary<string, AnalysisResultData> _analysisResults = new();
    private readonly Timer _cleanupTimer;

    public string Scheme => "codenav-analysis";
    public string Name => "Analysis Results";
    public string Description => "Provides persistent access to code analysis results like GoToDefinition and FindAllReferences";

    public AnalysisResultResourceProvider(ILogger<AnalysisResultResourceProvider> logger)
    {
        _logger = logger;
        
        // Clean up old results every hour
        _cleanupTimer = new Timer(CleanupOldResults, null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    /// <summary>
    /// Stores an analysis result and returns its resource URI
    /// </summary>
    public string StoreAnalysisResult(string analysisType, object result, string? description = null)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var uri = $"{Scheme}://{analysisType}/{id}";

        var data = new AnalysisResultData
        {
            Id = id,
            Type = analysisType,
            Result = result,
            Description = description ?? $"{analysisType} analysis result",
            CreatedAt = DateTime.UtcNow,
            LastAccessed = DateTime.UtcNow
        };

        _analysisResults[id] = data;
        _logger.LogDebug("Stored {AnalysisType} result with ID {Id}, URI: {Uri}", analysisType, id, uri);

        return uri;
    }

    /// <inheritdoc />
    public Task<List<Resource>> ListResourcesAsync(CancellationToken cancellationToken = default)
    {
        var resources = new List<Resource>();

        foreach (var kvp in _analysisResults)
        {
            var id = kvp.Key;
            var data = kvp.Value;

            resources.Add(new Resource
            {
                Uri = $"{Scheme}://{data.Type}/{id}",
                Name = $"{data.Type}: {data.Description}",
                Description = $"Analysis performed at {data.CreatedAt:g} (accessed {data.LastAccessed:g})",
                MimeType = "application/json"
            });
        }

        _logger.LogDebug("Listed {Count} analysis result resources", resources.Count);
        return Task.FromResult(resources);
    }

    /// <inheritdoc />
    public Task<ReadResourceResult?> ReadResourceAsync(string uri, CancellationToken cancellationToken = default)
    {
        if (!CanHandle(uri))
            return Task.FromResult<ReadResourceResult?>(null);

        try
        {
            // Extract ID and optional page from URI format: 
            // codenav-analysis://type/id or codenav-analysis://type/id/page/1
            var parts = uri.Split('/');
            if (parts.Length < 4)
            {
                _logger.LogWarning("Invalid URI format: {Uri}", uri);
                return Task.FromResult<ReadResourceResult?>(null);
            }

            var id = parts[3]; // parts[0]=codenav-analysis:, parts[1]=empty, parts[2]=type, parts[3]=id
            var page = 1;
            var pageSize = 100; // Default page size for paginated results
            
            // Check for pagination parameters
            if (parts.Length >= 6 && parts[4] == "page")
            {
                if (int.TryParse(parts[5], out var parsedPage))
                {
                    page = Math.Max(1, parsedPage);
                }
            }
            
            _logger.LogDebug("Extracted ID '{Id}' from URI '{Uri}', page {Page}", id, uri, page);

            if (!_analysisResults.TryGetValue(id, out var data))
            {
                _logger.LogWarning("Analysis result not found: {Id}", id);
                return Task.FromResult<ReadResourceResult?>(null);
            }

            // Update last accessed time
            data.LastAccessed = DateTime.UtcNow;

            // Check if result needs pagination
            object resultData = data.Result;
            int? totalItems = null;
            int? totalPages = null;
            
            // Handle pagination for known collection types
            if (data.Result is IDictionary<string, object> dict)
            {
                // For diagnostics results
                if (dict.TryGetValue("diagnostics", out var diagnosticsObj) && diagnosticsObj is IList<object> diagnosticsList)
                {
                    totalItems = diagnosticsList.Count;
                    totalPages = (int)Math.Ceiling((double)totalItems / pageSize);
                    
                    if (totalItems > pageSize)
                    {
                        var skip = (page - 1) * pageSize;
                        var pagedDiagnostics = diagnosticsList.Skip(skip).Take(pageSize).ToList();
                        
                        // Create paginated result
                        resultData = new Dictionary<string, object>(dict)
                        {
                            ["diagnostics"] = pagedDiagnostics,
                            ["_pagination"] = new
                            {
                                page = page,
                                pageSize = pageSize,
                                totalItems = totalItems,
                                totalPages = totalPages,
                                hasNext = page < totalPages,
                                hasPrevious = page > 1,
                                nextUri = page < totalPages ? $"{uri.Split("/page/")[0]}/page/{page + 1}" : null,
                                previousUri = page > 1 ? $"{uri.Split("/page/")[0]}/page/{page - 1}" : null,
                                firstUri = $"{uri.Split("/page/")[0]}/page/1",
                                lastUri = $"{uri.Split("/page/")[0]}/page/{totalPages}"
                            }
                        };
                    }
                }
                // For find-all-references results
                else if (dict.TryGetValue("allLocations", out var locationsObj) && locationsObj is IList<object> locationsList)
                {
                    totalItems = locationsList.Count;
                    totalPages = (int)Math.Ceiling((double)totalItems / pageSize);
                    
                    if (totalItems > pageSize)
                    {
                        var skip = (page - 1) * pageSize;
                        var pagedLocations = locationsList.Skip(skip).Take(pageSize).ToList();
                        
                        // Create paginated result
                        resultData = new Dictionary<string, object>(dict)
                        {
                            ["allLocations"] = pagedLocations,
                            ["_pagination"] = new
                            {
                                page = page,
                                pageSize = pageSize,
                                totalItems = totalItems,
                                totalPages = totalPages,
                                hasNext = page < totalPages,
                                hasPrevious = page > 1,
                                nextUri = page < totalPages ? $"{uri.Split("/page/")[0]}/page/{page + 1}" : null,
                                previousUri = page > 1 ? $"{uri.Split("/page/")[0]}/page/{page - 1}" : null,
                                firstUri = $"{uri.Split("/page/")[0]}/page/1",
                                lastUri = $"{uri.Split("/page/")[0]}/page/{totalPages}"
                            }
                        };
                    }
                }
            }

            // Build response with AI-friendly structure
            var response = new
            {
                uri = uri,
                type = data.Type,
                created = data.CreatedAt,
                result = resultData,
                meta = new
                {
                    accessCount = data.AccessCount++,
                    lastAccessed = data.LastAccessed,
                    age = DateTime.UtcNow - data.CreatedAt,
                    pagination = totalItems > pageSize ? new
                    {
                        enabled = true,
                        page = page,
                        pageSize = pageSize,
                        totalItems = totalItems,
                        totalPages = totalPages
                    } : null
                }
            };

            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            return Task.FromResult<ReadResourceResult?>(new ReadResourceResult
            {
                Contents = new List<ResourceContent>
                {
                    new ResourceContent
                    {
                        Uri = uri,
                        MimeType = "application/json",
                        Text = json
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading analysis result resource: {Uri}", uri);
            return Task.FromResult<ReadResourceResult?>(null);
        }
    }

    /// <inheritdoc />
    public bool CanHandle(string uri)
    {
        return uri?.StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    private void CleanupOldResults(object? state)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var toRemove = _analysisResults
            .Where(kvp => kvp.Value.LastAccessed < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            if (_analysisResults.TryRemove(key, out _))
            {
                _logger.LogDebug("Removed old analysis result: {Id}", key);
            }
        }

        if (toRemove.Count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} old analysis results", toRemove.Count);
        }
    }

    private class AnalysisResultData
    {
        public required string Id { get; set; }
        public required string Type { get; set; }
        public required object Result { get; set; }
        public required string Description { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastAccessed { get; set; }
        public int AccessCount { get; set; }
    }
}