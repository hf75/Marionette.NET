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
// Higher ranks (rank 3+) follow the same pattern recursively. We ship a
// rank-2 converter here because that's the overwhelmingly common case
// (matrices, image data, grid state). Rank-3+ adopters can derive their
// own JsonConverter following the same shape; the generator currently
// falls back to runtime serialisation for ranks > 2, which under AOT
// produces a clear `InvalidOperationException`.

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
