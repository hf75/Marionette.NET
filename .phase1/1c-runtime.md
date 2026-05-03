# Phase 1.2 (1c) — Runtime: real MCP host + four tools + watchable resources

**Status:** PASS
**Date:** 2026-05-03
**SDK:** .NET 10.0.202 · ModelContextProtocol 1.2.0 · Roslyn 4.14.0

## Goal & verdict

Turn `Marionette.NET.Runtime` from "stub host with marionette_ping" into the
real MCP host: discover the source-generator-emitted manifest, expose the
four Phase-1 tools, expose watchable observables as MCP resources with
`resources/subscribe` + `notifications/resources/updated`, install the
`Ai.Trigger` channel push, and keep the IL-stripping promise from Phase 0
Spike A intact.

**Verdict: GO for Phase 1.3.** All build-matrix steps pass, the IL probe
stays at 0 hits across all four needles in the stripped Release build, the
updated stdio handshake harness passes 7/7 checks, and the source-generator
test suite passes 8/8 (including a new gating test for the
`MCP_ENABLED` off path).

## What was built

### Manifest ownership change — descriptor records now live in Runtime

Phase 1.b emitted `RootDescriptor` / `CallableDescriptor` / `ObservableDescriptor` /
`TriggerableDescriptor` / `ParamDescriptor` inline into every user assembly's
`Marionette.g.cs`. Phase 1.2 moves those records into the runtime assembly
under `Marionette.Runtime.Manifest` (file:
`src/Marionette.NET.Runtime/Manifest/Descriptors.cs`). The source generator's
`Emitter.cs` now (a) emits `using Marionette.Runtime.Manifest;` at the top of
`Marionette.g.cs` and references the descriptors by short name, and (b) does
NOT emit them inline. Runtime consumption is therefore strongly typed against
a single CLR identity instead of duck-typing per-assembly duplicates.

Stripping invariant preserved: emission is gated on the user csproj defining
`MCP_ENABLED` (which `build/Marionette.NET.targets` contributes whenever
`EnableMcpAutomation=true`). When MCP is off, the generator emits nothing —
the user assembly never references `Marionette.Runtime.Manifest` types, so
the IL probe stays at 0. The gate is checked via the generator's
`ParseOptionsProvider`, comparing `CSharpParseOptions.PreprocessorSymbolNames`.

### `IUiAutomationAdapter` + `NoOpAdapter` (in Runtime)

`src/Marionette.NET.Runtime/Adapters/IUiAutomationAdapter.cs` — narrow
contract for Phase 1.2:

```csharp
Task DispatchAsync(Action action, CancellationToken ct);
Task<T> DispatchAsync<T>(Func<T> func, CancellationToken ct);
Task<byte[]> CaptureScreenshotAsync(string? targetName, CancellationToken ct);
Task<object?> ResolveControlAsync(string rootName, string controlName, CancellationToken ct);
```

`NoOpAdapter` runs callbacks inline (good enough for headless / unit tests),
throws `NotSupportedException` on screenshot, and returns `null` from
`ResolveControlAsync`. The `MarionetteHost` falls back to `NoOpAdapter`
when no adapter is passed.

### Runtime services

| Service | File | Lifetime | Responsibility |
|---|---|---|---|
| `ManifestRegistry` | `Manifest/ManifestRegistry.cs` | singleton | Holds the descriptor list keyed by manifest name; calls each `RootDescriptor.Create` factory once at startup; captures factory errors so a single bad root doesn't kill the process. Exposes `Find(name)` and `BindInstance(name, instance)`. |
| `LoopProtectionService` | `Loop/LoopProtectionService.cs` | singleton | Process-global hop counter. `TryEnterHop` (used by `invoke_method`) returns `(Hops, Exceeded)`; `RecordChannelHop` (used by `Ai.Trigger`) just increments. 30-second decay window resets the counter when activity stops. Default `MaxDepth=5`, env override via `MARIONETTE_MAX_DEPTH`. |
| `ChannelEmitter` | `Channel/ChannelEmitter.cs` | singleton | `IAsyncDisposable`. On `Bind(McpServer)` installs `Ai.TriggerHook` and `Ai.ScheduleTriggerHook` (internal fields, populated via `InternalsVisibleTo`). Each call increments the loop counter, builds a `JsonObject` payload (`{prompt, hops, scheduledFor?}`), and sends `notifications/marionette/channel` via `McpServer.SendNotificationAsync`. `ScheduleTrigger` uses one-shot `Timer` instances. On dispose, cancels pending timers and un-installs the hooks. |
| `WatchableResourceProvider` | `Resources/WatchableResourceProvider.cs` | singleton | Builds a catalog of every `[McpObservable(Watchable=true)]` keyed by URI (`marionette://<root>/<prop>`). Implements `List()`, `ReadAsync()`, `Subscribe()`, `Unsubscribe()`. Subscriptions prefer `INotifyPropertyChanged`; otherwise poll at `PollingIntervalMs`. Updates within a 200 ms window per resource collapse to one `notifications/resources/updated` push. |

