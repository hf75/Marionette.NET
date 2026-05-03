// Marionette.NET — channel-push emitter
//
// Bridges `Marionette.Ai.Trigger` / `Marionette.Ai.ScheduleTrigger` (defined in
// Abstractions) onto the live MCP server session as JSON-RPC notifications.
//
// Wire format (per MASTERPLAN "Channel-Push" / Spielregel 7):
//   method: "notifications/marionette/channel"
//   params: {
//     "prompt":       "User clicked Add; result is 5.",
//     "hops":         3,                    // current loop-protection counter
//     "scheduledFor": "2026-05-03T12:34:56Z"  // only present for ScheduleTrigger
//   }
//
// Loop-protection: every Trigger / ScheduleTrigger increments the
// LoopProtectionService counter via RecordChannelHop. The counter value goes
// into the "hops" field so the LLM can see it.
//
// Lifetime: registered as a singleton hosted-service-style. On startup the
// emitter installs hooks on `Marionette.Ai`; on disposal it un-installs them
// (so a host re-creation doesn't double-up).
// `Marionette.Ai.TriggerHook` is internal and only writable from the runtime
// via InternalsVisibleTo (see Abstractions csproj).

using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Marionette.Runtime.Loop;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace Marionette.Runtime.Channel;

/// <summary>
/// Forwards <see cref="Ai.Trigger"/> / <see cref="Ai.ScheduleTrigger"/> calls
/// from user code to the connected MCP client as JSON-RPC notifications.
/// </summary>
public sealed class ChannelEmitter : IAsyncDisposable
{
    private readonly LoopProtectionService _loopGuard;
    private readonly ILogger<ChannelEmitter> _logger;
    private readonly ConcurrentBag<Timer> _scheduledTimers = new();
    private readonly CancellationTokenSource _shutdownCts = new();

    private McpServer? _server;
    private bool _hooksInstalled;

    public ChannelEmitter(LoopProtectionService loopGuard, ILogger<ChannelEmitter> logger)
    {
        _loopGuard = loopGuard ?? throw new ArgumentNullException(nameof(loopGuard));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Bind the emitter to a live MCP server session and install the
    /// <see cref="Ai"/> hooks. Called from the host once the server has been
    /// constructed but before the run loop starts.
    /// </summary>
    /// <param name="server">The MCP server instance to push notifications through.</param>
    public void Bind(McpServer server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        if (_hooksInstalled) return;

        // Install hooks via InternalsVisibleTo from Abstractions (see
        // src/Marionette.NET.Abstractions/Marionette.NET.Abstractions.csproj).
        Ai.TriggerHook = OnTrigger;
        Ai.ScheduleTriggerHook = OnScheduleTrigger;
        _hooksInstalled = true;
    }

    private void OnTrigger(string prompt)
    {
        if (_server is null) return;
        if (string.IsNullOrEmpty(prompt)) return;

        var hops = _loopGuard.RecordChannelHop();
        var payload = BuildPayload(prompt, hops, scheduledFor: null);
        // Fire and forget: Ai.Trigger is a void method. We swallow async errors
        // and log them to stderr (the SDK's stdio transport reserves stdout).
        _ = SendAsync(payload, _shutdownCts.Token);
    }

    private void OnScheduleTrigger(TimeSpan delay, string prompt)
    {
        if (_server is null) return;
        if (string.IsNullOrEmpty(prompt)) return;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        // Fire-once timer; we keep a reference so it doesn't get GC'd. On
        // shutdown the CancellationTokenSource cancels the eventual SendAsync.
        var fireAtUtc = DateTime.UtcNow + delay;
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            try
            {
                var hops = _loopGuard.RecordChannelHop();
                var payload = BuildPayload(prompt, hops, scheduledFor: fireAtUtc);
                _ = SendAsync(payload, _shutdownCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deliver scheduled channel push.");
            }
            finally
            {
                try { timer?.Dispose(); } catch { /* ignore */ }
            }
        }, state: null, dueTime: delay, period: Timeout.InfiniteTimeSpan);
        _scheduledTimers.Add(timer);
    }

    private async Task SendAsync(JsonObject payload, CancellationToken ct)
    {
        var srv = _server;
        if (srv is null) return;
        try
        {
            await srv.SendNotificationAsync(
                method: "notifications/marionette/channel",
                parameters: payload,
                serializerOptions: ModelContextProtocol.McpJsonUtilities.DefaultOptions,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutting down — expected.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Channel-push send failed (prompt suppressed): {Hops}", payload["hops"]);
        }
    }

    private static JsonObject BuildPayload(string prompt, int hops, DateTime? scheduledFor)
    {
        var obj = new JsonObject
        {
            ["prompt"] = prompt,
            ["hops"] = hops,
        };
        if (scheduledFor.HasValue)
        {
            // ISO-8601 UTC. JsonValue.Create handles the round-trip.
            obj["scheduledFor"] = scheduledFor.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        }
        return obj;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        if (_hooksInstalled)
        {
            Ai.TriggerHook = null;
            Ai.ScheduleTriggerHook = null;
            _hooksInstalled = false;
        }
        foreach (var t in _scheduledTimers)
        {
            try { t.Dispose(); } catch { /* ignore */ }
        }
        _shutdownCts.Dispose();
        return ValueTask.CompletedTask;
    }
}
