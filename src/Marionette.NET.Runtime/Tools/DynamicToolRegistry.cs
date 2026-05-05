// Marionette.NET — Phase 2.2 dynamic per-method tool registry
//
// Registers ONE McpServerTool per discovered (root, callable). The tool's
// name is `<rootName>.<methodName>` (with overload-collision suffix when
// needed; see ToolIdentity). The handler dispatches into
// MarionetteDispatch.InvokeAsync, sharing the loop-protection / UI-thread /
// async-unwrap pipeline with the meta-tool `invoke_method`.
//
// Lifetime
//   * Singleton service.
//   * `RegisterInitial(server, ct)` is called once during host startup,
//     AFTER the McpServer instance is materialised but BEFORE the run loop
//     starts (i.e. before the first `tools/list`). This is the
//     deferred-schema-fetch insurance from Spielregel 7 — Claude Code CLI
//     fetches the tool list once on connect and won't re-fetch later, so
//     dynamic tools must exist by then.
//   * `RefreshFromManifest()` is the future hook for hot-plug roots
//     (Phase 5+). It diffs the current set against the manifest, applies
//     adds/removes/changes, and emits notifications/tools/list_changed when
//     anything changed.
//
// Idempotence (MASTERPLAN Spielregel 5 / Phase 2.2 brief): the registry
// stores `Dictionary<toolName, stableHash>`. A subsequent
// `RefreshFromManifest()` call only mutates the SDK's primitive collection
// when (name appears, name disappears, name persists with different hash).
// Description-only changes do NOT touch the cache.
//
// AOT-clean: the dispatch path goes through MarionetteDispatch which uses
// only the source-generator-emitted typed lambdas. No reflection on user
// types; one MethodInfo lookup of `RaiseChanged` on the SDK's primitive
// collection is the entire reflection surface (and it's against the SDK's
// own type, not user code).
//
// Phase 10 (2026-05-05): tool registration switched from
// `McpServerTool.Create((Delegate)handler, ...)` (which goes through
// the SDK's AIFunctionFactory.Create reflection path) to
// `McpServerTool.Create(AIFunction, ...)` with a custom AIFunction
// subclass `MarionetteAIFunction`. The SDK's AIFunction-overload only
// reads Name / Description / JsonSchema / UnderlyingMethod and invokes
// `function.InvokeAsync(args, ct)`. We supply a pre-built JSON schema
// (Phase 1.b source-generator output), set UnderlyingMethod=null so the
// SDK skips its attribute/XML-doc reflection branches, and our
// InvokeCoreAsync returns a CallToolResult directly so the existing
// IsError contract is preserved. This closes the previous Phase 4.2
// finding (per-method dynamic tools failing under AOT).

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Marionette.Runtime.Adapters;
using Marionette.Runtime.Loop;
using Marionette.Runtime.Manifest;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Marionette.Runtime.Tools;

/// <summary>
/// Per-method MCP tool registrar (Phase 2.2). Registered as a singleton in
/// <see cref="MarionetteHost"/>; its <see cref="RegisterInitial"/> is called
/// once after the <see cref="McpServer"/> is built so the dynamic tools
/// appear in the very first <c>tools/list</c> response.
/// </summary>
public sealed class DynamicToolRegistry : IDisposable
{
    private readonly ManifestRegistry _manifest;
    private readonly IUiAutomationAdapter _adapter;
    private readonly LoopProtectionService _loopGuard;
    private readonly ILogger<DynamicToolRegistry> _logger;
    private readonly Dictionary<string, string> _registered = new(StringComparer.Ordinal); // toolName → stableHash
    private readonly object _lock = new();

    // Phase 3.3 coalesce: the adapter's WindowsChanged event can fire
    // multiple times when two windows open in close succession. We
    // schedule a single refresh after a 100 ms quiet window so consumers
    // get one tools/list_changed notification, not three.
    private readonly object _refreshLock = new();
    private System.Threading.Timer? _refreshTimer;
    private static readonly TimeSpan s_refreshCoalesceDelay = TimeSpan.FromMilliseconds(100);

    private McpServer? _server;
    private bool _initialRegistrationDone;
    private bool _disposed;

