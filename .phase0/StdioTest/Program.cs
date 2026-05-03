// Spike C / Phase 1.2 / 1.3 — stdio handshake harness for Sample.Wpf.StripeProbe.exe
//
// Validates the contract end-to-end across two modes:
//
//   * Default (no flag):       child runs `--mcp --headless` (no GUI / no Dispatcher).
//                              Exercises the manifest + meta-tools surface; the
//                              capture_screenshot tool must surface the
//                              documented `screenshot_not_supported` structured
//                              error (NoOpAdapter path).
//
//   * `--gui`:                 child runs `--mcp` (WPF GUI alongside the MCP server).
//                              Same handshake AND additionally asserts that
//                              capture_screenshot returns a valid PNG-encoded
//                              ImageContentBlock. The captured image is saved
//                              to `.phase1/screenshot-test.png` for human
//                              sanity-checking. This path requires an
//                              interactive desktop session — it WILL NOT work
//                              on a headless CI runner. The harness force-kills
//                              the GUI child on completion (Spike C taught us
//                              `Application.Run` keeps the process alive).
//
// Runs covered by the harness:
//   * MCP initialize handshake succeeds.
//   * tools/list returns exactly the four Phase-1 tools:
//       inspect_app_api, invoke_method, read_observable, capture_screenshot
//   * tools/call inspect_app_api returns JSON containing the sample's root.
//   * tools/call invoke_method on MainWindow.Add(2,3) returns 5.
//   * tools/call read_observable on MainWindow.Result returns an integer.
//   * tools/call capture_screenshot:
//       - default mode: structured `screenshot_not_supported` error
//       - --gui    mode: image/png ContentBlock, base64 decodes to a valid
//                        PNG (magic 89 50 4E 47 0D 0A 1A 0A).
//   * Stdout contains 0 pollution lines (every line parses as JSON-RPC).
//   * Child exits cleanly on stdin EOF (default mode) or is force-killed
//     (--gui mode).

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
            Console.Error.WriteLine("Usage: StdioTest <path-to-Sample.Wpf.StripeProbe.exe> [--gui] [--probe]");
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

        var phaseLabel = guiMode ? "Phase 1.3 stdio + GUI screenshot harness" : "Phase 1.2 stdio handshake harness";
        Console.WriteLine($"=== {phaseLabel} ===");
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
                    clientInfo = new { name = "phase-1.3-harness", version = "0.1.0" },
                },
            };
            await SendAsync(child, initReq);
            // GUI startup is slower than headless because WPF has to spin up
            // the Dispatcher + first render before the host's stdio loop
            // ackowledges the init request. Give it more headroom.
            var initTimeout = guiMode ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(10);
            var initResp = await WaitForResponseAsync(stdoutMessages, initId, initTimeout);
            if (initResp is null)
            {
                Console.Error.WriteLine($"FAIL — no response to initialize within {initTimeout.TotalSeconds:F0}s");
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
            // Note: Add is pure math; the sample's MainWindow does NOT mutate
            // Result inside Add (Result is only updated by the GUI button
            // click). In headless mode Result remains 0; in GUI mode Result
            // ALSO stays 0 unless the user clicks the button. We assert "the
            // call succeeds and returns a JSON number" rather than a specific
            // value.
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

            // -------- tools/call capture_screenshot --------
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
            var shotResp = await WaitForResponseAsync(stdoutMessages, shotId, TimeSpan.FromSeconds(15));
            if (shotResp is null)
            {
                Console.Error.WriteLine("FAIL — no response to capture_screenshot within 15s");
                failures++;
            }
            else if (guiMode)
            {
                // GUI mode: expect a real PNG-encoded ImageContentBlock.
                if (!TryReadToolImage(shotResp.RootElement, out var imageMimeType, out var imageBase64, out var imageError))
                {
                    Console.Error.WriteLine($"FAIL — capture_screenshot did not return a valid image content block. Reason: {imageError}. Raw: {shotResp.RootElement.GetRawText()}");
                    failures++;
                }
                else if (imageMimeType != "image/png")
                {
                    Console.Error.WriteLine($"FAIL — capture_screenshot mimeType was '{imageMimeType}', expected 'image/png'.");
                    failures++;
                }
                else
                {
                    byte[] imageBytes;
                    try { imageBytes = Convert.FromBase64String(imageBase64); }
                    catch (FormatException ex)
                    {
                        Console.Error.WriteLine($"FAIL — capture_screenshot base64 was malformed: {ex.Message}.");
                        imageBytes = Array.Empty<byte>();
                        failures++;
                    }

                    if (imageBytes.Length == 0)
                    {
                        Console.Error.WriteLine("FAIL — capture_screenshot returned a zero-length image.");
                        failures++;
                    }
                    else if (!HasPngMagic(imageBytes))
                    {
                        Console.Error.WriteLine($"FAIL — capture_screenshot bytes do not start with the PNG magic header. First 16 bytes: {Hex(imageBytes, 16)}.");
                        failures++;
                    }
                    else
                    {
                        // Save the captured screenshot for human sanity-check.
                        var outPath = ResolveScreenshotOutPath();
                        try
                        {
                            File.WriteAllBytes(outPath, imageBytes);
                            Console.WriteLine($"PASS — capture_screenshot returned a valid PNG ({imageBytes.Length} bytes, mimeType={imageMimeType}). Saved to {outPath}.");
                        }
                        catch (Exception ex)
                        {
                            // Validation passed but we couldn't write the
                            // sanity-check file; keep the PASS but note it.
                            Console.WriteLine($"PASS — capture_screenshot returned a valid PNG ({imageBytes.Length} bytes, mimeType={imageMimeType}). [could not save to {outPath}: {ex.Message}]");
                        }
                    }
                }
            }
            else
            {
                // Headless mode: expect the documented NoOpAdapter error.
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
            }
            shotResp?.Dispose();
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
        var verdictLabel = guiMode ? "Phase 1.3 GUI handshake" : "Phase 1.2 handshake";
        if (failures == 0)
        {
            Console.WriteLine($"=== {verdictLabel}: PASS ===");
            return 0;
        }
        Console.Error.WriteLine($"=== {verdictLabel}: FAIL — {failures} failure(s) ===");
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

    /// <summary>
    /// Drill into a tools/call result and pull the first image content block.
    /// Asserts content[0].type == "image" and pulls .mimeType / .data.
    /// </summary>
    private static bool TryReadToolImage(JsonElement root, out string mimeType, out string base64, out string reason)
    {
        mimeType = string.Empty;
        base64 = string.Empty;
        reason = string.Empty;

        if (!root.TryGetProperty("result", out var result)) { reason = "no result"; return false; }
        // Phase 1.3 capture_screenshot returns IsError=false implicitly. If
        // an error block leaked into a `--gui` run we want a useful failure
        // reason rather than "no image."
        if (result.TryGetProperty("isError", out var err) && err.ValueKind == JsonValueKind.True)
        {
            if (TryReadToolText(root, out var errText))
            {
                reason = $"isError=true with content text: {errText}";
            }
            else
            {
                reason = "isError=true";
            }
            return false;
        }
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            reason = "result has no content array";
            return false;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "image") continue;
            if (item.TryGetProperty("mimeType", out var mt)) mimeType = mt.GetString() ?? string.Empty;
            if (item.TryGetProperty("data", out var data)) base64 = data.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(base64)) { reason = "image content block had no data field"; return false; }
            return true;
        }
        reason = "no content[].type == image entry";
        return false;
    }

    private static bool HasPngMagic(byte[] bytes)
    {
        // PNG file signature: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length < 8) return false;
        return bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
    }

    private static string Hex(byte[] bytes, int max)
    {
        var n = Math.Min(bytes.Length, max);
        var sb = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.AppendFormat("{0:X2}", bytes[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resolve where to drop the captured PNG. Walks up from the harness's
    /// current directory looking for a `.phase1` folder; falls back to the
    /// CWD if not found. Always relative — keeps test output near the repo.
    /// </summary>
    private static string ResolveScreenshotOutPath()
    {
        var cwd = Directory.GetCurrentDirectory();
        var probe = cwd;
        for (var i = 0; i < 10 && probe is not null; i++)
        {
            var candidate = Path.Combine(probe, ".phase1");
            if (Directory.Exists(candidate))
            {
                return Path.Combine(candidate, "screenshot-test.png");
            }
            probe = Path.GetDirectoryName(probe);
        }
        return Path.Combine(cwd, "screenshot-test.png");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";
}
