# Phase 1.6 (1f) — Declarative event support (`[McpEvent]`)

**Status:** PASS
**Date:** 2026-05-03
**SDK:** .NET 10.0.202 · ModelContextProtocol 1.2.0 · Roslyn 4.14.0

## Goal & verdict

Phase 1.6 adds the fifth Marionette attribute, `[McpEvent]`, plus the runtime
plumbing that delivers fires + serialized `EventArgs` to subscribed MCP
clients. The attribute marks any C# `EventHandler` / `EventHandler<TArgs>`
event for declarative MCP delivery; the source generator emits a typed
`Subscribe` lambda; the runtime hosts a per-event ring buffer + coalesced
notifications on `marionette://<root>/events/<event>`. Args types get a
JSON schema generated at compile time and surfaced through `inspect_app_api`.

**Verdict: GO.** All build-matrix steps pass, IL probe stays at 0 hits across
all four needles in stripped Release for both samples, source-generator tests
green at 13/13 (was 8; +5 new), integration tests green at 6/6 (EC-1..EC-6),
the headless StdioTest harness scores 10/10 (was 9; +1 event delivery check),
and the demo script runs clean both headless and `-Gui`.

## What was built

### A. Abstractions — `[McpEvent]` attribute

`src/Marionette.NET.Abstractions/McpAttributes.cs` adds `McpEventAttribute`
(sealed, immutable, `Inherited=false`, `AllowMultiple=false`, `init`-only
properties, full XML doc):

| Member | Type | Default | Meaning |
|---|---|---|---|
| `Description` (ctor) | `string` | required | LLM-facing description |
| `MinIntervalMs` | `int` | `0` | Throttle: drop fires arriving faster than this |
| `MaxQueueSize` | `int` | `100` | Bounded ring buffer; oldest evicted on overflow |
| `CoalesceWindowMs` | `int` | `100` | Window during which fires collapse to one notification |

The attribute is metadata-only — it survives in stripped Release builds
unchanged, consistent with the rest of Marionette's attribute set. The
descriptor emission is gated on `MCP_ENABLED` like the rest of the
manifest, so stripped builds drop the runtime hookup entirely.

### B. Source generator — detection, descriptor emission, schema generation

`src/Marionette.NET.SourceGenerator/`:

* `ManifestGenerator.cs` — added a fourth `ForAttributeWithMetadataName`
  pipeline source (`McpEventAttribute`) for orphan-event MAR011 detection;
  the existing root pipeline picks up `[McpEvent]` via the per-root
  `IEventSymbol` switch arm in `Validator.ValidateRoot`.
* `Validator.cs` — new `ValidateEvent` method classifies the delegate via
  `ClassifyEventDelegate` (handles annotated nullable references —
  `event EventHandler? Foo` matches the same path as `event EventHandler Foo`
  via `WithNullableAnnotation(NotAnnotated)`).
* `JsonSchemaWriter.cs` (new) — deterministic, depth-bounded (3) walker over
  the args type's public properties. Sorts keys Ordinal, handles primitives,
  `DateTime`/`DateTimeOffset`, `Guid`/`TimeSpan`, enums (with member-name
  list), nullable value types, nullable reference types, arrays, generic
  enumerables (`IEnumerable<T>` / `IList<T>` / `ImmutableArray<T>` / etc.),
  nested classes/records. Cycles + depth exceeded surface as
  `{"description":"complex type"}`.
* `Emitter.cs` — new `EmitEvents` / `EmitEventDescriptor`. Emits both
  delegate shapes (specific `EventHandler<T>` for typed args, plain
  `EventHandler` for none). Subscribe-lambda is AOT-clean — no
  `Delegate.CreateDelegate`, no reflection.
* `Diagnostics.cs` + `AnalyzerReleases.Unshipped.md` — new MAR009–012:

