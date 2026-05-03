// Marionette.NET - Avalonia visual-tree resolver
//
// Phase 2.1 helper: walks an Avalonia application's open Windows looking for a
// Control matching the requested name. Mirrors the WPF VisualTreeFinder but
// against Avalonia 11.x types:
//
//   * Avalonia.Controls.Window         instead of System.Windows.Window
//   * Avalonia.Controls.Control        instead of System.Windows.FrameworkElement
//   * AvaloniaObject + LogicalChildren instead of LogicalTreeHelper.GetChildren
//   * IClassicDesktopStyleApplicationLifetime.Windows instead of Application.Windows
//
// Match precedence (per Phase 2.1 spec):
//
//   1. AutomationProperties.AutomationId - semantic, AT-friendly, the value
//      adopters set when they want a stable name independent of x:Name.
//   2. Control.Name (i.e. x:Name)        - fallback when no automation id is
//      configured.
//
// Walk strategy: logical tree first (LogicalChildren), visual tree second
// (VisualChildren). The logical tree is more stable than the visual tree for
// named-element lookups - it doesn't include implementation visuals (chrome,
// presenters, generated parts), so two adopters can name elements the same way
// regardless of styling. The visual tree is consulted only for elements that
// the logical tree cannot reach (e.g. items inside a custom-templated control
// whose template doesn't surface them as logical children).

using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.Avalonia.Internal;

/// <summary>
/// Walks open Avalonia <see cref="Window"/>s to find a <see cref="Control"/>
/// by name. Matches against <see cref="AutomationProperties.AutomationIdProperty"/>
/// first, falling back to <see cref="StyledElement.Name"/>.
/// </summary>
internal static class VisualTreeFinder
{
    /// <summary>
    /// Locate the first <see cref="Control"/> whose
    /// <see cref="AutomationProperties.AutomationIdProperty"/> equals
    /// <paramref name="name"/>, or whose <see cref="StyledElement.Name"/>
    /// equals <paramref name="name"/> as a fallback. Walks every currently-open
    /// window in the supplied <see cref="global::Avalonia.Application"/>.
    /// </summary>
    /// <param name="app">The Avalonia application whose windows to walk.</param>
    /// <param name="name">The element name to look up (case-sensitive, ordinal).</param>
    /// <param name="log">Optional logger; on a miss the candidate list is logged at <see cref="LogLevel.Information"/>.</param>
    /// <returns>The first matching element, or <see langword="null"/> when none was found.</returns>
    /// <remarks>
    /// Must be called on the Avalonia UI thread - touches AvaloniaProperty
    /// values and visual trees, both of which are thread-affine. The
    /// <c>AvaloniaUiAutomationAdapter</c> dispatches to the UI thread before
    /// invoking this method.
    /// </remarks>
    public static Control? FindByName(global::Avalonia.Application app, string name, ILogger? log = null)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name must be non-empty.", nameof(name));

        var candidateNames = new List<string>();
        Control? match = null;

        foreach (var win in EnumerateWindows(app))
        {
            // Cheap early exit: the window itself.
            if (Matches(win, name))
            {
                match = win;
                break;
            }
            RecordCandidate(win, candidateNames);

            // Logical-tree walk first - preferred for named-element lookups
            // because the logical tree omits styling-internal visuals.
            match = WalkLogicalTree(win, name, candidateNames);
            if (match is not null) break;

            // Visual-tree walk as fallback - picks up elements that live inside
            // ItemsControl templates / ContentPresenters that the logical tree
            // doesn't expose to user code.
            match = WalkVisualTree(win, name, candidateNames);
            if (match is not null) break;
        }

        if (log is not null)
        {
            if (match is not null)
            {
                log.LogDebug(
                    "VisualTreeFinder resolved '{Name}' to {ElementType} (Name={ActualName}, AutomationId={AutomationId}).",
                    name,
                    match.GetType().Name,
                    string.IsNullOrEmpty(match.Name) ? "(unset)" : match.Name,
                    GetAutomationId(match) is var id && string.IsNullOrEmpty(id) ? "(unset)" : id);
            }
            else
            {
                // Cap candidate list in the log to avoid unbounded spam on
                // huge UIs; the first 32 are usually enough to debug a miss.
                var preview = candidateNames.Count > 32
                    ? string.Join(",", candidateNames.Take(32)) + $",...(+{candidateNames.Count - 32})"
                    : string.Join(",", candidateNames);
                log.LogInformation(
                    "VisualTreeFinder did NOT find '{Name}'. Candidates considered: [{Candidates}].",
                    name,
                    preview.Length == 0 ? "(none)" : preview);
            }
        }

