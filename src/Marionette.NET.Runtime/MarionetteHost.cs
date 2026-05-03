// Marionette.NET — Runtime host (Spike C wiring)
//
// Real stdio MCP server bootstrap. The contract is strict: stdout is reserved for
// JSON-RPC frames and nothing else. All logging, diagnostics, and any other
// "console output" must go to stderr or be silenced.
//
// To detect violators (so we don't ship a quiet bug into Phase 1) we install a
// guard TextWriter as Console.Out before the MCP transport is wired up. The
// transport hands stdout to itself via Console.OpenStandardOutput(), so it is
// unaffected — every other code path that writes via Console.Write* is caught.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Marionette.Runtime;

/// <summary>
/// Marionette.NET runtime entry point. Spike C: minimal but real stdio MCP server
/// using <c>ModelContextProtocol</c> 1.2.0. Phase 1 will replace the trivial
/// <see cref="PingTool"/> with the full ManifestRegistry-driven dispatch.
/// </summary>
public static class MarionetteHost
{
    /// <summary>
    /// Run the Marionette stdio MCP server until the client disconnects.
    /// </summary>
    /// <param name="args">CLI args, currently unused but kept for forward compat.</param>
    public static async Task RunAsync(string[] args)
    {
        _ = args;

        // Step 1 — clamp Console.Out *before* anything else has a chance to write.
        // We retain a private handle on the real stdout so the MCP transport can
        // still drive JSON-RPC frames via Console.OpenStandardOutput(); see
        // https://github.com/modelcontextprotocol/csharp-sdk for the contract.
        var realStdoutForTransport = Console.OpenStandardOutput();
        var stdoutGuard = new StdoutGuardWriter(Console.Error);
        Console.SetOut(stdoutGuard);

        // Step 2 — empty application builder. CreateApplicationBuilder would
        // register the default Console logging provider (which writes to stdout)
        // and would surface as garbage in the JSON-RPC stream. The empty
        // variant gives us a blank slate.
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);

        // Step 3 — logging to stderr ONLY. Any provider that writes to stdout
        // would corrupt the protocol. We register a tiny custom logger that
        // unconditionally writes to Console.Error, plus a high-enough default
        // filter so noisy framework messages don't spam the channel.
        builder.Logging.AddProvider(new StderrLoggerProvider());
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // The MCP SDK logs at Information for protocol traces; let it through if a user wants it.
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Information);
        builder.Logging.AddFilter("Marionette", LogLevel.Information);

        // Step 4 — wire the MCP server. WithStdioServerTransport reserves stdout
        // for JSON-RPC. WithTools<PingTool> is the explicit-registration API we
        // chose over WithToolsFromAssembly (Spike B noted the latter is reflection-
        // heavy and not AOT-clean; the generic-typed call lets the compiler track
        // the type and is the canonical path for our SourceGen integration later).
        builder.Services
            .AddMcpServer(opts =>
            {
                opts.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "Marionette.NET",
                    Version = "0.0.1-spike-c",
                };
            })
            .WithStdioServerTransport()
            .WithTools<PingTool>();

        // Hand a logger to the guard so it can self-report violations once
        // logging is up, instead of fire-and-forget Console.Error.WriteLine.
        var host = builder.Build();
        stdoutGuard.AttachLogger(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Marionette.StdoutGuard"));

        try
        {
            await host.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            // If anything wrote to the guard during the run, surface a final
            // count to stderr so the report has a number to point at.
            var leaked = stdoutGuard.LeakedByteCount;
            if (leaked > 0)
            {
                Console.Error.WriteLine($"[marionette] STDOUT GUARD: caught {leaked} bytes of would-be stdout pollution during the run.");
            }
            // The guard does not own realStdoutForTransport; nothing to dispose here.
            _ = realStdoutForTransport;
        }
    }
}

/// <summary>
/// A <see cref="TextWriter"/> that swallows any writes intended for stdout, counts
/// them, and forwards a one-line warning to stderr (or a logger once one is
/// available). Used as the diagnostic guard during Spike C — Phase 1 will drop
/// the byte counter once the source-gen analyzer rejects offending call sites
/// at compile time.
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
        // First violation: log it loudly. Subsequent violations: stay quiet so
        // we don't drown the stderr channel.
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
/// <see cref="Console.Error"/>. We do not register the default Console provider
/// (which writes to stdout) — that would be fatal for a stdio MCP server.
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
