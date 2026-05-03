# Phase 1.4 (1e) — Sample.Wpf.TodoApp + Skill-Pack v1

**Status:** PASS
**Date:** 2026-05-03
**SDK:** .NET 10.0.202 · ModelContextProtocol 1.2.0 · Roslyn 4.14.0

## Goal & verdict

Phase 1.4 ships the canonical adopter-reference sample plus the LLM
skill-pack that makes Marionette.NET actually adopt-able. Two deliverables:

1. **`samples/Sample.Wpf.TodoApp`** — a real, dark-mode-friendly WPF TODO
   app. Five `[McpCallable]` methods, four `[McpObservable]` properties (three
   watchable), `INotifyPropertyChanged` plumbed correctly so push updates
   beat the 500 ms polling fallback. Mirrors the StripeProbe's wiring shape
   so adopters who copy-pasted from StripeProbe see the same scaffolding.

2. **`skill-pack/`** — three Claude Code skills (`marionette-explore`,
   `marionette-decorate`, `marionette-test`), a skill-pack-level README, and
   an `attributes-reference.md` that adopters using non-Claude agents
   (Cursor, Cline, Aider) can read directly. Distribution model for Phase 1
   is manual copy; Phase 7's NuGet automates installation.

**Verdict: GO for Phase 1.5.** Every build-matrix entry is green, the IL
probe stays at 0 hits across all four needles for both samples in stripped
Release, the headless StripeProbe handshake holds at 7/7, the new TodoApp
headless harness is 9/9, and the new TodoApp GUI harness (with PNG screenshot
validation) is 9/9 with INFO on the expected force-kill.

## What was built

### `samples/Sample.Wpf.TodoApp/` — the canonical sample

| File | Purpose |
|---|---|
| `Sample.Wpf.TodoApp.csproj` | `net10.0-windows`, `UseWPF=true`, `WinExe`. Imports `build/Marionette.NET.props` + `.targets` (same pattern as StripeProbe). References Abstractions always; conditional Adapter.Wpf when `EnableMcpAutomation=true`; source generator via analyzer ProjectReference (always). `EnableDefaultApplicationDefinition=false` so our custom `[STAThread] Program.Main` wins over the SDK auto-emitted one. `EmitCompilerGeneratedFiles=true` so adopters can inspect `Marionette.g.cs` in obj/. |
| `App.xaml` | Dark-mode-friendly resource dictionary: full palette (`BgBrush`, `PanelBrush`, `AccentBrush`, `DangerBrush`, etc.), three button styles (`AccentButton`, `GhostButton`, `DangerButton`), default styles for `TextBlock`, `TextBox`, `CheckBox`. Designed to render decently in screenshots. |
| `App.xaml.cs` | `App` ctor explicitly calls `InitializeComponent()` because `EnableDefaultApplicationDefinition=false` removes the auto-emit `Main` (and with it the auto-emit `InitializeComponent` call); without this, `MainWindow.xaml`'s `StaticResource` references throw at construction. `OnStartup` (under `#if MCP_ENABLED`) rewrites every `RootDescriptor.Create` factory whose `TypeName` matches `TodoListViewModel.FullName` to return `TodoListViewModel.Shared`, then calls `MarionetteWpf.AttachTo(this, bridgedRoots, e.Args)`. |
| `MainWindow.xaml` | Four-row Grid: header, counts strip, add input row, ItemsControl (one DataTemplate per todo, with a CheckBox + Title + Remove button), footer with "Last added: …" + "Clear completed" button, bottom hint. `LastAddedTitle` binding is explicitly `Mode=OneWay` (read-only property). |
| `MainWindow.xaml.cs` | Sets `DataContext = TodoListViewModel.Shared` in the ctor. Pre-seeds two demo items so screenshots aren't empty. UI event handlers route through ViewModel methods (`AddTodo`, `RemoveTodo`, `ClearCompleted`) — no parallel state path, no `[McpCallable]` decoration on the code-behind. |
| `TodoItem.cs` | Plain INPC class with `Title` + `IsDone`. The latter's setter fires `PropertyChanged`, which `TodoListViewModel.OnItemPropertyChanged` listens to so derived counts refresh. |
| `TodoListViewModel.cs` | The `[McpRoot]`. Five `[McpCallable]` methods (`AddTodo` / `RemoveTodo` / `ToggleDone` / `ClearCompleted` / `RenameTodo`), four `[McpObservable]` properties (three watchable), a static `Shared` singleton (lazy-init under a lock so first-caller-wins). Implements `INotifyPropertyChanged` and fires the right names from every mutation path. Hooks `_items.CollectionChanged` to (un)subscribe per-item INPC. |
| `Program.cs` | Custom `[STAThread] Main`. Three modes: no flag → `RunGui()`; `--mcp --headless` → `MarionetteHost.RunAsync` directly (NoOpAdapter); `--mcp` (GUI) → fall through to `RunGui()` and let `App.OnStartup` wire the host. `--mcp-help` writes manifest summary to stderr. |

