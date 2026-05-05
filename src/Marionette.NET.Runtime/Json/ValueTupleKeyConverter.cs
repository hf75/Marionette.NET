// Marionette.NET — Phase 12.5 ValueTuple key converter for dictionaries
//
// STJ supports a fixed set of dictionary key types via built-in converters
// (string, primitives, char, bool, DateTime / DateTimeOffset, TimeSpan,
// Guid, Uri, Version, enums). Tuple-shaped keys (`(int, string)`) are NOT
// in that set: STJ throws `NotSupportedException` from the dictionary
// key-converter lookup.
//
// Phase 12.5 ships a generic `JsonConverter<(T1, T2)>` (and the rank-3
// variant) that delegates per-component read/write to typed primitive
// converters supplied at construction time. The wire format is a JSON
// array string used as the dictionary's JSON property name:
//
//   {
//     "[1,\"alpha\"]": "v",
//     "[2,\"beta\"]":  "v2"
//   }
//
// The array form is round-trippable for any element types that have
// JSON converters (the source generator gates on
// `IsSupportedDictionaryKey` ensuring each component has a built-in
// primitive or enum converter).
//
// AOT contract: the converter itself only ever talks to caller-supplied
// `JsonConverter<TX>` instances. Both Read* and Write* paths use a
// `Utf8JsonReader` / `Utf8JsonWriter` allocated inside the method — no
// reflection, no dynamic codegen.

using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marionette.Runtime.Json;

/// <summary>
/// Phase 12.5: <see cref="JsonConverter{T}"/> for the rank-2 value-tuple
/// dictionary-key shape <c>(T1, T2)</c>. Reads / writes both as a JSON
/// array (when the tuple appears as a value) and as a JSON-array string
/// (when the tuple appears as a dictionary key).
/// </summary>
public sealed class ValueTupleKeyConverter<T1, T2> : JsonConverter<(T1, T2)>
{
    private readonly JsonConverter<T1> _c1;
    private readonly JsonConverter<T2> _c2;

    public ValueTupleKeyConverter(JsonConverter<T1> component1, JsonConverter<T2> component2)
    {
        _c1 = component1 ?? throw new ArgumentNullException(nameof(component1));
        _c2 = component2 ?? throw new ArgumentNullException(nameof(component2));
    }

    public override (T1, T2) Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadFromArray(ref reader, _c1, _c2, options);

    public override void Write(Utf8JsonWriter writer, (T1, T2) value, JsonSerializerOptions options)
        => WriteAsArray(writer, value, _c1, _c2, options);

    public override (T1, T2) ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? throw new JsonException("dictionary key was null");
        var bytes = Encoding.UTF8.GetBytes(s);
        var inner = new Utf8JsonReader(bytes);
        if (!inner.Read())
            throw new JsonException("empty dictionary-key payload");
        return ReadFromArray(ref inner, _c1, _c2, options);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, (T1, T2) value, JsonSerializerOptions options)
    {
        using var ms = new MemoryStream();
        using (var inner = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            WriteAsArray(inner, value, _c1, _c2, options);
        }
        writer.WritePropertyName(Encoding.UTF8.GetString(ms.ToArray()));
    }

    internal static (T1, T2) ReadFromArray(
        ref Utf8JsonReader reader,
        JsonConverter<T1> c1,
        JsonConverter<T2> c2,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected StartArray for ValueTuple<,>, got {reader.TokenType}.");
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v1 = c1.Read(ref reader, typeof(T1), options)!;
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v2 = c2.Read(ref reader, typeof(T2), options)!;
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("ValueTuple<,> array did not end after 2 components");
        return (v1, v2);
    }

    internal static void WriteAsArray(
        Utf8JsonWriter writer,
        (T1, T2) value,
        JsonConverter<T1> c1,
        JsonConverter<T2> c2,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        c1.Write(writer, value.Item1, options);
        c2.Write(writer, value.Item2, options);
        writer.WriteEndArray();
    }
}

/// <summary>
/// Phase 12.5: <see cref="JsonConverter{T}"/> for the rank-3 value-tuple
/// dictionary-key shape <c>(T1, T2, T3)</c>.
/// </summary>
public sealed class ValueTupleKeyConverter<T1, T2, T3> : JsonConverter<(T1, T2, T3)>
{
    private readonly JsonConverter<T1> _c1;
    private readonly JsonConverter<T2> _c2;
    private readonly JsonConverter<T3> _c3;

    public ValueTupleKeyConverter(
        JsonConverter<T1> component1,
        JsonConverter<T2> component2,
        JsonConverter<T3> component3)
    {
        _c1 = component1 ?? throw new ArgumentNullException(nameof(component1));
        _c2 = component2 ?? throw new ArgumentNullException(nameof(component2));
        _c3 = component3 ?? throw new ArgumentNullException(nameof(component3));
    }

