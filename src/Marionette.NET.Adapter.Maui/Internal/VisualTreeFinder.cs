// Marionette.NET — MAUI visual-tree resolver (Phase 4.1)
//
// Phase 4.1 helper: walks every live `Microsoft.Maui.Controls.Window` looking
// for an `Element` matching the requested name. Mirrors the WPF / Avalonia /
// WinUI VisualTreeFinder shape, against the MAUI public API.
//
// API differences from WPF / Avalonia / WinUI:
//   * NO VisualTreeHelper. MAUI exposes the visual tree via `IVisualTreeElement`
//     (every Element implements it). Walk via `GetVisualChildren()` on
//     `Microsoft.Maui.IVisualTreeElement`.
//   * NO `AutomationProperties.GetAutomationId(...)` static getter (the WPF /
//     WinUI shape). MAUI's `Element.AutomationId` is a regular CLR property
//     on every Element, set via XAML attribute `AutomationId="MyButton"` or
//     code. The accessibility-oriented `SemanticProperties` static API
//     (Description / Hint) is for screen-reader text, not automation ids;
//     adopters who need explicit AutomationId on a non-Element instance fall
//     back to `Element.AutomationId` because that's where MAUI puts it.
//   * NO `LogicalTreeHelper`. MAUI doesn't expose a logical-tree helper public
//     surface; the `IVisualTreeElement.GetVisualChildren()` walk is the
//     canonical "everything in the tree" enumerator.
//   * Multi-window: `Application.Current.Windows` is `IReadOnlyList<Window>`
//     in MAUI 5+. The walker iterates every live Window.
//
// Match precedence (per Phase 4.1 spec, mirrors the other adapters):
//   1. `Element.AutomationId` — MAUI's canonical automation id
//   2. `Element.StyleId` — MAUI's "x:Name in code" hook (set by XAML compiler
//      from x:Name when no AutomationId is set)
//   3. INameScope.FindByName / Element.FindByName — XAML x:Name lookup
//
// Threading: caller (MauiUiAutomationAdapter) MUST dispatch to the UI thread
// via IDispatcher.Dispatch before invoking. MAUI's dependency-property reads
// and visual-tree walks are thread-affine.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Maui;
using Microsoft.Maui.Controls;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.Maui.Internal;

/// <summary>
/// Walks live MAUI <see cref="Window"/>s to find an <see cref="Element"/> by
/// name. Matches against <see cref="Element.AutomationId"/> first,
/// <see cref="Element.StyleId"/> second, falling back to
/// <see cref="NameScope.GetNameScope"/>'s
/// <c>FindByName</c> for x:Name lookup.
/// </summary>
internal static class VisualTreeFinder
{
    /// <summary>
    /// Locate the first <see cref="Element"/> whose
    /// <see cref="Element.AutomationId"/> equals <paramref name="name"/>, with
    /// fallbacks to <see cref="Element.StyleId"/> and INameScope lookup.
    /// Walks every live window of <paramref name="app"/>.
    /// </summary>
    /// <param name="app">The live MAUI application.</param>
    /// <param name="name">The element name to look up (case-sensitive, ordinal).</param>
    /// <param name="log">Optional logger; on a miss the candidate list is logged at <see cref="LogLevel.Information"/>.</param>
    /// <returns>The first matching element, or <see langword="null"/> when none was found.</returns>
    /// <remarks>
    /// Must be called on the MAUI UI thread - touches Element properties
    /// (BindableObject reads) and the visual tree, both of which are
    /// thread-affine.
    /// </remarks>
    public static Element? FindByName(Application app, string name, ILogger? log = null)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name must be non-empty.", nameof(name));

        var candidateNames = new List<string>();
        Element? match = null;

