// Marionette.NET — JSON schema writer for [McpEvent] EventArgs (Phase 1.6)
//
// Walks a TArgs ITypeSymbol's public instance properties and produces a
// deterministic single-line JSON schema string. The output is stored verbatim
// in EventDescriptor.ArgsJsonSchema so the runtime can return it (parsed) from
// inspect_app_api without re-walking symbols at runtime.
//
// Design rules:
//   * Deterministic order: properties sorted by name (Ordinal). Cycle detection
//     plus a depth-3 limit keeps output bounded for pathological types.
//   * Stable output: no whitespace beyond a single space after each ':' / ','
//     so the schema fits on one C# string-literal line in the emitted manifest.
//     The runtime's `JsonNode.Parse` reformats it for inspect_app_api anyway.
//   * AOT-clean: zero reflection at runtime (the schema is computed at
//     compile time, embedded as a plain string literal).
//
// Mapping (per Phase 1.6 spec):
//   * string                            -> { "type": "string" }
//   * bool                              -> { "type": "boolean" }
//   * int/long/short/byte (+unsigned)   -> { "type": "integer" }
//   * float/double/decimal              -> { "type": "number" }
//   * DateTime / DateTimeOffset         -> { "type": "string", "format": "date-time" }
//   * Guid / TimeSpan                   -> { "type": "string" }
//   * enum                              -> { "type": "string", "enum": [members...] }
//   * Nullable<T>                       -> base schema with "null" added to type
//   * arrays / IEnumerable<T>           -> { "type": "array", "items": {schema for T} }
//   * nested record/class               -> recurse, depth-bounded
//   * cycles, depth exceeded, unknown   -> { "description": "complex type" }

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Marionette.SourceGenerator;

internal static class JsonSchemaWriter
{
    private const int MaxDepth = 3;

    /// <summary>
    /// Emit a JSON schema for the given <c>TArgs</c> type symbol. Returns the
    /// canonical "complex type" placeholder when <paramref name="argsType"/>
    /// is null / error / has no public properties to describe, EXCEPT that
    /// <see cref="System.EventArgs"/> itself returns
    /// <c>{"type":"object","properties":{}}</c>.
    /// </summary>
    public static string WriteSchema(ITypeSymbol? argsType)
    {
        if (argsType is null) return EmptyObjectSchema();
        var sb = new StringBuilder();
        var seen = new HashSet<string>();
        WriteType(sb, argsType, seen, depth: 0);
        return sb.ToString();
    }

    /// <summary>
    /// Schema for a non-generic <see cref="System.EventArgs"/>, treated as the
    /// degenerate "no public payload" case.
    /// </summary>
    public static string EmptyObjectSchema() => "{\"type\":\"object\",\"properties\":{}}";

    /// <summary>
    /// Phase 2.2: Build a parameter object schema for an <c>[McpCallable]</c>
    /// method. Output shape:
    /// <code>{"type":"object","properties":{name1:&lt;schema&gt;,...},"required":["a","b"]}</code>
    /// Required parameters (no compile-time default) appear in
    /// <c>"required"</c>; optional parameters are listed in
    /// <c>"properties"</c> only. The order of properties matches the C#
    /// declaration order (deterministic). For zero-parameter methods we emit
    /// <c>{"type":"object","properties":{}}</c> (no <c>"required"</c>).
    /// </summary>
    /// <param name="parameters">Sequence of <c>(name, type, isRequired)</c> tuples.</param>
    public static string WriteParametersSchema(
        IEnumerable<(string Name, ITypeSymbol Type, bool IsRequired)> parameters)
    {
        var list = parameters?.ToList() ?? new List<(string, ITypeSymbol, bool)>();
        if (list.Count == 0) return EmptyObjectSchema();

        var sb = new StringBuilder();
        sb.Append("{\"type\":\"object\",\"properties\":{");
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(EscapeJson(list[i].Name)).Append("\":");
            // Each parameter type gets its own schema sub-tree. We re-use the
            // event-args walker — it already handles primitives, enums,
            // arrays, INPC-records — plus the depth/cycle guards. parameters
            // are always at depth 0 so depth budget is preserved.
            WriteType(sb, list[i].Type, new HashSet<string>(), depth: 0);
        }
        sb.Append('}');

