// Marionette.NET — Phase 14 Avalonia reflection-based raw-input fallback
//
// Avalonia 12.0.2's reference assembly seals the constructors of
// `RawKeyEventArgs`, `KeyEventArgs`, `RawPointerEventArgs`, and the
// `IInputManager.ProcessInput(...)` method as `internal`. From outside
// Avalonia.Base.dll there is no AOT-clean way to construct or dispatch them.
//
// This module provides an OPT-IN reflection-based fallback that gives
// adopters a working `key_press` / `key_down` / `key_up` / `mouse_move` path
// in exchange for losing AOT compatibility on those calls.
//
// AOT note:
//   * Every method in this file is marked [RequiresUnreferencedCode] +
//     [RequiresDynamicCode]. Adopters who AOT-publish should leave the
//     fallback disabled (the default); the warnings flow only when the
//     opt-in is explicitly turned on.
//   * The reflection targets (RawKeyEventArgs ctor, KeyboardDevice instance,
//     IInputManager.ProcessInput) live in Avalonia.Base — at runtime the
//     IMPLEMENTATION assembly exposes them as public, so reflection on
//     non-AOT-published binaries succeeds.
//
// Threading: caller MUST be on the Avalonia UI thread.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
// Note: Avalonia.Input.InputManager itself is `internal` in the reference
// assembly, so we resolve it at runtime via Avalonia.Base.Assembly +
// Type.GetType(...). Same pattern as the other internal-only members.

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.Avalonia.Internal;

