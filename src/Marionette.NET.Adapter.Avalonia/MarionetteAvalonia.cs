// Marionette.NET - Avalonia bootstrap entry point
//
// Phase 2.1 contract: adopters wire Marionette into their Avalonia Application
// with one call from `App.OnFrameworkInitializationCompleted`:
//
//     public override void OnFrameworkInitializationCompleted()
//     {
//         if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
//         {
//             desktop.MainWindow = new MainWindow();
//         }
//         base.OnFrameworkInitializationCompleted();
//
//         #if MCP_ENABLED
//         MarionetteAvalonia.AttachTo(this, GeneratedManifest.Roots, args);
//         #endif
//     }
//
// The call:
//
//   1. Constructs an `AvaloniaUiAutomationAdapter(app, logger)`.
//   2. Rewrites every RootDescriptor's `Create` factory so it dispatches
//      through the Avalonia UI thread AND, when possible, returns a live
//      instance already attached to the visible window tree (e.g. the desktop
//      lifetime's `MainWindow`) rather than a fresh `new T()`. This is the
//      Avalonia analogue of the Phase 1.3 WPF rewrite. Two problems solved:
//      thread-affinity (Avalonia Window ctors only work on the UI thread) and
//      instance-affinity (the UI binds one DataContext but the runtime
//      registry would otherwise see a different instance).
//   3. Spawns `MarionetteHost.RunAsync(args, roots, adapter, ct)` on a
//      background Task so the UI thread is never blocked.
//   4. Hooks the desktop lifetime's `Exit` event to cancel the host (clean
//      shutdown). On non-classic-desktop lifetimes (mobile, single-view) we
//      skip the hook - the `IDisposable` returned remains the adopter's only
//      shutdown path.
//   5. Returns immediately. The returned IDisposable can be Disposed early to
//      detach the host explicitly (cancel + wait for the run task).
//
// SCENARIO COVERAGE
//
// `--mcp` (with GUI): adopters call AttachTo. The args from Program.Main need
// to be propagated to AttachTo (via the `args` parameter) so MarionetteHost
// sees `--mcp`. Without `--mcp` in args, the host's RunAsync returns 0
// immediately and AttachTo becomes a no-op.
//
// `--mcp --headless`: do NOT use AttachTo. There is no Avalonia application
// (the headless path skips the AppBuilder entirely). Adopters call
// `MarionetteHost.RunAsync(args, roots, adapter: null)` directly from
// `Program.Main`; the host falls back to NoOpAdapter, screenshot returns the
// `screenshot_not_supported` structured error, and dispatch runs inline.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

using Marionette.Runtime;
using Marionette.Runtime.Adapters;
using Marionette.Runtime.Manifest;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marionette.Adapter.Avalonia;

/// <summary>
/// One-call bootstrap for adding Marionette MCP automation to an Avalonia
/// application. Use this from
/// <see cref="global::Avalonia.Application.OnFrameworkInitializationCompleted"/>
/// in GUI mode (i.e. the <c>--mcp</c> path; not <c>--mcp --headless</c>,
/// which has no Avalonia Application).
/// </summary>
/// <remarks>
/// <para>
/// In <c>--mcp --headless</c> mode there is no <see cref="global::Avalonia.Application"/>,
/// no <see cref="Dispatcher"/>, and no UI thread. MarionetteAvalonia cannot run there -
/// adopters must call
/// <see cref="MarionetteHost.RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>
/// directly from <c>Program.Main</c> with <c>adapter: null</c>; the runtime
/// falls back to <see cref="NoOpAdapter"/>.
/// </para>
/// </remarks>
public static class MarionetteAvalonia
{
    /// <summary>
    /// Attach the Marionette MCP host to a running Avalonia <see cref="global::Avalonia.Application"/>.
    /// Non-blocking: the host runs on a background <see cref="Task"/>; the
    /// caller's UI thread continues into the regular Avalonia message loop.
    /// </summary>
    /// <param name="app">The Avalonia application instance (typically <c>this</c> from <c>App.OnFrameworkInitializationCompleted</c>).</param>
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
    /// to the desktop lifetime's <see cref="IClassicDesktopStyleApplicationLifetime.Exit"/>
    /// event so adopters who don't track the handle still get clean shutdown
    /// when the Avalonia Application exits.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> or <paramref name="roots"/> is null.</exception>
    public static IDisposable AttachTo(
        global::Avalonia.Application app,
        IReadOnlyList<RootDescriptor> roots,
        string[]? args = null,
        ILoggerFactory? loggerFactory = null)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        if (roots is null) throw new ArgumentNullException(nameof(roots));

        var resolvedArgs = args ?? CommandLineArgsExceptExe();
        var lf = loggerFactory ?? NullLoggerFactory.Instance;
        var adapter = new AvaloniaUiAutomationAdapter(
            app,
            lf.CreateLogger<AvaloniaUiAutomationAdapter>());

