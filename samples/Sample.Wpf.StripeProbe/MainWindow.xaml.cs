using System.Windows;
using Marionette;

namespace Sample.Wpf.StripeProbe;

// Phase 1.b: short manifest name. The Phase-0 sample originally used the
// (now-renamed) constructor parameter as a free-form description; per
// 1a-foundation.md the ctor argument is the manifest name, defaulting to the
// type name when omitted. Using the short type name keeps the generator's
// output in the typical adopter shape.
[McpRoot]
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
