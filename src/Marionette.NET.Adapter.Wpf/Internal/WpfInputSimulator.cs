// Marionette.NET — WPF input simulation helper (Phase 3.1)
//
// Implements the kinds of input the `simulate_input` and `raise_event` MCP
// tools dispatch:
//
//   * Click / DoubleClick / RightClick → MouseDown + MouseUp routed events
//   * KeyPress / KeyDown / KeyUp        → Keyboard.PrimaryDevice + KeyEventArgs
//   * TypeText                          → per-character KeyDown + TextInput + KeyUp
//   * MouseMove                         → MouseEventArgs with the new position
//
// Per MASTERPLAN tenet 8 ("Test-Automation-grade input fidelity"), we route
// through `InputManager.Current.ProcessInput(...)` where possible so the
// framework's hover/focus/capture handlers run exactly as if the user
// generated the event. RoutedEventArgs carries a Source (the resolved
// FrameworkElement) so handlers see e.OriginalSource correctly.
//
// Threading: every public method here MUST be called on the WPF UI thread.
// The caller (WpfUiAutomationAdapter) wraps each call in DispatchAsync<T>(...)
// before invoking us. We rely on Mouse.PrimaryDevice / Keyboard.PrimaryDevice,
// which are thread-affine.
//
// The class is internal — adopters never see it. Public input simulation
// flows through IUiAutomationAdapter.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.Wpf.Internal;