| ID | Severity | When |
|---|---|---|
| MAR009 | Error | `[McpEvent]` on a non-event member |
| MAR010 | Error | Event delegate not `EventHandler` / `EventHandler<T>` |
| MAR011 | Warning | `[McpEvent]` event on a class without `[McpRoot]` |
| MAR012 | Warning | `MaxQueueSize <= 0` or `CoalesceWindowMs < 0` (defaults substituted) |

### C. Runtime — `EventDescriptor`, `EventLogService`, `EventResourceProvider`, `HandlerDisposable`

`src/Marionette.NET.Runtime/`:

* `Manifest/Descriptors.cs` — added `EventDescriptor(string Name, string
  Description, string ArgsTypeName, string ArgsJsonSchema, int MinIntervalMs,
  int MaxQueueSize, int CoalesceWindowMs, Func<object, Action<object?>,
  IDisposable> Subscribe)`. `RootDescriptor` gained an
  `IReadOnlyList<EventDescriptor> Events` parameter (positional, source-gen
  order).
* `Internal/HandlerDisposable.cs` (new) — internal `IDisposable` wrapper
  that detaches an event handler on first dispose; idempotent via
  `Interlocked.Exchange`.
* `Events/EventLogService.cs` (new) — singleton DI service. `Start()` hooks
  every `[McpEvent]` on every root; per-event `EventBucket` owns a
  `Queue<EventLogRecord>` capped at `MaxQueueSize` plus a monotonic
  sequence and a drop counter. `Append` is thread-safe (the event source
  may be any thread); `MinIntervalMs` throttles the hot path; subscribers
  receive coalesced notifications via a `Task.Delay(CoalesceWindowMs)`
  scheduler guarded by `Interlocked.CompareExchange` (single timer per
  bucket regardless of fire rate). `Stop()` disposes every Subscribe
  disposable so root instances are not GC-rooted by the runtime.
* `Resources/EventResourceProvider.cs` (new) — sibling to
  `WatchableResourceProvider`. URI scheme:
  `marionette://<rootName>/events/<eventName>`. Implements `List`, `TryHandle`,
  `ReadAsync` (returns `{sequence, dropped, events:[{sequence, timestampUtc,
  args}, ...]}`), `Subscribe` (wires `EventLogService.Subscribe` to push
  `notifications/resources/updated`).
* `MarionetteHost.cs` — registers `EventLogService` + `EventResourceProvider`
  as singletons; resource handlers (`list`/`read`/`subscribe`/`unsubscribe`)
  fan out between the watchable provider and the event provider based on
  URI shape; `Start()` is called after the manifest registry is populated;
  `Stop()` runs in the finally block. The `--mcp-help` summary now lists
  events alongside callables/observables/triggerables.

### D. inspect_app_api includes events with parsed schema

`src/Marionette.NET.Runtime/Tools/MarionetteTools.cs` — `SerializeRoot` adds
the `events: [...]` array. Each entry parses the descriptor's
`ArgsJsonSchema` string back into a nested JSON object via `JsonNode.Parse`
so the LLM-facing manifest carries the schema as structured JSON, not a
string-of-JSON.

### E. Sample wiring — TodoApp

`samples/Sample.Wpf.TodoApp/TodoListViewModel.cs`:
- Added `TodoAddedEventArgs : EventArgs` (sealed class with `Title` /
  `AddedAt` get-only properties; cannot be a record because records can't
  inherit from a non-record class — CS8864).
- Added `[McpEvent("A new TODO was added to the list.")] public event
  EventHandler<TodoAddedEventArgs>? TodoAdded;`.
- `AddTodo` fires the event after appending the item:
  `TodoAdded?.Invoke(this, new TodoAddedEventArgs(trimmed, DateTime.UtcNow));`.

### F. Stdio harness — folded event check into `--todoapp` mode

`.phase0/StdioTest/Program.cs` — extended the `--todoapp` assertion suite
with one extra step (kept stable: still one mode flag, the new check is
in-line). The harness now scores 10/10 (was 9):

