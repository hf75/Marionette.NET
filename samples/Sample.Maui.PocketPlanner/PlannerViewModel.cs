// Sample.Maui.PocketPlanner - PlannerViewModel
//
// The canonical MAUI [McpRoot]. Adopters who land on this file should see
// at a glance:
//
//   * How [McpRoot] declares the root.
//   * How [McpCallable] decorates public methods that mutate planner state,
//     including primitive parameter signatures (string, DateTime, int).
//   * How [McpObservable(Watchable=true)] exposes computed/derived state for
//     push updates; INotifyPropertyChanged powers the live notifications
//     channel so subscribers don't poll.
//   * How [McpEvent] declares an event for declarative MCP delivery -
//     here the appointment-added cycle, with a typed payload.
//   * How a non-Page root shares a single instance with the live MainPage
//     (see PlannerViewModel.Shared and the App.OnStart wiring).
//
// "Real-world example" framing: this VM models a daily appointment planner
// distinct from TodoApp's todo list, Dashboard's metric stream, and
// FormLab's settings form. Adopters can reuse the structure for any
// appointment / event / scheduling UI.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

using Marionette;

using Sample.Maui.PocketPlanner.Models;

namespace Sample.Maui.PocketPlanner;

/// <summary>
/// EventArgs payload for <see cref="PlannerViewModel.AppointmentAdded"/>.
/// Carries the title and start time so subscribers receive the new
/// appointment shape in one push without re-reading observables.
/// </summary>
public sealed class AppointmentAddedEventArgs : EventArgs
{
    public AppointmentAddedEventArgs(string title, DateTime startTime, int durationMinutes)
    {
        Title = title;
        StartTime = startTime;
        DurationMinutes = durationMinutes;
    }

    /// <summary>The added appointment's title.</summary>
    public string Title { get; }
    /// <summary>The added appointment's start time.</summary>
    public DateTime StartTime { get; }
    /// <summary>The added appointment's duration in minutes.</summary>
    public int DurationMinutes { get; }
}

/// <summary>
/// Pocket-planner view-model: a small daily appointment planner with
/// add / move / remove / complete actions plus aggregate observables.
/// Decorated as the app's single [McpRoot]; every method or property the
/// LLM is meant to drive lives here.
/// </summary>
[McpRoot]
public sealed class PlannerViewModel : INotifyPropertyChanged
{
    private static PlannerViewModel? s_shared;
    private static readonly object s_sharedLock = new();

    /// <summary>
    /// Process-wide singleton shared between the MAUI MainPage's
    /// BindingContext and the runtime's RootDescriptor.Create factory. Both
    /// refer to the same instance so [McpCallable] mutations flip fields the
    /// user can see, and user changes in the GUI update the observables the
    /// LLM reads.
    /// </summary>
    /// <remarks>
    /// Same pattern as TodoListViewModel.Shared / DashboardViewModel.Shared /
    /// FormLabViewModel.Shared. The non-Window/non-Page root needs the
    /// explicit factory rewrite in App.OnStart (see App.xaml.cs).
    /// </remarks>
    public static PlannerViewModel Shared
    {
        get
        {
            if (s_shared is not null) return s_shared;
            lock (s_sharedLock)
            {
                s_shared ??= new PlannerViewModel();
                return s_shared;
            }
        }
    }

    /// <summary>Live appointments. Bound by the MAUI MainPage's CollectionView.</summary>
    public ObservableCollection<Appointment> Appointments { get; } = new();

    public PlannerViewModel()
    {
        // First-construction-wins singleton.
        lock (s_sharedLock)
        {
            s_shared ??= this;
        }

        // Hook the collection so derived observables push updates whenever
        // items are added / removed / replaced. The MAUI dispatcher handles
        // its own UI-side marshalling for the bound CollectionView.
        Appointments.CollectionChanged += OnAppointmentsChanged;
    }

    // -------------------------------------------------------------------------
    // [McpCallable] surface - five planner actions
    // -------------------------------------------------------------------------

