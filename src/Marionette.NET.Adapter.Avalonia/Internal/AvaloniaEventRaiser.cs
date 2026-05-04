// Marionette.NET — Avalonia routed-event resolver and raiser (Phase 3.1)
//
// Mirrors the WPF event raiser shape against Avalonia's RoutedEvent system.
// Walks a control's type chain looking for a static `<EventName>Event` field
// of type Avalonia.Interactivity.RoutedEvent (the Avalonia idiom).
// Constructs a default RoutedEventArgs and dispatches via Interactive.RaiseEvent.
//
// Threading: UI-thread-only. Caller wraps in DispatchAsync.
//
// AOT note: same caveat as WPF — reflection on the control type for static
// `<EventName>Event` fields is intrinsically incompatible with full
// trimming. Avalonia 11.x is more AOT-friendly than WPF as a baseline, so
// this is a reasonable trade-off for Phase 3.1; Phase 5 may emit a
// source-gen alternative.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Avalonia.Controls;
using Avalonia.Interactivity;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.Avalonia.Internal;

internal static class AvaloniaEventRaiser
{
    /// <summary>
    /// Resolve and raise a named routed event on <paramref name="target"/>.
    /// Returns <see langword="true"/> on success.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Phase 3.1: Avalonia event lookup walks types via reflection; framework controls keep their RoutedEvent fields rooted via XAML. Phase 5 AOT-hardening to revisit.")]
    public static bool Raise(
        Control target,
        string eventName,
        IReadOnlyDictionary<string, object?>? args,
        ILogger log)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrEmpty(eventName)) return false;
        _ = args; // Phase 3.1 ignores args; default RoutedEventArgs covers Click et al.

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
            // Avalonia.Interactivity.RoutedEventArgs has a public ctor taking
            // (RoutedEvent, source). RaiseEvent is on Avalonia.Interactivity.Interactive
            // which Control extends; routing strategies (Bubble / Tunnel / Direct)
            // are honoured by Avalonia's dispatch.
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
    /// Walk the type chain looking for a public/non-public static field
    /// named <c>&lt;EventName&gt;Event</c> of type
    /// <see cref="RoutedEvent"/>. Avalonia's idiom is
    /// <c>public static readonly RoutedEvent ClickEvent = ...</c> — same
    /// shape as WPF.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern",
        Justification = "Phase 4.2: bound to Avalonia framework types whose RoutedEvent fields are kept rooted by XAML / templating.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Phase 4.2: same justification.")]
    private static RoutedEvent? ResolveRoutedEvent(Type type, string eventName)
    {
        var fieldName = eventName + "Event";
        Type? cur = type;
        while (cur is not null)
        {
            var f = cur.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f is { } && typeof(RoutedEvent).IsAssignableFrom(f.FieldType))
            {
                if (f.GetValue(null) is RoutedEvent re) return re;
            }
            cur = cur.BaseType;
        }
        return null;
    }
}
