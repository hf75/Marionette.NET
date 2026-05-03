# Phase 2.2 (2b) — Dynamic per-method tools + idempotent tool identity

**Status:** PASS
**Date:** 2026-05-03
**SDK:** .NET 10.0.202 · ModelContextProtocol 1.2.0 · Roslyn 4.14.0

## Goal & verdict

Phase 2.2 ships the masterplan promise of per-method dynamic MCP tools
alongside the four Phase-1 meta-tools, with deterministic
idempotent tool identity:

1. `ToolIdentity` — pure helper that turns `(rootName, CallableDescriptor)`
   into a stable `<rootName>.<methodName>` tool name plus a SHA-256
   fingerprint over the canonical signature. Description-only changes
   leave the hash unchanged (Spielregel 5: tool-cache idempotence).
2. `DynamicToolRegistry` — singleton that registers ONE `McpServerTool`
   per discovered callable into the SDK's `McpServerOptions.ToolCollection`
   primitive collection. Closes over `(rootName, callable)` per tool;
   dispatch goes through the new shared `MarionetteDispatch` pipeline
   so meta-tool and dynamic-tool surfaces share loop-protection,
   UI-thread routing, async unwrapping, and structured-error shaping.
3. Per-method input schemas are pre-computed at compile time by the
   source generator's `JsonSchemaWriter.WriteParametersSchema`, embedded
   into `CallableDescriptor.ParametersJsonSchema`, and stamped onto
   `tool.ProtocolTool.InputSchema` at registration. Runtime never walks
   ITypeSymbols (Phase-1 tenet preserved).
4. `notifications/tools/list_changed` is auto-emitted by the SDK on
   `McpServerPrimitiveCollection<McpServerTool>` mutations; the registry
   ALSO sends one explicit notification after a `RefreshFromManifest`
   diff, so the path is robust regardless of which mechanism the host
   ultimately settles on.

**Verdict: GO for Phase 2.3** (final findings + commit). All build
matrix items pass, the IL probe stays at 0 hits across all 5 needles
on all 3 stripped samples, every existing eval-case (1 through 6) and
the new EC-7 pass, the source-gen tests jumped from 13 to 25 (overload
+ ToolIdentity + parameter-schema coverage), and all three stdio
harnesses verify both the meta-tool AND the dynamic-tool dispatch
paths.

## A. ToolIdentity design

`src/Marionette.NET.Runtime/Tools/ToolIdentity.cs`. Pure static helper
with three methods:

```csharp
public static string ComputeToolName(string rootName, CallableDescriptor callable);
public static string ComputeStableHash(string rootName, CallableDescriptor callable);
public static string BuildCanonicalSignature(string rootName, CallableDescriptor callable);
public static IReadOnlyList<(string Name, string Hash)> DisambiguateOverloads(
    IReadOnlyList<(string BaseName, string Hash)> entries);
```

### Canonical hash string

UTF-8 bytes of:

```
<rootName>\n<methodName>\n<param0Name>:<param0Type>\n<param1Name>:<param1Type>\n...
```

(no trailing newline; if zero parameters the body ends at
`<methodName>`). Fed into SHA-256, lower-case hex.

### Worked example for AddTodo

`AddTodo(string title)` on root `TodoListViewModel`:

| Field | Value |
|---|---|
| Tool name | `TodoListViewModel.AddTodo` |
| Canonical signature | `TodoListViewModel\nAddTodo\ntitle:string` |
| Stable hash (8-hex prefix) | derived per build (deterministic) |

### Idempotence proof

Unit test `ComputeStableHash_IgnoresDescriptionChange` instantiates two
`CallableDescriptor` with **different descriptions** but the same
signature; the hashes are byte-equal. Conversely,
`ComputeStableHash_ChangesOnSignatureChange` verifies that adding a
parameter, renaming a parameter, OR changing a parameter's type all
produce different hashes.

### Naming policy

- Default: `<rootName>.<methodName>` — root casing preserved from
  manifest name; method casing preserved as declared in C#.
- Overload disambiguation: when two callables in the same root would
  collapse to the same default name (a manifest emits two methods
  named `Add` with different signatures), the registry suffixes the
  second-and-following with `_<8-hex>` derived from the full hash.
  The suffix is stable per signature so a rebuild yields the same
  name. Unit tests `DisambiguateOverloads_*` cover this.

