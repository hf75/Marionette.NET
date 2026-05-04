// Marionette.NET — MAUI input simulation helper (Phase 4.1)
//
// Implements simulate_input for .NET MAUI 10.x. MAUI's input surface is
// fundamentally different from WPF/Avalonia/WinUI:
//
//   * NO unified raw-input pipeline. MAUI delegates platform input to the
//     handlers (WinUI handler on Windows, UIKit on iOS, Android views, etc.)
//     and there is no cross-platform `InputManager.ProcessInput` analogue.
//   * NO public RoutedEvent surface. MAUI events are standard CLR events on
//     the Element / View / Control hierarchy; subscribers register with
//     `+=`, the platform handler bridges from native input to those events.
//   * NO `RaiseEvent` API on Element. Synthesising an event is done either
//     via the controller interface (`IButtonController.SendClicked()`) or
//     by reflection on the compiler-emitted backing field (Phase 3.2 WinUI
//     pattern; covered separately by MauiEventRaiser).
//   * NO `AutomationPeer.Invoke()` cross-platform. Each platform has its own
//     accessibility shape; MAUI doesn't surface one.
//
// Phase 4.1 strategy follows MASTERPLAN tenet 2 ("Semantic beats visual"):
//
//   * For "click" / "double_click" / "right_click" on a Button (or any
//     IButtonController-implementing element): call
//     `IButtonController.SendClicked()`. This fires the framework's Click
//     event AND any bound Command in one call - the same path the platform
//     handler uses when the user actually clicks. Works on every MAUI head
//     (Windows / Android / iOS / MacCatalyst) without elevation, capabilities,
//     or platform-specific input injection.
//   * For "type_text" on an Entry / Editor / SearchBar: set the Text
//     property directly. This bypasses the platform IME but produces the
//     same TextChanged event flow that adopters subscribe to.
//   * For "key_press" / "key_down" / "key_up" / "mouse_move": Phase 4.1
//     returns success:false with a logged limitation. MAUI 10.x doesn't
//     expose a publicly-constructible KeyboardEventArgs; adopters who need
//     keyboard automation should:
//       (a) use a [McpCallable] method that mutates the underlying state, or
//       (b) on Windows-only adopters, escalate to platform-specific
//           InputInjector via custom code (Phase 6 may surface this).
//
// Threading: every public method here MUST be called on the MAUI UI thread.
// MauiUiAutomationAdapter wraps each call in DispatchAsync<T>(...) before
// invoking us.

using System;
using System.Collections.Generic;

using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.Maui.Internal;

/// <summary>
/// Builds and dispatches synthetic MAUI input events against a target
/// <see cref="Element"/>. UI-thread-only.
/// </summary>
internal static class MauiInputSimulator
{
    /// <summary>
    /// Drive the named input kind against <paramref name="target"/>. Returns
    /// <see langword="true"/> on success, <see langword="false"/> when the
    /// kind isn't recognised, args are missing/wrong, or the kind isn't
    /// supported by the Phase-4.1 MAUI adapter.
    /// </summary>
    public static bool Simulate(
        Element target,
        string kind,
        IReadOnlyDictionary<string, object?>? args,
        ILogger log)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (string.IsNullOrEmpty(kind)) return false;

        switch (kind)
        {
            case "click":
                return TrySendClick(target, log);

            case "double_click":
                // MAUI doesn't have a separate DoubleClick event; fire Click
                // twice to match the WinUI / Avalonia "second click is the
                // semantic effect" convention.
                if (!TrySendClick(target, log)) return false;
                return TrySendClick(target, log);

            case "right_click":
                // No public right-click semantic surface in MAUI. Adopters
                // who need context-menu semantics should use a [McpCallable]
                // method bound to the same handler.
                log.LogInformation(
                    "simulate_input(right_click): not supported by the Phase-4.1 MAUI adapter " +
                    "(MAUI has no public right-click semantic surface). " +
                    "Use a [McpCallable] for the context-menu action.");
                return false;

            case "type_text":
                return TryTypeText(target, args, log);

            case "key_press":
            case "key_down":
            case "key_up":
            case "mouse_move":
                log.LogInformation(
                    "simulate_input: kind '{Kind}' is not supported by the Phase-4.1 MAUI adapter " +
                    "(MAUI 10.x has no publicly-constructible keyboard / pointer event args). " +
                    "Use a [McpCallable] method that mutates the underlying state, OR " +
                    "raise_event with the framework event name.",
                    kind);
                return false;

            default:
                log.LogWarning("simulate_input: unknown kind '{Kind}'.", kind);
                return false;
        }
    }

    /// <summary>
    /// Fire a Click on the target. Walks up the parent chain looking for an
    /// <see cref="IButtonController"/>-capable element (typically
    /// <see cref="Button"/>) — the canonical MAUI semantic surface for "the
    /// user pressed this button".
    /// </summary>
    private static bool TrySendClick(Element target, ILogger log)
    {
        // Walk up via Element.Parent looking for a Button or other
        // IButtonController. MAUI's nested layout (a TextLabel inside a
        // Button's content) means the LLM might address the inner label by
        // AutomationId; we want the click to land on the surrounding Button.
        Element? cur = target;
        while (cur is not null)
        {
            if (cur is IButtonController bc)
            {
                try
                {
                    bc.SendClicked();
                    return true;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex,
                        "simulate_input(click): IButtonController.SendClicked on '{Name}' threw.",
                        (cur as Element)?.AutomationId ?? "(unset)");
                    return false;
                }
            }
            cur = cur.Parent;
        }
        log.LogInformation(
            "simulate_input(click) on {Type}: no IButtonController in the parent chain. " +
            "Phase 4.1 MAUI simulate_input click handles Button-like targets (anything " +
            "implementing IButtonController). For non-button controls use raise_event " +
            "with a specific event name, OR a [McpCallable] method.",
            target.GetType().Name);
        return false;
    }

    /// <summary>
    /// Set the Text property of an Entry / Editor / SearchBar. The semantic
    /// equivalent of typing - bypasses the platform IME but fires
    /// TextChanged / TextChanging / Completed handlers identically.
    /// </summary>
    private static bool TryTypeText(Element target, IReadOnlyDictionary<string, object?>? args, ILogger log)
    {
        if (args is null || !args.TryGetValue("text", out var t) || t is not string text)
        {
            log.LogWarning("simulate_input(type_text) requires args.text (string).");
            return false;
        }
        try
        {
            switch (target)
            {
                case Entry entry:
                    entry.Text = text;
                    return true;
                case Editor editor:
                    editor.Text = text;
                    return true;
                case SearchBar sb:
                    sb.Text = text;
                    return true;
                case Label label:
                    // Labels aren't really "type-able" but adopters sometimes
                    // address them for status text setting; honour the
                    // semantic intent.
                    label.Text = text;
                    return true;
                default:
                    log.LogInformation(
                        "simulate_input(type_text) on {Type}: target type doesn't expose " +
                        "a public Text setter. Phase 4.1 supports Entry / Editor / " +
                        "SearchBar / Label. Use a [McpCallable] for other input controls.",
                        target.GetType().Name);
                    return false;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "simulate_input(type_text) on '{Name}' threw.",
                target.AutomationId ?? "(unset)");
            return false;
        }
    }
}