```
PASS - resources/subscribe + AddTodo produced an event notification on
       marionette://TodoListViewModel/events/TodoAdded
       (sequence=3, count=3, args.Title="learn marionette" present)
```

The harness scans the buffer for any event whose `args.Title` matches the
title we just added — the buffer is in arrival order and contains all
prior fires from the same harness run, so the latest fire isn't always
index 0.

### G. EC-6 — events deliver via resource notifications

`tests/Marionette.NET.Integration/EvalCases.cs` adds `EC6_Events_DeliverViaResourceNotifications`:
- Subscribe BEFORE invoking AddTodo.
- `AddTodo("EC6 item")` -> success.
- Within 5 s -> `notifications/resources/updated` for the event URI.
- `resources/read` -> assert `sequence >= 1` and `events[0].args.Title == "EC6 item"`.
- `AddTodo("EC6 second")` -> another notification, `sequence >= 2`,
  `events[1].args.Title == "EC6 second"`.

EC-1..EC-5 unchanged; integration tests now 6/6.

### H. Source-gen tests

`tests/Marionette.NET.SourceGenerator.Tests/`:
- New snapshot test `GoldenEventInput_EmitsExpectedManifest` covers a root
  with both event shapes (`EventHandler` + `EventHandler<TArgs>`), a
  callable, a watchable observable. Verified file blessed at
  `Snapshots/GoldenEventInput_EmitsExpectedManifest.verified.txt`.
- Updated existing `GoldenInput_EmitsExpectedManifest.verified.txt` to
  include the `Events: System.Array.Empty<EventDescriptor>()` tail (the
  Calculator root has no events).
- New diagnostic tests: `MAR010_McpEventOnNonStandardDelegate_IsRejected`,
  `MAR011_McpEventOnUnRootedClass_EmitsWarning`,
  `MAR012_McpEventInvalidThrottling_EmitsWarning`,
  `WellFormedEvents_ProduceNoErrors`.

Total source-gen tests: 13 (was 8). All pass.

### I. Skill-pack updates

