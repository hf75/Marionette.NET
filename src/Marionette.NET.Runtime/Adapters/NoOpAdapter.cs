// Marionette.NET — fallback UI automation adapter
//
// Used when MarionetteHost.RunAsync is invoked without an explicit adapter
// (typically `--mcp --headless` in unit tests, or framework-less integration
// scenarios).
//
// Behaviour:
//   * DispatchAsync(action) executes the action inline on the calling thread.
//   * DispatchAsync<T>(func) executes the function inline.
//   * CaptureScreenshot throws NotSupportedException — the runtime surfaces
//     this as a structured `{success:false, errorCode:"..."}` to the LLM
//     instead of crashing.
//   * ResolveControl returns null — Phase 1.2's manifest never has triggerables
//     in headless mode.
//   * SimulateInputAsync / RaiseEventAsync (Phase 3.1) return false — input
//     simulation requires a real UI thread + visual tree, which headless mode
//     doesn't provide. The runtime tools surface this as a structured
//     `simulate_input_not_supported` / `raise_event_not_supported` error.
//   * GetWindowIds (Phase 3.3) returns an empty list — headless mode has no
//     live windows. The runtime falls back to the registry's bare-form root.
//   * GetRootInstance (Phase 3.3) returns null — same reason.
//
// This adapter is intentionally trivial; anything more complex belongs in a
// real framework-specific adapter.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marionette.Runtime.Adapters;

/// <summary>
/// No-op <see cref="IUiAutomationAdapter"/> used when the host runs without
/// a real UI framework attached (typically <c>--mcp --headless</c> mode).
/// </summary>
/// <remarks>
/// The dispatcher methods run their callbacks inline on the calling thread.
/// Screenshot and control-resolution methods either throw
/// <see cref="NotSupportedException"/> or return <see langword="null"/> so
/// the runtime can surface a structured error to the LLM rather than
/// crashing.
/// </remarks>
public sealed class NoOpAdapter : IUiAutomationAdapter
{
    private readonly ILogger _log;

    /// <summary>
    /// Construct a no-op adapter. Without a logger, breadcrumb messages for
    /// unsupported input/event paths are silently dropped — adopters running
    /// headless tests usually want this. Pass a non-null logger when wiring
    /// the adapter into a DI container.
    /// </summary>
    public NoOpAdapter(ILogger<NoOpAdapter>? log = null)
    {
        _log = log ?? NullLogger<NoOpAdapter>.Instance;
    }

    /// <inheritdoc />
    public Task DispatchAsync(Action action, CancellationToken ct)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        ct.ThrowIfCancellationRequested();
        action();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<T> DispatchAsync<T>(Func<T> func, CancellationToken ct)
    {
        if (func is null) throw new ArgumentNullException(nameof(func));
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(func());
    }

    /// <inheritdoc />
    public Task<byte[]> CaptureScreenshotAsync(string? targetName, string? windowId, CancellationToken ct)
    {
        _ = targetName;
        _ = windowId;
        _ = ct;
        // Surfaced as a structured error by the runtime's capture_screenshot
        // tool — never reaches the JSON-RPC stream as an unhandled exception.
        throw new NotSupportedException(
            "NoOpAdapter cannot capture screenshots. Register a framework-specific " +
            "IUiAutomationAdapter (e.g. WpfUiAutomationAdapter from Phase 1.3) " +
            "via MarionetteHost.RunAsync.");
    }

    /// <inheritdoc />
    public Task<object?> ResolveControlAsync(string rootName, string controlName, string? windowId, CancellationToken ct)
    {
        _ = rootName;
        _ = controlName;
        _ = windowId;
        _ = ct;
        return Task.FromResult<object?>(null);
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
        _ = args;
        _ = windowId;
        _ = ct;
        _log.LogWarning(
            "simulate_input not supported in headless mode (root='{Root}', control='{Control}', kind='{Kind}'). " +
            "Register a framework-specific IUiAutomationAdapter (Phase 3.1: WPF, Avalonia) to drive real input through the UI pipeline.",
            rootName, controlName, kind);
        return Task.FromResult(false);
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
        _ = args;
        _ = windowId;
        _ = ct;
        _log.LogWarning(
            "raise_event not supported in headless mode (root='{Root}', control='{Control}', event='{Event}'). " +
            "Register a framework-specific IUiAutomationAdapter (Phase 3.1: WPF, Avalonia) to fire routed events on real controls.",
            rootName, controlName, eventName);
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetWindowIds(string rootName)
    {
        _ = rootName;
        // Headless mode has no live windows — the runtime falls back to the
        // single registry instance via Manifest. Returning an empty list
        // signals "no per-window routing"; DynamicToolRegistry then keeps
        // the bare-form tool names without per-window suffixes.
        return Array.Empty<string>();
    }

    /// <inheritdoc />
    public object? GetRootInstance(string rootName, string? windowId)
    {
        _ = rootName;
        _ = windowId;
        // No tracker — the runtime falls back to ManifestRegistry.Find.
        return null;
    }

    /// <inheritdoc />
#pragma warning disable CS0067 // Event is never used — by design (NoOp)
    public event EventHandler? WindowsChanged;
#pragma warning restore CS0067
}
