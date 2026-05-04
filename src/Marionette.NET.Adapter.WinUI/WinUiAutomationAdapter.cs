// Marionette.NET — WinUI UI automation adapter (Phase 3.2 production impl)
//
// Implements `IUiAutomationAdapter` against the WinUI 3 (Microsoft.UI.Xaml)
// Dispatcher / visual tree pipelines. Mirrors the WPF / Avalonia adapters one
// API at a time, with WinUI-specific quirks documented per method:
//
//   * DispatchAsync(Action / Func<T>, ct) -> DispatcherQueue.TryEnqueue.
//     WinUI's DispatcherQueue is the public threading primitive (replacing
//     WPF's Dispatcher); it lacks an awaitable Task surface, so we wrap each
//     enqueue in a TaskCompletionSource. CheckAccess analogue:
//     DispatcherQueue.HasThreadAccess.
//   * CaptureScreenshotAsync(target?, ct) -> RenderTargetBitmap.RenderAsync(element)
//     (async!). WinUI's RTB is async whereas WPF/Avalonia's is sync.
//     Result -> GetPixelsAsync() -> BGRA8 byte buffer -> BitmapEncoder PNG ->
//     Save to MemoryStream -> bytes.
//   * ResolveControlAsync(rootName, controlName, ct) -> walk every tracked
//     window via WindowTracker.Snapshot(), match AutomationProperties.AutomationId
//     first, FrameworkElement.Name fallback. Same shape as the other adapters,
//     just against WinUI types.
//   * SimulateInputAsync(...) -> WinUiInputSimulator (AutomationPeer first,
//     InputInjector fallback for non-button kinds and elevated scenarios).
//   * RaiseEventAsync(...) -> WinUiEventRaiser (CLR-event reflection; AOT
//     caveat documented).
//
// Stripping invariant: this file is in `Marionette.NET.Adapter.WinUI`, which is
// only ProjectReferenced when EnableMcpAutomation=true. Stripped Release
// builds never see this type.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

using Marionette.Adapter.WinUI.Internal;
using Marionette.Runtime.Adapters;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Marionette.Adapter.WinUI;

/// <summary>
/// WinUI 3 (Windows App SDK) implementation of <see cref="IUiAutomationAdapter"/>.
/// Marshals work onto the WinUI UI thread via
/// <see cref="Microsoft.UI.Dispatching.DispatcherQueue"/>, captures screenshots
/// via <see cref="RenderTargetBitmap"/> + <see cref="BitmapEncoder"/>, and
/// resolves named controls by walking tracked windows.
/// </summary>
/// <remarks>
/// Constructed and registered into the Marionette runtime's DI container by
/// <see cref="MarionetteWinUI.AttachTo(Application, Microsoft.UI.Dispatching.DispatcherQueue, IReadOnlyList{Marionette.Runtime.Manifest.RootDescriptor}, string[]?, ILoggerFactory?)"/> -
/// adopters do not new this up directly.
/// </remarks>
public sealed class WinUiAutomationAdapter : IUiAutomationAdapter
{
    private readonly Application _app;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private readonly ILogger<WinUiAutomationAdapter> _log;
    private readonly RootInstanceTracker _tracker;

