using System.Text.Json;

namespace COA.CodeNav.McpServer.Utilities;

/// <summary>
/// Utility class for estimating token usage in API responses.
/// Based on proven patterns from CodeSearch MCP.
/// </summary>
public static class TokenEstimator
{
    /// <summary>
    /// Default safety limit for responses (10K tokens = ~5% of context window)
    /// </summary>
    public const int DEFAULT_SAFETY_LIMIT = 10000;
    
    /// <summary>
    /// Conservative safety limit for complex responses (5K tokens)
    /// </summary>
    public const int CONSERVATIVE_SAFETY_LIMIT = 5000;
    
    /// <summary>
    /// Base tokens for response structure overhead
    /// </summary>
    public const int BASE_RESPONSE_TOKENS = 500;

    /// <summary>
    /// Estimate tokens for a string (approximately 1 token per 4 characters)
    /// </summary>
    public static int EstimateString(string? text)
        => (text?.Length ?? 0) / 4;

    /// <summary>
    /// Estimate tokens for an object by serializing to JSON
    /// </summary>
    public static int EstimateObject(object obj)
    {
        try
        {
            var json = JsonSerializer.Serialize(obj);
            return json.Length / 4;
        }
        catch
        {
            // Conservative estimate if serialization fails
            return 100;
        }
    }

    /// <summary>
    /// Estimate tokens for a collection using sampling
    /// </summary>
    public static int EstimateCollection<T>(
        IEnumerable<T> items, 
        Func<T, int> itemEstimator,
        int baseTokens = BASE_RESPONSE_TOKENS,
        int sampleSize = 5)
    {
        var itemList = items as IList<T> ?? items.ToList();
        if (!itemList.Any())
            return baseTokens;

        // Sample first few items for accurate estimation
        var sample = itemList.Take(sampleSize).ToList();
        var avgTokensPerItem = sample.Average(itemEstimator);
        
        return baseTokens + (int)(itemList.Count * avgTokensPerItem);
    }

    /// <summary>
    /// Apply progressive reduction to stay within token limit
    /// </summary>
    public static List<T> ApplyProgressiveReduction<T>(
        List<T> items,
        Func<List<T>, int> estimator,
        int tokenLimit,
        int[]? reductionSteps = null)
    {
        reductionSteps = reductionSteps ?? new[] { 100, 75, 50, 30, 20, 10, 5 };
        
        // First check if we're already under the limit
        if (estimator(items) <= tokenLimit)
            return items;

        // Try progressive reduction
        foreach (var count in reductionSteps)
        {
            if (count >= items.Count)
                continue;
                
            var candidateItems = items.Take(count).ToList();
            if (estimator(candidateItems) <= tokenLimit)
                return candidateItems;
        }

        // If still over limit, return minimal set
        return items.Take(Math.Min(3, items.Count)).ToList();
    }

    /// <summary>
    /// Estimate tokens for common Roslyn data types
    /// </summary>
    public static class Roslyn
    {
        public static int EstimateDiagnostic(dynamic diagnostic)
        {
            var tokens = 50; // Base structure
            tokens += EstimateString(diagnostic.Message);
            tokens += EstimateString(diagnostic.Location?.FilePath) / 2; // Paths appear multiple times
            tokens += (diagnostic.Tags?.Count ?? 0) * 5;
            tokens += (diagnostic.Properties?.Count ?? 0) * 20;
            return tokens;
        }

        public static int EstimateReference(dynamic reference)
        {
            var tokens = 50; // Base structure
            tokens += EstimateString(reference.FilePath) / 2;
            
            // ReferenceLocation has Text and Kind properties
            if (reference.Text != null)
                tokens += EstimateString(reference.Text);
            if (reference.Kind != null)
                tokens += EstimateString(reference.Kind);
                
            return tokens;
        }