### The four MCP tools — `Tools/MarionetteTools.cs`

Sealed class `MarionetteTools` registered via `WithTools<MarionetteTools>()` (the
AOT-friendly path per PHASE0_FINDINGS implication 6). All four methods are
`static`. The class has a private ctor; the SDK never instantiates it.

| Tool | Shape |
|---|---|
| `inspect_app_api(rootName?)` | Returns JSON manifest. Without `rootName`, returns the array of all roots. With `rootName`, returns that single root's shape (or `{success:false, errorCode:"unknown_root", available:[…]}`). Each entry has `name, typeName, instanceAvailable, callables[], observables[], triggerables[]`. Watchable observables include `resourceUri`. |
| `invoke_method(root, method, args?)` | Increments loop counter via `TryEnterHop`. On exceed → `{success:false, errorCode:"loop_limit_exceeded", hops}`. Marshals `args` (a `JsonElement`) into the `IReadOnlyDictionary<string, object?>` the generator's lambda expects, dispatching through `IUiAutomationAdapter.DispatchAsync` unless `OffUiThread=true` (then `Task.Run`). Awaits Tasks/ValueTasks for async callables. Honours per-method `TimeoutSeconds`. Returns the JSON-serialised result, or a structured error object. |
| `read_observable(root, property)` | Resolves the observable, dispatches `Read(instance)` to the UI thread, JSON-serialises the value. |
| `capture_screenshot(target?)` | Delegates to `IUiAutomationAdapter.CaptureScreenshotAsync(target, ct)`. Returns an MCP `ImageContentBlock` with `mimeType="image/png"`. On `NotSupportedException` (NoOpAdapter), returns `IsError=true` with a structured `{success:false, errorCode:"screenshot_not_supported", message:"..."}` text block. |

### `MarionetteHost.RunAsync` — the new composition root

`src/Marionette.NET.Runtime/MarionetteHost.cs` replaces the Spike-C stub. Its
shape:

```csharp
public static async Task<int> RunAsync(
    string[] args,
    IReadOnlyList<RootDescriptor> roots,
    IUiAutomationAdapter? adapter = null,
    CancellationToken ct = default);
```

