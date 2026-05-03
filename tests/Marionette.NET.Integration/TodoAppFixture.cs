// Marionette.NET — Integration test fixture
//
// Spawns a fresh Sample.Wpf.TodoApp.exe in `--mcp --headless` mode for each
// test, exposes a small stdio JSON-RPC client surface, and guarantees the
// child is killed when the fixture is disposed (even if a test throws).
//
// One fixture instance per [Fact] (xUnit's default lifetime when injected
// via constructor + IDisposable). EC-1..5 each spawn their own child to
// keep state isolation strict — leaking state between cases would mask real
// regressions.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Marionette.Integration;

/// <summary>
/// Owns a TodoApp child process plus its stdio streams. One instance per test.
/// </summary>
internal sealed class TodoAppFixture : IDisposable
{
    private readonly Process _child;
    private readonly ConcurrentQueue<string> _stdoutLines = new();
    private readonly ConcurrentQueue<string> _stderrLines = new();
    private readonly BlockingCollection<JsonDocument> _messageQueue = new(boundedCapacity: 256);
    private readonly ConcurrentQueue<JsonDocument> _stashedNotifications = new();
    private int _nextRequestId = 1;
    private bool _disposed;

    public IReadOnlyCollection<string> StdoutLines => (IReadOnlyCollection<string>)_stdoutLines;
    public IReadOnlyCollection<string> StderrLines => (IReadOnlyCollection<string>)_stderrLines;

    public Process Child => _child;

    /// <summary>
    /// Locate the TodoApp .exe relative to the integration-test assembly's
    /// path. The project's csproj registers a build dependency on the sample
    /// (with <c>ReferenceOutputAssembly=false</c>), so the exe is built
    /// before our tests run as part of any solution build.
    /// </summary>
    public static string ResolveTodoAppExePath(string configuration = "Debug")
    {
        // Walk up from this test assembly's directory to the repo root, then
        // descend into the sample's bin folder. Tested working dir is
        // typically tests/Marionette.NET.Integration/bin/Debug/net10.0/.
        var here = Path.GetDirectoryName(typeof(TodoAppFixture).Assembly.Location)
                   ?? Directory.GetCurrentDirectory();
        var probe = here;
        for (var i = 0; i < 8 && probe is not null; i++)
        {
            var candidate = Path.Combine(probe, "samples", "Sample.Wpf.TodoApp", "bin",
                configuration, "net10.0-windows", "Sample.Wpf.TodoApp.exe");
            if (File.Exists(candidate)) return candidate;
            probe = Path.GetDirectoryName(probe);
        }
        throw new FileNotFoundException(
            $"Could not locate Sample.Wpf.TodoApp.exe by walking up from '{here}'. " +
            "Build the solution first: dotnet build Marionette.NET.sln -c " + configuration + ".");
    }