    public DynamicToolRegistry(
        ManifestRegistry manifest,
        IUiAutomationAdapter adapter,
        LoopProtectionService loopGuard,
        ILogger<DynamicToolRegistry> logger)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _loopGuard = loopGuard ?? throw new ArgumentNullException(nameof(loopGuard));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Snapshot of currently-registered (toolName, stableHash) pairs, in
    /// registration order. Useful for diagnostics and for the
    /// <c>--mcp-help</c> output.
    /// </summary>
    public IReadOnlyDictionary<string, string> RegisteredTools => _registered;

    /// <summary>
    /// Number of dynamic tools currently registered.
    /// </summary>
    public int Count => _registered.Count;

    /// <summary>
    /// Bind to a live <see cref="McpServer"/> and register every tool for
    /// every known callable. Idempotent across re-binds; subsequent calls
    /// are no-ops.
    /// </summary>
    /// <param name="server">The live MCP server instance.</param>
    public void RegisterInitial(McpServer server)
    {
        if (server is null) throw new ArgumentNullException(nameof(server));
        if (_initialRegistrationDone)
        {
            _logger.LogDebug("DynamicToolRegistry.RegisterInitial called twice; ignoring.");
            return;
        }
        _server = server;

        var collection = TryGetToolCollection(server);
        if (collection is null)
        {
            _logger.LogWarning(
                "Could not access ToolCollection on McpServer.ServerOptions; per-method dynamic tools will not be available. " +
                "Meta-tools (invoke_method etc.) remain functional.");
            return;
        }

        lock (_lock)
        {
            var entries = ComputeEntries();
            foreach (var entry in entries)
            {
                try
                {
                    var tool = BuildTool(entry);
                    if (collection.TryAdd(tool))
                    {
                        _registered[entry.ToolName] = entry.StableHash;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Dynamic tool '{Tool}' could not be added (name collision). The four meta-tools or another root may already own this name.",
                            entry.ToolName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to register dynamic tool for {Root}.{Callable}",
                        entry.RootName, entry.Callable.Name);
                }
            }
            _initialRegistrationDone = true;
        }

        // Phase 3.3: subscribe to the adapter's window-set changes so
        // per-window tool variants appear/disappear as windows open/close.
        // Coalesce bursts via a short timer — see ScheduleRefresh comment.
        _adapter.WindowsChanged += OnAdapterWindowsChanged;

        // Initial registration happens BEFORE the SDK starts servicing
        // tools/list, so we deliberately do NOT raise the changed event
        // here — clients will read the dynamic tools in the very first
        // listing.
        _logger.LogInformation("Dynamic per-method tools registered: {Count}.", _registered.Count);
    }

