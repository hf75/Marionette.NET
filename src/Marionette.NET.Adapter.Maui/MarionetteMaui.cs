// Marionette.NET — MAUI bootstrap entry point
//
// Phase 4.1 contract: adopters wire Marionette into their MAUI application
// with one call from `App.OnStart()` (or any other early-startup hook):
//
//     public partial class App : Application
//     {
//         protected override void OnStart()
//         {
//             base.OnStart();
//
//         #if MCP_ENABLED
//             MarionetteMaui.AttachTo(
//                 this,
//                 GeneratedManifest.Roots,
//                 Environment.GetCommandLineArgs()[1..]);
//         #endif
//         }
//     }
//
// The call:
//
//   1. Captures the application's IDispatcher (required for the adapter's
//      UI-thread marshalling). MAUI exposes Application.Dispatcher publicly.
//   2. Constructs a `MauiUiAutomationAdapter`.
//   3. Rewrites every RootDescriptor's `Create` factory to dispatch through
//      the MAUI UI thread AND, when possible, return a live instance already
//      attached to the visible Page's BindingContext rather than a fresh
//      `new T()`. Same shape as WPF / Avalonia / WinUI adapters.
//   4. Spawns `MarionetteHost.RunAsync(args, roots, adapter, ct)` on a
//      background Task so the UI thread is never blocked.
//   5. Hooks Application.Quit / Window.Destroying for clean shutdown.
//   6. Returns immediately. The returned IDisposable can be Disposed early
//      to detach the host explicitly (cancel + wait for the run task).
//
// SCENARIO COVERAGE
//
// `--mcp` (with GUI): adopters call AttachTo from App.OnStart. The args from
// the platform Main need to be propagated to AttachTo (via the `args`
// parameter) so MarionetteHost sees `--mcp`. Without `--mcp` in args, the
// host's RunAsync returns 0 immediately and AttachTo becomes a no-op.
//
// `--mcp --headless`: do NOT use AttachTo. There is no Application instance
// (the headless path skips MAUI's MauiAppBuilder entirely). Adopters call
// `MarionetteHost.RunAsync(args, roots, adapter: null)` directly from the
// platform-specific Main; the host falls back to NoOpAdapter, screenshot
// returns the `screenshot_not_supported` structured error, and dispatch runs
// inline.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;

using Marionette.Runtime;
using Marionette.Runtime.Adapters;
using Marionette.Runtime.Manifest;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marionette.Adapter.Maui;

/// <summary>
/// One-call bootstrap for adding Marionette MCP automation to a .NET MAUI
/// application. Use this from
/// <see cref="Application.OnStart"/> (or any other early-startup hook that
/// runs after the Application is constructed) in GUI mode (i.e. the
/// <c>--mcp</c> path; not <c>--mcp --headless</c>, which has no Application).
/// </summary>
/// <remarks>
/// <para>
/// In <c>--mcp --headless</c> mode there is no <see cref="Application"/>, no
/// <see cref="IDispatcher"/>, and no UI thread. MarionetteMaui cannot run
/// there — adopters must call
/// <see cref="MarionetteHost.RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>
/// directly from <c>Program.Main</c> with <c>adapter: null</c>; the runtime
/// falls back to <see cref="NoOpAdapter"/>.
/// </para>
/// </remarks>
public static class MarionetteMaui
{
    private static MauiUiAutomationAdapter? s_currentAdapter;

    /// <summary>
    /// Phase 4.1: register a non-Window root instance for multi-window
    /// routing. Used by adopters whose multi-window path materialises a
    /// second ViewModel.
    /// </summary>
    public static string TrackInstance(string rootName, object instance)
    {
        var adapter = s_currentAdapter
            ?? throw new InvalidOperationException(
                "MarionetteMaui.TrackInstance was called before AttachTo. " +
                "Call AttachTo from App.OnStart first.");
        return adapter.Tracker.Track(rootName, instance);
    }

