# Phase 3.3 (3c) - Multi-Window Routing

**Status:** PASS (PARTIAL on GUI-only assertions - documented below)
**Date:** 2026-05-03
**SDK:** .NET 10.0.202 - WPF / Avalonia 11.x / WinAppSDK 1.8.260416003 - ModelContextProtocol 1.2.0 - Roslyn 4.14.0

## Goal & verdict

Phase 3.3 ships multi-window routing. A single `[McpRoot]` type can have N live instances simultaneously (typical: a TodoApp opening two MainWindows, each with its own `TodoListViewModel`). The runtime gives each live instance a stable per-process windowId (`w1`, `w2`, ...) and the LLM addresses any specific window via that ID through the existing tool surface.

Three layers cooperate:

1. **`IUiAutomationAdapter` contract gains an OPTIONAL `windowId` parameter** on every method that needs to address a specific window plus two new enumeration methods (`GetWindowIds` / `GetRootInstance`) and a `WindowsChanged` event. `null` windowId = "first / oldest live window" (single-window backward-compat).
2. **`RootInstanceTracker`** in `src/Marionette.NET.Runtime/Adapters/` is the shared per-process registry. Lock-protected, monotonic counter, `Track`/`Untrack`/`GetWindowIds`/`GetInstance`/`Snapshot`/`SnapshotAll` plus a `Changed` event. All three adapters compose it.
3. **Runtime tools (`MarionetteTools` + `MarionetteDispatch` + `DynamicToolRegistry`)** thread the `windowId` through to the adapter. `inspect_app_api` advertises a `windowIds` array on roots with 2+ live windows. `DynamicToolRegistry` registers per-window dynamic-tool variants (`<RootName>.<Method>:<windowId>`) and emits a debounced `tools/list_changed` notification on every adapter `WindowsChanged` event.

**Verdict: GO for Phase 3.4.** All non-GUI tests pass: solution builds clean Debug+Release with `0 Warnung(en) 0 Fehler`; integration tests run 7/7 + 3 skip without `-m:1`; source-gen 25/25; IL probe 0 hits across all 6 needles on all 4 stripped samples; StdioTest handshake against TodoApp / Avalonia Dashboard / WinUI FormLab all PASS. The GUI-only multi-window assertions (EC-10 + StdioTest `--two-windows`) are gated behind `MARIONETTE_GUI_TESTS=1` per the established Phase-3.1 pattern - they're manual-verification on a desktop session.

## Architecture summary

### Contract diff (`IUiAutomationAdapter`)

```csharp
// Phase 3.3 additions / changes
Task<byte[]>      CaptureScreenshotAsync(string? targetName, string? windowId, CancellationToken ct);
Task<object?>     ResolveControlAsync(string rootName, string controlName, string? windowId, CancellationToken ct);
Task<bool>        SimulateInputAsync(string rootName, string controlName, string kind,
                                     IReadOnlyDictionary<string, object?>? args,
                                     string? windowId, CancellationToken ct);
Task<bool>        RaiseEventAsync(string rootName, string controlName, string eventName,
                                  IReadOnlyDictionary<string, object?>? args,
                                  string? windowId, CancellationToken ct);

IReadOnlyList<string> GetWindowIds(string rootName);
object?               GetRootInstance(string rootName, string? windowId);
event EventHandler?   WindowsChanged;
```

Backward-compat invariant: `null` windowId on every existing call routes to "first / oldest live window" (the one originally tracked by the bridged factory). Phase 1/2/3.1/3.2 callers pass `null` and observe identical behaviour.

### `RootInstanceTracker` (shared utility)

`src/Marionette.NET.Runtime/Adapters/RootInstanceTracker.cs` is a self-contained AOT-clean state machine:

* Process-global monotonic `int s_nextCounter` -> string IDs `w<n>`. Counter never resets; closing a window does NOT free the ID for reuse. An identical-class reopen gets a fresh ID.
* Per-root `Dictionary<string, List<Entry>>` with a single guarding `lock`.
* Reference-equality dedup so a given instance only registers once even if `Track` is called twice (the bridged factory + a Window-class reconciliation walk both fire for the same MainWindow).
* `Untrack(instance)` removes by reference equality across all roots (used by Window.Closed handlers).
* `Changed` event raised after every successful mutation. Subscribers run in a try/catch so a failing handler can't escape into the adapter.
* Snapshot APIs (`GetWindowIds`, `Snapshot`, `SnapshotAll`) copy-out under the lock so callers can iterate without holding it.

