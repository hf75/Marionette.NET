# Phase 13 Findings — closing the source-generator shape gaps

Date: 2026-05-05

## Status

**All 7 items from "section E" of the open-features list shipped.** Phase 13 closes every source-generator shape that was previously refused, deferred, or worked-around. Every slice is verified by a dedicated snapshot test that compiles the generated code; the full source-gen test suite is green at **49/49** (up from 39 at start of phase).

## What landed

### 13.E.17 — `in` parameters

`Validator.ValidateCallable` previously refused **all** non-`None` `RefKind` values. `in` is JSON-RPC-compatible (caller hands a value, callee sees a readonly reference); the dispatcher emits the call site without the `in` keyword and C# overload resolution binds correctly. `ref` and `out` remain refused (no JSON-RPC story for round-tripping caller-side variables).

### 13.E.13 — Multi-dim arrays rank 3 + rank 4

Previously: only rank 2 (Phase 12.4). Phase 13 extends with `MultiDimArrayRank3Converter<TElement>` + `MultiDimArrayRank4Converter<TElement>` in `Marionette.NET.Runtime.Json.MultiDimArrayConverter.cs` plus matching `JsonTypeKind.MultiDimArrayRank3` / `Rank4` cases in the source generator. Wire format is recursively-nested JSON arrays with rectangular-invariant validation on read. Element-type constraint is unchanged: must be a primitive shape with a built-in JSON converter.

Adopter-visible: `public int[,,] Voxels { get; init; }` and `public double[,,,] Tensor { get; init; }` now ship AOT-clean.

Rank 5+ remains unsupported — the pattern is mechanical (each rank adds one more nested loop pair); adopters can ship their own converter.

### 13.E.14 — Tuple-keyed dictionaries rank 4 + rank 5

Previously: only rank 2 + 3 (Phase 12.5). Phase 13 adds `ValueTupleKeyConverter<T1,T2,T3,T4>` and `<T1,T2,T3,T4,T5>` plus `JsonTypeKind.ValueTupleKey4` / `Key5`. The collector's `IsSupportedDictionaryKey` and tuple branch lifted from `rank ∈ {2, 3}` to `rank ∈ {2..5}`.

Adopter-visible: `Dictionary<(int A, int B, int C, int D), V>` and rank-5 keys now AOT-clean.

### 13.E.18 — Stream parameters via base64 wrapper

`System.IO.Stream` and `System.IO.MemoryStream` lifted from the parameter-type blacklist. Detection happens in `Validator.IsBase64StreamParamType`; the dispatcher emits `new MemoryStream(Convert.FromBase64String(jsonElement.GetString()))` instead of going through System.Text.Json. The schema writer emits `{"type":"string","format":"byte"}` (the JSON Schema convention).

`FileStream` stays blacklisted — there's no sane base64 wrap (you'd need a file path, not bytes). Adopters who really want a file on disk should take a path string instead.

Adopter-visible:
```csharp
[McpCallable("Hash a base64-encoded payload.")]
public int CountBytes(Stream payload) => (int)payload.Length;
```

### 13.E.19 — Custom `[JsonConverter]` honored on types + properties

Two paths were added:

1. **Type-level**: `[JsonConverter(typeof(MyConverter))]` on a user struct/class registers the type as `JsonTypeKind.CustomConverter`. The emitter renders `JsonMetadataServices.CreateValueInfo<T>(Options, new MyConverter())` and **skips the property walk entirely** — the user's converter owns the round-trip.

2. **Property-level**: `[JsonConverter(typeof(MyConverter))]` on a property declaration overrides the property type's default converter. The emitter renders `Converter = new MyConverter()` in the per-property `JsonPropertyInfoValues`; the property type's own `JsonTypeInfo` stays reachable for other call sites.

Both paths require the converter type to have a public parameterless ctor (the emitter renders `new X()`); converters with required dependencies fall through silently.

Adopter-visible: Money / DateOnly / hex-encoded ints / domain-specific value types ship AOT-clean with custom converters.

### 13.E.15 — Generic `[McpRoot]` via `[assembly: McpClosedRoot]`

Previously: generic `[McpRoot]` classes were rejected with MAR001. Phase 13 introduces `[assembly: McpClosedRoot(typeof(MyGen<int>), Name="...")]` — the source generator scans the assembly's attributes (CompilationProvider source F), validates each closed instantiation through `Validator.ValidateClosedGenericRoot`, and emits one `RootDescriptor` per closed type.

