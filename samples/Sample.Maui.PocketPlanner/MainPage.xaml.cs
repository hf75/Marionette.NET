// Sample.Maui.PocketPlanner - MainPage code-behind
//
// Pure UI glue. The PlannerViewModel does the heavy lifting; this file just
// wires control events to ViewModel methods. Mirrors the WPF / Avalonia /
// WinUI samples - the MainPage's BindingContext is the SAME
// PlannerViewModel.Shared instance the runtime's RootDescriptor.Create
// factory was rewritten to return in App.OnStart. So when the LLM calls
// AddAppointment / MoveAppointment / RemoveAppointment / ... via
// invoke_method, the bound CollectionView and counts update live.
//
// What this file deliberately does NOT contain:
//   * Any [McpCallable] / [McpObservable] / [McpEvent] attributes - those
//     live on the ViewModel where they belong.
//   * Any reference to Marionette types - the UI is framework-agnostic, the
//     same XAML+code-behind pattern works without any MCP wiring at all.

using System;

namespace Sample.Maui.PocketPlanner;

public partial class MainPage : ContentPage
{
    /// <summary>
    /// The shared ViewModel instance bound by the XAML <c>Binding</c> markup.
    /// Reaches the same singleton the runtime's manifest registry holds (the
    /// App.OnStart factory rewrite ensures parity).
    /// </summary>
    public PlannerViewModel ViewModel { get; } = PlannerViewModel.Shared;

    public MainPage()
    {
        InitializeComponent();
        BindingContext = ViewModel;

        // Default the date-picker to today so the LLM (and the user) see a
        // sensible starting value.
        DatePicker.Date = DateTime.Today;
    }

    private void AddButton_Clicked(object? sender, EventArgs e)
    {
        // Combine the entered title with the picked date (snap to 09:00).
        var title = string.IsNullOrWhiteSpace(TitleEntry.Text)
            ? "New appointment"
            : TitleEntry.Text;
        var picked = DatePicker.Date ?? DateTime.Today;
        var start = picked.Date.AddHours(9);
        ViewModel.AddAppointment(title, start);
        TitleEntry.Text = string.Empty;
    }

    private void ClearButton_Clicked(object? sender, EventArgs e) =>
        ViewModel.Clear();
}
