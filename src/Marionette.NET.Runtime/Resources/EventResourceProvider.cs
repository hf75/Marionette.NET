// Marionette.NET — event resource provider (Phase 1.6)
//
// Sibling of WatchableResourceProvider — exposes every [McpEvent] as an MCP
// resource at marionette://<rootName>/events/<eventName>:
//
//   * resources/list contributes one entry per [McpEvent]. Description is
//     copied from the EventDescriptor.
//   * resources/read returns the current EventLogSnapshot serialized as
//     {"sequence":N, "dropped":M, "events":[{sequence, ts, args}, ...]}.
//   * resources/subscribe wires the EventLogService's coalesced subscription
//     to push notifications/resources/updated whenever a fire is logged.
//
// MarionetteHost composes this provider with the WatchableResourceProvider —
// the host's resource handlers iterate both providers' lists, pick the one
// whose URI matches on read/subscribe.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Marionette.Runtime.Events;
using Marionette.Runtime.Manifest;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Marionette.Runtime.Resources;

/// <summary>
/// MCP resource provider for <c>[McpEvent]</c> events. Singleton in DI;
/// composed alongside <see cref="WatchableResourceProvider"/> by
/// <see cref="MarionetteHost"/>.
/// </summary>
public sealed class EventResourceProvider : IAsyncDisposable
{
    public const string UriPrefix = "marionette://";
    public const string EventsSegment = "/events/";

    private readonly EventLogService _eventLog;
    private readonly ILogger<EventResourceProvider> _logger;

    private readonly Dictionary<string, EventEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IDisposable> _subs = new(StringComparer.Ordinal);
    private McpServer? _server;
    private CancellationTokenSource _shutdownCts = new();

