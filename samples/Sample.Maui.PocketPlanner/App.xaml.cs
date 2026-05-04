// Sample.Maui.PocketPlanner - App shell
//
// Phase 4.1 wiring contract (mirrors Sample.Wpf.TodoApp / Sample.Avalonia.Dashboard /
// Sample.WinUI.FormLab but uses MAUI's lifecycle hooks instead):
//
//   * In `--mcp` GUI mode, App.OnStart rewrites the PlannerViewModel root's
//     Create factory to `() => PlannerViewModel.Shared`, then calls
//     MarionetteMaui.AttachTo(this, customRoots, args) to start the MCP
//     server on a background Task and bind the MAUI adapter for UI-thread
//     dispatch.
//   * In `--mcp --headless` mode this App class is never constructed - the
//     headless code path in Platforms/Windows/Program.cs (or wherever the
//     platform-specific Main lives) calls MarionetteHost.RunAsync directly
//     with the same customRoots.
//   * In normal GUI mode (no --mcp), AttachTo sees no `--mcp` in args and
//     returns immediately - no MCP server is started.
//
// Why we rewrite RootDescriptor.Create factories before passing them to
// AttachTo: the source generator emits `static () => new PlannerViewModel()`
// which would create a SECOND ViewModel instance, separate from the one the
// MainPage binds as its BindingContext. The runtime's manifest registry
// would hold the second instance; mutations from invoke_method would not be
// visible in the live MainPage, and observables read by the LLM would always
// reflect a phantom instance. Solution: substitute every PlannerViewModel
// descriptor factory with `() => PlannerViewModel.Shared`. This is the
// canonical non-Window/non-Page root pattern across all four adapters.
//
// The `#if MCP_ENABLED` gate makes the entire AttachTo + factory-rewrite
// block disappear from stripped Release builds. `MarionetteMaui` and the
// Runtime's `RootDescriptor` type live in Marionette.NET.Adapter.Maui /
// .Runtime which are only referenced when EnableMcpAutomation is true;
// without the gate, the using directives would fail to compile in stripped
// builds.

#if MCP_ENABLED
using System;
using System.Collections.Generic;
using Marionette.Adapter.Maui;
using Marionette.Generated;
using Marionette.Runtime.Manifest;
#endif

namespace Sample.Maui.PocketPlanner;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // The Window directly hosts MainPage (no Shell). MAUI's CreateWindow
        // is the modern equivalent of MainWindow construction; runs once
        // per Window the platform creates.
        var window = new Window(new MainPage());
        return window;
    }

#if MCP_ENABLED
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 4.2: PocketPlanner forwards into MarionetteMaui.AttachTo which carries " +
                        "the cascading raise_event reflection warning. PocketPlanner doesn't use " +
                        "raise_event from any deployed flow.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Phase 4.2: cascading IL3050 from STJ.")]
#endif
    protected override void OnStart()
    {
        base.OnStart();

#if MCP_ENABLED
        // Rewrite the PlannerViewModel root's Create factory to return our
        // singleton instance instead of `new PlannerViewModel()`. This keeps
        // MainPage.BindingContext and ManifestRegistry's instance identical
        // so [McpCallable] mutations and [McpObservable] reads see the same
        // underlying state. Same pattern as TodoApp / Dashboard / FormLab.
        var bridgedRoots = new List<RootDescriptor>(GeneratedManifest.Roots.Count);
        foreach (var root in GeneratedManifest.Roots)
        {
            if (root.TypeName == typeof(PlannerViewModel).FullName)
            {
                bridgedRoots.Add(root with { Create = static () => PlannerViewModel.Shared });
            }
            else
            {
                bridgedRoots.Add(root);
            }
        }

        // One-line wiring per Phase 4.1 contract. AttachTo:
        //   1. Captures the application's IDispatcher.
        //   2. Builds a MauiUiAutomationAdapter against this Application.
        //   3. Wraps RootDescriptor.Create factories to dispatch through the
        //      MAUI UI thread and prefer the live MainPage when type-compatible.
        //      Our pre-substituted `() => PlannerViewModel.Shared` flows through
        //      that wrapping unchanged.
        //   4. Spawns MarionetteHost.RunAsync on a background Task so the
        //      MAUI message loop never blocks.
        // Without `--mcp` in argv, AttachTo's host returns 0 immediately and
        // this becomes a no-op.
        var argv = Environment.GetCommandLineArgs();
        var argsExceptExe = argv.Length > 1 ? argv[1..] : Array.Empty<string>();
        MarionetteMaui.AttachTo(this, bridgedRoots, argsExceptExe);
#endif
    }
}
