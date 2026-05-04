// Marionette.NET — manifest descriptor record types
//
// Phase 1.2 ownership decision (see .phase1/1c-runtime.md):
//
// In Phase 1.b the source generator emitted these record types inline into
// every user assembly's Marionette.g.cs. That kept the generated file
// self-contained but had two costs:
//
//   1. Every adopter shipped a private copy of the descriptor types under
//      `Marionette.Generated`, with separate CLR identity. Runtime consumption
//      required reflection over the well-known type name to "duck-type" the
//      shape — exactly the kind of footgun the AOT contract is designed to
//      avoid.
//
//   2. Phase 1.2 (this file) needs to consume `IReadOnlyList<RootDescriptor>`
//      strongly-typed from the runtime side. Doing that against per-assembly
//      duplicates means the runtime would have to either reference the user
//      assembly (impossible — generic library) or wrap each value through
//      reflection (not AOT-clean).
//
// Resolution: move the descriptor records into the Runtime assembly under
// the `Marionette.Runtime.Manifest` namespace. The Phase 1.b emitter is
// updated to:
//
//   * Drop its inline record definitions.
//   * Emit `using Marionette.Runtime.Manifest;` at the top of Marionette.g.cs
//     so the descriptor type names resolve to this file.
//   * Continue to gate emission on the `MCP_ENABLED` preprocessor symbol so
//     stripped Release builds (`EnableMcpAutomation=false`) get no generated
//     manifest at all — preserving the IL-stripping promise from Phase 0
//     Spike A.
//
// Stripping invariant: when MCP_ENABLED is not defined, the generator emits
// nothing, so the user assembly never references `Marionette.Runtime.Manifest`
// types. The IL probe stays at zero hits on stripped builds.

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Marionette.Runtime.Manifest;

/// <summary>
/// Describes a single <c>[McpRoot]</c>-decorated class in the user assembly.
/// Emitted by the Phase 1.b source generator and consumed by the Phase 1.2
/// runtime registry.
/// </summary>
/// <param name="Name">
/// Short manifest name. Either the explicit value passed to
/// <c>[McpRoot("…")]</c>, or the type's short name when the attribute is
/// parameterless.
/// </param>
/// <param name="TypeName">
/// Fully-qualified CLR type name of the root. Provided for diagnostics —
/// runtime dispatch never reflects on it.
/// </param>
/// <param name="Create">
/// Optional zero-argument factory that produces a new instance of the root
/// type. Set when the type has a public parameterless constructor; the
/// runtime falls back to adopter-supplied instances when it is null.
/// </param>
/// <param name="Callables">All <c>[McpCallable]</c> methods found on the root.</param>
/// <param name="Observables">All <c>[McpObservable]</c> properties found on the root.</param>
/// <param name="Triggerables">All <c>[McpTriggerable]</c> properties found on the root.</param>
/// <param name="Events">All <c>[McpEvent]</c> events found on the root (Phase 1.6).</param>
public sealed record RootDescriptor(
    string Name,
    string TypeName,
    Func<object>? Create,
    IReadOnlyList<CallableDescriptor> Callables,
    IReadOnlyList<ObservableDescriptor> Observables,
    IReadOnlyList<TriggerableDescriptor> Triggerables,
    IReadOnlyList<EventDescriptor> Events);

