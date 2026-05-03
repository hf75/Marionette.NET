// Sample.Avalonia.Dashboard - Metric model
//
// Plain INPC class (not a record) so two-way Avalonia bindings update
// in-place. The DashboardViewModel hosts an ObservableCollection<Metric>
// and surfaces aggregate observables (Total, MetricCount, ...) over the top.
// Trend is a computed enum value used by the UI to colour-code each row;
// the simulated RefreshAsync nudges values and recomputes trend.

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sample.Avalonia.Dashboard.Models;

/// <summary>
/// Direction of a metric's last update.
/// </summary>
public enum MetricTrend
{
    /// <summary>No prior value or value unchanged.</summary>
    Flat,
    /// <summary>Value increased on last refresh.</summary>
    Up,
    /// <summary>Value decreased on last refresh.</summary>
    Down,
}

/// <summary>
/// One row in the dashboard. INPC plumbed so the bound list refreshes when
/// <see cref="Value"/> or <see cref="Trend"/> mutates.
/// </summary>
public sealed class Metric : INotifyPropertyChanged
{
    private double _value;
    private string _unit;
    private MetricTrend _trend;

    public Metric(string name, double value, string unit, MetricTrend trend = MetricTrend.Flat)
    {
        Name = name ?? string.Empty;
        _value = value;
        _unit = unit ?? string.Empty;
        _trend = trend;
    }

    /// <summary>Stable name used as the metric's identity (case-sensitive).</summary>
    public string Name { get; }

    /// <summary>Current numeric value.</summary>
    public double Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Display unit (e.g. "%", "MB", "ms"). Free-form text.</summary>
    public string Unit
    {
        get => _unit;
        set
        {
            if (_unit == value) return;
            _unit = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    /// <summary>Last-known direction of change. UI uses this for the up/down badge.</summary>
    public MetricTrend Trend
    {
        get => _trend;
        set
        {
            if (_trend == value) return;
            _trend = value;
            OnPropertyChanged();
            // The UI binds a derived "TrendGlyph" off this; emit alongside so
            // the row redraws fully.
            OnPropertyChanged(nameof(TrendGlyph));
        }
    }

    /// <summary>UI-friendly arrow / dash for the current trend.</summary>
    public string TrendGlyph => _trend switch
    {
        MetricTrend.Up => "^",
        MetricTrend.Down => "v",
        _ => "-",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
