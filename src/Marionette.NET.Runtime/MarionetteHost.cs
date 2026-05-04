// Marionette.NET — Phase 1.2 runtime host
//
// Replaces the Spike-C stub. The host:
//
//   1. Installs StdoutGuardWriter on Console.Out BEFORE any other code
//      (including the WPF Application ctor) writes a byte. The guard counts
//      stdout violations and forwards a one-line warning to stderr.
//   2. Parses CLI flags: --mcp, --mcp --headless, --mcp-help.
//      In any other case (no --mcp), returns 0 immediately so the caller's
//      regular GUI bootstrap runs.
//   3. Builds an empty Application host (CreateEmptyApplicationBuilder, NOT
//      CreateApplicationBuilder — the latter wires a stdout Console logger
//      that corrupts JSON-RPC frames).
//   4. Registers the runtime services:
//        IUiAutomationAdapter      (caller-supplied or NoOpAdapter)
//        ManifestRegistry          (built from caller-supplied roots)
//        LoopProtectionService     (singleton, env-overridable depth)
//        ChannelEmitter            (installs Marionette.Ai hooks)
//        WatchableResourceProvider (manages resources/list, /read, /subscribe)
//   5. Wires the MCP server:
//        WithStdioServerTransport  (stdout reserved for JSON-RPC)
//        WithTools<MarionetteTools> (the four Phase-1 tools)
//        WithListResourcesHandler / WithReadResourceHandler /
//          WithSubscribeToResourcesHandler / WithUnsubscribeFromResourcesHandler
//        Capabilities.Resources.Subscribe = true
//   6. Runs the host until stdin closes.
//
// This file is the only Phase-1.2 wiring point — it's intentionally a single
// composition root. Everything else is plain DI services with explicit
// dependencies. AOT-clean: no MakeGenericMethod, no reflective tool registration
// (WithTools<T> is generic-typed), no unguarded reflection.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Marionette.Runtime.Adapters;
using Marionette.Runtime.Channel;
using Marionette.Runtime.Events;
using Marionette.Runtime.Loop;
using Marionette.Runtime.Manifest;
using Marionette.Runtime.Resources;
using Marionette.Runtime.Tools;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Marionette.Runtime;

