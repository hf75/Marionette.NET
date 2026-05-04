// Marionette.NET — Source Generator model records
//
// These records flow through the incremental pipeline. Roslyn caches each
// pipeline stage by structural equality on the produced value: if a record's
// fields are unchanged, the downstream stage is skipped and the same emitted
// source is reused. That is why every model type is a record (value equality
// for free) and every collection field is an EquatableArray<T> (sequence
// equality for free).
//
// Do NOT include `ISymbol`, `Compilation`, or `SyntaxNode` references in any
// of these models — those types do NOT support structural equality and would
// hold compilation graphs alive across edits, defeating the whole point of
// the Incremental Generator API.
//
// Diagnostics produced during validation flow alongside the model via a
// (Model?, ImmutableArray<DiagnosticInfo>) tuple. DiagnosticInfo is itself a
// record that carries everything needed to reconstruct a Roslyn Diagnostic at
// emit time without holding ISymbol references alive.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Marionette.SourceGenerator.Model;

/// <summary>
/// Top-level pipeline value: one emit unit per user assembly. Carries every
/// validated root and the diagnostics produced while validating them.
/// </summary>
/// <param name="AssemblyName">The user assembly's logical name.</param>
/// <param name="Roots">Validated [McpRoot]-decorated classes.</param>
/// <param name="Diagnostics">Diagnostics produced during validation.</param>
/// <param name="EventArgsJsonTypes">
/// Phase 8.1: types that need a typed <c>JsonTypeInfo&lt;T&gt;</c> in the
/// generated <c>MarionetteEventArgsJsonContext</c>. Discovered by walking
/// every [McpEvent] args type's public properties recursively. Empty when
/// no [McpEvent] declares an args type, or when the type graph contains
/// something the slice does not (yet) support — in which case the runtime
/// keeps the existing reflection-based serialisation path with the
/// <c>[RequiresUnreferencedCode]</c> annotation honoured.
/// </param>
/// <param name="EventArgsRootTypes">
/// Phase 8.1: the subset of [McpEvent] args types that are top-level
/// "root" types of the JSON graph — i.e. the descriptor's <c>SerializeArgs</c>
/// lambda references their <see cref="JsonTypeModel.PropertyName"/> directly.
/// Stored separately so the emitter can quickly decide whether to wire a
/// typed <c>SerializeArgs</c> lambda or leave it default-null.
/// </param>
internal sealed record ManifestModel(
    string AssemblyName,
    EquatableArray<RootModel> Roots,
    EquatableArray<DiagnosticInfo> Diagnostics,
    EquatableArray<JsonTypeModel> EventArgsJsonTypes = default,
    EquatableArray<string> EventArgsRootTypes = default);

/// <summary>
/// One [McpRoot]-decorated class plus its callable / observable / triggerable
/// members.
/// </summary>
internal sealed record RootModel(
    string ManifestName,           // [McpRoot(Name = "...")] or fallback to type name
    string TypeFullName,           // "Sample.Wpf.TodoApp.MainWindow"
    string TypeKind,               // for diagnostics: "Class", "Struct" — drives MAR001
    bool HasParameterlessCtor,     // controls whether we emit `() => new T()` or `null`
    EquatableArray<CallableModel> Callables,
    EquatableArray<ObservableModel> Observables,
    EquatableArray<TriggerableModel> Triggerables,
    EquatableArray<EventModel> Events,
    // Phase 8.1: closed transitive set of types that need JsonTypeInfo for
    // [McpEvent] args serialisation on this root. Empty when no [McpEvent]
    // declares an args type or every encountered graph contained an
    // unsupported shape (caller falls back to runtime serialisation).
    EquatableArray<JsonTypeModel> EventArgsJsonTypes = default);

/// <summary>
/// One [McpCallable] method. The generator emits an Invoke lambda that boxes
/// arguments out of the IReadOnlyDictionary, calls the method, and returns
/// the (possibly null) result.
/// </summary>
internal sealed record CallableModel(
    string MethodName,
    string Description,
    bool OffUiThread,
    int TimeoutSeconds,
    string ReturnTypeFullName,     // "System.Int32", "System.Void", "System.Threading.Tasks.Task"
    bool ReturnsTask,
    bool ReturnsTaskOfT,
    string? TaskResultTypeFullName,
    EquatableArray<ParameterModel> Parameters,
    string ParametersJsonSchema); // Phase 2.2: pre-computed input schema for the per-method MCP tool