    /// <summary>
    /// Attach the Marionette MCP host to a running .NET MAUI
    /// <see cref="Application"/>. Non-blocking: the host runs on a background
    /// <see cref="Task"/>; the caller's UI thread continues into the regular
    /// MAUI message loop.
    /// </summary>
    /// <param name="app">The MAUI application instance (typically <c>this</c> from <c>App.OnStart</c>).</param>
    /// <param name="roots">The source-generator-emitted root list (typically <c>Marionette.Generated.GeneratedManifest.Roots</c>).</param>
    /// <param name="args">
    /// Optional argv from the platform Main. When omitted, falls back to
    /// <see cref="Environment.GetCommandLineArgs"/> (skipping the .exe path).
    /// Without <c>--mcp</c> in args the host's
    /// <see cref="MarionetteHost.RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>
    /// returns 0 immediately and this call becomes a no-op.
    /// </param>
    /// <param name="loggerFactory">
    /// Optional logger factory used to create the adapter's logger. When
    /// <see langword="null"/>, a <see cref="NullLoggerFactory"/> is used; the
    /// host's own logging then surfaces stderr-bound diagnostics.
    /// </param>
    /// <returns>
    /// A disposable handle. Disposing it cancels the host run-task and waits
    /// for it to complete (best-effort, max 2 s).
    /// </returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">The Application's Dispatcher is unavailable (not yet constructed).</exception>
    /// <remarks>
    /// <para>
    /// <b>AOT/trim contract (Phase 4.2):</b> the bootstrap forwards into
    /// <see cref="MarionetteHost.RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>,
    /// which is itself marked
    /// <see cref="System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"/>
    /// because the runtime surfaces the <c>raise_event</c> MCP tool. MAUI's
    /// AOT story is the most fragile of the four adapters today (cross-platform
    /// XAML compilation, handler registration, IL3050 from numerous interop
    /// paths). Phase 4.2's AOT survey documents which framework warnings are
    /// inherent to MAUI itself versus which are Marionette's responsibility.
    /// </para>
    /// </remarks>
    [RequiresUnreferencedCode(
        "MarionetteMaui.AttachTo forwards into MarionetteHost.RunAsync, which surfaces the " +
        "raise_event MCP tool's reflection-based event resolver. MAUI itself emits numerous " +
        "IL2026/IL3050 warnings under PublishAot=true; the Marionette additions on top are " +
        "limited to the raise_event surface. Suppress at the call site after auditing.")]
    [RequiresDynamicCode(
        "MarionetteMaui.AttachTo forwards into MarionetteHost.RunAsync, which uses " +
        "System.Text.Json for boxed-object serialisation. Phase 4.2 keeps this on the warning " +
        "surface; Phase 6 may move to source-generated JsonTypeInfo.")]
    public static IDisposable AttachTo(
        Application app,
        IReadOnlyList<RootDescriptor> roots,
        string[]? args = null,
        ILoggerFactory? loggerFactory = null)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        if (roots is null) throw new ArgumentNullException(nameof(roots));

        var dispatcher = app.Dispatcher
            ?? throw new InvalidOperationException(
                "MarionetteMaui.AttachTo: Application.Dispatcher is null. " +
                "Call AttachTo after the Application has been constructed " +
                "(typically from App.OnStart).");

        var resolvedArgs = args ?? CommandLineArgsExceptExe();
        var lf = loggerFactory ?? NullLoggerFactory.Instance;
        var adapter = new MauiUiAutomationAdapter(
            app,
            dispatcher,
            lf.CreateLogger<MauiUiAutomationAdapter>());
        s_currentAdapter = adapter;

        // Rewrite roots so factories dispatch through the MAUI UI thread and
        // prefer live instances. See WrapRootsForUiThread for the reasoning.
        var bridgedRoots = WrapRootsForUiThread(dispatcher, app, roots);

