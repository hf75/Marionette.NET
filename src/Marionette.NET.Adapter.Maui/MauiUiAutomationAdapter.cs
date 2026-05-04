// Marionette.NET — MAUI UI automation adapter (Phase 4.1 production impl)
//
// Implements `IUiAutomationAdapter` against the .NET MAUI 10.x Dispatcher /
// visual tree pipelines. Mirrors the WPF / Avalonia / WinUI adapters one API
// at a time, with MAUI-specific quirks documented per method:
//
//   * DispatchAsync(Action / Func<T>, ct) -> IDispatcher.Dispatch.
//     MAUI's `Microsoft.Maui.Dispatching.IDispatcher` is the public threading
//     primitive (replacing WPF's `Dispatcher`, Avalonia's
//     `Dispatcher.UIThread`, WinUI's `DispatcherQueue`). It exposes
//     `Dispatch(Action)` (fire-and-forget) so we wrap each call in a
//     TaskCompletionSource. CheckAccess analogue: `IsDispatchRequired`.
//   * CaptureScreenshotAsync(target?, ct) -> Microsoft.Maui.Media.Screenshot
//     .Default.CaptureAsync(). MAUI exposes window-level screenshot via the
//     Essentials API; element-level screenshot is not part of the cross-
//     platform surface in 10.x. Phase 4.1 implements window-level only;
//     element-level deferred to Phase 6.
//   * ResolveControlAsync(rootName, controlName, ct) -> walks every live
//     `Application.Windows`'s Page subtree via `IVisualTreeElement`. Match
//     precedence: `Element.AutomationId` first, `Element.StyleId` second,
//     INameScope.FindByName third. Same shape as the other adapters, just
//     against MAUI types.
//   * SimulateInputAsync(...) -> MauiInputSimulator (IButtonController.SendClicked
//     semantic path; type_text via Entry.Text direct setter).
//   * RaiseEventAsync(...) -> MauiEventRaiser (CLR-event reflection; AOT
//     caveat documented).
//
// Stripping invariant: this file is in `Marionette.NET.Adapter.Maui`, which
// is only ProjectReferenced when EnableMcpAutomation=true. Stripped Release
// builds never see this type.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Media;

using Marionette.Adapter.Maui.Internal;
using Marionette.Runtime.Adapters;

using Microsoft.Extensions.Logging;

namespace Marionette.Adapter.Maui;

/// <summary>
/// .NET MAUI implementation of <see cref="IUiAutomationAdapter"/>. Marshals
/// work onto the MAUI UI thread via
/// <see cref="Microsoft.Maui.Dispatching.IDispatcher"/>, captures screenshots
/// via <see cref="Microsoft.Maui.Media.Screenshot"/>, and resolves named
/// controls by walking live <see cref="Application.Windows"/>.
/// </summary>
/// <remarks>
/// Constructed and registered into the Marionette runtime's DI container by
/// <see cref="MarionetteMaui.AttachTo(Application, IReadOnlyList{Marionette.Runtime.Manifest.RootDescriptor}, string[]?, ILoggerFactory?)"/> -
/// adopters do not new this up directly.
/// </remarks>
public sealed class MauiUiAutomationAdapter : IUiAutomationAdapter
{
    private readonly Application _app;
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<MauiUiAutomationAdapter> _log;
    private readonly RootInstanceTracker _tracker;

