// Sample.Avalonia.Dashboard - custom Main entry point
//
// Three modes, picked from argv (mirrors Sample.Wpf.TodoApp):
//
//   (no args)                  -> normal Avalonia GUI
//   --mcp                      -> start MCP server on stdio AND show GUI
//                                 (Claude controls + user watches)
//   --mcp --headless           -> start MCP server on stdio, no Application / no window
//   --mcp-help                 -> print manifest summary to stderr and exit
//
// Headless paths bypass the Avalonia AppBuilder entirely; they call
// MarionetteHost.RunAsync directly with adapter:null (NoOpAdapter). In that
// mode capture_screenshot returns the documented `screenshot_not_supported`
// structured error, and method invocations run inline rather than dispatched.
//
// GUI `--mcp` path: falls through to BuildAvaloniaApp().StartWithClassicDesktopLifetime
// which constructs the Avalonia App. App.OnFrameworkInitializationCompleted
// then runs the descriptor-factory rewrite + MarionetteAvalonia.AttachTo.

using System;
using System.Threading.Tasks;

using Avalonia;

namespace Sample.Avalonia.Dashboard;

internal static class Program
{
    // Avalonia's recommended entrypoint shape: the SDK calls Main with argv,
    // we wire the AppBuilder + classic-desktop lifetime, and the lifetime owns
    // the message-pump. STAThread is unnecessary on Avalonia.
    [STAThread]
#if MCP_ENABLED
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 4.2: Dashboard's headless path calls MarionetteHost.RunAsync " +
                        "directly. The cascading raise_event reflection warning is acknowledged here.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Phase 4.2: cascading IL3050 from STJ.")]
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
            // The help path doesn't construct any roots, just lists them.
            return Marionette.Runtime.MarionetteHost.RunAsync(
                args,
                Marionette.Generated.GeneratedManifest.Roots,
                adapter: null).GetAwaiter().GetResult();
        }

        if (wantsMcp && wantsHeadless)
        {
            // Pure stdio MCP server. No Application, no Dispatcher, no window.
            // The headless registry will materialise DashboardViewModel via
            // the generator's `new DashboardViewModel()` factory directly -
            // that's fine because there's no UI here for the instance to be
            // out of sync with. Adopters running headless tests get a fresh
            // ViewModel every host start.
            try
            {
                return Marionette.Runtime.MarionetteHost.RunAsync(
                    args,
                    Marionette.Generated.GeneratedManifest.Roots,
                    adapter: null).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[avalonia-dashboard] MCP host crashed: {ex}");
                return 1;
            }
        }

        // GUI `--mcp` path: fall through to RunGui(). App.OnFrameworkInitializationCompleted
        // rewrites the descriptor factory to return DashboardViewModel.Shared
        // and then calls MarionetteAvalonia.AttachTo.
        if (wantsMcp)
        {
            return RunGui(args);
        }
#endif

        // Default - normal Avalonia, no MCP.
        return RunGui(args);
    }

    private static int RunGui(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// AppBuilder shape used by both the regular GUI run and Avalonia's
    /// design-time previewer (the IDE introspects this method by reflection
    /// so it must stay public-static and signature-stable).
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
