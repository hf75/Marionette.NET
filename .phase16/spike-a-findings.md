# Spike A — Phase 16 Uno Adapter Foundation — Findings

**Status:** Partial — investigation surfaced architectural complexity that warrants a scoping discussion before the full Phase 16 implementation.
**Date:** 2026-05-08
**SDK:** .NET 10, Uno.Templates 6.4.42 (latest stable: 6.5.33).

## What I built

`.phase16/SpikeUno/` — Uno project scaffolded via:

```
dotnet new unoapp -n SpikeUno -preset blank -tfm net10.0 -platforms windows -presentation none -markup xaml
```

Produces a single-project Uno solution targeting `net10.0-windows10.0.26100`, `<UnoSingleProject>true</UnoSingleProject>`, with `<UnoFeatures>SkiaRenderer;</UnoFeatures>` in the default scaffold.

Step 1 (build the bare scaffold): **PASS** — 1 unrelated warning (publish profile not found), 0 errors, ~14s.

Step 2 (add Marionette references — the actual reuse claim): **FAIL.**

## What I learned (and why this matters)

### Finding 1 — Modern Uno default is Skia renderer, not WinAppSDK / WinUI 3

The bare `unoapp -platforms windows` scaffold ships with `<UnoFeatures>SkiaRenderer;</UnoFeatures>`. That means even the Windows head uses Uno's own Skia-based renderer — `Microsoft.UI.Xaml.*` namespaces are populated by `Uno.UI` reimplementations, NOT by Microsoft's Windows App SDK / WinUI 3 native types.

This invalidates the spike's foundational assumption ("Uno.WinUI on Windows ≈ WinUI 3 with extra glue → Marionette WinUI adapter just works"). The MODERN Uno on Windows is closer to "a cross-platform XAML implementation that happens to render on Windows" than to "WinUI 3 with glue."

There IS a WinAppSDK-backed Uno head (`unoapp` with `-features WinAppSdk` or older default), but it's no longer the recommended path. Uno's own docs steer adopters toward Skia for Desktop targets.

### Finding 2 — NuGet version graph has hard conflicts

Adding `ProjectReference` to `Marionette.NET.Adapter.WinUI` against the bare Uno project produces NU1605 downgrade errors:

| Package | Marionette pins | Uno 6.4.42 scaffold pins |
|---|---|---|
| `Microsoft.WindowsAppSDK` | 2.0.1 | 1.7.250909003 |
| `Microsoft.Extensions.Logging.Console` | 10.0.7 | 10.0.0 |

Both treat the downgrade as an error (NuGet `WarningsAsErrors`), which is correct project hygiene. To unblock the spike we'd need to either pin matching versions in the Uno project or relax the Marionette version pins — neither is a one-line config change, and neither has been verified to produce a *runtime-compatible* combination (WinAppSDK 2.0.1 vs Uno's tested-against version may produce silent ABI mismatches).

### Finding 3 — Uno project shape is fundamentally different from a normal WPF/Avalonia/WinUI sample

Uno uses `<Project Sdk="Uno.Sdk">` not `Microsoft.NET.Sdk`. It has `<UnoSingleProject>true</UnoSingleProject>` which is a multi-target abstraction layer that conditionally pulls different head packages per TFM. The build output goes through Uno's pipeline rather than the standard SDK pipeline. None of the existing Marionette `Sample.*` csproj patterns transfer directly.

### Finding 4 — The "WinUI adapter just works" claim cannot be verified without resolving the above

Without a buildable spike, we can't observe whether the WinUI adapter's visual-tree walker actually finds elements in Uno's Skia-rendered tree, whether `RenderTargetBitmap` works against Uno's renderer, whether `DispatcherQueue.TryEnqueue` lands on the right thread, whether `AutomationPeer.Invoke` resolves to anything actionable. Each of these is a load-bearing claim for the adapter's correctness; each needs its own spike claim.

## Architectural decision pending — three options

### Option 1 — Ship a Uno-specific adapter (full Phase-15-style implementation)

`Marionette.NET.Adapter.Uno` as a separate project that targets `Uno.Sdk` and pins Uno-compatible versions of WinAppSDK (or none, depending on Skia vs WinAppSDK head choice). Implements `IUiAutomationAdapter` against Uno-specific dispatch / screenshot / tree-walk APIs. Full set of internal helpers (UnoControlTreeFinder, UnoInputSimulator, UnoEventRaiser, UnoFormsTracker analogue).

