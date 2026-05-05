// Marionette.NET — WinUI input simulation helper (Phase 3.2; refined Phase 9.3)
//
// Implements simulate_input for WinUI 3 (Microsoft.UI.Xaml). WinUI's input
// surface differs meaningfully from WPF/Avalonia:
//
//   * NO Mouse.PrimaryDevice / Keyboard.PrimaryDevice — the per-thread input
//     primitives that WPF exposes are not present.
//   * NO UIElement.RaiseEvent that synthesises a routed event the way WPF does.
//     WinUI exposes RoutedEvents as standard CLR events on the control surface;
//     adopters subscribe via `+=`.
//   * Real input fidelity is provided by Windows.UI.Input.Preview.Injection.InputInjector
//     (the Windows SDK projection — accessible from WinUI via the SDK contract).
//
// InputInjector availability (Phase 9.3 update):
//
//   The original Phase 3.2 framing was that `InputInjector.TryCreate()` requires
//   either an elevated process OR an MSIX-packaged manifest that declares the
//   `inputInjectionBrokered` restricted capability. That description matched
//   Windows 10 behaviour and Microsoft's documentation circa 2022-2023.
//
//   Verified May 2026: on current Windows 11 (build 26200, .NET 10.0.6) a
//   plain console app and an unpackaged WinUI-3 process both return a working
//   `InputInjector` from `TryCreate()` with no manifest declaration and no
//   elevation. Microsoft has relaxed the gate (or never enforced it on Win11
//   the way it was documented). The strict requirement still applies to:
//      * Older Windows builds (Win10 ≤ 22H2, certain enterprise lockdown
//        configurations);
//      * Windows Sandbox / Hyper-V VM scenarios with input-injection blocked
//        at the host level;
//      * Locked-down SKUs (Education / Enterprise with input restrictions).
//
//   Runtime behaviour: TryCreateInjector probes once and logs whether the
//   keyboard / mouse-move path is operational. Adopters who hit a null
//   injector on a constrained system have two AOT-clean fall-backs:
//      * Run the host elevated;
//      * Switch the sample to MSIX-packaged with the
//        `inputInjectionBrokered` restricted capability declared in
//        `Package.appxmanifest`.
//   See `docs/winui-input-injection.md` for the manifest snippet + the
//   Visual Studio project-property change.
//
// Phase 3.2 strategy (unchanged in 9.3):
//   * For "click" / "double_click" / "right_click": prefer the
//     ButtonAutomationPeer.Invoke() path (or any IInvokeProvider). It runs the
//     framework's Click handler synchronously, fires bubbling, and works
//     unpackaged & unelevated. If the target isn't a button, fall through to
//     InputInjector.
//   * For "key_press" / "key_down" / "key_up" / "type_text" / "mouse_move":
//     use InputInjector.InjectKeyboardInput / InjectMouseInput. Skips with a
//     logged limitation when InputInjector.TryCreate returns null.
//
// AOT note: we don't reflect on user types here. The reflection in the input
// simulator is bounded to type checks (target is Button, target is FrameworkElement,
// etc.) which the trim analyser handles fine.
//
// Threading: every public method here MUST be called on the WinUI UI thread.
// WinUiAutomationAdapter wraps each call in DispatchAsync<T>(...) before
// invoking us.

using System;
using System.Collections.Generic;
using System.Globalization;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Microsoft.Extensions.Logging;

using Windows.System;
using Windows.UI.Input.Preview.Injection;

namespace Marionette.Adapter.WinUI.Internal;

