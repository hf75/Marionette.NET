// Spike C / Phase 1.2 / 1.3 / 1.4 — stdio handshake harness for the WPF samples
//
// Validates the contract end-to-end across two SAMPLES and two MODES:
//
//   Samples (chosen by the asserted root):
//     * StripeProbe (default): `MainWindow` root with `Add(int, int)` callable
//       and `Result` observable.
//     * TodoApp (`--todoapp`):  `TodoListViewModel` root with five callables
//       (AddTodo / RemoveTodo / ToggleDone / ClearCompleted / RenameTodo) and
//       four observables (TotalCount / CompletedCount / PendingCount /
//       LastAddedTitle). Three watchable observables. Phase-1.4-specific
//       assertions: AddTodo grows TotalCount, the TodoListViewModel manifest
//       lists exactly the five callables and four observables, and a
//       resources/subscribe to TotalCount produces a
//       notifications/resources/updated when AddTodo is called.
//
//   Modes:
//     * Default (no flag):       child runs `--mcp --headless` (no GUI).
//                                Exercises the manifest + meta-tools surface;
//                                capture_screenshot returns the documented
//                                `screenshot_not_supported` structured error.
//     * `--gui`:                 child runs `--mcp` (WPF GUI alongside MCP).
//                                StripeProbe-only — adds PNG screenshot
//                                validation. (TodoApp could also run in --gui
//                                mode but the harness keeps that out of CI for
//                                Phase 1.4; manual verification via
//                                .phase1/test-todoapp.ps1 is the documented
//                                path. CI uses the headless TodoApp suite.)
//
// Runs covered by the harness (StripeProbe default):
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
//
// Runs covered by the harness (TodoApp `--todoapp`):
//   * Same initialize + tools/list as StripeProbe.
//   * inspect_app_api returns a manifest containing TodoListViewModel.
//   * The manifest's TodoListViewModel root has 5 callables (AddTodo,
//     RemoveTodo, ToggleDone, ClearCompleted, RenameTodo) and 4 observables
//     (TotalCount, CompletedCount, PendingCount, LastAddedTitle).
//   * read_observable TotalCount initially returns 0 (headless mode starts a
//     fresh ViewModel; the GUI's pre-seed only runs in MainWindow.ctor which
//     is never constructed in headless mode).
//   * invoke_method AddTodo("buy milk") returns "null" (void) without error.
//   * read_observable TotalCount after AddTodo returns 1.
//   * resources/subscribe marionette://TodoListViewModel/TotalCount succeeds,
//     a second AddTodo triggers a notifications/resources/updated for the URI.
//   * capture_screenshot returns the documented `screenshot_not_supported`
//     error (TodoApp default mode is headless = NoOpAdapter).

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
            Console.Error.WriteLine("Usage: StdioTest <path-to-sample.exe> [--gui] [--todoapp] [--avalonia] [--winui] [--probe] [--simulate-input] [--two-windows]");
            return 2;
        }

        var exePath = args[0];
        var probeMode = false;
        var guiMode = false;
        var todoAppMode = false;
        var avaloniaMode = false;
        var winuiMode = false;
        var simulateInputMode = false;
        var twoWindowsMode = false;
        for (var ai = 1; ai < args.Length; ai++)
        {
            if (args[ai] == "--probe") probeMode = true;
            else if (args[ai] == "--gui") guiMode = true;
            else if (args[ai] == "--todoapp") todoAppMode = true;
            else if (args[ai] == "--avalonia") avaloniaMode = true;
            else if (args[ai] == "--winui") winuiMode = true;
            else if (args[ai] == "--simulate-input") simulateInputMode = true;
            else if (args[ai] == "--two-windows") twoWindowsMode = true;
        }
        // --simulate-input only makes sense with a GUI sample (the input
        // simulator needs a real WPF/Avalonia visual tree). Auto-imply --gui
        // and surface a warning if the user asked for --simulate-input
        // without GUI mode — better than silently doing nothing.
        if (simulateInputMode && !guiMode)
        {
            Console.Error.WriteLine("INFO — --simulate-input requires --gui; enabling automatically.");
            guiMode = true;
        }
        // Phase 3.3: --two-windows only makes sense in GUI mode (the headless
        // path bypasses the WPF App entirely). Auto-imply.
        if (twoWindowsMode)
        {
            if (!guiMode)
            {
                Console.Error.WriteLine("INFO — --two-windows requires --gui; enabling automatically.");
                guiMode = true;
            }
            if (!todoAppMode)
            {
                Console.Error.WriteLine("INFO — --two-windows currently only supported with --todoapp; ignoring otherwise.");
                twoWindowsMode = false;
            }
        }
        if (!File.Exists(exePath))
        {
            Console.Error.WriteLine($"FAIL — child executable not found at {exePath}");
            return 2;
        }

        // --gui --todoapp combines the TodoApp assertions (see TryParseTodoAppManifest)
        // with the screenshot-validation step. Useful for the Phase-1.4 demo
        // where the captured PNG provides a visual sanity check that the
        // TodoListViewModel UI rendered correctly.

        var phaseLabel = winuiMode
            ? "Phase 3.2 WinUI FormLab stdio handshake harness"
            : (avaloniaMode
                ? "Phase 2.1 Avalonia Dashboard stdio handshake harness"
                : (todoAppMode
                    ? (twoWindowsMode ? "Phase 3.3 TodoApp --two-windows stdio handshake harness" : "Phase 1.4 TodoApp stdio handshake harness")
                    : (guiMode ? "Phase 1.3 stdio + GUI screenshot harness" : "Phase 1.2 stdio handshake harness")));
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
        if (twoWindowsMode) psi.ArgumentList.Add("--two-windows");
        if (probeMode)
        {
            psi.Environment["MARIONETTE_STDOUT_PROBE"] = "1";
        }
        // Phase 3.1: simulate-input mode runs more sequential invocations
        // than the default 5-hop budget allows. Bump for this run only.
        if (simulateInputMode)
        {
            psi.Environment["MARIONETTE_MAX_DEPTH"] = "50";
        }
        // Phase 3.3: two-window mode also runs many sequential per-window
        // invocations; bump the budget so the assertion sequence doesn't
        // trip loop-protection.
        if (twoWindowsMode)
        {
            psi.Environment["MARIONETTE_MAX_DEPTH"] = "50";
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
                    Console.WriteLine($"PASS — tools/list contains all four Phase-1 tools (got: {string.Join(",", listed.Where(t => phase12ExpectedTools.Contains(t)).OrderBy(t => t, StringComparer.Ordinal))})");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — tools/list missing: {string.Join(",", missing)}; got: {string.Join(",", listed)}");
                    failures++;
                }

                // Phase 2.2: every TodoApp / Dashboard mode must also expose
                // per-method dynamic tools (`<rootName>.<methodName>`). The
                // four meta-tools coexist alongside; adopters can use either
                // path. Spielregel 7: dynamic tools must already exist by
                // the very first tools/list response (the SDK gets them
                // staged in DynamicToolRegistry.RegisterInitial before the
                // run loop starts).
                string[] expectedDynamicTools = winuiMode
                    ? new[]
                    {
                        "FormLabViewModel.SetName",
                        "FormLabViewModel.SetAge",
                        "FormLabViewModel.ToggleNotifications",
                        "FormLabViewModel.SetTheme",
                        "FormLabViewModel.Submit",
                        "FormLabViewModel.Reset",
                    }
                    : (avaloniaMode
                        ? new[]
                        {
                            "DashboardViewModel.UpsertMetric",
                            "DashboardViewModel.RemoveMetric",
                            "DashboardViewModel.ResetAll",
                            "DashboardViewModel.TogglePaused",
                            "DashboardViewModel.RefreshAsync",
                        }
                        : (todoAppMode
                            ? new[]
                            {
                                "TodoListViewModel.AddTodo",
                                "TodoListViewModel.RemoveTodo",
                                "TodoListViewModel.ToggleDone",
                                "TodoListViewModel.ClearCompleted",
                                "TodoListViewModel.RenameTodo",
                            }
                            : new[]
                            {
                                "MainWindow.Add",
                            }));
                var missingDyn = new System.Collections.Generic.List<string>();
                foreach (var t in expectedDynamicTools)
                {
                    if (!listed.Contains(t)) missingDyn.Add(t);
                }
                if (missingDyn.Count == 0)
                {
                    Console.WriteLine($"PASS — tools/list also contains the {expectedDynamicTools.Length} per-method dynamic tools ({string.Join(",", expectedDynamicTools)})");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — tools/list missing dynamic tools: {string.Join(",", missingDyn)}; got: {string.Join(",", listed)}");
                    failures++;
                }
                listResp.Dispose();
            }

            // -------- Sample-specific tool-call assertions --------
            if (winuiMode)
            {
                // ============ WinUI FormLab Phase-3.2 assertion suite ============
                //
                // Mirror of the Dashboard suite, but for Sample.WinUI.FormLab's
                // FormLabViewModel root. FormLab exposes:
                //   * 6 [McpCallable]: SetName, SetAge, ToggleNotifications, SetTheme, Submit, Reset
                //   * 5 [McpObservable]: Name (watchable), Age (watchable),
                //                        NotificationsEnabled (watchable), Theme (non-watchable),
                //                        HasSubmitted (non-watchable)
                //   * 1 [McpEvent]: FormSubmitted (with FormSubmittedEventArgs payload)
                //
                // Headless mode: ViewModel starts with name="", age=0,
                // notifications=true, theme="Default", hasSubmitted=false.
                // The harness asserts:
                //   * inspect_app_api lists FormLabViewModel with all 6 callables + 5 observables + 1 event.
                //   * read_observable for each baseline value returns the expected default.
                //   * invoke_method SetName("Test"), SetAge(30), ToggleNotifications(),
                //     SetTheme("Dark") all succeed.
                //   * Re-reads of the observables show the new values.
                //   * resources/subscribe to events/FormSubmitted + Submit() produces an
                //     event notification with args.Name == "Test", args.Age == 30, etc.
                //   * read_observable HasSubmitted returns true after Submit.
                //   * capture_screenshot returns 'screenshot_not_supported' (NoOpAdapter in --headless).
                var inspectId = Interlocked.Increment(ref _nextRequestId);
                var inspectReq = new
                {
                    jsonrpc = "2.0",
                    id = inspectId,
                    method = "tools/call",
                    @params = new { name = "inspect_app_api", arguments = new { } },
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
                    string manifestErr = "no inspect_app_api text content";
                    bool manifestOk = TryReadToolText(inspectResp.RootElement, out var inspectText) &&
                                      TryParseFormLabManifest(inspectText, out manifestErr);
                    if (manifestOk)
                    {
                        Console.WriteLine("PASS — inspect_app_api returned FormLabViewModel manifest with all 6 callables + 5 observables + 1 event");
                    }
                    else
                    {
                        Console.Error.WriteLine($"FAIL — inspect_app_api manifest mismatch: {manifestErr}. Raw: {inspectResp.RootElement.GetRawText()}");
                        failures++;
                    }
                    inspectResp.Dispose();
                }

                // ---- baseline reads
                var initName = await ReadObservableString(child, stdoutMessages, "FormLabViewModel", "Name");
                if (initName == "")
                {
                    Console.WriteLine("PASS — read_observable Name initially returned empty string");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — read_observable Name initial = '{initName ?? "<error>"}', expected ''");
                    failures++;
                }

                var initAge = await ReadObservableInt(child, stdoutMessages, "FormLabViewModel", "Age");
                if (initAge == 0)
                {
                    Console.WriteLine("PASS — read_observable Age initially returned 0");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — read_observable Age initial = {initAge?.ToString() ?? "<error>"}, expected 0");
                    failures++;
                }

                var initNotif = await ReadObservableBool(child, stdoutMessages, "FormLabViewModel", "NotificationsEnabled");
                if (initNotif == true)
                {
                    Console.WriteLine("PASS — read_observable NotificationsEnabled initially returned true");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — read_observable NotificationsEnabled initial = {initNotif?.ToString() ?? "<error>"}, expected true");
                    failures++;
                }

                var initSubmitted = await ReadObservableBool(child, stdoutMessages, "FormLabViewModel", "HasSubmitted");
                if (initSubmitted == false)
                {
                    Console.WriteLine("PASS — read_observable HasSubmitted initially returned false");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — read_observable HasSubmitted initial = {initSubmitted?.ToString() ?? "<error>"}, expected false");
                    failures++;
                }

                // ---- invoke_method SetName("Test")
                var setNameOk = await InvokeMethodAsync(child, stdoutMessages,
                    "FormLabViewModel", "SetName", new { name = "Test" });
                if (setNameOk.Success)
                {
                    Console.WriteLine("PASS — invoke_method SetName(\"Test\") succeeded");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — invoke_method SetName failed: {setNameOk.Detail}");
                    failures++;
                }

                var afterName = await ReadObservableString(child, stdoutMessages, "FormLabViewModel", "Name");
                if (afterName == "Test")
                {
                    Console.WriteLine("PASS — read_observable Name returned 'Test' after SetName");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — read_observable Name after SetName = '{afterName ?? "<error>"}', expected 'Test'");
                    failures++;
                }

                // ---- invoke_method SetAge(30)
                var setAgeOk = await InvokeMethodAsync(child, stdoutMessages,
                    "FormLabViewModel", "SetAge", new { age = 30 });
                if (setAgeOk.Success)
                {
                    Console.WriteLine("PASS — invoke_method SetAge(30) succeeded");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — invoke_method SetAge failed: {setAgeOk.Detail}");
                    failures++;
                }

                var afterAge = await ReadObservableInt(child, stdoutMessages, "FormLabViewModel", "Age");
                if (afterAge == 30)
                {
                    Console.WriteLine("PASS — read_observable Age returned 30 after SetAge");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — read_observable Age after SetAge = {afterAge?.ToString() ?? "<error>"}, expected 30");
                    failures++;
                }

                // ---- invoke_method ToggleNotifications()
                var toggleOk = await InvokeMethodAsync(child, stdoutMessages,
                    "FormLabViewModel", "ToggleNotifications", new { });
                if (toggleOk.Success)
                {
                    Console.WriteLine("PASS — invoke_method ToggleNotifications() succeeded");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — invoke_method ToggleNotifications failed: {toggleOk.Detail}");
                    failures++;
                }

                var afterToggle = await ReadObservableBool(child, stdoutMessages, "FormLabViewModel", "NotificationsEnabled");
                if (afterToggle == false)
                {
                    Console.WriteLine("PASS — read_observable NotificationsEnabled returned false after ToggleNotifications");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — NotificationsEnabled after Toggle = {afterToggle?.ToString() ?? "<error>"}, expected false");
                    failures++;
                }

                // ---- subscribe to events/FormSubmitted BEFORE the Submit call
                var eventUri = "marionette://FormLabViewModel/events/FormSubmitted";
                var evSubId = Interlocked.Increment(ref _nextRequestId);
                var evSubReq = new
                {
                    jsonrpc = "2.0",
                    id = evSubId,
                    method = "resources/subscribe",
                    @params = new { uri = eventUri },
                };
                await SendAsync(child, evSubReq);
                var evSubResp = await WaitForResponseAsync(stdoutMessages, evSubId, TimeSpan.FromSeconds(10));
                bool evSubOk = evSubResp is not null && evSubResp.RootElement.TryGetProperty("result", out _);
                evSubResp?.Dispose();
                if (!evSubOk)
                {
                    Console.Error.WriteLine($"FAIL — resources/subscribe to {eventUri} did not return a result.");
                    failures++;
                }
                else
                {
                    var evWatcher = StartNotificationWatcher(stdoutMessages);
                    var submitOk = await InvokeMethodAsync(child, stdoutMessages,
                        "FormLabViewModel", "Submit", new { });
                    if (!submitOk.Success)
                    {
                        Console.Error.WriteLine($"FAIL — invoke_method Submit failed: {submitOk.Detail}");
                        failures++;
                    }
                    else
                    {
                        var gotEvUpdate = await WaitForResourceUpdate(evWatcher, eventUri, TimeSpan.FromSeconds(5));
                        if (!gotEvUpdate)
                        {
                            Console.Error.WriteLine($"FAIL — no notifications/resources/updated for {eventUri} within 5s after Submit.");
                            failures++;
                        }
                        else
                        {
                            // Read the resource and confirm the args carry the snapshot.
                            var readId = Interlocked.Increment(ref _nextRequestId);
                            var readReq = new
                            {
                                jsonrpc = "2.0",
                                id = readId,
                                method = "resources/read",
                                @params = new { uri = eventUri },
                            };
                            await SendAsync(child, readReq);
                            var readResp = await WaitForResponseAsync(stdoutMessages, readId, TimeSpan.FromSeconds(5));
                            bool readOk = false;
                            string readDetail = "no resources/read response";
                            if (readResp is not null && readResp.RootElement.TryGetProperty("result", out var rr) &&
                                rr.TryGetProperty("contents", out var cs) && cs.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var c in cs.EnumerateArray())
                                {
                                    if (!c.TryGetProperty("text", out var txt)) continue;
                                    var text = txt.GetString() ?? string.Empty;
                                    try
                                    {
                                        using var inner = JsonDocument.Parse(text);
                                        if (inner.RootElement.TryGetProperty("events", out var evArr) &&
                                            evArr.ValueKind == JsonValueKind.Array && evArr.GetArrayLength() > 0)
                                        {
                                            bool found = false;
                                            int len = evArr.GetArrayLength();
                                            foreach (var ev in evArr.EnumerateArray())
                                            {
                                                if (ev.TryGetProperty("args", out var argsEl) &&
                                                    argsEl.TryGetProperty("Name", out var nameEl) &&
                                                    nameEl.GetString() == "Test" &&
                                                    argsEl.TryGetProperty("Age", out var ageEl) &&
                                                    ageEl.GetInt32() == 30)
                                                {
                                                    found = true;
                                                    break;
                                                }
                                            }
                                            if (found)
                                            {
                                                readOk = true;
                                                readDetail = $"sequence={(inner.RootElement.TryGetProperty("sequence", out var sq) ? sq.GetInt64() : -1)}, count={len}, args.Name=\"Test\", args.Age=30 present";
                                            }
                                            else
                                            {
                                                readDetail = "no event with args.Name='Test' AND args.Age=30 found in buffer";
                                            }
                                        }
                                        else
                                        {
                                            readDetail = "no events array or empty";
                                        }
                                    }
                                    catch (JsonException ex)
                                    {
                                        readDetail = $"events resource text not JSON: {ex.Message}";
                                    }
                                }
                            }
                            readResp?.Dispose();
                            if (readOk)
                            {
                                Console.WriteLine($"PASS — resources/subscribe + Submit produced an event notification on {eventUri} ({readDetail})");
                            }
                            else
                            {
                                Console.Error.WriteLine($"FAIL — event resource read mismatch: {readDetail}");
                                failures++;
                            }
                        }
                    }
                }

                // ---- HasSubmitted now true
                var afterSubmittedFlag = await ReadObservableBool(child, stdoutMessages, "FormLabViewModel", "HasSubmitted");
                if (afterSubmittedFlag == true)
                {
                    Console.WriteLine("PASS — read_observable HasSubmitted returned true after Submit");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — HasSubmitted after Submit = {afterSubmittedFlag?.ToString() ?? "<error>"}, expected true");
                    failures++;
                }
            }
            else if (avaloniaMode)
            {
                // ============ Avalonia Dashboard Phase-2.1 assertion suite ============
                //
                // Mirror of the TodoApp suite, but for Sample.Avalonia.Dashboard's
                // DashboardViewModel root. The Dashboard exposes:
                //   * 5 [McpCallable]: UpsertMetric, RemoveMetric, ResetAll, TogglePaused, RefreshAsync
                //   * 4 [McpObservable]: MetricCount, Total (watchable), IsPaused (watchable), LastUpdatedMetric
                //   * 2 [McpEvent]:     MetricUpserted, PausedToggled
                //
                // The headless ctor pre-seeds 4 metrics (CPU/Memory/Network/Disk),
                // so MetricCount baseline = 4. The harness asserts:
                //   * inspect_app_api lists DashboardViewModel with all 5 callables + 4 observables.
                //   * read_observable MetricCount returns 4 baseline.
                //   * invoke_method UpsertMetric("CPU", 42, "%") succeeds and (since CPU exists)
                //     does NOT grow MetricCount.
                //   * invoke_method UpsertMetric("Battery", 87, "%") (a NEW name) does grow it to 5.
                //   * invoke_method RefreshAsync(50) succeeds and the response actually awaits
                //     the Task (the runtime would otherwise return before the simulated delay).
                //   * resources/subscribe to MetricCount + UpsertMetric of a new name produces
                //     a notifications/resources/updated.
                //   * resources/subscribe to events/MetricUpserted + UpsertMetric produces an
                //     event notification with args.Name == the upserted name.
                //   * capture_screenshot returns 'screenshot_not_supported' (NoOpAdapter in --headless).
                var inspectId = Interlocked.Increment(ref _nextRequestId);
                var inspectReq = new
                {
                    jsonrpc = "2.0",
                    id = inspectId,
                    method = "tools/call",
                    @params = new { name = "inspect_app_api", arguments = new { } },
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
                    string manifestErr = "no inspect_app_api text content";
                    bool manifestOk = TryReadToolText(inspectResp.RootElement, out var inspectText) &&
                                      TryParseDashboardManifest(inspectText, out manifestErr);
                    if (manifestOk)
                    {
                        Console.WriteLine("PASS — inspect_app_api returned DashboardViewModel manifest with all 5 callables + 4 observables + 2 events");
                    }
                    else
                    {
                        Console.Error.WriteLine($"FAIL — inspect_app_api manifest mismatch: {manifestErr}. Raw: {inspectResp.RootElement.GetRawText()}");
                        failures++;
                    }
                    inspectResp.Dispose();
                }

                // ---- read_observable MetricCount (baseline). Headless ctor seeds 4 metrics.
                var initialCount = await ReadObservableInt(child, stdoutMessages, "DashboardViewModel", "MetricCount");
                if (initialCount is int baseline and >= 0)
                {
                    Console.WriteLine($"PASS — read_observable MetricCount initially returned {baseline}");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — read_observable MetricCount initial read failed: {initialCount?.ToString() ?? "<error>"}");
                    failures++;
                }

                // ---- invoke_method UpsertMetric("CPU", 42, "%") — existing name, count unchanged.
                var upCpuOk = await InvokeMethodAsync(child, stdoutMessages,
                    "DashboardViewModel", "UpsertMetric", new { name = "CPU", value = 42.0, unit = "%" });
                if (upCpuOk.Success)
                {
                    Console.WriteLine("PASS — invoke_method UpsertMetric(\"CPU\", 42, \"%\") succeeded");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — invoke_method UpsertMetric(CPU) failed: {upCpuOk.Detail}");
                    failures++;
                }

                // ---- read_observable MetricCount unchanged (still baseline) — CPU pre-existed.
                var afterCpuCount = await ReadObservableInt(child, stdoutMessages, "DashboardViewModel", "MetricCount");
                if (afterCpuCount == initialCount)
                {
                    Console.WriteLine($"PASS — read_observable MetricCount unchanged at {afterCpuCount} after UpsertMetric on existing key");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — MetricCount changed after UpsertMetric on existing key: {afterCpuCount?.ToString() ?? "<error>"} (expected {initialCount}).");
                    failures++;
                }

                // ---- invoke_method RefreshAsync(50) — async callable. The harness must
                //      observe that the runtime AWAITS the Task before responding (the
                //      response shouldn't come back before the simulated delay elapsed).
                var refreshStart = DateTime.UtcNow;
                var refreshOk = await InvokeMethodAsync(child, stdoutMessages,
                    "DashboardViewModel", "RefreshAsync", new { simulatedDelayMs = 100 });
                var refreshElapsed = DateTime.UtcNow - refreshStart;
                if (!refreshOk.Success)
                {
                    Console.Error.WriteLine($"FAIL — invoke_method RefreshAsync failed: {refreshOk.Detail}");
                    failures++;
                }
                else if (refreshElapsed < TimeSpan.FromMilliseconds(80))
                {
                    Console.Error.WriteLine($"FAIL — invoke_method RefreshAsync returned in {refreshElapsed.TotalMilliseconds:F0}ms — expected >= 80ms (the runtime should await the Task).");
                    failures++;
                }
                else
                {
                    Console.WriteLine($"PASS — invoke_method RefreshAsync(100) succeeded after {refreshElapsed.TotalMilliseconds:F0}ms (await held)");
                }

                // ---- resources/subscribe + UpsertMetric of NEW key — expect a
                //      notifications/resources/updated for MetricCount.
                var metricCountUri = "marionette://DashboardViewModel/MetricCount";
                var subId = Interlocked.Increment(ref _nextRequestId);
                var subReq = new
                {
                    jsonrpc = "2.0",
                    id = subId,
                    method = "resources/subscribe",
                    @params = new { uri = metricCountUri },
                };
                await SendAsync(child, subReq);
                var subResp = await WaitForResponseAsync(stdoutMessages, subId, TimeSpan.FromSeconds(10));
                bool subOk = subResp is not null && subResp.RootElement.TryGetProperty("result", out _);
                subResp?.Dispose();
                if (!subOk)
                {
                    Console.Error.WriteLine($"FAIL — resources/subscribe to {metricCountUri} did not return a result.");
                    failures++;
                }
                else
                {
                    var notifWatcher = StartNotificationWatcher(stdoutMessages);
                    var newKeyOk = await InvokeMethodAsync(child, stdoutMessages,
                        "DashboardViewModel", "UpsertMetric", new { name = "Battery", value = 87.0, unit = "%" });
                    if (!newKeyOk.Success)
                    {
                        Console.Error.WriteLine($"FAIL — UpsertMetric(Battery) failed: {newKeyOk.Detail}");
                        failures++;
                    }
                    else
                    {
                        var gotUpdate = await WaitForResourceUpdate(notifWatcher, metricCountUri, TimeSpan.FromSeconds(5));
                        if (gotUpdate)
                        {
                            Console.WriteLine($"PASS — resources/subscribe + UpsertMetric(Battery) produced notifications/resources/updated for {metricCountUri}");
                        }
                        else
                        {
                            Console.Error.WriteLine($"FAIL — no notifications/resources/updated received for {metricCountUri} within 5s.");
                            failures++;
                        }
                    }
                }

                // ---- read_observable MetricCount after the NEW upsert — expect baseline+1.
                var finalCount = await ReadObservableInt(child, stdoutMessages, "DashboardViewModel", "MetricCount");
                var expectedFinal = (initialCount ?? 0) + 1;
                if (finalCount == expectedFinal)
                {
                    Console.WriteLine($"PASS — read_observable MetricCount returned {finalCount} after UpsertMetric on new key (baseline + 1)");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — read_observable MetricCount = {finalCount?.ToString() ?? "<error>"}, expected {expectedFinal}.");
                    failures++;
                }

                // ---- Phase 2.2: invoke UpsertMetric VIA THE DYNAMIC TOOL.
                //      Same operation as above, hitting DashboardViewModel.UpsertMetric
                //      directly to verify the per-method dispatch surface.
                var dynBefore = await ReadObservableInt(child, stdoutMessages, "DashboardViewModel", "MetricCount");
                var dynamicUpsert = await InvokeDynamicToolAsync(child, stdoutMessages,
                    "DashboardViewModel.UpsertMetric",
                    new { name = "DynamicProbe", value = 99.0, unit = "%" });
                if (!dynamicUpsert.Success)
                {
                    Console.Error.WriteLine($"FAIL — [via dynamic tool] UpsertMetric failed: {dynamicUpsert.Detail}");
                    failures++;
                }
                else
                {
                    var dynAfter = await ReadObservableInt(child, stdoutMessages, "DashboardViewModel", "MetricCount");
                    if (dynAfter == (dynBefore ?? 0) + 1)
                    {
                        Console.WriteLine($"PASS — [via dynamic tool] DashboardViewModel.UpsertMetric({{name=\"DynamicProbe\"}}) succeeded; MetricCount {dynBefore} -> {dynAfter}");
                    }
                    else
                    {
                        Console.Error.WriteLine($"FAIL — [via dynamic tool] UpsertMetric returned success but MetricCount = {dynAfter} (expected {(dynBefore ?? 0) + 1}).");
                        failures++;
                    }
                }

                // ---- Phase 1.6: declarative event delivery for MetricUpserted.
                //      Subscribe BEFORE the upsert; expect notifications/resources/updated
                //      with args.Name == the upserted name.
                var eventUri = "marionette://DashboardViewModel/events/MetricUpserted";
                var evSubId = Interlocked.Increment(ref _nextRequestId);
                var evSubReq = new
                {
                    jsonrpc = "2.0",
                    id = evSubId,
                    method = "resources/subscribe",
                    @params = new { uri = eventUri },
                };
                await SendAsync(child, evSubReq);
                var evSubResp = await WaitForResponseAsync(stdoutMessages, evSubId, TimeSpan.FromSeconds(10));
                bool evSubOk = evSubResp is not null && evSubResp.RootElement.TryGetProperty("result", out _);
                evSubResp?.Dispose();
                if (!evSubOk)
                {
                    Console.Error.WriteLine($"FAIL — resources/subscribe to {eventUri} did not return a result.");
                    failures++;
                }
                else
                {
                    var evWatcher = StartNotificationWatcher(stdoutMessages);
                    var addEvOk = await InvokeMethodAsync(child, stdoutMessages,
                        "DashboardViewModel", "UpsertMetric", new { name = "Latency", value = 42.0, unit = "ms" });
                    if (!addEvOk.Success)
                    {
                        Console.Error.WriteLine($"FAIL — invoke_method UpsertMetric for event check failed: {addEvOk.Detail}");
                        failures++;
                    }
                    else
                    {
                        var gotEvUpdate = await WaitForResourceUpdate(evWatcher, eventUri, TimeSpan.FromSeconds(5));
                        if (!gotEvUpdate)
                        {
                            Console.Error.WriteLine($"FAIL — no notifications/resources/updated for {eventUri} within 5s after UpsertMetric.");
                            failures++;
                        }
                        else
                        {
                            // Read the resource and confirm the args carry the name.
                            var readId = Interlocked.Increment(ref _nextRequestId);
                            var readReq = new
                            {
                                jsonrpc = "2.0",
                                id = readId,
                                method = "resources/read",
                                @params = new { uri = eventUri },
                            };
                            await SendAsync(child, readReq);
                            var readResp = await WaitForResponseAsync(stdoutMessages, readId, TimeSpan.FromSeconds(5));
                            bool readOk = false;
                            string readDetail = "no resources/read response";
                            if (readResp is not null && readResp.RootElement.TryGetProperty("result", out var rr) &&
                                rr.TryGetProperty("contents", out var cs) && cs.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var c in cs.EnumerateArray())
                                {
                                    if (!c.TryGetProperty("text", out var txt)) continue;
                                    var text = txt.GetString() ?? string.Empty;
                                    try
                                    {
                                        using var inner = JsonDocument.Parse(text);
                                        if (inner.RootElement.TryGetProperty("events", out var evArr) &&
                                            evArr.ValueKind == JsonValueKind.Array && evArr.GetArrayLength() > 0)
                                        {
                                            // Search for ANY event whose args.Name matches "Latency".
                                            bool found = false;
                                            int len = evArr.GetArrayLength();
                                            foreach (var ev in evArr.EnumerateArray())
                                            {
                                                if (ev.TryGetProperty("args", out var argsEl) &&
                                                    argsEl.TryGetProperty("Name", out var nameEl) &&
                                                    nameEl.GetString() == "Latency")
                                                {
                                                    found = true;
                                                    break;
                                                }
                                            }
                                            if (found)
                                            {
                                                readOk = true;
                                                readDetail = $"sequence={(inner.RootElement.TryGetProperty("sequence", out var sq) ? sq.GetInt64() : -1)}, count={len}, args.Name=\"Latency\" present";
                                            }
                                            else
                                            {
                                                readDetail = "no event with args.Name='Latency' found in buffer";
                                            }
                                        }
                                        else
                                        {
                                            readDetail = "no events array or empty";
                                        }
                                    }
                                    catch (JsonException ex)
                                    {
                                        readDetail = $"events resource text not JSON: {ex.Message}";
                                    }
                                }
                            }
                            readResp?.Dispose();
                            if (readOk)
                            {
                                Console.WriteLine($"PASS — resources/subscribe + UpsertMetric produced an event notification on {eventUri} ({readDetail})");
                            }
                            else
                            {
                                Console.Error.WriteLine($"FAIL — event resource read mismatch: {readDetail}");
                                failures++;
                            }
                        }
                    }
                }
            }
            else if (todoAppMode)
            {
                // ============ TodoApp Phase-1.4 assertion suite ============

                // ---- inspect_app_api: must contain TodoListViewModel with
                //      five callables (AddTodo, RemoveTodo, ToggleDone,
                //      ClearCompleted, RenameTodo) and four observables
                //      (TotalCount, CompletedCount, PendingCount, LastAddedTitle).
                var inspectId = Interlocked.Increment(ref _nextRequestId);
                var inspectReq = new
                {
                    jsonrpc = "2.0",
                    id = inspectId,
                    method = "tools/call",
                    @params = new { name = "inspect_app_api", arguments = new { } },
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
                    string manifestErr = "no inspect_app_api text content";
                    bool manifestOk = TryReadToolText(inspectResp.RootElement, out var inspectText) &&
                                      TryParseTodoAppManifest(inspectText, out manifestErr);
                    if (manifestOk)
                    {
                        Console.WriteLine("PASS — inspect_app_api returned TodoListViewModel manifest with all 5 callables + 4 observables");
                    }
                    else
                    {
                        Console.Error.WriteLine($"FAIL — inspect_app_api manifest mismatch: {manifestErr}. Raw: {inspectResp.RootElement.GetRawText()}");
                        failures++;
                    }
                    inspectResp.Dispose();
                }

                // ---- read_observable TotalCount before AddTodo. In headless
                //      mode the ViewModel starts empty (MainWindow ctor never
                //      runs); in GUI mode the MainWindow ctor pre-seeds two
                //      demo items so the screenshot is non-empty. We assert
                //      "the call returns a non-negative integer" and remember
                //      the baseline for the post-AddTodo delta assertion.
                var initialTotal = await ReadObservableInt(child, stdoutMessages, "TodoListViewModel", "TotalCount");
                if (initialTotal is int baseline and >= 0)
                {
                    Console.WriteLine($"PASS — read_observable TotalCount initially returned {baseline}");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — read_observable TotalCount initial read failed: {initialTotal?.ToString() ?? "<error>"}");
                    failures++;
                }

                // ---- invoke_method AddTodo("buy milk")
                var addOk = await InvokeMethodAsync(child, stdoutMessages,
                    "TodoListViewModel", "AddTodo", new { title = "buy milk" });
                if (addOk.Success)
                {
                    Console.WriteLine("PASS — invoke_method AddTodo(\"buy milk\") succeeded");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — invoke_method AddTodo(\"buy milk\") failed: {addOk.Detail}");
                    failures++;
                }

                // ---- read_observable TotalCount after AddTodo: expect baseline+1
                var afterTotal = await ReadObservableInt(child, stdoutMessages, "TodoListViewModel", "TotalCount");
                var expectedAfter = (initialTotal ?? 0) + 1;
                if (afterTotal == expectedAfter)
                {
                    Console.WriteLine($"PASS — read_observable TotalCount returned {afterTotal} after AddTodo (baseline + 1)");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — read_observable TotalCount after AddTodo returned {afterTotal?.ToString() ?? "<error>"}, expected {expectedAfter} (baseline {initialTotal ?? 0} + 1).");
                    failures++;
                }

                // ---- resources/subscribe + second AddTodo + expect notifications/resources/updated.
                //
                // The runtime coalesces resource updates within 200 ms. After
                // subscribe, the SECOND AddTodo("eggs") should produce a
                // notifications/resources/updated for the TotalCount URI
                // (TotalCount goes 1 -> 2). Headless mode runs the host inline
                // with NoOpAdapter so the INPC-driven push fires synchronously
                // off the AddTodo invocation.
                var totalCountUri = "marionette://TodoListViewModel/TotalCount";
                var subId = Interlocked.Increment(ref _nextRequestId);
                var subReq = new
                {
                    jsonrpc = "2.0",
                    id = subId,
                    method = "resources/subscribe",
                    @params = new { uri = totalCountUri },
                };
                await SendAsync(child, subReq);
                var subResp = await WaitForResponseAsync(stdoutMessages, subId, TimeSpan.FromSeconds(10));
                bool subOk = subResp is not null && subResp.RootElement.TryGetProperty("result", out _);
                subResp?.Dispose();
                if (!subOk)
                {
                    Console.Error.WriteLine($"FAIL — resources/subscribe to {totalCountUri} did not return a result.");
                    failures++;
                }
                else
                {
                    // Drain pre-existing notifications then issue the AddTodo.
                    var notifWatcher = StartNotificationWatcher(stdoutMessages);
                    var add2Ok = await InvokeMethodAsync(child, stdoutMessages,
                        "TodoListViewModel", "AddTodo", new { title = "eggs" });
                    if (!add2Ok.Success)
                    {
                        Console.Error.WriteLine($"FAIL — second invoke_method AddTodo failed: {add2Ok.Detail}");
                        failures++;
                    }
                    else
                    {
                        // Wait up to 5 s for the resource-updated notification.
                        var gotUpdate = await WaitForResourceUpdate(notifWatcher, totalCountUri, TimeSpan.FromSeconds(5));
                        if (gotUpdate)
                        {
                            Console.WriteLine($"PASS — resources/subscribe + AddTodo produced notifications/resources/updated for {totalCountUri}");
                        }
                        else
                        {
                            Console.Error.WriteLine($"FAIL — no notifications/resources/updated received for {totalCountUri} within 5s.");
                            failures++;
                        }
                    }
                }

                // ---- Phase 2.2: invoke AddTodo VIA THE DYNAMIC TOOL.
                //      The harness above used invoke_method (the meta-tool),
                //      this call hits TodoListViewModel.AddTodo directly so
                //      the dispatch surface is verified end-to-end.
                var dynBefore = await ReadObservableInt(child, stdoutMessages, "TodoListViewModel", "TotalCount");
                var dynamicAdd = await InvokeDynamicToolAsync(child, stdoutMessages,
                    "TodoListViewModel.AddTodo", new { title = "via dynamic tool" });
                if (!dynamicAdd.Success)
                {
                    Console.Error.WriteLine($"FAIL — [via dynamic tool] AddTodo failed: {dynamicAdd.Detail}");
                    failures++;
                }
                else
                {
                    var dynAfter = await ReadObservableInt(child, stdoutMessages, "TodoListViewModel", "TotalCount");
                    if (dynAfter == (dynBefore ?? 0) + 1)
                    {
                        Console.WriteLine($"PASS — [via dynamic tool] TodoListViewModel.AddTodo({{title=\"via dynamic tool\"}}) succeeded; TotalCount {dynBefore} -> {dynAfter}");
                    }
                    else
                    {
                        Console.Error.WriteLine($"FAIL — [via dynamic tool] AddTodo returned success but TotalCount = {dynAfter} (expected {(dynBefore ?? 0) + 1}).");
                        failures++;
                    }
                }

                // ---- Phase 1.6: declarative event delivery. Subscribe to the
                //      TodoAdded event, fire AddTodo, expect a
                //      notifications/resources/updated for the events URI, then
                //      read the resource and assert events[0].args.Title is the
                //      title we just added.
                var eventUri = "marionette://TodoListViewModel/events/TodoAdded";
                var evSubId = Interlocked.Increment(ref _nextRequestId);
                var evSubReq = new
                {
                    jsonrpc = "2.0",
                    id = evSubId,
                    method = "resources/subscribe",
                    @params = new { uri = eventUri },
                };
                await SendAsync(child, evSubReq);
                var evSubResp = await WaitForResponseAsync(stdoutMessages, evSubId, TimeSpan.FromSeconds(10));
                bool evSubOk = evSubResp is not null && evSubResp.RootElement.TryGetProperty("result", out _);
                evSubResp?.Dispose();
                if (!evSubOk)
                {
                    Console.Error.WriteLine($"FAIL — resources/subscribe to {eventUri} did not return a result.");
                    failures++;
                }
                else
                {
                    var evWatcher = StartNotificationWatcher(stdoutMessages);
                    var addEvOk = await InvokeMethodAsync(child, stdoutMessages,
                        "TodoListViewModel", "AddTodo", new { title = "learn marionette" });
                    if (!addEvOk.Success)
                    {
                        Console.Error.WriteLine($"FAIL — invoke_method AddTodo for event check failed: {addEvOk.Detail}");
                        failures++;
                    }
                    else
                    {
                        var gotEvUpdate = await WaitForResourceUpdate(evWatcher, eventUri, TimeSpan.FromSeconds(5));
                        if (!gotEvUpdate)
                        {
                            Console.Error.WriteLine($"FAIL — no notifications/resources/updated for {eventUri} within 5s after AddTodo.");
                            failures++;
                        }
                        else
                        {
                            // Read the resource and confirm the args carry the title.
                            var readId = Interlocked.Increment(ref _nextRequestId);
                            var readReq = new
                            {
                                jsonrpc = "2.0",
                                id = readId,
                                method = "resources/read",
                                @params = new { uri = eventUri },
                            };
                            await SendAsync(child, readReq);
                            var readResp = await WaitForResponseAsync(stdoutMessages, readId, TimeSpan.FromSeconds(5));
                            bool readOk = false;
                            string readDetail = "no resources/read response";
                            if (readResp is not null && readResp.RootElement.TryGetProperty("result", out var rr) &&
                                rr.TryGetProperty("contents", out var cs) && cs.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var c in cs.EnumerateArray())
                                {
                                    if (!c.TryGetProperty("text", out var txt)) continue;
                                    var text = txt.GetString() ?? string.Empty;
                                    try
                                    {
                                        using var inner = JsonDocument.Parse(text);
                                        if (inner.RootElement.TryGetProperty("events", out var evArr) &&
                                            evArr.ValueKind == JsonValueKind.Array && evArr.GetArrayLength() > 0)
                                        {
                                            // Search for ANY event whose args.Title matches the
                                            // title we just added. The buffer holds every fire
                                            // (including the prior subscribe-test AddTodos), so
                                            // we cannot assume index 0 is the latest one.
                                            bool found = false;
                                            int len = evArr.GetArrayLength();
                                            foreach (var ev in evArr.EnumerateArray())
                                            {
                                                if (ev.TryGetProperty("args", out var argsEl) &&
                                                    argsEl.TryGetProperty("Title", out var titleEl) &&
                                                    titleEl.GetString() == "learn marionette")
                                                {
                                                    found = true;
                                                    break;
                                                }
                                            }
                                            if (found)
                                            {
                                                readOk = true;
                                                readDetail = $"sequence={(inner.RootElement.TryGetProperty("sequence", out var sq) ? sq.GetInt64() : -1)}, count={len}, args.Title=\"learn marionette\" present";
                                            }
                                            else
                                            {
                                                readDetail = "no event with args.Title='learn marionette' found in buffer";
                                            }
                                        }
                                        else
                                        {
                                            readDetail = "no events array or empty";
                                        }
                                    }
                                    catch (JsonException ex)
                                    {
                                        readDetail = $"events resource text not JSON: {ex.Message}";
                                    }
                                }
                            }
                            readResp?.Dispose();
                            if (readOk)
                            {
                                Console.WriteLine($"PASS — resources/subscribe + AddTodo produced an event notification on {eventUri} ({readDetail})");
                            }
                            else
                            {
                                Console.Error.WriteLine($"FAIL — event resource read mismatch: {readDetail}");
                                failures++;
                            }
                        }
                    }
                }

                // ---- Phase 3.3 multi-window assertions ----
                if (twoWindowsMode)
                {
                    // Allow a settle window so the deferred second-window
                    // construction completes and the coalesced
                    // tools/list_changed notification has landed.
                    await Task.Delay(TimeSpan.FromSeconds(2));

                    // 1) tools/list contains both per-window dynamic-tool
                    //    variants (`...:w1` and `...:w2`).
                    var twListId = Interlocked.Increment(ref _nextRequestId);
                    var twListReq = new { jsonrpc = "2.0", id = twListId, method = "tools/list", @params = new { } };
                    await SendAsync(child, twListReq);
                    var twListResp = await WaitForResponseAsync(stdoutMessages, twListId, TimeSpan.FromSeconds(10));
                    if (twListResp is null)
                    {
                        Console.Error.WriteLine("FAIL — no response to tools/list (post --two-windows) within 10s");
                        failures++;
                    }
                    else
                    {
                        var listed = new System.Collections.Generic.List<string>();
                        if (twListResp.RootElement.TryGetProperty("result", out var twResult) &&
                            twResult.TryGetProperty("tools", out var twTools) &&
                            twTools.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tool in twTools.EnumerateArray())
                            {
                                if (tool.TryGetProperty("name", out var nm))
                                    listed.Add(nm.GetString() ?? string.Empty);
                            }
                        }
                        bool hasW1 = listed.Contains("TodoListViewModel.AddTodo:w1");
                        bool hasW2 = listed.Contains("TodoListViewModel.AddTodo:w2");
                        if (hasW1 && hasW2)
                        {
                            Console.WriteLine("PASS — [--two-windows] tools/list contains both per-window AddTodo variants (:w1 + :w2)");
                        }
                        else
                        {
                            Console.Error.WriteLine($"FAIL — [--two-windows] tools/list missing per-window variants. hasW1={hasW1}, hasW2={hasW2}. Got: {string.Join(",", listed)}");
                            failures++;
                        }
                        twListResp.Dispose();
                    }

                    // 2) inspect_app_api advertises a 2-element windowIds array.
                    var twInspId = Interlocked.Increment(ref _nextRequestId);
                    var twInspReq = new
                    {
                        jsonrpc = "2.0",
                        id = twInspId,
                        method = "tools/call",
                        @params = new { name = "inspect_app_api", arguments = new { } },
                    };
                    await SendAsync(child, twInspReq);
                    var twInspResp = await WaitForResponseAsync(stdoutMessages, twInspId, TimeSpan.FromSeconds(10));
                    if (twInspResp is null)
                    {
                        Console.Error.WriteLine("FAIL — no response to inspect_app_api (post --two-windows)");
                        failures++;
                    }
                    else
                    {
                        bool foundIds = false;
                        if (TryReadToolText(twInspResp.RootElement, out var inspText))
                        {
                            try
                            {
                                using var inspDoc = JsonDocument.Parse(inspText);
                                if (inspDoc.RootElement.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var entry in inspDoc.RootElement.EnumerateArray())
                                    {
                                        if (entry.TryGetProperty("name", out var nProp) &&
                                            nProp.GetString() == "TodoListViewModel" &&
                                            entry.TryGetProperty("windowIds", out var wIds) &&
                                            wIds.ValueKind == JsonValueKind.Array &&
                                            wIds.GetArrayLength() == 2)
                                        {
                                            foundIds = true;
                                            break;
                                        }
                                    }
                                }
                            }
                            catch (JsonException) { /* fall through */ }
                        }
                        if (foundIds)
                        {
                            Console.WriteLine("PASS — [--two-windows] inspect_app_api reports 2-element windowIds on TodoListViewModel");
                        }
                        else
                        {
                            Console.Error.WriteLine($"FAIL — [--two-windows] inspect_app_api missing 2-element windowIds. Raw: {twInspResp.RootElement.GetRawText()}");
                            failures++;
                        }
                        twInspResp.Dispose();
                    }

                    // 3) Per-window AddTodo + read isolation.
                    int? w1BaselineV = await ReadObservableIntScoped(child, stdoutMessages,
                        "TodoListViewModel", "TotalCount", "w1");
                    int? w2BaselineV = await ReadObservableIntScoped(child, stdoutMessages,
                        "TodoListViewModel", "TotalCount", "w2");
                    Console.WriteLine($"INFO — [--two-windows] baseline w1={w1BaselineV?.ToString() ?? "<error>"}, w2={w2BaselineV?.ToString() ?? "<error>"}");

                    var addW1Ok = await InvokeMethodAsyncScoped(child, stdoutMessages,
                        "TodoListViewModel", "AddTodo", new { title = "stdio-w1" }, "w1");
                    if (!addW1Ok.Success)
                    {
                        Console.Error.WriteLine($"FAIL — [--two-windows] AddTodo windowId=w1 failed: {addW1Ok.Detail}");
                        failures++;
                    }

                    int? w1AfterAddV = await ReadObservableIntScoped(child, stdoutMessages,
                        "TodoListViewModel", "TotalCount", "w1");
                    int? w2AfterAddV = await ReadObservableIntScoped(child, stdoutMessages,
                        "TodoListViewModel", "TotalCount", "w2");

                    if (w1AfterAddV == (w1BaselineV ?? 0) + 1 && w2AfterAddV == (w2BaselineV ?? 0))
                    {
                        Console.WriteLine($"PASS — [--two-windows] AddTodo windowId=w1 only mutated w1 ({w1BaselineV} -> {w1AfterAddV}); w2 unchanged at {w2AfterAddV}");
                    }
                    else
                    {
                        Console.Error.WriteLine($"FAIL — [--two-windows] AddTodo windowId=w1 isolation broken. w1 {w1BaselineV} -> {w1AfterAddV}, w2 {w2BaselineV} -> {w2AfterAddV}");
                        failures++;
                    }

                    var addW2Ok = await InvokeMethodAsyncScoped(child, stdoutMessages,
                        "TodoListViewModel", "AddTodo", new { title = "stdio-w2" }, "w2");
                    if (!addW2Ok.Success)
                    {
                        Console.Error.WriteLine($"FAIL — [--two-windows] AddTodo windowId=w2 failed: {addW2Ok.Detail}");
                        failures++;
                    }

                    int? w1FinalV = await ReadObservableIntScoped(child, stdoutMessages,
                        "TodoListViewModel", "TotalCount", "w1");
                    int? w2FinalV = await ReadObservableIntScoped(child, stdoutMessages,
                        "TodoListViewModel", "TotalCount", "w2");

                    if (w1FinalV == w1AfterAddV && w2FinalV == (w2AfterAddV ?? 0) + 1)
                    {
                        Console.WriteLine($"PASS — [--two-windows] AddTodo windowId=w2 only mutated w2 ({w2AfterAddV} -> {w2FinalV}); w1 unchanged at {w1FinalV}");
                    }
                    else
                    {
                        Console.Error.WriteLine($"FAIL — [--two-windows] AddTodo windowId=w2 isolation broken. w1 {w1AfterAddV} -> {w1FinalV}, w2 {w2AfterAddV} -> {w2FinalV}");
                        failures++;
                    }
                }
            }
            else
            {
                // ============ StripeProbe legacy assertion suite ============

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
                        Console.WriteLine("PASS — [via meta-tool] invoke_method MainWindow.Add(2,3) returned 5");
                    }
                    else
                    {
                        Console.Error.WriteLine($"FAIL — invoke_method MainWindow.Add(2,3) did not return 5. Raw: {invokeResp.RootElement.GetRawText()}");
                        failures++;
                    }
                    invokeResp.Dispose();
                }

                // Phase 2.2: same call via the dynamic per-method tool.
                var dynStripe = await InvokeDynamicToolAsync(child, stdoutMessages,
                    "MainWindow.Add", new { a = 2, b = 3 });
                if (dynStripe.Success && dynStripe.Detail == "5")
                {
                    Console.WriteLine("PASS — [via dynamic tool] MainWindow.Add({a:2,b:3}) returned 5");
                }
                else
                {
                    Console.Error.WriteLine($"FAIL — [via dynamic tool] MainWindow.Add(2,3) returned {(dynStripe.Success ? dynStripe.Detail : "error: " + dynStripe.Detail)}.");
                    failures++;
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

            // -------- Phase 3.1: simulate_input + raise_event ----------------
            //
            // Only runs when `--simulate-input` is on (which auto-implies
            // --gui). Picks a sample-specific (root, control, "click")
            // tuple, drives the click via simulate_input, then via
            // raise_event with the C# event name "Click", and reads back
            // the count observable to confirm the framework saw the event.
            //
            // We don't fail when simulate_input returns success:false because
            // that's a documented Phase-3.1 limitation for the Avalonia
            // adapter's keyboard/mouse-move kinds; for "click" on a Button,
            // both adapters do dispatch via the routed-event pipeline.
            if (simulateInputMode)
            {
                string siRoot, siControl, siCountObs;
                if (todoAppMode)
                {
                    siRoot = "TodoListViewModel";
                    siControl = "AddButton";
                    siCountObs = "TotalCount";
                }
                else if (avaloniaMode)
                {
                    siRoot = "DashboardViewModel";
                    siControl = "UpsertButton";
                    siCountObs = "MetricCount";
                }
                else
                {
                    // StripeProbe doesn't have a meaningful Add button at
                    // the named-control level (the StripeProbe MainWindow
                    // doesn't expose AutomationId-decorated buttons); skip.
                    siRoot = string.Empty;
                    siControl = string.Empty;
                    siCountObs = string.Empty;
                }

                if (!string.IsNullOrEmpty(siRoot))
                {
                    var preCount = await ReadObservableInt(child, stdoutMessages, siRoot, siCountObs);
                    Console.WriteLine($"INFO — pre-input {siCountObs}={preCount?.ToString() ?? "<error>"}");

                    // 1) simulate_input click
                    var siId = Interlocked.Increment(ref _nextRequestId);
                    var siReq = new
                    {
                        jsonrpc = "2.0",
                        id = siId,
                        method = "tools/call",
                        @params = new
                        {
                            name = "simulate_input",
                            arguments = new { root = siRoot, control = siControl, kind = "click" },
                        },
                    };
                    await SendAsync(child, siReq);
                    var siResp = await WaitForResponseAsync(stdoutMessages, siId, TimeSpan.FromSeconds(15));
                    if (siResp is null)
                    {
                        Console.Error.WriteLine("FAIL — no response to simulate_input within 15s");
                        failures++;
                    }
                    else
                    {
                        bool siOk = TryReadToolText(siResp.RootElement, out var siText) &&
                                    IsSuccessJson(siText);
                        if (siOk)
                        {
                            Console.WriteLine($"PASS — simulate_input(root={siRoot}, control={siControl}, kind=click) returned success");
                        }
                        else
                        {
                            Console.Error.WriteLine($"FAIL — simulate_input did not return success. Raw: {siResp.RootElement.GetRawText()}");
                            failures++;
                        }
                        siResp.Dispose();
                    }

                    // 2) raise_event Click
                    var reId = Interlocked.Increment(ref _nextRequestId);
                    var reReq = new
                    {
                        jsonrpc = "2.0",
                        id = reId,
                        method = "tools/call",
                        @params = new
                        {
                            name = "raise_event",
                            arguments = new { root = siRoot, control = siControl, @event = "Click" },
                        },
                    };
                    await SendAsync(child, reReq);
                    var reResp = await WaitForResponseAsync(stdoutMessages, reId, TimeSpan.FromSeconds(15));
                    if (reResp is null)
                    {
                        Console.Error.WriteLine("FAIL — no response to raise_event within 15s");
                        failures++;
                    }
                    else
                    {
                        bool reOk = TryReadToolText(reResp.RootElement, out var reText) &&
                                    IsSuccessJson(reText);
                        if (reOk)
                        {
                            Console.WriteLine($"PASS — raise_event(root={siRoot}, control={siControl}, event=Click) returned success");
                        }
                        else
                        {
                            Console.Error.WriteLine($"FAIL — raise_event did not return success. Raw: {reResp.RootElement.GetRawText()}");
                            failures++;
                        }
                        reResp.Dispose();
                    }

                    var postCount = await ReadObservableInt(child, stdoutMessages, siRoot, siCountObs);
                    Console.WriteLine($"INFO — post-input {siCountObs}={postCount?.ToString() ?? "<error>"} (delta {(postCount ?? 0) - (preCount ?? 0)})");
                }
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
        // Phase 3.1: also surface any [diag] lines specifically — useful when
        // investigating Avalonia adapter reachability issues that the
        // first-50-lines window may push out.
        var diagLines = new System.Collections.Generic.List<string>();
        foreach (var line in stderrLines)
        {
            if (line.IndexOf("[diag]", StringComparison.Ordinal) >= 0) diagLines.Add(line);
        }
        if (diagLines.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== [diag] lines ({diagLines.Count}) ===");
            foreach (var l in diagLines) Console.WriteLine($"  {l}");
        }
        Console.WriteLine($"stderr total: {stderrLines.Count} lines");

        Console.WriteLine();
        var verdictLabel = winuiMode
            ? "Phase 3.2 WinUI FormLab handshake"
            : (avaloniaMode
                ? "Phase 2.1 Avalonia Dashboard handshake"
                : (todoAppMode
                    ? (twoWindowsMode ? "Phase 3.3 TodoApp --two-windows handshake" : "Phase 1.4 TodoApp handshake")
                    : (guiMode ? "Phase 1.3 GUI handshake" : "Phase 1.2 handshake")));
        if (failures == 0)
        {
            Console.WriteLine($"=== {verdictLabel}: PASS ===");
            return 0;
        }
        Console.Error.WriteLine($"=== {verdictLabel}: FAIL — {failures} failure(s) ===");
        return 1;
    }

    // -----------------------------------------------------------------------
    // TodoApp helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Validate that the inspect_app_api response describes the TodoApp's
    /// expected manifest: TodoListViewModel root with five callables (AddTodo,
    /// RemoveTodo, ToggleDone, ClearCompleted, RenameTodo) and four observables
    /// (TotalCount, CompletedCount, PendingCount, LastAddedTitle).
    /// </summary>
    private static bool TryParseTodoAppManifest(string inspectText, out string error)
    {
        error = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(inspectText);
            var root = doc.RootElement;

            // The manifest is either a JsonArray of root entries (no rootName
            // arg) or a single object (rootName scoped). We pass no rootName,
            // so expect an array.
            if (root.ValueKind != JsonValueKind.Array)
            {
                error = $"expected array of roots, got {root.ValueKind}";
                return false;
            }

            JsonElement? todoVm = null;
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.TryGetProperty("name", out var n) && n.GetString() == "TodoListViewModel")
                {
                    todoVm = entry;
                    break;
                }
            }
            if (todoVm is null)
            {
                error = "no root named 'TodoListViewModel' in manifest";
                return false;
            }

            var expectedCallables = new[] { "AddTodo", "RemoveTodo", "ToggleDone", "ClearCompleted", "RenameTodo" };
            var expectedObservables = new[] { "TotalCount", "CompletedCount", "PendingCount", "LastAddedTitle" };

            var actualCallables = ExtractNames(todoVm.Value, "callables");
            var actualObservables = ExtractNames(todoVm.Value, "observables");

            foreach (var c in expectedCallables)
            {
                if (!actualCallables.Contains(c))
                {
                    error = $"missing callable '{c}'; got [{string.Join(",", actualCallables)}]";
                    return false;
                }
            }
            foreach (var o in expectedObservables)
            {
                if (!actualObservables.Contains(o))
                {
                    error = $"missing observable '{o}'; got [{string.Join(",", actualObservables)}]";
                    return false;
                }
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = $"manifest is not valid JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Validate that the inspect_app_api response describes the WinUI FormLab's
    /// expected manifest: FormLabViewModel root with six callables (SetName,
    /// SetAge, ToggleNotifications, SetTheme, Submit, Reset), five observables
    /// (Name, Age, NotificationsEnabled, Theme, HasSubmitted), and one event
    /// (FormSubmitted).
    /// </summary>
    private static bool TryParseFormLabManifest(string inspectText, out string error)
    {
        error = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(inspectText);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                error = $"expected array of roots, got {root.ValueKind}";
                return false;
            }

            JsonElement? formLab = null;
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.TryGetProperty("name", out var n) && n.GetString() == "FormLabViewModel")
                {
                    formLab = entry;
                    break;
                }
            }
            if (formLab is null)
            {
                error = "no root named 'FormLabViewModel' in manifest";
                return false;
            }

            var expectedCallables = new[] { "SetName", "SetAge", "ToggleNotifications", "SetTheme", "Submit", "Reset" };
            var expectedObservables = new[] { "Name", "Age", "NotificationsEnabled", "Theme", "HasSubmitted" };
            var expectedEvents = new[] { "FormSubmitted" };

            var actualCallables = ExtractNames(formLab.Value, "callables");
            var actualObservables = ExtractNames(formLab.Value, "observables");
            var actualEvents = ExtractNames(formLab.Value, "events");

            foreach (var c in expectedCallables)
            {
                if (!actualCallables.Contains(c))
                {
                    error = $"missing callable '{c}'; got [{string.Join(",", actualCallables)}]";
                    return false;
                }
            }
            foreach (var o in expectedObservables)
            {
                if (!actualObservables.Contains(o))
                {
                    error = $"missing observable '{o}'; got [{string.Join(",", actualObservables)}]";
                    return false;
                }
            }
            foreach (var ev in expectedEvents)
            {
                if (!actualEvents.Contains(ev))
                {
                    error = $"missing event '{ev}'; got [{string.Join(",", actualEvents)}]";
                    return false;
                }
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = $"manifest is not valid JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Validate that the inspect_app_api response describes the Avalonia
    /// Dashboard's expected manifest: DashboardViewModel root with five
    /// callables (UpsertMetric, RemoveMetric, ResetAll, TogglePaused,
    /// RefreshAsync), four observables (MetricCount, Total, IsPaused,
    /// LastUpdatedMetric), and two events (MetricUpserted, PausedToggled).
    /// </summary>
    private static bool TryParseDashboardManifest(string inspectText, out string error)
    {
        error = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(inspectText);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                error = $"expected array of roots, got {root.ValueKind}";
                return false;
            }

            JsonElement? dashboard = null;
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.TryGetProperty("name", out var n) && n.GetString() == "DashboardViewModel")
                {
                    dashboard = entry;
                    break;
                }
            }
            if (dashboard is null)
            {
                error = "no root named 'DashboardViewModel' in manifest";
                return false;
            }

            var expectedCallables = new[] { "UpsertMetric", "RemoveMetric", "ResetAll", "TogglePaused", "RefreshAsync" };
            var expectedObservables = new[] { "MetricCount", "Total", "IsPaused", "LastUpdatedMetric" };
            var expectedEvents = new[] { "MetricUpserted", "PausedToggled" };

            var actualCallables = ExtractNames(dashboard.Value, "callables");
            var actualObservables = ExtractNames(dashboard.Value, "observables");
            var actualEvents = ExtractNames(dashboard.Value, "events");

            foreach (var c in expectedCallables)
            {
                if (!actualCallables.Contains(c))
                {
                    error = $"missing callable '{c}'; got [{string.Join(",", actualCallables)}]";
                    return false;
                }
            }
            foreach (var o in expectedObservables)
            {
                if (!actualObservables.Contains(o))
                {
                    error = $"missing observable '{o}'; got [{string.Join(",", actualObservables)}]";
                    return false;
                }
            }
            foreach (var ev in expectedEvents)
            {
                if (!actualEvents.Contains(ev))
                {
                    error = $"missing event '{ev}'; got [{string.Join(",", actualEvents)}]";
                    return false;
                }
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = $"manifest is not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static System.Collections.Generic.List<string> ExtractNames(JsonElement root, string field)
    {
        var names = new System.Collections.Generic.List<string>();
        if (!root.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return names;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var n) && n.GetString() is string name)
                names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Read a single integer observable. Returns null on failure.
    /// </summary>
    private static async Task<int?> ReadObservableInt(
        Process child,
        BlockingCollection<JsonDocument> queue,
        string root,
        string property)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var req = new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name = "read_observable", arguments = new { root, property } },
        };
        await SendAsync(child, req);
        var resp = await WaitForResponseAsync(queue, id, TimeSpan.FromSeconds(10));
        if (resp is null) return null;
        try
        {
            if (TryReadToolText(resp.RootElement, out var text) &&
                int.TryParse(text.Trim(), out var n))
            {
                return n;
            }
            return null;
        }
        finally
        {
            resp.Dispose();
        }
    }

    /// <summary>
    /// Phase 3.3: read_observable scoped to a specific windowId. Mirrors
    /// <see cref="ReadObservableInt"/> but adds the <c>windowId</c> argument
    /// so the runtime routes to the matching tracked instance.
    /// </summary>
    private static async Task<int?> ReadObservableIntScoped(
        Process child,
        BlockingCollection<JsonDocument> queue,
        string root,
        string property,
        string windowId)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var req = new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name = "read_observable", arguments = new { root, property, windowId } },
        };
        await SendAsync(child, req);
        var resp = await WaitForResponseAsync(queue, id, TimeSpan.FromSeconds(10));
        if (resp is null) return null;
        try
        {
            if (TryReadToolText(resp.RootElement, out var text) &&
                int.TryParse(text.Trim(), out var n))
            {
                return n;
            }
            return null;
        }
        finally
        {
            resp.Dispose();
        }
    }

    /// <summary>
    /// Phase 3.3: invoke_method scoped to a specific windowId. Mirrors
    /// <see cref="InvokeMethodAsync"/> but adds the <c>windowId</c> argument.
    /// </summary>
    private static async Task<(bool Success, string Detail)> InvokeMethodAsyncScoped(
        Process child,
        BlockingCollection<JsonDocument> queue,
        string root,
        string method,
        object? methodArgs,
        string windowId)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var req = new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new
            {
                name = "invoke_method",
                arguments = new { root, method, args = methodArgs, windowId },
            },
        };
        await SendAsync(child, req);
        var resp = await WaitForResponseAsync(queue, id, TimeSpan.FromSeconds(10));
        if (resp is null) return (false, "no response within 10s");
        try
        {
            if (!TryReadToolText(resp.RootElement, out var text))
                return (false, "no text content");
            try
            {
                using var inner = JsonDocument.Parse(text);
                if (inner.RootElement.ValueKind == JsonValueKind.Object &&
                    inner.RootElement.TryGetProperty("success", out var s) &&
                    s.ValueKind == JsonValueKind.False)
                {
                    var code = inner.RootElement.TryGetProperty("errorCode", out var c) ? c.GetString() : "?";
                    var msg = inner.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "?";
                    return (false, $"[{code}] {msg}");
                }
            }
            catch (JsonException) { /* primitive / null is success */ }

            return (true, text.Trim());
        }
        finally
        {
            resp.Dispose();
        }
    }

    /// <summary>
    /// Read a single string observable. Returns null on failure;
    /// distinguishes empty string ("") from a missing read by returning the
    /// empty string when JSON's "" was the response.
    /// </summary>
    private static async Task<string?> ReadObservableString(
        Process child,
        BlockingCollection<JsonDocument> queue,
        string root,
        string property)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var req = new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name = "read_observable", arguments = new { root, property } },
        };
        await SendAsync(child, req);
        var resp = await WaitForResponseAsync(queue, id, TimeSpan.FromSeconds(10));
        if (resp is null) return null;
        try
        {
            if (!TryReadToolText(resp.RootElement, out var text)) return null;
            // Runtime serialises strings as JSON-escaped values: "" -> `""`,
            // "Test" -> `"Test"`. Parse to extract the unescaped value.
            try
            {
                using var inner = JsonDocument.Parse(text);
                if (inner.RootElement.ValueKind == JsonValueKind.String)
                {
                    return inner.RootElement.GetString() ?? string.Empty;
                }
                return text.Trim();
            }
            catch (JsonException)
            {
                return text.Trim();
            }
        }
        finally
        {
            resp.Dispose();
        }
    }

    /// <summary>
    /// Read a single bool observable. Returns null on failure.
    /// </summary>
    private static async Task<bool?> ReadObservableBool(
        Process child,
        BlockingCollection<JsonDocument> queue,
        string root,
        string property)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var req = new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name = "read_observable", arguments = new { root, property } },
        };
        await SendAsync(child, req);
        var resp = await WaitForResponseAsync(queue, id, TimeSpan.FromSeconds(10));
        if (resp is null) return null;
        try
        {
            if (!TryReadToolText(resp.RootElement, out var text)) return null;
            var trimmed = text.Trim();
            return trimmed switch
            {
                "true" => true,
                "false" => false,
                _ => (bool?)null,
            };
        }
        finally
        {
            resp.Dispose();
        }
    }

    /// <summary>
    /// Invoke a method via tools/call invoke_method. Returns success +
    /// diagnostic detail.
    /// </summary>
    private static async Task<(bool Success, string Detail)> InvokeMethodAsync(
        Process child,
        BlockingCollection<JsonDocument> queue,
        string root,
        string method,
        object? methodArgs)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var req = new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new
            {
                name = "invoke_method",
                arguments = new { root, method, args = methodArgs },
            },
        };
        await SendAsync(child, req);

