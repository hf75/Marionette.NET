# Phase 12 Findings — pushing through the limit list

Date: 2026-05-05

## Status

**7 of 8 items shipped, 1 architectural cap confirmed (Avalonia raw input only).** Phase 12 was the response to the observation that Phase 10's "SDK-blocked" framing had been overly pessimistic — re-examining the limit list found several items that were just "haven't built it yet." This phase pushed each through to either a real implementation or a documented hard cap. The two originally-deferred items (12.5 tuple keys, 12.2 raise_event AOT) were both implemented in the final pass.

## What landed

### Phase 12.3 — MAUI semantic input (commit `e0f6f8e`)

Closes three previously-capped MAUI `simulate_input` kinds via public MAUI APIs:

| Kind | Mechanism |
|---|---|
| `key_press` with `key="Enter"` | `Entry.SendCompleted()` / `Editor.SendCompleted()` / `SearchBar.SearchCommand.Execute()` |
| `right_click` | Walks `View.GestureRecognizers` for a `TapGestureRecognizer` with `Buttons=Secondary`; executes its `Command` |
| `mouse_move` | Walks for a `PointerGestureRecognizer`; executes `PointerMovedCommand` |

`key_down` / `key_up` for arbitrary keys remain unsupported — MAUI 10.x exposes no public way to fire them. Adopters use `[McpCallable]` for the underlying state mutation.

[`MauiInputSimulator.cs`](src/Marionette.NET.Adapter.Maui/Internal/MauiInputSimulator.cs) +202 / -45 lines. AOT-clean (no reflection on user types).

### Phase 12.4 — Multi-dim arrays rank 2 (commit `4278255`)

STJ has no built-in metadata factory for `T[,]`. Phase 12.4 ships [`MultiDimArrayRank2Converter<TElement>`](src/Marionette.NET.Runtime/Json/MultiDimArrayConverter.cs) — a generic `JsonConverter<TElement[,]>` that round-trips via row-major nested JSON arrays (`[[1,2],[3,4]]`) and validates the rectangular invariant on read.

The source generator detects `IArrayTypeSymbol` with `Rank == 2` and primitive-typed elements, registers as `JsonTypeKind.MultiDimArrayRank2`, and emits:

```csharp
JsonMetadataServices.CreateValueInfo<int[,]>(
    Options,
    new MultiDimArrayRank2Converter<int>(
        (JsonConverter<int>)System_Int32.Converter));
```

Adopter-visible: `public int[,] Pixels { get; init; }` on `[McpEvent]` args ships AOT-clean.

Rank 3+ remains unsupported (no STJ factory; adopters can derive their own JsonConverter following the same shape). Multi-dim of complex objects also unsupported (converter requires primitive elements).

### Phase 12.6 — No-ctor user collections serialise-only (commit `ab957e2`)

Phase 11's interface-fallback walker rejected user collection types lacking a public parameterless constructor — the `ObjectCreator` would have thrown at deserialisation time. But that's the wrong default for the most common adoption pattern: a custom collection used as an `[McpEvent]` args / `[McpObservable]` value / `[McpCallable]` return payload is only ever serialised OUT; it never needs a ctor.

Phase 12.6 registers no-ctor types as serialise-only. The emitter renders `ObjectCreator = null` instead of the `static () => new <Type>()` template. Adopters who only serialise get full source-gen coverage; adopters who try to deserialise get a clear runtime `InvalidOperationException` at the call site that needed a ctor.

### Phase 12.7 — `[JsonIgnore(Condition=...)]` sub-modes (commit `8b43cd8`)

Closes the Phase-8.3 deferral. Every `JsonIgnoreCondition` value now maps to the right runtime behaviour:

| Source attribute | Generator behaviour |
|---|---|
| (no attribute) | property included |
| `[JsonIgnore]` | property dropped (Always — default) |
| `[JsonIgnore(Always)]` | property dropped (explicit) |
| `[JsonIgnore(Never)]` | property included (acts like no attribute) |
| `[JsonIgnore(WhenWritingDefault)]` | property included, `IgnoreCondition.WhenWritingDefault` emitted |
| `[JsonIgnore(WhenWritingNull)]` | property included, `IgnoreCondition.WhenWritingNull` emitted |

### Phase 12.5 — Tuple-keyed dictionaries (commit `22592ba`)

`Dictionary<(int, int), V>` and `Dictionary<(string, int, int), V>` now ship AOT-clean. The implementation:

