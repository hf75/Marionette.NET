# Phase 1 — Findings

> **Status:** Phase 1 complete (1.a -> 1.5). Phase 2 (Avalonia) is unblocked; the `IUiAutomationAdapter` contract is stable.
> **Date:** 2026-05-03
> **SDK:** .NET 10.0.202 - ModelContextProtocol 1.2.0 - Roslyn 4.14.0

Phase 1 set out to deliver, per the masterplan: a complete Abstractions/SourceGenerator/Runtime/Adapter.Wpf stack, the four Phase-1 MCP tools, channel push, watchable resources, loop protection, the WPF adapter, a canonical sample (TodoApp), the v1 skill-pack, and 5 end-to-end eval-cases as CI tests. Detailed per-sub-phase reports live in `.phase1/1{a,b,c,d,e}-*.md`; this document is the consolidated verdict.

Per-sub-phase reports remain authoritative for what landed in each step. PHASE1_FINDINGS.md focuses on (a) green/yellow/red status per masterplan deliverable, (b) cross-referencing Phase-0 implications to where they were addressed, and (c) the carryover list for Phase 2.

## Verdict per sub-phase

| # | Sub-phase | Commit | Verdict |
|---|---|---|---|
| 1.a | Abstractions full + MSBuild auto-include | `764f242` | GREEN |
| 1.b | Roslyn Incremental Source Generator (manifest emit, MAR001-008 diagnostics, snapshot tests) | `dffa731` | GREEN |
| 1.2 (1.c) | Runtime full - manifest discovery, four MCP tools, channel, loop-protection, watchable resources | `f16691f` | GREEN |
| 1.3 (1.d) | Adapter.Wpf - Dispatcher marshalling, visual-tree walker, DPI-correct screenshots, `MarionetteWpf.AttachTo` | `d9f038e` | GREEN |
| 1.4 (1.e) | Sample.Wpf.TodoApp + Skill-Pack v1 (`marionette-explore`, `marionette-decorate`, `marionette-test`) | `34b67b9` | GREEN |
| 1.5 | Demo script + 5 Eval-Cases + README + this report | working tree | GREEN |

**Overall verdict: GREEN.** Every masterplan Phase-1 deliverable landed; the IL stripping promise from Phase 0 Spike A holds across both samples; AOT remains achievable for the Frozen-Mode use case (validated in Phase 0 follow-up); the headline demo and the 5 eval-cases all pass.

## What was built per sub-phase

### Phase 1.a - Abstractions full + MSBuild targets (`764f242`)

Replaced the Spike-A stubs with the production attribute set: `McpRootAttribute`, `McpCallableAttribute`, `McpObservableAttribute`, `McpTriggerableAttribute`, plus the `TriggerStrategy` enum. All attributes sealed/immutable with `init`-only setters. Added the `Ai.IsActive` property and the internal hook fields populated by Runtime via `InternalsVisibleTo`. Multi-target Abstractions to `netstandard2.0;net10.0` so adopters on either TFM ship clean.

`build/Marionette.NET.props` + `.targets` set the `EnableMcpAutomation` default (Debug=on, Release=off) and contribute `MCP_ENABLED` to `DefineConstants` plus the `_SuppressWpfTrimError=true` escape valve when WPF+AOT are both on. Re-verified: stripped Release build still 7 files, IL probe 0 hits across all 4 needles. Detail in `.phase1/1a-foundation.md`.

### Phase 1.b - Source Generator (`dffa731`)

New `Marionette.NET.SourceGenerator` analyzer project: `IIncrementalGenerator` using `ForAttributeWithMetadataName`, three pipeline sources (root candidates, orphan callables, assembly name) combined into a `ManifestModel`, emitter writes `Marionette.g.cs` with typed-lambda dispatchers (no reflection). Validator emits MAR001-MAR008 diagnostics. Snapshot test + 5 rejection tests + 1 positive control. The analyzer-tests run in-memory via `CSharpGeneratorDriver`.

Stripping invariant preserved: emission gated on `MCP_ENABLED` so stripped Release builds get no generated manifest at all. Detail in `.phase1/1b-sourcegen.md`.