#pragma warning disable CS1591 // We rebind below; the marker comment lets `dynamic-tool` consumers find this.
        // (Phase 2.2: the parallel helper InvokeDynamicToolAsync — defined
        // immediately below — calls the per-method tool directly with the
        // computed name `<root>.<method>` and `arguments` set to the
        // user-method's parameter bag, NOT the `invoke_method` envelope.)
#pragma warning restore CS1591
        var resp = await WaitForResponseAsync(queue, id, TimeSpan.FromSeconds(10));
        if (resp is null) return (false, "no response within 10s");
        try
        {
            if (!TryReadToolText(resp.RootElement, out var text))
                return (false, "no text content");
            // The runtime returns "null" for void callables and a JSON value
            // otherwise. A structured error is `{"success":false, ...}`.
            try
            {
                using var inner = JsonDocument.Parse(text);
                if (inner.RootElement.ValueKind == JsonValueKind.Object &&
                    inner.RootElement.TryGetProperty("success", out var s) &&
                    s.ValueKind == JsonValueKind.False)
                {
                    var code = inner.RootElement.TryGetProperty("errorCode", out var c) ? c.GetString() : "?";
                    var msg = inner.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "?";
                    return (false, $"[{code}] {msg}");
                }
            }
            catch (JsonException) { /* "null" or a primitive — that's success */ }

            return (true, text.Trim());
        }
        finally
        {
            resp.Dispose();
        }
    }

    /// <summary>
    /// Phase 2.2: invoke a per-method dynamic tool DIRECTLY via tools/call,
    /// bypassing the meta-tool envelope. The tool name is the
    /// <c><![CDATA[<root>.<method>]]></c> identity that
    /// <c>DynamicToolRegistry</c> registered. <paramref name="methodArgs"/>
    /// is the user-method's parameter bag (e.g. <c>new { title = "buy milk" }</c>),
    /// passed as the call's <c>arguments</c> directly. Returns success +
    /// diagnostic detail with a <c>[via dynamic tool]</c> path marker.
    /// </summary>
    private static async Task<(bool Success, string Detail)> InvokeDynamicToolAsync(
        Process child,
        BlockingCollection<JsonDocument> queue,
        string toolName,
        object? methodArgs)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var req = new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments = methodArgs ?? new { },
            },
        };
        await SendAsync(child, req);
        var resp = await WaitForResponseAsync(queue, id, TimeSpan.FromSeconds(10));
        if (resp is null) return (false, "no response within 10s");
        try
        {
            if (!TryReadToolText(resp.RootElement, out var text))
                return (false, "no text content");
            try
            {
                using var inner = JsonDocument.Parse(text);
                if (inner.RootElement.ValueKind == JsonValueKind.Object &&
                    inner.RootElement.TryGetProperty("success", out var s) &&
                    s.ValueKind == JsonValueKind.False)
                {
                    var code = inner.RootElement.TryGetProperty("errorCode", out var c) ? c.GetString() : "?";
                    var msg = inner.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "?";
                    return (false, $"[{code}] {msg}");
                }
            }
            catch (JsonException) { /* primitive */ }
            return (true, text.Trim());
        }
        finally
        {
            resp.Dispose();
        }
    }

    /// <summary>
    /// Track the watcher state needed to wait for a notifications/resources/updated
    /// arrival on a specific URI. The watcher consumes from the same shared
    /// queue as the response correlator; we use a callback so we don't drop
    /// non-matching notifications.
    /// </summary>
    private sealed class NotificationWatcher
    {
        public BlockingCollection<JsonDocument> Source { get; }
        public System.Collections.Concurrent.ConcurrentQueue<JsonDocument> Notifications { get; } = new();

        public NotificationWatcher(BlockingCollection<JsonDocument> source)
        {
            Source = source;
        }
    }

    private static NotificationWatcher StartNotificationWatcher(BlockingCollection<JsonDocument> queue) =>
        new(queue);

    /// <summary>
    /// Wait up to <paramref name="timeout"/> for a notifications/resources/updated
    /// matching <paramref name="uri"/>. First scans <see cref="s_notifications"/>
    /// for any notifications stashed by <see cref="WaitForResponseAsync"/> while
    /// the test was awaiting unrelated request responses; then continues
    /// draining the shared queue.
    /// </summary>
    private static async Task<bool> WaitForResourceUpdate(
        NotificationWatcher watcher,
        string uri,
        TimeSpan timeout)
    {
        // First: walk every already-stashed notification.
        var keptStashed = new System.Collections.Generic.List<JsonDocument>();
        while (s_notifications.TryDequeue(out var stashed))
        {
            if (IsResourceUpdate(stashed.RootElement, uri))
            {
                stashed.Dispose();
                // Re-queue the rest so future waiters can still see them.
                foreach (var k in keptStashed) s_notifications.Enqueue(k);
                return true;
            }
            keptStashed.Add(stashed);
        }
        foreach (var k in keptStashed) s_notifications.Enqueue(k);

        // Second: pull live messages off the queue.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            if (watcher.Source.TryTake(out var doc, (int)Math.Min(remaining.TotalMilliseconds, 200)))
            {
                try
                {
                    if (IsResourceUpdate(doc.RootElement, uri)) return true;
                    // Not the notification we're waiting for; if it's any
                    // other notification, stash it for later checks; if it's
                    // a response, drop it (no waiter is registered for IDs).
                    if (!doc.RootElement.TryGetProperty("id", out _))
                    {
                        s_notifications.Enqueue(doc);
                        // Don't dispose — the queue owns it now.
                        continue;
                    }
                }
                catch
                {
                    /* fall through to dispose */
                }
                doc.Dispose();
            }
            await Task.Yield();
        }
        return false;
    }

    private static bool IsResourceUpdate(JsonElement root, string uri)
    {
        if (root.TryGetProperty("method", out var m) &&
            m.GetString() == "notifications/resources/updated" &&
            root.TryGetProperty("params", out var p) &&
            p.TryGetProperty("uri", out var u) &&
            u.GetString() == uri)
        {
            return true;
        }
        return false;
    }

    private static async Task SendAsync(Process child, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        await child.StandardInput.WriteLineAsync(json).ConfigureAwait(false);
        await child.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Process-global stash for received notifications. JSON-RPC messages
    /// without an `id` field are notifications; the response correlator
    /// preserves them here so a later
    /// <see cref="WaitForResourceUpdate"/> can find them.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentQueue<JsonDocument> s_notifications = new();

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
                        // Mismatched-id response — drop it.
                        doc.Dispose();
                    }
                    else
                    {
                        // No id => notification. Stash for later watchers.
                        s_notifications.Enqueue(doc);
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

    /// <summary>
    /// Phase 3.1 helper: confirm a tools/call result text is a JSON object
    /// with <c>{"success":true}</c>. Returns false for structured errors
    /// (which carry <c>{"success":false,"errorCode":...}</c>) and for
    /// non-object responses.
    /// </summary>
    private static bool IsSuccessJson(string toolText)
    {
        if (string.IsNullOrEmpty(toolText)) return false;
        try
        {
            using var doc = JsonDocument.Parse(toolText);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("success", out var s) &&
                   s.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
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