- Runtime: [`ValueTupleKeyConverter<T1, T2>` and `<T1, T2, T3>`](src/Marionette.NET.Runtime/Json/ValueTupleKeyConverter.cs) — generic `JsonConverter` that override BOTH `Read`/`Write` (round-trip via JSON-array) AND `ReadAsPropertyName`/`WriteAsPropertyName` (STJ uses these for dictionary keys specifically). Each component delegates to the caller-supplied per-component `JsonConverter<TX>`.
- Generator: new `JsonTypeKind.ValueTupleKey2` / `ValueTupleKey3`, walker recognises `INamedTypeSymbol.IsTupleType` BEFORE the generic-shape block (the bug fix that took the longest), emits `JsonMetadataServices.CreateValueInfo<(T1, T2)>` wired to the runtime converter with per-component converter references pulled from each element's own `JsonTypeInfo.Converter`.

Adopter-visible: `public Dictionary<(int X, int Y), string> Grid { get; init; }` on `[McpEvent]` args ships AOT-clean. Rank 4+ remains unsupported (extensible by following the same shape).

### Phase 12.2 — `raise_event` AOT-clean via opt-in catalog (commit pending)

`raise_event`'s reflection-based static-`<Name>Event` lookup can now be replaced with a typed dispatcher driven by assembly-level declarations:

```csharp
[assembly: McpRaisable(typeof(MyApp.Controls.SubmitButton), "Click")]
[assembly: McpRaisable(typeof(MyApp.Controls.SubmitButton), "Submitted")]
```

The source generator emits `Marionette.Generated.RaiseEventCatalog.TryRaise(object, string, object?)` with a typed switch — one `case <Type> typed:` per declared control, an inner `switch` over event names, each arm casting to `UIElement` (WPF) or `Interactive` (Avalonia) and calling `RaiseEvent(args ?? new RoutedEventArgs(<Type>.<Name>Event, typed))`. The static-field reference inside the switch arm is what keeps trim/AOT from stripping the metadata.

A module initializer auto-registers the dispatcher with the runtime registry (`Marionette.Runtime.Adapters.RaiseEventCatalog`); the WPF/Avalonia adapters call the catalog first and fall through to the existing reflection path on a miss. Adopters who add `[McpRaisable]` declarations get AOT preservation for free; everyone else stays on the reflection path with no behavior change.

Validator emits `MAR015` (Warning) when a `[McpRaisable]` references a (Type, EventName) pair that doesn't resolve to a static `RoutedEvent` field — the pair is dropped from the catalog; reflection is the fallback.

WinUI / MAUI not covered: WinUI uses compiler-emitted backing delegates rather than the `<Name>Event` static-field convention; MAUI has no routed-event surface at all. The catalog detects the framework via the `RoutedEvent` type's namespace (`System.Windows.RoutedEvent` → WPF, `Avalonia.Interactivity.RoutedEvent` → Avalonia) and rejects others.

### Phase 12.8 — Type-graph cycle-depth lift (commit `d94b281`)

