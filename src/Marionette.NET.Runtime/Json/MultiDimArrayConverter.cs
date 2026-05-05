// Marionette.NET — Phase 12.4 multi-dimensional array JsonConverter
//
// STJ has no built-in metadata factory for `T[,]` (or higher ranks) — the
// `[JsonSerializable(typeof(int[,]))]` source-gen path errors out, and
// `JsonMetadataServices.CreateArrayInfo` is rank-1-only. The pragmatic
// solution adopters were left with: hand-written custom JsonConverters
// per element type. We ship a generic one here so the source generator
// can register multi-dim shapes through `JsonMetadataServices.CreateValueInfo`.
//
// Wire format: row-major nested JSON arrays. For `int[2,3]`:
//
//   [[1, 2, 3], [4, 5, 6]]
//
// Higher ranks follow the same pattern recursively. Phase 13.E.13 adds
// rank-3 and rank-4 converters here. Rank 5+ remains unsupported (the
// pattern is mechanical — adopters can derive their own converter
// following the same shape if they need it).

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marionette.Runtime.Json;

/// <summary>
/// Phase 12.4: AOT-clean <see cref="JsonConverter{T}"/> for
/// rank-2 arrays of <typeparamref name="TElement"/>. The converter assumes
/// the element type is itself a primitive that <see cref="JsonSerializer"/>
/// can round-trip without metadata (most numeric types, <see cref="bool"/>,
/// <see cref="string"/>) — the source generator only registers a
/// rank-2 array type when its element type is a source-gen-eligible
/// primitive, so this constraint is satisfied at compile time.
/// </summary>
public sealed class MultiDimArrayRank2Converter<TElement> : JsonConverter<TElement[,]>
{
    private readonly JsonConverter<TElement> _elementConverter;

    /// <summary>
    /// Construct a converter using a source-gen-emitted typed
    /// <see cref="JsonConverter{TElement}"/> for the element type. Phase
    /// 12.4 supplies a primitive converter from
    /// <see cref="System.Text.Json.Serialization.Metadata.JsonMetadataServices"/>.
    /// </summary>
    public MultiDimArrayRank2Converter(JsonConverter<TElement> elementConverter)
    {
        _elementConverter = elementConverter ?? throw new ArgumentNullException(nameof(elementConverter));
    }

    public override TElement[,]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected StartArray for {typeof(TElement[,])}, got {reader.TokenType}.");

