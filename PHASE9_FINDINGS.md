# Phase 9 Findings — cross-adapter polish

Date: 2026-05-04 (retrospective doc written 2026-05-05)

## Status

**GREEN.** Three small but adopter-visible polish slices across the three
non-WPF adapters. Each slice is a single-commit landing; this document
consolidates them after the fact for parity with the rest of the phase
findings. Phase 10 (AIFunction) and Phase 11 (interface fallback +
RunAsyncSourceGenSafe) build on top of this baseline.

| Slice | Adapter | Commit | What |
|---|---|---|---|
| 9.1 | Avalonia | [`cc4ec64`](../../commit/cc4ec64) | `simulate_input(type_text)` via direct `Text`-property setter |
| 9.2 | MAUI | [`c9bce30`](../../commit/c9bce30) | Multi-window tracking via `Application.Windows` lifecycle hooks |
| 9.3 | WinUI | [`91e0cca`](../../commit/91e0cca) | Refined Win11 input-injection reality + adopter docs |

## What landed

### 9.1 — Avalonia `simulate_input(type_text)` via direct property setter

`Sample.Avalonia.Dashboard`'s text inputs were not reachable via
`simulate_input(kind="type_text")` because Avalonia 11.x's keyboard /
text-input event-args types (`KeyEventArgs`, `PointerEventArgs`,
`TextInputEventArgs`) all have `internal` constructors. Phase 3.1 had
left this as `return false` with a documented breadcrumb pointing
adopters at `[McpCallable]` or `raise_event`.

Phase 9.1 added a pragmatic `type_text` path that mirrors the MAUI
adapter's pattern: when the target is a `TextBox`, `MaskedTextBox`,
`AutoCompleteBox`, or `TextBlock`, the simulator sets the `Text`
property directly. The setter goes through Avalonia's `StyledProperty`
system so any `PropertyChanged` binding fires normally; data-bound
view-models see the change.

Order of pattern matching matters — `MaskedTextBox : TextBox` so the
derived type must come first or the base would shadow it (the C#
compiler also flags this as CS8120 if the order is wrong).

`key_press` / `key_down` / `key_up` / `mouse_move` remain `return false`
with the architectural-limitation log message — the raw-input pipeline
needs `IInputRoot` plumbing the adapter does not have, and the typed
event-args ctors are not adopter-constructible.

[`AvaloniaInputSimulator.cs`](src/Marionette.NET.Adapter.Avalonia/Internal/AvaloniaInputSimulator.cs)
+79 / -28 lines.

### 9.2 — MAUI multi-window tracking

WPF / Avalonia / WinUI have shipped per-window dynamic-tool variants
since Phase 3.3 (`<Root>.<Method>:<windowId>` for any root with 2+ live
windows). MAUI was the holdout — `MarionetteMaui.AttachTo` registered
the `[McpRoot]` view-models but did not subscribe to MAUI's window
lifecycle, so the adapter's `RootInstanceTracker` could not advertise
multiple windows.

Phase 9.2 hooks `Application.Windows`:

- **Initial reconcile at attach time**: every already-open
  `Microsoft.Maui.Controls.Window` is walked once. Both the page and
  its `BindingContext` are tracked (covers the typical MVVM root
  position).
- **Per-window `Activated` and `Destroying` handlers**: keep the
  tracker's known set in sync with live windows.
- **Type filtering**: only types that match a registered `[McpRoot]`
  manifest name are tracked — adopters' arbitrary windows do not
  pollute the tracker.

`Sample.Maui.PocketPlanner` is single-window so this change is a no-op
for it. Multi-window MAUI adopters now get the same dynamic per-window
tool-routing behaviour as the other three adapters.

[`MarionetteMaui.cs`](src/Marionette.NET.Adapter.Maui/MarionetteMaui.cs)
+161 lines.

### 9.3 — WinUI `InputInjector` Win11 reality + adopter docs