### Per-adapter wiring

All three adapters now own a `RootInstanceTracker` and forward its `Changed` event onto their public `WindowsChanged`. The implementations (already present from the previous agent's pass; verified in this finishing sprint):

| Adapter | Tracker integration |
|---|---|
| **WPF** (`WpfUiAutomationAdapter` + `MarionetteWpf.AttachTo`) | Tracker constructed in `MarionetteWpf.AttachTo`, passed into the adapter ctor. `WrapRootsForUiThread` calls `tracker.Track(rootName, resolved)` on every successful factory dispatch (the live MainWindow OR a fresh-from-factory instance). `InstallWindowOpenHook` reconciles `Application.Windows` periodically (initial + on `Activated` + on Dispatcher idle); newly-opened Window-typed roots auto-register, and a per-Window `Closed` handler calls `tracker.Untrack(window)`. `MarionetteWpf.TrackInstance(rootName, instance)` lets adopters with non-Window roots (the TodoApp `--two-windows` ViewModel case) register a second ViewModel manually. |
| **Avalonia** (`AvaloniaUiAutomationAdapter` + `MarionetteAvalonia`) | Same shape - the bridged factory's resolution path is wrapped to call `tracker.Track(rootName, resolved)`. `GetWindowIds`/`GetRootInstance` delegate to the tracker. The classic-desktop Lifetime exposes a `Windows` list that drives the periodic reconciliation. |
| **WinUI** (`WinUiAutomationAdapter` + `MarionetteWinUI`) | Same shape adapted to WinUI 3's lack of `Application.Windows` - the tracker plus `WindowTracker` (the WinUI-specific lazy registry kept from Phase 3.2) cooperate. The previous agent left `MarionetteWinUI.s_currentAdapter` declared but never assigned; this sprint added the missing `s_currentAdapter = adapter;` line so `MarionetteWinUI.TrackInstance` works for non-Window roots. |

### Runtime tools (the wiring this sprint completed)

The previous agent stopped at the contract change. The runtime side was still on the old signature - the build failed in three places (`MarionetteTools.CaptureScreenshotAsync` line 212, `SimulateInputAsync` line 325, `RaiseEventAsync` line 416). This sprint threaded `windowId` through end-to-end:

**`src/Marionette.NET.Runtime/Tools/MarionetteTools.cs`:**
* `inspect_app_api(rootName?, windowId?)` - new `IUiAutomationAdapter` injection, calls `adapter.GetWindowIds(rootName)`. When `> 1`, the serialised root carries a `windowIds:["w1","w2"]` field. Single-window cases omit the field (compat with Phase 1/2/3.1/3.2 manifest consumers).
* `invoke_method(root, method, args?, windowId?)` - passes windowId through to `MarionetteDispatch.InvokeAsync`.
* `read_observable(root, property, windowId?)` - looks up the per-window instance via `adapter.GetRootInstance(root, windowId)` (falling back to `RegisteredRoot.Instance` when the adapter has nothing tracked, e.g. headless NoOp).
* `capture_screenshot(target?, windowId?)` - passes through to the adapter.
* `simulate_input(root, control, kind, args?, windowId?)` and `raise_event(root, control, event, args?, windowId?)` - same pass-through.

**`src/Marionette.NET.Runtime/Tools/MarionetteDispatch.cs`:**
* `InvokeAsync(...)` gained a `string? windowId` parameter. Per-window instance resolution: `var instance = adapter.GetRootInstance(rootName, windowId) ?? root.Instance;`. Loop-protection / dispatch / async-unwrap unchanged.

**`src/Marionette.NET.Runtime/Tools/DynamicToolRegistry.cs` (the meatiest change):**
* Implements `IDisposable`. `RegisterInitial(server)` subscribes to `_adapter.WindowsChanged += OnAdapterWindowsChanged`.
* `ComputeEntries()` now per (root, callable):
  * Always emits the bare-form `<RootName>.<MethodName>` tool (windowId=null).
  * When `_adapter.GetWindowIds(rootName).Count > 1`, ALSO emits one per-window variant `<RootName>.<MethodName>:<windowId>` per live window.
  * The hash for per-window variants includes the windowId in the canonical signature (`rootName + "@" + windowId`) so each variant gets a distinct stable identity.
* `BuildTool(entry)` closes over `entry.WindowId` and routes through `MarionetteDispatch.InvokeAsync(... capturedWindowId, ct)`. The bare tool gets `null` windowId; per-window variants get their captured ID.
* `ScheduleRefresh()` debounces `WindowsChanged` events through a 100ms `System.Threading.Timer`. Multiple Changed events that arrive within 100ms collapse into a single `RefreshFromManifestAsync` call -> a single `tools/list_changed` notification. Mitigates the documented two-window-startup race where a tracker fires twice in quick succession.
* `Dispose()` unsubscribes and disposes the timer.

The lock discipline: `RegisterInitial` and `RefreshFromManifestAsync` both hold `_lock` during the diff to prevent concurrent mutations from racing. The timer callback's `RefreshFromManifestAsync` invocation enters that same lock.

### Sample.Wpf.TodoApp `--two-windows` flag

* `Program.cs` parses `--two-windows`. When present, sets the static `App.OpenSecondWindowOnStartup = true` BEFORE `RunGui()`.
* `App.xaml.cs.OnStartup`: after the existing `MarionetteWpf.AttachTo` wiring, schedules a `Dispatcher.BeginInvoke(OpenSecondWindow, ApplicationIdle)` callback. The deferral lets the first window's tracker registration (via the bridged factory) win the lower windowId.
* `OpenSecondWindow()`:
  * `var freshVm = new TodoListViewModel(useShared: false);` - the new ctor overload skips the `s_shared` install, so the second VM is a distinct instance.
  * `MarionetteWpf.TrackInstance(typeof(TodoListViewModel).FullName!, freshVm);` - manually registers the fresh VM with the tracker (WPF's automatic Window-class reconciliation can't reach a ViewModel).
  * Constructs `new MainWindow(freshVm)` with the new `MainWindow(TodoListViewModel viewModel)` ctor overload. Default ctor (`new MainWindow()`) still binds to `TodoListViewModel.Shared` so single-window behaviour is unchanged.
  * Offsets the window position +80px and titles it "TodoApp - Window #2" so the user can tell the two apart.

`MainWindow.xaml.cs` now keeps a private `_vm` reference and routes button clicks (`AddButton_Click`, `RemoveItemButton_Click`, `ClearCompletedButton_Click`) through it instead of always calling `TodoListViewModel.Shared`. The pre-seed of two demo items only runs on the FIRST construction of `Shared` (`ReferenceEquals(_vm, TodoListViewModel.Shared) && _vm.Items.Count == 0`) so the secondary window starts empty - the multi-window assertions can rely on a clean baseline.

### Parallel-build collision fix

`tests/Marionette.NET.Integration/Marionette.NET.Integration.csproj` previously declared a `<Target Name="BuildTodoAppForIntegrationTests" BeforeTargets="Build">` that called `dotnet build` on the sample. Solution-level parallel builds (`dotnet build Marionette.NET.sln` without `-m:1`) raced over `samples/Sample.Wpf.TodoApp/obj/` and intermittently failed with file-locked errors (PHASE2_FINDINGS follow-up #1).

The fix (option a from the brief): drop the BeforeBuild target. `TodoAppFixture` now builds the sample lazily exactly once per test session via `EnsureSampleBuiltOnce("Debug")`:
* Static `s_buildLock` + `bool s_built` flag.
* Walks up from the test assembly's location to find `Marionette.NET.sln`, then probes for `samples/Sample.Wpf.TodoApp/Sample.Wpf.TodoApp.csproj`.
* Spawns `dotnet build <csproj> -c Debug -p:EnableMcpAutomation=true --nologo -v:quiet`, drains stdout/stderr to avoid pipe-buffer deadlock, throws on non-zero exit code with the captured output for forensic detail.

Verified: `dotnet test tests/Marionette.NET.Integration/Marionette.NET.Integration.csproj --no-build -c Debug` runs cleanly without `-m:1`, 7 passing + 3 skipped (EC-8, EC-9, EC-10), 7s wall.

### EC-10 multi-window eval case

`tests/Marionette.NET.Integration/EvalCases.cs` adds `EC10_MultiWindowRouting_Works`, gated behind the `MARIONETTE_GUI_TESTS=1` env var (mirrors EC-8 / EC-9 pattern). Asserts:
1. `tools/list` contains both `TodoListViewModel.AddTodo:w1` and `TodoListViewModel.AddTodo:w2` AND the bare-form `TodoListViewModel.AddTodo`.
2. `inspect_app_api` reports a 2-element `windowIds` array on the TodoListViewModel root.
3. `invoke_method windowId:"w1" AddTodo("for w1")` grows w1's `read_observable windowId:"w1" TotalCount` by exactly 1; w2's count is unchanged.
4. Mirror check for windowId:"w2".

A 2-second settle delay covers the deferred second-window construction + the coalesced `tools/list_changed` notification; initialize timeout bumped to 60s. The `TodoAppFixture(twoWindows: true)` ctor implies guiMode and appends `--two-windows` to the child argv.

### StdioTest `--two-windows` mode

`.phase0/StdioTest/Program.cs` accepts `--two-windows` (auto-implies `--gui` and currently requires `--todoapp`). When set:
* The child argv gains `--two-windows`.
* `MARIONETTE_MAX_DEPTH=50` is exported so the multi-window assertion sequence doesn't trip loop-protection.
* After the existing TodoApp assertion suite, the harness runs the same four-check pattern as EC-10 (per-window dynamic tools, inspect_app_api windowIds, per-window AddTodo isolation w1, per-window AddTodo isolation w2). New helpers `ReadObservableIntScoped` and `InvokeMethodAsyncScoped` mirror the existing helpers but pass a `windowId` argument through.

The verdict label switches to `Phase 3.3 TodoApp --two-windows handshake`.

### CI workflow extension

`.github/workflows/ci.yml`'s `aot-publish-smoke` job gains four new steps after the existing StripeProbe AOT publishes:

1. `AOT publish - Avalonia Dashboard stripped (=false)` -> `publish-aot-avalonia-off/` + `aot-avalonia-off.binlog`.
2. `AOT publish - Avalonia Dashboard full (=true)` -> `publish-aot-avalonia-on/` + `aot-avalonia-on.binlog`.
3. `Verify stripped Avalonia AOT binary was produced` (size check; mirrors the StripeProbe pattern that learned WPF GUI under AOT crashes intrinsically and stopped launching the binary).
4. `Smoke-test AOT-on Avalonia binary via stdio handshake` -> `dotnet .phase0/StdioTest/.../StdioTest.dll <publish-aot-avalonia-on/Sample.Avalonia.Dashboard.exe> --avalonia` -> `stdio-aot-avalonia.log`.

The failure-upload step's `path:` block adds the new artifacts (`aot-avalonia-{off,on}.binlog`, `stdio-aot-avalonia.log`, `publish-aot-avalonia-{off,on}/**/*`).

This addresses PHASE2_FINDINGS follow-up #2. Whether the AOT GUI launch crashes intrinsically (as WPF does) is documented as deferred-to-first-push observation - the sandbox here can't run the publish without the C++ desktop workload. Local sandbox build matrix confirmed the .csproj wiring + `EnableMcpAutomation=false` strip path are intact (Avalonia Dashboard stripped Release builds clean, IL probe 0/6 hits).

## Build matrix

| Step | Result |
|---|---|
| `dotnet build Marionette.NET.sln -c Debug` | 0 Warnung 0 Fehler (5.78s) |
| `dotnet build Marionette.NET.sln -c Release` | 0 Warnung 0 Fehler (4.33s) |
| `dotnet test tests/Marionette.NET.SourceGenerator.Tests/...` | **25/25 PASS** (1s) - no new snapshot needed: per-window naming is a runtime-only concern, the source generator emits the same descriptors as before. |
| `dotnet test tests/Marionette.NET.Integration/... --no-build` | **7/7 PASS + 3 SKIP** (EC-8, EC-9, EC-10) (7s); runs cleanly without `-m:1`. |
| IL probe StripeProbe.dll (stripped Release) | **0/6 needles** PASS |
| IL probe TodoApp.dll (stripped Release) | **0/6 needles** PASS |
| IL probe Sample.Avalonia.Dashboard.dll (stripped Release) | **0/6 needles** PASS |
| IL probe Sample.WinUI.FormLab.dll (stripped Release) | **0/6 needles** PASS |
| StdioTest --todoapp (headless) | **PASS** (12/12 checks) |
| StdioTest --avalonia (headless) | **PASS** (14/14 checks) |
| StdioTest --winui (headless) | **PASS** (18/18 checks) |
| StdioTest <StripeProbe> (default) | **PASS** (9/9 checks) |
| `.phase1/demo.ps1 -NoBuild` | **PASS** (12/12 checks) |
| StdioTest --two-windows (GUI) | **MANUAL** - requires interactive desktop; sandbox can't run. Auto-implied gating via `MARIONETTE_GUI_TESTS=1`. |
| EC-10 (GUI) | **SKIPPED by design** - same gating as EC-8/EC-9; manual verification when an attended desktop is available. |
| AOT publish Avalonia Dashboard (CI step) | **DEFERRED** to first push - sandbox lacks the C++ desktop workload. Workflow YAML mirrors the working StripeProbe pattern. |

## Stripping invariant

Cross-checked: the new windowId-aware code lives entirely in the Runtime tools (MarionetteTools / MarionetteDispatch / DynamicToolRegistry) and the adapter assemblies. None of those types are referenced by the stripped sample Release builds (`EnableMcpAutomation=false` removes the conditional ProjectReferences via `build/Marionette.NET.targets`). The IL probe confirmed: 0 hits across all 6 needles (Marionette.NET.Runtime, Adapter.Wpf, Adapter.Avalonia, Adapter.WinUI, Marionette.Ai, ModelContextProtocol) on all 4 stripped sample DLLs.

## AOT cleanliness

The Phase 3.3 changes added no reflection. The new code uses:
* `RootInstanceTracker` - typed `Dictionary<string, List<Entry>>` + a single `Interlocked.Increment` counter.
* `MarionetteTools.SerializeRoot` - `IUiAutomationAdapter.GetWindowIds(string)` is a typed virtual call.
* `DynamicToolRegistry.OnAdapterWindowsChanged` - `EventHandler` delegate.
* `DynamicToolRegistry.ComputeEntries` - typed list ops + the existing `ToolIdentity.ComputeStableHash` (UTF-8 + SHA-256, AOT-clean).
* `DynamicToolRegistry.BuildTool` - closure capture + `Func<RequestContext<...>, CancellationToken, ValueTask<CallToolResult>>` matches the SDK's documented dynamic-tool delegate shape (already used in Phase 2.2; no new reflection).
* `System.Threading.Timer` for the 100ms coalesce window - AOT-friendly.

The pre-existing `WpfEventRaiser.RaiseEventReflectively` and `WinUiEventRaiser` reflection paths from Phase 3.1/3.2 are unchanged. They keep their `[UnconditionalSuppressMessage]` attributes; the trim caveat documented in those XML docs still applies.

## Stdout purity

Verified by EC-5 (still passing, 7/7 integration test) plus the StdioTest harness's own per-line `JsonDocument.Parse` validation across all four sample modes. Stdout summary on every run: 0 pollution lines.

## Phase 3.4 hand-off

Phase 3.4 picks up fresh-tree (the host signals an `[McpEvent]` whenever a tracked tree mutates) and watch-controls (the runtime auto-subscribes to N-bounded controls so `read_observable` reads return cached values).

**Inputs Phase 3.4 inherits from 3.3:**
* `RootInstanceTracker.Snapshot` / `SnapshotAll` give Phase 3.4 a per-process iterator over every live root instance for fresh-tree's tree-mutation hooks.
* `IUiAutomationAdapter.WindowsChanged` is the cleanest hook for "the set of live trees just changed" - Phase 3.4's tree-mutation provider can subscribe and walk on every event.
* `DynamicToolRegistry.RefreshFromManifestAsync` already debounces 100ms; Phase 3.4 can reuse the same timer if its tree-mutation throttle window matches.

**Open follow-ups for Phase 3.4 to triage:**
1. The Avalonia AOT CI step is wired but unverified - first CI push will tell us whether the `Sample.Avalonia.Dashboard` AOT-on stdio handshake holds. If it doesn't, the WPF-known WPF+AOT GUI crash precedent applies and we update the workflow to skip the AOT-on launch the same way.
2. EC-10 / `--two-windows` are GUI-gated; bringing them to attended-CI (or a Windows desktop runner) would close the manual-verification gap.
3. `inspect_app_api`'s `windowIds` field is currently emitted only when `> 1` live windows exist. Phase 3.4 may want to also expose it on single-window roots (always-array) for consistency with the per-window dynamic-tool variants - but that's a manifest-shape change that needs the Phase 5 manifest-versioning conversation first.

## Files touched (this finishing sprint)

* `src/Marionette.NET.Runtime/Tools/MarionetteTools.cs` - threaded windowId through every tool method, `SerializeRoot` advertises `windowIds`, `read_observable` resolves per-window instance.
* `src/Marionette.NET.Runtime/Tools/MarionetteDispatch.cs` - `InvokeAsync` gained `string? windowId`; per-window instance resolution.
* `src/Marionette.NET.Runtime/Tools/DynamicToolRegistry.cs` - implements `IDisposable`; subscribes to `WindowsChanged`; `ComputeEntries` emits per-window variants when adapter reports >1 windows; `BuildTool` captures windowId; 100ms coalesced refresh timer.
* `src/Marionette.NET.Adapter.WinUI/MarionetteWinUI.cs` - assigned `s_currentAdapter = adapter` (was declared but unused; previous agent's omission).
* `samples/Sample.Wpf.TodoApp/Program.cs` - parses `--two-windows`; sets `App.OpenSecondWindowOnStartup`.
* `samples/Sample.Wpf.TodoApp/App.xaml.cs` - on `OpenSecondWindowOnStartup`, schedules a deferred second-window open with a fresh non-Shared ViewModel.
* `samples/Sample.Wpf.TodoApp/MainWindow.xaml.cs` - new `MainWindow(TodoListViewModel)` ctor overload; pre-seed only on the first Shared construction.
* `samples/Sample.Wpf.TodoApp/TodoListViewModel.cs` - new `TodoListViewModel(bool useShared)` ctor overload.
* `tests/Marionette.NET.Integration/Marionette.NET.Integration.csproj` - dropped the `<Target Name="BuildTodoAppForIntegrationTests">` BeforeBuild target.
* `tests/Marionette.NET.Integration/TodoAppFixture.cs` - lazy `EnsureSampleBuiltOnce` static; new `twoWindows` ctor parameter.
* `tests/Marionette.NET.Integration/EvalCases.cs` - added `EC10_MultiWindowRouting_Works` ([Fact(Skip=...)]).
* `.phase0/StdioTest/Program.cs` - `--two-windows` flag; new helpers `ReadObservableIntScoped` / `InvokeMethodAsyncScoped`; multi-window assertion block at the end of the TodoApp suite.
* `.github/workflows/ci.yml` - added Avalonia Dashboard AOT publish + smoke + artifact upload to `aot-publish-smoke`.
* `.phase3/3c-multi-window.md` - this report.

## Files NOT touched (per brief constraints)

`MASTERPLAN.md`, `LICENSE`, `.gitignore`, `Directory.Build.props`, `global.json`, `PHASE0_FINDINGS.md`, `PHASE1_FINDINGS.md`, `PHASE2_FINDINGS.md`, `README.md`, source code in `src/Marionette.NET.Abstractions/`, source code in `src/Marionette.NET.SourceGenerator/`. The previous agent's changes to the three adapters' `*UiAutomationAdapter.cs` + `Internal/VisualTreeFinder.cs` + `Marionette*.cs` bootstrap files were verified by build (zero diff needed beyond the WinUI `s_currentAdapter` assignment). The shared `RootInstanceTracker.cs` + `IUiAutomationAdapter.cs` + `NoOpAdapter.cs` + abstractions Manifest types were also left as the previous agent shipped them.

## Working tree status

Dirty - no commits per brief constraint.