        return match;
    }

    /// <summary>
    /// Resolve the canonical "first window" for the application - used by
    /// <c>CaptureScreenshotAsync(null)</c>. Order of preference:
    /// <list type="bullet">
    ///   <item><description><see cref="IClassicDesktopStyleApplicationLifetime.MainWindow"/> when set,</description></item>
    ///   <item><description>the first active window, otherwise</description></item>
    ///   <item><description>the first window in the lifetime's collection.</description></item>
    /// </list>
    /// Returns <see langword="null"/> on non-classic-desktop lifetimes (e.g.
    /// <see cref="ISingleViewApplicationLifetime"/> on mobile) or when no window
    /// is open.
    /// </summary>
    public static Window? FirstWindow(global::Avalonia.Application app)
    {
        if (app.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }
        if (desktop.MainWindow is { } mw) return mw;
        // Walk the collection looking for an active window first.
        foreach (var w in desktop.Windows)
        {
            if (w is { IsActive: true }) return w;
        }
        return desktop.Windows.Count > 0 ? desktop.Windows[0] : null;
    }

    private static IEnumerable<Window> EnumerateWindows(global::Avalonia.Application app)
    {
        // Avalonia exposes the open-window collection through its desktop
        // lifetime. Mobile / single-view lifetimes won't have one - we
        // gracefully return an empty enumeration so the rest of the resolver
        // doesn't crash. Phase 3+ may extend this for non-window roots
        // (TopLevel.GetTopLevels).
        if (app.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            yield break;
        }

        // Snapshot the collection so a window opening / closing during the walk
        // doesn't trip us.
        var snapshot = new List<Window>(desktop.Windows.Count);
        foreach (var w in desktop.Windows)
        {
            if (w is not null) snapshot.Add(w);
        }
        foreach (var w in snapshot) yield return w;
    }

    private static Control? WalkLogicalTree(Control root, string name, List<string> candidates)
    {
        // Iterative DFS; recursion-free to avoid blowing the stack on
        // pathologically deep trees and to make cancellation/observability
        // easier in future phases.
        var stack = new Stack<ILogical>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();

            if (node is Control fe)
            {
                if (Matches(fe, name)) return fe;
                RecordCandidate(fe, candidates);
            }

            // ILogical.LogicalChildren is an IAvaloniaReadOnlyList<ILogical>;
            // every entry is itself ILogical so we can keep walking.
            foreach (var child in node.LogicalChildren)
            {
                if (child is not null) stack.Push(child);
            }
        }

        return null;
    }

    private static Control? WalkVisualTree(Control root, string name, List<string> candidates)
    {
        // Visual-tree walk requires that the element is rendered. Avalonia
        // exposes the visual tree via the IVisual interface (or, since 11.x,
        // the Visual base class).
        var stack = new Stack<Visual>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();

            if (node is Control fe)
            {
                if (Matches(fe, name)) return fe;
                RecordCandidate(fe, candidates);
            }

            // VisualChildren is an IReadOnlyList<Visual>; iterate in declared
            // order (DFS variant, slightly off-by-one vs strict DFS but
            // produces the same set).
            foreach (var child in node.GetVisualChildren())
            {
                if (child is not null) stack.Push(child);
            }
        }

        return null;
    }

    private static bool Matches(Control fe, string name)
    {
        var automationId = GetAutomationId(fe);
        if (!string.IsNullOrEmpty(automationId) &&
            string.Equals(automationId, name, StringComparison.Ordinal))
        {
            return true;
        }
        if (!string.IsNullOrEmpty(fe.Name) &&
            string.Equals(fe.Name, name, StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private static void RecordCandidate(Control fe, List<string> sink)
    {
        // Build a compact "Name|AutomationId@TypeName" candidate token so a
        // failed resolution can show the LLM what names ARE in scope.
        var n = string.IsNullOrEmpty(fe.Name) ? "?" : fe.Name;
        var id = GetAutomationId(fe);
        var idPart = string.IsNullOrEmpty(id) ? string.Empty : $"|{id}";
        sink.Add($"{n}{idPart}@{fe.GetType().Name}");
    }

    /// <summary>
    /// Read the automation id off an Avalonia control. Avalonia stores this
    /// under <see cref="AutomationProperties.AutomationIdProperty"/> the same
    /// way WPF does, but the static getter lives on a different namespace
    /// (<c>Avalonia.Automation.AutomationProperties</c>).
    /// </summary>
    private static string GetAutomationId(StyledElement fe)
    {
        // GetAutomationId returns a string? - the Avalonia 11.x signature
        // matches WPF's. Treat null/empty as "unset" so the caller can keep
        // its match precedence simple.
        return AutomationProperties.GetAutomationId(fe) ?? string.Empty;
    }
}