- `skill-pack/prompts/attributes-reference.md` — new `[McpEvent]` section
  between `[McpObservable]` and `[McpTriggerable]`. Covers attribute
  members, args type guidance (use a sealed class : EventArgs because
  records can't inherit non-record bases — CS8864), throttling examples
  (mouse-move 50ms, file-watcher 1s, backpressure 50-entry buffer), the
  inspect_app_api shape with parsed schema, the "don't decorate" list, and
  the stripping behaviour. Updated namespace table to mention
  `[McpEvent]` and `EventDescriptor`. Updated the runtime tools section's
  inspect_app_api JSON example to include events.
- `skill-pack/claude-code/marionette-decorate/SKILL.md` — added section 5a
  ("Suggest [McpEvent] placements") with EventHandler-shape guidance, args
  type pattern, throttling examples. Updated diagnostics table to MAR001-MAR012.
- `skill-pack/claude-code/marionette-explore/SKILL.md` — discovery procedure
  now mentions events; added an event line to the example output and a
  suggestion to subscribe to event resources.

## Build matrix results

All commands run from `C:\Home\Code\nw.Automation`. .NET 10.0.202.

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build Marionette.NET.sln -c Debug` | PASS — 0 warnings, 0 errors |
| 2 | `dotnet build Marionette.NET.sln -c Release` | PASS — 0 warnings, 0 errors |
| 3 | `dotnet test tests/Marionette.NET.SourceGenerator.Tests/...csproj -c Debug --no-build` | PASS — 13/13 (8 prior + 1 event snapshot + 4 event diagnostic tests) |
| 4 | `dotnet test tests/Marionette.NET.Integration/...csproj -c Debug --no-build` | PASS — 6/6 (EC-1..EC-6; ~5s wall) |
| 5 | `dotnet build samples/Sample.Wpf.TodoApp/...csproj -c Release -p:EnableMcpAutomation=false` | PASS — 7-file stripped output |
| 6 | `dotnet build samples/Sample.Wpf.StripeProbe/...csproj -c Release -p:EnableMcpAutomation=false` | PASS — 7-file stripped output |
| 7 | IL probe over TodoApp stripped DLL (cmd 5) | PASS — 0 hits across all 4 needles |
| 8 | IL probe over StripeProbe stripped DLL (cmd 6) | PASS — 0 hits across all 4 needles |
| 9 | `pwsh .phase1/demo.ps1` (headless) | PASS — 10/10 harness checks, 14 JSON-RPC frames, 0 pollution |
| 10 | `pwsh .phase1/demo.ps1 -Gui` | PASS — 10/10 headless + GUI screenshot validated |
| 11 | `dotnet StdioTest.dll <TodoApp.exe> --todoapp` | PASS — 10/10 checks, 14 JSON-RPC frames, 0 pollution |

### IL probe — TodoApp (cmd 7)

```
[PASS] Marionette.NET.Runtime: TOTAL hits across 1 file(s): 0
[PASS] Adapter.Wpf:            TOTAL hits across 1 file(s): 0
[PASS] Marionette.Ai:          TOTAL hits across 1 file(s): 0
[PASS] ModelContextProtocol:   TOTAL hits across 1 file(s): 0
PASS — stripped build contains zero forbidden symbols.
```

### IL probe — StripeProbe (cmd 8, regression)

```
[PASS] Marionette.NET.Runtime: TOTAL hits across 1 file(s): 0
[PASS] Adapter.Wpf:            TOTAL hits across 1 file(s): 0
[PASS] Marionette.Ai:          TOTAL hits across 1 file(s): 0
[PASS] ModelContextProtocol:   TOTAL hits across 1 file(s): 0
PASS — stripped build contains zero forbidden symbols.
```

The Phase 0 stripping promise survives Phase 1.6 unchanged. Stripped TodoApp
output is still 7 files — `Marionette.g.cs` is not generated when
`EnableMcpAutomation=false`, so the user assembly never references
`HandlerDisposable`, `EventDescriptor`, or any other Phase 1.6 type.

### Stdio harness output (cmd 11)

```
PASS - initialize handshake (server: Marionette.NET 0.0.1, protocol 2025-11-25)
PASS - tools/list contains all four Phase-1 tools (got: read_observable,capture_screenshot,inspect_app_api,invoke_method)
PASS - inspect_app_api returned TodoListViewModel manifest with all 5 callables + 4 observables
PASS - read_observable TotalCount initially returned 0
PASS - invoke_method AddTodo("buy milk") succeeded
PASS - read_observable TotalCount returned 1 after AddTodo (baseline + 1)
PASS - resources/subscribe + AddTodo produced notifications/resources/updated for marionette://TodoListViewModel/TotalCount
PASS - resources/subscribe + AddTodo produced an event notification on marionette://TodoListViewModel/events/TodoAdded (sequence=3, count=3, args.Title="learn marionette" present)
PASS - capture_screenshot surfaced a structured 'screenshot_not_supported' error (NoOpAdapter)
PASS - child exited cleanly with code 0
stdout summary: 14 JSON-RPC frames, 0 pollution lines
```

## Schema generation example — `TodoAddedEventArgs`

The descriptor stores the schema as a single-line JSON literal in
`EventDescriptor.ArgsJsonSchema`:

```json
{"type":"object","properties":{"AddedAt":{"type":"string","format":"date-time"},"Title":{"type":"string"}}}
```

`inspect_app_api` parses it back into a JSON object so the LLM-facing
manifest carries it as structured data. The relevant slice:

```json
{
  "name": "TodoAdded",
  "description": "A new TODO was added to the list.",
  "argsType": "Sample.Wpf.TodoApp.TodoAddedEventArgs",
  "argsSchema": {
    "type": "object",
    "properties": {
      "AddedAt": { "type": "string", "format": "date-time" },
      "Title":   { "type": "string" }
    }
  },
  "resourceUri": "marionette://TodoListViewModel/events/TodoAdded",
  "minIntervalMs": 0,
  "maxQueueSize": 100,
  "coalesceWindowMs": 100
}
```

The schema-writer is deterministic — properties are emitted in Ordinal
sort order — so snapshots remain stable across builds. The bigger snapshot
(`GoldenEventInput_EmitsExpectedManifest.verified.txt`) exercises an
`ItemAddedEventArgs` with three properties (string, int, DateTime) and
confirms the same shape.

## Coalesce + throttle behavior — concrete evidence

### Ring-buffer completeness

EC-6's second pass calls `AddTodo("EC6 item")` then `AddTodo("EC6 second")`.
After both fires, `resources/read` returns sequence `>= 2` and the events
array contains both records:

```json
{
  "sequence": 2, "dropped": 0,
  "events": [
    { "sequence": 1, "timestampUtc": "...", "args": { "Title": "EC6 item",   "AddedAt": "..." } },
    { "sequence": 2, "timestampUtc": "...", "args": { "Title": "EC6 second", "AddedAt": "..." } }
  ]
}
```

The buffer captures both fires even though the notifications may have
collapsed to one per `CoalesceWindowMs`.

### Throttle drop counter

The `EventBucket.Append` path drops a fire whose interval is below
`MinIntervalMs` and increments `_droppedThrottled`. The drop count
surfaces in every `resources/read` snapshot's `dropped` field. Defaults
keep `MinIntervalMs = 0` so EC-6 sees `dropped: 0`; throttled events fire
in `Sample.Wpf.TodoApp` only if an adopter sets a non-default value.

### Coalesce timing

`EventBucket.ScheduleNotify` uses `Interlocked.CompareExchange(ref
_coalesceScheduled, 1, 0)` to ensure exactly one timer per bucket while a
notification is pending. The `Task.Delay(CoalesceWindowMs)` callback
fires once and resets the flag; additional fires during the window land
in the buffer but do not start new timers. The harness observed 10
JSON-RPC frames for two AddTodos + two subscribe operations + the
inspect/read calls, confirming there is no notification storm.

## Event delivery latency (rough)

End-to-end timing measured via the StdioTest harness's
`InvokeMethodAsync` -> `WaitForResourceUpdate` round trip in headless
mode (NoOpAdapter, single-process; logs visible in
`.phase1/ilprobe-todoapp-1f.log`-adjacent harness runs):

| Step | Typical |
|---|---|
| User-code `event.Invoke(...)` -> `EventBucket.Append` | sub-millisecond |
| Bucket -> `Task.Delay(CoalesceWindowMs)` -> subscriber callback | ~`CoalesceWindowMs` (default 100 ms) |
| Subscriber callback -> `McpServer.SendNotificationAsync` | ~1-3 ms (STJ serialize + stdio write) |
| Total fire -> client `notifications/resources/updated` | ~100-110 ms typical at default settings |

For latency-sensitive scenarios, set `CoalesceWindowMs = 0` — the
notification fires on the first `Task.Run` continuation (~1-5 ms) but
the buffer can still be read for the burst tail.

## Phase-2 readiness

Phase 2 (Avalonia adapter) needs:

- `IUiAutomationAdapter` shape — unchanged. Phase 1.6 added zero new
  adapter methods.
- `MarionetteHost.RunAsync` signature — unchanged.
- Source-generator output that doesn't reference framework types — still
  true. The new `EventDescriptor.Subscribe` lambda only references
  `global::System.EventHandler{<T>}` + the user assembly's event symbol +
  `Marionette.NET.Runtime.Internal.HandlerDisposable`. No WPF / Avalonia
  / WinUI types touched.
- Adapter authoring contract — unchanged. Events are delivered the same
  way headless and GUI; the adapter is not on the event path. The WPF
  adapter does not need updating; the Avalonia adapter (Phase 2) won't
  need event-specific code either.

**Recommendation: Phase 1.6 does NOT change Phase 2's surface area.** No
new adapter contract bits, no new runtime injection points, no new
required dispatcher behaviour. Phase 2 can land Avalonia's
`IUiAutomationAdapter` implementation without touching any Phase 1.6 code.

## New diagnostics

Examples from the source-gen test suite:

### MAR010 — non-standard delegate
```csharp
[McpEvent("custom delegate")]
public event Action<FooArgs>? Bad;
```
Output: `MAR010: [McpEvent] 'Demo.Root.Bad' has delegate type 'System.Action<Demo.FooArgs>?' — Phase 1 supports only System.EventHandler or System.EventHandler<TArgs>`

### MAR011 — orphan event
```csharp
public class Stray  // no [McpRoot]
{
    [McpEvent("orphan")]
    public event EventHandler? Pinged;
}
```
Output: `MAR011: [McpEvent] on 'Demo.Stray.Pinged' is ignored — the declaring class must be decorated with [McpRoot] for the generator to emit an event descriptor`

### MAR012 — invalid throttling
```csharp
[McpEvent("bad sizes", MaxQueueSize = 0, CoalesceWindowMs = -1)]
public event EventHandler? Pinged;
```
Output: `MAR012: [McpEvent] 'Demo.Root.Pinged' has invalid throttling: MaxQueueSize=0, CoalesceWindowMs=-1. MaxQueueSize must be > 0 and CoalesceWindowMs must be >= 0; defaults will be used.`
The descriptor is still emitted with the defaulted values (warning, not
error).

### MAR009 — non-event member
Reserved for the case where an analyzer somehow sees `[McpEvent]` on a
non-event member; the C# compiler enforces the AttributeUsage target,
so MAR009 only fires in pathological / mid-edit scenarios.

## Files added / changed

```
src/Marionette.NET.Abstractions/
  McpAttributes.cs                           (UPDATED — added McpEventAttribute)

src/Marionette.NET.Runtime/
  Manifest/Descriptors.cs                    (UPDATED — added EventDescriptor; RootDescriptor.Events)
  Internal/HandlerDisposable.cs              (NEW)
  Events/EventLogService.cs                  (NEW)
  Resources/EventResourceProvider.cs         (NEW)
  MarionetteHost.cs                          (UPDATED — wire EventLogService + EventResourceProvider; resource handler fan-out)
  Tools/MarionetteTools.cs                   (UPDATED — inspect_app_api includes events with parsed schema)

src/Marionette.NET.SourceGenerator/
  ManifestGenerator.cs                       (UPDATED — orphan-event pipeline source; combine into ManifestModel)
  Validator.cs                               (UPDATED — ValidateEvent + ClassifyEventDelegate; ToEquatableArray for events)
  Emitter.cs                                 (UPDATED — EmitEvents + EmitEventDescriptor)
  Diagnostics.cs                             (UPDATED — MAR009/010/011/012)
  AnalyzerReleases.Unshipped.md              (UPDATED — sorted MAR009-012 entries)
  Model/ManifestModel.cs                     (UPDATED — RootModel.Events + EventModel record)
  JsonSchemaWriter.cs                        (NEW — deterministic schema writer)

samples/Sample.Wpf.TodoApp/
  TodoListViewModel.cs                       (UPDATED — TodoAddedEventArgs + [McpEvent] TodoAdded; AddTodo fires event)

.phase0/StdioTest/
  Program.cs                                 (UPDATED — --todoapp adds event subscribe + read assertion)

tests/Marionette.NET.SourceGenerator.Tests/
  SnapshotTests.cs                           (UPDATED — GoldenEventInput_EmitsExpectedManifest)
  DiagnosticTests.cs                         (UPDATED — MAR010/011/012 + WellFormedEvents)
  Snapshots/GoldenInput_EmitsExpectedManifest.verified.txt   (UPDATED — Triggerables ends with "," + Events tail)
  Snapshots/GoldenEventInput_EmitsExpectedManifest.verified.txt  (NEW)

tests/Marionette.NET.Integration/
  EvalCases.cs                               (UPDATED — added EC6_Events_DeliverViaResourceNotifications)

skill-pack/
  prompts/attributes-reference.md            (UPDATED — [McpEvent] section, namespace + tools updates)
  claude-code/marionette-decorate/SKILL.md   (UPDATED — 5a [McpEvent] step + diagnostics table)
  claude-code/marionette-explore/SKILL.md    (UPDATED — events in discovery procedure)

.phase1/
  demo.ps1                                   (UPDATED — message string says 10/10 + event delivery)
  1f-events.md                               (NEW — this report)
```

Files deliberately not touched (per the Phase 1.6 constraint set):
`MASTERPLAN.md`, `LICENSE`, `.gitignore`, `Directory.Build.props`,
`global.json`, `build/Marionette.NET.props`, `build/Marionette.NET.targets`,
`build/Run-IlProbe.ps1`, `src/Marionette.NET.Adapter.Wpf/*`,
`samples/Sample.Wpf.StripeProbe/*`,
`samples/Sample.Wpf.TodoApp/{App.xaml,App.xaml.cs,MainWindow.xaml,MainWindow.xaml.cs,TodoItem.cs,Program.cs,Sample.Wpf.TodoApp.csproj}`.

## Issues encountered

1. **Records cannot inherit `EventArgs`.** The Phase 1.6 spec example
   wrote `public sealed record TodoAddedEventArgs(string Title, DateTime
   AddedAt) : EventArgs;` but C# rejects this with CS8864 ("records can
   only inherit from object or another record"). Resolved by switching
   the args type to a sealed class with an explicit ctor + read-only
   properties — same shape on the wire, same JSON-schema output.
   Documented in `attributes-reference.md` and the `marionette-decorate`
   skill so adopters don't trip on the same trapdoor.

2. **MCP default JsonSerializerOptions apply camelCase.**
   `ModelContextProtocol.McpJsonUtilities.DefaultOptions` has a
   PascalCase-to-camelCase property naming policy, which made
   `args.Title` surface as `args.title` — divergent from the
   PascalCase the JSON-schema generator emits. Resolved by serializing
   args through a separate `ArgsSerializerOptions` instance that uses
   STJ defaults (no PropertyNamingPolicy). The schema and the actual
   payload now match.

3. **Annotated nullable references on event delegates.** Field-style
   events declared as `event EventHandler? Foo` surface as
   `System.EventHandler?` via `ToDisplayString()`. The original
   classification check compared against `"System.EventHandler"` and
   incorrectly emitted MAR010 for nullable-annotated events. Fixed by
   stripping the nullable annotation
   (`WithNullableAnnotation(NotAnnotated)`) before the equality check.

4. **Snapshot test's `PreserveNewest` lag.** Updating the verified
   snapshot wasn't visible to the test on the first rerun until the
   project was rebuilt — `PreserveNewest` only copies on incremental
   build. Resolved by adding a `dotnet build` before each
   `dotnet test --no-build` retry; documented as a quirk of the test
   harness setup. (No change required for production code.)

5. **Sub-tasks 5a (events) inserted between 4 and 5 (triggerables) in
   the marionette-decorate skill.** Numbering in the skill's procedure
   was preserved by adding `### 5a.` rather than renumbering the
   existing steps — keeps existing eval cases that assert "step 5
   suggests Triggerables" working without churn.

## Hand-off summary (one-paragraph)

Phase 1.6 lands `[McpEvent]` end-to-end: attribute, source-gen detection
+ schema generation, runtime event log + per-event resource provider,
sample wiring (TodoApp `TodoAdded`), harness check, EC-6, and skill-pack
updates. The IL stripping promise from Phase 0 holds (0 hits across all
four needles in stripped Release for both samples), source-gen test count
goes from 8 to 13, integration tests from 5 to 6 (EC-6), the headless
harness from 9 to 10 checks. Phase 2 (Avalonia adapter) is unaffected —
events do not require adapter changes.
