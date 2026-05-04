// Sample.WinUI.FormLab - MainWindow code-behind
//
// Pure UI glue. The FormLabViewModel does the heavy lifting; this file just
// wires control events to ViewModel methods. Mirrors the Sample.Wpf.TodoApp
// and Sample.Avalonia.Dashboard structures - the MainWindow's ViewModel
// property binds the SAME FormLabViewModel.Shared instance the runtime's
// RootDescriptor.Create factory was rewritten to return in App.OnLaunched.
// So when the LLM calls SetName / SetAge / Submit / ... via invoke_method,
// the bound TextBox/NumberBox/ToggleSwitch values update live, and the bound
// counts refresh.
//
// What this file deliberately does NOT contain:
//   * Any [McpCallable] / [McpObservable] / [McpEvent] attributes - those live
//     on the ViewModel where they belong.
//   * Any reference to Marionette types - the UI is framework-agnostic, the
//     same XAML+code-behind pattern works without any MCP wiring at all.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sample.WinUI.FormLab;

public sealed partial class MainWindow : Window
{
    /// <summary>
    /// The shared ViewModel instance bound by the XAML <c>x:Bind</c> markup.
    /// Reaches the same singleton the runtime's manifest registry holds (the
    /// App.OnLaunched factory rewrite ensures parity).
    /// </summary>
    public FormLabViewModel ViewModel => FormLabViewModel.Shared;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        ViewModel.SetName(tb.Text);
    }

    private void AgeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // NumberBox.Value is double - cast for the int-typed ViewModel API.
        // NaN happens during empty-input edits; guard.
        var v = double.IsNaN(args.NewValue) ? 0 : (int)args.NewValue;
        ViewModel.SetAge(v);
    }

    private void NotificationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts) return;
        // Only call ToggleNotifications when the user-driven IsOn diverges from
        // the model state - prevents recursive toggles when the OneWay binding
        // re-applies the model value.
        if (ts.IsOn != ViewModel.NotificationsEnabled)
        {
            ViewModel.ToggleNotifications();
        }
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cb) return;
        if (cb.SelectedItem is string theme && theme != ViewModel.Theme)
        {
            ViewModel.SetTheme(theme);
        }
    }

    private void SubmitButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Submit();

    private void ResetButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Reset();
}