/// <summary>
/// Phase 14: opt-in reflection-based input fallback for Avalonia. Used when
/// <see cref="MarionetteAvalonia.AttachTo"/> is called with
/// <c>useRawInputReflectionFallback: true</c>. Trim-/AOT-incompatible by
/// design — the corresponding warnings flow on every call site.
/// </summary>
internal static class AvaloniaReflectionInputFallback
{
    /// <summary>
    /// Set by <see cref="MarionetteAvalonia"/> when the adopter opts in.
    /// The simulator checks this flag before attempting reflection.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// Reflection-pumped <see cref="RawKeyEventArgs"/> through
    /// <c>IInputManager.ProcessInput</c>. Returns <see langword="true"/> on
    /// success, <see langword="false"/> when the runtime reflection couldn't
    /// resolve a required member or the dispatch threw.
    /// </summary>
    [RequiresUnreferencedCode("Phase 14 Avalonia reflection fallback uses Reflection on Avalonia.Base internals.")]
    [RequiresDynamicCode("Phase 14 Avalonia reflection fallback uses Reflection-based ctor invocation.")]
    public static bool TrySendKey(Control target, Key key, bool isDown, ILogger log)
    {
        try
        {
            var inputRoot = ResolveInputRoot(target);
            if (inputRoot is null)
            {
                log.LogInformation("Avalonia reflection input: no IInputRoot for {Type}.", target.GetType().Name);
                return false;
            }

            var keyboardDevice = ResolveKeyboardDevice();
            if (keyboardDevice is null)
            {
                log.LogInformation("Avalonia reflection input: KeyboardDevice not resolvable.");
                return false;
            }

            var rawType = isDown
                ? RawKeyEventType.KeyDown
                : RawKeyEventType.KeyUp;

            // Construct RawKeyEventArgs via the public-on-implementation
            // (internal-on-reference) ctor:
            //   RawKeyEventArgs(IKeyboardDevice device, ulong timestamp,
            //                   IInputRoot root, RawKeyEventType type,
            //                   Key key, RawInputModifiers modifiers)
            var ctor = typeof(RawKeyEventArgs).GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c =>
                {
                    var ps = c.GetParameters();
                    return ps.Length == 6 &&
                           ps[3].ParameterType == typeof(RawKeyEventType) &&
                           ps[4].ParameterType == typeof(Key);
                });

            if (ctor is null)
            {
                log.LogInformation("Avalonia reflection input: RawKeyEventArgs(6-arg) ctor not found.");
                return false;
            }

            var args = ctor.Invoke(new object?[]
            {
                keyboardDevice,
                (ulong)Environment.TickCount,
                inputRoot,
                rawType,
                key,
                RawInputModifiers.None,
            });

            return DispatchRaw(args, log);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Avalonia reflection input: TrySendKey threw.");
            return false;
        }
    }

    /// <summary>
    /// Reflection-pumped <see cref="RawPointerEventArgs"/> for mouse-move.
    /// </summary>
    [RequiresUnreferencedCode("Phase 14 Avalonia reflection fallback uses Reflection on Avalonia.Base internals.")]
    [RequiresDynamicCode("Phase 14 Avalonia reflection fallback uses Reflection-based ctor invocation.")]
    public static bool TrySendMouseMove(Control target, Point point, ILogger log)
    {
        try
        {
            var inputRoot = ResolveInputRoot(target);
            if (inputRoot is null) return false;

            var pointerDevice = ResolvePointerDevice();
            if (pointerDevice is null) return false;

            // Construct RawPointerEventArgs:
            //   RawPointerEventArgs(IInputDevice device, ulong timestamp,
            //                       IInputRoot root, RawPointerEventType type,
            //                       Point position, RawInputModifiers modifiers)
            var ctor = typeof(RawPointerEventArgs).GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c =>
                {
                    var ps = c.GetParameters();
                    return ps.Length == 6 &&
                           ps[3].ParameterType == typeof(RawPointerEventType) &&
                           ps[4].ParameterType == typeof(Point);
                });

            if (ctor is null) return false;

            var args = ctor.Invoke(new object?[]
            {
                pointerDevice,
                (ulong)Environment.TickCount,
                inputRoot,
                RawPointerEventType.Move,
                point,
                RawInputModifiers.None,
            });

            return DispatchRaw(args, log);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Avalonia reflection input: TrySendMouseMove threw.");
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    [RequiresUnreferencedCode("Phase 14 Avalonia reflection fallback uses Reflection on Avalonia.Base internals.")]
    [RequiresDynamicCode("Phase 14 Avalonia reflection fallback uses Reflection-based dispatch.")]
    private static bool DispatchRaw(object rawArgs, ILogger log)
    {
        // InputManager is internal in Avalonia 12.x's reference assembly, so
        // we can't reference it by type. Resolve at runtime via the loaded
        // Avalonia.Base assembly. RawInputEventArgs IS public, so we use it
        // as our anchor to find Avalonia.Base.
        var imType = typeof(RawInputEventArgs).Assembly.GetType("Avalonia.Input.InputManager");
        if (imType is null)
        {
            log.LogInformation("Avalonia reflection input: Avalonia.Input.InputManager type not found.");
            return false;
        }

        var instanceProp = imType.GetProperty(
            "Instance",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        var instance = instanceProp?.GetValue(null);
        if (instance is null)
        {
            log.LogInformation("Avalonia reflection input: InputManager.Instance is null.");
            return false;
        }

        var processInput = instance.GetType().GetMethod(
            "ProcessInput",
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(RawInputEventArgs) },
            modifiers: null);
        if (processInput is null)
        {
            log.LogInformation("Avalonia reflection input: ProcessInput method not found on {Type}.", instance.GetType().FullName);
            return false;
        }

        processInput.Invoke(instance, new[] { rawArgs });
        return true;
    }

    [RequiresUnreferencedCode("Phase 14 Avalonia reflection fallback uses Reflection.")]
    private static object? ResolveInputRoot(Control target)
    {
        // Walk the visual tree up looking for something that implements
        // IInputRoot. TopLevel implements it; that's the typical answer.
        var top = TopLevel.GetTopLevel(target);
        return top;
    }

    [RequiresUnreferencedCode("Phase 14 Avalonia reflection fallback uses Reflection.")]
    [RequiresDynamicCode("Phase 14 Avalonia reflection fallback uses Reflection-based ctor invocation.")]
    private static object? ResolveKeyboardDevice()
    {
        // KeyboardDevice has an internal singleton. Construct one via the
        // parameterless internal ctor.
        var t = typeof(KeyboardDevice);
        var ctor = t.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        return ctor?.Invoke(null);
    }

    [RequiresUnreferencedCode("Phase 14 Avalonia reflection fallback uses Reflection.")]
    [RequiresDynamicCode("Phase 14 Avalonia reflection fallback uses Reflection-based ctor invocation.")]
    private static object? ResolvePointerDevice()
    {
        // For RawPointerEventArgs we need an IPointerDevice. The interface
        // and PointerDevice impl are both internal in Avalonia 12.x; we
        // resolve via runtime reflection on the loaded Avalonia.Base
        // assembly (anchored on the public RawInputEventArgs type).
        try
        {
            var avaloniaBase = typeof(RawInputEventArgs).Assembly;

            // First try AvaloniaLocator.Current.GetService<IPointerDevice>().
            var locType = typeof(AvaloniaLocator);
            var currentProp = locType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
            var current = currentProp?.GetValue(null);

            var ptrIface = avaloniaBase.GetType("Avalonia.Input.IPointerDevice");
            if (current is not null && ptrIface is not null)
            {
                var getService = current.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (getService is not null)
                {
                    var resolved = getService.MakeGenericMethod(ptrIface).Invoke(current, null);
                    if (resolved is not null) return resolved;
                }
            }

            // Last resort: construct a PointerDevice via internal ctor.
            var pdType = avaloniaBase.GetType("Avalonia.Input.PointerDevice");
            var pdCtor = pdType?.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            return pdCtor?.Invoke(null);
        }
        catch
        {
            return null;
        }
    }
}
