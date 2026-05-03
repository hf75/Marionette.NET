// Spike C — stdio handshake harness for Sample.Wpf.StripeProbe.exe --mcp --headless
//
// Validates Q1: a real MCP initialize handshake, tools/list, and tools/call round-trip
// over stdio with no stdout pollution.
//
// Strict invariants enforced:
//   * EVERY line emitted on the child's stdout must parse as a single JSON-RPC frame.
//   * Stderr is captured separately and reported but doesn't fail the test.
//   * The child receives Content-Length / line-delimited JSON on its stdin and is
//     expected to follow newline-delimited JSON-RPC (the SDK's stdio default).
//
// Exit code 0 = PASS, non-zero = FAIL.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StdioTest;

internal static class Program
{
    private static int _nextRequestId = 1;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: StdioTest <path-to-Sample.Wpf.StripeProbe.exe>");
            return 2;
        }

        var exePath = args[0];
        var probeMode = false;
        var guiMode = false;
        for (var ai = 1; ai < args.Length; ai++)
        {
            if (args[ai] == "--probe") probeMode = true;
            else if (args[ai] == "--gui") guiMode = true;
        }
        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"FAIL — child executable not found at {exePath}");
            return 2;
        }

        Console.WriteLine($"=== Spike C stdio handshake harness ===");
        Console.WriteLine($"Child: {exePath}");
        Console.WriteLine($"Args:  --mcp{(guiMode ? string.Empty : " --headless")}");
        if (probeMode) Console.WriteLine($"Diagnostic: MARIONETTE_STDOUT_PROBE=1 (Q2 violator probe)");
        Console.WriteLine();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Force UTF-8 to keep parity with what the MCP SDK assumes.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        psi.ArgumentList.Add("--mcp");
        if (!guiMode) psi.ArgumentList.Add("--headless");
        if (probeMode)
        {
            psi.Environment["MARIONETTE_STDOUT_PROBE"] = "1";
        }

        using var child = new Process { StartInfo = psi };
        var stdoutLines = new ConcurrentQueue<string>();
        var stderrLines = new ConcurrentQueue<string>();
        var stdoutMessages = new BlockingCollection<JsonDocument>(boundedCapacity: 256);

        child.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdoutLines.Enqueue(e.Data);
            // Best-effort: try to parse as a JSON-RPC frame. Anything that doesn't parse
            // is logged for the report but does NOT block the test from making progress.
            try
            {
                var doc = JsonDocument.Parse(e.Data);
                stdoutMessages.Add(doc);
            }
            catch (JsonException)
            {
                // We'll catch this in the post-run validation.
            }
        };
        child.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderrLines.Enqueue(e.Data);
        };

        if (!child.Start())
        {
            Console.Error.WriteLine("FAIL — could not start child process");
            return 1;
        }
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

        var failures = 0;

        try
        {
            // -------- Initialize --------
            var initId = Interlocked.Increment(ref _nextRequestId);
            var initReq = new
            {
                jsonrpc = "2.0",
                id = initId,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { },
                    clientInfo = new { name = "spike-c-harness", version = "0.0.1" },
                },
            };
            await SendAsync(child, initReq);
            var initResp = await WaitForResponseAsync(stdoutMessages, initId, TimeSpan.FromSeconds(10));
            if (initResp is null)
            {
                Console.Error.WriteLine("FAIL — no response to initialize within 10s");
                failures++;
            }
            else
            {
                var (ok, why) = ValidateInitializeResponse(initResp.RootElement, initId);
                if (ok)
                {
                    Console.WriteLine($"PASS — initialize handshake (server: {ServerInfoString(initResp.RootElement)})");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — initialize response invalid: {why}");
                    failures++;
                }
                initResp.Dispose();
            }

            // -------- Initialized notification --------
            var initialized = new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized",
                @params = new { },
            };
            await SendAsync(child, initialized);
            // No response expected for notifications.

            // -------- tools/list --------
            var listId = Interlocked.Increment(ref _nextRequestId);
            var listReq = new { jsonrpc = "2.0", id = listId, method = "tools/list", @params = new { } };
            await SendAsync(child, listReq);
            var listResp = await WaitForResponseAsync(stdoutMessages, listId, TimeSpan.FromSeconds(10));
            if (listResp is null)
            {
                Console.Error.WriteLine("FAIL — no response to tools/list within 10s");
                failures++;
            }
            else
            {
                var found = false;
                if (listResp.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("tools", out var tools) &&
                    tools.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tool in tools.EnumerateArray())
                    {
                        if (tool.TryGetProperty("name", out var nameProp) &&
                            nameProp.GetString() == "marionette_ping")
                        {
                            found = true;
                            break;
                        }
                    }
                }
                if (found)
                {
                    Console.WriteLine("PASS — tools/list contains marionette_ping");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — tools/list does not contain marionette_ping. Raw: {listResp.RootElement.GetRawText()}");
                    failures++;
                }
                listResp.Dispose();
            }

            // -------- tools/call marionette_ping --------
            var callId = Interlocked.Increment(ref _nextRequestId);
            var callReq = new
            {
                jsonrpc = "2.0",
                id = callId,
                method = "tools/call",
                @params = new
                {
                    name = "marionette_ping",
                    arguments = new { },
                },
            };
            await SendAsync(child, callReq);
            var callResp = await WaitForResponseAsync(stdoutMessages, callId, TimeSpan.FromSeconds(10));
            if (callResp is null)
            {
                Console.Error.WriteLine("FAIL — no response to tools/call within 10s");
                failures++;
            }
            else
            {
                if (TryFindPongInToolCallResult(callResp.RootElement, out var pongFound))
                {
                    Console.WriteLine("PASS — tools/call marionette_ping returned \"pong\"");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — tools/call marionette_ping did not return \"pong\". Raw: {callResp.RootElement.GetRawText()}; pongFound={pongFound}");
                    failures++;
                }
                callResp.Dispose();
            }
        }
        finally
        {
            // Clean shutdown: close stdin so the SDK's stdio host loop sees EOF and exits.
            // In GUI mode the WPF Application keeps the process alive even after the MCP
            // host loop ends, so we give it a shorter window and force-kill on timeout —
            // that's expected behaviour, not a failure.
            try { child.StandardInput.Close(); } catch { /* ignore */ }
            var exitWait = guiMode ? 2000 : 5000;
            if (!child.WaitForExit(exitWait))
            {
                if (guiMode)
                {
                    Console.WriteLine("INFO — GUI-mode child still alive after MCP shutdown (expected; killing).");
                    try { child.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    child.WaitForExit(2000);
                }
                else
                {
                    Console.Error.WriteLine("WARN — child did not exit within 5s after stdin close. Killing.");
                    try { child.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    child.WaitForExit(2000);
                    failures++;
                }
            }
            else
            {
                Console.WriteLine($"PASS — child exited cleanly with code {child.ExitCode}");
            }
        }

        // -------- Validate stdout purity --------
        Console.WriteLine();
        Console.WriteLine("=== Captured stdout ===");
        var stdoutCorrupt = 0;
        var stdoutClean = 0;
        foreach (var line in stdoutLines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                stdoutClean++;
                Console.WriteLine($"  [JSON-RPC ok] {Truncate(line, 200)}");
            }
            catch (JsonException ex)
            {
                stdoutCorrupt++;
                Console.Error.WriteLine($"  [POLLUTION] {line}  ({ex.Message})");
            }
        }
        Console.WriteLine($"stdout summary: {stdoutClean} JSON-RPC frames, {stdoutCorrupt} pollution lines");
        if (stdoutCorrupt > 0)
        {
            Console.Error.WriteLine("FAIL — stdout pollution detected");
            failures++;
        }

        // -------- Stderr summary --------
        Console.WriteLine();
        Console.WriteLine("=== Captured stderr (first 50 lines) ===");
        var i = 0;
        foreach (var line in stderrLines)
        {
            if (i++ < 50) Console.WriteLine($"  {line}");
        }
        Console.WriteLine($"stderr total: {stderrLines.Count} lines");

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("=== Spike C handshake: PASS ===");
            return 0;
        }
        Console.Error.WriteLine($"=== Spike C handshake: FAIL — {failures} failure(s) ===");
        return 1;
    }

    private static async Task SendAsync(Process child, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        // Newline-delimited JSON is the wire format the SDK's stdio transport speaks.
        await child.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
        await child.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<JsonDocument?> WaitForResponseAsync(BlockingCollection<JsonDocument> queue, int expectedId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            try
            {
                if (queue.TryTake(out var doc, (int)Math.Min(remaining.TotalMilliseconds, 200)))
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
                    }
                    // Notification or unrelated response — ignore for this wait.
                    doc.Dispose();
                }
            }
            catch (InvalidOperationException)
            {
                // Queue completed.
                return null;
            }
            await Task.Yield();
        }
        return null;
    }

    private static (bool, string) ValidateInitializeResponse(JsonElement root, int expectedId)
    {
        if (!root.TryGetProperty("jsonrpc", out var jr) || jr.GetString() != "2.0")
            return (false, "missing or wrong jsonrpc");
        if (!root.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var id) || id != expectedId)
            return (false, $"id mismatch (expected {expectedId})");
        if (!root.TryGetProperty("result", out var result))
            return (false, "no result field");
        if (!result.TryGetProperty("protocolVersion", out _))
            return (false, "result missing protocolVersion");
        if (!result.TryGetProperty("serverInfo", out var info) || !info.TryGetProperty("name", out _))
            return (false, "result missing serverInfo.name");
        return (true, string.Empty);
    }

    private static string ServerInfoString(JsonElement root)
    {
        if (root.TryGetProperty("result", out var result) &&
            result.TryGetProperty("serverInfo", out var info))
        {
            var name = info.TryGetProperty("name", out var n) ? n.GetString() : "?";
            var version = info.TryGetProperty("version", out var v) ? v.GetString() : "?";
            var proto = result.TryGetProperty("protocolVersion", out var p) ? p.GetString() : "?";
            return $"{name} {version}, protocol {proto}";
        }
        return "?";
    }

    private static bool TryFindPongInToolCallResult(JsonElement root, out string note)
    {
        note = string.Empty;
        if (!root.TryGetProperty("result", out var result))
        {
            note = "no result";
            return false;
        }
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            note = "no content array";
            return false;
        }
        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var text) && text.GetString() == "pong")
            {
                return true;
            }
        }
        // Fallback: structuredContent for tools that opted into UseStructuredContent.
        if (result.TryGetProperty("structuredContent", out var sc) &&
            sc.TryGetProperty("result", out var srRes) &&
            srRes.GetString() == "pong")
        {
            return true;
        }
        note = "pong not found in content[]";
        return false;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";
}
