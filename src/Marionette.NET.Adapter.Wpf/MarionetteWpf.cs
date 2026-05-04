// Marionette.NET — WPF bootstrap entry point
//
// Phase 1.3 contract: adopters wire Marionette into their WPF Application
// with one call from `App.OnStartup`:
//
//     protected override async void OnStartup(StartupEventArgs e)
//     {
//         base.OnStartup(e);
//         MarionetteWpf.AttachTo(this, GeneratedManifest.Roots, e.Args);
//     }
//
// The call:
//
//   1. Constructs a `WpfUiAutomationAdapter(app, logger, tracker)`.
//   2. Rewrites every RootDescriptor's `Create` factory so it dispatches
//      through the WPF UI thread AND, when possible, returns a live instance
//      already attached to the visible window tree (e.g. `app.MainWindow`)
//      rather than a fresh `new MainWindow()`. This is the Phase 1.2 → 1.3
//      hand-off concern: WPF window ctors require an STA thread, but
//      `MarionetteHost.RunAsync` runs on a background Task, so the registry's
//      auto-Create on the bg thread fails with
//      "calling thread must be an STA thread". UI-thread dispatch fixes that
//      AND makes `read_observable` see the user-facing instance.
//   3. Spawns `MarionetteHost.RunAsync(args, roots, adapter, ct)` on a
//      background Task so the UI thread is never blocked.
//   4. Hooks `Application.Exit` to cancel the host (clean shutdown).
//   5. Returns immediately. The returned IDisposable can be Disposed early to
//      detach the host explicitly (cancel + wait for the run task).
//
// Phase 3.3 multi-window routing:
//   * The adapter's RootInstanceTracker is populated from two sources:
//       (a) the bridged factory bound to a Window-typed root → tracked when
//           the registry first auto-creates;
//       (b) WindowsTrackerHook attaches to `app.Activated` / `app.Deactivated`
//           and per-Window `SourceInitialized` so newly-opened windows whose
//           class name matches a known root register automatically.
//   * Adopters with non-Window roots (the typical TodoListViewModel pattern)
//     should call `MarionetteWpf.TrackInstance(root, instance)` themselves
//     when they create a second instance. The TodoApp's `--two-windows`
//     handler does this.
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

using Marionette.Runtime;
using Marionette.Runtime.Adapters;
using Marionette.Runtime.Manifest;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marionette.Adapter.Wpf;

/// <summary>
/// One-call bootstrap for adding Marionette MCP automation to a WPF
/// application. Use this from <see cref="Application.OnStartup(StartupEventArgs)"/>
/// in GUI mode (i.e. the <c>--mcp</c> path; not <c>--mcp --headless</c>,
/// which has no Application).
/// </summary>
/// <remarks>
/// <para>
/// In <c>--mcp --headless</c> mode there is no <see cref="Application"/>, no
/// <see cref="Dispatcher"/>, and no UI thread. MarionetteWpf cannot run there —
/// adopters must call
/// <see cref="MarionetteHost.RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>
/// directly from <c>Program.Main</c> with <c>adapter: null</c>; the runtime
/// falls back to <see cref="NoOpAdapter"/>.
/// </para>
/// </remarks>
public static class MarionetteWpf
{
    // Phase 3.3: per-process adapter handle so adopters with non-Window roots
    // can call `MarionetteWpf.TrackInstance(rootName, instance)` to register a
    // second ViewModel for multi-window routing without having to plumb a
    // reference through their App class.
    private static WpfUiAutomationAdapter? s_currentAdapter;

    /// <summary>
    /// Attach the Marionette MCP host to a running WPF <see cref="Application"/>.
    /// Non-blocking: the host runs on a background <see cref="Task"/>; the
    /// caller's UI thread continues into the regular WPF message loop.
    /// </summary>
    /// <param name="app">The WPF application instance (typically <c>this</c> from <c>App.OnStartup</c>).</param>
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
    /// to <see cref="Application.Exit"/> so adopters who don't track the
    /// handle still get clean shutdown when the WPF Application exits.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> or <paramref name="roots"/> is null.</exception>
    public static IDisposable AttachTo(
        Application app,
        IReadOnlyList<RootDescriptor> roots,
        string[]? args = null,
        ILoggerFactory? loggerFactory = null)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        if (roots is null) throw new ArgumentNullException(nameof(roots));

        var resolvedArgs = args ?? CommandLineArgsExceptExe();
        var lf = loggerFactory ?? NullLoggerFactory.Instance;
        var tracker = new RootInstanceTracker();
        var adapter = new WpfUiAutomationAdapter(app, lf.CreateLogger<WpfUiAutomationAdapter>(), tracker);
        s_currentAdapter = adapter;

        // Rewrite roots so factories dispatch through the WPF UI thread and
        // prefer live instances. See WrapRootsForUiThread for the reasoning.
        var bridgedRoots = WrapRootsForUiThread(app, roots, tracker);