        // "required" array — only if at least one parameter is required.
        var required = list.Where(p => p.IsRequired).Select(p => p.Name).ToList();
        if (required.Count > 0)
        {
            sb.Append(",\"required\":[");
            for (int i = 0; i < required.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(EscapeJson(required[i])).Append('"');
            }
            sb.Append(']');
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void WriteType(StringBuilder sb, ITypeSymbol type, HashSet<string> seen, int depth)
    {
        // Unwrap Nullable<T> first; the "null" union is added in the wrapper.
        if (type is INamedTypeSymbol nullable && IsNullableValueType(nullable, out var innerNullable))
        {
            WriteNullableUnion(sb, innerNullable!, seen, depth);
            return;
        }

        // Annotated reference types: T? where T is class/interface/struct.
        if (type.NullableAnnotation == NullableAnnotation.Annotated && type.IsReferenceType)
        {
            WriteNullableUnion(sb, type.WithNullableAnnotation(NullableAnnotation.NotAnnotated), seen, depth);
            return;
        }

        // Primitives.
        var fq = type.ToDisplayString();
        switch (fq)
        {
            case "string":
            case "System.String":
                sb.Append("{\"type\":\"string\"}"); return;
            case "bool":
            case "System.Boolean":
                sb.Append("{\"type\":\"boolean\"}"); return;
            case "byte":
            case "sbyte":
            case "short":
            case "ushort":
            case "int":
            case "uint":
            case "long":
            case "ulong":
            case "System.Byte":
            case "System.SByte":
            case "System.Int16":
            case "System.UInt16":
            case "System.Int32":
            case "System.UInt32":
            case "System.Int64":
            case "System.UInt64":
                sb.Append("{\"type\":\"integer\"}"); return;
            case "float":
            case "double":
            case "decimal":
            case "System.Single":
            case "System.Double":
            case "System.Decimal":
                sb.Append("{\"type\":\"number\"}"); return;
            case "System.DateTime":
            case "System.DateTimeOffset":
                sb.Append("{\"type\":\"string\",\"format\":\"date-time\"}"); return;
            case "System.Guid":
            case "System.TimeSpan":
                sb.Append("{\"type\":\"string\"}"); return;
            case "System.IO.Stream":
            case "System.IO.MemoryStream":
                // Phase 13.E.18: Stream params are wire-marshalled as base64
                // strings. JSON Schema convention is type=string format=byte.
                sb.Append("{\"type\":\"string\",\"format\":\"byte\"}"); return;
        }

        // Phase 13.E.18: Stream-derived user types — same base64 schema.
        for (var b = type.BaseType; b is not null; b = b.BaseType)
        {
            if (b.ToDisplayString() == "System.IO.Stream")
            {
                sb.Append("{\"type\":\"string\",\"format\":\"byte\"}");
                return;
            }
        }

        // Enum — emit string with enum-of-member-names list.
        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
        {
            var members = enumType.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(f => f.IsConst)
                .Select(f => f.Name)
                .OrderBy(n => n, System.StringComparer.Ordinal)
                .ToArray();
            sb.Append("{\"type\":\"string\",\"enum\":[");
            for (int i = 0; i < members.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(EscapeJson(members[i])).Append('"');
            }
            sb.Append("]}");
            return;
        }

        // Arrays.
        if (type is IArrayTypeSymbol arr)
        {
            sb.Append("{\"type\":\"array\",\"items\":");
            WriteType(sb, arr.ElementType, seen, depth + 1);
            sb.Append('}');
            return;
        }

        // IEnumerable<T> / IList<T> / ICollection<T> / List<T> / IReadOnlyList<T>.
        if (type is INamedTypeSymbol named && named.IsGenericType && IsEnumerableLike(named, out var element))
        {
            sb.Append("{\"type\":\"array\",\"items\":");
            WriteType(sb, element!, seen, depth + 1);
            sb.Append('}');
            return;
        }

        // Depth / cycle guard.
        if (depth >= MaxDepth)
        {
            sb.Append(ComplexTypePlaceholder); return;
        }
        var key = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!seen.Add(key))
        {
            sb.Append(ComplexTypePlaceholder); return;
        }
        try
        {
            // Object — walk public instance properties.
            if (type is INamedTypeSymbol nt && (nt.TypeKind == TypeKind.Class || nt.TypeKind == TypeKind.Struct))
            {
                var props = CollectPublicProperties(nt);
                sb.Append("{\"type\":\"object\",\"properties\":{");
                for (int i = 0; i < props.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(EscapeJson(props[i].Name)).Append("\":");
                    WriteType(sb, props[i].Type, seen, depth + 1);
                }
                sb.Append("}}");
                return;
            }

            sb.Append(ComplexTypePlaceholder);
        }
        finally
        {
            seen.Remove(key);
        }
    }

