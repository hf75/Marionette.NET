// Marionette.NET — WinUI bootstrap entry point
//
// Phase 3.2 contract: adopters wire Marionette into their WinUI 3 application
// with one call from `App.OnLaunched(LaunchActivatedEventArgs args)`:
//
//     protected override void OnLaunched(LaunchActivatedEventArgs args)
//     {
//         _mainWindow = new MainWindow();
//         _mainWindow.Activate();
//
//     #if MCP_ENABLED
//         MarionetteWinUI.AttachTo(
//             this,
//             _mainWindow,
//             GeneratedManifest.Roots,
//             Environment.GetCommandLineArgs()[1..]);
//     #endif
//     }
//
// The call:
//
//   1. Captures the current thread's DispatcherQueue (required for the adapter's
//      UI-thread marshalling). Adopter MUST call from the UI thread (typical
//      App.OnLaunched runs there).
//   2. Tracks the supplied MainWindow via WindowTracker so VisualTreeFinder can
//      enumerate it. Adopters with secondary windows can call WindowTracker.Track
//      directly for each.
//   3. Constructs a `WinUiAutomationAdapter`.
//   4. Rewrites every RootDescriptor's `Create` factory to dispatch through the
//      WinUI UI thread AND, when possible, return a live instance already
//      attached to the visible window's Content/DataContext rather than a fresh
//      `new T()`. Same shape as WPF/Avalonia adapters.
//   5. Spawns `MarionetteHost.RunAsync(args, roots, adapter, ct)` on a background
//      Task so the UI thread is never blocked.
//   6. Hooks the supplied window's `Closed` event to cancel the host (clean
//      shutdown). Adopters with a multi-window app may prefer to dispose the
//      handle explicitly; the close-hook fires per-window so the first close
//      currently shuts down MCP. (Multi-window-routing is Phase 3.3.)
//   7. Returns immediately. The returned IDisposable can be Disposed early to
//      detach the host explicitly (cancel + wait for the run task).
//
// SCENARIO COVERAGE
//
// `--mcp` (with GUI): adopters call AttachTo. The args from Program.Main need
// to be propagated to AttachTo (via the `args` parameter) so MarionetteHost
// sees `--mcp`. Without `--mcp` in args, the host's RunAsync returns 0
// immediately and AttachTo becomes a no-op.
//
// `--mcp --headless`: do NOT use AttachTo. There is no Application instance
// (the headless path skips `App` entirely). Adopters call
// `MarionetteHost.RunAsync(args, roots, adapter: null)` directly from
// `Program.Main`; the host falls back to NoOpAdapter, screenshot returns the
// `screenshot_not_supported` structured error, and dispatch runs inline.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

using Marionette.Adapter.WinUI.Internal;
using Marionette.Runtime;
using Marionette.Runtime.Adapters;
using Marionette.Runtime.Manifest;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Windows.Foundation;

namespace Marionette.Adapter.WinUI;

/// <summary>
/// One-call bootstrap for adding Marionette MCP automation to a WinUI 3
/// application. Use this from <see cref="Application.OnLaunched(LaunchActivatedEventArgs)"/>
/// in GUI mode (i.e. the <c>--mcp</c> path; not <c>--mcp --headless</c>,
/// which has no Application).
/// </summary>
/// <remarks>
/// <para>
/// In <c>--mcp --headless</c> mode there is no <see cref="Application"/>, no
/// <see cref="DispatcherQueue"/>, and no UI thread. MarionetteWinUI cannot run
/// there — adopters must call
/// <see cref="MarionetteHost.RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>
/// directly from <c>Program.Main</c> with <c>adapter: null</c>; the runtime
/// falls back to <see cref="NoOpAdapter"/>.
/// </para>
/// </remarks>
public static class MarionetteWinUI
{
    private static WinUiAutomationAdapter? s_currentAdapter;

    /// <summary>
    /// Phase 3.3: register a non-Window root instance for multi-window
    /// routing. Used by adopters whose multi-window path materialises a
    /// second ViewModel.
    /// </summary>
    public static string TrackInstance(string rootName, object instance)
    {
        var adapter = s_currentAdapter
            ?? throw new InvalidOperationException(
                "MarionetteWinUI.TrackInstance was called before AttachTo. " +
                "Call AttachTo from App.OnLaunched first.");
        return adapter.Tracker.Track(rootName, instance);
    }

