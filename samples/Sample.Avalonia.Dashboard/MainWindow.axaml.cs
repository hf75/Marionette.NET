// Sample.Avalonia.Dashboard - MainWindow code-behind
//
// Pure UI glue. The DashboardViewModel does the heavy lifting; this file just
// wires button clicks to ViewModel methods. Mirrors the Sample.Wpf.TodoApp
// structure - the MainWindow's DataContext binds the SAME
// DashboardViewModel.Shared instance the runtime's RootDescriptor.Create
// factory was rewritten to return in App.OnFrameworkInitializationCompleted.
// So when the LLM calls UpsertMetric / RefreshAsync / ... via invoke_method,
// the ObservableCollection mutation surfaces in the UI as live updates and
// the bound counts refresh.
//
// What this file deliberately does NOT contain:
//   * Any [McpCallable] / [McpObservable] / [McpEvent] attributes - those live
//     on the ViewModel where they belong.
//   * Any reference to Marionette types - the UI is framework-agnostic, the
//     same XAML+code-behind pattern works without any MCP wiring at all.

using System.Globalization;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Sample.Avalonia.Dashboard;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Bind the SAME instance the manifest registry holds. App.OnFrameworkInitializationCompleted
        // rewrites the RootDescriptor.Create factory to return DashboardViewModel.Shared
        // so the runtime + UI share state.
        DataContext = DashboardViewModel.Shared;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void UpsertButton_Click(object? sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text;
        var unit = UnitTextBox.Text;
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!double.TryParse(ValueTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return;
        DashboardViewModel.Shared.UpsertMetric(name!, value, unit ?? string.Empty);
        ValueTextBox.Text = string.Empty;
        NameTextBox.Focus();
    }

    private void RemoveMetricButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not string name) return;
        DashboardViewModel.Shared.RemoveMetric(name);
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        // Fire-and-forget; the UI binding live-updates from the awaited
        // dispatcher continuation inside RefreshAsync.
        _ = DashboardViewModel.Shared.RefreshAsync();
    }

    private void TogglePauseButton_Click(object? sender, RoutedEventArgs e) =>
        DashboardViewModel.Shared.TogglePaused();

    private void ResetAllButton_Click(object? sender, RoutedEventArgs e) =>
        DashboardViewModel.Shared.ResetAll();
}
