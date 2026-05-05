// Sample.Wpf.NeonControlCenter — MissionControlViewModel
//
// Full-spectrum Marionette showcase. Exercises every attribute the framework
// ships:
//   * [McpRoot]
//   * [McpCallable] — sync, async, OffUiThread variants
//   * [McpObservable] — Watchable=true (INPC-driven push) and Watchable=false
//   * [McpTriggerable] — the Engage button
//   * [McpEvent] — typed args (AlertRaisedEventArgs)
//
// Also exercises Phase 8 source-gen JSON contexts:
//   * Telemetry returned as a record (object-shape JsonTypeInfo)
//   * Operations history as List<string> (collection JsonTypeInfo)
//   * Status snapshot as Dictionary<string, double> (dictionary JsonTypeInfo)

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media;

using Marionette;

namespace Sample.Wpf.NeonControlCenter;

/// <summary>
/// Typed payload for the <see cref="MissionControlViewModel.AlertRaised"/>
/// event. Sealed class with init-only props — exactly the shape the
/// JsonTypeCollector handles cleanly via source-gen.
/// </summary>
public sealed class AlertRaisedEventArgs : EventArgs
{
    public AlertRaisedEventArgs(string severity, string message, DateTime timestamp)
    {
        Severity = severity;
        Message = message;
        Timestamp = timestamp;
    }

    /// <summary>Alert severity: "info" / "warning" / "critical".</summary>
    public string Severity { get; }

    /// <summary>Human-readable alert text.</summary>
    public string Message { get; }

    /// <summary>UTC timestamp.</summary>
    public DateTime Timestamp { get; }
}

/// <summary>
/// Snapshot of all telemetry channels as a single object — illustrates the
/// source-generator's ability to register a user-defined record as a typed
/// JSON return type (vs falling back to reflection-based serialization).
/// </summary>
public sealed record TelemetrySnapshot(
    double ReactorOutput,
    double CoolantPressure,
    int QuantumFlux,
    string SystemStatus,
    int CyclesCompleted);

/// <summary>
/// Mission-control view-model. Decorated as the app's [McpRoot]; backs the
/// MainWindow as DataContext via the <see cref="Shared"/> singleton.
/// </summary>
[McpRoot("mission")]
public sealed class MissionControlViewModel : INotifyPropertyChanged
{
    private static MissionControlViewModel? s_shared;
    private static readonly object s_sharedLock = new();

    /// <summary>Process-wide singleton shared between MainWindow and runtime.</summary>
    public static MissionControlViewModel Shared
    {
        get
        {
            if (s_shared is not null) return s_shared;
            lock (s_sharedLock)
            {
                s_shared ??= new MissionControlViewModel();
                return s_shared;
            }
        }
    }

    private double _reactorOutput = 47.5;
    private double _coolantPressure = 124.7;
    private int _quantumFlux = 4242;
    private string _systemStatus = "STANDBY";
    private int _cyclesCompleted;
    private string _targetDesignation = "ALPHA-7";
    private double _powerLevel = 65;
    private string _statusLine = "READY :: AWAITING COMMAND";
    private string _lastEventLine = "—";

    public MissionControlViewModel()
    {
        AlertFeed.Add("[BOOT] Mission control initialised at " + DateTime.UtcNow.ToString("O"));
        AlertFeed.Add("[INFO] All channels nominal");
    }

    // ---------------------------------------------------------------------
    // Observables
    // ---------------------------------------------------------------------

    [McpObservable("Reactor output in megawatts (0-100).", Watchable = true)]
    public double ReactorOutput
    {
        get => _reactorOutput;
        private set { _reactorOutput = value; OnChanged(); }
    }

    [McpObservable("Coolant pressure in bar (0-200).", Watchable = true)]
    public double CoolantPressure
    {
        get => _coolantPressure;
        private set { _coolantPressure = value; OnChanged(); }
    }

    [McpObservable("Quantum flux in qf (0-9999).", Watchable = true)]
    public int QuantumFlux
    {
        get => _quantumFlux;
        private set { _quantumFlux = value; OnChanged(); }
    }

    [McpObservable("Current system status (STANDBY / ENGAGED / CRITICAL / ABORTED).", Watchable = true)]
    public string SystemStatus
    {
        get => _systemStatus;
        private set
        {
            _systemStatus = value;
            OnChanged();
            OnChanged(nameof(StatusColor));
        }
    }

    [McpObservable("Mission cycles completed since boot.", Watchable = true)]
    public int CyclesCompleted
    {
        get => _cyclesCompleted;
        private set { _cyclesCompleted = value; OnChanged(); }
    }

    [McpObservable("Active target designation (e.g. ALPHA-7, BRAVO-12).", Watchable = true)]
    public string TargetDesignation
    {
        get => _targetDesignation;
        set { _targetDesignation = value; OnChanged(); }
    }

    [McpObservable("Power level setting (0-100 percent).", Watchable = true)]
    public double PowerLevel
    {
        get => _powerLevel;
        set { _powerLevel = value; OnChanged(); }
    }

    [McpObservable("Status-line text shown in the header.", Watchable = false)]
    public string StatusLine
    {
        get => _statusLine;
        private set { _statusLine = value; OnChanged(); }
    }

    [McpObservable("Last event-line text shown in the footer.", Watchable = false)]
    public string LastEventLine
    {
        get => _lastEventLine;
        private set { _lastEventLine = value; OnChanged(); }
    }

    /// <summary>UI-bound color brush; not exposed to MCP.</summary>
    public Brush StatusColor => SystemStatus switch
    {
        "ENGAGED" => Brushes.LimeGreen,
        "CRITICAL" => Brushes.OrangeRed,
        "ABORTED" => Brushes.OrangeRed,
        _ => Brushes.Cyan,
    };