/// <summary>
/// Marionette.NET runtime entry point. Adopters call
/// <see cref="RunAsync(string[], IReadOnlyList{RootDescriptor}, IUiAutomationAdapter?, CancellationToken)"/>
/// from their <c>Main</c> after parsing argv; the host does the rest.
/// </summary>
public static class MarionetteHost
{
    /// <summary>
    /// Entry point used by Marionette-enabled applications.
    /// </summary>
    /// <param name="args">CLI args from <c>Main</c>. The host inspects <c>--mcp</c>, <c>--headless</c>, and <c>--mcp-help</c>.</param>
    /// <param name="roots">
    /// The source-generator-emitted root list, typically
    /// <c>Marionette.Generated.GeneratedManifest.Roots</c>.
    /// </param>
    /// <param name="adapter">
    /// Optional UI automation adapter. When <see langword="null"/>, the host
    /// installs a <see cref="NoOpAdapter"/>. The Phase 1.3 WPF adapter will
    /// be passed in here from the sample's <c>Program.cs</c>.
    /// </param>
    /// <param name="ct">Cancellation token bound to the host lifetime.</param>
    /// <returns>
    /// <c>0</c> on success or when the call is a no-op (no <c>--mcp</c> flag);
    /// non-zero on host startup failure.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>AOT/trim contract (Phase 4.2):</b> the runtime's hot paths are
    /// reflection-free — every <c>[McpCallable]</c> dispatch goes through a
    /// typed lambda emitted by the source generator, every observable read uses
    /// a typed getter, every event subscribe uses a typed delegate bridge.
    /// The reflection surfaces that DO exist are:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>raise_event</c> — see
    ///     <see cref="IUiAutomationAdapter.RaiseEventAsync(string, string, string, IReadOnlyDictionary{string, object?}?, string?, CancellationToken)"/>
    ///     which is itself marked <see cref="RequiresUnreferencedCodeAttribute"/>.
    ///   </description></item>
    ///   <item><description>
    ///     The MCP SDK's per-tool argument marshalling reflects on the
    ///     <see cref="ModelContextProtocol.Server.McpServerToolType"/>'s tool
    ///     methods. The SDK's analyzer surface ships its own annotations;
    ///     adopters who enable AOT see the SDK's IL2026 / IL3050 hints rather
    ///     than Marionette's.
    ///   </description></item>
    ///   <item><description>
    ///     The optional <see cref="IUiAutomationAdapter.RaiseEventAsync"/>
    ///     surface (see above).
    ///   </description></item>
    /// </list>
    /// <para>
    /// <see cref="RequiresUnreferencedCodeAttribute"/> is applied here at the
    /// composition boundary: adopters call <c>RunAsync</c> from their <c>Main</c>
    /// after parsing argv. AOT-publishing apps suppress the warning at that
    /// single call site once they've audited their use of <c>raise_event</c>;
    /// alternatively they avoid <c>raise_event</c> entirely and use
    /// <c>simulate_input</c> + <c>[McpCallable]</c> for fully reflection-free
    /// automation.
    /// </para>
    /// </remarks>
    [RequiresUnreferencedCode(
        "MarionetteHost.RunAsync surfaces the raise_event MCP tool, which resolves event " +
        "names by reflection on user types. Trimmed apps may lose event metadata for custom " +
        "controls. Additionally, the runtime serialises observable values, callable results, " +
        "and event payloads via System.Text.Json's reflection-based JsonSerializer; trimming " +
        "may strip the property metadata of user types. Suppress at the call site after " +
        "auditing, or avoid raise_event and confine [McpObservable]/event payloads to JSON-" +
        "primitive shapes (int/string/bool/arrays/INPC records) so the serializer's reflection " +
        "doesn't reach beyond what the trimmer keeps.")]
    [RequiresDynamicCode(
        "MarionetteHost.RunAsync uses System.Text.Json for boxed-object serialisation of " +
        "[McpObservable] reads, [McpCallable] results, and [McpEvent] payloads. JsonSerializer " +
        "may JIT-generate code at runtime when no JsonTypeInfo is supplied. Phase 4.2 keeps " +
        "this on the AOT-warning surface; Phase 6 may migrate to System.Text.Json source-" +
        "generation per descriptor type to close the warning. Suppress at the call site if " +
        "your AOT toolchain ships the JsonReader fallback path.")]
    public static async Task<int> RunAsync(
        string[] args,
        IReadOnlyList<RootDescriptor> roots,
        IUiAutomationAdapter? adapter = null,
        CancellationToken ct = default)
    {
        if (args is null) throw new ArgumentNullException(nameof(args));
        if (roots is null) throw new ArgumentNullException(nameof(roots));

        // ---- Parse CLI ---------------------------------------------------
        var (wantsMcp, wantsHelp, _) = ParseFlags(args);

        if (!wantsMcp && !wantsHelp)
        {
            // Caller's GUI / non-MCP path is what runs; we have nothing to do.
            return 0;
        }

        if (wantsHelp)
        {
            WriteHelpToStderr(roots);
            return 0;
        }

        // ---- Stdout guard (BEFORE any further code emits a byte) ---------
        // A real stdout handle is grabbed for the SDK's transport via
        // Console.OpenStandardOutput() inside WithStdioServerTransport — that
        // call bypasses our SetOut hook (which is exactly what we want).
        var realStdoutForTransport = Console.OpenStandardOutput();
        var stdoutGuard = new StdoutGuardWriter(Console.Error);
        Console.SetOut(stdoutGuard);

        // ---- Build host --------------------------------------------------
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);