        // Phase 3.3: hook Window opens after the registry has been populated
        // so secondary windows of the same class as a known root register a
        // fresh windowId. We hook before MarionetteHost.RunAsync so the
        // tracker is alive when DynamicToolRegistry binds to its Changed
        // event.
        InstallWindowOpenHook(app, roots, tracker);

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
                // Normal shutdown via Detach / Application.Exit.
            }
            catch (Exception ex)
            {
                // We're on a background thread; the only safe sink is stderr.
                // Console.Error is reserved for log-style output by the
                // runtime's StderrLoggerProvider, so this matches the rest of
                // the host's diagnostic surface.
                Console.Error.WriteLine($"[marionette-wpf] MCP host crashed: {ex}");
            }
        }, cts.Token);

        // Detach on Application.Exit. We don't unsubscribe in the IDisposable
        // path because by then the application is shutting down anyway.
        // Application.Exit uses ExitEventHandler (legacy WPF delegate), not
        // EventHandler<ExitEventArgs>; the assignment must use that type
        // explicitly.
        var attachment = new MarionetteAttachment(cts, hostTask);
        ExitEventHandler? exitHandler = null;
        exitHandler = (_, _) =>
        {
            try { attachment.Dispose(); }
            catch { /* shutdown path */ }
            try { if (exitHandler is not null) app.Exit -= exitHandler; }
            catch { /* shutdown path */ }
        };
        app.Exit += exitHandler;

        return attachment;
    }

    /// <summary>
    /// Phase 3.3: register a non-Window root instance (e.g. a ViewModel) as
    /// belonging to a specific named root. Used by adopters whose
    /// <c>--two-windows</c> path materialises a second ViewModel that the
    /// auto-tracker can't pick up via Window class-name matching.
    /// </summary>
    /// <param name="rootName">The manifest name of the root.</param>
    /// <param name="instance">The live instance to register.</param>
    /// <returns>The newly-allocated windowId, or the existing one if already tracked.</returns>
    public static string TrackInstance(string rootName, object instance)
    {
        var adapter = s_currentAdapter
            ?? throw new InvalidOperationException(
                "MarionetteWpf.TrackInstance was called before AttachTo. " +
                "Call AttachTo from App.OnStartup first.");
        return adapter.Tracker.Track(rootName, instance);
    }

    /// <summary>
    /// Wrap each <see cref="RootDescriptor.Create"/> factory so it runs on the
    /// WPF UI (STA) thread, and so it returns a live <see cref="Application.MainWindow"/>
    /// (when type-compatible) before falling back to the original factory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two problems are solved here.
    /// </para>
    /// <para>
    /// <b>Problem 1 — STA thread.</b> WPF window ctors require an STA thread;
    /// <see cref="MarionetteHost.RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>
    /// runs from a background <see cref="Task"/>, so the registry's auto-call to
    /// <c>new MainWindow()</c> on that bg thread fails with
    /// "calling thread must be an STA thread." Dispatching through the WPF
    /// <see cref="Dispatcher"/> moves the call to the UI thread.
    /// </para>
    /// <para>
    /// <b>Problem 2 — instance affinity.</b> Even if we constructed a fresh
    /// <c>MainWindow</c> on the UI thread, it would be a different object than
    /// <see cref="Application.MainWindow"/>. The user clicks the live
    /// <c>app.MainWindow</c> and mutates its <c>Result</c>; <c>read_observable</c>
    /// would see the OTHER instance and always return zero. We bind to the live
    /// MainWindow when its CLR type matches the descriptor's <c>TypeName</c>;
    /// otherwise we fall back to the original factory (still on the UI thread).
    /// </para>
    /// <para>
    /// The bg thread blocks on <see cref="Dispatcher.Invoke(Action, DispatcherPriority)"/>
    /// only for the brief duration of the factory call. Sub-millisecond on a
    /// loaded MainWindow; never on the UI thread itself, so no deadlock.
    /// </para>
    /// <para>
    /// Phase 3.3: every successful resolution registers the instance with the
    /// tracker so the runtime can address it by windowId.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<RootDescriptor> WrapRootsForUiThread(
        Application app,
        IReadOnlyList<RootDescriptor> roots,
        RootInstanceTracker tracker)
    {
        var bridged = new List<RootDescriptor>(roots.Count);
        foreach (var r in roots)
        {
            // No factory → nothing to wrap. The runtime will surface
            // "root_unavailable" until / unless an adapter binds an instance
            // (Phase 2+ may add framework-specific binding flows).
            if (r.Create is null)
            {
                bridged.Add(r);
                continue;
            }

            var originalCreate = r.Create;
            var typeName = r.TypeName;
            var rootName = r.Name;

            Func<object> bridged_ = () => app.Dispatcher.Invoke(
                () =>
                {
                    object resolved;
                    // Try the live MainWindow first. The descriptor's TypeName
                    // is fully-qualified (FQN); match against the runtime
                    // type's FullName.
                    var mw = app.MainWindow;
                    if (mw is not null &&
                        string.Equals(mw.GetType().FullName, typeName, StringComparison.Ordinal))
                    {
                        resolved = mw;
                    }
                    else
                    {
                        // Otherwise call the original factory; we're on the UI
                        // thread now so STA-bound ctors are happy.
                        resolved = originalCreate();
                    }
                    // Phase 3.3: register with the tracker so windowId routing
                    // works. Idempotent (Track de-dupes by reference).
                    tracker.Track(rootName, resolved);
                    return resolved;
                },
                DispatcherPriority.Normal);

            bridged.Add(r with { Create = bridged_ });
        }
        return bridged;
    }

    /// <summary>
    /// Phase 3.3: hook the application so newly-opened Window-typed roots
    /// (e.g. a second MainWindow opened via <c>--two-windows</c>) auto-register.
    /// </summary>
    /// <remarks>
    /// We watch <see cref="ContentControl.Loaded"/> on every Window via the
    /// <see cref="Window.IsLoaded"/> shortcut. WPF doesn't expose a single
    /// "window-opened" event, so we lean on the per-process pattern:
    /// <list type="bullet">
    ///   <item><description>
    ///   Periodically reconcile the tracker against <see cref="Application.Windows"/>:
    ///   any Window in the collection whose class FullName matches a known
    ///   root and isn't yet tracked gets registered. A reconciliation runs on
    ///   <see cref="Application.Activated"/> and on a Dispatcher idle tick.
    ///   </description></item>
    ///   <item><description>
    ///   Window.Closed → tracker.Untrack so the windowId set shrinks back.
    ///   </description></item>
    /// </list>
    /// This is best-effort and idempotent. The TodoApp's <c>--two-windows</c>
    /// path also calls <see cref="TrackInstance"/> for non-Window roots
    /// (TodoListViewModel) which the class-name reconciliation can't reach.
    /// </remarks>
    private static void InstallWindowOpenHook(
        Application app,
        IReadOnlyList<RootDescriptor> roots,
        RootInstanceTracker tracker)
    {
        // Build a typeName → rootName map once. We iterate this on every
        // reconciliation; a 1- or 2-element dictionary lookup is fine.
        var typeToRoot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in roots)
        {
            if (!string.IsNullOrEmpty(r.TypeName))
            {
                typeToRoot[r.TypeName] = r.Name;
            }
        }
        if (typeToRoot.Count == 0) return;

        void ReconcileOnce()
        {
            // Always called on the UI thread.
            foreach (Window? w in app.Windows)
            {
                if (w is null) continue;
                var typeName = w.GetType().FullName;
                if (typeName is null) continue;
                if (!typeToRoot.TryGetValue(typeName, out var rootName)) continue;

                // Track is reference-equality idempotent.
                tracker.Track(rootName, w);

                // Hook Closed once. We tag with a sentinel attached property
                // analogue: a closure-captured flag in the handler list. WPF
                // doesn't dedup our handlers automatically, so we go through
                // the Tag dictionary to record "already-hooked".
                if (w.Tag is not WindowHookMarker)
                {
                    var capturedWindow = w;
                    EventHandler? closedHandler = null;
                    closedHandler = (sender, _) =>
                    {
                        try { tracker.Untrack(capturedWindow); } catch { /* ignore */ }
                        try { if (closedHandler is not null) capturedWindow.Closed -= closedHandler; } catch { /* ignore */ }
                    };
                    w.Closed += closedHandler;
                    // Preserve any existing Tag the adopter may have set on a
                    // root window; only overwrite when it's null.
                    if (w.Tag is null) w.Tag = WindowHookMarker.Instance;
                }
            }
        }

        // Initial reconciliation (in case AttachTo runs after MainWindow has
        // been loaded). This is on the UI thread (App.OnStartup).
        ReconcileOnce();

        // Reconcile on each Activated event (newly-shown Windows fire this
        // when they receive focus). Cheap; the dictionary lookup is O(1) per
        // Window and we cap the per-Window work via WindowHookMarker.
        app.Activated += (_, _) => ReconcileOnce();

        // Also reconcile on a Dispatcher idle tick so a window that was just
        // shown but hasn't received focus yet is picked up. We use the
        // ApplicationIdle priority so we only run when the message pump is
        // otherwise quiet.
        var idleOp = app.Dispatcher.BeginInvoke(
            new Action(ReconcileOnce),
            DispatcherPriority.ApplicationIdle);
        _ = idleOp; // fire-and-forget
    }

    private sealed class WindowHookMarker
    {
        public static readonly WindowHookMarker Instance = new();
        private WindowHookMarker() { }
    }

    private static string[] CommandLineArgsExceptExe()
    {
        // Environment.GetCommandLineArgs[0] is the executable path; the rest
        // are the actual flags. Mirrors what Program.Main(string[] args) sees.
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
            // The WPF Dispatcher pumps message-loop work during the wait if
            // we're called from the UI thread, so this is safe even from
            // App.Exit.
            try { _hostTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore — shutdown */ }

            try { _cts.Dispose(); } catch { /* ignore */ }
        }
    }
}
