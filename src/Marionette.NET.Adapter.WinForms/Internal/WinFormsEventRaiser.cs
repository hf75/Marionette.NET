// Marionette.NET — Windows Forms event raiser (Phase 15)
//
// WinForms uses plain CLR events on Controls (Click, TextChanged, KeyDown, ...)
// rather than WPF/Avalonia routed events. To raise an event by name we walk
// the control's type chain looking for a non-public field of type
// EventHandlerList-keyed-by-Object that backs the event, OR we resolve the
// `On<EventName>` virtual via reflection and invoke it.
//
// The OnXxxEvent path is preferred — it's the documented framework pattern
// (Control.OnClick, Control.OnTextChanged, etc.) and produces the same
// observable behaviour as a real user-driven event because Control.OnXxx is
// what the real input pipeline calls internally.
//
// Threading: UI-thread-only. Caller (the adapter) wraps in DispatchAsync.
//
// AOT: like the WPF adapter, this path is fundamentally reflection-based —
// the user-supplied event name resolves at runtime. We therefore mark the
// invoking adapter method with [RequiresUnreferencedCode] and the same
// adopter-facing message: prefer [McpRaisable] (Phase 12.2 catalog) or
// [McpCallable] + invoke_method for AOT-clean event firing.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Windows.Forms;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.WinForms.Internal;

internal static class WinFormsEventRaiser
{
    /// <summary>
    /// Raise a CLR event on <paramref name="target"/> by name. Returns
    /// <see langword="true"/> on success.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern",
        Justification = "Phase 15: WinForms event lookup walks types via reflection. Same trim contract as WPF/WinUI/MAUI adapters.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Phase 15: WinForms event lookup walks types via reflection. Same trim contract as WPF/WinUI/MAUI adapters.")]
    public static bool Raise(
        Control target,
        string eventName,
        IReadOnlyDictionary<string, object?>? args,
        ILogger log)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrEmpty(eventName)) return false;
        _ = args;

        // Phase 12.2: try the AOT-clean source-gen catalog first, same as the
        // WPF / WinUI / MAUI adapters. When [McpRaisable] is declared, the
        // generator emits a typed dispatch that preserves the event reference
        // through trimming.
        if (Marionette.Runtime.Adapters.RaiseEventCatalog.TryRaise(target, eventName, args: null))
        {
            return true;
        }

        // Preferred path: virtual `On<EventName>(EventArgs)` method on the
        // control type. This is the framework idiom — calling it produces the
        // exact same observable behaviour as a user-driven event because
        // that's the method the real input pipeline calls internally.
        if (TryInvokeOnVirtual(target, eventName, log))
        {
            return true;
        }

        // Fallback path: raise the event via its private backing
        // EventHandlerList entry. WinForms stores event handlers in
        // Component.Events keyed by static event-key objects (e.g.
        // Control.EventClick), and Control.OnXxx methods just look up
        // those handlers and invoke them with appropriate EventArgs. We
        // duplicate the lookup using reflection.
        if (TryInvokeViaEventHandlerList(target, eventName, log))
        {
            return true;
        }

        log.LogInformation(
            "raise_event: could not resolve '{Event}' on {Type} or any base type. " +
            "Pass the C# event name (e.g. 'Click').",
            eventName, target.GetType().FullName);
        return false;
    }

    private static bool TryInvokeOnVirtual(Control target, string eventName, ILogger log)
    {
        // OnClick / OnTextChanged / OnKeyDown / etc. are protected virtual.
        // EventArgs.Empty is correct for the parameterless events (Click,
        // Enter, Leave, ...). For events that need typed args (KeyDown,
        // MouseDown, ...), Phase-15 raise_event sticks to EventArgs.Empty —
        // adopters who need typed args use simulate_input or [McpCallable].
        var method = ResolveOnMethod(target.GetType(), eventName);
        if (method is null) return false;

        try
        {
            var arg = ResolveDefaultEventArgs(method.GetParameters());
            method.Invoke(target, new[] { arg });
            return true;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            log.LogWarning(tie.InnerException, "raise_event: On{Event} on {Type} threw.", eventName, target.GetType().Name);
            return false;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "raise_event: invoking On{Event} on {Type} threw.", eventName, target.GetType().Name);
            return false;
        }
    }

    private static MethodInfo? ResolveOnMethod(Type type, string eventName)
    {
        var methodName = "On" + eventName;
        Type? cur = type;
        while (cur is not null)
        {
            // Candidate signature: `void OnClick(EventArgs)` (and its
            // typed-args variants — we accept any single-parameter signature
            // whose parameter is assignable from EventArgs).
            var methods = cur.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var m in methods)
            {
                if (!string.Equals(m.Name, methodName, StringComparison.Ordinal)) continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                if (typeof(EventArgs).IsAssignableFrom(ps[0].ParameterType))
                {
                    return m;
                }
            }
            cur = cur.BaseType;
        }
        return null;
    }

    private static EventArgs ResolveDefaultEventArgs(ParameterInfo[] parameters)
    {
        // Best-effort default. EventArgs.Empty satisfies the common case
        // (Click, Enter, Leave, ...). Typed-args events get the closest
        // we can construct — typically a parameterless ctor, otherwise null
        // (which Invoke turns into "object required" — caller falls back to
        // the EventHandlerList path).
        var paramType = parameters[0].ParameterType;
        if (paramType == typeof(EventArgs)) return EventArgs.Empty;

        try
        {
            // Some args have a parameterless ctor (CancelEventArgs, etc.).
            var ctor = paramType.GetConstructor(Type.EmptyTypes);
            if (ctor is not null) return (EventArgs)ctor.Invoke(null);
        }
        catch
        {
            // ignore
        }
        return EventArgs.Empty;
    }

    private static bool TryInvokeViaEventHandlerList(Control target, string eventName, ILogger log)
    {
        // Component stores its event delegates in `private EventHandlerList Events`.
        // Each event has a static `internal static readonly object EventClick`-style
        // key field on the owning type. We resolve both via reflection.
        try
        {
            var componentType = typeof(System.ComponentModel.Component);
            var eventsProperty = componentType.GetProperty(
                "Events",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (eventsProperty is null) return false;
            if (eventsProperty.GetValue(target) is not System.ComponentModel.EventHandlerList events) return false;

            // Find the `EventXxx` static field on the type chain.
            var keyField = ResolveEventKey(target.GetType(), eventName);
            if (keyField is null) return false;
            var key = keyField.GetValue(null);
            if (key is null) return false;

            var handler = events[key];
            if (handler is null)
            {
                log.LogDebug("raise_event: no handlers attached to {Type}.{Event}.", target.GetType().Name, eventName);
                return true; // Treat "no handlers" as success — the event "fired" but had no listeners.
            }

            handler.DynamicInvoke(target, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "raise_event EventHandlerList path on {Type}.{Event} failed.", target.GetType().Name, eventName);
            return false;
        }
    }

    private static FieldInfo? ResolveEventKey(Type type, string eventName)
    {
        var fieldName = "Event" + eventName;
        Type? cur = type;
        while (cur is not null)
        {
            var f = cur.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f is not null && f.FieldType == typeof(object)) return f;
            cur = cur.BaseType;
        }
        return null;
    }
}
