using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.Caching;

namespace COA.CodeNav.McpServer.Caching;

public class SymbolCache : IDisposable
{
    private readonly ILogger<SymbolCache> _logger;
    private readonly MemoryCache _cache;
    private readonly ConcurrentDictionary<string, CacheMetrics> _metrics = new();
    private readonly CacheItemPolicy _defaultPolicy;
    private readonly object _cacheLock = new();

    public SymbolCache(ILogger<SymbolCache> logger)
    {
        _logger = logger;
        _cache = new MemoryCache("SymbolCache");
        
        // Default cache policy with sliding expiration
        _defaultPolicy = new CacheItemPolicy
        {
            SlidingExpiration = TimeSpan.FromMinutes(30),
            Priority = CacheItemPriority.Default,
            RemovedCallback = OnCacheItemRemoved
        };
    }

    public void CacheSymbol(ISymbol symbol, string? additionalKey = null)
    {
        if (symbol == null) return;

        var key = GenerateSymbolKey(symbol, additionalKey);
        var cacheItem = new CachedSymbol
        {
            Symbol = symbol,
            CachedAt = DateTime.UtcNow,
            ProjectId = symbol.ContainingAssembly?.Name ?? "Unknown"
        };

        _cache.Set(key, cacheItem, _defaultPolicy);
        UpdateMetrics(key, CacheOperation.Add);
        
        _logger.LogDebug("Cached symbol: {Key}", key);
    }

    public ISymbol? GetSymbol(string symbolName, string? containingType = null, string? additionalKey = null)
    {
        var key = GenerateKey(symbolName, containingType, additionalKey);
        
        if (_cache.Get(key) is CachedSymbol cachedSymbol)
        {
            UpdateMetrics(key, CacheOperation.Hit);
            _logger.LogDebug("Cache hit for symbol: {Key}", key);
            return cachedSymbol.Symbol;
        }

        UpdateMetrics(key, CacheOperation.Miss);
        return null;
    }

    public void CacheCompilation(ProjectId projectId, Compilation compilation)
    {
        var key = $"compilation:{projectId}";
        var cacheItem = new CachedCompilation
        {
            Compilation = compilation,
            CachedAt = DateTime.UtcNow,
            ProjectId = projectId
        };

        // Compilations get longer expiration
        var policy = new CacheItemPolicy
        {
            SlidingExpiration = TimeSpan.FromHours(2),
            Priority = CacheItemPriority.NotRemovable,
            RemovedCallback = OnCacheItemRemoved
        };

        _cache.Set(key, cacheItem, policy);
        UpdateMetrics(key, CacheOperation.Add);
        
        _logger.LogInformation("Cached compilation for project: {ProjectId}", projectId);
    }

    public Compilation? GetCompilation(ProjectId projectId)
    {
        var key = $"compilation:{projectId}";
        
        if (_cache.Get(key) is CachedCompilation cachedCompilation)
        {
            UpdateMetrics(key, CacheOperation.Hit);
            _logger.LogDebug("Cache hit for compilation: {ProjectId}", projectId);
            return cachedCompilation.Compilation;
        }

        UpdateMetrics(key, CacheOperation.Miss);
        return null;
    }

    public void CacheDocumentSymbols(DocumentId documentId, IEnumerable<ISymbol> symbols)
    {
        var key = $"document-symbols:{documentId}";
        var symbolList = symbols.ToList();
        
        var cacheItem = new CachedDocumentSymbols
        {
            Symbols = symbolList,
            CachedAt = DateTime.UtcNow,
            DocumentId = documentId
        };

        _cache.Set(key, cacheItem, _defaultPolicy);
        UpdateMetrics(key, CacheOperation.Add);
        
        _logger.LogDebug("Cached {Count} symbols for document: {DocumentId}", symbolList.Count, documentId);
    }

    public IReadOnlyList<ISymbol>? GetDocumentSymbols(DocumentId documentId)
    {
        var key = $"document-symbols:{documentId}";
        
        if (_cache.Get(key) is CachedDocumentSymbols cachedSymbols)
        {
            UpdateMetrics(key, CacheOperation.Hit);
            return cachedSymbols.Symbols;
        }

        UpdateMetrics(key, CacheOperation.Miss);
        return null;
    }

    public void InvalidateDocument(DocumentId documentId)
    {
        var key = $"document-symbols:{documentId}";
        _cache.Remove(key);
        _logger.LogDebug("Invalidated cache for document: {DocumentId}", documentId);
    }

    public void InvalidateProject(ProjectId projectId)
    {
        var compilationKey = $"compilation:{projectId}";
        _cache.Remove(compilationKey);
        
        // Remove all cached items for this project
        var keysToRemove = new List<string>();
        
        lock (_cacheLock)
        {
            foreach (var item in _cache)
            {
                if (item.Value is ICachedItem cachedItem && cachedItem.ProjectId == projectId.ToString())
                {
                    if (item.Key != null)
                    {
                        keysToRemove.Add(item.Key);
                    }
                }
            }
        }

        foreach (var key in keysToRemove)
        {
            _cache.Remove(key);
        }
        
        _logger.LogInformation("Invalidated cache for project: {ProjectId}. Removed {Count} items", projectId, keysToRemove.Count);
    }