    public EventResourceProvider(
        EventLogService eventLog,
        ILogger<EventResourceProvider> logger)
    {
        _eventLog = eventLog ?? throw new ArgumentNullException(nameof(eventLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        foreach (var (root, ev, descriptor) in _eventLog.Entries())
        {
            var uri = $"{UriPrefix}{root}{EventsSegment}{ev}";
            _entries[uri] = new EventEntry(uri, root, ev, descriptor);
        }
    }

    public void Bind(McpServer server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    /// <summary>Return every event resource. Caller merges with the
    /// observable provider's list.</summary>
    public IReadOnlyList<Resource> List()
    {
        return _entries.Values
            .Select(e => new Resource
            {
                Uri = e.Uri,
                Name = $"{e.RootName}.events.{e.EventName}",
                Description = e.Descriptor.Description,
                MimeType = "application/json",
            })
            .ToList();
    }

    /// <summary>
    /// True when the URI matches one of our registered event resources.
    /// </summary>
    public bool TryHandle(string uri) => _entries.ContainsKey(uri);

    /// <summary>
    /// resources/read implementation for an event URI. Returns a snapshot of
    /// the ring buffer plus the current monotonic sequence head.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 4.2: forwards into SerializeArgsToNode which has the same suppression. " +
                        "The cascading warning surfaces at MarionetteHost.RunAsync.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Phase 4.2: same reasoning.")]
    public Task<ReadResourceResult> ReadAsync(string uri, CancellationToken ct)
    {
        if (!_entries.TryGetValue(uri, out var entry))
        {
            return Task.FromResult(ErrorResult(uri, "unknown_resource",
                $"No event resource matches '{uri}'."));
        }

        var snapshot = _eventLog.GetSnapshot(entry.RootName, entry.EventName);
        if (snapshot is null)
        {
            return Task.FromResult(ErrorResult(uri, "event_unavailable",
                $"Event '{entry.RootName}.{entry.EventName}' is registered but has no snapshot — root may be unavailable."));
        }

        // Build a compact JSON payload. Args are passed straight through STJ;
        // serializers handle most user-defined records and primitives via the
        // default options.
        var eventsArr = new JsonArray();
        foreach (var rec in snapshot.Events)
        {
            var argsNode = SerializeArgsToNode(rec.Args);
            eventsArr.Add(new JsonObject
            {
                ["sequence"] = rec.Sequence,
                ["timestampUtc"] = rec.TimestampUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                ["args"] = argsNode,
            });
        }

        var payload = new JsonObject
        {
            ["sequence"] = snapshot.Sequence,
            ["dropped"] = snapshot.Dropped,
            ["events"] = eventsArr,
        };

        return Task.FromResult(new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents
                {
                    Uri = uri,
                    MimeType = "application/json",
                    Text = payload.ToJsonString(),
                },
            },
        });
    }

    /// <summary>
    /// resources/subscribe — start delivering coalesced
    /// <c>notifications/resources/updated</c> for the event URI.
    /// </summary>
    public bool Subscribe(string uri)
    {
        if (!_entries.TryGetValue(uri, out var entry)) return false;
        if (_subs.ContainsKey(uri)) return false;

        var sub = _eventLog.Subscribe(entry.RootName, entry.EventName, () =>
        {
            _ = PushUpdatedAsync(uri, _shutdownCts.Token);
        });
        return _subs.TryAdd(uri, sub);
    }

    public bool Unsubscribe(string uri)
    {
        if (!_subs.TryRemove(uri, out var sub)) return false;
        try { sub.Dispose(); } catch { /* shutdown path */ }
        return true;
    }

    private async Task PushUpdatedAsync(string uri, CancellationToken ct)
    {
        var srv = _server;
        if (srv is null) return;
        try
        {
            await srv.SendNotificationAsync(
                method: NotificationMethods.ResourceUpdatedNotification,
                parameters: new ResourceUpdatedNotificationParams { Uri = uri },
                serializerOptions: ModelContextProtocol.McpJsonUtilities.DefaultOptions,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "events/updated push failed for {Uri}", uri);
        }
    }

    private static readonly JsonSerializerOptions ArgsSerializerOptions = new()
    {
        // Plain options: no PropertyNamingPolicy, no IncludeFields. We want
        // the args type's public properties to surface verbatim (PascalCase),
        // matching the JSON schema the source generator emits. The MCP
        // default options apply camelCase, which would diverge from the
        // schema and confuse adopters.
    };

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Phase 4.2: SerializeArgsToNode runs STJ over user [McpEvent]Args types. " +
                        "The cascading warning surfaces at MarionetteHost.RunAsync's RequiresUnreferencedCode. " +
                        "Adopters who AOT-publish should keep [McpEvent]Args public properties on " +
                        "JSON-primitive shapes (records of int/string/DateTime/etc.) so trimming " +
                        "preserves their getters.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Phase 4.2: same reasoning — JsonSerializer may JIT-generate code at " +
                        "runtime for unknown user types. Phase 6 may move to source-generated " +
                        "JsonTypeInfo per descriptor.")]
    private static JsonNode? SerializeArgsToNode(object? args)
    {
        if (args is null) return null;
        try
        {
            // Run through STJ to get a JSON tree we can stitch into the
            // outer payload. ArgsSerializerOptions intentionally preserves
            // PascalCase to match the source-generator-emitted schema.
            var json = JsonSerializer.Serialize(args, args.GetType(), ArgsSerializerOptions);
            return JsonNode.Parse(json);
        }
        catch (Exception)
        {
            // Fallback to a minimal stringified description so a single
            // misbehaving args type does not poison the snapshot.
            return JsonValue.Create(args.ToString());
        }
    }

    private static ReadResourceResult ErrorResult(string uri, string code, string message)
    {
        var payload = new JsonObject
        {
            ["success"] = false,
            ["errorCode"] = code,
            ["message"] = message,
        };
        return new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents
                {
                    Uri = uri,
                    MimeType = "application/json",
                    Text = payload.ToJsonString(),
                },
            },
        };
    }

    public ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        foreach (var (_, sub) in _subs)
        {
            try { sub.Dispose(); } catch { /* shutdown path */ }
        }
        _subs.Clear();
        _shutdownCts.Dispose();
        _shutdownCts = new CancellationTokenSource();
        return ValueTask.CompletedTask;
    }

    private sealed record EventEntry(string Uri, string RootName, string EventName, EventDescriptor Descriptor);
}
