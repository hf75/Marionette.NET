# Phase 15 — Windows Forms Adapter Findings

**Status:** Complete
**Date:** 2026-05-08
**SDK:** .NET 10
**One-line summary:** WinForms adapter ships, fifth framework on the Marionette.NET roster, ~1300 LOC, no surprises — Phase 14's `Win32InputInjector` made `simulate_input` essentially free.

## Why Phase 15 ran

The "tollkühn idea" conversation (decompile-and-inject path for source-less .NET apps) made it obvious that the WinForms adapter gap was strategically significant: the legacy LOB-app market that needs an "MCP-attach with no source" workflow is dominantly WinForms (apps from 2008-2015 that the original developer no longer maintains). Without a WinForms adapter the inject idea would target a framework Marionette doesn't support — the gap had to close before the inject conversation could move to implementation.

This phase ships only the WinForms adapter; the inject tool remains a separate (and not-yet-started) workstream.

## Slices

### 15.A — Spike (load-bearing claims)

`.phase15/SpikeWinForms/` — single-project net10.0-windows WinForms harness verifying three claims:

| Claim | Method | Result |
|---|---|---|
| **C1: UI-thread marshalling** | `Control.BeginInvoke` wrapped in `TaskCompletionSource<T>` | bg-thread=5, action-thread=2, func returns `"Spike A/2"`, `InvokeRequired` from bg = true. **PASS.** |
| **C2: Form.DrawToBitmap → PNG** | `Bitmap.Save(stream, ImageFormat.Png)` | Form: 4198 B (420×220), Button: 655 B (120×40), valid PNG magic. **PASS.** |
| **C3: Win32InputInjector reuse** | Phase-14 SendInput against focused control | Space → Click counter 0→1, "hello" typed verbatim, mouse-move + click counter 1→2. **PASS.** |

Full results: [.phase15/spike-a-findings.md](.phase15/spike-a-findings.md).

### 15.B — Adapter implementation

[`src/Marionette.NET.Adapter.WinForms/`](src/Marionette.NET.Adapter.WinForms/) — six source files, all internal except the public adapter and bootstrap:

| File | Role | Lines |
|---|---|---|
| `Marionette.NET.Adapter.WinForms.csproj` | net10.0-windows, UseWindowsForms=true, ProjectReference Marionette.NET.Runtime | 30 |
| `WinFormsUiAutomationAdapter.cs` | `IUiAutomationAdapter` impl: dispatch / screenshot / resolve / simulate_input / raise_event / multi-window | 314 |
| `MarionetteWinForms.cs` | `AttachTo(Form, roots, args)` bootstrap analogous to `MarionetteWpf.AttachTo` | 270 |
| `Internal/ControlTreeFinder.cs` | Walks Application.OpenForms + Form.Controls + ToolStrip candidates | 211 |
| `Internal/WinFormsInputSimulator.cs` | simulate_input dispatch using `Win32InputInjector` (no per-framework input synthesis needed) | 207 |
| `Internal/WinFormsEventRaiser.cs` | raise_event via reflection on `On<EventName>` + Component.Events EventHandlerList fallback | 187 |
| `Internal/OpenFormsHook.cs` | Multi-window tracker reconciles Application.OpenForms on Application.Idle | 109 |

Total: ~1330 lines incl. extensive XML docs. Compares to ~2000 lines for the WinUI adapter — significantly leaner because:

- No XAML metadata story (WinForms has no x:Name vs Name distinction; Control.Name is canonical).
- No DispatcherQueue / RunAsync ceremony (BeginInvoke is direct).
- `simulate_input` reuses Phase-14 `Win32InputInjector` verbatim — no per-kind framework dispatch (no RoutedEventArgs ctors, no PresentationSource lookups).

### 15.C — Sample showcase

[`samples/Sample.WinForms.OrderTracker/`](samples/Sample.WinForms.OrderTracker/) — order-management LOB-style app with ListView + status panel + add/promote/cancel/clear actions. Manifest:

- 1 `[McpRoot]` (`OrderViewModel`, INPC)
- 5 `[McpCallable]`: `AddOrder`, `PromoteOrder`, `CancelOrder`, `ClearCompleted`, `SetFilter`
- 4 `[McpObservable]` (all `Watchable=true`): `TotalOrders`, `NewOrders`, `TotalRevenue`, `StatusFilter`
- 1 `[McpEvent]`: `OrderShipped` (typed `OrderShippedEventArgs`)

Three modes (mirrors every other sample): plain GUI, `--mcp` (GUI + MCP), `--mcp --headless` (stdio only).

### 15.D — Solution + build matrix

Adapter + sample registered in [Marionette.NET.sln](Marionette.NET.sln) under the `src` and `samples` solution folders.

**Verification matrix — all green:**

| Check | Result |
|---|---|
| Solution Debug build (22 projects) | **0 warnings, 0 errors** in 29.7s |
| OrderTracker Release stripped (`EnableMcpAutomation=false`) | **0 warnings, 0 errors** |
| Source-gen tests | **49/49 PASS** |
| Testing toolkit tests | **12/12 PASS** |
| Integration tests | **7/7 PASS + 3 skipped** (GUI tests gated on `MARIONETTE_GUI_TESTS=1`) |
| IL strip probe (8 needles incl. new `Adapter.WinForms`) | **0/8 hits** on stripped OrderTracker.dll |
| Stdio handshake (initialize + tools/list + invoke + read_observable) | **4/4 frames PASS** |

**Stripping verification:** the IL probe was extended with a new `Adapter.WinForms` needle alongside the existing `Adapter.Wpf` / `.Avalonia` / `.WinUI` / `.Maui` ones. Stripped Release build of `Sample.WinForms.OrderTracker.dll` contains zero references to any Marionette adapter or runtime symbol — Phase-0 stripping promise holds across all five frameworks.

