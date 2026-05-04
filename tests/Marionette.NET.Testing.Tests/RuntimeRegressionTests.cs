using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

using Marionette.Runtime.Adapters;
using Marionette.Runtime.Events;
using Marionette.Runtime.Manifest;
using Marionette.Runtime.Resources;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Protocol;

using Xunit;

namespace Marionette.Testing.Tests;

public sealed class RuntimeRegressionTests
{
    [Fact]
    public async Task WatchableRead_UsesAdapterTrackedRootInstance()
    {
        var root = new WatchRoot();
        root.Add();

        var provider = new WatchableResourceProvider(
            new ManifestRegistry(new[] { WatchDescriptor(create: null) }),
            new TrackingAdapter("WatchRoot", root),
            NullLogger<WatchableResourceProvider>.Instance);

        var result = await provider.ReadAsync("marionette://WatchRoot/Count", CancellationToken.None);

        var content = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Equal("1", content.Text);
    }

    [Fact]
    public void EventLogStart_UsesAdapterTrackedRootInstance()
    {
        var root = new EventRoot();
        var eventLog = new EventLogService(
            new ManifestRegistry(new[] { EventRootDescriptor(create: null) }),
            new TrackingAdapter("EventRoot", root),
            NullLogger<EventLogService>.Instance);

        eventLog.Start();
        root.Fire();

        var snapshot = eventLog.GetSnapshot("EventRoot", "Happened");
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot!.Sequence);
    }

    private static RootDescriptor WatchDescriptor(Func<object>? create) =>
        new(
            Name: "WatchRoot",
            TypeName: typeof(WatchRoot).FullName ?? nameof(WatchRoot),
            Create: create,
            Callables: Array.Empty<CallableDescriptor>(),
            Observables: new[]
            {
                new ObservableDescriptor(
                    Name: "Count",
                    Description: "Count.",
                    Watchable: true,
                    PollingIntervalMs: 500,
                    ClrTypeName: "int",
                    Read: static instance => ((WatchRoot)instance).Count),
            },
            Triggerables: Array.Empty<TriggerableDescriptor>(),
            Events: Array.Empty<EventDescriptor>());

    private static RootDescriptor EventRootDescriptor(Func<object>? create) =>
        new(
            Name: "EventRoot",
            TypeName: typeof(EventRoot).FullName ?? nameof(EventRoot),
            Create: create,
            Callables: Array.Empty<CallableDescriptor>(),
            Observables: Array.Empty<ObservableDescriptor>(),
            Triggerables: Array.Empty<TriggerableDescriptor>(),
            Events: new[]
            {
                new EventDescriptor(
                    Name: "Happened",
                    Description: "Happened.",
                    ArgsTypeName: "System.EventArgs",
                    ArgsJsonSchema: "{\"type\":\"object\",\"properties\":{}}",
                    MinIntervalMs: 0,
                    MaxQueueSize: 100,
                    CoalesceWindowMs: 0,
                    Subscribe: static (instance, callback) =>
                    {
                        var root = (EventRoot)instance;
                        EventHandler handler = (_, args) => callback(args);
                        root.Happened += handler;
                        return new DelegateDisposable(() => root.Happened -= handler);
                    }),
            });

    private sealed class WatchRoot
    {
        public int Count { get; private set; }

        public void Add() => Count++;
    }

    private sealed class EventRoot
    {
        public event EventHandler? Happened;

        public void Fire() => Happened?.Invoke(this, EventArgs.Empty);
    }

    private sealed class DelegateDisposable : IDisposable
    {
        private readonly Action _dispose;

        public DelegateDisposable(Action dispose) => _dispose = dispose;

        public void Dispose() => _dispose();
    }

    private sealed class TrackingAdapter : IUiAutomationAdapter
    {
        private readonly string _rootName;
        private readonly object _instance;

        public TrackingAdapter(string rootName, object instance)
        {
            _rootName = rootName;
            _instance = instance;
        }

        public Task DispatchAsync(Action action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }

        public Task<T> DispatchAsync<T>(Func<T> func, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(func());
        }

        public Task<byte[]> CaptureScreenshotAsync(string? targetName, string? windowId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<object?> ResolveControlAsync(string rootName, string controlName, string? windowId, CancellationToken ct)
            => Task.FromResult<object?>(null);

        public Task<bool> SimulateInputAsync(
            string rootName,
            string controlName,
            string kind,
            IReadOnlyDictionary<string, object?>? args,
            string? windowId,
            CancellationToken ct)
            => Task.FromResult(false);

        [RequiresUnreferencedCode("Test adapter mirrors the runtime adapter contract.")]
        public Task<bool> RaiseEventAsync(
            string rootName,
            string controlName,
            string eventName,
            IReadOnlyDictionary<string, object?>? args,
            string? windowId,
            CancellationToken ct)
            => Task.FromResult(false);

        public IReadOnlyList<string> GetWindowIds(string rootName)
            => string.Equals(rootName, _rootName, StringComparison.Ordinal)
                ? new[] { "w1" }
                : Array.Empty<string>();

        public object? GetRootInstance(string rootName, string? windowId)
            => string.Equals(rootName, _rootName, StringComparison.Ordinal) &&
               (windowId is null || string.Equals(windowId, "w1", StringComparison.Ordinal))
                ? _instance
                : null;

#pragma warning disable CS0067
        public event EventHandler? WindowsChanged;
#pragma warning restore CS0067
    }
}
