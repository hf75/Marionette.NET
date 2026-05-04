// Marionette.NET — WPF UI automation adapter (Phase 1.3 production impl)
//
// Implements `IUiAutomationAdapter` against the WPF Dispatcher / visual tree
// pipelines. Phase 1.2 introduced the contract; Phase 1.3 fills it in:
//
//   * DispatchAsync(Action / Func<T>, ct) → Application.Current.Dispatcher.InvokeAsync(...)
//     (asynchronous; we never call the synchronous Invoke, which would
//     deadlock if the caller is already on the UI thread).
//   * CaptureScreenshotAsync(target?, ct) → RenderTargetBitmap + PngBitmapEncoder.
//     Target null → MainWindow (or first IsActive Window, or Windows[0]).
//     Target named → resolve via VisualTreeFinder, capture just that element.
//   * ResolveControlAsync(rootName, controlName, ct) → walk all open Windows
//     looking for a FrameworkElement whose AutomationProperties.AutomationId
//     equals controlName, falling back to FrameworkElement.Name.
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

    /// <summary>
    /// Construct a WPF adapter bound to the supplied <see cref="Application"/>.
    /// </summary>
    /// <param name="app">The live WPF <see cref="Application"/>. Must not be null.</param>
    /// <param name="log">Logger (use <see cref="NullLogger{T}.Instance"/> when not wiring DI).</param>
    public WpfUiAutomationAdapter(Application app, ILogger<WpfUiAutomationAdapter> log)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

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
    public Task<byte[]> CaptureScreenshotAsync(string? targetName, CancellationToken ct)
    {
        // Wrap the actual capture in DispatchAsync<T> so we always run on the
        // UI thread (RenderTargetBitmap, AutomationProperties, and the visual
        // tree are all thread-affine).
        return DispatchAsync(() => CaptureScreenshotOnUiThread(targetName), ct);
    }

    /// <inheritdoc />
    public Task<object?> ResolveControlAsync(string rootName, string controlName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(controlName))
            throw new ArgumentException("controlName must be non-empty.", nameof(controlName));

        // rootName is intentionally unused in Phase 1.3 — the adapter walks
        // every open Window and matches by name. Phase 2 may use rootName as a
        // disambiguation hint for multi-root scenarios; today we want
        // single-name lookup behaviour to stay consistent across windows.
        _ = rootName;

        return DispatchAsync<object?>(() =>
        {
            _log.LogDebug("ResolveControlAsync requested '{Control}' (rootHint='{Root}').", controlName, rootName);
            var fe = VisualTreeFinder.FindByName(_app, controlName, _log);
            return fe;
        }, ct);
    }

    /// <inheritdoc />
    public Task<bool> SimulateInputAsync(
        string rootName,
        string controlName,
        string kind,
        IReadOnlyDictionary<string, object?>? args,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(controlName))
            throw new ArgumentException("controlName must be non-empty.", nameof(controlName));
        if (string.IsNullOrEmpty(kind))
            throw new ArgumentException("kind must be non-empty.", nameof(kind));
        _ = rootName;

        return DispatchAsync(() =>
        {
            var fe = VisualTreeFinder.FindByName(_app, controlName, _log);
            if (fe is null)
            {
                _log.LogInformation("simulate_input: could not resolve '{Control}'.", controlName);
                return false;
            }
            _log.LogDebug("simulate_input '{Kind}' on {Type} (Name={Name}).", kind, fe.GetType().Name, fe.Name);
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
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(controlName))
            throw new ArgumentException("controlName must be non-empty.", nameof(controlName));
        if (string.IsNullOrEmpty(eventName))
            throw new ArgumentException("eventName must be non-empty.", nameof(eventName));
        _ = rootName;

        return DispatchAsync(() =>
        {
            var fe = VisualTreeFinder.FindByName(_app, controlName, _log);
            if (fe is null)
            {
                _log.LogInformation("raise_event: could not resolve '{Control}'.", controlName);
                return false;
            }
            _log.LogDebug("raise_event '{Event}' on {Type} (Name={Name}).", eventName, fe.GetType().Name, fe.Name);
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

    // -------------------------------------------------------------------------
    // Screenshot internals (UI-thread-only)
    // -------------------------------------------------------------------------

    private byte[] CaptureScreenshotOnUiThread(string? targetName)
    {
        var element = ResolveScreenshotTarget(targetName);
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

    private FrameworkElement? ResolveScreenshotTarget(string? targetName)
    {
        if (string.IsNullOrEmpty(targetName))
        {
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

        var fe = VisualTreeFinder.FindByName(_app, targetName!, _log);
        if (fe is null)
        {
            throw new InvalidOperationException(
                $"No FrameworkElement named '{targetName}' was found in any open window. " +
                "Pass an element with x:Name or AutomationProperties.AutomationId set.");
        }
        return fe;
    }
}