    /// <summary>
    /// Attach the Marionette MCP host to a running WinUI 3 <see cref="Application"/>.
    /// Non-blocking: the host runs on a background <see cref="Task"/>; the
    /// caller's UI thread continues into the regular WinUI message loop.
    /// </summary>
    /// <param name="app">The WinUI application instance (typically <c>this</c> from <c>App.OnLaunched</c>).</param>
    /// <param name="mainWindow">
    /// The application's main <see cref="Window"/>. Tracked for visual-tree
    /// walking and screenshots; its <see cref="Window.Closed"/> event triggers
    /// host shutdown. Adopters with secondary windows can call
    /// <c>WindowTracker.Track</c> directly for each.
    /// </param>
    /// <param name="roots">The source-generator-emitted root list (typically <c>Marionette.Generated.GeneratedManifest.Roots</c>).</param>
    /// <param name="args">
    /// Optional argv from <c>Program.Main</c>. When omitted, falls back to
    /// <see cref="Environment.GetCommandLineArgs"/> (skipping the .exe path).
    /// Without <c>--mcp</c> in args the host's <see cref="MarionetteHost.RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>
    /// returns 0 immediately and this call becomes a no-op.
    /// </param>
    /// <param name="loggerFactory">
    /// Optional logger factory used to create the adapter's logger. When
    /// <see langword="null"/>, a <see cref="NullLoggerFactory"/> is used; the
    /// host's own logging then surfaces stderr-bound diagnostics.
    /// </param>
    /// <returns>
    /// A disposable handle. Disposing it cancels the host run-task and waits
    /// for it to complete (best-effort, max 2 s). Disposal is also auto-wired
    /// to <paramref name="mainWindow"/>'s <see cref="Window.Closed"/> event.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">Called from a non-UI thread (no DispatcherQueue available).</exception>
    /// <remarks>
    /// <para>
    /// <b>AOT/trim contract (Phase 4.2):</b> the bootstrap forwards into
    /// <see cref="MarionetteHost.RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>,
    /// which is itself marked
    /// <see cref="System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"/>
    /// because the runtime surfaces the <c>raise_event</c> MCP tool. WinUI's
    /// raiser is <b>more fragile</b> than WPF/Avalonia's because it reflects on
    /// compiler-emitted backing delegate fields rather than canonical static
    /// <c>RoutedEvent</c> identifiers — adopters who AOT-publish should plan
    /// to use <c>simulate_input</c> (AutomationPeer-based, reflection-free) or
    /// <c>[McpCallable]</c> shims for any UI flow they care about.
    /// </para>
    /// </remarks>
    [RequiresUnreferencedCode(
        "MarionetteWinUI.AttachTo forwards into MarionetteHost.RunAsync, which surfaces the " +
        "raise_event MCP tool's reflection-based event resolver. WinUI 3's CLR-event surface " +
        "makes the raiser especially trim-fragile; prefer simulate_input + [McpCallable] for " +
        "AOT-published WinUI apps.")]
    [RequiresDynamicCode(
        "MarionetteWinUI.AttachTo forwards into MarionetteHost.RunAsync, which uses " +
        "System.Text.Json for boxed-object serialisation. Phase 4.2 keeps this on the warning " +
        "surface; Phase 6 may move to source-generated JsonTypeInfo.")]
    public static IDisposable AttachTo(
        Application app,
        Window mainWindow,
        IReadOnlyList<RootDescriptor> roots,
        string[]? args = null,
        ILoggerFactory? loggerFactory = null)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        if (mainWindow is null) throw new ArgumentNullException(nameof(mainWindow));
        if (roots is null) throw new ArgumentNullException(nameof(roots));

