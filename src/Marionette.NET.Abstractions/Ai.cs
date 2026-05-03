// Marionette.NET — Channel-Push surface
//
// `Ai.Trigger` and `Ai.ScheduleTrigger` are the one-way channel from the
// application back to the LLM. They allow user code to push prompts that
// reflect domain events ("user added an item", "background sync finished")
// without taking a hard reference on the runtime assembly.
//
// Stripping contract:
//   - Both methods are decorated with [Conditional("MCP_ENABLED")], so the
//     C# compiler elides every CALL SITE in builds where MCP_ENABLED is not
//     defined. The methods themselves remain in this assembly, but they are
//     unreachable in stripped builds and trim out cleanly.
//   - Internal hooks (TriggerHook, ScheduleTriggerHook) are populated by
//     Marionette.Runtime when the runtime is loaded. The Abstractions
//     assembly never references the runtime.
//   - `Ai.IsActive` is intentionally NOT [Conditional] — adopters need to
//     query it from non-conditional code paths (e.g. to skip expensive work
//     when no LLM is listening).

using System;
using System.Diagnostics;

namespace Marionette;

/// <summary>
/// Channel-push surface. Allows user code to send notifications back to the
/// LLM without a hard dependency on the runtime assembly.
/// </summary>
/// <remarks>
/// <para>
/// Both <see cref="Trigger(string)"/> and
/// <see cref="ScheduleTrigger(TimeSpan, string)"/> are conditional methods —
/// the C# compiler removes every call site in builds that do not define the
/// <c>MCP_ENABLED</c> constant. Adopters can therefore sprinkle
/// <c>Ai.Trigger("…")</c> through domain code without paying any cost in
/// stripped Release builds.
/// </para>
/// <para>
/// At runtime, when Marionette's runtime assembly is loaded, it installs
/// callbacks into <see cref="TriggerHook"/> and
/// <see cref="ScheduleTriggerHook"/>. Until that happens (or in builds where
/// the runtime is never referenced), the methods are effectively no-ops.
/// </para>
/// </remarks>
public static class Ai
{
    // The hook fields are populated reflectively from another assembly (the
    // runtime, via InternalsVisibleTo). The C# compiler cannot see those
    // assignments and would otherwise emit CS0649 ("field is never assigned
    // and will always have its default value"). The pragma scopes the
    // suppression precisely to these two declarations.
#pragma warning disable CS0649

    /// <summary>
    /// Internal callback set by <c>Marionette.Runtime</c> when the runtime is
    /// loaded. Receives the prompt passed to <see cref="Trigger(string)"/>
    /// and is responsible for forwarding it to the LLM session.
    /// </summary>
    /// <remarks>
    /// Internal access is intentional: only the runtime assembly may install
    /// the hook, and only the public <see cref="Trigger(string)"/> method
    /// invokes it. Keeping this internal preserves the one-way contract.
    /// </remarks>
    internal static Action<string>? TriggerHook;

    /// <summary>
    /// Internal callback set by <c>Marionette.Runtime</c> when the runtime is
    /// loaded. Receives the delay and prompt passed to
    /// <see cref="ScheduleTrigger(TimeSpan, string)"/> and is responsible for
    /// forwarding the scheduled notification to the LLM session.
    /// </summary>
    internal static Action<TimeSpan, string>? ScheduleTriggerHook;

#pragma warning restore CS0649

    /// <summary>
    /// Indicates whether a Marionette runtime is currently loaded and
    /// listening for channel-push notifications.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when the runtime has installed its callbacks;
    /// <see langword="false"/> in stripped builds, in builds where the
    /// runtime DLL is not referenced, or while the runtime is still
    /// initializing.
    /// </value>
    /// <remarks>
    /// This property is intentionally NOT marked <see cref="ConditionalAttribute"/>
    /// so adopters can guard non-trivial work behind it from any code path:
    /// <code>
    /// if (Ai.IsActive)
    /// {
    ///     var summary = ExpensiveSummary();
    ///     Ai.Trigger($"State changed: {summary}");
    /// }
    /// </code>
    /// In stripped builds the property is reachable but always returns
    /// <see langword="false"/>, because <see cref="TriggerHook"/> can only be
    /// populated by the runtime — which is itself absent from stripped
    /// builds.
    /// </remarks>
    public static bool IsActive => TriggerHook is not null;

    /// <summary>
    /// Pushes a one-way prompt to the LLM session. Intended for narrating
    /// domain events ("background sync finished", "validation failed", etc.)
    /// so the LLM can react without explicit polling.
    /// </summary>
    /// <param name="prompt">
    /// The prompt text to deliver to the LLM. Should be a single
    /// human-readable sentence; runtime may prepend metadata (timestamp,
    /// source root) before forwarding.
    /// </param>
    /// <remarks>
    /// <para>
    /// This method is decorated with
    /// <c>[Conditional("MCP_ENABLED")]</c>. In builds where
    /// <c>MCP_ENABLED</c> is not defined the C# compiler elides every call
    /// site, so the method body is unreachable and trims away cleanly.
    /// </para>
    /// <para>
    /// When MCP is enabled but the runtime has not yet installed
    /// <see cref="TriggerHook"/>, the call is silently dropped. Use
    /// <see cref="IsActive"/> to gate non-trivial work that should only run
    /// when the LLM is listening.
    /// </para>
    /// </remarks>
    [Conditional("MCP_ENABLED")]
    public static void Trigger(string prompt)
    {
        TriggerHook?.Invoke(prompt);
    }

    /// <summary>
    /// Pushes a deferred prompt to the LLM session, to be delivered after the
    /// specified delay. Useful for nudging the LLM to follow up on
    /// long-running operations without busy-looping.
    /// </summary>
    /// <param name="delay">Time to wait before delivering the prompt.</param>
    /// <param name="prompt">
    /// The prompt text to deliver to the LLM after the delay elapses.
    /// </param>
    /// <remarks>
    /// <para>
    /// This method is decorated with
    /// <c>[Conditional("MCP_ENABLED")]</c>. In builds where
    /// <c>MCP_ENABLED</c> is not defined the C# compiler elides every call
    /// site.
    /// </para>
    /// <para>
    /// When MCP is enabled but the runtime has not yet installed
    /// <see cref="ScheduleTriggerHook"/>, the call is silently dropped. The
    /// runtime is responsible for cancelling pending schedules at shutdown
    /// so the timer never outlives the LLM session.
    /// </para>
    /// </remarks>
    [Conditional("MCP_ENABLED")]
    public static void ScheduleTrigger(TimeSpan delay, string prompt)
    {
        ScheduleTriggerHook?.Invoke(delay, prompt);
    }
}
