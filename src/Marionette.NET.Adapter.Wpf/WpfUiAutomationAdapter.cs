// Marionette.NET — WPF UI automation adapter (Phase 1.3 production impl)
//
// Implements `IUiAutomationAdapter` against the WPF Dispatcher / visual tree
// pipelines. Phase 1.2 introduced the contract; Phase 1.3 fills it in:
//
//   * DispatchAsync(Action / Func<T>, ct) → Application.Current.Dispatcher.InvokeAsync(...)
//     (asynchronous; we never call the synchronous Invoke, which would
//     deadlock if the caller is already on the UI thread).
//   * CaptureScreenshotAsync(target?, windowId?, ct) → RenderTargetBitmap + PngBitmapEncoder.
//     Target null → default-window (lowest windowId), or Application.MainWindow fallback.
//     Target named → resolve via VisualTreeFinder, capture just that element.
//   * ResolveControlAsync(rootName, controlName, windowId?, ct) → walk windows
//     looking for a FrameworkElement whose AutomationProperties.AutomationId
//     equals controlName, falling back to FrameworkElement.Name. Phase 3.3:
//     when windowId is non-null, scope the walk to the matching tracked
//     window's visual tree.
//
// Phase 3.3 multi-window routing additions:
//   * RootInstanceTracker holds the live list of root instances tagged with
//     stable windowIds (`w1`, `w2`, ...). MarionetteWpf.AttachTo populates it
//     from the bridged factories AND from Application.Windows changes.
//   * GetWindowIds(rootName) / GetRootInstance(rootName, windowId) expose
//     the tracker to the runtime.
//   * WindowsChanged forwards the tracker's Changed event so the
//     DynamicToolRegistry can refresh tool naming and emit
//     notifications/tools/list_changed.
//
// Logging: every dispatch / resolve / capture is logged on the supplied
// ILogger<WpfUiAutomationAdapter>. Resolution failures include the searched
// name plus the candidate names found — actionable for the LLM and for
// adopters debugging triggerable misses.
//
// Stripping invariant: this file is in `Marionette.NET.Adapter.Wpf`, which is
// only ProjectReferenced when EnableMcpAutomation=true (see
// build/Marionette.NET.targets and the user csproj's conditional reference
// pattern). Stripped Release builds never see this type.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Marionette.Adapter.Wpf.Internal;
using Marionette.Runtime.Adapters;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marionette.Adapter.Wpf;

/// <summary>
/// WPF implementation of <see cref="IUiAutomationAdapter"/>. Marshals work
/// onto the WPF UI thread via <see cref="Application.Dispatcher"/>, captures
/// screenshots via <see cref="RenderTargetBitmap"/> + <see cref="PngBitmapEncoder"/>,
/// and resolves named controls by walking <see cref="Application.Windows"/>.
/// </summary>
/// <remarks>
/// Constructed and registered into the Marionette runtime's DI container by
/// <see cref="MarionetteWpf.AttachTo"/> — adopters do not new this up directly.
/// </remarks>
public sealed class WpfUiAutomationAdapter : IUiAutomationAdapter
{
    private readonly Application _app;
    private readonly ILogger<WpfUiAutomationAdapter> _log;
    private readonly RootInstanceTracker _tracker;

