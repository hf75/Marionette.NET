# Phase 3.2 (3b) - Adapter.WinUI + Sample.WinUI.FormLab

**Status:** PASS
**Date:** 2026-05-03
**SDK:** .NET 10.0.202 - Microsoft.WindowsAppSDK 1.8.260416003 - ModelContextProtocol 1.2.0 - Roslyn 4.14.0

## Goal & verdict

Phase 3.2 ships the third Marionette adapter and its canonical sample, mirroring the WPF (Phase 1.3) and Avalonia (Phase 2.1) work against WinUI 3 / Windows App SDK:

1. `src/Marionette.NET.Adapter.WinUI/` - the production `IUiAutomationAdapter` impl for WinUI 3 (Windows-only by definition, pinned to WinAppSDK 1.8.x stable).
2. `samples/Sample.WinUI.FormLab/` - a real INPC-plumbed settings-form sample with a diverse input mix (TextBox, NumberBox, ToggleSwitch, ComboBox, two Buttons), six `[McpCallable]` methods, five `[McpObservable]` properties (three watchable), one `[McpEvent]` (with a typed FormSubmittedEventArgs payload).
3. Skill-pack updates: WinUI adopters subsection in `attributes-reference.md`, framework detection in `marionette-decorate`, "Compatible apps: WPF + Avalonia + WinUI" mentions in `marionette-explore` / `marionette-test`.
4. Tooling updates: `build/Run-IlProbe.ps1` default needles include `Adapter.WinUI` (now 6 needles); `.phase0/StdioTest/Program.cs` has a `--winui` mode handshaking against FormLab.

**Verdict: GO for Phase 3.3.** All build-matrix steps pass, the IL stripping promise from Phase 0 Spike A holds (0 hits across all 6 needles on all 4 stripped samples), the new WinUI headless harness scores 18/18, and the existing WPF/Avalonia tests hold steady (25/25 source-gen, 7/7 + 2 skip integration, demo.ps1 PASS).

## What was built

### A. `src/Marionette.NET.Adapter.WinUI/`

Five production source files plus the csproj. Mirrors the WPF/Avalonia adapter shape with WinUI-specific quirks isolated.

