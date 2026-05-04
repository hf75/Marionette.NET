// Sample.Wpf.StripeProbe — custom entry point for Spike C / Phase 1.2 / 1.3
//
// Three modes, picked from argv:
//   (no args)                    → normal WPF GUI
//   --mcp                        → start MCP server on stdio AND show GUI
//                                  (Claude controls + user watches)
//   --mcp --headless             → start MCP server on stdio, no Application / no window
//   --mcp-help                   → print manifest summary to stderr and exit
//
// Phase 1.3 split:
//
//   * Headless paths (`--mcp --headless`, `--mcp-help`) construct NO WPF
//     Application — they call MarionetteHost.RunAsync directly. The host
//     installs NoOpAdapter; capture_screenshot returns the documented
//     `screenshot_not_supported` structured error in headless mode.
//
//   * GUI `--mcp` path lets the regular WPF App class handle MCP wiring via
//     `MarionetteWpf.AttachTo(this, GeneratedManifest.Roots, e.Args)` from
//     `App.OnStartup`. The host runs on a background Task; UI thread runs the
//     normal message loop. This is the path that Phase 1.3 makes screenshot
//     work end-to-end.

using System;
using System.Threading.Tasks;

namespace Sample.Wpf.StripeProbe;

internal static class Program
{
    [STAThread]
#if MCP_ENABLED
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 4.2: StripeProbe surfaces MarionetteHost.RunAsync from Main; the " +
                        "host's RequiresUnreferencedCode warning is acknowledged here. The probe " +
                        "exercises [McpCallable] only and does not invoke raise_event.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Phase 4.2: cascading IL3050 from MarionetteHost.RunAsync's STJ path. " +
                        "StripeProbe's manifest exposes only int/string callables and an int observable " +
                        "— STJ's reflection over those primitive shapes is AOT-safe in practice.")]
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
            // The host writes the manifest summary to stderr and returns 0.
            return Marionette.Runtime.MarionetteHost.RunAsync(
                args,
                Marionette.Generated.GeneratedManifest.Roots,
                adapter: null).GetAwaiter().GetResult();
        }

        if (wantsMcp && wantsHeadless)
        {
            // Pure stdio MCP server. No Application, no Dispatcher, no window.
            // Headless mode intentionally uses NoOpAdapter — there is no UI
            // thread to dispatch onto and no visual tree to screenshot.
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

        // GUI `--mcp` path: fall through to RunGui(). App.OnStartup calls
        // MarionetteWpf.AttachTo, which constructs the WpfUiAutomationAdapter,
        // starts MarionetteHost.RunAsync on a background task, and hooks
        // Application.Exit for clean shutdown.
        if (wantsMcp)
        {
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
