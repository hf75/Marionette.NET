// Marionette.NET — Source Generator diagnostics
//
// All Marionette generator diagnostics use the MAR#### ID prefix and the
// "Marionette.Generator" category. IDs are stable across versions: never
// renumber, never repurpose. Add new IDs at the end.
//
// Severity policy:
//   Error    — Generator emits no descriptor for the offending site. Build
//              should fail (the user assembly is in an inconsistent state).
//   Warning  — Generator emits no descriptor but the rest of the assembly is
//              fine. User can ship without fixing.
//   Info     — Heuristic note; never blocks anything.

using Microsoft.CodeAnalysis;

namespace Marionette.SourceGenerator;

internal static class Diagnostics
{
    private const string Category = "Marionette.Generator";

    /// <summary>
    /// MAR001: a class decorated with [McpRoot] is static or generic, so it
    /// cannot be used as a manifest root. The generator skips it entirely.
    /// </summary>
    public static readonly DiagnosticDescriptor InvalidRootClass = new(
        id: "MAR001",
        title: "Invalid [McpRoot] target",
        messageFormat: "[McpRoot] requires a non-static, non-generic reference type; '{0}' does not qualify",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR002: an [McpCallable] method is not public. Public access is required
    /// because the generator emits a strongly-typed dispatcher that calls the
    /// method directly; it does not use reflection to reach into private
    /// members.
    /// </summary>
    public static readonly DiagnosticDescriptor CallableNotPublic = new(
        id: "MAR002",
        title: "[McpCallable] method must be public",
        messageFormat: "[McpCallable] method '{0}' must be public; the generator does not emit dispatchers for non-public methods",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR003: an [McpCallable] method's declaring class lacks [McpRoot]. The
    /// member is silently ignored (warning, not error, so adopters can ship
    /// while migrating).
    /// </summary>
    public static readonly DiagnosticDescriptor CallableOnUnrootedClass = new(
        id: "MAR003",
        title: "[McpCallable] on un-rooted class is ignored",
        messageFormat: "[McpCallable] on '{0}' is ignored — the declaring class must be decorated with [McpRoot] for the generator to emit a dispatcher",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR004: an [McpCallable] parameter has a type the runtime cannot
    /// safely marshal. Phase 1.b is permissive — only obvious blacklist hits
    /// trigger the diagnostic.
    /// </summary>
    public static readonly DiagnosticDescriptor UnsupportedParameterType = new(
        id: "MAR004",
        title: "[McpCallable] parameter type not supported",
        messageFormat: "Parameter '{0}' of type '{1}' on [McpCallable] '{2}' is not supported (blacklisted: Stream, delegate, IntPtr, pointer)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR005: an [McpObservable] property has a setter but no getter. There
    /// is nothing to read.
    /// </summary>
    public static readonly DiagnosticDescriptor ObservableHasNoGetter = new(
        id: "MAR005",
        title: "[McpObservable] requires a public getter",
        messageFormat: "[McpObservable] property '{0}' requires a public getter",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR006: an [McpObservable] property is not public. The generator
    /// cannot emit a typed getter that reaches the value, so the property is
    /// skipped.
    /// </summary>
    public static readonly DiagnosticDescriptor ObservableNotPublic = new(
        id: "MAR006",
        title: "[McpObservable] property should be public",
        messageFormat: "[McpObservable] '{0}' is not public and will be skipped by the generator",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR007: an [McpTriggerable] property's static type is not a known
    /// "clickable" control type. Phase 1 only supports WPF Button-like
    /// controls; other surfaces are punted to later phases.
    /// </summary>
    public static readonly DiagnosticDescriptor TriggerableUnsupportedType = new(
        id: "MAR007",
        title: "[McpTriggerable] only supports controls with a Click event in Phase 1",
        messageFormat: "[McpTriggerable] '{0}' only supports controls with a Click event in Phase 1 (got '{1}')",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR008: an [McpRoot] class declares no MCP entrypoints. Likely an
    /// authoring oversight; emitted as Info so it never blocks a build.
    /// </summary>
    public static readonly DiagnosticDescriptor RootHasNoMembers = new(
        id: "MAR008",
        title: "[McpRoot] declares no MCP entrypoints",
        messageFormat: "Root '{0}' exposes no [McpCallable]/[McpObservable]/[McpTriggerable]/[McpEvent] members; the manifest entry will be empty",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR009: <c>[McpEvent]</c> on a non-event member. The C# compiler also
    /// rejects attribute targets, but if the analyzer somehow sees one anyway
    /// we surface a clean error rather than swallowing it.
    /// </summary>
    public static readonly DiagnosticDescriptor McpEventOnNonEventMember = new(
        id: "MAR009",
        title: "[McpEvent] only applies to C# event members",
        messageFormat: "[McpEvent] on '{0}' is ignored — the attribute targets only C# event declarations",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR010: <c>[McpEvent]</c> applied to an event whose delegate is not
    /// <c>EventHandler</c> or <c>EventHandler&lt;T&gt;</c>. Phase 1 supports
    /// only the standard EventHandler family.
    /// </summary>
    public static readonly DiagnosticDescriptor McpEventUnsupportedDelegate = new(
        id: "MAR010",
        title: "[McpEvent] requires EventHandler or EventHandler<T>",
        messageFormat: "[McpEvent] '{0}' has delegate type '{1}' — Phase 1 supports only System.EventHandler or System.EventHandler<TArgs>",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR011: an <c>[McpEvent]</c> event whose declaring class lacks
    /// <c>[McpRoot]</c> is silently ignored. Mirror of <see cref="CallableOnUnrootedClass"/>.
    /// </summary>
    public static readonly DiagnosticDescriptor McpEventOnUnrootedClass = new(
        id: "MAR011",
        title: "[McpEvent] on un-rooted class is ignored",
        messageFormat: "[McpEvent] on '{0}' is ignored — the declaring class must be decorated with [McpRoot] for the generator to emit an event descriptor",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR012: invalid throttling parameters on <c>[McpEvent]</c>. Defaults
    /// are substituted; the build is not blocked.
    /// </summary>
    public static readonly DiagnosticDescriptor McpEventInvalidThrottling = new(
        id: "MAR012",
        title: "[McpEvent] throttling parameter out of range",
        messageFormat: "[McpEvent] '{0}' has invalid throttling: MaxQueueSize={1}, CoalesceWindowMs={2}. MaxQueueSize must be > 0 and CoalesceWindowMs must be >= 0; defaults will be used.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR013: a public instance method on an <c>[McpRoot]</c> has a
    /// Marionette-compatible signature but is not decorated. This is a DX
    /// hint only; public methods often exist for UI/framework plumbing and
    /// should not always become LLM-facing tools.
    /// </summary>
    public static readonly DiagnosticDescriptor PublicMethodCouldBeCallable = new(
        id: "MAR013",
        title: "Public method could be exposed with [McpCallable]",
        messageFormat: "Public method '{0}' is on [McpRoot] '{1}' and has a Marionette-compatible signature; add [McpCallable] if this is a user-facing action",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR014: an <c>[McpCallable]</c> method has a signature the generator
    /// cannot emit safely. Generic methods need type-argument inference at
    /// runtime, and by-ref parameters need call-site modifiers that are not
    /// compatible with JSON-RPC argument bags.
    /// </summary>
    public static readonly DiagnosticDescriptor CallableUnsupportedSignature = new(
        id: "MAR014",
        title: "[McpCallable] method signature is not supported",
        messageFormat: "[McpCallable] method '{0}' is not supported: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// MAR015: an <c>[assembly: McpRaisable]</c> attribute references a
    /// (Type, EventName) pair the generator could not validate. The pair is
    /// dropped from the emitted catalog (the runtime then falls back to
    /// reflection on the static field at runtime, which costs the AOT
    /// guarantee but does not break the build).
    /// </summary>
    public static readonly DiagnosticDescriptor McpRaisableInvalid = new(
        id: "MAR015",
        title: "[McpRaisable] declaration could not be validated",
        messageFormat: "[McpRaisable(typeof({0}), \"{1}\")] could not be validated: {2}. The catalog entry is skipped; raise_event falls back to reflection on this pair.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