| File | Purpose |
|---|---|
| `Marionette.NET.Adapter.WinUI.csproj` | TFM `net10.0-windows10.0.19041.0` (matching WinAppSDK 1.8.x's bundled SDK ref). `<UseWinUI>true</UseWinUI>`, `<WindowsPackageType>None</WindowsPackageType>` (unpackaged-first). `<PackageReference Include="Microsoft.WindowsAppSDK" Version="1.8.260416003" />`. `IsAotCompatible` / `IsTrimmable` gated on PublishAot=true. ProjectReference to Marionette.NET.Runtime. |
| `WinUiAutomationAdapter.cs` | Production `IUiAutomationAdapter` impl. `DispatchAsync(Action/Func<T>, ct)` wraps `DispatcherQueue.TryEnqueue(...)` in a `TaskCompletionSource`. `CaptureScreenshotAsync` composes a UI-thread `RunCaptureAsync` that uses `RenderTargetBitmap.RenderAsync` (async!) -> `GetPixelsAsync` -> `BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ...)` -> `SetPixelData` -> `FlushAsync` -> read PNG bytes from `InMemoryRandomAccessStream`. `ResolveControlAsync` walks tracked windows via `WindowTracker.Snapshot()`. `SimulateInputAsync` / `RaiseEventAsync` delegate to the helper classes below. |
| `MarionetteWinUI.cs` | One-call `AttachTo(Application, Window, IReadOnlyList<RootDescriptor>, string[]?, ILoggerFactory?)` bootstrap. Captures `DispatcherQueue.GetForCurrentThread()` (must be called on UI thread). Tracks the supplied MainWindow. Rewrites `RootDescriptor.Create` factories to dispatch through the UI thread AND prefer the live MainWindow when type-compatible. Spawns `MarionetteHost.RunAsync` on a background `Task`. Hooks `Window.Closed` for clean shutdown. Returns an `IDisposable` for explicit detach. |
| `Internal/WindowTracker.cs` | Thread-safe registry of live `Window` instances. WinUI 3 doesn't expose `Application.Windows` (unlike WPF / Avalonia classic-desktop), so the adapter maintains its own list. `Track(window)` registers + auto-unregisters on `Window.Closed`. `Snapshot()` and `FirstReadyWindow()` are the read APIs. |
| `Internal/VisualTreeFinder.cs` | Iterative-DFS named-element resolver. Walks every tracked window's `Window.Content`. Match precedence: `Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId()` first, `FrameworkElement.Name` second. WinUI 3 doesn't expose a logical tree separate from the visual tree, so this is a single visual-tree walk via `Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild` (NOT `System.Windows.Media.VisualTreeHelper`). |
| `Internal/WinUiInputSimulator.cs` | Eight-kind input simulator. `click` / `double_click` prefer `ButtonAutomationPeer.Invoke()` (semantic, framework-routed, works unpackaged + unelevated) and fall back to `InputInjector` for non-button targets and elevated/manifest-capability scenarios. `right_click` / `key_*` / `mouse_move` go straight to `Windows.UI.Input.Preview.Injection.InputInjector`. `type_text` on a TextBox sets `TextBox.Text` directly (semantic > visual); on other targets uses `InputInjector` with Unicode scancode mode. Returns `success:false` with logged limitation when InputInjector.TryCreate returns null. |
| `Internal/WinUiEventRaiser.cs` | Reflection-based CLR-event raiser. WinUI 3 has NO `EventManager.GetRoutedEventsForOwner` and NO `UIElement.RaiseEvent` (the WPF idiom). Routed events surface as standard CLR events (`event RoutedEventHandler Click`). The raiser walks the type chain looking for an `EventInfo` with the requested name, pulls the compiler-emitted private backing-field delegate, and invokes it with a default-constructed args type. Wrapped in `[UnconditionalSuppressMessage]` for IL2026/IL2070/IL2075 - documented AOT/trim caveat in the public adapter XML doc. |

### B. `samples/Sample.WinUI.FormLab/`

The canonical WinUI 3 adopter-reference. A real settings/configuration form, distinct from TodoApp's list and Dashboard's metric stream. Diverse input controls (TextBox, NumberBox, ToggleSwitch, ComboBox, two Buttons) so adopters see the full WinUI form vocabulary working through Marionette.

| File | Purpose |
|---|---|
| `Sample.WinUI.FormLab.csproj` | TFM matches the adapter (`net10.0-windows10.0.19041.0`). `<OutputType>WinExe</OutputType>`, `<UseWinUI>true</UseWinUI>`, `<WindowsPackageType>None</WindowsPackageType>`. `<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>` to suppress the XAML compiler's auto-emitted `Program.Main` (we ship our own). Conditional Adapter.WinUI ProjectReference + always-on Abstractions ProjectReference + always-on SourceGenerator analyzer. Pulls `Microsoft.WindowsAppSDK 1.8.260416003`. Imports `build/Marionette.NET.props` and `.targets`. |
| `app.manifest` | PerMonitor V2 DPI awareness (the only DPI mode WinUI 3 supports). Win10 supportedOS. |
| `Program.cs` | Custom `[STAThread] Main`. Three modes: no flag -> `RunGui()`; `--mcp --headless` -> `MarionetteHost.RunAsync` directly (NoOpAdapter); `--mcp` (GUI) -> falls through to `RunGui()` and lets `App.OnLaunched` wire the host. `RunGui` calls `WinRT.ComWrappersSupport.InitializeComWrappers()` and `Application.Start(p => { ctx = ...; SetSynchronizationContext; new App(); })` - the WinUI canonical entry point. |
| `App.xaml` / `App.xaml.cs` | `OnLaunched` constructs the MainWindow, activates it, then (under `#if MCP_ENABLED`) rewrites the `FormLabViewModel` root's `Create` factory to return `FormLabViewModel.Shared` and calls `MarionetteWinUI.AttachTo(this, _mainWindow, bridgedRoots, args)`. Same factory-rewrite pattern as TodoApp / Dashboard. |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | Form layout: header, status strip (3 cols), then form fields (Name TextBox, Age NumberBox, Notifications ToggleSwitch, Theme ComboBox), action buttons (Reset, Submit). Each control has `automation:AutomationProperties.AutomationId` set so `simulate_input` resolves them by stable name. Code-behind delegates user input events to ViewModel methods. |
| `Models/FormState.cs` | Read-only snapshot of the form's submitted state. The payload of `FormSubmittedEventArgs`. |
| `FormLabViewModel.cs` | The `[McpRoot]`. See breakdown below. |

### C. `FormLabViewModel` decorations

The "richer-than-TodoApp form-shaped" promise made concrete:

| Decoration | Member | Notes |
|---|---|---|
| `[McpRoot]` | class | Implicit name `FormLabViewModel`. |
| `[McpCallable]` | `SetName(string)` | Updates the bound textbox. |
| `[McpCallable]` | `SetAge(int)` | Negative values clamped to zero. |
| `[McpCallable]` | `ToggleNotifications()` | Inverts the bound ToggleSwitch. |
| `[McpCallable]` | `SetTheme(string)` | Validates against {Light, Dark, Default}. |
| `[McpCallable]` | `Submit()` | Captures snapshot, fires `FormSubmitted` event. Sets `HasSubmitted=true`. |
| `[McpCallable]` | `Reset()` | Clears every field to its default; preserves `HasSubmitted`. |
| `[McpObservable(Watchable=true)]` | `Name` | INPC fires from `SetName`. |
| `[McpObservable(Watchable=true)]` | `Age` | INPC fires from `SetAge`. |
| `[McpObservable(Watchable=true)]` | `NotificationsEnabled` | INPC fires from `ToggleNotifications`. |
| `[McpObservable]` | `Theme` | Non-watchable to demonstrate alternate shape. |
| `[McpObservable]` | `HasSubmitted` | Non-watchable; "form was used" sentinel that survives Reset. |
| `[McpEvent]` | `FormSubmitted` (with `FormSubmittedEventArgs` carrying `Name`, `Age`, `NotificationsEnabled`, `Theme`) | Fires on every `Submit`. |

The ViewModel is framework-agnostic - no WinUI types touched. INPC fires from any thread; the WinUI bindings handle their own UI-side dispatch through DispatcherQueue.

### D. Solution wiring

`Marionette.NET.sln` adds two projects:
* `Marionette.NET.Adapter.WinUI` under the `src` solution folder (GUID `{C2C2C2C2-...}`).
* `Sample.WinUI.FormLab` under the `samples` folder (GUID `{D3D3D3D3-...}`).

All four configurations (`Debug|Any CPU`, `Release|Any CPU`) wired with `ActiveCfg` + `Build.0`.

### E. IL probe regression gate

`build/Run-IlProbe.ps1` default `$Needles` array now includes `Adapter.WinUI` as the fourth entry (mirrored alphabetically with the other adapter needles). The script signature is unchanged - adopters who already invoke it with explicit `-Needles` still work; defaults pick up the new check automatically.

### F. StdioTest harness `--winui` mode

`.phase0/StdioTest/Program.cs` now accepts `--winui`. The new mode runs against `Sample.WinUI.FormLab.exe --mcp --headless` and asserts eighteen checks:

1. `initialize` handshake.
2. `tools/list` returns the four meta-tools (sorted equality).
3. `tools/list` also contains the six per-method dynamic tools (FormLabViewModel.SetName, SetAge, ToggleNotifications, SetTheme, Submit, Reset).
4. `inspect_app_api` lists FormLabViewModel with all 6 callables + 5 observables + 1 event.
5. `read_observable Name` returns "" baseline.
6. `read_observable Age` returns 0 baseline.
7. `read_observable NotificationsEnabled` returns true baseline.
8. `read_observable HasSubmitted` returns false baseline.
9. `invoke_method SetName("Test")` succeeds.
10. `read_observable Name` returns "Test" after.
11. `invoke_method SetAge(30)` succeeds.
12. `read_observable Age` returns 30 after.
13. `invoke_method ToggleNotifications()` succeeds.
14. `read_observable NotificationsEnabled` returns false after.
15. `resources/subscribe` to events/FormSubmitted + `Submit()` produces a `notifications/resources/updated`.
16. The event resource read carries args.Name="Test" AND args.Age=30 (verifies the typed payload round-trips).
17. `read_observable HasSubmitted` returns true after Submit.
18. `capture_screenshot` returns the documented `screenshot_not_supported` error (NoOpAdapter in headless).

Plus stdout-purity assertion (zero pollution lines) and clean child exit.

### G. Skill-pack additions

* `skill-pack/prompts/attributes-reference.md`:
  * Status header bumped to Phase 3.2.
  * Namespace table adds `MarionetteWinUI.AttachTo` -> `Marionette.Adapter.WinUI`.
  * New "WinUI 3 - App.OnLaunched" wiring snippet section (after the Avalonia section).
  * New "Non-Window root binding (WinUI)" snippet mirroring the WPF/Avalonia pattern.
  * TFM choice for WinUI adopters documented (`net10.0-windows10.0.<sdk>.0`).
  * Unpackaged-first guidance + simulate_input on WinUI caveats (InputInjector + manifest capability requirements).
* `skill-pack/claude-code/marionette-decorate/SKILL.md`:
  * Step 8 "Wire the host" now detects three frameworks: `<UseWPF>true</UseWPF>` -> WPF, `<PackageReference Include="Avalonia"` -> Avalonia, `<UseWinUI>true</UseWinUI>` OR `<PackageReference Include="Microsoft.WindowsAppSDK"` -> WinUI 3.
  * WinUI wiring snippet added alongside the WPF and Avalonia ones.
  * Notes on `DISABLE_XAML_GENERATED_MAIN` (WinUI's analogue of `EnableDefaultApplicationDefinition=false`).
* `skill-pack/claude-code/marionette-explore/SKILL.md`: "Compatible apps" subsection mentions WinUI 3.
* `skill-pack/claude-code/marionette-test/SKILL.md`: same. Plus a WinUI-specific note about simulate_input behaviour for unelevated/unmanifested processes.

## Build matrix results

All commands run from `C:\Home\Code\nw.Automation`. .NET 10.0.202.

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build Marionette.NET.sln -c Debug -m:1` | PASS - 0 warnings, 0 errors (12 projects: 10 from Phase 2.2 + Adapter.WinUI + Sample.WinUI.FormLab) |
| 2 | `dotnet build Marionette.NET.sln -c Release -m:1` | PASS - 0 warnings, 0 errors |
| 3 | `dotnet test tests/Marionette.NET.SourceGenerator.Tests/...csproj -c Debug --no-build` | PASS - 25/25 (unchanged from Phase 3.1) |
| 4 | `dotnet test tests/Marionette.NET.Integration/...csproj -c Debug --no-build` | PASS - 7 passed + 2 skipped (EC-1..EC-7 + EC-8/EC-9 gated, unchanged) |
| 5 | `dotnet build samples/Sample.WinUI.FormLab/...csproj -c Release -p:EnableMcpAutomation=false` | PASS - stripped output, 0 warnings |
| 6 | `dotnet build samples/Sample.WinUI.FormLab/...csproj -c Debug -p:EnableMcpAutomation=true` | PASS - 0 warnings |
| 7 | IL probe over FormLab stripped DLL (6 needles) | PASS - 0 hits across all 6 needles |
| 8 | IL probe over StripeProbe stripped DLL (regression check, new Adapter.WinUI needle picks up nothing) | PASS - 0 hits all 6 |
| 9 | IL probe over TodoApp stripped DLL | PASS - 0 hits all 6 |
| 10 | IL probe over Avalonia Dashboard stripped DLL | PASS - 0 hits all 6 |
| 11 | `dotnet StdioTest.dll <Sample.WinUI.FormLab.exe> --winui` (NEW) | PASS - 18/18 checks, 25 JSON-RPC frames, 0 pollution |
| 12 | `pwsh .phase1/demo.ps1 -NoBuild` (regression) | PASS |

### IL probe - FormLab (cmd 7)

```
[PASS] Marionette.NET.Runtime: TOTAL hits across 1 file(s): 0
[PASS] Adapter.Wpf:            TOTAL hits across 1 file(s): 0
[PASS] Adapter.Avalonia:       TOTAL hits across 1 file(s): 0
[PASS] Adapter.WinUI:          TOTAL hits across 1 file(s): 0
[PASS] Marionette.Ai:          TOTAL hits across 1 file(s): 0
[PASS] ModelContextProtocol:   TOTAL hits across 1 file(s): 0
PASS - stripped build contains zero forbidden symbols.
```

The stripped FormLab build's user assembly references zero Marionette types beyond Abstractions. (The shipped binary tree includes the WindowsAppSDK / WinUI DLLs - those are inherent to WinUI, not Marionette's responsibility.)

### IL probe - regression checks (cmds 8, 9, 10)

All three previous samples (StripeProbe, TodoApp, Avalonia Dashboard) hit 0 across all 6 needles when their stripped Release builds are rebuilt with `EnableMcpAutomation=false`. The new `Adapter.WinUI` needle is harmless on WPF/Avalonia builds - they never referenced WinUI, so there's nothing to find.

Note: the integration-test framework's `BeforeBuild` target force-builds TodoApp with `EnableMcpAutomation=true`, so a fresh `dotnet test` run will leave TodoApp's Release output as MCP-on. Re-running the IL probe immediately after `dotnet test` will report leaks. Workaround: pass `-p:EnableMcpAutomation=false --no-incremental` to the explicit Release rebuild before running the probe (this is the same pattern Phase 2.1 used).

### Stdio harness output (cmd 11)

```
=== Phase 3.2 WinUI FormLab stdio handshake harness ===
PASS - initialize handshake (server: Marionette.NET 0.0.1, protocol 2025-11-25)
PASS - tools/list contains all four Phase-1 tools (got: capture_screenshot,inspect_app_api,invoke_method,read_observable)
PASS - tools/list also contains the 6 per-method dynamic tools (FormLabViewModel.SetName,SetAge,ToggleNotifications,SetTheme,Submit,Reset)
PASS - inspect_app_api returned FormLabViewModel manifest with all 6 callables + 5 observables + 1 event
PASS - read_observable Name initially returned empty string
PASS - read_observable Age initially returned 0
PASS - read_observable NotificationsEnabled initially returned true
PASS - read_observable HasSubmitted initially returned false
PASS - invoke_method SetName("Test") succeeded
PASS - read_observable Name returned 'Test' after SetName
PASS - invoke_method SetAge(30) succeeded
PASS - read_observable Age returned 30 after SetAge
PASS - invoke_method ToggleNotifications() succeeded
PASS - read_observable NotificationsEnabled returned false after ToggleNotifications
PASS - resources/subscribe + Submit produced an event notification on marionette://FormLabViewModel/events/FormSubmitted (sequence=1, count=1, args.Name="Test", args.Age=30 present)
PASS - read_observable HasSubmitted returned true after Submit
PASS - capture_screenshot surfaced a structured 'screenshot_not_supported' error (NoOpAdapter)
PASS - child exited cleanly with code 0
stdout summary: 25 JSON-RPC frames, 0 pollution lines
=== Phase 3.2 WinUI FormLab handshake: PASS ===
```

Stderr lines (56 total) are all SDK-internal informational logs from `ModelContextProtocol.Server.{StdioServerTransport, McpServer}` plus `Marionette.Runtime.Tools.DynamicToolRegistry` ("Dynamic per-method tools registered: 6.") - no Marionette code wrote to stdout.

## WinUI-specific notes

### TFM choice and Windows App SDK pinning

* TFM: `net10.0-windows10.0.19041.0` - matches the `Microsoft.Windows.SDK.NET.Ref` projection that `Microsoft.WindowsAppSDK 1.8.x` brings transitively. The naive `net10.0-windows` TFM doesn't resolve the WinUI types because the SDK ref isn't the default Windows desktop pack.
* WinAppSDK version: `1.8.260416003` - the latest stable identified locally (release channel "stable" per `WindowsAppSDK-VersionInfo.cs`). Adopters who track the release notes can override; the adapter doesn't pin a specific minor.
* Per-monitor V2 DPI awareness via `app.manifest` is required (the only DPI mode WinUI 3 supports).

### Unpackaged deployment

* `<WindowsPackageType>None</WindowsPackageType>` ships an unpackaged `.exe` (no MSIX). Adopters who want packaged builds set `MSIX` instead and provide a `Package.appxmanifest`; the adapter is identical either way.
* Unpackaged WinAppSDK 1.x relies on the SDK's auto-emitted bootstrap (the runtime DLL set is staged alongside the .exe via `UseRidGraph` + `RuntimeIdentifiers`). No manual `Bootstrap.Initialize()` call is needed for Phase 3.2.

### DispatcherQueue vs Dispatcher (WPF) vs Dispatcher.UIThread (Avalonia)

WinUI's `Microsoft.UI.Dispatching.DispatcherQueue` is the public threading primitive (replacing WPF's `Dispatcher`). Notable differences:
* `DispatcherQueue.GetForCurrentThread()` requires being on the UI thread; `MarionetteWinUI.AttachTo` enforces this with an `InvalidOperationException` if called from a non-UI thread.
* `DispatcherQueue.TryEnqueue(DispatcherQueueHandler)` is fire-and-forget - no awaitable Task surface. The adapter wraps each enqueue in a `TaskCompletionSource` so the IUiAutomationAdapter contract's `Task DispatchAsync(...)` shape holds.
* `DispatcherQueue.HasThreadAccess` is the analogue of `Dispatcher.CheckAccess` / `Dispatcher.UIThread.CheckAccess`. Same short-circuit pattern as the other adapters.

### RenderTargetBitmap async pattern

WinUI's `RenderTargetBitmap` is async-end-to-end (`RenderAsync`, `GetPixelsAsync`), unlike WPF's synchronous `Render` + `Save` and Avalonia's synchronous `Render` + `Save`. The adapter composes:
1. `DispatchAsync(Action, ct)` to get to the UI thread.
2. Inside the dispatched lambda: kick off an async helper `RunCaptureAsync` that completes a captured outer `TaskCompletionSource<byte[]>`.
3. The outer adapter method awaits the TCS Task.

This avoids deadlocks (no `.GetAwaiter().GetResult()` on the UI thread) and keeps the adapter's external interface synchronous-async-friendly.

PNG encoding goes through `Windows.Graphics.Imaging.BitmapEncoder` (NOT WPF's `PngBitmapEncoder` and NOT Avalonia's RTB.Save). Pattern: `BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras)` -> `encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, w, h, dpiX, dpiY, bgraBytes)` -> `encoder.FlushAsync()` -> read PNG bytes from the `InMemoryRandomAccessStream`.

### InputInjector behavior + caveats (the trapdoor)

`Windows.UI.Input.Preview.Injection.InputInjector.TryCreate()` returns `null` when:
1. The process isn't elevated, AND
2. The app's manifest doesn't declare the `inputInjectionBrokered` capability (`uap5:Capability` element in Package.appxmanifest).

For Phase 3.2's unpackaged FormLab sample, neither of those is the default - `TryCreate` returns null when the user runs the .exe normally. The adapter handles this by:
* For `kind:"click"` / `"double_click"`: prefer `ButtonAutomationPeer.Invoke()` first (semantic, framework-routed, works unpackaged + unelevated). Only fall through to `InputInjector` for non-button targets.
* For `kind:"type_text"` on a `TextBox` target: set `TextBox.Text` directly (semantic > visual per masterplan tenet 2). Adopters who need `simulate_input` text input rarely need the keyboard pipeline; the semantic API is faster and more reliable.
* For `kind:"key_*"` / `"mouse_move"` / non-TextBox `"type_text"`: use `InputInjector`. Returns `success:false` with a logged limitation when `TryCreate` returns null.

This honors the brief's documented fallback: "If the InputInjector path is too brittle ... fall back to the routed-event raise pattern that Avalonia uses." For WinUI 3, `RaiseEvent` doesn't exist (no `EventManager`, no `UIElement.RaiseEvent`); the equivalent semantic-fallback is the AutomationPeer path.

### WindowTracker pattern

WinUI 3 does NOT expose `Application.Windows`. Adopters create `Microsoft.UI.Xaml.Window` instances explicitly in `App.OnLaunched` and they are NOT auto-tracked anywhere. The adapter maintains its own thread-safe registry (`Internal/WindowTracker.cs`) - `MarionetteWinUI.AttachTo` calls `WindowTracker.Track(mainWindow)` for the supplied window, and adopters with secondary windows can call `WindowTracker.Track` directly for each. Auto-unregistration is wired through `Window.Closed`.

`VisualTreeFinder.FindByName` walks `WindowTracker.Snapshot()` (each Window's `Content`); `WinUiAutomationAdapter.CaptureScreenshotAsync(null)` resolves the target via `WindowTracker.FirstReadyWindow()` (first tracked window with non-null Content).

### CLR-event reflection for raise_event

WinUI 3 surfaces routed events as standard CLR events on the control type. There is no public `RoutedEvent` static-field idiom (WPF/Avalonia) and no `EventManager.GetRoutedEventsForOwner`. The Phase 3.2 raiser walks the type chain looking for an `EventInfo` with the requested name, pulls the compiler-emitted private backing-field delegate, and `DynamicInvoke`s it with a default-constructed args type.

This is meaningfully more trim-fragile than the WPF/Avalonia raisers; the public adapter XML doc surfaces the AOT caveat. Adopters who need reliable `raise_event` coverage in AOT scenarios should use the alternative path: decorate the handler logic on a `[McpCallable]` method and call it via `invoke_method`. This keeps the semantic intent clear and works in every shipping configuration.

### Non-Window root pattern bites WinUI too

Phase 1.4 (TodoApp) and Phase 2.1 (Dashboard) taught us that the source generator emits `() => new TViewModel()`, which produces a SECOND instance separate from the one MainWindow's DataContext binds. The fix: `App.OnLaunched` rewrites the `RootDescriptor.Create` factory to return the singleton BEFORE calling `AttachTo`. The Phase 3.2 sample documents this in `App.xaml.cs` comments and the skill-pack reference. `MarionetteWinUI.AttachTo` only auto-substitutes Window-typed roots that match the live MainWindow's `FullName`; everything else falls through to the original factory.

### XAML-compiler `Program.Main` collision

The WinUI XAML compiler auto-emits a `Program.Main` inside `App.g.i.cs` unless `DISABLE_XAML_GENERATED_MAIN` is defined. Since FormLab ships its own custom `Main` in `Program.cs` (mirroring TodoApp / Dashboard), the csproj defines that constant: `<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>`. This is the WinUI analogue of WPF's `<EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>`.

### Comment escaping in csproj

XML's "no `--` in comments" rule bit the initial draft of `Sample.WinUI.FormLab.csproj`: a comment containing the literal `--mcp --headless` flag failed to parse with `MSB4025: An XML comment cannot contain '--'`. Workaround: rephrase to "the headless MCP mode" without the literal flag. (Phase 1/2 adapter csprojs avoided this by not embedding flag examples in inline XML comments.)

### TypedEventHandler delegate shape

WinUI's `Window.Closed` is `TypedEventHandler<object, WindowEventArgs>` - NOT `TypedEventHandler<Window, WindowEventArgs>` as you might expect. The initial draft used the strongly-typed sender variant; the C# compiler rejected the implicit conversion. Fixed by using `object` as the sender type. (Same shape as the WinRT runtime exposes the event.)

## Deviations from the WPF/Avalonia pattern

* **No `EventManager` / no `UIElement.RaiseEvent`.** WinUI 3 has neither. The event raiser uses CLR-event reflection on compiler-emitted backing fields - meaningfully more trim-fragile than the WPF/Avalonia raisers. Documented as a Phase 5 AOT-hardening hand-off.
* **No `Application.Windows`.** Replaced by `WindowTracker`, an explicit registry maintained by `MarionetteWinUI.AttachTo`. Adopters with multiple windows call `WindowTracker.Track` per window.
* **No logical tree.** WinUI's `VisualTreeHelper.GetChild` walks the only tree there is. The visual-tree finder is single-pass (no logical-tree-first / visual-tree-fallback split).
* **Async screenshot.** `RenderTargetBitmap.RenderAsync` and `BitmapEncoder.CreateAsync`/`FlushAsync` force the screenshot path to be async-end-to-end. Composed by dispatching an outer `TaskCompletionSource<byte[]>`-completing helper.
* **Window is not a UIElement.** WinUI 3's `Microsoft.UI.Xaml.Window` is a standalone class, not a `DependencyObject` and not part of the visual tree. The adapter captures `window.Content` (the visual root) for screenshot/finder operations.
* **InputInjector is in `Windows.UI.Input.Preview.Injection`** (the legacy projection name, not `Microsoft.UI.Input.Preview.Injection`). The "Preview" namespace persists despite WinUI 3's `Microsoft.UI.*` rebrand. The adapter pulls the projection from the Windows SDK ref that WinAppSDK 1.8.x brings transitively.
* **Required UI thread for AttachTo.** WPF and Avalonia versions tolerate being called from any thread (they dispatch). WinUI's `DispatcherQueue.GetForCurrentThread` requires the UI thread; the adapter throws if not. Adopters call from `App.OnLaunched`, which already runs on the UI thread.

## Files added / changed in Phase 3.2

```
src/Marionette.NET.Adapter.WinUI/                     (NEW)
  Marionette.NET.Adapter.WinUI.csproj
  WinUiAutomationAdapter.cs
  MarionetteWinUI.cs
  Internal/WindowTracker.cs
  Internal/VisualTreeFinder.cs
  Internal/WinUiInputSimulator.cs
  Internal/WinUiEventRaiser.cs

