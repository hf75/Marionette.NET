// Sample.WinUI.FormLab - FormLabViewModel
//
// The canonical WinUI [McpRoot]. Adopters who land on this file should see
// at a glance:
//
//   * How [McpRoot] declares the root.
//   * How [McpCallable] decorates public methods that mutate form state,
//     including primitive parameter signatures (string, int, bool).
//   * How [McpObservable(Watchable=true)] exposes form fields for push
//     updates; INotifyPropertyChanged powers the live notifications channel
//     so subscribers don't poll.
//   * How [McpEvent] declares an event for declarative MCP delivery -
//     here the form-submission cycle, with a richer FormState payload.
//   * How a non-Window root shares a single instance with the MainWindow
//     (see FormLabViewModel.Shared and the App.OnLaunched wiring).
//
// "Real-world example" framing: this VM models a settings/configuration form
// with diverse input types (text, number, toggle, dropdown, two buttons),
// distinct from TodoApp's list and Dashboard's metric stream. Adopters can
// reuse the structure for any preferences / profile / setup-wizard UI.

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using Marionette;

using Sample.WinUI.FormLab.Models;

namespace Sample.WinUI.FormLab;

/// <summary>
/// EventArgs payload for <see cref="FormLabViewModel.FormSubmitted"/>. Carries
/// a complete <see cref="FormState"/> snapshot so subscribers receive the
/// submitted shape in one push without re-reading the observables.
/// </summary>
public sealed class FormSubmittedEventArgs : EventArgs
{
    public FormSubmittedEventArgs(FormState state)
    {
        State = state;
    }

    /// <summary>Form snapshot at the moment of submission.</summary>
    public FormState State { get; }

    // Surface the snapshot fields directly on the args object too — the
    // runtime serialises them into the event resource payload, so subscribers
    // can read args.Name / args.Age / args.NotificationsEnabled / args.Theme
    // without dereferencing args.State.
    public string Name => State.Name;
    public int Age => State.Age;
    public bool NotificationsEnabled => State.NotificationsEnabled;
    public string Theme => State.Theme;
}

/// <summary>
/// FormLab view-model: a small settings form with a diverse input mix
/// (name / age / notifications-toggle / theme-dropdown / submit / reset).
/// Decorated as the app's single [McpRoot]; every method or property the
/// LLM is meant to drive lives here.
/// </summary>
[McpRoot]
public sealed class FormLabViewModel : INotifyPropertyChanged
{
    private static FormLabViewModel? s_shared;
    private static readonly object s_sharedLock = new();

    /// <summary>
    /// Process-wide singleton shared between the WinUI MainWindow's
    /// DataContext and the runtime's RootDescriptor.Create factory. Both refer
    /// to the same instance so [McpCallable] mutations flip fields the user
    /// can see, and user changes in the GUI update the observables the LLM
    /// reads.
    /// </summary>
    /// <remarks>
    /// Same pattern as Sample.Wpf.TodoApp.TodoListViewModel.Shared and
    /// Sample.Avalonia.Dashboard.DashboardViewModel.Shared. The non-Window
    /// root needs the explicit factory rewrite in App.OnLaunched (see
    /// App.xaml.cs).
    /// </remarks>
    public static FormLabViewModel Shared
    {
        get
        {
            if (s_shared is not null) return s_shared;
            lock (s_sharedLock)
            {
                s_shared ??= new FormLabViewModel();
                return s_shared;
            }
        }
    }

    private string _name = string.Empty;
    private int _age;
    private bool _notificationsEnabled = true;
    private string _theme = "Default";
    private bool _hasSubmitted;

    public FormLabViewModel()
    {
        // First-construction-wins singleton.
        lock (s_sharedLock)
        {
            s_shared ??= this;
        }
    }

    // -------------------------------------------------------------------------
    // [McpCallable] surface - five form actions
    // -------------------------------------------------------------------------

    /// <summary>
    /// Set the display name. Empty / whitespace strings are accepted (the form
    /// surfaces a validation hint in the UI but the underlying state still
    /// updates). Returns no value; observe via the Name observable.
    /// </summary>
    [McpCallable("Set the user's display name on the form.")]
    public void SetName(string name)
    {
        // Mirror the XAML two-way binding: empty input is allowed and the form
        // surfaces a validation hint in the UI, but the underlying state
        // updates either way so the LLM and the user see the same value.
        var v = name ?? string.Empty;
        if (_name == v) return;
        _name = v;
        RaisePropertyChanged(nameof(Name));
    }