    private static void WriteNullableUnion(StringBuilder sb, ITypeSymbol inner, HashSet<string> seen, int depth)
    {
        // We emit `{"type":["string","null"]}` for primitives. For non-primitives
        // the spec says omit "required"; we still emit the inner schema so the
        // shape is descriptive. Adopters who rely on JSON-Schema strict mode can
        // post-process; the goal is informative, not validation-grade.
        var primitiveUnion = TryWritePrimitiveNullable(sb, inner);
        if (primitiveUnion) return;

        // Non-primitive nullable — write the inner schema unchanged.
        WriteType(sb, inner, seen, depth);
    }

    private static bool TryWritePrimitiveNullable(StringBuilder sb, ITypeSymbol inner)
    {
        var fq = inner.ToDisplayString();
        string? simple = fq switch
        {
            "string" or "System.String" => "string",
            "bool" or "System.Boolean" => "boolean",
            "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong"
                or "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16"
                or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" => "integer",
            "float" or "double" or "decimal"
                or "System.Single" or "System.Double" or "System.Decimal" => "number",
            "System.DateTime" or "System.DateTimeOffset" => "string",
            "System.Guid" or "System.TimeSpan" => "string",
            _ => null,
        };
        if (simple is null) return false;
        if (fq == "System.DateTime" || fq == "System.DateTimeOffset")
        {
            sb.Append("{\"type\":[\"string\",\"null\"],\"format\":\"date-time\"}");
        }
        else
        {
            sb.Append("{\"type\":[\"").Append(simple).Append("\",\"null\"]}");
        }
        return true;
    }

    private static bool IsNullableValueType(INamedTypeSymbol named, out ITypeSymbol? inner)
    {
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            named.TypeArguments.Length == 1)
        {
            inner = named.TypeArguments[0];
            return true;
        }
        inner = null;
        return false;
    }

    private static bool IsEnumerableLike(INamedTypeSymbol named, out ITypeSymbol? element)
    {
        // Direct match against well-known generic collections.
        var def = named.OriginalDefinition.ToDisplayString();
        switch (def)
        {
            case "System.Collections.Generic.IEnumerable<T>":
            case "System.Collections.Generic.IList<T>":
            case "System.Collections.Generic.ICollection<T>":
            case "System.Collections.Generic.IReadOnlyList<T>":
            case "System.Collections.Generic.IReadOnlyCollection<T>":
            case "System.Collections.Generic.List<T>":
            case "System.Collections.Generic.HashSet<T>":
            case "System.Collections.Immutable.ImmutableArray<T>":
            case "System.Collections.Immutable.ImmutableList<T>":
                element = named.TypeArguments[0];
                return true;
        }

        // Implements IEnumerable<T> (e.g. ObservableCollection<T>)?
        foreach (var i in named.AllInterfaces)
        {
            var iDef = i.OriginalDefinition.ToDisplayString();
            if (iDef == "System.Collections.Generic.IEnumerable<T>" && i.TypeArguments.Length == 1)
            {
                element = i.TypeArguments[0];
                return true;
            }
        }
        element = null;
        return false;
    }

    private static List<(string Name, ITypeSymbol Type)> CollectPublicProperties(INamedTypeSymbol type)
    {
        var seenNames = new HashSet<string>();
        var list = new List<(string, ITypeSymbol)>();
        for (var t = (INamedTypeSymbol?)type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var m in t.GetMembers())
            {
                if (m is not IPropertySymbol p) continue;
                if (p.IsStatic) continue;
                if (p.DeclaredAccessibility != Accessibility.Public) continue;
                if (p.GetMethod is null || p.GetMethod.DeclaredAccessibility != Accessibility.Public) continue;
                if (!seenNames.Add(p.Name)) continue;
                list.Add((p.Name, p.Type));
            }
        }
        list.Sort((a, b) => System.StringComparer.Ordinal.Compare(a.Item1, b.Item1));
        return list;
    }

    private const string ComplexTypePlaceholder = "{\"description\":\"complex type\"}";

    private static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
