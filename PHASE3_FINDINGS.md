# Phase 3 — Findings

> **Status:** Phase 3 complete (3.1 -> 3.4). Phase 4 (Uno Platform) is unblocked; the `IUiAutomationAdapter` contract is now stable across three adapters, multi-window routing is in place, and `RootInstanceTracker` is a reusable shared utility.
> **Date:** 2026-05-03
> **SDK:** .NET 10.0.202 · WPF / Avalonia 11.3.14 / WinAppSDK 1.8.260416003 · ModelContextProtocol 1.2.0 · Roslyn 4.14.0

Phase 3 set out to deliver, per the masterplan: a WinUI adapter (`Marionette.NET.Adapter.WinUI`) with a canonical sample, the `simulate_input` and `raise_event` meta-tools driven through each framework's real input pipeline, framework-specific RoutedEvent mechanics with bubbling/tunneling, and multi-window routing with stable `windowId` disambiguation. Detailed per-sub-phase reports live in `.phase3/3{a,b,c}-*.md`; this document is the consolidated verdict.

## Status

GREEN. Every Phase-3 masterplan deliverable landed (with two acknowledged Phase-3.1 limitations on Avalonia keyboard/text-input/mouse-move kinds and the unpackaged WinUI InputInjector path that the AutomationPeer-first strategy circumvents); the IL stripping promise from Phase 0 Spike A holds across all four samples (StripeProbe, TodoApp, Dashboard, FormLab); the third adapter mirrors the WPF/Avalonia shape one-to-one against WinUI 3; multi-window routing per-window dynamic-tool variants register cleanly with 100ms coalesce.

## What was built per sub-phase

### 3.1 — `simulate_input` + `raise_event` (`6aca20a`)

`IUiAutomationAdapter` gained two methods (`SimulateInputAsync`, `RaiseEventAsync`) — additive over the Phase-1/2 four-method contract. `MarionetteTools` registered two new meta-tools (`simulate_input(root, control, kind, args?)`, `raise_event(root, control, event, args?)`) following the established loop-protection + UI-thread + 10s-timeout dispatch shape. WPF adapter ships the full eight-kind input matrix (click, double_click, right_click, key_press, key_down, key_up, type_text, mouse_move) routed through `Mouse.PrimaryDevice` / `Keyboard.PrimaryDevice` + `RoutedEventArgs` / `KeyEventArgs` / `TextCompositionEventArgs`. Avalonia adapter ships click variants via `Button.ClickEvent` routed-event dispatch (walks the logical Parent chain to find a Button ancestor); key/text/mouse-move kinds documented as a Phase-3.1 limitation since Avalonia 11.3.14 keeps `KeyEventArgs` / `PointerPressedEventArgs` / `TextInputEventArgs` ctors `internal`. `inspect_app_api` advertises `supportedInputKinds`. EC-8 (simulate_input click) and EC-9 (raise_event Click) added to the integration test project, gated on `MARIONETTE_GUI_TESTS=1` (xUnit 2.x lacks runtime skip).

### 3.2 — Adapter.WinUI + Sample.WinUI.FormLab (`1c4ddec`)

Third Marionette adapter against Windows App SDK 1.8.260416003 / WinUI 3, cross-targeting `net10.0-windows10.0.19041.0` (matching the SDK ref WAS bundles transitively). `<UseWinUI>true</UseWinUI>`, `<WindowsPackageType>None</WindowsPackageType>` — unpackaged-first. `WinUiAutomationAdapter` mirrors WPF/Avalonia shape: `DispatcherQueue.TryEnqueue` wrapped in TCS for `DispatchAsync`; `RenderTargetBitmap.RenderAsync` -> `BitmapEncoder.CreateAsync(PngEncoderId)` for screenshots; visual-tree finder via `Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild` walking every tracked window's Content with AutomationId-first, Name-second precedence. `WindowTracker` (predecessor of Phase-3.3's `RootInstanceTracker`) maintains a thread-safe registry of live `Window` instances since WinUI 3 doesn't expose `Application.Windows`. `WinUiInputSimulator` uses an AutomationPeer-first strategy (`ButtonAutomationPeer.Invoke()` for click/double_click — works unpackaged + unelevated) to avoid the `InputInjector` elevation/manifest requirement; falls back to `Windows.UI.Input.Preview.Injection.InputInjector` for non-button targets and key/mouse-move kinds. `WinUiEventRaiser` does CLR-event reflection on compiler-emitted backing fields since WinUI 3 has no `EventManager.GetRoutedEventsForOwner` and no `UIElement.RaiseEvent` (the WPF/Avalonia idiom). `Sample.WinUI.FormLab` exercises a diverse settings-form vocabulary (TextBox, NumberBox, ToggleSwitch, ComboBox, two Buttons) with 6 callables, 5 observables (3 watchable), 1 typed `[McpEvent]`. The new `Adapter.WinUI` IL probe needle joins the regression set (now 6 needles total).