    /// <summary>
    /// Construct a WPF adapter bound to the supplied <see cref="Application"/>.
    /// </summary>
    /// <param name="app">The live WPF <see cref="Application"/>. Must not be null.</param>
    /// <param name="log">Logger (use <see cref="NullLogger{T}.Instance"/> when not wiring DI).</param>
    /// <param name="tracker">
    /// Phase 3.3: shared per-process root-instance tracker. The adapter forwards
    /// its <see cref="RootInstanceTracker.Changed"/> event onto
    /// <see cref="WindowsChanged"/> so the runtime's DynamicToolRegistry can
    /// react. Pass <see langword="null"/> to construct a fresh tracker (the
    /// MarionetteWpf bootstrap typically supplies one for the lifetime of the
    /// application).
    /// </param>
    public WpfUiAutomationAdapter(
        Application app,
        ILogger<WpfUiAutomationAdapter> log,
        RootInstanceTracker? tracker = null)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _tracker = tracker ?? new RootInstanceTracker();
        _tracker.Changed += OnTrackerChanged;
    }

    /// <summary>
    /// Phase 3.3: expose the tracker to <see cref="MarionetteWpf"/> so
    /// it can register live root instances + hook
    /// <see cref="Application.Windows"/> changes.
    /// </summary>
    public RootInstanceTracker Tracker => _tracker;

    /// <inheritdoc />
    public async Task DispatchAsync(Action action, CancellationToken ct)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        ct.ThrowIfCancellationRequested();

        var disp = _app.Dispatcher;
        if (disp.CheckAccess())
        {
            // Already on the UI thread — running inline avoids one Dispatcher
            // round-trip and matches how Avalonia / WinUI adapters will behave.
            // Subscriptions to watchable resources may end up here on first
            // read; redundant InvokeAsync would just queue and finish later.
            try { action(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DispatchAsync(Action) inline call threw.");
                throw;
            }
            return;
        }

        // InvokeAsync is the right primitive: never blocks the calling thread,
        // never deadlocks if the dispatcher is currently pumping. We honour
        // cancellation by passing the token to the underlying DispatcherOperation.
        var op = disp.InvokeAsync(action, DispatcherPriority.Normal, ct);
        try
        {
            await op.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DispatchAsync(Action) dispatcher operation threw.");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<T> DispatchAsync<T>(Func<T> func, CancellationToken ct)
    {
        if (func is null) throw new ArgumentNullException(nameof(func));
        ct.ThrowIfCancellationRequested();

        var disp = _app.Dispatcher;
        if (disp.CheckAccess())
        {
            try { return func(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DispatchAsync<{T}> inline call threw.", typeof(T).Name);
                throw;
            }
        }

        var op = disp.InvokeAsync(func, DispatcherPriority.Normal, ct);
        try
        {
            return await op.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DispatchAsync<{T}> dispatcher operation threw.", typeof(T).Name);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<byte[]> CaptureScreenshotAsync(string? targetName, string? windowId, CancellationToken ct)
    {
        // Wrap the actual capture in DispatchAsync<T> so we always run on the
        // UI thread (RenderTargetBitmap, AutomationProperties, and the visual
        // tree are all thread-affine).
        return DispatchAsync(() => CaptureScreenshotOnUiThread(targetName, windowId), ct);
    }

    /// <inheritdoc />
    public Task<object?> ResolveControlAsync(string rootName, string controlName, string? windowId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(controlName))
            throw new ArgumentException("controlName must be non-empty.", nameof(controlName));

        return DispatchAsync<object?>(() =>
        {
            _log.LogDebug("ResolveControlAsync requested '{Control}' (rootHint='{Root}', windowId='{WindowId}').",
                controlName, rootName, windowId ?? "(default)");
            // Phase 3.3: when windowId is supplied AND it maps to a tracked
            // Window-typed instance, scope the search to that single window.
            // Otherwise (legacy / null path) walk every open window like
            // Phase 1.3.
            var scopedWindow = ResolveScopedWindow(rootName, windowId);
            if (scopedWindow is not null)
            {
                return VisualTreeFinder.FindByNameInWindow(scopedWindow, controlName, _log);
            }
            return VisualTreeFinder.FindByName(_app, controlName, _log);
        }, ct);
    }

    /// <inheritdoc />
    public Task<bool> SimulateInputAsync(
        string rootName,
        string controlName,
        string kind,
        IReadOnlyDictionary<string, object?>? args,
        string? windowId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(controlName))
            throw new ArgumentException("controlName must be non-empty.", nameof(controlName));
        if (string.IsNullOrEmpty(kind))
            throw new ArgumentException("kind must be non-empty.", nameof(kind));

        return DispatchAsync(() =>
        {
            var scopedWindow = ResolveScopedWindow(rootName, windowId);
            var fe = scopedWindow is not null
                ? VisualTreeFinder.FindByNameInWindow(scopedWindow, controlName, _log)
                : VisualTreeFinder.FindByName(_app, controlName, _log);
            if (fe is null)
            {
                _log.LogInformation("simulate_input: could not resolve '{Control}' (windowId={WindowId}).",
                    controlName, windowId ?? "(default)");
                return false;
            }
            _log.LogDebug("simulate_input '{Kind}' on {Type} (Name={Name}, windowId={WindowId}).",
                kind, fe.GetType().Name, fe.Name, windowId ?? "(default)");
            return WpfInputSimulator.Simulate(fe, kind, args, _log);
        }, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// AOT note: <see cref="WpfEventRaiser.Raise"/> walks the control's type
    /// chain via reflection looking for static <c>&lt;EventName&gt;Event</c>
    /// fields (the WPF idiom). Trimming MAY remove unreferenced fields and
    /// break the lookup; Phase 5's AOT-hardening pass may surface a
    /// source-gen-emitted alternative that doesn't reflect at runtime. For
    /// now WPF's own framework controls (Button, TextBox, …) keep their
    /// RoutedEvent fields rooted because XAML / templating references them.
    /// Custom controls that strip should fall back to <c>simulate_input</c>.
    /// </remarks>
    public Task<bool> RaiseEventAsync(
        string rootName,
        string controlName,
        string eventName,
        IReadOnlyDictionary<string, object?>? args,
        string? windowId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(controlName))
            throw new ArgumentException("controlName must be non-empty.", nameof(controlName));
        if (string.IsNullOrEmpty(eventName))
            throw new ArgumentException("eventName must be non-empty.", nameof(eventName));

        return DispatchAsync(() =>
        {
            var scopedWindow = ResolveScopedWindow(rootName, windowId);
            var fe = scopedWindow is not null
                ? VisualTreeFinder.FindByNameInWindow(scopedWindow, controlName, _log)
                : VisualTreeFinder.FindByName(_app, controlName, _log);
            if (fe is null)
            {
                _log.LogInformation("raise_event: could not resolve '{Control}' (windowId={WindowId}).",
                    controlName, windowId ?? "(default)");
                return false;
            }
            _log.LogDebug("raise_event '{Event}' on {Type} (Name={Name}, windowId={WindowId}).",
                eventName, fe.GetType().Name, fe.Name, windowId ?? "(default)");
            return RaiseEventReflectively(fe, eventName, args);
        }, ct);
    }

    // Wrapping the call lets us mark the helper with the trim attribute and
    // keep the public RaiseEventAsync clean; the caller chain doesn't need
    // its own attribute because the IUiAutomationAdapter contract doesn't
    // promise AOT cleanliness for this method (Phase 5 follow-up).
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 3.1: WPF event lookup uses reflection; trimming caveat documented in adapter XML doc. Phase 5 AOT-hardening pass to produce a source-gen alternative.")]
    private bool RaiseEventReflectively(UIElement fe, string eventName, IReadOnlyDictionary<string, object?>? args)
        => WpfEventRaiser.Raise(fe, eventName, args, _log);

    // ---------------------------------------------------------------------
    // Phase 3.3 — multi-window routing
    // ---------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyList<string> GetWindowIds(string rootName) => _tracker.GetWindowIds(rootName);

    /// <inheritdoc />
    public object? GetRootInstance(string rootName, string? windowId) => _tracker.GetInstance(rootName, windowId);

    /// <inheritdoc />
    public event EventHandler? WindowsChanged;

    private void OnTrackerChanged(object? sender, EventArgs e) => WindowsChanged?.Invoke(this, e);

    /// <summary>
    /// When a windowId is supplied AND it maps to a tracked Window-typed
    /// root instance, return that Window so callers can scope their walk to
    /// it. Otherwise return null (caller falls back to walking every
    /// <see cref="Application.Windows"/>).
    /// </summary>
    /// <remarks>
    /// rootName is required: a windowId is meaningless without it (the same
    /// monotonic counter spans every root). When the tracker has no matching
    /// entry — or the entry isn't itself a <see cref="Window"/> — we return
    /// null and the legacy whole-app walk runs. This preserves Phase 1.3
    /// behaviour for adopters whose roots are non-Window ViewModels.
    /// </remarks>
    private Window? ResolveScopedWindow(string? rootName, string? windowId)
    {
        if (string.IsNullOrEmpty(rootName) || string.IsNullOrEmpty(windowId)) return null;
        var inst = _tracker.GetInstance(rootName!, windowId);
        // Phase 3.3 trapdoor: TodoListViewModel and similar non-Window roots
        // are tracked but aren't visual roots themselves. We can't scope a
        // visual-tree walk to a ViewModel — fall through to whole-app walk.
        return inst as Window;
    }

    // -------------------------------------------------------------------------
    // Screenshot internals (UI-thread-only)
    // -------------------------------------------------------------------------

    private byte[] CaptureScreenshotOnUiThread(string? targetName, string? windowId)
    {
        var element = ResolveScreenshotTarget(targetName, windowId);
        if (element is null)
        {
            // Never reached for the targetName==null path (we throw inside
            // ResolveScreenshotTarget then). Defensive — keeps the compiler
            // happy and surfaces a useful error if a future change makes this
            // path reachable.
            throw new InvalidOperationException(
                $"Could not resolve a screenshot target for '{targetName ?? "(main window)"}'.");
        }

        // DPI math: RenderTargetBitmap takes pixels for width/height plus the
        // logical DPI (96-relative). We size in pixels so the encoded PNG
        // matches the on-screen pixel count, and we set DPI so consumers can
        // resize correctly.
        var dpi = VisualTreeHelper.GetDpi(element);
        var pxW = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX);
        var pxH = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
        if (pxW <= 0 || pxH <= 0)
        {
            // Common with elements that haven't been laid out yet (zero-size
            // panels, headless windows, controls inside a hidden window).
            throw new InvalidOperationException(
                $"Element '{targetName ?? "(main window)"}' has no visible size " +
                $"(ActualWidth={element.ActualWidth}, ActualHeight={element.ActualHeight}). " +
                "Wait until the window is laid out (e.g. after Loaded) before capturing.");
        }

        var bmp = new RenderTargetBitmap(
            pixelWidth: pxW,
            pixelHeight: pxH,
            dpiX: dpi.PixelsPerInchX,
            dpiY: dpi.PixelsPerInchY,
            pixelFormat: PixelFormats.Pbgra32);

        bmp.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        var bytes = ms.ToArray();

        _log.LogDebug(
            "CaptureScreenshotAsync produced {Bytes} bytes for {Target} (size {Width}x{Height}px @ {DpiX}x{DpiY} DPI).",
            bytes.Length,
            targetName ?? "(main window)",
            pxW,
            pxH,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY);
        return bytes;
    }

    private FrameworkElement? ResolveScreenshotTarget(string? targetName, string? windowId)
    {
        if (string.IsNullOrEmpty(targetName))
        {
            // Phase 3.3: if the runtime supplied a windowId AND the tracker
            // has a matching Window entry, prefer that window. Otherwise fall
            // back to the original Application.MainWindow / first-IsActive
            // logic. The runtime always knows which root it's targeting; we
            // walk every tracked root to find one whose tracker entry maps
            // to the supplied windowId.
            if (!string.IsNullOrEmpty(windowId))
            {
                var byId = LookupWindowById(windowId!);
                if (byId is not null) return byId;
            }
            var win = _app.MainWindow
                ?? _app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                ?? _app.Windows.OfType<Window>().FirstOrDefault();
            if (win is null)
            {
                throw new InvalidOperationException(
                    "No window is currently open; cannot capture a screenshot. " +
                    "Open a window or specify an element name via the target argument.");
            }
            return win;
        }

        // Named target: scope to specific window when windowId supplied.
        FrameworkElement? fe;
        if (!string.IsNullOrEmpty(windowId))
        {
            var scoped = LookupWindowById(windowId!);
            fe = scoped is null
                ? VisualTreeFinder.FindByName(_app, targetName!, _log)
                : VisualTreeFinder.FindByNameInWindow(scoped, targetName!, _log);
        }
        else
        {
            fe = VisualTreeFinder.FindByName(_app, targetName!, _log);
        }
        if (fe is null)
        {
            throw new InvalidOperationException(
                $"No FrameworkElement named '{targetName}' was found in any open window. " +
                "Pass an element with x:Name or AutomationProperties.AutomationId set.");
        }
        return fe;
    }

    private Window? LookupWindowById(string windowId)
    {
        foreach (var (_, id, instance) in _tracker.SnapshotAll())
        {
            if (string.Equals(id, windowId, StringComparison.Ordinal) && instance is Window w)
                return w;
        }
        return null;
    }
}
