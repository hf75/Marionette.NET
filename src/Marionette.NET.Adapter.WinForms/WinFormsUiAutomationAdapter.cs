// Marionette.NET — Windows Forms UI automation adapter (Phase 15)
//
// Implements IUiAutomationAdapter against Windows Forms primitives:
//
//   * DispatchAsync via Control.BeginInvoke wrapped in TaskCompletionSource<T>.
//     The Task<T>-around-BeginInvoke pattern was verified by Spike A
//     (.phase15/spike-a-findings.md, Claim 1).
//   * CaptureScreenshotAsync via Control.DrawToBitmap → System.Drawing.Bitmap →
//     PNG via Bitmap.Save(stream, ImageFormat.Png). Verified by Spike A
//     (Claim 2).
//   * ResolveControlAsync walks Application.OpenForms via ControlTreeFinder.
//   * SimulateInputAsync routes through WinFormsInputSimulator, which calls
//     the Phase-14 Win32InputInjector. Verified by Spike A (Claim 3).
//   * RaiseEventAsync invokes the protected `On<EventName>(EventArgs)` virtual
//     on the Control via reflection. Same trim contract as the WPF adapter.
//
// Phase 3.3 multi-window routing: the adapter holds a RootInstanceTracker and
// installs an OpenFormsHook that reconciles Application.OpenForms on every
// idle tick. Window IDs (`w1`, `w2`, ...) come from the shared tracker.
//
// Threading: every Control / Form access is dispatched through the UI
// thread. Win32InputInjector.* calls are thread-safe so they don't need
// the dispatch wrapper, but we still go through it so the adapter's
// behaviour is consistent.
//
// Stripping invariant: file is in Marionette.NET.Adapter.WinForms, only
// referenced when EnableMcpAutomation=true. Stripped Release builds never
// see this type.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Marionette.Adapter.WinForms.Internal;
using Marionette.Runtime.Adapters;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marionette.Adapter.WinForms;

