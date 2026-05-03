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

using System;
using System.Collections.Generic;
using System.Text.Json;
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
/// Per-method MCP tool registrar (Phase 2.2). Registered as a singleton in
/// <see cref="MarionetteHost"/>; its <see cref="RegisterInitial"/> is called
/// once after the <see cref="McpServer"/> is built so the dynamic tools
/// appear in the very first <c>tools/list</c> response.
/// </summary>
public sealed class DynamicToolRegistry
{
    private readonly ManifestRegistry _manifest;
    private readonly IUiAutomationAdapter _adapter;
    private readonly LoopProtectionService _loopGuard;
    private readonly ILogger<DynamicToolRegistry> _logger;
    private readonly Dictionary<string, string> _registered = new(StringComparer.Ordinal); // toolName → stableHash

    private McpServer? _server;
    private bool _initialRegistrationDone;

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

        // Initial registration happens BEFORE the SDK starts servicing
        // tools/list, so we deliberately do NOT raise the changed event
        // here — clients will read the dynamic tools in the very first
        // listing.
        _logger.LogInformation("Dynamic per-method tools registered: {Count}.", _registered.Count);
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

        var entries = ComputeEntries();
        var newSet = new Dictionary<string, (CallableEntry Entry, string Hash)>(StringComparer.Ordinal);
        foreach (var e in entries) newSet[e.ToolName] = (e, e.StableHash);

        var dirty = false;

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

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private List<CallableEntry> ComputeEntries()
    {
        // Build a base-name list per (root, callable) so
        // ToolIdentity.DisambiguateOverloads can rewrite duplicates with
        // a stable suffix derived from the hash. The order of iteration
        // matches RootDescriptor order (deterministic).
        var bases = new List<(string BaseName, string Hash, string RootName, CallableDescriptor Callable)>();
        foreach (var root in _manifest.Roots)
        {
            foreach (var callable in root.Descriptor.Callables)
            {
                var baseName = ToolIdentity.ComputeToolName(root.Descriptor.Name, callable);
                var hash = ToolIdentity.ComputeStableHash(root.Descriptor.Name, callable);
                bases.Add((baseName, hash, root.Descriptor.Name, callable));
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
                Callable: bases[i].Callable));
        }
        return result;
    }

    private McpServerTool BuildTool(CallableEntry entry)
    {
        // Closure captures rootName + callable. The same root + callable
        // CLR identity is shared across all tool invocations, which is
        // intentional: the registry is the single source of truth.
        var rootName = entry.RootName;
        var callable = entry.Callable;
        var dispatchAdapter = _adapter;
        var loopGuard = _loopGuard;
        var manifest = _manifest;
        var logger = _logger;

        // The handler delegate signature uses the SDK's request-context
        // injection pattern: `RequestContext<CallToolRequestParams>` is
        // auto-bound by the SDK and NOT included in the input schema. The
        // handler reads the args dict directly from ctx.Params.Arguments
        // and converts to the JsonElement bag MarionetteDispatch wants.
        Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<CallToolResult>> handler =
            async (ctx, ct) =>
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

                var argsElement = BuildArgsElement(ctx);

                var resultJson = await MarionetteDispatch.InvokeAsync(
                    registered, callable, argsElement, dispatchAdapter, loopGuard, logger, ct).ConfigureAwait(false);

                // Detect structured errors and surface IsError=true so the
                // client treats them as failures rather than success values.
                bool isError = LooksLikeStructuredError(resultJson);

                return new CallToolResult
                {
                    IsError = isError,
                    Content = new List<ContentBlock>
                    {
                        new TextContentBlock { Text = resultJson },
                    },
                };
            };

        var options = new McpServerToolCreateOptions
        {
            Name = entry.ToolName,
            Description = string.IsNullOrEmpty(callable.Description)
                ? $"Invoke the [McpCallable] method {rootName}.{callable.Name}."
                : callable.Description,
        };

        var tool = McpServerTool.Create((Delegate)handler, options);

        // Phase 2.2: stamp the source-gen-emitted parameter schema onto the
        // protocol Tool. The SDK auto-derives a default schema from the
        // delegate's signature (which only sees RequestContext + ct after
        // its DI/special-binding pass), so the default is the minimal
        // {"type":"object"}. We replace it with the rich per-method schema.
        try
        {
            if (!string.IsNullOrEmpty(callable.ParametersJsonSchema))
            {
                using var doc = JsonDocument.Parse(callable.ParametersJsonSchema);
                tool.ProtocolTool.InputSchema = doc.RootElement.Clone();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse ParametersJsonSchema for {Tool}; falling back to SDK-derived default.", entry.ToolName);
        }

        return tool;
    }

    /// <summary>
    /// Convert <c>ctx.Params.Arguments</c> (a dict of JsonElement values) into
    /// a single <see cref="JsonElement"/> object that
    /// <see cref="MarionetteDispatch.InvokeAsync"/> can consume.
    /// </summary>
    private static JsonElement? BuildArgsElement(RequestContext<CallToolRequestParams> ctx)
    {
        var args = ctx.Params?.Arguments;
        if (args is null || args.Count == 0) return null;

        // Construct a minimal JSON object via a JsonNode to avoid building a
        // string. JsonNode → JsonElement round-trip is cheap and AOT-clean.
        var obj = new System.Text.Json.Nodes.JsonObject();
        foreach (var kvp in args)
        {
            obj[kvp.Key] = System.Text.Json.Nodes.JsonNode.Parse(kvp.Value.GetRawText());
        }
        var root = System.Text.Json.Nodes.JsonNode.Parse(obj.ToJsonString());
        return root is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(root);
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
    /// One discovered (root, callable) pair after disambiguation.
    /// </summary>
    private readonly record struct CallableEntry(
        string ToolName,
        string StableHash,
        string RootName,
        CallableDescriptor Callable);
}