/// <summary>
/// Describes a single <c>[McpCallable]</c> method. <see cref="Invoke"/> is a
/// strongly-typed dispatcher emitted by the source generator (no reflection).
/// </summary>
/// <param name="Name">Method name as declared in user code.</param>
/// <param name="Description">Human-readable description shown to the LLM in tool listings.</param>
/// <param name="OffUiThread">When <see langword="true"/>, the runtime invokes the method off the UI dispatcher.</param>
/// <param name="TimeoutSeconds">Per-invocation timeout in seconds. Zero means no timeout.</param>
/// <param name="IsAsync"><see langword="true"/> if the method returns <c>Task</c>, <c>Task&lt;T&gt;</c>, <c>ValueTask</c>, or <c>ValueTask&lt;T&gt;</c>.</param>
/// <param name="Parameters">Parameter descriptors in declaration order.</param>
/// <param name="ParametersJsonSchema">
/// Single-line JSON schema describing the callable's parameter object — shape
/// <c>{"type":"object","properties":{...},"required":[...]}</c>. Built at
/// compile time by the source generator's <c>JsonSchemaWriter</c>; consumed
/// by the Phase 2.2 <c>DynamicToolRegistry</c> to set
/// <c>Tool.InputSchema</c> for each per-method MCP tool. Empty-parameter
/// callables get <c>{"type":"object","properties":{}}</c>.
/// </param>
/// <param name="Invoke">
/// Compile-time emitted dispatcher. Receives the root instance and a
/// dictionary of named arguments; returns:
/// <list type="bullet">
///   <item><description>the (boxed) return value for sync methods,</description></item>
///   <item><description><see langword="null"/> for <see langword="void"/> methods,</description></item>
///   <item><description>the <see cref="System.Threading.Tasks.Task"/> / <see cref="System.Threading.Tasks.ValueTask"/> instance for async methods (the runtime awaits).</description></item>
/// </list>
/// </param>
/// <param name="SerializeResult">
/// Phase 8.2: optional source-generator-emitted typed JSON serialiser for
/// the callable's sync return value (or T from <c>Task&lt;T&gt;</c> /
/// <c>ValueTask&lt;T&gt;</c>). Receives the boxed result and returns the
/// JSON string. Bound at compile time to
/// <c>MarionetteJsonContext.Default.&lt;TypeName&gt;</c>; AOT-clean. When
/// non-null, <c>MarionetteDispatch.SerializeResult</c> uses this lambda
/// instead of <c>JsonSerializer.Serialize(object?, options)</c>.
/// </param>
public sealed record CallableDescriptor(
    string Name,
    string Description,
    bool OffUiThread,
    int TimeoutSeconds,
    bool IsAsync,
    IReadOnlyList<ParamDescriptor> Parameters,
    string ParametersJsonSchema,
    Func<object, IReadOnlyDictionary<string, object?>, object?> Invoke,
    Func<object?, string>? SerializeResult = null);

/// <summary>
/// One parameter on a <see cref="CallableDescriptor"/>.
/// </summary>
/// <param name="Name">Parameter name as declared in user code.</param>
/// <param name="ClrTypeName">Short CLR type name (e.g. <c>int</c>, <c>string</c>) — used for the JSON-schema description shown to the LLM.</param>
/// <param name="IsRequired"><see langword="true"/> when the parameter has no compile-time default.</param>
/// <param name="DefaultValue">
/// Boxed default value for optional parameters; <see langword="null"/> for
/// required ones. The generator pre-evaluates the default literal at compile
/// time so the runtime doesn't have to.
/// </param>
public sealed record ParamDescriptor(
    string Name,
    string ClrTypeName,
    bool IsRequired,
    object? DefaultValue);

/// <summary>
/// Describes a single <c>[McpObservable]</c> property.
/// </summary>
/// <param name="Name">Property name as declared in user code.</param>
/// <param name="Description">Human-readable description shown to the LLM.</param>
/// <param name="Watchable">When <see langword="true"/>, the runtime exposes the property as an MCP resource and supports <c>resources/subscribe</c>.</param>
/// <param name="PollingIntervalMs">Polling interval used when <see cref="Watchable"/> is set and the declaring type does not implement <c>INotifyPropertyChanged</c>.</param>
/// <param name="ClrTypeName">Short CLR type name — used for shape information shown to the LLM.</param>
/// <param name="Read">Compile-time emitted getter lambda. Receives the root instance, returns the (boxed) property value.</param>
/// <param name="SerializeValue">
/// Phase 8.2: optional source-generator-emitted typed JSON serialiser for
/// the property's value type. Receives the boxed value (or
/// <see langword="null"/>) and returns a JSON string. Bound at compile time
/// to <c>MarionetteJsonContext.Default.&lt;TypeName&gt;</c>. When non-null,
/// <c>WatchableResourceProvider</c> uses this lambda instead of
/// <c>JsonSerializer.Serialize(value, options)</c>.
/// </param>
public sealed record ObservableDescriptor(
    string Name,
    string Description,
    bool Watchable,
    int PollingIntervalMs,
    string ClrTypeName,
    Func<object, object?> Read,
    Func<object?, string>? SerializeValue = null);

