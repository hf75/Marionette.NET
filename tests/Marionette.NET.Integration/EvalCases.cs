// Marionette.NET — Phase 1.5 End-to-End Eval Cases
//
// Per MASTERPLAN Phase 1 deliverable: "5 End-to-End-Eval-Cases als CI-Test".
// These five cases collectively cover:
//
//   EC-1  Discovery (tools/list + inspect_app_api shape).
//   EC-2  Method dispatch + observable read consistency.
//   EC-3  Watchable resources push notifications via resources/subscribe.
//   EC-4  Loop-protection triggers + decay.
//   EC-5  Stdout JSON-RPC purity (no log spew, no stack traces).
//
// Each test spawns a fresh `Sample.Wpf.TodoApp.exe --mcp --headless` child
// via TodoAppFixture, drives it through stdio JSON-RPC, asserts behaviour,
// and disposes the fixture (which guarantees no orphan processes survive).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Xunit;

namespace Marionette.Integration;

public class EvalCases
{
    // MASTERPLAN-required tool names. Discovery (EC-1) asserts equality.
    private static readonly string[] RequiredTools =
    {
        "capture_screenshot",
        "inspect_app_api",
        "invoke_method",
        "read_observable",
    };

    // TodoListViewModel surface — five callables, four observables, three
    // watchable. See samples/Sample.Wpf.TodoApp/TodoListViewModel.cs.
    private static readonly string[] ExpectedCallables =
    {
        "AddTodo", "ClearCompleted", "RemoveTodo", "RenameTodo", "ToggleDone",
    };

    private static readonly string[] ExpectedObservables =
    {
        "CompletedCount", "LastAddedTitle", "PendingCount", "TotalCount",
    };

    private const string TotalCountUri = "marionette://TodoListViewModel/TotalCount";

    // -------------------------------------------------------------------------
    // EC-1: Discovery is complete
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EC1_Discovery_IsComplete()
    {
        using var fx = new TodoAppFixture();

        // initialize handshake
        using var initResp = await fx.InitializeAsync();
        Assert.True(initResp.RootElement.TryGetProperty("result", out var result),
            "initialize response had no result");
        Assert.True(result.TryGetProperty("protocolVersion", out _),
            "initialize result missing protocolVersion");
        Assert.True(result.TryGetProperty("serverInfo", out var serverInfo) &&
                    serverInfo.TryGetProperty("name", out var serverName) &&
                    serverName.GetString() == "Marionette.NET",
            "initialize serverInfo.name should be 'Marionette.NET'");

        // tools/list — must include all four Marionette tools (sorted).
        var actualTools = await fx.ListToolsAsync();
        var actualCore = actualTools
            .Where(t => RequiredTools.Contains(t, StringComparer.Ordinal))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(RequiredTools, actualCore);

        // inspect_app_api() — must contain TodoListViewModel root with the
        // documented 5 callables and 4 observables.
        var manifestText = await fx.InspectAppApiAsync();
        using var manifest = JsonDocument.Parse(manifestText);

        Assert.True(manifest.RootElement.ValueKind == JsonValueKind.Array,
            $"EC-1 manifest expected JSON array of roots, got {manifest.RootElement.ValueKind}.");

        JsonElement? todoRoot = null;
        foreach (var entry in manifest.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("name", out var n) &&
                n.GetString() == "TodoListViewModel")
            {
                todoRoot = entry;
                break;
            }
        }
        Assert.True(todoRoot is not null,
            "EC-1: no root named 'TodoListViewModel' in inspect_app_api output. " +
            $"Got: {manifestText}");

        var actualCallables = ExtractNames(todoRoot!.Value, "callables")
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var actualObservables = ExtractNames(todoRoot!.Value, "observables")
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();

