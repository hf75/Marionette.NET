// Sample.Wpf.TodoApp — App shell
//
// Phase 1.4 wiring contract (mirrors Sample.Wpf.StripeProbe but also
// demonstrates the "non-Window root" wiring pattern that adopters with a
// dedicated ViewModel will recognise):
//
//   * In `--mcp` GUI mode, App.OnStartup calls
//     `MarionetteWpf.AttachTo(this, customRoots, e.Args)` to start the MCP
//     server on a background Task and bind the WPF adapter for UI-thread
//     dispatch + screenshot capture.
//   * In `--mcp --headless` mode, this App class is never constructed — the
//     headless code path in Program.cs calls MarionetteHost.RunAsync directly
//     with the same customRoots.
//   * In normal GUI mode (no --mcp), AttachTo sees no `--mcp` in args and
//     returns immediately — no MCP server is started.
//
// Why we rewrite RootDescriptor.Create factories before passing them to
// AttachTo: the source generator emits `static () => new TodoListViewModel()`
// which would create a SECOND ViewModel instance, separate from the one
// MainWindow binds as its DataContext. The runtime's manifest registry would
// hold the second instance; mutations from invoke_method would not be visible
// in the live MainWindow, and observables read by the LLM would always reflect
// a phantom instance. Solution: substitute every TodoListViewModel descriptor
// factory with `() => TodoListViewModel.Shared`. MarionetteWpf.AttachTo does
// this for Window-typed roots automatically (it prefers app.MainWindow when
// type-compatible), but TodoListViewModel is not a Window — adopters must
// wire their own non-Window roots.
//
// The `#if MCP_ENABLED` gate makes the entire AttachTo + factory-rewrite block
// disappear from stripped Release builds. `MarionetteWpf` and the Runtime's
// `RootDescriptor` type live in Marionette.NET.Adapter.Wpf / .Runtime which
// are only referenced when EnableMcpAutomation is true; without the gate, the
// using directives would fail to compile in stripped builds.

using System.Windows;

#if MCP_ENABLED
using System.Collections.Generic;
using Marionette.Adapter.Wpf;
using Marionette.Generated;
using Marionette.Runtime.Manifest;
#endif

namespace Sample.Wpf.TodoApp;

public partial class App : Application
{
    public App()
    {
        // EnableDefaultApplicationDefinition=false in the csproj means the SDK
        // does not auto-emit Main into App.g.cs (so our [STAThread] Main in
        // Program.cs takes precedence). The side-effect is that nothing calls
        // App.InitializeComponent() automatically — and without it, App.xaml's
        // Application.Resources (BgBrush, AccentButton, …) never load. The
        // MainWindow's StaticResource bindings then throw at construction. Fix
        // by invoking the generated method ourselves.
        InitializeComponent();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if MCP_ENABLED
        // Rewrite the TodoListViewModel root's Create factory to return our
        // singleton instance instead of `new TodoListViewModel()`. This keeps
        // MainWindow.DataContext and ManifestRegistry's instance identical so
        // [McpCallable] mutations and [McpObservable] reads see the same
        // underlying state.
        var bridgedRoots = new List<RootDescriptor>(GeneratedManifest.Roots.Count);
        foreach (var root in GeneratedManifest.Roots)
        {
            if (root.TypeName == typeof(TodoListViewModel).FullName)
            {
                bridgedRoots.Add(root with { Create = static () => TodoListViewModel.Shared });
            }
            else
            {
                bridgedRoots.Add(root);
            }
        }

        // One-line wiring per Phase 1.3 contract. AttachTo:
        //   1. Builds a WpfUiAutomationAdapter against this Application.
        //   2. Wraps RootDescriptor.Create factories to dispatch through the
        //      WPF UI thread (so STA-bound ctors don't fail on the bg thread).
        //      Our pre-substituted `() => TodoListViewModel.Shared` flows
        //      through that wrapping unchanged — the bg thread issues a
        //      Dispatcher.Invoke that just reads the static singleton, no
        //      construction needed.
        //   3. Spawns MarionetteHost.RunAsync on a background Task so the WPF
        //      message loop never blocks.
        //   4. Hooks Application.Exit for clean shutdown.
        // Without `--mcp` in e.Args, AttachTo's host returns 0 immediately.
        MarionetteWpf.AttachTo(this, bridgedRoots, e.Args);
#endif
    }
}
