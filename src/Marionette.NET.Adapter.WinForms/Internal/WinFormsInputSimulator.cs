// Marionette.NET — WinForms input simulation helper (Phase 15)
//
// Implements the simulate_input kinds against WinForms controls. WinForms has
// no public "synthesise this routed event" API — every input path goes through
// the OS message queue, so we use the Phase-14 Win32InputInjector verbatim.
//
// Two-step pattern:
//   1. Focus the target Control (semantic focus shift).
//   2. Have the OS deliver synthetic input via SendInput (it lands on the
//      currently-focused window).
//
// Spike A verified all three required paths (key press → Click event,
// Unicode text → TextBox.Text, mouse-move + click). See
// .phase15/spike-a-findings.md for the measured evidence.
//
// Threading: callers MUST be on the WinForms UI thread. The adapter wraps
// every call in DispatchAsync<bool> before invoking us. SendInput itself is
// thread-safe — but Control.Focus() is not.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Forms;

using Marionette.Runtime.Internal;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.WinForms.Internal;

/// <summary>
/// Drives synthetic input against WinForms controls via the Phase-14
/// <see cref="Win32InputInjector"/>. UI-thread-only.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WinFormsInputSimulator
{
    /// <summary>
    /// Drive the named input kind against <paramref name="target"/>. Returns
    /// <see langword="true"/> on success, <see langword="false"/> when the
    /// kind isn't recognised, args are missing, or the OS rejects the input.
    /// </summary>
    public static bool Simulate(
        Control target,
        string kind,
        IReadOnlyDictionary<string, object?>? args,
        ILogger log)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrEmpty(kind)) return false;
        if (!Win32InputInjector.IsAvailable)
        {
            log.LogWarning("simulate_input: Win32InputInjector unavailable (non-Windows host).");
            return false;
        }

        // Ensure the target's owning form is the foreground window — synthetic
        // input lands on whatever currently has focus. Without this step,
        // simulate_input could leak keystrokes to whichever window happens to
        // have focus when the call lands.
        EnsureForeground(target);

        // Then focus the specific control. Buttons need keyboard focus to
        // receive Space; TextBoxes need it to receive characters.
        if (target.CanFocus && !target.Focused)
        {
            target.Focus();
        }

        switch (kind)
        {
            case "click":
                return ClickAtControlCenter(target, MouseButton.Left, log);
            case "double_click":
                if (!ClickAtControlCenter(target, MouseButton.Left, log)) return false;
                // Two real clicks within Windows' double-click time produce a
                // DoubleClick event on controls that opt-in (Form, ListView,
                // etc.). For Button.Click semantics, a single click is enough;
                // we send two and let the framework decide which event fires.
                Thread.Sleep(20);
                return ClickAtControlCenter(target, MouseButton.Left, log);
            case "right_click":
                return ClickAtControlCenter(target, MouseButton.Right, log);
            case "key_press":
                {
                    if (TryGetKey(args, out var key))
                    {
                        return Win32InputInjector.SendKeyPress(key);
                    }
                    log.LogWarning("simulate_input(key_press) requires args.key (string).");
                    return false;
                }
            case "key_down":
                {
                    if (TryGetKey(args, out var key))
                    {
                        return Win32InputInjector.SendKeyDown(key);
                    }
                    log.LogWarning("simulate_input(key_down) requires args.key (string).");
                    return false;
                }
            case "key_up":
                {
                    if (TryGetKey(args, out var key))
                    {
                        return Win32InputInjector.SendKeyUp(key);
                    }
                    log.LogWarning("simulate_input(key_up) requires args.key (string).");
                    return false;
                }
            case "type_text":
                {
                    if (args is null || !args.TryGetValue("text", out var t) || t is not string text)
                    {
                        log.LogWarning("simulate_input(type_text) requires args.text (string).");
                        return false;
                    }
                    return Win32InputInjector.SendUnicodeText(text);
                }
            case "mouse_move":
                {
                    var (sx, sy) = ResolveScreenPoint(target, args);
                    return Win32InputInjector.SendMouseMoveAbsolute(sx, sy);
                }
            default:
                log.LogWarning("simulate_input: unknown kind '{Kind}'.", kind);
                return false;
        }
    }

    private static bool ClickAtControlCenter(Control target, MouseButton button, ILogger log)
    {
        var (sx, sy) = ResolveControlCenter(target);
        if (!Win32InputInjector.SendMouseMoveAbsolute(sx, sy)) return false;
        // A small settle pause helps when controls react to MouseEnter
        // before their Click handler runs (e.g. tooltip-suppression code).
        Thread.Sleep(10);
        return Win32InputInjector.SendMouseClick(button);
    }

    private static (int X, int Y) ResolveControlCenter(Control c)
    {
        var screenPt = c.PointToScreen(new Point(c.Width / 2, c.Height / 2));
        return (screenPt.X, screenPt.Y);
    }

    private static (int X, int Y) ResolveScreenPoint(Control target, IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null) return ResolveControlCenter(target);
        if (args.TryGetValue("x", out var xR) && TryGetInt(xR, out var x) &&
            args.TryGetValue("y", out var yR) && TryGetInt(yR, out var y))
        {
            // Caller-supplied x/y are interpreted as control-local coords —
            // matches the WPF/Avalonia adapters' contract. Convert via
            // PointToScreen so the OS gets absolute desktop pixels.
            var screen = target.PointToScreen(new Point(x, y));
            return (screen.X, screen.Y);
        }
        return ResolveControlCenter(target);
    }

    private static bool TryGetKey(IReadOnlyDictionary<string, object?>? args, out VirtualKey key)
    {
        key = default;
        if (args is null || !args.TryGetValue("key", out var raw) || raw is not string s)
        {
            return false;
        }
        var parsed = Win32InputInjector.TryParseKeyName(s);
        if (parsed is null) return false;
        key = parsed.Value;
        return true;
    }

    private static bool TryGetInt(object? v, out int n)
    {
        switch (v)
        {
            case int ii: n = ii; return true;
            case long ll: n = (int)ll; return true;
            case double dd: n = (int)dd; return true;
            case float ff: n = (int)ff; return true;
            case string ss when int.TryParse(ss, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p):
                n = p;
                return true;
            default:
                n = 0;
                return false;
        }
    }

    private static void EnsureForeground(Control target)
    {
        // Best-effort: bring the owning form to the foreground so synthetic
        // OS input lands on it. Windows enforces "you can only steal focus
        // if you're already the foreground process" — automated callers
        // typically already are (the host process owns the form), but a
        // long-running stdio MCP server might have lost focus to the user's
        // current window. SetForegroundWindow is the closest thing to a
        // forceful focus shift available without UI Automation; it can fail
        // silently (returns FALSE) and we accept that — the synthetic input
        // will land on the actually-foreground window, which is documented
        // limitation of the WinForms adapter.
        var form = target.FindForm();
        if (form is null) return;
        try { form.Activate(); } catch { /* ignore */ }
        try { _ = SetForegroundWindow(form.Handle); } catch { /* ignore */ }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
