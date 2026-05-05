// Marionette.NET — Abstractions
//
// Sealed attribute set marking the surface of an MCP-controllable application.
// These attributes are pure markup: they carry no behavior and never reach the
// runtime side of Marionette. The Source Generator (Phase 1.b) reads them at
// compile time to emit the manifest; at runtime they survive only as metadata
// that is naturally discarded by the trimmer for code that does not reflect
// over them.
//
// Naming / namespace contract (locked in MASTERPLAN):
//   - All public types live in the `Marionette` namespace.
//   - The fully-qualified attribute names (Marionette.McpRootAttribute, …)
//     are stable and consumed verbatim by the Phase-1.b Source Generator.
//   - The TriggerStrategy enum lives in the same namespace; do not relocate.
//
// Design rules (apply to every attribute in this file):
//   - sealed                                  — no inheritance.
//   - AllowMultiple = false, Inherited = false — exactly one per target, no
//                                                 implicit propagation through
//                                                 base classes.
//   - All settable members are init-only      — attribute instances are
//                                                 immutable after construction.
//   - Constructor takes the description       — keeps usage terse:
//                                                 [McpCallable("…")] not
//                                                 [McpCallable(Description="…")].

using System;

namespace Marionette;

/// <summary>
/// Marks a class as a Marionette manifest root. The Source Generator scans
/// only types decorated with this attribute (and their members) — never the
/// whole AppDomain, never base classes. Roots are typically ViewModels,
/// page-level controls, or service classes that an LLM should be allowed to
/// drive from outside the process.
/// </summary>
/// <remarks>
/// <para>
/// A Marionette-enabled app may declare any number of roots; each one becomes
/// an independent slice of the manifest with its own tools and resources. The
/// runtime addresses members beneath a root via the URI scheme
/// <c>marionette://&lt;root-name&gt;/&lt;member-name&gt;</c>.
/// </para>
/// <para>
/// Example:
/// <code>
/// [McpRoot]
/// public sealed class TodoViewModel
/// {
///     [McpCallable("Adds a new todo to the list.")]
///     public void Add(string title) { ... }
/// }
///
/// [McpRoot(Name = "settings")]      // explicit short name, otherwise type name is used
/// public sealed class AppSettings { ... }
/// </code>
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class McpRootAttribute : Attribute
{
    /// <summary>
    /// Initializes a new <see cref="McpRootAttribute"/> with the type name as
    /// its manifest name.
    /// </summary>
    public McpRootAttribute()
    {
    }

    /// <summary>
    /// Initializes a new <see cref="McpRootAttribute"/> with an explicit
    /// short name. The name is used verbatim in tool ids, resource URIs, and
    /// LLM-facing manifest listings, so prefer lowercase, ASCII, and short.
    /// </summary>
    /// <param name="name">
    /// Short manifest name. Must not be null or whitespace; the runtime does
    /// not validate at attribute-construction time, so a malformed value will
    /// surface as a Source-Generator diagnostic in Phase 1.b.
    /// </param>
    public McpRootAttribute(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Optional short name used in tool ids and resource URIs. When
    /// <see langword="null"/>, the runtime defaults to the type name.
    /// </summary>
    public string? Name { get; }
}

/// <summary>
/// Marks a method as callable through Marionette's MCP automation surface.
/// The Source Generator emits a strongly-typed dispatcher entry for each
/// decorated method; reflection is never used on hot paths.
/// </summary>
/// <remarks>
/// <para>
/// Without this attribute, a method is invisible to the LLM — opt-in by
/// design. Decorated methods must be public; non-public methods produce a
/// generator diagnostic in Phase 1.b.
/// </para>
/// <para>
/// Example:
/// <code>
/// [McpCallable("Filters the product list by category and minimum price.")]
/// public void ApplyFilter(string category, decimal minPrice) { ... }
///
/// [McpCallable("Reloads data fresh from the API.", OffUiThread = true, TimeoutSeconds = 60)]
/// public Task&lt;int&gt; RefreshFromApi() { ... }
/// </code>
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpCallableAttribute : Attribute
{
    /// <summary>
    /// Initializes a new <see cref="McpCallableAttribute"/> with the
    /// LLM-facing description text.
    /// </summary>
    /// <param name="description">
    /// Human-readable description shown to the LLM in tool listings. Should
    /// state what the method does in a single sentence.
    /// </param>
    public McpCallableAttribute(string description)
    {
        Description = description;
    }

    /// <summary>
    /// Human-readable description shown to the LLM in tool listings.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// When <see langword="true"/>, the runtime invokes the method off the UI
    /// dispatcher (typically on a thread-pool thread). The default
    /// (<see langword="false"/>) is the safe choice for methods that read or
    /// modify UI controls.
    /// </summary>
    public bool OffUiThread { get; init; }

    /// <summary>
    /// Per-invocation timeout in seconds. Zero (the default) means
    /// "no timeout" — the runtime waits indefinitely. Set this for
    /// long-running calls that must not stall the LLM session.
    /// </summary>
    public int TimeoutSeconds { get; init; }
}

/// <summary>
/// Marks a property as readable state for the LLM via the
/// <c>read_observable</c> tool. When <see cref="Watchable"/> is set, the
/// property additionally becomes an MCP resource that supports
/// <c>resources/subscribe</c> for push-based updates.
/// </summary>
/// <remarks>
/// <para>
/// Properties decorated with this attribute should expose serializable values
/// — primitives, strings, simple records, or arrays of those. Complex object
/// graphs surface as JSON via <c>System.Text.Json</c>; cycles or unsupported
/// types produce a Source-Generator diagnostic in Phase 1.b.
/// </para>
/// <para>
/// When <see cref="Watchable"/> is <see langword="true"/>, the runtime watches
/// the property for changes. If the declaring type implements
/// <see cref="System.ComponentModel.INotifyPropertyChanged"/>, change events
/// drive the push; otherwise the runtime polls at <see cref="PollingIntervalMs"/>.
/// </para>
/// <para>
/// Example:
/// <code>
/// [McpObservable("Number of products currently visible after filtering.")]
/// public int VisibleProductCount =&gt; _filtered.Count;
///
/// [McpObservable("Currently active filter category.", Watchable = true)]
/// public string CurrentCategory { get; private set; }
/// </code>
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class McpObservableAttribute : Attribute
{
    /// <summary>
    /// Initializes a new <see cref="McpObservableAttribute"/> with the
    /// LLM-facing description text.
    /// </summary>
    /// <param name="description">
    /// Human-readable description shown to the LLM in resource and manifest
    /// listings.
    /// </param>
    public McpObservableAttribute(string description)
    {
        Description = description;
    }

    /// <summary>
    /// Human-readable description shown to the LLM.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// When <see langword="true"/>, the runtime exposes the property as an
    /// MCP resource at <c>marionette://&lt;root&gt;/&lt;property&gt;</c> and
    /// supports <c>resources/subscribe</c>. Updates are watched via
    /// <see cref="System.ComponentModel.INotifyPropertyChanged"/> when
    /// available, otherwise via polling at
    /// <see cref="PollingIntervalMs"/>.
    /// </summary>
    public bool Watchable { get; init; }

    /// <summary>
    /// Polling interval in milliseconds, used when
    /// <see cref="Watchable"/> is <see langword="true"/> and the declaring
    /// type does not implement
    /// <see cref="System.ComponentModel.INotifyPropertyChanged"/>. Default
    /// is 500 ms. Ignored when <see cref="Watchable"/> is
    /// <see langword="false"/>.
    /// </summary>
    public int PollingIntervalMs { get; init; } = 500;
}

/// <summary>
/// Marks a property whose value is a UI control (e.g. a WPF
/// <c>System.Windows.Controls.Button</c>) as a triggerable surface point.
/// The LLM can fire its primary user interaction either semantically (the
/// runtime calls the control's main command) or via simulated input through
/// the framework's input pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="Strategy"/> to control how the runtime fires the trigger.
/// <see cref="TriggerStrategy.Semantic"/> is the default and the fastest;
/// <see cref="TriggerStrategy.EventSystem"/> raises the framework's routed
/// event so handlers see realistic event args; <see cref="TriggerStrategy.InputSystem"/>
/// pumps a synthetic input event through the OS-level input pipeline,
/// suitable for high-fidelity automation tests where every layer must run.
/// </para>
/// <para>
/// Example:
/// <code>
/// [McpTriggerable("Reloads the dashboard.")]
/// public Button RefreshButton =&gt; _refreshButton;
///
/// [McpTriggerable("Submits the form.", Strategy = TriggerStrategy.InputSystem)]
/// public Button SubmitButton =&gt; _submitButton;
/// </code>
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class McpTriggerableAttribute : Attribute
{
    /// <summary>
    /// Initializes a new <see cref="McpTriggerableAttribute"/> with the
    /// LLM-facing description text.
    /// </summary>
    /// <param name="description">
    /// Human-readable description of what the trigger does, shown to the LLM
    /// in the manifest.
    /// </param>
    public McpTriggerableAttribute(string description)
    {
        Description = description;
    }

    /// <summary>
    /// Human-readable description shown to the LLM in the manifest.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Strategy by which the runtime fires the trigger. Defaults to
    /// <see cref="TriggerStrategy.Semantic"/>.
    /// </summary>
    public TriggerStrategy Strategy { get; init; } = TriggerStrategy.Semantic;
}

/// <summary>
/// Marks a C# event as deliverable through Marionette's MCP automation surface.
/// The runtime subscribes to the event at startup and forwards every fire (with
/// the serialized <c>EventArgs</c>) to clients that have subscribed to the
/// matching <c>marionette://&lt;root&gt;/events/&lt;event&gt;</c> resource via
/// <c>resources/subscribe</c>.
/// </summary>
/// <remarks>
/// <para>
/// The C# event delegate must be either <see cref="System.EventHandler"/> or
/// <see cref="System.EventHandler{TEventArgs}"/> for Phase 1. Other delegate
/// shapes are rejected by the source generator (diagnostic <c>MAR010</c>).
/// </para>
/// <para>
/// When the delegate carries a <c>TEventArgs</c>, the source generator walks
/// the type's public properties and emits a JSON schema describing the args
/// shape. The schema is exposed through <c>inspect_app_api</c> so the LLM can
/// understand what data each fire carries.
/// </para>
/// <para>
/// <b>Stripping:</b> the attribute itself is metadata-only and survives in
/// stripped builds (consistent with the rest of Marionette's attribute set).
/// The source-generator-emitted descriptor and the runtime hookup vanish when
/// <c>MCP_ENABLED</c> is not defined — there is zero call-site cost in
/// stripped Release builds.
/// </para>
/// <para>
/// <b>Throttling and coalescing:</b> the runtime stores every fire in a per-event
/// ring buffer (<see cref="MaxQueueSize"/>) so adopters do not lose events.
/// Notifications to the client are debounced by <see cref="CoalesceWindowMs"/>
/// — a burst of fires produces a single <c>notifications/resources/updated</c>
/// per window, and the client re-reads the resource to pick up every entry.
/// <see cref="MinIntervalMs"/> drops fires that arrive faster than the
/// configured rate (with a per-event drop counter).
/// </para>
/// <para>
/// Example:
/// <code>
/// public sealed record TodoAddedEventArgs(string Title, DateTime AddedAt) : EventArgs;
///
/// [McpEvent("A new TODO was added.")]
/// public event EventHandler&lt;TodoAddedEventArgs&gt;? TodoAdded;
///
/// [McpEvent("Generic refresh ping.")]
/// public event EventHandler? Refreshed;
///
/// [McpEvent("Mouse moved over the canvas.", MinIntervalMs = 50, MaxQueueSize = 200, CoalesceWindowMs = 100)]
/// public event EventHandler&lt;CursorEventArgs&gt;? CursorMoved;
/// </code>
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Event, AllowMultiple = false, Inherited = false)]
public sealed class McpEventAttribute : Attribute
{
    /// <summary>
    /// Initializes a new <see cref="McpEventAttribute"/> with the LLM-facing
    /// description text.
    /// </summary>
    /// <param name="description">
    /// Human-readable description shown to the LLM in the manifest. Should
    /// state what the event represents in a single sentence.
    /// </param>
    public McpEventAttribute(string description)
    {
        Description = description;
    }

