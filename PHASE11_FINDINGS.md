# Phase 11 Findings — interface fallback + AOT-clean entry point + harness

Date: 2026-05-05

## Status

**GREEN.** Closes the three remaining items the previous "what's left" survey
listed under "smaller optional erweiterungen": interface fallback for custom
+ concurrent collections (was Phase-8.5 leftover slice 5), explicit
dynamic-tool invocation in the WinUI + MAUI stdio harnesses (was a Phase-10
verification gap), and a granular annotation-free entry point on the host
(was the open `[RequiresUnreferencedCode]` question after Phase 10).

## What changed

### 1. Interface fallback in `JsonTypeCollector` (Point 5)

When the source generator encounters a generic instantiation whose unbound
generic name does NOT match a known shape (e.g. `ConcurrentDictionary<K,V>`,
`ConcurrentQueue<T>`, or a user-defined `class MyList<T> : IList<T>`), the
collector now walks the type's `AllInterfaces` for a supported standard
contract and registers the user type as that kind.

Dispatch precedence (most-specific first):

| Interface | Kind | STJ factory |
|---|---|---|
| `IDictionary<K,V>` | `IDictionary` | `CreateIDictionaryInfo<TCollection, K, V>` |
| `IReadOnlyDictionary<K,V>` | `IReadOnlyDictionary` | `CreateIReadOnlyDictionaryInfo<…>` |
| `ISet<T>` | `ISet` | `CreateISetInfo<TCollection, T>` |
| `IList<T>` | `IList` | `CreateIListInfo<TCollection, T>` |
| `ICollection<T>` | `ICollection` | `CreateICollectionInfo<TCollection, T>` |
| `IEnumerable<T>` | `IEnumerable` | `CreateIEnumerableInfo<TCollection, T>` |

The user type itself is the `TCollection` generic argument, and a new
`JsonTypeModel.ConcreteContainerOverride` field carries the user's full
type name so the emitter renders `ObjectCreator = static () => new <UserType>()`
verbatim instead of substituting a default container template. The user
type must have a public parameterless constructor; otherwise the collector
rolls back (the runtime would have thrown at deserialisation).

**Concurrent collections** therefore work without needing dedicated
factories: `ConcurrentDictionary<K,V>` matches `IDictionary<K,V>`,
`ConcurrentQueue<T>` / `ConcurrentStack<T>` / `ConcurrentBag<T>` match
`IEnumerable<T>`. Each registers as the corresponding `JsonTypeKind` with
the concurrent type as the concrete container.

`EncodePropertyName` was extended to also encode the generic-syntax
characters (`<`, `>`, `,`, ` `, `?`, `[`, `]`) so that property names
formed from closed generic CLR full names (e.g.
`ConcurrentDictionary<string, int>`) become valid C# identifiers
(`Custom_System_Collections_Concurrent_ConcurrentDictionary_string__int_`).

Two new snapshot fixtures (`GoldenInterfaceFallbackShapes_EmitsExpectedManifest`
and `GoldenAbstractCustomCollection_FallsBackToReflection`) verify the
concurrent + custom user collection path and the public-parameterless-ctor
guard.

### 2. Dynamic-tool exercise in WinUI + MAUI stdio harness (Point 6)

The Phase-10 verification matrix proved registration parity across all
four adapters but only TodoApp + Avalonia Dashboard explicitly invoked a
dynamic per-method tool from the stdio harness. WinUI + MAUI registered
the tools but only enumerated them via `tools/list`.

`.phase0/StdioTest/Program.cs` now adds an explicit dynamic-tool call to
both the WinUI section (`FormLabViewModel.SetTheme({theme="Dark"})`) and
the MAUI section (`PlannerViewModel.AddAppointment({title="Dinner",
startTime, durationMinutes})`). The MAUI test exercises a parameter graph
(string + DateTime + int) that flows through the Phase-8 typed JSON
deserialisation under AOT.

Verified post-AOT-publish: 4/4 samples now pass an explicit dynamic-tool
invoke step (TodoApp 12, Avalonia 14, WinUI 19, MAUI 13 PASS lines).

### 3. `MarionetteHost.RunAsyncSourceGenSafe` annotation-free entry point (Point 7)

The Phase-10 + Phase-8.5 work narrowed `MarionetteHost.RunAsync`'s
`[RequiresUnreferencedCode]` reasoning to two surfaces: `raise_event`
(framework-control event-name reflection) and the legacy JSON fallback
for type graphs the source generator does NOT cover. Adopters who
*neither* call `raise_event` *nor* let unsupported type graphs reach the
runtime path can in principle compile without the annotation — but the
single-entry-point design forced the warning on everyone.

Phase 11 splits the tools and adds a dedicated entry point:

- **`MarionetteRaiseEventTools`** — new `[McpServerToolType]` class hosting
  `RaiseEventAsync`. Moved out of `MarionetteTools` (which now holds the
  five other tools — `inspect_app_api`, `invoke_method`, `read_observable`,
  `capture_screenshot`, `simulate_input`).
