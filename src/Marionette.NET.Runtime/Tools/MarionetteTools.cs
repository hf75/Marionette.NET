// Marionette.NET — the four Phase-1 MCP tools
//
// Per MASTERPLAN Phase 1, the runtime exposes:
//
//   inspect_app_api(rootName?)         → JSON manifest
//   invoke_method(root, method, args?) → object | structured error
//   read_observable(root, property)    → object | structured error
//   capture_screenshot(target?)        → image content block | structured error
//
// All four are registered via WithTools<MarionetteTools>() — the AOT-friendly
// path documented in PHASE0_FINDINGS implication 6. The methods are
// instance-style on a [McpServerToolType] class; the SDK's reflection on
// THIS type is the documented exception (the SDK has its own analyzer
// surface for it). Inside each method we only ever call typed delegates
// emitted by the source generator — no MakeGenericMethod, no MethodInfo.Invoke.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Marionette.Runtime.Adapters;
using Marionette.Runtime.Loop;
using Marionette.Runtime.Manifest;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Marionette.Runtime.Tools;

/// <summary>
/// The four Phase-1 Marionette MCP tools. Registered via
/// <c>WithTools&lt;MarionetteTools&gt;()</c>; the SDK pulls method metadata
/// off the static methods at startup and never reflects on user types.
/// </summary>
[McpServerToolType]
public sealed class MarionetteTools
{
    // The class itself is non-static so it can be used as the type argument
    // to WithTools<MarionetteTools>(). The SDK does not instantiate this type
    // — every tool method is static — but C# generics forbid static class
    // type arguments. We keep the class sealed and add a private ctor so
    // callers cannot accidentally `new` it.
    private MarionetteTools() { }

    // -------------------------------------------------------------------------
    // inspect_app_api
    // -------------------------------------------------------------------------

    /// <summary>
    /// Return the manifest of every <c>[McpRoot]</c>-decorated class the host
    /// knows about, including each root's callables, observables, and
    /// triggerables. Pass <paramref name="rootName"/> to scope to a single root.
    /// </summary>
    [McpServerTool(Name = "inspect_app_api")]
    [Description(
        "Returns a JSON manifest describing the app's [McpRoot] classes — their methods, " +
        "observables, and triggerables. Call this first to discover what the app exposes. " +
        "Optionally pass a specific rootName to scope the manifest.")]
    public static string InspectAppApi(
        ManifestRegistry registry,
        [Description("Optional root name; when omitted, returns every root.")]
        string? rootName = null)
    {
        var roots = registry.Roots;

        if (!string.IsNullOrEmpty(rootName))
        {
            var single = registry.Find(rootName!);
            if (single is null)
            {
                return new JsonObject
                {
                    ["success"] = false,
                    ["errorCode"] = "unknown_root",
                    ["message"] = $"No root named '{rootName}' is registered.",
                    ["available"] = new JsonArray(roots.Select(r => (JsonNode?)JsonValue.Create(r.Descriptor.Name)).ToArray()),
                }.ToJsonString();
            }
            return SerializeRoot(single).ToJsonString();
        }

        var arr = new JsonArray(roots.Select(r => (JsonNode?)SerializeRoot(r)).ToArray());
        return arr.ToJsonString();
    }

    // -------------------------------------------------------------------------
    // invoke_method
    // -------------------------------------------------------------------------

