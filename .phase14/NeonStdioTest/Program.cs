// Phase 14 — end-of-phase showcase MCP debug harness for Sample.Wpf.NeonControlCenter
//
// Verbatim port of the .phase0/StdioTest pattern adapted for the Neon
// sample's manifest. Sends each JSON-RPC request and waits for the matching
// id-correlated response before sending the next one — this is what makes
// the OutputDataReceived event-pump actually deliver stdout lines.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NeonStdioTest;

internal static class Program
{
    private static int _nextId;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: NeonStdioTest <path-to-Sample.Wpf.NeonControlCenter.exe> [--watch]");
            return 2;
        }
        var exe = args[0];
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"FAIL — exe not found: {exe}");
            return 2;
        }
        // --watch: GUI mode (no --headless) + delays between steps so the
        // user can see live state changes in the WPF window.
        bool watch = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--watch") watch = true;
        }
        var stepDelay = watch ? TimeSpan.FromMilliseconds(2000) : TimeSpan.Zero;

        Console.WriteLine();
        Console.WriteLine("===== NEON CONTROL CENTER ===== MCP DEBUG SESSION =====");
        Console.WriteLine();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("--mcp");
        if (!watch) psi.ArgumentList.Add("--headless");
        // Bump the loop-protection hop budget so the 13-step test sequence
        // doesn't trip Spielregel 3's default cap of 5 hops. The protection
        // itself is working correctly; the test just exercises more tools
        // in one session than typical adopter usage.
        psi.Environment["MARIONETTE_MAX_DEPTH"] = "50";

        using var child = new Process { StartInfo = psi };
        var stdoutMessages = new BlockingCollection<JsonDocument>(boundedCapacity: 256);
        var stderrSb = new StringBuilder();

        child.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            try
            {
                var doc = JsonDocument.Parse(e.Data);
                stdoutMessages.Add(doc);
            }
            catch { /* non-JSON, ignore */ }
        };
        child.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderrSb) { stderrSb.AppendLine(e.Data); }
        };

        if (!child.Start())
        {
            Console.Error.WriteLine("FAIL — could not start child");
            return 1;
        }
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

        var pass = 0;
        var fail = 0;

        async Task<bool> RunStep(string label, string method, object? @params, Func<JsonElement, string>? preview = null)
        {
            var id = Interlocked.Increment(ref _nextId);
            var payload = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
            };
            if (@params is not null) payload["params"] = @params;
            var json = JsonSerializer.Serialize(payload);
            await child.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
            await child.StandardInput.FlushAsync().ConfigureAwait(false);

            var resp = await WaitForResponseAsync(stdoutMessages, id, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (resp is null)
            {
                Print($"  [{id,2}] {label} ... FAIL: no response within 10s", red: true);
                fail++; return false;
            }
            var root = resp.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "(unknown)";
                Print($"  [{id,2}] {label} ... FAIL: {msg}", red: true);
                fail++; return false;
            }
            if (!root.TryGetProperty("result", out var result))
            {
                Print($"  [{id,2}] {label} ... FAIL: no result", red: true);
                fail++; return false;
            }
            if (result.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True)
            {
                var content = result.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0
                    ? (c[0].TryGetProperty("text", out var t) ? t.GetString() ?? "" : "")
                    : "";
                if (content.Length > 220) content = content.Substring(0, 220) + "...";
                Print($"  [{id,2}] {label} ... FAIL: tool isError. {content}", red: true);
                fail++; return false;
            }

            Print($"  [{id,2}] {label} ... PASS", green: true);
            pass++;
            if (preview is not null)
            {
                var s = preview(result);
                if (!string.IsNullOrEmpty(s))
                {
                    if (s.Length > 140) s = s.Substring(0, 140) + "...";
                    Console.WriteLine($"       -> {s}");
                }
            }
            // Watch-mode delay: gives the WPF UI a moment to render the
            // change so the user can see it before the next call fires.
            if (stepDelay > TimeSpan.Zero) await Task.Delay(stepDelay).ConfigureAwait(false);
            return true;
        }

        // ---- Initialize handshake ----
        await RunStep("initialize handshake", "initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "neon-stdio-test", version = "1.0" },
        }).ConfigureAwait(false);

        // notifications/initialized — no response expected, just send.
        var initNotif = new { jsonrpc = "2.0", method = "notifications/initialized" };
        await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(initNotif)).ConfigureAwait(false);
        await child.StandardInput.FlushAsync().ConfigureAwait(false);

        // ---- Steps ----
        await RunStep("tools/list", "tools/list", new { }, r => $"tools advertised: {r.GetProperty("tools").GetArrayLength()}");
        await RunStep("inspect_app_api", "tools/call", new { name = "inspect_app_api", arguments = new { } });
        await RunStep("read_observable(ReactorOutput)", "tools/call",
            new { name = "read_observable", arguments = new { root = "mission", property = "ReactorOutput" } },
            r => r.GetProperty("content")[0].GetProperty("text").GetString() ?? "");
        await RunStep("mission.Snapshot() — record JSON", "tools/call",
            new { name = "mission.Snapshot", arguments = new { } },
            r => r.GetProperty("content")[0].GetProperty("text").GetString() ?? "");
        await RunStep("mission.Engage()", "tools/call",
            new { name = "mission.Engage", arguments = new { } });
        await RunStep("read_observable(SystemStatus) after Engage", "tools/call",
            new { name = "read_observable", arguments = new { root = "mission", property = "SystemStatus" } },
            r => r.GetProperty("content")[0].GetProperty("text").GetString() ?? "");
        await RunStep("mission.AdjustPower(delta=15)", "tools/call",
            new { name = "mission.AdjustPower", arguments = new { delta = 15 } },
            r => r.GetProperty("content")[0].GetProperty("text").GetString() ?? "");
        await RunStep("mission.RunDiagnosticAsync() — async, OffUiThread", "tools/call",
            new { name = "mission.RunDiagnosticAsync", arguments = new { } },
            r => r.GetProperty("content")[0].GetProperty("text").GetString() ?? "");
        await RunStep("mission.SnapshotMetrics() — Dictionary<string,double>", "tools/call",
            new { name = "mission.SnapshotMetrics", arguments = new { } },
            r => r.GetProperty("content")[0].GetProperty("text").GetString() ?? "");
        await RunStep("mission.GetAlertFeed() — List<string>", "tools/call",
            new { name = "mission.GetAlertFeed", arguments = new { } },
            r => r.GetProperty("content")[0].GetProperty("text").GetString() ?? "");
        await RunStep("mission.ResetTelemetry()", "tools/call",
            new { name = "mission.ResetTelemetry", arguments = new { } });
        await RunStep("resources/list", "resources/list", new { },
            r => $"resources advertised: {r.GetProperty("resources").GetArrayLength()}");

        // ---- Shutdown ----
        try { child.StandardInput.Close(); } catch { }
        if (!child.WaitForExit(10_000))
        {
            try { child.Kill(); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("============== SESSION COMPLETE ==============");
        Console.WriteLine($"  PASS: {pass}");
        Console.WriteLine($"  FAIL: {fail}");
        Console.WriteLine();

        if (fail > 0)
        {
            Console.WriteLine("stderr tail (server log):");
            var lines = stderrSb.ToString().Split('\n');
            int start = Math.Max(0, lines.Length - 30);
            for (int i = start; i < lines.Length; i++)
            {
                Console.WriteLine($"  {lines[i].TrimEnd()}");
            }
            return 1;
        }
        return 0;
    }

    private static Task<JsonDocument?> WaitForResponseAsync(
        BlockingCollection<JsonDocument> queue, int expectedId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            if (queue.TryTake(out var doc, (int)Math.Min(remaining.TotalMilliseconds, 200)))
            {
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    var match = idProp.ValueKind switch
                    {
                        JsonValueKind.Number => idProp.TryGetInt32(out var n) && n == expectedId,
                        JsonValueKind.String => idProp.GetString() == expectedId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        _ => false,
                    };
                    if (match) return Task.FromResult<JsonDocument?>(doc);
                }
                // Not for us — keep going.
            }
        }
        return Task.FromResult<JsonDocument?>(null);
    }

    private static void Print(string s, bool green = false, bool red = false)
    {
        var prev = Console.ForegroundColor;
        if (green) Console.ForegroundColor = ConsoleColor.Green;
        if (red) Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(s);
        Console.ForegroundColor = prev;
    }
}