    /// <summary>
    /// Phase 3.3: respond to the adapter's per-window mutation event by
    /// scheduling a coalesced refresh. Two-window startup (the
    /// <c>--two-windows</c> case) typically fires Changed twice within a
    /// few milliseconds; coalescing avoids two redundant
    /// <c>tools/list_changed</c> notifications. The timer reschedules each
    /// time a new event arrives, so a steady burst defers refresh until
    /// the burst ends.
    /// </summary>
    private void OnAdapterWindowsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        lock (_refreshLock)
        {
            if (_disposed) return;
            if (_refreshTimer is null)
            {
                _refreshTimer = new System.Threading.Timer(_ => RefreshTimerCallback(), state: null,
                    dueTime: s_refreshCoalesceDelay, period: System.Threading.Timeout.InfiniteTimeSpan);
            }
            else
            {
                _refreshTimer.Change(s_refreshCoalesceDelay, System.Threading.Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void RefreshTimerCallback()
    {
        // Best-effort fire-and-forget; failures are logged but don't crash
        // the timer thread.
        try
        {
            _ = RefreshFromManifestAsync(default);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DynamicToolRegistry coalesced refresh failed.");
        }
    }

    /// <summary>
    /// Re-compute the set of dynamic tools from the (possibly updated)
    /// manifest, apply add / remove / change diffs to the SDK's primitive
    /// collection, and emit <c>notifications/tools/list_changed</c> when
    /// anything actually changed. Phase 2.2's initial registration goes
    /// through <see cref="RegisterInitial"/>; this method is the future
    /// hot-plug entry point (Phase 5+).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when at least one tool was added, removed, or
    /// re-registered (and a list-changed notification was emitted).
    /// </returns>
    public async Task<bool> RefreshFromManifestAsync(CancellationToken cancellationToken = default)
    {
        if (_server is null)
        {
            throw new InvalidOperationException(
                "DynamicToolRegistry.RefreshFromManifestAsync called before RegisterInitial. The server must be bound first.");
        }
        var collection = TryGetToolCollection(_server);
        if (collection is null) return false;

        bool dirty;
        lock (_lock)
        {
            var entries = ComputeEntries();
            var newSet = new Dictionary<string, (CallableEntry Entry, string Hash)>(StringComparer.Ordinal);
            foreach (var e in entries) newSet[e.ToolName] = (e, e.StableHash);

            dirty = false;

            // Removes: anything in _registered not in newSet.
            var toRemove = new List<string>();
            foreach (var (name, _) in _registered)
            {
                if (!newSet.ContainsKey(name)) toRemove.Add(name);
            }
            foreach (var name in toRemove)
            {
                if (collection.TryGetPrimitive(name, out var existing) && existing is not null)
                {
                    if (collection.Remove(existing)) dirty = true;
                }
                _registered.Remove(name);
            }

            // Adds + changes
            foreach (var (name, payload) in newSet)
            {
                if (_registered.TryGetValue(name, out var existingHash))
                {
                    if (string.Equals(existingHash, payload.Hash, StringComparison.Ordinal))
                    {
                        // Unchanged — idempotent path. Skip.
                        continue;
                    }
                    // Changed — remove + re-add.
                    if (collection.TryGetPrimitive(name, out var stale) && stale is not null)
                    {
                        collection.Remove(stale);
                    }
                }

                try
                {
                    var tool = BuildTool(payload.Entry);
                    if (collection.TryAdd(tool))
                    {
                        _registered[name] = payload.Hash;
                        dirty = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to refresh dynamic tool {Tool}.", name);
                }
            }
        }

        if (dirty)
        {
            // The SDK's primitive collection raises Changed automatically on
            // mutation — but to keep the contract explicit and not depend on
            // the SDK's internal hookup, we ALSO send the standard
            // notifications/tools/list_changed manually. Duplicate
            // notifications are harmless: clients refresh idempotently.
            try
            {
                await _server.SendNotificationAsync(
                    method: NotificationMethods.ToolListChangedNotification,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send tools/list_changed notification.");
            }
        }
        return dirty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _adapter.WindowsChanged -= OnAdapterWindowsChanged; } catch { /* shutdown */ }
        lock (_refreshLock)
        {
            try { _refreshTimer?.Dispose(); } catch { /* shutdown */ }
            _refreshTimer = null;
        }
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private List<CallableEntry> ComputeEntries()
    {
        // Phase 3.3 multi-window expansion: each (root, callable) yields:
        //   * Always the bare-form `<RootName>.<MethodName>` variant
        //     (windowId=null → adapter routes to oldest window).
        //   * When the adapter reports 2+ live windowIds for the root,
        //     ALSO emit one per-window variant `<RootName>.<MethodName>:<wId>`
        //     so the LLM can address a specific window.
        // The hash for per-window variants includes the windowId so the
        // ToolIdentity becomes unique per (signature, window).
        var bases = new List<(string BaseName, string Hash, string RootName, CallableDescriptor Callable, string? WindowId)>();
        foreach (var root in _manifest.Roots)
        {
            var rootName = root.Descriptor.Name;
            var liveWindowIds = _adapter.GetWindowIds(rootName);
            var multiWindow = liveWindowIds is { Count: > 1 };

            foreach (var callable in root.Descriptor.Callables)
            {
                var baseName = ToolIdentity.ComputeToolName(rootName, callable);
                var bareHash = ToolIdentity.ComputeStableHash(rootName, callable);

                // Bare form — always present. Default-window routing.
                bases.Add((baseName, bareHash, rootName, callable, WindowId: null));

                if (multiWindow)
                {
                    foreach (var wId in liveWindowIds!)
                    {
                        var perWindowName = baseName + ":" + wId;
                        // Mix windowId into the hash so per-window variants
                        // get distinct stable identities — they're routed to
                        // different runtime instances even though the
                        // signature is identical.
                        var combinedHash = ToolIdentity.ComputeStableHash(rootName + "@" + wId, callable);
                        bases.Add((perWindowName, combinedHash, rootName, callable, WindowId: wId));
                    }
                }
            }
        }
        var disambiguated = ToolIdentity.DisambiguateOverloads(
            bases.ConvertAll(b => (b.BaseName, b.Hash)));

        var result = new List<CallableEntry>(bases.Count);
        for (int i = 0; i < bases.Count; i++)
        {
            result.Add(new CallableEntry(
                ToolName: disambiguated[i].Name,
                StableHash: disambiguated[i].Hash,
                RootName: bases[i].RootName,
                Callable: bases[i].Callable,
                WindowId: bases[i].WindowId));
        }
        return result;
    }

    private McpServerTool BuildTool(CallableEntry entry)
    {
        // Closure captures rootName + callable + windowId. The same root +
        // callable CLR identity is shared across all tool invocations, which
        // is intentional: the registry is the single source of truth. The
        // per-window variants close over their windowId so each one routes
        // to a specific tracked instance regardless of LLM calling pattern.
        var rootName = entry.RootName;
        var callable = entry.Callable;
        var capturedWindowId = entry.WindowId;
        var dispatchAdapter = _adapter;
        var loopGuard = _loopGuard;
        var manifest = _manifest;
        var logger = _logger;

        // Phase 10: the InvokeCoreAsync closure runs the existing
        // MarionetteDispatch pipeline. Returning a CallToolResult directly
        // lets the SDK's AIFunctionMcpServerTool path pass it through with
        // IsError preserved (the SDK has a dedicated arm for CallToolResult
        // in its return-shape switch).
        Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> invoke =
            async (args, ct) =>
            {
                var registered = manifest.Find(rootName);
                if (registered is null)
                {
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = new List<ContentBlock>
                        {
                            new TextContentBlock
                            {
                                Text = MarionetteDispatch
                                    .MakeError("unknown_root", $"Root '{rootName}' is no longer registered.")
                                    .ToJsonString(),
                            },
                        },
                    };
                }

                var argsElement = BuildArgsElement(args);

                var resultJson = await MarionetteDispatch.InvokeAsync(
                    registered, callable, argsElement, dispatchAdapter, loopGuard, logger,
                    capturedWindowId, ct).ConfigureAwait(false);

                // Detect structured errors and surface IsError=true so the
                // client treats them as failures rather than success values.
                bool isError = LooksLikeStructuredError(resultJson);

                return (object?)new CallToolResult
                {
                    IsError = isError,
                    Content = new List<ContentBlock>
                    {
                        new TextContentBlock { Text = resultJson },
                    },
                };
            };

        var description = string.IsNullOrEmpty(callable.Description)
            ? $"Invoke the [McpCallable] method {rootName}.{callable.Name}."
            : callable.Description;

        // Schema is the source-generator-emitted ParametersJsonSchema string
        // (Phase 1.b). Parse once and hand the JsonElement to the AIFunction;
        // the SDK reads it directly as the protocol Tool's InputSchema. If
        // parsing fails, fall back to a minimal `{}` schema so the tool stays
        // registered (with a logged warning).
        JsonElement schema = ParseSchemaOrFallback(callable.ParametersJsonSchema, entry.ToolName);

        var fn = new MarionetteAIFunction(entry.ToolName, description, schema, invoke);

        var options = new McpServerToolCreateOptions
        {
            Name = entry.ToolName,
            Description = description,
        };

        return McpServerTool.Create(fn, options);
    }

    private JsonElement ParseSchemaOrFallback(string? schemaJson, string toolName)
    {
        if (!string.IsNullOrEmpty(schemaJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(schemaJson);
                return doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to parse ParametersJsonSchema for {Tool}; falling back to empty schema.",
                    toolName);
            }
        }
        using var fallback = JsonDocument.Parse("{\"type\":\"object\"}");
        return fallback.RootElement.Clone();
    }

    /// <summary>
    /// Convert <see cref="AIFunctionArguments"/> (a dict of name → object?)
    /// into a single <see cref="JsonElement"/> object that
    /// <see cref="MarionetteDispatch.InvokeAsync"/> can consume.
    /// </summary>
    /// <remarks>
    /// AOT-clean: every byte goes through <see cref="System.Text.Json.Nodes.JsonObject"/>
    /// (typed JSON DOM) and <see cref="JsonDocument.Parse(string)"/> (the
    /// allocator-only parser). We deliberately avoid
    /// <see cref="JsonSerializer.SerializeToElement{TValue}(TValue)"/> /
    /// <c>SerializeToElement(object?)</c> because both require a
    /// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/> resolver
    /// and throw <see cref="InvalidOperationException"/> at runtime under AOT
    /// when reflection-based serialization is disabled.
    /// </remarks>
    private static JsonElement? BuildArgsElement(AIFunctionArguments args)
    {
        if (args is null || args.Count == 0) return null;

        // The SDK populates AIFunctionArguments from CallToolRequestParams.Arguments,
        // so each value is typically a JsonElement. We map back into a JsonObject
        // so MarionetteDispatch sees the same JsonElement tree it always has.
        var obj = new System.Text.Json.Nodes.JsonObject();
        foreach (var key in args.Keys)
        {
            var value = args[key];
            obj[key] = value switch
            {
                JsonElement el => System.Text.Json.Nodes.JsonNode.Parse(el.GetRawText()),
                string s => System.Text.Json.Nodes.JsonValue.Create(s),
                bool b => System.Text.Json.Nodes.JsonValue.Create(b),
                int i => System.Text.Json.Nodes.JsonValue.Create(i),
                long l => System.Text.Json.Nodes.JsonValue.Create(l),
                double d => System.Text.Json.Nodes.JsonValue.Create(d),
                null => null,
                _ => System.Text.Json.Nodes.JsonValue.Create(value.ToString()),
            };
        }
        // Round-trip through JsonDocument.Parse so the result is a freestanding
        // JsonElement value (no JsonNode-owned buffer, AOT-safe). JsonObject's
        // ToJsonString uses its own writer — no JsonTypeInfo required.
        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Detect a structured-error JSON object as emitted by
    /// <see cref="MarionetteDispatch.MakeError"/>. We probe cheaply (string
    /// match on success/false) rather than re-parse the response.
    /// </summary>
    private static bool LooksLikeStructuredError(string json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        if (json.Length < 16) return false;
        if (json[0] != '{') return false;
        // The pattern emitted by MakeError is
        // {"success":false,"errorCode":"…","message":"…"}.
        return json.Contains("\"success\":false", StringComparison.Ordinal)
               && json.Contains("\"errorCode\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// Pull the SDK's tool collection off the live <see cref="McpServer"/>.
    /// Returns <see langword="null"/> when the SDK shape has changed (defensive;
    /// 1.2.0 exposes it via <c>ServerOptions.ToolCollection</c>).
    /// </summary>
    private static McpServerPrimitiveCollection<McpServerTool>? TryGetToolCollection(McpServer server)
    {
        // The collection exists on McpServerOptions.ToolCollection and is
        // auto-populated by AddMcpServer + WithTools. The SDK creates the
        // collection lazily when needed; we ensure it's materialised by
        // creating one here when null.
        var options = server.ServerOptions;
        if (options is null) return null;

        var collection = options.ToolCollection;
        if (collection is not null) return collection;

        // Lazily create the collection. The SDK's docs (1.2.0) state that
        // tools added via WithTools<T>() are placed into this collection;
        // if it's still null, the host wiring put them somewhere else and
        // dynamic tools wouldn't merge cleanly anyway.
        try
        {
            var fresh = new McpServerPrimitiveCollection<McpServerTool>();
            options.ToolCollection = fresh;
            return fresh;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One discovered (root, callable, windowId) tuple after disambiguation.
    /// <see cref="WindowId"/> is <c>null</c> for the bare-form variant
    /// (default-window routing) and a specific window ID like <c>"w1"</c>
    /// for per-window variants emitted when the adapter reports 2+ live
    /// windows for the root.
    /// </summary>
    private readonly record struct CallableEntry(
        string ToolName,
        string StableHash,
        string RootName,
        CallableDescriptor Callable,
        string? WindowId);
}