    /// <summary>
    /// Construct a WinUI adapter bound to the supplied <see cref="Application"/>
    /// and <see cref="Microsoft.UI.Dispatching.DispatcherQueue"/>. The
    /// dispatcher queue MUST be the queue for the UI thread (typically
    /// retrieved from <see cref="Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread"/>
    /// on the UI thread, e.g. inside <see cref="Application.OnLaunched(LaunchActivatedEventArgs)"/>).
    /// </summary>
    public WinUiAutomationAdapter(
        Application app,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue,
        ILogger<WinUiAutomationAdapter> log,
        RootInstanceTracker? tracker = null)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _tracker = tracker ?? new RootInstanceTracker();
        _tracker.Changed += OnTrackerChanged;
    }

    /// <summary>Phase 3.3: expose the tracker to MarionetteWinUI.</summary>
    public RootInstanceTracker Tracker => _tracker;

    /// <inheritdoc />
    public Task DispatchAsync(Action action, CancellationToken ct)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        ct.ThrowIfCancellationRequested();

        if (_dispatcherQueue.HasThreadAccess)
        {
            // Already on the UI thread - run inline. Same short-circuit as
            // WPF/Avalonia adapters; saves one Dispatcher round-trip.
            try { action(); return Task.CompletedTask; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DispatchAsync(Action) inline call threw.");
                return Task.FromException(ex);
            }
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => tcs.TrySetCanceled(ct));

        var enqueued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (OperationCanceledException oce) when (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DispatchAsync(Action) dispatcher operation threw.");
                tcs.TrySetException(ex);
            }
            finally
            {
                registration.Dispose();
            }
        });
        if (!enqueued)
        {
            registration.Dispose();
            return Task.FromException(new InvalidOperationException(
                "DispatcherQueue.TryEnqueue failed - the queue may have been shut down."));
        }
        return tcs.Task;
    }

    /// <inheritdoc />
    public Task<T> DispatchAsync<T>(Func<T> func, CancellationToken ct)
    {
        if (func is null) throw new ArgumentNullException(nameof(func));
        ct.ThrowIfCancellationRequested();

        if (_dispatcherQueue.HasThreadAccess)
        {
            try { return Task.FromResult(func()); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DispatchAsync<{T}> inline call threw.", typeof(T).Name);
                return Task.FromException<T>(ex);
            }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => tcs.TrySetCanceled(ct));

        var enqueued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (OperationCanceledException oce) when (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DispatchAsync<{T}> dispatcher operation threw.", typeof(T).Name);
                tcs.TrySetException(ex);
            }
            finally
            {
                registration.Dispose();
            }
        });
        if (!enqueued)
        {
            registration.Dispose();
            return Task.FromException<T>(new InvalidOperationException(
                "DispatcherQueue.TryEnqueue failed - the queue may have been shut down."));
        }
        return tcs.Task;
    }

    /// <inheritdoc />
    public async Task<byte[]> CaptureScreenshotAsync(string? targetName, string? windowId, CancellationToken ct)
    {
        // Screenshot path is async-end-to-end on WinUI: RenderTargetBitmap.RenderAsync
        // is awaitable, GetPixelsAsync is awaitable, BitmapEncoder.CreateAsync
        // / FlushAsync are awaitable. We dispatch a Task-returning lambda to
        // the UI thread by composing DispatchAsync (Action) with a captured
        // local TaskCompletionSource: the Action body kicks off the async work
        // and resolves the TCS, the outer await observes the result on
        // whatever thread the continuation runs on.

        var screenshotTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await DispatchAsync(() =>
        {
            // Fire and forget the async work; complete the outer TCS when done.
            _ = RunCaptureAsync(targetName, windowId, screenshotTcs, ct);
        }, ct).ConfigureAwait(false);
        return await screenshotTcs.Task.ConfigureAwait(false);
    }

    private async Task RunCaptureAsync(
        string? targetName,
        string? windowId,
        TaskCompletionSource<byte[]> outer,
        CancellationToken ct)
    {
        try
        {
            var element = ResolveScreenshotTarget(targetName, windowId);
            if (element is null)
            {
                outer.TrySetException(new InvalidOperationException(
                    $"Could not resolve a screenshot target for '{targetName ?? "(main window)"}'."));
                return;
            }

            // WinUI RenderTargetBitmap: RenderAsync(element). Pixel buffer is
            // BGRA8 - 4 bytes per pixel. Width/height come back as PixelWidth /
            // PixelHeight after RenderAsync completes.
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(element).AsTask(ct).ConfigureAwait(true);
            var pixelW = rtb.PixelWidth;
            var pixelH = rtb.PixelHeight;
            if (pixelW <= 0 || pixelH <= 0)
            {
                outer.TrySetException(new InvalidOperationException(
                    $"Element '{targetName ?? "(main window)"}' has no visible size " +
                    $"(PixelWidth={pixelW}, PixelHeight={pixelH}). " +
                    "Wait until the window is laid out before capturing."));
                return;
            }
            var pixelBuffer = await rtb.GetPixelsAsync().AsTask(ct).ConfigureAwait(true);

            // Encode to PNG via BitmapEncoder. We need an IRandomAccessStream
            // for the encoder; InMemoryRandomAccessStream is the canonical
            // adapter-side primitive.
            using var ras = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras).AsTask(ct).ConfigureAwait(true);

            // SetPixelData expects a byte[] - we copy out of the IBuffer once.
            var bgraBytes = new byte[pixelBuffer.Length];
            using (var dr = Windows.Storage.Streams.DataReader.FromBuffer(pixelBuffer))
            {
                dr.ReadBytes(bgraBytes);
            }
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                width: (uint)pixelW,
                height: (uint)pixelH,
                dpiX: 96.0,
                dpiY: 96.0,
                pixels: bgraBytes);
            await encoder.FlushAsync().AsTask(ct).ConfigureAwait(true);

            // Pull the encoded PNG bytes out of the stream.
            var streamLength = ras.Size;
            var pngBytes = new byte[streamLength];
            ras.Seek(0);
            using (var dr = new Windows.Storage.Streams.DataReader(ras.GetInputStreamAt(0)))
            {
                await dr.LoadAsync((uint)streamLength).AsTask(ct).ConfigureAwait(true);
                dr.ReadBytes(pngBytes);
            }

            _log.LogDebug(
                "CaptureScreenshotAsync produced {Bytes} bytes for {Target} (size {Width}x{Height}px).",
                pngBytes.Length, targetName ?? "(main window)", pixelW, pixelH);

            outer.TrySetResult(pngBytes);
        }
        catch (OperationCanceledException oce) when (ct.IsCancellationRequested)
        {
            outer.TrySetCanceled(oce.CancellationToken);
        }
        catch (Exception ex)
        {
            outer.TrySetException(ex);
        }
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
            // Phase 3.3: when windowId resolves to a tracked Window-typed
            // instance, scope the visual-tree walk to it. Otherwise walk
            // every tracked window like Phase 3.2.
            var scoped = ResolveScopedWindow(rootName, windowId);
            if (scoped is not null)
            {
                return VisualTreeFinder.FindByNameInWindow(scoped, controlName, _log);
            }
            return VisualTreeFinder.FindByName(controlName, _log);
        }, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Phase 3.2 caveat: WinUI's <c>InputInjector.TryCreate()</c> may return
    /// <see langword="null"/> when the process isn't elevated and the app
    /// manifest doesn't declare the <c>inputInjectionBrokered</c> capability.
    /// In that case the simulator falls through to the <c>ButtonAutomationPeer.Invoke</c>
    /// path for click variants - which works unpackaged + unelevated. Other
    /// kinds (key_press, key_down, key_up, mouse_move) require the injector
    /// and surface <see langword="false"/> with a logged limitation. Adopters
    /// who need keyboard automation in unelevated WinUI scenarios can either
    /// (a) use <c>simulate_input(kind:"type_text")</c> on a target TextBox
    /// (which sets <c>TextBox.Text</c> directly) or (b) decorate the
    /// underlying handler with <c>[McpCallable]</c> and invoke via
    /// invoke_method.
    /// </remarks>
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
            var scoped = ResolveScopedWindow(rootName, windowId);
            var fe = scoped is not null
                ? VisualTreeFinder.FindByNameInWindow(scoped, controlName, _log)
                : VisualTreeFinder.FindByName(controlName, _log);
            if (fe is null)
            {
                _log.LogInformation("simulate_input: could not resolve '{Control}' (windowId={WindowId}).",
                    controlName, windowId ?? "(default)");
                return false;
            }
            _log.LogDebug("simulate_input '{Kind}' on {Type} (Name={Name}, windowId={WindowId}).",
                kind, fe.GetType().Name, fe.Name, windowId ?? "(default)");
            return WinUiInputSimulator.Simulate(fe, kind, args, _log);
        }, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// AOT note: <see cref="WinUiEventRaiser.Raise"/> walks the control's type
    /// chain via reflection looking for the compiler-emitted backing field of
    /// the named CLR event. Trimming MAY remove the backing field; framework
    /// controls keep theirs rooted via XAML usage. Phase 4.2: the interface
    /// method is marked
    /// <see cref="System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"/>;
    /// the suppression below acknowledges the warning here at the
    /// implementation site rather than re-propagating it.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(
        "WinUiAutomationAdapter.RaiseEventAsync reflects on the compiler-emitted backing delegate " +
        "field of CLR events on the control's type chain. Trimming may strip these fields for " +
        "custom controls. Use simulate_input or [McpCallable] for AOT.")]
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
            var scoped = ResolveScopedWindow(rootName, windowId);
            var fe = scoped is not null
                ? VisualTreeFinder.FindByNameInWindow(scoped, controlName, _log)
                : VisualTreeFinder.FindByName(controlName, _log);
            if (fe is null)
            {
                _log.LogInformation("raise_event: could not resolve '{Control}' (windowId={WindowId}).",
                    controlName, windowId ?? "(default)");
                return false;
            }
            _log.LogDebug("raise_event '{Event}' on {Type} (Name={Name}, windowId={WindowId}).",
                eventName, fe.GetType().Name, fe.Name, windowId ?? "(default)");
            return WinUiEventRaiser.Raise(fe, eventName, args, _log);
        }, ct);
    }

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
    /// Resolve a windowId hint to a tracked WinUI Window, when the tracker
    /// holds a Window-typed instance for that root. Returns null otherwise
    /// (the caller falls back to the legacy WindowTracker.Snapshot walk).
    /// </summary>
    private Window? ResolveScopedWindow(string? rootName, string? windowId)
    {
        if (string.IsNullOrEmpty(rootName) || string.IsNullOrEmpty(windowId)) return null;
        return _tracker.GetInstance(rootName!, windowId) as Window;
    }

    // -------------------------------------------------------------------------
    // Screenshot internals (UI-thread-only)
    // -------------------------------------------------------------------------

    private UIElement? ResolveScreenshotTarget(string? targetName, string? windowId)
    {
        if (string.IsNullOrEmpty(targetName))
        {
            // Phase 3.3: if windowId resolves to a tracked Window, capture its Content.
            if (!string.IsNullOrEmpty(windowId))
            {
                foreach (var (_, id, instance) in _tracker.SnapshotAll())
                {
                    if (string.Equals(id, windowId, StringComparison.Ordinal) && instance is Window scoped)
                    {
                        if (scoped.Content is UIElement scopedContent) return scopedContent;
                        throw new InvalidOperationException(
                            $"Window for windowId='{windowId}' has no Content; cannot capture a screenshot.");
                    }
                }
            }
            var win = WindowTracker.FirstReadyWindow();
            if (win is null)
            {
                throw new InvalidOperationException(
                    "No window is currently tracked; cannot capture a screenshot. " +
                    "Open a window via App.OnLaunched and ensure MarionetteWinUI.AttachTo " +
                    "was called with the live window's DispatcherQueue.");
            }
            if (win.Content is UIElement uie) return uie;
            throw new InvalidOperationException("Tracked window has no Content; cannot capture a screenshot.");
        }

        FrameworkElement? fe;
        if (!string.IsNullOrEmpty(windowId))
        {
            Window? scoped = null;
            foreach (var (_, id, instance) in _tracker.SnapshotAll())
            {
                if (string.Equals(id, windowId, StringComparison.Ordinal) && instance is Window w) { scoped = w; break; }
            }
            fe = scoped is null
                ? VisualTreeFinder.FindByName(targetName!, _log)
                : VisualTreeFinder.FindByNameInWindow(scoped, targetName!, _log);
        }
        else
        {
            fe = VisualTreeFinder.FindByName(targetName!, _log);
        }
        if (fe is null)
        {
            throw new InvalidOperationException(
                $"No FrameworkElement named '{targetName}' was found in any tracked window. " +
                "Pass an element with x:Name or AutomationProperties.AutomationId set.");
        }
        return fe;
    }
}
