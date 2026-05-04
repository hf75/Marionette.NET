// Marionette.NET — EventLogService (Phase 1.6)
//
// Per-event ring buffer + notification coalescing for [McpEvent]-decorated
// events. Singleton in DI. Hooks into every event on every root at Start();
// detaches at Stop() so root instances are not GC-rooted by the runtime.
//
// Per-event state (one EventLogEntry per arrival):
//   * Sequence (monotonic, scoped to (root, event))
//   * Timestamp (UtcNow at receipt)
//   * Args (the EventArgs instance fired by user code; pass-through to STJ)
//
// Throttle: when MinIntervalMs > 0, fires arriving faster than that interval
// are dropped. A per-event drop counter is exposed in the snapshot payload.
//
// Coalesce: on each accepted fire, schedule a Task.Delay(CoalesceWindowMs).
// If another fire happens before the delay elapses, mark the bucket dirty
// and let the existing timer fire one notification. Adopters can rely on the
// buffer for completeness; the notification is throttled cadence-wise.
//
// Concurrency: events fire from arbitrary threads. The ring buffer is
// guarded by a lock; the scheduler uses Interlocked.CompareExchange to make
// scheduling cheap from the hot path.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Marionette.Runtime.Adapters;
using Marionette.Runtime.Manifest;

using Microsoft.Extensions.Logging;

namespace Marionette.Runtime.Events;

/// <summary>
/// Singleton runtime service that owns the in-memory log of every fired
/// <c>[McpEvent]</c>. The MCP host calls <see cref="Start"/> after the
/// manifest registry is built; subscribing to a per-event resource pulls
/// notifications from this service.
/// </summary>
public sealed class EventLogService : IDisposable
{
    private readonly ManifestRegistry _registry;
    private readonly IUiAutomationAdapter _adapter;
    private readonly ILogger<EventLogService> _log;
    private readonly Dictionary<string, EventBucket> _buckets = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _handlers = new();
    private bool _started;
    private bool _disposed;

    public EventLogService(ManifestRegistry registry, ILogger<EventLogService> log)
        : this(registry, new NoOpAdapter(), log)
    {
    }