- **`MarionetteHost.RunAsync`** — keeps its `[RequiresUnreferencedCode]` /
  `[RequiresDynamicCode]` annotations; registers BOTH tool classes via
  `WithTools<MarionetteTools>().WithTools<MarionetteRaiseEventTools>()`.
- **`MarionetteHost.RunAsyncSourceGenSafe`** — new annotation-free entry
  point. Internally routes through the same `RunAsyncCore` private helper
  but with `includeRaiseEventTool = false`, so `MarionetteRaiseEventTools`
  is never added to the registered tool set. Carries an
  `[UnconditionalSuppressMessage]` for IL2026 / IL3050 with the documented
  contract.
- **`MarionetteTestHost.RaiseEventRawAsync`** updated to call the new
  location. Adopters' test code paths are unchanged (the testing toolkit
  still exposes the full surface).

Adopters who pass the contract:

```csharp
// Before:
[RequiresUnreferencedCode("…")]
public static async Task<int> Main(string[] args)
    => await MarionetteHost.RunAsync(args, GeneratedManifest.Roots, …);

// After (no annotation needed if you stay within source-gen-eligible types
//        and never invoke raise_event):
public static async Task<int> Main(string[] args)
    => await MarionetteHost.RunAsyncSourceGenSafe(args, GeneratedManifest.Roots, …);
```

The contract is documented on the method's XML doc + `UnconditionalSuppressMessage`
justification. Misuse (raise_event would have been a no-op anyway since
the tool isn't registered; an unsupported JSON shape throws
`InvalidOperationException` from `JsonSerializer` at runtime — exactly the
behaviour any AOT-published .NET app gets when it hits a reflection path
that's been disabled).

### 4. Tuple-keyed dictionaries — explicitly unsupported

`Dictionary<(int, int), V>` and similar tuple-keyed dictionary shapes are
NOT covered by the source generator. STJ has no built-in `JsonConverter`
for `ValueTuple` keys (the runtime would refuse the key with a
`NotSupportedException` even before our generator got involved).
`JsonTypeCollector.IsSupportedDictionaryKey` rejects them and the
descriptor falls back to the legacy reflection path; on AOT this means a
runtime `InvalidOperationException`. Adopters who need composite keys
should serialise them as a string-keyed nested object or custom-format
the key into a single string.

## Verification

| Step | Result |
|---|---|
| `dotnet build Marionette.NET.sln -c Debug` | 0 warnings, 0 errors |
| `dotnet test ...SourceGenerator.Tests` | 32/32 PASS (was 30/30) |
| `dotnet test ...Testing.Tests` | 12/12 PASS |
| `dotnet test ...Integration` | 7/7 PASS + 3 GUI-skipped |
| AOT publish (TodoApp / Avalonia / WinUI / MAUI full) | 4/4 exit 0, 0 Marionette IL warnings |
| AOT-runtime stdio handshake (4 samples) | 12 / 14 / 19 / 13 PASS, 0 FAIL each |
| FormLab dynamic-tool invoke under AOT | PASS — `FormLabViewModel.SetTheme` Default → Dark |
| PocketPlanner dynamic-tool invoke under AOT | PASS — `PlannerViewModel.AddAppointment` count 1 → 2, title='Dinner' |

## AOT scorecard (after Phase 11)

| Scenario | Before Phase 11 | After Phase 11 |
|---|---|---|
| `RunAsync` adopter that uses `raise_event` or non-source-gen types | `[RequiresUnreferencedCode]` | unchanged (correct) |
| `RunAsyncSourceGenSafe` adopter (no raise_event + source-gen-eligible types) | annotation forced | **annotation-free** ✅ |
| Source-gen for custom user collections (`MyList<T> : IList<T>`) | unsupported | **AOT guarantee** via interface fallback ✅ |
| Source-gen for concurrent collections (`ConcurrentDictionary` etc.) | unsupported | **AOT guarantee** via interface fallback ✅ |
| WinUI + MAUI dynamic-tool runtime under AOT | enumerated only | **invoked + verified** ✅ |
| Multi-dim arrays (`T[,]`) | unsupported | unsupported (STJ has no factory) |
| Tuple-keyed dictionaries | unsupported | unsupported (STJ has no built-in key converter) |

## Adopter migration notes

- **Existing `RunAsync` callers**: no change required. The full six-tool
  surface (including `raise_event`) is preserved; the annotation surface
  is preserved.
- **New AOT-strict callers**: switch the entry point to
  `MarionetteHost.RunAsyncSourceGenSafe` to drop the `[RequiresUnreferencedCode]`
  warning. `raise_event` becomes unavailable to MCP clients in this mode;
  use `simulate_input` + `[McpCallable]` instead per masterplan tenet 2.
- **`MarionetteTools.RaiseEventAsync` direct callers**: the static method
  has moved to `MarionetteRaiseEventTools.RaiseEventAsync`. The signature
  is unchanged. The `MarionetteTestHost.RaiseEventRawAsync` instance method
  is unaffected.
