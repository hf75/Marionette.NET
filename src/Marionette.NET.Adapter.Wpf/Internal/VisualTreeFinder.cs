// Marionette.NET — WPF visual-tree resolver
//
// Phase 1.3 helper: walks the open WPF Application's windows looking for a
// FrameworkElement matching the requested name. Used by
// `WpfUiAutomationAdapter.ResolveControlAsync` (triggerable resolution) and by
// `WpfUiAutomationAdapter.CaptureScreenshotAsync` (named-element screenshots).
//
// Match precedence (per Phase 1.3 contract):
//
//   1. AutomationProperties.AutomationId — semantic, AT-friendly, the value
//      adopters set when they want a stable name independent of x:Name.
//   2. FrameworkElement.Name (i.e. x:Name) — fallback when no automation id is
//      configured.
//
// Walk strategy: logical tree first (LogicalTreeHelper), visual tree second
// (VisualTreeHelper). The logical tree is more stable than the visual tree
// for named-element lookups — it doesn't include implementation-detail visuals
// (chrome, presenters, generated parts), so two adopters can name elements
// the same way regardless of styling. The visual tree is consulted only for
// elements that the logical tree cannot reach (e.g. items inside a custom
// templated control whose template doesn't surface them as logical children).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.Wpf.Internal;

/// <summary>
/// Walks open WPF windows to find a <see cref="FrameworkElement"/> by name.
/// Matches against <see cref="AutomationProperties.AutomationIdProperty"/>
/// first, falling back to <see cref="FrameworkElement.Name"/>.
/// </summary>
internal static class VisualTreeFinder
{
    /// <summary>
    /// Locate the first <see cref="FrameworkElement"/> whose
    /// <see cref="AutomationProperties.AutomationIdProperty"/> equals
    /// <paramref name="name"/>, or whose <see cref="FrameworkElement.Name"/>
    /// equals <paramref name="name"/> as a fallback. Walks every currently-open
    /// window in the supplied <see cref="Application"/>.
    /// </summary>
    /// <param name="app">The WPF application whose windows to walk.</param>
    /// <param name="name">The element name to look up (case-sensitive, ordinal).</param>
    /// <param name="log">Optional logger; when supplied, every candidate element
    /// considered by the walk is logged at <see cref="LogLevel.Trace"/>, and
    /// the resolution result (success or miss with candidate list) is logged at
    /// <see cref="LogLevel.Debug"/> / <see cref="LogLevel.Information"/>.</param>
    /// <returns>The first matching element, or <see langword="null"/> when none was found.</returns>
    /// <remarks>
    /// Must be called on the WPF UI thread — touches dependency-property
    /// values and visual trees, both of which are thread-affine. The
    /// <c>WpfUiAutomationAdapter</c> dispatches to the UI thread before
    /// invoking this method.
    /// </remarks>
    public static FrameworkElement? FindByName(Application app, string name, ILogger? log = null)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name must be non-empty.", nameof(name));

        var candidateNames = new List<string>();
        FrameworkElement? match = null;

        foreach (var win in EnumerateWindows(app))
        {
            // Cheap early exit: the window itself.
            if (Matches(win, name))
            {
                match = win;
                break;
            }
            RecordCandidate(win, candidateNames);

            // Logical-tree walk first — preferred for named-element lookups
            // because the logical tree omits styling-internal visuals.
            match = WalkLogicalTree(win, name, candidateNames);
            if (match is not null) break;

            // Visual-tree walk as fallback — picks up elements that live inside
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
                    AutomationProperties.GetAutomationId(match) is var id && string.IsNullOrEmpty(id) ? "(unset)" : id);
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

    private static IEnumerable<Window> EnumerateWindows(Application app)
    {
        // Application.Windows is a WindowCollection; copy to a typed array so
        // a window opening / closing during the walk doesn't trip us.
        var snapshot = new List<Window>(app.Windows.Count);
        foreach (Window? w in app.Windows)
        {
            if (w is not null) snapshot.Add(w);
        }
        return snapshot;
    }

    private static FrameworkElement? WalkLogicalTree(DependencyObject root, string name, List<string> candidates)
    {
        // Iterative DFS; recursion-free to avoid blowing the stack on
        // pathologically deep trees and to make cancellation/observability
        // easier in future phases.
        var stack = new Stack<DependencyObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();

            if (node is FrameworkElement fe)
            {
                if (Matches(fe, name)) return fe;
                RecordCandidate(fe, candidates);
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject dep) stack.Push(dep);
            }
        }

        return null;
    }

    private static FrameworkElement? WalkVisualTree(DependencyObject root, string name, List<string> candidates)
    {
        // Visual-tree walk requires that the element is rendered. Best-effort:
        // when the root isn't a Visual / Visual3D, VisualTreeHelper.GetChildrenCount
        // throws — so guard.
        if (root is not Visual && root is not System.Windows.Media.Media3D.Visual3D) return null;

        var stack = new Stack<DependencyObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();

            if (node is FrameworkElement fe)
            {
                if (Matches(fe, name)) return fe;
                RecordCandidate(fe, candidates);
            }

            int count;
            try { count = VisualTreeHelper.GetChildrenCount(node); }
            catch { continue; }
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is not null) stack.Push(child);
            }
        }

        return null;
    }

    private static bool Matches(FrameworkElement fe, string name)
    {
        var automationId = AutomationProperties.GetAutomationId(fe);
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

    private static void RecordCandidate(FrameworkElement fe, List<string> sink)
    {
        // Build a compact "Name|AutomationId@TypeName" candidate token so a
        // failed resolution can show the LLM what names ARE in scope.
        var n = string.IsNullOrEmpty(fe.Name) ? "?" : fe.Name;
        var id = AutomationProperties.GetAutomationId(fe);
        var idPart = string.IsNullOrEmpty(id) ? string.Empty : $"|{id}";
        sink.Add($"{n}{idPart}@{fe.GetType().Name}");
    }
}