        public static int EstimateSymbol(dynamic symbol)
        {
            var tokens = 80; // Base structure
            tokens += EstimateString(symbol.Name);
            tokens += EstimateString(symbol.Kind);
            
            // SymbolInfo doesn't have Documentation or Signature properties
            // Those belong to other types like HoverInfo or TypeMemberInfo
            if (symbol.FullName != null)
                tokens += EstimateString(symbol.FullName);
            if (symbol.ContainerName != null)
                tokens += EstimateString(symbol.ContainerName);
            if (symbol.Namespace != null)
                tokens += EstimateString(symbol.Namespace);
                
            return tokens;
        }

        public static int EstimateTypeMember(dynamic member)
        {
            var tokens = 100; // Base structure
            tokens += EstimateString(member.Name);
            tokens += EstimateString(member.Signature);
            tokens += EstimateString(member.Documentation) / 2;
            tokens += EstimateString(member.ReturnType);
            tokens += (member.Parameters?.Count ?? 0) * 30;
            return tokens;
        }

        public static int EstimateCallFrame(dynamic frame, bool includeContext = false)
        {
            var tokens = 100; // Base structure
            tokens += EstimateString(frame.Method);
            
            // CallStep has 'File' property, not 'FilePath'
            try
            {
                if (frame.GetType().GetProperty("File") != null)
                    tokens += EstimateString(frame.File) / 2;
                else if (frame.GetType().GetProperty("FilePath") != null)
                    tokens += EstimateString(frame.FilePath) / 2;
            }
            catch
            {
                // Ignore if properties don't exist
            }
                
            return tokens;
        }

        public static int EstimateDocumentSymbol(dynamic symbol, bool recursive = true)
        {
            var tokens = 80; // Base structure
            tokens += EstimateString(symbol.Name);
            tokens += EstimateString(symbol.Kind);
            
            if (recursive && symbol.Children != null)
            {
                foreach (var child in symbol.Children)
                    tokens += EstimateDocumentSymbol(child, true);
            }
            
            return tokens;
        }
    }

    /// <summary>
    /// Create a token-aware response with safety limit applied
    /// </summary>
    public static TokenAwareResponse<T> CreateTokenAwareResponse<T>(
        List<T> allItems,
        Func<List<T>, int> estimator,
        int requestedMax,
        int safetyLimit = DEFAULT_SAFETY_LIMIT,
        string toolName = "unknown")
    {
        var totalCount = allItems.Count;
        var maxToConsider = Math.Min(requestedMax, 500); // Hard limit
        var candidateItems = allItems.Take(maxToConsider).ToList();
        
        // Pre-estimate tokens
        var preEstimatedTokens = estimator(candidateItems);
        var safetyLimitApplied = false;
        List<T> returnedItems;
        
        if (preEstimatedTokens > safetyLimit)
        {
            // Apply progressive reduction
            returnedItems = ApplyProgressiveReduction(candidateItems, estimator, safetyLimit);
            safetyLimitApplied = true;
        }
        else
        {
            returnedItems = candidateItems;
        }
        
        return new TokenAwareResponse<T>
        {
            Items = returnedItems,
            TotalCount = totalCount,
            ReturnedCount = returnedItems.Count,
            WasTruncated = totalCount > returnedItems.Count,
            SafetyLimitApplied = safetyLimitApplied,
            EstimatedTokens = estimator(returnedItems),
            PreEstimatedTokens = safetyLimitApplied ? preEstimatedTokens : null
        };
    }
}

/// <summary>
/// Response wrapper with token awareness information
/// </summary>
public class TokenAwareResponse<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int ReturnedCount { get; set; }
    public bool WasTruncated { get; set; }
    public bool SafetyLimitApplied { get; set; }
    public int EstimatedTokens { get; set; }
    public int? PreEstimatedTokens { get; set; }
    
    public string GetTruncationMessage()
    {
        if (!WasTruncated)
            return string.Empty;
            
        if (SafetyLimitApplied)
            return $"⚠️ Response size limit applied ({PreEstimatedTokens} tokens). Showing {ReturnedCount} of {TotalCount} results.";
        else
            return $"Showing {ReturnedCount} of {TotalCount} results to manage response size.";
    }
}
