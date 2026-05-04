// Marionette.NET - Avalonia bootstrap entry point
//
// Phase 2.1 contract: adopters wire Marionette into their Avalonia Application
// with one call from `App.OnFrameworkInitializationCompleted`.
//
// Phase 3.3 multi-window routing: the adapter's RootInstanceTracker is
// populated from two sources:
//   (a) bridged factories register the live root instance on first
//       materialisation;
//   (b) MarionetteAvalonia subscribes to the desktop lifetime's WindowOpened
//       event so secondary Window-typed roots auto-register. Adopters with
//       non-Window roots can call MarionetteAvalonia.TrackInstance directly.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
/// application.
/// </summary>
public static class MarionetteAvalonia
{
    private static AvaloniaUiAutomationAdapter? s_currentAdapter;

    /// <summary>
    /// Attach the Marionette MCP host to a running Avalonia Application.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>AOT/trim contract (Phase 4.2):</b> the bootstrap forwards into
    /// <see cref="MarionetteHost.RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>,
    /// which is itself marked
    /// <see cref="System.Diagnostics.CodeAnalysis.RequiresUnreferencedCodeAttribute"/>
    /// because the runtime surfaces the <c>raise_event</c> MCP tool. Suppress
    /// at this entry point if you only use <c>simulate_input</c> +
    /// <c>[McpCallable]</c>.
    /// </para>
    /// </remarks>
    [RequiresUnreferencedCode(
        "MarionetteAvalonia.AttachTo forwards into MarionetteHost.RunAsync, which surfaces the " +
        "raise_event MCP tool's reflection-based event resolver. Suppress at the call site " +
        "after auditing your raise_event use, or avoid raise_event in favour of " +
        "simulate_input + [McpCallable]+invoke_method.")]
    [RequiresDynamicCode(
        "MarionetteAvalonia.AttachTo forwards into MarionetteHost.RunAsync, which uses " +
        "System.Text.Json for boxed-object serialisation. Phase 4.2 keeps this on the warning " +
        "surface; Phase 6 may move to source-generated JsonTypeInfo.")]
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
        var tracker = new RootInstanceTracker();
        var adapter = new AvaloniaUiAutomationAdapter(
            app,
            lf.CreateLogger<AvaloniaUiAutomationAdapter>(),
            tracker);
        s_currentAdapter = adapter;

        var bridgedRoots = WrapRootsForUiThread(app, roots, tracker);

        // Phase 3.3: hook desktop.Windows for auto-registration of Window-typed
        // root types beyond the initial MainWindow.
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
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[marionette-avalonia] MCP host crashed: {ex}");
            }
        }, cts.Token);

        var attachment = new MarionetteAttachment(cts, hostTask);
        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            EventHandler<ControlledApplicationLifetimeExitEventArgs>? exitHandler = null;
            exitHandler = (_, _) =>
            {
                try { attachment.Dispose(); }
                catch { /* shutdown */ }
                try { if (exitHandler is not null) desktop.Exit -= exitHandler; }
                catch { /* shutdown */ }
            };
            desktop.Exit += exitHandler;
        }

        return attachment;
    }

    /// <summary>
    /// Phase 3.3: register a non-Window root instance for multi-window routing.
    /// </summary>
    public static string TrackInstance(string rootName, object instance)
    {
        var adapter = s_currentAdapter
            ?? throw new InvalidOperationException(
                "MarionetteAvalonia.TrackInstance was called before AttachTo. " +
                "Call AttachTo from App.OnFrameworkInitializationCompleted first.");
        return adapter.Tracker.Track(rootName, instance);
    }

    private static IReadOnlyList<RootDescriptor> WrapRootsForUiThread(
        global::Avalonia.Application app,
        IReadOnlyList<RootDescriptor> roots,
        RootInstanceTracker tracker)
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
            var rootName = r.Name;

            Func<object> bridged_ = () => Dispatcher.UIThread.Invoke(
                () =>
                {
                    object resolved;
                    if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                        desktop.MainWindow is { } mw &&
                        string.Equals(mw.GetType().FullName, typeName, StringComparison.Ordinal))
                    {
                        resolved = mw;
                    }
                    else
                    {
                        resolved = originalCreate();
                    }
                    tracker.Track(rootName, resolved);
                    return resolved;
                });

            bridged.Add(r with { Create = bridged_ });
        }
        return bridged;
    }

    /// <summary>
    /// Phase 3.3: hook the desktop lifetime's <see cref="IClassicDesktopStyleApplicationLifetime.Windows"/>
    /// indirectly via per-window <c>Opened</c>/<c>Closed</c> events captured
    /// from a periodic reconciliation. Avalonia 11.x doesn't expose a public
    /// "window-opened" application-level event; reconciling on
    /// <see cref="Dispatcher.UIThread"/> idle ticks is the simplest match.
    /// </summary>
    private static void InstallWindowOpenHook(
        global::Avalonia.Application app,
        IReadOnlyList<RootDescriptor> roots,
        RootInstanceTracker tracker)
    {
        var typeToRoot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in roots)
        {
            if (!string.IsNullOrEmpty(r.TypeName))
            {
                typeToRoot[r.TypeName] = r.Name;
            }
        }
        if (typeToRoot.Count == 0) return;
        if (app.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        var hooked = new System.Runtime.CompilerServices.ConditionalWeakTable<Window, object>();

        void ReconcileOnce()
        {
            foreach (var w in desktop.Windows)
            {
                if (w is null) continue;
                var typeName = w.GetType().FullName;
                if (typeName is null) continue;
                if (!typeToRoot.TryGetValue(typeName, out var rootName)) continue;

                tracker.Track(rootName, w);

                // Hook close exactly once per Window via the CWT sentinel.
                if (hooked.TryGetValue(w, out _)) continue;
                hooked.Add(w, new object());
                var capturedWindow = w;
                EventHandler? closedHandler = null;
                closedHandler = (_, _) =>
                {
                    try { tracker.Untrack(capturedWindow); } catch { /* ignore */ }
                    try { if (closedHandler is not null) capturedWindow.Closed -= closedHandler; } catch { /* ignore */ }
                };
                capturedWindow.Closed += closedHandler;
            }
        }

        ReconcileOnce();

        // Avalonia 11.x exposes a Window.Activated event but no
        // application-wide "window-opened" notifier. We rely on the same
        // periodic-tick pattern as the WPF adapter — schedule a low-priority
        // reconciliation on each application idle.
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        timer.Tick += (_, _) => ReconcileOnce();
        timer.Start();
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
            try { _hostTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore - shutdown */ }
            try { _cts.Dispose(); } catch { /* ignore */ }
        }
    }
}
