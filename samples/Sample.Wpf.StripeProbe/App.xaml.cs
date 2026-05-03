using System.Windows;

namespace Sample.Wpf.StripeProbe;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if MCP_ENABLED
        // The Adapter reference is only present when MCP_ENABLED is defined,
        // so this call must be inside an #if block — otherwise the compiler
        // would need the type even in stripped builds.
        Marionette.Adapter.Wpf.WpfMarionetteBootstrap.Initialize(this);
#endif
    }
}
