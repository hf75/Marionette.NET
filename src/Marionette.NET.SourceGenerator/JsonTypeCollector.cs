// Marionette.NET — JSON type collector (Phase 8.1)
//
// Walks an ITypeSymbol and produces a closed transitive set of JsonTypeModel
// records that the JsonContextEmitter then materialises as a partial
// JsonSerializerContext using JsonMetadataServices. The walker is the
// boundary between Roslyn's symbol world (compilation-bound, non-equatable)
// and the source-generator pipeline's value world (immutable records,
// equatable, cache-stable).
//
// Slice 1 scope (event args only):
//   * Primitives: string, bool, byte, sbyte, short, ushort, int, uint,
//     long, ulong, float, double, decimal, char, DateTime, DateTimeOffset,
//     TimeSpan, Guid, Uri.
//   * Plain user classes/records with public read-accessible properties of
//     supported types (recursive). "Read-accessible" means a public getter;
//     init / set is not required (we serialise only).
//   * Nullable<T> where T is a supported value type.
//
// Out of scope for slice 1 — types that hit any of these break support for
// the entire transitive graph and the caller falls back to the existing
// reflection-based JsonSerializer.Serialize path:
//   * Generic instantiations (List<int>, Dictionary<K,V>, ...).
//   * Arrays (string[], int[], ...).
//   * Enums (deferred — JsonMetadataServices.GetEnumConverter shape is
//     different from primitive converters and warrants its own plumbing).
//   * Interfaces / abstract classes (no concrete shape to serialise).
//   * Types with [JsonIgnore]-decorated properties (we'd need to honour the
//     attribute; defer to slice 3).
//   * Self-referencing graphs (cycle detection just bails out).
//
// Design rules (mirroring the rest of the generator):
//   * Output records contain ONLY primitive values (strings, ints, bools).
//     No ITypeSymbol references survive past the walker boundary, keeping
//     the incremental pipeline cache-stable.
//   * Failure is non-fatal: TryAdd returns false; the caller leaves the
//     descriptor's typed Serialize lambda default-null, and the runtime's
//     legacy reflection path takes over for that descriptor.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Marionette.SourceGenerator.Model;
using Microsoft.CodeAnalysis;

namespace Marionette.SourceGenerator;

internal sealed class JsonTypeCollector
{
    /// <summary>
    /// Maximum recursion depth. Matches <see cref="JsonSchemaWriter"/>'s budget
    /// so the schema string and the JSON-context closure stay in sync. Beyond
    /// the limit the walker bails out — the descriptor falls back to runtime
    /// serialisation.
    /// </summary>
    private const int MaxDepth = 6;

    private readonly Dictionary<string, JsonTypeModel> _types = new(System.StringComparer.Ordinal);

    /// <summary>
    /// All collected types keyed by encoded property name (so the emitter can
    /// iterate deterministically and the JSON context's property-info
    /// references stay correct).
    /// </summary>
    public ImmutableArray<JsonTypeModel> AllTypes =>
        _types.Values.OrderBy(t => t.PropertyName, System.StringComparer.Ordinal).ToImmutableArray();

    /// <summary>
    /// Try to register <paramref name="type"/> (and every type its public
    /// properties transitively reference) into the collector.
    /// </summary>
    /// <param name="type">The candidate type symbol.</param>
    /// <param name="propertyName">
    /// On success: the encoded JSON-context property identifier the emitter
    /// should reference (e.g. <c>Demo_AppointmentAddedEventArgs</c>). On
    /// failure: <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="type"/> and every
    /// transitively-required type is supported and was added; <see langword="false"/>
    /// when any type in the graph is unsupported (the collector's state is
    /// rolled back so a partial registration does not pollute later attempts).
    /// </returns>
    public bool TryAdd(ITypeSymbol? type, out string? propertyName)
    {
        propertyName = null;
        if (type is null || type is IErrorTypeSymbol) return false;

        // Pre-snapshot dictionary contents for rollback-on-failure semantics.
        var snapshot = new Dictionary<string, JsonTypeModel>(_types, System.StringComparer.Ordinal);

        var visiting = new HashSet<string>(System.StringComparer.Ordinal);
        var name = TryRegister(type, visiting, depth: 0);
        if (name is null)
        {
            // Roll back any partial work.
            _types.Clear();
            foreach (var kv in snapshot) _types[kv.Key] = kv.Value;
            return false;
        }

        propertyName = name;
        return true;
    }

    private string? TryRegister(ITypeSymbol type, HashSet<string> visiting, int depth)
    {
        if (depth > MaxDepth) return null;
        if (type is IErrorTypeSymbol) return null;

        // Strip nullable annotation so `string?` and `string` register as the
        // same primitive type — the property's nullability is encoded by the
        // declaring container, not by a separate JsonTypeInfo.
        var unannotated = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);

