# Marionette.NET.Integration — Phase 1 End-to-End Eval Cases

Per the masterplan's Phase 1 deliverable: **"5 End-to-End-Eval-Cases als CI-Test"**.

Each `[Fact]` spawns a fresh `Sample.Wpf.TodoApp.exe --mcp --headless` child via `TodoAppFixture`, drives it through stdio JSON-RPC, asserts behaviour, and disposes the fixture (which guarantees no orphan processes survive a test failure).

## How to run

From the repo root:

```
dotnet test tests/Marionette.NET.Integration/Marionette.NET.Integration.csproj -c Debug
```

Or, as part of the full build matrix:

```
dotnet test Marionette.NET.sln -c Debug
```

Phase-1 expectation: **5 passed, 0 failed**.

## What each case covers

| # | Name | Purpose |
|---|---|---|
| EC-1 | `EC1_Discovery_IsComplete` | `tools/list` returns the four Marionette tools; `inspect_app_api` returns the TodoListViewModel root with the documented 5 callables and 4 observables. |
| EC-2 | `EC2_Methods_InvokeAndUpdateObservables` | `AddTodo` invocations succeed and the derived observables (`TotalCount`, `LastAddedTitle`) reflect the new state. |
| EC-3 | `EC3_WatchableObservables_PushNotifications` | `resources/subscribe` to `marionette://TodoListViewModel/TotalCount` produces `notifications/resources/updated` after each `AddTodo`. The subscription is registered BEFORE the first invoke to avoid races. |
| EC-4 | `EC4_LoopProtection_TriggersAndDecays` | With `MARIONETTE_MAX_DEPTH=2` and `MARIONETTE_DECAY_SECONDS=2`, the third `invoke_method` returns `loop_limit_exceeded`; after a 3-second wait the counter has decayed and the next call succeeds. |
| EC-5 | `EC5_Stdout_StaysJsonRpcPure` | After exercising every tool path (including the screenshot's `screenshot_not_supported` error and a watcher notification), every stdout line parses as JSON-RPC. Zero pollution lines. This is the StdoutGuardWriter regression net. |

## Adding a new EC

1. Add a `[Fact]` method to `EvalCases.cs`.
2. Use `using var fx = new TodoAppFixture();` to spawn the child.
3. If you need env overrides (rare), pass them as a dict: `new TodoAppFixture(new Dictionary<string, string?> { ["KEY"] = "value" })`.
4. Use the fixture's `InitializeAsync`, `ListToolsAsync`, `InspectAppApiAsync`, `InvokeMethodAsync`, `ReadObservableAsync`, `SubscribeAsync`, `ReadResourceAsync`, and `WaitForResourceUpdateAsync` helpers to drive the child.
5. Fail with a clear message — the eval suite is the regression dam, so a vague failure is worse than no failure at all.

## Trapdoors to avoid

- **Don't run against `--mcp` (GUI mode).** The fixture always uses `--mcp --headless`. GUI mode requires an interactive desktop and is covered by `.phase1/demo.ps1 -Gui`.
- **Don't share state between tests.** Each `[Fact]` gets its own fresh process. xUnit by default runs Facts in parallel; the fixture instance is per-test, but the OS-level processes are also per-test, so parallelism is safe.
- **Subscribe BEFORE invoking.** EC-3's notification race: if you call `AddTodo` first, the PropertyChanged event fires before the subscription is registered and the notification is missed.
- **Sort tool/manifest names before asserting equality.** MCP doesn't guarantee ordering of `tools/list` or the manifest's nested arrays.
