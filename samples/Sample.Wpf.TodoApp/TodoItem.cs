// Sample.Wpf.TodoApp — TodoItem
//
// Single TODO row in the list. Implements INotifyPropertyChanged so that the
// IsDone toggle (data-bound CheckBox) propagates to the ViewModel's derived
// observables (CompletedCount / PendingCount). The Title is set once at
// construction; Marionette's RenameTodo uses Title's setter to update the row
// in-place and the binding refreshes the visible label.

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sample.Wpf.TodoApp;

/// <summary>
/// One row in the TODO list. Plain INPC class — no records here because WPF
/// data binding to a derived property on a record requires more ceremony than
/// an explicit class with manual change notification.
/// </summary>
public sealed class TodoItem : INotifyPropertyChanged
{
    private string _title;
    private bool _isDone;

    public TodoItem(string title)
    {
        _title = title ?? string.Empty;
    }

    /// <summary>The user-facing label of this todo.</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    /// <summary>Whether this todo has been completed.</summary>
    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (_isDone == value) return;
            _isDone = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
