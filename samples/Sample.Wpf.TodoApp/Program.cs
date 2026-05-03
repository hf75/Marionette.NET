// Sample.Wpf.TodoApp — custom Main entry point
//
// Three modes, picked from argv (mirrors Sample.Wpf.StripeProbe):
//
//   (no args)                  → normal WPF GUI
//   --mcp                      → start MCP server on stdio AND show GUI
//                                (Claude controls + user watches)
//   --mcp --headless           → start MCP server on stdio, no Application / no window
//   --mcp-help                 → print manifest summary to stderr and exit
//
// Headless paths bypass the WPF Application entirely; they call
// MarionetteHost.RunAsync directly with adapter:null (NoOpAdapter). In that
// mode capture_screenshot returns the documented `screenshot_not_supported`
// structured error, and method invocations run inline rather than dispatched.
// For TodoApp specifically, the [McpCallable] methods don't touch UI controls
// (they mutate the ViewModel's ObservableCollection) so headless invocation
// works correctly — TotalCount, RemoveTodo, etc. all behave the same way.
//
// GUI `--mcp` path: falls through to RunGui() which constructs the WPF App.
// App.OnStartup then runs the descriptor-factory rewrite + MarionetteWpf.AttachTo
// (see App.xaml.cs for the why).

using System;
using System.Threading.Tasks;

namespace Sample.Wpf.TodoApp;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
#if MCP_ENABLED
        bool wantsMcp = false;
        bool wantsHeadless = false;
        bool wantsHelp = false;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--mcp", StringComparison.Ordinal)) wantsMcp = true;
            else if (string.Equals(arg, "--headless", StringComparison.Ordinal)) wantsHeadless = true;
            else if (string.Equals(arg, "--mcp-help", StringComparison.Ordinal)) wantsHelp = true;
        }

        if (wantsHelp)
        {
            // The host writes the manifest summary to stderr and returns 0.
            // We pass GeneratedManifest.Roots directly (no Shared rewrite) —
            // the help path doesn't construct any roots, just lists them.
            return Marionette.Runtime.MarionetteHost.RunAsync(
                args,
                Marionette.Generated.GeneratedManifest.Roots,
                adapter: null).GetAwaiter().GetResult();
        }

        if (wantsMcp && wantsHeadless)
        {
            // Pure stdio MCP server. No Application, no Dispatcher, no window.
            // The headless registry will materialise TodoListViewModel via the
            // generator's `new TodoListViewModel()` factory directly — that's
            // fine because there's no UI here for the instance to be out of
            // sync with. Adopters running headless tests get a fresh ViewModel
            // every host start.
            try
            {
                return Marionette.Runtime.MarionetteHost.RunAsync(
                    args,
                    Marionette.Generated.GeneratedManifest.Roots,
                    adapter: null).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[todoapp] MCP host crashed: {ex}");
                return 1;
            }
        }

        // GUI `--mcp` path: fall through to RunGui(). App.OnStartup rewrites
        // the descriptor factory to return TodoListViewModel.Shared and then
        // calls MarionetteWpf.AttachTo, which spawns the host on a background
        // task and hooks Application.Exit for clean shutdown.
        if (wantsMcp)
        {
            return RunGui();
        }
#endif

        // Default — normal WPF, no MCP.
        return RunGui();
    }

    private static int RunGui()
    {
        var app = new App();
        var window = new MainWindow();
        return app.Run(window);
    }
}