        builder.Logging.AddProvider(new StderrLoggerProvider());
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Information);
        builder.Logging.AddFilter("Marionette", LogLevel.Information);

        // Adapter — caller-supplied or no-op fallback.
        var effectiveAdapter = adapter ?? new NoOpAdapter();
        builder.Services.AddSingleton(effectiveAdapter);

        // Manifest registry — single source of truth for descriptor metadata
        // and live root instances.
        var registry = new ManifestRegistry(roots);
        builder.Services.AddSingleton(registry);

        // Loop-protection: env-overridable, picked up at construction.
        builder.Services.AddSingleton<LoopProtectionService>();

        // Channel emitter (Ai.Trigger / Ai.ScheduleTrigger → notifications).
        builder.Services.AddSingleton<ChannelEmitter>();

        // Watchable resources.
        builder.Services.AddSingleton<WatchableResourceProvider>();

        // Phase 1.6: event log + per-event resource provider.
        builder.Services.AddSingleton<EventLogService>();
        builder.Services.AddSingleton<EventResourceProvider>();

        // Phase 2.2: per-method dynamic-tool registry. Bound to McpServer
        // after host build (see step 7 below) so the very first tools/list
        // response includes the per-method tools — Spielregel 7
        // deferred-schema-fetch insurance.
        builder.Services.AddSingleton<Marionette.Runtime.Tools.DynamicToolRegistry>();

        // ---- MCP server wiring ------------------------------------------
        var mcpBuilder = builder.Services
            .AddMcpServer(opts =>
            {
                opts.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "Marionette.NET",
                    Version = ThisAssemblyVersion(),
                };

                // Advertise resource subscription capability so Claude knows
                // it can call resources/subscribe and expect updated pushes.
                opts.Capabilities ??= new ServerCapabilities();
                opts.Capabilities.Resources ??= new ResourcesCapability();
                opts.Capabilities.Resources.Subscribe = true;

                // Phase 2.2: advertise tool-list-changed support so clients
                // refresh their tool cache when DynamicToolRegistry mutates
                // the set (hot-plug roots, future Phase 5+).
                opts.Capabilities.Tools ??= new ToolsCapability();
                opts.Capabilities.Tools.ListChanged = true;
            })
            .WithStdioServerTransport()
            // Generic-typed tool registration is the AOT-friendly path
            // (PHASE0_FINDINGS implication 6 + Spike B). The four Phase-1 tools
            // live as static methods on MarionetteTools.
            .WithTools<MarionetteTools>()
            // Resource handlers — Phase 1.6 dispatches between the watchable
            // observable provider and the event provider based on URI shape.
            // Both providers are singletons; the host fans out list/read/
            // subscribe/unsubscribe to whichever owns the URI.
            .WithListResourcesHandler(static async (ctx, ct) =>
            {
                var watchable = ctx.Services!.GetRequiredService<WatchableResourceProvider>();
                var events = ctx.Services!.GetRequiredService<EventResourceProvider>();
                var combined = new ListResourcesResult();
                var list = new List<Resource>();
                list.AddRange(watchable.List().Resources);
                list.AddRange(events.List());
                combined.Resources = list;
                return await Task.FromResult(combined);
            })
            .WithReadResourceHandler(static async (ctx, ct) =>
            {
                var watchable = ctx.Services!.GetRequiredService<WatchableResourceProvider>();
                var events = ctx.Services!.GetRequiredService<EventResourceProvider>();
                var uri = ctx.Params?.Uri ?? string.Empty;
                if (events.TryHandle(uri))
                    return await events.ReadAsync(uri, ct).ConfigureAwait(false);
                return await watchable.ReadAsync(uri, ct).ConfigureAwait(false);
            })
            .WithSubscribeToResourcesHandler(static async (ctx, ct) =>
            {
                var watchable = ctx.Services!.GetRequiredService<WatchableResourceProvider>();
                var events = ctx.Services!.GetRequiredService<EventResourceProvider>();
                var uri = ctx.Params?.Uri ?? string.Empty;
                if (events.TryHandle(uri))
                {
                    events.Subscribe(uri);
                }
                else
                {
                    watchable.Subscribe(uri);
                }
                return await Task.FromResult(new EmptyResult());
            })
            .WithUnsubscribeFromResourcesHandler(static async (ctx, ct) =>
            {
                var watchable = ctx.Services!.GetRequiredService<WatchableResourceProvider>();
                var events = ctx.Services!.GetRequiredService<EventResourceProvider>();
                var uri = ctx.Params?.Uri ?? string.Empty;
                if (events.TryHandle(uri))
                {
                    events.Unsubscribe(uri);
                }
                else
                {
                    watchable.Unsubscribe(uri);
                }
                return await Task.FromResult(new EmptyResult());
            });

        _ = mcpBuilder; // builder mutates services; the variable suppresses CS0219 if MCP API changes.

        // ---- Build, attach, run -----------------------------------------
        var host = builder.Build();
        var sp = host.Services;

        // Surface diagnostics for any root whose factory threw at startup.
        var startupLogger = sp.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Marionette.Runtime.MarionetteHost");
        foreach (var r in registry.Roots)
        {
            if (r.Instance is null && r.CreateError is not null)
            {
                startupLogger.LogWarning(
                    "Root '{Root}' factory threw at startup: {Error}. The runtime will surface a structured " +
                    "error when this root is referenced via invoke_method or read_observable.",
                    r.Descriptor.Name, r.CreateError);
            }
        }

        // Bind ChannelEmitter + Resource providers to the live MCP server. The
        // McpServer instance is registered by AddMcpServer — we pull it via DI.
        var server = sp.GetRequiredService<McpServer>();
        sp.GetRequiredService<ChannelEmitter>().Bind(server);
        sp.GetRequiredService<WatchableResourceProvider>().Bind(server);

        // Phase 1.6: start the event log + bind the per-event resource
        // provider to the server. Start() hooks every [McpEvent] on every
        // root; Stop() in the finally block detaches.
        var eventLog = sp.GetRequiredService<EventLogService>();
        eventLog.Start();
        sp.GetRequiredService<EventResourceProvider>().Bind(server);

        // Phase 2.2: register per-method dynamic tools. Must happen BEFORE
        // the server starts the run loop (i.e. before the first tools/list
        // response goes out). Spielregel 7: clients with deferred schema
        // fetch only see tools that exist at handshake time.
        sp.GetRequiredService<Marionette.Runtime.Tools.DynamicToolRegistry>().RegisterInitial(server);

        stdoutGuard.AttachLogger(startupLogger);

        try
        {
            await host.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // Best-effort hook teardown.
            try { await sp.GetRequiredService<ChannelEmitter>().DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore — shutdown path */ }
            try { await sp.GetRequiredService<WatchableResourceProvider>().DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore — shutdown path */ }
            // Phase 1.6: detach every [McpEvent] handler so root instances
            // are not GC-rooted by the runtime.
            try { await sp.GetRequiredService<EventResourceProvider>().DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore — shutdown path */ }
            try { sp.GetRequiredService<EventLogService>().Dispose(); }
            catch { /* ignore — shutdown path */ }

            var leaked = stdoutGuard.LeakedByteCount;
            if (leaked > 0)
            {
                Console.Error.WriteLine(
                    $"[marionette] STDOUT GUARD: caught {leaked} bytes of would-be stdout pollution during the run.");
            }
            _ = realStdoutForTransport;
        }

        return 0;
    }

    // -------------------------------------------------------------------------
    // CLI parsing + help
    // -------------------------------------------------------------------------

    private static (bool wantsMcp, bool wantsHelp, bool wantsHeadless) ParseFlags(string[] args)
    {
        bool mcp = false, help = false, headless = false;
        foreach (var a in args)
        {
            if (string.Equals(a, "--mcp", StringComparison.Ordinal)) mcp = true;
            else if (string.Equals(a, "--mcp-help", StringComparison.Ordinal)) help = true;
            else if (string.Equals(a, "--headless", StringComparison.Ordinal)) headless = true;
        }
        return (mcp, help, headless);
    }

    private static void WriteHelpToStderr(IReadOnlyList<RootDescriptor> roots)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Marionette.NET — manifest");
        sb.AppendLine("=========================");
        sb.AppendLine("This .exe ships with the following [McpRoot]-decorated entrypoints:");
        sb.AppendLine();
        if (roots.Count == 0)
        {
            sb.AppendLine("(no roots — the host will start but expose only the four meta-tools)");
        }
        foreach (var r in roots)
        {
            sb.AppendLine($"  root: {r.Name}  ({r.TypeName})");
            foreach (var c in r.Callables)
            {
                var ps = string.Join(", ",
                    c.Parameters.Select(p => $"{p.ClrTypeName} {p.Name}{(p.IsRequired ? string.Empty : "?")}"));
                sb.AppendLine($"    invoke   {r.Name}.{c.Name}({ps})  — {c.Description}");
            }
            foreach (var o in r.Observables)
            {
                var w = o.Watchable ? "watchable" : "read-only";
                sb.AppendLine($"    observe  {r.Name}.{o.Name}: {o.ClrTypeName}  ({w})  — {o.Description}");
            }
            foreach (var t in r.Triggerables)
            {
                sb.AppendLine($"    trigger  {r.Name}.{t.Name}: {t.ControlTypeName}  ({t.Strategy})  — {t.Description}");
            }
            foreach (var e in r.Events)
            {
                sb.AppendLine($"    event    {r.Name}.{e.Name}: {e.ArgsTypeName}  (queue={e.MaxQueueSize}, coalesce={e.CoalesceWindowMs}ms, minInterval={e.MinIntervalMs}ms)  — {e.Description}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("CLI flags:");
        sb.AppendLine("  --mcp              run as a stdio MCP server alongside the GUI (Phase 1.2)");
        sb.AppendLine("  --mcp --headless   run as a stdio MCP server with no GUI (Frozen-Mode style)");
        sb.AppendLine("  --mcp-help         print this manifest summary to stderr and exit");
        sb.AppendLine();
        sb.AppendLine("Environment:");
        sb.AppendLine("  MARIONETTE_MAX_DEPTH       override the loop-protection depth (default 5)");
        sb.AppendLine("  MARIONETTE_DECAY_SECONDS   override the loop-protection decay window seconds (default 30)");

        Console.Error.Write(sb.ToString());
    }

    private static string ThisAssemblyVersion()
    {
        var asm = typeof(MarionetteHost).Assembly;
        var ver = asm.GetName().Version;
        return ver?.ToString(3) ?? "0.0.1";
    }
}

/// <summary>
/// A <see cref="TextWriter"/> that swallows any writes intended for stdout,
/// counts them, and forwards a one-line warning to stderr (or a logger once
/// one is available). Permanent fixture per PHASE0_FINDINGS implication 6.
/// </summary>
internal sealed class StdoutGuardWriter : TextWriter
{
    private readonly TextWriter _stderr;
    private long _leakedBytes;
    private ILogger? _logger;
    private int _warned;

    public StdoutGuardWriter(TextWriter stderr)
    {
        _stderr = stderr ?? throw new ArgumentNullException(nameof(stderr));
    }

    public long LeakedByteCount => Interlocked.Read(ref _leakedBytes);

    public override Encoding Encoding => Encoding.UTF8;

    public void AttachLogger(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override void Write(char value)
    {
        Interlocked.Add(ref _leakedBytes, 1);
        Warn("char");
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Interlocked.Add(ref _leakedBytes, value!.Length);
        Warn(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _leakedBytes, count);
        Warn(new string(buffer, index, count));
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        Write(Environment.NewLine);
    }

    private void Warn(string what)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            var msg = $"[marionette] STDOUT GUARD: caught a write to Console.Out (\"{Truncate(what, 80)}\"). " +
                      "Stdio MCP servers must keep stdout reserved for JSON-RPC. " +
                      "Route this output to stderr (or an ILogger) instead. Further violations will be silently counted.";
            if (_logger is not null)
            {
                _logger.LogError("{Message}", msg);
            }
            else
            {
                _stderr.WriteLine(msg);
            }
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";
}

/// <summary>
/// Tiny <see cref="ILoggerProvider"/> that forwards every log entry to
/// <see cref="Console.Error"/>. We do not register the default Console
/// provider (which writes to stdout) — that would be fatal for a stdio MCP
/// server.
/// </summary>
internal sealed class StderrLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StderrLogger(categoryName);

    public void Dispose() { }

    private sealed class StderrLogger(string category) : ILogger
    {
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null) return;
            var line = $"[{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                line += " " + exception;
            }
            Console.Error.WriteLine(line);
        }
    }
}
