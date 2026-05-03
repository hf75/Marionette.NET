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
    public static Task<string> InvokeMethodAsync(
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
        // Phase 2.2: the dispatch pipeline (loop-protection, marshalling,
        // UI-thread routing, async unwrap, error shaping) is shared with the
        // dynamic per-method tool path via MarionetteDispatch. Resolution
        // (root + method lookup) stays here because the meta-tool surface
        // accepts string identifiers from the LLM and must surface
        // unknown_root / unknown_method structured errors itself.
        var registered = registry.Find(root);
        if (registered is null)
        {
            return Task.FromResult(MakeError("unknown_root", $"No root named '{root}' is registered.").ToJsonString());
        }

        var callable = registered.Descriptor.Callables.FirstOrDefault(c => c.Name == method);
        if (callable is null)
        {
            return Task.FromResult(MakeError("unknown_method",
                $"Root '{root}' has no [McpCallable] method named '{method}'.")
                .ToJsonString());
        }

        return MarionetteDispatch.InvokeAsync(
            registered, callable, args, adapter, loopGuard, logger, cancellationToken);
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
    // Helpers — argument marshalling, async unwrapping, and dispatch live in
    // MarionetteDispatch (Phase 2.2; shared with DynamicCallableHandler).
    // The remaining helpers below are inspect_app_api / read_observable
    // serialisation and the structured-error builder.
    // =========================================================================

    private static string SerializeResult(object? value)
        => MarionetteDispatch.SerializeResult(value);

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
