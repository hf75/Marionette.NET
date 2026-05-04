// Sample.Avalonia.Dashboard - App shell
//
// Phase 2.1 wiring contract (mirrors Sample.Wpf.TodoApp but uses Avalonia's
// lifecycle hooks instead of WPF's):
//
//   * In `--mcp` GUI mode, `OnFrameworkInitializationCompleted` calls
//     MarionetteAvalonia.AttachTo(this, customRoots, args) to start the MCP
//     server on a background Task and bind the Avalonia adapter for UI-thread
//     dispatch + screenshot capture.
//   * In `--mcp --headless` mode this App class is never constructed - the
//     headless code path in Program.cs calls MarionetteHost.RunAsync directly
//     with the same customRoots.
//   * In normal GUI mode (no --mcp), AttachTo sees no `--mcp` in args and
//     returns immediately - no MCP server is started.
//
// Why we rewrite RootDescriptor.Create factories before passing them to
// AttachTo: the source generator emits `static () => new DashboardViewModel()`
// which would create a SECOND ViewModel instance, separate from the one
// MainWindow binds as its DataContext. The runtime's manifest registry would
// hold the second instance; mutations from invoke_method would not be visible
// in the live MainWindow, and observables read by the LLM would always reflect
// a phantom instance. Solution: substitute every DashboardViewModel descriptor
// factory with `() => DashboardViewModel.Shared`. This is the canonical
// non-Window root pattern for both adapters.
//
// The `#if MCP_ENABLED` gate makes the entire AttachTo + factory-rewrite block
// disappear from stripped Release builds. `MarionetteAvalonia` and the
// Runtime's `RootDescriptor` type live in Marionette.NET.Adapter.Avalonia /
// .Runtime which are only referenced when EnableMcpAutomation is true; without
// the gate, the using directives would fail to compile in stripped builds.

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

#if MCP_ENABLED
using System.Collections.Generic;
using Marionette.Adapter.Avalonia;
using Marionette.Generated;
using Marionette.Runtime.Manifest;
#endif

namespace Sample.Avalonia.Dashboard;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

#if MCP_ENABLED
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 4.2: Dashboard sample forwards into MarionetteAvalonia.AttachTo " +
                        "which surfaces the cascading raise_event reflection warning. The Dashboard " +
                        "doesn't use raise_event from any deployed flow.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Phase 4.2: cascading IL3050 from STJ.")]
#endif
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Construct the MainWindow before MCP attach so the descriptor
            // rewrite in AttachTo can see it as the desktop's MainWindow when
            // matching by FullName. The window's DataContext binds
            // DashboardViewModel.Shared; the same instance is what the
            // explicit factory rewrite below substitutes into the runtime's
            // RootDescriptor.
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();

#if MCP_ENABLED
        // Rewrite the DashboardViewModel root's Create factory to return our
        // singleton instance instead of `new DashboardViewModel()`. This keeps
        // MainWindow.DataContext and ManifestRegistry's instance identical so
        // [McpCallable] mutations and [McpObservable] reads see the same
        // underlying state.
        var bridgedRoots = new List<RootDescriptor>(GeneratedManifest.Roots.Count);
        foreach (var root in GeneratedManifest.Roots)
        {
            if (root.TypeName == typeof(DashboardViewModel).FullName)
            {
                bridgedRoots.Add(root with { Create = static () => DashboardViewModel.Shared });
            }
            else
            {
                bridgedRoots.Add(root);
            }
        }

        // One-line wiring per Phase 2.1 contract. AttachTo:
        //   1. Builds an AvaloniaUiAutomationAdapter against this Application.
        //   2. Wraps RootDescriptor.Create factories to dispatch through the
        //      Avalonia UI thread (so thread-affine ctors don't fail on the bg
        //      thread). Our pre-substituted `() => DashboardViewModel.Shared`
        //      flows through that wrapping unchanged.
        //   3. Spawns MarionetteHost.RunAsync on a background Task so the
        //      Avalonia message loop never blocks.
        //   4. Hooks IClassicDesktopStyleApplicationLifetime.Exit for clean
        //      shutdown.
        // Without `--mcp` in argv, AttachTo's host returns 0 immediately and
        // this becomes a no-op.
        MarionetteAvalonia.AttachTo(this, bridgedRoots);
#endif
    }
}