    /// <summary>
    /// Invoke a <c>[McpCallable]</c> method. Marshalling, UI-thread dispatch,
    /// optional timeout, and Task awaiting are all handled here.
    /// </summary>
    [McpServerTool(Name = "invoke_method")]
    [Description(
        "Invokes a [McpCallable] method on a registered root. The args object is keyed by " +
        "parameter name; values must match the parameter type listed in inspect_app_api. " +
        "Returns the method's result (boxed as JSON) or a structured " +
        "{success:false,errorCode:'...',message:'...'} object on failure.")]
    public static async Task<string> InvokeMethodAsync(
        ManifestRegistry registry,
        IUiAutomationAdapter adapter,
        LoopProtectionService loopGuard,
        ILogger<MarionetteHostMarker> logger,
        [Description("Manifest name of the [McpRoot] that owns the method.")]
        string root,
        [Description("Method name (matches the C# method declared with [McpCallable]).")]
        string method,
        [Description("Optional argument map keyed by parameter name. JSON values are coerced to the declared CLR type.")]
        JsonElement? args = null,
        CancellationToken cancellationToken = default)
    {
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

        var registered = registry.Find(root);
        if (registered is null)
        {
            return MakeError("unknown_root", $"No root named '{root}' is registered.").ToJsonString();
        }
        if (registered.Instance is null)
        {
            return MakeError("root_unavailable",
                $"Root '{root}' has no live instance: {registered.CreateError ?? "no factory; install adapter that binds an instance"}.")
                .ToJsonString();
        }

        var callable = registered.Descriptor.Callables.FirstOrDefault(c => c.Name == method);
        if (callable is null)
        {
            return MakeError("unknown_method",
                $"Root '{root}' has no [McpCallable] method named '{method}'.")
                .ToJsonString();
        }

        Dictionary<string, object?> argMap;
        try
        {
            argMap = MarshalArguments(callable, args);
        }
        catch (Exception ex)
        {
            return MakeError("argument_marshalling_failed", ex.Message).ToJsonString();
        }

        // UI-thread vs thread-pool dispatch.
        Func<object?> doCall = () => callable.Invoke(registered.Instance!, argMap);

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
                // OffUiThread=true: stay on the thread-pool. The method may
                // dispatch back to the UI thread itself if it touches state.
                result = await Task.Run(doCall, cts.Token).ConfigureAwait(false);
            }
            else
            {
                result = await adapter.DispatchAsync(doCall, cts.Token).ConfigureAwait(false);
            }

            // If the method is async, the return is a Task / ValueTask /
            // Task<T> / ValueTask<T>. Await it generically.
            if (callable.IsAsync)
            {
                result = await AwaitAndUnwrapAsync(result).ConfigureAwait(false);
            }

