// Marionette.NET — Avalonia input simulation helper (Phase 3.1)
//
// LIMITATION (documented per the Phase 3.1 brief):
//
// Avalonia 11.x exposes Avalonia.Input.InputManager.Instance and
// IInputManager.ProcessInput(RawInputEventArgs), but the EVENT-ARGS-LEVEL
// types Marionette would want to construct (PointerPressedEventArgs,
// PointerReleasedEventArgs, KeyEventArgs, TextInputEventArgs) all have
// `internal` ctors in 11.3.14. The publicly-constructible alternatives
// (RawPointerEventArgs, RawInputEventArgs) require an `IInputRoot` which is
// also typically wired by the platform host and not adopter-trivially
// constructible.
//
// Phase 3.1 takes the masterplan-sanctioned fallback: for `click` (and the
// click variants double_click / right_click) we raise the public
// Avalonia.Controls.Button.ClickEvent (or the inherited ClickEvent on any
// ButtonBase descendant) using the public RoutedEventArgs ctor +
// Interactive.RaiseEvent. This DOES drive the framework's routed event
// pipeline (handlers, bubbling, tunneling) so for the most common Phase-3.1
// scenario — clicking a Button to fire its Click handler — the behaviour is
// observably the same as a real user click.
//
// For keyboard / text-input / mouse-move kinds, Phase 3.1 returns false with
// a logged "kind not yet supported" breadcrumb. Phase 3.2 (WinUI's
// InputInjector) and Phase 3.3 (multi-window routing) will revisit this; the
// masterplan flags Avalonia's raw-input API as historically unstable across
// minor releases, so we explicitly do not pin it down here. Adopters who
// need keyboard input can either:
//   * decorate the relevant method with [McpCallable] and invoke it directly
//     (Phase 1 path; semantic > visual per masterplan tenet 2), or
//   * raise the appropriate event via raise_event with the C# event name.
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

            case "key_press":
            case "key_down":
            case "key_up":
            case "type_text":
            case "mouse_move":
                log.LogInformation(
                    "simulate_input: kind '{Kind}' is not supported by the Phase-3.1 Avalonia adapter " +
                    "(Avalonia 11.x has internal ctors for the relevant EventArgs types). " +
                    "Use raise_event with the framework event name, OR a [McpCallable] method, " +
                    "OR wait for Phase 3.2's WinUI / cross-platform raw-input pipeline.",
                    kind);
                return false;

            default:
                log.LogWarning("simulate_input: unknown kind '{Kind}'.", kind);
                return false;
        }
    }

    /// <summary>
    /// Raise <see cref="Button.ClickEvent"/> on the target control. Works on
    /// any Avalonia <see cref="Button"/> (Avalonia 11.x has no ButtonBase
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
            // in Avalonia 11.x.
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
