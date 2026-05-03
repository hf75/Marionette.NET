// Marionette.NET — Phase 2.2 deterministic tool identity
//
// Pure helper that turns a (rootName, CallableDescriptor) pair into a stable
// MCP tool name plus a SHA-256 fingerprint over the SIGNATURE (root + method
// name + ordered parameter (name, CLR type) pairs).
//
// Why "stable"? See MASTERPLAN Spielregel 5 ("idempotente Tool-Identity"):
// Claude's tool cache should not churn when an unrelated descriptor field
// (e.g. the description text) changes. Only signature changes alter the
// hash, so re-registering the same callable across runs produces the same
// identity.
//
// Naming policy:
//   * Default tool name = "<rootName>.<methodName>" (root casing is
//     preserved from the [McpRoot] manifest name; method casing is
//     preserved as declared in C#).
//   * Overload disambiguation: if two callables in the same root collapse
//     to the same default name (a manifest emits two methods named "Add"
//     with different signatures), suffix the second-and-following with
//     "_<8-hex>" derived from the full SHA-256 hash. The suffix is stable
//     per signature so a rebuild yields the same name.
//
// Canonical hash string (UTF-8, no BOM):
//   <rootName>\n<methodName>\n<paramName>:<clrTypeName>\n<paramName>:<clrTypeName>\n...
//   (no trailing newline; the first param block is preceded by exactly one
//   newline; if zero parameters, the body ends after the methodName line)
//
// AOT-clean: zero reflection, only string + SHA-256. The SHA implementation
// uses System.Security.Cryptography which is AOT-friendly on net10.0.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

using Marionette.Runtime.Manifest;

namespace Marionette.Runtime.Tools;

/// <summary>
/// Computes deterministic, signature-driven names and hashes for per-method
/// MCP tools (Phase 2.2). Public so the host can recompute identities when
/// the manifest is re-built (for hot-plug roots in later phases).
/// </summary>
public static class ToolIdentity
{
    private const int OverloadSuffixHexLength = 8;

    /// <summary>
    /// Compute the deterministic MCP tool name for a callable.
    /// </summary>
    /// <param name="rootName">The root's manifest name (e.g.
    /// <c>"TodoListViewModel"</c>). Case-preserved.</param>
    /// <param name="callable">The callable descriptor.</param>
    /// <returns>
    /// <c>"<rootName>.<methodName>"</c>. The <see cref="DisambiguateOverloads"/>
    /// helper applies a suffix when two callables in the same root would
    /// otherwise collide.
    /// </returns>
    public static string ComputeToolName(string rootName, CallableDescriptor callable)
    {
        if (rootName is null) throw new ArgumentNullException(nameof(rootName));
        if (callable is null) throw new ArgumentNullException(nameof(callable));
        return rootName + "." + callable.Name;
    }

    /// <summary>
    /// Compute the SHA-256 hash of the canonical signature string. Returns
    /// the lower-case hex of the full digest (64 characters). The first
    /// <see cref="OverloadSuffixHexLength"/> characters are used by
    /// <see cref="DisambiguateOverloads"/> as a stable suffix when overload
    /// names collide.
    /// </summary>
    public static string ComputeStableHash(string rootName, CallableDescriptor callable)
    {
        if (rootName is null) throw new ArgumentNullException(nameof(rootName));
        if (callable is null) throw new ArgumentNullException(nameof(callable));

        var canonical = BuildCanonicalSignature(rootName, callable);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var digest = SHA256.HashData(bytes);
        return ToHexLower(digest);
    }

    /// <summary>
    /// Take a list of <c>(name, hash)</c> pairs in registration order and
    /// rewrite any duplicate name to a disambiguated form
    /// <c>"<name>_<8-hex>"</c> using the first 8 hex chars of the hash. The
    /// first occurrence keeps the bare name; subsequent ones are suffixed.
    /// Idempotent: feeding the same input always produces the same output.
    /// </summary>
    /// <remarks>
    /// Disambiguation matters in two scenarios: (a) overloaded methods
    /// (same C# name, different signatures); (b) two roots that happened to
    /// share a manifest name plus a method name (the registry would never
    /// allow this, but defensive tools should still degrade gracefully).
    /// </remarks>
    public static IReadOnlyList<(string Name, string Hash)> DisambiguateOverloads(
        IReadOnlyList<(string BaseName, string Hash)> entries)
    {
        if (entries is null) throw new ArgumentNullException(nameof(entries));
        var result = new List<(string Name, string Hash)>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (baseName, hash) in entries)
        {
            if (seen.Add(baseName))
            {
                result.Add((baseName, hash));
                continue;
            }
            var suffix = hash.Length >= OverloadSuffixHexLength
                ? hash.Substring(0, OverloadSuffixHexLength)
                : hash;
            var disambiguated = baseName + "_" + suffix;
            // If even the suffix collides (extreme corner case), append the
            // full hash. We never silently drop entries.
            if (!seen.Add(disambiguated))
            {
                disambiguated = baseName + "_" + hash;
                seen.Add(disambiguated);
            }
            result.Add((disambiguated, hash));
        }
        return result;
    }

    /// <summary>
    /// Build the canonical signature string used for hashing. Format:
    /// <code>
    /// <rootName>\n<methodName>\n<param0Name>:<param0Type>\n...
    /// </code>
    /// (no trailing newline). Public for tests; downstream callers should
    /// prefer <see cref="ComputeStableHash"/>.
    /// </summary>
    public static string BuildCanonicalSignature(string rootName, CallableDescriptor callable)
    {
        var sb = new StringBuilder(rootName.Length + callable.Name.Length + 32);
        sb.Append(rootName);
        sb.Append('\n');
        sb.Append(callable.Name);
        foreach (var p in callable.Parameters)
        {
            sb.Append('\n');
            sb.Append(p.Name);
            sb.Append(':');
            sb.Append(NormalizeClrTypeName(p.ClrTypeName));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Strip a leading <c>global::</c> prefix so semantically-equivalent
    /// type spellings hash to the same value. Mirrors the runtime's
    /// parameter-marshaller normalization. Public for tests.
    /// </summary>
    public static string NormalizeClrTypeName(string clrTypeName)
    {
        if (string.IsNullOrEmpty(clrTypeName)) return string.Empty;
        var s = clrTypeName;
        if (s.StartsWith("global::", StringComparison.Ordinal)) s = s.Substring("global::".Length);
        // Trim the question mark so `int` and `int?` are NOT considered the
        // same — nullability IS part of the signature. Keep as-is.
        return s;
    }

    private static string ToHexLower(byte[] bytes)
    {
        var hex = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
        {
            hex.Append(bytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return hex.ToString();
    }
}
