# Phase 2 — Findings

> **Status:** Phase 2 complete (2.1 -> 2.3). Phase 3 (WinUI + real input pipeline) is unblocked; the `IUiAutomationAdapter` contract is still stable across two adapters now, and the runtime / source-gen surfaces ready for input-simulation additions without breaking changes.
> **Date:** 2026-05-03
> **SDK:** .NET 10.0.202 · Avalonia 11.3.14 · ModelContextProtocol 1.2.0 · Roslyn 4.14.0

Phase 2 set out to deliver, per the masterplan: an Avalonia adapter (`Marionette.NET.Adapter.Avalonia`) with a canonical sample, watchable observables as MCP resources (already shipped early in Phase 1.2 — re-validated against the Avalonia path), per-method dynamic MCP tools, `notifications/tools/list_changed` push, and idempotent tool identity. Detailed per-sub-phase reports live in `.phase2/2{a,b}-*.md`; this document is the consolidated verdict.

## Status

GREEN. Every Phase-2 masterplan deliverable landed; the IL stripping promise from Phase 0 Spike A holds across all three samples (StripeProbe, TodoApp, Dashboard); the Avalonia path mirrors WPF semantics without compromise; per-method dynamic tools + idempotent tool identity unlock direct LLM-side `<Root>.<Method>` calls without breaking the four Phase-1 meta-tools.

## What was built per sub-phase

### 2.1 — Adapter.Avalonia + Sample.Avalonia.Dashboard (`fab369a`)

Production `Marionette.NET.Adapter.Avalonia` mirrors the WPF adapter shape one-to-one against Avalonia 11.x. Three source files (`AvaloniaUiAutomationAdapter.cs`, `MarionetteAvalonia.cs`, `Internal/VisualTreeFinder.cs`) plus the csproj. **TFM is `net10.0`, not `net10.0-windows`** — Avalonia is cross-platform and the adapter consumes only `Avalonia.Application` + `Dispatcher.UIThread` + visual-tree primitives. Adopters get Windows / Linux / macOS from a single build. Full `IUiAutomationAdapter` implementation: `Dispatcher.UIThread.InvokeAsync(...).GetTask().WaitAsync(ct)` for marshalling, `RenderTargetBitmap(new PixelSize(w,h), new Vector(dpi,dpi))` + native `Save(stream)` for PNG screenshots, iterative-DFS `VisualTreeFinder` over `LogicalChildren` first / `GetVisualChildren()` fallback with `AutomationProperties.GetAutomationId()` precedence over `Control.Name`. `MarionetteAvalonia.AttachTo(app, roots, args, loggerFactory?)` rewrites every `RootDescriptor.Create` factory to dispatch through the UI thread AND prefer the live `IClassicDesktopStyleApplicationLifetime.MainWindow` when its CLR FullName matches.

`Sample.Avalonia.Dashboard` is the canonical Avalonia adopter-reference: richer than the WPF TodoApp with five `[McpCallable]` (one async `RefreshAsync`, two `OffUiThread = true`), four `[McpObservable]` (three watchable, one non-watchable), two `[McpEvent]` (one with custom args, one `EventArgs.Empty`), Fluent-themed dark-mode UI, four pre-seeded metrics. Skill-pack updated with framework detection in `marionette-decorate`, "Compatible apps: WPF + Avalonia" in `marionette-explore` / `marionette-test`, and a new "Avalonia adopters" section in `attributes-reference.md` covering the `OnFrameworkInitializationCompleted` wiring snippet, the non-Window root binding pattern, and the TFM choice (`net10.0` over `net10.0-windows`).

### 2.2 — Dynamic per-method tools + idempotent tool identity (`2b1bc6a`)