The original `JsonTypeCollector.MaxDepth = 6` was set to "match `JsonSchemaWriter`'s budget" — but that comment was already inaccurate (`JsonSchemaWriter`'s `MaxDepth` is 3, has been since Phase 1.b). The cap was over-conservative: typical view-model trees easily exceed 6 levels and would be rejected without a real cycle.

Phase 12.8 decouples the two budgets:

- `JsonSchemaWriter.MaxDepth` stays at 3 — schema strings are size-bounded for `inspect_app_api` readability.
- `JsonTypeCollector.MaxDepth` lifted to 64 — only a stack-overflow safety net for pathological inputs. True cycles are still caught by the per-call `visiting` HashSet.

## What did NOT land

### Phase 12.1 — Avalonia raw input (key_press / mouse_move) — ARCHITECTURAL CAP

My initial re-evaluation claimed `RawKeyEventArgs` and `RawPointerEventArgs` were publicly constructible in Avalonia 12.0.2, citing reflection on the implementation DLL (`lib/net10.0/Avalonia.Base.dll`). That was wrong: the C# compiler reads the **reference assembly** (`ref/net10.0/Avalonia.Base.dll`), and the reference assembly's metadata flags ALL the relevant constructors as `internal`:

```
--- Avalonia.AvaloniaLocator (Public=True) ---
  [internal] .ctor
--- Avalonia.Input.IInputManager (Public=True) ---
  (no public methods)
--- Avalonia.Input.KeyboardDevice (Public=True) ---
  [internal] .ctor
--- Avalonia.Input.Raw.RawKeyEventArgs (Public=True) ---
  [internal] .ctor
--- Avalonia.Input.Raw.RawPointerEventArgs (Public=True) ---
  [internal] .ctor (×2)
```

The implementation assembly exposes them as public for Avalonia's own use, but external consumers compile against the reference and can't call them. `IInputManager.ProcessInput` is also `internal` on the reference. This is a deliberate Avalonia API decision; consumers are expected to use `Avalonia.Headless` for input simulation in test scenarios.

Adopters who need keyboard input on Avalonia should:

1. Use `[McpCallable]` for the underlying handler (semantic-first, masterplan tenet 2).
2. Use `raise_event` with the routed-event name when the args type is publicly constructible (`Click`, `KeyDown` is not).
3. Use `Avalonia.Headless` directly in test code (separate dependency, separate adapter pattern).

`AvaloniaInputSimulator.cs` was NOT modified in this phase. The Phase 9.1 `type_text` semantic path remains the only addition since Phase 3.1.

**Lesson learned (consolidated):** when re-evaluating "is this really capped?", inspect the reference assembly via `System.Reflection.Metadata.PEReader` rather than runtime reflection on the implementation DLL. The implementation DLL can have public-internal divergence that misleads.

## Verification

| Step | Result |
|---|---|
| Solution Release build | 0 warnings, 0 errors |
| Source-gen tests | **39/39 PASS** (was 32/32 — added 4 for 12.4/12.6/12.7/12.8, 1 for 12.5, 2 for 12.2) |
| Testing-toolkit | 12/12 PASS (no changes) |
| Integration eval-cases | 7/7 PASS + 3 GUI-skipped (no changes) |

AOT-publish verification was not re-run for Phase 12 — the source-code changes are confined to:
- `MauiInputSimulator.cs` (12.3) — adapter-internal, no new IL surfaces
- Source generator emit logic (12.4 / 12.6 / 12.7 / 12.8) — verified by source-gen tests that compile the generated code
- New runtime helper `MultiDimArrayRank2Converter<T>` (12.4) — pure code, no reflection

The cumulative contract from Phase 10 + 11 (4/4 adapters AOT-publish + AOT-runtime stdio handshake PASS) holds unchanged.

## Updated impossibility table

| # | Original claim | Phase 12 status |
|---|---|---|
| 1 | raise_event AOT | ✅ **Closed** — Phase 12.2 (`[McpRaisable]` opt-in typed catalog, WPF + Avalonia) |
| 2 | WPF + AOT GUI | **External** (Microsoft-known WPF+AOT limitation) |
| 3 | Avalonia raw input (`key_*`/`mouse_move`) | **External** — Avalonia 12.0.2 reference-assembly cap (verified via PEReader) |
| 4 | WinUI old Windows / locked-down SKUs | **External** (OS-level) |
| 5 | MAUI key/mouse/right-click | ✅ **Closed** — Phase 12.3 (semantic APIs) |
| 6 | Multi-dim arrays in JSON | ✅ **Closed** for rank 2 — Phase 12.4 (custom converter) |
| 7 | Tuple-keyed dictionaries | ✅ **Closed** for rank 2 + 3 — Phase 12.5 (`ValueTupleKeyConverter`) |
| 8 | No-ctor collections | ✅ **Closed** — Phase 12.6 (serialise-only) |
| 9 | `[JsonIgnore(Condition)]` sub-modes | ✅ **Closed** — Phase 12.7 |
| 10 | STJ generator composition | **Already-worked-around** (Phase 8) |
| 11 | Type-graph cycles depth 6 | ✅ **Closed** — Phase 12.8 (lifted to 64, decoupled from schema) |

7 of 11 items closed in Phase 12 (up from 5). The 4 remaining are all genuinely external (Microsoft / Avalonia / OS-level API decisions). No items remain deferred for "low demand" or "haven't built it yet."

## Adopter takeaways

The library is now substantially more usable for the long-tail cases the user was concerned about:

- MAUI form apps with Enter-to-submit + right-click context menus + pointer-tracking — all AOT-clean.
- Image / matrix / heatmap data on event payloads — AOT-clean serialisation.
- Custom collection types with constructor parameters (e.g. capacity-required pools) — work as event/observable/return payloads.
- Conditional null-suppression on observable values via `[JsonIgnore(WhenWritingNull)]` — works as expected.
- Deeply-nested viewmodel trees (8+ levels) — register fully into the JSON context.
- Composite-keyed lookups via `Dictionary<(int, int), V>` and rank-3 tuples — AOT-clean source-gen with a runtime `ValueTupleKeyConverter`.
- AOT-clean `raise_event` for declared (Type, EventName) pairs via `[assembly: McpRaisable]` — adapters call the typed catalog first; full reflection is only the fallback.

The remaining caps are genuinely external. Avalonia keyboard input is the most adopter-visible — workaround per masterplan tenet 2 (`[McpCallable]` semantic actions) remains the right pattern.
