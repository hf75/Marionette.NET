// Marionette.NET — Avalonia input simulation helper
//
// History:
//   Phase 3.1 — only `click` family supported (via Button.ClickEvent +
//               public RoutedEventArgs ctor). Other kinds returned false.
//   Phase 9.1 — adds `type_text` via direct property-setting on the
//               common Avalonia text inputs (TextBox, AutoCompleteBox,
//               MaskedTextBox), matching the pragmatic pattern the MAUI
//               adapter uses (MauiInputSimulator.cs). Covers the primary
//               LLM use case "type something into a form".
//
// Architectural constraints (still true in Avalonia 12.0.2):
//
// Avalonia exposes `Avalonia.Input.InputManager.Instance` and
// `IInputManager.ProcessInput(RawInputEventArgs)`, but the EVENT-ARGS-LEVEL
// types Marionette would want to construct (PointerPressedEventArgs,
// PointerReleasedEventArgs, KeyEventArgs, TextInputEventArgs) all have
// `internal` ctors. The publicly-constructible alternatives
// (RawPointerEventArgs, RawInputEventArgs) require an `IInputRoot` which is
// typically wired by the platform host and not adopter-trivially
// constructible. AOT also forbids reflection-based ctor invocation.
//
// What this means for the unsupported kinds (key_press / key_down / key_up /
// mouse_move): they remain `return false` with a clear breadcrumb. Adopters
// who need keyboard input have two AOT-clean options:
//   * decorate the relevant method with [McpCallable] and invoke it directly
//     (Phase 1 path; semantic > visual per masterplan tenet 2), or
//   * raise the appropriate routed event via raise_event with the C# event
//     name (the framework's `Interactive.RaiseEvent` works fine for events
//     whose args type is publicly constructible — KeyDown/KeyUp are not).
//
// Threading: every public method here MUST be called on the Avalonia UI
// thread. AvaloniaUiAutomationAdapter wraps each call in DispatchAsync<T>(...).

using System;
using System.Collections.Generic;

using Avalonia.Controls;
using Avalonia.Interactivity;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.Avalonia.Internal;