/// <summary>
/// Describes a single <c>[McpTriggerable]</c> property whose value is a UI
/// control. Phase 1.2's runtime delegates the actual click/event firing to
/// the framework-specific <c>IUiAutomationAdapter</c>; this descriptor only
/// resolves the control instance.
/// </summary>
/// <param name="Name">Property name as declared in user code.</param>
/// <param name="Description">Human-readable description shown to the LLM.</param>
/// <param name="Strategy">How the runtime should fire the trigger (Semantic / EventSystem / InputSystem).</param>
/// <param name="ControlTypeName">Short CLR type name of the control surface.</param>
/// <param name="ResolveControl">Compile-time emitted control resolver. Receives the root instance, returns the control object.</param>
public sealed record TriggerableDescriptor(
    string Name,
    string Description,
    global::Marionette.TriggerStrategy Strategy,
    string ControlTypeName,
    Func<object, object?> ResolveControl);

/// <summary>
/// Describes a single <c>[McpEvent]</c>-decorated C# event (Phase 1.6). The
/// <see cref="Subscribe"/> delegate is a strongly-typed bridge emitted by the
/// source generator — no reflection, AOT-clean.
/// </summary>
/// <param name="Name">Event name as declared in user code.</param>
/// <param name="Description">Human-readable description shown to the LLM in the manifest.</param>
/// <param name="ArgsTypeName">
/// Fully-qualified CLR type name of the event-args type (e.g.
/// <c>"Sample.Wpf.TodoApp.TodoAddedEventArgs"</c>) or <c>"System.EventArgs"</c>
/// when the delegate is the non-generic <see cref="System.EventHandler"/>.
/// </param>
/// <param name="ArgsJsonSchema">
/// Single-line JSON schema describing the args type's public properties. Built
/// at compile time by the source generator's <c>JsonSchemaWriter</c> helper.
/// For <c>EventHandler</c> with no args type, the schema is
/// <c>{"type":"object","properties":{}}</c>.
/// </param>
/// <param name="MinIntervalMs">Minimum interval between accepted fires; fires faster than this are dropped.</param>
/// <param name="MaxQueueSize">Bounded ring-buffer capacity; oldest entries evicted on overflow.</param>
/// <param name="CoalesceWindowMs">Window during which multiple fires collapse to a single client notification.</param>
/// <param name="Subscribe">
/// Compile-time emitted subscription delegate. Receives the live root instance
/// and a callback the runtime calls on every fire (with the EventArgs as the
/// argument). Returns an <see cref="IDisposable"/> the runtime disposes at
/// shutdown to detach the handler.
/// </param>
/// <param name="SerializeArgs">
/// Phase 8.1: optional source-generator-emitted typed JSON serialiser for the
/// event's args. Receives the boxed args (or <see langword="null"/>) and
/// returns a <see cref="JsonNode"/> tree the resource provider stitches into
/// the outer payload, matching the shape <c>EventResourceProvider</c>'s legacy
/// reflection-based path produces. When non-null, the runtime uses this typed
/// lambda instead of <c>JsonSerializer.Serialize(args, args.GetType(),
/// options)</c> — closing the <c>IL2026</c>/<c>IL3050</c> warnings on the
/// args path. Bound at compile time to
/// <c>MarionetteEventArgsJsonContext.Default.&lt;TypeName&gt;</c> emitted by
/// the source generator; AOT-clean.
/// </param>
public sealed record EventDescriptor(
    string Name,
    string Description,
    string ArgsTypeName,
    string ArgsJsonSchema,
    int MinIntervalMs,
    int MaxQueueSize,
    int CoalesceWindowMs,
    Func<object, Action<object?>, IDisposable> Subscribe,
    Func<object?, JsonNode?>? SerializeArgs = null);
