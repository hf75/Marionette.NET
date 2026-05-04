// Sample.Maui.PocketPlanner - Windows-platform App shell
//
// Standard MauiWinUIApplication subclass. This is the platform-specific
// entry that delegates back to the cross-platform MauiProgram.CreateMauiApp
// for the rest of the wiring. The MAUI tooling generates the
// Program.Main from this class via the [DllImport]-style entry point.
//
// Phase 4.1 ships its own custom Main in Platforms/Windows/Program.cs that
// branches on `--mcp --headless` BEFORE this class is constructed; the
// disabled Main here lets us own the entry point.

using Microsoft.UI.Xaml;

namespace Sample.Maui.PocketPlanner.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
