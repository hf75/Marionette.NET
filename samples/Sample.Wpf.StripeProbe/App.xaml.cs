using System.Windows;

#if MCP_ENABLED
using Marionette.Adapter.Wpf;
using Marionette.Generated;
#endif

namespace Sample.Wpf.StripeProbe;

public partial class App : Application
{
#if MCP_ENABLED
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 4.2: StripeProbe forwards into MarionetteWpf.AttachTo which carries " +
                        "the cascading raise_event reflection warning. The probe doesn't use " +
                        "raise_event.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Phase 4.2: cascading IL3050 from STJ.")]
#endif
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