        // First pass: buffer all rows into a list of element arrays so we
        // can determine the column count from the first non-empty row and
        // confirm subsequent rows match (rectangular invariant of T[,]).
        var rows = new System.Collections.Generic.List<TElement[]>();
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException($"Expected nested StartArray (row), got {reader.TokenType}.");
            var row = new System.Collections.Generic.List<TElement>();
            reader.Read();
            while (reader.TokenType != JsonTokenType.EndArray)
            {
                row.Add(_elementConverter.Read(ref reader, typeof(TElement), options)!);
                reader.Read();
            }
            rows.Add(row.ToArray());
            reader.Read();
        }

        var rowCount = rows.Count;
        var colCount = rowCount == 0 ? 0 : rows[0].Length;
        for (int r = 1; r < rowCount; r++)
        {
            if (rows[r].Length != colCount)
            {
                throw new JsonException(
                    $"Rectangular invariant violated: row 0 has {colCount} columns, row {r} has {rows[r].Length}.");
            }
        }

        var result = new TElement[rowCount, colCount];
        for (int r = 0; r < rowCount; r++)
        {
            for (int c = 0; c < colCount; c++)
            {
                result[r, c] = rows[r][c];
            }
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, TElement[,] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        var rows = value.GetLength(0);
        var cols = value.GetLength(1);
        for (int r = 0; r < rows; r++)
        {
            writer.WriteStartArray();
            for (int c = 0; c < cols; c++)
            {
                _elementConverter.Write(writer, value[r, c], options);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// Phase 13.E.13: rank-3 multi-dim array converter. Wire format is nested
/// JSON arrays, three levels deep, row/plane/major: <c>[[[1,2],[3,4]],[[5,6],[7,8]]]</c>.
/// All three "axes" must be rectangular — the converter validates this on
/// read and throws <see cref="JsonException"/> when not.
/// </summary>
public sealed class MultiDimArrayRank3Converter<TElement> : JsonConverter<TElement[,,]>
{
    private readonly JsonConverter<TElement> _elementConverter;

    public MultiDimArrayRank3Converter(JsonConverter<TElement> elementConverter)
    {
        _elementConverter = elementConverter ?? throw new ArgumentNullException(nameof(elementConverter));
    }

    public override TElement[,,]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected StartArray for {typeof(TElement[,,])}, got {reader.TokenType}.");

        var planes = new System.Collections.Generic.List<TElement[][]>();
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException($"Expected nested StartArray (plane), got {reader.TokenType}.");
            var rows = new System.Collections.Generic.List<TElement[]>();
            reader.Read();
            while (reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                    throw new JsonException($"Expected nested StartArray (row), got {reader.TokenType}.");
                var row = new System.Collections.Generic.List<TElement>();
                reader.Read();
                while (reader.TokenType != JsonTokenType.EndArray)
                {
                    row.Add(_elementConverter.Read(ref reader, typeof(TElement), options)!);
                    reader.Read();
                }
                rows.Add(row.ToArray());
                reader.Read();
            }
            planes.Add(rows.ToArray());
            reader.Read();
        }

        var d0 = planes.Count;
        var d1 = d0 == 0 ? 0 : planes[0].Length;
        var d2 = d1 == 0 ? 0 : planes[0][0].Length;
        for (int i = 0; i < d0; i++)
        {
            if (planes[i].Length != d1)
                throw new JsonException($"Rectangular invariant violated on dim 1: plane 0 has {d1} rows, plane {i} has {planes[i].Length}.");
            for (int j = 0; j < d1; j++)
            {
                if (planes[i][j].Length != d2)
                    throw new JsonException($"Rectangular invariant violated on dim 2: row [0,0] has {d2} columns, row [{i},{j}] has {planes[i][j].Length}.");
            }
        }

        var result = new TElement[d0, d1, d2];
        for (int i = 0; i < d0; i++)
            for (int j = 0; j < d1; j++)
                for (int k = 0; k < d2; k++)
                    result[i, j, k] = planes[i][j][k];
        return result;
    }

    public override void Write(Utf8JsonWriter writer, TElement[,,] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        var d0 = value.GetLength(0);
        var d1 = value.GetLength(1);
        var d2 = value.GetLength(2);
        for (int i = 0; i < d0; i++)
        {
            writer.WriteStartArray();
            for (int j = 0; j < d1; j++)
            {
                writer.WriteStartArray();
                for (int k = 0; k < d2; k++)
                    _elementConverter.Write(writer, value[i, j, k], options);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// Phase 13.E.13: rank-4 multi-dim array converter. Same recursive nested-array
/// pattern as rank-3, one level deeper. Common adopter cases:
/// time-series-of-3D-volumes, 4-D weight tensors. Rectangular on every axis.
/// </summary>
public sealed class MultiDimArrayRank4Converter<TElement> : JsonConverter<TElement[,,,]>
{
    private readonly JsonConverter<TElement> _elementConverter;

    public MultiDimArrayRank4Converter(JsonConverter<TElement> elementConverter)
    {
        _elementConverter = elementConverter ?? throw new ArgumentNullException(nameof(elementConverter));
    }

    public override TElement[,,,]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Expected StartArray for {typeof(TElement[,,,])}, got {reader.TokenType}.");

        var hyperplanes = new System.Collections.Generic.List<TElement[][][]>();
        reader.Read();
        while (reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException($"Expected nested StartArray (hyperplane), got {reader.TokenType}.");
            var planes = new System.Collections.Generic.List<TElement[][]>();
            reader.Read();
            while (reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                    throw new JsonException($"Expected nested StartArray (plane), got {reader.TokenType}.");
                var rows = new System.Collections.Generic.List<TElement[]>();
                reader.Read();
                while (reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartArray)
                        throw new JsonException($"Expected nested StartArray (row), got {reader.TokenType}.");
                    var row = new System.Collections.Generic.List<TElement>();
                    reader.Read();
                    while (reader.TokenType != JsonTokenType.EndArray)
                    {
                        row.Add(_elementConverter.Read(ref reader, typeof(TElement), options)!);
                        reader.Read();
                    }
                    rows.Add(row.ToArray());
                    reader.Read();
                }
                planes.Add(rows.ToArray());
                reader.Read();
            }
            hyperplanes.Add(planes.ToArray());
            reader.Read();
        }

        var d0 = hyperplanes.Count;
        var d1 = d0 == 0 ? 0 : hyperplanes[0].Length;
        var d2 = d1 == 0 ? 0 : hyperplanes[0][0].Length;
        var d3 = d2 == 0 ? 0 : hyperplanes[0][0][0].Length;
        for (int h = 0; h < d0; h++)
        {
            if (hyperplanes[h].Length != d1)
                throw new JsonException($"Rectangular invariant violated on dim 1 (h={h}).");
            for (int i = 0; i < d1; i++)
            {
                if (hyperplanes[h][i].Length != d2)
                    throw new JsonException($"Rectangular invariant violated on dim 2 (h={h}, i={i}).");
                for (int j = 0; j < d2; j++)
                {
                    if (hyperplanes[h][i][j].Length != d3)
                        throw new JsonException($"Rectangular invariant violated on dim 3 (h={h}, i={i}, j={j}).");
                }
            }
        }

        var result = new TElement[d0, d1, d2, d3];
        for (int h = 0; h < d0; h++)
            for (int i = 0; i < d1; i++)
                for (int j = 0; j < d2; j++)
                    for (int k = 0; k < d3; k++)
                        result[h, i, j, k] = hyperplanes[h][i][j][k];
        return result;
    }

    public override void Write(Utf8JsonWriter writer, TElement[,,,] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        var d0 = value.GetLength(0);
        var d1 = value.GetLength(1);
        var d2 = value.GetLength(2);
        var d3 = value.GetLength(3);
        for (int h = 0; h < d0; h++)
        {
            writer.WriteStartArray();
            for (int i = 0; i < d1; i++)
            {
                writer.WriteStartArray();
                for (int j = 0; j < d2; j++)
                {
                    writer.WriteStartArray();
                    for (int k = 0; k < d3; k++)
                        _elementConverter.Write(writer, value[h, i, j, k], options);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}
