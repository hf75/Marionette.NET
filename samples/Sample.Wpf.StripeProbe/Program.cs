// Sample.Wpf.StripeProbe — custom entry point for Spike C
//
// Three modes, picked from argv:
//   (no args)                    → normal WPF GUI
//   --mcp                        → start MCP server on stdio AND show GUI (Claude controls + user watches)
//   --mcp --headless             → start MCP server on stdio, no Application / no window
//
// The headless path explicitly avoids constructing a WPF Application instance,
// because (a) we don't want a Dispatcher, and (b) it keeps the Adapter.Wpf
// initialization out of the loop — Spike C is about the stdio transport alone.

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;

namespace Sample.Wpf.StripeProbe;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
#if MCP_ENABLED
        bool wantsMcp = false;
        bool wantsHeadless = false;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--mcp", StringComparison.Ordinal)) wantsMcp = true;
            else if (string.Equals(arg, "--headless", StringComparison.Ordinal)) wantsHeadless = true;
        }

        if (wantsMcp && wantsHeadless)
        {
            // Pure stdio MCP server. No Application, no Dispatcher, no window.
            // Synchronous wait — this entry point is the process lifetime.
            try
            {
                Marionette.Runtime.MarionetteHost.RunAsync(args).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[stripeprobe] MCP host crashed: {ex}");
                return 1;
            }
        }

        if (wantsMcp)
        {
            // GUI + MCP. Run the MCP host on a background task; let WPF own the main thread.
            // Spike C goal: get headless working; this branch is the stretch goal.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Marionette.Runtime.MarionetteHost.RunAsync(args).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[stripeprobe] MCP host crashed: {ex}");
                }
            });

            return RunGui();
        }
#endif

        // Default — normal WPF.
        return RunGui();
    }

    private static int RunGui()
    {
        var app = new App();
        var window = new MainWindow();
        return app.Run(window);
    }
}
