# Phase 8 Findings — AOT JSON Source-Gen

Date: 2026-05-04

## Status

**Phase 8.1 (event args) GREEN.** The source generator now emits a hand-written
`MarionetteEventArgsJsonContext : JsonSerializerContext` populated via
`JsonMetadataServices` factory calls, and each `[McpEvent]` descriptor carries
a typed `SerializeArgs` lambda referencing it. `EventResourceProvider.ReadAsync`
prefers the lambda; the legacy reflection path is the fallback. AOT publish on
`Sample.Maui.PocketPlanner` (which carries `AppointmentAddedEventArgs`) emits
**0 IL2026/IL3050/IL2070/IL2075/IL2046 warnings** across the entire publish.

## What was tried before this design

Two architectural blockers shaped the chosen approach.

### 1. `raise_event` source-gen — not viable

`raise_event(root, control, event, args)` resolves a control via the visual
tree at runtime and reflects on its CLR type chain to find a `RoutedEvent`
static field (WPF / Avalonia) or compiler-emitted backing delegate (WinUI /
MAUI). Both the control type and the event name come from MCP request
arguments — the source generator has nothing to bind statically. The
`[RequiresUnreferencedCode]` annotation on
`IUiAutomationAdapter.RaiseEventAsync` is sachlich correct and stays.

Adopters who need AOT-clean event firing should use `simulate_input`
(semantic, no reflection on user types) or `[McpCallable]+invoke_method`
(typed dispatcher emitted by the source generator).

### 2. STJ `[JsonSerializable]` source-gen composition — blocked by Roslyn

The first attempt was to emit a partial `JsonSerializerContext` with
`[JsonSerializable(typeof(...))]` attributes and let
`System.Text.Json.SourceGeneration` complete the abstract members. Build
failed with:

```
error CS0534: 'MarionetteEventArgsJsonContext' does not implement
inherited abstract member 'JsonSerializerContext.GetTypeInfo(Type)'.
```

**Root cause:** Roslyn runs all `IIncrementalGenerator`s in parallel against
the same input compilation. Each generator's output is added to the
compilation atomically AFTER all generators have finished their `Execute`
phase. The STJ generator scans for `[JsonSerializable]` attributes only on
the original syntax trees — never on another generator's emitted code. This
is a fundamental architectural constraint of the Roslyn compilation model.

**Resolution:** become our own JSON source generator. The `JsonContextEmitter`
in `src/Marionette.NET.SourceGenerator` writes a `JsonSerializerContext`
subclass with hand-coded `JsonTypeInfo<T>` properties built via
`JsonMetadataServices.CreateObjectInfo<T>` / `CreateValueInfo<T>` /
`CreateNullableInfo<T>`. Each property's metadata (object creator,
`PropertyMetadataInitializer`, typed getters, property-type-info references)
is materialised at compile time. Zero runtime reflection.

