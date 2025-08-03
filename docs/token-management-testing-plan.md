# Token Management Testing Plan for COA CodeNav MCP

## Overview

This document provides a comprehensive testing plan for the token management implementation in COA CodeNav MCP tools. The token management system was improved to prevent context window overflow by implementing pre-estimation, progressive reduction, and resource storage patterns learned from the proven CodeSearch MCP implementation.

## Critical Testing Principles

### 1. **Pre-estimation Testing**
Each tool now estimates response size BEFORE building the full response to prevent token overflow.

### 2. **Progressive Reduction Validation**  
When pre-estimated tokens exceed safety limits, tools apply progressive reduction (100→75→50→30→20→10→5 items).

### 3. **NextActions Verification**
Truncated responses MUST provide NextActions to guide users on getting more results.

### 4. **Resource Storage Testing**
Full results are stored as resources when truncated for later retrieval.

### 5. **Insights Transparency**
Tools must clearly indicate when truncation occurs and why.

## Tool-Specific Test Scenarios

### 1. GetDiagnosticsTool (Enhanced from Basic Token Management)

**Previous Implementation:** Had basic token management with simple limits
**Current Implementation:** Advanced pre-estimation with progressive reduction

#### Test Scenarios:

**Scenario 1.1: Solution with Many Diagnostic-Heavy Files**
```bash
# Setup: Create solution with 20+ files, each having 50+ compilation errors
# Example: Missing using statements, undefined variables, type errors

Test Parameters:
- Load solution with intentional compilation errors
- Call: roslyn_get_diagnostics with scope="solution"
- Expected: Pre-estimation triggers, progressive reduction applied
- Verify: Insights show "⚠️ Response size limit applied (X tokens)"
- Verify: NextAction provided to get more results with maxResults=500
```

**Scenario 1.2: Single File with Complex Diagnostics**
```bash
# Setup: Large file (2000+ lines) with analyzer diagnostics and detailed properties

Test Parameters:
- File with CA rules, nullable warnings, style violations
- Call: roslyn_get_diagnostics with scope="file", includeAnalyzers=true
- Expected: Complex diagnostics with properties trigger token management
- Verify: EstimateDiagnosticResponseTokens calculates correctly
- Verify: Resource URI stored if truncated
```

**Scenario 1.3: Grouped Diagnostics Token Impact**
```bash
# Setup: Solution with diverse diagnostic categories

Test Parameters:
- Call: roslyn_get_diagnostics with groupBy="Category"
- Expected: Grouping increases token overhead
- Verify: Token estimation accounts for grouping structure
- Verify: Progressive reduction still works with grouped results
```

### 2. DocumentSymbolsTool (Previously NO LIMIT)

**Previous Implementation:** No token limits - could return unlimited symbols
**Current Implementation:** TokenEstimator.CreateTokenAwareResponse with hierarchy awareness

#### Test Scenarios:

**Scenario 2.1: Large File with Deep Symbol Hierarchy**
```bash
# Setup: Large C# file with nested classes, extensive member lists

Test File Structure:
- 5+ nested namespaces
- 20+ classes per namespace  
- 50+ methods/properties per class
- Generic types with type parameters

Test Parameters:
- Call: roslyn_document_symbols with includePrivate=true
- Expected: Hierarchical counting triggers token management
- Verify: CountSymbols() recursive counting works correctly
- Verify: FlattenSymbolHierarchy() when safety limit applied
```

**Scenario 2.2: Generated Code Files**
```bash
# Setup: Auto-generated files (like Designer files, scaffolded code)

Test Parameters:
- Large .Designer.cs file with 1000+ controls
- Call: roslyn_document_symbols with maxResults=100
- Expected: Token management limits results appropriately
- Verify: Insights suggest includePrivate=false for more results
- Verify: NextAction to get more symbols provided
```

**Scenario 2.3: Symbol Kind Filtering with Large Results**
```bash
# Setup: File with many symbol types

Test Parameters:
- Call: roslyn_document_symbols with symbolKinds=["Method", "Property"]
- File with 500+ methods and properties
- Expected: Filtering reduces count but may still hit token limits
- Verify: FilterSymbolsByKind preserves hierarchy correctly
- Verify: Token estimation accounts for filtered results
```

### 3. GetTypeMembersTool (Previously NO LIMIT)

**Previous Implementation:** No token limits - could return all type members
**Current Implementation:** Token-aware response with documentation size considerations

#### Test Scenarios:

