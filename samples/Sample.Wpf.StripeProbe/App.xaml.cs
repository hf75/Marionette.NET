using System.Windows;

namespace Sample.Wpf.StripeProbe;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Phase 1.2: the runtime no longer needs an Application-side bootstrap
        // call. The host's RunAsync receives the adapter instance directly via
        // its `adapter:` parameter. Phase 1.3 will pass the WPF adapter through
        // from Program.cs; for Phase 1.2 the GUI `--mcp` path passes
        // `adapter: null` (NoOpAdapter fallback).
    }
}
