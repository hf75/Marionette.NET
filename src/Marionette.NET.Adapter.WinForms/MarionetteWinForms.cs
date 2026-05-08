// Marionette.NET — Windows Forms bootstrap entry point (Phase 15)
//
// Adopters wire Marionette into a WinForms application with one call from
// their main form's Shown handler:
//
//     private void MainForm_Shown(object sender, EventArgs e)
//     {
//     #if MCP_ENABLED
//         MarionetteWinForms.AttachTo(this, GeneratedManifest.Roots, args);
//     #endif
//     }
//
// Why Shown and not Load: BeginInvoke requires Form.Handle to exist. Form.Load
// fires *before* the handle is created in some adopter scenarios (notably when
// a Form is constructed but never .Show()-ed before Application.Run picks it
// up via Application.OpenForms manipulation). Hooking Shown guarantees the
// handle is alive AND the form is visible — which is also when DrawToBitmap
// produces meaningful output.
//
// The call:
//   1. Constructs a `WinFormsUiAutomationAdapter` bound to the supplied Form.
//   2. Rewrites every RootDescriptor's `Create` factory so it dispatches
//      through the WinForms UI thread.
//   3. Installs an `OpenFormsHook` that reconciles Application.OpenForms on
//      every idle tick — secondary forms of a known [McpRoot] type
//      auto-register without adopter ceremony.
//   4. Spawns `MarionetteHost.RunAsync(args, roots, adapter, ct)` on a
//      background Task so the UI thread is never blocked.
//   5. Hooks Application.ApplicationExit to cancel the host (clean shutdown).
//   6. Returns immediately. The IDisposable can be Disposed early.
//
// SCENARIO COVERAGE
//
// `--mcp` (with GUI): adopters call AttachTo from MainForm.Shown. The args
// from Program.Main need to flow through (via the `args` parameter) so
// MarionetteHost sees `--mcp`. Without `--mcp` in args, the host's RunAsync
// returns 0 immediately and AttachTo becomes a no-op.
//
// `--mcp --headless`: do NOT use AttachTo. There is no Application.Run, no
// main form, no UI thread. Adopters call
// `MarionetteHost.RunAsync(args, roots, adapter: null)` directly from
// Program.Main. The host falls back to NoOpAdapter, screenshot returns the
// `screenshot_not_supported` structured error, and dispatch runs inline.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Marionette.Adapter.WinForms.Internal;
using Marionette.Runtime;
using Marionette.Runtime.Adapters;
using Marionette.Runtime.Manifest;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marionette.Adapter.WinForms;

/// <summary>
/// One-call bootstrap for adding Marionette MCP automation to a Windows
/// Forms application. Use this from the main form's <see cref="Form.Shown"/>
/// handler in GUI mode (i.e. the <c>--mcp</c> path; not <c>--mcp --headless</c>,
/// which has no Application).
/// </summary>
[SupportedOSPlatform("windows")]
public static class MarionetteWinForms
{
    // Per-process adapter handle so adopters with non-Form roots can call
    // MarionetteWinForms.TrackInstance(rootName, viewModelInstance) to
    // register a second ViewModel for multi-window routing without having to
    // plumb a reference through their Form class.
    private static WinFormsUiAutomationAdapter? s_currentAdapter;

