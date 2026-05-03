// Marionette.NET — watchable observable resource provider
//
// Implements MASTERPLAN Spielregel 8 (watchable resources):
//
//   Any [McpObservable(Watchable=true)] becomes an MCP resource at
//     marionette://<rootName>/<propertyName>
//
//   * resources/list returns the union of all watchable observables.
//   * resources/read fetches the current value (dispatched to the UI thread
//     via IUiAutomationAdapter; the value is JSON-serialised and returned as
//     the resource's text content).
//   * resources/subscribe starts watching:
//       - If the root instance implements INotifyPropertyChanged, the
//         provider hooks PropertyChanged and pushes
//         notifications/resources/updated whenever the matching name fires.
//       - Otherwise it polls at PollingIntervalMs (default 500 ms).
//     Updates within a 200 ms coalesce window per resource collapse to a
//     single notification.
//   * resources/unsubscribe stops the corresponding watcher.
//
// Lifetime: singleton, registered into DI. The ChannelEmitter pattern of
// Bind(McpServer) is mirrored here so the provider can push updates.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Marionette.Runtime.Adapters;
using Marionette.Runtime.Manifest;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Marionette.Runtime.Resources;

/// <summary>
/// Hosts the runtime watchable-resource catalog plus the per-subscription
/// watcher state. Built once at startup from the
/// <see cref="ManifestRegistry"/>; the MCP host registers handler delegates
/// against the <c>resources/list</c>, <c>resources/read</c>,
/// <c>resources/subscribe</c>, and <c>resources/unsubscribe</c> methods that
/// forward into this provider.
/// </summary>
public sealed class WatchableResourceProvider : IAsyncDisposable
{
    private const string UriScheme = "marionette";
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(200);

    private readonly ManifestRegistry _registry;
    private readonly IUiAutomationAdapter _adapter;
    private readonly ILogger<WatchableResourceProvider> _logger;

    /// <summary>All watchable observables, keyed by URI.</summary>
    private readonly Dictionary<string, WatchableEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>Active subscriptions, keyed by URI. Each holds the watcher
    /// state (PropertyChanged hook OR polling timer) plus the last-pushed
    /// value for change detection.</summary>
    private readonly ConcurrentDictionary<string, Subscription> _subs = new(StringComparer.Ordinal);

    private McpServer? _server;
    private CancellationTokenSource _shutdownCts = new();

    public WatchableResourceProvider(
        ManifestRegistry registry,
        IUiAutomationAdapter adapter,
        ILogger<WatchableResourceProvider> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        foreach (var root in registry.Roots)
        {
            foreach (var obs in root.Descriptor.Observables)
            {
                if (!obs.Watchable) continue;
                var uri = $"{UriScheme}://{root.Descriptor.Name}/{obs.Name}";
                _entries[uri] = new WatchableEntry(uri, root, obs);
            }
        }
    }

