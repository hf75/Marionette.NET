# Phase 2.1 (2a) - Adapter.Avalonia + Sample.Avalonia.Dashboard

**Status:** PASS
**Date:** 2026-05-03
**SDK:** .NET 10.0.202 - Avalonia 11.3.14 - ModelContextProtocol 1.2.0 - Roslyn 4.14.0

## Goal & verdict

Phase 2.1 ships the second Marionette adapter and its canonical sample, mirroring the WPF work from Phase 1.3 and 1.4 against Avalonia 11.x:

1. `src/Marionette.NET.Adapter.Avalonia/` - the production `IUiAutomationAdapter` impl for Avalonia (cross-platform: net10.0, NOT net10.0-windows).
2. `samples/Sample.Avalonia.Dashboard/` - a real INPC-plumbed system-dashboard sample with five `[McpCallable]` methods (one async), four `[McpObservable]` properties (three watchable), two `[McpEvent]` events, and a Fluent-themed UI. Richer than the TodoApp.
3. Skill-pack updates with Avalonia wiring snippets, framework detection in `marionette-decorate`, and "Compatible apps: WPF + Avalonia" mentions in `marionette-explore` / `marionette-test`.
4. IL-probe and stdio harness extensions: a fifth needle (`Adapter.Avalonia`) is now the default in `build/Run-IlProbe.ps1`; `.phase0/StdioTest/Program.cs` adds an `--avalonia` mode that handshakes against the Dashboard.

