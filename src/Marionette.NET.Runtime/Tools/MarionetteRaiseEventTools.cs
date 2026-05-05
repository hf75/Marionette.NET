// Marionette.NET — raise_event MCP tool, hosted in its own [McpServerToolType]
// so MarionetteHost.RunAsyncSourceGenSafe can omit it.
//
// Why split: the underlying IUiAutomationAdapter.RaiseEventAsync resolves
// framework event names (`"Click"`, `"MouseDown"`, …) by reflection on the
// user control's CLR type chain. That reflection is the only architecturally
// non-source-gen-able piece of the runtime — adopters who AOT-publish and do
// NOT use raise_event should be able to opt out of the warning surface.
//
// Static methods on a [McpServerToolType] class are picked up by the SDK's
// build-time source generator. WithTools<T>() registers the tools statically
// — there is no runtime reflection on this type. Phase 11: MarionetteHost's
// RunAsync registers both MarionetteTools and MarionetteRaiseEventTools;
// RunAsyncSourceGenSafe registers only MarionetteTools and is therefore
// annotation-free.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Marionette.Runtime.Adapters;
using Marionette.Runtime.Loop;
using Marionette.Runtime.Manifest;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace Marionette.Runtime.Tools;

/// <summary>
/// Phase 11: the <c>raise_event</c> MCP tool, hosted on its own
/// <see cref="McpServerToolTypeAttribute"/>-decorated class so adopters who
/// avoid framework event-name reflection can opt out of registration entirely
/// via <c>MarionetteHost.RunAsyncSourceGenSafe</c>.
/// </summary>
[McpServerToolType]
public sealed class MarionetteRaiseEventTools
{
    private MarionetteRaiseEventTools() { }

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
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "The underlying IUiAutomationAdapter.RaiseEventAsync is marked " +
                        "RequiresUnreferencedCode; MarionetteHost.RunAsync's annotation also " +
                        "carries the warning so adopters see it at the boundary they own. " +
                        "Phase-11 RunAsyncSourceGenSafe excludes this entire tool type from " +
                        "registration so adopters who avoid raise_event get no warning.")]
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
            argMap = MarionetteTools.MaterialiseArgsForRaiseEvent(args);
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

    private static JsonObject MakeError(string code, string message) => new()
    {
        ["success"] = false,
        ["errorCode"] = code,
        ["message"] = message,
    };
}