    /// <summary>
    /// Add a new appointment with the given title, start time, and duration.
    /// Default duration is 60 minutes when not specified.
    /// </summary>
    [McpCallable("Add a new appointment with the given title and start time.")]
    public void AddAppointment(string title, DateTime startTime, int durationMinutes = 60)
    {
        var t = string.IsNullOrWhiteSpace(title) ? "(untitled)" : title.Trim();
        var d = Math.Max(5, durationMinutes); // never zero / negative duration
        var a = new Appointment(t, startTime, d);
        Appointments.Add(a);
        AppointmentAdded?.Invoke(this, new AppointmentAddedEventArgs(t, startTime, d));
        // The CollectionChanged subscription below fires the derived
        // observables; LastAddedTitle is computed from the collection state.
    }

    /// <summary>
    /// Remove the appointment at the given zero-based index. Out-of-range
    /// indexes are no-ops with a logged warning.
    /// </summary>
    [McpCallable("Remove an appointment by zero-based index.")]
    public void RemoveAppointment(int index)
    {
        if (index < 0 || index >= Appointments.Count) return;
        Appointments.RemoveAt(index);
    }

    /// <summary>
    /// Move the appointment at the given index to a new start time. Duration
    /// is preserved.
    /// </summary>
    [McpCallable("Move an appointment to a new start time. Duration is preserved.")]
    public void MoveAppointment(int index, DateTime newStartTime)
    {
        if (index < 0 || index >= Appointments.Count) return;
        var existing = Appointments[index];
        Appointments[index] = existing with { StartTime = newStartTime };
    }

    /// <summary>
    /// Mark every appointment as completed.
    /// </summary>
    [McpCallable("Mark every appointment as completed.")]
    public void CompleteAll()
    {
        for (var i = 0; i < Appointments.Count; i++)
        {
            var a = Appointments[i];
            if (!a.IsCompleted)
            {
                Appointments[i] = a with { IsCompleted = true };
            }
        }
        // The Replace events in the collection are coalesced through INPC
        // for the derived properties below.
        RaisePropertyChanged(nameof(CompletedCount));
    }

    /// <summary>
    /// Clear the planner completely (no appointments left).
    /// </summary>
    [McpCallable("Clear the planner: remove every appointment.")]
    public void Clear()
    {
        if (Appointments.Count == 0) return;
        Appointments.Clear();
    }

    // -------------------------------------------------------------------------
    // [McpObservable] surface - watchable + non-watchable
    // -------------------------------------------------------------------------

    /// <summary>Total number of appointments in the planner.</summary>
    [McpObservable("Total number of appointments in the planner.", Watchable = true)]
    public int AppointmentCount => Appointments.Count;

    /// <summary>Number of appointments marked as completed.</summary>
    [McpObservable("Number of appointments marked as completed.", Watchable = true)]
    public int CompletedCount => Appointments.Count(a => a.IsCompleted);

    /// <summary>Earliest appointment start time, or null if the planner is empty.</summary>
    [McpObservable("Earliest appointment start time, or null when the planner is empty.")]
    public DateTime? EarliestStartTime =>
        Appointments.Count == 0 ? null : Appointments.Min(a => a.StartTime);

    /// <summary>Title of the most recently-added appointment, or null when empty.</summary>
    [McpObservable("Title of the most recently-added appointment, or null when empty.")]
    public string? LastAddedTitle => Appointments.LastOrDefault()?.Title;

    // -------------------------------------------------------------------------
    // [McpEvent] surface
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fires when <see cref="AddAppointment"/> is called. Subscribers to
    /// <c>marionette://PlannerViewModel/events/AppointmentAdded</c> receive
    /// a notification carrying the new appointment's title, start time, and
    /// duration on every fire.
    /// </summary>
    [McpEvent("Fired when a new appointment is added.")]
    public event EventHandler<AppointmentAddedEventArgs>? AppointmentAdded;

    // -------------------------------------------------------------------------
    // INotifyPropertyChanged plumbing
    // -------------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaisePropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnAppointmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Every change to the collection re-derives every aggregate. The
        // INPC notifications coalesce inside the runtime's 200ms watchable
        // window so adopters don't see a flood.
        RaisePropertyChanged(nameof(AppointmentCount));
        RaisePropertyChanged(nameof(CompletedCount));
        RaisePropertyChanged(nameof(EarliestStartTime));
        RaisePropertyChanged(nameof(LastAddedTitle));
    }
}