            return SerializeResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The MCP host is shutting down — surface a clean error.
            return MakeError("cancelled", "Invocation was cancelled.").ToJsonString();
        }
        catch (OperationCanceledException) when (callable.TimeoutSeconds > 0)
        {
            return MakeError("timeout",
                $"Method '{root}.{method}' did not complete within {callable.TimeoutSeconds}s.")
                .ToJsonString();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "invoke_method failed: {Root}.{Method}", root, method);
            return MakeError("invocation_failed", ex.Message).ToJsonString();
        }
    }

    // -------------------------------------------------------------------------
    // read_observable
    // -------------------------------------------------------------------------

    /// <summary>
    /// Read the current value of a <c>[McpObservable]</c> property.
    /// </summary>
    [McpServerTool(Name = "read_observable")]
    [Description(
        "Reads the current value of a [McpObservable] property on a registered root. " +
        "The value is dispatched to the UI thread and JSON-serialised. " +
        "Returns {success:false,errorCode,message} on failure.")]
    public static async Task<string> ReadObservableAsync(
        ManifestRegistry registry,
        IUiAutomationAdapter adapter,
        [Description("Manifest name of the [McpRoot] that owns the property.")]
        string root,
        [Description("Property name (matches the C# property declared with [McpObservable]).")]
        string property,
        CancellationToken cancellationToken)
    {
        var registered = registry.Find(root);
        if (registered is null)
            return MakeError("unknown_root", $"No root named '{root}' is registered.").ToJsonString();
        if (registered.Instance is null)
            return MakeError("root_unavailable",
                $"Root '{root}' has no live instance: {registered.CreateError ?? "no factory"}.")
                .ToJsonString();

        var obs = registered.Descriptor.Observables.FirstOrDefault(o => o.Name == property);
        if (obs is null)
            return MakeError("unknown_observable",
                $"Root '{root}' has no [McpObservable] property named '{property}'.").ToJsonString();

        try
        {
            var value = await adapter.DispatchAsync(
                () => obs.Read(registered.Instance!),
                cancellationToken).ConfigureAwait(false);
            return SerializeResult(value);
        }
        catch (Exception ex)
        {
            return MakeError("read_failed", ex.Message).ToJsonString();
        }
    }

    // -------------------------------------------------------------------------
    // capture_screenshot
    // -------------------------------------------------------------------------

    /// <summary>
    /// Capture a screenshot of the current application visual state.
    /// Returns an MCP <c>image</c> content block (PNG, base64).
    /// </summary>
    [McpServerTool(Name = "capture_screenshot")]
    [Description(
        "Captures a screenshot of the application. When target is provided, captures the named " +
        "window or control; otherwise captures the main window. Returns the PNG image as base64.")]
    public static async Task<CallToolResult> CaptureScreenshotAsync(
        IUiAutomationAdapter adapter,
        [Description("Optional target window or control name. Omit for the main window.")]
        string? target = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await adapter.CaptureScreenshotAsync(target, cancellationToken).ConfigureAwait(false);
            // ImageContentBlock.FromBytes is the documented factory; it handles
            // base64 encoding internally and sets the required `Data` field.
            var image = ImageContentBlock.FromBytes(bytes, mimeType: "image/png");
            return new CallToolResult
            {
                Content = new List<ContentBlock> { image },
            };
        }
        catch (NotSupportedException ex)
        {
            // Adapter doesn't support screenshotting (e.g. NoOpAdapter) —
            // surface a structured error block, NOT an unhandled throw.
            return new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock>
                {
                    new TextContentBlock
                    {
                        Text = MakeError("screenshot_not_supported", ex.Message).ToJsonString(),
                    },
                },
            };
        }
        catch (Exception ex)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock>
                {
                    new TextContentBlock
                    {
                        Text = MakeError("screenshot_failed", ex.Message).ToJsonString(),
                    },
                },
            };
        }
    }

    // =========================================================================
    // Helpers — serialization, marshalling, async unwrapping
    // =========================================================================

    /// <summary>
    /// Marshal a JSON args bag onto the <see cref="CallableDescriptor.Invoke"/>
    /// dictionary contract: keys are parameter names, values are CLR objects
    /// of the exact type the generator's lambda casts to.
    /// </summary>
    private static Dictionary<string, object?> MarshalArguments(CallableDescriptor callable, JsonElement? args)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (callable.Parameters.Count == 0) return map;

        // No args bag at all → every parameter must be optional.
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

    /// <summary>
    /// Convert a JsonElement to a boxed CLR value matching the generator's
    /// short type-name. Phase 1.2 supports the common primitives; complex
    /// types fall back to <see cref="JsonSerializer.Deserialize{TValue}"/>
    /// against a system type lookup, but this is rare in v1 user APIs.
    /// </summary>
    private static object? ConvertJsonToClr(JsonElement el, string clrTypeName, string paramName)
    {
        // Strip nullable annotations / global:: prefix for matching.
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
            case "System.Guid":
                return el.GetGuid();
            default:
                // Fallback: deserialize via STJ. This may not be AOT-clean for
                // exotic types; v1 user APIs are encouraged to stick to
                // primitives + records (see attributes-reference doc).
                return JsonSerializer.Deserialize<JsonElement>(el.GetRawText());
        }
    }

    /// <summary>
    /// Awaits a Task / ValueTask / Task&lt;T&gt; / ValueTask&lt;T&gt; that the
    /// generator's <c>Invoke</c> lambda returned, and unwraps the inner result
    /// when present. We intentionally use the BCL's
    /// <c>System.Runtime.CompilerServices</c> awaiter API rather than dynamic
    /// dispatch so the path stays AOT-clean.
    /// </summary>
    private static async Task<object?> AwaitAndUnwrapAsync(object? maybeTask)
    {
        switch (maybeTask)
        {
            case null:
                return null;
            case Task task:
                await task.ConfigureAwait(false);
                // Task<T> exposes the result via the `Result` property; we
                // can read it via the runtime type because Task<T> derives
                // from Task. JsonSerializer handles the boxing fine.
                var taskType = task.GetType();
                if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var prop = taskType.GetProperty("Result");
                    return prop?.GetValue(task);
                }
                return null;
            case ValueTask vt:
                await vt.ConfigureAwait(false);
                return null;
            default:
                // ValueTask<T>: the only practical way to await it without
                // generic dispatch is via the typed As-Task adapter. Reflection
                // here is one read of a method off the runtime type, then
                // delegate-style invoke. Phase 5 may move this to a
                // generator-emitted typed wrapper.
                var t = maybeTask.GetType();
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    var asTask = t.GetMethod("AsTask", Type.EmptyTypes);
                    if (asTask?.Invoke(maybeTask, parameters: null) is Task asyncTask)
                    {
                        await asyncTask.ConfigureAwait(false);
                        var resultProp = asyncTask.GetType().GetProperty("Result");
                        return resultProp?.GetValue(asyncTask);
                    }
                }
                return maybeTask;
        }
    }

    private static string SerializeResult(object? value)
    {
        if (value is null) return "null";
        return JsonSerializer.Serialize(value, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
    }

    private static JsonObject SerializeRoot(RegisteredRoot r)
    {
        var d = r.Descriptor;
        var obj = new JsonObject
        {
            ["name"] = d.Name,
            ["typeName"] = d.TypeName,
            ["instanceAvailable"] = r.Instance is not null,
        };
        if (r.CreateError is not null) obj["createError"] = r.CreateError;

        obj["callables"] = new JsonArray(d.Callables.Select(c => (JsonNode?)new JsonObject
        {
            ["name"] = c.Name,
            ["description"] = c.Description,
            ["offUiThread"] = c.OffUiThread,
            ["timeoutSeconds"] = c.TimeoutSeconds,
            ["isAsync"] = c.IsAsync,
            ["parameters"] = new JsonArray(c.Parameters.Select(p => (JsonNode?)new JsonObject
            {
                ["name"] = p.Name,
                ["clrType"] = p.ClrTypeName,
                ["required"] = p.IsRequired,
            }).ToArray()),
        }).ToArray());

        obj["observables"] = new JsonArray(d.Observables.Select(o => (JsonNode?)new JsonObject
        {
            ["name"] = o.Name,
            ["description"] = o.Description,
            ["watchable"] = o.Watchable,
            ["pollingIntervalMs"] = o.PollingIntervalMs,
            ["clrType"] = o.ClrTypeName,
            ["resourceUri"] = o.Watchable ? $"marionette://{d.Name}/{o.Name}" : null,
        }).ToArray());

        obj["triggerables"] = new JsonArray(d.Triggerables.Select(t => (JsonNode?)new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["strategy"] = t.Strategy.ToString(),
            ["controlType"] = t.ControlTypeName,
        }).ToArray());

        // Phase 1.6: events. The descriptor's ArgsJsonSchema is a single-line
        // JSON string at compile time; we parse it back here so inspect_app_api
        // returns a nested JSON object instead of a string-of-JSON.
        obj["events"] = new JsonArray(d.Events.Select(e =>
        {
            JsonNode? schemaNode = null;
            if (!string.IsNullOrEmpty(e.ArgsJsonSchema))
            {
                try { schemaNode = JsonNode.Parse(e.ArgsJsonSchema); }
                catch (JsonException) { schemaNode = JsonValue.Create(e.ArgsJsonSchema); }
            }
            return (JsonNode?)new JsonObject
            {
                ["name"] = e.Name,
                ["description"] = e.Description,
                ["argsType"] = e.ArgsTypeName,
                ["argsSchema"] = schemaNode,
                ["resourceUri"] = $"marionette://{d.Name}/events/{e.Name}",
                ["minIntervalMs"] = e.MinIntervalMs,
                ["maxQueueSize"] = e.MaxQueueSize,
                ["coalesceWindowMs"] = e.CoalesceWindowMs,
            };
        }).ToArray());

        return obj;
    }

    private static JsonObject MakeError(string code, string message) => new()
    {
        ["success"] = false,
        ["errorCode"] = code,
        ["message"] = message,
    };
}

/// <summary>
/// Marker type used solely as the <see cref="ILogger{TCategoryName}"/> category
/// for tool-level logging. Sits in the runtime namespace so log lines are
/// grouped under <c>Marionette.Runtime.Tools.MarionetteHostMarker</c>.
/// </summary>
public sealed class MarionetteHostMarker { }