A standalone prototype under `C:\tmp\json-aot-prototype` validated the path
end-to-end before any generator code was written: a hand-written context
with `JsonMetadataServices.CreateObjectInfo` produced the correct JSON
output and AOT-published with 0 IL warnings under
`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.

## What landed in Phase 8.1

### Source generator additions

- **`Model/ManifestModel.cs`** — new records: `JsonTypeKind` enum
  (Object / Primitive / Nullable), `JsonTypeModel`, `JsonPropertyModel`. New
  fields on `ManifestModel`, `RootModel`, and `EventModel` that flow type
  metadata through the incremental pipeline as immutable values (no
  ITypeSymbol references survive the validator boundary).
- **`JsonTypeCollector.cs`** — DFS walker that maps an `ITypeSymbol` to the
  closed transitive set of types needed to serialise it. Per-call
  rollback-on-failure semantics: any unsupported shape anywhere in the
  graph reverts the dictionary so partial registrations never pollute later
  attempts. Slice 1 supports primitives (string, int family, float family,
  decimal, char, DateTime, DateTimeOffset, TimeSpan, Guid, Uri, Version),
  plain user classes/records with public-getter properties, and
  `Nullable<T>`. Out of scope for slice 1: arrays, generics, enums,
  interfaces, abstract types — these mark the entire descriptor's typed
  Serialize lambda as null and the runtime falls back to the legacy
  reflection path.
- **`JsonContextEmitter.cs`** — renders the per-assembly partial
  `MarionetteEventArgsJsonContext` class. Each `JsonTypeInfo<T>` property is
  lazy-init'd through a private factory; object-kind types get a `Create_<X>()`
  helper that builds a `JsonObjectInfoValues<T>` with typed getter lambdas
  per property and `PropertyTypeInfo` references back to other context
  members. The override of `GetTypeInfo(Type)` dispatches every registered
  type to its property; unknown types return null so `JsonSerializer` raises
  the standard "no type info" error rather than silently degrading.
- **`Emitter.cs`** — emits a typed `SerializeArgs` lambda on each
  `EventDescriptor` whose args type's transitive graph was successfully
  registered by the collector. The lambda boxes-cast the value, runs
  `JsonSerializer.Serialize<TArgs>(value, context.Default.<TypeName>)`, and
  parses the result into a `JsonNode` for the resource provider to splice.

### Runtime additions

- **`EventDescriptor.SerializeArgs`** — new optional positional parameter
  (default null). Adopters who hand-craft descriptors keep the legacy path;
  source-gen-emitted descriptors use the typed lambda.
- **`EventResourceProvider.ReadAsync`** — prefers
  `entry.Descriptor.SerializeArgs` over the legacy reflection-based
  `SerializeArgsToNode` helper. Existing internal suppressions narrowed in
  scope (now justify the fallback path only).

### Verification

- `dotnet build Marionette.NET.sln -c Debug` — 0 warnings, 0 errors across
  18 projects.
- `dotnet test tests/Marionette.NET.SourceGenerator.Tests` — 28/28 PASS
  (snapshot updated for `GoldenEventInput`).
- `dotnet test tests/Marionette.NET.Testing.Tests` — 12/12 PASS.
- `dotnet test tests/Marionette.NET.Integration` — 7 PASS + 3 GUI-skipped.
- `dotnet publish samples/Sample.Maui.PocketPlanner ... -p:PublishAot=true` —
  exit 0, 0 IL2026/IL3050/IL2070/IL2075/IL2046 warnings across the publish.
- AOT-published `Sample.Maui.PocketPlanner.exe --mcp --headless` — JSON-RPC
  initialize handshake completes; dynamic per-method tools register (5 of
  them); transport shuts down cleanly.

## Use cases this unlocks

Recall the AOT story before Phase 8.1 (see "Welcher Use-Case funktioniert
momentan nicht?"):

| # | Scenario | Before 8.1 | After 8.1 |
|---|---|---|---|
| 4 | Frozen-Mode (`--mcp --headless`) | clean | clean (unchanged) |
| 8 | `[McpEvent]` args with complex user types | trim-fragile | **AOT guarantee** for source-gen-eligible graphs |
| 9 | `[McpObservable]` with complex user types | trim-fragile | unchanged — slice 2 work |
| 7 | `raise_event` reflection on framework controls | `[RequiresUnreferencedCode]` | unchanged (architecturally not source-gen-able) |
| 6 | Per-method dynamic tools under AOT | SDK blocker | unchanged (waiting on `ModelContextProtocol` upstream) |

Adopters whose `[McpEvent]` args types live within the slice-1 supported
shape (primitives + plain user records/classes + Nullable<T>) now get a
compile-time guarantee instead of a hopeful trim attempt.

## Remaining slice 2 scope

- `[McpObservable]` typed `SerializeValue` lambdas → `MarionetteJsonContext`
  (camelCase, matching `McpJsonUtilities.DefaultOptions` convention) for
  observable property types.
- `[McpCallable]` typed `SerializeResult` lambdas through the same context.
- `EmitJsonAwareConversion` (parameter deserialisation) updated to use
  context-bound `JsonSerializer.Deserialize` overloads.
- Runtime: `WatchableResourceProvider.{ReadAsync,MaybePushUpdatedAsync,
  ReadValueJsonInline}`, `MarionetteDispatch.SerializeResult` use the new
  lambdas; existing IL2026/IL3050 suppressions narrowed.

Slice 3 (future): expand collector support to enums, arrays, and common
collection generics (`List<T>`, `Dictionary<K, V>`).

## Known limitations of slice 1

- Args type with `[JsonIgnore]`-decorated properties: the slice-1 collector
  does not honour the attribute and would emit a property the serializer
  may then fail to bind. Mitigation: such types fall outside the supported
  shape and the descriptor falls back to runtime serialisation. To be
  addressed in slice 3 alongside enum support.
- Args type with non-default ctor + init-only properties: serialisation
  works (we only need a public getter); deserialisation is a slice-3
  concern (parameters were always passed as JsonElement and only
  deserialised at the runtime boundary).
- Cycles in the args graph: collector breaks the recursion at depth 6 and
  bails out. The MaxDepth budget matches `JsonSchemaWriter` so the schema
  string and the JSON-context closure stay consistent.