    /// <summary>
    /// Construct a MAUI adapter bound to the supplied <see cref="Application"/>
    /// and <see cref="IDispatcher"/>. The dispatcher MUST be the application's
    /// UI dispatcher (typically <see cref="Application.Dispatcher"/>).
    /// </summary>
    public MauiUiAutomationAdapter(
        Application app,
        IDispatcher dispatcher,
        ILogger<MauiUiAutomationAdapter> log,
        RootInstanceTracker? tracker = null)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _tracker = tracker ?? new RootInstanceTracker();
        _tracker.Changed += OnTrackerChanged;
    }

    /// <summary>Phase 4.1: expose the tracker to MarionetteMaui.</summary>
    public RootInstanceTracker Tracker => _tracker;

    /// <inheritdoc />
    public Task DispatchAsync(Action action, CancellationToken ct)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        ct.ThrowIfCancellationRequested();

        if (!_dispatcher.IsDispatchRequired)
        {
            // Already on the UI thread - run inline. Same short-circuit as
            // WPF/Avalonia/WinUI adapters; saves one Dispatcher round-trip.
            try { action(); return Task.CompletedTask; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DispatchAsync(Action) inline call threw.");
                return Task.FromException(ex);
            }
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => tcs.TrySetCanceled(ct));

        var enqueued = _dispatcher.Dispatch(() =>
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
                "IDispatcher.Dispatch failed - the dispatcher may have been shut down."));
        }
        return tcs.Task;
    }

    /// <inheritdoc />
    public Task<T> DispatchAsync<T>(Func<T> func, CancellationToken ct)
    {
        if (func is null) throw new ArgumentNullException(nameof(func));
        ct.ThrowIfCancellationRequested();

        if (!_dispatcher.IsDispatchRequired)
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

        var enqueued = _dispatcher.Dispatch(() =>
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
                "IDispatcher.Dispatch failed - the dispatcher may have been shut down."));
        }
        return tcs.Task;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Phase 4.1 captures the entire device screen via
    /// <see cref="Microsoft.Maui.Media.Screenshot.Default"/>. MAUI's
    /// Essentials API doesn't surface element-level screenshot in 10.x;
    /// passing a non-null <paramref name="targetName"/> still captures the
    /// entire screen (with a debug log noting the limitation). Element-level
    /// screenshot is a Phase-6 refinement.
    /// </remarks>
    public async Task<byte[]> CaptureScreenshotAsync(string? targetName, string? windowId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Microsoft.Maui.Media.Screenshot.Default.IsCaptureSupported)
        {
            throw new NotSupportedException(
                "Microsoft.Maui.Media.Screenshot.Default.IsCaptureSupported reports false " +
                "on this platform; Phase 4.1 cannot capture a screenshot in this configuration.");
        }
        if (!string.IsNullOrEmpty(targetName))
        {
            _log.LogDebug(
                "CaptureScreenshotAsync: targetName '{Target}' requested but Phase-4.1 MAUI " +
                "captures the full screen via Microsoft.Maui.Media.Screenshot.Default. " +
                "Element-level screenshot is a Phase-6 refinement.",
                targetName);
        }
        _ = windowId; // Phase 4.1: full-screen capture is window-agnostic.

        // Microsoft.Maui.Media.Screenshot.CaptureAsync() must run on the UI
        // thread on most platforms; dispatch first, then await the result via
        // a captured outer TaskCompletionSource.
        var screenshotTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await DispatchAsync(() =>
        {
            _ = RunCaptureAsync(screenshotTcs, ct);
        }, ct).ConfigureAwait(false);
        return await screenshotTcs.Task.ConfigureAwait(false);
    }

    private async Task RunCaptureAsync(TaskCompletionSource<byte[]> outer, CancellationToken ct)
    {
        try
        {
            var result = await Microsoft.Maui.Media.Screenshot.Default.CaptureAsync().ConfigureAwait(true);
            if (result is null)
            {
                outer.TrySetException(new InvalidOperationException(
                    "Microsoft.Maui.Media.Screenshot.Default.CaptureAsync returned null."));
                return;
            }

            using var stream = await result.OpenReadAsync(Microsoft.Maui.Media.ScreenshotFormat.Png)
                .ConfigureAwait(true);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(true);
            var bytes = ms.ToArray();

            _log.LogDebug(
                "CaptureScreenshotAsync produced {Bytes} bytes ({Width}x{Height}px).",
                bytes.Length, result.Width, result.Height);

            outer.TrySetResult(bytes);
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
            var scoped = ResolveScopedWindow(rootName, windowId);
            if (scoped is not null)
            {
                return VisualTreeFinder.FindByNameInWindow(scoped, controlName, _log);
            }
            return VisualTreeFinder.FindByName(_app, controlName, _log);
        }, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Phase 4.1 caveat: MAUI 10.x doesn't surface a public raw-input pipeline
    /// nor a unified RoutedEvent system. The simulator covers the most common
    /// cases via semantic paths:
    /// <list type="bullet">
    ///   <item><description>click / double_click on Button (or any IButtonController) via SendClicked.</description></item>
    ///   <item><description>type_text on Entry / Editor / SearchBar via direct Text setter.</description></item>
    /// </list>
    /// Other kinds (key_*, mouse_move, right_click) return false with a
    /// logged limitation. Adopters who need full keyboard / pointer
    /// automation should decorate the underlying handler with
    /// <c>[McpCallable]</c> and invoke via invoke_method.
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
            var el = scoped is not null
                ? VisualTreeFinder.FindByNameInWindow(scoped, controlName, _log)
                : VisualTreeFinder.FindByName(_app, controlName, _log);
            if (el is null)
            {
                _log.LogInformation("simulate_input: could not resolve '{Control}' (windowId={WindowId}).",
                    controlName, windowId ?? "(default)");
                return false;
            }
            _log.LogDebug("simulate_input '{Kind}' on {Type} (AutomationId={AutomationId}, windowId={WindowId}).",
                kind, el.GetType().Name, el.AutomationId ?? "(unset)", windowId ?? "(default)");
            return MauiInputSimulator.Simulate(el, kind, args, _log);
        }, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// AOT note: <see cref="MauiEventRaiser.Raise"/> walks the control's type
    /// chain via reflection looking for the compiler-emitted backing field of
    /// the named CLR event. Trimming MAY remove the backing field; framework
    /// controls keep theirs rooted via XAML usage. MAUI's CLR-event surface is
    /// the most fragile of the four adapters (no canonical static-field
    /// idiom); Phase 4.2's AOT survey confirms WPF/Avalonia AOT-publish more
    /// reliably than MAUI. The interface method is marked
    /// <see cref="System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"/>;
    /// the suppression below acknowledges the warning here at the
    /// implementation site rather than re-propagating it.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(
        "MauiUiAutomationAdapter.RaiseEventAsync reflects on the compiler-emitted backing delegate " +
        "field of CLR events on the element's type chain. Trimming may strip these fields for " +
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
            var el = scoped is not null
                ? VisualTreeFinder.FindByNameInWindow(scoped, controlName, _log)
                : VisualTreeFinder.FindByName(_app, controlName, _log);
            if (el is null)
            {
                _log.LogInformation("raise_event: could not resolve '{Control}' (windowId={WindowId}).",
                    controlName, windowId ?? "(default)");
                return false;
            }
            _log.LogDebug("raise_event '{Event}' on {Type} (AutomationId={AutomationId}, windowId={WindowId}).",
                eventName, el.GetType().Name, el.AutomationId ?? "(unset)", windowId ?? "(default)");
            return MauiEventRaiser.Raise(el, eventName, args, _log);
        }, ct);
    }

    // ---------------------------------------------------------------------
    // Phase 4.1 — multi-window routing (single-window is the common case)
    // ---------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyList<string> GetWindowIds(string rootName) => _tracker.GetWindowIds(rootName);

    /// <inheritdoc />
    public object? GetRootInstance(string rootName, string? windowId) => _tracker.GetInstance(rootName, windowId);

    /// <inheritdoc />
    public event EventHandler? WindowsChanged;

    private void OnTrackerChanged(object? sender, EventArgs e) => WindowsChanged?.Invoke(this, e);

    /// <summary>
    /// Resolve a windowId hint to a tracked MAUI Window when the tracker holds
    /// a Window-typed instance for that root. Returns null otherwise (the
    /// caller falls back to the multi-window walk).
    /// </summary>
    private Window? ResolveScopedWindow(string? rootName, string? windowId)
    {
        if (string.IsNullOrEmpty(rootName) || string.IsNullOrEmpty(windowId)) return null;
        return _tracker.GetInstance(rootName!, windowId) as Window;
    }
}