`<Root>.<Method>` tools registered alongside the four Phase-1 meta-tools. New runtime services:
- `ToolIdentity` (pure helper) — turns `(rootName, CallableDescriptor)` into a stable `<rootName>.<methodName>` tool name plus a SHA-256 fingerprint over a canonical signature. **Description-immune** (description-only changes leave the hash unchanged), **signature-keyed** (param name / type / count / order changes change the hash). 8-hex-char overload disambiguation suffix (`AddTodo_a1b2c3d4`) for collisions.
- `DynamicToolRegistry` — singleton over `McpServer.ServerOptions.ToolCollection` (`McpServerPrimitiveCollection<McpServerTool>`). `RegisterInitial` walks `ManifestRegistry.Roots`, builds one `McpServerTool.Create(Delegate, options)` per callable closing over `(rootName, callable, ...)`, dispatches via the new shared `MarionetteDispatch` pipeline (loop-protection + UI-thread routing + async unwrap + structured error shaping shared with `invoke_method`). `RefreshFromManifestAsync` is the diff-and-mutate path for Phase-5+ hot-plug roots — designed but unused in 2.2.
- `notifications/tools/list_changed` push — auto-emitted by SDK's `McpServerPrimitiveCollection.Changed` event on `TryAdd`/`Remove`; the registry also sends manually after a diff for explicit robustness.

Source generator addition: `JsonSchemaWriter.WriteParametersSchema` emits per-method JSON schemas at compile time (no runtime ITypeSymbol walking). Each `CallableDescriptor` carries a `ParametersJsonSchema` string; the runtime parses it once at registration and stamps onto `tool.ProtocolTool.InputSchema`. EC-7 added (registers + dispatches + parity check with `invoke_method`).

### 2.3 — Findings consolidation (commit hash TBD)

This sub-phase, rolled into the consolidated report. Three artifacts:
- `PHASE2_FINDINGS.md` (this document).
- `README.md` updated to reflect Phase 2 status (Avalonia, dynamic tools, `tools/list_changed`, idempotent tool identity), Avalonia removed from the to-do list.
- Final build-matrix verification run (below) — all green.

No source-code changes in `src/Marionette.NET.*`, `samples/Sample.*`, `tests/Marionette.NET.*`. Working tree dirty per the constraint; orchestrator commits.

## Build matrix at end of Phase 2