        // Nullable<T>: register the wrapped value type, then the wrapper.
        // We canonicalise the wrapper's full name from the inner JsonTypeModel
        // so primitives don't leak C# aliases (e.g. `int?` -> `System.Int32?`).
        if (unannotated is INamedTypeSymbol named && IsNullableValueType(named, out var inner))
        {
            var innerName = TryRegister(inner!, visiting, depth + 1);
            if (innerName is null) return null;
            var nullableKey = "Nullable_" + innerName;
            if (!_types.ContainsKey(nullableKey))
            {
                var innerCanonical = _types[innerName].TypeFullName;
                _types[nullableKey] = new JsonTypeModel(
                    TypeFullName: innerCanonical + "?",
                    PropertyName: nullableKey,
                    Kind: JsonTypeKind.Nullable,
                    PrimitiveConverter: null,
                    UnderlyingTypeFullName: innerCanonical,
                    Properties: EquatableArray<JsonPropertyModel>.Empty);
            }
            return nullableKey;
        }

        // Primitive lookup table. We map the SpecialType to the canonical
        // CLR full name (e.g. SpecialType.System_String -> "System.String"),
        // NOT to ToDisplayString — which would return the C# keyword alias
        // (`string`) and produce invalid output like `global::string` and a
        // property named `string` (a reserved identifier).
        var (primitiveConverter, canonicalFqn) = ClassifyPrimitive(unannotated);
        if (primitiveConverter is not null)
        {
            var primKey = EncodePropertyName(canonicalFqn!);
            if (!_types.ContainsKey(primKey))
            {
                _types[primKey] = new JsonTypeModel(
                    TypeFullName: canonicalFqn!,
                    PropertyName: primKey,
                    Kind: JsonTypeKind.Primitive,
                    PrimitiveConverter: primitiveConverter,
                    UnderlyingTypeFullName: null,
                    Properties: EquatableArray<JsonPropertyModel>.Empty);
            }
            return primKey;
        }

        // Phase 8.3: enums first — they're concrete value types but the
        // emitter needs a different shape (CreateValueInfo + an enum
        // converter), so handle them before the object branch.
        if (unannotated is INamedTypeSymbol enumSym && enumSym.TypeKind == TypeKind.Enum)
        {
            var enumFqn = enumSym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var enumKey = EncodePropertyName(enumFqn);
            if (!_types.ContainsKey(enumKey))
            {
                _types[enumKey] = new JsonTypeModel(
                    TypeFullName: enumFqn,
                    PropertyName: enumKey,
                    Kind: JsonTypeKind.Enum,
                    PrimitiveConverter: null,
                    UnderlyingTypeFullName: null,
                    Properties: EquatableArray<JsonPropertyModel>.Empty);
            }
            return enumKey;
        }

        // Reject non-object shapes early.
        if (unannotated is not INamedTypeSymbol obj) return null;
        if (obj.IsAbstract || obj.IsStatic) return null;
        if (obj.TypeKind == TypeKind.Interface) return null;
        if (obj.TypeKind == TypeKind.Array) return null;          // slice 2+
        if (obj.IsGenericType && !obj.IsUnboundGenericType)
        {
            // Closed generic — defer until slice 2+ when we can wire
            // CreateListInfo / CreateDictionaryInfo correctly.
            return null;
        }
        if (obj.IsGenericType) return null;                        // open generic

        var typeKey = EncodePropertyName(obj.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        // Cycle: type already being registered upstream in this DFS branch.
        if (visiting.Contains(typeKey)) return null;
        // Already registered in a previous successful branch.
        if (_types.ContainsKey(typeKey)) return typeKey;

        visiting.Add(typeKey);

        // Walk public instance properties. We accept get-only, init-only, and
        // get/set — slice 1 only serialises so the setter requirement is
        // relaxed. We require a public getter though; otherwise we'd produce
        // a property the runtime can't actually read.
        var declaringFqn = obj.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var props = new List<JsonPropertyModel>();
        foreach (var member in obj.GetMembers())
        {
            if (member is not IPropertySymbol prop) continue;
            if (prop.DeclaredAccessibility != Accessibility.Public) continue;
            if (prop.IsStatic) continue;
            if (prop.IsIndexer) continue;
            if (prop.GetMethod is null) continue;
            if (prop.GetMethod.DeclaredAccessibility != Accessibility.Public) continue;
            // Phase 8.3: honour [JsonIgnore]. The attribute is matched by
            // metadata-name string (we cannot reference STJ from a
            // netstandard2.0 analyzer that doesn't reference the runtime
            // assembly directly). [JsonIgnore(Condition = ...)] sub-modes
            // are not differentiated — any non-default Condition is treated
            // as "always ignore" for source-gen purposes; adopters who need
            // conditional ignoring should keep the property out of the
            // generated context entirely.
            if (HasJsonIgnoreAttribute(prop)) continue;

            var childName = TryRegister(prop.Type, visiting, depth + 1);
            if (childName is null)
            {
                visiting.Remove(typeKey);
                return null;                  // unsupported transitive type
            }

            // Use the canonical full name stored on the just-registered
            // JsonTypeModel rather than ITypeSymbol.ToDisplayString, which
            // would return C# keyword aliases (`string`, `int`, `bool`) and
            // produce invalid `global::string`-style identifiers in the
            // emitted code.
            var canonicalPropTypeFqn = _types[childName].TypeFullName;

            props.Add(new JsonPropertyModel(
                Name: prop.Name,
                DeclaringTypeFullName: declaringFqn,
                PropertyTypeFullName: canonicalPropTypeFqn,
                PropertyTypeContextName: childName));
        }

        visiting.Remove(typeKey);

        _types[typeKey] = new JsonTypeModel(
            TypeFullName: declaringFqn,
            PropertyName: typeKey,
            Kind: JsonTypeKind.Object,
            PrimitiveConverter: null,
            UnderlyingTypeFullName: null,
            Properties: props.ToImmutableArray().ToEquatableArray());
        return typeKey;
    }