samples/Sample.WinUI.FormLab/                         (NEW)
  Sample.WinUI.FormLab.csproj
  app.manifest
  Program.cs
  App.xaml
  App.xaml.cs
  MainWindow.xaml
  MainWindow.xaml.cs
  FormLabViewModel.cs
  Models/FormState.cs

Marionette.NET.sln                                    (UPDATED - added two projects)
build/Run-IlProbe.ps1                                 (UPDATED - default Needles list adds Adapter.WinUI)
.phase0/StdioTest/Program.cs                          (UPDATED - --winui mode + TryParseFormLabManifest helper + ReadObservableString/Bool helpers)

skill-pack/prompts/attributes-reference.md            (UPDATED - WinUI wiring snippet, namespace table, status line)
skill-pack/claude-code/marionette-decorate/SKILL.md   (UPDATED - framework detection, WinUI wiring, DISABLE_XAML_GENERATED_MAIN guidance)
skill-pack/claude-code/marionette-explore/SKILL.md    (UPDATED - "Compatible apps" subsection mentions WinUI)
skill-pack/claude-code/marionette-test/SKILL.md       (UPDATED - "Compatible apps" + WinUI simulate_input note)

.phase3/3b-adapter-winui.md                           (NEW - this report)
```

Files deliberately NOT touched (per the Phase 3.2 constraint set):
* `MASTERPLAN.md`, `LICENSE`, `.gitignore`, `Directory.Build.props`, `global.json`, `PHASE0_FINDINGS.md`, `PHASE1_FINDINGS.md`, `PHASE2_FINDINGS.md`, `README.md`.
* `build/Marionette.NET.props`, `build/Marionette.NET.targets`.
* All of `src/Marionette.NET.Abstractions/`, `src/Marionette.NET.SourceGenerator/`, `src/Marionette.NET.Runtime/`, `src/Marionette.NET.Adapter.Wpf/`, `src/Marionette.NET.Adapter.Avalonia/`.
* All of `samples/Sample.Wpf.*/`, `samples/Sample.Avalonia.*/`.
* All of `tests/Marionette.NET.SourceGenerator.Tests/`, `tests/Marionette.NET.Integration/`.
* `.phase1/demo.ps1`, `.phase2/`, `.phase3/3a-input-events.md`.

## Issues encountered

1. **TFM mismatch.** Initial draft used `net10.0-windows`. The naive TFM doesn't pull the WinUI types because the SDK ref isn't bound. Resolution: pin `net10.0-windows10.0.19041.0` (matching WAS 1.8's bundled SDK ref).

2. **Auto-emitted XAML compiler `Program.Main`.** First build hit `CS0101: namespace already contains a definition for Program`. Fixed by adding `<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>` to the sample csproj.

3. **`DispatcherQueueSynchronizationContext.CreateOnCurrentThread` doesn't exist.** Used in the initial Program.cs draft; the actual API is `new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread())`. Fixed.

4. **`TypedEventHandler<Window, WindowEventArgs>` rejected.** The actual delegate type for `Window.Closed` is `TypedEventHandler<object, WindowEventArgs>`. Fixed by using `object` for the sender.

5. **XML comment with `--mcp --headless` literal.** MSBuild's XML parser hates `--` in comments. Rephrased to "the headless MCP mode" without the literal flag.

6. **Stripped TodoApp regression after `dotnet test`.** The integration-test framework's BeforeBuild target force-rebuilds TodoApp with `EnableMcpAutomation=true`, leaving its Release output MCP-on. IL probe after `dotnet test` reports leaks unless the explicit Release rebuild forces `=false --no-incremental`. Same trapdoor as Phase 2.1 cmd 1's parallel-build serialization issue. Worth a Phase 6 cleanup (gate the BeforeBuild target on a sentinel or use a separate `BaseIntermediateOutputPath`).

7. **No `EventManager` in WinUI 3.** Forced the event raiser to use CLR-event reflection on compiler-emitted backing fields. Documented in the WinUiEventRaiser file header and the public adapter XML doc.

## Phase-3.3 hand-off

Phase 3.3 (Multi-Window Routing) is independent of Phase 3.2's adapter creation. The `IUiAutomationAdapter` contract may evolve to add an optional `windowId` parameter to `SimulateInputAsync` / `RaiseEventAsync` (and to `EnumerateWindowsAsync` to return stable IDs); the WinUI adapter's `WindowTracker` already snapshots all live windows, so adding stable per-window IDs is a small addition. The `MarionetteWinUI.AttachTo` signature can stay backward-compatible by defaulting `windowId = null` to the live MainWindow.

Nothing else from Phase 3.2 needs API surface beyond what's already public:

* `MarionetteWinUI.AttachTo(Application, Window, IReadOnlyList<RootDescriptor>, string[]?, ILoggerFactory?)` - the one-line wiring point.
* `WinUiAutomationAdapter` - visible by name (the source generator's manifest never references it; only `App.OnLaunched` does).
* `WindowTracker` is `internal`; if Phase 3.3 needs adopter-side multi-window registration, promote `Track` to `public` on `MarionetteWinUI`.

## Status against the original Phase 3.2 prompt

| Prompt requirement | Status |
|---|---|
| `src/Marionette.NET.Adapter.WinUI/` with csproj + adapter + bootstrap + visual-tree finder + input simulator + event raiser | DONE |
| TFM `net10.0-windows10.0.<latest>.0` on adapter csproj | DONE - `19041.0` matching WAS 1.8.x |
| `<UseWinUI>true</UseWinUI>` + `<WindowsPackageType>None</WindowsPackageType>` | DONE |
| WindowsAppSDK pinned to current stable | DONE - 1.8.260416003 |
| `IsTrimmable` + `IsAotCompatible` gated on net8+ AND PublishAot=true | DONE |
| `WinUiAutomationAdapter` with DispatcherQueue + RenderTargetBitmap + AutomationProperties resolution | DONE |
| `Internal/VisualTreeFinder.cs` walking AutomationId + Name precedence | DONE |
| `Internal/WinUiInputSimulator.cs` using InputInjector | DONE - with AutomationPeer fallback |
| `Internal/WinUiEventRaiser.cs` for routed events | DONE - via CLR-event reflection |
| `MarionetteWinUI.AttachTo` one-liner with descriptor-factory rewrite | DONE |
| `samples/Sample.WinUI.FormLab/` with full ViewModel + UI + Program | DONE |
| Form-shaped UI distinct from TodoApp's list and Dashboard's metrics | DONE - settings form with TextBox/NumberBox/ToggleSwitch/ComboBox/buttons |
| 5+ [McpCallable] (we shipped 6: SetName, SetAge, ToggleNotifications, SetTheme, Submit, Reset) | DONE |
| Multiple [McpObservable] (3+ watchable) | DONE - 3 watchable (Name, Age, NotificationsEnabled) + 2 non-watchable (Theme, HasSubmitted) |
| [McpEvent] with typed payload | DONE - FormSubmitted with FormSubmittedEventArgs |
| Marionette.NET.sln updated | DONE |
| IL probe over stripped FormLab.dll: 0 hits across all 6 needles | DONE |
| `build/Run-IlProbe.ps1` default needles include Adapter.WinUI | DONE |
| StdioTest harness: --winui mode | DONE |
| --winui handshake passes (manifest, invoke_method, observables, event subscribe + read) | DONE - 18/18 |
| Skill-pack: WinUI adopters subsection in attributes-reference, decorate detection, explore/test compatibility note | DONE |
| Don't commit (working tree dirty) | DONE |
| Don't modify forbidden files | DONE |
| Phase 1/2/3.1 invariants preserved | DONE - 25/25 source-gen, 7/7 + 2 skip integration, demo.ps1 PASS, 0 IL leaks across 4 stripped samples |
| WinUI handshake is the new-feature test | DONE - 18/18 PASS headless |

Phase 3.2 deliverables are complete.
