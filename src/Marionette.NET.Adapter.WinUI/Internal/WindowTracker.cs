// Marionette.NET — WinUI WindowTracker
//
// WinUI 3 does NOT expose Application.Windows the way WPF and Avalonia
// classic-desktop lifetimes do. Adopters create Microsoft.UI.Xaml.Window
// instances explicitly in `App.OnLaunched` (and elsewhere) and they are NOT
// auto-tracked anywhere. To enumerate live windows for visual-tree walking
// and screenshot fallbacks, we maintain our own thread-safe registry.
//
// `MarionetteWinUI.AttachTo` calls `WindowTracker.Track` for each window the
// adopter knows about (typically the live MainWindow). Adopters with multiple
// windows can call `Track` again for each.
//
// Threading: the tracker itself uses a lock + a snapshot pattern; reads return
// a ToArray() snapshot so the caller can walk it without holding the lock. All
// AvaloniaProperty / WPF-style "must run on UI thread" reads are the caller's
// responsibility — the visual-tree walker dispatches to the UI thread before
// touching any Window.

using System;
using System.Collections.Generic;

using Microsoft.UI.Xaml;

namespace Marionette.Adapter.WinUI.Internal;

/// <summary>
/// Tracks live <see cref="Window"/> instances created by the adopter.
/// WinUI 3 does not expose <c>Application.Windows</c>; the adapter relies on
/// adopters calling <see cref="MarionetteWinUI.AttachTo"/> with the live
/// MainWindow plus optional secondary windows.
/// </summary>
internal static class WindowTracker
{
    // List + lock keeps the API minimal and the contention bounded. Windows are
    // typically <10 per app; List<>.Contains is fine.
    private static readonly object s_lock = new();
    private static readonly List<WeakReference<Window>> s_windows = new();

    /// <summary>
    /// Register a window for enumeration. Repeated calls with the same window
    /// are no-ops (de-duplicated by reference). Auto-unregisters on
    /// <see cref="Window.Closed"/>.
    /// </summary>
    public static void Track(Window window)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));
        lock (s_lock)
        {
            // Compact dead refs while we hold the lock - cheap and bounded.
            for (var i = s_windows.Count - 1; i >= 0; i--)
            {
                if (!s_windows[i].TryGetTarget(out var existing))
                {
                    s_windows.RemoveAt(i);
                    continue;
                }
                if (ReferenceEquals(existing, window)) return; // de-dup
            }
            s_windows.Add(new WeakReference<Window>(window));
        }
        // Hook close to remove. WinUI's Window.Closed fires once before the
        // native window is destroyed; safe place to drop the registration.
        window.Closed += OnWindowClosed;
    }

    private static void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is not Window window) return;
        lock (s_lock)
        {
            for (var i = s_windows.Count - 1; i >= 0; i--)
            {
                if (!s_windows[i].TryGetTarget(out var existing) || ReferenceEquals(existing, window))
                {
                    s_windows.RemoveAt(i);
                }
            }
        }
        try { window.Closed -= OnWindowClosed!; } catch { /* ignore */ }
    }

    /// <summary>
    /// Snapshot the tracked windows. Returned array does not change after the
    /// caller receives it.
    /// </summary>
    public static Window[] Snapshot()
    {
        lock (s_lock)
        {
            var result = new List<Window>(s_windows.Count);
            for (var i = s_windows.Count - 1; i >= 0; i--)
            {
                if (s_windows[i].TryGetTarget(out var w))
                {
                    result.Add(w);
                }
                else
                {
                    s_windows.RemoveAt(i);
                }
            }
            return result.ToArray();
        }
    }

    /// <summary>
    /// Resolve the canonical "first window" for the application. Order:
    /// the first tracked window with a non-null Content (i.e. ready to render).
    /// Returns null when no tracked windows exist or none have rendered yet.
    /// </summary>
    public static Window? FirstReadyWindow()
    {
        var snap = Snapshot();
        foreach (var w in snap)
        {
            if (w.Content is not null) return w;
        }
        return snap.Length > 0 ? snap[0] : null;
    }
}
