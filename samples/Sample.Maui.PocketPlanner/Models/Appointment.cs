// Sample.Maui.PocketPlanner - Appointment model
//
// Plain immutable record used by the PlannerViewModel's ObservableCollection.
// MAUI's ItemsControl-style controls (CollectionView) bind to this via the
// regular property accessors.

using System;

namespace Sample.Maui.PocketPlanner.Models;

/// <summary>
/// One appointment on the daily planner. Immutable except for IsCompleted
/// which the ViewModel toggles via a "with" expression.
/// </summary>
public sealed record Appointment(
    string Title,
    DateTime StartTime,
    int DurationMinutes,
    bool IsCompleted = false)
{
    /// <summary>Convenience accessor: end time = StartTime + DurationMinutes.</summary>
    public DateTime EndTime => StartTime.AddMinutes(DurationMinutes);

    /// <summary>Display string for the time window.</summary>
    public string TimeWindow => $"{StartTime:HH:mm} - {EndTime:HH:mm}";

    /// <summary>Display string suitable for binding to a Label.</summary>
    public string DisplayTitle => IsCompleted ? $"[OK] {Title}" : Title;
}