    /// <summary>
    /// Human-readable description shown to the LLM in the manifest.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Minimum interval between accepted fires, in milliseconds. Fires that
    /// arrive faster than this are dropped (a per-event drop counter is
    /// surfaced in the resource payload). Default <c>0</c> = no throttle.
    /// </summary>
    public int MinIntervalMs { get; init; }

    /// <summary>
    /// Maximum number of recent fires retained in the runtime ring buffer for
    /// this event. Older entries are evicted when the buffer fills. Default
    /// <c>100</c>.
    /// </summary>
    public int MaxQueueSize { get; init; } = 100;

    /// <summary>
    /// Coalescing window for client notifications, in milliseconds. A burst of
    /// fires within this window produces a single
    /// <c>notifications/resources/updated</c> push; the ring buffer still
    /// retains every event so the client can read them all on the next
    /// <c>resources/read</c>. Default <c>100</c>.
    /// </summary>
    public int CoalesceWindowMs { get; init; } = 100;
}

/// <summary>
/// Strategies a Marionette adapter may use to fire an
/// <see cref="McpTriggerableAttribute"/>-decorated control's primary
/// interaction.
/// </summary>
public enum TriggerStrategy
{
    /// <summary>
    /// Invoke the control's primary command or click handler directly,
    /// bypassing routed events and the input pipeline. Fastest;
    /// recommended for the majority of LLM-driven flows where realistic
    /// event args do not matter.
    /// </summary>
    Semantic = 0,