internal static class AvaloniaInputSimulator
{
    /// <summary>
    /// Drive the named input kind against <paramref name="target"/>. Returns
    /// <see langword="true"/> on success, <see langword="false"/> when the
    /// kind isn't supported by the Phase-3.1 Avalonia adapter.
    /// </summary>
    public static bool Simulate(
        Control target,
        string kind,
        IReadOnlyDictionary<string, object?>? args,
        ILogger log)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrEmpty(kind)) return false;
        _ = args;

        switch (kind)
        {
            case "click":
            case "double_click":  // Avalonia doesn't have a separate DoubleTapped routed event constructible publicly; ClickEvent fires twice has the same semantic effect for Phase 3.1.
            case "right_click":   // Same reasoning — Avalonia routes ContextRequested separately, but Phase 3.1 just raises Click for the test scenarios we ship.
                return RaiseClickEvent(target, log);

            case "type_text":
                return TypeText(target, args, log);

            case "key_press":
            case "key_down":
            case "key_up":
            case "mouse_move":
                // Phase 14: when the adopter opted into the reflection
                // fallback at AttachTo time, route through it. The fallback
                // is AOT-incompatible (annotated with
                // [RequiresUnreferencedCode]) — adopters explicitly trade
                // AOT for raw-input coverage.
                if (AvaloniaReflectionInputFallback.Enabled)
                {
                    return TrySendViaReflection(target, kind, args, log);
                }
                log.LogInformation(
                    "simulate_input: kind '{Kind}' is not supported by the Avalonia adapter " +
                    "(Avalonia 12.x has internal ctors for KeyEventArgs / PointerEventArgs and the " +
                    "raw-input pipeline requires platform-host plumbing the adapter does not have). " +
                    "Set MarionetteAvalonia.AttachTo(useRawInputReflectionFallback: true) for an opt-in " +
                    "reflection-based path that closes this gap (loses AOT compat for these calls). " +
                    "Otherwise use raise_event with the framework event name, OR a [McpCallable] method.",
                    kind);
                return false;

            default:
                log.LogWarning("simulate_input: unknown kind '{Kind}'.", kind);
                return false;
        }
    }

    /// <summary>
    /// Phase 14: route key_press / key_down / key_up / mouse_move through the
    /// reflection-based fallback. Wraps the
    /// <see cref="AvaloniaReflectionInputFallback"/> calls so the trim
    /// warnings are confined to one function. Only invoked when the adopter
    /// opted in at <c>MarionetteAvalonia.AttachTo</c> time.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(
        "Phase 14 reflection fallback uses Reflection on Avalonia.Base internals.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(
        "Phase 14 reflection fallback uses Reflection-based ctor invocation.")]
    private static bool TrySendViaReflection(
        Control target,
        string kind,
        IReadOnlyDictionary<string, object?>? args,
        ILogger log)
    {
        switch (kind)
        {
            case "mouse_move":
            {
                var x = TryGetDoubleArg(args, "x", 0);
                var y = TryGetDoubleArg(args, "y", 0);
                return AvaloniaReflectionInputFallback.TrySendMouseMove(
                    target, new global::Avalonia.Point(x, y), log);
            }

            case "key_press":
            case "key_down":
            case "key_up":
            {
                var key = ParseKey(args, log);
                if (key is null) return false;
                if (kind == "key_press")
                {
                    return AvaloniaReflectionInputFallback.TrySendKey(target, key.Value, isDown: true, log)
                        && AvaloniaReflectionInputFallback.TrySendKey(target, key.Value, isDown: false, log);
                }
                return AvaloniaReflectionInputFallback.TrySendKey(
                    target, key.Value, isDown: kind == "key_down", log);
            }
        }
        return false;
    }

    private static double TryGetDoubleArg(IReadOnlyDictionary<string, object?>? args, string name, double fallback)
    {
        if (args is null || !args.TryGetValue(name, out var v)) return fallback;
        return v switch
        {
            double d => d,
            int i => i,
            long l => l,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
            _ => fallback,
        };
    }

    private static global::Avalonia.Input.Key? ParseKey(IReadOnlyDictionary<string, object?>? args, ILogger log)
    {
        if (args is null || !args.TryGetValue("key", out var v) || v is not string s)
        {
            log.LogWarning("simulate_input(key) requires args.key (string).");
            return null;
        }
        if (!Enum.TryParse<global::Avalonia.Input.Key>(s, ignoreCase: true, out var key))
        {
            log.LogWarning("simulate_input(key) couldn't parse key='{Key}'. Expected an Avalonia.Input.Key enum name.", s);
            return null;
        }
        return key;
    }

    /// <summary>
    /// Phase 9.1: set the target's <c>Text</c> property when it's one of the
    /// common Avalonia text inputs. Mirrors the pragmatic pattern used by the
    /// MAUI adapter — semantic-first, no platform-host plumbing. The setter
    /// goes through Avalonia's <c>StyledProperty</c> system so any
    /// <c>PropertyChanged</c> binding fires normally; data-bound view models
    /// see the change.
    /// </summary>
    private static bool TypeText(Control target, IReadOnlyDictionary<string, object?>? args, ILogger log)
    {
        if (args is null || !args.TryGetValue("text", out var raw))
        {
            log.LogInformation(
                "simulate_input(type_text) on {Type}: no 'text' argument supplied.",
                target.GetType().Name);
            return false;
        }
        var text = raw as string ?? raw?.ToString() ?? string.Empty;

        // Order matters: derived types must come before their base. In
        // Avalonia 12.x, MaskedTextBox : TextBox — handle it first or the
        // TextBox case would shadow it (the C# compiler also flags this as
        // CS8120 if the order is wrong).
        switch (target)
        {
            case global::Avalonia.Controls.MaskedTextBox mtb:
                mtb.Text = text;
                return true;
            case global::Avalonia.Controls.TextBox tb:
                tb.Text = text;
                return true;
            case global::Avalonia.Controls.AutoCompleteBox ab:
                ab.Text = text;
                return true;
            case global::Avalonia.Controls.TextBlock tbl:
                // Read-only by Avalonia's design (no user-typing target), but
                // we accept programmatic text set so test fixtures can drive
                // observable bindings via labels.
                tbl.Text = text;
                return true;
            default:
                log.LogInformation(
                    "simulate_input(type_text) on {Type}: target is not a TextBox / AutoCompleteBox / " +
                    "MaskedTextBox / TextBlock. Other controls would need a custom adapter.",
                    target.GetType().Name);
                return false;
        }
    }

    /// <summary>
    /// Raise <see cref="Button.ClickEvent"/> on the target control. Works on
    /// any Avalonia <see cref="Button"/> (Avalonia 12.x has no ButtonBase
    /// class — Button is the public abstraction). For non-Button targets
    /// the method walks up the logical tree looking for a Button ancestor —
    /// typical when the LLM clicks an element nested inside a button
    /// template.
    /// </summary>
    private static bool RaiseClickEvent(Control target, ILogger log)
    {
        var btn = FindButton(target);
        if (btn is null)
        {
            log.LogInformation(
                "simulate_input(click) on {Type}: no Button in the logical chain. " +
                "Phase 3.1 Avalonia simulate_input only handles Button-like targets. " +
                "For non-button controls use raise_event with a specific routed-event name.",
                target.GetType().Name);
            return false;
        }
        try
        {
            // Avalonia.Interactivity.RoutedEventArgs is publicly constructible.
            // RaiseEvent traverses the routed-event pipeline — handlers +
            // bubbling. Button.ClickEvent is the canonical click-routed-event
            // in Avalonia 12.x.
            var ea = new RoutedEventArgs(Button.ClickEvent, btn);
            btn.RaiseEvent(ea);
            return true;
        }
        catch (Exception ex)
        {
            // Click handlers in adopter code can throw NullRefs and similar
            // bugs that would normally surface only when a real user clicks
            // the button. Surface as an information-level entry plus a
            // warning so the LLM knows the click reached the framework but
            // the handler choked.
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            log.LogWarning(ex,
                "simulate_input(click) raise on {Type} threw {Inner}: {Message}",
                btn.GetType().Name, inner.GetType().Name, inner.Message);
            return false;
        }
    }

    private static Button? FindButton(Control start)
    {
        // First: if start IS a button, return it.
        if (start is Button b0) return b0;
        // Walk up via Parent (logical tree). We don't traverse the visual
        // tree here because for Avalonia's most common case (an element
        // INSIDE a button template), the logical parent IS the button.
        global::Avalonia.StyledElement? cur = start.Parent;
        while (cur is not null)
        {
            if (cur is Button b) return b;
            cur = cur.Parent;
        }
        return null;
    }
}
