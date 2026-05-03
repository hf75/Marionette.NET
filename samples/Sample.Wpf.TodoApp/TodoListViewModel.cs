// Sample.Wpf.TodoApp — TodoListViewModel
//
// The canonical [McpRoot] reference. Adopters who land on this file should see
// at a glance:
//
//   * How [McpRoot] declares the root.
//   * How [McpCallable] decorates public methods that perform a meaningful
//     change to model state.
//   * How [McpObservable(Watchable = true)] exposes derived state for push
//     updates (via INotifyPropertyChanged on this class).
//   * How a non-Window root shares a single instance with the MainWindow
//     (see TodoListViewModel.Shared and the App.OnStartup wiring).
//   * How a callable that mutates the list also fires PropertyChanged for the
//     derived observable names so MCP subscribers get coalesced updates.
//
// Crucial design call (PHASE0_FINDINGS implication on watchable observables):
// this class implements INotifyPropertyChanged so that the runtime's
// WatchableResourceProvider attaches a PropertyChanged handler instead of
// falling back to polling at PollingIntervalMs. Push updates feel
// substantially nicer in demos than 500 ms polling.
//
// Trapdoor avoided: when [McpCallable] methods mutate the list, the runtime's
// resource provider may try to read the observable from the same callstack as
// the PropertyChanged event. We use the standard PropertyChanged event pattern
// (synchronous fire) and the resource provider has a 200 ms coalesce window —
// so a burst of "AddTodo, AddTodo, AddTodo" in rapid succession collapses to a
// single notifications/resources/updated push, not three.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

using Marionette;

namespace Sample.Wpf.TodoApp;

/// <summary>
/// View-model backing the MainWindow's TODO list. Decorated as the app's
/// single [McpRoot]; every method or property the LLM is meant to drive lives
/// here.
/// </summary>
[McpRoot]
public sealed class TodoListViewModel : INotifyPropertyChanged
{
    private static TodoListViewModel? s_shared;
    private static readonly object s_sharedLock = new();

    /// <summary>
    /// Process-wide singleton shared between the WPF MainWindow's DataContext
    /// and the runtime's RootDescriptor.Create factory. Both refer to the same
    /// instance so [McpCallable] mutations flip checkboxes the user can see,
    /// and user clicks in the GUI update the observables the LLM reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why a static singleton, not a service-locator / DI container? In v1
    /// Marionette has no opinion on how the user wires their app; the simplest
    /// pattern that keeps the runtime registry's instance and the
    /// DataContext's instance identical is a static singleton.
    /// Phase 7's NuGet templating may surface this pattern as a
    /// <c>[McpSharedInstance]</c> shorthand; for now adopters do it by hand.
    /// </para>
    /// <para>
    /// Implementation note: the source-generator emits the descriptor factory
    /// as <c>static () =&gt; new TodoListViewModel()</c>, and <c>MainWindow</c>
    /// reads <see cref="Shared"/> for its DataContext. Whichever runs first
    /// wins; the ctor stores <c>this</c> into <see cref="s_shared"/> and the
    /// other caller picks it up. <see cref="MarionetteWpf.AttachTo"/>
    /// dispatches the factory to the WPF UI thread so STA + INPC wiring is
    /// always satisfied.
    /// </para>
    /// </remarks>
    public static TodoListViewModel Shared
    {
        get
        {
            // Get-or-create: if the runtime hasn't materialised the instance
            // yet (e.g. normal no-MCP GUI mode, or the WPF MainWindow loads
            // before the host's bg thread reaches the descriptor factory),
            // create it here. The ctor populates s_shared.
            if (s_shared is not null) return s_shared;
            lock (s_sharedLock)
            {
                s_shared ??= new TodoListViewModel();
                return s_shared;
            }
        }
    }

    private readonly ObservableCollection<TodoItem> _items = new();

    /// <summary>
    /// Underlying observable list, bound to the WPF ItemsControl. Not exposed
    /// as [McpObservable] — adopters can read the list shape via the derived
    /// Total/Completed/Pending counts plus LastAddedTitle. Phase 2's
    /// list-typed observables will let this become a single observable; Phase
    /// 1 keeps things primitive-typed for the simplest LLM marshalling.
    /// </summary>
    public ObservableCollection<TodoItem> Items => _items;

    public TodoListViewModel()
    {
        // First-construction-wins singleton: whoever runs the ctor first
        // installs themselves as the shared instance. The Shared getter only
        // creates a new one when s_shared is null, so the second caller picks
        // up the first one and never reaches this ctor.
        lock (s_sharedLock)
        {
            s_shared ??= this;
        }

        // Subscribe so per-item IsDone toggles refresh the derived counts.
        // The CollectionChanged handler also (un)hooks per-item INPC so we
        // pick up rename + done-toggle for items added later.
        _items.CollectionChanged += OnItemsChanged;
    }

    // -------------------------------------------------------------------------
    // [McpCallable] surface — five meaningful actions
    // -------------------------------------------------------------------------

