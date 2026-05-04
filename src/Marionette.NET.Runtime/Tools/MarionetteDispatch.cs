// Marionette.NET — Phase 2.2 shared dispatch pipeline
//
// The four Phase-1 meta-tools (`invoke_method` etc.) and the Phase 2.2
// per-method dynamic tools both invoke `[McpCallable]` methods through the
// same registry / loop-protection / UI-dispatch pipeline. Phase 1.2 inlined
// the dispatch in MarionetteTools.InvokeMethodAsync; Phase 2.2 hoists it
// here so DynamicCallableHandler can reuse it without duplicating the
// argument-marshalling, UI-thread-routing, async-unwrapping, and
// loop-protection logic.
//
// Contract:
//   * Single async entrypoint `InvokeAsync(...)` — caller passes a resolved
//     `(rootName, callable)` plus the inbound JSON args bag. We return a
//     pre-serialised JSON string in all paths (success + structured error)
//     so callers can hand the result directly to MCP content blocks.
//   * Loop protection runs first; a failure short-circuits without touching
//     the adapter. Exceeded → structured error.
//   * Argument marshalling, dispatch, async unwrapping, error shaping are
//     identical to the Phase 1.2 implementation in MarionetteTools.
//
// AOT-clean: inputs are typed lambdas from the source generator. We never
// reflect on user types here.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Marionette.Runtime.Adapters;
using Marionette.Runtime.Loop;
using Marionette.Runtime.Manifest;

using Microsoft.Extensions.Logging;

namespace Marionette.Runtime.Tools;