**Scenario 3.1: Type with Extensive XML Documentation**
```bash
# Setup: Class with comprehensive XML docs on all members

Test Class Features:
- 100+ methods with detailed <summary>, <param>, <returns>
- Include inherited members from base classes
- Include interface implementations

Test Parameters:
- Call: roslyn_get_type_members with includeDocumentation=true, includeInherited=true
- Expected: Documentation significantly increases token count
- Verify: EstimateTypeMemberTokens accounts for documentation
- Verify: Insight suggests includeDocumentation=false for more results
```

**Scenario 3.2: Framework Types with Large APIs**
```bash
# Setup: Position cursor on large framework-like types

Test Parameters:
- Target: HttpClient, DbContext, or similar types with many members
- Call: roslyn_get_type_members with includeInherited=true
- Expected: Inherited members cause token overflow
- Verify: Progressive reduction maintains most relevant members
- Verify: Resource URI provided for full member list
```

**Scenario 3.3: Generic Types with Complex Signatures**
```bash
# Setup: Generic class with complex method signatures

Test Class Features:
- Generic constraints: where T : IDisposable, new()
- Complex return types: Task<IEnumerable<KeyValuePair<string, object>>>
- Method overloads with multiple parameters

Test Parameters:
- Call: roslyn_get_type_members with maxResults=50
- Expected: Complex signatures increase per-member token cost
- Verify: CreateMemberInfo() generates accurate signatures
- Verify: Token estimation handles complex types correctly
```

### 4. FindAllReferencesTool (Improved from Simple Limit)

**Previous Implementation:** Simple 50-item hard limit
**Current Implementation:** Dynamic token estimation with reference text

#### Test Scenarios:

**Scenario 4.1: Popular Symbol with Many References**
```bash
# Setup: Common utility method called throughout solution

Test Parameters:
- Symbol: Utility method called 500+ times across 50+ files
- Call: roslyn_find_all_references with maxResults=50
- Expected: Token management considers reference text length
- Verify: EstimateReferenceTokens accounts for Text property
- Verify: Distribution by file calculated on full results
```

**Scenario 4.2: String-Heavy Reference Contexts**
```bash
# Setup: Symbol used in contexts with long surrounding text

Test Parameters:
- References in files with long lines (JSON, SQL strings, etc.)
- Call: roslyn_find_all_references
- Expected: Long reference text triggers token limits
- Verify: ReferenceLocation.Text properly estimated
- Verify: Progressive reduction preserves most relevant references
```

**Scenario 4.3: Cross-Solution References**
```bash
# Setup: Symbol referenced across multiple projects

Test Parameters:
- Symbol used in 10+ projects within solution
- Call: roslyn_find_all_references
- Expected: Cross-project references increase token overhead
- Verify: NextActions suggest filtering to specific files
- Verify: Insights show distribution across projects
```

### 5. SymbolSearchTool (Improved from Simple Limit)

**Previous Implementation:** Simple 50-item hard limit
**Current Implementation:** Token-aware search with parallel processing

#### Test Scenarios:

**Scenario 5.1: Broad Search Patterns**
```bash
# Setup: Large solution with many symbol types

Test Parameters:
- Query: "*Service" (wildcard matching many symbols)
- SearchType: "wildcard"
- Expected: Parallel search finds hundreds of matches
- Verify: EstimateSymbolTokens handles SymbolInfo correctly
- Verify: Token management preserves most relevant symbols
```

**Scenario 5.2: Fuzzy Search with Many Results**
```bash
# Setup: Solution with similar naming patterns

Test Parameters:
- Query: "UsrSrvc~" (fuzzy search for UserService variants)
- SearchType: "fuzzy"  
- Expected: Fuzzy matching returns many partial matches
- Verify: Token estimation accounts for FullName and NameSpace
- Verify: Insights show search pattern effectiveness
```

**Scenario 5.3: Symbol Kind Filtering**
```bash
# Setup: Large codebase with diverse symbol types

Test Parameters:
- Query: "*"
- SymbolKinds: ["Class", "Interface", "Method"]
- Expected: Filtering reduces results but may still hit limits
- Verify: ShouldIncludeSymbol() filtering works correctly
- Verify: Progressive reduction maintains symbol diversity
```

### 6. TraceCallStackTool (Depth + Token Limits)

**Previous Implementation:** Only depth limit (10 levels)
**Current Implementation:** Depth + token management for complex call paths

#### Test Scenarios:

**Scenario 6.1: Deep Call Chains with Framework Calls**
```bash
# Setup: Method that makes many framework calls

Test Parameters:
- Direction: "forward"
- MaxDepth: 10
- IncludeFramework: true
- Expected: Framework calls significantly increase token count
- Verify: EstimateCallPathsTokens considers call complexity
- Verify: Token management limits paths appropriately
```