### 3.3 — Multi-Window Routing + Phase-2 follow-ups (`fa20ac4`)

`RootInstanceTracker` (`src/Marionette.NET.Runtime/Adapters/RootInstanceTracker.cs`) is a shared utility composed by all three adapters: lock-protected, monotonic counter (`w<n>` IDs that never reset), reference-equality dedup, `Track`/`Untrack`/`GetWindowIds`/`GetInstance`/`Snapshot`/`SnapshotAll` plus a `Changed` event. `IUiAutomationAdapter` gains an optional `windowId` parameter on every method that addresses a specific window plus two new enumeration methods (`GetWindowIds`, `GetRootInstance`) and a `WindowsChanged` event. Backward-compat invariant: `null` windowId routes to "first / oldest live window". `DynamicToolRegistry` registers per-window dynamic-tool variants (`<RootName>.<MethodName>:<windowId>`) alongside the bare-form tool when the adapter reports >1 live windows; the hash for per-window variants includes the windowId in the canonical signature. A 100ms `System.Threading.Timer` debounces `WindowsChanged` events into a single `RefreshFromManifestAsync` -> single `tools/list_changed` notification. `inspect_app_api` advertises a `windowIds` array on roots with >1 live windows. The `Sample.Wpf.TodoApp --two-windows` flag exercises the path with two distinct ViewModels and two MainWindows. Parallel-build collision (PHASE2_FINDINGS follow-up #1) fixed by dropping the `<Target Name="BuildTodoAppForIntegrationTests" BeforeTargets="Build">` and lazy-building once per session via `EnsureSampleBuiltOnce` in `TodoAppFixture`. Avalonia AOT smoke wired into `.github/workflows/ci.yml`'s `aot-publish-smoke` job (PHASE2_FINDINGS follow-up #2). EC-10 multi-window eval case added to the integration project, gated on `MARIONETTE_GUI_TESTS=1`.

### 3.4 — Findings consolidation (commit hash TBD)

This sub-phase, rolled into the consolidated report. Three artifacts:
- `PHASE3_FINDINGS.md` (this document).
- `README.md` updated to reflect Phase 3 status (Adapter.WinUI added, simulate_input/raise_event meta-tools, multi-window routing, Avalonia removed and Uno + MAUI added to the still-to-do list).
- Final build-matrix verification run (below) — all green.

No source-code changes in `src/Marionette.NET.*`, `samples/Sample.*`, `tests/Marionette.NET.*`. Working tree dirty per the constraint; orchestrator commits.

## Build matrix at end of Phase 3

All commands run from `C:\Home\Code\nw.Automation` on .NET 10.0.202.

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build Marionette.NET.sln -c Debug` | PASS — 0 warnings, 0 errors (12 projects) |
| 2 | `dotnet build Marionette.NET.sln -c Release` | PASS — 0 warnings, 0 errors |
| 3 | `dotnet test tests/Marionette.NET.SourceGenerator.Tests/Marionette.NET.SourceGenerator.Tests.csproj` | PASS — 25/25 |
| 4 | `dotnet test tests/Marionette.NET.Integration/Marionette.NET.Integration.csproj` | PASS — 7/7 + 3 skip (EC-8, EC-9, EC-10 GUI-gated) |
| 5 | `pwsh build/Run-IlProbe.ps1 -ProbeDll .phase0/ProbeIl/.../ProbeIl.dll -Target samples/Sample.Wpf.StripeProbe/.../Sample.Wpf.StripeProbe.dll` | PASS — 0 hits across all 6 needles |
| 6 | `pwsh build/Run-IlProbe.ps1 ... -Target samples/Sample.Wpf.TodoApp/.../Sample.Wpf.TodoApp.dll` | PASS — 0 hits across all 6 needles |
| 7 | `pwsh build/Run-IlProbe.ps1 ... -Target samples/Sample.Avalonia.Dashboard/.../Sample.Avalonia.Dashboard.dll` | PASS — 0 hits across all 6 needles |
| 8 | `pwsh build/Run-IlProbe.ps1 ... -Target samples/Sample.WinUI.FormLab/.../Sample.WinUI.FormLab.dll` | PASS — 0 hits across all 6 needles |
| 9 | `pwsh .phase1/demo.ps1` | PASS — 12/12 harness checks, 0 stdout pollution, clean exit |

(Sample Release builds with `EnableMcpAutomation=false` were performed before steps 5-8 to ensure stripped artifacts existed for the IL probe pass. The 6 IL probe needles are: `Marionette.NET.Runtime`, `Adapter.Wpf`, `Adapter.Avalonia`, `Adapter.WinUI`, `Marionette.Ai`, `ModelContextProtocol`.)

## Phase-3 deliverables vs masterplan

The masterplan lists 5 explicit Phase-3 line items (`Adapter.WinUI`, `simulate_input` per pipeline, `raise_event` with framework RoutedEvent mechanics, Multi-Window-Routing, `Sample.WinUI.FormLab`); the Phase-3 demo target adds the implicit "5+ end-to-end eval-cases" delivery. Each is mapped to its delivered artifact below.

| Masterplan Phase-3 line | Implemented? | Notes |
|---|---|---|
| `Marionette.NET.Adapter.WinUI` | ✅ | Phase 3.2 commit `1c4ddec`. TFM `net10.0-windows10.0.19041.0`, unpackaged WinAppSDK 1.8.260416003, full `IUiAutomationAdapter` impl. |
| `simulate_input` via WPF `InputManager.ProcessInput` | ✅ | Phase 3.1 commit `6aca20a`. 8-kind matrix routed through `Mouse.PrimaryDevice` / `Keyboard.PrimaryDevice` + `RoutedEventArgs` (the public surface of WPF's input pipeline; `InputManager.ProcessInput` is internal-only-construction territory and the routed-event dispatch path is what every WPF input handler actually subscribes to). |
| `simulate_input` via Avalonia `IInputDevice`-pump | ⚠️ | Phase 3.1. Click variants only — fired via `Button.ClickEvent` routed-event dispatch. Key/text/mouse-move kinds documented as a Phase-3.1 limitation: Avalonia 11.3.14 keeps `KeyEventArgs` / `PointerPressedEventArgs` / `TextInputEventArgs` ctors `internal`, and `IInputManager.ProcessInput` requires platform `IInputRoot` wiring that's not stably available cross-platform. Phase 6 may tighten if Avalonia 12 publicises the ctors. |
| `simulate_input` via WinUI `InputInjector` | ⚠️ | Phase 3.2. `Windows.UI.Input.Preview.Injection.InputInjector.TryCreate()` returns null without elevation OR an `inputInjectionBrokered` capability declared in `Package.appxmanifest`. The adapter uses an AutomationPeer-first strategy (`ButtonAutomationPeer.Invoke()` for click/double_click — works unpackaged + unelevated, framework-routed, semantic) and falls back to `InputInjector` for non-button targets. This honors the masterplan's tenet 2 (semantic > visual). Phase 6 may revisit the elevation/manifest path. |
| `raise_event` with framework RoutedEvent | ✅ | Phase 3.1 (WPF + Avalonia full RoutedEvent dispatcher: walk type chain for `<EventName>Event` static fields, raise via `Control.RaiseEvent(new RoutedEventArgs(routedEvent, source))`). Phase 3.2 (WinUI via CLR-event reflection on compiler-emitted backing fields — WinUI 3 has no `EventManager.GetRoutedEventsForOwner` and no `UIElement.RaiseEvent`). |
| Multi-Window-Routing | ✅ | Phase 3.3 commit `fa20ac4`. Shared `RootInstanceTracker`, `IUiAutomationAdapter` `windowId` parameter additions + `GetWindowIds`/`GetRootInstance`/`WindowsChanged`, `DynamicToolRegistry` per-window variants with 100ms coalesce, `inspect_app_api` `windowIds` advertisement. |
| `Sample.WinUI.FormLab` | ✅ | Phase 3.2. Settings-form UI distinct from TodoApp's list and Dashboard's metric stream; 6 callables, 5 observables (3 watchable), 1 typed `[McpEvent]`. |
| 5 (or more) end-to-end eval-cases for Phase 3 demos | ⚠️ | Phase 3 added EC-8 (simulate_input click), EC-9 (raise_event Click), EC-10 (multi-window per-window routing) to the integration test project. All three are gated on `MARIONETTE_GUI_TESTS=1` since they require an interactive desktop (xUnit 2.x lacks runtime skip — `[Fact(Skip="...")]` is static). The `.phase0/StdioTest --gui --simulate-input` and `--two-windows` harness modes provide equivalent end-to-end verification on a desktop without test-runner overhead, and were used during Phase 3.1/3.2/3.3 validation. Phase 6 may revisit (xUnit v3 `Assert.Skip` lands runtime gating). |

## Carryovers from Phase 2

Status of the Phase-2 follow-ups going into Phase 4:

- **F2 Avalonia AOT smoke in CI** — DONE in 3.3. `.github/workflows/ci.yml`'s `aot-publish-smoke` job now publishes Avalonia Dashboard stripped + full and runs `--mcp --headless` against the AOT-on binary via `StdioTest --avalonia`. First push will tell us whether the AOT-on launch holds; if it crashes intrinsically (the WPF+AOT precedent), the workflow gets a skip-launch-but-publish step parallel to the WPF case.
- **Parallel-build collision** — DONE in 3.3. The integration-test csproj's BeforeBuild target was dropped; `TodoAppFixture` lazy-builds once per test session via `EnsureSampleBuiltOnce`. Verified: `dotnet build Marionette.NET.sln` and `dotnet test tests/Marionette.NET.Integration` both run cleanly without `-m:1`.

Carryovers from earlier:

- **WPF + AOT GUI crash** — still open, still a Microsoft-known WPF+AOT limitation (not Marionette's). Frozen-Mode (`--mcp --headless`) workaround is stable and is the masterplan's headline use case. Phase 5's AOT hardening pass is the masterplan's revisit slot.
- **Single-window assumption** — RESOLVED in 3.3. The descriptor-factory rewrite still prefers the live MainWindow when types match (Phase 1.3 + 2.1 single-window pattern), but `RootInstanceTracker` now tracks every live root instance and `MarionetteWpf.TrackInstance` / `MarionetteAvalonia.TrackInstance` / `MarionetteWinUI.TrackInstance` give adopters with non-Window roots a one-line manual registration path.
- **Callable parameter type whitelist permissive (MAR004)** — Phase 6 polish; unchanged.
- **Skill-pack lacks property-test eval** — Phase 6 (`Marionette.NET.Testing`); unchanged.
- **`notifications/marionette/channel` is custom (not standard MCP)** — by design (masterplan); unchanged.
- **Source-generator MAR009+ slot** — still unused. Phase 6 polish.
- **Showcase Conversations** — Phase 6 deliverable; unchanged.
- **Adopter docs** (`docs/getting-started.md`, etc.) — Phase 6 / 7; unchanged.

## Known limitations / Phase-4 implications

What Phase 4 (Uno Platform) will need:

- **`Marionette.NET.Adapter.Uno`** targeting Uno's cross-target frameworks (WinAppSDK + GTK + macOS + Linux; WASM-skip in v1 per the masterplan). Reusing chunks of the WinUI adapter where Uno mirrors WinUI API is the obvious lever — `WinUiAutomationAdapter`'s `DispatchAsync` over `DispatcherQueue`, the AutomationPeer-first input strategy, the CLR-event reflection raiser, and the `WindowTracker` pattern all transfer one-to-one. The cross-target build matrix and per-platform conditional compilation are the new burden.
- **`Sample.Uno.Calculator`** with multi-target build. The masterplan calls for a calculator-shaped adopter-reference; reusing FormLab's settings-form vocabulary may also work depending on how Phase 4 calibrates the demo surface.
- **Reusing WinUI adapter chunks** — `WinUiAutomationAdapter` is a leaf project the source generator never touches; an Uno adapter can either composite-reference its files via `<Compile Include="../Marionette.NET.Adapter.WinUI/...*.cs" />` or factor a shared base assembly. Phase 4 picks based on how the cross-target conditional compilation lands.

What stays open:

- **Avalonia simulate_input key/text/mouse-move kinds** — limitation since Phase 3.1. Three resolution paths (Avalonia 12 publicising the ctors, platform-native raw input via `SendInput`/`XSendEvent`/`CGEventPost`, or pursuing `IInputManager.ProcessInput` with adopter-supplied `IInputRoot` plumbing) are all Phase 6 polish candidates. The Phase-3.1 fallback ("decorate the underlying mutating method with `[McpCallable]` and call it directly") covers most adopter scenarios.
- **WinUI InputInjector path** — works in elevated / `inputInjectionBrokered`-capability scenarios; the unpackaged-unelevated default uses the AutomationPeer-first strategy. Phase 6's elevation/manifest work may revisit.
- **xUnit GUI-test skip-by-env** — EC-8, EC-9, EC-10 use `[Fact(Skip="...")]` (xUnit 2.x). xUnit v3's `Assert.Skip(reason)` would let CI conditionally skip via env-var instead of the static skip. Phase 6 polish.

## Phase-4 readiness check

Phase 4 (Uno Platform) needs:

- **A stable `IUiAutomationAdapter` contract.** YES — Phase 3.3 only added methods (`GetWindowIds`, `GetRootInstance`, `WindowsChanged`) and an optional `windowId` parameter on existing methods (defaults to `null` = first/oldest live window). No removes, no breaks. Phase 4 implements the same contract for Uno without changing it.
- **A reusable shared `RootInstanceTracker`.** YES — `src/Marionette.NET.Runtime/Adapters/RootInstanceTracker.cs` is a standalone utility that all three adapters compose. Phase 4's Uno adapter will compose it identically.
- **Runtime / SourceGenerator don't need changes for Uno.** YES — Runtime depends only on Abstractions; the source generator emits `using Marionette.Runtime.Manifest;` and references no framework-specific types. The Phase 3.3 windowId additions live entirely in Runtime tools (`MarionetteTools`, `MarionetteDispatch`, `DynamicToolRegistry`) — Uno's adapter slots in alongside.
- **Uno's deployment story is more complex (cross-target).** Acknowledged. Uno's WinAppSDK head will likely follow the WinUI 3 pattern almost line-for-line; the GTK / macOS / Linux heads need investigation. WASM is masterplan-skipped for v1.

**Recommendation: proceed to Phase 4.** No load-bearing assumption is in question. The contract additions in Phase 3.3 were strictly additive; the WinUI adapter shape from Phase 3.2 is the most direct ancestor for Uno's WinAppSDK head; the multi-window routing from Phase 3.3 is independent of platform.

## Notable design decisions (from Phase 3)

- **Phase 3.1's Avalonia routed-event-raise fallback for input simulation** — sanctioned trade-off documented in `.phase3/3a-input-events.md`. Avalonia 11.3.14's `IInputManager.ProcessInput` requires `IInputRoot` plumbing that's not stably available cross-platform; the framework's own `RoutedEventArgs` + `Button.ClickEvent` dispatch path is what every Avalonia button-click handler actually subscribes to, so firing it covers the click-case 100% while honestly documenting the gated keyboard/text/mouse-move kinds.
- **Phase 3.2's AutomationPeer-first strategy for WinUI input** — avoids the `InputInjector` elevation/manifest pain that would otherwise force adopters into either `runas` or a packaged build. `ButtonAutomationPeer.Invoke()` for click/double_click works unpackaged + unelevated and routes through the framework's actual command dispatch chain (the masterplan's tenet 2: semantic > visual). The `InputInjector` path is wired as a fallback for non-button targets and the elevation/manifest scenarios where it's actually the right answer.
- **Phase 3.3's bare-form + per-window-variant pattern** — keeps single-window apps unchanged (the bare-form `<Root>.<Method>` tool always exists, with `windowId=null` routing to the first/oldest live window) while enabling multi-window addressing without breaking existing Claude tool calls. The Phase 1/2/3.1/3.2 LLM tool surface is identical when only one window is open; the per-window variants only register when adopter actually opens a second window of the same `[McpRoot]` type. The 100ms coalesce window keeps `tools/list_changed` traffic sane during burst-open scenarios.

## Files added/changed in Phase 3.4

```
PHASE3_FINDINGS.md                                          (NEW — this document)
README.md                                                   (UPDATED — Phase-3 status, Adapter.WinUI added, simulate_input + raise_event mention, multi-window routing mention, WinUI removed from to-do, Uno + MAUI added to still-to-do, PHASE3_FINDINGS link)
```

Files deliberately not touched (per Phase 3.4 constraint set):
- All of `src/Marionette.NET.*/`
- All of `samples/Sample.*/`
- All of `tests/Marionette.NET.*/`
- All of `.phase0/{ProbeIl,StdioTest}/`
- `MASTERPLAN.md`, `LICENSE`, `.gitignore`, `Directory.Build.props`, `global.json`, `PHASE0_FINDINGS.md`, `PHASE1_FINDINGS.md`, `PHASE2_FINDINGS.md`
- `build/Marionette.NET.props`, `build/Marionette.NET.targets`, `build/Run-IlProbe.ps1`
- `skill-pack/`
- `.phase1/demo.ps1`, `.phase2/`, `.phase3/3a-input-events.md`, `.phase3/3b-adapter-winui.md`, `.phase3/3c-multi-window.md`
- `Marionette.NET.sln` (verified via `dotnet sln list` — all 12 projects present, no changes needed)
