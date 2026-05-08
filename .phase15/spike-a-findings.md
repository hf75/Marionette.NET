# Spike A — Phase 15 WinForms Adapter Foundation — Findings

**Status:** Pass
**Date:** 2026-05-08
**SDK:** .NET 10
**Verdict:** All three load-bearing claims pass. Full Phase 15 adapter work is unblocked.

## What I built

`.phase15/SpikeWinForms/` — single-project WinForms harness, `net10.0-windows`, `WinExe`, `UseWindowsForms=true`. Outside the solution. References `Marionette.NET.Runtime` to consume `Win32InputInjector` directly.

The harness opens a small TopMost form with one Button (`TestButton`), one TextBox (`TestText`), and a status label, then on `Form.Shown` runs three verification routines on a background `Task.Run` and writes per-claim verdicts to `spike-a-result.txt` next to the .exe. Process exit code: 0 = all three pass, 1 = at least one failed.

## Build

```
dotnet build .phase15/SpikeWinForms/SpikeWinForms.csproj -c Release
```

Output: 0 warnings, 0 errors. Build time 5.56s incl. Runtime/Abstractions transitive restore.

## Run

```
.phase15/SpikeWinForms/bin/Release/net10.0-windows/SpikeWinForms.exe
```

Stderr verdict line:

```
[spike-a] VERDICT: PASS (3/3 claims)
```

Exit code: 0.

## Per-claim findings

### Claim 1 — `Control.BeginInvoke` marshalling — **PASS**

```
bg-thread=5, action-thread=2, func-result=Spike A/2, invokeRequired-from-bg=True
```

