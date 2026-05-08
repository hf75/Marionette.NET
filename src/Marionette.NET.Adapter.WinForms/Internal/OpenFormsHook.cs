// Marionette.NET — Windows Forms multi-window tracker hook (Phase 15)
//
// Phase 3.3 multi-window routing requires that the adapter notice when
// secondary forms of a known [McpRoot] type are opened or closed and
// register / unregister them in the shared RootInstanceTracker.
//
// WinForms doesn't have an "application-wide form opened" event the way
// WPF has via Application.Activated. The closest framework hook is
// Application.Idle (fires when the message pump goes idle); we reconcile
// Application.OpenForms there.
//
// Form.HandleCreated fires when a form is shown for the first time; we
// hook into Shown for our reconciliation since not every Form materialises
// its handle before becoming visible (some adopters chain visibility through
// MdiParent.MdiChildren).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

using Marionette.Runtime.Adapters;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.WinForms.Internal;

/// <summary>
/// Watches <see cref="Application.OpenForms"/> and registers any matching
/// form whose CLR type is a known <c>[McpRoot]</c> with the supplied
/// <see cref="RootInstanceTracker"/>. Idempotent: repeated reconciliations
/// of the same form are no-ops.
/// </summary>
internal sealed class OpenFormsHook
{
    private readonly Dictionary<string, string> _typeToRoot;
    private readonly RootInstanceTracker _tracker;
    private readonly ILogger _log;
    private readonly HashSet<Form> _hooked = new();

    private OpenFormsHook(
        Dictionary<string, string> typeToRoot,
        RootInstanceTracker tracker,
        ILogger log)
    {
        _typeToRoot = typeToRoot;
        _tracker = tracker;
        _log = log;
    }

    /// <summary>
    /// Install a tracker hook against the supplied roots.  Returns <see langword="null"/>
    /// when no <c>Form</c>-typed roots are present (no work to do).
    /// </summary>
    public static OpenFormsHook? Install(
        IReadOnlyList<Marionette.Runtime.Manifest.RootDescriptor> roots,
        RootInstanceTracker tracker,
        ILogger log)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in roots)
        {
            if (!string.IsNullOrEmpty(r.TypeName))
            {
                map[r.TypeName] = r.Name;
            }
        }
        if (map.Count == 0) return null;

        var hook = new OpenFormsHook(map, tracker, log);

        // Initial reconcile picks up any Form already open at AttachTo time
        // (typical: the bootstrap was called from the main Form's Shown
        // handler). Subsequent reconciles run on Application.Idle — cheap
        // because the OpenForms count is small for typical adopters.
        hook.Reconcile();
        Application.Idle += (_, _) => hook.Reconcile();

        return hook;
    }

    /// <summary>
    /// Walk <see cref="Application.OpenForms"/>, register any matching form
    /// not yet tracked, and ensure each tracked form's <see cref="Form.FormClosed"/>
    /// is hooked so the tracker can untrack on close.
    /// </summary>
    public void Reconcile()
    {
        // Snapshot first — modifying the FormCollection during iteration
        // (e.g. a Form Closing in response to our hookup) corrupts the
        // enumeration.
        Form[] snap;
        try
        {
            snap = Application.OpenForms.Cast<Form>().ToArray();
        }
        catch
        {
            return; // Application is shutting down.
        }

        foreach (var form in snap)
        {
            if (form is null) continue;
            var typeName = form.GetType().FullName;
            if (typeName is null) continue;
            if (!_typeToRoot.TryGetValue(typeName, out var rootName)) continue;

            // Track is reference-equality idempotent.
            _tracker.Track(rootName, form);

            // Hook FormClosed once per form.
            if (_hooked.Add(form))
            {
                FormClosedEventHandler? handler = null;
                handler = (_, _) =>
                {
                    try { _tracker.Untrack(form); } catch { /* ignore */ }
                    try { _hooked.Remove(form); } catch { /* ignore */ }
                    try { if (handler is not null) form.FormClosed -= handler; } catch { /* ignore */ }
                };
                form.FormClosed += handler;
            }
        }
    }
}