/// <summary>
/// Windows Forms implementation of <see cref="IUiAutomationAdapter"/>.
/// Marshals work onto the WinForms UI thread via the bootstrap-supplied
/// <see cref="Control"/> handle (typically the application's main form);
/// captures screenshots via <see cref="Control.DrawToBitmap"/>; resolves
/// named controls by walking <see cref="Application.OpenForms"/>.
/// </summary>
/// <remarks>
/// Constructed and registered into the runtime by
/// <see cref="MarionetteWinForms.AttachTo"/>. Adopters do not new this up
/// directly.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WinFormsUiAutomationAdapter : IUiAutomationAdapter
{
    private readonly Control _dispatchControl;
    private readonly ILogger<WinFormsUiAutomationAdapter> _log;
    private readonly RootInstanceTracker _tracker;

    /// <summary>
    /// Construct a WinForms adapter bound to the supplied dispatch control.
    /// </summary>
    /// <param name="dispatchControl">
    /// A live Control whose handle has been created (typically the
    /// application's main form). Used as the BeginInvoke target so dispatched
    /// actions land on the WinForms UI thread.
    /// </param>
    /// <param name="log">Logger.</param>
    /// <param name="tracker">
    /// Shared per-process root-instance tracker. The adapter forwards its
    /// <see cref="RootInstanceTracker.Changed"/> event onto
    /// <see cref="WindowsChanged"/> so the runtime's DynamicToolRegistry can
    /// react. Pass <see langword="null"/> to construct a fresh tracker
    /// (the bootstrap typically supplies one).
    /// </param>
    public WinFormsUiAutomationAdapter(
        Control dispatchControl,
        ILogger<WinFormsUiAutomationAdapter> log,
        RootInstanceTracker? tracker = null)
    {
        _dispatchControl = dispatchControl ?? throw new ArgumentNullException(nameof(dispatchControl));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _tracker = tracker ?? new RootInstanceTracker();
        _tracker.Changed += OnTrackerChanged;
    }

    /// <summary>
    /// Expose the tracker so <see cref="MarionetteWinForms"/> can register
    /// live root instances + hook <see cref="Application.OpenForms"/> changes.
    /// </summary>
    public RootInstanceTracker Tracker => _tracker;

    /// <inheritdoc />
    public Task DispatchAsync(Action action, CancellationToken ct)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        ct.ThrowIfCancellationRequested();

        if (!_dispatchControl.InvokeRequired)
        {
            // Already on the UI thread — running inline avoids one BeginInvoke
            // round-trip. Matches the WPF / Avalonia adapters' pattern.
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DispatchAsync(Action) inline call threw.");
                return Task.FromException(ex);
            }
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _dispatchControl.BeginInvoke(new Action(() =>
            {
                if (ct.IsCancellationRequested) { tcs.SetCanceled(ct); return; }
                try { action(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            }));
        }
        catch (Exception ex)
        {
            // BeginInvoke throws if the handle isn't created yet — surface
            // that to the caller rather than swallowing.
            _log.LogWarning(ex, "DispatchAsync(Action) BeginInvoke threw (handle not created?).");
            tcs.TrySetException(ex);
        }
        return tcs.Task;
    }

    /// <inheritdoc />
    public Task<T> DispatchAsync<T>(Func<T> func, CancellationToken ct)
    {
        if (func is null) throw new ArgumentNullException(nameof(func));
        ct.ThrowIfCancellationRequested();

        if (!_dispatchControl.InvokeRequired)
        {
            try { return Task.FromResult(func()); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DispatchAsync<{T}> inline call threw.", typeof(T).Name);
                return Task.FromException<T>(ex);
            }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _dispatchControl.BeginInvoke(new Action(() =>
            {
                if (ct.IsCancellationRequested) { tcs.SetCanceled(ct); return; }
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DispatchAsync<{T}> BeginInvoke threw (handle not created?).", typeof(T).Name);
            tcs.TrySetException(ex);
        }
        return tcs.Task;
    }

    /// <inheritdoc />
    public Task<byte[]> CaptureScreenshotAsync(string? targetName, string? windowId, CancellationToken ct)
    {
        return DispatchAsync(() => CaptureOnUiThread(targetName, windowId), ct);
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
            var scopedForm = ResolveScopedForm(rootName, windowId);
            return scopedForm is not null
                ? ControlTreeFinder.FindByNameInForm(scopedForm, controlName, _log)
                : ControlTreeFinder.FindByName(controlName, _log);
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
            var scopedForm = ResolveScopedForm(rootName, windowId);
            var ctrl = scopedForm is not null
                ? ControlTreeFinder.FindByNameInForm(scopedForm, controlName, _log)
                : ControlTreeFinder.FindByName(controlName, _log);
            if (ctrl is null)
            {
                _log.LogInformation("simulate_input: could not resolve '{Control}' (windowId={WindowId}).",
                    controlName, windowId ?? "(default)");
                return false;
            }
            _log.LogDebug("simulate_input '{Kind}' on {Type} (Name={Name}, windowId={WindowId}).",
                kind, ctrl.GetType().Name, ctrl.Name, windowId ?? "(default)");
            return WinFormsInputSimulator.Simulate(ctrl, kind, args, _log);
        }, ct);
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(
        "WinFormsUiAutomationAdapter.RaiseEventAsync resolves CLR events on Controls via reflection. " +
        "Trimming may strip the protected `On<EventName>` virtual or the EventXxx static field. " +
        "Use simulate_input or [McpCallable] for AOT-clean event firing.")]
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
            var scopedForm = ResolveScopedForm(rootName, windowId);
            var ctrl = scopedForm is not null
                ? ControlTreeFinder.FindByNameInForm(scopedForm, controlName, _log)
                : ControlTreeFinder.FindByName(controlName, _log);
            if (ctrl is null)
            {
                _log.LogInformation("raise_event: could not resolve '{Control}' (windowId={WindowId}).",
                    controlName, windowId ?? "(default)");
                return false;
            }
            _log.LogDebug("raise_event '{Event}' on {Type} (Name={Name}, windowId={WindowId}).",
                eventName, ctrl.GetType().Name, ctrl.Name, windowId ?? "(default)");
            return RaiseEventReflectively(ctrl, eventName, args);
        }, ct);
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 15: WinForms event lookup uses reflection; trimming caveat documented in adapter XML doc.")]
    private bool RaiseEventReflectively(Control ctrl, string eventName, IReadOnlyDictionary<string, object?>? args)
        => WinFormsEventRaiser.Raise(ctrl, eventName, args, _log);

    /// <inheritdoc />
    public IReadOnlyList<string> GetWindowIds(string rootName) => _tracker.GetWindowIds(rootName);

    /// <inheritdoc />
    public object? GetRootInstance(string rootName, string? windowId) => _tracker.GetInstance(rootName, windowId);

    /// <inheritdoc />
    public event EventHandler? WindowsChanged;

    private void OnTrackerChanged(object? sender, EventArgs e) => WindowsChanged?.Invoke(this, e);

    /// <summary>
    /// When a windowId is supplied AND it maps to a tracked Form-typed root
    /// instance, return that Form so callers can scope their walk to it.
    /// Otherwise return null and the legacy whole-app walk runs.
    /// </summary>
    private Form? ResolveScopedForm(string? rootName, string? windowId)
    {
        if (string.IsNullOrEmpty(rootName) || string.IsNullOrEmpty(windowId)) return null;
        var inst = _tracker.GetInstance(rootName!, windowId);
        return inst as Form;
    }

    // -------------------------------------------------------------------------
    // Screenshot internals (UI-thread-only)
    // -------------------------------------------------------------------------

    private byte[] CaptureOnUiThread(string? targetName, string? windowId)
    {
        var (control, size) = ResolveScreenshotTarget(targetName, windowId);
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new InvalidOperationException(
                $"Control '{targetName ?? "(main form)"}' has no visible size " +
                $"(Size={size.Width}x{size.Height}). Wait until Shown / HandleCreated before capturing.");
        }

        using var bmp = new Bitmap(size.Width, size.Height);
        control.DrawToBitmap(bmp, new Rectangle(Point.Empty, size));
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        var bytes = ms.ToArray();

        _log.LogDebug(
            "CaptureScreenshotAsync produced {Bytes} bytes for {Target} (size {Width}x{Height}).",
            bytes.Length,
            targetName ?? "(main form)",
            size.Width,
            size.Height);
        return bytes;
    }

    private (Control Control, Size Size) ResolveScreenshotTarget(string? targetName, string? windowId)
    {
        if (string.IsNullOrEmpty(targetName))
        {
            // No target → screenshot a Form. Prefer the windowId-scoped form
            // when supplied, otherwise the first OpenForm.
            Form? form = null;
            if (!string.IsNullOrEmpty(windowId))
            {
                form = LookupFormById(windowId!);
            }
            form ??= Application.OpenForms.Cast<Form>().FirstOrDefault();
            if (form is null)
            {
                throw new InvalidOperationException(
                    "No form is currently open; cannot capture a screenshot. " +
                    "Open a form or specify a control name via the target argument.");
            }
            return (form, form.ClientSize);
        }

        // Named target: scope to specific form when windowId supplied.
        Control? ctrl;
        if (!string.IsNullOrEmpty(windowId))
        {
            var scoped = LookupFormById(windowId!);
            ctrl = scoped is null
                ? ControlTreeFinder.FindByName(targetName!, _log)
                : ControlTreeFinder.FindByNameInForm(scoped, targetName!, _log);
        }
        else
        {
            ctrl = ControlTreeFinder.FindByName(targetName!, _log);
        }
        if (ctrl is null)
        {
            throw new InvalidOperationException(
                $"No Control named '{targetName}' was found in any open form. " +
                "Set Control.Name (or Control.AccessibleName) on the target.");
        }
        return (ctrl, ctrl.Size);
    }

    private Form? LookupFormById(string windowId)
    {
        foreach (var (_, id, instance) in _tracker.SnapshotAll())
        {
            if (string.Equals(id, windowId, StringComparison.Ordinal) && instance is Form f)
                return f;
        }
        return null;
    }
}