## B. DynamicToolRegistry design

`src/Marionette.NET.Runtime/Tools/DynamicToolRegistry.cs`. Singleton
service with two entry points:

### Startup registration flow

`RegisterInitial(McpServer server)`:

1. Pulls `server.ServerOptions.ToolCollection` (typed
   `McpServerPrimitiveCollection<McpServerTool>`). Lazily creates the
   collection if the SDK left it null.
2. Walks `ManifestRegistry.Roots`. For each `(root, callable)` pair:
   - Compute the base tool name and stable hash via `ToolIdentity`.
   - Run the result through `ToolIdentity.DisambiguateOverloads` so
     overload collisions get a stable suffix.
3. For each disambiguated entry, build an `McpServerTool` via
   `McpServerTool.Create((Delegate)handler, options)`:
   - Handler is a closure capturing `(rootName, callable, adapter,
     loopGuard, manifest, logger)`. Signature:
     `(RequestContext<CallToolRequestParams> ctx, CancellationToken ct)
        => ValueTask<CallToolResult>`. The SDK auto-binds the
     `RequestContext` (see SDK 1.2.0 docs) and DOES NOT include it in
     the input schema.
   - Inside, the handler reads `ctx.Params.Arguments` (the raw
     argument dict from the LLM), packs it into a single
     `JsonElement` object, and calls
     `MarionetteDispatch.InvokeAsync(...)`. The dispatch path is
     identical to the `invoke_method` meta-tool's path — same
     loop-protection, UI-thread routing, timeout, async unwrap,
     structured error shaping.
   - The handler returns `CallToolResult` with the dispatch's JSON
     output as a `TextContentBlock`. Structured errors are surfaced
     with `IsError=true` so the client treats them as failures.
4. Override `tool.ProtocolTool.InputSchema = JsonDocument.Parse(callable.ParametersJsonSchema).RootElement.Clone()`.
   The SDK's auto-derived default (just `{"type":"object"}`) is
   replaced with the source-gen-emitted per-method schema.
5. `collection.TryAdd(tool)` — fails (logs warning) only on name
   collision with an already-registered tool. Successful adds populate
   `_registered[name] = stableHash` for future diff.

The initial registration is invoked in `MarionetteHost.RunAsync` AFTER
the host is built but BEFORE the run loop starts. Spielregel 7
guarantee: dynamic tools exist by the very first `tools/list` response.

### Diff-and-mutate algorithm (RefreshFromManifestAsync)

For Phase 5+ hot-plug roots; not exercised in Phase 2.2 but designed
as a one-line call:

```
 1. Recompute (toolName, stableHash) entries from current manifest.
 2. For each (oldName, oldHash) NOT in new set:
      collection.Remove(...); _registered.Remove(name); dirty=true.
 3. For each (newName, newHash) in new set:
      a) if name absent       → create + Add → dirty=true
      b) if name present, hash unchanged → no-op (IDEMPOTENCE)
      c) if name present, hash changed   → Remove old + Add new → dirty=true
 4. If dirty: send notifications/tools/list_changed (the SDK's collection
    fires Changed automatically on mutation; we ALSO send manually for
    robustness — duplicate notifications are idempotent client-side).
```

Per-tool storage is `Dictionary<string toolName, string stableHash>` so
the diff is O(N).

### Where the McpServerPrimitiveCollection is mutated

`McpServer.ServerOptions.ToolCollection` — the typed
`McpServerPrimitiveCollection<McpServerTool>` exposed by SDK 1.2.0.
The collection has `TryAdd(T)`, `Remove(T)`, and a `Changed` event.
We never bypass the collection; every add/remove goes through it so
the SDK's default Changed handler can trigger automatic
list-changed notifications.

## C. SDK 1.2.0 dynamic-tool API wiring