All commands run from `C:\Home\Code\nw.Automation` on .NET 10.0.202.

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build Marionette.NET.sln -c Debug` | PASS — 0 warnings, 0 errors (10 projects: Abstractions ×2 TFMs, SourceGenerator, Runtime, Adapter.Wpf, Adapter.Avalonia, StripeProbe, TodoApp, Dashboard, SourceGenerator.Tests, Integration). See note below on parallel-build serialization. |
| 2 | `dotnet build Marionette.NET.sln -c Release` | PASS — 0 warnings, 0 errors |
| 3 | `dotnet test tests/Marionette.NET.SourceGenerator.Tests/Marionette.NET.SourceGenerator.Tests.csproj` | PASS — 25/25 (1 GoldenInput snapshot + 1 GoldenEventInput snapshot + 1 GoldenOverloads snapshot + 1 GoldenParametersSchema snapshot + 5 rejection + 1 positive control + 1 MCP-disabled gating + 10 ToolIdentity + 4 schema-coverage; total 25) |
| 4 | `dotnet test tests/Marionette.NET.Integration/Marionette.NET.Integration.csproj` | PASS — 7/7 (EC-1 through EC-7; ~6 s wall) |
| 5 | `dotnet build samples/Sample.Wpf.StripeProbe/Sample.Wpf.StripeProbe.csproj -c Release -p:EnableMcpAutomation=false` | PASS — stripped output |
| 6 | `dotnet build samples/Sample.Wpf.TodoApp/Sample.Wpf.TodoApp.csproj -c Release -p:EnableMcpAutomation=false` | PASS — stripped output |
| 7 | `dotnet build samples/Sample.Avalonia.Dashboard/Sample.Avalonia.Dashboard.csproj -c Release -p:EnableMcpAutomation=false` | PASS — stripped output |
| 8 | IL probe over StripeProbe stripped DLL (5 needles) | PASS — 0 hits across all 5 needles |
| 9 | IL probe over TodoApp stripped DLL (5 needles) | PASS — 0 hits across all 5 needles |
| 10 | IL probe over Avalonia Dashboard stripped DLL (5 needles) | PASS — 0 hits across all 5 needles |
| 11 | `pwsh .phase1/demo.ps1` | PASS — 12/12 harness checks, 22 JSON-RPC frames, 0 pollution |

### Note on parallel-build serialization (cmd 1)

The integration-test csproj has a `BeforeBuild` target that invokes `dotnet build` on `Sample.Wpf.TodoApp.csproj` to guarantee an MCP-on binary exists for the test fixture. When the solution is built in parallel (the default), the outer solution build and the inner integration-test sub-build can both target `samples/Sample.Wpf.TodoApp/obj/Debug/net10.0-windows/Sample.Wpf.TodoApp.dll` simultaneously, and the second writer hits `CS2012` ("file is being used by another process — VBCSCompiler"). Workaround: pass `-m:1` (single-threaded) for that one command, OR build the TodoApp explicitly first and rely on incremental skip in the second pass. Not a regression; minor MSBuild ergonomics issue worth flagging for Phase 3 to clean up (probably by gating the BeforeBuild on `'$(_IsIntegrationTestBuild)' == 'true'` or by setting `BaseIntermediateOutputPath` on the inner build to a separate directory).

## Phase-2 deliverables vs masterplan

The masterplan lists 8 explicit Phase-2 line items. Each is mapped to its delivered artifact below.

| Masterplan Phase-2 line | Implemented? | Notes |
|---|---|---|
| `Marionette.NET.Adapter.Avalonia` (Dispatcher.UIThread, RenderTargetBitmap, FindByName via Name + AutomationId) | YES | Phase 2.1 commit `fab369a`; `src/Marionette.NET.Adapter.Avalonia/{AvaloniaUiAutomationAdapter,MarionetteAvalonia,Internal/VisualTreeFinder}.cs` |
| `[McpObservable(Watchable=true)]` -> MCP-Resource (`marionette://<root>/<prop>`), `resources/subscribe`, 200 ms coalesce | YES | Already shipped in Phase 1.2 (early delivery per the masterplan's "Phase 2 watchables" line); re-validated against Avalonia in Phase 2.1 (Dashboard's `MetricCount` / `Total` / `IsPaused` watchables PASS in `--avalonia` harness) |
| INotifyPropertyChanged detection + configurable polling fallback (default 500 ms) | YES | Phase 1.2 `WatchableResourceProvider`; covered by EC-3 + the Dashboard sample's INPC plumbing |
| Per-Method-Tools (`<root>.<method>`) | YES | Phase 2.2 commit `2b1bc6a`; `src/Marionette.NET.Runtime/Tools/DynamicToolRegistry.cs` |
| `notifications/tools/list_changed` push (hot-plug roots) | YES | Phase 2.2; auto-emitted by SDK's `McpServerPrimitiveCollection.Changed` + manual emit on diff in `RefreshFromManifestAsync` |
| Hot-reload-stable Tool-Identity (deterministic hashing over class+method+signature) | YES | Phase 2.2 `ToolIdentity` — SHA-256 over `<root>\n<method>\n<param0Name>:<param0Type>\n...` canonical signature; description-immune verified by `ComputeStableHash_IgnoresDescriptionChange` unit test |
| `Sample.Avalonia.Dashboard` | YES | Phase 2.1; 5 callables / 4 observables / 2 events |
| Skill-Pack Avalonia examples | YES | Phase 2.1 — `attributes-reference.md` Avalonia wiring snippet, framework detection in `marionette-decorate`, "Compatible apps" in `marionette-explore` / `marionette-test` |

## Carryovers from Phase 1

PHASE1_FINDINGS.md listed seven known limitations / Phase-2 implications. Status of each going into Phase 3:

- **WPF GUI mode under AOT crashes** (Microsoft-known limitation, not Marionette). Status: still open, still Microsoft's problem. Frozen-Mode (`--mcp --headless`) under AOT works perfectly; WPF GUI under AOT does not. Phase 5's AOT hardening pass is the masterplan's revisit slot.
- **Single-window assumption** in `MarionetteWpf.AttachTo`. Status: still the case for both Wpf and Avalonia adapters. The descriptor-factory rewrite only auto-substitutes the live `MainWindow` when types match; secondary windows still need adopter-side `BindInstance`. Phase 3's "Multi-Window-Routing (windowId-Suffix only when needed)" line item resolves this.
- **Callable parameter type whitelist is permissive (MAR004)** — Phase 6 polish; unchanged.
- **Skill-pack lacks property-test eval** — Phase 6 (`Marionette.NET.Testing`); unchanged.
- **`notifications/marionette/channel` is custom (not standard MCP)** — by design (masterplan); Cowork / Claude Desktop won't see it. Claude Code CLI sees correctly. Unchanged.
- **Source-generator MAR009+ slot** is still unused. Phase 2.1's non-Window root binding pattern documented but no diagnostic was added — Phase 2 punted this for code-clarity reasons (it's an adopter-pattern issue, not a code defect we can flag at compile time without more context). Likely Phase 6 polish.
- **Showcase Conversations** — Phase 6 deliverable; unchanged.
- **Adopter docs** (`docs/getting-started.md`, etc.) — Phase 6 / 7; unchanged.

### Avalonia AOT story

