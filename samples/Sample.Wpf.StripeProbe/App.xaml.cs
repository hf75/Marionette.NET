using System.Windows;

#if MCP_ENABLED
using Marionette.Adapter.Wpf;
using Marionette.Generated;
#endif

namespace Sample.Wpf.StripeProbe;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if MCP_ENABLED
        // Phase 1.3: one-line wiring for the GUI `--mcp` path. Without --mcp
        // in the args, MarionetteWpf.AttachTo / MarionetteHost.RunAsync return
        // immediately, so this is also safe in the no-flag GUI path.
        //
        // The `--mcp --headless` path never reaches App — it stays in Program.Main
        // and calls MarionetteHost.RunAsync directly with adapter:null (NoOpAdapter).
        MarionetteWpf.AttachTo(this, GeneratedManifest.Roots, e.Args);
#endif
    }
}
