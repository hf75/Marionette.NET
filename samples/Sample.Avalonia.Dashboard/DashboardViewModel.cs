// Sample.Avalonia.Dashboard - DashboardViewModel
//
// The canonical Avalonia [McpRoot]. Adopters who land on this file should see
// at a glance:
//
//   * How [McpRoot] declares the root.
//   * How [McpCallable] decorates public methods that mutate dashboard state,
//     including OffUiThread + TimeoutSeconds for an async RefreshAsync.
//   * How [McpObservable(Watchable=true)] exposes aggregate state for push
//     updates; INotifyPropertyChanged powers the live notifications channel
//     so subscribers don't poll.
//   * How a non-Window root shares a single instance with the MainWindow
//     (see DashboardViewModel.Shared and the App.OnFrameworkInitializationCompleted
//     wiring).
//   * How [McpEvent] declares an event for declarative MCP delivery.
//
// Crucial design call: this class implements INotifyPropertyChanged so the
// runtime's WatchableResourceProvider attaches a PropertyChanged handler
// instead of falling back to polling. Push updates feel substantially nicer
// in demos than 500 ms polling.
//
// "Real-world example" framing: this VM models a tiny system dashboard with
// four pre-seeded metrics (CPU, Memory, Network, Disk). Adopters can reuse
// the structure for any monitoring / status app.

using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Marionette;

using Sample.Avalonia.Dashboard.Models;

namespace Sample.Avalonia.Dashboard;

/// <summary>
/// EventArgs payload for <see cref="DashboardViewModel.MetricUpserted"/>.
/// Sealed class (not a record - records can't inherit a non-record base, and
/// EventArgs is a non-record class - CS8864). The JSON-schema generator
/// produces a clean shape for these read-only primitive properties.
/// </summary>
public sealed class MetricUpsertedEventArgs : EventArgs
{
    public MetricUpsertedEventArgs(string name, double value, string unit)
    {
        Name = name;
        Value = value;
        Unit = unit;
    }

    /// <summary>Name of the metric that was inserted or updated.</summary>
    public string Name { get; }
    /// <summary>New value of the metric.</summary>
    public double Value { get; }
    /// <summary>Unit of measurement (e.g. "%", "MB").</summary>
    public string Unit { get; }
}

/// <summary>
/// Dashboard view-model: a small list of named metrics plus aggregate
/// counters, with refresh / pause / reset actions. Decorated as the app's
/// single [McpRoot]; every method or property the LLM is meant to drive
/// lives here.
/// </summary>
[McpRoot]
public sealed class DashboardViewModel : INotifyPropertyChanged
{
    private static DashboardViewModel? s_shared;
    private static readonly object s_sharedLock = new();

    /// <summary>
    /// Process-wide singleton shared between the Avalonia MainWindow's
    /// DataContext and the runtime's RootDescriptor.Create factory. Both refer
    /// to the same instance so [McpCallable] mutations flip rows the user can
    /// see, and user clicks in the GUI update the observables the LLM reads.
    /// </summary>
    /// <remarks>
    /// Same pattern as Sample.Wpf.TodoApp.TodoListViewModel.Shared. The
    /// non-Window root needs the explicit factory rewrite in
    /// App.OnFrameworkInitializationCompleted (see App.axaml.cs).
    /// </remarks>
    public static DashboardViewModel Shared
    {
        get
        {
            if (s_shared is not null) return s_shared;
            lock (s_sharedLock)
            {
                s_shared ??= new DashboardViewModel();
                return s_shared;
            }
        }
    }

    private readonly ObservableCollection<Metric> _metrics = new();
    private bool _isPaused;
    private string? _lastUpdatedMetric;

    /// <summary>
    /// Underlying observable list, bound to the Avalonia ItemsControl. Not
    /// exposed as [McpObservable] - adopters can read shape via the derived
    /// MetricCount / Total + LastUpdatedMetric.
    /// </summary>
    public ObservableCollection<Metric> Metrics => _metrics;

