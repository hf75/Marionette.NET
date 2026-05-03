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
//
// This adapter is intentionally trivial; anything more complex belongs in a
// real framework-specific adapter.

using System;
using System.Threading;
using System.Threading.Tasks;

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
    public Task<byte[]> CaptureScreenshotAsync(string? targetName, CancellationToken ct)
    {
        _ = targetName;
        _ = ct;
        // Surfaced as a structured error by the runtime's capture_screenshot
        // tool — never reaches the JSON-RPC stream as an unhandled exception.
        throw new NotSupportedException(
            "NoOpAdapter cannot capture screenshots. Register a framework-specific " +
            "IUiAutomationAdapter (e.g. WpfUiAutomationAdapter from Phase 1.3) " +
            "via MarionetteHost.RunAsync.");
    }

    /// <inheritdoc />
    public Task<object?> ResolveControlAsync(string rootName, string controlName, CancellationToken ct)
    {
        _ = rootName;
        _ = controlName;
        _ = ct;
        return Task.FromResult<object?>(null);
    }
}
