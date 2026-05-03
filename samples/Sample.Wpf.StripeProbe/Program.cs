// Sample.Wpf.StripeProbe — custom entry point for Spike C / Phase 1.2
//
// Three modes, picked from argv:
//   (no args)                    → normal WPF GUI
//   --mcp                        → start MCP server on stdio AND show GUI (Claude controls + user watches)
//   --mcp --headless             → start MCP server on stdio, no Application / no window
//   --mcp-help                   → print manifest summary to stderr and exit
//
// Phase 1.2: the host is no longer a stub. It registers the four real MCP
// tools (inspect_app_api, invoke_method, read_observable, capture_screenshot)
// driven by the source-generator-emitted GeneratedManifest.Roots.
//
// Adapter wiring: Phase 1.3 owns the WPF-specific IUiAutomationAdapter
// implementation (Dispatcher marshalling, RenderTargetBitmap screenshot,
// FindByName control resolution). For Phase 1.2 both the headless and the
// GUI paths pass `adapter: null`; the host falls back to NoOpAdapter.
// `capture_screenshot` therefore returns a structured "not_supported" error
// in Phase 1.2 — that's the documented intermediate state until Phase 1.3
// lands.

using System;
using System.Threading.Tasks;

namespace Sample.Wpf.StripeProbe;

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
            return Marionette.Runtime.MarionetteHost.RunAsync(
                args,
                Marionette.Generated.GeneratedManifest.Roots,
                adapter: null).GetAwaiter().GetResult();
        }

        if (wantsMcp && wantsHeadless)
        {
            // Pure stdio MCP server. No Application, no Dispatcher, no window.
            try
            {
                return Marionette.Runtime.MarionetteHost.RunAsync(
                    args,
                    Marionette.Generated.GeneratedManifest.Roots,
                    adapter: null).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[stripeprobe] MCP host crashed: {ex}");
                return 1;
            }
        }

        if (wantsMcp)
        {
            // GUI + MCP. Run the MCP host on a background task; let WPF own
            // the main thread. Phase 1.3 will switch this to use the WPF
            // adapter and bind MainWindow into the manifest registry.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Marionette.Runtime.MarionetteHost.RunAsync(
                        args,
                        Marionette.Generated.GeneratedManifest.Roots,
                        adapter: null).ConfigureAwait(false);
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
