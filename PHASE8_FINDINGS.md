# Phase 8 Findings — AOT JSON Source-Gen

Date: 2026-05-04

## Status

**Phase 8.1 + 8.2 GREEN.** The source generator emits two hand-written
`JsonSerializerContext`-derived classes populated via `JsonMetadataServices`
factory calls:
- `MarionetteEventArgsJsonContext` — PascalCase property naming (matches the
  `ArgsJsonSchema` advertised through `inspect_app_api`); used by every
  `[McpEvent]` whose args graph is source-gen-eligible.
- `MarionetteJsonContext` — camelCase property naming (matches
  `McpJsonUtilities.DefaultOptions`); used by every `[McpObservable]` value,
  every `[McpCallable]` return type (sync, `Task<T>`, `ValueTask<T>`), and
  every `[McpCallable]` parameter type whose value arrives as a `JsonElement`.

Each affected descriptor carries a typed `SerializeArgs` /
`SerializeValue` / `SerializeResult` lambda referencing the right context
member; the runtime prefers the typed lambda and falls back to the legacy
reflection-based path only for types the JsonTypeCollector does not yet
support (slice 3 expansion).

AOT publish on `Sample.Maui.PocketPlanner` (which carries an
`AppointmentAddedEventArgs` event, four observables incl. `DateTime?`, and
five callables): **0 IL2026/IL3050/IL2070/IL2075/IL2046 warnings** across
the entire publish. Marionette code itself emits zero linker warnings.

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

## What landed in Phase 8.2

### Source generator additions

- **Second `JsonTypeCollector`** instance per root (`valueTypes`) parallel
  to the args-graph collector. Same recursive walk, same rollback-on-failure
  semantics, same supported shape (primitives + plain user records/classes
  + `Nullable<T>`).
- **`JsonContextEmitter` extended** with `EmitEventArgsContext` (PascalCase)
  and `EmitValueContext` (camelCase via
  `JsonNamingPolicy.CamelCase` baked into the constructor's
  `JsonSerializerOptions`). The shared private helper renders the
  `JsonSerializerContext` body — both contexts emit the same shape, only
  the options differ.
- **Per-descriptor lambdas**: `ObservableDescriptor.SerializeValue` and
  `CallableDescriptor.SerializeResult` (both `Func<object?, string>?`).
  When the property/return type's transitive graph is source-gen-eligible,
  the emitter wires `JsonSerializer.Serialize<T>(value, context.Default.<X>)`;
  otherwise the descriptor's lambda stays default-null.
- **Parameter deserialisation** rewritten to use the typed
  `JsonSerializer.Deserialize<T>(string, JsonTypeInfo<T>)` overload through
  `MarionetteJsonContext.Default.<X>` — closes the IL2026/IL3050 warnings
  that surfaced once the user assembly grew a custom
  `JsonSerializerContext` (the AOT linker becomes more aggressive about
  flagging non-source-gen `JsonSerializer.Deserialize<T>(string)` calls when
  it sees a `JsonSerializerContext` derivative).

### Runtime additions

- **`ObservableDescriptor.SerializeValue`** + **`CallableDescriptor.SerializeResult`** —
  new optional positional parameters (default null). Adopters' hand-crafted
  descriptors keep the legacy path; source-gen-emitted descriptors use the
  typed lambdas.
- **`WatchableResourceProvider`** (3 call sites — `ReadAsync`,
  `MaybePushUpdatedAsync`, `ReadValueJsonInline`) prefers
  `entry.Observable.SerializeValue` over the legacy
  `JsonSerializer.Serialize(value, McpJsonUtilities.DefaultOptions)`.
- **`MarionetteDispatch`** prefers `callable.SerializeResult` over
  `MarionetteDispatch.SerializeResult(object?)`. Existing internal
  suppressions narrowed in scope.

### Verification

- `dotnet build Marionette.NET.sln -c Debug` — 0 warnings, 0 errors across
  18 projects.
- `dotnet test tests/Marionette.NET.SourceGenerator.Tests` — 28/28 PASS
  (3 snapshots updated for the new typed-deserialise paths).
- `dotnet test tests/Marionette.NET.Testing.Tests` — 12/12 PASS.
- `dotnet test tests/Marionette.NET.Integration` — 7 PASS + 3 GUI-skipped.
- `dotnet publish samples/Sample.Maui.PocketPlanner ... -p:PublishAot=true` —
  exit 0, **0 IL warnings** (any of IL2026/IL3050/IL2070/IL2075/IL2046)
  across the publish. Marionette code itself emits zero linker warnings.

## Use cases this unlocks (after 8.1 + 8.2)

| # | Scenario | Before 8.0 | After 8.1 | After 8.2 |
|---|---|---|---|---|
| 4 | Frozen-Mode (`--mcp --headless`) | clean | clean | clean |
| 7 | `raise_event` on framework controls | warn | warn | warn (architectural) |
| 8 | `[McpEvent]` args complex types | trim-fragile | **AOT guarantee** | **AOT guarantee** |
| 9 | `[McpObservable]` complex types | trim-fragile | trim-fragile | **AOT guarantee** |
| — | `[McpCallable]` sync return values | trim-fragile | trim-fragile | **AOT guarantee** |
| — | `[McpCallable]` `Task<T>` results | trim-fragile | trim-fragile | **AOT guarantee** |
| — | `[McpCallable]` parameter deserialisation (JsonElement → T) | trim-fragile | trim-fragile | **AOT guarantee** |
| 6 | Per-method dynamic tools under AOT | SDK blocker | SDK blocker | SDK blocker (`ModelContextProtocol`) |

## Slice 3 scope (deferred)

- **Enums** — JsonMetadataServices uses a different factory shape
  (`GetEnumConverter<TEnum>` + `CreateValueInfo<TEnum>`); needs a fourth
  `JsonTypeKind`.
- **Arrays + collection generics** — `List<T>`, `Dictionary<K, V>`,
  `T[]`, `IEnumerable<T>` → `JsonMetadataServices.CreateListInfo` /
  `CreateArrayInfo` / `CreateDictionaryInfo`.
- **`[JsonIgnore]` honouring** on object-kind types.
- **`MarionetteTools.SerializeRoot`** (used by `inspect_app_api`) —
  currently still on the legacy path; low-frequency call so impact is small.

## Known limitations of slice 1 + 2

- Type with `[JsonIgnore]`-decorated properties: the collector does not
  honour the attribute and would emit a property STJ then expects to be
  there. Mitigation: such types currently fall outside the supported shape
  via the recursive walk failing on a sub-property's `[JsonIgnore]`-only
  type — descriptor falls back to runtime serialisation. Proper handling
  is slice 3 alongside enums.
- Cycles in the type graph: collector breaks the recursion at depth 6 and
  bails out. The MaxDepth budget matches `JsonSchemaWriter` so the schema
  string and the JSON-context closure stay consistent.
- Adopter hand-crafted descriptors: pre-Phase-8 callers that build
  `CallableDescriptor` / `ObservableDescriptor` / `EventDescriptor` by
  hand without setting the new optional `Serialize*` lambdas continue to
  work — the runtime checks `is { } typed` and falls back to the legacy
  reflection path. No source-breaking changes.
