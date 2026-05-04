// Marionette.NET - Avalonia UI automation adapter (Phase 2.1 production impl)
//
// Implements `IUiAutomationAdapter` against the Avalonia 11.x Dispatcher /
// visual tree pipelines. Mirrors the WPF adapter shape:
//
//   * DispatchAsync(Action / Func<T>, ct) -> Dispatcher.UIThread.InvokeAsync(...)
//     (asynchronous; we never call the synchronous Invoke which can deadlock).
//     Avalonia's DispatcherOperation lacks a public Task in 11.x so we use the
//     GetTask() extension; on the Func<T> overload we await GetTask() via
//     a JIT-resolved overload (see helper below).
//   * CaptureScreenshotAsync(target?, ct) -> RenderTargetBitmap.Render(visual)
//     and Save(stream) - Avalonia's RenderTargetBitmap.Save defaults to PNG.
//     Target null -> first window via VisualTreeFinder.FirstWindow.
//     Target named -> resolve via VisualTreeFinder, capture just that element.
//   * ResolveControlAsync(rootName, controlName, ct) -> walk every open Window
//     looking for a Control whose AutomationProperties.AutomationId equals
//     controlName, falling back to Control.Name.
//
// Logging: every dispatch / resolve / capture is logged on the supplied
// ILogger<AvaloniaUiAutomationAdapter>. Resolution failures include the
// searched name plus the candidate names found - actionable for the LLM and
// for adopters debugging triggerable misses.
//
// Stripping invariant: this file is in `Marionette.NET.Adapter.Avalonia`,
// which is only ProjectReferenced when EnableMcpAutomation=true (see
// build/Marionette.NET.targets and the user csproj's conditional reference
// pattern). Stripped Release builds never see this type.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Marionette.Adapter.Avalonia.Internal;
using Marionette.Runtime.Adapters;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marionette.Adapter.Avalonia;

/// <summary>
/// Avalonia 11.x implementation of <see cref="IUiAutomationAdapter"/>. Marshals
/// work onto the Avalonia UI thread via <see cref="Dispatcher.UIThread"/>,
/// captures screenshots via <see cref="RenderTargetBitmap"/> (Avalonia's
/// PNG-default Save), and resolves named controls by walking the application's
/// open Windows.
/// </summary>
/// <remarks>
/// Constructed and registered into the Marionette runtime's DI container by
/// <see cref="MarionetteAvalonia.AttachTo"/> - adopters do not new this up
/// directly.
/// </remarks>
public sealed class AvaloniaUiAutomationAdapter : IUiAutomationAdapter
{
    private readonly global::Avalonia.Application _app;
    private readonly ILogger<AvaloniaUiAutomationAdapter> _log;

    /// <summary>
    /// Construct an Avalonia adapter bound to the supplied
    /// <see cref="global::Avalonia.Application"/>.
    /// </summary>
    /// <param name="app">The live Avalonia <see cref="global::Avalonia.Application"/>. Must not be null.</param>
    /// <param name="log">Logger (use <see cref="NullLogger{T}.Instance"/> when not wiring DI).</param>
    public AvaloniaUiAutomationAdapter(
        global::Avalonia.Application app,
        ILogger<AvaloniaUiAutomationAdapter> log)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public async Task DispatchAsync(Action action, CancellationToken ct)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        ct.ThrowIfCancellationRequested();

