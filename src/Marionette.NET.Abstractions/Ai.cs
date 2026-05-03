// Marionette.NET — Channel-Push stub
// Calls to Ai.Trigger / Ai.ScheduleTrigger compile to no-ops unless MCP_ENABLED
// is defined. The [Conditional] attribute causes the C# compiler to elide the
// CALL SITES entirely, so user code that uses Ai still strips cleanly.

using System;
using System.Diagnostics;

namespace Marionette;

/// <summary>
/// Channel-push surface. Allows user code to send notifications back to the LLM
/// without a hard dependency on the runtime assembly. When <c>MCP_ENABLED</c> is
/// not defined, both calls vanish at the call site (Conditional attribute).
/// </summary>
public static class Ai
{
    /// <summary>
    /// Notify the LLM with a prompt. Compiled out unless <c>MCP_ENABLED</c> is defined.
    /// </summary>
    [Conditional("MCP_ENABLED")]
    public static void Trigger(string prompt)
    {
        // Spike A: no-op stub. Phase 1 will route this to the runtime channel.
        _ = prompt;
    }

    /// <summary>
    /// Schedule a trigger after the given delay. Compiled out unless <c>MCP_ENABLED</c> is defined.
    /// </summary>
    [Conditional("MCP_ENABLED")]
    public static void ScheduleTrigger(TimeSpan delay, string prompt)
    {
        _ = delay;
        _ = prompt;
    }
}
