// Sample.WinUI.FormLab - custom Main entry point
//
// Three modes, picked from argv (mirrors Sample.Wpf.TodoApp / Sample.Avalonia.Dashboard):
//
//   (no args)                  -> normal WinUI GUI
//   --mcp                      -> start MCP server on stdio AND show GUI
//                                 (Claude controls + user watches)
//   --mcp --headless           -> start MCP server on stdio, no Application / no window
//   --mcp-help                 -> print manifest summary to stderr and exit
//
// Headless paths bypass the WinUI Application entirely; they call
// MarionetteHost.RunAsync directly with adapter:null (NoOpAdapter). In that
// mode capture_screenshot returns the documented `screenshot_not_supported`
// structured error, and method invocations run inline rather than dispatched.
//
// GUI `--mcp` path: falls through to RunGui() which boots the WinUI runtime.
// App.OnLaunched then runs the descriptor-factory rewrite + MarionetteWinUI.AttachTo
// (see App.xaml.cs for the why).
//
// WinUI 3 unpackaged-app initialisation: WindowsAppSDK 1.x ships a "self-contained"
// model where (for unpackaged scenarios) the app does NOT need to call
// Bootstrap.Initialize() if the runtime DLL set is deployed alongside the .exe
// (UseRidGraph + the appropriate RuntimeIdentifier in csproj). For Phase 3.2
// we lean on this self-contained mode - no manual bootstrap call needed; the
// SDK's MSBuild targets stage everything correctly.

using System;
using System.Threading.Tasks;

namespace Sample.WinUI.FormLab;

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
            // We pass GeneratedManifest.Roots directly (no Shared rewrite) -
            // the help path doesn't construct any roots, just lists them.
            return Marionette.Runtime.MarionetteHost.RunAsync(
                args,
                Marionette.Generated.GeneratedManifest.Roots,
                adapter: null).GetAwaiter().GetResult();
        }

        if (wantsMcp && wantsHeadless)
        {
            // Pure stdio MCP server. No Application, no DispatcherQueue, no
            // window. The headless registry will materialise FormLabViewModel
            // via the generator's `new FormLabViewModel()` factory directly -
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
                Console.Error.WriteLine($"[formlab] MCP host crashed: {ex}");
                return 1;
            }
        }

        // GUI `--mcp` path: fall through to RunGui(). App.OnLaunched rewrites
        // the descriptor factory to return FormLabViewModel.Shared and then
        // calls MarionetteWinUI.AttachTo, which spawns the host on a background
        // task and hooks Window.Closed for clean shutdown.
        if (wantsMcp)
        {
            return RunGui();
        }
#endif

        // Default - normal WinUI, no MCP.
        return RunGui();
    }

    private static int RunGui()
    {
        // WinUI 3's recommended entrypoint shape: ComWrappersSupport.InitializeComWrappers
        // (called by the SDK's auto-injected initialization) + Application.Start
        // with a callback that creates the App. WinUI handles the message
        // pump from there.
        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var ctx = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
        return 0;
    }
}