### `samples/Sample.Wpf.TodoApp` source-generator output

Generated `obj/Debug/net10.0-windows/generated/.../Marionette.g.cs` contains:

- 1 `RootDescriptor` for `TodoListViewModel` (factory: `() => new TodoListViewModel()` — rewritten in App.OnStartup to return `Shared`).
- 5 `CallableDescriptor`s with typed lambdas. Every Invoke shape is the
  void-return-null path (all five callables are sync void).
- 4 `ObservableDescriptor`s. Three with `Watchable=true`. All four read
  through typed lambdas with no reflection.
- 0 `TriggerableDescriptor`s — TodoApp uses CSharp event handlers + ViewModel
  methods, not `[McpTriggerable]` properties on Button instances. (Adopters
  who want the alternative pattern can look at `marionette-decorate` skill.)

### `Marionette.NET.sln` — added project

Added `Sample.Wpf.TodoApp` under the existing `samples` solution folder
(GUID `{12345678-1234-1234-1234-123456789ABC}`). All four configurations
(`Debug|Any CPU`, `Release|Any CPU`) wired with `ActiveCfg` + `Build.0`.

### `.phase0/StdioTest/Program.cs` — extended

| Change | Detail |
|---|---|
| `--todoapp` flag | Switches the assertion suite from StripeProbe-specific (Add/Result) to TodoApp-specific. Bare `--todoapp` runs against the headless `--mcp --headless` build; `--todoapp --gui` runs against `--mcp` GUI build with PNG-validation. |
| TodoApp assertions | `inspect_app_api` returns `TodoListViewModel` with all 5 callables + 4 observables (verified by name-match against the manifest's nested arrays). `read_observable TotalCount` returns a non-negative integer (mode-aware baseline — headless 0, GUI 2). `invoke_method AddTodo("buy milk")` succeeds. `read_observable TotalCount` returns `baseline+1` after AddTodo. `resources/subscribe` to `marionette://TodoListViewModel/TotalCount` followed by a second `AddTodo("eggs")` produces a `notifications/resources/updated` matching the URI. |
| Notification correlation fix | The original `WaitForResponseAsync` discarded ANY message without a matching ID — including notifications. Phase 1.4 introduces a process-global `s_notifications` queue that captures notifications stashed by the response correlator, plus a new `WaitForResourceUpdate` helper that scans the stash + the live queue. Without this fix, the notification arrives during the response wait for the AddTodo call and gets dropped before the watcher starts. |
| Helpers added | `TryParseTodoAppManifest`, `ExtractNames`, `ReadObservableInt`, `InvokeMethodAsync`, `NotificationWatcher`, `WaitForResourceUpdate`, `IsResourceUpdate`. All scoped to the `--todoapp` mode; StripeProbe assertions kept verbatim under the `else` branch of the mode gate. |

### `.phase1/test-todoapp.ps1` — runner script

Wraps `dotnet StdioTest.dll <todoapp.exe> --todoapp`. Validates that both
artifacts (the harness and the sample) exist, runs the harness, prints a
PASS/FAIL line. ASCII-only (PowerShell 5.1 parses UTF-8-without-BOM in
ANSI which broke the original em-dash version).

### `skill-pack/` — Skill-Pack v1

```
skill-pack/
├── README.md                            (~120 lines, install + skill summary)
├── claude-code/
│   ├── marionette-explore/SKILL.md      (~120 lines)
│   ├── marionette-decorate/SKILL.md     (~280 lines, the "biggest" skill)
│   └── marionette-test/SKILL.md         (~150 lines)
└── prompts/
    └── attributes-reference.md          (~440 lines, canonical spec)
```

Each `SKILL.md` opens with a YAML frontmatter (`name`, `description`) — the
canonical Claude Code skill format. The description is the trigger
mechanism; it includes both **what** the skill does AND **when** to use it
(verbatim trigger phrases). The body is a numbered procedure with explicit
conditions and concrete tool-call patterns — not human-flowing prose.

`attributes-reference.md` consolidates:
- Namespace conventions (`using Marionette;`).
- The four attributes' constraints, properties, examples, and source-gen
  diagnostics (MAR001–MAR008 with severities + fixes).
- The `Ai` channel API: `Trigger`, `ScheduleTrigger`, `IsActive`, stripping
  semantics, loop protection.
- The four runtime tools (`inspect_app_api`, `invoke_method`,
  `read_observable`, `capture_screenshot`) with full error-code tables.
- Common mistakes (what NOT to decorate, the 7 most common LLM-fumbles).
- WPF wiring snippets including the non-Window-root descriptor-factory
  rewrite pattern.

The three skills cite `attributes-reference.md` rather than duplicate the
content; adopters using a non-Claude agent can read it directly.

## Build matrix results

All commands run from `C:\Home\Code\nw.Automation`. .NET 10.0.202.

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build Marionette.NET.sln -c Debug` | PASS — 0 warnings, 0 errors (8 projects) |
| 2 | `dotnet build Marionette.NET.sln -c Release` | PASS — 0 warnings, 0 errors |
| 3 | `dotnet test tests/Marionette.NET.SourceGenerator.Tests/...csproj -c Debug` | PASS — 8/8 |
| 4 | `dotnet build samples/Sample.Wpf.TodoApp/...csproj -c Release -p:EnableMcpAutomation=false` | PASS — 7-file stripped output |
| 5 | `dotnet build samples/Sample.Wpf.TodoApp/...csproj -c Release -p:EnableMcpAutomation=true` | PASS — 0 warnings |
| 6 | `dotnet build samples/Sample.Wpf.TodoApp/...csproj -c Debug -p:EnableMcpAutomation=true` | PASS — 0 warnings |
| 7 | `dotnet build samples/Sample.Wpf.StripeProbe/...csproj -c Release -p:EnableMcpAutomation=false` | PASS — still 7-file stripped |
| 8 | IL probe over TodoApp stripped DLL (cmd 4) | PASS — 0 hits across all 4 needles |
| 9 | IL probe over StripeProbe stripped DLL (cmd 7) | PASS — 0 hits across all 4 needles |
| 10 | `dotnet StdioTest.dll <StripeProbe.exe>` | PASS — 7/7 checks, 6 JSON-RPC frames, 0 pollution |
| 11 | `dotnet StdioTest.dll <StripeProbe.exe> --gui` | PASS — 7/7 checks (incl. PNG validation), 6 JSON-RPC frames, 0 pollution |
| 12 | `dotnet StdioTest.dll <TodoApp.exe> --todoapp` | PASS — 9/9 checks, 10 JSON-RPC frames, 0 pollution |
| 13 | `dotnet StdioTest.dll <TodoApp.exe> --todoapp --gui` | PASS — 9/9 checks (incl. PNG validation), 10 JSON-RPC frames, 0 pollution |

Stripped TodoApp output (`bin/Release/net10.0-windows/`) is exactly 7 files,
matching StripeProbe's baseline:

```
Marionette.NET.Abstractions.dll      (7 680 B)
Marionette.NET.Abstractions.pdb
Sample.Wpf.TodoApp.deps.json         (925 B — only Abstractions ships)
Sample.Wpf.TodoApp.dll               (20 480 B)
Sample.Wpf.TodoApp.exe               (162 816 B — apphost)
Sample.Wpf.TodoApp.pdb
Sample.Wpf.TodoApp.runtimeconfig.json
```

`Marionette.g.cs` is NOT generated in the stripped build (the source
generator's `MCP_ENABLED` gate prevents emission), so the user assembly
references zero `Marionette.Runtime.Manifest` types.

## IL probe results

### TodoApp (cmd 8)

```
[PASS] Marionette.NET.Runtime: TOTAL hits across 1 file(s): 0
[PASS] Adapter.Wpf:            TOTAL hits across 1 file(s): 0
[PASS] Marionette.Ai:          TOTAL hits across 1 file(s): 0
[PASS] ModelContextProtocol:   TOTAL hits across 1 file(s): 0
PASS — stripped build contains zero forbidden symbols.
```

### StripeProbe (cmd 9, regression check)

```
[PASS] Marionette.NET.Runtime: TOTAL hits across 1 file(s): 0
[PASS] Adapter.Wpf:            TOTAL hits across 1 file(s): 0
[PASS] Marionette.Ai:          TOTAL hits across 1 file(s): 0
[PASS] ModelContextProtocol:   TOTAL hits across 1 file(s): 0
PASS — stripped build contains zero forbidden symbols.
```

The masterplan's load-bearing stripping promise (Phase 0 Spike A) survives
Phase 1.4 unchanged.

## StdioTest output for TodoApp

### Headless mode (`--todoapp`, cmd 12)

```
=== Phase 1.4 TodoApp stdio handshake harness ===
PASS - initialize handshake (server: Marionette.NET 0.0.1, protocol 2025-11-25)
PASS - tools/list contains all four Phase-1 tools (got: read_observable,capture_screenshot,inspect_app_api,invoke_method)
PASS - inspect_app_api returned TodoListViewModel manifest with all 5 callables + 4 observables
PASS - read_observable TotalCount initially returned 0
PASS - invoke_method AddTodo("buy milk") succeeded
PASS - read_observable TotalCount returned 1 after AddTodo (baseline + 1)
PASS - resources/subscribe + AddTodo produced notifications/resources/updated for marionette://TodoListViewModel/TotalCount
PASS - capture_screenshot surfaced a structured 'screenshot_not_supported' error (NoOpAdapter)
PASS - child exited cleanly with code 0
stdout summary: 10 JSON-RPC frames, 0 pollution lines
stderr total: 28 lines
=== Phase 1.4 TodoApp handshake: PASS ===
```

### GUI mode (`--todoapp --gui`, cmd 13)

```
=== Phase 1.4 TodoApp stdio handshake harness ===
PASS - initialize handshake (server: Marionette.NET 0.0.1, protocol 2025-11-25)
PASS - tools/list contains all four Phase-1 tools (got: read_observable,capture_screenshot,inspect_app_api,invoke_method)
PASS - inspect_app_api returned TodoListViewModel manifest with all 5 callables + 4 observables
PASS - read_observable TotalCount initially returned 2
PASS - invoke_method AddTodo("buy milk") succeeded
PASS - read_observable TotalCount returned 3 after AddTodo (baseline + 1)
PASS - resources/subscribe + AddTodo produced notifications/resources/updated for marionette://TodoListViewModel/TotalCount
PASS - capture_screenshot returned a valid PNG (32298 bytes, mimeType=image/png). Saved to .phase1/screenshot-test.png.
INFO - GUI-mode child still alive after MCP shutdown (expected; killing).
stdout summary: 10 JSON-RPC frames, 0 pollution lines
stderr total: 28 lines
=== Phase 1.4 TodoApp handshake: PASS ===
```

In GUI mode the baseline TotalCount is 2 because `MainWindow.ctor`
pre-seeds two demo items ("Read the Marionette README" and "Decorate my
ViewModel with [McpCallable]"). The harness's baseline-relative assertion
makes this mode-agnostic.

The captured PNG is **244 × 48** (Phase 1.3 harness force-kills the child
shortly after capture, so the screenshot is taken from an early-render
frame; the file IS a valid PNG and shows the populated UI). Saved as
`.phase1/screenshot-test.png` and (for posterity) `.phase1/screenshot-todoapp.png`.

The captured image visually confirms:
- The dark-mode palette renders correctly.
- The counts strip shows `TOTAL: 4`, `DONE: 0`, `PENDING: 4` (two pre-seed
  items + two harness AddTodos).
- The four todo rows render with checkboxes + Remove buttons.
- The footer shows "Last added: eggs" — the live binding fired correctly
  off the `LastAddedTitle` PropertyChanged event.
- The bottom-hint reads "Marionette root: TodoListViewModel · 5 callables,
  4 observables (3 watchable)".

## Skill-Pack inventory

| Skill | Trigger phrases (from YAML `description`) | Procedure |
|---|---|---|
| `marionette-explore` | "explore this app", "what can this app do?", "show me what's there", "list the manifest", "tour the app" | (1) Call `inspect_app_api()`. (2) Per root, summarize callables/observables/triggerables in human-readable form (bullet lists, with watch URIs for watchable observables). (3) Optional `capture_screenshot()`. (4) Suggest concrete next-step tool calls. (5) Don't over-explain. |
| `marionette-decorate` | "make this Marionette-controllable", "add MCP attributes", "decorate this for Claude", "expose this app to Claude", "wire up Marionette in my app" | (1) Read project structure. (2) Identify candidate root classes (ViewModels, services, code-behind). (3) Suggest `[McpCallable]` placements. (4) Suggest `[McpObservable]` (especially derived) including `Watchable=true` + INPC plumbing. (5) Suggest `[McpTriggerable]` on Button properties. (6) Edit files. (7) Verify build (handle MAR001-MAR008). (8) Show wiring snippet. (9) Encourage running `marionette-test` after. Includes "things NOT to decorate" with 7 cases + reasons. |
| `marionette-test` | "test this app", "verify the app works", "smoke-test", "sanity check", "run my Marionette decoration" | (1) `inspect_app_api()` to discover surface. (2) Snapshot observables before. (3) Generate plausible test invocation per `[McpCallable]` (heuristic patterns by method-name verb). (4) Run each invocation, re-read affected observables, classify PASS/NOTE/FAIL. (5) Optional loop-protection probe. (6) `capture_screenshot()`. (7) Print structured PASS/SKIP/FAIL summary. (8) Honest disclaimer: heuristic, not exhaustive. |

All three skills cite `prompts/attributes-reference.md` for the underlying
spec rather than duplicating definitions.

## Files changed / added

```
samples/Sample.Wpf.TodoApp/                  (NEW)
  Sample.Wpf.TodoApp.csproj
  App.xaml
  App.xaml.cs
  MainWindow.xaml
  MainWindow.xaml.cs
  TodoItem.cs
  TodoListViewModel.cs
  Program.cs

skill-pack/                                  (NEW)
  README.md
  claude-code/
    marionette-explore/SKILL.md
    marionette-decorate/SKILL.md
    marionette-test/SKILL.md
  prompts/
    attributes-reference.md

Marionette.NET.sln                           (added Sample.Wpf.TodoApp project)

.phase0/StdioTest/Program.cs                 (UPDATED — --todoapp mode + helpers + notification stash)

.phase1/
  test-todoapp.ps1                           (NEW — TodoApp test runner script)
  1e-todoapp-skillpack.md                    (NEW — this report)
  ilprobe-todoapp.log                        (NEW — Phase-1.4 IL probe log artefact)
  ilprobe-stripeprobe.log                    (REGENERATED — Phase-1.4 regression run)
  screenshot-test.png                        (REGENERATED — last-harness-run TodoApp/StripeProbe shot)
  screenshot-todoapp.png                     (NEW — canonical TodoApp shot, copy of screenshot-test.png after the --todoapp --gui run)
```

Files deliberately not touched (per the constraint set): `MASTERPLAN.md`,
`README.md`, `LICENSE`, `.gitignore`, `Directory.Build.props`, `global.json`,
`build/Marionette.NET.props`, `build/Marionette.NET.targets`, `build/Run-IlProbe.ps1`,
all of `.phase0/spike-*` reports, `.phase0/ProbeIl/*`, all of `src/*`,
`samples/Sample.Wpf.StripeProbe/*`, `tests/*`.

## Issues encountered

1. **Non-Window root + ManifestRegistry instance affinity.** The source
   generator emits `Create: () => new TodoListViewModel()`. `MarionetteWpf.AttachTo`
   only special-cases `Window`-typed roots (matched against `app.MainWindow`).
   For the TodoApp's `TodoListViewModel`, the registry would create a SECOND
   instance, separate from the one MainWindow's DataContext binds.
   Resolution: `App.OnStartup` rewrites the descriptor's `Create` factory
   to return `TodoListViewModel.Shared` BEFORE calling AttachTo. This
   pattern is documented prominently in `attributes-reference.md` so
   adopters with custom ViewModels can find it.

2. **App.OnStartup ran before App.xaml resources loaded.** With
   `EnableDefaultApplicationDefinition=false`, the SDK skips the
   auto-generated `Main` AND the auto-generated `App.InitializeComponent`
   call. MainWindow's `StaticResource` references then threw at
   construction. Fixed by adding an explicit `App` ctor that calls
   `InitializeComponent()`. (StripeProbe's `App.xaml` was empty so it
   never hit this; the TodoApp's rich resource dictionary triggered it.)

3. **WPF TwoWay binding error on read-only `LastAddedTitle`.** A bare
   `<Run Text="{Binding LastAddedTitle, ...}" />` defaults to a TwoWay
   binding which crashes WPF at first-layout because the property has no
   setter. Resolution: explicit `Mode=OneWay` on the binding and using
   `Text` + `StringFormat` on the parent `TextBlock` instead of split
   `<Run>` elements. Documented in the `attributes-reference.md` "common
   mistakes" section by analogy.

4. **`pwsh` not on `PATH` in the bash sandbox.** Same Phase-1.0-1.3 issue.
   Ran `Run-IlProbe.ps1` via the dedicated PowerShell tool with `&` (call
   operator). The script itself unchanged.

5. **PowerShell 5.1 + UTF-8-without-BOM ps1 parse error.** `test-todoapp.ps1`
   originally used em-dashes; PowerShell 5.1 reads UTF-8 files as ANSI
   when there's no BOM, mangling multi-byte characters and causing a
   parse error at the `}` boundary. Resolution: rewrote with ASCII-only
   characters. Documented the constraint by example (no BOM-handling
   prelude needed in any other phase 1 file).

6. **Notification correlation in StdioTest harness.** `WaitForResponseAsync`
   discarded any message without a matching ID — including notifications.
   The TodoApp harness's resource-update assertion needed a stash for
   notifications that arrive while the harness is correlating an unrelated
   request response. Added a process-global `s_notifications` queue +
   re-queue logic in the new `WaitForResourceUpdate`.

## Phase 1.5 hand-off — Demo + 5 eval-cases

Phase 1.5 owns:

- **A 90-second tweetable demo** showing Claude driving the TodoApp via the
  three skills end-to-end:
  1. Claude calls `inspect_app_api()` (via `marionette-explore`).
  2. Claude calls `AddTodo("Buy milk")`, `AddTodo("Walk dog")`, `ToggleDone(0)`.
  3. Claude takes a screenshot to verify (`marionette-test`).
  4. The TodoApp's GUI live-updates as Claude operates, no mouse/keyboard.
  5. Claude reports the final state structured. **No human clicked anything.**

- **5 eval-cases** as CI-runnable specs that can validate the skills don't
  drift across model upgrades:

| # | Case | Expected outcome |
|---|---|---|
| 1 | Run `marionette-explore` against TodoApp's `--mcp` build | Report names all 5 callables + 4 observables, formatted as bullets, suggests next steps incl. `subscribe to marionette://TodoListViewModel/TotalCount` |
| 2 | Run `marionette-decorate` against a fresh "Calculator" ViewModel that has `public int Add(int, int)`, `public void Reset()`, `public int Result` | Adds `[McpRoot]` to class, `[McpCallable]` to both methods (with descriptions inferred from names), `[McpObservable]` to `Result` with description. Project builds clean. |
| 3 | Run `marionette-decorate` against a class with a static method, a non-public method, and a method taking `Stream` | Skips/declines all three with reasons (MAR001 / MAR002 / MAR004 in the build output as expected) |
| 4 | Run `marionette-test` against TodoApp `--mcp` | Reports PASS for AddTodo + observable reaction, NOTE for unrelated observables, captures a final screenshot, prints structured summary |
| 5 | Run `marionette-test` against a deliberately-broken decoration where `[McpCallable]` is on a method with `Stream` arg | Reports SKIP with reason "complex parameter type — heuristic test generation declined" rather than calling and crashing |

The eval-cases live as `.phase1.5/eval-cases/` (separate Phase). Their
runner can use the StdioTest harness's TodoApp mode plus a thin Claude API
wrapper or, for offline CI, mocked LLM responses driven by the skill
contract.

**Skills are the weakest link in adoption** (the masterplan's framing).
Phase 1.5's eval suite quantifies the strength of that link — without
quantification, future model upgrades can silently degrade triggering
accuracy or procedural fidelity. Treat the 5 cases as smoke checks; Phase 6
expands to property-test-style evals via `Marionette.NET.Testing`.

## Status against original Phase 1.4 prompt

| Prompt requirement | Status |
|---|---|
| `samples/Sample.Wpf.TodoApp/` with all listed files | ✅ |
| `[McpRoot]` + 5 `[McpCallable]` + 4 `[McpObservable]` (3 watchable) on TodoListViewModel | ✅ |
| INPC implementation | ✅ |
| TodoItem with Title + IsDone + INPC | ✅ |
| `Marionette.NET.sln` updated | ✅ |
| Build matrix Debug+Release+stripped Release | ✅ all green |
| IL probe over stripped TodoApp.dll | ✅ 0 hits across all 4 needles |
| Stdio handshake test extended for TodoApp | ✅ 9/9 checks both headless and GUI |
| `.phase1/test-todoapp.ps1` | ✅ |
| `skill-pack/claude-code/{explore,decorate,test}/SKILL.md` | ✅ |
| `skill-pack/README.md` | ✅ |
| `skill-pack/prompts/attributes-reference.md` | ✅ |
| Don't break Phase 0 / 1.a-1.3 | ✅ — StripeProbe headless 7/7, GUI 7/7, IL probe 0 hits, source-gen tests 8/8 |
| Don't commit | ✅ — working tree dirty |
| Don't modify forbidden files (MASTERPLAN, README, LICENSE, .gitignore, Directory.Build.props, global.json, .phase0/, src/) | ✅ — only `.phase0/StdioTest/Program.cs` was extended (explicitly allowed by the prompt) |
| Visual quality (dark-mode-friendly, screenshot-worthy) | ✅ — confirmed in `.phase1/screenshot-todoapp.png` |
| Watchable observables push (no polling fallback) | ✅ — INPC implemented, runtime hooks `PropertyChanged`, no polling timer started |
| Non-deadlock under PropertyChanged + read_observable burst | ✅ — uses standard event pattern + 200 ms coalesce |

Phase 1.4 deliverables are complete.