        var disp = Dispatcher.UIThread;
        if (disp.CheckAccess())
        {
            // Already on the UI thread - running inline avoids one Dispatcher
            // round-trip and matches how the WPF adapter behaves. Subscriptions
            // to watchable resources may end up here on first read; redundant
            // InvokeAsync would just queue and finish later.
            try { action(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DispatchAsync(Action) inline call threw.");
                throw;
            }
            return;
        }

        // Avalonia 11.x: Dispatcher.InvokeAsync returns a DispatcherOperation
        // whose underlying Task is exposed via GetTask(). Unlike WPF, the
        // 11.x Avalonia API does not accept a CancellationToken on the
        // operation itself - we honour cancellation before scheduling and
        // surface it on the returned Task by using WaitAsync.
        var op = disp.InvokeAsync(action, DispatcherPriority.Normal);
        try
        {
            await op.GetTask().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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

        var disp = Dispatcher.UIThread;
        if (disp.CheckAccess())
        {
            try { return func(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DispatchAsync<{T}> inline call threw.", typeof(T).Name);
                throw;
            }
        }

        // The Func<T> overload of InvokeAsync returns a typed
        // DispatcherOperation<T>; GetTask() returns Task<T>.
        var op = disp.InvokeAsync(func, DispatcherPriority.Normal);
        try
        {
            return await op.GetTask().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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

        // rootName is intentionally unused in Phase 2.1 - the adapter walks
        // every open Window and matches by name. Phase 3 may use rootName as a
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
    /// <remarks>
    /// Phase 3.1 caveat: Avalonia 11.x makes the EventArgs ctors for
    /// <c>PointerPressedEventArgs</c> / <c>KeyEventArgs</c> /
    /// <c>TextInputEventArgs</c> internal, so the "test-automation-grade"
    /// raw-input-pipeline path is not directly available. The Phase 3.1
    /// Avalonia adapter handles the click variants by raising
    /// <c>Button.ClickEvent</c> through the public RoutedEvent dispatcher
    /// (which DOES drive the routed-event handler chain — bubbling /
    /// tunneling included). Other kinds (key_press, type_text, mouse_move)
    /// return <see langword="false"/> with a logged limitation. See
    /// <c>AvaloniaInputSimulator</c> for the full rationale.
    /// </remarks>
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
            return AvaloniaInputSimulator.Simulate(fe, kind, args, _log);
        }, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// AOT note: <see cref="AvaloniaEventRaiser.Raise"/> walks the control's
    /// type chain via reflection looking for static
    /// <c>&lt;EventName&gt;Event</c> fields (the Avalonia idiom). Trimming
    /// MAY remove unreferenced fields; framework controls keep theirs
    /// rooted via XAML. Phase 5's AOT-hardening pass may surface a
    /// source-gen alternative.
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
            return AvaloniaEventRaiser.Raise(fe, eventName, args, _log);
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Screenshot internals (UI-thread-only)
    // -------------------------------------------------------------------------

    private byte[] CaptureScreenshotOnUiThread(string? targetName)
    {
        var element = ResolveScreenshotTarget(targetName);
        if (element is null)
        {
            // Never reached for the targetName==null path (we throw inside
            // ResolveScreenshotTarget then). Defensive - keeps the compiler
            // happy and surfaces a useful error if a future change makes this
            // path reachable.
            throw new InvalidOperationException(
                $"Could not resolve a screenshot target for '{targetName ?? "(main window)"}'.");
        }

        // Avalonia uses RenderScaling on TopLevel for HiDPI. Default is 1.0;
        // multiply pixel dimensions by RenderScaling so the encoded PNG matches
        // the on-screen pixel count, and convert RenderScaling -> DPI (96 base).
        var topLevel = TopLevel.GetTopLevel(element);
        var scaling = topLevel?.RenderScaling ?? 1.0;
        var bounds = element.Bounds;
        var pxW = (int)Math.Ceiling(bounds.Width * scaling);
        var pxH = (int)Math.Ceiling(bounds.Height * scaling);
        if (pxW <= 0 || pxH <= 0)
        {
            // Common with elements that haven't been laid out yet (zero-size
            // panels, headless windows, controls inside a hidden window).
            throw new InvalidOperationException(
                $"Element '{targetName ?? "(main window)"}' has no visible size " +
                $"(Width={bounds.Width}, Height={bounds.Height}). " +
                "Wait until the window is laid out (e.g. after Opened) before capturing.");
        }

        // Avalonia.Media.Imaging.RenderTargetBitmap takes a PixelSize and an
        // optional Vector(dpiX, dpiY). DPI is 96 * scaling on each axis.
        var dpiVal = 96.0 * scaling;
        var bmp = new RenderTargetBitmap(
            new PixelSize(pxW, pxH),
            new Vector(dpiVal, dpiVal));

        bmp.Render(element);

        using var ms = new MemoryStream();
        // Avalonia's Save writes PNG by default - no explicit encoder needed.
        bmp.Save(ms);
        var bytes = ms.ToArray();

        _log.LogDebug(
            "CaptureScreenshotAsync produced {Bytes} bytes for {Target} (size {Width}x{Height}px @ {DpiX}x{DpiY} DPI).",
            bytes.Length,
            targetName ?? "(main window)",
            pxW,
            pxH,
            dpiVal,
            dpiVal);
        return bytes;
    }

    private Control? ResolveScreenshotTarget(string? targetName)
    {
        if (string.IsNullOrEmpty(targetName))
        {
            var win = VisualTreeFinder.FirstWindow(_app);
            if (win is null)
            {
                throw new InvalidOperationException(
                    "No window is currently open; cannot capture a screenshot. " +
                    "Open a window or specify an element name via the target argument. " +
                    "(Note: ISingleViewApplicationLifetime hosts have no Windows collection - " +
                    "Phase 2.1's adapter targets desktop only.)");
            }
            return win;
        }

        var fe = VisualTreeFinder.FindByName(_app, targetName!, _log);
        if (fe is null)
        {
            throw new InvalidOperationException(
                $"No Control named '{targetName}' was found in any open window. " +
                "Pass an element with x:Name or AutomationProperties.AutomationId set.");
        }
        return fe;
    }
}
