# Global Middleware Implementation Plan

## Overview
Implementation plan for adding global middleware support to COA MCP Framework, specifically for type verification and TDD enforcement.

## Current State Analysis

### Changes Made:
1. **McpToolBase.cs**: Modified to accept IServiceProvider in constructor to resolve global middleware from DI
   - Added _globalMiddleware field to store resolved middleware
   - Split Middleware property into ToolSpecificMiddleware and combined Middleware
   - Constructor now resolves IEnumerable<ISimpleMiddleware> from DI
2. **DisposableToolBase.cs**: Updated constructor to pass IServiceProvider to base class
3. **McpToolRegistry.cs**: Enhanced to skip test assemblies and classes to avoid registration warnings
4. **Documentation**: Comprehensive updates to README, CHANGELOG, lifecycle-hooks.md, VALIDATION_AND_ERROR_HANDLING.md, and CLAUDE.md

### Test Coverage Status:
- TypeVerificationMiddleware: 17 tests
- TddEnforcementMiddleware: 6 tests  
- VerificationStateManager: 26 tests
- DefaultTestStatusService: 29 tests
- Total Middleware Tests: ~78 tests

### Current Issues:
1. **Compilation Errors**: 9 test files failing due to constructor signature change
   - Test tools passing ILogger instead of IServiceProvider as first parameter
   - Affects 4 test files with ~9 test tool classes
2. **Missing Implementation**:
   - WithGlobalMiddleware() method not found in McpServerBuilder
   - Global middleware registration mechanism incomplete
3. **Example Tools**: 4 example tools need updating to new constructor signature

## Comprehensive Fix Plan

### Phase 1: Fix Test Compilation Errors
Update all test tool constructors in:
- McpToolBaseGenericTests.cs (3 instances)
- DisposableToolBaseTests.cs (2 instances)  
- BuiltInMiddlewareTests.cs (2 instances)
- SimpleMiddlewareTests.cs (2 instances)

Change from: `base(logger)` to: `base(null, logger)`

### Phase 2: Add Global Middleware Registration
Add dual registration approaches:

#### Option A: Instance-based registration
```csharp
public McpServerBuilder WithGlobalMiddleware(IEnumerable<ISimpleMiddleware> middleware)
{
    foreach (var m in middleware)
    {
        _services.AddSingleton<ISimpleMiddleware>(m);
    }
    return this;
}
```

#### Option B: Type-based registration  
```csharp
public McpServerBuilder AddMiddleware<TMiddleware>()
    where TMiddleware : class, ISimpleMiddleware
{
    _services.AddSingleton<ISimpleMiddleware, TMiddleware>();
    return this;
}
```

### Phase 3: Update Example Tools
Update 4 example tools to new constructor signature:
- MetricsDemoTool.cs
- SearchDemoTool.cs
- LifecycleExampleTool.cs
- WeatherExample.cs

### Phase 4: Add Integration Tests
Create integration test for global middleware:
- Test that global middleware affects all tools
- Test middleware ordering (global + tool-specific)
- Test DI resolution of middleware
- Test that tools without IServiceProvider still work

### Phase 5: Verify Test Coverage
1. Run all tests to ensure 100% pass rate
2. Add missing test scenarios:
   - Global middleware injection
   - Combined middleware ordering
   - Null IServiceProvider handling
   - Middleware enabling/disabling

### Phase 6: Update Documentation
1. Add example of registering global middleware via DI
2. Document the new constructor parameter
3. Add migration guide for existing tools

## Summary of Required Changes
- 9 test tool classes need constructor updates
- 4 example tools need constructor updates
- 1 new method in McpServerBuilder for middleware registration
- ~5-10 new integration tests for global middleware functionality
- Documentation updates for migration guide

## Type Verification Integration
The TypeVerificationMiddleware and TddEnforcementMiddleware are already registered in ServiceCollectionExtensions.cs:

```csharp
services.TryAddScoped<TypeVerificationMiddleware>();
services.TryAddScoped<TddEnforcementMiddleware>();
```

These will be automatically discovered and injected into all tools that use the updated McpToolBase constructor.

## CodeNav Integration Plan
Once framework v1.7.23+ is published:
1. Update CodeNav package references
2. Update all 43 tool constructors to pass IServiceProvider
3. Test that Edit/Write operations are blocked for unverified types
4. Verify Hover/GoToDefinition operations populate verification cache

This completes the global middleware implementation and enables type verification enforcement across all MCP tools.