Pros: clean separation; can pin Uno-tested versions; behaviour is predictable. Mirrors the discipline of the other four adapters.

Cons: significant LOC (~1500+ estimated); needs Uno-specific knowledge for each Uno head we want to support; multi-platform target (Skia Windows / Skia Mac / Skia Linux) is a multi-week effort, not a multi-day phase.

### Option 2 — Document "Uno is not yet supported" and ship a roadmap entry

Be honest with adopters: Uno on modern setups doesn't work with the WinUI adapter, and a proper Uno adapter is a multi-week project we haven't budgeted. Phase 16 ships only documentation:

- `docs/uno-status.md` capturing why Uno is harder than the other four frameworks
- README roadmap entry "Uno adapter — under investigation, no timeline"
- Issue template / discussion seed for adopters who want to drive this

Pros: honest, low effort, no half-baked code.

Cons: no concrete progress on the roadmap item.

### Option 3 — Defer Phase 16 entirely, surface as user decision

Don't ship anything Uno-related in this session. Mark Phase 16 as "investigated, requires bigger commitment than current scope allows." The spike findings + brief stand as the input for a future scoping discussion.

Pros: avoids overcommitting; respects the discovered complexity.

Cons: Uno remains the only roadmap item without a concrete path forward.

## Pragmatic stuff worth recording

- `.phase16/SpikeUno/` is left in place as the empirical trace of this spike. Its csproj has Marionette references that don't currently compile (NuGet conflicts above). Future Uno-adapter work can either fix the version pins or scrap and re-scaffold.
- Uno templates listed: `unoapp`, `unolib`, `unomauilib`, `unoapp-uitest`. The flag for Skia-vs-WinAppSDK head selection is via `<UnoFeatures>` element in csproj, not a CLI parameter on the template.
- `dotnet build` of the bare scaffold succeeded in 14s — restore is fast on a warm cache, the slow part will be running through Uno's per-platform conditional restore on first run after a flag flip.
- Modern Uno (6.x) ships its own MCP integration story is NOT a thing as of the SDK version probed; we're not in a competitive position with an existing Uno-MCP package.

## My recommendation

**Option 3 — defer Phase 16, surface to the user.** Reasoning:

1. The "tollkühn idea" workstream that motivated the WinForms phase doesn't have an obvious Uno overlap — Uno apps are typically greenfield (where adopters can decorate from day one), not legacy LOB apps where a source-less inject path makes sense.
2. Five adapters (WPF / Avalonia / WinUI / MAUI / WinForms) cover the dominant desktop framework market today. Uno's adopter base is smaller and tends to be cross-platform-mobile-focused — where Marionette's stdio transport doesn't apply anyway.
3. A half-baked "thin Uno wrapper" that papers over the version conflicts without verifying runtime behaviour would be a Marionette-discipline regression. The other four adapters ship with verified spike claims; Uno deserves the same.
4. The user's energy might be better spent on the Inject workstream (genuine new strategic value) than on yet another adapter clone.

If the user disagrees and wants Phase 16 to ship: my recommendation becomes **Option 1**, multi-week effort, with the first sub-phase being a real spike that pins workable versions, scaffolds an installable Uno project, and proves at least one of the IUiAutomationAdapter methods (DispatchAsync via Uno's DispatcherQueue) actually works at runtime against the Skia renderer. Only after that minimum verification does it make sense to write the full adapter.

## Concrete next-step proposals (for the user to choose between)

1. **Stop here.** Mark Phase 16 as deferred, leave the spike findings + brief in `.phase16/`, update the memory accordingly, propose to revisit the topic when an adopter actually requests Uno support (or when the inject conversation matures into something Uno-relevant).

2. **Pivot to the Inject spike.** That's the strategically novel workstream — the Uno gap is mostly a roadmap-completion concern, while the inject workstream is genuinely new value. A 2-day Inject Spike would prove the dnlib + auto-attach pattern against a known-working WinForms app (the just-shipped OrderTracker provides a perfect target).

3. **Commit to multi-week Phase 16 — Option 1.** I'd want a clear go-ahead and a checkpointed plan: pin Uno-compatible package versions, build a real Uno project that compiles with Marionette, then write the adapter. Realistically 2-3 sessions of work.
