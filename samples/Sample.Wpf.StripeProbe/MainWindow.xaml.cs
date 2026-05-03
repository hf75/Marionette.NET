using System.Windows;
using Marionette;

namespace Sample.Wpf.StripeProbe;

[McpRoot("StripeProbe main window — Phase 0 fixture for IL stripping verification.")]
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    [McpObservable("The most recent sum result")]
    public int Result { get; private set; }

    [McpCallable("Adds two numbers")]
    public int Add(int a, int b) => a + b;

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        Result = Add(2, 3);
        ResultLabel.Text = $"Result = {Result}";

        // Channel push: this call should compile out entirely in Release-stripped builds.
        Ai.Trigger($"User clicked Add; result is {Result}.");
    }
}
