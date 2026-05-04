# Phase 4.1 (4a) - Adapter.Maui + Sample.Maui.PocketPlanner

**Status:** PASS
**Date:** 2026-05-03
**SDK:** .NET 10.0.202 - .NET MAUI 10.0.20 (workload `maui-windows`) - ModelContextProtocol 1.2.0 - Roslyn 4.14.0

## Goal & verdict

Phase 4.1 (the masterplan's Phase 5 reorganised after Uno was skipped) ships the fourth Marionette adapter and its canonical sample, mirroring the WPF (Phase 1.3), Avalonia (Phase 2.1), and WinUI 3 (Phase 3.2) work against .NET MAUI 10.x:

1. `src/Marionette.NET.Adapter.Maui/` - the production `IUiAutomationAdapter` impl for .NET MAUI. Pinned to MAUI 10.0.20 stable (the locally cached build of the `maui-windows` workload). Phase 4.1 single-targets `net10.0-windows10.0.19041.0`; multi-target to Android / iOS / MacCatalyst is documented out of scope (requires platform toolchains we don't have).
2. `samples/Sample.Maui.PocketPlanner/` - a real INPC-plumbed daily-appointment-planner sample with 5 `[McpCallable]` methods, 4 `[McpObservable]` properties (2 watchable), and 1 typed `[McpEvent]` (with `AppointmentAddedEventArgs`). Theme is intentionally distinct from TodoApp's list, Dashboard's metric stream, and FormLab's settings form.
3. Skill-pack updates: MAUI adopters subsection in `attributes-reference.md`, framework detection in `marionette-decorate`, "Compatible apps: WPF + Avalonia + WinUI + MAUI" mentions in `marionette-explore` / `marionette-test`.
4. Tooling updates: `build/Run-IlProbe.ps1` default needles include `Adapter.Maui` (now 7 needles); `.phase0/StdioTest/Program.cs` has a `--maui` mode handshaking against PocketPlanner.

**Verdict: GO for Phase 4.2 (or whatever the next phase becomes).** All build-matrix steps pass, the IL stripping promise from Phase 0 Spike A holds (0 hits across all 7 needles on all 5 stripped samples), the new MAUI headless harness scores 12/12 PASS, and the existing WPF / Avalonia / WinUI tests hold steady (25/25 source-gen, 7/7 + 3 skip integration, demo.ps1 PASS).

## What was built

### A. `src/Marionette.NET.Adapter.Maui/`

Five production source files plus the csproj. Mirrors the WinUI adapter shape with MAUI-specific quirks isolated.

| File | Purpose |
|---|---|
| `Marionette.NET.Adapter.Maui.csproj` | TFM `net10.0-windows10.0.19041.0` (matching WinUI adapter and the SDK ref MAUI Windows brings). `<UseMaui>true</UseMaui>`, single Windows TFM (multi-target Phase-6). `<PackageReference Include="Microsoft.Maui.Controls" Version="10.0.20" />`. `IsAotCompatible` / `IsTrimmable` gated on PublishAot=true. ProjectReference to Marionette.NET.Runtime. Deliberately does NOT pull `Microsoft.Maui.Controls.Compatibility` (no legacy Forms compat needed). |
| `MauiUiAutomationAdapter.cs` | Production `IUiAutomationAdapter` impl. `DispatchAsync(Action/Func<T>, ct)` wraps `IDispatcher.Dispatch(...)` in a `TaskCompletionSource`. `CaptureScreenshotAsync` composes a UI-thread async helper that uses `Microsoft.Maui.Media.Screenshot.Default.CaptureAsync()` -> `OpenReadAsync(ScreenshotFormat.Png)` -> `MemoryStream.ToArray()`. `ResolveControlAsync` walks every live `Application.Windows` via `IVisualTreeElement.GetVisualChildren()`. `SimulateInputAsync` / `RaiseEventAsync` delegate to the helper classes below. Multi-window routing via shared `RootInstanceTracker` (Phase 3.3 utility). |
| `MarionetteMaui.cs` | One-call `AttachTo(Application, IReadOnlyList<RootDescriptor>, string[]?, ILoggerFactory?)` bootstrap. Captures `Application.Dispatcher`. Rewrites `RootDescriptor.Create` factories to dispatch through the UI thread AND prefer the live `Application.Windows[0].Page` when type-compatible. Spawns `MarionetteHost.RunAsync` on a background `Task`. Returns an `IDisposable` for explicit detach. |
| `Internal/VisualTreeFinder.cs` | Iterative-DFS named-element resolver via `IVisualTreeElement`. Walks every live `Application.Windows` -> `Window.Page` -> visual subtree. Match precedence: `Element.AutomationId` first, `Element.StyleId` second, INameScope `FindByName<Element>` third. MAUI 10.x uses `IVisualTreeElement.GetVisualChildren()` as the canonical walker (no `VisualTreeHelper`, no `LogicalTreeHelper`). Phase 3.3 multi-window scope variant `FindByNameInWindow` included. |
| `Internal/MauiInputSimulator.cs` | Phase 4.1-pragmatic input simulator. `click` / `double_click` walk up the parent chain looking for an `IButtonController`-capable element (Button, RadioButton, etc.) and call `SendClicked()` - a semantic-first path that fires the framework's Click event AND any bound Command. Works on every MAUI head. `type_text` sets `Entry.Text` / `Editor.Text` / `SearchBar.Text` / `Label.Text` directly. Other kinds (`right_click`, `key_*`, `mouse_move`) return `success:false` with a logged limitation - MAUI 10.x has no publicly-constructible KeyboardEventArgs / PointerEventArgs / right-click semantics. |
| `Internal/MauiEventRaiser.cs` | Reflection-based CLR-event raiser. MAUI events surface as standard CLR events on Element / View (e.g. `event EventHandler Clicked` on Button). The raiser walks the type chain looking for an `EventInfo`, pulls the compiler-emitted private backing-field delegate, and `DynamicInvoke`s it with a default-constructed args type. Wrapped in `[UnconditionalSuppressMessage]` for IL2026/IL2070/IL2075 - documented AOT/trim caveat in the public adapter XML doc, mirroring the Phase 3.2 WinUI raiser. |

### B. `samples/Sample.Maui.PocketPlanner/`

The canonical .NET MAUI adopter-reference. A real, decorated, INPC-plumbed daily-appointment-planner sample with diverse decorations distinct from TodoApp's list, Dashboard's metric stream, and FormLab's settings form.

| File | Purpose |
|---|---|
| `Sample.Maui.PocketPlanner.csproj` | TFM `net10.0-windows10.0.19041.0` only. `<OutputType>Exe</OutputType>`, `<UseMaui>true</UseMaui>`, `<SingleProject>true</SingleProject>`, `<WindowsPackageType>None</WindowsPackageType>`. `<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>` to suppress the auto-emitted `Program.Main` (we ship our own in `Platforms/Windows/Program.cs`). Conditional Adapter.Maui ProjectReference + always-on Abstractions ProjectReference + always-on SourceGenerator analyzer. Pulls `Microsoft.Maui.Controls 10.0.20`. Imports `build/Marionette.NET.props` and `.targets`. |
| `MauiProgram.cs` | Standard `MauiApp.CreateBuilder().UseMauiApp<App>().Build()` builder. |
| `App.xaml` / `App.xaml.cs` | `App` with `CreateWindow` returning `new Window(new MainPage())`. `OnStart` (under `#if MCP_ENABLED`) rewrites the `PlannerViewModel` root's `Create` factory to `() => PlannerViewModel.Shared` and calls `MarionetteMaui.AttachTo(this, bridgedRoots, args)`. Same factory-rewrite pattern as TodoApp / Dashboard / FormLab. |
| `MainPage.xaml` / `MainPage.xaml.cs` | Form layout: header, status bar (3 metrics), CollectionView of appointments, quick-add row (TitleEntry, DatePicker, AddButton, ClearButton). Each control has an `AutomationId` set so `simulate_input` resolves them by stable name. Code-behind delegates user input events to ViewModel methods. |
| `Models/Appointment.cs` | Plain immutable record used by the ObservableCollection. |
| `PlannerViewModel.cs` | The `[McpRoot]`. See breakdown below. |
| `Platforms/Windows/Program.cs` | Custom `[STAThread] Main` mirroring the WinUI sample. Three modes: no flag -> `RunGui()`; `--mcp --headless` -> `MarionetteHost.RunAsync` directly (NoOpAdapter); `--mcp` (GUI) -> `RunGui()` + `App.OnStart` wires the host. `RunGui` uses `WinRT.ComWrappersSupport.InitializeComWrappers()` + `Application.Start(...)`. |
| `Platforms/Windows/App.xaml` / `App.xaml.cs` | Standard `MauiWinUIApplication` subclass that delegates to `MauiProgram.CreateMauiApp()`. |
| `Platforms/Windows/app.manifest` | PerMonitor V2 DPI awareness (copied from the WinUI sample). |
| `Resources/AppIcon/`, `Resources/Splash/`, `Resources/Styles/`, `Resources/Fonts/`, `Resources/Images/`, `Resources/Raw/` | Default MAUI scaffold resources (copied from `dotnet new maui` and pruned). |

### C. `PlannerViewModel` decorations

The "richer-than-TodoApp daily-planner" promise made concrete:

| Decoration | Member | Notes |
|---|---|---|
| `[McpRoot]` | class | Implicit name `PlannerViewModel`. |
| `[McpCallable]` | `AddAppointment(string title, DateTime startTime, int durationMinutes = 60)` | Default-arg duration (60 min) demonstrates optional-parameter handling through the source generator. |
| `[McpCallable]` | `RemoveAppointment(int index)` | Bounds-checked. |
| `[McpCallable]` | `MoveAppointment(int index, DateTime newStartTime)` | Preserves duration; uses `record with` syntax. |
| `[McpCallable]` | `CompleteAll()` | Marks every appointment completed via index-replace. |
| `[McpCallable]` | `Clear()` | Removes every appointment. |
| `[McpObservable(Watchable=true)]` | `AppointmentCount` | INPC fires from the ObservableCollection's CollectionChanged hook. |
| `[McpObservable(Watchable=true)]` | `CompletedCount` | Same. |
| `[McpObservable]` | `EarliestStartTime` | Non-watchable; demonstrates `DateTime?` return. |
| `[McpObservable]` | `LastAddedTitle` | Non-watchable; demonstrates `string?` return. |
| `[McpEvent]` | `AppointmentAdded` (with `AppointmentAddedEventArgs` carrying `Title`, `StartTime`, `DurationMinutes`) | Fires on every `AddAppointment`. |

The ViewModel is framework-agnostic - no MAUI types touched. INPC fires from any thread; the MAUI bindings handle their own UI-side dispatch through IDispatcher.

### D. Solution wiring

`Marionette.NET.sln` adds two projects:
* `Marionette.NET.Adapter.Maui` under the `src` solution folder (GUID `{E4E4E4E4-...}`).
* `Sample.Maui.PocketPlanner` under the `samples` folder (GUID `{F5F5F5F5-...}`).

All four configurations (`Debug|Any CPU`, `Release|Any CPU`) wired with `ActiveCfg` + `Build.0`.

### E. IL probe regression gate

`build/Run-IlProbe.ps1` default `$Needles` array now includes `Adapter.Maui` as the fifth entry (mirrored alphabetically with the other adapter needles). The script signature is unchanged - adopters who already invoke it with explicit `-Needles` still work; defaults pick up the new check automatically.

### F. StdioTest harness `--maui` mode

`.phase0/StdioTest/Program.cs` now accepts `--maui`. The new mode runs against `Sample.Maui.PocketPlanner.exe --mcp --headless` and asserts twelve checks:

1. `initialize` handshake.
2. `tools/list` returns the four meta-tools.
3. `tools/list` also contains the five per-method dynamic tools (PlannerViewModel.AddAppointment, RemoveAppointment, MoveAppointment, CompleteAll, Clear).
4. `inspect_app_api` lists PlannerViewModel with all 5 callables + 4 observables + 1 event.
5. `read_observable AppointmentCount` returns 0 baseline.
6. `read_observable LastAddedTitle` returns null/empty baseline.
7. `resources/subscribe` to events/AppointmentAdded BEFORE the add.
8. `invoke_method AddAppointment("Lunch", "2026-05-04T12:00:00", 60)` succeeds.
9. The event resource read carries `args.Title="Lunch"` (verifies the typed payload round-trips).
10. `read_observable AppointmentCount` returns 1 after add.
11. `read_observable LastAddedTitle` returns "Lunch" after add.
12. `capture_screenshot` returns the documented `screenshot_not_supported` error (NoOpAdapter in headless).

Plus stdout-purity assertion (zero pollution lines) and clean child exit.

### G. Skill-pack additions

* `skill-pack/prompts/attributes-reference.md`:
  * Status header bumped to Phase 4.1.
  * Namespace table adds `MarionetteMaui.AttachTo` -> `Marionette.Adapter.Maui`.
  * New "MAUI - App.OnStart" wiring snippet section (after the WinUI section).
  * New "Non-Window root binding (MAUI)" snippet mirroring the WPF/Avalonia/WinUI pattern.
  * TFM choice for MAUI adopters documented (`net10.0-windows10.0.19041.0` single-target, with multi-target documented as a Phase-6 follow-up).
  * Unpackaged-first guidance, `simulate_input` MAUI-specific notes (IButtonController.SendClicked path), screenshot-window-only caveat.
* `skill-pack/claude-code/marionette-decorate/SKILL.md`:
  * Step 8 "Wire the host" now detects four frameworks: `<UseWPF>true</UseWPF>` -> WPF, `<PackageReference Include="Avalonia"` -> Avalonia, `<UseWinUI>true</UseWinUI>` OR `<PackageReference Include="Microsoft.WindowsAppSDK"` -> WinUI 3, `<UseMaui>true</UseMaui>` OR `<PackageReference Include="Microsoft.Maui.Controls"` -> .NET MAUI.
  * MAUI wiring snippet added alongside the WPF / Avalonia / WinUI ones.
  * Notes on `DISABLE_XAML_GENERATED_MAIN` (MAUI Windows head's analogue of the WinUI `Program.Main` collision).
* `skill-pack/claude-code/marionette-explore/SKILL.md`: "Compatible apps" subsection mentions MAUI.
* `skill-pack/claude-code/marionette-test/SKILL.md`: same. Plus a MAUI-specific note about simulate_input behaviour and screenshot scope.

## Build matrix results

All commands run from `C:\Home\Code\nw.Automation`. .NET 10.0.202 + MAUI workload 10.0.20.

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build Marionette.NET.sln -c Debug` | PASS - 0 warnings, 0 errors (14 projects: 12 from Phase 3.3 + Adapter.Maui + Sample.Maui.PocketPlanner) |
| 2 | `dotnet build Marionette.NET.sln -c Release` | PASS - 0 warnings, 0 errors |
| 3 | `dotnet test tests/Marionette.NET.SourceGenerator.Tests/...csproj -c Debug --no-build` | PASS - 25/25 (unchanged from Phase 3.4) |
| 4 | `dotnet test tests/Marionette.NET.Integration/...csproj -c Debug --no-build` | PASS - 7 passed + 3 skipped (EC-1..EC-7 + EC-8/EC-9/EC-10 GUI-gated, unchanged) |
| 5 | `dotnet build samples/Sample.Maui.PocketPlanner/...csproj -c Release -p:EnableMcpAutomation=false` | PASS - stripped output, 0 warnings |
| 6 | `dotnet build samples/Sample.Maui.PocketPlanner/...csproj -c Debug -p:EnableMcpAutomation=true` | PASS - 0 warnings |
| 7 | IL probe over PocketPlanner stripped DLL (7 needles) | PASS - 0 hits across all 7 needles |
| 8 | IL probe over StripeProbe stripped DLL (regression check, new Adapter.Maui needle picks up nothing) | PASS - 0 hits all 7 |
| 9 | IL probe over TodoApp stripped DLL | PASS - 0 hits all 7 |
| 10 | IL probe over Avalonia Dashboard stripped DLL | PASS - 0 hits all 7 |
| 11 | IL probe over WinUI FormLab stripped DLL | PASS - 0 hits all 7 |
| 12 | `dotnet StdioTest.dll <Sample.Maui.PocketPlanner.exe> --maui` (NEW) | PASS - 12/12 checks, 17 JSON-RPC frames, 0 pollution |
| 13 | `dotnet StdioTest.dll <Sample.WinUI.FormLab.exe> --winui` (regression) | PASS - 18/18 checks |
| 14 | `dotnet StdioTest.dll <Sample.Avalonia.Dashboard.exe> --avalonia` (regression) | PASS |
| 15 | `dotnet StdioTest.dll <Sample.Wpf.TodoApp.exe> --todoapp` (regression) | PASS |
| 16 | `dotnet StdioTest.dll <Sample.Wpf.StripeProbe.exe>` (regression) | PASS |
| 17 | `pwsh .phase1/demo.ps1 -NoBuild` | PASS - 12/12 harness checks, 0 stdout pollution, clean exit |

### IL probe - PocketPlanner (cmd 7)

```
[PASS] Marionette.NET.Runtime: TOTAL hits across 1 file(s): 0
[PASS] Adapter.Wpf:            TOTAL hits across 1 file(s): 0
[PASS] Adapter.Avalonia:       TOTAL hits across 1 file(s): 0
[PASS] Adapter.WinUI:          TOTAL hits across 1 file(s): 0
[PASS] Adapter.Maui:           TOTAL hits across 1 file(s): 0
[PASS] Marionette.Ai:          TOTAL hits across 1 file(s): 0
[PASS] ModelContextProtocol:   TOTAL hits across 1 file(s): 0
PASS - stripped build contains zero forbidden symbols.
```

The stripped PocketPlanner build's user assembly references zero Marionette types beyond Abstractions. (The shipped binary tree includes the MAUI / WinAppSDK / WinUI DLLs - those are inherent to MAUI's Windows head, not Marionette's responsibility.)

### Stdio harness output (cmd 12)

```
=== Phase 4.1 MAUI PocketPlanner stdio handshake harness ===
PASS - initialize handshake (server: Marionette.NET 0.0.1, protocol 2025-11-25)
PASS - tools/list contains all four Phase-1 tools (got: capture_screenshot,inspect_app_api,invoke_method,read_observable)
PASS - tools/list also contains the 5 per-method dynamic tools (PlannerViewModel.AddAppointment,PlannerViewModel.RemoveAppointment,PlannerViewModel.MoveAppointment,PlannerViewModel.CompleteAll,PlannerViewModel.Clear)
PASS - inspect_app_api returned PlannerViewModel manifest with all 5 callables + 4 observables + 1 event
PASS - read_observable AppointmentCount initially returned 0
PASS - read_observable LastAddedTitle initially returned null/empty (got 'null')
PASS - invoke_method AddAppointment("Lunch", ..., 60) succeeded
PASS - resources/subscribe + AddAppointment produced an event notification on marionette://PlannerViewModel/events/AppointmentAdded (sequence=1, count=1, args.Title="Lunch" present)
PASS - read_observable AppointmentCount returned 1 after AddAppointment
PASS - read_observable LastAddedTitle returned 'Lunch' after AddAppointment
PASS - capture_screenshot surfaced a structured 'screenshot_not_supported' error (NoOpAdapter)
PASS - child exited cleanly with code 0
=== Phase 4.1 MAUI PocketPlanner handshake: PASS ===
```

Stderr lines are SDK-internal informational logs (ModelContextProtocol.Server.{StdioServerTransport, McpServer} plus DynamicToolRegistry's "Dynamic per-method tools registered: 5.") - no Marionette code wrote to stdout.

## MAUI-specific notes

### TFM choice and MAUI version pinning

* TFM: `net10.0-windows10.0.19041.0` - matches the SDK ref MAUI Windows head pulls in from the `maui-windows` workload (10.0.20). Same TFM the WinUI 3 adapter uses, which keeps the dependency graph predictable.
* MAUI version: `10.0.20` - the locally-cached stable from the `maui-windows` workload (release channel "stable"). Adopters who track newer stables override the package version in their own csproj; the adapter doesn't pin a specific minor.
* Single-target Windows for Phase 4.1 - a deliberate scope decision documented in the report: this build environment lacks the cross-platform toolchains for Android (Java + Android SDK), iOS / MacCatalyst (Apple SDKs). Multi-target is a Phase-6 follow-up and the adapter source compiles identically on every MAUI head because it depends only on platform-neutral types.

### IDispatcher vs DispatcherQueue (WinUI) vs Dispatcher (WPF) vs Dispatcher.UIThread (Avalonia)

MAUI's `Microsoft.Maui.Dispatching.IDispatcher` is the public threading primitive. Notable differences:
* `Application.Dispatcher` (instance property) is the canonical accessor; no `GetForCurrentThread()` static needed because the Application owns it.
* `IDispatcher.Dispatch(Action)` is fire-and-forget and returns `bool` (whether the post succeeded). The adapter wraps each enqueue in a `TaskCompletionSource` so the IUiAutomationAdapter contract's `Task DispatchAsync(...)` shape holds.
* `IDispatcher.IsDispatchRequired` is the analogue of `Dispatcher.CheckAccess` / `Dispatcher.UIThread.CheckAccess` / `DispatcherQueue.HasThreadAccess`. Same short-circuit pattern as the other adapters.

### Application.Windows enumeration

* `Microsoft.Maui.Controls.Application.Windows` is `IReadOnlyList<Window>` in MAUI 5+. The adapter walks every live window directly via that property - no equivalent of WinUI 3's `WindowTracker` is needed because MAUI auto-tracks for adopters.
* Multi-window in MAUI is rough in 10.x: the desktop heads support multiple windows but mobile heads don't. Phase 4.1's adapter handles single-window cleanly; multi-window is a Phase-6 polish slot.

### Screenshot.CaptureAsync vs RenderTargetBitmap

* MAUI's `Microsoft.Maui.Media.Screenshot.Default.CaptureAsync()` is the canonical screenshot path. Returns `IScreenshotResult`. Phase 4.1 uses it for full-screen capture (PNG via `OpenReadAsync(ScreenshotFormat.Png)` then `MemoryStream.ToArray()`).
* `IsCaptureSupported` is checked first; the adapter throws `NotSupportedException` when the platform doesn't support it (rare on Windows).
* Element-level capture is NOT part of MAUI's cross-platform surface. Phase 4.1 logs a debug note when `targetName` is non-null but still captures the full screen. Element-level via the WinUI handler underneath + `RenderTargetBitmap` on the platform view is a Phase-6 refinement.

### AutomationProperties API differences (MAUI vs WinUI vs WPF)

* WPF / WinUI: `AutomationProperties.GetAutomationId(element)` is a static getter on a `DependencyProperty`-backed attached property.
* MAUI: `Element.AutomationId` is a regular CLR property on every Element (set via XAML attribute `AutomationId="MyButton"` or code). `SemanticProperties.GetDescription` exists for screen-reader text but is NOT the automation-id accessor.
* The match precedence in `VisualTreeFinder` is: `Element.AutomationId` first, `Element.StyleId` second (XAML compiler's `x:Name` -> `StyleId` mapping), `INameScope.FindByName<Element>` third (XAML name table, authoritative even before realisation).

### Input simulation strategies (Button.SendClicked vs InputInjector vs raw routed-event)

| Strategy | When | Phase 4.1 MAUI behaviour |
|---|---|---|
| `IButtonController.SendClicked()` | `kind:"click"` / `"double_click"` on Button-like targets | PRIMARY. Walks parent chain looking for `IButtonController`-implementing element, calls `SendClicked()`. Fires the framework's Click event AND any bound Command. Works on every MAUI head, no elevation. |
| Direct property setter | `kind:"type_text"` on Entry / Editor / SearchBar / Label | PRIMARY. Sets `Entry.Text = ...` etc. directly. Bypasses platform IME but produces the same TextChanged event flow. |
| Raw input pipeline | `kind:"key_*"` / `"mouse_move"` / `"right_click"` | NOT IMPLEMENTED. MAUI 10.x has no publicly-constructible KeyboardEventArgs / PointerEventArgs / right-click semantics across all heads. Returns `success:false` with logged limitation. |
| Routed-event raise | (not directly used in MAUI) | N/A. MAUI has no `RaiseEvent` API on Element; the closest analogue is `MauiEventRaiser`'s reflection on the compiler-emitted backing field. |

### CLR-event reflection for raise_event

MAUI surfaces routed-style events as standard CLR events on the Element / View hierarchy. There is NO public `RoutedEvent` static-field idiom (WPF/Avalonia) and NO `EventManager.GetRoutedEventsForOwner`. The Phase 4.1 raiser walks the type chain looking for an `EventInfo` with the requested name, pulls the compiler-emitted private backing-field delegate, and `DynamicInvoke`s it with a default-constructed args type.

This is meaningfully more trim-fragile than the WPF / Avalonia raisers; the public adapter XML doc surfaces the AOT caveat. Adopters who need reliable `raise_event` coverage in AOT scenarios should use the alternative path: decorate the handler logic on a `[McpCallable]` method and call it via `invoke_method`. Same Phase 3.2 WinUI pattern.

### Non-Window/non-Page root pattern

Phase 1.4 (TodoApp), Phase 2.1 (Dashboard), Phase 3.2 (FormLab) taught us that the source generator emits `() => new TViewModel()`, which produces a SECOND instance separate from the one the live page binds. Phase 4.1 inherits the fix: `App.OnStart` rewrites the `RootDescriptor.Create` factory to return the singleton BEFORE calling `AttachTo`. The Phase 4.1 sample documents this in `App.xaml.cs` comments. `MarionetteMaui.AttachTo` only auto-substitutes Page-typed roots that match the live `Application.Windows[0].Page`'s `FullName`; everything else falls through to the original factory.

### XAML-compiler `Program.Main` collision

The MAUI Windows head's XAML compiler auto-emits a `Program.Main` inside `App.g.i.cs` unless `DISABLE_XAML_GENERATED_MAIN` is defined. Same constant the WinUI 3 sample uses (the MAUI Windows head IS WinUI 3 underneath). The Phase 4.1 sample csproj defines that constant + sets `<StartupObject>Sample.Maui.PocketPlanner.WinUI.Program</StartupObject>`. This is the MAUI analogue of WPF's `<EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>`.

### MAUI implicit package reference warning

MAUI 10.x emits the MA002 warning when adopters set `<UseMaui>true</UseMaui>` without an explicit `<PackageReference Include="Microsoft.Maui.Controls" Version="..." />`. Phase 4.1 sample sets the explicit reference (10.0.20) AND `<SkipValidateMauiImplicitPackageReferences>true</SkipValidateMauiImplicitPackageReferences>` to silence the residual nag - same shape adopters get from `dotnet new maui` template output once they pin their version.

## Deviations from the WPF/Avalonia/WinUI patterns

* **No `WindowTracker` analogue needed.** MAUI's `Application.Windows` collection exposes every live window directly; the adapter walks it without maintaining its own list. Phase 3.3's `RootInstanceTracker` is still used for multi-window root-instance lookup (per the Phase 3.3 contract additions) but window enumeration goes through MAUI's public API.
* **No `AutomationProperties` static getter.** `Element.AutomationId` is the canonical automation id. Phase 4.1's VisualTreeFinder matches against `Element.AutomationId` + `Element.StyleId` + INameScope's FindByName.
* **Async screenshot via Essentials.** `Microsoft.Maui.Media.Screenshot.Default.CaptureAsync()` produces an `IScreenshotResult`; the adapter reads PNG bytes via `OpenReadAsync(ScreenshotFormat.Png)`. Composed by dispatching an outer `TaskCompletionSource<byte[]>`-completing helper, mirroring the WinUI async-end-to-end pattern.
* **Window is part of the public surface.** Unlike WinUI 3 where Window is standalone, MAUI's `Microsoft.Maui.Controls.Window` is a NavigableElement (descendant of Element) - it can have an AutomationId set; the visual-tree walker tests it before walking the Page subtree.
* **No `IButtonController.SendClicked()` analogue across all heads for non-button targets.** The simulator returns `success:false` with a logged limitation for `right_click` / `key_*` / `mouse_move` kinds rather than reaching for platform-specific input injection. MAUI-on-Windows could escalate to `Windows.UI.Input.Preview.Injection.InputInjector` (the WinUI 3 path) but Phase 4.1 takes the cross-platform-clean stance.
* **Dispatcher returns bool.** `IDispatcher.Dispatch(Action)` returns `bool` (post succeeded). The adapter checks the return and surfaces `InvalidOperationException` when the dispatcher is shut down.

## Files added/changed in Phase 4.1

```
src/Marionette.NET.Adapter.Maui/                         (NEW)
  Marionette.NET.Adapter.Maui.csproj
  MauiUiAutomationAdapter.cs
  MarionetteMaui.cs
  Internal/VisualTreeFinder.cs
  Internal/MauiInputSimulator.cs
  Internal/MauiEventRaiser.cs

samples/Sample.Maui.PocketPlanner/                       (NEW)
  Sample.Maui.PocketPlanner.csproj
  MauiProgram.cs
  App.xaml
  App.xaml.cs
  MainPage.xaml
  MainPage.xaml.cs
  PlannerViewModel.cs
  Models/Appointment.cs
  Platforms/Windows/Program.cs
  Platforms/Windows/App.xaml
  Platforms/Windows/App.xaml.cs
  Platforms/Windows/app.manifest
  Resources/AppIcon/{appicon.svg,appiconfg.svg}
  Resources/Splash/splash.svg
  Resources/Styles/{Colors.xaml,Styles.xaml}
  Resources/{Fonts,Images,Raw}/                          (empty placeholders)

Marionette.NET.sln                                       (UPDATED - added two projects under src + samples folders)
build/Run-IlProbe.ps1                                    (UPDATED - default Needles list adds Adapter.Maui; now 7 needles total)
.phase0/StdioTest/Program.cs                             (UPDATED - --maui mode + TryParsePocketPlannerManifest helper)

skill-pack/prompts/attributes-reference.md               (UPDATED - Phase 4.1 status, namespace table adds MarionetteMaui, MAUI wiring snippet, non-Page root snippet, MAUI-specific notes)
skill-pack/claude-code/marionette-decorate/SKILL.md      (UPDATED - framework detection adds MAUI, MAUI wiring snippet, MAUI-specific TFM/Main/screenshot notes)
skill-pack/claude-code/marionette-explore/SKILL.md       (UPDATED - "Compatible apps" subsection adds MAUI)
skill-pack/claude-code/marionette-test/SKILL.md          (UPDATED - "Compatible apps" + MAUI simulate_input note + MAUI screenshot scope note)

.phase4/4a-adapter-maui.md                               (NEW - this report)
```

Files deliberately NOT touched (per the Phase 4.1 constraint set):
* `MASTERPLAN.md`, `LICENSE`, `.gitignore`, `Directory.Build.props`, `global.json`, `PHASE0_FINDINGS.md`, `PHASE1_FINDINGS.md`, `PHASE2_FINDINGS.md`, `PHASE3_FINDINGS.md`, `README.md`.
* `build/Marionette.NET.props`, `build/Marionette.NET.targets`.
* All of `src/Marionette.NET.Abstractions/`, `src/Marionette.NET.SourceGenerator/`, `src/Marionette.NET.Runtime/`, `src/Marionette.NET.Adapter.Wpf/`, `src/Marionette.NET.Adapter.Avalonia/`, `src/Marionette.NET.Adapter.WinUI/`.
* All of `samples/Sample.Wpf.*/`, `samples/Sample.Avalonia.*/`, `samples/Sample.WinUI.*/`.
* All of `tests/Marionette.NET.SourceGenerator.Tests/`, `tests/Marionette.NET.Integration/`.
* `.phase1/demo.ps1`, `.phase1/test-todoapp.ps1`, `.phase2/`, `.phase3/`.
* `.phase0/ProbeIl/`.

## Issues encountered

1. **MA002 implicit package reference nag.** The MAUI 10.x SDK emits a warning when adopters set `<UseMaui>true</UseMaui>` without an explicit `Microsoft.Maui.Controls` PackageReference. Resolution: pin the explicit reference (10.0.20) AND set `<SkipValidateMauiImplicitPackageReferences>true</SkipValidateMauiImplicitPackageReferences>` to silence the residual nag.

2. **Microsoft.Extensions.Logging.Debug downgrade.** First sample build hit NU1605 because the MAUI default template uses `10.0.0` but Marionette.NET.Runtime transitively requires `10.0.1`. Fixed by pinning `10.0.1` in the sample csproj (matches the runtime's transitive constraint).

3. **Auto-emitted XAML compiler `Program.Main`.** Same trapdoor the WinUI sample hit. Fixed by adding `<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>` + `<StartupObject>Sample.Maui.PocketPlanner.WinUI.Program</StartupObject>` to the sample csproj.

4. **`AutomationProperties.GetAutomationId` doesn't exist on MAUI.** Initial draft used the WinUI/WPF static getter shape. MAUI's API is different - `Element.AutomationId` is a regular CLR property. Resolution: use `Element.AutomationId` (canonical), `Element.StyleId` (XAML x:Name fallback), and `Element.FindByName<T>(string)` (INameScope lookup). `SemanticProperties.GetDescription` exists for accessibility but is NOT the automation-id accessor.

5. **`IElementController` not in `Microsoft.Maui.Controls.Internals`.** Initial draft wanted to walk `LogicalChildren` via the IElementController interface (Xamarin.Forms shape). MAUI 10.x exposes the visual tree via `IVisualTreeElement.GetVisualChildren()` instead - cleaner, public, and the only supported way to walk descendants without poking `Internal` types.

6. **`DatePicker.Date` is nullable.** Initial MainPage.xaml.cs draft assumed `DatePicker.Date` was `DateTime`; MAUI 10.x types it as `DateTime?`. Fixed with a `?? DateTime.Today` fallback.

## Phase-4.2 hand-off

The Phase 4.1 prompt called out a "Phase 4.2: AOT/Trim-hardening across all five adapters' public APIs" follow-up. This is the natural next slot:

* **AOT/trim hardening of the public adapter surface.** All four (Wpf / Avalonia / WinUI / Maui) raisers use reflection on compiler-emitted backing fields - documented IL2026/IL2070/IL2075 with `[UnconditionalSuppressMessage]`. A source-generator alternative (emit a typed dispatcher per `[McpEvent]` so the runtime can fire without reflecting) would close that gap.
* **Element-level screenshot for MAUI.** Phase 4.1 captures the entire screen via Essentials. Element-bounded capture would need to drop into the platform handler and use the platform-specific RenderTargetBitmap (WinUI on Windows, UIGraphicsImageRenderer on iOS/MacCatalyst, etc.). Phase 6-shaped work.
* **Multi-target the MAUI adapter and sample.** Single-target Windows for Phase 4.1 was a deliberate scope decision; Phase 6 with platform toolchains adds Android / iOS / MacCatalyst. The adapter source compiles unchanged because it depends only on platform-neutral `Microsoft.Maui.Controls` + `Microsoft.Maui.Dispatching` types.
* **MAUI keyboard / pointer input fidelity.** `simulate_input(kind:"key_*"/"mouse_move"/"right_click")` returns `success:false` because MAUI 10.x has no publicly-constructible event-args. Phase 6 may revisit if MAUI 11.x publicises the ctors, OR escalate to platform-specific input injection on the Windows head (the WinUI 3 InputInjector path). The Phase-3.1 fallback ("decorate the underlying mutating method with `[McpCallable]` and call it directly") covers most adopter scenarios.
* **MAUI multi-window polish.** MAUI's multi-window support is rough in 10.x; Phase 6 may surface stable per-window IDs (the adapter already wires `RootInstanceTracker` so this is a small addition).

What stays open from earlier phases:
* WPF + AOT GUI crash, callable parameter type whitelist permissive (MAR004), source-generator MAR009+ slot, showcase conversations, adopter docs - all unchanged from Phase 3 carryover.

Phase 4.1 deliverables are complete.