**Scenario 6.2: Backward Tracing from Popular Methods**
```bash
# Setup: Core utility method called from many places

Test Parameters:
- Direction: "backward"  
- MaxPaths: 10
- Expected: Many caller paths found
- Verify: FindCallersAsync returns realistic caller count
- Verify: Progressive reduction preserves most relevant paths
```

**Scenario 6.3: API Endpoint Tracing**
```bash
# Setup: Web API controller action method

Test Parameters:
- Position: Inside API controller method
- Direction: "forward"
- Expected: API flow detection and specialized insights
- Verify: DeterminePathType() identifies "API Flow"
- Verify: KeyFindings detect database operations, validation
```

### 7. RenameSymbolTool (Preview Mode Token Management)

**Previous Implementation:** Basic rename without token considerations
**Current Implementation:** Token-aware preview with change estimation

#### Test Scenarios:

**Scenario 7.1: Renaming Popular Symbol with Many Files**
```bash
# Setup: Rename widely-used interface or base class

Test Parameters:
- Symbol: Interface implemented across 100+ files
- Preview: true
- Expected: Preview shows many file changes
- Verify: EstimateFileChangesTokens handles FileChange complexity
- Verify: Progressive reduction applied to preview
- Verify: NextAction to see all changes provided
```

**Scenario 7.2: Rename with Comments and Strings**
```bash
# Setup: Symbol referenced in comments and string literals

Test Parameters:
- RenameInComments: true
- RenameInStrings: true
- Preview: true
- Expected: Additional changes increase token count
- Verify: GetChangesAsync captures all change types
- Verify: Insights explain change scope and options
```

**Scenario 7.3: Type Rename Affecting File Names**
```bash
# Setup: Rename public class where file should be renamed too

Test Parameters:
- RenameFile: true
- Preview: true
- Expected: File rename operations included in preview
- Verify: Token management handles file operations
- Verify: Preview shows file rename implications clearly
```

### 8. ApplyCodeFixTool (Preview Mode Token Management)

**Previous Implementation:** Basic code fix application
**Current Implementation:** Token-aware preview for multi-file fixes

#### Test Scenarios:

**Scenario 8.1: Solution-Wide Code Fix**
```bash
# Setup: Apply analyzer fix that affects many files

Test Parameters:
- Diagnostic: CA1822 (Make static) applied solution-wide
- Preview: true
- Expected: Multi-file changes trigger token management
- Verify: EstimateFileChangesTokens handles multiple files
- Verify: Progressive reduction preserves most affected files
```

**Scenario 8.2: Complex Refactoring Fix**
```bash
# Setup: Complex code fix with multiple change types

Test Parameters:
- Fix: Extract interface or similar complex refactoring
- Preview: true
- MaxChangedFiles: 20
- Expected: Complex changes increase per-file token cost
- Verify: GetChangesAsync captures all change types correctly
- Verify: Insights explain fix complexity and scope
```

**Scenario 8.3: Bulk Warning Fixes**
```bash
# Setup: Apply bulk fixes for nullable warnings

Test Parameters:
- Apply fixes for CS8618, CS8625, CS8629 warnings
- Preview: true
- Expected: Many small changes across many files
- Verify: Token management handles bulk operations
- Verify: NextActions suggest checking remaining diagnostics
```

## Testing Framework and Validation

### Automated Test Structure

```csharp
[Test]
public async Task TokenManagement_PreEstimation_Works()
{
    // Arrange: Create scenario with known token overhead
    var heavyDiagnostics = CreateDiagnosticsWithLargeProperties(count: 200);
    
    // Act: Call tool
    var result = await tool.ExecuteAsync(parameters);
    
    // Assert: Verify token management behaviors
    Assert.That(result.Meta.Truncated, Is.True, "Should be truncated");
    Assert.That(result.Insights, Contains.Substring("Response size limit applied"));
    Assert.That(result.Actions, Has.Some.Matches<NextAction>(a => a.Id == "get_more_results"));
    Assert.That(result.ResourceUri, Is.Not.Null, "Should store full results");
}
```

### Validation Checklist

For each tool and scenario, verify:

#### ✅ **Pre-estimation Accuracy**
- [ ] Pre-estimated tokens reasonably match actual response size
- [ ] Estimation considers all response components (metadata, insights, actions)
- [ ] Sampling-based estimation is representative

#### ✅ **Progressive Reduction**
- [ ] Reduction steps (100→75→50→30→20→10→5) are followed
- [ ] Reduced results maintain quality and relevance
- [ ] Minimum useful set preserved even at lowest reduction

