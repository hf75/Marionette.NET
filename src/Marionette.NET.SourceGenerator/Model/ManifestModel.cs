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
    EquatableArray<string> EventArgsRootTypes = default,
    // Phase 8.2: parallel set for [McpObservable] property values and
    // [McpCallable] return types. Lives in MarionetteJsonContext (camelCase
    // naming policy, matching McpJsonUtilities.DefaultOptions). Built from
    // a separate JsonTypeCollector so the type graphs don't cross-pollute
    // and the two contexts each carry independent JsonTypeInfo<T> instances.
    EquatableArray<JsonTypeModel> ValueJsonTypes = default,
    EquatableArray<string> ValueRootTypes = default);

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
    EquatableArray<JsonTypeModel> EventArgsJsonTypes = default,
    // Phase 8.2: parallel set for [McpObservable] values and [McpCallable]
    // sync return types on this root. Feeds the camelCase
    // MarionetteJsonContext.
    EquatableArray<JsonTypeModel> ValueJsonTypes = default);

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
    string ParametersJsonSchema, // Phase 2.2: pre-computed input schema for the per-method MCP tool
    // Phase 8.2: encoded property name on MarionetteJsonContext for the
    // method's return type (or T from Task<T> / ValueTask<T>). Set when the
    // type's transitive graph is fully source-gen-eligible; null forces the
    // runtime to fall back to the legacy reflection-based MarionetteDispatch
    // serialiser.
    string? JsonReturnContextName = null);

/// <summary>
/// One parameter to an [McpCallable] method. DefaultValue is captured as a
/// pre-formatted C# literal expression — emitting it is a string copy.
/// </summary>
internal sealed record ParameterModel(
    string Name,
    string TypeFullName,
    bool IsRequired,
    string? DefaultLiteral,    // e.g. "0", "\"foo\"", "null", "1.5"
    string? EnumTypeFullName,  // non-null for enum / Nullable<enum> parameters
    // Phase 8.2: encoded property name on MarionetteJsonContext for the
    // parameter type. Lets the emitter switch from the AOT-unsafe
    // JsonSerializer.Deserialize<T>(string) overload to the typed
    // Deserialize<T>(string, JsonTypeInfo<T>) overload.
    string? JsonContextName = null);

