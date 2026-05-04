// Marionette.NET — WinUI routed-event resolver and raiser (Phase 3.2)
//
// WinUI 3 differs sharply from WPF/Avalonia in its routed-event surface:
//
//   * NO EventManager.GetRoutedEventsForOwner — the public WinUI surface does
//     not expose routed events as static fields. RoutedEvents are exposed as
//     standard CLR events on the control surface (e.g. ButtonBase.Click is a
//     `event RoutedEventHandler Click`).
//   * NO UIElement.RaiseEvent that synthesises a routed event. Subscribers
//     register through the standard CLR `+=` and the framework fires through
//     internal mechanisms not surfaced to the public API.
//
// Phase 3.2 strategy: reflection-find the public CLR event by name on the
// control's type chain. Pull the underlying delegate field (compiler-emitted
// `<EventName>` private backing field for `event` declarations) and invoke
// it with a default-constructed RoutedEventArgs (or a typed args subclass when
// the event handler signature requires it).
//
// This is a meaningful AOT/trim hazard: the compiler-emitted delegate field is
// not surfaced via the EventInfo metadata, only via raw reflection. Trimming
// will likely strip the field. We mark the helper with the standard
// IL2026/IL2075 suppressions and document the caveat in the public adapter
// XML doc, mirroring the pattern WPF and Avalonia adapters use.
//
// Adopters who need reliable raise_event coverage in AOT scenarios should use
// the alternative path: decorate the handler logic on a [McpCallable] method,
// and call it via invoke_method. This keeps the semantic intent clear and
// works in every shipping configuration.
//
// Threading: UI-thread-only. Caller wraps in DispatchAsync.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Microsoft.UI.Xaml;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.WinUI.Internal;

internal static class WinUiEventRaiser
{
    /// <summary>
    /// Resolve and raise a named CLR event on <paramref name="target"/>. Returns
    /// <see langword="true"/> on success.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Phase 3.2: WinUI 3 exposes routed events as CLR events; reflection on the compiler-emitted delegate field is the only public-API path. AOT-trim caveat documented in adapter XML doc.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070:UnrecognizedReflectionPattern",
        Justification = "Phase 3.2: bound to known WinUI types; framework controls keep their event delegates rooted via XAML.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 3.2: same justification as IL2075/IL2070 - we walk the BaseType chain looking for the compiler-emitted backing field.")]
    public static bool Raise(
        FrameworkElement target,
        string eventName,
        IReadOnlyDictionary<string, object?>? args,
        ILogger log)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrEmpty(eventName)) return false;
        _ = args; // Phase 3.2 ignores args; default RoutedEventArgs is enough for Click etc.

        // Walk the type chain looking for an EventInfo with the requested name,
        // then pull the compiler-emitted backing delegate field.
        Type? cur = target.GetType();
        while (cur is not null)
        {
            var ev = cur.GetEvent(eventName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (ev is not null)
            {
                // Compiler-emitted field for `event TDelegate Name { add; remove; }`
                // is a private instance field with the same name. Some libraries
                // use a different convention - try the EventInfo's RaiseMethod
                // if present (rare; almost always null).
                if (ev.RaiseMethod is { } raise)
                {
                    try
                    {
                        var ea = ConstructEventArgs(ev, target);
                        raise.Invoke(target, new[] { (object?)target, ea });
                        return true;
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "raise_event: RaiseMethod invoke on {Type}.{Event} threw.", cur.Name, eventName);
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
                        "raise_event: WinUI's {Event} on {Type} does not expose a compiler-emitted backing field. " +
                        "WinUI custom events often use a registration model that doesn't surface a public delegate. " +
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
                    var ea = ConstructEventArgs(ev, target);
                    del.DynamicInvoke(target, ea);
                    return true;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "raise_event on {Type}.{Event} threw.", cur.Name, eventName);
                    return false;
                }
            }
            cur = cur.BaseType;
        }

        log.LogInformation(
            "raise_event: could not resolve '{Event}' on {Type} or any base type. " +
            "Pass the C# event name (e.g. 'Click').",
            eventName, target.GetType().FullName);
        return false;
    }

    /// <summary>
    /// Best-effort construct an EventArgs subclass matching the event handler
    /// signature. Most WinUI events use <see cref="RoutedEventArgs"/> as the
    /// args type; the few exceptions accept default-constructible subclasses
    /// (TappedRoutedEventArgs, KeyRoutedEventArgs, ...). We try the handler's
    /// declared args type first, then fall back to RoutedEventArgs.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075:UnrecognizedReflectionPattern",
        Justification = "Phase 3.2: same trim caveat as Raise().")]
    private static object ConstructEventArgs(EventInfo ev, object _)
    {
        var handlerType = ev.EventHandlerType;
        if (handlerType is not null)
        {
            // Handler is `Action<TSender, TArgs>` shape (RoutedEventHandler is
            // `delegate void RoutedEventHandler(object sender, RoutedEventArgs e)`).
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
        return new RoutedEventArgs();
    }
}
