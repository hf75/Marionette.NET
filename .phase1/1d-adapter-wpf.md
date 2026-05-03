# Phase 1.3 (1d) — Adapter.Wpf: real WPF `IUiAutomationAdapter`

**Status:** PASS
**Date:** 2026-05-03
**SDK:** .NET 10.0.202 · ModelContextProtocol 1.2.0 · Roslyn 4.14.0

## Goal & verdict

Replace the Phase 1.2 stub of `Marionette.NET.Adapter.Wpf` with a production
`IUiAutomationAdapter` implementation:

* `DispatchAsync(Action / Func<T>, ct)` → `Application.Current.Dispatcher.InvokeAsync`
  (asynchronous; never the synchronous `Invoke` that can deadlock).
* `CaptureScreenshotAsync(target?, ct)` → `RenderTargetBitmap` +
  `PngBitmapEncoder`, with DPI-correct math (`VisualTreeHelper.GetDpi`).
* `ResolveControlAsync(rootName, controlName, ct)` → walk every open
  `Application.Windows`, matching `AutomationProperties.AutomationId` first
  and `FrameworkElement.Name` (x:Name) as fallback.
* `MarionetteWpf.AttachTo(app, roots, args)` — one-line wiring from
  `App.OnStartup` that builds the adapter, starts `MarionetteHost.RunAsync`
  on a background `Task`, and hooks `Application.Exit` for clean shutdown.

**Verdict: GO.** All eight build-matrix steps pass, the IL probe stays at 0
hits across all four needles in the stripped Release build, the headless
stdio harness still passes 7/7 checks, and the new GUI harness passes 6/6
checks including a valid PNG screenshot of the live `MainWindow`.

## What was built

### `WpfUiAutomationAdapter` (Phase 1.3 production impl)

`src/Marionette.NET.Adapter.Wpf/WpfUiAutomationAdapter.cs` — replaces the
Phase 1.2 fall-through stub.

| Method | Phase 1.3 impl |
|---|---|
| `DispatchAsync(Action, ct)` | Inline path when `Dispatcher.CheckAccess()` is true (avoids a redundant queue round-trip when the caller is already on the UI thread, e.g. first watchable resource read). Otherwise `Dispatcher.InvokeAsync(action, DispatcherPriority.Normal, ct)`. Logs exceptions at `Warning`. |
| `DispatchAsync<T>(Func<T>, ct)` | Same shape as the action variant; returns the dispatched function's result. |
| `CaptureScreenshotAsync(target?, ct)` | Wraps the actual capture in `DispatchAsync<T>` so it always runs on the UI thread. Inside the dispatch: resolve the target element (`MainWindow` → first active Window → first Window for `target == null`; `VisualTreeFinder.FindByName` for a named target), compute pixel dimensions via `VisualTreeHelper.GetDpi`, render via `RenderTargetBitmap` + `Pbgra32`, encode with `PngBitmapEncoder` to a `MemoryStream`, return the bytes. Throws a descriptive `InvalidOperationException` for zero-sized elements (not yet laid out) or missing windows. |
| `ResolveControlAsync(rootName, controlName, ct)` | Dispatches `VisualTreeFinder.FindByName(_app, controlName, _log)` to the UI thread. `rootName` is an unused future-disambiguation hint per the Phase 1.3 spec. |

The adapter takes `Application` and `ILogger<WpfUiAutomationAdapter>`. Every
dispatch / resolve / capture path emits a `Debug` or `Warning` log; resolution
misses log at `Information` with the candidate name list (truncated at 32
candidates to keep the line readable).

### `Internal/VisualTreeFinder` — named-element resolver

`src/Marionette.NET.Adapter.Wpf/Internal/VisualTreeFinder.cs` is a single
`internal static` helper:

* Walks every currently-open `Window` in the supplied `Application`.
* For each window: matches the window itself, then walks the **logical tree**
  via `LogicalTreeHelper.GetChildren` (preferred — more stable than visual
  tree for adopter-named elements; doesn't include implementation visuals).
* Falls back to the **visual tree** via `VisualTreeHelper.GetChild` for any
  named element that doesn't surface in the logical tree (e.g. items inside
  a custom-templated control).