    /// <summary>
    /// Add a new TODO with the given title. The item is appended to the end
    /// of the list and the derived counts are refreshed.
    /// </summary>
    [McpCallable("Add a new TODO with the given title; appends to the end of the list.")]
    public void AddTodo(string title)
    {
        // Reject empty / null titles silently rather than throwing — the LLM
        // gets a clean "list grew by 0" outcome and a fresh read of
        // TotalCount confirms the no-op.
        if (string.IsNullOrWhiteSpace(title)) return;

        _items.Add(new TodoItem(title.Trim()));
        // CollectionChanged fires the count refreshes; OnLastAddedTitleChanged
        // fires LastAddedTitle's PropertyChanged.
        RaisePropertyChanged(nameof(LastAddedTitle));
    }

    /// <summary>
    /// Remove the TODO at the given index. No-op if the index is out of range.
    /// </summary>
    [McpCallable("Remove the TODO at the given zero-based index. Index out of range is a no-op.")]
    public void RemoveTodo(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _items.RemoveAt(index);
        RaisePropertyChanged(nameof(LastAddedTitle));
    }

    /// <summary>
    /// Toggle the done state of the TODO at the given index. No-op if the
    /// index is out of range.
    /// </summary>
    [McpCallable("Flip the done flag on the TODO at the given zero-based index. Index out of range is a no-op.")]
    public void ToggleDone(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        item.IsDone = !item.IsDone;
        // OnItemPropertyChanged refreshes Completed/Pending counts.
    }

    /// <summary>
    /// Remove every TODO that is currently marked done.
    /// </summary>
    [McpCallable("Remove every TODO whose IsDone is true.")]
    public void ClearCompleted()
    {
        // Walk in reverse so RemoveAt indices stay valid.
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (_items[i].IsDone) _items.RemoveAt(i);
        }
        RaisePropertyChanged(nameof(LastAddedTitle));
    }

    /// <summary>
    /// Replace the title of the TODO at the given index. No-op if the index
    /// is out of range or the new title is null/whitespace.
    /// </summary>
    [McpCallable("Replace the title of the TODO at the given zero-based index. Empty / out-of-range arguments are no-ops.")]
    public void RenameTodo(int index, string newTitle)
    {
        if (index < 0 || index >= _items.Count) return;
        if (string.IsNullOrWhiteSpace(newTitle)) return;
        _items[index].Title = newTitle.Trim();
        // If the renamed item happens to be the last-added one, the derived
        // LastAddedTitle expression still resolves correctly because we read
        // it lazily — but we still fire a property-changed signal because the
        // emitted text changed.
        if (index == _items.Count - 1)
        {
            RaisePropertyChanged(nameof(LastAddedTitle));
        }
    }

    // -------------------------------------------------------------------------
    // [McpObservable] surface — three watchable counts + last-added title
    // -------------------------------------------------------------------------

    /// <summary>
    /// Total number of TODOs in the list.
    /// </summary>
    [McpObservable("Total number of todos.", Watchable = true)]
    public int TotalCount => _items.Count;

    /// <summary>
    /// Number of TODOs whose IsDone is true.
    /// </summary>
    [McpObservable("Number of completed todos.", Watchable = true)]
    public int CompletedCount => _items.Count(i => i.IsDone);

    /// <summary>
    /// Number of TODOs whose IsDone is false. Derived as Total - Completed.
    /// </summary>
    [McpObservable("Number of pending todos.", Watchable = true)]
    public int PendingCount => TotalCount - CompletedCount;

    /// <summary>
    /// Title of the most recently added TODO, or null if the list is empty.
    /// Non-watchable — adopters who want push updates here would mark it
    /// Watchable=true; we keep one observable non-watchable in this sample so
    /// the LLM has both shapes side-by-side.
    /// </summary>
    [McpObservable("Most recently added todo title or null if empty.")]
    public string? LastAddedTitle => _items.LastOrDefault()?.Title;

    // -------------------------------------------------------------------------
    // INotifyPropertyChanged plumbing + collection-change wiring
    // -------------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaisePropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// React to add/remove/replace on the underlying ObservableCollection by:
    ///   1. Hooking PropertyChanged on each NEW item so per-item flips refresh
    ///      the derived counts.
    ///   2. Unhooking from REMOVED items so we don't leak handlers.
    ///   3. Firing PropertyChanged for every derived observable name. The
    ///      WatchableResourceProvider has a 200 ms coalesce window so a burst
    ///      of operations collapses to one notifications/resources/updated
    ///      push.
    /// </summary>
    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<TodoItem>())
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<TodoItem>())
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }
        // Total/Completed/Pending all change when the list grows/shrinks/replaces.
        RaisePropertyChanged(nameof(TotalCount));
        RaisePropertyChanged(nameof(CompletedCount));
        RaisePropertyChanged(nameof(PendingCount));
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodoItem.IsDone))
        {
            // IsDone flip changes Completed and Pending; Total is unchanged.
            RaisePropertyChanged(nameof(CompletedCount));
            RaisePropertyChanged(nameof(PendingCount));
        }
        else if (e.PropertyName == nameof(TodoItem.Title))
        {
            // A rename of the last item changes LastAddedTitle. We don't know
            // the index here without scanning, so for safety re-emit on every
            // title change. Cheap; resource provider coalesces the burst.
            RaisePropertyChanged(nameof(LastAddedTitle));
        }
    }
}
