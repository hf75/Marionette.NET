// Marionette.NET — WPF routed-event resolver and raiser (Phase 3.1)
//
// Walks a control's type chain looking for a static `<EventName>Event` field
// of type RoutedEvent (the WPF idiom). Falls back to
// `EventManager.GetRoutedEventsForOwner(type)` when the static-field lookup
// misses (custom controls that register events differently). Constructs a
// default RoutedEventArgs and dispatches via Control.RaiseEvent.
//
// Threading: UI-thread-only. Caller wraps in DispatchAsync.
//
// AOT note: this file uses reflection on the control's type to find the
// RoutedEvent static field. That's intrinsically incompatible with full
// trimming because the field name comes from a runtime string; the user
// assembly may strip the field if it isn't referenced elsewhere. We mark
// the calling adapter method with [RequiresUnreferencedCode] / log a hint
// in the AOT publish path. Phase 5 (AOT hardening) may surface a
// source-gen-emitted lookup that avoids this entirely.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Windows;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.Wpf.Internal;

internal static class WpfEventRaiser
{
    /// <summary>
    /// Resolve and raise a named routed event on <paramref name="target"/>.
    /// Returns <see langword="true"/> on success.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Phase 3.1: WPF event lookup walks types via reflection; framework controls keep their RoutedEvent fields rooted via XAML. Phase 5 AOT-hardening to revisit.")]
    public static bool Raise(
        UIElement target,
        string eventName,
        IReadOnlyDictionary<string, object?>? args,
        ILogger log)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrEmpty(eventName)) return false;
        _ = args; // Phase 3.1 ignores args; default RoutedEventArgs is enough for Click etc.

        var routedEvent = ResolveRoutedEvent(target.GetType(), eventName);
        if (routedEvent is null)
        {
            log.LogInformation(
                "raise_event: could not resolve '{Event}' on {Type} or any base type. " +
                "Pass the C# event name (e.g. 'Click').",
                eventName, target.GetType().FullName);
            return false;
        }
        try
        {
            // Default RoutedEventArgs covers Click. Specific events that need
            // typed args (MouseMove, KeyDown) prefer simulate_input — Phase
            // 3.1 keeps raise_event minimal-but-effective.
            var ea = new RoutedEventArgs(routedEvent, target);
            target.RaiseEvent(ea);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "raise_event on {Type}.{Event} threw.", target.GetType().Name, eventName);
            return false;
        }
    }

    /// <summary>
    /// Walk the type chain looking for a public static field named
    /// <c>&lt;EventName&gt;Event</c> of type <see cref="RoutedEvent"/>. WPF's
    /// canonical pattern is <c>public static readonly RoutedEvent ClickEvent = ...</c>
    /// declared on the owner type. Inherited events resolve through the base
    /// type chain (<c>Button</c> inherits <c>ButtonBase.ClickEvent</c>).
    /// </summary>
    private static RoutedEvent? ResolveRoutedEvent(Type type, string eventName)
    {
        var fieldName = eventName + "Event";
        Type? cur = type;
        while (cur is not null)
        {
            // Public + non-public statics — WPF events are frequently public
            // static readonly, but some library controls register them
            // internally.
            var f = cur.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f is { } && f.FieldType == typeof(RoutedEvent))
            {
                if (f.GetValue(null) is RoutedEvent re) return re;
            }
            cur = cur.BaseType;
        }

        // Last resort: walk EventManager's registry. This is a single
        // reflection lookup; static fields on the owner type are the
        // preferred path because they're trim-friendlier.
        try
        {
            var ownerEvents = EventManager.GetRoutedEventsForOwner(type);
            if (ownerEvents is not null)
            {
                foreach (var re in ownerEvents)
                {
                    if (string.Equals(re.Name, eventName, StringComparison.Ordinal)) return re;
                }
            }
            // Walk every registered routed event and match by name + handlers
            // declared anywhere in the type chain.
            var all = EventManager.GetRoutedEvents();
            if (all is not null)
            {
                foreach (var re in all)
                {
                    if (string.Equals(re.Name, eventName, StringComparison.Ordinal) &&
                        re.OwnerType.IsAssignableFrom(type))
                    {
                        return re;
                    }
                }
            }
        }
        catch
        {
            // Ignored — fall through to null.
        }
        return null;
    }
}