    /// <summary>
    /// Attach the Marionette MCP host to a running WinForms application.
    /// Non-blocking: the host runs on a background <see cref="Task"/>; the
    /// caller's UI thread continues into the regular WinForms message loop.
    /// </summary>
    /// <param name="bootstrapForm">
    /// A live <see cref="Form"/> whose <see cref="Form.Handle"/> has been
    /// created (typically the application's main form, called from its
    /// <see cref="Form.Shown"/> handler). Used as the BeginInvoke dispatch
    /// target — every adapter call goes through this form's handle to land
    /// on the UI thread.
    /// </param>
    /// <param name="roots">The source-generator-emitted root list (typically <c>Marionette.Generated.GeneratedManifest.Roots</c>).</param>
    /// <param name="args">
    /// Optional argv from <c>Program.Main</c>. When omitted, falls back to
    /// <see cref="Environment.GetCommandLineArgs"/> (skipping the .exe path).
    /// Without <c>--mcp</c> in args the host returns 0 immediately.
    /// </param>
    /// <param name="loggerFactory">
    /// Optional logger factory. <see langword="null"/> uses a
    /// <see cref="NullLoggerFactory"/>.
    /// </param>
    /// <returns>
    /// A disposable handle. Disposing it cancels the host run-task and waits
    /// for it (best-effort, max 2 s). Disposal is also auto-wired to
    /// <see cref="Application.ApplicationExit"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="bootstrapForm"/> or <paramref name="roots"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The supplied form's handle has not been created yet.
    /// </exception>
    [RequiresUnreferencedCode(
        "MarionetteWinForms.AttachTo forwards into MarionetteHost.RunAsync, which surfaces the " +
        "raise_event MCP tool's reflection-based event resolver. Suppress at the call site after " +
        "auditing your raise_event use, or avoid raise_event in favour of [McpCallable] / simulate_input.")]
    [RequiresDynamicCode(
        "MarionetteWinForms.AttachTo forwards into MarionetteHost.RunAsync, which uses System.Text.Json " +
        "to serialise observable values and callable results. Phase 8/8.5 source-gen covers all standard " +
        "shapes; the warning persists at the boundary for legacy fallback paths.")]
    public static IDisposable AttachTo(
        Form bootstrapForm,
        IReadOnlyList<RootDescriptor> roots,
        string[]? args = null,
        ILoggerFactory? loggerFactory = null)
    {
        if (bootstrapForm is null) throw new ArgumentNullException(nameof(bootstrapForm));
        if (roots is null) throw new ArgumentNullException(nameof(roots));
        if (!bootstrapForm.IsHandleCreated)
        {
            throw new InvalidOperationException(
                "MarionetteWinForms.AttachTo requires a Form whose Handle is created. " +
                "Call from Form.Shown (not Form.Load) — the handle is guaranteed by then.");
        }

        var resolvedArgs = args ?? CommandLineArgsExceptExe();
        var lf = loggerFactory ?? NullLoggerFactory.Instance;
        var tracker = new RootInstanceTracker();
        var adapter = new WinFormsUiAutomationAdapter(
            bootstrapForm,
            lf.CreateLogger<WinFormsUiAutomationAdapter>(),
            tracker);
        s_currentAdapter = adapter;

        // Rewrite roots so factories dispatch through the WinForms UI thread
        // and prefer a live form instance when type-compatible.
        var bridgedRoots = WrapRootsForUiThread(bootstrapForm, roots, tracker);

        // Install the multi-window reconciliation hook against
        // Application.OpenForms.
        _ = OpenFormsHook.Install(roots, tracker, lf.CreateLogger("Marionette.WinForms.OpenFormsHook"));

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
                Console.Error.WriteLine($"[marionette-winforms] MCP host crashed: {ex}");
            }
        }, cts.Token);

        var attachment = new MarionetteAttachment(cts, hostTask);

        // Application.ApplicationExit fires when Application.Exit() is called
        // OR when the message loop ends naturally (last form closes). Hooking
        // it gives clean shutdown without adopter ceremony.
        EventHandler? exitHandler = null;
        exitHandler = (_, _) =>
        {
            try { attachment.Dispose(); } catch { /* shutdown */ }
            try { if (exitHandler is not null) Application.ApplicationExit -= exitHandler; } catch { /* shutdown */ }
        };
        Application.ApplicationExit += exitHandler;

        return attachment;
    }

    /// <summary>
    /// Phase 3.3: register a non-Form root instance (e.g. a ViewModel that
    /// owns no Form) as belonging to a specific named root. Used by adopters
    /// whose multi-window scenario materialises a second ViewModel that the
    /// auto-tracker can't pick up via Form class-name matching.
    /// </summary>
    /// <param name="rootName">The manifest name of the root.</param>
    /// <param name="instance">The live instance to register.</param>
    /// <returns>The newly-allocated windowId, or the existing one if already tracked.</returns>
    public static string TrackInstance(string rootName, object instance)
    {
        var adapter = s_currentAdapter
            ?? throw new InvalidOperationException(
                "MarionetteWinForms.TrackInstance was called before AttachTo. " +
                "Call AttachTo from your main form's Shown handler first.");
        return adapter.Tracker.Track(rootName, instance);
    }

    /// <summary>
    /// Wrap each <see cref="RootDescriptor.Create"/> factory so it runs on the
    /// WinForms UI thread, and so it returns a live Form (when type-compatible)
    /// before falling back to the original factory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two problems are solved here, identical to the WPF adapter's analogue:
    /// </para>
    /// <para>
    /// <b>Problem 1 — UI thread.</b> WinForms control ctors don't strictly
    /// require an STA thread the way WPF does, but Form.Show / Form.Visible
    /// access requires UI-thread access if the form is wired up. Dispatching
    /// through the bootstrap form's BeginInvoke moves the call to the UI
    /// thread.
    /// </para>
    /// <para>
    /// <b>Problem 2 — instance affinity.</b> If we constructed a fresh
    /// <c>MainForm</c> on the UI thread, it would be a different object than
    /// <see cref="Application.OpenForms"/>[0]. The user clicks the live form
    /// and mutates its state; <c>read_observable</c> would see the OTHER
    /// instance and always return zero. We bind to the live OpenForms[0]
    /// when its CLR type matches the descriptor's <c>TypeName</c>.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<RootDescriptor> WrapRootsForUiThread(
        Form bootstrapForm,
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

            Func<object> bridgedFactory = () =>
            {
                // Run on the UI thread.
                if (!bootstrapForm.InvokeRequired)
                {
                    return ResolveOrCreate();
                }
                object? result = null;
                Exception? error = null;
                bootstrapForm.Invoke(new Action(() =>
                {
                    try { result = ResolveOrCreate(); }
                    catch (Exception ex) { error = ex; }
                }));
                if (error is not null) throw error;
                return result!;
            };

            object ResolveOrCreate()
            {
                object resolved;
                // Prefer a live Form whose runtime type matches the descriptor
                // — that's the one the user is actually interacting with.
                Form? matching = null;
                foreach (Form? f in Application.OpenForms)
                {
                    if (f is null) continue;
                    if (string.Equals(f.GetType().FullName, typeName, StringComparison.Ordinal))
                    {
                        matching = f;
                        break;
                    }
                }
                resolved = matching ?? originalCreate();
                tracker.Track(rootName, resolved);
                return resolved;
            }

            bridged.Add(r with { Create = bridgedFactory });
        }
        return bridged;
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
            try { _hostTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            try { _cts.Dispose(); } catch { /* ignore */ }
        }
    }
}