- The background-thread caller has managed thread ID 5; the action ran on thread 2 (the UI thread that owns the Form's window handle).
- `Func<T>` variant returned `"Spike A/2"` — `form.Text` value plus the executing thread ID — confirming the function ran on the UI thread.
- `form.InvokeRequired` correctly returned `true` when checked from the background thread.

**Implication for the production adapter:** the `Task<T>`-wrapper-around-`BeginInvoke` pattern shown in the spike maps cleanly onto the WPF adapter's `Dispatcher.InvokeAsync` shape. Cancellation-token handling and the inline-on-UI-thread shortcut are mechanical to port.

### Claim 2 — `Form.DrawToBitmap` → PNG — **PASS**

```
form-png=4198B (420x220), button-png=655B (120x40), magic-form=True, magic-button=True
```

- Form-level capture: 4198-byte PNG, dimensions 420x220 matching `ClientSize`, valid PNG magic bytes (`89 50 4E 47`).
- Button-level capture (named child resolved via `Form.Controls.Find("TestButton", recursive: true)`): 655-byte PNG, 120x40 matching the button's size, valid magic.
- Both captures executed on the UI thread via `form.Invoke`.

**Implication for the production adapter:** WinForms screenshot story is essentially `Bitmap` + `Control.DrawToBitmap` + `Bitmap.Save(ms, ImageFormat.Png)`. The DPI dance WPF needs (`VisualTreeHelper.GetDpi` + manual pixel-size computation) does NOT apply to GDI bitmaps — DrawToBitmap captures whatever the control's logical size is. We document this difference in the adapter's XML doc.

**Known limitation already flagged in the brief:** `DrawToBitmap` does not capture top-level overlay popups (tooltips, dropdown menus, modal dialogs that are separate windows). For those, an adapter-level `Graphics.CopyFromScreen` of the form's screen rectangle would work but introduces a "screen scrape" dependency we don't need for v1.

### Claim 3 — `Win32InputInjector` reuse — **PASS**

```
space-click=0→1 (OK), text="hello" (OK), mouse-click=1→2 @(830,457) (OK)
```

- **Space → focused button Click**: focused TestButton, sent `VirtualKey.Space` via `Win32InputInjector.SendKeyPress`. WinForms convention activates the focused button on Space; the click counter incremented from 0 to 1.
- **Unicode text**: focused TestText, sent `"hello"` via `Win32InputInjector.SendUnicodeText`. The TextBox.Text post-condition is exactly `"hello"` — every character delivered through the OS input pipeline reached the WinForms text edit control.
- **Mouse-move + click**: computed the button's center in screen coords via `Control.PointToScreen`, called `SendMouseMoveAbsolute(830, 457)` then `SendMouseClick(MouseButton.Left)`. Click counter incremented again from 1 to 2.

**Implication for the production adapter:** the entire Phase-14 input layer ports as-is. The WinForms adapter's `SimulateInputAsync` will pre-resolve the target Control via `Form.Controls.Find`, call `Control.Focus()` semantically, then dispatch identical `Win32InputInjector` calls. No new P/Invoke surface is required.

## Gotchas captured for the production adapter

1. **TopMost matters during synthetic input.** The spike sets `Form.TopMost = true` so it holds focus during the input injection. A production adapter does NOT control the adopter's window topmost flag — the adopter's app might lose focus mid-`simulate_input` to another window. Mitigation: the adapter should call `form.Activate()` (and possibly `SetForegroundWindow` via P/Invoke if `Activate()` isn't enough — Win32 has a notorious "you can only steal focus if you're already the foreground window" rule). Investigate during full Phase 15.

2. **`InvokeRequired`-but-no-handle race.** If `BeginInvoke` is called before the form has materialised its window handle, it throws `InvalidOperationException("Invoke or BeginInvoke cannot be called on a control until the window handle has been created")`. The adapter must either (a) wait for `HandleCreated` / `Form.Shown` before allowing dispatches, or (b) defer dispatches into a queue that flushes after handle creation. The simpler path: the bootstrap `MarionetteWinForms.AttachTo` must be called from `Form.Shown` (or after `Application.OpenForms.Count > 0`), not from `Form.Load` of a not-yet-shown form.

3. **`Application.Current`-equivalent doesn't exist.** WPF has `Application.Current` as a global singleton. WinForms has `Application.OpenForms`, which is a *list* — adapter code that needs "the main form" must pick one (typically `Application.OpenForms[0]` or the first `Form` whose owner is null).

4. **Thread-ID identifier in spike output is repeatable but environment-specific.** `bg-thread=5, action-thread=2` reflects the spike harness's specific thread pool state at run time. The PASS verdict checks only that the two IDs differ AND that the action ran on a non-bg thread — not on absolute IDs.

5. **`spike-a-result.txt` is UTF-8 with BOM** (the leading `﻿` visible in the readback). Inconsequential for the spike, but worth noting if any downstream tool consumes the file: use `new StreamWriter(path, append: false, new UTF8Encoding(false))` to strip the BOM.

## What remains (full Phase 15)

Going into the full adapter implementation with these confirmed mechanics:

- **`Marionette.NET.Adapter.WinForms` project** — `net10.0-windows`, `UseWindowsForms=true`, references Runtime.
- **`WinFormsUiAutomationAdapter`** implementing all `IUiAutomationAdapter` methods using the spike's verified primitives.
- **`MarionetteWinForms.AttachTo(applicationContext, roots, args)`** bootstrap analogous to `MarionetteWpf.AttachTo`. Picks the bootstrap form from `Application.OpenForms[0]` or accepts an explicit `Form` argument.
- **Multi-window tracking** via subscription to `Application.OpenForms` changes. WinForms exposes this via `Form.HandleCreated` / `Form.HandleDestroyed` events on individual forms; the adapter walks `OpenForms` periodically and on those events.
- **`raise_event`** via reflection on the control's CLR events (no RoutedEvent abstraction in WinForms, just plain CLR events backed by `EventHandler`-shaped delegates). Same `[RequiresUnreferencedCode]` annotation as WPF.
- **Sample.WinForms.OrderTracker** showcase app exercising the full surface.
- **Phase-15 integration tests** — extend the existing test list to include EC-11 (WinForms invoke/observe) and EC-12 (WinForms simulate_input), gated identically to EC-8/9/10 on `MARIONETTE_GUI_TESTS=1`.

No redesigns required. The spike pattern is structurally identical to the WPF adapter's MO; the production code will be similar in size and shape.
