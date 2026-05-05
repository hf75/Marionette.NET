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
    /// Maximum recursion depth — Phase 12.8 lifted from 6 to 64 so deeply-
    /// nested-but-acyclic graphs (typical view-model trees, large DTO
    /// hierarchies) survive registration. True cycles are still caught by
    /// the per-call <c>visiting</c> HashSet — the depth cap is now only a
    /// stack-overflow safety net for pathological inputs. <see cref="JsonSchemaWriter"/>
    /// has its own much smaller cap (depth 3) for schema string size; the
    /// two are intentionally decoupled because the schema is descriptive
    /// while the JsonTypeInfo closure is load-bearing.
    /// </summary>
    private const int MaxDepth = 64;

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

        // Phase 8.4: arrays — IArrayTypeSymbol carries the element type via
        // .ElementType. Multi-dim arrays are unsupported (STJ requires a
        // typed factory shape that doesn't exist for them).
        if (unannotated is IArrayTypeSymbol arrSym && arrSym.Rank == 1)
        {
            var elementName = TryRegister(arrSym.ElementType, visiting, depth + 1);
            if (elementName is null) return null;
            var elementCanonical = _types[elementName].TypeFullName;
            var arrKey = "Array_" + elementName;
            if (!_types.ContainsKey(arrKey))
            {
                _types[arrKey] = new JsonTypeModel(
                    TypeFullName: elementCanonical + "[]",
                    PropertyName: arrKey,
                    Kind: JsonTypeKind.Array,
                    PrimitiveConverter: null,
                    UnderlyingTypeFullName: null,
                    Properties: EquatableArray<JsonPropertyModel>.Empty,
                    ElementContextName: elementName,
                    ElementTypeFullName: elementCanonical);
            }
            return arrKey;
        }

        // Phase 8.4 + 8.5: generic collection / dictionary types. Each
        // unbound-generic name maps to a JsonTypeKind and (for collections) to
        // a known STJ JsonMetadataServices factory.
        if (unannotated is INamedTypeSymbol genericSym &&
            genericSym.IsGenericType &&
            !genericSym.IsUnboundGenericType)
        {
            var unbound = genericSym.ConstructUnboundGenericType()
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            // Element-only generic collections: every shape STJ ships a
            // CreateXxxInfo<TCollection, TElement> factory for, plus the
            // List<T> case Phase 8.4 already handled. Each entry maps the
            // unbound generic name to a (kind, propertyKeyPrefix, userFqnTemplate)
            // triple — userFqnTemplate substitutes "{T}" with the element's
            // canonical full name.
            (JsonTypeKind kind, string keyPrefix, string fqnTemplate)? collectionShape = unbound switch
            {
                "global::System.Collections.Generic.List<>" =>
                    (JsonTypeKind.List, "List", "System.Collections.Generic.List<{T}>"),
                "global::System.Collections.Generic.IEnumerable<>" =>
                    (JsonTypeKind.IEnumerable, "IEnumerable", "System.Collections.Generic.IEnumerable<{T}>"),
                "global::System.Collections.Generic.IReadOnlyList<>" =>
                    (JsonTypeKind.IEnumerable, "IReadOnlyList", "System.Collections.Generic.IReadOnlyList<{T}>"),
                "global::System.Collections.Generic.IReadOnlyCollection<>" =>
                    (JsonTypeKind.IEnumerable, "IReadOnlyCollection", "System.Collections.Generic.IReadOnlyCollection<{T}>"),
                "global::System.Collections.Generic.IList<>" =>
                    (JsonTypeKind.IList, "IList", "System.Collections.Generic.IList<{T}>"),
                "global::System.Collections.Generic.ICollection<>" =>
                    (JsonTypeKind.ICollection, "ICollection", "System.Collections.Generic.ICollection<{T}>"),
                "global::System.Collections.Generic.ISet<>" =>
                    (JsonTypeKind.ISet, "ISet", "System.Collections.Generic.ISet<{T}>"),
                "global::System.Collections.Generic.IReadOnlySet<>" =>
                    (JsonTypeKind.IReadOnlySet, "IReadOnlySet", "System.Collections.Generic.IReadOnlySet<{T}>"),
                "global::System.Collections.Generic.HashSet<>" =>
                    (JsonTypeKind.HashSet, "HashSet", "System.Collections.Generic.HashSet<{T}>"),
                "global::System.Collections.Generic.Stack<>" =>
                    (JsonTypeKind.Stack, "Stack", "System.Collections.Generic.Stack<{T}>"),
                "global::System.Collections.Generic.Queue<>" =>
                    (JsonTypeKind.Queue, "Queue", "System.Collections.Generic.Queue<{T}>"),
                _ => null,
            };
            if (collectionShape.HasValue)
            {
                var (kind, keyPrefix, fqnTemplate) = collectionShape.Value;
                var elementName = TryRegister(genericSym.TypeArguments[0], visiting, depth + 1);
                if (elementName is null) return null;
                var elementCanonical = _types[elementName].TypeFullName;
                var collKey = keyPrefix + "_" + elementName;
                if (!_types.ContainsKey(collKey))
                {
                    _types[collKey] = new JsonTypeModel(
                        TypeFullName: fqnTemplate.Replace("{T}", elementCanonical),
                        PropertyName: collKey,
                        Kind: kind,
                        PrimitiveConverter: null,
                        UnderlyingTypeFullName: null,
                        Properties: EquatableArray<JsonPropertyModel>.Empty,
                        ElementContextName: elementName,
                        ElementTypeFullName: elementCanonical);
                }
                return collKey;
            }

            // Dictionary shapes — three flavours. The factory for each is
            // structurally identical; the only difference is the user-visible
            // type and the JsonTypeKind for the emitter to dispatch on.
            (JsonTypeKind kind, string keyPrefix, string fqnTemplate)? dictShape = unbound switch
            {
                "global::System.Collections.Generic.Dictionary<,>" =>
                    (JsonTypeKind.Dictionary, "Dictionary", "System.Collections.Generic.Dictionary<{K}, {V}>"),
                "global::System.Collections.Generic.IDictionary<,>" =>
                    (JsonTypeKind.IDictionary, "IDictionary", "System.Collections.Generic.IDictionary<{K}, {V}>"),
                "global::System.Collections.Generic.IReadOnlyDictionary<,>" =>
                    (JsonTypeKind.IReadOnlyDictionary, "IReadOnlyDictionary", "System.Collections.Generic.IReadOnlyDictionary<{K}, {V}>"),
                _ => null,
            };
            if (dictShape.HasValue)
            {
                var (kind, keyPrefix, fqnTemplate) = dictShape.Value;
                // Slice 5: accept every TKey shape STJ has a built-in
                // converter for. That's: string, all integral / floating-point
                // primitives, bool, char, DateTime / DateTimeOffset / TimeSpan,
                // Guid, Uri, Version, and enums.
                if (!IsSupportedDictionaryKey(genericSym.TypeArguments[0])) return null;
                var keyName = TryRegister(genericSym.TypeArguments[0], visiting, depth + 1);
                if (keyName is null) return null;
                var keyCanonical = _types[keyName].TypeFullName;
                var valueName = TryRegister(genericSym.TypeArguments[1], visiting, depth + 1);
                if (valueName is null) return null;
                var valueCanonical = _types[valueName].TypeFullName;
                var dictKey = keyPrefix + "_" + keyName + "_" + valueName;
                if (!_types.ContainsKey(dictKey))
                {
                    _types[dictKey] = new JsonTypeModel(
                        TypeFullName: fqnTemplate.Replace("{K}", keyCanonical).Replace("{V}", valueCanonical),
                        PropertyName: dictKey,
                        Kind: kind,
                        PrimitiveConverter: null,
                        UnderlyingTypeFullName: null,
                        Properties: EquatableArray<JsonPropertyModel>.Empty,
                        ElementContextName: valueName,
                        KeyContextName: keyName,
                        ElementTypeFullName: valueCanonical,
                        KeyTypeFullName: keyCanonical);
                }
                return dictKey;
            }

            // Phase 11: interface fallback. The exact unbound generic name did
            // not match a known shape, but the type might still be a custom
            // user collection (e.g. `class MyList<T> : IList<T>`) or a BCL
            // concurrent collection (`ConcurrentDictionary<K,V>`,
            // `ConcurrentQueue<T>`, `ConcurrentStack<T>`, `ConcurrentBag<T>`).
            // Walk the implemented interfaces — most-specific wins — and
            // register the user type as that kind, with TCollection = user
            // type and ConcreteContainerOverride = user type's FQN so the
            // ObjectCreator allocates the correct concrete instance.
            var fallbackKey = TryRegisterViaInterfaceFallback(genericSym, visiting, depth);
            if (fallbackKey is not null) return fallbackKey;

            // Any other generic instantiation we cannot route: caller falls
            // back to the runtime reflection serialiser.
            return null;
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

        // Reject non-object shapes early. (Arrays + the supported generics
        // are handled above; any remaining INamedTypeSymbol that's still a
        // generic at this point is an unsupported instantiation that the
        // generic block already rejected. We do reject open generics here
        // for completeness — they shouldn't appear in real user code as
        // typed parameters but are theoretically reachable through
        // typeof(MyType<>) tricks.)
        if (unannotated is not INamedTypeSymbol obj) return null;
        if (obj.IsAbstract || obj.IsStatic) return null;
        if (obj.TypeKind == TypeKind.Interface) return null;
        if (obj.IsGenericType) return null;

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
            // Phase 8.3 + 12.7: honour [JsonIgnore]. The attribute is matched by
            // metadata-name string (we cannot reference STJ from a
            // netstandard2.0 analyzer that doesn't reference the runtime
            // assembly directly).
            //
            // The `Condition` named argument selects the sub-mode:
            //   * Always (default; or no Condition arg)  → drop the property
            //   * Never                                  → include unconditionally (no IgnoreCondition emitted)
            //   * WhenWritingDefault / WhenWritingNull   → include + emit IgnoreCondition.<Mode>
            var ignoreMode = GetJsonIgnoreCondition(prop);
            if (ignoreMode == JsonIgnoreMode.Always) continue;

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

            string? ignoreLiteral = ignoreMode switch
            {
                JsonIgnoreMode.WhenWritingDefault => "WhenWritingDefault",
                JsonIgnoreMode.WhenWritingNull => "WhenWritingNull",
                _ => null,
            };

            props.Add(new JsonPropertyModel(
                Name: prop.Name,
                DeclaringTypeFullName: declaringFqn,
                PropertyTypeFullName: canonicalPropTypeFqn,
                PropertyTypeContextName: childName,
                IgnoreConditionLiteral: ignoreLiteral));
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
    /// Phase 11: when an unknown generic type is encountered, walk its
    /// implemented interfaces to find one of the standard collection
    /// contracts STJ has a factory for. Returns the registered property name
    /// when a match was made, or <see langword="null"/> when no supported
    /// interface was found.
    /// </summary>
    /// <remarks>
    /// Most-specific match wins. The dispatch order is:
    /// <list type="number">
    ///   <item><description>
    ///     <c>IDictionary&lt;K, V&gt;</c> → <see cref="JsonTypeKind.IDictionary"/>
    ///     (covers <c>ConcurrentDictionary&lt;K,V&gt;</c>, custom user dictionaries)
    ///   </description></item>
    ///   <item><description>
    ///     <c>IReadOnlyDictionary&lt;K, V&gt;</c> → <see cref="JsonTypeKind.IReadOnlyDictionary"/>
    ///   </description></item>
    ///   <item><description>
    ///     <c>ISet&lt;T&gt;</c> → <see cref="JsonTypeKind.ISet"/>
    ///     (covers any user set; <c>HashSet&lt;T&gt;</c> hits its dedicated
    ///     <see cref="JsonTypeKind.HashSet"/> match earlier)
    ///   </description></item>
    ///   <item><description>
    ///     <c>IList&lt;T&gt;</c> → <see cref="JsonTypeKind.IList"/>
    ///   </description></item>
    ///   <item><description>
    ///     <c>ICollection&lt;T&gt;</c> → <see cref="JsonTypeKind.ICollection"/>
    ///   </description></item>
    ///   <item><description>
    ///     <c>IEnumerable&lt;T&gt;</c> → <see cref="JsonTypeKind.IEnumerable"/>
    ///     (covers <c>ConcurrentQueue</c>, <c>ConcurrentStack</c>,
    ///     <c>ConcurrentBag</c>, custom enumerables)
    ///   </description></item>
    /// </list>
    /// The user type must have a public parameterless constructor; otherwise
    /// the runtime <c>ObjectCreator</c> would throw at deserialisation time
    /// (we cannot detect "needs init args" from the type alone). Types lacking
    /// a parameterless ctor stay on the legacy reflection-fallback path.
    /// </remarks>
    private string? TryRegisterViaInterfaceFallback(
        INamedTypeSymbol type,
        HashSet<string> visiting,
        int depth)
    {
        // The user type must be concrete and instantiable.
        if (type.IsAbstract || type.IsStatic) return null;
        if (type.TypeKind == TypeKind.Interface) return null;
        if (!HasPublicParameterlessCtor(type)) return null;

        var typeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Inspect every interface (direct + transitive) for a known shape.
        // We track the matched kind + the matched interface's type arguments
        // by unbound-generic-name string instead of by symbol reference, so
        // Roslyn's RS1024 (use SymbolEqualityComparer for symbol comparisons)
        // doesn't trigger.
        ITypeSymbol? matchedKey = null;
        ITypeSymbol? matchedValueOrElement = null;
        JsonTypeKind? matchedKind = null;
        // Most-specific first; once a kind is matched we stop refining.
        foreach (var iface in type.AllInterfaces)
        {
            if (!iface.IsGenericType || iface.IsUnboundGenericType) continue;
            var unbound = iface.ConstructUnboundGenericType()
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            JsonTypeKind? candidateKind = unbound switch
            {
                "global::System.Collections.Generic.IDictionary<,>" => JsonTypeKind.IDictionary,
                "global::System.Collections.Generic.IReadOnlyDictionary<,>" => JsonTypeKind.IReadOnlyDictionary,
                "global::System.Collections.Generic.ISet<>" => JsonTypeKind.ISet,
                "global::System.Collections.Generic.IList<>" => JsonTypeKind.IList,
                "global::System.Collections.Generic.ICollection<>" => JsonTypeKind.ICollection,
                "global::System.Collections.Generic.IEnumerable<>" => JsonTypeKind.IEnumerable,
                _ => null,
            };
            if (candidateKind is null) continue;
            // More-specific wins. The numeric ordering is hand-picked so that
            // smaller kind values are MORE specific. (See PrecedenceRank.)
            if (matchedKind is null || PrecedenceRank(candidateKind.Value) < PrecedenceRank(matchedKind.Value))
            {
                matchedKind = candidateKind;
                if (candidateKind is JsonTypeKind.IDictionary or JsonTypeKind.IReadOnlyDictionary)
                {
                    matchedKey = iface.TypeArguments[0];
                    matchedValueOrElement = iface.TypeArguments[1];
                }
                else
                {
                    matchedKey = null;
                    matchedValueOrElement = iface.TypeArguments[0];
                }
            }
        }
        if (matchedKind is null) return null;

        var unprefixedTypeFqn = typeFqn.StartsWith("global::", System.StringComparison.Ordinal)
            ? typeFqn.Substring("global::".Length)
            : typeFqn;
        var customKey = "Custom_" + EncodePropertyName(typeFqn);

        if (matchedKind is JsonTypeKind.IDictionary or JsonTypeKind.IReadOnlyDictionary)
        {
            if (!IsSupportedDictionaryKey(matchedKey!)) return null;
            var keyName = TryRegister(matchedKey!, visiting, depth + 1);
            if (keyName is null) return null;
            var keyCanonical = _types[keyName].TypeFullName;
            var valueName = TryRegister(matchedValueOrElement!, visiting, depth + 1);
            if (valueName is null) return null;
            var valueCanonical = _types[valueName].TypeFullName;
            if (!_types.ContainsKey(customKey))
            {
                _types[customKey] = new JsonTypeModel(
                    TypeFullName: unprefixedTypeFqn,
                    PropertyName: customKey,
                    Kind: matchedKind.Value,
                    PrimitiveConverter: null,
                    UnderlyingTypeFullName: null,
                    Properties: EquatableArray<JsonPropertyModel>.Empty,
                    ElementContextName: valueName,
                    KeyContextName: keyName,
                    ElementTypeFullName: valueCanonical,
                    KeyTypeFullName: keyCanonical,
                    ConcreteContainerOverride: unprefixedTypeFqn);
            }
            return customKey;
        }
        else
        {
            var elementName = TryRegister(matchedValueOrElement!, visiting, depth + 1);
            if (elementName is null) return null;
            var elementCanonical = _types[elementName].TypeFullName;
            if (!_types.ContainsKey(customKey))
            {
                _types[customKey] = new JsonTypeModel(
                    TypeFullName: unprefixedTypeFqn,
                    PropertyName: customKey,
                    Kind: matchedKind.Value,
                    PrimitiveConverter: null,
                    UnderlyingTypeFullName: null,
                    Properties: EquatableArray<JsonPropertyModel>.Empty,
                    ElementContextName: elementName,
                    ElementTypeFullName: elementCanonical,
                    ConcreteContainerOverride: unprefixedTypeFqn);
            }
            return customKey;
        }
    }

    /// <summary>
    /// Lower rank = more specific. Used by the interface-fallback walker to
    /// prefer the most-specific supported interface when a user type
    /// implements several at once.
    /// </summary>
    private static int PrecedenceRank(JsonTypeKind kind) => kind switch
    {
        JsonTypeKind.IDictionary => 0,
        JsonTypeKind.IReadOnlyDictionary => 1,
        JsonTypeKind.ISet => 2,
        JsonTypeKind.IList => 3,
        JsonTypeKind.ICollection => 4,
        JsonTypeKind.IEnumerable => 5,
        _ => 99,
    };

    private static bool HasPublicParameterlessCtor(INamedTypeSymbol type)
    {
        foreach (var ctor in type.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility == Accessibility.Public && ctor.Parameters.Length == 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Phase 8.5: STJ ships built-in <c>JsonConverter</c>s for a fixed set of
    /// dictionary key types. This helper enumerates that set so the collector
    /// only registers a dictionary type whose key actually round-trips through
    /// JSON. Out-of-set keys (custom types, <c>Tuple</c>, <c>nint</c>, etc.)
    /// fall back to the legacy reflection path.
    /// </summary>
    /// <remarks>
    /// Source: <c>System.Text.Json</c> documentation, "Supported types →
    /// Dictionary keys" in .NET 10. Enums are accepted because STJ converts
    /// them through their underlying integral converter at the key boundary.
    /// </remarks>
    private static bool IsSupportedDictionaryKey(ITypeSymbol type)
    {
        var unannotated = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        if (unannotated is INamedTypeSymbol enumSym && enumSym.TypeKind == TypeKind.Enum) return true;
        var (converter, _) = ClassifyPrimitive(unannotated);
        return converter is not null && converter != "ObjectConverter";
    }

    /// <summary>
    /// Phase 12.7: detect <c>[JsonIgnore]</c> + extract the
    /// <c>Condition</c> named argument. Returns:
    /// <list type="bullet">
    ///   <item><description><see cref="JsonIgnoreMode.None"/> — no <c>[JsonIgnore]</c> on the property.</description></item>
    ///   <item><description><see cref="JsonIgnoreMode.Always"/> — <c>[JsonIgnore]</c> with no Condition or <c>Condition = Always</c>.</description></item>
    ///   <item><description><see cref="JsonIgnoreMode.Never"/> — <c>Condition = Never</c> (treated like no [JsonIgnore]).</description></item>
    ///   <item><description><see cref="JsonIgnoreMode.WhenWritingDefault"/> / <see cref="JsonIgnoreMode.WhenWritingNull"/> — emit conditional skip.</description></item>
    /// </list>
    /// The attribute is matched by metadata-name string because the
    /// generator may run in a Roslyn host that doesn't reference
    /// <c>System.Text.Json</c> directly.
    /// </summary>
    private static JsonIgnoreMode GetJsonIgnoreCondition(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() != "System.Text.Json.Serialization.JsonIgnoreAttribute")
                continue;
            // Look for `Condition = JsonIgnoreCondition.X` in named args.
            foreach (var na in attr.NamedArguments)
            {
                if (na.Key != "Condition") continue;
                // The TypedConstant for an enum carries the underlying value
                // as Int32 + the enum's CLR type. Match by integer.
                // JsonIgnoreCondition: Never=0, Always=1, WhenWritingDefault=2, WhenWritingNull=3.
                if (na.Value.Value is int v)
                {
                    return v switch
                    {
                        0 => JsonIgnoreMode.Never,
                        1 => JsonIgnoreMode.Always,
                        2 => JsonIgnoreMode.WhenWritingDefault,
                        3 => JsonIgnoreMode.WhenWritingNull,
                        _ => JsonIgnoreMode.Always,
                    };
                }
            }
            // Bare [JsonIgnore] without Condition argument — default is
            // Always (per STJ docs).
            return JsonIgnoreMode.Always;
        }
        return JsonIgnoreMode.None;
    }

    /// <summary>
    /// Phase 12.7: encoded <c>System.Text.Json.Serialization.JsonIgnoreCondition</c>
    /// values plus a sentinel <see cref="None"/> for "no attribute".
    /// </summary>
    private enum JsonIgnoreMode
    {
        None = -1,
        Never = 0,
        Always = 1,
        WhenWritingDefault = 2,
        WhenWritingNull = 3,
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
    /// Replace dots / globals / colons / generic-syntax characters with
    /// <c>'_'</c> so a CLR full name becomes a valid C# identifier suitable
    /// for a <see cref="JsonTypeInfo{T}"/> property name on the generated
    /// context. Deterministic + collision-free across namespaces, even for
    /// closed generics like <c>MyDict&lt;string, int&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Characters mapped to <c>'_'</c>: <c>'.'</c>, <c>'&lt;'</c>,
    /// <c>'&gt;'</c>, <c>','</c>, space, <c>'?'</c> (nullable suffix),
    /// <c>'['</c>, <c>']'</c>. Consecutive runs of these collapse into
    /// underscores in the output, which is fine — the output is meant to
    /// be a stable token, not a round-trippable encoding.
    /// </remarks>
    public static string EncodePropertyName(string typeFullName)
    {
        var name = typeFullName;
        if (name.StartsWith("global::", System.StringComparison.Ordinal))
            name = name.Substring("global::".Length);

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            switch (ch)
            {
                case '.':
                case '<':
                case '>':
                case ',':
                case ' ':
                case '?':
                case '[':
                case ']':
                    sb.Append('_');
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