        foreach (var win in app.Windows)
        {
            // The Window itself is a NavigableElement (descendant of Element)
            // in MAUI - but adopters typically address controls inside the
            // Window's Page, not the Window itself. We still test the Window
            // for an explicit match (rare, but a Window can have an
            // AutomationId set in XAML).
            if (Matches(win, name))
            {
                match = win;
                break;
            }
            RecordCandidate(win, candidateNames);

            var page = win.Page;
            if (page is null) continue;

            // First check the Page itself.
            if (Matches(page, name))
            {
                match = page;
                break;
            }
            RecordCandidate(page, candidateNames);

            // Then walk the Page's visual subtree.
            match = WalkVisualTree(page, name, candidateNames);
            if (match is not null) break;

            // Last resort: INameScope lookup. Catches x:Name'd elements that
            // didn't show up in the visual walk because the platform handler
            // hadn't realised them yet (rare, but the XAML name table is
            // authoritative even before realisation).
            var named = page.FindByName<Element>(name);
            if (named is not null)
            {
                match = named;
                break;
            }
        }
        EmitLog(log, name, match, candidateNames);
        return match;
    }

    /// <summary>
    /// Phase 4.1: scope the lookup to a single <see cref="Window"/>'s subtree.
    /// Used by the multi-window adapter path so a control resolution targets
    /// only the requested window's visual tree.
    /// </summary>
    public static Element? FindByNameInWindow(Window win, string name, ILogger? log = null)
    {
        if (win is null) throw new ArgumentNullException(nameof(win));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name must be non-empty.", nameof(name));

        var candidateNames = new List<string>();
        Element? match = null;

        if (Matches(win, name))
        {
            match = win;
        }
        else
        {
            RecordCandidate(win, candidateNames);
            var page = win.Page;
            if (page is not null)
            {
                if (Matches(page, name))
                {
                    match = page;
                }
                else
                {
                    RecordCandidate(page, candidateNames);
                    match = WalkVisualTree(page, name, candidateNames);
                    if (match is null)
                    {
                        var named = page.FindByName<Element>(name);
                        if (named is not null) match = named;
                    }
                }
            }
        }
        EmitLog(log, name, match, candidateNames);
        return match;
    }

    private static void EmitLog(ILogger? log, string name, Element? match, List<string> candidateNames)
    {
        if (log is null) return;
        if (match is not null)
        {
            log.LogDebug(
                "VisualTreeFinder resolved '{Name}' to {ElementType} (AutomationId={AutomationId}, StyleId={StyleId}).",
                name,
                match.GetType().Name,
                string.IsNullOrEmpty(match.AutomationId) ? "(unset)" : match.AutomationId,
                string.IsNullOrEmpty(match.StyleId) ? "(unset)" : match.StyleId);
        }
        else
        {
            var preview = candidateNames.Count > 32
                ? string.Join(",", candidateNames.Take(32)) + $",...(+{candidateNames.Count - 32})"
                : string.Join(",", candidateNames);
            log.LogInformation(
                "VisualTreeFinder did NOT find '{Name}'. Candidates considered: [{Candidates}].",
                name,
                preview.Length == 0 ? "(none)" : preview);
        }
    }

    private static Element? WalkVisualTree(Element root, string name, List<string> candidates)
    {
        // Iterative DFS via Stack; recursion-free to avoid blowing the stack on
        // pathologically deep trees and to make cancellation/observability
        // easier in future phases. Same shape as WPF/Avalonia/WinUI adapters.
        var stack = new Stack<IVisualTreeElement>();
        if (root is IVisualTreeElement vte) stack.Push(vte);

        while (stack.Count > 0)
        {
            var node = stack.Pop();

            if (node is Element el && !ReferenceEquals(el, root))
            {
                if (Matches(el, name)) return el;
                RecordCandidate(el, candidates);
            }

            IReadOnlyList<IVisualTreeElement> children;
            try { children = node.GetVisualChildren(); }
            catch { continue; }

            // Stack reverses order; iterate backwards so we visit children
            // left-to-right per logical document order.
            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }

        return null;
    }

    private static bool Matches(Element el, string name)
    {
        if (!string.IsNullOrEmpty(el.AutomationId) &&
            string.Equals(el.AutomationId, name, StringComparison.Ordinal))
        {
            return true;
        }
        if (!string.IsNullOrEmpty(el.StyleId) &&
            string.Equals(el.StyleId, name, StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private static void RecordCandidate(Element el, List<string> sink)
    {
        // Build a compact "AutomationId|StyleId@TypeName" candidate token so a
        // failed resolution can show the LLM what names ARE in scope.
        var aid = string.IsNullOrEmpty(el.AutomationId) ? "?" : el.AutomationId;
        var sid = el.StyleId;
        var sidPart = string.IsNullOrEmpty(sid) ? string.Empty : $"|{sid}";
        sink.Add($"{aid}{sidPart}@{el.GetType().Name}");
    }
}