    /// <summary>
    /// Spawn a TodoApp child in `--mcp --headless` mode. Optional environment
    /// overrides cover the loop-protection knobs used by EC-4.
    /// </summary>
    public TodoAppFixture(IDictionary<string, string?>? extraEnv = null)
    {
        var exePath = ResolveTodoAppExePath();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        psi.ArgumentList.Add("--mcp");
        psi.ArgumentList.Add("--headless");

        if (extraEnv is not null)
        {
            foreach (var kvp in extraEnv)
            {
                if (kvp.Value is null) continue;
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }

        _child = new Process { StartInfo = psi };
        _child.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _stdoutLines.Enqueue(e.Data);
            try
            {
                var doc = JsonDocument.Parse(e.Data);
                _messageQueue.Add(doc);
            }
            catch (JsonException)
            {
                // Pollution will be detected later. EC-5 asserts on this.
            }
        };
        _child.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) _stderrLines.Enqueue(e.Data);
        };

        if (!_child.Start())
        {
            throw new InvalidOperationException("Could not start TodoApp child process: " + exePath);
        }
        _child.BeginOutputReadLine();
        _child.BeginErrorReadLine();
    }

    /// <summary>
    /// Send the MCP initialize handshake and wait for its response. Throws
    /// when the response does not arrive within <paramref name="timeout"/>.
    /// </summary>
    public async Task<JsonDocument> InitializeAsync(TimeSpan? timeout = null)
    {
        var initId = NextId();
        await SendRawAsync(new
        {
            jsonrpc = "2.0",
            id = initId,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-11-25",
                capabilities = new { },
                clientInfo = new { name = "marionette-integration", version = "0.1.0" },
            },
        }).ConfigureAwait(false);

        // Default timeout 30s — enough headroom for cold-start of the WPF
        // SDK + CLR JIT on a slow CI machine. Headless mode usually takes
        // <2s end-to-end on a warm box.
        var resp = await WaitForResponseAsync(initId, timeout ?? TimeSpan.FromSeconds(30))
                  .ConfigureAwait(false)
                  ?? throw new TimeoutException("No response to initialize within the timeout window.");

        await SendRawAsync(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized",
            @params = new { },
        }).ConfigureAwait(false);

        return resp;
    }

    /// <summary>
    /// tools/list — returns the array of tool descriptor names alphabetised.
    /// Sorting is necessary because the MCP spec does not guarantee order.
    /// </summary>
    public async Task<List<string>> ListToolsAsync(TimeSpan? timeout = null)
    {
        var id = NextId();
        await SendRawAsync(new { jsonrpc = "2.0", id, method = "tools/list", @params = new { } })
            .ConfigureAwait(false);
        using var resp = await WaitForResponseAsync(id, timeout ?? TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false)
                        ?? throw new TimeoutException("No response to tools/list within the timeout window.");

        var names = new List<string>();
        if (resp.RootElement.TryGetProperty("result", out var result) &&
            result.TryGetProperty("tools", out var tools) &&
            tools.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.TryGetProperty("name", out var n) && n.GetString() is string s)
                    names.Add(s);
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// tools/call inspect_app_api(rootName?). Returns the parsed result text
    /// (which is itself a JSON string).
    /// </summary>
    public async Task<string> InspectAppApiAsync(string? rootName = null, TimeSpan? timeout = null)
    {
        var id = NextId();
        var arguments = rootName is null
            ? (object)new { }
            : new { rootName };
        await SendRawAsync(new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name = "inspect_app_api", arguments },
        }).ConfigureAwait(false);

        using var resp = await WaitForResponseAsync(id, timeout ?? TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false)
                        ?? throw new TimeoutException("No response to inspect_app_api within the timeout window.");

        if (!TryReadToolText(resp.RootElement, out var text))
            throw new InvalidOperationException("inspect_app_api response had no text content: " +
                resp.RootElement.GetRawText());
        return text;
    }

    /// <summary>
    /// tools/call invoke_method(root, method, args). Returns the raw text
    /// result string ("null" for void callables, JSON for primitives, or a
    /// JSON object describing a structured error).
    /// </summary>
    public async Task<string> InvokeMethodAsync(
        string root,
        string method,
        object? methodArgs = null,
        TimeSpan? timeout = null)
    {
        var id = NextId();
        await SendRawAsync(new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new
            {
                name = "invoke_method",
                arguments = new { root, method, args = methodArgs },
            },
        }).ConfigureAwait(false);

        using var resp = await WaitForResponseAsync(id, timeout ?? TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false)
                        ?? throw new TimeoutException(
                            $"No response to invoke_method {root}.{method} within the timeout window.");

        if (!TryReadToolText(resp.RootElement, out var text))
            throw new InvalidOperationException("invoke_method response had no text content: " +
                resp.RootElement.GetRawText());
        return text;
    }

    /// <summary>
    /// tools/call read_observable(root, property). Returns the raw text result
    /// (a JSON-serialised value or a structured error object).
    /// </summary>
    public async Task<string> ReadObservableAsync(string root, string property, TimeSpan? timeout = null)
    {
        var id = NextId();
        await SendRawAsync(new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new
            {
                name = "read_observable",
                arguments = new { root, property },
            },
        }).ConfigureAwait(false);

        using var resp = await WaitForResponseAsync(id, timeout ?? TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false)
                        ?? throw new TimeoutException(
                            $"No response to read_observable {root}.{property} within the timeout window.");

        if (!TryReadToolText(resp.RootElement, out var text))
            throw new InvalidOperationException("read_observable response had no text content: " +
                resp.RootElement.GetRawText());
        return text;
    }

    /// <summary>
    /// resources/subscribe to the given URI. Returns true when the response
    /// carries a result (the SDK's standard EmptyResult).
    /// </summary>
    public async Task<bool> SubscribeAsync(string uri, TimeSpan? timeout = null)
    {
        var id = NextId();
        await SendRawAsync(new
        {
            jsonrpc = "2.0",
            id,
            method = "resources/subscribe",
            @params = new { uri },
        }).ConfigureAwait(false);

        using var resp = await WaitForResponseAsync(id, timeout ?? TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false)
                        ?? throw new TimeoutException("No response to resources/subscribe within the timeout window.");
        return resp.RootElement.TryGetProperty("result", out _);
    }

    /// <summary>
    /// resources/read for the given URI. Returns the integer text from the
    /// first content block. Returns null if the response shape is unexpected.
    /// </summary>
    public async Task<string?> ReadResourceAsync(string uri, TimeSpan? timeout = null)
    {
        var id = NextId();
        await SendRawAsync(new
        {
            jsonrpc = "2.0",
            id,
            method = "resources/read",
            @params = new { uri },
        }).ConfigureAwait(false);

        using var resp = await WaitForResponseAsync(id, timeout ?? TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false)
                        ?? throw new TimeoutException("No response to resources/read within the timeout window.");

        if (resp.RootElement.TryGetProperty("result", out var result) &&
            result.TryGetProperty("contents", out var contents) &&
            contents.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in contents.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var t)) return t.GetString();
            }
        }
        return null;
    }

    /// <summary>
    /// Wait for a notifications/resources/updated for the specific URI. Looks
    /// at notifications stashed during prior request/response correlations
    /// first, then drains the live queue. Returns true on first match.
    /// </summary>
    public async Task<bool> WaitForResourceUpdateAsync(string uri, TimeSpan timeout)
    {
        // First scan stashed notifications.
        var keptStashed = new List<JsonDocument>();
        while (_stashedNotifications.TryDequeue(out var stashed))
        {
            if (IsResourceUpdate(stashed.RootElement, uri))
            {
                stashed.Dispose();
                foreach (var k in keptStashed) _stashedNotifications.Enqueue(k);
                return true;
            }
            keptStashed.Add(stashed);
        }
        foreach (var k in keptStashed) _stashedNotifications.Enqueue(k);

        // Then live queue.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            if (_messageQueue.TryTake(out var doc, (int)Math.Min(remaining.TotalMilliseconds, 200)))
            {
                try
                {
                    if (IsResourceUpdate(doc.RootElement, uri)) return true;
                    if (!doc.RootElement.TryGetProperty("id", out _))
                    {
                        _stashedNotifications.Enqueue(doc);
                        continue;
                    }
                }
                catch
                {
                    // fall through to dispose
                }
                doc.Dispose();
            }
            await Task.Yield();
        }
        return false;
    }

    /// <summary>
    /// Send a raw JSON-RPC payload to the child's stdin (newline-terminated).
    /// </summary>
    public async Task SendRawAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        await _child.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
        await _child.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    private int NextId() => Interlocked.Increment(ref _nextRequestId);

    private async Task<JsonDocument?> WaitForResponseAsync(int expectedId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            try
            {
                if (_messageQueue.TryTake(out var doc, (int)Math.Min(remaining.TotalMilliseconds, 200)))
                {
                    if (doc.RootElement.TryGetProperty("id", out var idProp))
                    {
                        var idMatch = idProp.ValueKind switch
                        {
                            JsonValueKind.Number => idProp.TryGetInt32(out var n) && n == expectedId,
                            JsonValueKind.String => idProp.GetString() == expectedId.ToString(),
                            _ => false,
                        };
                        if (idMatch) return doc;
                        doc.Dispose();
                    }
                    else
                    {
                        // Notification — stash for later.
                        _stashedNotifications.Enqueue(doc);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            await Task.Yield();
        }
        return null;
    }

    private static bool TryReadToolText(JsonElement root, out string text)
    {
        text = string.Empty;
        if (!root.TryGetProperty("result", out var result)) return false;
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var t))
            {
                text = t.GetString() ?? string.Empty;
                return true;
            }
        }
        return false;
    }

    private static bool IsResourceUpdate(JsonElement root, string uri)
    {
        return root.TryGetProperty("method", out var m) &&
               m.GetString() == "notifications/resources/updated" &&
               root.TryGetProperty("params", out var p) &&
               p.TryGetProperty("uri", out var u) &&
               u.GetString() == uri;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _child.StandardInput.Close(); } catch { /* ignore */ }
        if (!_child.WaitForExit(2000))
        {
            try { _child.Kill(entireProcessTree: true); } catch { /* ignore */ }
            _child.WaitForExit(2000);
        }
        try { _child.Dispose(); } catch { /* ignore */ }

        try { _messageQueue.CompleteAdding(); } catch { /* ignore */ }
        while (_messageQueue.TryTake(out var d)) { try { d.Dispose(); } catch { /* ignore */ } }
        while (_stashedNotifications.TryDequeue(out var n)) { try { n.Dispose(); } catch { /* ignore */ } }
        try { _messageQueue.Dispose(); } catch { /* ignore */ }
    }
}
