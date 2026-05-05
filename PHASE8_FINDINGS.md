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

## What landed in Phase 8.5

### Closed every "deferred" item from slice 5

The original Phase 8 findings noted: *"Slice 5 deferred: IEnumerable<T> / IList<T> / IReadOnlyList<T> / ICollection<T>, multi-dimensional arrays, Dictionary<int,V> / Dictionary<TEnum,V>, Stack<T>/Queue<T>/HashSet<T>."*

Every line of that list except multi-dimensional arrays (which STJ does not natively support) is now covered. The generator dispatches each user-visible shape to its matching `JsonMetadataServices.CreateXxxInfo<TCollection, TElement>` factory:

| User type | `JsonTypeKind` | STJ factory | Notes |
|---|---|---|---|
| `T[]` (rank=1) | `Array` | `CreateArrayInfo<T>` | Phase 8.4 |
| `List<T>` | `List` | `CreateListInfo<List<T>, T>` | Phase 8.4 |
| `IEnumerable<T>` | `IEnumerable` | `CreateIEnumerableInfo<IEnumerable<T>, T>` | Phase 8.5 |
| `IReadOnlyList<T>` | `IEnumerable` | `CreateIEnumerableInfo<IReadOnlyList<T>, T>` | Phase 8.5; STJ has no dedicated factory |
| `IReadOnlyCollection<T>` | `IEnumerable` | `CreateIEnumerableInfo<IReadOnlyCollection<T>, T>` | Phase 8.5; STJ has no dedicated factory |
| `IList<T>` | `IList` | `CreateIListInfo<IList<T>, T>` | Phase 8.5 |
| `ICollection<T>` | `ICollection` | `CreateICollectionInfo<ICollection<T>, T>` | Phase 8.5 |
| `ISet<T>` | `ISet` | `CreateISetInfo<ISet<T>, T>` | Phase 8.5 |
| `IReadOnlySet<T>` | `IReadOnlySet` | `CreateIEnumerableInfo<IReadOnlySet<T>, T>` | Phase 8.5; .NET 10 STJ ships no `CreateIReadOnlySetInfo`, so we fall through to the IEnumerable factory and rely on HashSet's `IReadOnlySet` implementation for the `ObjectCreator` |
| `HashSet<T>` | `HashSet` | `CreateISetInfo<HashSet<T>, T>` | Phase 8.5 |
| `Stack<T>` | `Stack` | `CreateStackInfo<Stack<T>, T>` | Phase 8.5 |
| `Queue<T>` | `Queue` | `CreateQueueInfo<Queue<T>, T>` | Phase 8.5 |
| `Dictionary<K,V>` | `Dictionary` | `CreateDictionaryInfo<Dictionary<K,V>, K, V>` | Phase 8.5 lifts the slice-4 string-key constraint |
| `IDictionary<K,V>` | `IDictionary` | `CreateIDictionaryInfo<IDictionary<K,V>, K, V>` | Phase 8.5 |
| `IReadOnlyDictionary<K,V>` | `IReadOnlyDictionary` | `CreateIReadOnlyDictionaryInfo<IReadOnlyDictionary<K,V>, K, V>` | Phase 8.5 |

### Non-string dictionary keys

`Dictionary<TKey, TValue>` and the two interface variants now accept any STJ-supported key shape: `string`, all integral and floating-point primitives, `bool`, `char`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`, `Uri`, `Version`, and any enum. The `JsonTypeCollector.IsSupportedDictionaryKey` predicate gates registration; out-of-set keys (custom types, `Tuple`, `nint`, …) trigger the legacy reflection fallback as before.

### Multi-dimensional arrays — out of scope

STJ has no `CreateMultiDimensionalArrayInfo<T>` factory. Multi-dim arrays (`T[,]`, `T[,,]`) are not naturally JSON-shaped (no canonical mapping to nested arrays without a custom converter). The collector continues to reject them via the `Rank == 1` check; the descriptor's typed `Serialize*` lambda stays null and the runtime falls back to reflection (which itself will throw at runtime for true multi-dim arrays — STJ does not handle them either). Adopters who need a JSON-friendly matrix should use `T[][]` (jagged arrays), which the existing array-recursion path already handles cleanly.

### Source generator hygiene

The emitter previously had per-shape `EmitListCreation` / `EmitDictionaryCreation` methods with substantial duplication. Phase 8.5 introduces two unified helpers:

- `EmitElementCollectionCreation(sb, type, factoryName, concreteContainerTemplate)` for every element-only collection kind (List, IEnumerable, IList, ICollection, ISet, IReadOnlySet, HashSet, Stack, Queue).
- `EmitDictionaryCreation(sb, type, factoryName, concreteContainerTemplate)` for the three dictionary kinds.

Each per-kind dispatch arm in the emitter's switch is now a single call with the factory name and the concrete-container template (`{T}` / `{K}` / `{V}` placeholders). The whole emitter is ~80 lines shorter and the per-shape rendering logic now lives in two places, not nine.

### Verification

- Build matrix: 0 warnings, 0 errors across 19 projects (Debug + Release).
- Source-gen tests: 30/30 PASS (was 28/28 — added `GoldenCollectionShapes` + `GoldenUnsupportedShapes`).
- Testing-toolkit tests: 12/12 PASS.
- Integration eval-cases: 7/7 PASS + 3 GUI-skipped.
- AOT publish smoke (TodoApp / Avalonia Dashboard / PocketPlanner full): all exit 0, 0 Marionette IL warnings.
- AOT-runtime stdio handshake against TodoApp / Avalonia Dashboard / PocketPlanner full: all PASS (12/14/12 PASS, 0 FAIL each), including the dynamic-tool exercise lines for TodoApp + Avalonia.

### Remaining limitations

- Multi-dimensional arrays (`T[,]`, `T[,,]`): no STJ factory; not coverable by source-gen.
- Custom collection types (e.g. user-defined `MyList<T> : IList<T>`): the collector matches by exact unbound generic name and does not yet recognise interface-implementations of supported shapes. Workaround: expose an `IList<T>`-typed property instead.
- Tuple-keyed dictionaries (`Dictionary<(int, int), V>`): STJ has no built-in tuple-key converter; rejected by `IsSupportedDictionaryKey`.
- Concurrent collections (`ConcurrentDictionary`, `ConcurrentBag`, etc.): STJ ships dedicated factories but they're rarely used as `[McpEvent]` payloads. Deferred until adopter demand surfaces.

These items continue to fall back to the legacy reflection-based serialiser; the runtime fallback path remains correct. The host's `[RequiresUnreferencedCode]` annotation explicitly carries the "out-of-scope shapes trigger the descriptor's runtime `JsonSerializer.Serialize` fallback" caveat for adopter visibility.

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
