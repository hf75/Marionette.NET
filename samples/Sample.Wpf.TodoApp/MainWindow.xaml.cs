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
    /// <summary>
    /// Phase 3.3: each MainWindow keeps its own ViewModel reference instead
    /// of always reading <see cref="TodoListViewModel.Shared"/>. Default
    /// path (single-window) still binds to Shared so Phase 1/2/3.1/3.2
    /// behaviour is unchanged. The <c>--two-windows</c> path constructs a
    /// second window with a non-Shared ViewModel.
    /// </summary>
    private readonly TodoListViewModel _vm;

    public MainWindow() : this(TodoListViewModel.Shared) { }

    public MainWindow(TodoListViewModel viewModel)
    {
        _vm = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));

        InitializeComponent();

        // Bind to the SUPPLIED instance (defaults to TodoListViewModel.Shared
        // for single-window mode; the secondary --two-windows window passes a
        // fresh, non-Shared instance).
        DataContext = _vm;

        // Pre-seed two demo items so the screenshot in adopter-facing demos
        // shows a non-empty list. Only do this for the FIRST construction of
        // a given ViewModel so a fresh secondary window starts empty (the
        // multi-window assertions can rely on a 0 baseline).
        if (_vm.Items.Count == 0 && ReferenceEquals(_vm, TodoListViewModel.Shared))
        {
            _vm.AddTodo("Read the Marionette README");
            _vm.AddTodo("Decorate my ViewModel with [McpCallable]");
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

        _vm.AddTodo(title);
        NewTodoTextBox.Clear();
        NewTodoTextBox.Focus();
    }

    private void RemoveItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not TodoItem item) return;

        var index = _vm.Items.IndexOf(item);
        if (index >= 0)
        {
            _vm.RemoveTodo(index);
        }
    }

    private void ClearCompletedButton_Click(object sender, RoutedEventArgs e) =>
        _vm.ClearCompleted();
}
