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
internal sealed record ManifestModel(
    string AssemblyName,
    EquatableArray<RootModel> Roots,
    EquatableArray<DiagnosticInfo> Diagnostics);

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
    EquatableArray<TriggerableModel> Triggerables);

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
    EquatableArray<ParameterModel> Parameters);

/// <summary>
/// One parameter to an [McpCallable] method. DefaultValue is captured as a
/// pre-formatted C# literal expression — emitting it is a string copy.
/// </summary>
internal sealed record ParameterModel(
    string Name,
    string TypeFullName,
    bool IsRequired,
    string? DefaultLiteral);   // e.g. "0", "\"foo\"", "null", "1.5"

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