    public override (T1, T2, T3) Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadFromArray(ref reader, _c1, _c2, _c3, options);

    public override void Write(Utf8JsonWriter writer, (T1, T2, T3) value, JsonSerializerOptions options)
        => WriteAsArray(writer, value, _c1, _c2, _c3, options);

    public override (T1, T2, T3) ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? throw new JsonException("dictionary key was null");
        var bytes = Encoding.UTF8.GetBytes(s);
        var inner = new Utf8JsonReader(bytes);
        if (!inner.Read())
            throw new JsonException("empty dictionary-key payload");
        return ReadFromArray(ref inner, _c1, _c2, _c3, options);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, (T1, T2, T3) value, JsonSerializerOptions options)
    {
        using var ms = new MemoryStream();
        using (var inner = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            WriteAsArray(inner, value, _c1, _c2, _c3, options);
        }
        writer.WritePropertyName(Encoding.UTF8.GetString(ms.ToArray()));
    }

    internal static (T1, T2, T3) ReadFromArray(
        ref Utf8JsonReader reader,
        JsonConverter<T1> c1,
        JsonConverter<T2> c2,
        JsonConverter<T3> c3,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected StartArray for ValueTuple<,,>, got {reader.TokenType}.");
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v1 = c1.Read(ref reader, typeof(T1), options)!;
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v2 = c2.Read(ref reader, typeof(T2), options)!;
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v3 = c3.Read(ref reader, typeof(T3), options)!;
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("ValueTuple<,,> array did not end after 3 components");
        return (v1, v2, v3);
    }

    internal static void WriteAsArray(
        Utf8JsonWriter writer,
        (T1, T2, T3) value,
        JsonConverter<T1> c1,
        JsonConverter<T2> c2,
        JsonConverter<T3> c3,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        c1.Write(writer, value.Item1, options);
        c2.Write(writer, value.Item2, options);
        c3.Write(writer, value.Item3, options);
        writer.WriteEndArray();
    }
}

/// <summary>
/// Phase 13.E.14: <see cref="JsonConverter{T}"/> for the rank-4 value-tuple
/// dictionary-key shape <c>(T1, T2, T3, T4)</c>. Same wire format as the
/// rank-2 / rank-3 variants — JSON-array string used as the dictionary's
/// JSON property name.
/// </summary>
public sealed class ValueTupleKeyConverter<T1, T2, T3, T4> : JsonConverter<(T1, T2, T3, T4)>
{
    private readonly JsonConverter<T1> _c1;
    private readonly JsonConverter<T2> _c2;
    private readonly JsonConverter<T3> _c3;
    private readonly JsonConverter<T4> _c4;

    public ValueTupleKeyConverter(
        JsonConverter<T1> component1,
        JsonConverter<T2> component2,
        JsonConverter<T3> component3,
        JsonConverter<T4> component4)
    {
        _c1 = component1 ?? throw new ArgumentNullException(nameof(component1));
        _c2 = component2 ?? throw new ArgumentNullException(nameof(component2));
        _c3 = component3 ?? throw new ArgumentNullException(nameof(component3));
        _c4 = component4 ?? throw new ArgumentNullException(nameof(component4));
    }

    public override (T1, T2, T3, T4) Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadFromArray(ref reader, _c1, _c2, _c3, _c4, options);

    public override void Write(Utf8JsonWriter writer, (T1, T2, T3, T4) value, JsonSerializerOptions options)
        => WriteAsArray(writer, value, _c1, _c2, _c3, _c4, options);

    public override (T1, T2, T3, T4) ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? throw new JsonException("dictionary key was null");
        var bytes = Encoding.UTF8.GetBytes(s);
        var inner = new Utf8JsonReader(bytes);
        if (!inner.Read())
            throw new JsonException("empty dictionary-key payload");
        return ReadFromArray(ref inner, _c1, _c2, _c3, _c4, options);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, (T1, T2, T3, T4) value, JsonSerializerOptions options)
    {
        using var ms = new MemoryStream();
        using (var inner = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            WriteAsArray(inner, value, _c1, _c2, _c3, _c4, options);
        }
        writer.WritePropertyName(Encoding.UTF8.GetString(ms.ToArray()));
    }

    internal static (T1, T2, T3, T4) ReadFromArray(
        ref Utf8JsonReader reader,
        JsonConverter<T1> c1,
        JsonConverter<T2> c2,
        JsonConverter<T3> c3,
        JsonConverter<T4> c4,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected StartArray for ValueTuple<,,,>, got {reader.TokenType}.");
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v1 = c1.Read(ref reader, typeof(T1), options)!;
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v2 = c2.Read(ref reader, typeof(T2), options)!;
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v3 = c3.Read(ref reader, typeof(T3), options)!;
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v4 = c4.Read(ref reader, typeof(T4), options)!;
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("ValueTuple<,,,> array did not end after 4 components");
        return (v1, v2, v3, v4);
    }

    internal static void WriteAsArray(
        Utf8JsonWriter writer,
        (T1, T2, T3, T4) value,
        JsonConverter<T1> c1,
        JsonConverter<T2> c2,
        JsonConverter<T3> c3,
        JsonConverter<T4> c4,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        c1.Write(writer, value.Item1, options);
        c2.Write(writer, value.Item2, options);
        c3.Write(writer, value.Item3, options);
        c4.Write(writer, value.Item4, options);
        writer.WriteEndArray();
    }
}

