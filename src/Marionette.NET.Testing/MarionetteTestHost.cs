using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Marionette.Runtime.Adapters;
using Marionette.Runtime.Loop;
using Marionette.Runtime.Manifest;
using Marionette.Runtime.Tools;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marionette.Testing;

/// <summary>
/// In-process test harness for Marionette-enabled applications.
/// </summary>
/// <remarks>
/// The host consumes the same source-generator manifest that production apps
/// pass to <c>MarionetteHost.RunAsync</c>, but it calls the runtime tools
/// directly instead of launching a stdio MCP process.
/// </remarks>
public sealed class MarionetteTestHost
{
    private readonly ManifestRegistry _registry;
    private readonly IUiAutomationAdapter _adapter;
    private readonly LoopProtectionService _loopProtection;
    private readonly ILogger<MarionetteHostMarker> _toolLogger;

    private MarionetteTestHost(
        IReadOnlyList<RootDescriptor> roots,
        MarionetteTestHostOptions? options)
    {
        if (roots is null) throw new ArgumentNullException(nameof(roots));

        options ??= new MarionetteTestHostOptions();
        _registry = new ManifestRegistry(roots);
        _adapter = options.Adapter ?? new NoOpAdapter();
        _loopProtection = options.LoopProtection ?? new LoopProtectionService();
        var loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        _toolLogger = loggerFactory.CreateLogger<MarionetteHostMarker>();
    }

    /// <summary>
    /// Create a host over a source-generated manifest.
    /// </summary>
    public static MarionetteTestHost Create(
        IReadOnlyList<RootDescriptor> roots,
        MarionetteTestHostOptions? options = null)
        => new(roots, options);

    /// <summary>
    /// The live runtime registry used by the test host.
    /// </summary>
    public ManifestRegistry Registry => _registry;

    /// <summary>
    /// The adapter used for dispatching and optional UI interactions.
    /// Defaults to <see cref="NoOpAdapter"/>.
    /// </summary>
    public IUiAutomationAdapter Adapter => _adapter;

    /// <summary>
    /// The loop-protection service used by calls through this host.
    /// </summary>
    public LoopProtectionService LoopProtection => _loopProtection;

    /// <summary>
    /// Replace the registry instance for an existing root.
    /// </summary>
    public void BindInstance(string rootName, object instance)
        => _registry.BindInstance(rootName, instance);

    /// <summary>
    /// Reset the loop counter between test scenarios.
    /// </summary>
    public void ResetLoopProtection() => _loopProtection.Reset();

    /// <summary>
    /// Return the same JSON manifest as the <c>inspect_app_api</c> MCP tool.
    /// </summary>
    public string InspectAppApi(string? rootName = null, string? windowId = null)
        => MarionetteTools.InspectAppApi(_registry, _adapter, rootName, windowId);

    /// <summary>
    /// Invoke a callable and return the raw JSON result.
    /// </summary>
    public Task<string> InvokeMethodRawAsync(
        string root,
        string method,
        object? args = null,
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        var jsonArgs = MarionetteJson.ToJsonElement(args);
        return MarionetteTools.InvokeMethodAsync(
            _registry,
            _adapter,
            _loopProtection,
            _toolLogger,
            root,
            method,
            jsonArgs,
            windowId,
            cancellationToken);
    }

    /// <summary>
    /// Invoke a callable and throw <see cref="MarionetteToolException"/> on a
    /// Marionette structured error.
    /// </summary>
    public async Task InvokeMethodAsync(
        string root,
        string method,
        object? args = null,
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        var raw = await InvokeMethodRawAsync(root, method, args, windowId, cancellationToken).ConfigureAwait(false);
        MarionetteAssert.Succeeds(raw);
    }

    /// <summary>
    /// Invoke a callable, throw on structured errors, and deserialize the JSON
    /// result into <typeparamref name="T"/>.
    /// </summary>
    public async Task<T?> InvokeMethodAsync<T>(
        string root,
        string method,
        object? args = null,
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        var raw = await InvokeMethodRawAsync(root, method, args, windowId, cancellationToken).ConfigureAwait(false);
        return MarionetteAssert.Deserialize<T>(raw);
    }

    /// <summary>
    /// Read an observable and return the raw JSON result.
    /// </summary>
    public Task<string> ReadObservableRawAsync(
        string root,
        string property,
        string? windowId = null,
        CancellationToken cancellationToken = default)
        => MarionetteTools.ReadObservableAsync(
            _registry,
            _adapter,
            root,
            property,
            windowId,
            cancellationToken);

    /// <summary>
    /// Read an observable, throw on structured errors, and deserialize the JSON
    /// value into <typeparamref name="T"/>.
    /// </summary>
    public async Task<T?> ReadObservableAsync<T>(
        string root,
        string property,
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        var raw = await ReadObservableRawAsync(root, property, windowId, cancellationToken).ConfigureAwait(false);
        return MarionetteAssert.Deserialize<T>(raw);
    }

    /// <summary>
    /// Drive the runtime's <c>simulate_input</c> tool and return raw JSON.
    /// </summary>
    public Task<string> SimulateInputRawAsync(
        string root,
        string control,
        string kind,
        object? args = null,
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        var jsonArgs = MarionetteJson.ToJsonElement(args);
        return MarionetteTools.SimulateInputAsync(
            _registry,
            _adapter,
            _loopProtection,
            _toolLogger,
            root,
            control,
            kind,
            jsonArgs,
            windowId,
            cancellationToken);
    }

    /// <summary>
    /// Drive the runtime's <c>raise_event</c> tool and return raw JSON.
    /// </summary>
    public Task<string> RaiseEventRawAsync(
        string root,
        string control,
        string eventName,
        object? args = null,
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        var jsonArgs = MarionetteJson.ToJsonElement(args);
        // Phase 11: raise_event was extracted into its own
        // [McpServerToolType] so RunAsyncSourceGenSafe can omit it. The
        // testing toolkit always exposes the full surface.
        return MarionetteRaiseEventTools.RaiseEventAsync(
            _registry,
            _adapter,
            _loopProtection,
            _toolLogger,
            root,
            control,
            eventName,
            jsonArgs,
            windowId,
            cancellationToken);
    }
}

/// <summary>
/// Optional dependencies for <see cref="MarionetteTestHost"/>.
/// </summary>
public sealed class MarionetteTestHostOptions
{
    /// <summary>
    /// UI adapter used for dispatching. Defaults to <see cref="NoOpAdapter"/>.
    /// </summary>
    public IUiAutomationAdapter? Adapter { get; init; }

    /// <summary>
    /// Loop-protection service. Defaults to a fresh service using runtime
    /// defaults and environment overrides.
    /// </summary>
    public LoopProtectionService? LoopProtection { get; init; }

    /// <summary>
    /// Logger factory for tool diagnostics. Defaults to
    /// <see cref="NullLoggerFactory"/>.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}

internal static class MarionetteJson
{
    internal static readonly JsonSerializerOptions Options = new();

    internal static JsonElement? ToJsonElement(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement el) return el.Clone();

        using var doc = JsonSerializer.SerializeToDocument(value, Options);
        return doc.RootElement.Clone();
    }
}