/// <summary>
/// Hoisted shared pipeline for dispatching <c>[McpCallable]</c> methods.
/// Used by both <see cref="MarionetteTools.InvokeMethodAsync"/> (the meta
/// tool) and the Phase 2.2 dynamic per-method tool handler.
/// </summary>
internal static class MarionetteDispatch
{
    /// <summary>
    /// Run <paramref name="callable"/> on <paramref name="root"/> through the
    /// loop-guard + UI-thread + async-unwrap pipeline. Returns the
    /// JSON-serialised result text (or a structured error JSON).
    /// </summary>
    /// <param name="root">A registered root with a live instance (or an
    /// unbound entry; the helper surfaces a structured error in the latter
    /// case).</param>
    /// <param name="callable">The callable descriptor.</param>
    /// <param name="rawArgs">Inbound argument bag. <see langword="null"/>,
    /// <see cref="JsonValueKind.Null"/>, or <see cref="JsonValueKind.Undefined"/>
    /// are accepted when every parameter is optional.</param>
    /// <param name="adapter">UI dispatch adapter (NoOp in headless mode).</param>
    /// <param name="loopGuard">Hop-counter service.</param>
    /// <param name="logger">Logger for dispatch failures.</param>
    /// <param name="cancellationToken">Outer cancellation token.</param>
    public static async Task<string> InvokeAsync(
        RegisteredRoot root,
        CallableDescriptor callable,
        JsonElement? rawArgs,
        IUiAutomationAdapter adapter,
        LoopProtectionService loopGuard,
        ILogger logger,
        string? windowId,
        CancellationToken cancellationToken)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));
        if (callable is null) throw new ArgumentNullException(nameof(callable));
        if (adapter is null) throw new ArgumentNullException(nameof(adapter));
        if (loopGuard is null) throw new ArgumentNullException(nameof(loopGuard));
        if (logger is null) throw new ArgumentNullException(nameof(logger));

        // Loop protection — same shape as the Phase-1.2 meta-tool path.
        var hop = loopGuard.TryEnterHop();
        if (hop.Exceeded)
        {
            return new JsonObject
            {
                ["success"] = false,
                ["errorCode"] = "loop_limit_exceeded",
                ["message"] = $"Hop counter {hop.Hops} exceeds limit {loopGuard.MaxDepth}. Loop-protection " +
                              "(MASTERPLAN Spielregel 3) refuses further invocations until the call chain decays.",
                ["hops"] = hop.Hops,
            }.ToJsonString();
        }

        // Phase 3.3: when the adapter has a tracked instance for the named
        // root + windowId, prefer it over the registry's bare singleton so
        // multi-window apps route to the right ViewModel/Window. When the
        // adapter has nothing (headless NoOpAdapter, single-window apps),
        // fall back to root.Instance which the registry seeded at startup.
        var instance = adapter.GetRootInstance(root.Descriptor.Name, windowId) ?? root.Instance;
        if (instance is null)
        {
            return MakeError("root_unavailable",
                $"Root '{root.Descriptor.Name}' has no live instance: {root.CreateError ?? "no factory; install adapter that binds an instance"}.")
                .ToJsonString();
        }

        Dictionary<string, object?> argMap;
        try
        {
            argMap = MarshalArguments(callable, rawArgs);
        }
        catch (Exception ex)
        {
            return MakeError("argument_marshalling_failed", ex.Message).ToJsonString();
        }

        Func<object?> doCall = () => callable.Invoke(instance, argMap);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (callable.TimeoutSeconds > 0)
            {
                cts.CancelAfter(TimeSpan.FromSeconds(callable.TimeoutSeconds));
            }

            object? result;
            if (callable.OffUiThread)
            {
                result = await Task.Run(doCall, cts.Token).ConfigureAwait(false);
            }
            else
            {
                result = await adapter.DispatchAsync(doCall, cts.Token).ConfigureAwait(false);
            }

            if (callable.IsAsync)
            {
                result = await AwaitAndUnwrapAsync(result).ConfigureAwait(false);
            }

            // Phase 8.2: prefer the descriptor's typed SerializeResult lambda
            // when the return type is source-gen-eligible; fall back to the
            // legacy reflection-based path otherwise.
            return callable.SerializeResult is { } typed
                ? typed(result)
                : SerializeResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MakeError("cancelled", "Invocation was cancelled.").ToJsonString();
        }
        catch (OperationCanceledException) when (callable.TimeoutSeconds > 0)
        {
            return MakeError("timeout",
                $"Method '{root.Descriptor.Name}.{callable.Name}' did not complete within {callable.TimeoutSeconds}s.")
                .ToJsonString();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Marionette dispatch failed: {Root}.{Method}",
                root.Descriptor.Name, callable.Name);
            return MakeError("invocation_failed", ex.Message).ToJsonString();
        }
    }

    // -------------------------------------------------------------------------
    // Helpers — moved here from MarionetteTools so both tool surfaces share.
    // -------------------------------------------------------------------------

    internal static Dictionary<string, object?> MarshalArguments(CallableDescriptor callable, JsonElement? args)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (callable.Parameters.Count == 0) return map;

        if (args is not { } argsEl || argsEl.ValueKind == JsonValueKind.Undefined || argsEl.ValueKind == JsonValueKind.Null)
        {
            foreach (var p in callable.Parameters)
            {
                if (p.IsRequired)
                    throw new ArgumentException($"Required parameter '{p.Name}' was not supplied.");
                if (p.DefaultValue is not null) map[p.Name] = p.DefaultValue;
            }
            return map;
        }

        if (argsEl.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"args must be a JSON object; got {argsEl.ValueKind}.");

        foreach (var p in callable.Parameters)
        {
            if (argsEl.TryGetProperty(p.Name, out var propEl))
            {
                map[p.Name] = ConvertJsonToClr(propEl, p.ClrTypeName, p.Name);
            }
            else if (p.IsRequired)
            {
                throw new ArgumentException($"Required parameter '{p.Name}' was not supplied.");
            }
            else if (p.DefaultValue is not null)
            {
                map[p.Name] = p.DefaultValue;
            }
        }
        return map;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 4.2: the default branch deserialises into JsonElement, which is " +
                        "primitive-only and AOT-safe. The compiler can't see this from the generic " +
                        "constraint; the warning surfaces at MarionetteHost.RunAsync's RequiresUnreferencedCode.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Phase 4.2: same reasoning — JsonElement deserialisation requires no dynamic code.")]
    internal static object? ConvertJsonToClr(JsonElement el, string clrTypeName, string paramName)
    {
        if (el.ValueKind == JsonValueKind.Null) return null;

        var name = clrTypeName;
        if (name.EndsWith("?", StringComparison.Ordinal)) name = name[..^1];
        if (name.StartsWith("global::", StringComparison.Ordinal)) name = name["global::".Length..];

        switch (name)
        {
            case "int":
            case "System.Int32":
                return el.GetInt32();
            case "long":
            case "System.Int64":
                return el.GetInt64();
            case "short":
            case "System.Int16":
                return (short)el.GetInt32();
            case "byte":
            case "System.Byte":
                return (byte)el.GetInt32();
            case "sbyte":
            case "System.SByte":
                return (sbyte)el.GetInt32();
            case "ushort":
            case "System.UInt16":
                return (ushort)el.GetInt32();
            case "uint":
            case "System.UInt32":
                return el.GetUInt32();
            case "ulong":
            case "System.UInt64":
                return el.GetUInt64();
            case "float":
            case "System.Single":
                return el.GetSingle();
            case "double":
            case "System.Double":
                return el.GetDouble();
            case "decimal":
            case "System.Decimal":
                return el.GetDecimal();
            case "bool":
            case "System.Boolean":
                return el.GetBoolean();
            case "string":
            case "System.String":
                return el.ValueKind == JsonValueKind.Null ? null : el.GetString();
            case "char":
            case "System.Char":
                var s = el.GetString();
                return string.IsNullOrEmpty(s) ? '\0' : s![0];
            case "System.DateTime":
                return el.GetDateTime();
            case "System.DateTimeOffset":
                return el.GetDateTimeOffset();
            case "System.Guid":
                return el.GetGuid();
            case "System.TimeSpan":
                return TimeSpan.Parse(el.GetString() ?? string.Empty, System.Globalization.CultureInfo.InvariantCulture);
            case "System.Text.Json.JsonElement":
                return el.Clone();
            default:
                return el.Clone();
        }
    }

    /// <summary>
    /// Await any boxed Task / Task&lt;T&gt; / ValueTask / ValueTask&lt;T&gt; coming
    /// out of a callable's <c>Invoke</c> lambda and return the (boxed) result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AOT contract (Phase 4.2): the source generator wraps async callables in
    /// a typed async lambda that returns <see cref="Task{Object}"/> with
    /// <c>object?</c> as the type argument — the runtime's await path is
    /// therefore <b>statically typed</b> and does not need
    /// <see cref="System.Reflection.MethodInfo.Invoke"/>,
    /// <c>MakeGenericMethod</c>, or <c>Task&lt;T&gt;.Result</c> reflection.
    /// </para>
    /// <para>
    /// The remaining non-generic <see cref="Task"/> / <see cref="ValueTask"/>
    /// branches handle adopters who manually populate a descriptor with a
    /// custom <c>Invoke</c> lambda — keeping the surface forgiving without
    /// re-introducing reflection. The most common path (source-generator
    /// emission) hits the <c>Task&lt;object?&gt;</c> case first and returns.
    /// </para>
    /// </remarks>
    internal static async Task<object?> AwaitAndUnwrapAsync(object? maybeTask)
    {
        switch (maybeTask)
        {
            case null:
                return null;
            // Source-generator-emitted shape (Phase 4.2): typed Task<object?>.
            // Statically typed — no reflection.
            case Task<object?> generatorWrapped:
                return await generatorWrapped.ConfigureAwait(false);
            case Task task:
                // Non-generic Task: await without trying to reflect on a result.
                // Adopters who hand-author a Task<T>-returning Invoke will need
                // to wrap with their own typed lambda; the source generator
                // already wraps every async callable into Task<object?>.
                await task.ConfigureAwait(false);
                return null;
            case ValueTask vt:
                await vt.ConfigureAwait(false);
                return null;
            default:
                // Anything else is treated as a sync return value (already
                // boxed). No reflection on user types — keeps the runtime
                // AOT/trim-clean (Phase 4.2).
                return maybeTask;
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 4.2: SerializeResult uses System.Text.Json's reflection-based " +
                        "serialiser to round-trip [McpCallable] return values. The cascading " +
                        "warning surfaces at MarionetteHost.RunAsync's RequiresUnreferencedCode " +
                        "annotation; adopters who AOT-publish suppress at the host call site after " +
                        "auditing their callable return types (JSON-primitive shapes are safe).")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Phase 4.2: same reasoning — JsonSerializer may JIT-generate code at " +
                        "runtime for unknown user types. Phase 6 may move to source-generated " +
                        "JsonTypeInfo per descriptor.")]
    internal static string SerializeResult(object? value)
    {
        if (value is null) return "null";
        return JsonSerializer.Serialize(value, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
    }

    internal static JsonObject MakeError(string code, string message) => new()
    {
        ["success"] = false,
        ["errorCode"] = code,
        ["message"] = message,
    };
}