        // DispatcherQueue.GetForCurrentThread requires us to be on the UI
        // thread. App.OnLaunched runs there in WinUI; if the adopter calls us
        // from elsewhere they need to marshal first.
        var dq = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "MarionetteWinUI.AttachTo must be called on the UI thread (typically from App.OnLaunched). " +
                "DispatcherQueue.GetForCurrentThread returned null.");

        WindowTracker.Track(mainWindow);

        var resolvedArgs = args ?? CommandLineArgsExceptExe();
        var lf = loggerFactory ?? NullLoggerFactory.Instance;
        var adapter = new WinUiAutomationAdapter(
            app,
            dq,
            lf.CreateLogger<WinUiAutomationAdapter>());
        s_currentAdapter = adapter;

        // Rewrite roots so factories dispatch through the WinUI UI thread and
        // prefer live instances. See WrapRootsForUiThread for the reasoning.
        var bridgedRoots = WrapRootsForUiThread(dq, mainWindow, roots);

        var cts = new CancellationTokenSource();
        var hostTask = Task.Run(async () =>
        {
            try
            {
                await MarionetteHost.RunAsync(
                    resolvedArgs,
                    bridgedRoots,
                    adapter: adapter,
                    ct: cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Normal shutdown via Detach / Window.Closed.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[marionette-winui] MCP host crashed: {ex}");
            }
        }, cts.Token);

        // Detach on Window.Closed. WinUI's Window.Closed is the canonical
        // shutdown hook (mirrors Application.Exit on WPF and Lifetime.Exit on
        // Avalonia). Adopters with multiple windows whose first-close should
        // NOT shut down MCP can dispose the handle manually instead.
        var attachment = new MarionetteAttachment(cts, hostTask);
        TypedEventHandler<object, WindowEventArgs>? closedHandler = null;
        closedHandler = (_, _) =>
        {
            try { attachment.Dispose(); }
            catch { /* shutdown path */ }
            try { if (closedHandler is not null) mainWindow.Closed -= closedHandler; }
            catch { /* shutdown path */ }
        };
        mainWindow.Closed += closedHandler;

        return attachment;
    }

    /// <summary>
    /// Wrap each <see cref="RootDescriptor.Create"/> factory so it runs on the
    /// WinUI UI thread, and so it returns the live <paramref name="mainWindow"/>
    /// (when type-compatible) before falling back to the original factory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two problems are solved here.
    /// </para>
    /// <para>
    /// <b>Problem 1 — thread affinity.</b> WinUI Window ctors and most
    /// Microsoft.UI.Xaml types must be touched only on the UI thread.
    /// <see cref="MarionetteHost.RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>
    /// runs from a background <see cref="Task"/>, so the registry's auto-call
    /// to <c>new MainWindow()</c> on that bg thread can fail. Dispatching
    /// through the captured <see cref="DispatcherQueue"/> moves the call.
    /// </para>
    /// <para>
    /// <b>Problem 2 — instance affinity.</b> Even if we constructed a fresh
    /// MainWindow on the UI thread, it would be a different object than the
    /// adopter's <paramref name="mainWindow"/>. We bind to the live MainWindow
    /// when its CLR type matches the descriptor's <c>TypeName</c>; otherwise
    /// we fall back to the original factory (still on the UI thread). The
    /// "non-Window root" pattern (typical WinUI ViewModel roots) requires the
    /// adopter to do the explicit `RootDescriptor.Create = () => MyViewModel.Shared`
    /// rewrite in App.OnLaunched BEFORE calling AttachTo, just as on WPF and
    /// Avalonia.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<RootDescriptor> WrapRootsForUiThread(
        DispatcherQueue dq,
        Window mainWindow,
        IReadOnlyList<RootDescriptor> roots)
    {
        var bridged = new List<RootDescriptor>(roots.Count);
        foreach (var r in roots)
        {
            // No factory -> nothing to wrap. The runtime will surface
            // "root_unavailable" until / unless an adapter binds an instance.
            if (r.Create is null)
            {
                bridged.Add(r);
                continue;
            }

            var originalCreate = r.Create;
            var typeName = r.TypeName;

            Func<object> bridged_ = () => InvokeOnUiThread(dq, () =>
            {
                // Try the live MainWindow first. The descriptor's TypeName is
                // fully-qualified (FQN); match against the runtime type's
                // FullName.
                if (string.Equals(mainWindow.GetType().FullName, typeName, StringComparison.Ordinal))
                {
                    return (object)mainWindow;
                }

                // Otherwise call the original factory; we're on the UI thread
                // now so thread-affine ctors are happy.
                return originalCreate();
            });

            bridged.Add(r with { Create = bridged_ });
        }
        return bridged;
    }

    /// <summary>
    /// Synchronously invoke <paramref name="func"/> on the UI thread via
    /// <see cref="DispatcherQueue.TryEnqueue(DispatcherQueueHandler)"/>. Blocks
    /// the calling (background) thread until the func completes.
    /// </summary>
    private static object InvokeOnUiThread(DispatcherQueue dq, Func<object> func)
    {
        if (dq.HasThreadAccess)
        {
            // Reentrant call from the UI thread - run inline. Bridges that the
            // adopter rewrote with `() => MyViewModel.Shared` (no new T()) hit
            // this path on the GUI startup pre-warm.
            return func();
        }
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = dq.TryEnqueue(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        if (!enqueued)
        {
            throw new InvalidOperationException(
                "DispatcherQueue.TryEnqueue failed during root factory bridging.");
        }
        return tcs.Task.GetAwaiter().GetResult();
    }

    private static string[] CommandLineArgsExceptExe()
    {
        var cli = Environment.GetCommandLineArgs();
        if (cli.Length <= 1) return Array.Empty<string>();
        var rest = new string[cli.Length - 1];
        Array.Copy(cli, 1, rest, 0, rest.Length);
        return rest;
    }

    private sealed class MarionetteAttachment : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _hostTask;
        private int _disposed;

        public MarionetteAttachment(CancellationTokenSource cts, Task hostTask)
        {
            _cts = cts;
            _hostTask = hostTask;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try { _cts.Cancel(); } catch { /* ignore */ }

            // Best-effort wait; never block the UI thread for more than 2s.
            try { _hostTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }

            try { _cts.Dispose(); } catch { /* ignore */ }
        }
    }
}