* Iterative DFS via `Stack<DependencyObject>` (no recursion — no stack
  blow-up on deep trees, and easier to extend with cancellation later).
* Match precedence: `AutomationProperties.AutomationId` first, then
  `FrameworkElement.Name`. Both compared with `StringComparison.Ordinal`.
* Logs every candidate considered (`Name|AutomationId@TypeName`) on a miss
  so the LLM / adopter can see what names are actually in scope.

### `MarionetteWpf.AttachTo` — one-line bootstrap

`src/Marionette.NET.Adapter.Wpf/MarionetteWpf.cs` is the GUI-mode entry
point per the Phase 1.3 contract:

```csharp
public static IDisposable AttachTo(
    Application app,
    IReadOnlyList<RootDescriptor> roots,
    string[]? args = null,
    ILoggerFactory? loggerFactory = null);
```

The call:

1. Constructs a `WpfUiAutomationAdapter(app, logger)`.
2. **Rewrites every `RootDescriptor.Create` factory** so it dispatches
   through the WPF UI thread AND prefers a live
   `Application.MainWindow` (when its CLR `FullName` matches the descriptor's
   `TypeName`). This solves two intertwined problems simultaneously:
   * **STA-thread.** WPF window ctors require an STA thread;
     `MarionetteHost.RunAsync` is on a background `Task.Run`, so calling
     `new MainWindow()` from the runtime's
     `ManifestRegistry` ctor on that thread fails with "The calling thread
     must be an STA thread, as this is required for many UI components."
   * **Instance affinity.** Even if we constructed a fresh `MainWindow` on
     the UI thread, it would be a different object than
     `Application.MainWindow`. The user clicks the live window and mutates
     `Result`; without binding, `read_observable` would always see the
     other instance and return zero. Preferring the live window when types
     match makes `read_observable` reflect the user-facing state.
   * The bg thread blocks on `Dispatcher.Invoke` only for the brief
     factory call (sub-ms when `app.MainWindow` is already loaded). Never
     on the UI thread itself, so no deadlock.
3. Spawns `MarionetteHost.RunAsync(args, bridgedRoots, adapter, ct)` on a
   background `Task` — UI thread is never blocked.
4. Hooks `Application.Exit` to dispose the returned attachment, which in
   turn cancels the host `CancellationTokenSource` and best-effort waits
   (max 2 s) for the host task. The handler also unsubscribes itself.
5. Returns a custom `IDisposable` so adopters who prefer explicit lifetime
   control (e.g. integration tests) can detach early.

`args` defaults to `Environment.GetCommandLineArgs()[1..]` so adopters who
don't propagate `e.Args` from `Program.Main` still work; `loggerFactory`
defaults to `NullLoggerFactory.Instance`.

### `WpfMarionetteBootstrap` — compatibility shim

`src/Marionette.NET.Adapter.Wpf/WpfMarionetteBootstrap.cs` was reduced to the
Phase 1.2 `CreateAdapter(Application, ILogger?)` factory the hand-off
documented as the public seam. Adopters who want manual host-lifecycle
control (for tests) can still call this; the new
`MarionetteWpf.AttachTo` is the recommended path. Keeping the type also gives
the IL probe a stable top-level symbol to detect adapter regressions in
stripped builds.

### Sample wiring update

* `samples/Sample.Wpf.StripeProbe/App.xaml.cs` — `OnStartup` now (under
  `#if MCP_ENABLED`) calls `MarionetteWpf.AttachTo(this, GeneratedManifest.Roots, e.Args)`.
* `samples/Sample.Wpf.StripeProbe/Program.cs` — the GUI `--mcp` branch no
  longer starts the host directly; it falls through to `RunGui()` and lets
  `App.OnStartup` do the wiring. The `--mcp --headless` and `--mcp-help`
  branches still call `MarionetteHost.RunAsync` directly with `adapter:null`
  (NoOpAdapter) — correct for the headless contract.

