// Sample.Wpf.NeonControlCenter — entry point
//
// Three modes (mirrors Sample.Wpf.TodoApp's wiring):
//   (no args)         → normal WPF GUI
//   --mcp             → start MCP server on stdio AND show GUI
//   --mcp --headless  → MCP server on stdio, no Application / no window
//   --mcp-help        → print manifest summary to stderr and exit
//
// Used as the end-of-phase showcase: the headless mode is what the integration
// debug session drives via JSON-RPC to verify every tool roundtrips.

using System;

namespace Sample.Wpf.NeonControlCenter;

internal static class Program
{
    [STAThread]
#if MCP_ENABLED
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "MarionetteHost.RunAsync surfaces raise_event reflection cap; sample acknowledges at the entry.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Cascading IL3050 from STJ.")]
#endif
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
            try
            {
                return Marionette.Runtime.MarionetteHost.RunAsync(
                    args,
                    Marionette.Generated.GeneratedManifest.Roots,
                    adapter: null).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[neon] MCP host crashed: {ex}");
                return 1;
            }
        }

        if (wantsMcp)
        {
            return RunGui();
        }
#endif

        return RunGui();
    }

    private static int RunGui()
    {
        var app = new App();
        var window = new MainWindow();
        return app.Run(window);
    }
}