    public EventLogService(
        ManifestRegistry registry,
        IUiAutomationAdapter adapter,
        ILogger<EventLogService> log)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Hook into every <c>[McpEvent]</c> on every registered root. Idempotent —
    /// repeated calls do nothing.
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        foreach (var root in _registry.Roots)
        {
            var instances = ResolveRootInstances(root);
            if (instances.Count == 0) continue;

            foreach (var ev in root.Descriptor.Events)
            {
                var key = MakeKey(root.Descriptor.Name, ev.Name);
                var bucket = new EventBucket(root.Descriptor.Name, ev.Name, ev);
                _buckets[key] = bucket;

                foreach (var instance in instances)
                {
                    try
                    {
                        var sub = ev.Subscribe(instance, args => OnFired(bucket, args));
                        _handlers.Add(sub);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex,
                            "EventLogService: Subscribe lambda for '{Root}.{Event}' threw at startup.",
                            root.Descriptor.Name, ev.Name);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Detach every event handler. Safe to call multiple times.
    /// </summary>
    public void Stop()
    {
        foreach (var h in _handlers)
        {
            try { h.Dispose(); } catch { /* shutdown path */ }
        }
        _handlers.Clear();
    }

    /// <summary>
    /// Read the current state of an (root, event) bucket. Returns a snapshot
    /// of the ring buffer plus the monotonic sequence head and drop count.
    /// Returns <see langword="null"/> when the (root, event) pair is unknown.
    /// </summary>
    public EventLogSnapshot? GetSnapshot(string rootName, string eventName)
    {
        if (!_buckets.TryGetValue(MakeKey(rootName, eventName), out var bucket)) return null;
        return bucket.Snapshot();
    }

    /// <summary>
    /// Subscribe to per-event coalesced update notifications. The
    /// <paramref name="onUpdated"/> callback is fired at most once per
    /// <c>CoalesceWindowMs</c>. Returns an <see cref="IDisposable"/> the
    /// caller disposes to unsubscribe.
    /// </summary>
    public IDisposable Subscribe(string rootName, string eventName, Action onUpdated)
    {
        if (!_buckets.TryGetValue(MakeKey(rootName, eventName), out var bucket))
        {
            // Unknown (root, event) — return a no-op disposable.
            return new NoOpDisposable();
        }
        return bucket.AddSubscriber(onUpdated);
    }

    /// <summary>
    /// Enumerate every (root, event) pair the service knows about.
    /// </summary>
    public IEnumerable<(string Root, string Event, EventDescriptor Descriptor)> Entries()
    {
        foreach (var b in _buckets.Values)
        {
            yield return (b.RootName, b.EventName, b.Descriptor);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void OnFired(EventBucket bucket, object? args)
    {
        bucket.Append(args);
    }

    private List<object> ResolveRootInstances(RegisteredRoot root)
    {
        var result = new List<object>();
        var rootName = root.Descriptor.Name;

        void AddDistinct(object? instance)
        {
            if (instance is null) return;
            foreach (var existing in result)
            {
                if (ReferenceEquals(existing, instance)) return;
            }
            result.Add(instance);
        }

        try
        {
            foreach (var windowId in _adapter.GetWindowIds(rootName))
            {
                AddDistinct(_adapter.GetRootInstance(rootName, windowId));
            }
            AddDistinct(_adapter.GetRootInstance(rootName, windowId: null));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "EventLogService: adapter instance lookup failed for root '{Root}'.", rootName);
        }

        AddDistinct(root.Instance);
        return result;
    }

    private static string MakeKey(string root, string ev) => root + "/" + ev;

    // -------------------------------------------------------------------------
    // Per-event bucket
    // -------------------------------------------------------------------------

    private sealed class EventBucket
    {
        public string RootName { get; }
        public string EventName { get; }
        public EventDescriptor Descriptor { get; }

        private readonly object _lock = new();
        private readonly Queue<EventLogRecord> _ring;
        private readonly int _capacity;
        private long _sequence;
        private long _droppedThrottled;
        private DateTime _lastAcceptedUtc = DateTime.MinValue;
        private readonly List<Action> _subscribers = new();
        private int _coalesceScheduled; // 0 = idle, 1 = scheduled

        public EventBucket(string rootName, string eventName, EventDescriptor descriptor)
        {
            RootName = rootName;
            EventName = eventName;
            Descriptor = descriptor;
            _capacity = Math.Max(1, descriptor.MaxQueueSize);
            _ring = new Queue<EventLogRecord>(_capacity);
        }

        public void Append(object? args)
        {
            // Throttle (per spec: drop, increment counter).
            var now = DateTime.UtcNow;
            if (Descriptor.MinIntervalMs > 0)
            {
                lock (_lock)
                {
                    if ((now - _lastAcceptedUtc).TotalMilliseconds < Descriptor.MinIntervalMs)
                    {
                        Interlocked.Increment(ref _droppedThrottled);
                        return;
                    }
                    _lastAcceptedUtc = now;
                }
            }

            EventLogRecord record;
            lock (_lock)
            {
                _sequence++;
                record = new EventLogRecord(_sequence, now, args);
                if (_ring.Count >= _capacity) _ring.Dequeue();
                _ring.Enqueue(record);
            }

            ScheduleNotify();
        }

        public EventLogSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new EventLogSnapshot(
                    Sequence: _sequence,
                    Dropped: Interlocked.Read(ref _droppedThrottled),
                    Events: _ring.ToArray());
            }
        }

        public IDisposable AddSubscriber(Action callback)
        {
            lock (_lock) { _subscribers.Add(callback); }
            return new SubscriberDisposable(this, callback);
        }

        public void RemoveSubscriber(Action callback)
        {
            lock (_lock) { _subscribers.Remove(callback); }
        }

        private void ScheduleNotify()
        {
            if (Interlocked.CompareExchange(ref _coalesceScheduled, 1, 0) != 0) return;
            var window = Math.Max(0, Descriptor.CoalesceWindowMs);
            // Last-write-wins coalescing: schedule a single fire after the
            // window elapses. While scheduled, additional fires append to the
            // ring buffer but do not start new timers; the timer's callback
            // fires once and the flag is reset.
            _ = Task.Run(async () =>
            {
                try
                {
                    if (window > 0) await Task.Delay(window).ConfigureAwait(false);
                    Action[] subs;
                    lock (_lock) { subs = _subscribers.ToArray(); }
                    foreach (var s in subs)
                    {
                        try { s(); }
                        catch { /* subscriber failure must not propagate */ }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _coalesceScheduled, 0);
                }
            });
        }

        private sealed class SubscriberDisposable : IDisposable
        {
            private readonly EventBucket _bucket;
            private Action? _cb;
            public SubscriberDisposable(EventBucket bucket, Action cb)
            {
                _bucket = bucket;
                _cb = cb;
            }
            public void Dispose()
            {
                var cb = Interlocked.Exchange(ref _cb, null);
                if (cb is not null) _bucket.RemoveSubscriber(cb);
            }
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// One entry in the event log ring buffer.
/// </summary>
public sealed record EventLogRecord(long Sequence, DateTime TimestampUtc, object? Args);

/// <summary>
/// Snapshot of the per-event ring buffer at a point in time.
/// </summary>
public sealed record EventLogSnapshot(long Sequence, long Dropped, IReadOnlyList<EventLogRecord> Events);
