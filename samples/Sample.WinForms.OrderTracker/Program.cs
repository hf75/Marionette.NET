// Sample.WinForms.OrderTracker — custom Main entry point
//
// Three modes (mirrors Sample.Wpf.TodoApp):
//   (no args)              → normal WinForms GUI
//   --mcp                  → start MCP server on stdio AND show GUI
//   --mcp --headless       → start MCP server on stdio, no Application / no Form
//   --mcp-help             → print manifest summary to stderr and exit
//
// Headless paths bypass Application.Run entirely — they call
// MarionetteHost.RunAsync directly with adapter:null (NoOpAdapter). In that
// mode capture_screenshot returns the documented `screenshot_not_supported`
// structured error, but [McpCallable] / [McpObservable] / [McpEvent] all work.

using System;

namespace Sample.WinForms.OrderTracker;

internal static class Program
{
    [STAThread]
#if MCP_ENABLED
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 15: OrderTracker uses [McpCallable] + [McpObservable] + [McpEvent]; raise_event is not exercised. Cascading IL2026 from MarionetteHost.RunAsync acknowledged here.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Phase 15: cascading IL3050 from STJ.")]
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
            // Pure stdio MCP. No Application.Run, no Form. The headless
            // registry materialises OrderViewModel via the source-gen factory
            // (which we substitute below to return Shared so observable reads
            // see consistent state across nested invocations within the same
            // host run).
            var bridgedRoots = BridgeRoots();
            try
            {
                return Marionette.Runtime.MarionetteHost.RunAsync(
                    args,
                    bridgedRoots,
                    adapter: null).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[order-tracker] headless host crashed: {ex}");
                return 2;
            }
        }
#endif
        // GUI path (with or without --mcp).
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        var form = new OrderTrackerForm(OrderViewModel.Shared);

#if MCP_ENABLED
        if (wantsMcp)
        {
            // Wire AttachTo from Form.Shown so the handle is guaranteed.
            form.Shown += (_, _) =>
            {
                try
                {
                    var bridged = BridgeRoots();
                    Marionette.Adapter.WinForms.MarionetteWinForms.AttachTo(form, bridged, args);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[order-tracker] MarionetteWinForms.AttachTo failed: {ex}");
                }
            };
        }
#endif

        System.Windows.Forms.Application.Run(form);
        return 0;
    }

#if MCP_ENABLED
    private static System.Collections.Generic.IReadOnlyList<Marionette.Runtime.Manifest.RootDescriptor> BridgeRoots()
    {
        var bridged = new System.Collections.Generic.List<Marionette.Runtime.Manifest.RootDescriptor>(Marionette.Generated.GeneratedManifest.Roots.Count);
        foreach (var root in Marionette.Generated.GeneratedManifest.Roots)
        {
            if (root.TypeName == typeof(OrderViewModel).FullName)
            {
                bridged.Add(root with { Create = static () => OrderViewModel.Shared });
            }
            else
            {
                bridged.Add(root);
            }
        }
        return bridged;
    }
#endif
}