    /// <summary>Bound to the right-pane ListBox.</summary>
    public ObservableCollection<string> AlertFeed { get; } = new();

    // ---------------------------------------------------------------------
    // Callables
    // ---------------------------------------------------------------------

    [McpCallable("Engage the reactor: sets status ENGAGED, increments cycle count, raises an alert.")]
    public void Engage()
    {
        SystemStatus = "ENGAGED";
        CyclesCompleted++;
        ReactorOutput = Math.Min(100, _reactorOutput + 10 + (_powerLevel / 5));
        QuantumFlux = Math.Clamp((int)(_quantumFlux + 200 + _powerLevel * 4), 0, 9999);
        StatusLine = $"ENGAGED :: TARGET {_targetDesignation} @ {_powerLevel:F0}%";
        LastEventLine = $"ENGAGE @ {DateTime.UtcNow:HH:mm:ss}";
        EmitAlert("info", $"Reactor engaged on target {_targetDesignation}");
    }

    [McpCallable("Abort the active operation: forces status ABORTED and drops reactor output.")]
    public void Abort()
    {
        SystemStatus = "ABORTED";
        ReactorOutput = Math.Max(0, _reactorOutput - 25);
        StatusLine = "ABORTED :: STANDING DOWN";
        LastEventLine = $"ABORT @ {DateTime.UtcNow:HH:mm:ss}";
        EmitAlert("warning", "Operation aborted by command console");
    }

    [McpCallable("Reset all telemetry to baseline values and clear alert feed.")]
    public void ResetTelemetry()
    {
        ReactorOutput = 47.5;
        CoolantPressure = 124.7;
        QuantumFlux = 4242;
        SystemStatus = "STANDBY";
        CyclesCompleted = 0;
        StatusLine = "READY :: AWAITING COMMAND";
        LastEventLine = $"RESET @ {DateTime.UtcNow:HH:mm:ss}";
        AlertFeed.Clear();
        EmitAlert("info", "Telemetry reset to baseline");
    }

    [McpCallable("Adjust the power level by delta (-100..+100, clamped to 0..100).")]
    public double AdjustPower(double delta)
    {
        var newLevel = Math.Clamp(_powerLevel + delta, 0, 100);
        PowerLevel = newLevel;
        EmitAlert("info", $"Power level adjusted to {newLevel:F0}%");
        return newLevel;
    }

    [McpCallable("Run a long-running diagnostic on a thread-pool thread; returns a status string when done.",
        OffUiThread = true, TimeoutSeconds = 10)]
    public async Task<string> RunDiagnosticAsync()
    {
        SystemStatus = "DIAGNOSTIC";
        StatusLine = "DIAGNOSTIC :: SCAN IN PROGRESS";
        await Task.Delay(800).ConfigureAwait(false);
        // simulated workload …
        var pass = (_reactorOutput + _coolantPressure / 2 + _quantumFlux / 100.0) % 7 < 5.5;
        var result = pass ? "DIAGNOSTIC :: ALL SUBSYSTEMS NOMINAL" : "DIAGNOSTIC :: ANOMALY DETECTED";
        SystemStatus = pass ? "STANDBY" : "CRITICAL";
        StatusLine = result;
        EmitAlert(pass ? "info" : "critical", result);
        return result;
    }

    [McpCallable("Take a telemetry snapshot — returns a TelemetrySnapshot record.")]
    public TelemetrySnapshot Snapshot()
    {
        return new TelemetrySnapshot(
            ReactorOutput: _reactorOutput,
            CoolantPressure: _coolantPressure,
            QuantumFlux: _quantumFlux,
            SystemStatus: _systemStatus,
            CyclesCompleted: _cyclesCompleted);
    }

    [McpCallable("Snapshot the numeric channels as a string-keyed dictionary.")]
    public Dictionary<string, double> SnapshotMetrics()
    {
        return new Dictionary<string, double>
        {
            ["reactorOutput"] = _reactorOutput,
            ["coolantPressure"] = _coolantPressure,
            ["quantumFlux"] = _quantumFlux,
            ["powerLevel"] = _powerLevel,
            ["cyclesCompleted"] = _cyclesCompleted,
        };
    }

    [McpCallable("Get the alert feed as a list of strings (newest first).")]
    public List<string> GetAlertFeed()
    {
        var copy = new List<string>(AlertFeed.Count);
        for (int i = AlertFeed.Count - 1; i >= 0; i--) copy.Add(AlertFeed[i]);
        return copy;
    }

    [McpCallable("Clear the alert feed list (keeps telemetry intact).")]
    public void ClearAlertFeed()
    {
        AlertFeed.Clear();
        LastEventLine = $"FEED CLEARED @ {DateTime.UtcNow:HH:mm:ss}";
    }

    // ---------------------------------------------------------------------
    // Events
    // ---------------------------------------------------------------------

    [McpEvent("Fires whenever the mission control raises a new alert (info/warning/critical).",
        MaxQueueSize = 50, CoalesceWindowMs = 200)]
    public event EventHandler<AlertRaisedEventArgs>? AlertRaised;

    private void EmitAlert(string severity, string message)
    {
        var ts = DateTime.UtcNow;
        var line = $"[{ts:HH:mm:ss}] {severity.ToUpperInvariant()}: {message}";
        AlertFeed.Insert(0, line);
        while (AlertFeed.Count > 30) AlertFeed.RemoveAt(AlertFeed.Count - 1);
        AlertRaised?.Invoke(this, new AlertRaisedEventArgs(severity, message, ts));
    }

    // ---------------------------------------------------------------------
    // INotifyPropertyChanged
    // ---------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? string.Empty));
}