| Concern | Resolution |
|---|---|
| Factory | `McpServerTool.Create(Delegate, McpServerToolCreateOptions)` — accepts an arbitrary delegate; the SDK introspects the delegate signature for parameter binding. |
| Per-tool name | `McpServerToolCreateOptions.Name = computedToolName`. |
| Per-tool description | `McpServerToolCreateOptions.Description = callable.Description`. |
| Closure capture | The delegate is a closure; it captures `(rootName, callable, ...)` per tool. The SDK invokes the captured delegate on every `tools/call`. |
| Argument shape | Delegate signature is `(RequestContext<CallToolRequestParams>, CancellationToken)`. The SDK auto-binds `RequestContext<>` (per SDK 1.2.0 XML doc: "can be injected as parameters into McpServerTool"). The handler reads `ctx.Params.Arguments` directly. No reflection on user method signatures. |
| Input schema | `tool.ProtocolTool.InputSchema = JsonDocument.Parse(callable.ParametersJsonSchema).RootElement.Clone()`. The SDK's default (derived from the delegate signature) is `{"type":"object"}` — we replace it with the rich per-method schema. |
| `tools/list_changed` notification | Auto-emitted by the SDK when `McpServerPrimitiveCollection<>` mutates. We additionally call `server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, ...)` after a diff in `RefreshFromManifestAsync` for explicit robustness. (Initial registration does NOT manually send; the SDK's auto-emit + the fact that initial registration runs BEFORE the run loop means clients see the per-method tools in the very first `tools/list` response anyway.) |
| Reflection cost | One `JsonDocument.Parse` per tool to clone the schema; no `MakeGenericMethod`, no `MethodInfo.Invoke`. The SDK does its own reflection on the delegate's signature, but the delegate is always the same shape (RequestContext + CancellationToken), so the reflection result is small and cached by the SDK. |

The brief asked us to consider falling back / downgrading if the SDK
factory was unclean: it is clean. Spend was ~25 minutes of XML doc
reading + 10 minutes prototyping; full speed ahead path.

## D. Coexistence with meta-tools

The four Phase-1 meta-tools (`inspect_app_api`, `invoke_method`,
`read_observable`, `capture_screenshot`) stay registered alongside the
per-method dynamic tools. Adopters using Claude Code CLI see both:

- Discovery via `inspect_app_api()` is still available.
- Direct call via `TodoListViewModel.AddTodo({"title": "buy milk"})`
  is now also available.

`invoke_method` and the dynamic tool path are idempotent: both go
through `MarionetteDispatch.InvokeAsync` so loop-protection counts
hops the same way, the UI-thread dispatch is identical, and structured
errors are shaped consistently. EC-7's bonus assertion exercises both
paths and confirms `TotalCount` reflects both calls.

## E. ParametersJsonSchema source-gen addition

### Pipeline change

`src/Marionette.NET.SourceGenerator/Validator.cs` collects
`(name, ITypeSymbol, isRequired)` tuples per parameter alongside the
existing `ParameterModel` build, then computes
`JsonSchemaWriter.WriteParametersSchema(...)` once. The string flows
through the equatable `CallableModel` record into the emitter.

`src/Marionette.NET.SourceGenerator/JsonSchemaWriter.cs` adds a new
public method:

```csharp
public static string WriteParametersSchema(
    IEnumerable<(string Name, ITypeSymbol Type, bool IsRequired)> parameters);
```

Output shape:

```json
{
  "type": "object",
  "properties": { "name1": <schema>, "name2": <schema>, ... },
  "required": ["a", "b"]
}
```

(`required` omitted when empty; zero-parameter methods produce
`{"type":"object","properties":{}}`.) Re-uses the existing event-args
walker for primitives, enums, arrays, INPC records, depth/cycle
guards.

### Snapshot diff excerpt

`Snapshots/GoldenInput_EmitsExpectedManifest.verified.txt` (Calculator
with `Add(int a, int b)`):

```diff
                     Parameters: new ParamDescriptor[]
                     {
                         new ParamDescriptor(Name: "a", ClrTypeName: "int", ...),
                         new ParamDescriptor(Name: "b", ClrTypeName: "int", ...),
                     },
+                    ParametersJsonSchema: "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"integer\"},\"b\":{\"type\":\"integer\"}},\"required\":[\"a\",\"b\"]}",
                     Invoke: static (instance, args) =>
                     {
                         ...
```

A new dedicated snapshot
`GoldenParametersSchema_EmitsExpectedManifest.verified.txt` exercises:

- Required scalars + optional with default (`bool flag = true`)
- Enum + array params
- Zero-parameter method

### Why source-gen, not runtime

Phase 1 tenet (Source Generators over runtime Reflection) explicitly
forbids walking ITypeSymbols at runtime. Computing the schema at
compile time keeps the runtime AOT-clean and trim-safe. The only
runtime work is `JsonDocument.Parse(callable.ParametersJsonSchema)`
once per dynamic tool at startup.

## F. Build matrix results

All commands run from `C:\Home\Code\nw.Automation` on .NET 10.0.202
after a clean of every `bin`/`obj`.

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build Marionette.NET.sln -c Debug` | PASS — 0 warnings, 0 errors (10 projects) |
| 2 | `dotnet build Marionette.NET.sln -c Release` | PASS — 0 warnings, 0 errors |
| 3 | `dotnet test tests/Marionette.NET.SourceGenerator.Tests/...csproj -c Debug --no-build` | PASS — 25/25 (was 13: +1 GoldenOverloads, +1 GoldenParametersSchema, +10 ToolIdentityTests) |
| 4 | `dotnet test tests/Marionette.NET.Integration/...csproj -c Debug --no-build` | PASS — 7/7 (EC-1..EC-7; was 6) |
| 5 | `dotnet build samples/Sample.Wpf.StripeProbe/...csproj -c Release -p:EnableMcpAutomation=false` | PASS — stripped output |
| 6 | `dotnet build samples/Sample.Wpf.TodoApp/...csproj -c Release -p:EnableMcpAutomation=false` | PASS — stripped output |
| 7 | `dotnet build samples/Sample.Avalonia.Dashboard/...csproj -c Release -p:EnableMcpAutomation=false` | PASS — stripped output |
| 8 | IL probe over StripeProbe stripped DLL (5 needles) | PASS — 0 hits across all 5 needles |
| 9 | IL probe over TodoApp stripped DLL (5 needles) | PASS — 0 hits across all 5 needles |
| 10 | IL probe over Avalonia Dashboard stripped DLL (5 needles) | PASS — 0 hits across all 5 needles |
| 11 | StdioTest TodoApp `--todoapp` headless | PASS — 12/12 (was 10; +2 dynamic-tool checks) |
| 12 | StdioTest StripeProbe (default) | PASS — 9/9 (was 7; +2 dynamic-tool checks) |
| 13 | StdioTest Avalonia `--avalonia` | PASS — 14/14 (was 12; +2 dynamic-tool checks) |
| 14 | `pwsh .phase1/demo.ps1 -NoBuild` | PASS — 12/12 |

## G. Stdio handshake outputs (excerpts)

### TodoApp (`--todoapp`) — selected lines showing the dynamic-tool path

```
PASS - tools/list contains all four Phase-1 tools (got: capture_screenshot,inspect_app_api,invoke_method,read_observable)
PASS - tools/list also contains the 5 per-method dynamic tools (TodoListViewModel.AddTodo,TodoListViewModel.RemoveTodo,TodoListViewModel.ToggleDone,TodoListViewModel.ClearCompleted,TodoListViewModel.RenameTodo)
PASS - inspect_app_api returned TodoListViewModel manifest with all 5 callables + 4 observables
PASS - read_observable TotalCount initially returned 0
PASS - invoke_method AddTodo("buy milk") succeeded
PASS - read_observable TotalCount returned 1 after AddTodo (baseline + 1)
PASS - resources/subscribe + AddTodo produced notifications/resources/updated for marionette://TodoListViewModel/TotalCount
PASS - [via dynamic tool] TodoListViewModel.AddTodo({title="via dynamic tool"}) succeeded; TotalCount 2 -> 3
PASS - resources/subscribe + AddTodo produced an event notification on marionette://TodoListViewModel/events/TodoAdded (sequence=4, count=4, args.Title="learn marionette" present)
PASS - capture_screenshot surfaced a structured 'screenshot_not_supported' error (NoOpAdapter)
PASS - child exited cleanly with code 0
stdout summary: 22 JSON-RPC frames, 0 pollution lines
=== Phase 1.4 TodoApp handshake: PASS ===
```

### StripeProbe — comparing the two paths

```
PASS - [via meta-tool] invoke_method MainWindow.Add(2,3) returned 5
PASS - [via dynamic tool] MainWindow.Add({a:2,b:3}) returned 5
```

Both paths return `5`; the dispatch pipeline is shared.

### Avalonia Dashboard (`--avalonia`)

```
PASS - tools/list also contains the 5 per-method dynamic tools (DashboardViewModel.UpsertMetric,DashboardViewModel.RemoveMetric,DashboardViewModel.ResetAll,DashboardViewModel.TogglePaused,DashboardViewModel.RefreshAsync)
...
PASS - [via dynamic tool] DashboardViewModel.UpsertMetric({name="DynamicProbe"}) succeeded; MetricCount 5 -> 6
```

### Captured `tools/list` content for TodoListViewModel.AddTodo

```json
{
  "name": "TodoListViewModel.AddTodo",
  "description": "Add a new TODO with the given title; appends to the end of the list.",
  "inputSchema": {
    "type": "object",
    "properties": { "title": { "type": "string" } },
    "required": ["title"]
  }
}
```

The input schema matches the source-gen-emitted
`ParametersJsonSchema` for `AddTodo(string title)` — required string,
no optional fields.

## H. Files changed in Phase 2.2

```
src/Marionette.NET.Runtime/
  Tools/ToolIdentity.cs               (NEW)
  Tools/DynamicToolRegistry.cs        (NEW)
  Tools/MarionetteDispatch.cs         (NEW — shared pipeline; meta-tool + dynamic-tool path)
  Tools/MarionetteTools.cs            (UPDATED — InvokeMethodAsync now thin wrapper over MarionetteDispatch)
  Manifest/Descriptors.cs             (UPDATED — added ParametersJsonSchema to CallableDescriptor)
  MarionetteHost.cs                   (UPDATED — register DynamicToolRegistry, advertise tools.listChanged, RegisterInitial(server) before run loop)

src/Marionette.NET.SourceGenerator/
  JsonSchemaWriter.cs                 (UPDATED — added WriteParametersSchema)
  Model/ManifestModel.cs              (UPDATED — added ParametersJsonSchema field)
  Validator.cs                        (UPDATED — collect schema inputs + emit schema string)
  Emitter.cs                          (UPDATED — emit ParametersJsonSchema literal)

tests/Marionette.NET.SourceGenerator.Tests/
  ToolIdentityTests.cs                (NEW — 10 unit tests)
  SnapshotTests.cs                    (UPDATED — added GoldenOverloads + GoldenParametersSchema)
  Snapshots/GoldenInput_EmitsExpectedManifest.verified.txt        (UPDATED — schema field added to each callable)
  Snapshots/GoldenEventInput_EmitsExpectedManifest.verified.txt   (UPDATED — schema field added)
  Snapshots/GoldenParametersSchema_EmitsExpectedManifest.verified.txt (NEW)

tests/Marionette.NET.Integration/
  EvalCases.cs                        (UPDATED — added EC-7 dynamic per-method tools test)
  TodoAppFixture.cs                   (UPDATED — added CallToolAsync helper for dynamic tool calls)

.phase0/StdioTest/
  Program.cs                          (UPDATED — dynamic-tool list assertion + dynamic-tool call helper + per-mode dynamic-tool calls)

.phase1/demo.ps1                      (UPDATED — verdict text reflects 12 checks)

.phase2/2b-dynamic-tools.md           (NEW — this report)
```

Files deliberately NOT touched (per Phase 2.2 constraint set):
- `MASTERPLAN.md`, `LICENSE`, `.gitignore`, `Directory.Build.props`,
  `global.json`, `PHASE0_FINDINGS.md`, `PHASE1_FINDINGS.md`,
  `README.md`.
- `samples/Sample.Wpf.StripeProbe/`,
  `src/Marionette.NET.Adapter.Wpf/`,
  `src/Marionette.NET.Adapter.Avalonia/`,
  `src/Marionette.NET.Abstractions/` source code.

## I. Trapdoor verifications

| Trapdoor | Mitigation | Verification |
|---|---|---|
| SDK 1.2.0 dynamic-tool factory might not exist | Found `McpServerTool.Create(Delegate, McpServerToolCreateOptions)` in 1.2.0 (confirmed via XML doc). | Three stdio harnesses now demonstrate per-method tools registered + dispatched end-to-end. |
| Hash must change ONLY on signature changes | Canonical signature includes only rootName + methodName + ordered parameter (name, type) pairs. Description, body, ordering of unrelated callables not included. | Unit test `ComputeStableHash_IgnoresDescriptionChange` (description-only change → same hash); `ComputeStableHash_ChangesOnSignatureChange` (each of param-add, param-rename, param-type-change → different hash). |
| Overload disambiguation must collide-free | `DisambiguateOverloads` keeps first occurrence as bare name; subsequent get `_<8-hex>` suffix derived from full hash. | Unit tests `DisambiguateOverloads_AppendsHexSuffixOnCollision` + `DisambiguateOverloads_StableAcrossRuns`. Snapshot test `GoldenOverloads_EmitsBothCallables` confirms the source generator emits both overloaded methods (with distinct ParametersJsonSchemas), and the runtime's disambiguation path applies after that. |
| `tools/list_changed` not seen by client | Dynamic tools are registered BEFORE the run loop starts, so they're in the very first `tools/list` response — no notification needed at startup. The SDK's `McpServerPrimitiveCollection` Changed event auto-emits notifications on subsequent mutations; the registry ALSO sends manually for explicit robustness. | Stdio harness sees per-method tools in `tools/list` immediately after handshake. Stdio capture confirms early `notifications/tools/list_changed` frames during startup (auto-emitted by the SDK as we add tools to the collection). |
| stdout pollution | All host logs route through `StderrLoggerProvider` (Phase 1.2 `StdoutGuardWriter` + `StderrLoggerProvider` permanent fixtures). Dynamic tool registration uses `ILogger<DynamicToolRegistry>` only. | EC-5 still passes (stdout pure, 0 pollution); harness reports `0 pollution lines` for all three samples. |
| AOT-clean | `MarionetteDispatch` reuses the descriptor's typed `Invoke` lambda; `JsonDocument.Parse` is AOT-friendly; the SDK does its own reflection but only on a fixed delegate signature shape. | `dotnet build -c Release` clean (0 warnings); IL probe at 0 hits across 5 needles on all 3 stripped samples — stripping invariant preserved. |

## J. Phase-2.3 hand-off

Phase 2.2 closed the masterplan's Phase-2 work scope:

- Phase 2.1: Avalonia adapter + Dashboard sample (landed).
- Phase 2.2: Per-method dynamic tools + idempotent tool identity (this
  report).

Phase 2.3 scope is the closing-out: consolidated Phase-2 findings in
`PHASE2_FINDINGS.md` and the final commit. Both Avalonia and dynamic
tools are stable, their stripping invariants verified, and the
adopter-facing surface (per-method tools showing up in `tools/list`
with rich input schemas) is what the masterplan called for.

Recommendation for the next agent: read this document + `2a-adapter-avalonia.md`,
write the consolidated findings, and propose a commit message covering
both 2.1 and 2.2 changes (working tree is currently dirty per the
constraint).

## Architectural decisions

### Why parse + clone the schema for each tool

The cleanest path is to replace the SDK-derived default
`{"type":"object"}` schema with our rich per-method schema after
`McpServerTool.Create`. `JsonDocument.Parse` is AOT-friendly, and the
parse happens once at startup per tool — not on every invocation.

We considered emitting `JsonElement` literals from the source
generator instead of strings; the runtime cost is the same, and the
descriptor record stays simple (one string field) without inviting
ITypeSymbol references back into runtime.

### Why a singleton registry rather than re-registering each request

`McpServerTool` instances are stable; the closure captures only
constants. Re-registering per request would add latency and break the
SDK's primitive collection invariants (the collection assumes stable
instance identity for `Changed` semantics). One registration at
startup matches both the masterplan's "manifest-frozen-on-startup"
tenet and the SDK's collection model.

### Why a shared `MarionetteDispatch` rather than calling `invoke_method` internally

The dynamic-tool handler could have just packaged its closure-captured
identifiers into a synthetic `invoke_method` call. That would have
worked but added a layer of indirection — Loop protection would still
fire, but error messages would mention `invoke_method` rather than the
actual method, and the call stack would be one frame deeper. Hoisting
the dispatch into a shared helper keeps both surfaces honest about
what they're really invoking.

### Why we still send a manual `notifications/tools/list_changed`

The SDK's `McpServerPrimitiveCollection` raises `Changed` on
mutations, and the SDK's default handler emits the notification. But
the contract is implicit (the docs don't spell out the auto-emit
guarantee), and notifications are idempotent client-side (they're
just "refresh your cache" hints). Sending a redundant manual
notification after our diff costs nothing and protects against future
SDK changes.

## Issues encountered

1. **`McpServerTool.Create(Delegate, options)` was not the obvious
   first overload.** The XML doc was clear enough, and the API shape
   is generally clean once you know the SDK auto-binds
   `RequestContext<>` (per the parameter-binding rules). Spent ~25
   minutes confirming the contract; no reflection-emit needed.

2. **Three pre-handshake `tools/list_changed` notifications.**
   Adding tools one-at-a-time during initial registration causes the
   SDK's primitive collection to raise Changed on each `TryAdd`. The
   notifications fire BEFORE the client sends `notifications/initialized`,
   so they're effectively ignored by the transport (the client hasn't
   subscribed yet). Functionally clean; cosmetically noisy. Could be
   suppressed by batching or by registering before `Bind`. Phase 2.3
   may want to clean this up.

3. **`internal` ToolIdentity helpers needed `public` visibility for
   tests.** `BuildCanonicalSignature` and `NormalizeClrTypeName` were
   originally `internal`. Promoted to `public` so the test project
   (different assembly) can exercise them. Marked with "Public for
   tests" in the doc comment so the API surface intent is clear.

4. **Snapshot files needed careful updating.** Adding
   `ParametersJsonSchema` to every callable shifted both verified
   files. The schema strings are escaped (every `"` becomes `\"`)
   inside the C# string literal, which makes diffs noisy. Verified
   files updated and re-confirmed by running the snapshot tests.

5. **The `RequestContext<CallToolRequestParams>` parameter binding
   wasn't documented as a "special parameter" the SDK auto-binds, but
   it IS — confirmed by the XML doc note "can be injected as
   parameters into McpServerTool". Also confirmed empirically:
   handlers using this signature get the live request context with
   `Params.Arguments` populated.

## Status against original Phase 2.2 prompt

| Prompt requirement | Status |
|---|---|
| Pure ToolIdentity helper at src/Marionette.NET.Runtime/Tools/ToolIdentity.cs | DONE |
| ComputeToolName + ComputeStableHash with 8-hex-char overload suffix | DONE |
| SHA-256 over canonical signature string | DONE |
| Naming policy: lowercase NOT applied (preserve root casing per masterplan/manifest) | DONE — `<rootName>.<methodName>` preserves both casings; "lowercase(rootName)" was a relaxation in the original brief. |
| Idempotence: description change → same hash; signature change → different hash | DONE — unit tests cover both directions |
| DynamicToolRegistry at src/Marionette.NET.Runtime/Tools/DynamicToolRegistry.cs | DONE |
| Startup registration + diff-and-mutate algorithm + idempotence cache | DONE — RegisterInitial + RefreshFromManifestAsync |
| `tools/list_changed` notification | DONE — auto-emitted by SDK + manual emit on diff |
| Use SDK's tool registration API for dynamic tools | DONE — McpServerTool.Create(Delegate, options) |
| Coexist with meta-tools | DONE — Phase 1 four tools still registered; per-method tools alongside |
| Source generator emits ParametersJsonSchema | DONE — option (ii) per the brief |
| StdioTest harness extended with dynamic-tool checks | DONE — both `--todoapp` and `--avalonia` modes |
| EC-7 added | DONE — registers + dispatches + parity check with invoke_method |
| All Phase 1/2.1 invariants preserved | DONE — IL probe 0/0/0/0/0 across 5 needles on 3 samples; 6/6 → 7/7 integration; 13/13 → 25/25 source-gen |
| Stdio handshakes pass for all three samples | DONE |
| Demo.ps1 still passes | DONE — 12/12 |
| Don't commit | DONE — working tree dirty |

Phase 2.2 deliverables are complete.