### StdioTest harness — Phase 1.3 `--gui` mode

`.phase0/StdioTest/Program.cs` was extended:

* New `--gui` flag spawns the child with `--mcp` (no `--headless`) and uses
  the Phase 1.2 handshake plus an additional **PNG-validation step**:
  * `content[0].type == "image"`
  * `content[0].mimeType == "image/png"`
  * `content[0].data` is non-empty base64
  * Decoded bytes start with the PNG magic header
    `89 50 4E 47 0D 0A 1A 0A`.
* On success, the captured image is saved to `.phase1/screenshot-test.png`
  (resolved by walking up to find a `.phase1/` directory). This file is a
  test artifact, not committed; the user can delete it after each run.
* Initialize timeout extended to 20 s in `--gui` mode (WPF Dispatcher
  startup adds latency that the headless path doesn't have).
* Kept Phase 1.2 behaviour: in headless mode the harness asserts the
  documented `screenshot_not_supported` structured error, and in `--gui`
  mode it force-kills the child after the assertions because
  `Application.Run` keeps the process alive (Spike C lesson).

## Build matrix results

All commands run from `C:\Home\Code\nw.Automation`. `bin`/`obj` cleaned for
`src/`, `samples/`, `tests/` before the run; `.phase0/StdioTest/bin` and
`.phase0/ProbeIl/bin` were preserved.

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build Marionette.NET.sln -c Debug` | PASS — 0 warnings, 0 errors |
| 2 | `dotnet build Marionette.NET.sln -c Release` | PASS — 0 warnings, 0 errors |
| 3 | `dotnet test tests/Marionette.NET.SourceGenerator.Tests/...csproj -c Debug` | PASS — 8/8 |
| 4 | `dotnet build samples/Sample.Wpf.StripeProbe/...csproj -c Release -p:EnableMcpAutomation=false` | PASS — 7 files (matches Phase 1.2 baseline) |
| 5 | `dotnet build samples/Sample.Wpf.StripeProbe/...csproj -c Debug -p:EnableMcpAutomation=true` | PASS — 42 files (matches Phase 1.2 baseline) |
| 6 | `pwsh build/Run-IlProbe.ps1 …` over cmd 4 output | PASS — 0 hits across all 4 needles |
| 7 | `dotnet .phase0/StdioTest/.../StdioTest.dll <Sample.exe>` (headless) | PASS — 7/7 checks, 6 JSON-RPC frames, 0 pollution lines |
| 8 | `dotnet .phase0/StdioTest/.../StdioTest.dll <Sample.exe> --gui` | PASS — 6/6 checks, valid PNG screenshot, 6 JSON-RPC frames, 0 pollution lines |

### IL probe (cmd 6)

```
[PASS] Marionette.NET.Runtime: TOTAL hits across 1 file(s): 0
[PASS] Adapter.Wpf:            TOTAL hits across 1 file(s): 0
[PASS] Marionette.Ai:          TOTAL hits across 1 file(s): 0
[PASS] ModelContextProtocol:   TOTAL hits across 1 file(s): 0
PASS — stripped build contains zero forbidden symbols.
```

The stripped build is still seven files, identical to the Phase 1.2 baseline
(`Marionette.NET.Abstractions.dll/.pdb`, `Sample.Wpf.StripeProbe.deps.json/.dll/.exe/.pdb/.runtimeconfig.json`).
No `Marionette.g.cs` is generated in the stripped build — the
source-generator's `MCP_ENABLED` gate prevents emission.

### Headless harness output (cmd 7)

```
=== Phase 1.2 stdio handshake harness ===
PASS - initialize handshake (server: Marionette.NET 0.0.1, protocol 2025-11-25)
PASS - tools/list contains all four Phase-1 tools (got: read_observable,capture_screenshot,inspect_app_api,invoke_method)
PASS - inspect_app_api returned manifest containing MainWindow
PASS - invoke_method MainWindow.Add(2,3) returned 5
PASS - read_observable MainWindow.Result returned 0
PASS - capture_screenshot surfaced a structured 'screenshot_not_supported' error (NoOpAdapter)
PASS - child exited cleanly with code 0
stdout summary: 6 JSON-RPC frames, 0 pollution lines
stderr total: 20 lines
=== Phase 1.2 handshake: PASS ===
```

### GUI harness output (cmd 8)

```
=== Phase 1.3 stdio + GUI screenshot harness ===
PASS - initialize handshake (server: Marionette.NET 0.0.1, protocol 2025-11-25)
PASS - tools/list contains all four Phase-1 tools (got: read_observable,capture_screenshot,inspect_app_api,invoke_method)
PASS - inspect_app_api returned manifest containing MainWindow
PASS - invoke_method MainWindow.Add(2,3) returned 5
PASS - read_observable MainWindow.Result returned 0
PASS - capture_screenshot returned a valid PNG (2358 bytes, mimeType=image/png). Saved to C:\Home\Code\nw.Automation\.phase1\screenshot-test.png.
INFO - GUI-mode child still alive after MCP shutdown (expected; killing).
stdout summary: 6 JSON-RPC frames, 0 pollution lines
stderr total: 20 lines
=== Phase 1.3 GUI handshake: PASS ===
```

`stderr` lines in both modes are SDK-internal informational logs from
`ModelContextProtocol.Server.{StdioServerTransport,McpServer}` — no
Marionette code wrote to either stream.

## Screenshot bytes summary

The captured PNG from cmd 8:

| Property | Value |
|---|---|
| Size on disk | 2 358 bytes |
| MIME type (as declared in MCP `ImageContentBlock`) | `image/png` |
| File magic | `89 50 4E 47 0D 0A 1A 0A` (canonical PNG) |
| Image dimensions | 400 × 200 px |
| Color | 8-bit/color RGBA (Pbgra32 → PNG RGBA), non-interlaced |
| Visual content | StripeProbe MainWindow as rendered: "Add 2 + 3" Button + "Result = (none)" TextBlock |

(The 400×200 dimensions match the StripeProbe `MainWindow.xaml`
`Height="200" Width="400"` exactly. The harness verified the file via
`HasPngMagic` before saving; an external `file` check confirms 8-bit RGBA
non-interlaced PNG.)

The captured image was visually inspected and shows the live WPF window's
content correctly. Saved file is `.phase1/screenshot-test.png` — a
test-only artifact, regenerated each `--gui` run; it is intentionally not
tracked (the constraint set forbade touching `.gitignore`, but the file is
small enough that adopters can ignore or delete it manually).

## Files changed / added

```
src/Marionette.NET.Adapter.Wpf/
  WpfUiAutomationAdapter.cs               (NEW — production IUiAutomationAdapter impl)
  MarionetteWpf.cs                        (NEW — AttachTo bootstrap)
  Internal/VisualTreeFinder.cs            (NEW — named-element resolver)
  WpfMarionetteBootstrap.cs               (UPDATED — slimmed to compatibility shim around CreateAdapter)

samples/Sample.Wpf.StripeProbe/
  App.xaml.cs                             (UPDATED — OnStartup calls MarionetteWpf.AttachTo under #if MCP_ENABLED)
  Program.cs                              (UPDATED — GUI --mcp path no longer starts host; falls through to RunGui())

.phase0/StdioTest/
  Program.cs                              (UPDATED — --gui mode with PNG-validation assertions)

.phase1/
  1d-adapter-wpf.md                       (NEW — this report)
  screenshot-test.png                     (NEW, regenerated each --gui run; test-only artifact)
```

Files deliberately not touched (per the Phase 1.3 constraint set):
`MASTERPLAN.md`, `README.md`, `LICENSE`, `.gitignore`, `Directory.Build.props`,
`global.json`, all of `src/Marionette.NET.Abstractions/`,
`src/Marionette.NET.Runtime/`, `src/Marionette.NET.SourceGenerator/`,
`samples/Sample.Wpf.StripeProbe/{MainWindow.xaml,MainWindow.xaml.cs,App.xaml,Sample.Wpf.StripeProbe.csproj}`,
`build/Marionette.NET.props`, `build/Marionette.NET.targets`,
`build/Run-IlProbe.ps1`, all of `.phase0/spike-*`,
`.phase0/ProbeIl/*`, `tests/Marionette.NET.SourceGenerator.Tests/*`.

## Architectural decisions

### Why `MarionetteWpf.AttachTo` rewrites the descriptor factories

The Phase 1.2 hand-off flagged that `read_observable` should see the live
`Application.MainWindow` rather than the registry's auto-created instance.
Two facts forced the descriptor-factory rewrite over a post-hoc
`ManifestRegistry.BindInstance` call:

* The Phase 1.3 constraint set forbade modifying `Marionette.NET.Runtime`,
  so the `ManifestRegistry` instance is reachable only via DI inside
  `MarionetteHost.RunAsync` — `MarionetteWpf` cannot get to it from outside.
* When the host's `ManifestRegistry` ctor runs the descriptor factory on a
  bg `Task.Run` thread, `new MainWindow()` fails immediately with the
  STA-thread error. The registry captures this in `CreateError` and leaves
  `Instance = null`, so even *attempting* to bind later runs against a
  stale `null`.

Rewriting `RootDescriptor.Create` before passing roots into the host (with
a closure that dispatches via `app.Dispatcher.Invoke` and returns
`app.MainWindow` when type-compatible) plugs both holes in one move and
keeps the runtime contract unchanged. The bg thread blocks on
`Dispatcher.Invoke` only for the duration of the factory call;
`app.MainWindow` is already loaded by the time `OnStartup` returns and
the host begins processing tools, so the wait is sub-millisecond in
practice.

### Why `DispatchAsync` short-circuits when already on the UI thread

`WatchableResourceProvider.Subscribe()` reads the baseline value on the
SDK's request thread. In adapter-installed scenarios that thread can already
be the UI thread (the SDK's resource handlers are wired in
`MarionetteHost.RunAsync`'s `WithSubscribeToResourcesHandler` lambda, which
runs synchronously off the calling request). Routing those reads through
`InvokeAsync` would queue them and complete asynchronously, which works,
but it adds a needless dispatcher round-trip. The `Dispatcher.CheckAccess()`
short-circuit matches how Avalonia's `Dispatcher.UIThread.CheckAccess` /
WinUI's `DispatcherQueue.HasThreadAccess` are intended to be used in their
own adapters; codifying the pattern here keeps cross-framework adapter
behaviour consistent.

### Why the visual-tree walk is iterative-DFS, not recursive

A `Stack<DependencyObject>` walk costs the same in the common case but
keeps the stack budget bounded on pathological adopter UIs (e.g. deeply
nested `ItemsControl` templates). It also makes future extensions cheap:
a `BFS` mode for cancellation, a stop-after-first-match log, or a
`yield return` over candidates for an `EnumerateNamedElements` API can
all be slotted in without touching the call sites. Phase 2's Avalonia
adapter will mirror this shape.

### Why `WpfMarionetteBootstrap.CreateAdapter` was kept

Two reasons. First, the Phase 1.2 hand-off documented `CreateAdapter` as
the public seam adopters might already reference. Second, integration tests
generally want manual host-lifecycle control (no `Application.Exit` hook,
explicit cancellation) and dropping a `CreateAdapter` call directly into
`MarionetteHost.RunAsync` is the simplest such path. The shim is a
two-line wrapper around `new WpfUiAutomationAdapter(...)` and costs nothing.

## Issues encountered

1. **`Application.Exit` uses the legacy `ExitEventHandler` delegate**, not
   `EventHandler<ExitEventArgs>`. Initial draft of `MarionetteWpf.AttachTo`
   used the generic delegate; the C# compiler rejected the implicit
   conversion. Fixed by typing the local as `ExitEventHandler?`.

2. **GUI-mode `invoke_method` and `read_observable` initially failed with
   `root_unavailable`** because the registry's auto-`new MainWindow()` ran
   on the bg `Task.Run` thread (non-STA). Resolved by the descriptor-factory
   rewrite described in the architecture section above. After the fix the
   GUI harness passes 6/6 checks (one less than headless because the
   "child exited cleanly" check is replaced by "force-killed after
   shutdown" — Application.Run keeps the process alive after stdin EOF).

3. **The `.gitignore` constraint vs. the screenshot-output convention.**
   The Phase 1.3 brief asked for the screenshot to be saved to
   `.phase1/screenshot-test.png` and noted "gitignored, of course," but
   the constraint set forbade touching `.gitignore`. Resolution: the
   harness writes the file as requested; it is a small (≈2 KB), regenerated
   test artifact that adopters can delete after each run. Phase 1.4 (or
   any subsequent phase that touches `.gitignore`) can add the line.

4. **Initialize timeout in GUI mode.** The first GUI run timed out within
   the 10-second window the headless harness uses. WPF's first-render path
   (Application ctor, OnStartup, MainWindow ctor + InitializeComponent +
   first layout pass) adds a few seconds. Bumped the GUI initialize
   timeout to 20 s; subsequent tool-call timeouts stay at 10 / 15 s.

## Hand-off to Phase 1.4

Phase 1.4 owns:
* `samples/Sample.Wpf.TodoApp` — the headline Phase-1 demo app.
* `skill-pack/` v1 — `marionette-explore`, `marionette-test`,
  `marionette-decorate` skills + system-prompts + showcase conversations.

**Nothing from Phase 1.3 needs API surface beyond what's already public:**

* `MarionetteWpf.AttachTo(app, roots, args)` — the one-line wiring point.
* `WpfMarionetteBootstrap.CreateAdapter(app, logger?)` — the manual shim
  for tests / advanced wiring.
* `WpfUiAutomationAdapter` — visible by name (the source generator's
  manifest never references it; only `App.OnStartup` does).

Phase 1.4 does NOT need to retouch `IUiAutomationAdapter` or the runtime
host; the Phase 1.3 contract is sufficient for screenshot, observable
reads, and method invocation against any adopter's WPF app.

## Known intermediate states (call-out for Phase 1.4 / 2)

* **`ResolveControlAsync` is wired but not yet exercised end-to-end.** The
  Phase 1 tool surface (`inspect_app_api`, `invoke_method`,
  `read_observable`, `capture_screenshot`) doesn't call
  `ResolveControlAsync` from the runtime tools — that connection lights up
  in Phase 3 alongside `simulate_input` / `raise_event`. The Phase 1.3
  implementation is in place and unit-testable; Phase 3's adapter contract
  expansion will bring it online for live tool calls.

* **`--gui` harness mode requires an interactive desktop session.** WPF
  needs a real desktop to render; CI runners without one (`SYSTEM` /
  unattended sessions) will fail at the `Application` ctor. The headless
  harness path is unaffected and remains the CI default. Phase 7's
  CI-wiring phase will document this limitation in
  `docs/architecture.md`.

* **Mode warm-up time in `--gui`.** The first WPF render adds a few
  seconds of latency before the host starts processing tool calls; the
  harness's 20-second initialize timeout accommodates this on a typical
  workstation. On very slow runners the constant may need bumping.

* **Multi-window support** is intentional Phase 2+ scope. Today
  `Application.MainWindow` is the only window matched by the descriptor
  rewrite; secondary windows would still need
  `ManifestRegistry.BindInstance` (or a Phase 2 extension to the
  factory-rewrite logic). For Phase 1's "single MainWindow per app" pattern
  the current behaviour is sufficient.

* **Adapter logging is wired but the sample doesn't pass an
  `ILoggerFactory`.** `MarionetteWpf.AttachTo` accepts an optional
  `ILoggerFactory` parameter; when omitted (the sample's case), the
  adapter logs into `NullLoggerFactory`. The host's own
  `StderrLoggerProvider` still surfaces `Marionette.Runtime.*` logs.
  Phase 1.4's TodoApp sample is a good place to demonstrate wiring an
  adopter logger factory through to the adapter.