#### ✅ **Truncation Indicators**
- [ ] `Meta.Truncated` correctly set when truncation occurs
- [ ] Insights include truncation message with token counts
- [ ] `ResultsSummary` shows accurate total vs returned counts

#### ✅ **NextActions for More Results**
- [ ] NextAction with "get_more_results" ID provided when truncated
- [ ] Parameters include appropriate maxResults increase
- [ ] Priority set to "high" for truncation recovery

#### ✅ **Resource Storage**
- [ ] Full results stored as resource when truncated
- [ ] ResourceUri provided in response
- [ ] Resource contains complete data for later retrieval

#### ✅ **Insights Quality**
- [ ] Insights explain why truncation occurred
- [ ] Suggestions provided for getting more results (e.g., excludeDocumentation)
- [ ] Token management transparent to users

#### ✅ **Error Handling**
- [ ] Token estimation errors don't break tools
- [ ] Fallback estimates used when calculation fails
- [ ] Minimum response always provided

#### ✅ **Performance Impact**
- [ ] Pre-estimation doesn't significantly slow tools
- [ ] Progressive reduction happens quickly
- [ ] Token management adds minimal overhead

## Load Testing Scenarios

### High-Volume Tests

**Large Solution Load Test:**
- Solution: 100+ projects, 10,000+ files
- Test all tools against various high-volume scenarios
- Measure: Response times, token accuracy, user experience

**Memory Pressure Test:**
- Run tools under memory constraints
- Verify: Token management prevents memory overflow
- Measure: Memory usage, garbage collection impact

**Concurrent Usage Test:**
- Multiple users triggering token management simultaneously
- Verify: Resource storage handles concurrent access
- Measure: Response consistency, resource conflicts

## Integration Testing

### End-to-End Workflows

**Scenario: Large Codebase Analysis**
1. Load large solution → roslyn_load_solution
2. Get overview → roslyn_get_diagnostics (scope=solution)
3. Focus on issues → roslyn_get_diagnostics (with filters)
4. Explore symbols → roslyn_symbol_search (*Service)
5. Analyze types → roslyn_get_type_members (includeInherited=true)
6. Trace execution → roslyn_trace_call_stack (direction=forward)

**Verification Points:**
- Token management prevents context overflow at each step
- NextActions guide logical workflow progression
- Resource storage enables deep-dive analysis
- User experience remains smooth despite large data volumes

### Cross-Tool Consistency

**Resource Handoff Testing:**
- Tool A stores truncated results as resource
- Tool B references that resource in NextActions
- Verify: Resource availability and format consistency

**Token Limit Consistency:**
- All tools use same DEFAULT_SAFETY_LIMIT (10K tokens)
- Conservative tools use CONSERVATIVE_SAFETY_LIMIT (5K tokens)
- Verify: Consistent user experience across tools

## Success Criteria

### Primary Goals
1. **No Context Window Overflow:** Tools never exceed 10K token responses
2. **Progressive Disclosure:** Users get useful results on first try
3. **Clear Navigation:** NextActions guide users to more complete results
4. **Transparent Operation:** Users understand when and why truncation occurs

### Performance Targets
- Pre-estimation overhead: <100ms
- Progressive reduction: <50ms
- Token management total impact: <10% of tool execution time

### User Experience Goals
- Users can work with large codebases without hitting context limits
- Truncation feels helpful, not restrictive
- Full data remains accessible through NextActions and resources
- Token management is invisible when not needed

## Implementation Notes

### Key Patterns from CodeSearch MCP

The token management implementation follows proven patterns from COA CodeSearch MCP:

1. **Pre-estimation with Sampling:** Use first 5 items to estimate total response size
2. **Safety Limits:** Conservative 10K token limit (5% of context window)
3. **Progressive Reduction:** Standardized reduction steps preserve quality
4. **Resource Storage:** Full results available for deep-dive analysis
5. **NextActions Integration:** Seamless workflow despite truncation

### Critical Implementation Details

- **Hierarchical Counting:** DocumentSymbolsTool counts nested symbols recursively
- **Reference Text Estimation:** FindAllReferencesTool estimates reference context text
- **Documentation Impact:** GetTypeMembersTool accounts for XML documentation size
- **Preview Mode Optimization:** Rename and CodeFix tools handle large previews efficiently
- **Path Complexity:** TraceCallStackTool considers call complexity in token estimation

This comprehensive testing plan ensures the token management implementation prevents context window overflow while maintaining excellent user experience and tool functionality.