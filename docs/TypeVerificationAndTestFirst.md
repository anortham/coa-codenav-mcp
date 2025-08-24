# Enforcing type verification in Claude Code

Claude Code's tendency to make incorrect assumptions about types despite having LSP tools available is a **documented and widespread problem** that the developer community has been actively addressing. The good news is that **concrete blocking mechanisms exist** that can force type verification before code generation, though they require custom implementation rather than native support.

The core issue, documented in GitHub issue #1315 and numerous community discussions, is that Claude Code lacks semantic code understanding and defaults to text-based assumptions rather than using available LSP capabilities. This leads to broken code requiring extensive debugging - exactly the problem you're experiencing. The community consensus is clear: soft approaches like CLAUDE.md entries and prompting consistently fail because Claude simply ignores them when generating code.

## FastMCP middleware provides the strongest enforcement capability

The most robust technical solution for enforcement comes through **FastMCP's middleware system**, which provides hierarchical hooks that can intercept and block requests at multiple levels. Unlike the core MCP specification which lacks native enforcement capabilities, FastMCP enables concrete blocking through its `on_call_tool` hooks that can terminate request chains without execution. This isn't just theory - the middleware can return error responses that completely prevent code generation until verification requirements are satisfied.

The middleware architecture supports request/response interception, state management across sessions, and dynamic tool filtering based on verification status. You can implement a verification gateway that maintains session state, tracks which types have been verified, and blocks any code generation requests until the verification requirements are met. The key is that this happens at the protocol level, making it impossible for Claude to bypass.

## TDD Guard demonstrates working enforcement patterns

**TDD Guard** provides the most immediately applicable example of enforcement that actually works. It hooks into Claude Code's file operations (`Write|Edit|MultiEdit|TodoWrite`) and validates TDD compliance before allowing execution. When a violation is detected, it returns a `block` decision that prevents the operation entirely - not just a warning or suggestion. This same pattern can be adapted for type verification by intercepting code generation requests and validating that type checking has occurred first.

The enforcement mechanism uses pre-execution hooks that receive the full context of the requested operation. The validation logic can check session state, examine previous tool calls, and verify that appropriate LSP operations have been performed. If verification hasn't occurred, the hook blocks execution and returns a message explaining what needs to happen first. This creates an unbypassable workflow where type verification becomes mandatory.

## Community-built LSP integrations address the root problem

The community has developed **CCLSP (Claude Code LSP)** specifically to address Claude's poor LSP integration. It provides intelligent position combination that handles Claude's line/column accuracy issues, robust symbol resolution, and multi-language support including TypeScript and C#. The critical feature is that it makes LSP operations reliable enough to enforce as prerequisites. When combined with enforcement middleware, you can require successful LSP type queries before allowing code generation.

Several teams have successfully implemented verification workflows by combining CCLSP with custom MCP servers. The pattern involves creating a wrapper server that proxies requests to CCLSP for type information, maintains verification state, and only enables code generation tools after successful type validation. This creates a two-phase workflow where exploration and verification must precede implementation.

## Implementation requires a multi-layer enforcement architecture

Based on the research, the most effective implementation combines several enforcement layers. First, deploy a FastMCP-based verification gateway server that acts as the primary enforcement point. This server implements middleware that intercepts all tool calls and maintains session-based verification state. Code generation tools are dynamically filtered based on verification status - they simply don't appear as available until types are verified.

Second, implement a state machine that tracks verification workflow stages: unverified, verifying, verified, failed, and expired. Each state has allowed transitions, and code generation is only permitted in the verified state. The state machine prevents circumvention by requiring proper progression through verification stages. Session data persists across requests, maintaining verification status even if Claude attempts multiple approaches.

Third, use the Chain of Responsibility pattern to create a verification pipeline. Start with syntax verification, then type verification via LSP, then compilation checks for C#. Each handler can block progression if its requirements aren't met. This ensures comprehensive validation before code generation becomes available.

## Technical implementation with concrete code patterns

The implementation uses TypeScript or Python with the FastMCP framework. Here's the core enforcement pattern that actually blocks code generation:

The verification middleware intercepts tool calls and checks session state. For any code generation request, it queries the session manager to determine if verification has occurred. If not, it returns an error response without calling the next handler - completely blocking execution. The session manager tracks verification status with timestamps and expiration, ensuring verification remains current.

Tool registration includes both verification and generation tools, but generation tools check session state before execution. The verify-types tool performs LSP queries, compilation checks, and updates session state upon success. Only after successful verification does the session allow code generation. This creates an enforced workflow where Claude must verify types first because generation simply won't work otherwise.

For .NET specifically, integrate with the C# Language Server via CCLSP or the dotnet-language-server. The verification process includes syntax checking via Roslyn, type resolution through the language server, and compilation verification. Cache verified types to avoid repeated checks, but require re-verification after significant code changes.

## Existing tools can be combined for immediate implementation

Rather than building everything from scratch, combine existing tools for faster implementation. Use **MCP-Scan** or **MCP-Context-Protector** as the base wrapper, providing security controls and request interception. Add CCLSP for reliable LSP integration. Implement custom guardrails in MCP-Scan's configuration that enforce type verification rules. This combination provides production-ready enforcement with minimal custom development.

Deploy the solution using Docker with the MCP catalog for easy distribution. Configure rate limiting to prevent verification bypass attempts. Use HTTP+SSE transport rather than stdio for better state management and authentication support. Monitor all verification attempts and blocked operations through comprehensive logging.

## Test-first approaches provide complementary enforcement

While addressing type verification directly, consider implementing TDD enforcement as a complementary strategy. TDD Guard's blocking mechanisms naturally encourage proper type definition since tests must compile and run. For C# development, combine with continuous testing in Visual Studio or Rider, creating tight feedback loops that catch type errors immediately.

Configure coverage gates in CI/CD pipelines that block merges below threshold. Use mutation testing with Stryker.NET to ensure test quality. These mechanisms create multiple enforcement points that collectively prevent assumption-based coding.

## Practical deployment recommendations

Start with TDD Guard for immediate enforcement capability while building the custom type verification server. It's available now and demonstrates that blocking enforcement works. Configure CCLSP for reliable LSP integration across your TypeScript and C# projects. This addresses the root cause of poor type information.

Build a FastMCP-based verification gateway using the documented patterns. Start with basic session management and type verification, then add compilation checks and caching. Deploy initially as a stdio server for development, then migrate to HTTP+SSE for production. This provides the full enforcement architecture.

Document the verification workflow in your CLAUDE.md file, but rely on technical enforcement rather than instructions. Include commands for manual verification when needed, but make the automated enforcement the primary mechanism. Share server configurations across your team for consistent enforcement.

## Conclusion

Enforcing type verification in Claude Code is technically feasible through FastMCP middleware, custom MCP servers, and hook-based enforcement mechanisms like TDD Guard. The key insight is that blocking must occur at the protocol level through middleware and state management, not through prompts or configuration files. By combining existing tools like CCLSP for LSP integration with custom verification gateways, you can create an environment where Claude Code simply cannot generate code without first verifying types. The community has proven these patterns work - the challenge is implementation rather than feasibility.