Steps:
1. Parse `--mcp` / `--headless` / `--mcp-help`. Without `--mcp`/`--mcp-help`,
   returns `0` immediately (caller's GUI bootstrap runs).
2. `--mcp-help` writes the manifest summary to stderr and exits.
3. Install `StdoutGuardWriter` on `Console.Out` BEFORE any other code emits
   bytes. The guard counts violations and forwards a one-line warning to
   stderr (PHASE0_FINDINGS implication 6: this stays permanent in Phase 1).
4. Build via `Host.CreateEmptyApplicationBuilder` (NOT `CreateApplicationBuilder`
   — the latter wires a stdout `Console` logger that corrupts the JSON-RPC
   stream).
5. Register: `IUiAutomationAdapter` (caller-supplied or `NoOpAdapter`),
   `ManifestRegistry` (built from `roots`), `LoopProtectionService`,
   `ChannelEmitter`, `WatchableResourceProvider`.
6. `AddMcpServer` with `Capabilities.Resources.Subscribe = true`,
   `WithStdioServerTransport`, `WithTools<MarionetteTools>`, plus four
   resource handlers (list/read/subscribe/unsubscribe) that forward into
   `WatchableResourceProvider`.
7. After `host.Build`, pull `McpServer` from DI and `Bind` it to both the
   `ChannelEmitter` and the `WatchableResourceProvider`.
8. `await host.RunAsync(ct)` until stdin EOF.
9. On finally: dispose the channel emitter and resource provider (un-installs
   `Ai` hooks, cancels pending timers, drops watchers); print final stdout
   leak count if non-zero.

### Adapter.Wpf — Phase 1.2 stub

`src/Marionette.NET.Adapter.Wpf/WpfMarionetteBootstrap.cs` now contains a
`WpfUiAutomationAdapter : IUiAutomationAdapter` whose method bodies fall
through (Phase 1.3 will implement them: `Application.Current.Dispatcher.InvokeAsync`,
`RenderTargetBitmap` + `PngBitmapEncoder`, visual-tree `FindByName`). The
old static `WpfMarionetteBootstrap.Initialize(Application)` is replaced with
a `CreateAdapter(Application?)` factory (kept as a non-trivial symbol so the
IL probe still has something to look at in non-stripped builds). The
sample's `App.xaml.cs` no longer calls `Initialize` — adapter wiring now
happens via `MarionetteHost.RunAsync`'s `adapter:` parameter.

### Sample wiring — Phase 1.2 intermediate state

`samples/Sample.Wpf.StripeProbe/Program.cs` now calls
`MarionetteHost.RunAsync(args, GeneratedManifest.Roots, adapter: null)` for
both the `--mcp --headless` and the GUI `--mcp` paths. Phase 1.3 will pass
the WPF adapter for the GUI path. **Known intermediate state**: the GUI
`--mcp` path uses `NoOpAdapter` until Phase 1.3 lands, so
`capture_screenshot` returns `screenshot_not_supported` and any
UI-touching `[McpCallable]` would run inline on the calling thread instead
of the WPF Dispatcher. The sample's `Add` method is pure math and works
correctly via either path.

### StdioTest harness — updated assertions

`.phase0/StdioTest/Program.cs` now exercises the Phase 1.2 contract:

| Check | Expectation |
|---|---|
| `initialize` handshake | server `Marionette.NET 0.0.1`, protocol `2025-11-25` |
| `tools/list` | contains `inspect_app_api`, `invoke_method`, `read_observable`, `capture_screenshot` |
| `inspect_app_api` (no args) | result text contains `MainWindow` |
| `invoke_method MainWindow.Add(2, 3)` | result text is `5` |
| `read_observable MainWindow.Result` | result text parses as integer (sample's GUI mutates Result; in headless mode the harness only asserts the call shape, not a specific value) |
| `capture_screenshot` | result has `IsError=true` and content text contains `screenshot_not_supported` (NoOpAdapter path) |
| stdout purity | every line parses as JSON-RPC; 0 pollution lines |
| clean exit | child exits with 0 on stdin EOF |

## Build matrix results

All runs from `C:\Home\Code\nw.Automation`, .NET 10.0.202, after a clean
of every `bin`/`obj` (preserving `.phase0/StdioTest/bin` and
`.phase0/ProbeIl/bin`).

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build Marionette.NET.sln -c Debug` | PASS — 0 warnings, 0 errors |
| 2 | `dotnet build Marionette.NET.sln -c Release` | PASS — 0 warnings, 0 errors |
| 3 | `dotnet test tests/Marionette.NET.SourceGenerator.Tests/...csproj -c Debug` | PASS — 8/8 (1 snapshot + 5 rejection + 1 positive control + 1 new MCP-disabled gating) |
| 4 | `dotnet build samples/Sample.Wpf.StripeProbe/...csproj -c Release -p:EnableMcpAutomation=false` | PASS — stripped output, 7 files |
| 5 | `dotnet build samples/Sample.Wpf.StripeProbe/...csproj -c Debug -p:EnableMcpAutomation=true` | PASS — MCP-on output, 42 files |
| 6 | IL probe over cmd 4 output | PASS — 0 hits across all 4 needles |
| 7 | `dotnet .phase0/StdioTest/.../StdioTest.dll <Sample.exe>` | PASS — 7/7 checks, 6 JSON-RPC frames, 0 pollution lines |

### IL probe (cmd 6)

```
[PASS] Marionette.NET.Runtime: TOTAL hits across 1 file(s): 0
[PASS] Adapter.Wpf:            TOTAL hits across 1 file(s): 0
[PASS] Marionette.Ai:          TOTAL hits across 1 file(s): 0
[PASS] ModelContextProtocol:   TOTAL hits across 1 file(s): 0
PASS — stripped build contains zero forbidden symbols.
```

Stripped build still 7 files (identical to Phase 1.a/1.b baseline):
`Marionette.NET.Abstractions.dll`, `Marionette.NET.Abstractions.pdb`,
`Sample.Wpf.StripeProbe.deps.json`, `Sample.Wpf.StripeProbe.dll`,
`Sample.Wpf.StripeProbe.exe`, `Sample.Wpf.StripeProbe.pdb`,
`Sample.Wpf.StripeProbe.runtimeconfig.json`. **No `Marionette.g.cs` is
generated** in the stripped build — the source-generator's `MCP_ENABLED`
gate prevents emission.

### StdioTest output (cmd 7)

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

stderr lines are all SDK-internal informational logs from
`ModelContextProtocol.Server.{StdioServerTransport,McpServer}` —
no Marionette code wrote to either stream.

## Files changed / added

```
src/Marionette.NET.Runtime/
  Manifest/Descriptors.cs              (NEW — RootDescriptor / CallableDescriptor / ParamDescriptor / ObservableDescriptor / TriggerableDescriptor)
  Manifest/ManifestRegistry.cs         (NEW)
  Adapters/IUiAutomationAdapter.cs     (NEW)
  Adapters/NoOpAdapter.cs              (NEW)
  Loop/LoopProtectionService.cs        (NEW)
  Channel/ChannelEmitter.cs            (NEW)
  Resources/WatchableResourceProvider.cs (NEW)
  Tools/MarionetteTools.cs             (NEW — the four Phase-1 tools)
  MarionetteHost.cs                    (rewritten; StdoutGuardWriter + StderrLoggerProvider preserved)
  PingTool.cs                          (DELETED — Spike-C smoke-test tool obsoleted)

src/Marionette.NET.SourceGenerator/
  Emitter.cs                           (UPDATED — emit `using Marionette.Runtime.Manifest;`, drop inline descriptor record emission)
  ManifestGenerator.cs                 (UPDATED — gate emission on MCP_ENABLED via ParseOptionsProvider)

src/Marionette.NET.Adapter.Wpf/
  WpfMarionetteBootstrap.cs            (UPDATED — WpfUiAutomationAdapter stub satisfying IUiAutomationAdapter; CreateAdapter(Application?) replaces old Initialize)

samples/Sample.Wpf.StripeProbe/
  Program.cs                           (UPDATED — uses new MarionetteHost.RunAsync signature)
  App.xaml.cs                          (UPDATED — removed Spike-C Initialize call)

tests/Marionette.NET.SourceGenerator.Tests/
  Marionette.NET.SourceGenerator.Tests.csproj  (UPDATED — added Marionette.NET.Runtime ProjectReference)
  GeneratorRunner.cs                   (UPDATED — wires MCP_ENABLED through CSharpParseOptions; references Runtime for descriptor types in test compilation)
  DiagnosticTests.cs                   (UPDATED — added McpDisabled_EmitsNoSource_ButReplaysDiagnostics)
  Snapshots/GoldenInput_EmitsExpectedManifest.verified.txt  (UPDATED — drops inline descriptor records, adds `using Marionette.Runtime.Manifest;`)

.phase0/StdioTest/Program.cs           (UPDATED — Phase-1.2 harness assertions)

.phase1/1c-runtime.md                  (NEW — this report)
```

Files deliberately not touched: `MASTERPLAN.md`, `README.md`, `LICENSE`,
`.gitignore`, `Directory.Build.props`, `global.json`, `build/Marionette.NET.props`,
`build/Marionette.NET.targets`, `build/Run-IlProbe.ps1`, all of `.phase0/spike-*`,
all of `src/Marionette.NET.Abstractions/*` source code (csproj untouched),
`samples/Sample.Wpf.StripeProbe/{MainWindow.xaml,MainWindow.xaml.cs,App.xaml,Sample.Wpf.StripeProbe.csproj}`.

## Architectural decisions

### Why move the descriptors into Runtime rather than a third "Marionette.NET.Manifest" library

Adding a fourth assembly costs an extra DLL in the MCP-on build, an extra
NuGet to ship in Phase 7, and provides no behavioural benefit — Runtime is
already referenced by every adapter and is the only consumer of the
descriptor types in v1. Co-locating them in Runtime (under the
`Marionette.Runtime.Manifest` namespace) keeps the dependency graph flat:

```
Abstractions (attributes)
   ▲
   │
   └── SourceGenerator (analyzer; emits using Marionette.Runtime.Manifest;)
   │
Runtime (descriptors + host + tools + services)
   ▲
   │
Adapter.{Framework} (implements IUiAutomationAdapter)
   ▲
   │
User assembly (references Abstractions always; Adapter.Wpf only when EnableMcpAutomation=true)
```

Stripping invariant: when MCP is off, the user's csproj's conditional
ProjectReference doesn't pull in Adapter.Wpf, which doesn't pull in Runtime,
and the source generator's `MCP_ENABLED` gate prevents it from emitting
`Marionette.g.cs`. The user assembly is thus identical in structure to a
Marionette-free build except for the (always-present) Abstractions
attribute markup.

### Why `LoopProtectionService` is process-global, not per-root

A chain like `Claude → Root.A.invoke → Ai.Trigger from Root.A → Claude → Root.B.invoke → Ai.Trigger from Root.B` is a real loop, even though Root.A and Root.B never share a counter. Per-root counting would let the loop hide between roots. The service tracks one (`hops`, `lastActivityUtc`) tuple; the 30-second decay window is enough to tolerate slow conversational flows.

### Why the resource subscription baseline is read inline (no dispatch) on first subscribe

Inside `Subscribe()`, the call is on the SDK's request thread which (in
adapter-installed scenarios) might already be the UI thread; a redundant
`DispatchAsync` would deadlock. Subsequent change-detection reads (in
`MaybePushUpdatedAsync`) DO dispatch — they're called from the
PropertyChanged handler or the polling timer, neither of which is on the UI
thread.

### Why we keep `[Conditional("MCP_ENABLED")]` on `Ai.Trigger` instead of moving the bridge to the runtime

Phase 1.a put the `Ai.TriggerHook` field inside `Marionette.NET.Abstractions`
with `InternalsVisibleTo("Marionette.NET.Runtime")`. The hooks become
unreachable in stripped builds (because every call site is elided by
`[Conditional]`), so trim removes the field. Verified: the IL probe finds 0
hits for `Marionette.Ai`. We do not move the hook population into Runtime
because that would force the user assembly to take a hard reference on
Runtime to populate the hook — exactly what Phase 0 said to avoid.

## Issues encountered

1. **`WithTools<T>` rejects static classes.** The C# generic constraint
   forbids static type arguments. Solution: make `MarionetteTools` a sealed
   non-static class with a private ctor; methods stay static. The SDK's
   reflection over the type works either way.

2. **`ImageContentBlock.Data` is `ReadOnlyMemory<byte>` (the base64 UTF-8
   bytes), not `string`.** The intuitive `Data = Convert.ToBase64String(...)`
   does not compile. Solution: use the static factory
   `ImageContentBlock.FromBytes(bytes, mimeType)`, which encodes lazily and
   sets the required `Data` field.

3. **The MCP SDK's reflective parameter marshaller treats nullable reference
   types without a default value as required.** `string? target` without
   `= null` triggered `ArgumentException: "missing a value for the required
   parameter 'target'"` at `tools/call` time. Solution: every nullable tool
   parameter that is genuinely optional gets `= null`. Same applies to
   `JsonElement? args`.

4. **Generator's pipeline cache stability.** Adding `MCP_ENABLED` as a fourth
   incremental input required rewiring the `Combine` chain to produce
   `(ManifestModel, bool McpEnabled)` instead of bare `ManifestModel`. The
   incremental cache is preserved because both arms of the tuple are
   equatable.

5. **`pwsh` not on PATH in the bash sandbox.** Same issue Phase 1.a/1.b hit;
   ran the IL probe through the dedicated PowerShell tool with the
   dot-source `&` operator. `Run-IlProbe.ps1` itself unchanged.

## Hand-off to Phase 1.3 (Adapter.Wpf)

Phase 1.3 implements `WpfUiAutomationAdapter : IUiAutomationAdapter` for
real:

| Method | Phase 1.3 implementation |
|---|---|
| `DispatchAsync(Action, ct)` | `Application.Current.Dispatcher.InvokeAsync(action, DispatcherPriority.Normal, ct).Task`. Honour cancellation before scheduling. |
| `DispatchAsync<T>(Func<T>, ct)` | Same, returning the dispatched function's result. |
| `CaptureScreenshotAsync(target, ct)` | `RenderTargetBitmap` against the target's `Visual` (or `Application.Current.MainWindow` when target is null), encode as PNG via `PngBitmapEncoder`. Return the byte stream. |
| `ResolveControlAsync(rootName, controlName, ct)` | Use the `ManifestRegistry`'s root instance, walk the visual tree (`LogicalTreeHelper.FindLogicalNode` or `VisualTreeHelper`-walk), match by `AutomationProperties.AutomationId` then by `x:Name` (`Window.FindName`). |

Integration points Phase 1.3 will need:

* The Sample's `Program.cs` GUI-`--mcp` branch needs to construct the WPF
  `App` first, then pass `WpfMarionetteBootstrap.CreateAdapter(app)` (or
  whatever Phase 1.3's helper renames to) into `MarionetteHost.RunAsync`.
  Phase 1.3 will also need to call `ManifestRegistry.BindInstance("MainWindow", app.MainWindow)`
  once the window has loaded so that `read_observable` and `invoke_method`
  see the live, bound instance instead of the registry's own
  `new MainWindow()` factory result.

* The `Capabilities.Resources.Subscribe = true` advertisement in
  `MarionetteHost` is already wired; Phase 1.3 only needs to make sure the
  WPF adapter dispatches the resource-read getter onto the UI thread (the
  current `WatchableResourceProvider` already does this — Phase 1.3 doesn't
  need to change the provider, just provide a dispatcher that actually
  marshals).

* The Phase 1.2 `WpfUiAutomationAdapter` constructor takes an
  `Application?`. Phase 1.3 may want to add an overload that takes an
  `IDispatcher` abstraction so unit tests can fake the dispatcher; that
  refinement is optional and not on the Phase 1.3 critical path.

## Known intermediate states (call-out for Phase 1.3)

* **GUI `--mcp` mode uses `NoOpAdapter` until Phase 1.3 lands.** The
  `Program.cs` GUI branch currently passes `adapter: null`. Phase 1.3 fixes
  this by passing the Wpf adapter and binding `MainWindow` into the
  registry once it's available.

* **`capture_screenshot` returns `screenshot_not_supported` end-to-end in
  Phase 1.2** because both the headless and the GUI paths use NoOpAdapter
  + `WpfUiAutomationAdapter`'s placeholder `CaptureScreenshotAsync` throws
  `NotSupportedException`. The harness asserts this exact behaviour as the
  Phase 1.2 contract; Phase 1.3 should update the harness to expect a real
  PNG-encoded image block instead.

* **`read_observable` of `MainWindow.Result` returns `0` in headless mode**
  because the sample's `Result` is only mutated by the GUI's button click
  handler (the `Add` `[McpCallable]` is pure math and does not touch
  `Result`). The harness asserts the call shape, not the value. Phase 1.3's
  WPF adapter does not change this — adopters are expected to wire their
  callables to the same state changes the GUI exercises if they want
  meaningful headless reads.

* **The `WpfMarionetteBootstrap` static class is kept as a compatibility
  shim** with a `CreateAdapter` factory. Phase 1.3 can rename / restructure
  it freely; the only consumer is the conditional ProjectReference in
  `Sample.Wpf.StripeProbe.csproj`, and the sample itself doesn't reference
  the type today.

* **The MCP SDK's argument marshalling is reflective and may not be
  AOT-clean** for the Phase 1.2 `MarionetteTools` methods. PHASE0_FINDINGS
  noted this in the AOT report (Spike B). For now, Phase 1.2 stays in JIT
  territory; Phase 5's AOT-hardening pass will move tool registration to
  the source-emitted typed bridge if needed.
