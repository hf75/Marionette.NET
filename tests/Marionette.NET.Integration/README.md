# Marionette.NET.Integration — End-to-End Eval Cases

Per the masterplan's Phase 1 deliverable: **"5 End-to-End-Eval-Cases als CI-Test"**, extended through Phase 1.6 (events), Phase 2.2 (dynamic per-method tools), and Phase 3.1 (input simulation).

Each `[Fact]` spawns a fresh `Sample.Wpf.TodoApp.exe` child via `TodoAppFixture` — by default in `--mcp --headless` mode, with Phase 3.1 EC-8/EC-9 in `--mcp` GUI mode (gated). Each test drives the child through stdio JSON-RPC, asserts behaviour, and disposes the fixture (which guarantees no orphan processes survive a test failure).

## How to run

From the repo root:

```
dotnet test tests/Marionette.NET.Integration/Marionette.NET.Integration.csproj -c Debug
```

Or, as part of the full build matrix:

```
dotnet test Marionette.NET.sln -c Debug
```

Default expectation: **7 passed, 2 skipped, 0 failed**. The 2 skipped tests are EC-8 and EC-9 (Phase 3.1 GUI tests; see below).

## What each case covers

| # | Name | Purpose |
|---|---|---|
| EC-1 | `EC1_Discovery_IsComplete` | `tools/list` returns the four Marionette tools; `inspect_app_api` returns the TodoListViewModel root with the documented 5 callables and 4 observables. |
| EC-2 | `EC2_Methods_InvokeAndUpdateObservables` | `AddTodo` invocations succeed and the derived observables (`TotalCount`, `LastAddedTitle`) reflect the new state. |
| EC-3 | `EC3_WatchableObservables_PushNotifications` | `resources/subscribe` to `marionette://TodoListViewModel/TotalCount` produces `notifications/resources/updated` after each `AddTodo`. The subscription is registered BEFORE the first invoke to avoid races. |
| EC-4 | `EC4_LoopProtection_TriggersAndDecays` | With `MARIONETTE_MAX_DEPTH=2` and `MARIONETTE_DECAY_SECONDS=2`, the third `invoke_method` returns `loop_limit_exceeded`; after a 3-second wait the counter has decayed and the next call succeeds. |
| EC-5 | `EC5_Stdout_StaysJsonRpcPure` | After exercising every tool path (including the screenshot's `screenshot_not_supported` error and a watcher notification), every stdout line parses as JSON-RPC. Zero pollution lines. This is the StdoutGuardWriter regression net. |
| EC-6 | `EC6_Events_DeliverViaResourceNotifications` | Phase 1.6 — `[McpEvent]` declarative events deliver via `notifications/resources/updated`; the resource read carries the args payload. |
| EC-7 | `EC7_DynamicTools_RegisterAndDispatch` | Phase 2.2 — per-method `<Root>.<Method>` dynamic tools register and dispatch through the same pipeline as `invoke_method`. |
| EC-8 | `EC8_SimulateInput_DrivesRealInputPipeline` | **GUI** (Phase 3.1) — `simulate_input(kind:"click")` on a control resolves via the visual tree and drives the framework's input pipeline. **Skipped by default** (requires interactive desktop). |
| EC-9 | `EC9_RaiseEvent_FiresRoutedEventHandler` | **GUI** (Phase 3.1) — `raise_event(event:"Click")` resolves the routed event by walking the control's type chain and dispatches via `RaiseEvent`. **Skipped by default**. |

## Phase 3.1 GUI tests (EC-8 / EC-9)

The Phase 3.1 input-simulation tests need a real WPF Application + MainWindow, which means spawning the TodoApp WITHOUT `--headless`. CI runners without an interactive desktop session fail at `Application` ctor with a thread-affinity / no-display error.

EC-8 and EC-9 are therefore marked `[Fact(Skip = "...")]` by default. To run them locally on a Windows desktop with an attended session:

1. Open `tests/Marionette.NET.Integration/EvalCases.cs`, comment out the `Skip = ...` argument on EC-8 and EC-9.
2. Set the env var: `set MARIONETTE_GUI_TESTS=1` (Windows) or `export MARIONETTE_GUI_TESTS=1` (bash).
3. `dotnet test tests/Marionette.NET.Integration/...`
4. Restore the `Skip = ...` argument before committing.

The xUnit-2.x dependency lacks a runtime skip API; once the project moves to xUnit v3 (Phase 6 polish), EC-8/9 can transition to `Assert.Skip(reason)` with the env-var gating without manual edits.

The Phase 3.1 stdio harness (`.phase0/StdioTest <TodoApp.exe> --todoapp --gui --simulate-input`) exercises the same flow without xUnit involvement and is the canonical manual-verification path.

## Adding a new EC

1. Add a `[Fact]` method to `EvalCases.cs`.
2. Use `using var fx = new TodoAppFixture();` to spawn the child.
3. If you need env overrides (rare), pass them as a dict: `new TodoAppFixture(new Dictionary<string, string?> { ["KEY"] = "value" })`.
4. Use the fixture's `InitializeAsync`, `ListToolsAsync`, `InspectAppApiAsync`, `InvokeMethodAsync`, `ReadObservableAsync`, `SubscribeAsync`, `ReadResourceAsync`, and `WaitForResourceUpdateAsync` helpers to drive the child.
5. Fail with a clear message — the eval suite is the regression dam, so a vague failure is worse than no failure at all.

## Trapdoors to avoid

- **Default mode is `--mcp --headless`.** EC-1 through EC-7 and EC-5 always use headless. Phase 3.1 EC-8/EC-9 explicitly opt into GUI mode via `TodoAppFixture(guiMode: true)` — these are the only tests where an interactive desktop is required.
- **Don't share state between tests.** Each `[Fact]` gets its own fresh process. xUnit by default runs Facts in parallel; the fixture instance is per-test, but the OS-level processes are also per-test, so parallelism is safe.
- **Subscribe BEFORE invoking.** EC-3's notification race: if you call `AddTodo` first, the PropertyChanged event fires before the subscription is registered and the notification is missed.
- **Sort tool/manifest names before asserting equality.** MCP doesn't guarantee ordering of `tools/list` or the manifest's nested arrays.
- **Phase 3.1 GUI tests are gated.** EC-8/EC-9 are `[Fact(Skip="...")]` by default. They need a desktop session AND `MARIONETTE_GUI_TESTS=1`. The default-skip behaviour means they don't gate CI green.