    /// <summary>
    /// Bind to a live MCP server session. Used by <see cref="PushUpdatedAsync"/>
    /// to send <c>notifications/resources/updated</c> to the client.
    /// </summary>
    public void Bind(McpServer server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    // -------------------------------------------------------------------------
    // resources/list
    // -------------------------------------------------------------------------

    /// <summary>
    /// Build the protocol-level <see cref="ListResourcesResult"/> for the
    /// <c>resources/list</c> handler.
    /// </summary>
    public ListResourcesResult List()
    {
        var resources = _entries.Values
            .Select(e => new Resource
            {
                Uri = e.Uri,
                Name = $"{e.Root.Descriptor.Name}.{e.Observable.Name}",
                Description = e.Observable.Description,
                MimeType = "application/json",
            })
            .ToList();
        return new ListResourcesResult { Resources = resources };
    }

    // -------------------------------------------------------------------------
    // resources/read
    // -------------------------------------------------------------------------

    /// <summary>
    /// Read the current value of a watchable resource. The value is dispatched
    /// to the UI thread (most observables read UI state) and JSON-serialised.
    /// </summary>
    public async Task<ReadResourceResult> ReadAsync(string uri, CancellationToken ct)
    {
        if (!_entries.TryGetValue(uri, out var entry))
        {
            // Unknown URI — surface a structured error rather than throwing.
            return ErrorResult(uri, "unknown_resource",
                $"No watchable observable matches '{uri}'.");
        }

        if (entry.Root.Instance is null)
        {
            return ErrorResult(uri, "root_unavailable",
                $"Root '{entry.Root.Descriptor.Name}' has no live instance: {entry.Root.CreateError ?? "no factory"}.");
        }

        try
        {
            var value = await _adapter.DispatchAsync(
                () => entry.Observable.Read(entry.Root.Instance!),
                ct).ConfigureAwait(false);

            var text = JsonSerializer.Serialize(value, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            return new ReadResourceResult
            {
                Contents = new List<ResourceContents>
                {
                    new TextResourceContents
                    {
                        Uri = uri,
                        MimeType = "application/json",
                        Text = text,
                    },
                },
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resource read failed for {Uri}", uri);
            return ErrorResult(uri, "read_failed", ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    // resources/subscribe / unsubscribe
    // -------------------------------------------------------------------------

    /// <summary>
    /// Start watching a resource. Returns <see langword="true"/> if a watcher
    /// was newly started; <see langword="false"/> if the URI was unknown or
    /// the subscription already existed.
    /// </summary>
    public bool Subscribe(string uri)
    {
        if (!_entries.TryGetValue(uri, out var entry)) return false;
        if (entry.Root.Instance is null) return false;

        var sub = _subs.GetOrAdd(uri, _ => new Subscription(entry));
        if (sub.Started) return false;

        sub.Started = true;

        // Prefer INotifyPropertyChanged; fall back to polling.
        if (entry.Root.Instance is INotifyPropertyChanged inpc)
        {
            sub.InpcHandler = (s, e) =>
            {
                if (e.PropertyName is null || e.PropertyName == entry.Observable.Name)
                {
                    _ = MaybePushUpdatedAsync(sub, _shutdownCts.Token);
                }
            };
            inpc.PropertyChanged += sub.InpcHandler;
        }
        else
        {
            // Polling — capture initial value then check on each tick.
            var period = TimeSpan.FromMilliseconds(Math.Max(50, entry.Observable.PollingIntervalMs));
            sub.Timer = new Timer(_ =>
            {
                _ = MaybePushUpdatedAsync(sub, _shutdownCts.Token);
            }, state: null, dueTime: period, period: period);
        }

        // Capture an initial baseline so the first push fires only on actual
        // change, not on subscription itself.
        try
        {
            sub.LastValueJson = ReadValueJsonInline(entry);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Initial read for {Uri} failed; will retry on change.", uri);
        }
        return true;
    }

    /// <summary>
    /// Stop watching a resource. Returns <see langword="true"/> if a watcher
    /// existed and was removed; <see langword="false"/> otherwise.
    /// </summary>
    public bool Unsubscribe(string uri)
    {
        if (!_subs.TryRemove(uri, out var sub)) return false;
        sub.Dispose();
        return true;
    }

    // -------------------------------------------------------------------------
    // Internal: change detection + 200 ms coalesce + push
    // -------------------------------------------------------------------------

    private async Task MaybePushUpdatedAsync(Subscription sub, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        // Coalesce: if a push already fired within CoalesceWindow, schedule
        // a single delayed re-check and bail. The re-check captures the most
        // recent value (whatever it ends up being) so rapid bursts collapse
        // to one notification.
        var now = DateTime.UtcNow;
        if (now - sub.LastPushUtc < CoalesceWindow)
        {
            if (Interlocked.CompareExchange(ref sub.CoalesceScheduled, 1, 0) == 0)
            {
                _ = Task.Delay(CoalesceWindow, ct).ContinueWith(_ =>
                {
                    Interlocked.Exchange(ref sub.CoalesceScheduled, 0);
                    _ = MaybePushUpdatedAsync(sub, ct);
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
            return;
        }

        try
        {
            var current = await _adapter.DispatchAsync(
                () => JsonSerializer.Serialize(
                    sub.Entry.Observable.Read(sub.Entry.Root.Instance!),
                    ModelContextProtocol.McpJsonUtilities.DefaultOptions),
                ct).ConfigureAwait(false);

            // Skip identical values — STJ-stable byte equality.
            if (string.Equals(current, sub.LastValueJson, StringComparison.Ordinal)) return;
            sub.LastValueJson = current;
            sub.LastPushUtc = now;

            await PushUpdatedAsync(sub.Entry.Uri, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — expected.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watcher push failed for {Uri}", sub.Entry.Uri);
        }
    }

    private string ReadValueJsonInline(WatchableEntry entry)
    {
        // Inline (no dispatch) — used for the initial-baseline read inside
        // Subscribe. The caller is the SDK's request thread; if the user
        // installed the WPF adapter, dispatching from here would deadlock
        // when the request happens on the UI thread already.
        return JsonSerializer.Serialize(
            entry.Observable.Read(entry.Root.Instance!),
            ModelContextProtocol.McpJsonUtilities.DefaultOptions);
    }

    private async Task PushUpdatedAsync(string uri, CancellationToken ct)
    {
        var srv = _server;
        if (srv is null) return;
        var payload = new ResourceUpdatedNotificationParams { Uri = uri };
        try
        {
            await srv.SendNotificationAsync(
                method: NotificationMethods.ResourceUpdatedNotification,
                parameters: payload,
                serializerOptions: ModelContextProtocol.McpJsonUtilities.DefaultOptions,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "resources/updated push failed for {Uri}", uri);
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

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        foreach (var (_, sub) in _subs) sub.Dispose();
        _subs.Clear();
        _shutdownCts.Dispose();
        _shutdownCts = new CancellationTokenSource();
        return ValueTask.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Internal data model
    // -------------------------------------------------------------------------

    private sealed record WatchableEntry(string Uri, RegisteredRoot Root, ObservableDescriptor Observable);

    private sealed class Subscription : IDisposable
    {
        public Subscription(WatchableEntry entry) { Entry = entry; }

        public WatchableEntry Entry { get; }
        public bool Started { get; set; }
        public string? LastValueJson { get; set; }
        public DateTime LastPushUtc { get; set; } = DateTime.MinValue;
        public Timer? Timer { get; set; }
        public PropertyChangedEventHandler? InpcHandler { get; set; }
        public int CoalesceScheduled;

        public void Dispose()
        {
            try { Timer?.Dispose(); } catch { }
            try
            {
                if (InpcHandler is not null && Entry.Root.Instance is INotifyPropertyChanged inpc)
                {
                    inpc.PropertyChanged -= InpcHandler;
                }
            }
            catch { }
        }
    }
}