The MAR001 rejection for generic classes was downgraded to silent skip — the open-generic class with `[McpRoot]` is no longer flagged; adopters who only declare `[McpRoot]` without any `[McpClosedRoot]` simply produce no manifest entry. New diagnostic **MAR016** (Warning) flags invalid `[McpClosedRoot]` declarations (open generic, type parameters not closed, non-class type).

Example:
```csharp
[McpRoot]
public class Counter<T> where T : struct
{
    [McpCallable("Bump.")] public T Bump() => default;
}

[assembly: McpClosedRoot(typeof(Counter<int>), Name = "intCounter")]
[assembly: McpClosedRoot(typeof(Counter<long>), Name = "longCounter")]
```
produces two manifest entries with `new Counter<int>()` and `new Counter<long>()` factories.

### 13.E.16 — Generic `[McpCallable]` via `ClosedTypes` property

Previously: generic `[McpCallable]` methods were rejected with MAR014. Phase 13 introduces a `ClosedTypes` named-arg on the `McpCallable` attribute:

```csharp
[McpCallable("Echo a typed value.", ClosedTypes = new[] { typeof(int), typeof(string) })]
public T Echo<T>(T value) => value;
```

The validator constructs the closed method symbol via `IMethodSymbol.Construct(closedType)` and walks its parameters per-instantiation — type parameters substitute through (`T value` → `int value`). The emitter renders the call site as `typed.Echo<int>(value)` (closed type-arg literal preserved) and the manifest entry's name becomes `Echo_int` / `Echo_string` (mangled with `EncodeForIdentifier`).

Without `ClosedTypes`, generic methods now emit **MAR017** (Warning, not Error) and are silently skipped — adopters get a clear hint without a build break.

Single-type-parameter only. Multi-type-parameter generic methods would need a nested array shape that `Type[]` can't represent — adopters with that need wrap each closure in a per-arity overload.

## Verification

| Step | Result |
|---|---|
| Solution Release build | 0 warnings, 0 errors |
| Source-gen tests | **49/49 PASS** (was 39 — added 10 fixtures: 1 in-param, 1 ref/out refusal, 1 rank-3-and-4, 1 rank-4-and-5, 1 stream, 2 custom converter, 1 closed-generic-root, 2 generic-callable) |
| Testing-toolkit tests | 12/12 PASS |

Existing tests updated:
- `MAR001_GenericClassWithMcpRoot_IsRejected` → renamed to `MAR001_GenericClassWithMcpRoot_SilentlySkippedSinceE15` (policy change)
- `MAR014_GenericCallableMethod_IsRejected` → renamed to `MAR017_GenericCallableMethodWithoutClosedTypes_IsSkipped` (severity + ID change)
- `GoldenUnsupportedShapes_FallsBackToReflection` updated to use rank-5 array (rank 3+ now supported)

## New diagnostics

| ID | Severity | Triggered by |
|---|---|---|
| MAR016 | Warning | `[assembly: McpClosedRoot]` references invalid type (not closed generic, open type parameter, etc.) |
| MAR017 | Warning | Generic `[McpCallable]` without `ClosedTypes = new[] { typeof(...) }` |

## Updated open-features list

Section E from the previous list is now **fully shipped**. Other sections (A: distribution, B: Uno adapter, C: external caps, D: raise_event WinUI/MAUI, F: resource cleanup, G: skill-pack v2, H: VS analyzer) are unchanged and remain on the roadmap or as documented external limits.

## Adopter takeaways

The library now covers essentially every reasonable C# parameter / property / type shape:

- Read-only by-reference parameter passing for large structs (`in`).
- Multi-dim arrays up to rank 4 (matrices, voxel grids, 4D weight tensors).
- Composite-keyed lookups up to 5-tuple keys.
- Binary payloads via `Stream` parameters (base64-on-the-wire).
- Custom serialization for domain types via `[JsonConverter]` (type-level or per-property).
- Generic root view-models via `[assembly: McpClosedRoot]`.
- Generic callables via `[McpCallable(ClosedTypes = ...)]`.

What's still rejected by design:
- `ref` / `out` parameters (no JSON-RPC story).
- `FileStream` parameters (no sane wrap; use a path string).
- Multi-type-parameter generic methods (use per-arity overloads).
- Rank 5+ multi-dim arrays / rank 6+ tuple keys (mechanical extension; ship your own converter).
- Generic root types without `[McpClosedRoot]` (silently skipped — opt closed instantiations in explicitly).