/// <summary>
/// One parameter to an [McpCallable] method. DefaultValue is captured as a
/// pre-formatted C# literal expression — emitting it is a string copy.
/// </summary>
internal sealed record ParameterModel(
    string Name,
    string TypeFullName,
    bool IsRequired,
    string? DefaultLiteral,    // e.g. "0", "\"foo\"", "null", "1.5"
    string? EnumTypeFullName); // non-null for enum / Nullable<enum> parameters

/// <summary>
/// One [McpObservable] property. The generator emits a typed getter lambda.
/// </summary>
internal sealed record ObservableModel(
    string PropertyName,
    string Description,
    bool Watchable,
    int PollingIntervalMs,
    string PropertyTypeFullName);

/// <summary>
/// One [McpTriggerable] property. The generator emits a typed control resolver.
/// </summary>
internal sealed record TriggerableModel(
    string PropertyName,
    string Description,
    int Strategy,                  // raw enum value (Semantic=0, EventSystem=1, InputSystem=2)
    string ControlTypeFullName);

/// <summary>
/// Phase 8.1: classifies how a <see cref="JsonTypeModel"/> is materialised in
/// the generated <c>JsonSerializerContext</c>:
/// <list type="bullet">
///   <item><description><see cref="Object"/> — public class/record with public
///     get-only or init properties; emitted via
///     <c>JsonMetadataServices.CreateObjectInfo&lt;T&gt;</c>.</description></item>
///   <item><description><see cref="Primitive"/> — string / int / bool / DateTime /
///     etc. emitted via <c>JsonMetadataServices.CreateValueInfo&lt;T&gt;</c>
///     with the matching built-in converter.</description></item>
///   <item><description><see cref="Nullable"/> — <c>Nullable&lt;TInner&gt;</c>;
///     emitted via <c>JsonMetadataServices.CreateNullableInfo&lt;TInner&gt;</c>.
///     The inner type is stored separately under
///     <see cref="JsonTypeModel.UnderlyingTypeFullName"/>.</description></item>
/// </list>
/// </summary>
internal enum JsonTypeKind
{
    Object,
    Primitive,
    Nullable,
}

/// <summary>
/// Phase 8.1: one type that needs a typed <c>JsonTypeInfo&lt;T&gt;</c> in the
/// generated <c>JsonSerializerContext</c>. Also carries the property metadata
/// needed by the emitter to construct <c>JsonObjectInfoValues&lt;T&gt;</c> /
/// <c>JsonPropertyInfoValues&lt;TProperty&gt;</c> at compile time — the runtime
/// path is fully reflection-free.
/// </summary>
/// <param name="TypeFullName">Fully-qualified CLR type name (with <c>global::</c>).</param>
/// <param name="PropertyName">
/// Encoded JSON-context property identifier (<c>'.' </c>replaced with
/// <c>'_'</c>). E.g. <c>Demo.AppointmentAddedEventArgs</c> becomes
/// <c>Demo_AppointmentAddedEventArgs</c>.
/// </param>
/// <param name="Kind">Object / Primitive / Nullable shape selector.</param>
/// <param name="PrimitiveConverter">
/// Non-null for <see cref="JsonTypeKind.Primitive"/>: the
/// <c>JsonMetadataServices.&lt;X&gt;Converter</c> identifier name (e.g.
/// <c>StringConverter</c>, <c>Int32Converter</c>, <c>DateTimeConverter</c>).
/// </param>
/// <param name="UnderlyingTypeFullName">
/// Non-null for <see cref="JsonTypeKind.Nullable"/>: the wrapped value-type
/// full name (e.g. <c>global::System.DateTime</c> for <c>DateTime?</c>).
/// </param>
/// <param name="Properties">
/// For <see cref="JsonTypeKind.Object"/>: the type's public, JSON-relevant
/// properties in declaration order. Empty for primitive/nullable kinds.
/// </param>
internal sealed record JsonTypeModel(
    string TypeFullName,
    string PropertyName,
    JsonTypeKind Kind,
    string? PrimitiveConverter,
    string? UnderlyingTypeFullName,
    EquatableArray<JsonPropertyModel> Properties);

/// <summary>
/// Phase 8.1: a single property on a Json-context-tracked object type. The
/// emitter renders a typed getter lambda <c>static (obj) => ((T)obj).Name</c>
/// plus a reference to the property type's own <c>JsonTypeInfo</c>.
/// </summary>
/// <param name="Name">Property name as declared (PascalCase).</param>
/// <param name="DeclaringTypeFullName">The owning type's full name.</param>
/// <param name="PropertyTypeFullName">The property's type full name.</param>
/// <param name="PropertyTypeContextName">
/// The encoded <c>JsonTypeInfo</c> property name on the generated context
/// (e.g. <c>System_String</c>, <c>System_DateTime</c>). The emitter uses this
/// to wire the <c>PropertyTypeInfo</c> field of <c>JsonPropertyInfoValues</c>.
/// </param>
internal sealed record JsonPropertyModel(
    string Name,
    string DeclaringTypeFullName,
    string PropertyTypeFullName,
    string PropertyTypeContextName);

