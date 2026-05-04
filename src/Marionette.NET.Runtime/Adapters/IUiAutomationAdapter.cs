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
// Phase 3.1 adds the input-simulation and event-raising hooks that the
// `simulate_input` and `raise_event` MCP tools depend on. Both methods are
// additive — Phase 1/2 adapters that don't override them continue to work,
// they just have to return `false` for the new pair (NoOpAdapter does this).
//
// Phase 3.3 (multi-window routing) adds an OPTIONAL string `windowId`
// parameter to every method that needs to address a specific window:
//
//   * CaptureScreenshotAsync(targetName, windowId, ct)
//   * ResolveControlAsync(rootName, controlName, windowId, ct)
//   * SimulateInputAsync(rootName, controlName, kind, args, windowId, ct)
//   * RaiseEventAsync(rootName, controlName, eventName, args, windowId, ct)
//
// Plus two new enumeration methods that the runtime + DynamicToolRegistry
// use to discover live instances:
//
//   * IReadOnlyList<string> GetWindowIds(string rootName);
//   * object? GetRootInstance(string rootName, string? windowId);
//
// `null` windowId is the default: route to the first / oldest live instance
// (the "default window" — the original single-window behaviour). Each
// adapter is responsible for assigning stable per-process windowIds (e.g.
// `w1`, `w2`, ...) to the live root instances of every [McpRoot] type.

