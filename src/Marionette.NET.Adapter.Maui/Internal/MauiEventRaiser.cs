// Marionette.NET — MAUI event resolver and raiser (Phase 4.1)
//
// MAUI 10.x exposes events as standard CLR events on the Element / View
// hierarchy:
//   * `event EventHandler Clicked` on Button
//   * `event EventHandler<TextChangedEventArgs> TextChanged` on Entry / Editor
//   * `event EventHandler<ToggledEventArgs> Toggled` on Switch / CheckBox
//   * etc.
//
// There is NO RoutedEvent surface. There is no `EventManager.GetRoutedEventsForOwner`
// (the WPF idiom). There is no `Element.RaiseEvent` (the WPF / Avalonia idiom).
// Subscribers register via `+=` and the platform handler fires through
// internal mechanisms not surfaced to the public API.
//
// Phase 4.1 strategy: reflection-find the public CLR event by name on the
// target's type chain. Pull the underlying delegate field (compiler-emitted
// `<EventName>` private backing field for `event` declarations) and invoke it
// with a default-constructed args type. Mirrors the Phase 3.2 WinUI raiser.
//
// AOT/trim caveat: the compiler-emitted delegate field is not surfaced via
// the EventInfo metadata, only via raw reflection. Trimming will likely strip
// the field. We mark the helper with the standard IL2026/IL2070/IL2075
// suppressions and document the caveat in the public adapter XML doc, mirroring
// the WinUI / Avalonia pattern.
//
// Adopters who need reliable raise_event coverage in AOT scenarios should use
// the alternative path: decorate the handler logic on a [McpCallable] method,
// and call it via invoke_method.
//
// Threading: UI-thread-only. Caller wraps in DispatchAsync.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Microsoft.Maui.Controls;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.Maui.Internal;

internal static class MauiEventRaiser
{
    /// <summary>
    /// Resolve and raise a named CLR event on <paramref name="target"/>.
    /// Returns <see langword="true"/> on success.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Phase 4.1: MAUI exposes events as CLR events; reflection on the compiler-emitted delegate field is the only public-API path. AOT-trim caveat documented in adapter XML doc.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern",
        Justification = "Phase 4.1: bound to known MAUI types; framework controls keep their event delegates rooted via XAML.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 4.1: same justification as IL2075/IL2070 - we walk the BaseType chain looking for the compiler-emitted backing field.")]
    public static bool Raise(
        Element target,
        string eventName,
        IReadOnlyDictionary<string, object?>? args,
        ILogger log)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrEmpty(eventName)) return false;
        _ = args; // Phase 4.1 ignores args; default EventArgs is enough for Clicked etc.

        // Walk the type chain looking for an EventInfo with the requested name,
        // then pull the compiler-emitted backing delegate field.
        Type? cur = target.GetType();
        while (cur is not null)
        {
            var ev = cur.GetEvent(eventName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (ev is not null)
            {
                if (ev.RaiseMethod is { } raise)
                {
                    try
                    {
                        var ea = ConstructEventArgs(ev);
                        raise.Invoke(target, new[] { (object?)target, ea });
                        return true;
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex,
                            "raise_event: RaiseMethod invoke on {Type}.{Event} threw.",
                            cur.Name, eventName);
                        return false;
                    }
                }

                // Fallback: pull the backing field. Standard C# event compiler
                // shape is `private TDelegate <EventName>;` (no underscore).
                var backingField = cur.GetField(eventName,
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (backingField is null)
                {
                    log.LogInformation(
                        "raise_event: MAUI's {Event} on {Type} does not expose a compiler-emitted backing field. " +
                        "Some MAUI controls use a registration model that doesn't surface a public delegate. " +
                        "Use simulate_input or [McpCallable] for this case.",
                        eventName, cur.Name);
                    return false;
                }
                if (backingField.GetValue(target) is not Delegate del)
                {
                    log.LogInformation(
                        "raise_event: {Type}.{Event} backing field is null - no subscribers attached.",
                        cur.Name, eventName);
                    // No subscribers is a no-op success - the event would have
                    // fired with no listeners normally too.
                    return true;
                }

                try
                {
                    var ea = ConstructEventArgs(ev);
                    del.DynamicInvoke(target, ea);
                    return true;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex,
                        "raise_event on {Type}.{Event} threw.",
                        cur.Name, eventName);
                    return false;
                }
            }
            cur = cur.BaseType;
        }

        log.LogInformation(
            "raise_event: could not resolve '{Event}' on {Type} or any base type. " +
            "Pass the C# event name (e.g. 'Clicked').",
            eventName, target.GetType().FullName);
        return false;
    }

    /// <summary>
    /// Best-effort construct an EventArgs subclass matching the event handler
    /// signature. Most MAUI events use <see cref="EventArgs"/> or a typed
    /// subclass with a default-constructible args type. We try the handler's
    /// declared args type first, then fall back to <see cref="EventArgs"/>.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Phase 4.1: same trim caveat as Raise().")]
    private static object ConstructEventArgs(EventInfo ev)
    {
        var handlerType = ev.EventHandlerType;
        if (handlerType is not null)
        {
            // Handler is `EventHandler` -> `Invoke(object, EventArgs)` shape;
            // `EventHandler<T>` -> `Invoke(object, T)`.
            var invoke = handlerType.GetMethod("Invoke");
            var pars = invoke?.GetParameters();
            if (pars is { Length: 2 })
            {
                var argsType = pars[1].ParameterType;
                if (argsType.IsClass && !argsType.IsAbstract)
                {
                    var defaultCtor = argsType.GetConstructor(Type.EmptyTypes);
                    if (defaultCtor is not null)
                    {
                        try { return defaultCtor.Invoke(null); }
                        catch { /* fall through */ }
                    }
                }
            }
        }
        return EventArgs.Empty;
    }
}
