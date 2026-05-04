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
//   * CaptureScreenshotAsync(target?, windowId?, ct) -> RenderTargetBitmap.Render(visual)
//     and Save(stream) - Avalonia's RenderTargetBitmap.Save defaults to PNG.
//     Target null -> first window via VisualTreeFinder.FirstWindow (or the
//     tracked windowId-scoped instance).
//     Target named -> resolve via VisualTreeFinder, capture just that element.
//   * ResolveControlAsync(rootName, controlName, windowId?, ct) -> walk every
//     open Window looking for a Control whose AutomationProperties.AutomationId
//     equals controlName, falling back to Control.Name. When windowId is
//     supplied AND maps to a Window-typed instance, the search is scoped.
//
// Phase 3.3 multi-window routing additions:
//   * RootInstanceTracker holds the live list of root instances tagged with
//     stable windowIds. MarionetteAvalonia.AttachTo populates it from the
//     bridged factories AND from desktop.Windows changes.
//   * GetWindowIds(rootName) / GetRootInstance(rootName, windowId) expose
//     the tracker to the runtime.
//   * WindowsChanged forwards the tracker's Changed event.
//
// Stripping invariant: this file is in `Marionette.NET.Adapter.Avalonia`,
// which is only ProjectReferenced when EnableMcpAutomation=true.

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
/// Avalonia 11.x implementation of <see cref="IUiAutomationAdapter"/>.
/// </summary>
public sealed class AvaloniaUiAutomationAdapter : IUiAutomationAdapter
{
    private readonly global::Avalonia.Application _app;
    private readonly ILogger<AvaloniaUiAutomationAdapter> _log;
    private readonly RootInstanceTracker _tracker;

    /// <summary>
    /// Construct an Avalonia adapter bound to the supplied application.
    /// </summary>
    public AvaloniaUiAutomationAdapter(
        global::Avalonia.Application app,
        ILogger<AvaloniaUiAutomationAdapter> log,
        RootInstanceTracker? tracker = null)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _tracker = tracker ?? new RootInstanceTracker();
        _tracker.Changed += OnTrackerChanged;
    }

    /// <summary>Phase 3.3: expose the tracker to MarionetteAvalonia.</summary>
    public RootInstanceTracker Tracker => _tracker;

    /// <inheritdoc />
    public async Task DispatchAsync(Action action, CancellationToken ct)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        ct.ThrowIfCancellationRequested();

        var disp = Dispatcher.UIThread;
        if (disp.CheckAccess())
        {
            try { action(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DispatchAsync(Action) inline call threw.");
                throw;
            }
            return;
        }

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
    public Task<byte[]> CaptureScreenshotAsync(string? targetName, string? windowId, CancellationToken ct)
    {
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
            return AvaloniaInputSimulator.Simulate(fe, kind, args, _log);
        }, ct);
    }

    /// <inheritdoc />
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
            return AvaloniaEventRaiser.Raise(fe, eventName, args, _log);
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

    private Window? ResolveScopedWindow(string? rootName, string? windowId)
    {
        if (string.IsNullOrEmpty(rootName) || string.IsNullOrEmpty(windowId)) return null;
        return _tracker.GetInstance(rootName!, windowId) as Window;
    }

    // -------------------------------------------------------------------------
    // Screenshot internals (UI-thread-only)
    // -------------------------------------------------------------------------

    private byte[] CaptureScreenshotOnUiThread(string? targetName, string? windowId)
    {
        var element = ResolveScreenshotTarget(targetName, windowId);
        if (element is null)
        {
            throw new InvalidOperationException(
                $"Could not resolve a screenshot target for '{targetName ?? "(main window)"}'.");
        }

        var topLevel = TopLevel.GetTopLevel(element);
        var scaling = topLevel?.RenderScaling ?? 1.0;
        var bounds = element.Bounds;
        var pxW = (int)Math.Ceiling(bounds.Width * scaling);
        var pxH = (int)Math.Ceiling(bounds.Height * scaling);
        if (pxW <= 0 || pxH <= 0)
        {
            throw new InvalidOperationException(
                $"Element '{targetName ?? "(main window)"}' has no visible size " +
                $"(Width={bounds.Width}, Height={bounds.Height}). " +
                "Wait until the window is laid out (e.g. after Opened) before capturing.");
        }

        var dpiVal = 96.0 * scaling;
        var bmp = new RenderTargetBitmap(
            new PixelSize(pxW, pxH),
            new Vector(dpiVal, dpiVal));

        bmp.Render(element);

        using var ms = new MemoryStream();
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

    private Control? ResolveScreenshotTarget(string? targetName, string? windowId)
    {
        if (string.IsNullOrEmpty(targetName))
        {
            if (!string.IsNullOrEmpty(windowId))
            {
                var byId = LookupWindowById(windowId!);
                if (byId is not null) return byId;
            }
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

        Control? fe;
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
                $"No Control named '{targetName}' was found in any open window. " +
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