## Design decisions worth carrying forward

### 1. `MarionetteWinForms.AttachTo` requires a `Form` with `Handle` already created

WPF's `Application.OnStartup` runs before any window has a handle, but the Dispatcher exists from app startup so `Dispatcher.InvokeAsync` is always safe. WinForms has no app-wide Dispatcher analogue — `Control.BeginInvoke` requires a control whose handle exists. The adapter takes a `Form` argument and validates `IsHandleCreated`. The bootstrap pattern is: call from the main form's `Shown` handler, not `Load`.

This is documented in the bootstrap XML docs and surfaced via `InvalidOperationException` when violated.

### 2. ToolStrip / MenuStrip items aren't returnable from the visual finder

`ToolStripItem` is NOT a `Control` (it's a `Component`). The walker now records ToolStrip item names as candidates for diagnostic purposes (so a failed lookup hints "there's a menu item with that name") but doesn't return them. Adopters who want LLM control over menu actions expose them as `[McpCallable]` methods on the ViewModel — this is the documented pattern and the only AOT-clean path anyway.

### 3. `Application.OpenForms` snapshot before iteration

Modifying the FormCollection during iteration corrupts the enumeration. Both `ControlTreeFinder.EnumerateOpenForms` and `OpenFormsHook.Reconcile` snapshot to a typed array first.

### 4. SetForegroundWindow best-effort, not guaranteed

`Win32InputInjector` delivers input to the focused window. The adapter's `WinFormsInputSimulator` calls `Form.Activate()` + `SetForegroundWindow(Handle)` before each `simulate_input` to maximise the chance of correct delivery — but Windows enforces "you can only steal focus if you're already foreground", so this can fail silently. This matches the WinUI adapter's same trade-off and is documented in the input simulator XML.

### 5. raise_event on CLR events uses the `On<EventName>` virtual

WPF / Avalonia adapters use `RoutedEvent` static fields. WinForms uses CLR events backed by `Component.Events` (an `EventHandlerList` keyed by `EventXxx` static keys). The adapter prefers calling the protected virtual `On<EventName>(EventArgs)` — that's the framework-internal path the real input pipeline uses, so observable behaviour matches a user-driven event exactly. EventHandlerList lookup is the fallback when `On<X>` doesn't exist (rare, mostly for custom events).

Same `[RequiresUnreferencedCode]` trim contract as the other adapters; same Phase-12 escape hatch via `[McpRaisable]` source-gen catalog.

## Known limitations

| Limitation | Impact | Mitigation |
|---|---|---|
| `DrawToBitmap` does not capture overlay popups (tooltips, drop-down menus, modal dialogs that are top-level windows) | Screenshots miss transient overlays | Adopters who need this can fall back to `Graphics.CopyFromScreen` of the form's screen rect; intentionally not in v1 to keep the screenshot path simple |
| AOT publish smoke not run in this phase | Not a regression — same trim contract as other adapters | Add `Sample.WinForms.OrderTracker` to the AOT-publish-smoke CI job in a follow-up |
| EC-11/12 GUI integration tests not added | Test parity with WPF EC-8/9/10 deferred | Same `MARIONETTE_GUI_TESTS=1` gate pattern; mechanical follow-up |

## Phase 15 follow-ups (non-blocking)

1. **AOT-publish smoke**: extend `.github/workflows/ci.yml` `aot-publish-smoke` job to publish `Sample.WinForms.OrderTracker` stripped + full and run an AOT-runtime stdio handshake. WinForms has no special AOT story (vs WPF's `_SuppressWpfTrimError` need) so this should "just work" or surface a small fix.
2. **EC-11 + EC-12 integration tests**: simulate_input click on `AddOrderButton`, raise_event Click on the button. Gate on `MARIONETTE_GUI_TESTS=1` like EC-8/EC-9.
3. **`Marionette.NET` meta-package**: when the meta-package is next refreshed, add `Marionette.NET.Adapter.WinForms` as a sibling of the existing four adapter packages.
4. **Skill-pack updates**: the `marionette-decorate` SKILL.md mentions WPF / Avalonia / WinUI / MAUI; bump the prose to mention WinForms as a first-class option. `attributes-reference.md` simulate_input table should gain a fifth column.
5. **`Marionette.NET.Inject` workstream** (separate phase): with WinForms now a supported target, the inject tool's biggest market segment has a landing pad. Spike-A pattern would be: take an unmodified WinForms .exe, inject `[McpCallable]` attributes via dnlib, drop in the runtime DLLs, verify stdio handshake works.

## What this enables

WinForms adopters can now:

- Decorate ViewModels with `[McpRoot]` / `[McpCallable]` / `[McpObservable]` / `[McpEvent]`.
- Wire MCP into their app with one `MarionetteWinForms.AttachTo(this, GeneratedManifest.Roots, args)` call from `Form.Shown`.
- Drive their app from Claude Desktop via stdio (`--mcp --headless`) or run GUI + MCP simultaneously (`--mcp`).
- Strip every byte of Marionette IL from production builds via `EnableMcpAutomation=false`, IL-verified.
- Use the same `Marionette.NET.Testing` in-process harness the other framework adopters use.

The five-framework adapter roster is now: WPF, Avalonia 12.x, WinUI 3 (Windows App SDK 2.x), MAUI 10.x (Windows head), **WinForms**. Uno was investigated in the Phase 16 spike (.phase16/spike-a-findings.md) and dropped from the roadmap on 2026-05-08 — modern Uno's Skia-renderer default + NuGet version conflicts made a clean adapter a multi-week clone with uncertain payoff. Adapter roster is final at five.