    /// <summary>
    /// Raise the framework's routed event (e.g. <c>Button.Click</c>),
    /// preserving bubbling and tunneling semantics. Use this when handler
    /// code inspects <c>e.OriginalSource</c> or relies on framework-level
    /// event propagation.
    /// </summary>
    EventSystem = 1,

    /// <summary>
    /// Pump a synthetic input event through the OS-level input pipeline
    /// (mouse, keyboard, or touch). Slowest, but the highest-fidelity
    /// option; required when validating end-to-end test-automation
    /// behaviour against unmodified application code.
    /// </summary>
    InputSystem = 2,
}

/// <summary>
/// Phase 12.2: assembly-level opt-in declaration for AOT-clean
/// <c>raise_event</c> dispatch on a specific framework control type +
/// event-name pair. The Source Generator scans these attributes at
/// compile time and emits a typed dispatcher
/// (<c>Marionette.Generated.RaiseEventCatalog.TryRaise</c>) that the
/// runtime adapters call before falling back to the reflection-based
/// path.
/// </summary>
/// <remarks>
/// <para>
/// Without this attribute, <c>raise_event</c> resolves the
/// <c>RoutedEvent</c> static field by reflection on the target control
/// type — which trim/AOT may strip the metadata for, especially on
/// custom user controls. Adopters who AOT-publish AND want
/// <c>raise_event</c> to work declare each (Type, EventName) pair via
/// this attribute; the generator emits a typed pattern-match that
/// preserves the static-field reference into the trimmed output.
/// </para>
/// <para>
/// Example:
/// <code>
/// [assembly: McpRaisable(typeof(System.Windows.Controls.Button), "Click")]
/// [assembly: McpRaisable(typeof(MyApp.Controls.SubmitButton), "Submitted")]
/// </code>
/// </para>
/// <para>
/// Constraints: the target type must declare (or inherit) a public
/// <c>static RoutedEvent &lt;EventName&gt;Event</c> field. The
/// constraint is enforced at generator time: a violating attribute
/// produces diagnostic <c>MAR015</c> and the catalog entry is skipped.
/// WPF (<c>System.Windows.RoutedEvent</c>) and Avalonia
/// (<c>Avalonia.Interactivity.RoutedEvent</c>) are both supported;
/// WinUI / MAUI types lack the routed-event surface and aren't
/// covered.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class McpRaisableAttribute : Attribute
{
    /// <summary>
    /// The framework control type the catalog dispatches on. Must be a
    /// concrete reference type with a public <c>static RoutedEvent
    /// &lt;EventName&gt;Event</c> field declared on it or one of its
    /// base classes.
    /// </summary>
    public Type ControlType { get; }

    /// <summary>
    /// The event name as it appears in C# source (e.g. <c>"Click"</c>,
    /// <c>"MouseDown"</c>). The generator looks up
    /// <c>&lt;ControlType&gt;.&lt;EventName&gt;Event</c> at compile
    /// time.
    /// </summary>
    public string EventName { get; }

    public McpRaisableAttribute(Type controlType, string eventName)
    {
        ControlType = controlType;
        EventName = eventName;
    }
}
