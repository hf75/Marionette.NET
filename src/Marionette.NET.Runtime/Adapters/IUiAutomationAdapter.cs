// Marionette.NET — UI automation adapter abstraction
//
// Phase 1.2 contract:
//
// The runtime is framework-agnostic. Every UI-framework-specific behaviour
// (Dispatcher marshalling, RenderTargetBitmap-style screenshot, control
// resolution for triggerables) lives behind this interface. Phase 1.3 ships
// the WPF implementation; Phase 2 + add Avalonia, WinUI, Uno, MAUI flavours
// against the same shape.
//
// Why the interface lives in Runtime (not in each Adapter assembly):
//
//   * The DI container in MarionetteHost only ever sees Runtime types — it
//     resolves an `IUiAutomationAdapter` registered by the adopter's host
//     bootstrap.
//   * Each adapter assembly references Runtime (Phase 0 csproj graph), so
//     they can implement this interface without a circular reference.
//   * Putting the interface in Runtime keeps the Phase 0 stripping promise
//     intact: a stripped Release build never references Runtime, never sees
//     the interface, never pulls in any adapter implementation.
//
// The interface intentionally exposes a *narrow* surface for Phase 1.2.
// Phase 1.3+ may add `RaiseEventAsync`, `SimulateInputAsync`,
// `EnumerateAutomationTreeAsync`, etc. We do NOT pre-add them here — Phase 1.2
// only needs Dispatch + Screenshot + ResolveControl.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Marionette.Runtime.Adapters;

/// <summary>
/// Framework-specific UI automation surface. Each Marionette adapter
/// (Wpf / Avalonia / WinUI / Uno / MAUI) implements this interface against
/// its own Dispatcher, screenshot pipeline, and control hierarchy.
/// </summary>
/// <remarks>
/// The runtime resolves a single instance from the DI container. Adopters
/// register their adapter via the host builder (see
/// <c>MarionetteHost.RunAsync</c>); when no adapter is registered the runtime
/// falls back to <see cref="NoOpAdapter"/> for headless modes.
/// </remarks>
public interface IUiAutomationAdapter
{
    /// <summary>
    /// Marshal an action onto the UI thread for the underlying framework.
    /// </summary>
    /// <param name="action">The action to execute on the UI thread.</param>
    /// <param name="ct">Cancellation token. Adapters honour cancellation
    /// before scheduling the action; in-flight actions are not interrupted.</param>
    /// <returns>A <see cref="Task"/> that completes when the action has run.</returns>
    Task DispatchAsync(Action action, CancellationToken ct);

    /// <summary>
    /// Marshal a function onto the UI thread for the underlying framework
    /// and return its result. Used by <c>read_observable</c> and value-returning
    /// callables that need to read UI state.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="func">The function to execute on the UI thread.</param>
    /// <param name="ct">Cancellation token (see <see cref="DispatchAsync(Action, CancellationToken)"/>).</param>
    /// <returns>The function's return value.</returns>
    Task<T> DispatchAsync<T>(Func<T> func, CancellationToken ct);

    /// <summary>
    /// Capture a screenshot of the application's current visual state.
    /// </summary>
    /// <param name="targetName">
    /// Optional <c>[McpRoot]</c>-relative window or control name. When
    /// <see langword="null"/>, the adapter captures the application's main
    /// window.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>PNG-encoded byte array.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown by adapters that do not (yet) implement screenshotting,
    /// including the Phase 1.2 <see cref="NoOpAdapter"/>.
    /// </exception>
    Task<byte[]> CaptureScreenshotAsync(string? targetName, CancellationToken ct);

    /// <summary>
    /// Resolve a control instance referenced by <c>[McpTriggerable]</c>
    /// to a framework-specific control object (e.g. WPF
    /// <c>System.Windows.Controls.Button</c>). Used by the runtime when
    /// firing a trigger to give the adapter the actual control reference.
    /// </summary>
    /// <param name="rootName">The <c>[McpRoot]</c>-relative root name (e.g. <c>"MainWindow"</c>).</param>
    /// <param name="controlName">The <c>[McpTriggerable]</c> property name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved control instance, or <see langword="null"/> when not found.</returns>
    Task<object?> ResolveControlAsync(string rootName, string controlName, CancellationToken ct);
}
