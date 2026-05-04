// Sample.WinUI.FormLab - App shell
//
// Phase 3.2 wiring contract (mirrors Sample.Wpf.TodoApp / Sample.Avalonia.Dashboard
// but uses WinUI 3's lifecycle hooks instead):
//
//   * In `--mcp` GUI mode, `OnLaunched` constructs the MainWindow, activates
//     it, then calls MarionetteWinUI.AttachTo(this, _mainWindow, customRoots,
//     args) to start the MCP server on a background Task and bind the WinUI
//     adapter for UI-thread dispatch + screenshot capture.
//   * In `--mcp --headless` mode this App class is never constructed - the
//     headless code path in Program.cs calls MarionetteHost.RunAsync directly
//     with the same customRoots.
//   * In normal GUI mode (no --mcp), AttachTo sees no `--mcp` in args and
//     returns immediately - no MCP server is started.
//
// Why we rewrite RootDescriptor.Create factories before passing them to
// AttachTo: the source generator emits `static () => new FormLabViewModel()`
// which would create a SECOND ViewModel instance, separate from the one the
// MainWindow binds as its DataContext. The runtime's manifest registry would
// hold the second instance; mutations from invoke_method would not be visible
// in the live MainWindow, and observables read by the LLM would always reflect
// a phantom instance. Solution: substitute every FormLabViewModel descriptor
// factory with `() => FormLabViewModel.Shared`. This is the canonical
// non-Window root pattern across all three adapters.
//
// The `#if MCP_ENABLED` gate makes the entire AttachTo + factory-rewrite block
// disappear from stripped Release builds. `MarionetteWinUI` and the
// Runtime's `RootDescriptor` type live in Marionette.NET.Adapter.WinUI /
// .Runtime which are only referenced when EnableMcpAutomation is true; without
// the gate, the using directives would fail to compile in stripped builds.

using Microsoft.UI.Xaml;

#if MCP_ENABLED
using System;
using System.Collections.Generic;
using Marionette.Adapter.WinUI;
using Marionette.Generated;
using Marionette.Runtime.Manifest;
#endif

namespace Sample.WinUI.FormLab;

public partial class App : Application
{
    private Window? _mainWindow;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();

#if MCP_ENABLED
        // Rewrite the FormLabViewModel root's Create factory to return our
        // singleton instance instead of `new FormLabViewModel()`. This keeps
        // MainWindow.DataContext and ManifestRegistry's instance identical so
        // [McpCallable] mutations and [McpObservable] reads see the same
        // underlying state. Same pattern as TodoApp / Dashboard.
        var bridgedRoots = new List<RootDescriptor>(GeneratedManifest.Roots.Count);
        foreach (var root in GeneratedManifest.Roots)
        {
            if (root.TypeName == typeof(FormLabViewModel).FullName)
            {
                bridgedRoots.Add(root with { Create = static () => FormLabViewModel.Shared });
            }
            else
            {
                bridgedRoots.Add(root);
            }
        }

        // One-line wiring per Phase 3.2 contract. AttachTo:
        //   1. Captures the UI thread's DispatcherQueue.
        //   2. Tracks the MainWindow via WindowTracker for visual-tree walks.
        //   3. Builds a WinUiAutomationAdapter against this Application.
        //   4. Wraps RootDescriptor.Create factories to dispatch through the
        //      WinUI UI thread (so thread-affine ctors don't fail on the bg
        //      thread). Our pre-substituted `() => FormLabViewModel.Shared`
        //      flows through that wrapping unchanged.
        //   5. Spawns MarionetteHost.RunAsync on a background Task so the
        //      WinUI message loop never blocks.
        //   6. Hooks Window.Closed for clean shutdown.
        // Without `--mcp` in argv, AttachTo's host returns 0 immediately and
        // this becomes a no-op.
        var argv = Environment.GetCommandLineArgs();
        var argsExceptExe = argv.Length > 1 ? argv[1..] : Array.Empty<string>();
        MarionetteWinUI.AttachTo(this, _mainWindow, bridgedRoots, argsExceptExe);
#endif
    }
}