The original Phase-3.2 framing — *"`InputInjector` requires elevation OR
manifest-declared `inputInjectionBrokered` capability"* — described
Windows 10 behaviour. Verified May 2026 on a stock Windows 11 build
26200 with .NET 10.0.6: `InputInjector.TryCreate()` returns a working
handle in both an unpackaged console app and an unpackaged WinUI 3
process with no manifest declaration and no elevation.

The strict gate still applies to older Windows builds, locked-down
SKUs, and Windows Sandbox / Hyper-V VM scenarios.

What landed:

- **`WinUiInputSimulator.ProbeInjectorAvailability(ILogger)`** — eagerly
  probes the injector at adapter construction and writes one info line
  so adopters see availability up front instead of finding out at the
  first failed `simulate_input` call:

  ```
  [Information] WinUI input-injection probe: InputInjector available —
  full simulate_input matrix (click / key_* / type_text / mouse_move)
  operational.
  ```

  Or on constrained systems:

  ```
  [Information] WinUI input-injection probe: InputInjector unavailable.
  simulate_input falls back to AutomationPeer for clicks and TextBox.Text
  for type_text. Other kinds (key_press, mouse_move) will return success=false.
  See docs/winui-input-injection.md.
  ```

- **`WinUiAutomationAdapter` ctor** calls the probe so the diagnostic
  appears as part of the adapter's normal startup banner.

- **`WinUiInputSimulator.cs` header comment** rewritten with the Win11
  reality + the historical Win10-era constraint, plus the two
  adopter fallback paths (run elevated, or MSIX-package with
  `inputInjectionBrokered` restricted capability).

- **`docs/winui-input-injection.md`** — comprehensive 161-line adopter
  guide with availability matrix, log-line interpretation, and the two
  fallback paths in detail.

`Sample.WinUI.FormLab` deliberately ships unpackaged so it can demo as a
single double-clickable EXE; on the test machine the probe confirms the
full `simulate_input` matrix works there without manifest changes.
Adopters who target older Windows builds, locked-down SKUs, or who need
the LLM to drive arbitrary-control keyboard input as a hard-deployment
guarantee follow Path A or B in their own project.

[`WinUiInputSimulator.cs`](src/Marionette.NET.Adapter.WinUI/Internal/WinUiInputSimulator.cs)
+89 / -11 lines, plus the new
[`docs/winui-input-injection.md`](docs/winui-input-injection.md) +161 lines.

## Verification (per-slice)

Each slice landed with the same gate matrix:

| Step | All three slices |
|---|---|
| Solution Debug build | 0 warnings, 0 errors |
| Source-gen tests | 28/28 PASS |
| Testing-toolkit tests | 12/12 PASS |
| Integration eval-cases | 7/7 PASS + 3 GUI-skipped |

Slice 9.3 additionally ran AOT publish on `Sample.WinUI.FormLab` to
confirm the new probe code stays AOT-clean (exit 0, 0 Marionette IL
warnings).

## Adopter takeaways

- **Avalonia adopters**: `simulate_input("type_text")` now works on the
  four common text-input control types. Other kinds still need
  `[McpCallable]` or `raise_event`.
- **MAUI adopters**: opening a second window of a `[McpRoot]`-decorated
  page now produces a per-window dynamic-tool variant
  (`<Root>.<Method>:<windowId>`) just like the other three adapters.
  Single-window adopters see no change.
- **WinUI adopters**: the adapter's startup banner now includes a
  one-line probe line indicating whether `InputInjector` is available
  on the runtime machine. Treat the probe output as the source of
  truth — `docs/winui-input-injection.md` covers the two fallback
  paths for constrained systems.

## Open follow-ups (all addressed in later phases)

- Avalonia `key_press` / `key_down` / `key_up` / `mouse_move` remain
  unsupported — architectural, no source-gen workaround. Adopters use
  `[McpCallable]` or `raise_event`.
- WinUI input-injection on locked-down systems requires elevation /
  manifest — by design, see `docs/winui-input-injection.md`.
- MAUI multi-window stress test (rapid open/close cycles) was not
  exercised by `Sample.Maui.PocketPlanner` (single-window). Phase 11's
  AOT-runtime stdio handshake tightened the runtime contract for the
  per-method tool dispatch path that multi-window relies on.