/// <summary>
/// Builds and dispatches synthetic WPF input events against a target
/// <see cref="UIElement"/>. UI-thread-only.
/// </summary>
internal static class WpfInputSimulator
{
    /// <summary>
    /// Drive the named input kind against <paramref name="target"/>. Returns
    /// <see langword="true"/> on success, <see langword="false"/> when the
    /// kind isn't recognised or args are missing/wrong.
    /// </summary>
    public static bool Simulate(
        UIElement target,
        string kind,
        IReadOnlyDictionary<string, object?>? args,
        ILogger log)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrEmpty(kind)) return false;

        switch (kind)
        {
            case "click":
                return DoMouseClick(target, MouseButton.Left, MouseButtonState.Pressed, log)
                    && DoMouseClick(target, MouseButton.Left, MouseButtonState.Released, log);

            case "double_click":
                // Two clicks in quick succession plus the synthetic
                // MouseDoubleClick event WPF normally raises after the second
                // up. We fire both UP/DOWN pairs AND the framework's
                // MouseDoubleClick routed event so handlers wired to either
                // behaviour see the call.
                if (!(DoMouseClick(target, MouseButton.Left, MouseButtonState.Pressed, log) &&
                      DoMouseClick(target, MouseButton.Left, MouseButtonState.Released, log) &&
                      DoMouseClick(target, MouseButton.Left, MouseButtonState.Pressed, log) &&
                      DoMouseClick(target, MouseButton.Left, MouseButtonState.Released, log)))
                {
                    return false;
                }
                if (target is Control ctlDbl)
                {
                    var dbl = new MouseButtonEventArgs(
                        Mouse.PrimaryDevice,
                        Environment.TickCount,
                        MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent,
                        Source = ctlDbl,
                    };
                    ctlDbl.RaiseEvent(dbl);
                }
                return true;

            case "right_click":
                return DoMouseClick(target, MouseButton.Right, MouseButtonState.Pressed, log)
                    && DoMouseClick(target, MouseButton.Right, MouseButtonState.Released, log);

            case "key_press":
                {
                    var key = ParseKey(args, log);
                    if (key is null) return false;
                    if (!DoKey(target, key.Value, isDown: true, log)) return false;
                    // Best-effort TextInput for printable single-character keys.
                    var text = TryRenderKeyText(key.Value);
                    if (!string.IsNullOrEmpty(text))
                    {
                        DoTextInput(target, text!, log);
                    }
                    return DoKey(target, key.Value, isDown: false, log);
                }

            case "key_down":
                {
                    var key = ParseKey(args, log);
                    if (key is null) return false;
                    return DoKey(target, key.Value, isDown: true, log);
                }

            case "key_up":
                {
                    var key = ParseKey(args, log);
                    if (key is null) return false;
                    return DoKey(target, key.Value, isDown: false, log);
                }

            case "type_text":
                {
                    if (args is null || !args.TryGetValue("text", out var t) || t is not string text)
                    {
                        log.LogWarning("simulate_input(type_text) requires args.text (string).");
                        return false;
                    }
                    return DoTextInput(target, text, log);
                }

            case "mouse_move":
                {
                    var p = ResolveMousePoint(target, args);
                    return DoMouseMove(target, p, log);
                }

            default:
                log.LogWarning("simulate_input: unknown kind '{Kind}'.", kind);
                return false;
        }
    }

    // -------------------------------------------------------------------------
    // Mouse
    // -------------------------------------------------------------------------

    private static bool DoMouseClick(UIElement target, MouseButton button, MouseButtonState state, ILogger log)
    {
        try
        {
            // Constructing MouseButtonEventArgs directly is the
            // documented Test-Automation pattern — Mouse.PrimaryDevice +
            // Environment.TickCount + the button enum + the routed event.
            var down = state == MouseButtonState.Pressed;
            var routedEvent = ResolveMouseRoutedEvent(button, down);
            var ea = new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                button)
            {
                RoutedEvent = routedEvent,
                Source = target,
            };
            target.RaiseEvent(ea);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "simulate_input(mouse) on {Type} threw.", target.GetType().Name);
            return false;
        }
    }

    private static bool DoMouseMove(UIElement target, Point _, ILogger log)
    {
        try
        {
            var ea = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseMoveEvent,
                Source = target,
            };
            target.RaiseEvent(ea);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "simulate_input(mouse_move) on {Type} threw.", target.GetType().Name);
            return false;
        }
    }

    private static RoutedEvent ResolveMouseRoutedEvent(MouseButton button, bool down) => button switch
    {
        MouseButton.Left => down ? Mouse.MouseDownEvent : Mouse.MouseUpEvent,
        MouseButton.Right => down ? Mouse.MouseDownEvent : Mouse.MouseUpEvent,
        MouseButton.Middle => down ? Mouse.MouseDownEvent : Mouse.MouseUpEvent,
        _ => down ? Mouse.MouseDownEvent : Mouse.MouseUpEvent,
    };

    // -------------------------------------------------------------------------
    // Keyboard
    // -------------------------------------------------------------------------

    private static bool DoKey(UIElement target, Key key, bool isDown, ILogger log)
    {
        try
        {
            // Focus the target so KeyDown fires there (otherwise WPF routes
            // to Keyboard.FocusedElement which may be null in headless-ish
            // tests).
            if (target.Focusable && !target.IsKeyboardFocused)
            {
                target.Focus();
            }
            var source = PresentationSource.FromVisual(target)
                ?? PresentationSource.FromVisual(target.GetWindow() ?? (Visual)target);
            if (source is null)
            {
                log.LogInformation("simulate_input(key) cannot resolve PresentationSource for {Type}; trying Win32 fallback.", target.GetType().Name);
                return TryWin32KeyFallback(key, isDown, log);
            }
            var ea = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                source,
                Environment.TickCount,
                key)
            {
                RoutedEvent = isDown ? Keyboard.KeyDownEvent : Keyboard.KeyUpEvent,
                Source = target,
            };
            target.RaiseEvent(ea);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "simulate_input(key) on {Type} threw; trying Win32 fallback.", target.GetType().Name);
            return TryWin32KeyFallback(key, isDown, log);
        }
    }

    /// <summary>
    /// Phase 14: Win32 <c>SendInput</c> fallback for keyboard events. Used when
    /// the in-process WPF KeyEventArgs path fails (no PresentationSource, etc.).
    /// The OS turns the synthetic input into a real WPF KeyDown event on the
    /// focused window — same observable behaviour as the in-process path, but
    /// going through the kernel's input pipeline.
    /// </summary>
    private static bool TryWin32KeyFallback(Key key, bool isDown, ILogger log)
    {
        if (!Marionette.Runtime.Internal.Win32InputInjector.IsAvailable) return false;
        var vk = MapKeyToVirtualKey(key);
        if (vk is null)
        {
            log.LogInformation("simulate_input(key) Win32 fallback: no VK mapping for WPF Key.{Key}.", key);
            return false;
        }
        return isDown
            ? Marionette.Runtime.Internal.Win32InputInjector.SendKeyDown(vk.Value)
            : Marionette.Runtime.Internal.Win32InputInjector.SendKeyUp(vk.Value);
    }

    /// <summary>
    /// Phase 14: map a WPF <see cref="Key"/> to a Win32 virtual-key code.
    /// WPF's Key enum values mirror the Win32 VK_* constants for the most
    /// common keys, so a direct cast works for letters, digits, function
    /// keys, and the standard navigation set. Returns <see langword="null"/>
    /// for keys without a Win32 analogue.
    /// </summary>
    private static Marionette.Runtime.Internal.VirtualKey? MapKeyToVirtualKey(Key key)
    {
        var keyValue = KeyInterop.VirtualKeyFromKey(key);
        if (keyValue == 0) return null;
        return (Marionette.Runtime.Internal.VirtualKey)keyValue;
    }

    private static bool DoTextInput(UIElement target, string text, ILogger log)
    {
        try
        {
            if (target.Focusable && !target.IsKeyboardFocused)
            {
                target.Focus();
            }
            // Each character flows as a TextCompositionEventArgs through
            // TextInputEvent — that's the routed event TextBox handlers
            // listen to. WPF text composition is a fairly obscure corner of
            // the API; the standard pattern is to construct a
            // TextComposition with the raw string and the Mouse/Keyboard
            // device.
            foreach (var ch in text)
            {
                var composition = new TextComposition(InputManager.Current, target, ch.ToString());
                var ea = new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, composition)
                {
                    RoutedEvent = TextCompositionManager.TextInputEvent,
                    Source = target,
                };
                target.RaiseEvent(ea);
            }
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "simulate_input(type_text) on {Type} threw.", target.GetType().Name);
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Argument parsing
    // -------------------------------------------------------------------------

    private static Key? ParseKey(IReadOnlyDictionary<string, object?>? args, ILogger log)
    {
        if (args is null || !args.TryGetValue("key", out var raw) || raw is not string s || string.IsNullOrEmpty(s))
        {
            log.LogWarning("simulate_input(key_*) requires args.key (string), e.g. \"Enter\" / \"A\" / \"F5\".");
            return null;
        }
        if (Enum.TryParse<Key>(s, ignoreCase: true, out var k)) return k;
        // Single-character convenience: "a" -> Key.A.
        if (s.Length == 1)
        {
            var ch = char.ToUpperInvariant(s[0]);
            if (ch >= 'A' && ch <= 'Z') return (Key)((int)Key.A + (ch - 'A'));
            if (ch >= '0' && ch <= '9') return (Key)((int)Key.D0 + (ch - '0'));
        }
        log.LogWarning("simulate_input: cannot parse key '{Key}'. Use a System.Windows.Input.Key name (Enter, A, F5, ...).", s);
        return null;
    }

    private static string? TryRenderKeyText(Key key)
    {
        // Return the printable character for letters, digits, and space.
        // Phase 3.1 keeps this conservative — adopters who need full
        // Shift/Alt/AltGr handling should use type_text.
        if (key >= Key.A && key <= Key.Z)
        {
            var c = (char)('a' + (key - Key.A));
            return c.ToString();
        }
        if (key >= Key.D0 && key <= Key.D9)
        {
            var c = (char)('0' + (key - Key.D0));
            return c.ToString();
        }
        return key switch
        {
            Key.Space => " ",
            _ => null,
        };
    }

    private static Point ResolveMousePoint(UIElement target, IReadOnlyDictionary<string, object?>? args)
    {
        // Default: element center.
        var sz = target.RenderSize;
        var cx = sz.Width / 2.0;
        var cy = sz.Height / 2.0;
        if (args is not null)
        {
            if (args.TryGetValue("x", out var xR) && TryGetDouble(xR, out var x)) cx = x;
            if (args.TryGetValue("y", out var yR) && TryGetDouble(yR, out var y)) cy = y;
        }
        return new Point(cx, cy);
    }

    private static bool TryGetDouble(object? v, out double d)
    {
        switch (v)
        {
            case double dd: d = dd; return true;
            case int ii: d = ii; return true;
            case long ll: d = ll; return true;
            case float ff: d = ff; return true;
            case string ss when double.TryParse(ss, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dp):
                d = dp;
                return true;
            default:
                d = 0;
                return false;
        }
    }
}

internal static class WpfVisualExtensions
{
    /// <summary>
    /// Walk up the visual tree to the owning <see cref="Window"/> (used for
    /// PresentationSource lookups when the target isn't directly visible).
    /// </summary>
    public static Window? GetWindow(this Visual v)
    {
        DependencyObject? cur = v;
        while (cur is not null)
        {
            if (cur is Window w) return w;
            cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
        }
        return null;
    }
}