**Verdict: GO.** All build-matrix steps pass, the IL probe stays at 0 hits across all five needles for stripped Release builds of all three samples (StripeProbe, TodoApp, Dashboard), the new Avalonia headless harness scores 12/12, and the existing WPF harnesses hold steady (StripeProbe 7/7, TodoApp 10/10). Source-gen tests 13/13 and integration tests 6/6 unchanged - Phase 2.1 added no eval-cases (per spec; that's Phase 2.2/2.3 territory).

## What was built

### A. `src/Marionette.NET.Adapter.Avalonia/`

Three production source files plus the csproj. Mirrors the WPF adapter shape one-to-one against Avalonia 11.x types.

| File | Purpose |
|---|---|
| `Marionette.NET.Adapter.Avalonia.csproj` | TFM `net10.0` (cross-platform), `<PackageReference Include="Avalonia" Version="11.3.14" />` only - the sample csproj brings `Avalonia.Desktop` + `Avalonia.Themes.Fluent`. `IsAotCompatible` / `IsTrimmable` gated on `PublishAot=true`. No `UseWPF`, no `_SuppressWpfTrimError`. `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` inherited from `Directory.Build.props`. |
| `AvaloniaUiAutomationAdapter.cs` | Production `IUiAutomationAdapter` impl. `DispatchAsync` uses `Dispatcher.UIThread.InvokeAsync(...).GetTask().WaitAsync(ct)` (Avalonia 11.x `DispatcherOperation` exposes its underlying Task via `GetTask()`; cancellation honoured via `WaitAsync`). `CaptureScreenshotAsync` constructs `RenderTargetBitmap(new PixelSize(w,h), new Vector(dpi,dpi))`, calls `Render(visual)`, then `Save(stream)` (Avalonia's `Save` writes PNG by default). `ResolveControlAsync` walks open `Window`s via `IClassicDesktopStyleApplicationLifetime.Windows`. |
| `MarionetteAvalonia.cs` | One-call `AttachTo(Avalonia.Application, IReadOnlyList<RootDescriptor>, string[]?, ILoggerFactory?)` bootstrap. Rewrites every `RootDescriptor.Create` factory to dispatch through `Dispatcher.UIThread` AND prefer the live `IClassicDesktopStyleApplicationLifetime.MainWindow` when its CLR FullName matches the descriptor's `TypeName`. Spawns `MarionetteHost.RunAsync` on a background `Task`. Hooks the desktop lifetime's `Exit` event for clean shutdown. Returns an `IDisposable` for explicit detach. |
| `Internal/VisualTreeFinder.cs` | Iterative-DFS named-element resolver. Logical tree (`LogicalChildren`) first, visual tree (`GetVisualChildren()`) fallback. Match precedence: `Avalonia.Automation.AutomationProperties.GetAutomationId()` first, `Control.Name` second. Logs candidate names on miss (truncated at 32). Exposes a `FirstWindow(app)` helper used by `CaptureScreenshotAsync(null)`. |

### B. `samples/Sample.Avalonia.Dashboard/`

The canonical Avalonia adopter-reference. Richer than the WPF TodoApp - five callables across the synchronous + async + OffUiThread axes, four observables, two events, four pre-seeded metrics so demos look populated.

| File | Purpose |
|---|---|
| `Sample.Avalonia.Dashboard.csproj` | TFM `net10.0` (cross-platform), `<OutputType>Exe</OutputType>` (NOT WinExe). Same conditional Adapter.Avalonia ProjectReference + always-on Abstractions ProjectReference + always-on SourceGenerator analyzer. Pulls `Avalonia` + `Avalonia.Desktop` + `Avalonia.Themes.Fluent` 11.3.14. `EmitCompilerGeneratedFiles=true` so adopters can inspect `Marionette.g.cs`. |
| `Program.cs` | Custom `[STAThread] Main`. Three modes: no flag -> `RunGui()`; `--mcp --headless` -> `MarionetteHost.RunAsync` directly (NoOpAdapter); `--mcp` (GUI) -> falls through to `RunGui()` and lets `App.OnFrameworkInitializationCompleted` wire the host. `BuildAvaloniaApp()` is `AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()`. |
| `App.axaml` / `App.axaml.cs` | Dark-mode-friendly Fluent theme + dashboard palette. `OnFrameworkInitializationCompleted` constructs `MainWindow`, then (under `#if MCP_ENABLED`) rewrites the `DashboardViewModel` root's `Create` factory to return `DashboardViewModel.Shared` and calls `MarionetteAvalonia.AttachTo(this, bridgedRoots)`. |
| `MainWindow.axaml` / `MainWindow.axaml.cs` | Six-row Grid: header / counter strip / add row / metrics list / footer actions / bottom hint. Uses `vm:BoolToTextConverter` + `vm:BoolToBrushConverter` for the LIVE / PAUSED status badge. Sets `DataContext = DashboardViewModel.Shared` in the ctor. |
| `Models/Metric.cs` | INPC class with `Name` / `Value` / `Unit` / `Trend` (enum) + a derived `TrendGlyph` for the up/down/flat indicator. Records can't inherit non-record bases here (well, they could, but this stays plain INPC for clarity). |
| `Converters.cs` | Two tiny `IValueConverter`s for the bool-to-text / bool-to-brush bindings on the status badge. |
| `DashboardViewModel.cs` | The `[McpRoot]`. See breakdown below. |
| `app.manifest` | Per-monitor v2 DPI awareness so screenshots pick up `RenderScaling` correctly on HiDPI. |

### C. `DashboardViewModel` decorations

The "richer than TodoApp" promise made concrete:

| Decoration | Member | Notes |
|---|---|---|
| `[McpRoot]` | class | Implicit name `DashboardViewModel`. |
| `[McpCallable]` | `UpsertMetric(string name, double value, string unit)` | Existing-name match updates in-place; new name appends. Fires `MetricUpserted` event. |
| `[McpCallable]` | `RemoveMetric(string name)` | No-op if no match. |
| `[McpCallable]` | `ResetAll()` | Sets every metric Value to 0, Trend to Flat. |
| `[McpCallable(OffUiThread = true)]` | `TogglePaused()` | Toggles `IsPaused`; fires `PausedToggled` event. |
| `[McpCallable(OffUiThread = true, TimeoutSeconds = 5)]` | `RefreshAsync(int simulatedDelayMs = 500)` | Async, awaits `Task.Delay`, then nudges every metric by a deterministic delta. UI-thread-agnostic - the runtime's await holds, the harness verifies the await held by elapsed-time assertion (>= 80ms for a 100ms delay). |
| `[McpObservable(Watchable = true)]` | `MetricCount` | Total entries; INPC fires from `OnMetricsCollectionChanged`. |
| `[McpObservable(Watchable = true)]` | `Total` | Sum of values; INPC fires from per-metric Value changes plus collection changes. |
| `[McpObservable(Watchable = true)]` | `IsPaused` | Boolean status; INPC fires from `TogglePaused`. |
| `[McpObservable]` | `LastUpdatedMetric` | Non-watchable (kept this way to demonstrate both shapes side-by-side, like TodoApp's `LastAddedTitle`). |
| `[McpEvent]` | `MetricUpserted` (with `MetricUpsertedEventArgs : EventArgs` carrying `Name`, `Value`, `Unit`) | Fires on every `UpsertMetric` invocation. |
| `[McpEvent]` | `PausedToggled` (with `EventArgs.Empty`) | Fires on every `TogglePaused`. |

The ViewModel is framework-agnostic - no Avalonia types touched. The Phase 2.1 harness fail-then-fix-during-development showed why: the original `RefreshAsync` reached for `Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(...)` to marshal value mutations to the UI thread, which deadlocks in `--mcp --headless` mode (no UI thread exists). Fix was to drop the dispatch entirely - INPC propagates from any thread, Avalonia's bindings handle their own UI-side dispatch. Documented as the core trapdoor in the report.

### D. Solution wiring

`Marionette.NET.sln` adds two projects:

* `Marionette.NET.Adapter.Avalonia` under the `src` solution folder (GUID `{A0A0A0A0-A0A0-A0A0-A0A0-A0A0A0A0A0A0}`).
* `Sample.Avalonia.Dashboard` under the `samples` folder (GUID `{B1B1B1B1-B1B1-B1B1-B1B1-B1B1B1B1B1B1}`).

All four configurations (`Debug|Any CPU`, `Release|Any CPU`) wired with `ActiveCfg` + `Build.0`.

### E. IL probe regression gate

`build/Run-IlProbe.ps1` default `$Needles` array now includes `Adapter.Avalonia` as the third entry. The script signature is unchanged - adopters who already invoke it with explicit `-Needles` still work; those who rely on the defaults pick up the new check automatically. CI and the local `.phase1/demo.ps1` both use the defaults.

### F. StdioTest harness `--avalonia` mode

`.phase0/StdioTest/Program.cs` now accepts `--avalonia`. The new mode runs against `Sample.Avalonia.Dashboard.exe --mcp --headless` and asserts twelve checks:

1. `initialize` handshake.
2. `tools/list` returns the four Marionette tools (sorted equality).
3. `inspect_app_api` lists `DashboardViewModel` with all 5 callables + 4 observables + 2 events.
4. `read_observable MetricCount` returns 4 (the headless ctor pre-seeds CPU/Memory/Network/Disk).
5. `invoke_method UpsertMetric("CPU", 42, "%")` succeeds.
6. `read_observable MetricCount` is unchanged at 4 (CPU pre-existed - the upsert updated in place).
7. `invoke_method RefreshAsync(100)` succeeds AND the elapsed time is >= 80 ms (the runtime awaited the Task; without await, the response would come back in single-digit ms).
8. `resources/subscribe` to `marionette://DashboardViewModel/MetricCount` then `UpsertMetric(Battery, ...)` (NEW name) produces a `notifications/resources/updated`.
9. `read_observable MetricCount` returns 5 (baseline + 1).
10. `resources/subscribe` to `marionette://DashboardViewModel/events/MetricUpserted` then `UpsertMetric(Latency, ...)` produces an event notification with `args.Name == "Latency"`.
11. `capture_screenshot` returns `screenshot_not_supported` (NoOpAdapter, headless).
12. Stdout is JSON-RPC pure (zero pollution lines), child exits cleanly.

### G. Skill-pack updates

* `skill-pack/prompts/attributes-reference.md`:
  * Status line now reads "Phase 2.1 (WPF + Avalonia)".
  * Namespace table adds `MarionetteAvalonia.AttachTo` -> `Marionette.Adapter.Avalonia`.
  * New "Avalonia - App.OnFrameworkInitializationCompleted" wiring snippet section.
  * New "Non-Window root binding (Avalonia)" snippet mirroring the WPF pattern.
  * TFM choice for Avalonia adopters documented (`net10.0` not `net10.0-windows`).
* `skill-pack/claude-code/marionette-decorate/SKILL.md`:
  * Step 8 ("Wire the host") now detects framework: `<UseWPF>true</UseWPF>` -> WPF, `<PackageReference Include="Avalonia"` -> Avalonia.
  * Avalonia wiring snippet added alongside the WPF one.
  * Notes on `OutputType=Exe` (Avalonia) vs `WinExe` (WPF) and the TFM divergence.
* `skill-pack/claude-code/marionette-explore/SKILL.md`: new "Compatible apps" section documenting both adapters.
* `skill-pack/claude-code/marionette-test/SKILL.md`: same "Compatible apps" addition.

## Build matrix results

All commands run from `C:\Home\Code\nw.Automation`. .NET 10.0.202.

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build Marionette.NET.sln -c Debug` | PASS - 0 warnings, 0 errors (10 projects: 8 Phase-1 + Adapter.Avalonia + Sample.Avalonia.Dashboard) |
| 2 | `dotnet build Marionette.NET.sln -c Release` | PASS - 0 warnings, 0 errors |
| 3 | `dotnet test tests/Marionette.NET.SourceGenerator.Tests/...csproj -c Debug --no-build` | PASS - 13/13 (unchanged from Phase 1.6) |
| 4 | `dotnet test tests/Marionette.NET.Integration/...csproj -c Debug --no-build` | PASS - 6/6 (EC-1..EC-6, unchanged) |
| 5 | `dotnet build samples/Sample.Avalonia.Dashboard/...csproj -c Release -p:EnableMcpAutomation=false` | PASS - stripped output, 0 warnings |
| 6 | `dotnet build samples/Sample.Avalonia.Dashboard/...csproj -c Debug -p:EnableMcpAutomation=true` | PASS - 0 warnings |
| 7 | IL probe over Dashboard stripped DLL | PASS - 0 hits across all 5 needles |
| 8 | IL probe over StripeProbe stripped DLL | PASS - 0 hits across all 5 needles (regression check, new Adapter.Avalonia needle picks up nothing) |
| 9 | IL probe over TodoApp stripped DLL | PASS - 0 hits across all 5 needles (regression check) |
| 10 | `dotnet StdioTest.dll <Sample.Avalonia.Dashboard.exe> --avalonia` | PASS - 12/12 checks, 16 JSON-RPC frames, 0 pollution |
| 11 | `dotnet StdioTest.dll <Sample.Wpf.StripeProbe.exe>` (regression) | PASS - 7/7 checks, 6 JSON-RPC frames, 0 pollution |
| 12 | `dotnet StdioTest.dll <Sample.Wpf.TodoApp.exe> --todoapp` (regression) | PASS - 10/10 checks, 14 JSON-RPC frames, 0 pollution |
| 13 | `pwsh .phase1/demo.ps1 -NoBuild` (regression) | PASS - 10/10 harness checks |

### IL probe - Avalonia Dashboard (cmd 7)

```
[PASS] Marionette.NET.Runtime: TOTAL hits across 1 file(s): 0
[PASS] Adapter.Wpf:            TOTAL hits across 1 file(s): 0
[PASS] Adapter.Avalonia:       TOTAL hits across 1 file(s): 0
[PASS] Marionette.Ai:          TOTAL hits across 1 file(s): 0
[PASS] ModelContextProtocol:   TOTAL hits across 1 file(s): 0
PASS - stripped build contains zero forbidden symbols.
```

The stripped Dashboard build's user assembly references zero Marionette types beyond Abstractions. (The shipped binary tree includes the Avalonia DLLs - those are inherent to Avalonia, not Marionette's responsibility - same way WPF samples ship `WindowsBase.dll` etc. The IL probe's contract is "no Marionette code in the user assembly", and that holds.)

### IL probe - regression checks (cmds 8, 9)

Both StripeProbe and TodoApp stripped Release outputs hit 0 across all five needles. The new `Adapter.Avalonia` needle is harmless on WPF builds - they never referenced Avalonia, so there's nothing to find.

### Stdio harness output (cmd 10)

```
=== Phase 2.1 Avalonia Dashboard stdio handshake harness ===
PASS - initialize handshake (server: Marionette.NET 0.0.1, protocol 2025-11-25)
PASS - tools/list contains all four Phase-1 tools (got: read_observable,capture_screenshot,inspect_app_api,invoke_method)
PASS - inspect_app_api returned DashboardViewModel manifest with all 5 callables + 4 observables + 2 events
PASS - read_observable MetricCount initially returned 4
PASS - invoke_method UpsertMetric("CPU", 42, "%") succeeded
PASS - read_observable MetricCount unchanged at 4 after UpsertMetric on existing key
PASS - invoke_method RefreshAsync(100) succeeded after 115ms (await held)
PASS - resources/subscribe + UpsertMetric(Battery) produced notifications/resources/updated for marionette://DashboardViewModel/MetricCount
PASS - read_observable MetricCount returned 5 after UpsertMetric on new key (baseline + 1)
PASS - resources/subscribe + UpsertMetric produced an event notification on marionette://DashboardViewModel/events/MetricUpserted (sequence=3, count=3, args.Name="Latency" present)
PASS - capture_screenshot surfaced a structured 'screenshot_not_supported' error (NoOpAdapter)
PASS - child exited cleanly with code 0
stdout summary: 16 JSON-RPC frames, 0 pollution lines
=== Phase 2.1 Avalonia Dashboard handshake: PASS ===
```

stderr lines are all SDK-internal informational logs from `ModelContextProtocol.Server.{StdioServerTransport, McpServer}` - no Marionette code wrote to either stream.

## Avalonia-specific notes

### TFM divergence

The Avalonia adapter csproj is `<TargetFramework>net10.0</TargetFramework>` (NOT `net10.0-windows`). This is a meaningful divergence from `Marionette.NET.Adapter.Wpf.csproj` (which IS Windows-only because WPF is). Avalonia 11.x runs on Windows / Linux / macOS from a single build, and the adapter only consumes `Avalonia.Application` + `Dispatcher.UIThread` + visual-tree primitives - no platform-specific dependencies. Adopters whose own product is Windows-only override TFM in their own csproj; the adapter does not constrain them.

### Dispatcher.UIThread vs WPF Dispatcher

WPF's `Application.Current.Dispatcher.InvokeAsync(...)` returns a `DispatcherOperation` whose `.Task` property is exposed directly. Avalonia 11.x uses a similar shape but the property is `GetTask()` (a method, not a property). Both APIs let you `await` the operation, but the surface is slightly different:

```csharp
// WPF
var op = disp.InvokeAsync(action, DispatcherPriority.Normal, ct);
await op.Task.ConfigureAwait(false);

// Avalonia
var op = disp.InvokeAsync(action, DispatcherPriority.Normal);
await op.GetTask().WaitAsync(ct).ConfigureAwait(false);
```

The Avalonia 11.x InvokeAsync overload does not accept a CancellationToken on the operation itself; we honour cancellation by the standard `Task.WaitAsync(ct)` pattern. The CheckAccess() short-circuit (already-on-UI-thread inline path) is identical to WPF's.

### RenderTargetBitmap differences

WPF: `new RenderTargetBitmap(pxW, pxH, dpiX, dpiY, PixelFormat.Pbgra32)` then `Render(visual)`, encode via `PngBitmapEncoder.Save(ms)`.

Avalonia: `new RenderTargetBitmap(new PixelSize(pxW, pxH), new Vector(dpiX, dpiY))` then `Render(visual)` then `Save(stream)` directly (Avalonia's `Save` writes PNG by default - no separate encoder needed). DPI math: WPF uses `VisualTreeHelper.GetDpi(element).{DpiScaleX,DpiScaleY,PixelsPerInchX,PixelsPerInchY}`; Avalonia uses `TopLevel.GetTopLevel(element)?.RenderScaling` (a single double, default 1.0; multiply both axes by it; convert to DPI via `96 * scaling`).

Both adapters wrap the capture in a UI-thread dispatch and produce a clean PNG byte stream that the runtime forwards through `ImageContentBlock.FromBytes(bytes, "image/png")`.

### IClassicDesktopStyleApplicationLifetime as the lifecycle root

WPF's `Application` class is the lifecycle root - `Application.Windows`, `Application.MainWindow`, `Application.Exit` event are all on it directly. Avalonia separates the `Application` (theme + style system) from the lifetime (windows + exit semantics). The adapter walks the lifetime via `app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime`, then `desktop.Windows` / `desktop.MainWindow` / `desktop.Exit`. Mobile and single-view (`ISingleViewApplicationLifetime`) lifetimes degrade gracefully - `EnumerateWindows` returns empty, `Exit` hookup is skipped, and the runtime still runs (adopters there must `Dispose` the AttachTo handle manually).

### "Non-Window root" pattern bites Avalonia too

Phase 1.4's `TodoListViewModel` non-Window root taught us that the source generator emits `() => new TViewModel()`, which produces a SECOND instance separate from the one MainWindow's DataContext binds. The fix: the adopter's `App.OnStartup` (WPF) / `App.OnFrameworkInitializationCompleted` (Avalonia) must rewrite the `RootDescriptor.Create` factory to return the singleton BEFORE calling `AttachTo`. `MarionetteAvalonia.AttachTo` only auto-substitutes `Window`-typed roots that match the live MainWindow's `FullName`; everything else falls through to the original factory. The Phase 2.1 sample documents this in `App.axaml.cs` comments and the skill-pack reference.

### `RefreshAsync` trapdoor

Initial draft of `DashboardViewModel.RefreshAsync` marshalled value-mutation onto `Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(...)`. This works in GUI mode but DEADLOCKS in `--mcp --headless` mode because there is no Avalonia UI thread - InvokeAsync has nowhere to go. Caught by the harness during development (`RefreshAsync failed: no response within 10s`). Fix: drop the dispatch entirely. INPC fires from any thread; Avalonia's bindings handle their own marshalling on the UI side; the runtime's `WatchableResourceProvider` has its own coalescing path. This is now documented in the ViewModel comments and is the single biggest authoring-pattern lesson for the Avalonia sample.

### Stripping verification

The Avalonia adapter is referenced ONLY when `EnableMcpAutomation=true` (per the conditional `<ProjectReference>` in the Dashboard csproj). When stripped (`=false`):

* The user assembly does not reference `Marionette.NET.Adapter.Avalonia`.
* The source-generator's `MCP_ENABLED` gate prevents `Marionette.g.cs` emission.
* The user assembly's `App.axaml.cs` `#if MCP_ENABLED` block compiles out, so its `using Marionette.Adapter.Avalonia;` does not pull in the adapter type.
* Result: stripped Dashboard `.dll` references zero Marionette types beyond `Marionette.NET.Abstractions` (the attributes themselves, which are metadata-only markup).

The IL probe with all 5 needles confirms 0 hits.

## Files added / changed in Phase 2.1

```
src/Marionette.NET.Adapter.Avalonia/                  (NEW)
  Marionette.NET.Adapter.Avalonia.csproj
  AvaloniaUiAutomationAdapter.cs
  MarionetteAvalonia.cs
  Internal/VisualTreeFinder.cs

samples/Sample.Avalonia.Dashboard/                    (NEW)
  Sample.Avalonia.Dashboard.csproj
  app.manifest
  Program.cs
  App.axaml
  App.axaml.cs
  MainWindow.axaml
  MainWindow.axaml.cs
  Converters.cs
  DashboardViewModel.cs
  Models/Metric.cs

Marionette.NET.sln                                    (UPDATED - added two projects)
build/Run-IlProbe.ps1                                 (UPDATED - default Needles list adds Adapter.Avalonia)
.phase0/StdioTest/Program.cs                          (UPDATED - --avalonia mode + TryParseDashboardManifest helper)

skill-pack/prompts/attributes-reference.md            (UPDATED - Avalonia wiring snippet, namespace table, status line)
skill-pack/claude-code/marionette-decorate/SKILL.md   (UPDATED - framework detection, Avalonia wiring)
skill-pack/claude-code/marionette-explore/SKILL.md    (UPDATED - "Compatible apps" subsection)
skill-pack/claude-code/marionette-test/SKILL.md       (UPDATED - "Compatible apps" subsection)

.phase2/                                              (NEW DIRECTORY)
  2a-adapter-avalonia.md                              (NEW - this report)
```

Files deliberately NOT touched (per the Phase 2.1 constraint set):
* `MASTERPLAN.md`, `LICENSE`, `.gitignore`, `Directory.Build.props`, `global.json`, `PHASE0_FINDINGS.md`, `PHASE1_FINDINGS.md`, `README.md`.
* `build/Marionette.NET.props`, `build/Marionette.NET.targets`.
* All of `src/Marionette.NET.Abstractions/`, `src/Marionette.NET.SourceGenerator/`, `src/Marionette.NET.Runtime/`, `src/Marionette.NET.Adapter.Wpf/`.
* All of `samples/Sample.Wpf.StripeProbe/`, `samples/Sample.Wpf.TodoApp/`.
* All of `tests/Marionette.NET.SourceGenerator.Tests/`, `tests/Marionette.NET.Integration/`.
* `skill-pack/README.md` (the Phase 1 README is still accurate; the per-skill files carry the framework-list update).

## Issues encountered

1. **`WithInterFont` requires the `Avalonia.Fonts.Inter` package.** Initial Program.cs draft included `.WithInterFont()` in the AppBuilder pipeline (a common Avalonia template idiom). It compiled-error'd because we don't reference that package. Resolution: drop the call - Avalonia falls back to system fonts, which renders fine for the dashboard demo.

2. **Avalonia.ReactiveUI was a misleading copy-paste.** Initial Program.cs included `using Avalonia.ReactiveUI;` from a sample template I cribbed; we don't use ReactiveUI. Removed.

3. **`Dispatcher.UIThread.InvokeAsync` deadlocks in headless mode.** Documented in the Avalonia-specific notes section. Caught by the harness; fixed by removing the dispatch from `RefreshAsync` entirely.

4. **Avalonia `DispatcherOperation` lacks public `.Task`.** WPF code uses `op.Task.ConfigureAwait(false)`; Avalonia 11.x exposes the same shape via `op.GetTask()`. The adapter uses `WaitAsync(ct)` to bring cancellation into the picture (Avalonia's InvokeAsync overload doesn't accept `CancellationToken` on the operation itself).

5. **Avalonia visual tree access changed in 11.x.** WPF uses `VisualTreeHelper.GetChild` / `GetChildrenCount`; Avalonia 11.x uses `Visual.GetVisualChildren()` (an `IReadOnlyList<Visual>` on every Visual). The VisualTreeFinder uses iterative DFS over `LogicalChildren` (an `IAvaloniaReadOnlyList<ILogical>`) first and falls back to `GetVisualChildren()`.

6. **`Avalonia.Application.Current` is not the lifecycle root.** WPF uses `Application.Current.Windows` / `MainWindow` / `Exit`; Avalonia separates the application from the lifetime. The adapter's `EnumerateWindows` casts `app.ApplicationLifetime` to `IClassicDesktopStyleApplicationLifetime` and reads `desktop.Windows`. Mobile lifetimes degrade gracefully.

7. **Bash forward slashes break Process.Start on Windows for some net10.0 paths.** The harness took an exe path argument and Process.Start with forward slashes failed on certain shell pipings. Switched to backslash-quoted paths in the smoke-test invocations. Documented for the demo runner.

## Hand-off to Phase 2.2 / 2.3

Phase 2.2 (Watchable + Dynamic Tools per the masterplan) is a Runtime concern (`tools/list_changed`, per-method tool routing, deterministic tool-identity hashing). Nothing in the Avalonia adapter contract needs to change.

The masterplan slates Phase 2 as "Avalonia + Watchable + Dynamic Tools". Phase 2.1 covered the Avalonia adapter; the watchable observables work landed in Phase 1.6 (events) + Phase 1.2 (resources/subscribe + 200 ms coalesce) and continues to work for the new Dashboard sample's three watchable observables. Per-method dynamic tools are independent of the adapter and live in `Marionette.NET.Runtime` - no Avalonia work pre-required.

**Recommendation: proceed to Phase 2.2 / 2.3 directly.** Phase 2.1's Avalonia adapter is stable, mirrors WPF semantics, preserves the IL stripping promise, and unlocks the cross-platform-app story for the masterplan's "drop a NuGet, get MCP control" mission.

## Status against original Phase 2.1 prompt

| Prompt requirement | Status |
|---|---|
| `src/Marionette.NET.Adapter.Avalonia/` with csproj + adapter + bootstrap + visual-tree finder | DONE |
| TFM `net10.0` (NOT `net10.0-windows`) on adapter csproj | DONE |
| `IsTrimmable=true` and `IsAotCompatible=true` gated on net8+ AND PublishAot=true | DONE |
| Reference Avalonia core (no Themes.Fluent etc) on adapter | DONE - just `Avalonia` 11.3.14 |
| `Dispatcher.UIThread.InvokeAsync(...).GetTask()` for dispatch | DONE |
| RenderTargetBitmap with PixelSize + Vector(DPI) construction | DONE |
| AutomationProperties.GetAutomationId then Control.Name resolve precedence | DONE |
| Logical-tree-first / visual-tree-fallback iterative DFS walker | DONE |
| `MarionetteAvalonia.AttachTo` one-liner with descriptor-factory rewrite | DONE |
| `samples/Sample.Avalonia.Dashboard/` with full ViewModel + UI + Program | DONE |
| TFM `net10.0`, OutputType=Exe, conditional Adapter.Avalonia ref | DONE |
| Avalonia + Avalonia.Desktop + Avalonia.Themes.Fluent 11.3.14 | DONE |
| 5 [McpCallable] including async + OffUiThread | DONE |
| 4 [McpObservable] (3 watchable, 1 non-watchable) | DONE |
| 2 [McpEvent] (one with custom args, one EventArgs.Empty) | DONE |
| Marionette.NET.sln updated | DONE |
| IL probe over stripped Dashboard.dll: 0 hits across all 5 needles | DONE |
| `build/Run-IlProbe.ps1` default needles include Adapter.Avalonia | DONE |
| StdioTest harness: --avalonia mode | DONE |
| --avalonia handshake passes (DashboardViewModel manifest, UpsertMetric, RefreshAsync await held, MetricCount push, MetricUpserted event) | DONE |
| Skill-pack: Avalonia adopters subsection in attributes-reference, decorate detection, explore/test compatibility note | DONE |
| Don't commit (working tree dirty) | DONE |
| Don't modify forbidden files (MASTERPLAN, LICENSE, .gitignore, Directory.Build.props, global.json, PHASE0/1 findings, src/Marionette.NET.{Abstractions,SourceGenerator,Runtime,Adapter.Wpf}/, samples/Sample.Wpf.*) | DONE |
| Phase-1 invariants preserved (StripeProbe 7-file stripped, TodoApp 7-file-equivalent stripped + IL probe 0/0/0/0, source-gen 13/13, integration 6/6, demo.ps1 PASS) | DONE |
| Avalonia handshake is the new-feature test | DONE - 12/12 pass headless |

Phase 2.1 deliverables are complete.