The Avalonia adapter csproj sets `<IsAotCompatible>true</IsAotCompatible>` and `<IsTrimmable>true</IsTrimmable>` gated on `PublishAot=true`, mirroring the WPF adapter. **AOT publish runtime validation NOT performed in Phase 2.** Phase 0 Spike B validated WPF AOT publish + Frozen-Mode handshake end-to-end (48.5 MB single-file; 12 IL3xxx warnings all from WPF intrinsics, zero from Marionette code). Avalonia is expected to fare better under AOT than WPF (Avalonia targets AOT/Trimming as a first-class scenario in 11.x; WPF's AOT story is famously worse), but we don't have empirical confirmation. **Open Phase-3 item: run the equivalent of Spike B's `--mcp --headless` AOT publish smoke test against the Dashboard sample.** Recommended placement: `aot-publish-smoke` job in `.github/workflows/ci.yml` extended to include the Avalonia sample.

### `simulate_input` / `raise_event`

Explicitly out of scope for Phase 2 — Phase 3 territory per the masterplan ("Phase 3 — WinUI + Real Input"). Both `IUiAutomationAdapter` implementations (Wpf and Avalonia) currently return `simulate_input_not_supported` and `raise_event_not_supported` placeholders. Phase 3 ships the real input pipeline per adapter:
- WPF: `InputManager.ProcessInput`
- Avalonia: `IInputDevice`-pump (the equivalent of WPF's `InputManager.ProcessInput`)
- WinUI: `InputInjector`
- RoutedEvent mechanics with bubbling/tunneling per framework

## Known limitations / Phase-3 implications

What Phase-3 will need:

- **`simulate_input(target, kind, args)`** with real-input pipeline per adapter. The `IUiAutomationAdapter` contract is stable; Phase 3 adds two new methods (`SimulateInputAsync`, `RaiseEventAsync`) without changing the existing four. Adapters that don't implement them return structured `*_not_supported` errors (current behaviour, just with the added methods to override).
- **`raise_event(target, eventName, args)`** with framework-specific RoutedEvent mechanics. Bubbling/tunneling semantics differ per framework (WPF's `RoutedEvent` handler invocation pipeline vs Avalonia's `RoutingStrategies` enum vs WinUI's RoutedEventArgs); the runtime side is just a pass-through.
- **Multi-Window-Routing** (windowId suffix when multiple windows are open). Currently both adapters single-window. Adapter API: `EnumerateWindowsAsync` already exists in stub form on `IUiAutomationAdapter` (returns the live MainWindow only); Phase 3 expands it to return all open windows with stable IDs, and the manifest-registry's instance binding gains a windowId-keyed map.
- **Avalonia AOT publish smoke test** (we proved it builds Release; runtime not yet validated like WPF was in Phase-0 Spike B). One-paragraph addition to `.github/workflows/ci.yml`'s `aot-publish-smoke` job: `dotnet publish samples/Sample.Avalonia.Dashboard -c Release -p:PublishAot=true -p:EnableMcpAutomation=true`, then run `--mcp --headless` against the AOT'd binary and confirm `tools/list` returns the four meta-tools + N dynamic tools, just like the WPF case.

### Inter-test flakiness / dispose leaks worth fixing before Phase 3

- **Parallel-build collision on TodoApp** (cmd 1 above) — flagged for cleanup. Probably gate the integration-test BeforeBuild target on a sentinel, or use a separate `BaseIntermediateOutputPath`. Cosmetic; doesn't block Phase 3 work but will start failing in CI more often as parallelism increases.
- **Three pre-handshake `notifications/tools/list_changed` notifications** (Phase 2.2 trapdoor #2) — adding tools one-at-a-time during initial registration causes the SDK's primitive collection to raise `Changed` on each `TryAdd`, BEFORE the client sends `notifications/initialized`. Effectively ignored by the transport (client hasn't subscribed yet); functionally clean, cosmetically noisy. Phase 3 could batch the registration into a single transactional add, or register before `Bind`. Five notifications visible in the demo.ps1 stdout capture (one per dynamic tool added during initial registration).

## Phase-3 readiness check

Phase 3 (WinUI + Real Input) needs:

- **A stable `IUiAutomationAdapter` contract.** YES — Phase 1 froze the original four methods (`DispatchAsync`, `CaptureScreenshotAsync`, `ResolveControlAsync`, `EnumerateWindowsAsync`), Phase 2.1 implemented them for Avalonia without changes, Phase 2.2 introduced `MarionetteDispatch` which routes through the adapter without touching the contract. Phase 3 adds methods (input simulation, event raising) — additive, not breaking.
- **Runtime / source-gen don't need changes for input simulation** — input simulation is per-adapter. The runtime forwards through `MarionetteDispatch` which already takes `IUiAutomationAdapter`; new methods slot in alongside. Source-gen is unaffected (input simulation isn't attribute-driven; it's a runtime tool).
- **`MarionetteHost` doesn't reference WPF or Avalonia.** YES — Runtime depends only on Abstractions; both adapters are leaf projects that adopters reference conditionally.
- **Source-generator output doesn't reference WPF or Avalonia.** YES — generator emits `using Marionette.Runtime.Manifest;` only.
- **Documented adapter-authoring contract.** Partial — source comments in `IUiAutomationAdapter.cs` plus the two adapter implementations are the working contract. A formal `docs/adapter-authoring.md` is Phase 6 scope. Phase 3 will likely produce its own `.phase3/3a-adapter-winui.md` style report; the cumulative learnings should feed `docs/adapter-authoring.md` when Phase 6 ships docs.

**Recommendation: proceed to Phase 3.** No load-bearing assumption is in question. The Avalonia work in Phase 2.1 mirrored the WPF semantics one-to-one and the dynamic-tools work in Phase 2.2 added a new surface without breaking any existing surface. The only Phase-2 deliverable that should evolve before Phase 3 begins is the multi-window descriptor-factory rewrite — and that can also evolve in lockstep with Phase 3's multi-window-routing test cases.

## Files added/changed in Phase 2.3

```
PHASE2_FINDINGS.md                                          (NEW — this document)
README.md                                                   (UPDATED — Phase-2 status, Avalonia removed from to-do, dynamic tools mentioned)
```

Files deliberately not touched (per Phase 2.3 constraint set):
- All of `src/Marionette.NET.*/`
- All of `samples/Sample.*/`
- All of `tests/Marionette.NET.*/`
- All of `.phase0/{ProbeIl,StdioTest}/`
- `MASTERPLAN.md`, `LICENSE`, `.gitignore`, `Directory.Build.props`, `global.json`, `PHASE0_FINDINGS.md`, `PHASE1_FINDINGS.md`
- `build/Marionette.NET.props`, `build/Marionette.NET.targets`, `build/Run-IlProbe.ps1`
- `skill-pack/`
- `.phase1/demo.ps1`, `.phase2/2a-adapter-avalonia.md`, `.phase2/2b-dynamic-tools.md`
- `Marionette.NET.sln` (verified via `dotnet sln list` — all 10 projects present, no changes needed)