        Assert.Equal(ExpectedCallables, actualCallables);
        Assert.Equal(ExpectedObservables, actualObservables);
    }

    // -------------------------------------------------------------------------
    // EC-2: Methods invoke and update observables
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EC2_Methods_InvokeAndUpdateObservables()
    {
        using var fx = new TodoAppFixture();
        await fx.InitializeAsync();

        // Sanity baseline: headless mode starts with TotalCount = 0.
        var totalBefore = await fx.ReadObservableAsync("TodoListViewModel", "TotalCount");
        Assert.Equal("0", totalBefore.Trim());

        // AddTodo("buy milk") -> success (void => "null")
        var add1 = await fx.InvokeMethodAsync(
            "TodoListViewModel", "AddTodo", new { title = "buy milk" });
        AssertCallableSuccess(add1, "AddTodo(\"buy milk\")");

        // TotalCount -> 1
        var totalAfter1 = await fx.ReadObservableAsync("TodoListViewModel", "TotalCount");
        Assert.Equal("1", totalAfter1.Trim());

        // LastAddedTitle -> "buy milk"
        var lastAdded = await fx.ReadObservableAsync("TodoListViewModel", "LastAddedTitle");
        // The runtime serialises strings with quotes via STJ, so the result is
        // a JSON-encoded string literal.
        Assert.Equal("\"buy milk\"", lastAdded.Trim());

        // AddTodo("walk dog") -> success
        var add2 = await fx.InvokeMethodAsync(
            "TodoListViewModel", "AddTodo", new { title = "walk dog" });
        AssertCallableSuccess(add2, "AddTodo(\"walk dog\")");

        // TotalCount -> 2
        var totalAfter2 = await fx.ReadObservableAsync("TodoListViewModel", "TotalCount");
        Assert.Equal("2", totalAfter2.Trim());
    }

    // -------------------------------------------------------------------------
    // EC-3: Watchable observables push notifications
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EC3_WatchableObservables_PushNotifications()
    {
        using var fx = new TodoAppFixture();
        await fx.InitializeAsync();

        // CRITICAL: subscribe BEFORE invoking any AddTodo. Otherwise the first
        // PropertyChanged on TotalCount has no subscriber and we miss the
        // notification.
        var subscribed = await fx.SubscribeAsync(TotalCountUri);
        Assert.True(subscribed,
            $"EC-3: resources/subscribe to {TotalCountUri} did not return a result.");

        // First AddTodo -> notification(uri=TotalCount), value=1
        var add1 = await fx.InvokeMethodAsync(
            "TodoListViewModel", "AddTodo", new { title = "study" });
        AssertCallableSuccess(add1, "AddTodo(\"study\")");

        var got1 = await fx.WaitForResourceUpdateAsync(TotalCountUri, TimeSpan.FromSeconds(5));
        Assert.True(got1,
            $"EC-3: timeout waiting for notifications/resources/updated on {TotalCountUri} " +
            "after first AddTodo (expected within 5s).");

        var read1 = await fx.ReadResourceAsync(TotalCountUri);
        Assert.Equal("1", read1?.Trim());

        // Second AddTodo -> another notification, value=2
        var add2 = await fx.InvokeMethodAsync(
            "TodoListViewModel", "AddTodo", new { title = "sleep" });
        AssertCallableSuccess(add2, "AddTodo(\"sleep\")");

        var got2 = await fx.WaitForResourceUpdateAsync(TotalCountUri, TimeSpan.FromSeconds(5));
        Assert.True(got2,
            $"EC-3: timeout waiting for notifications/resources/updated on {TotalCountUri} " +
            "after second AddTodo (expected within 5s).");

        var read2 = await fx.ReadResourceAsync(TotalCountUri);
        Assert.Equal("2", read2?.Trim());
    }

    // -------------------------------------------------------------------------
    // EC-4: Loop protection triggers (and decays)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EC4_LoopProtection_TriggersAndDecays()
    {
        // MARIONETTE_MAX_DEPTH=2 -> the THIRD invoke_method call (third hop)
        // exceeds the limit and returns a structured loop_limit_exceeded error.
        // MARIONETTE_DECAY_SECONDS=2 -> the counter resets after a 2-second
        // idle window, letting us validate the decay path without waiting 30s.
        using var fx = new TodoAppFixture(new Dictionary<string, string?>
        {
            ["MARIONETTE_MAX_DEPTH"] = "2",
            ["MARIONETTE_DECAY_SECONDS"] = "2",
        });
        await fx.InitializeAsync();

        // Hop 1 — succeeds.
        var hop1 = await fx.InvokeMethodAsync(
            "TodoListViewModel", "AddTodo", new { title = "h1" });
        AssertCallableSuccess(hop1, "Hop-1 AddTodo");

        // Hop 2 — succeeds (counter reaches the limit, but isn't yet
        // exceeded).
        var hop2 = await fx.InvokeMethodAsync(
            "TodoListViewModel", "AddTodo", new { title = "h2" });
        AssertCallableSuccess(hop2, "Hop-2 AddTodo");

        // Hop 3 — exceeds. Expect structured error.
        var hop3 = await fx.InvokeMethodAsync(
            "TodoListViewModel", "AddTodo", new { title = "h3" });
        AssertLoopLimitExceeded(hop3);

        // Wait for decay (decay window is 2s; sleep 3s to be safe).
        await Task.Delay(TimeSpan.FromSeconds(3));

        // After decay -> hop counter reset; this call succeeds.
        var afterDecay = await fx.InvokeMethodAsync(
            "TodoListViewModel", "AddTodo", new { title = "after-decay" });
        AssertCallableSuccess(afterDecay, "Post-decay AddTodo");
    }

    // -------------------------------------------------------------------------
    // EC-5: Stdout stays JSON-RPC pure
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EC5_Stdout_StaysJsonRpcPure()
    {
        using var fx = new TodoAppFixture();
        await fx.InitializeAsync();

        // Exercise each of the four tools so the regression net catches any
        // path that leaks to stdout.
        await fx.ListToolsAsync();
        _ = await fx.InspectAppApiAsync();
        _ = await fx.InvokeMethodAsync("TodoListViewModel", "AddTodo", new { title = "ec5" });
        _ = await fx.ReadObservableAsync("TodoListViewModel", "TotalCount");

        // capture_screenshot returns a structured `screenshot_not_supported`
        // error in headless mode; that is still a JSON-RPC frame, so it
        // exercises the path without polluting stdout.
        await fx.SendRawAsync(new
        {
            jsonrpc = "2.0",
            id = 9999,
            method = "tools/call",
            @params = new { name = "capture_screenshot", arguments = new { } },
        });

        // Subscribe + AddTodo to exercise the notification path too.
        await fx.SubscribeAsync(TotalCountUri);
        await fx.InvokeMethodAsync("TodoListViewModel", "AddTodo", new { title = "ec5b" });
        await fx.WaitForResourceUpdateAsync(TotalCountUri, TimeSpan.FromSeconds(2));

        // Cleanly close stdin so the host shuts down. Dispose handles the
        // forced kill if it doesn't exit promptly.
        try { fx.Child.StandardInput.Close(); } catch { /* ignore */ }
        // Give the host a moment to flush any final notifications/resources.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // Now validate every captured stdout line parses as JSON.
        var stdoutLines = fx.StdoutLines.ToArray();
        Assert.NotEmpty(stdoutLines);

        var pollutionLines = new List<string>();
        var validFrames = 0;
        foreach (var line in stdoutLines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                validFrames++;
            }
            catch (JsonException)
            {
                pollutionLines.Add(line);
            }
        }

        Assert.True(pollutionLines.Count == 0,
            $"EC-5: stdout pollution detected ({pollutionLines.Count} lines). First few:\n" +
            string.Join("\n", pollutionLines.Take(5)));

        Assert.True(validFrames > 0, "EC-5: expected at least one JSON-RPC frame on stdout.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static List<string> ExtractNames(JsonElement root, string field)
    {
        var names = new List<string>();
        if (!root.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return names;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var n) && n.GetString() is string s)
                names.Add(s);
        }
        return names;
    }

    private static void AssertCallableSuccess(string responseText, string label)
    {
        var trimmed = responseText.Trim();
        // Void callables return literal "null"; non-void return JSON. A
        // structured error is always a JSON object with "success":false.
        if (trimmed == "null") return;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("success", out var s) &&
                s.ValueKind == JsonValueKind.False)
            {
                var code = doc.RootElement.TryGetProperty("errorCode", out var c) ? c.GetString() : "?";
                var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "?";
                Assert.Fail($"{label} failed: [{code}] {msg}");
            }
        }
        catch (JsonException)
        {
            // primitive (e.g. integer) — that's success too.
        }
    }

    private static void AssertLoopLimitExceeded(string responseText)
    {
        var trimmed = responseText.Trim();
        Assert.NotEqual("null", trimmed);

        using var doc = JsonDocument.Parse(trimmed);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object,
            $"EC-4: expected structured error JSON object, got {doc.RootElement.ValueKind}: {trimmed}");
        Assert.True(doc.RootElement.TryGetProperty("success", out var s) &&
                    s.ValueKind == JsonValueKind.False,
            $"EC-4: expected success=false. Got: {trimmed}");
        Assert.True(doc.RootElement.TryGetProperty("errorCode", out var c) &&
                    c.GetString() == "loop_limit_exceeded",
            $"EC-4: expected errorCode='loop_limit_exceeded'. Got: {trimmed}");
    }
}
