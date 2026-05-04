// Marionette.NET — the Phase-1 + Phase-3.1 MCP tools
//
// Per MASTERPLAN Phase 1 + 3, the runtime exposes:
//
//   inspect_app_api(rootName?)                        → JSON manifest
//   invoke_method(root, method, args?)                → object | structured error
//   read_observable(root, property)                   → object | structured error
//   capture_screenshot(target?)                       → image content block | structured error
//   simulate_input(root, control, kind, args?)        → {success,errorCode?,message?}    [Phase 3.1]
//   raise_event(root, control, event, args?)          → {success,errorCode?,message?}    [Phase 3.1]
//
// All are registered via WithTools<MarionetteTools>() — the AOT-friendly
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
    /// Pass <paramref name="windowId"/> (Phase 3.3) to scope to a single live
    /// window — when omitted, the response includes a <c>windowIds</c> array
    /// for any root with 2+ live instances so the LLM can disambiguate.
    /// </summary>
    [McpServerTool(Name = "inspect_app_api")]
    [Description(
        "Returns a JSON manifest describing the app's [McpRoot] classes — their methods, " +
        "observables, and triggerables. Call this first to discover what the app exposes. " +
        "Optionally pass a specific rootName to scope the manifest. " +
        "When multiple windows of the same root are open (Phase 3.3 multi-window), " +
        "the response includes a 'windowIds' array per root so you can route per-window calls.")]
    public static string InspectAppApi(
        ManifestRegistry registry,
        IUiAutomationAdapter adapter,
        [Description("Optional root name; when omitted, returns every root.")]
        string? rootName = null,
        [Description("Optional Phase-3.3 window ID (e.g. 'w1', 'w2'). When omitted and multiple windows exist, the response advertises every windowId for that root.")]
        string? windowId = null)
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
            return SerializeRoot(single, adapter, windowId).ToJsonString();
        }

        var arr = new JsonArray(roots.Select(r => (JsonNode?)SerializeRoot(r, adapter, windowId)).ToArray());
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
        [Description("Optional Phase-3.3 window ID (e.g. 'w1', 'w2'). When omitted, routes to the oldest live window for the root.")]
        string? windowId = null,
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
            registered, callable, args, adapter, loopGuard, logger, windowId, cancellationToken);
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
        [Description("Optional Phase-3.3 window ID (e.g. 'w1', 'w2'). When omitted, reads the oldest live window for the root.")]
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        var registered = registry.Find(root);
        if (registered is null)
            return MakeError("unknown_root", $"No root named '{root}' is registered.").ToJsonString();

        // Phase 3.3: when an adapter has tracked a live instance for the named
        // root + windowId, prefer that one over the registry's bare singleton
        // — multi-window apps need the per-window instance, not the
        // registry-default. When the adapter has nothing tracked (headless
        // NoOpAdapter, single-window apps), fall back to registry.Instance.
        var instance = adapter.GetRootInstance(root, windowId) ?? registered.Instance;
        if (instance is null)
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
                () => obs.Read(instance),
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
        [Description("Optional Phase-3.3 window ID (e.g. 'w1', 'w2'). When omitted, captures the oldest live window.")]
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await adapter.CaptureScreenshotAsync(target, windowId, cancellationToken).ConfigureAwait(false);
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

    // -------------------------------------------------------------------------
    // simulate_input  (Phase 3.1)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Drive a real input event through the framework's input pipeline against
    /// the named control. Per MASTERPLAN tenet 8 ("Test-Automation-grade input
    /// fidelity"), this is the path that pumps real mouse/keyboard events
    /// through the framework's dispatcher so handlers, focus, capture, and
    /// hover all behave as if the user clicked.
    /// </summary>
    [McpServerTool(Name = "simulate_input")]
    [Description(
        "Drives a real input event (click, key press, type text, ...) through the framework's " +
        "input pipeline against a named control. The control is resolved via " +
        "AutomationProperties.AutomationId or x:Name in any open window. " +
        "Returns {success:true} or {success:false,errorCode,message}.")]
    public static async Task<string> SimulateInputAsync(
        ManifestRegistry registry,
        IUiAutomationAdapter adapter,
        LoopProtectionService loopGuard,
        ILogger<MarionetteHostMarker> logger,
        [Description("Manifest name of the [McpRoot] that owns the control (used as a multi-window disambiguation hint).")]
        string root,
        [Description("AutomationId or x:Name of the target control inside any currently-open window.")]
        string control,
        [Description("Input kind: click | double_click | right_click | key_press | key_down | key_up | type_text | mouse_move.")]
        string kind,
        [Description("Optional kind-specific arguments — e.g. {\"key\":\"Enter\"} for key_press, {\"text\":\"hello\"} for type_text, {\"x\":10,\"y\":20} for mouse_move.")]
        JsonElement? args = null,
        [Description("Optional Phase-3.3 window ID (e.g. 'w1', 'w2'). When omitted, the adapter walks every open window.")]
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(root))
            return MakeError("argument_marshalling_failed", "root must be non-empty.").ToJsonString();
        if (string.IsNullOrEmpty(control))
            return MakeError("argument_marshalling_failed", "control must be non-empty.").ToJsonString();
        if (string.IsNullOrEmpty(kind))
            return MakeError("argument_marshalling_failed", "kind must be non-empty.").ToJsonString();

        // Loop-protection — same shape as invoke_method. Without this an
        // Ai.Trigger -> simulate_input -> Ai.Trigger cycle could go wild.
        var hop = loopGuard.TryEnterHop();
        if (hop.Exceeded)
        {
            return new JsonObject
            {
                ["success"] = false,
                ["errorCode"] = "loop_limit_exceeded",
                ["message"] = $"Hop counter {hop.Hops} exceeds limit {loopGuard.MaxDepth}. " +
                              "Loop-protection (MASTERPLAN Spielregel 3) refuses further invocations until the call chain decays.",
                ["hops"] = hop.Hops,
            }.ToJsonString();
        }

        // The runtime can dispatch / not dispatch; the adapter is responsible
        // for routing onto the UI thread because input simulation is
        // thread-affine in WPF and Avalonia. The token + timeout (default 10s)
        // bound the call so a hung dispatcher doesn't lock the MCP loop.
        IReadOnlyDictionary<string, object?>? argMap = null;
        try
        {
            argMap = MaterialiseArgs(args);
        }
        catch (Exception ex)
        {
            return MakeError("argument_marshalling_failed", ex.Message).ToJsonString();
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var ok = await adapter.SimulateInputAsync(root, control, kind, argMap, windowId, cts.Token).ConfigureAwait(false);
            if (ok)
            {
                return new JsonObject
                {
                    ["success"] = true,
                    ["root"] = root,
                    ["control"] = control,
                    ["kind"] = kind,
                }.ToJsonString();
            }
            return MakeError("simulate_input_not_supported",
                $"Adapter could not simulate '{kind}' on '{root}.{control}'. The control may not exist, the kind may not be supported by the active adapter, or the headless mode lacks a UI thread.")
                .ToJsonString();
        }
        catch (OperationCanceledException)
        {
            return MakeError("cancelled", "simulate_input was cancelled or timed out (10s).").ToJsonString();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "simulate_input failed: {Root}.{Control} kind={Kind}", root, control, kind);
            return MakeError("simulate_input_failed", ex.Message).ToJsonString();
        }
    }

    // -------------------------------------------------------------------------
    // raise_event  (Phase 3.1)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Raise a routed/bubbling event on the named control. Bubbling/tunneling
    /// is honoured by the framework — handlers on parent containers fire as
    /// if the event came from a real source. Phase 3.1 defaults to
    /// parameterless <see cref="System.Windows.RoutedEventArgs"/> (or the
    /// Avalonia analogue); kind-specific args may be honoured in later
    /// phases.
    /// </summary>
    [McpServerTool(Name = "raise_event")]
    [Description(
        "Raises a named routed event (e.g. Click) on a control resolved by AutomationId or x:Name. " +
        "Bubbling and tunneling are honoured by the framework. Returns {success:true} or " +
        "{success:false,errorCode,message}.")]
    public static async Task<string> RaiseEventAsync(
        ManifestRegistry registry,
        IUiAutomationAdapter adapter,
        LoopProtectionService loopGuard,
        ILogger<MarionetteHostMarker> logger,
        [Description("Manifest name of the [McpRoot] that owns the control (multi-window disambiguation hint).")]
        string root,
        [Description("AutomationId or x:Name of the target control.")]
        string control,
        [Description("Event name as declared in C# (e.g. \"Click\", \"MouseDown\"). Inherited events on base types resolve too.")]
        string @event,
        [Description("Optional EventArgs property bag. Phase 3.1 ships default-constructed args.")]
        JsonElement? args = null,
        [Description("Optional Phase-3.3 window ID (e.g. 'w1', 'w2'). When omitted, the adapter walks every open window.")]
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(root))
            return MakeError("argument_marshalling_failed", "root must be non-empty.").ToJsonString();
        if (string.IsNullOrEmpty(control))
            return MakeError("argument_marshalling_failed", "control must be non-empty.").ToJsonString();
        if (string.IsNullOrEmpty(@event))
            return MakeError("argument_marshalling_failed", "event must be non-empty.").ToJsonString();

        var hop = loopGuard.TryEnterHop();
        if (hop.Exceeded)
        {
            return new JsonObject
            {
                ["success"] = false,
                ["errorCode"] = "loop_limit_exceeded",
                ["message"] = $"Hop counter {hop.Hops} exceeds limit {loopGuard.MaxDepth}.",
                ["hops"] = hop.Hops,
            }.ToJsonString();
        }

        IReadOnlyDictionary<string, object?>? argMap = null;
        try
        {
            argMap = MaterialiseArgs(args);
        }
        catch (Exception ex)
        {
            return MakeError("argument_marshalling_failed", ex.Message).ToJsonString();
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var ok = await adapter.RaiseEventAsync(root, control, @event, argMap, windowId, cts.Token).ConfigureAwait(false);
            if (ok)
            {
                return new JsonObject
                {
                    ["success"] = true,
                    ["root"] = root,
                    ["control"] = control,
                    ["event"] = @event,
                }.ToJsonString();
            }
            return MakeError("raise_event_not_supported",
                $"Adapter could not raise '{@event}' on '{root}.{control}'. The control may not exist, the event may not resolve on the control's type chain, or the active adapter doesn't implement event raising.")
                .ToJsonString();
        }
        catch (OperationCanceledException)
        {
            return MakeError("cancelled", "raise_event was cancelled or timed out (10s).").ToJsonString();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "raise_event failed: {Root}.{Control} event={Event}", root, control, @event);
            return MakeError("raise_event_failed", ex.Message).ToJsonString();
        }
    }

    /// <summary>
    /// Convert a JSON object element into the dictionary shape the adapter
    /// surface uses. Returns <see langword="null"/> for null/undefined args
    /// (the adapter treats this as "use defaults"). Throws on non-object
    /// JSON shapes — the runtime surfaces this as
    /// <c>argument_marshalling_failed</c>.
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? MaterialiseArgs(JsonElement? args)
    {
        if (args is not { } el || el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }
        if (el.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"args must be a JSON object; got {el.ValueKind}.");
        }

        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in el.EnumerateObject())
        {
            map[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? (object?)i :
                                       prop.Value.TryGetInt64(out var l) ? (object?)l :
                                       prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                _ => prop.Value.GetRawText(),
            };
        }
        return map;
    }

    // =========================================================================
    // Helpers — argument marshalling, async unwrapping, and dispatch live in
    // MarionetteDispatch (Phase 2.2; shared with DynamicCallableHandler).
    // The remaining helpers below are inspect_app_api / read_observable
    // serialisation and the structured-error builder.
    // =========================================================================

    private static string SerializeResult(object? value)
        => MarionetteDispatch.SerializeResult(value);

    private static JsonObject SerializeRoot(RegisteredRoot r, IUiAutomationAdapter adapter, string? windowId)
    {
        var d = r.Descriptor;
        var obj = new JsonObject
        {
            ["name"] = d.Name,
            ["typeName"] = d.TypeName,
            ["instanceAvailable"] = r.Instance is not null,
        };
        if (r.CreateError is not null) obj["createError"] = r.CreateError;

        // Phase 3.3: advertise per-window IDs for any root with 2+ live
        // instances so the LLM can route per-window calls. Single-window
        // (or zero-window-headless) cases omit the field — keeps Phase
        // 1.x/2.x consumers compatible.
        var liveWindowIds = adapter.GetWindowIds(d.Name);
        if (liveWindowIds is { Count: > 1 })
        {
            var arr = new JsonArray();
            foreach (var id in liveWindowIds) arr.Add(JsonValue.Create(id));
            obj["windowIds"] = arr;
        }
        if (!string.IsNullOrEmpty(windowId))
        {
            // Echo back the resolved windowId so the LLM can correlate the
            // response with the request when scoping was requested.
            obj["windowId"] = windowId;
        }

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

        // Phase 3.1: advertise the input kinds simulate_input accepts so the
        // LLM doesn't have to guess. The list is adapter-independent — every
        // implemented adapter (WPF, Avalonia) handles all eight kinds; an
        // adapter that doesn't returns false at SimulateInputAsync time. The
        // entry sits at the root level (not per-triggerable) because
        // simulate_input works on ANY named control, not just [McpTriggerable]
        // properties.
        obj["supportedInputKinds"] = new JsonArray(
            (JsonNode?)"click",
            (JsonNode?)"double_click",
            (JsonNode?)"right_click",
            (JsonNode?)"key_press",
            (JsonNode?)"key_down",
            (JsonNode?)"key_up",
            (JsonNode?)"type_text",
            (JsonNode?)"mouse_move");

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
