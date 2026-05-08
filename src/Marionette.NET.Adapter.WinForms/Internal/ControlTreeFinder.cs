// Marionette.NET — Windows Forms control-tree resolver (Phase 15)
//
// WinForms equivalent of WPF VisualTreeFinder. Walks Application.OpenForms
// (or a single named Form) looking for a Control whose Name matches the
// requested string. WinForms has no AutomationProperties.AutomationId
// equivalent at the framework level — Control.Name is the canonical handle
// and is what Form.Controls.Find(name, recursive: true) uses internally.
//
// We re-implement the walk explicitly (rather than relying on Find) so we can:
//   1. Capture candidate names for diagnostic logging on a miss.
//   2. Check Control.AccessibleName as a secondary match (stable across
//      designer regeneration; some adopters use it for AT compatibility).
//   3. Walk into MdiChildren and ToolStrip items, which Form.Controls.Find
//      doesn't recurse into.
//
// Threading: the framework requires UI-thread access for any property read
// on a Control whose handle has been created. The adapter dispatches before
// invoking us. Internal class, never exposed.

using System;
using System.Collections.Generic;
using System.Windows.Forms;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.WinForms.Internal;

/// <summary>
/// Walks open WinForms forms to find a <see cref="Control"/> by
/// <see cref="Control.Name"/> (with <see cref="Control.AccessibleName"/>
/// as a fallback). Match is ordinal, case-sensitive — same convention as
/// WPF's <c>VisualTreeFinder</c>.
/// </summary>
internal static class ControlTreeFinder
{
    /// <summary>
    /// Locate the first <see cref="Control"/> whose <see cref="Control.Name"/>
    /// or <see cref="Control.AccessibleName"/> equals <paramref name="name"/>.
    /// Walks every form in <see cref="Application.OpenForms"/>.
    /// </summary>
    public static Control? FindByName(string name, ILogger? log = null)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name must be non-empty.", nameof(name));

        var candidates = new List<string>();
        Control? match = null;

        foreach (var form in EnumerateOpenForms())
        {
            match = WalkControl(form, name, candidates);
            if (match is not null) break;
        }

        EmitLog(log, name, match, candidates);
        return match;
    }

    /// <summary>
    /// Phase 3.3-style scoped lookup: walk only the supplied form's tree.
    /// Used by the multi-window routing path so a control resolution targets
    /// only the requested form's controls (avoiding cross-form name
    /// collisions when two forms of the same class are open).
    /// </summary>
    public static Control? FindByNameInForm(Form form, string name, ILogger? log = null)
    {
        if (form is null) throw new ArgumentNullException(nameof(form));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name must be non-empty.", nameof(name));

        var candidates = new List<string>();
        var match = WalkControl(form, name, candidates);
        EmitLog(log, name, match, candidates);
        return match;
    }

    private static IEnumerable<Form> EnumerateOpenForms()
    {
        // Snapshot Application.OpenForms — opening / closing during the walk
        // would otherwise mutate the FormCollection underneath us.
        var snap = new List<Form>(Application.OpenForms.Count);
        foreach (Form f in Application.OpenForms)
        {
            if (f is not null) snap.Add(f);
        }
        return snap;
    }

    private static Control? WalkControl(Control root, string name, List<string> candidates)
    {
        // Iterative DFS over Controls.Controls. Recursion-free both for stack
        // safety on huge UIs and to make future cancellation easy.
        var stack = new Stack<Control>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();

            if (Matches(node, name)) return node;
            RecordCandidate(node, candidates);

            // Standard child controls
            foreach (Control? child in node.Controls)
            {
                if (child is not null) stack.Push(child);
            }

            // Form-specific descendants the regular Controls collection
            // doesn't expose:
            if (node is Form form)
            {
                foreach (Form? child in form.MdiChildren)
                {
                    if (child is not null) stack.Push(child);
                }
                if (form.MainMenuStrip is { } menu)
                {
                    RecordToolStripCandidates(menu.Items, candidates);
                }
            }

            // ToolStrip / MenuStrip / ContextMenuStrip items aren't Controls —
            // they're ToolStripItems and can't be returned through this API.
            // We RECORD them as candidates for diagnostic purposes (so a
            // failed lookup can hint that there IS a menu item with that
            // name), but adopters who want to invoke menu actions should
            // expose them as [McpCallable] methods on their ViewModel —
            // that's the documented Marionette pattern and the only
            // AOT-clean path.
            if (node is ToolStrip strip)
            {
                RecordToolStripCandidates(strip.Items, candidates);
            }
        }

        return null;
    }


    private static void RecordToolStripCandidates(ToolStripItemCollection items, List<string> candidates)
    {
        foreach (ToolStripItem? item in items)
        {
            if (item is null) continue;
            var n = string.IsNullOrEmpty(item.Name) ? "?" : item.Name;
            candidates.Add($"{n}@{item.GetType().Name}(menu)");
            if (item is ToolStripDropDownItem dd && dd.HasDropDownItems)
            {
                RecordToolStripCandidates(dd.DropDownItems, candidates);
            }
        }
    }

    private static bool Matches(Control c, string name)
    {
        if (!string.IsNullOrEmpty(c.Name) &&
            string.Equals(c.Name, name, StringComparison.Ordinal))
        {
            return true;
        }
        if (!string.IsNullOrEmpty(c.AccessibleName) &&
            string.Equals(c.AccessibleName, name, StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private static void RecordCandidate(Control c, List<string> sink)
    {
        var n = string.IsNullOrEmpty(c.Name) ? "?" : c.Name;
        var an = string.IsNullOrEmpty(c.AccessibleName) ? string.Empty : $"|{c.AccessibleName}";
        sink.Add($"{n}{an}@{c.GetType().Name}");
    }

    private static void EmitLog(ILogger? log, string name, Control? match, List<string> candidates)
    {
        if (log is null) return;
        if (match is not null)
        {
            log.LogDebug(
                "ControlTreeFinder resolved '{Name}' to {Type} (Name={ActualName}, Accessible={Accessible}).",
                name,
                match.GetType().Name,
                string.IsNullOrEmpty(match.Name) ? "(unset)" : match.Name,
                string.IsNullOrEmpty(match.AccessibleName) ? "(unset)" : match.AccessibleName);
        }
        else
        {
            var preview = candidates.Count > 32
                ? string.Join(",", candidates.GetRange(0, 32)) + $",...(+{candidates.Count - 32})"
                : string.Join(",", candidates);
            log.LogInformation(
                "ControlTreeFinder did NOT find '{Name}'. Candidates: [{Candidates}].",
                name,
                preview.Length == 0 ? "(none)" : preview);
        }
    }
}
