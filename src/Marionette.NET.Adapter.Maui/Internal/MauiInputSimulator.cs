// Marionette.NET — MAUI input simulation helper
//
// History:
//   Phase 4.1  — `click`, `double_click`, `type_text` semantically (via
//                IButtonController.SendClicked + direct Entry/Editor/SearchBar
//                Text property set). Other kinds returned false.
//   Phase 12.3 — completes the matrix where MAUI's public API allows it:
//                  * `key_press` with `key="Enter"` on Entry / Editor / SearchBar
//                    via SendCompleted() / SearchCommand.Execute()
//                  * `right_click` via TapGestureRecognizer with
//                    Buttons=Secondary (when the adopter has wired one)
//                  * `mouse_move` via PointerGestureRecognizer.PointerMovedCommand
//                    (when the adopter has wired one)
//                Other key codes still return success:false — MAUI 10.x has
//                no publicly-constructible KeyboardEventArgs.
//
// MAUI's input model still differs from WPF/Avalonia/WinUI:
//
//   * No unified raw-input pipeline. MAUI delegates to platform handlers
//     and there is no cross-platform `InputManager.ProcessInput` analogue.
//   * No public RoutedEvent surface. MAUI events are CLR events on
//     Element/View/Control; the platform handler bridges native input.
//   * Phase 12.3 leverages MAUI's gesture-recognizer system instead — when
//     adopters register a PointerGestureRecognizer (or a Secondary-button
//     TapGestureRecognizer) on a target View, we invoke the corresponding
//     command. That's the closest semantic equivalent MAUI ships.
//
// Strategy (MASTERPLAN tenet 2, "Semantic beats visual"):
//
//   * `click` / `double_click`: IButtonController.SendClicked.
//   * `right_click`: TapGestureRecognizer with ButtonsMask.Secondary, command
//     execution. Adopters without a Secondary-button gesture see false.
//   * `type_text`: direct Text property set (Entry/Editor/SearchBar/Label).
//   * `key_press` with key="Enter": SendCompleted() on Entry/Editor,
//     SearchCommand.Execute() on SearchBar. Other keys return false.
//   * `key_down` / `key_up`: not supported (no public events to fire).
//   * `mouse_move`: PointerGestureRecognizer.PointerMovedCommand.Execute().
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
                // Phase 12.3: invoke a TapGestureRecognizer with
                // ButtonsMask.Secondary if the adopter has wired one on the
                // target View. Otherwise log + return false.
                return TrySendRightClick(target, log);

            case "type_text":
                return TryTypeText(target, args, log);

            case "key_press":
                // Phase 12.3: only `key="Enter"` is meaningful in MAUI's
                // semantic input model — that's the form-submit path on
                // Entry / Editor / SearchBar. Other keys return false.
                return TrySendEnter(target, args, log);

            case "key_down":
            case "key_up":
                log.LogInformation(
                    "simulate_input: kind '{Kind}' is not supported by the MAUI adapter — " +
                    "MAUI 10.x has no publicly-constructible KeyboardEventArgs and no public " +
                    "way to fire the platform handler's key-down / key-up events. Use a " +
                    "[McpCallable] method that mutates the underlying state, OR `key_press` " +
                    "with key=\"Enter\" for the form-submit semantic on Entry / Editor / SearchBar.",
                    kind);
                return false;

            case "mouse_move":
                // Phase 12.3: invoke PointerGestureRecognizer.PointerMovedCommand
                // when the adopter has wired one on the target View.
                return TrySendPointerMove(target, log);

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
    /// Phase 12.3: fire the form-submit semantic on text inputs:
    /// <list type="bullet">
    ///   <item><description><see cref="Entry"/>: <c>SendCompleted()</c> — fires <c>Completed</c> event + executes <c>ReturnCommand</c>.</description></item>
    ///   <item><description><see cref="Editor"/>: <c>SendCompleted()</c>.</description></item>
    ///   <item><description><see cref="SearchBar"/>: invokes <c>SearchCommand</c> with <c>SearchCommandParameter</c>.</description></item>
    /// </list>
    /// Only fires when <c>args.key == "Enter"</c> (case-insensitive); other
    /// key codes log and return false.
    /// </summary>
    private static bool TrySendEnter(Element target, IReadOnlyDictionary<string, object?>? args, ILogger log)
    {
        var key = (args is not null && args.TryGetValue("key", out var k) && k is string ks) ? ks : null;
        if (!string.Equals(key, "Enter", StringComparison.OrdinalIgnoreCase))
        {
            log.LogInformation(
                "simulate_input(key_press) on {Type}: only key=\"Enter\" is supported in the MAUI adapter " +
                "(form-submit semantic). Got key=\"{Key}\". Use a [McpCallable] for arbitrary keys.",
                target.GetType().Name, key ?? "<null>");
            return false;
        }

        try
        {
            switch (target)
            {
                case Entry entry:
                    entry.SendCompleted();
                    return true;
                case Editor editor:
                    editor.SendCompleted();
                    return true;
                case SearchBar sb:
                    if (sb.SearchCommand is { } cmd)
                    {
                        cmd.Execute(sb.SearchCommandParameter);
                        return true;
                    }
                    log.LogInformation(
                        "simulate_input(key_press, Enter) on SearchBar '{Name}': SearchCommand is not bound. " +
                        "Set a SearchCommand or subscribe to SearchButtonPressed and invoke a [McpCallable] instead.",
                        sb.AutomationId ?? "(unset)");
                    return false;
                default:
                    log.LogInformation(
                        "simulate_input(key_press, Enter) on {Type}: target is not an Entry / Editor / SearchBar. " +
                        "Phase 12.3 supports those three. Use a [McpCallable] for other input controls.",
                        target.GetType().Name);
                    return false;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "simulate_input(key_press, Enter) on '{Name}' threw.",
                target.AutomationId ?? "(unset)");
            return false;
        }
    }

    /// <summary>
    /// Phase 12.3: invoke a <see cref="TapGestureRecognizer"/> bound for
    /// <c>ButtonsMask.Secondary</c> on the target view's gesture-recognizer
    /// chain. MAUI's right-click contract is: the adopter wires a Tap
    /// recognizer with <c>Buttons = ButtonsMask.Secondary</c> and a Command;
    /// we execute that Command.
    /// </summary>
    private static bool TrySendRightClick(Element target, ILogger log)
    {
        if (target is not View view)
        {
            log.LogInformation(
                "simulate_input(right_click) on {Type}: target is not a View, has no GestureRecognizers collection.",
                target.GetType().Name);
            return false;
        }

        foreach (var gr in view.GestureRecognizers)
        {
            if (gr is TapGestureRecognizer tap && (tap.Buttons & ButtonsMask.Secondary) == ButtonsMask.Secondary)
            {
                if (tap.Command is null)
                {
                    log.LogInformation(
                        "simulate_input(right_click) on {Name}: found a Secondary-button TapGestureRecognizer " +
                        "but its Command is not bound.",
                        target.AutomationId ?? "(unset)");
                    continue;
                }
                if (!tap.Command.CanExecute(tap.CommandParameter))
                {
                    log.LogInformation(
                        "simulate_input(right_click) on {Name}: Secondary-button gesture's Command.CanExecute returned false.",
                        target.AutomationId ?? "(unset)");
                    return false;
                }
                tap.Command.Execute(tap.CommandParameter);
                return true;
            }
        }
        log.LogInformation(
            "simulate_input(right_click) on {Name}: no TapGestureRecognizer with Buttons=Secondary attached. " +
            "Adopters who need right-click semantics should attach one with a bound Command.",
            target.AutomationId ?? "(unset)");
        return false;
    }

    /// <summary>
    /// Phase 12.3: invoke a <see cref="PointerGestureRecognizer.PointerMovedCommand"/>
    /// on the target view's gesture-recognizer chain. The recognizer must be
    /// adopter-attached with the command bound.
    /// </summary>
    private static bool TrySendPointerMove(Element target, ILogger log)
    {
        if (target is not View view)
        {
            log.LogInformation(
                "simulate_input(mouse_move) on {Type}: target is not a View, has no GestureRecognizers collection.",
                target.GetType().Name);
            return false;
        }

        foreach (var gr in view.GestureRecognizers)
        {
            if (gr is PointerGestureRecognizer ptr)
            {
                if (ptr.PointerMovedCommand is null)
                {
                    log.LogInformation(
                        "simulate_input(mouse_move) on {Name}: found a PointerGestureRecognizer but " +
                        "PointerMovedCommand is not bound.",
                        target.AutomationId ?? "(unset)");
                    continue;
                }
                if (!ptr.PointerMovedCommand.CanExecute(ptr.PointerMovedCommandParameter))
                {
                    log.LogInformation(
                        "simulate_input(mouse_move) on {Name}: PointerMovedCommand.CanExecute returned false.",
                        target.AutomationId ?? "(unset)");
                    return false;
                }
                ptr.PointerMovedCommand.Execute(ptr.PointerMovedCommandParameter);
                return true;
            }
        }
        log.LogInformation(
            "simulate_input(mouse_move) on {Name}: no PointerGestureRecognizer attached. " +
            "Adopters who need pointer-move semantics should attach one with PointerMovedCommand bound.",
            target.AutomationId ?? "(unset)");
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