/// <summary>
/// One [McpObservable] property. The generator emits a typed getter lambda.
/// </summary>
internal sealed record ObservableModel(
    string PropertyName,
    string Description,
    bool Watchable,
    int PollingIntervalMs,
    string PropertyTypeFullName,
    // Phase 8.2: encoded property name on MarionetteJsonContext for the
    // observable's value type. Same semantics as
    // <see cref="CallableModel.JsonReturnContextName"/>.
    string? JsonValueContextName = null);

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
    /// <summary>
    /// Phase 8.3: enum types. Emitted via
    /// <c>JsonMetadataServices.CreateValueInfo&lt;TEnum&gt;</c> with a
    /// <c>JsonStringEnumConverter&lt;TEnum&gt;</c>-derived converter so the
    /// JSON output matches the schema string emitted by
    /// <see cref="JsonSchemaWriter"/> (<c>"type":"string","enum":[...]</c>).
    /// </summary>
    Enum,
    /// <summary>
    /// Phase 8.4: single-dimensional arrays (<c>T[]</c>). Emitted via
    /// <c>JsonMetadataServices.CreateArrayInfo&lt;TElement&gt;</c> with an
    /// <c>ElementInfo</c> reference to the element type's
    /// <see cref="JsonTypeModel.UnderlyingTypeFullName"/>.
    /// </summary>
    Array,
    /// <summary>
    /// Phase 8.4: <c>List&lt;T&gt;</c>. Emitted via
    /// <c>JsonMetadataServices.CreateListInfo&lt;List&lt;T&gt;, T&gt;</c>.
    /// </summary>
    List,
    /// <summary>
    /// Phase 8.4 / 8.5: <c>Dictionary&lt;TKey, TValue&gt;</c>. The typed factory
    /// is <c>CreateDictionaryInfo&lt;Dictionary&lt;K,V&gt;, K, V&gt;</c>. Slice 4 only allowed
    /// <c>TKey == string</c>; slice 5 expands to every STJ-supported key shape
    /// (string + numeric primitives + bool + char + DateTime / DateTimeOffset /
    /// TimeSpan + Guid + Uri + Version + enums).
    /// </summary>
    Dictionary,
    /// <summary>
    /// Phase 8.5: <c>IEnumerable&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c>, and
    /// <c>IReadOnlyCollection&lt;T&gt;</c>. STJ groups these three under the
    /// <c>CreateIEnumerableInfo&lt;TCollection, TElement&gt;</c> factory; the
    /// <c>TCollection</c> generic argument carries the user-visible interface
    /// type so the produced <c>JsonTypeInfo&lt;T&gt;</c> matches the property's
    /// declared type.
    /// </summary>
    IEnumerable,
    /// <summary>
    /// Phase 8.5: <c>IList&lt;T&gt;</c> via
    /// <c>CreateIListInfo&lt;TCollection, TElement&gt;</c>.
    /// </summary>
    IList,
    /// <summary>
    /// Phase 8.5: <c>ICollection&lt;T&gt;</c> via
    /// <c>CreateICollectionInfo&lt;TCollection, TElement&gt;</c>.
    /// </summary>
    ICollection,
    /// <summary>
    /// Phase 8.5: <c>ISet&lt;T&gt;</c> via
    /// <c>CreateISetInfo&lt;TCollection, TElement&gt;</c>. Used for both the
    /// interface and concrete <c>HashSet&lt;T&gt;</c> (HashSet has no dedicated
    /// factory in STJ — the source generator dispatches concrete sets through
    /// the same path with <c>TCollection = HashSet&lt;T&gt;</c>).
    /// </summary>
    ISet,
    /// <summary>
    /// Phase 8.5: <c>IReadOnlySet&lt;T&gt;</c>. STJ on .NET 10 has no dedicated
    /// factory for this interface, so the emitter routes it through
    /// <c>CreateIEnumerableInfo&lt;TCollection, TElement&gt;</c> with
    /// <c>TCollection = IReadOnlySet&lt;T&gt;</c> and a <c>HashSet&lt;T&gt;</c>
    /// <c>ObjectCreator</c> (HashSet implements IReadOnlySet from .NET 5+).
    /// </summary>
    IReadOnlySet,
    /// <summary>
    /// Phase 8.5: <c>HashSet&lt;T&gt;</c>. Same factory call as
    /// <see cref="ISet"/> but the model carries the concrete <c>TCollection</c>
    /// for the JsonTypeInfo property's typing.
    /// </summary>
    HashSet,
    /// <summary>
    /// Phase 8.5: <c>Stack&lt;T&gt;</c> via
    /// <c>CreateStackInfo&lt;TCollection, TElement&gt;</c>.
    /// </summary>
    Stack,
    /// <summary>
    /// Phase 8.5: <c>Queue&lt;T&gt;</c> via
    /// <c>CreateQueueInfo&lt;TCollection, TElement&gt;</c>.
    /// </summary>
    Queue,
    /// <summary>
    /// Phase 8.5: <c>IDictionary&lt;TKey, TValue&gt;</c> via
    /// <c>CreateIDictionaryInfo&lt;TCollection, TKey, TValue&gt;</c>.
    /// Concrete container for the runtime <c>ObjectCreator</c> is
    /// <c>Dictionary&lt;TKey, TValue&gt;</c>.
    /// </summary>
    IDictionary,
    /// <summary>
    /// Phase 8.5: <c>IReadOnlyDictionary&lt;TKey, TValue&gt;</c> via
    /// <c>CreateIReadOnlyDictionaryInfo&lt;TCollection, TKey, TValue&gt;</c>.
    /// </summary>
    IReadOnlyDictionary,
    /// <summary>
    /// Phase 12.4: rank-2 multi-dimensional arrays (<c>T[,]</c>). STJ has no
    /// built-in metadata factory for them; the emitter wires
    /// <c>JsonMetadataServices.CreateValueInfo&lt;T[,]&gt;</c> against a
    /// runtime-supplied <see cref="Marionette.Runtime.Json.MultiDimArrayRank2Converter{TElement}"/>.
    /// Higher ranks (T[,,], T[,,,]) remain unsupported.
    /// </summary>
    MultiDimArrayRank2,
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
/// <param name="ElementContextName">
/// Phase 8.4: for <see cref="JsonTypeKind.Array"/> / <see cref="JsonTypeKind.List"/> /
/// <see cref="JsonTypeKind.Dictionary"/>, the encoded property name on the
/// generated context for the collection's element type. The Dictionary case
/// also uses <see cref="KeyContextName"/>; arrays and lists leave that null.
/// </param>
/// <param name="KeyContextName">
/// Phase 8.4: for <see cref="JsonTypeKind.Dictionary"/>, the encoded
/// property name for the key type (always <c>System_String</c> in slice 4 —
/// non-string-keyed dictionaries are unsupported and fall back to runtime
/// serialisation).
/// </param>
/// <param name="ElementTypeFullName">
/// Phase 8.4: canonical full name of the collection element type (so the
/// emitter can render <c>JsonMetadataServices.CreateListInfo&lt;...&gt;</c>
/// generic arguments without re-resolving symbols).
/// </param>
/// <param name="KeyTypeFullName">
/// Phase 8.5: canonical full name of the dictionary key type. Slice 4 hardcoded
/// <c>System.String</c>; slice 5 lifts the key to any STJ-supported shape, so
/// the emitter needs the full name to render the factory's <c>TKey</c> generic
/// argument. Always <see langword="null"/> for non-dictionary kinds.
/// </param>
/// <param name="ConcreteContainerOverride">
/// Phase 11: when set, this is the canonical full name of the concrete container
/// the emitter should pass to the factory's <c>ObjectCreator</c>. Built-in
/// shapes (<c>List&lt;T&gt;</c>, <c>Dictionary&lt;K,V&gt;</c>, <c>HashSet&lt;T&gt;</c>, …) leave this
/// <see langword="null"/> and the emitter substitutes a hard-coded template
/// (<c>List&lt;T&gt;</c>, <c>HashSet&lt;T&gt;</c>, etc.). When the collector matches a user
/// type that implements one of the supported interfaces (e.g.
/// <c>ConcurrentDictionary&lt;K,V&gt;</c> via <see cref="JsonTypeKind.IDictionary"/>),
/// the override carries the user type so the runtime allocates the correct
/// concrete instance instead of a default substitute.
/// </param>
/// <param name="ConcreteContainerHasParameterlessCtor">
/// Phase 12.6: <see langword="true"/> when the emitter should render
/// <c>ObjectCreator = static () =&gt; new &lt;Type&gt;()</c>. <see langword="false"/>
/// when the user collection type lacks a public parameterless ctor — the
/// emitter renders <c>ObjectCreator = null</c> instead, leaving the type
/// serialise-only (deserialisation by callers throws at runtime, which is
/// the correct contract: the serialise path enumerates an existing
/// instance and never needs a ctor).
/// </param>
internal sealed record JsonTypeModel(
    string TypeFullName,
    string PropertyName,
    JsonTypeKind Kind,
    string? PrimitiveConverter,
    string? UnderlyingTypeFullName,
    EquatableArray<JsonPropertyModel> Properties,
    string? ElementContextName = null,
    string? KeyContextName = null,
    string? ElementTypeFullName = null,
    string? KeyTypeFullName = null,
    string? ConcreteContainerOverride = null,
    bool ConcreteContainerHasParameterlessCtor = true);

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
/// <param name="IgnoreConditionLiteral">
/// Phase 12.7: a pre-rendered <c>JsonIgnoreCondition.X</c> literal (e.g.
/// <c>"WhenWritingDefault"</c> or <c>"WhenWritingNull"</c>) to emit into
/// <c>JsonPropertyInfoValues.IgnoreCondition</c>. <see langword="null"/>
/// when the property has no <c>[JsonIgnore]</c> at all OR has
/// <c>[JsonIgnore(Condition = JsonIgnoreCondition.Never)]</c>. Properties
/// with <c>Condition.Always</c> (the default) and <c>WhenWritingDefault</c>
/// /<c>WhenWritingNull</c> are handled by <see cref="JsonTypeCollector"/>:
/// <c>Always</c> drops the property entirely; the conditional cases keep
/// the property and store the condition here.
/// </param>
internal sealed record JsonPropertyModel(
    string Name,
    string DeclaringTypeFullName,
    string PropertyTypeFullName,
    string PropertyTypeContextName,
    string? IgnoreConditionLiteral = null);

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