    /// <summary>
    /// Map a <see cref="ITypeSymbol"/> to a tuple of (JsonMetadataServices
    /// converter identifier, canonical CLR full name). The canonical full name
    /// is the BCL type name (e.g. <c>System.String</c>) — never the C#
    /// keyword alias — so the emitter can produce a valid
    /// <c>global::</c>-qualified identifier and a non-keyword property name.
    /// Returns <c>(null, null)</c> for non-primitives.
    /// </summary>
    private static (string? Converter, string? CanonicalFullName) ClassifyPrimitive(ITypeSymbol type)
    {
        var st = type.SpecialType;
        if (st != SpecialType.None)
        {
            return st switch
            {
                SpecialType.System_String => ("StringConverter", "System.String"),
                SpecialType.System_Boolean => ("BooleanConverter", "System.Boolean"),
                SpecialType.System_Byte => ("ByteConverter", "System.Byte"),
                SpecialType.System_SByte => ("SByteConverter", "System.SByte"),
                SpecialType.System_Int16 => ("Int16Converter", "System.Int16"),
                SpecialType.System_UInt16 => ("UInt16Converter", "System.UInt16"),
                SpecialType.System_Int32 => ("Int32Converter", "System.Int32"),
                SpecialType.System_UInt32 => ("UInt32Converter", "System.UInt32"),
                SpecialType.System_Int64 => ("Int64Converter", "System.Int64"),
                SpecialType.System_UInt64 => ("UInt64Converter", "System.UInt64"),
                SpecialType.System_Single => ("SingleConverter", "System.Single"),
                SpecialType.System_Double => ("DoubleConverter", "System.Double"),
                SpecialType.System_Decimal => ("DecimalConverter", "System.Decimal"),
                SpecialType.System_Char => ("CharConverter", "System.Char"),
                SpecialType.System_DateTime => ("DateTimeConverter", "System.DateTime"),
                SpecialType.System_Object => ("ObjectConverter", "System.Object"),
                _ => ((string?)null, (string?)null),
            };
        }

        // Non-special primitives: match by fully-qualified name.
        var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fqn switch
        {
            "global::System.DateTimeOffset" => ("DateTimeOffsetConverter", "System.DateTimeOffset"),
            "global::System.TimeSpan" => ("TimeSpanConverter", "System.TimeSpan"),
            "global::System.Guid" => ("GuidConverter", "System.Guid"),
            "global::System.Uri" => ("UriConverter", "System.Uri"),
            "global::System.Version" => ("VersionConverter", "System.Version"),
            _ => ((string?)null, (string?)null),
        };
    }

    /// <summary>
    /// Phase 8.3: detect <c>[JsonIgnore]</c> on a property. We match by
    /// metadata-name string because the generator may run in a Roslyn host
    /// that doesn't reference <c>System.Text.Json</c> directly. The actual
    /// <c>Condition</c> property is not inspected — adopters who need
    /// conditional ignoring should not rely on source-gen for that property.
    /// </summary>
    private static bool HasJsonIgnoreAttribute(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIgnoreAttribute")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Detect <see cref="System.Nullable{T}"/> instantiation. Must come before
    /// the generic-rejection branch so <c>int?</c> / <c>DateTime?</c> survive.
    /// </summary>
    private static bool IsNullableValueType(INamedTypeSymbol named, out ITypeSymbol? inner)
    {
        if (named.IsGenericType
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1)
        {
            inner = named.TypeArguments[0];
            return true;
        }
        inner = null;
        return false;
    }

    /// <summary>
    /// Replace dots / globals / colons with <c>'_'</c> so a CLR full name
    /// becomes a valid C# identifier suitable for a <see cref="JsonTypeInfo{T}"/>
    /// property name on the generated context. Deterministic + collision-free
    /// across namespaces.
    /// </summary>
    public static string EncodePropertyName(string typeFullName)
    {
        var name = typeFullName;
        if (name.StartsWith("global::", System.StringComparison.Ordinal))
            name = name.Substring("global::".Length);

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(ch == '.' ? '_' : ch);
        }
        return sb.ToString();
    }
}