/// <summary>
/// Builds and dispatches synthetic WinUI input events against a target
/// <see cref="FrameworkElement"/>. UI-thread-only.
/// </summary>
internal static class WinUiInputSimulator
{
    /// <summary>
    /// Drive the named input kind against <paramref name="target"/>. Returns
    /// <see langword="true"/> on success, <see langword="false"/> when the
    /// kind isn't recognised, args are missing/wrong, or the underlying
    /// injection primitive isn't available.
    /// </summary>
    public static bool Simulate(
        FrameworkElement target,
        string kind,
        IReadOnlyDictionary<string, object?>? args,
        ILogger log)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrEmpty(kind)) return false;

        switch (kind)
        {
            case "click":
                return TryInvokeAutomationPeer(target, log) || TryInjectMouseClick(target, leftButton: true, doubleClick: false, log);

            case "double_click":
                if (TryInvokeAutomationPeer(target, log)) return true; // peers fire one logical click; double-click handler still hears nothing.
                return TryInjectMouseClick(target, leftButton: true, doubleClick: true, log);

            case "right_click":
                // Right-click cannot be IInvoke'd via UIA; go straight to the
                // injector. Many WinUI controls treat right-click as a context
                // menu cue - if no injector is available we surface the
                // limitation rather than fake-succeed.
                return TryInjectMouseClick(target, leftButton: false, doubleClick: false, log);

            case "key_press":
                {
                    var key = ParseKey(args, log);
                    if (key is null) return false;
                    return InjectKey(key.Value, isDown: true, log) && InjectKey(key.Value, isDown: false, log);
                }

            case "key_down":
                {
                    var key = ParseKey(args, log);
                    if (key is null) return false;
                    return InjectKey(key.Value, isDown: true, log);
                }

            case "key_up":
                {
                    var key = ParseKey(args, log);
                    if (key is null) return false;
                    return InjectKey(key.Value, isDown: false, log);
                }

            case "type_text":
                {
                    if (args is null || !args.TryGetValue("text", out var t) || t is not string text)
                    {
                        log.LogWarning("simulate_input(type_text) requires args.text (string).");
                        return false;
                    }
                    // Prefer the semantic path when the target is a TextBox: set
                    // its Text property directly. This avoids InputInjector
                    // (which requires elevation/manifest capabilities) for the
                    // most common adopter use case.
                    if (target is TextBox tb)
                    {
                        try
                        {
                            tb.Text = text;
                            return true;
                        }
                        catch (Exception ex)
                        {
                            log.LogWarning(ex, "simulate_input(type_text) on TextBox '{Name}' threw.", tb.Name);
                            return false;
                        }
                    }
                    return InjectText(text, log);
                }

            case "mouse_move":
                {
                    var p = ResolveScreenPoint(target, args);
                    if (p is null) return false;
                    return InjectMouseMove(p.Value.X, p.Value.Y, log);
                }

            default:
                log.LogWarning("simulate_input: unknown kind '{Kind}'.", kind);
                return false;
        }
    }

    // -------------------------------------------------------------------------
    // Path A: AutomationPeer (no elevation, no manifest capability needed).
    // -------------------------------------------------------------------------

    private static bool TryInvokeAutomationPeer(FrameworkElement target, ILogger log)
    {
        // Walk up the parent chain looking for a Button / IInvokeProvider-capable
        // peer. WinUI matches WPF's Button.AutomationPeer giving an
        // IInvokeProvider; we use that for the "click" path because it (a)
        // works unpackaged + unelevated, (b) fires the framework's Click
        // handler synchronously, (c) doesn't depend on screen coordinates.
        FrameworkElement? cur = target;
        while (cur is not null)
        {
            if (cur is ButtonBase btn)
            {
                try
                {
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(btn);
                    var invoke = peer?.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
                    if (invoke is not null)
                    {
                        invoke.Invoke();
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "simulate_input(click): IInvokeProvider.Invoke on '{Name}' threw.", btn.Name);
                    return false;
                }
            }
            cur = cur.Parent as FrameworkElement;
        }
        return false;
    }

    // -------------------------------------------------------------------------
    // Path B: InputInjector (real OS-level input; may need manifest capability).
    // -------------------------------------------------------------------------

    private static InputInjector? TryCreateInjector(ILogger log)
    {
        try
        {
            var injector = InputInjector.TryCreate();
            if (injector is null)
            {
                log.LogInformation(
                    "simulate_input: InputInjector.TryCreate returned null on this system. " +
                    "Keyboard / mouse-move paths fall back to AutomationPeer + property-set " +
                    "where possible; arbitrary key/pointer injection is unavailable. " +
                    "Common causes: pre-Windows-11-22H2 build, locked-down SKU, or sandbox " +
                    "host blocking input injection. See docs/winui-input-injection.md for the " +
                    "elevated-host / MSIX-capability fallback paths.");
            }
            return injector;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "simulate_input: InputInjector.TryCreate threw unexpectedly.");
            return null;
        }
    }

    /// <summary>
    /// Phase 9.3: eagerly probe <see cref="InputInjector.TryCreate"/> at
    /// adapter construction so adopters see the keyboard/mouse-move
    /// availability state in their startup log instead of finding out at
    /// the first failed simulate_input call. Returns
    /// <see langword="true"/> when keyboard / mouse-move injection is
    /// available on this system.
    /// </summary>
    /// <remarks>
    /// Calling <c>TryCreate()</c> twice is cheap — the runtime caches the
    /// broker handle internally. We discard the probe result; the
    /// per-call <see cref="TryCreateInjector"/> path retrieves a fresh
    /// instance for actual injection.
    /// </remarks>
    public static bool ProbeInjectorAvailability(ILogger log)
    {
        try
        {
            var probe = InputInjector.TryCreate();
            if (probe is null)
            {
                log.LogInformation(
                    "WinUI input-injection probe: InputInjector unavailable. " +
                    "simulate_input falls back to AutomationPeer for clicks and " +
                    "TextBox.Text for type_text. Other kinds (key_press, mouse_move) " +
                    "will return success=false. See docs/winui-input-injection.md.");
                return false;
            }
            log.LogInformation(
                "WinUI input-injection probe: InputInjector available — full " +
                "simulate_input matrix (click / key_* / type_text / mouse_move) operational.");
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "WinUI input-injection probe: InputInjector.TryCreate threw — " +
                "treating injector as unavailable.");
            return false;
        }
    }

    private static bool TryInjectMouseClick(FrameworkElement target, bool leftButton, bool doubleClick, ILogger log)
    {
        var screenPt = TryComputeScreenCenter(target, log);
        if (screenPt is null)
        {
            log.LogInformation("simulate_input(click): could not compute screen-space center for '{Name}'; aborting.", target.Name);
            return false;
        }

        var injector = TryCreateInjector(log);
        if (injector is null)
        {
            // Phase 14: Win32 SendInput click fallback. Move mouse to the
            // computed center, then send the matching down+up sequence.
            return TryWin32ClickFallback(screenPt.Value.X, screenPt.Value.Y, leftButton, doubleClick, log);
        }

        try
        {
            var moveAndClick = new List<InjectedInputMouseInfo>
            {
                new()
                {
                    DeltaX = screenPt.Value.X,
                    DeltaY = screenPt.Value.Y,
                    MouseOptions = InjectedInputMouseOptions.MoveNoCoalesce | InjectedInputMouseOptions.Absolute,
                },
                new()
                {
                    MouseOptions = leftButton ? InjectedInputMouseOptions.LeftDown : InjectedInputMouseOptions.RightDown,
                },
                new()
                {
                    MouseOptions = leftButton ? InjectedInputMouseOptions.LeftUp : InjectedInputMouseOptions.RightUp,
                },
            };
            if (doubleClick)
            {
                moveAndClick.Add(new InjectedInputMouseInfo
                {
                    MouseOptions = leftButton ? InjectedInputMouseOptions.LeftDown : InjectedInputMouseOptions.RightDown,
                });
                moveAndClick.Add(new InjectedInputMouseInfo
                {
                    MouseOptions = leftButton ? InjectedInputMouseOptions.LeftUp : InjectedInputMouseOptions.RightUp,
                });
            }
            injector.InjectMouseInput(moveAndClick);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "simulate_input(click) InjectMouseInput on '{Name}' threw; trying Win32 SendInput fallback.", target.Name);
            return TryWin32ClickFallback(screenPt.Value.X, screenPt.Value.Y, leftButton, doubleClick, log);
        }
    }

    /// <summary>
    /// Phase 14: Win32 <c>SendInput</c> click fallback. Moves the mouse to the
    /// computed screen center then sends the appropriate down/up sequence
    /// (twice for double-click).
    /// </summary>
    private static bool TryWin32ClickFallback(int screenX, int screenY, bool leftButton, bool doubleClick, ILogger log)
    {
        if (!Marionette.Runtime.Internal.Win32InputInjector.IsAvailable) return false;
        if (!Marionette.Runtime.Internal.Win32InputInjector.SendMouseMoveAbsolute(screenX, screenY)) return false;
        var btn = leftButton
            ? Marionette.Runtime.Internal.MouseButton.Left
            : Marionette.Runtime.Internal.MouseButton.Right;
        if (!Marionette.Runtime.Internal.Win32InputInjector.SendMouseClick(btn)) return false;
        if (doubleClick && !Marionette.Runtime.Internal.Win32InputInjector.SendMouseClick(btn)) return false;
        log.LogDebug("simulate_input(click) succeeded via Win32 SendInput fallback.");
        return true;
    }

    private static bool InjectMouseMove(int screenX, int screenY, ILogger log)
    {
        var injector = TryCreateInjector(log);
        if (injector is null)
        {
            // Phase 14: Win32 fallback.
            return TryWin32MouseMoveFallback(screenX, screenY, log);
        }
        try
        {
            injector.InjectMouseInput(new[]
            {
                new InjectedInputMouseInfo
                {
                    DeltaX = screenX,
                    DeltaY = screenY,
                    MouseOptions = InjectedInputMouseOptions.MoveNoCoalesce | InjectedInputMouseOptions.Absolute,
                },
            });
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "simulate_input(mouse_move) threw; trying Win32 SendInput fallback.");
            return TryWin32MouseMoveFallback(screenX, screenY, log);
        }
    }

    /// <summary>
    /// Phase 14: Win32 <c>SendInput</c> mouse-move fallback when
    /// <see cref="InputInjector"/> is unavailable.
    /// </summary>
    private static bool TryWin32MouseMoveFallback(int screenX, int screenY, ILogger log)
    {
        if (!Marionette.Runtime.Internal.Win32InputInjector.IsAvailable) return false;
        var ok = Marionette.Runtime.Internal.Win32InputInjector.SendMouseMoveAbsolute(screenX, screenY);
        if (ok) log.LogDebug("simulate_input(mouse_move) succeeded via Win32 SendInput fallback.");
        return ok;
    }

    private static bool InjectKey(VirtualKey key, bool isDown, ILogger log)
    {
        var injector = TryCreateInjector(log);
        if (injector is null)
        {
            // Phase 14: InputInjector unavailable (Win10, locked-down session,
            // missing manifest cap). Try Win32 SendInput as last resort —
            // works on Win7+ and in non-elevated processes.
            return TryWin32KeyFallback(key, isDown, log);
        }
        try
        {
            var info = new InjectedInputKeyboardInfo
            {
                VirtualKey = (ushort)key,
                KeyOptions = isDown ? InjectedInputKeyOptions.None : InjectedInputKeyOptions.KeyUp,
            };
            injector.InjectKeyboardInput(new[] { info });
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "simulate_input(key) threw; trying Win32 SendInput fallback.");
            return TryWin32KeyFallback(key, isDown, log);
        }
    }

    /// <summary>
    /// Phase 14: Win32 <c>SendInput</c> fallback for keyboard events when
    /// <see cref="InputInjector"/> is unavailable (Win10, missing manifest
    /// capability, locked-down session). Maps the WinUI <see cref="VirtualKey"/>
    /// to a Win32 VK code (they share the same numeric values).
    /// </summary>
    private static bool TryWin32KeyFallback(VirtualKey key, bool isDown, ILogger log)
    {
        if (!Marionette.Runtime.Internal.Win32InputInjector.IsAvailable) return false;
        var vk = (Marionette.Runtime.Internal.VirtualKey)(ushort)key;
        var ok = isDown
            ? Marionette.Runtime.Internal.Win32InputInjector.SendKeyDown(vk)
            : Marionette.Runtime.Internal.Win32InputInjector.SendKeyUp(vk);
        if (ok) log.LogDebug("simulate_input(key) succeeded via Win32 SendInput fallback.");
        return ok;
    }

    private static bool InjectText(string text, ILogger log)
    {
        var injector = TryCreateInjector(log);
        if (injector is null)
        {
            // Phase 14: Win32 SendInput Unicode-mode fallback.
            if (Marionette.Runtime.Internal.Win32InputInjector.IsAvailable &&
                Marionette.Runtime.Internal.Win32InputInjector.SendUnicodeText(text))
            {
                log.LogDebug("simulate_input(type_text) succeeded via Win32 SendInput Unicode fallback.");
                return true;
            }
            return false;
        }
        try
        {
            // ScanCode flag + Unicode flag is the canonical "type a unicode
            // character" pattern with InputInjector. We send DOWN+UP per
            // character.
            var infos = new List<InjectedInputKeyboardInfo>(capacity: text.Length * 2);
            foreach (var ch in text)
            {
                infos.Add(new InjectedInputKeyboardInfo
                {
                    ScanCode = (ushort)ch,
                    KeyOptions = InjectedInputKeyOptions.ScanCode | InjectedInputKeyOptions.Unicode,
                });
                infos.Add(new InjectedInputKeyboardInfo
                {
                    ScanCode = (ushort)ch,
                    KeyOptions = InjectedInputKeyOptions.ScanCode | InjectedInputKeyOptions.Unicode | InjectedInputKeyOptions.KeyUp,
                });
            }
            injector.InjectKeyboardInput(infos);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "simulate_input(type_text) threw.");
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Coordinate / argument parsing
    // -------------------------------------------------------------------------

    private record struct ScreenPoint(int X, int Y);

    private static ScreenPoint? TryComputeScreenCenter(FrameworkElement target, ILogger log)
    {
        try
        {
            // Element-relative center -> Window-content-relative point.
            var transform = target.TransformToVisual(null);
            var elementBounds = transform.TransformBounds(
                new Windows.Foundation.Rect(0, 0, target.ActualWidth, target.ActualHeight));
            var contentX = elementBounds.X + elementBounds.Width / 2.0;
            var contentY = elementBounds.Y + elementBounds.Height / 2.0;

            // Find the owning Window so we can map content -> screen.
            var owningWindow = FindOwningWindow(target);
            if (owningWindow is null)
            {
                log.LogWarning("simulate_input: target '{Name}' has no owning window; cannot compute screen coordinates.", target.Name);
                return null;
            }

            // Window.Bounds is in screen coordinates (DIPs at 1:1 for desktop
            // single-monitor setups; multi-monitor / HiDPI is best-effort).
            var winBounds = owningWindow.Bounds;
            var screenX = (int)Math.Round(winBounds.X + contentX);
            var screenY = (int)Math.Round(winBounds.Y + contentY);
            return new ScreenPoint(screenX, screenY);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "simulate_input: failed to compute screen coordinates for '{Name}'.", target.Name);
            return null;
        }
    }

    private static ScreenPoint? ResolveScreenPoint(FrameworkElement target, IReadOnlyDictionary<string, object?>? args)
    {
        // Default: element center mapped to screen.
        var center = TryComputeScreenCenter(target, NullLogger);
        if (args is null) return center;

        // If the user provided x/y, treat them as element-relative content
        // coordinates and add to the window's screen origin.
        var owningWindow = FindOwningWindow(target);
        if (owningWindow is null) return center;
        var winBounds = owningWindow.Bounds;

        if (args.TryGetValue("x", out var xR) && TryGetDouble(xR, out var xVal) &&
            args.TryGetValue("y", out var yR) && TryGetDouble(yR, out var yVal))
        {
            var transform = target.TransformToVisual(null);
            var pt = transform.TransformPoint(new Windows.Foundation.Point(xVal, yVal));
            return new ScreenPoint(
                (int)Math.Round(winBounds.X + pt.X),
                (int)Math.Round(winBounds.Y + pt.Y));
        }
        return center;
    }

    private static Window? FindOwningWindow(FrameworkElement element)
    {
        // WinUI 3 doesn't have a "find owning window" helper; we reverse-look
        // by walking the parent chain to a UIElement that matches a tracked
        // Window's Content. (The Window class itself is not a DependencyObject
        // in the visual tree.)
        DependencyObject? cur = element;
        while (cur is not null)
        {
            var parent = VisualTreeHelper.GetParent(cur);
            if (parent is null) break;
            cur = parent;
        }
        if (cur is null) return null;
        // `cur` is the visual root - i.e. some Window's Content. Walk tracked
        // windows looking for the one whose Content === cur.
        foreach (var w in WindowTracker.Snapshot())
        {
            if (ReferenceEquals(w.Content, cur)) return w;
        }
        return null;
    }

    private static VirtualKey? ParseKey(IReadOnlyDictionary<string, object?>? args, ILogger log)
    {
        if (args is null || !args.TryGetValue("key", out var raw) || raw is not string s || string.IsNullOrEmpty(s))
        {
            log.LogWarning("simulate_input(key_*) requires args.key (string), e.g. \"Enter\" / \"A\" / \"F5\".");
            return null;
        }
        if (Enum.TryParse<VirtualKey>(s, ignoreCase: true, out var k)) return k;
        // Single-character convenience: "a" -> VirtualKey.A.
        if (s.Length == 1)
        {
            var ch = char.ToUpperInvariant(s[0]);
            if (ch >= 'A' && ch <= 'Z') return (VirtualKey)((int)VirtualKey.A + (ch - 'A'));
            if (ch >= '0' && ch <= '9') return (VirtualKey)((int)VirtualKey.Number0 + (ch - '0'));
        }
        log.LogWarning("simulate_input: cannot parse key '{Key}'. Use a Windows.System.VirtualKey name (Enter, A, F5, ...).", s);
        return null;
    }

    private static bool TryGetDouble(object? v, out double d)
    {
        switch (v)
        {
            case double dd: d = dd; return true;
            case int ii: d = ii; return true;
            case long ll: d = ll; return true;
            case float ff: d = ff; return true;
            case string ss when double.TryParse(ss, NumberStyles.Float, CultureInfo.InvariantCulture, out var dp):
                d = dp;
                return true;
            default:
                d = 0;
                return false;
        }
    }

    // Fallback logger for the args-parsing path that we don't want to surface
    // diagnostics for - the caller's logger handles real failures.
    private static readonly ILogger NullLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}
