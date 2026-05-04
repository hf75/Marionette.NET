// Sample.Maui.PocketPlanner - Windows-platform custom Main
//
// Three modes, picked from argv (mirrors Sample.Wpf.TodoApp /
// Sample.Avalonia.Dashboard / Sample.WinUI.FormLab):
//
//   (no args)                  -> normal MAUI GUI
//   --mcp                      -> start MCP server on stdio AND show GUI
//                                 (Claude controls + user watches)
//   --mcp --headless           -> start MCP server on stdio, no Application
//   --mcp-help                 -> print manifest summary to stderr and exit
//
// Headless paths bypass the MAUI Application entirely; they call
// MarionetteHost.RunAsync directly with adapter:null (NoOpAdapter). In that
// mode capture_screenshot returns the documented `screenshot_not_supported`
// structured error, and method invocations run inline rather than dispatched.
//
// GUI `--mcp` path: falls through to RunGui() which boots the MAUI
// Application via WinUI's standard application bootstrap. App.OnStart then
// runs the descriptor-factory rewrite + MarionetteMaui.AttachTo (see
// App.xaml.cs in the cross-platform sample root).

using System;
using System.Threading.Tasks;

namespace Sample.Maui.PocketPlanner.WinUI;

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
            return Marionette.Runtime.MarionetteHost.RunAsync(
                args,
                Marionette.Generated.GeneratedManifest.Roots,
                adapter: null).GetAwaiter().GetResult();
        }

        if (wantsMcp && wantsHeadless)
        {
            // Pure stdio MCP server. No Application, no IDispatcher, no
            // window. The headless registry materialises PlannerViewModel
            // via the generator's `new PlannerViewModel()` factory directly.
            try
            {
                return Marionette.Runtime.MarionetteHost.RunAsync(
                    args,
                    Marionette.Generated.GeneratedManifest.Roots,
                    adapter: null).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[pocketplanner] MCP host crashed: {ex}");
                return 1;
            }
        }

        // GUI `--mcp` path: fall through to RunGui(). App.OnStart rewrites
        // the descriptor factory to return PlannerViewModel.Shared and then
        // calls MarionetteMaui.AttachTo, which spawns the host on a
        // background task.
        if (wantsMcp)
        {
            return RunGui();
        }
#endif

        // Default - normal MAUI, no MCP.
        return RunGui();
    }

    private static int RunGui()
    {
        // MAUI Windows head boots through the WinUI runtime's
        // Application.Start callback. Same shape as WinUI 3 directly.
        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var ctx = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
        return 0;
    }
}