    /// <summary>
    /// Set the user's age. Negative values are clamped to zero.
    /// </summary>
    [McpCallable("Set the user's age. Negative values are clamped to zero.")]
    public void SetAge(int age)
    {
        var v = Math.Max(0, age);
        if (_age == v) return;
        _age = v;
        RaisePropertyChanged(nameof(Age));
    }

    /// <summary>
    /// Toggle the notifications-enabled state.
    /// </summary>
    [McpCallable("Toggle whether notifications are enabled.")]
    public void ToggleNotifications()
    {
        _notificationsEnabled = !_notificationsEnabled;
        RaisePropertyChanged(nameof(NotificationsEnabled));
    }

    /// <summary>
    /// Set the theme. Recognised values: "Light", "Dark", "Default". Other
    /// values fall back to "Default" with a logged warning.
    /// </summary>
    [McpCallable("Set the form's theme. One of: Light, Dark, Default.")]
    public void SetTheme(string theme)
    {
        var v = theme switch
        {
            "Light" or "Dark" or "Default" => theme,
            _ => "Default",
        };
        if (_theme == v) return;
        _theme = v;
        RaisePropertyChanged(nameof(Theme));
    }

    /// <summary>
    /// Submit the form. Captures the current state into a <see cref="FormState"/>
    /// snapshot and fires <see cref="FormSubmitted"/>. Adopters can subscribe
    /// via <c>marionette://FormLabViewModel/events/FormSubmitted</c>.
    /// </summary>
    [McpCallable("Submit the form. Captures current state and fires FormSubmitted.")]
    public void Submit()
    {
        var snapshot = new FormState(_name, _age, _notificationsEnabled, _theme);
        if (!_hasSubmitted)
        {
            _hasSubmitted = true;
            RaisePropertyChanged(nameof(HasSubmitted));
        }
        FormSubmitted?.Invoke(this, new FormSubmittedEventArgs(snapshot));
    }

    /// <summary>
    /// Reset every field to its initial default value.
    /// </summary>
    [McpCallable("Reset all fields to defaults: name='', age=0, notifications=true, theme='Default'.")]
    public void Reset()
    {
        var changed = false;
        if (_name.Length != 0)
        {
            _name = string.Empty;
            RaisePropertyChanged(nameof(Name));
            changed = true;
        }
        if (_age != 0)
        {
            _age = 0;
            RaisePropertyChanged(nameof(Age));
            changed = true;
        }
        if (!_notificationsEnabled)
        {
            _notificationsEnabled = true;
            RaisePropertyChanged(nameof(NotificationsEnabled));
            changed = true;
        }
        if (_theme != "Default")
        {
            _theme = "Default";
            RaisePropertyChanged(nameof(Theme));
            changed = true;
        }
        // HasSubmitted intentionally NOT reset - it's a "form has been used at
        // least once" sentinel that survives Reset. Adopters can read it via
        // read_observable to know whether any prior submission has happened.
        _ = changed;
    }

    // -------------------------------------------------------------------------
    // [McpObservable] surface - watchable + non-watchable
    // -------------------------------------------------------------------------

    /// <summary>Current display name.</summary>
    [McpObservable("Current display name.", Watchable = true)]
    public string Name => _name;

    /// <summary>Current age.</summary>
    [McpObservable("Current age (non-negative integer).", Watchable = true)]
    public int Age => _age;

    /// <summary>Whether notifications are enabled.</summary>
    [McpObservable("Whether notifications are enabled.", Watchable = true)]
    public bool NotificationsEnabled => _notificationsEnabled;

    /// <summary>Current theme. Non-watchable to demonstrate the alternative shape.</summary>
    [McpObservable("Current theme: Light, Dark, or Default.")]
    public string Theme => _theme;

    /// <summary>
    /// Whether the form has been submitted at least once since startup.
    /// Reset() does NOT clear this flag - it's a "form was used" sentinel.
    /// </summary>
    [McpObservable("Whether the form has been submitted at least once.")]
    public bool HasSubmitted => _hasSubmitted;

    // -------------------------------------------------------------------------
    // [McpEvent] surface
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fires when <see cref="Submit"/> is called. Subscribers to
    /// <c>marionette://FormLabViewModel/events/FormSubmitted</c> receive a
    /// notification carrying the submitted state on every fire.
    /// </summary>
    [McpEvent("Fired when the form is submitted with the current state.")]
    public event EventHandler<FormSubmittedEventArgs>? FormSubmitted;

    // -------------------------------------------------------------------------
    // INotifyPropertyChanged plumbing
    // -------------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaisePropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