    public CacheStatistics GetStatistics()
    {
        var stats = new CacheStatistics
        {
            TotalItems = (int)_cache.GetCount(),
            Metrics = _metrics.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone())
        };

        // Calculate hit ratio
        var totalHits = _metrics.Values.Sum(m => m.Hits);
        var totalMisses = _metrics.Values.Sum(m => m.Misses);
        var totalRequests = totalHits + totalMisses;
        
        stats.HitRatio = totalRequests > 0 ? (double)totalHits / (double)totalRequests : 0;
        
        return stats;
    }

    public void Clear()
    {
        lock (_cacheLock)
        {
            // Dispose of cache properly
            var cacheItems = _cache.ToList();
            foreach (var item in cacheItems)
            {
                _cache.Remove(item.Key);
            }
        }
        
        _metrics.Clear();
        _logger.LogInformation("Cache cleared");
    }

    private string GenerateSymbolKey(ISymbol symbol, string? additionalKey)
    {
        var parts = new List<string> { "symbol" };
        
        var namespaceName = symbol.ContainingNamespace?.ToString();
        if (!string.IsNullOrEmpty(namespaceName))
        {
            parts.Add(namespaceName);
        }
        
        if (symbol.ContainingType != null)
        {
            parts.Add(symbol.ContainingType.Name);
        }
        
        parts.Add(symbol.Name);
        
        if (!string.IsNullOrEmpty(additionalKey))
        {
            parts.Add(additionalKey);
        }
        
        return string.Join(":", parts);
    }

    private string GenerateKey(string symbolName, string? containingType, string? additionalKey)
    {
        var parts = new List<string> { "symbol" };
        
        if (!string.IsNullOrEmpty(containingType))
        {
            parts.Add(containingType);
        }
        
        parts.Add(symbolName);
        
        if (!string.IsNullOrEmpty(additionalKey))
        {
            parts.Add(additionalKey);
        }
        
        return string.Join(":", parts);
    }

    private void UpdateMetrics(string key, CacheOperation operation)
    {
        var metrics = _metrics.GetOrAdd(key, k => new CacheMetrics { Key = k });
        
        switch (operation)
        {
            case CacheOperation.Add:
                metrics.Additions++;
                break;
            case CacheOperation.Hit:
                metrics.Hits++;
                break;
            case CacheOperation.Miss:
                metrics.Misses++;
                break;
            case CacheOperation.Remove:
                metrics.Removals++;
                break;
        }
        
        metrics.LastAccessed = DateTime.UtcNow;
    }

    private void OnCacheItemRemoved(CacheEntryRemovedArguments args)
    {
        UpdateMetrics(args.CacheItem.Key, CacheOperation.Remove);
        _logger.LogDebug("Cache item removed: {Key} (Reason: {Reason})", args.CacheItem.Key, args.RemovedReason);
    }

    public void Dispose()
    {
        _cache?.Dispose();
    }
}

// Cache item interfaces and implementations
public interface ICachedItem
{
    DateTime CachedAt { get; }
    string ProjectId { get; }
}

public class CachedSymbol : ICachedItem
{
    public required ISymbol Symbol { get; set; }
    public DateTime CachedAt { get; set; }
    public required string ProjectId { get; set; }
}

public class CachedCompilation : ICachedItem
{
    public required Compilation Compilation { get; set; }
    public DateTime CachedAt { get; set; }
    public required ProjectId ProjectId { get; set; }
    string ICachedItem.ProjectId => ProjectId.ToString();
}

public class CachedDocumentSymbols : ICachedItem
{
    public required IReadOnlyList<ISymbol> Symbols { get; set; }
    public DateTime CachedAt { get; set; }
    public required DocumentId DocumentId { get; set; }
    public string ProjectId => DocumentId.ProjectId.ToString();
}

// Metrics and statistics
public enum CacheOperation
{
    Add,
    Hit,
    Miss,
    Remove
}

public class CacheMetrics
{
    public string Key { get; set; } = "";
    public int Hits { get; set; }
    public int Misses { get; set; }
    public int Additions { get; set; }
    public int Removals { get; set; }
    public DateTime LastAccessed { get; set; }
    
    public CacheMetrics Clone()
    {
        return new CacheMetrics
        {
            Key = Key,
            Hits = Hits,
            Misses = Misses,
            Additions = Additions,
            Removals = Removals,
            LastAccessed = LastAccessed
        };
    }
}

public class CacheStatistics
{
    public int TotalItems { get; set; }
    public double HitRatio { get; set; }
    public Dictionary<string, CacheMetrics> Metrics { get; set; } = new();
}