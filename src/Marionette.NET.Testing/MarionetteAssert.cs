using System;
using System.Text.Json;

namespace Marionette.Testing;

/// <summary>
/// Framework-neutral assertion helpers for Marionette tool JSON.
/// </summary>
public static class MarionetteAssert
{
    /// <summary>
    /// Throws <see cref="MarionetteToolException"/> when <paramref name="rawJson"/>
    /// is a Marionette structured error, otherwise returns the original JSON.
    /// </summary>
    public static string Succeeds(string rawJson)
    {
        if (TryGetError(rawJson, out var error))
        {
            throw new MarionetteToolException(error.ErrorCode, error.Message, rawJson);
        }
        return rawJson;
    }

    /// <summary>
    /// Assert that a raw JSON payload succeeded and deserialize it into
    /// <typeparamref name="T"/>.
    /// </summary>
    public static T? Deserialize<T>(string rawJson)
    {
        Succeeds(rawJson);
        if (string.Equals(rawJson, "null", StringComparison.Ordinal)) return default;
        return JsonSerializer.Deserialize<T>(rawJson, MarionetteJson.Options);
    }

    /// <summary>
    /// Detect the runtime's structured error shape.
    /// </summary>
    public static bool TryGetError(string rawJson, out MarionetteToolError error)
    {
        error = default;
        if (string.IsNullOrWhiteSpace(rawJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("success", out var success) ||
                success.ValueKind != JsonValueKind.False)
            {
                return false;
            }

            var code = root.TryGetProperty("errorCode", out var codeEl) &&
                       codeEl.ValueKind == JsonValueKind.String
                ? codeEl.GetString()
                : "tool_error";
            var message = root.TryGetProperty("message", out var msgEl) &&
                          msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString()
                : rawJson;

            error = new MarionetteToolError(code ?? "tool_error", message ?? rawJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// A parsed Marionette structured error.
/// </summary>
public readonly record struct MarionetteToolError(string ErrorCode, string Message);

/// <summary>
/// Exception thrown by typed testing helpers when the runtime returns a
/// Marionette structured error.
/// </summary>
public sealed class MarionetteToolException : Exception
{
    public MarionetteToolException(string errorCode, string message, string rawJson)
        : base(message)
    {
        ErrorCode = errorCode;
        RawJson = rawJson;
    }

    /// <summary>
    /// Runtime error code, for example <c>unknown_method</c> or
    /// <c>loop_limit_exceeded</c>.
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>
    /// Original JSON returned by the runtime.
    /// </summary>
    public string RawJson { get; }
}
