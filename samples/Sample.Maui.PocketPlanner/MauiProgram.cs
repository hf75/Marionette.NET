// Sample.Maui.PocketPlanner - MauiProgram
//
// Standard MAUI 10.x app builder pattern. App.OnStart wires Marionette;
// the headless mode is handled in Platforms\Windows\App.xaml.cs / Program
// before this builder is even invoked.

using Microsoft.Extensions.Logging;

namespace Sample.Maui.PocketPlanner;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                // Resources/Fonts/* would land here. The PocketPlanner sample
                // ships no custom fonts; the OS default is fine.
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
