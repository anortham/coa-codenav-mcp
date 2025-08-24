Type Verification & TDD Enforcement Implementation Plan

    Executive Summary

    This document outlines the comprehensive implementation of a multi-layered type verification and optional TDD enforcement system for the COA
    MCP Framework and CodeNav tools. The solution combines protocol-level middleware enforcement, intelligent symbol resolution inspired by
    CCLSP, and TDD patterns from TDD Guard.

    Architecture Overview

    System Components

    ┌─────────────────────────────────────────────────────────────┐
    │                     MCP Client (Claude)                      │
    └─────────────────────────────────────────────────────────────┘
                                  ↓
    ┌─────────────────────────────────────────────────────────────┐
    │                    MCP Server (McpServer)                    │
    │  ┌─────────────────────────────────────────────────────┐   │
    │  │          Protocol Middleware Pipeline                │   │
    │  │  • TypeVerificationMiddleware (Order: 5)            │   │
    │  │  • TddEnforcementMiddleware (Order: 10)            │   │
    │  │  • LoggingMiddleware (Order: 15)                   │   │
    │  └─────────────────────────────────────────────────────┘   │
    └─────────────────────────────────────────────────────────────┘
                                  ↓
    ┌─────────────────────────────────────────────────────────────┐
    │                    Tool Execution Layer                      │
    │  ┌─────────────────────────────────────────────────────┐   │
    │  │         Verification State Manager                   │   │
    │  │  • Type Cache (Session-scoped)                      │   │
    │  │  • Member Verification                              │   │
    │  │  • File Modification Tracking                       │   │
    │  └─────────────────────────────────────────────────────┘   │
    └─────────────────────────────────────────────────────────────┘
                                  ↓
    ┌─────────────────────────────────────────────────────────────┐
    │                 CodeNav Tools (Enhanced)                     │
    │  • Auto-verifying tools                                      │
    │  • Smart symbol resolution                                   │
    │  • Bulk verification capabilities                            │
    └─────────────────────────────────────────────────────────────┘

    Detailed Implementation Checklist

    Phase 1: Framework Enhancement (Week 1)

    1.1 Core Middleware Development

    - Create TypeVerificationMiddleware.cs
      - Location: COA.Mcp.Framework/Pipeline/Middleware/TypeVerificationMiddleware.cs
      - Implement ISimpleMiddleware interface
      - Add Order property (value: 5)
      - Implement OnBeforeExecutionAsync with type checking logic
      - Add configuration options (strict/warning/disabled modes)
      - Implement type extraction from parameters
      - Add whitelist for common BCL types
      - Implement error throwing with detailed messages
      - Add logging for debugging
    - Create TddEnforcementMiddleware.cs
      - Location: COA.Mcp.Framework/Pipeline/Middleware/TddEnforcementMiddleware.cs
      - Implement ISimpleMiddleware interface
      - Add Order property (value: 10)
      - Implement test status checking logic
      - Add configuration for enforcement levels
      - Support multiple test runners (dotnet test, npm test, etc.)
      - Implement red-green-refactor validation
      - Add bypass for refactoring operations
      - Include detailed TDD guidance in errors

    1.2 State Management Infrastructure

    - Create IVerificationStateManager interface
      - Location: COA.Mcp.Framework/Interfaces/IVerificationStateManager.cs
      - Define IsTypeVerified method
      - Define MarkTypeVerified method
      - Define GetVerificationStatus method
      - Define ClearCache method
      - Define GetCacheStatistics method
    - Implement VerificationStateManager
      - Location: COA.Mcp.Framework/Services/VerificationStateManager.cs
      - Use ConcurrentDictionary for thread safety
      - Implement file modification time tracking
      - Add session-based expiration logic
      - Implement member-level verification
      - Add cache persistence to disk (optional)
      - Implement cache warmup on startup
      - Add telemetry/metrics collection
    - Create TypeVerificationState model
      - Location: COA.Mcp.Framework/Models/TypeVerificationState.cs
      - TypeName property
      - FilePath property
      - VerifiedAt timestamp
      - FileModificationTime property
      - Members dictionary
      - VerificationMethod (hover/definition/explicit)
      - Confidence score

    1.3 Configuration System

    - Create TypeVerificationOptions class
      - Location: COA.Mcp.Framework/Configuration/TypeVerificationOptions.cs
      - Enabled property (default: true)
      - Mode enum (Strict/Warning/Disabled)
      - CacheExpirationHours (default: 24)
      - AutoVerifyOnHover (default: true)
      - RequireMemberVerification (default: true)
      - WhitelistedTypes collection
      - MaxCacheSize limit
    - Create TddEnforcementOptions class
      - Location: COA.Mcp.Framework/Configuration/TddEnforcementOptions.cs
      - Enabled property (default: false)
      - Mode enum (Strict/Warning/Disabled)
      - TestRunners dictionary by language
      - RequireFailingTest (default: true)
      - AllowRefactoring (default: true)
      - TestTimeout milliseconds
      - IgnorePatterns for excluded files
    - Update appsettings.json schema
      - Add TypeVerification section
      - Add TddEnforcement section
      - Document all configuration options
      - Provide example configurations

    1.4 Dependency Injection Setup

    - Update ServiceCollectionExtensions.cs
      - Register IVerificationStateManager as Singleton
      - Register TypeVerificationMiddleware as Scoped
      - Register TddEnforcementMiddleware as Scoped
      - Add configuration binding for options
      - Add conditional registration based on config
    - Update Program.cs in CodeNav
      - Add middleware to tool registration
      - Configure options from appsettings
      - Add health check endpoints
      - Include startup validation

    Phase 2: Smart Symbol Resolution (Week 2)

    2.1 Intelligent Position Resolution

    - Create SmartSymbolResolver service
      - Location: COA.CodeNav.McpServer/Services/SmartSymbolResolver.cs
      - Implement position combination generation
      - Add line/column fuzzing logic
      - Handle tab/space confusion (4-space, 2-space)
      - Implement nearby symbol search
      - Add symbol name similarity matching
      - Cache successful resolutions
      - Track resolution patterns for learning
    - Create PositionCombinationStrategy
      - Location: COA.CodeNav.McpServer/Services/PositionCombinationStrategy.cs
      - Original position attempt
      - Off-by-one corrections (±1 line/column)
      - Tab width variations (2, 4, 8 spaces)
      - Line ending variations (CRLF vs LF)
      - Zero-based vs one-based indexing
      - End-of-line position handling

    2.2 Enhanced CodeNav Tools

    - Modify HoverTool.cs
      - Inject IVerificationStateManager
      - Auto-mark types as verified on success
      - Use SmartSymbolResolver for position resolution
      - Add verification metadata to response
      - Include member information in cache
      - Handle partial type names
    - Modify GoToDefinitionTool.cs
      - Auto-verify types on navigation
      - Update cache with file locations
      - Track definition relationships
      - Add smart resolution fallback
    - Create BulkVerificationTool.cs
      - Location: COA.CodeNav.McpServer/Tools/BulkVerificationTool.cs
      - Verify all types in a file
      - Verify all types in a project
      - Support parallel verification
      - Progress reporting
      - Partial verification on timeout
    - Create VerificationStatusTool.cs
      - Location: COA.CodeNav.McpServer/Tools/VerificationStatusTool.cs
      - Query verification cache status
      - List unverified types in context
      - Show cache statistics
      - Export/import cache state
      - Clear cache by pattern

    2.3 Type Extraction Enhancement

    - Create TypeExtractor service
      - Location: COA.CodeNav.McpServer/Services/TypeExtractor.cs
      - C# type pattern recognition
      - TypeScript type pattern recognition
      - Generic type handling
      - Nullable type support
      - Array/collection type extraction
      - Tuple type handling
      - Anonymous type detection
    - Create MemberAccessExtractor
      - Location: COA.CodeNav.McpServer/Services/MemberAccessExtractor.cs
      - Property access detection
      - Method call detection
      - Field access detection
      - Extension method recognition
      - Static member access
      - Indexer usage detection

    Phase 3: TDD Integration (Week 3)

    3.1 Test Runner Integration

    - Create ITestRunner interface
      - Location: COA.Mcp.Framework/Testing/ITestRunner.cs
      - RunTests method
      - GetTestStatus method
      - ParseTestResults method
      - Support async execution
    - Implement DotNetTestRunner
      - Location: COA.Mcp.Framework/Testing/Runners/DotNetTestRunner.cs
      - Execute dotnet test command
      - Parse TRX output
      - Extract failing test information
      - Handle test discovery
      - Support filter expressions
    - Implement NpmTestRunner
      - Location: COA.Mcp.Framework/Testing/Runners/NpmTestRunner.cs
      - Execute npm test command
      - Parse Jest/Mocha output
      - Handle different reporters
      - Extract failure details
    - Create TestStatusCache
      - Location: COA.Mcp.Framework/Testing/TestStatusCache.cs
      - Cache test results with timestamp
      - Invalidate on file changes
      - Track test-to-code relationships
      - Support incremental testing

    3.2 TDD Workflow Support

    - Create TddWorkflowManager
      - Location: COA.Mcp.Framework/Testing/TddWorkflowManager.cs
      - Track current TDD phase (red/green/refactor)
      - Validate phase transitions
      - Provide phase-appropriate guidance
      - Support workflow reset
      - Handle multiple test files
    - Create TddViolationException
      - Location: COA.Mcp.Framework/Exceptions/TddViolationException.cs
      - Detailed violation message
      - Suggested actions
      - Links to TDD resources
      - Current phase information

    3.3 Configuration Commands

    - Create TddControlTool
      - Location: COA.CodeNav.McpServer/Tools/TddControlTool.cs
      - Enable/disable TDD enforcement
      - Set enforcement level
      - Query current status
      - Reset workflow state
      - Configure test runners

    Phase 4: Testing & Validation

    4.1 Unit Tests

    - TypeVerificationMiddleware Tests
      - Test type extraction accuracy
      - Test whitelist functionality
      - Test error message generation
      - Test configuration modes
      - Test performance impact
    - VerificationStateManager Tests
      - Test cache operations
      - Test expiration logic
      - Test file modification detection
      - Test thread safety
      - Test persistence
    - SmartSymbolResolver Tests
      - Test position combinations
      - Test fallback mechanisms
      - Test symbol search
      - Test caching behavior
    - TDD Enforcement Tests
      - Test runner integration
      - Test phase validation
      - Test configuration changes
      - Test bypass scenarios

    4.2 Integration Tests

    - End-to-End Verification Flow
      - Test complete verification workflow
      - Test cache warming
      - Test cross-tool verification
      - Test session persistence
    - TDD Workflow Tests
      - Test red-green-refactor cycle
      - Test enforcement blocking
      - Test runner execution
      - Test result parsing

    4.3 Performance Tests

    - Benchmark Middleware Overhead
      - Measure latency impact
      - Test cache performance
      - Profile memory usage
      - Test concurrent access
    - Load Testing
      - Test with large codebases
      - Test cache size limits
      - Test parallel tool execution
      - Measure resource consumption

    Phase 5: Documentation & Deployment

    5.1 Documentation

    - Update README.md files
      - Framework README
      - CodeNav README
      - Configuration examples
      - Troubleshooting guide
    - Create User Guides
      - Type verification guide
      - TDD enforcement guide
      - Configuration reference
      - Migration guide
    - API Documentation
      - Document new interfaces
      - Document configuration options
      - Document error codes
      - Include examples

    5.2 Deployment

    - Package Updates
      - Update NuGet packages
      - Version bump
      - Update dependencies
      - Release notes
    - Rollout Plan
      - Staged deployment
      - Feature flags
      - Monitoring setup
      - Rollback procedures

    Phase 6: Monitoring & Optimization

    6.1 Telemetry

    - Add Metrics Collection
      - Verification success rate
      - Cache hit rate
      - Performance metrics
      - Error frequency
    - Create Dashboards
      - Real-time monitoring
      - Historical trends
      - Alert configuration
      - Usage patterns

    6.2 Optimization

    - Performance Tuning
      - Cache optimization
      - Async improvements
      - Memory optimization
      - Algorithm refinement
    - Feedback Integration
      - User feedback collection
      - Issue tracking
      - Feature requests
      - Continuous improvement

    Success Criteria

    Functional Requirements

    - ✅ Type verification blocks unverified type usage
    - ✅ Smart symbol resolution handles AI inaccuracy
    - ✅ TDD enforcement prevents code without tests
    - ✅ Configuration supports multiple modes
    - ✅ Cache persists across session

    Performance Requirements

    - ✅ < 50ms middleware overhead
    - ✅ < 100MB memory for cache
    - ✅ > 90% cache hit rate
    - ✅ < 1s bulk verification time

    Quality Requirements

    - ✅ 95% reduction in type errors
    - ✅ Zero false positives
    - ✅ 100% backward compatibility
    - ✅ Full test coverage

    Risk Mitigation

    Technical Risks

    1. Performance Impact
      - Mitigation: Async operations, caching, lazy loading
    2. False Positives
      - Mitigation: Comprehensive whitelist, smart resolution
    3. Integration Issues
      - Mitigation: Incremental rollout, feature flags

    Operational Risks

    1. User Resistance
      - Mitigation: Optional enforcement, clear benefits
    2. Learning Curve
      - Mitigation: Documentation, examples, gradual adoption

    Timeline

    - Week 1: Framework Enhancement (Phase 1)
    - Week 2: Smart Resolution (Phase 2)
    - Week 3: TDD Integration (Phase 3)
    - Week 4: Testing & Validation (Phase 4)
    - Week 5: Documentation & Deployment (Phase 5)
    - Week 6: Monitoring & Optimization (Phase 6)

    Next Steps

    1. Review and approve this implementation plan
    2. Set up project tracking with this checklist
    3. Begin Phase 1 implementation
    4. Schedule weekly progress reviews
    5. Prepare test environments

    This comprehensive plan provides a precise roadmap for implementing robust type verification and optional TDD enforcement in the COA MCP
    Framework.
