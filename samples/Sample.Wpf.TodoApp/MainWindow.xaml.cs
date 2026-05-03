// Sample.Wpf.TodoApp — MainWindow code-behind
//
// Pure UI glue. The TodoListViewModel does the heavy lifting; this file just
// wires button clicks to ViewModel methods, including a couple of items the
// adopters' eyes will be drawn to:
//
//   * The DataContext binds to the SAME `TodoListViewModel.Shared` instance
//     the runtime's RootDescriptor.Create factory was rewritten to return in
//     App.OnStartup. So when the LLM calls `AddTodo("buy milk")` via
//     invoke_method, the ObservableCollection mutation surfaces in the UI as
//     a new row and the bound counts refresh — no extra plumbing.
//   * Per-item CheckBox toggles call the ViewModel's INPC paths, which
//     re-emit CompletedCount / PendingCount property-changed events; the
//     runtime's WatchableResourceProvider hooks PropertyChanged and pushes
//     `notifications/resources/updated` to subscribers within a 200 ms
//     coalesce window.
//   * The Add textbox supports Enter as a shortcut.
//
// What this file deliberately does NOT contain:
//
//   * Any [McpCallable] / [McpObservable] / [McpTriggerable] attributes —
//     those live on the ViewModel where they belong.
//   * Any reference to Marionette types — the UI is framework-agnostic, the
//     same XAML+code-behind pattern works without any MCP wiring at all.
//   * Any direct manipulation of the items collection — call ViewModel
//     methods so [McpCallable] semantics and UI semantics stay identical.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Sample.Wpf.TodoApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Bind the SAME instance the manifest registry holds. See
        // App.OnStartup for the descriptor-factory rewrite that ties the
        // runtime's root instance to TodoListViewModel.Shared.
        DataContext = TodoListViewModel.Shared;

        // Pre-seed two demo items so the screenshot in adopter-facing demos
        // shows a non-empty list. Adopters who don't want this can delete the
        // call.
        var vm = TodoListViewModel.Shared;
        if (vm.Items.Count == 0)
        {
            vm.AddTodo("Read the Marionette README");
            vm.AddTodo("Decorate my ViewModel with [McpCallable]");
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => SubmitNewTodo();

    private void NewTodoTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SubmitNewTodo();
            e.Handled = true;
        }
    }

    private void SubmitNewTodo()
    {
        var title = NewTodoTextBox.Text;
        if (string.IsNullOrWhiteSpace(title)) return;

        TodoListViewModel.Shared.AddTodo(title);
        NewTodoTextBox.Clear();
        NewTodoTextBox.Focus();
    }

    private void RemoveItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not TodoItem item) return;

        var index = TodoListViewModel.Shared.Items.IndexOf(item);
        if (index >= 0)
        {
            TodoListViewModel.Shared.RemoveTodo(index);
        }
    }

    private void ClearCompletedButton_Click(object sender, RoutedEventArgs e) =>
        TodoListViewModel.Shared.ClearCompleted();
}