    public DashboardViewModel()
    {
        // First-construction-wins singleton: whoever runs the ctor first
        // installs themselves as the shared instance. The Shared getter only
        // creates a new one when s_shared is null, so the second caller picks
        // up the first one and never reaches this ctor.
        lock (s_sharedLock)
        {
            s_shared ??= this;
        }

        // Subscribe so per-metric value changes refresh the derived Total.
        _metrics.CollectionChanged += OnMetricsCollectionChanged;

        // Pre-seed four representative metrics so the GUI looks populated
        // out of the box. Adopter-facing demos start with a non-empty list
        // - the LLM driver still gets meaningful read_observable values
        // before any [McpCallable] runs.
        _metrics.Add(new Metric("CPU", 12.5, "%"));
        _metrics.Add(new Metric("Memory", 4096, "MB"));
        _metrics.Add(new Metric("Network", 250, "KB/s"));
        _metrics.Add(new Metric("Disk", 78.2, "%"));
    }

    // -------------------------------------------------------------------------
    // [McpCallable] surface - five meaningful actions
    // -------------------------------------------------------------------------

    /// <summary>
    /// Add a new metric or update an existing one (matched by Name).
    /// </summary>
    [McpCallable("Add or update a metric on the dashboard. Existing metrics are matched by Name.")]
    public void UpsertMetric(string name, double value, string unit)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var existing = _metrics.FirstOrDefault(m => m.Name == name);
        if (existing is null)
        {
            _metrics.Add(new Metric(name, value, unit ?? string.Empty, MetricTrend.Flat));
        }
        else
        {
            var prev = existing.Value;
            existing.Value = value;
            existing.Unit = unit ?? string.Empty;
            existing.Trend = value > prev ? MetricTrend.Up :
                             value < prev ? MetricTrend.Down :
                                            MetricTrend.Flat;
        }
        _lastUpdatedMetric = name;
        RaisePropertyChanged(nameof(LastUpdatedMetric));
        RaisePropertyChanged(nameof(Total));
        // Phase 1.6 declarative event delivery: subscribers to
        // marionette://DashboardViewModel/events/MetricUpserted receive a
        // notification with a MetricUpsertedEventArgs payload.
        MetricUpserted?.Invoke(this, new MetricUpsertedEventArgs(name, value, unit ?? string.Empty));
    }

    /// <summary>
    /// Remove a metric by name. No-op if no metric matches.
    /// </summary>
    [McpCallable("Remove a metric by Name. No-op if no metric matches.")]
    public void RemoveMetric(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        for (var i = _metrics.Count - 1; i >= 0; i--)
        {
            if (_metrics[i].Name == name)
            {
                _metrics.RemoveAt(i);
            }
        }
        // Total and MetricCount fire via OnMetricsCollectionChanged; LastUpdatedMetric
        // stays unchanged because removal isn't an "update".
    }

    /// <summary>
    /// Reset every metric's Value to zero (and Trend to Flat). Keeps the
    /// metric set; useful for "clear" semantics.
    /// </summary>
    [McpCallable("Reset every metric's Value to zero. Metric names are preserved.")]
    public void ResetAll()
    {
        foreach (var m in _metrics)
        {
            m.Value = 0;
            m.Trend = MetricTrend.Flat;
        }
        // Total derived; the per-metric Value setter fires PropertyChanged
        // so subscribers see the burst, but Total reads off the live values
        // so we re-emit it here too.
        RaisePropertyChanged(nameof(Total));
    }

    /// <summary>
    /// Toggle the paused state. While paused, RefreshAsync becomes a no-op.
    /// Marked OffUiThread because the action does no UI work; the runtime
    /// schedules it via Task.Run so the UI stays responsive even on a long
    /// chain of paused-state inquiries.
    /// </summary>
    [McpCallable("Toggle paused state. While paused, RefreshAsync is a no-op.", OffUiThread = true)]
    public void TogglePaused()
    {
        IsPaused = !IsPaused;
        PausedToggled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Async refresh from a simulated source. Demonstrates the runtime's
    /// async-callable handling: the source generator emits an
    /// <c>IsAsync=true</c> CallableDescriptor, the runtime awaits the
    /// returned Task before sending the tools/call response, and the
    /// per-call timeout (<c>TimeoutSeconds=5</c>) bounds it. Marked
    /// OffUiThread because the simulated work is pure computation - no
    /// UI touch.
    /// </summary>
    [McpCallable("Refresh from a simulated source. Async (~simulatedDelayMs). No-op when paused.", OffUiThread = true, TimeoutSeconds = 5)]
    public async Task RefreshAsync(int simulatedDelayMs = 500)
    {
        if (IsPaused) return;
        await Task.Delay(Math.Max(0, simulatedDelayMs)).ConfigureAwait(false);

        // After the simulated I/O, nudge each metric value by a deterministic
        // pseudo-random offset. (Random with a constant seed keeps the
        // RefreshAsync output reproducible across CI runs.)
        //
        // We deliberately do NOT marshal these mutations to the Avalonia UI
        // thread here - the ViewModel stays framework-agnostic so adopters
        // can reuse it on WPF / WinUI / headless tests without code changes.
        // Avalonia's bindings dispatch their UI work on PropertyChanged, and
        // the runtime's WatchableResourceProvider has its own coalescing path.
        // In headless mode there is no Avalonia.Threading.Dispatcher.UIThread -
        // an InvokeAsync there would block forever (the harness exposed this
        // as an EC fail in --avalonia mode before this fix).
        var rng = new Random(_metrics.Count);
        foreach (var m in _metrics)
        {
            var prev = m.Value;
            var delta = (rng.NextDouble() - 0.5) * 4.0; // +- 2.0
            m.Value = Math.Round(prev + delta, 2);
            m.Trend = m.Value > prev ? MetricTrend.Up :
                      m.Value < prev ? MetricTrend.Down :
                                       MetricTrend.Flat;
        }
        RaisePropertyChanged(nameof(Total));
    }

    // -------------------------------------------------------------------------
    // [McpObservable] surface - watchable counters + non-watchable last-update
    // -------------------------------------------------------------------------

    /// <summary>
    /// Total number of tracked metrics.
    /// </summary>
    [McpObservable("Total number of tracked metrics.", Watchable = true)]
    public int MetricCount => _metrics.Count;

    /// <summary>
    /// Sum of all metric values (sums numerically, regardless of Unit).
    /// </summary>
    [McpObservable("Sum of all metric values (sums numerically, regardless of Unit).", Watchable = true)]
    public double Total => _metrics.Sum(m => m.Value);

    /// <summary>
    /// Whether the dashboard is currently paused.
    /// </summary>
    [McpObservable("Whether the dashboard is currently paused.", Watchable = true)]
    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (_isPaused == value) return;
            _isPaused = value;
            RaisePropertyChanged();
        }
    }

    /// <summary>
    /// Most recently updated metric name, or null if no metric has been
    /// upserted since startup. Non-watchable to demonstrate the alternative
    /// shape; adopters who want push updates here would mark it Watchable=true.
    /// </summary>
    [McpObservable("Most recently updated metric name, or null if none.")]
    public string? LastUpdatedMetric => _lastUpdatedMetric;

    // -------------------------------------------------------------------------
    // [McpEvent] surface - Phase 1.6
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fires when <see cref="UpsertMetric"/> inserts or updates a metric.
    /// Subscribers to <c>marionette://DashboardViewModel/events/MetricUpserted</c>
    /// receive a notification carrying name / value / unit on every fire.
    /// </summary>
    [McpEvent("A metric was inserted or updated.")]
    public event EventHandler<MetricUpsertedEventArgs>? MetricUpserted;

    /// <summary>
    /// Fires when the paused state toggles. Args are <see cref="EventArgs.Empty"/>;
    /// subscribers can read the current state via the IsPaused observable.
    /// </summary>
    [McpEvent("Fired when the paused state toggles.")]
    public event EventHandler<EventArgs>? PausedToggled;

    // -------------------------------------------------------------------------
    // INotifyPropertyChanged plumbing
    // -------------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaisePropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnMetricsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // MetricCount and Total both shift on add/remove/replace. Per-metric
        // Value mutations re-fire Total via the per-callable code paths.
        if (e.NewItems is not null)
        {
            foreach (var m in e.NewItems.OfType<Metric>())
            {
                m.PropertyChanged += OnMetricPropertyChanged;
            }
        }
        if (e.OldItems is not null)
        {
            foreach (var m in e.OldItems.OfType<Metric>())
            {
                m.PropertyChanged -= OnMetricPropertyChanged;
            }
        }
        RaisePropertyChanged(nameof(MetricCount));
        RaisePropertyChanged(nameof(Total));
    }

    private void OnMetricPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Metric.Value))
        {
            // Total derives from per-metric values; re-emit on each tick.
            // Resource provider has 200 ms coalesce - bursts collapse to one push.
            RaisePropertyChanged(nameof(Total));
        }
    }
}