        // Rewrite roots so factories dispatch through the Avalonia UI thread
        // and prefer the live MainWindow when its CLR FullName matches the
        // descriptor's TypeName. See WrapRootsForUiThread for the reasoning.
        var bridgedRoots = WrapRootsForUiThread(app, roots);

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
                Console.Error.WriteLine($"[marionette-avalonia] MCP host crashed: {ex}");
            }
        }, cts.Token);

        // Detach on Avalonia desktop lifetime Exit. Mobile / single-view
        // lifetimes don't have an Exit event; adopters there must Dispose the
        // handle manually.
        var attachment = new MarionetteAttachment(cts, hostTask);
        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            EventHandler<ControlledApplicationLifetimeExitEventArgs>? exitHandler = null;
            exitHandler = (_, _) =>
            {
                try { attachment.Dispose(); }
                catch { /* shutdown path */ }
                try { if (exitHandler is not null) desktop.Exit -= exitHandler; }
                catch { /* shutdown path */ }
            };
            desktop.Exit += exitHandler;
        }

        return attachment;
    }

    /// <summary>
    /// Wrap each <see cref="RootDescriptor.Create"/> factory so it runs on the
    /// Avalonia UI thread, and so it returns a live
    /// <see cref="IClassicDesktopStyleApplicationLifetime.MainWindow"/> (when
    /// type-compatible) before falling back to the original factory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two problems are solved here.
    /// </para>
    /// <para>
    /// <b>Problem 1 - thread affinity.</b> Avalonia Window ctors and many
    /// AvaloniaProperty operations require the UI thread;
    /// <see cref="MarionetteHost.RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>
    /// runs from a background <see cref="Task"/>, so the registry's auto-call
    /// to <c>new MainWindow()</c> on that bg thread can fail. Dispatching
    /// through <see cref="Dispatcher.UIThread"/> moves the call.
    /// </para>
    /// <para>
    /// <b>Problem 2 - instance affinity.</b> Even if we constructed a fresh
    /// MainWindow on the UI thread, it would be a different object than the
    /// desktop lifetime's MainWindow. The user clicks the live window and
    /// mutates its DataContext; <c>read_observable</c> would see the OTHER
    /// instance. We bind to the live MainWindow when its CLR type matches the
    /// descriptor's <c>TypeName</c>; otherwise we fall back to the original
    /// factory (still on the UI thread).
    /// </para>
    /// <para>
    /// The bg thread blocks on <see cref="Dispatcher.Invoke"/> only for the
    /// brief duration of the factory call. Sub-millisecond on a loaded
    /// MainWindow; never on the UI thread itself, so no deadlock.
    /// </para>
    /// <para>
    /// Note: this auto-wiring matches Window-typed roots only. Adopters whose
    /// root is a non-Window ViewModel (typical Avalonia pattern) must still do
    /// the explicit `RootDescriptor.Create = () => MyViewModel.Shared` rewrite
    /// in their `App.OnFrameworkInitializationCompleted` BEFORE calling
    /// AttachTo - exactly the same way the WPF TodoApp does. See the Phase 2
    /// sample (Sample.Avalonia.Dashboard) for the canonical example.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<RootDescriptor> WrapRootsForUiThread(
        global::Avalonia.Application app,
        IReadOnlyList<RootDescriptor> roots)
    {
        var bridged = new List<RootDescriptor>(roots.Count);
        foreach (var r in roots)
        {
            // No factory -> nothing to wrap. The runtime will surface
            // "root_unavailable" until / unless an adapter binds an instance
            // (Phase 3+ may add framework-specific binding flows).
            if (r.Create is null)
            {
                bridged.Add(r);
                continue;
            }

            var originalCreate = r.Create;
            var typeName = r.TypeName;

            Func<object> bridged_ = () => Dispatcher.UIThread.Invoke(
                () =>
                {
                    // Try the live MainWindow first. The descriptor's TypeName
                    // is fully-qualified (FQN); match against the runtime
                    // type's FullName.
                    if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                        desktop.MainWindow is { } mw &&
                        string.Equals(mw.GetType().FullName, typeName, StringComparison.Ordinal))
                    {
                        return (object)mw;
                    }

                    // Otherwise call the original factory; we're on the UI
                    // thread now so thread-affine ctors are happy.
                    return originalCreate();
                });

            bridged.Add(r with { Create = bridged_ });
        }
        return bridged;
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
            // The Avalonia Dispatcher pumps message-loop work during the wait
            // if we're called from the UI thread, so this is safe even from
            // the desktop lifetime's Exit handler.
            try { _hostTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore - shutdown */ }

            try { _cts.Dispose(); } catch { /* ignore */ }
        }
    }
}