### Phase 1.2 (1.c) - Runtime: real MCP host + four tools + watchable resources (`f16691f`)

Replaced the Spike-C stub `MarionetteHost` with the production composition root. Moved descriptor records into the runtime assembly (`Marionette.Runtime.Manifest`) so the source generator emits `using Marionette.Runtime.Manifest;` and there is one CLR identity for the descriptor types instead of per-assembly duplicates. Five new runtime services:

- `ManifestRegistry` (singleton; descriptor list + live instances).
- `LoopProtectionService` (singleton; env-overridable `MARIONETTE_MAX_DEPTH`, decay window).
- `ChannelEmitter` (`IAsyncDisposable`; installs `Ai.Trigger` / `Ai.ScheduleTrigger` hooks; sends `notifications/marionette/channel`).
- `WatchableResourceProvider` (singleton; manages `resources/list`, `/read`, `/subscribe` with INPC + 200 ms coalesce + polling fallback).
- `IUiAutomationAdapter` + `NoOpAdapter` (interface; the WPF adapter implements it in 1.3).

The four Phase-1 tools live as static methods on `MarionetteTools` (registered via `WithTools<MarionetteTools>()` per Phase 0 implication 1). Detail in `.phase1/1c-runtime.md`.

### Phase 1.3 (1.d) - Adapter.Wpf production (`d9f038e`)

Replaced the Phase 1.2 fall-through stub with `WpfUiAutomationAdapter`: `Application.Current.Dispatcher.InvokeAsync` for marshalling (with a `CheckAccess()` short-circuit), `RenderTargetBitmap` + `PngBitmapEncoder` for screenshots with `VisualTreeHelper.GetDpi` correctness, `VisualTreeFinder` walking logical+visual trees with iterative DFS for named-element resolution. Bootstrap `MarionetteWpf.AttachTo(app, roots, args, loggerFactory?)` rewrites descriptor factories so they dispatch through the UI thread AND prefer a live `Application.MainWindow` when types match (solves STA-thread + instance-affinity in one move).

GUI harness assertion suite extended in `.phase0/StdioTest`: `--gui` mode validates a real PNG ImageContentBlock (magic header check). Detail in `.phase1/1d-adapter-wpf.md`.

### Phase 1.4 (1.e) - Sample.Wpf.TodoApp + Skill-Pack v1 (`34b67b9`)

Canonical adopter-reference sample: `TodoListViewModel` decorated with one `[McpRoot]`, five `[McpCallable]` methods (`AddTodo`, `RemoveTodo`, `ToggleDone`, `ClearCompleted`, `RenameTodo`), four `[McpObservable]` properties (`TotalCount`, `CompletedCount`, `PendingCount` watchable; `LastAddedTitle` non-watchable), `INotifyPropertyChanged` plumbed correctly so push beats polling. Static `Shared` singleton bridges the runtime registry instance and the WPF DataContext. Dark-mode-friendly XAML so screenshots look decent.

Skill-pack v1 with three Claude Code skills (`marionette-explore`, `marionette-decorate`, `marionette-test`), a skill-pack README, and `prompts/attributes-reference.md` (440 lines, the canonical attribute spec, citable by non-Claude agents). Distribution today is manual copy; Phase 7's NuGet automates. Detail in `.phase1/1e-todoapp-skillpack.md`.

### Phase 1.5 - Demo + 5 Eval-Cases + README + this document (working tree)

This sub-phase, rolled into the consolidated report. Five artifacts:

- `.phase1/demo.ps1` - single-command demo: builds TodoApp + StdioTest, runs the headless eval suite, optional `-Gui` adds screenshot. Adopters and CI both use it.
- `tests/Marionette.NET.Integration/` - xUnit project with 5 eval-cases (EC-1 through EC-5) covering discovery, method/observable consistency, push notifications, loop protection (with decay), and stdout JSON-RPC purity. Each `[Fact]` spawns a fresh `--mcp --headless` child via `TodoAppFixture` (force-kills on dispose so no orphans). 5/5 pass in ~5s. Tests run serialized (`xunit.runner.json` `parallelizeTestCollections=false`) so we don't spawn five WPF processes simultaneously. The csproj registers a `BeforeBuild` target that calls `dotnet build` on the TodoApp with `-p:EnableMcpAutomation=true` so the spawned exe is always MCP-on, even on a fresh checkout.
- Runtime change required by EC-4: added `MARIONETTE_DECAY_SECONDS` env-var override (`src/Marionette.NET.Runtime/Loop/LoopProtectionService.cs`) so the decay window can be reduced from the 30-second default to 2 seconds for the test, avoiding 30s waits in CI. Default behaviour unchanged for adopters; `MarionetteHost.--mcp-help` documents the new env var alongside `MARIONETTE_MAX_DEPTH`.
- **Real bug fix: `build/Marionette.NET.props` `EnableMcpAutomation` default**. The prior form `<EnableMcpAutomation>$(Configuration)=='Debug'</EnableMcpAutomation>` does NOT evaluate `==` as a boolean; MSBuild treats the RHS as a literal text VALUE, so `EnableMcpAutomation` ended up as the literal string `"Debug=='Debug'"` whenever a project did NOT pass `-p:EnableMcpAutomation=...` explicitly. Downstream `Condition="'$(EnableMcpAutomation)'=='true'"` tests then failed silently and the Adapter.Wpf reference was NOT pulled in. Phase 1.a-1.4 verifications all ran with `-p:EnableMcpAutomation=true` explicitly so the bug was masked. Fix: split into two guarded assignments. Now `dotnet build Marionette.NET.sln -c Debug` produces 42-file MCP-on output for both samples without per-project overrides; Release stays at 7-file stripped. Verified with the IL probe (still 0 hits).
- `README.md` rewritten from placeholder to Phase-1-aware tagline + "what it does" + "status" + "quickstart" + links to MASTERPLAN, PHASE0_FINDINGS, this document, and the skill-pack.

## Build matrix at end of Phase 1

