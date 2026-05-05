// Marionette.NET — Phase 12.2 raise_event opt-in typed catalog
//
// `raise_event` resolves a framework `RoutedEvent` static field by reflection
// at runtime (`<Name>Event` lookup on the control's type chain). That's
// trim-fragile for custom user controls — the trim analyzer doesn't know
// the static field is a reachable target. Adopters who AOT-publish AND
// want `raise_event` to keep working declare each control-type + event-name
// pair via the assembly-level `[McpRaisable]` attribute (Phase 12.2). The
// source generator emits a typed `Marionette.Generated.RaiseEventCatalog.TryRaise`
// dispatcher; adopters wire it up by calling `Register(...)` at startup.
//
// Lookup order at the adapter level:
//   1. `RaiseEventCatalog.TryRaise(control, eventName, args)` — typed
//      pattern-match emitted by the source generator. Returns true on a
//      hit.
//   2. fallback: the adapter's existing reflection path (annotated with
//      `[RequiresUnreferencedCode]` so the adopter sees the warning if
//      they hit it).
//
// Adopters who never call `raise_event` need not register a catalog at
// all; `MarionetteHost.RunAsyncSourceGenSafe` (Phase 11) goes one step
// further and excludes the tool from registration entirely.

using System;
using System.Threading;

namespace Marionette.Runtime.Adapters;

/// <summary>
/// Phase 12.2: process-wide registry for the source-gen-emitted typed
/// raise-event dispatcher. Adapters consult this registry on every
/// <c>raise_event</c> call before falling back to reflection.
/// </summary>
public static class RaiseEventCatalog
{
    private static Func<object, string, object?, bool>? s_dispatcher;

    /// <summary>
    /// Register the typed dispatcher emitted by the source generator. Pass
    /// <c>Marionette.Generated.RaiseEventCatalog.TryRaise</c> at app
    /// startup (typically in <c>Program.cs</c> or right before
    /// <c>MarionetteWpf.AttachTo</c> / <c>MarionetteAvalonia.AttachTo</c>).
    /// Calling <see cref="Register(Func{object, string, object?, bool}?)"/>
    /// twice replaces the previous dispatcher.
    /// </summary>
    /// <remarks>
    /// The signature mirrors the generated catalog's <c>TryRaise(object,
    /// string, object?)</c>: pass any control, the event name, and the
    /// optional <c>RoutedEventArgs</c> instance — returns <see langword="true"/>
    /// when the (type, name) pair matched a declared
    /// <c>[McpRaisable]</c>; <see langword="false"/> otherwise (the
    /// adapter then runs its reflection fallback).
    /// </remarks>
    public static void Register(Func<object, string, object?, bool>? dispatcher)
    {
        Volatile.Write(ref s_dispatcher, dispatcher);
    }

    /// <summary>
    /// Attempt typed dispatch via the registered catalog. Returns
    /// <see langword="false"/> when no catalog is registered or the
    /// dispatcher returned <see langword="false"/> (meaning the (type,
    /// event-name) pair was not declared via <c>[McpRaisable]</c>).
    /// </summary>
    public static bool TryRaise(object control, string eventName, object? args)
    {
        var d = Volatile.Read(ref s_dispatcher);
        if (d is null) return false;
        return d(control, eventName, args);
    }

    /// <summary>
    /// Snapshot whether a catalog is currently registered. Useful for
    /// startup-banner diagnostics.
    /// </summary>
    public static bool IsRegistered => Volatile.Read(ref s_dispatcher) is not null;
}
