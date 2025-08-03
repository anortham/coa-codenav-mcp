# Token Management Implementation Plan for CodeNav MCP

## Overview

This document outlines the implementation plan for adding proper token management to all CodeNav MCP tools, following the proven pattern from CodeSearch MCP.

## Problem Statement

Without proper token management, tools can consume 10%+ of the context window per call, forcing frequent clearing/compacting and severely degrading usefulness. We need lightweight results with progressive disclosure.

## Core Pattern (From CodeSearch)

```csharp
// 1. Pre-estimate tokens BEFORE building response
var preEstimatedTokens = EstimateResponseTokens(candidateResults);

// 2. Apply safety limit (typically 5K-10K tokens)
const int SAFETY_TOKEN_LIMIT = 10000;
if (preEstimatedTokens > SAFETY_TOKEN_LIMIT)
{
    // Progressive reduction
    for (int count = 50; count >= 10; count -= 10)
    {
        var testResults = results.Take(count).ToList();
        if (EstimateResponseTokens(testResults) <= SAFETY_TOKEN_LIMIT)
        {
            candidateResults = testResults;
            break;
        }
    }
}

// 3. Always provide pagination hints
if (results.Count > candidateResults.Count)
{
    var resourceUri = _resourceProvider.StoreAnalysisResult(...);
    actions.Add(new NextAction { /* get more results */ });
}
```

## Token Size Estimates (Roslyn Data)

| Data Type | Estimated Tokens |
|-----------|------------------|
| Diagnostic with full details | 300-500 |
| Reference with context | 100-200 |
| Symbol with documentation | 200-400 |
| Type member with signature | 150-300 |
| Call stack frame | 100-150 |
| File location | 50-75 |

## Implementation Priority

### Phase 1: Critical Tools (HIGH RISK)

#### 1. FindAllReferencesTool
- **Current**: Hard limit of 50 references
- **Issue**: No pre-estimation, could still overflow with context
- **Implementation**:
  ```csharp
  private int EstimateReferenceTokens(List<ReferenceLocation> refs)
  {
      var baseTokens = 500;
      var perRefTokens = 150; // Include location, text snippet
      if (refs.Any(r => r.Context != null))
          perRefTokens += 100; // Additional for context
      return baseTokens + (refs.Count * perRefTokens);
  }
  ```
  - Safety limit: 8K tokens
  - Progressive reduction: 100 → 50 → 30 → 20 → 10

#### 2. SymbolSearchTool
- **Current**: Hard limit of 50 symbols
- **Issue**: Symbols with docs can be very large
- **Implementation**:
  ```csharp
  private int EstimateSymbolTokens(List<SymbolInfo> symbols)
  {
      var baseTokens = 500;
      var perSymbolTokens = symbols.Take(5).Average(s => 
          100 + // Base
          (s.Documentation?.Length ?? 0) / 4 + // Docs
          (s.Signature?.Length ?? 0) / 4); // Signature
      return baseTokens + (symbols.Count * (int)perSymbolTokens);
  }
  ```
  - Safety limit: 8K tokens
  - Remove documentation if over limit

#### 3. DocumentSymbolsTool
- **Current**: NO LIMIT!
- **Issue**: Large files can have 100s of symbols
- **Implementation**:
  ```csharp
  private int EstimateDocumentSymbolTokens(List<DocumentSymbol> symbols)
  {
      var baseTokens = 500;
      var perSymbolTokens = 80; // Name, kind, range
      var totalSymbols = CountNestedSymbols(symbols);
      return baseTokens + (totalSymbols * perSymbolTokens);
  }
  ```
  - Safety limit: 10K tokens
  - Flatten hierarchy if needed
  - Limit depth of nesting

#### 4. GetTypeMembersTool
- **Current**: NO LIMIT!
- **Issue**: Large types with extensive docs
- **Implementation**:
  ```csharp
  private int EstimateTypeMemberTokens(List<MemberInfo> members)
  {
      var baseTokens = 500;
      var sample = members.Take(5);
      var avgTokens = sample.Average(m =>
          100 + // Base
          (m.Documentation?.Length ?? 0) / 4 +
          (m.Signature?.Length ?? 0) / 4);
      return baseTokens + (members.Count * (int)avgTokens);
  }
  ```
  - Safety limit: 8K tokens
  - Option to exclude docs/private members

#### 5. TraceCallStackTool
- **Current**: Depth limit only
- **Issue**: Deep traces with context
- **Implementation**:
  ```csharp
  private int EstimateTraceTokens(List<CallFrame> frames)
  {
      var baseTokens = 500;
      var perFrameTokens = 120;
      if (includeContext) perFrameTokens += 150;
      return baseTokens + (frames.Count * perFrameTokens);
  }
  ```
  - Safety limit: 10K tokens
  - Limit depth progressively
  - Option to exclude context

### Phase 2: Medium Risk Tools

#### 6. RenameSymbolTool
- Add estimation for affected files list
- Limit preview snippets
- Safety limit: 10K tokens

#### 7. ApplyCodeFixTool
- Estimate based on changed line count
- Truncate large diffs
- Safety limit: 10K tokens

#### 8. FindImplementationsTool
- Similar to FindAllReferences pattern
- Safety limit: 8K tokens

### Phase 3: Infrastructure

#### 9. Create TokenEstimator Utility
```csharp
public static class TokenEstimator
{
    public const int SAFETY_LIMIT = 10000;
    
    public static int EstimateString(string? text)
        => (text?.Length ?? 0) / 4;
    
    public static int EstimateObject(object obj)
        => JsonSerializer.Serialize(obj).Length / 4;
    
    public static int EstimateCollection<T>(
        IEnumerable<T> items, 
        Func<T, int> estimator,
        int baseTokens = 500)
    {
        var sample = items.Take(5).ToList();
        var avgTokens = sample.Any() 
            ? sample.Average(estimator) 
            : 100; // Conservative default
        return baseTokens + (items.Count() * (int)avgTokens);
    }
}
```

## Success Criteria

1. ✅ No tool throws MCP token overflow errors
2. ✅ All tools pre-estimate response size
3. ✅ Progressive reduction when over limits
4. ✅ Clear NextActions for getting more data
5. ✅ Resource URIs for full results
6. ✅ Insights explain when limits applied

## Testing Strategy

1. Create test solution with:
   - Type with 100+ members
   - Symbol referenced 500+ times
   - File with 200+ symbols
   - Deep call chains (20+ levels)

2. Verify each tool:
   - Handles large results gracefully
   - Never exceeds token limits
   - Provides useful partial results
   - Offers clear next steps

## Rollout Plan

1. Week 1: Implement Phase 1 (High Risk Tools)
2. Week 2: Implement Phase 2 + Infrastructure
3. Week 3: Testing and refinement
4. Week 4: Documentation and best practices

## Long-term Considerations

1. Monitor actual token usage in production
2. Adjust estimates based on real data
3. Consider streaming for very large results
4. Implement caching for repeated queries
