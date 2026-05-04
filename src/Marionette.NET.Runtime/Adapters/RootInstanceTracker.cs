// Marionette.NET — Phase 3.3 multi-window root-instance tracker
//
// Each framework-specific adapter (Wpf / Avalonia / WinUI) maintains a list
// of live <c>[McpRoot]</c> instances per root name. A `RootInstanceTracker`
// is the shared state machine used by all three adapters:
//
//   * Stable monotonic windowId allocation: `w1`, `w2`, `w3`, ... in
//     per-process order. Counter never resets; closing a window does NOT
//     free the ID for reuse. An identical reopen gets a fresh ID.
//   * Per-root list of (windowId, instance) entries with reference equality
//     dedup so a given instance only registers once.
//   * Auto-removal API for window-close hooks.
//   * `Changed` event the adapter forwards to its `WindowsChanged`
//     IUiAutomationAdapter event so the runtime can refresh tool naming.
//
// Threading: all mutations + reads are guarded by a single lock. Snapshots
// are copy-on-read so callers can iterate without holding the lock. The
// adapters that consume this tracker dispatch every UI-thread interaction
// through their framework's Dispatcher / DispatcherQueue separately.
//
// AOT-clean: no reflection, no MakeGeneric*. Generic delegate field for
// the optional change-notification raiser.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Marionette.Runtime.Adapters;

/// <summary>
/// Per-root list of live <c>[McpRoot]</c> instances, each tagged with a
/// stable per-process windowId. Adapter-private state, but lives in Runtime
/// so all three framework adapters share a single implementation.
/// </summary>
/// <remarks>
/// Window IDs are short, stable strings like <c>w1</c>, <c>w2</c>, <c>w3</c>
/// — derived from a per-process monotonically-increasing counter. The
/// counter is shared across all roots so a fresh window-open of any root
/// type allocates the next free ID.
/// </remarks>
public sealed class RootInstanceTracker
{
    private static int s_nextCounter; // process-global monotonic counter

    private readonly object _lock = new();

    /// <summary>Per-root-name list of entries, oldest-first.</summary>
    private readonly Dictionary<string, List<Entry>> _byRoot = new(StringComparer.Ordinal);

    /// <summary>
    /// Raised after every <see cref="Track(string, object)"/> /
    /// <see cref="Untrack(object)"/> mutation. The adapter forwards this to
    /// its public <c>WindowsChanged</c> event so the runtime can refresh
    /// tool naming / emit <c>tools/list_changed</c>.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Register an instance for <paramref name="rootName"/> and allocate a
    /// fresh windowId for it. Repeated calls with the same instance are
    /// no-ops (de-duplicated by reference equality). Returns the existing
    /// or newly-allocated windowId.
    /// </summary>
    public string Track(string rootName, object instance)
    {
        if (string.IsNullOrEmpty(rootName)) throw new ArgumentException("rootName must be non-empty.", nameof(rootName));
        if (instance is null) throw new ArgumentNullException(nameof(instance));

        bool changed = false;
        string id;
        lock (_lock)
        {
            if (!_byRoot.TryGetValue(rootName, out var list))
            {
                list = new List<Entry>(2);
                _byRoot[rootName] = list;
            }
            // De-dup by reference. If already tracked, return the existing ID.
            foreach (var e in list)
            {
                if (ReferenceEquals(e.Instance, instance)) return e.WindowId;
            }
            var n = Interlocked.Increment(ref s_nextCounter);
            id = "w" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            list.Add(new Entry(id, instance));
            changed = true;
        }
        if (changed) RaiseChanged();
        return id;
    }

    /// <summary>
    /// Remove a tracked instance. No-op if the instance was never tracked or
    /// is in a different root.
    /// </summary>
    public void Untrack(object instance)
    {
        if (instance is null) return;
        bool changed = false;
        lock (_lock)
        {
            foreach (var kvp in _byRoot)
            {
                var list = kvp.Value;
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(list[i].Instance, instance))
                    {
                        list.RemoveAt(i);
                        changed = true;
                    }
                }
            }
        }
        if (changed) RaiseChanged();
    }

    /// <summary>
    /// Return the live windowIds for <paramref name="rootName"/>, oldest
    /// first. Empty when no instances are tracked for this root.
    /// </summary>
    public IReadOnlyList<string> GetWindowIds(string rootName)
    {
        if (string.IsNullOrEmpty(rootName)) return Array.Empty<string>();
        lock (_lock)
        {
            if (!_byRoot.TryGetValue(rootName, out var list) || list.Count == 0)
                return Array.Empty<string>();
            var snapshot = new string[list.Count];
            for (var i = 0; i < list.Count; i++) snapshot[i] = list[i].WindowId;
            return snapshot;
        }
    }

    /// <summary>
    /// Look up the instance bound to a specific windowId; pass
    /// <see langword="null"/> for "first / oldest" (default-window
    /// semantics).
    /// </summary>
    public object? GetInstance(string rootName, string? windowId)
    {
        if (string.IsNullOrEmpty(rootName)) return null;
        lock (_lock)
        {
            if (!_byRoot.TryGetValue(rootName, out var list) || list.Count == 0)
                return null;
            if (string.IsNullOrEmpty(windowId))
            {
                // First / default window - oldest tracked entry.
                return list[0].Instance;
            }
            foreach (var e in list)
            {
                if (string.Equals(e.WindowId, windowId, StringComparison.Ordinal))
                    return e.Instance;
            }
            return null;
        }
    }

    /// <summary>
    /// Take a snapshot of every instance for a root; oldest-first. Each
    /// element is (windowId, instance). Returned array does not change after
    /// the caller receives it.
    /// </summary>
    public IReadOnlyList<(string WindowId, object Instance)> Snapshot(string rootName)
    {
        if (string.IsNullOrEmpty(rootName)) return Array.Empty<(string, object)>();
        lock (_lock)
        {
            if (!_byRoot.TryGetValue(rootName, out var list) || list.Count == 0)
                return Array.Empty<(string, object)>();
            var snap = new (string, object)[list.Count];
            for (var i = 0; i < list.Count; i++)
                snap[i] = (list[i].WindowId, list[i].Instance);
            return snap;
        }
    }

    /// <summary>
    /// Return every (rootName, windowId, instance) tuple known to the
    /// tracker. Useful for diagnostics and for iterating "all live windows
    /// regardless of root".
    /// </summary>
    public IReadOnlyList<(string RootName, string WindowId, object Instance)> SnapshotAll()
    {
        lock (_lock)
        {
            var total = 0;
            foreach (var kvp in _byRoot) total += kvp.Value.Count;
            var snap = new (string, string, object)[total];
            var idx = 0;
            foreach (var kvp in _byRoot)
            {
                foreach (var e in kvp.Value)
                {
                    snap[idx++] = (kvp.Key, e.WindowId, e.Instance);
                }
            }
            return snap;
        }
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null) return;
        try { handler(this, EventArgs.Empty); }
        catch { /* never let a subscriber failure escape into the adapter */ }
    }

    private readonly record struct Entry(string WindowId, object Instance);
}