/// <summary>
/// Phase 13.E.14: <see cref="JsonConverter{T}"/> for the rank-5 value-tuple
/// dictionary-key shape <c>(T1, T2, T3, T4, T5)</c>. Rank 6+ remains
/// unsupported (mechanical extension if a real adopter case appears).
/// </summary>
public sealed class ValueTupleKeyConverter<T1, T2, T3, T4, T5> : JsonConverter<(T1, T2, T3, T4, T5)>
{
    private readonly JsonConverter<T1> _c1;
    private readonly JsonConverter<T2> _c2;
    private readonly JsonConverter<T3> _c3;
    private readonly JsonConverter<T4> _c4;
    private readonly JsonConverter<T5> _c5;

    public ValueTupleKeyConverter(
        JsonConverter<T1> component1,
        JsonConverter<T2> component2,
        JsonConverter<T3> component3,
        JsonConverter<T4> component4,
        JsonConverter<T5> component5)
    {
        _c1 = component1 ?? throw new ArgumentNullException(nameof(component1));
        _c2 = component2 ?? throw new ArgumentNullException(nameof(component2));
        _c3 = component3 ?? throw new ArgumentNullException(nameof(component3));
        _c4 = component4 ?? throw new ArgumentNullException(nameof(component4));
        _c5 = component5 ?? throw new ArgumentNullException(nameof(component5));
    }

    public override (T1, T2, T3, T4, T5) Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadFromArray(ref reader, _c1, _c2, _c3, _c4, _c5, options);

    public override void Write(Utf8JsonWriter writer, (T1, T2, T3, T4, T5) value, JsonSerializerOptions options)
        => WriteAsArray(writer, value, _c1, _c2, _c3, _c4, _c5, options);

    public override (T1, T2, T3, T4, T5) ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? throw new JsonException("dictionary key was null");
        var bytes = Encoding.UTF8.GetBytes(s);
        var inner = new Utf8JsonReader(bytes);
        if (!inner.Read())
            throw new JsonException("empty dictionary-key payload");
        return ReadFromArray(ref inner, _c1, _c2, _c3, _c4, _c5, options);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, (T1, T2, T3, T4, T5) value, JsonSerializerOptions options)
    {
        using var ms = new MemoryStream();
        using (var inner = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            WriteAsArray(inner, value, _c1, _c2, _c3, _c4, _c5, options);
        }
        writer.WritePropertyName(Encoding.UTF8.GetString(ms.ToArray()));
    }

    internal static (T1, T2, T3, T4, T5) ReadFromArray(
        ref Utf8JsonReader reader,
        JsonConverter<T1> c1,
        JsonConverter<T2> c2,
        JsonConverter<T3> c3,
        JsonConverter<T4> c4,
        JsonConverter<T5> c5,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected StartArray for ValueTuple<,,,,>, got {reader.TokenType}.");
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v1 = c1.Read(ref reader, typeof(T1), options)!;
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v2 = c2.Read(ref reader, typeof(T2), options)!;
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v3 = c3.Read(ref reader, typeof(T3), options)!;
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v4 = c4.Read(ref reader, typeof(T4), options)!;
        if (!reader.Read()) throw new JsonException("truncated tuple key");
        var v5 = c5.Read(ref reader, typeof(T5), options)!;
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException("ValueTuple<,,,,> array did not end after 5 components");
        return (v1, v2, v3, v4, v5);
    }

    internal static void WriteAsArray(
        Utf8JsonWriter writer,
        (T1, T2, T3, T4, T5) value,
        JsonConverter<T1> c1,
        JsonConverter<T2> c2,
        JsonConverter<T3> c3,
        JsonConverter<T4> c4,
        JsonConverter<T5> c5,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        c1.Write(writer, value.Item1, options);
        c2.Write(writer, value.Item2, options);
        c3.Write(writer, value.Item3, options);
        c4.Write(writer, value.Item4, options);
        c5.Write(writer, value.Item5, options);
        writer.WriteEndArray();
    }
}
