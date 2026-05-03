// Marionette.NET — Spike C smoke-test tool
//
// The minimum viable MCP tool: takes nothing, returns "pong". Used by
// the Spike C stdio harness to confirm tools/list and tools/call round-trip.
//
// We deliberately use the explicit, non-reflective registration path:
//   builder.WithTools<PingTool>()
// rather than WithToolsFromAssembly. The 1.2.0 docs explicitly call out the
// generic form as the AOT-friendly choice, which matches Spike B's findings.
//
// Phase 1 will replace this with a ManifestRegistry-backed dispatcher; the
// Marionette user attribute set ([McpCallable] etc.) lives in Abstractions and
// is intentionally distinct from ModelContextProtocol's own attributes.

using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Marionette.Runtime;

/// <summary>
/// Built-in smoke-test tool. <c>marionette_ping</c> always returns the literal
/// string <c>"pong"</c>. Phase 1 may keep it as a liveness probe but the real
/// surface area will come from user-attributed methods discovered by the
/// Source Generator.
/// </summary>
[McpServerToolType]
public sealed class PingTool
{
    [McpServerTool(Name = "marionette_ping")]
    [Description("Liveness probe. Always returns the string \"pong\".")]
    public string Ping() => "pong";
}