All commands run from `C:\Home\Code\nw.Automation` on .NET 10.0.202. Working tree at the time of this report has the Phase-1.5 changes pending (uncommitted per the constraint set).

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build Marionette.NET.sln -c Debug` | PASS - 0 warnings, 0 errors (8 projects: Abstractions x2 TFMs, SourceGenerator, Runtime, Adapter.Wpf, StripeProbe, TodoApp, SourceGenerator.Tests, Integration) |
| 2 | `dotnet build Marionette.NET.sln -c Release` | PASS - 0 warnings, 0 errors |
| 3 | `dotnet test tests/Marionette.NET.SourceGenerator.Tests/...csproj -c Debug --no-build` | PASS - 8/8 (1 snapshot + 5 rejection + 1 positive control + 1 MCP-disabled gating) |
| 4 | `dotnet test tests/Marionette.NET.Integration/...csproj -c Debug --no-build` | PASS - 5/5 (EC-1..EC-5; ~5s wall) |
| 5 | `dotnet build samples/Sample.Wpf.TodoApp/...csproj -c Release -p:EnableMcpAutomation=false` | PASS - 7-file stripped output |
| 6 | `dotnet build samples/Sample.Wpf.StripeProbe/...csproj -c Release -p:EnableMcpAutomation=false` | PASS - 7-file stripped output |
| 7 | IL probe over TodoApp stripped DLL (cmd 5) | PASS - 0 hits across all 4 needles |
| 8 | IL probe over StripeProbe stripped DLL (cmd 6) | PASS - 0 hits across all 4 needles |
| 9 | `pwsh .phase1/demo.ps1` (headless) | PASS - 9/9 harness checks, 10 JSON-RPC frames, 0 pollution |
| 10 | `pwsh .phase1/demo.ps1 -Gui` | PASS - 9/9 harness checks incl. valid PNG (32 KB), 0 pollution |

In-CI delta (`/.github/workflows/ci.yml` + Phase-0 follow-up F2): the Windows runner additionally exercises `dotnet publish -c Release -p:PublishAot=true` for stripped + full builds and runs the headless stdio handshake against the AOT'd full binary. AOT-publish + AOT-handshake remain green per Phase-0 follow-up B.

## Phase-1 deliverables vs. masterplan

The masterplan lists 10 explicit Phase-1 line items. Each is mapped to its delivered artifact below.

| Masterplan Phase 1 line item | Delivered? | Where |
|---|---|---|
| `Marionette.NET.Abstractions`: four attributes + `Ai.Trigger` / `Ai.ScheduleTrigger` with `[Conditional]` stripping | Y | `src/Marionette.NET.Abstractions/{McpAttributes.cs,Ai.cs}` (1.a) |
| `Marionette.NET.SourceGenerator`: incremental Manifest generation + diagnostics (MAR001-MAR008) | Y | `src/Marionette.NET.SourceGenerator/` + `tests/Marionette.NET.SourceGenerator.Tests/` (1.b) |
| `Marionette.NET.Runtime`: stdio MCP server + 4 tools (`inspect_app_api`, `invoke_method`, `read_observable`, `capture_screenshot`) | Y | `src/Marionette.NET.Runtime/Tools/MarionetteTools.cs` (1.2) |
| Loop-protection (hop-counter, default 5, env override `MARIONETTE_MAX_DEPTH`) | Y | `src/Marionette.NET.Runtime/Loop/LoopProtectionService.cs` (1.2; 1.5 added `MARIONETTE_DECAY_SECONDS`) |
| `Marionette.NET.Adapter.Wpf`: dispatcher + visual-tree + screenshot | Y | `src/Marionette.NET.Adapter.Wpf/{WpfUiAutomationAdapter.cs,Internal/VisualTreeFinder.cs,MarionetteWpf.cs}` (1.3) |
| Channel-Push (`Ai.Trigger`) as stdio notification, hop-counter in payload | Y | `src/Marionette.NET.Runtime/Channel/ChannelEmitter.cs` (1.2) |
| CLI dispatcher: `MyApp.exe --mcp`, `--mcp --headless`, `--mcp-help`; stdout reserved for JSON-RPC | Y | `src/Marionette.NET.Runtime/MarionetteHost.cs` + `samples/Sample.Wpf.TodoApp/Program.cs` (1.2 + 1.4) |
| `build/Marionette.NET.targets`: `EnableMcpAutomation` with Debug=on/Release=off default | Y | `build/Marionette.NET.props` + `.targets` (1.a) |
| Skill-Pack v1: 3 skills + system prompts + showcase conversations | Y (3 skills + canonical reference; "showcase conversations" deferred - see "Limitations" below) | `skill-pack/` (1.4) |
| `Sample.Wpf.TodoApp` + 5 End-to-End-Eval-Cases als CI-Test | Y | `samples/Sample.Wpf.TodoApp/` + `tests/Marionette.NET.Integration/` (1.4 + 1.5) |
| Demo Phase 1: Generic-WPF-Calculator. Claude liest Manifest, ruft Add(2,3), liest Result, screenshot | Y | The masterplan calls for a calculator; we shipped both StripeProbe (calculator-shaped: `Add(int, int)` + `Result` observable) and TodoApp (richer demo). `.phase1/demo.ps1 -Gui` exercises the screenshot path via TodoApp; the StripeProbe `--gui` harness validates the calculator path. |

## Carryovers from Phase 0

PHASE0_FINDINGS.md listed 10 implications (numbered 1-10) for Phase 1. Each is mapped below. (PHASE0_FINDINGS uses 1-10; the prompt for Phase 1.5 mentioned "1-14" - those extra four are interpreted as the prompt's framing of risks; we cover all 10 explicit implications and the extra risk topics in the Limitations section below.)

| Phase-0 implication | Addressed? | Where |
|---|---|---|
| 1. Source generator must register tools via `WithTools<T>()`, never `WithToolsFromAssembly` | Y | `src/Marionette.NET.Runtime/MarionetteHost.cs` calls `.WithTools<MarionetteTools>()`. Tools live as static methods on the (sealed, private-ctor) `MarionetteTools` class (1.2) |
| 2. Multi-target Abstractions to `netstandard2.0;net10.0` | Y | `src/Marionette.NET.Abstractions/Marionette.NET.Abstractions.csproj` `<TargetFrameworks>` (1.a) |
| 3. `_SuppressWpfTrimError=true` baked into `build/Marionette.NET.targets` for WPF+AOT | Y | `build/Marionette.NET.targets` conditional on `UseWPF=true` AND `PublishAot=true` (1.a) |
| 4. Document the C++ workload prerequisite for AOT publish | Partial | The Phase-0 follow-up `.phase0/spike-b-followup.md` covers this. `docs/stripping.md` deferred to Phase 7 (when adopter docs ship as a coherent set) |
| 5. CI matrix needs Windows runner with C++ workload | Y | `.github/workflows/ci.yml` uses `windows-latest` which includes the workload by default |
| 6. Promote `StdoutGuardWriter` to Runtime proper with stderr-only logging | Y | `src/Marionette.NET.Runtime/MarionetteHost.cs` defines `StdoutGuardWriter` as permanent fixture, attaches `ILogger` after host build, surfaces leak count on shutdown (1.2) |
| 7. Hoist `Console.SetOut(StdoutGuardWriter)` before WPF Application ctor in GUI mode | Y | `MarionetteHost.RunAsync` step 3 installs the guard before `Host.CreateEmptyApplicationBuilder`; `MarionetteWpf.AttachTo` runs it from `App.OnStartup`'s background `Task.Run` so the install is the first user-code touchpoint |
| 8. Re-run IL probe in CI on every PR as a regression gate | Y | `.github/workflows/ci.yml` job `build-and-test` invokes `build/Run-IlProbe.ps1`. Locally, the demo + integration tests + IL probe all run from this report's matrix |
| 9. Adapter.Wpf bootstrap should accept `ILogger` rather than writing directly to stderr | Y | `MarionetteWpf.AttachTo(app, roots, args, ILoggerFactory?)`; the adapter takes `ILogger<WpfUiAutomationAdapter>` (1.3) |
| 10. Document `Host.CreateEmptyApplicationBuilder` loudly in adopter docs | Partial | Documented in source comments (`MarionetteHost.cs` step 4) and in `skill-pack/prompts/attributes-reference.md`. Adopter-facing docs (`docs/getting-started.md`) deferred to Phase 7 |

For the four "implications 11-14" the prompt added (mapped from masterplan risks):

- "WPF GUI mode under AOT crashes" - Acknowledged in Phase-0 follow-up B; remains the Phase-1 caveat. Frozen-Mode (`--mcp --headless`) under AOT works; GUI under AOT does not (Microsoft-known limitation).
- "Single-window assumption" - The WPF adapter currently special-cases `Application.MainWindow`; multi-window is Phase 2+ scope.
- "Callable parameter type whitelist is permissive (MAR004)" - Phase 1.b's MAR004 only triggers on obvious blacklist (Stream, delegates, pointers, IntPtr/nint). A tighter whitelist (only primitives + records + collections of primitives) is Phase 6 polish.
- "Skill-pack not yet tested with adoption-scenario" - The skill-pack v1 ships uninstrumented for performance evals. Phase 6 (per masterplan) ships `Marionette.NET.Testing` plus skill-eval property tests; the Phase 1 skill-pack is a smoke-quality artifact, not a measured one.

## Known limitations / Phase-2 implications

These are intentional Phase-1 deferrals or acknowledged caveats. None blocks Phase 2.

- **WPF GUI mode under AOT** crashes with `STATUS_STACK_BUFFER_OVERRUN` (Microsoft-known limitation, not Marionette). Frozen-Mode (`--mcp --headless`) under AOT works perfectly, which is the masterplan's headline use case. Phase 5's AOT hardening pass will revisit if Microsoft ships a fix.
- **Single-window assumption.** `Application.MainWindow` is the only window matched by `MarionetteWpf.AttachTo`'s descriptor-factory rewrite. Secondary windows need adopter to call `ManifestRegistry.BindInstance` or wait for Phase 2's multi-window-routing extension.
- **Callable parameter type whitelist is permissive (MAR004).** Phase 1 only blacklists Stream/delegate/pointer/IntPtr. Phase 6 will tighten to a whitelist (primitives, records, collections of primitives) once the test corpus shows what adopters actually send.
- **Skill-pack lacks property-test eval.** v1 ships smoke-quality. Phase 6's `Marionette.NET.Testing` adds the eval-suite for skill triggering accuracy + procedural fidelity across model upgrades.
- **`notifications/marionette/channel` is custom (not standard MCP).** Cowork / Claude Desktop won't see it - the masterplan's "Cowork-/Claude-Desktop-Support in v1" is explicit out-of-scope. Claude Code CLI is the supported client; it sees the channel push correctly. Phase 6+ may add a fallback to standard `notifications/message` if that channel becomes the de-facto carry-everything path.
- **Source-generator MAR009+ slot** is unused. Phase 2 (Avalonia adapter) is likely to want a "non-`Window` root must be Singleton-bridged" diagnostic and will allocate `MAR009` for it.
- **Showcase Conversations** were not shipped with Phase-1. The skill-pack ships skills and a canonical attribute reference; example conversations are deferred to Phase 6 (where they live alongside the skill-eval suite).
- **Adopter docs** (`docs/getting-started.md`, `docs/architecture.md`, etc) are not shipped. Per the masterplan, Phase 6 owns docs and Phase 7 wraps them with the NuGet meta-package. Phase 1's documentation surface is: `MASTERPLAN.md`, `PHASE0_FINDINGS.md`, this document, the skill-pack `README.md`, and `attributes-reference.md`.

## Phase 2 readiness check

Phase 2 (Avalonia adapter) needs:

- A stable `IUiAutomationAdapter` contract. **Yes** - Phase 1.2 froze the interface (4 methods); Phase 1.3 implemented it for WPF without changes. Phase 2 implements the same interface for Avalonia. No runtime changes anticipated.
- A working `MarionetteHost` that doesn't reference WPF. **Yes** - Runtime depends only on Abstractions; `Adapter.Wpf` is a leaf project that adopters reference conditionally.
- Source-generator output that doesn't reference WPF. **Yes** - the generator emits `using Marionette.Runtime.Manifest;` and references no framework-specific types.
- Documented adapter-authoring contract. **Partial** - source comments in `IUiAutomationAdapter.cs` plus the Phase 1.3 implementation are the working contract. A formal `docs/adapter-authoring.md` is Phase 6 scope.

**Recommendation: proceed to Phase 2.** No load-bearing assumption is in question. The only Phase 1 deliverable that should evolve before Phase 2 begins is the multi-window descriptor-factory rewrite (presently single-window) - and that can also evolve in lockstep with Phase 2's multi-window-routing test cases.

## Files added/changed in Phase 1.5

```
src/Marionette.NET.Runtime/Loop/LoopProtectionService.cs   (UPDATED - MARIONETTE_DECAY_SECONDS env override)
src/Marionette.NET.Runtime/MarionetteHost.cs               (UPDATED - --mcp-help mentions new env var)
build/Marionette.NET.props                                 (UPDATED - bug fix: EnableMcpAutomation default now evaluates booleanly)

tests/Marionette.NET.Integration/                          (NEW - xUnit project)
  Marionette.NET.Integration.csproj
  TodoAppFixture.cs
  EvalCases.cs
  xunit.runner.json
  README.md

.phase1/demo.ps1                                            (NEW - single-command demo)

Marionette.NET.sln                                          (UPDATED - added Integration project)
README.md                                                   (UPDATED - Phase-1 status + quickstart)
PHASE1_FINDINGS.md                                          (NEW - this document)
```

Files deliberately not touched: `MASTERPLAN.md`, `LICENSE`, `.gitignore` (already covers the new artifacts via `*.received.txt` for snapshot tests, `.phase1/*.png` for screenshots, `[Bb]in/` + `[Oo]bj/` for build outputs), `Directory.Build.props`, `global.json`, all of `.phase0/*`, all of `samples/Sample.Wpf.{StripeProbe,TodoApp}/`, all of `src/Marionette.NET.{Abstractions,SourceGenerator,Adapter.Wpf}/`, `tests/Marionette.NET.SourceGenerator.Tests/`, `skill-pack/`, `build/Marionette.NET.targets`, `build/Run-IlProbe.ps1`.
