// Marionette.NET — WinUI visual-tree resolver (Phase 3.2)
//
// Phase 3.2 helper: walks tracked WinUI Windows looking for a FrameworkElement
// matching the requested name. Mirrors WPF/Avalonia VisualTreeFinder but against
// WinUI 3 (Microsoft.UI.Xaml) types.
//
// API differences from WPF:
//   * VisualTreeHelper lives in `Microsoft.UI.Xaml.Media` (NOT
//     `System.Windows.Media`). Otherwise the API is identical: GetChild +
//     GetChildrenCount work the same way.
//   * AutomationProperties lives in `Microsoft.UI.Xaml.Automation`.
//   * No FrameworkElement.Name shadow-tree concept; every named XAML element
//     surfaces in the visual tree once it's been added to a Window's Content.
//   * No LogicalTreeHelper - WinUI doesn't expose a logical tree separate from
//     the visual tree. We do a single visual-tree walk.
//   * No `Application.Windows` - we use WindowTracker.Snapshot() instead.
//
// Match precedence (per Phase 3.2 spec, mirrors the other adapters):
//
//   1. AutomationProperties.AutomationId — semantic, AT-friendly
//   2. FrameworkElement.Name (i.e. x:Name) — fallback
//
// Threading: caller (WinUiAutomationAdapter) MUST dispatch to the UI thread
// via DispatcherQueue.TryEnqueue before invoking. WinUI's visual-tree and
// dependency-property reads are thread-affine.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.WinUI.Internal;

/// <summary>
/// Walks live WinUI <see cref="Window"/>s to find a <see cref="FrameworkElement"/>
/// by name. Matches against
/// <see cref="AutomationProperties.AutomationIdProperty"/> first, falling back
/// to <see cref="FrameworkElement.Name"/>.
/// </summary>
internal static class VisualTreeFinder
{
    /// <summary>
    /// Locate the first <see cref="FrameworkElement"/> whose
    /// <see cref="AutomationProperties.AutomationIdProperty"/> equals
    /// <paramref name="name"/>, or whose <see cref="FrameworkElement.Name"/>
    /// equals <paramref name="name"/> as a fallback. Walks every currently
    /// tracked window in <see cref="WindowTracker"/>.
    /// </summary>
    /// <param name="name">The element name to look up (case-sensitive, ordinal).</param>
    /// <param name="log">Optional logger; on a miss the candidate list is logged at <see cref="LogLevel.Information"/>.</param>
    /// <returns>The first matching element, or <see langword="null"/> when none was found.</returns>
    /// <remarks>
    /// Must be called on the WinUI UI thread - touches DependencyProperty
    /// values and the visual tree, both of which are thread-affine.
    /// </remarks>
    public static FrameworkElement? FindByName(string name, ILogger? log = null)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name must be non-empty.", nameof(name));

        var candidateNames = new List<string>();
        FrameworkElement? match = null;

        foreach (var win in WindowTracker.Snapshot())
        {
            // The Window itself is NOT a FrameworkElement in WinUI 3 (it's a
            // standalone class). The visual root is `win.Content`. If Content
            // is named, match it; otherwise walk its children.
            if (win.Content is FrameworkElement rootFe)
            {
                if (Matches(rootFe, name))
                {
                    match = rootFe;
                    break;
                }
                RecordCandidate(rootFe, candidateNames);
                match = WalkVisualTree(rootFe, name, candidateNames);
                if (match is not null) break;
            }
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

    private static FrameworkElement? WalkVisualTree(DependencyObject root, string name, List<string> candidates)
    {
        // Iterative DFS via Stack; recursion-free to avoid blowing the stack on
        // pathologically deep trees and to make cancellation/observability
        // easier in future phases. Same shape as WPF/Avalonia adapters.
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

    private static void RecordCandidate(FrameworkElement fe, List<string> sink)
    {
        // Build a compact "Name|AutomationId@TypeName" candidate token so a
        // failed resolution can show the LLM what names ARE in scope.
        var n = string.IsNullOrEmpty(fe.Name) ? "?" : fe.Name;
        var id = GetAutomationId(fe);
        var idPart = string.IsNullOrEmpty(id) ? string.Empty : $"|{id}";
        sink.Add($"{n}{idPart}@{fe.GetType().Name}");
    }

    /// <summary>
    /// Read the automation id off a WinUI element. Returns empty string when
    /// none is set (the static getter returns null in that case; we normalise
    /// for the caller's match-precedence simplicity).
    /// </summary>
    private static string GetAutomationId(DependencyObject fe)
    {
        return AutomationProperties.GetAutomationId(fe) ?? string.Empty;
    }
}
