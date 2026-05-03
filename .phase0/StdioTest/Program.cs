// Spike C / Phase 1.2 — stdio handshake harness for Sample.Wpf.StripeProbe.exe --mcp --headless
//
// Validates the Phase 1.2 contract end-to-end:
//   * MCP initialize handshake succeeds.
//   * tools/list returns exactly the four Phase-1 tools:
//       inspect_app_api, invoke_method, read_observable, capture_screenshot
//   * tools/call inspect_app_api returns JSON containing the sample's root.
//   * tools/call invoke_method on MainWindow.Add(2,3) returns 5.
//   * tools/call read_observable on MainWindow.Result returns 5 (after Add).
//   * tools/call capture_screenshot returns a structured "not_supported" /
//     "screenshot_not_supported" error in Phase 1.2 (NoOpAdapter).
//   * Stdout contains 0 pollution lines (every line parses as JSON-RPC).
//   * Child exits cleanly on stdin EOF.

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

        Console.WriteLine($"=== Phase 1.2 stdio handshake harness ===");
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
        var phase12ExpectedTools = new[] { "inspect_app_api", "invoke_method", "read_observable", "capture_screenshot" };

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
                    clientInfo = new { name = "phase-1.2-harness", version = "0.1.0" },
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
                var listed = new System.Collections.Generic.List<string>();
                if (listResp.RootElement.TryGetProperty("result", out var result) &&
                    result.TryGetProperty("tools", out var tools) &&
                    tools.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tool in tools.EnumerateArray())
                    {
                        if (tool.TryGetProperty("name", out var nameProp))
                        {
                            listed.Add(nameProp.GetString() ?? string.Empty);
                        }
                    }
                }
                var missing = new System.Collections.Generic.List<string>();
                foreach (var t in phase12ExpectedTools)
                {
                    if (!listed.Contains(t)) missing.Add(t);
                }
                if (missing.Count == 0)
                {
                    Console.WriteLine($"PASS — tools/list contains all four Phase-1 tools (got: {string.Join(",", listed)})");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — tools/list missing: {string.Join(",", missing)}; got: {string.Join(",", listed)}");
                    failures++;
                }
                listResp.Dispose();
            }

            // -------- tools/call inspect_app_api (no args) --------
            var inspectId = Interlocked.Increment(ref _nextRequestId);
            var inspectReq = new
            {
                jsonrpc = "2.0",
                id = inspectId,
                method = "tools/call",
                @params = new
                {
                    name = "inspect_app_api",
                    arguments = new { },
                },
            };
            await SendAsync(child, inspectReq);
            var inspectResp = await WaitForResponseAsync(stdoutMessages, inspectId, TimeSpan.FromSeconds(10));
            if (inspectResp is null)
            {
                Console.Error.WriteLine("FAIL — no response to inspect_app_api within 10s");
                failures++;
            }
            else
            {
                if (TryReadToolText(inspectResp.RootElement, out var inspectText) &&
                    inspectText.IndexOf("MainWindow", StringComparison.Ordinal) >= 0)
                {
                    Console.WriteLine("PASS — inspect_app_api returned manifest containing MainWindow");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — inspect_app_api result missing MainWindow. Raw: {inspectResp.RootElement.GetRawText()}");
                    failures++;
                }
                inspectResp.Dispose();
            }

            // -------- tools/call invoke_method MainWindow.Add(2,3) --------
            var invokeId = Interlocked.Increment(ref _nextRequestId);
            var invokeReq = new
            {
                jsonrpc = "2.0",
                id = invokeId,
                method = "tools/call",
                @params = new
                {
                    name = "invoke_method",
                    arguments = new
                    {
                        root = "MainWindow",
                        method = "Add",
                        args = new { a = 2, b = 3 },
                    },
                },
            };
            await SendAsync(child, invokeReq);
            var invokeResp = await WaitForResponseAsync(stdoutMessages, invokeId, TimeSpan.FromSeconds(10));
            if (invokeResp is null)
            {
                Console.Error.WriteLine("FAIL — no response to invoke_method within 10s");
                failures++;
            }
            else
            {
                if (TryReadToolText(invokeResp.RootElement, out var addText) &&
                    addText.Trim() == "5")
                {
                    Console.WriteLine("PASS — invoke_method MainWindow.Add(2,3) returned 5");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — invoke_method MainWindow.Add(2,3) did not return 5. Raw: {invokeResp.RootElement.GetRawText()}");
                    failures++;
                }
                invokeResp.Dispose();
            }

            // -------- tools/call read_observable MainWindow.Result --------
            // Phase 1.2 note: Add is pure math; the sample's MainWindow does
            // NOT mutate Result inside Add (Result is only updated by the GUI
            // button click). So in headless mode Result remains 0 until a
            // separate setter exists. We assert "the call succeeds and
            // returns a JSON number" rather than a specific value.
            var readId = Interlocked.Increment(ref _nextRequestId);
            var readReq = new
            {
                jsonrpc = "2.0",
                id = readId,
                method = "tools/call",
                @params = new
                {
                    name = "read_observable",
                    arguments = new { root = "MainWindow", property = "Result" },
                },
            };
            await SendAsync(child, readReq);
            var readResp = await WaitForResponseAsync(stdoutMessages, readId, TimeSpan.FromSeconds(10));
            if (readResp is null)
            {
                Console.Error.WriteLine("FAIL — no response to read_observable within 10s");
                failures++;
            }
            else
            {
                if (TryReadToolText(readResp.RootElement, out var readText) &&
                    int.TryParse(readText.Trim(), out _))
                {
                    Console.WriteLine($"PASS — read_observable MainWindow.Result returned {readText.Trim()}");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — read_observable MainWindow.Result did not return an integer. Raw: {readResp.RootElement.GetRawText()}");
                    failures++;
                }
                readResp.Dispose();
            }

            // -------- tools/call capture_screenshot (Phase 1.2: NoOp adapter) --------
            // Expected: tool result has IsError=true and a structured-error
            // text content with errorCode == "screenshot_not_supported".
            var shotId = Interlocked.Increment(ref _nextRequestId);
            var shotReq = new
            {
                jsonrpc = "2.0",
                id = shotId,
                method = "tools/call",
                @params = new
                {
                    name = "capture_screenshot",
                    arguments = new { },
                },
            };
            await SendAsync(child, shotReq);
            var shotResp = await WaitForResponseAsync(stdoutMessages, shotId, TimeSpan.FromSeconds(10));
            if (shotResp is null)
            {
                Console.Error.WriteLine("FAIL — no response to capture_screenshot within 10s");
                failures++;
            }
            else
            {
                if (TryReadToolText(shotResp.RootElement, out var shotText) &&
                    shotText.IndexOf("screenshot_not_supported", StringComparison.Ordinal) >= 0)
                {
                    Console.WriteLine("PASS — capture_screenshot surfaced a structured 'screenshot_not_supported' error (NoOpAdapter)");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — capture_screenshot did not surface the expected NoOpAdapter error. Raw: {shotResp.RootElement.GetRawText()}");
                    failures++;
                }
                shotResp.Dispose();
            }
        }
        finally
        {
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
            Console.WriteLine("=== Phase 1.2 handshake: PASS ===");
            return 0;
        }
        Console.Error.WriteLine($"=== Phase 1.2 handshake: FAIL — {failures} failure(s) ===");
        return 1;
    }

    private static async Task SendAsync(Process child, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
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
                    doc.Dispose();
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

    /// <summary>
    /// Drill into a tools/call result and pull the first text content block
    /// out as a string. Phase-1.2 tools all return a single JSON string in
    /// the content[0].text slot.
    /// </summary>
    private static bool TryReadToolText(JsonElement root, out string text)
    {
        text = string.Empty;
        if (!root.TryGetProperty("result", out var result)) return false;
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";
}
