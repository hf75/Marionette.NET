# Spike A — Phase 16 Uno Adapter Foundation

**Status:** Planned
**Date:** 2026-05-08
**Goal:** Decide the Uno adapter's architectural shape by answering a single question: **does the existing `Marionette.NET.Adapter.WinUI` work for a Uno.WinUI Windows-head app, or do we need a separate `Marionette.NET.Adapter.Uno` project?**

## Why Phase 16 (and why a different shape than Phase 15)

Uno is the only adapter still on the masterplan roadmap. Unlike WinForms (which is a clean separate framework needing its own dispatcher / screenshot / event story), **Uno on Windows is built ON TOP of WinUI 3** — same `Microsoft.UI.Xaml.*` types, same `DispatcherQueue`, same `AutomationPeer`. So the natural first question is whether Marionette's WinUI adapter just works.

If yes: Phase 16 ships a thin wrapper package (or just docs telling Uno adopters to add the WinUI adapter), plus a Uno sample. Small phase.

If no (because Uno re-implements key types in `Uno.UI` rather than using Microsoft's): Phase 16 is a real adapter clone with its own LOC and test surface. Bigger phase.

## Scope cut (locked)

Phase 16 covers **Uno desktop heads only**:
- Uno.WinUI on Windows (uses Microsoft Windows App SDK)
- Uno.Skia on Windows / Mac / Linux desktop (Uno's own renderer)

**Out of scope, deferred:**
- Uno.WASM — browser, no stdio. Marionette's transport is stdio. WASM would need an MCP-over-WebSocket transport, which is a separate phase.
- Uno.Android / Uno.iOS / Uno.Mac Catalyst — mobile. Stdio works in dev only. Out of scope for v1.

## The single load-bearing claim to verify

**C1 — Marionette.NET.Adapter.WinUI works against a Uno.WinUI Windows-head app.**

Method:
1. Scaffold a minimal Uno.WinUI app via `dotnet new unoapp -presets blank -platforms windows`.
2. Strip every Uno head except Windows from the multi-target list (keep the build cycle small for the spike).
3. Add `Marionette.NET.Abstractions` + `Marionette.NET.SourceGenerator` + `Marionette.NET.Adapter.WinUI` ProjectReferences using the same csproj wiring as `Sample.WinUI.FormLab`.
4. Decorate one ViewModel-shaped class with `[McpRoot]` + 1 `[McpCallable]` + 1 `[McpObservable]`.
5. From the Uno App's `OnLaunched`, call `MarionetteWinUI.AttachTo(...)` exactly like `Sample.WinUI.FormLab` does.
6. Build + run with `--mcp --headless`. Verify stdio handshake (initialize + tools/list + read_observable) returns valid responses.

**Pass:** all three frames return valid responses, no compile errors, no runtime exceptions related to type mismatches between `Microsoft.UI.Xaml.*` and `Uno.UI`. Uno.WinUI Windows head IS a Marionette adopter using the existing WinUI adapter — Phase 16 becomes a thin packaging/docs phase.

**Fail:** compile errors due to type-graph divergence, or runtime exceptions when the adapter tries to walk the visual tree / capture screenshot / dispatch input. We then need to clone the WinUI adapter into a Uno-specific variant, fix the diverging types, and proceed with a full Phase-15-style implementation.

## Path B (if Path A fails or needs adjustment): Uno.Skia desktop

If the Uno.WinUI Windows-head reuse works, we still need to verify the Uno.Skia desktop heads. Skia uses Uno's own renderer — `Uno.UI` reimplementations of Microsoft.UI.Xaml types. Different visual tree internals, different input plumbing.

For Skia we'd need either:
- A separate adapter targeting Uno.UI types, OR
- A documented "use AutomationPeer.Invoke for callable triggering, no screenshot" reduced surface.

This spike defers Skia head verification — we'll address it in spike-b if and when the Windows-head story is settled.

## What gets shipped in the full Phase 16 (depending on spike outcome)

**Outcome A: WinUI adapter just works**
- New thin package `Marionette.NET.Adapter.Uno` that ProjectReferences `Marionette.NET.Adapter.WinUI` and re-exports `MarionetteWinUI.AttachTo` under `MarionetteUno.AttachTo` for naming consistency. Optional: maybe just docs.
- New sample `Sample.Uno.WeatherStation` (Uno.WinUI Windows head only for v1).
- Findings doc + README bump.
- Skia + WASM + mobile heads documented as deferred.

**Outcome B: WinUI adapter needs Uno-specific surgery**
- Phase 15-style implementation: separate `WinUiAutomationAdapter` clone with Uno-specific type references, `MarionetteUno.AttachTo` proper bootstrap, sample, full build matrix verification.

## Non-goals for the spike

- AOT publish (Uno AOT is its own thicket; Phase-15-style follow-up).
- Multi-window routing on Uno (the WinUI adapter's tracker is in Runtime; should "just work" if Path A holds).
- Uno-specific input simulation (`simulate_input` should reuse Win32InputInjector via the WinUI adapter exactly as it does now).

## Pass criteria for the spike

`spike-a-findings.md` documents:
- Whether the Uno project scaffolded + built successfully
- Whether `MarionetteWinUI.AttachTo` compiled against Uno.WinUI types
- Whether the stdio handshake returned valid responses
- A clear architectural decision: **A (thin/no wrapper)** or **B (full clone)**

After the spike I either ship the thin wrapper + sample (small phase), or escalate scope and write a full PHASE16_BRIEF clone of Phase 15.