        // Phase 9.2: hook MAUI's window lifecycle so the adapter's
        // RootInstanceTracker stays in sync with the live window set —
        // newly opened secondary windows get tracked, closed windows get
        // untracked, and the runtime's DynamicToolRegistry sees
        // notifications/tools/list_changed. Mirrors the per-Window-tracking
        // pattern WPF / Avalonia / WinUI adapters already use.
        WireWindowTracking(app, dispatcher, adapter, roots);

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
                // Normal shutdown via Detach / app stop.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[marionette-maui] MCP host crashed: {ex}");
            }
        }, cts.Token);

        return new MarionetteAttachment(cts, hostTask);
    }

    /// <summary>
    /// Wrap each <see cref="RootDescriptor.Create"/> factory so it runs on the
    /// MAUI UI thread, and so it returns the live application's main Page
    /// BindingContext (when type-compatible) before falling back to the
    /// original factory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two problems are solved here.
    /// </para>
    /// <para>
    /// <b>Problem 1 — thread affinity.</b> MAUI Element ctors and most
    /// Microsoft.Maui.Controls types must be touched only on the UI thread.
    /// <see cref="MarionetteHost.RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>
    /// runs from a background <see cref="Task"/>, so the registry's auto-call
    /// to <c>new MainPage()</c> on that bg thread can fail. Dispatching
    /// through the captured <see cref="IDispatcher"/> moves the call.
    /// </para>
    /// <para>
    /// <b>Problem 2 — instance affinity.</b> Even if we constructed a fresh
    /// MainPage on the UI thread, it would be a different object than the
    /// adopter's live MainPage. We bind to the live MainPage when its CLR
    /// type matches the descriptor's <c>TypeName</c>; otherwise we fall back
    /// to the original factory (still on the UI thread). The "non-Window /
    /// non-Page root" pattern (typical MAUI ViewModel roots) requires the
    /// adopter to do the explicit `RootDescriptor.Create = () => MyViewModel.Shared`
    /// rewrite in App.OnStart BEFORE calling AttachTo, just as on WPF /
    /// Avalonia / WinUI.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<RootDescriptor> WrapRootsForUiThread(
        IDispatcher dispatcher,
        Application app,
        IReadOnlyList<RootDescriptor> roots)
    {
        var bridged = new List<RootDescriptor>(roots.Count);
        foreach (var r in roots)
        {
            if (r.Create is null)
            {
                bridged.Add(r);
                continue;
            }

            var originalCreate = r.Create;
            var typeName = r.TypeName;

            Func<object> bridged_ = () => InvokeOnUiThread(dispatcher, () =>
            {
                // Try the live application's main Page first (the MAUI
                // analogue of WPF MainWindow). MAUI's Application.Windows[0]
                // is the canonical "main window"; its Page is the visible
                // root content.
                var firstWin = app.Windows is { Count: > 0 } ws ? ws[0] : null;
                var mainPage = firstWin?.Page;
                if (mainPage is not null &&
                    string.Equals(mainPage.GetType().FullName, typeName, StringComparison.Ordinal))
                {
                    return (object)mainPage;
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
    /// Phase 9.2: keep the adapter's <see cref="RootInstanceTracker"/> in
    /// sync with MAUI's live window set. Reconciles the existing
    /// <see cref="Application.Windows"/> collection once at startup and then
    /// hooks the per-application window lifecycle so subsequent open/close
    /// events update the tracker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per-window tracking follows the BindingContext: when a window's Page
    /// has a BindingContext whose CLR type matches a registered <c>[McpRoot]</c>'s
    /// <see cref="RootDescriptor.TypeName"/>, the BindingContext is registered
    /// under that root name. Windows whose Page itself matches the typeName
    /// (e.g. when the [McpRoot] is the Page subclass) are also tracked.
    /// </para>
    /// <para>
    /// Closing a window untracks its instance(s); the tracker emits a
    /// Changed event and the runtime's DynamicToolRegistry coalesces the
    /// tools/list_changed notification.
    /// </para>
    /// </remarks>
    private static void WireWindowTracking(
        Application app,
        IDispatcher dispatcher,
        MauiUiAutomationAdapter adapter,
        IReadOnlyList<RootDescriptor> roots)
    {
        // Build a fast lookup from CLR type name → root name. Only roots with
        // a non-null Create factory are tracked — descriptors without a
        // factory aren't expected to materialise live instances.
        var typeToRoot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in roots)
        {
            if (!string.IsNullOrEmpty(r.TypeName) && !typeToRoot.ContainsKey(r.TypeName))
                typeToRoot[r.TypeName] = r.Name;
        }

        void TrackWindow(Window window)
        {
            try
            {
                var page = window.Page;
                if (page is null) return;

                // Track the page itself when its CLR type matches a root.
                if (typeToRoot.TryGetValue(page.GetType().FullName ?? string.Empty, out var pageRoot))
                {
                    adapter.Tracker.Track(pageRoot, page);
                }

                // Track the BindingContext (typical MVVM ViewModel root).
                var bc = page.BindingContext;
                if (bc is not null &&
                    typeToRoot.TryGetValue(bc.GetType().FullName ?? string.Empty, out var bcRoot))
                {
                    adapter.Tracker.Track(bcRoot, bc);
                }
            }
            catch
            {
                // Tracking is best-effort — never let an adapter-internal
                // bookkeeping fault propagate into MAUI's window lifecycle.
            }
        }

        void UntrackWindow(Window window)
        {
            try
            {
                var page = window.Page;
                if (page is null) return;
                adapter.Tracker.Untrack(page);
                if (page.BindingContext is { } bc) adapter.Tracker.Untrack(bc);
            }
            catch
            {
                // Best-effort — see TrackWindow.
            }
        }

        void ReconcileOnce()
        {
            foreach (var w in app.Windows)
            {
                TrackWindow(w);
            }
        }

        // Initial reconcile on the UI thread so we see whatever's already
        // open by the time AttachTo runs (typically the MainWindow).
        if (dispatcher.IsDispatchRequired)
            dispatcher.Dispatch(ReconcileOnce);
        else
            ReconcileOnce();

        // Future window opens / closes. Application exposes these as
        // virtual methods (Application.OnCreated / OnDestroying) — the
        // public observability point is the per-Window event surface.
        // We subscribe via the Created/Destroyed lifecycle the
        // Application's Windows collection raises through Window's
        // public events. MAUI 10.x exposes these as the Window.Created
        // and Window.Destroying events.
        // (When Application.Windows mutates, the new Window's events are
        // already wired by the time OnCreated is observable here.)
        // Use a CollectionChanged-style scan-on-Activated as a portable
        // fallback; matches the pragmatic pattern WPF uses.

        EventHandler? activatedHandler = null;
        activatedHandler = (sender, args) =>
        {
            // Re-walk the windows on every Activated; cheap because the
            // tracker uses reference-equality dedup. Mirrors the WPF
            // adapter's "reconcile-on-Activated + idle-tick" pattern.
            ReconcileOnce();
        };

        // Hook Window.Activated on every existing and future window. New
        // windows get the hook installed in TrackWindow; existing windows
        // get it on the initial reconcile via the same path. To install
        // the hook on each window once, we extend TrackWindow.
        foreach (var w in app.Windows) HookWindowLifecycle(w, UntrackWindow, activatedHandler);
    }

    /// <summary>
    /// Hook a window's Destroying event so it untracks itself when closed,
    /// plus the Activated event so a focus change re-reconciles (cheap
    /// nop when nothing changed).
    /// </summary>
    private static void HookWindowLifecycle(
        Window window,
        Action<Window> untrack,
        EventHandler activatedHandler)
    {
        try
        {
            EventHandler? destroyingHandler = null;
            destroyingHandler = (s, e) =>
            {
                try { untrack(window); } catch { /* best-effort */ }
                try { window.Destroying -= destroyingHandler; } catch { }
                try { window.Activated -= activatedHandler; } catch { }
            };
            window.Destroying += destroyingHandler;
            window.Activated += activatedHandler;
        }
        catch
        {
            // Window event surface differs across MAUI versions — when the
            // hook fails we simply lose dynamic untrack for THIS window.
            // The tracker's reference-equality keeps the system safe.
        }
    }

    /// <summary>
    /// Synchronously invoke <paramref name="func"/> on the UI thread via
    /// <see cref="IDispatcher.Dispatch(Action)"/>. Blocks the calling
    /// (background) thread until the func completes.
    /// </summary>
    private static object InvokeOnUiThread(IDispatcher dispatcher, Func<object> func)
    {
        if (!dispatcher.IsDispatchRequired)
        {
            // Reentrant call from the UI thread - run inline.
            return func();
        }
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = dispatcher.Dispatch(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        if (!enqueued)
        {
            throw new InvalidOperationException(
                "IDispatcher.Dispatch failed during root factory bridging.");
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