using System;
using System.Collections.Generic;
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
    /// <param name="windowId">
    /// Phase 3.3: optional <see cref="GetWindowIds(string)"/>-issued ID of
    /// the window to capture. <see langword="null"/> means "first / default
    /// window" (the original single-window behaviour).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>PNG-encoded byte array.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown by adapters that do not (yet) implement screenshotting,
    /// including the Phase 1.2 <see cref="NoOpAdapter"/>.
    /// </exception>
    Task<byte[]> CaptureScreenshotAsync(string? targetName, string? windowId, CancellationToken ct);

    /// <summary>
    /// Resolve a control instance referenced by <c>[McpTriggerable]</c>
    /// to a framework-specific control object (e.g. WPF
    /// <c>System.Windows.Controls.Button</c>). Used by the runtime when
    /// firing a trigger to give the adapter the actual control reference.
    /// </summary>
    /// <param name="rootName">The <c>[McpRoot]</c>-relative root name (e.g. <c>"MainWindow"</c>).</param>
    /// <param name="controlName">The <c>[McpTriggerable]</c> property name.</param>
    /// <param name="windowId">
    /// Phase 3.3: optional window-scoped lookup. <see langword="null"/> means
    /// "search every window" (compat with single-window mode); a specific ID
    /// scopes the search to that window's visual tree.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved control instance, or <see langword="null"/> when not found.</returns>
    Task<object?> ResolveControlAsync(string rootName, string controlName, string? windowId, CancellationToken ct);

    /// <summary>
    /// Drive a real input event through the framework's input pipeline against
    /// the named control. The framework treats the input as if it were
    /// user-driven — focus changes, capture, hover, and routed click handlers
    /// fire normally. This is the "test-automation-grade" input fidelity
    /// promised by MASTERPLAN tenet 8.
    /// </summary>
    /// <param name="rootName">The <c>[McpRoot]</c>-relative root name; passed through to the visual-tree finder as a disambiguation hint.</param>
    /// <param name="controlName">
    /// Either an <c>AutomationProperties.AutomationId</c> or an <c>x:Name</c>
    /// of a control inside a currently-open window. The adapter resolves the
    /// element through the same visual-tree walker used by
    /// <see cref="ResolveControlAsync"/>.
    /// </param>
    /// <param name="kind">
    /// String discriminator selecting the kind of input. Phase 3.1 supports:
    /// <list type="bullet">
    ///   <item><description><c>"click"</c> — left mouse button down + up</description></item>
    ///   <item><description><c>"double_click"</c> — left double-click</description></item>
    ///   <item><description><c>"right_click"</c> — right mouse button down + up</description></item>
    ///   <item><description><c>"key_press"</c> — key down + up (with optional <c>{key:"Enter"}</c> in <paramref name="args"/>)</description></item>
    ///   <item><description><c>"key_down"</c> — key down only</description></item>
    ///   <item><description><c>"key_up"</c> — key up only</description></item>
    ///   <item><description><c>"type_text"</c> — text input (with <c>{text:"hello"}</c>)</description></item>
    ///   <item><description><c>"mouse_move"</c> — mouse position update (with <c>{x,y}</c>)</description></item>
    /// </list>
    /// Adapters that don't support a kind return <see langword="false"/>.
    /// </param>
    /// <param name="args">
    /// Optional kind-specific argument bag. Keys vary by kind:
    /// <c>"key"</c> for key-* kinds, <c>"text"</c> for type_text, <c>"x"</c>/<c>"y"</c>
    /// for mouse_move. <see langword="null"/> means default (e.g. click at the
    /// element's center).
    /// </param>
    /// <param name="windowId">
    /// Phase 3.3: optional window-scoped lookup. <see langword="null"/> means
    /// "any window" (the Phase 3.1 behaviour).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> on success, <see langword="false"/> when the
    /// control couldn't be found or the kind isn't supported by this adapter.
    /// </returns>
    Task<bool> SimulateInputAsync(
        string rootName,
        string controlName,
        string kind,
        IReadOnlyDictionary<string, object?>? args,
        string? windowId,
        CancellationToken ct);

    /// <summary>
    /// Raise a named routed/bubbling event on the named control through the
    /// framework's event system (WPF <c>RoutedEvent</c>, Avalonia
    /// <c>RoutedEvent</c>, etc.). EventArgs are constructed from
    /// <paramref name="args"/> if provided, otherwise default. Bubbling /
    /// tunneling is honoured by the framework — handlers up and down the
    /// visual tree fire as if the event came from a real source.
    /// </summary>
    /// <param name="rootName">The <c>[McpRoot]</c>-relative root name; passed through as a disambiguation hint.</param>
    /// <param name="controlName">
    /// Either an <c>AutomationProperties.AutomationId</c> or an <c>x:Name</c>
    /// of a control inside a currently-open window.
    /// </param>
    /// <param name="eventName">
    /// The C# event name (e.g. <c>"Click"</c>) — adapters resolve it against
    /// the control's type, walking the base type chain so inherited events
    /// like <c>ButtonBase.Click</c> work for <c>Button</c>.
    /// </param>
    /// <param name="args">
    /// Optional EventArgs property bag. Phase 3.1 ships default-constructed
    /// args for routed events that take a parameterless constructor;
    /// kind-specific args may be honoured in later phases.
    /// </param>
    /// <param name="windowId">
    /// Phase 3.3: optional window-scoped lookup. <see langword="null"/> means
    /// "any window".
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the event was raised, <see langword="false"/>
    /// when the control or event name didn't resolve.
    /// </returns>
    Task<bool> RaiseEventAsync(
        string rootName,
        string controlName,
        string eventName,
        IReadOnlyDictionary<string, object?>? args,
        string? windowId,
        CancellationToken ct);

    // ---------------------------------------------------------------------
    // Phase 3.3 — multi-window routing
    // ---------------------------------------------------------------------

    /// <summary>
    /// Enumerate the live windowIds for a given root, in deterministic
    /// (oldest-first / monotonic-counter) order. An empty list means no
    /// instances are currently bound for that root (the runtime uses
    /// <see cref="Manifest.RegisteredRoot.Instance"/> in that case).
    /// </summary>
    /// <param name="rootName">The <c>[McpRoot]</c>-relative root name.</param>
    /// <returns>Window IDs ordered oldest-first.</returns>
    /// <remarks>
    /// Window IDs are short, stable strings such as <c>w1</c>, <c>w2</c>,
    /// <c>w3</c> ... — derived from a per-process monotonically-increasing
    /// counter. Each window-open allocates a fresh ID; closing a window does
    /// NOT free the ID for reuse. An identical-class reopen gets a new ID.
    /// </remarks>
    IReadOnlyList<string> GetWindowIds(string rootName);

    /// <summary>
    /// Look up the live root instance bound to a specific
    /// <paramref name="windowId"/> for <paramref name="rootName"/>.
    /// </summary>
    /// <param name="rootName">The root name.</param>
    /// <param name="windowId">
    /// Phase 3.3 window ID, or <see langword="null"/> to mean "first /
    /// default window" (the lowest live windowId — typically the
    /// originally-opened MainWindow).
    /// </param>
    /// <returns>
    /// The live instance, or <see langword="null"/> when no window with that
    /// ID exists.
    /// </returns>
    object? GetRootInstance(string rootName, string? windowId);

    /// <summary>
    /// Raised when the live windowId set for any root changes (window opened
    /// or closed). The runtime's <c>DynamicToolRegistry</c> subscribes so it
    /// can re-key per-window tool names and emit
    /// <c>notifications/tools/list_changed</c>.
    /// </summary>
    /// <remarks>
    /// Adapters MUST raise the mutation AFTER updating the internal registry
    /// (so subscribers see consistent state) and SHOULD coalesce rapid
    /// open/close bursts to avoid notification floods. The single-window
    /// case (one instance per root) does NOT raise — the bare-form tool
    /// names already cover it.
    /// </remarks>
    event EventHandler? WindowsChanged;
}