/// <summary>
/// One [McpEvent] event (Phase 1.6). Carries the delegate shape information
/// (HasArgsType + ArgsTypeFullName) so the emitter can write either an
/// <c>EventHandler</c> or <c>EventHandler&lt;T&gt;</c> typed bridge, plus the
/// pre-computed JSON schema string for the args type's public properties.
/// </summary>
internal sealed record EventModel(
    string EventName,
    string Description,
    bool HasArgsType,                      // false => EventHandler, true => EventHandler<T>
    string ArgsTypeFullName,               // "global::System.EventArgs" when HasArgsType is false
    string ArgsJsonSchema,                 // single-line JSON, deterministic
    int MinIntervalMs,
    int MaxQueueSize,
    int CoalesceWindowMs,
    // Phase 8.1: encoded property name on MarionetteEventArgsJsonContext. Set
    // when the args type's transitive graph is fully source-gen-eligible and
    // the JsonTypeCollector successfully registered it; the emitter then
    // wires a typed SerializeArgs lambda referencing
    // MarionetteEventArgsJsonContext.Default.<JsonRootContextName>. Null when
    // the args type is unsupported (System.EventArgs, generic, abstract, …)
    // and the runtime falls back to the legacy reflection-based path.
    string? JsonRootContextName = null);

/// <summary>
/// Carries enough information to reconstruct a Roslyn Diagnostic at the
/// emit-stage. We do not hold ISymbol or Location references in the pipeline
/// because those are not equatable and would defeat caching.
/// </summary>
internal sealed record DiagnosticInfo(
    string Id,
    string Title,
    string MessageFormat,
    string Category,
    int DefaultSeverity,           // mirrors DiagnosticSeverity (0=Hidden,1=Info,2=Warning,3=Error)
    EquatableArray<string> MessageArgs,
    LocationInfo? Location);

/// <summary>
/// File-path / line-position location, sufficient to rebuild a Roslyn
/// <see cref="Microsoft.CodeAnalysis.Location"/> without holding the original
/// SyntaxTree alive across compilation edits.
/// </summary>
internal sealed record LocationInfo(
    string FilePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

/// <summary>
/// Tiny equatable wrapper around an <see cref="ImmutableArray{T}"/>. The
/// stock <c>ImmutableArray</c> equality compares by reference (an array is
/// only equal to itself), which forces every pipeline node to invalidate on
/// every run. <see cref="EquatableArray{T}"/> compares by sequence so the
/// generator caches correctly. This pattern is the canonical
/// Microsoft-recommended remedy; see the .NET runtime source generator
/// (`System.Text.RegularExpressions.Generator`) for the same idiom.
/// </summary>
internal readonly struct EquatableArray<T> : System.IEquatable<EquatableArray<T>>
    where T : System.IEquatable<T>?
{
    public ImmutableArray<T> Array { get; }

    public EquatableArray(ImmutableArray<T> array) => Array = array;

    public static EquatableArray<T> Empty { get; } = new(ImmutableArray<T>.Empty);

    public bool Equals(EquatableArray<T> other)
    {
        if (Array.IsDefault) return other.Array.IsDefault;
        if (other.Array.IsDefault) return false;
        if (Array.Length != other.Array.Length) return false;
        for (int i = 0; i < Array.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(Array[i], other.Array[i])) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        if (Array.IsDefault) return 0;
        int hash = 17;
        foreach (var item in Array)
        {
            hash = unchecked(hash * 31 + (item is null ? 0 : EqualityComparer<T>.Default.GetHashCode(item!)));
        }
        return hash;
    }

    public int Length => Array.IsDefault ? 0 : Array.Length;

    public T this[int index] => Array[index];

    public ImmutableArray<T>.Enumerator GetEnumerator() => Array.GetEnumerator();

    public IEnumerable<T> AsEnumerable() => Array.IsDefault ? System.Linq.Enumerable.Empty<T>() : Array;
}

internal static class EquatableArrayExtensions
{
    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> source)
        where T : System.IEquatable<T>?
        => new(source.ToImmutableArray());

    public static EquatableArray<T> ToEquatableArray<T>(this ImmutableArray<T> source)
        where T : System.IEquatable<T>?
        => new(source);
}
