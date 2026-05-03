# Phase 1.b — Source Generator (Manifest emission)

**Status:** PASS
**Date:** 2026-05-03
**SDK:** .NET 10.0.202 · Roslyn 4.14.0

## Goal & verdict

Build a Roslyn Incremental Source Generator that scans every user assembly
for `[McpRoot]` / `[McpCallable]` / `[McpObservable]` / `[McpTriggerable]`
attribute sites, validates them, and emits a single `Marionette.g.cs` in the
reserved `Marionette.Generated` namespace. The emitted manifest is the
runtime's compile-time contract: every dispatcher is a typed lambda, no
reflection, AOT-clean.

**Verdict: GO for Phase 1.2.** The generator wires through clean for the
existing `Sample.Wpf.StripeProbe`, the IL stripping promise survives
unchanged, the stdio handshake stays at 4/4 PASS, and the snapshot test +
diagnostic tests cover the canonical shapes.

## What was built

### `src/Marionette.NET.SourceGenerator/` — new project

| File | Purpose |
|---|---|
| `Marionette.NET.SourceGenerator.csproj` | netstandard2.0, `IsRoslynComponent=true`, `EnforceExtendedAnalyzerRules=true`, `IncludeBuildOutput=false`, references `Microsoft.CodeAnalysis.CSharp 4.14.0` + `Microsoft.CodeAnalysis.Analyzers 3.11.0` (PrivateAssets=all). NO ProjectReference to Abstractions. NuGet pack metadata directs the DLL to `analyzers/dotnet/cs/` for Phase 7. |
| `ManifestGenerator.cs` | The `IIncrementalGenerator`. Three pipeline sources (root candidates, orphan callables, assembly name) `Combine`d into a `ManifestModel` per assembly. `RegisterSourceOutput` emits `Marionette.g.cs` and replays diagnostics. Uses `ForAttributeWithMetadataName` (Roslyn 4.3+) for efficient attribute pre-filtering. |
| `Validator.cs` | Symbol→model conversion. Match attributes by metadata-name string (the generator cannot reference Abstractions). Emits MAR001/002/003/004/005/006/007/008. |
| `Emitter.cs` | Renders `ManifestModel` to source text. AOT-critical: every callable gets a typed lambda `(instance, args) => { var typed = (T)instance; var a = (int)args["a"]!; return typed.Method(a); }`. Void → `return null;`, Task/Task<T> → returns the task as `object?` and the runtime awaits. |
| `Diagnostics.cs` | Eight `DiagnosticDescriptor`s, MAR-prefixed, category `Marionette.Generator`. |
| `Model/ManifestModel.cs` | Equatable record types: `ManifestModel`, `RootModel`, `CallableModel`, `ParameterModel`, `ObservableModel`, `TriggerableModel`, `DiagnosticInfo`, `LocationInfo`, `EquatableArray<T>`. No `ISymbol` / `Compilation` / `SyntaxNode` references — required for incremental cache stability. |
| `Internal/IsExternalInit.cs` | `init`-only setter polyfill for ns2.0 (the generator can't reference Abstractions, so the polyfill is duplicated here). |
| `AnalyzerReleases.{Shipped,Unshipped}.md` | Required by RS2008 once `EnforceExtendedAnalyzerRules=true`. |

### `tests/Marionette.NET.SourceGenerator.Tests/` — new project

| File | Purpose |
|---|---|
| `Marionette.NET.SourceGenerator.Tests.csproj` | net10.0 + xUnit. References Abstractions normally (test compilations need the attributes on the reference list). References the source generator as a regular `ProjectReference` (NOT analyzer wiring) so tests can drive it in-memory. |
| `GeneratorRunner.cs` | Lightweight in-memory harness — feeds C# source into `CSharpGeneratorDriver`, returns generated text + Roslyn diagnostics. No Verify/Microsoft.CodeAnalysis.Testing dependencies; this keeps the harness transparent and avoids needing the Verify NuGet (not in this machine's offline cache). |
| `SnapshotTests.cs` | One golden-input test: a `Calculator` with int/double/void/Task callables and watchable + non-watchable observables. Custom `Snapshot.Verify` writes `.received.txt` next to test sources, compares against committed `.verified.txt`, fails loudly if the verified file is missing. |
| `DiagnosticTests.cs` | Five rejection tests + one positive control: MAR001 static, MAR001 generic, MAR002 internal callable, MAR003 orphan callable, MAR008 empty root, plus a well-formed input that must produce zero errors. |
| `Snapshots/GoldenInput_EmitsExpectedManifest.verified.txt` | Blessed snapshot of the generator's output for the canary input. Tracked in git. |

### Wiring changes

- `Marionette.NET.sln` — added `Marionette.NET.SourceGenerator` (under `src` folder) and `Marionette.NET.SourceGenerator.Tests` (under new `tests` folder).
- `samples/Sample.Wpf.StripeProbe/Sample.Wpf.StripeProbe.csproj` — added the generator as `OutputItemType="Analyzer" ReferenceOutputAssembly="false"` ProjectReference (regardless of `EnableMcpAutomation`); added `EmitCompilerGeneratedFiles=true` for adopter-visible `obj/.../generated/Marionette.g.cs`.
- `samples/Sample.Wpf.StripeProbe/MainWindow.xaml.cs` — replaced the long `[McpRoot("...long string...")]` (the string was treated as a description in Phase 0; in the locked Phase 1.a contract that ctor argument is `Name`) with a parameterless `[McpRoot]`, so the manifest name falls back to the type name `MainWindow`.
- `.gitignore` — added `*.received.txt` so snapshot-test scratch files are not committed.

## Generator architecture (incremental pipeline)

```
                +-------------------------------------+
                |  ForAttributeWithMetadataName       |
                |    "Marionette.McpRootAttribute"    |
                |  -> (RootModel?, Diagnostics)       |
                +------------------+------------------+
                                   |
                +------------------+------------------+
                |  ForAttributeWithMetadataName       |
                |    "Marionette.McpCallableAttribute"|
                |  -> DiagnosticInfo? (MAR003 only)   |
                +------------------+------------------+
                                   |
                +------------------+------------------+
                |  CompilationProvider.Select         |
                |  -> string AssemblyName             |
                +------------------+------------------+
                                   |
                                   v
                +-------------------------------------+
                |  Collect + Combine                  |
                |  -> ManifestModel (equatable)       |
                +------------------+------------------+
                                   |
                                   v
                +-------------------------------------+
                |  RegisterSourceOutput               |
                |    * report diagnostics             |
                |    * AddSource("Marionette.g.cs")   |
                +-------------------------------------+
```

**Cache behaviour:** every model type is a `record` with equatable-array
collections. Roslyn invalidates a downstream node only when an upstream
model's structural equality changes; an unrelated edit elsewhere in the user
assembly does not retrigger emission. `EquatableArray<T>` (idiom borrowed
from `System.Text.RegularExpressions.Generator` in dotnet/runtime) replaces
`ImmutableArray<T>` to give us per-element equality instead of
reference equality.

## Validator diagnostics

| ID | Severity | Trigger | What the generator does |
|---|---|---|---|
| MAR001 | Error | `[McpRoot]` on static OR generic class, or non-class type | Skip the root entirely. |
| MAR002 | Error | `[McpCallable]` method is non-public | Skip the callable; emit no descriptor. |
| MAR003 | Warning | `[McpCallable]` method's class lacks `[McpRoot]` | Skip the callable; the rest of the assembly is unaffected. |
| MAR004 | Error | `[McpCallable]` parameter type is blacklisted (`Stream`, delegate, pointer, `IntPtr`/`nint`) | Skip the callable. Phase 1.b is permissive — only obvious blacklist hits trigger. |
| MAR005 | Error | `[McpObservable]` property has setter but no getter | Skip the observable. |
| MAR006 | Warning | `[McpObservable]` property OR getter is non-public | Skip the observable. |
| MAR007 | Error | `[McpTriggerable]` property's type is not `Button`/`ButtonBase` and exposes no public `Click` event | Skip the triggerable. Tolerant of `IErrorTypeSymbol` (mid-edit). |
| MAR008 | Info | `[McpRoot]` class declares no MCP entrypoints | Emit a placeholder root descriptor; never block the build. |

Diagnostics flow through the pipeline as `DiagnosticInfo` records (not Roslyn
`Diagnostic` instances) so they can be cache-keyed — `Diagnostic` itself is
not equatable. The emit stage rebuilds them via `Validator.ToRoslynDiagnostic`
just before reporting.

## Emitter contract — `Marionette.Generated.GeneratedManifest`

The emitter writes one `Marionette.g.cs` per user assembly. The shape is
fixed across Phase 1.b:

```csharp
namespace Marionette.Generated;

public static class GeneratedManifest
{
    public const string AssemblyName = "Sample.Wpf.StripeProbe";
    public static IReadOnlyList<RootDescriptor> Roots { get; } = ...;
}

public sealed record RootDescriptor(
    string Name,
    string TypeName,
    Func<object>? Create,             // null when no public parameterless ctor
    IReadOnlyList<CallableDescriptor> Callables,
    IReadOnlyList<ObservableDescriptor> Observables,
    IReadOnlyList<TriggerableDescriptor> Triggerables);

public sealed record CallableDescriptor(
    string Name, string Description,
    bool OffUiThread, int TimeoutSeconds, bool IsAsync,
    IReadOnlyList<ParamDescriptor> Parameters,
    Func<object, IReadOnlyDictionary<string, object?>, object?> Invoke);

public sealed record ParamDescriptor(
    string Name, string ClrTypeName, bool IsRequired, object? DefaultValue);

public sealed record ObservableDescriptor(
    string Name, string Description, bool Watchable,
    int PollingIntervalMs, string ClrTypeName,
    Func<object, object?> Read);

public sealed record TriggerableDescriptor(
    string Name, string Description,
    Marionette.TriggerStrategy Strategy, string ControlTypeName,
    Func<object, object?> ResolveControl);
```

**Inline-emit decision (vs. shared-runtime-types):** the descriptor records
are emitted inline in every user assembly's `Marionette.g.cs` rather than
defined once in a shared `Marionette.NET.Manifest.Model` library. Three
reasons:
1. Stripped Release builds keep the existing 7-file output without adding a
   Marionette-prefixed assembly to ship.
2. The runtime can use `Type.GetType("Marionette.Generated.GeneratedManifest, <userAsm>")`
   discovery (Phase 1.2) without a hard reference contract on a model
   assembly.
3. Phase 7's NuGet packaging stays simple — the generator NuGet ships only
   the analyzer DLL, no companion model library.

Trade-off: the descriptor types are duplicated across user assemblies. Each
copy is ~20 lines of records, no shared identity. Phase 1.2 will need to bind
to the generated `GeneratedManifest` type by reflection-over-emitted-symbols;
since the symbol name is well-known and the contract is frozen, this is
trim/AOT-safe.

### AOT-critical: the `Invoke` lambdas

For `[McpCallable] public int Add(int a, int b)`, the emitter writes:

```csharp
Invoke: static (instance, args) =>
{
    var typed = (global::Sample.Wpf.StripeProbe.MainWindow)instance;
    var a = (int)args["a"]!;
    var b = (int)args["b"]!;
    return typed.Add(a, b);
}
```

For `void Reset()`:

```csharp
Invoke: static (instance, args) =>
{
    var typed = (global::Demo.Calculator)instance;
    typed.Reset();
    return null;
}
```

For `Task<int> LoadAsync()`:

```csharp
Invoke: static (instance, args) =>
{
    var typed = (global::Demo.Calculator)instance;
    return typed.LoadAsync();      // runtime awaits
}
```

For optional parameters with defaults — `args.TryGetValue` with the literal
default fallback baked in. Reserved keywords are `@`-escaped.

**Zero reflection in the emitted code:** no `MakeGenericMethod`, no
`Activator.CreateInstance(Type)`, no `Type.GetType(string)`, no
`MethodInfo.Invoke`. `grep`-confirmed in `Emitter.cs` (only matches are in
the contract-doc comment block).

## Snapshot test strategy

Custom `Snapshot.Verify(actual, testName)`:

1. Always writes `Snapshots/<TestName>.received.txt` for diff-target convenience.
2. Reads `Snapshots/<TestName>.verified.txt`; if missing, **fails the test
   with an actionable message** (no auto-bless on CI).
3. Compares text byte-for-byte after CRLF→LF normalization.

`*.received.txt` is in `.gitignore`; `*.verified.txt` is committed. To bless
a regenerated output, copy `received` → `verified`. The mechanism is one
screen of code (no Verify/Microsoft.CodeAnalysis.Testing dependency) which
keeps it transparent and offline-friendly.

The chosen golden input exercises every emitter shape: int/double/void/Task
return shapes, a callable with non-default `OffUiThread`+`TimeoutSeconds`,
two observables (one watchable with custom polling, one default), and an
explicit `[McpRoot("calculator")]` name override.

## Build matrix results

All commands run from `C:\Home\Code\nw.Automation` after a clean rebuild on
.NET 10.0.202.

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build src/Marionette.NET.SourceGenerator/Marionette.NET.SourceGenerator.csproj -c Release` | PASS — 0 warnings, 0 errors |
| 2 | `dotnet build Marionette.NET.sln -c Debug` | PASS — 0 warnings, 0 errors (7 projects) |
| 3 | `dotnet build Marionette.NET.sln -c Release` | PASS — 0 warnings, 0 errors |
| 4 | `dotnet test tests/Marionette.NET.SourceGenerator.Tests/...csproj -c Debug` | PASS — 7/7 tests green (1 snapshot + 5 rejection + 1 positive control) |
| 5 | `dotnet build samples/Sample.Wpf.StripeProbe/...csproj -c Release -p:EnableMcpAutomation=false` | PASS — stripped output, 7 files |
| 6 | `dotnet build samples/Sample.Wpf.StripeProbe/...csproj -c Debug -p:EnableMcpAutomation=true` | PASS — MCP-on output |
| 7 | `pwsh build/Run-IlProbe.ps1 -ProbeDll ... -Target Sample.Wpf.StripeProbe.dll` | PASS — 0 hits across all 4 needles |
| 8 | `dotnet .phase0/StdioTest/.../StdioTest.dll <Sample.Wpf.StripeProbe.exe>` | PASS — 4/4 handshake checks, 3 JSON-RPC frames, 0 pollution |

### IL probe (cmd 7)

```
[PASS] Marionette.NET.Runtime: TOTAL hits across 1 file(s): 0
[PASS] Adapter.Wpf:            TOTAL hits across 1 file(s): 0
[PASS] Marionette.Ai:          TOTAL hits across 1 file(s): 0
[PASS] ModelContextProtocol:   TOTAL hits across 1 file(s): 0
PASS — stripped build contains zero forbidden symbols.
```

The generator's `Marionette.g.cs` lives inside the user assembly and uses only
`global::Marionette.TriggerStrategy` from Abstractions (allowed) plus BCL
types — no Runtime/Adapter/Ai/MCP references. Stripping promise intact.

### Stdio handshake (cmd 8)

```
PASS - initialize handshake (server: Marionette.NET 0.0.1-spike-c, protocol 2025-11-25)
PASS - tools/list contains marionette_ping
PASS - tools/call marionette_ping returned "pong"
PASS - child exited cleanly with code 0
stdout summary: 3 JSON-RPC frames, 0 pollution lines
```

Identical to the Phase 1.a baseline. Phase 1.b's generator output sits in
the user assembly without disturbing the Runtime's existing `WithTools<PingTool>()`
registration — that's expected: Phase 1.2 will be the one to consume
`GeneratedManifest.Roots`.

## Generated manifest excerpt — `Sample.Wpf.StripeProbe`

After Phase 1.b, the sample emits this `Marionette.g.cs` (key lines, full
file in `obj/Debug/net10.0-windows/generated/.../Marionette.g.cs`):

```csharp
namespace Marionette.Generated;

public static class GeneratedManifest
{
    public const string AssemblyName = "Sample.Wpf.StripeProbe";
    public static IReadOnlyList<RootDescriptor> Roots { get; } = new RootDescriptor[]
    {
        new RootDescriptor(
            Name: "MainWindow",
            TypeName: "Sample.Wpf.StripeProbe.MainWindow",
            Create: static () => new global::Sample.Wpf.StripeProbe.MainWindow(),
            Callables: new CallableDescriptor[]
            {
                new CallableDescriptor(
                    Name: "Add",
                    Description: "Adds two numbers",
                    OffUiThread: false, TimeoutSeconds: 0, IsAsync: false,
                    Parameters: new ParamDescriptor[]
                    {
                        new ParamDescriptor(Name: "a", ClrTypeName: "int", IsRequired: true, DefaultValue: null),
                        new ParamDescriptor(Name: "b", ClrTypeName: "int", IsRequired: true, DefaultValue: null),
                    },
                    Invoke: static (instance, args) =>
                    {
                        var typed = (global::Sample.Wpf.StripeProbe.MainWindow)instance;
                        var a = (int)args["a"]!;
                        var b = (int)args["b"]!;
                        return typed.Add(a, b);
                    }),
            },
            Observables: new ObservableDescriptor[]
            {
                new ObservableDescriptor(
                    Name: "Result", Description: "The most recent sum result",
                    Watchable: false, PollingIntervalMs: 500,
                    ClrTypeName: "int",
                    Read: static (instance) => ((global::Sample.Wpf.StripeProbe.MainWindow)instance).Result),
            },
            Triggerables: global::System.Array.Empty<TriggerableDescriptor>()
        ),
    };
}
```

Counts match the spec: 1 `RootDescriptor` for `MainWindow`, 1
`CallableDescriptor` for `Add(a, b)`, 1 `ObservableDescriptor` for `Result`,
0 triggerables (Phase 0 sample has no `[McpTriggerable]` properties — that's
correct).

## Hand-off to Phase 1.2 (Runtime)

The Runtime needs to discover and consume the per-assembly manifest. The
contract Phase 1.b locks:

### Discovery

The user-assembly DLL exports a static type
`Marionette.Generated.GeneratedManifest` with:

- `public const string AssemblyName` — sanity-check value, equals
  `Compilation.AssemblyName`.
- `public static IReadOnlyList<RootDescriptor> Roots { get; }` — every
  `[McpRoot]` discovered, in source order.

Runtime discovery in Phase 1.2 should:

1. Iterate `AppDomain.CurrentDomain.GetAssemblies()` (or accept an explicit
   `Assembly` enumeration via the `MarionetteHost.RunAsync` entry point).
2. For each, look up `Marionette.Generated.GeneratedManifest` via
   `Assembly.GetType("Marionette.Generated.GeneratedManifest", throwOnError:false)`.
3. If present, call the static `Roots` getter via the typed cast
   `(IReadOnlyList<RootDescriptor>)`.

Note: the descriptor types are duplicated per user assembly (separate CLR
identities). Phase 1.2 must shape its consumption around that — either
re-bind by reflection over the well-known shape, or define an
`IManifestProvider` interface that user code implements via a tiny
generated bridge (the latter is recommended; the bridge can be added to
`Emitter.EmitManifest` without breaking the descriptor contract).

### Iteration shape per `RootDescriptor`

For each root, the Runtime should:

1. Decide instance lifetime: prefer adopter-supplied (e.g. `MainWindow`
   resolved via the WPF `Application.MainWindow`), fall back to
   `descriptor.Create?.Invoke()` if non-null. If both are null, skip the
   root with a stderr warning.
2. For every `CallableDescriptor`: register an MCP tool named
   `<rootName>.<callableName>` (or just `<callableName>` if names are
   unique). Tool handler: marshal arguments from the MCP `tools/call`
   request into an `IReadOnlyDictionary<string, object?>` keyed by
   `ParamDescriptor.Name`, then `descriptor.Invoke(instance, args)`. If
   `IsAsync==true`, the returned `object?` is a `Task` / `Task<T>` /
   `ValueTask` / `ValueTask<T>` — `await` it generically (via dynamic or
   `((dynamic)task).GetAwaiter().GetResult()`-style handling — Phase 1.2's
   call to make).
3. For every `ObservableDescriptor`: register a `read_observable` entry.
   When `Watchable==true`, additionally register an MCP resource at
   `marionette://<rootName>/<observableName>` — Phase 2 ships the watcher
   (INPC + polling fallback at `PollingIntervalMs`).
4. For every `TriggerableDescriptor`: Phase 1 `Adapter.Wpf` only handles
   `Strategy.Semantic` for `Button.Click`. Resolve the control via
   `descriptor.ResolveControl(instance)`, dispatch through the WPF
   dispatcher, raise the click. Phase 3 expands to `EventSystem` and
   `InputSystem`.

### Argument marshalling note

`ParamDescriptor.ClrTypeName` is the C# short type name (`int`, `double`,
`string`) — not the metadata name. Runtime arg-marshalling can map the JSON
shape to those names via a fixed lookup; for unknown types the runtime falls
back to `JsonSerializer.Deserialize` with the looked-up CLR type. The
`Invoke` lambda's cast `(int)args["a"]!` is exact: the Runtime must place a
boxed `int` in the dictionary value, not a `JsonElement`. Phase 1.2 will own
the JSON→CLR bridge.

### What Phase 1.b deliberately did NOT do

- Did NOT modify `MarionetteHost.cs` or its `WithTools<PingTool>()` call.
  Phase 1.2 will replace `PingTool` with manifest-driven dispatchers.
- Did NOT add an `IManifestProvider` interface. Phase 1.2 adds this in
  Runtime + extends `Emitter.cs` to emit the bridge if it wants typed
  consumption instead of reflection.
- Did NOT touch `Marionette.NET.Adapter.Wpf` — the dispatcher hookup is
  Phase 1.2's call.
- Did NOT integrate the generator-emitted descriptors with
  `Marionette.Ai.TriggerHook`. Channel push remains a direct hook
  installation in Phase 1.2.

## Files changed / added

```
src/Marionette.NET.SourceGenerator/                   (NEW project)
  Marionette.NET.SourceGenerator.csproj
  ManifestGenerator.cs
  Validator.cs
  Emitter.cs
  Diagnostics.cs
  Model/ManifestModel.cs
  Internal/IsExternalInit.cs
  AnalyzerReleases.Shipped.md
  AnalyzerReleases.Unshipped.md

tests/Marionette.NET.SourceGenerator.Tests/           (NEW project)
  Marionette.NET.SourceGenerator.Tests.csproj
  GeneratorRunner.cs
  SnapshotTests.cs
  DiagnosticTests.cs
  Snapshots/GoldenInput_EmitsExpectedManifest.verified.txt

Marionette.NET.sln                                    (added 2 projects + tests folder)
.gitignore                                            (added *.received.txt)
samples/Sample.Wpf.StripeProbe/Sample.Wpf.StripeProbe.csproj
                                                      (analyzer ProjectReference + EmitCompilerGeneratedFiles)
samples/Sample.Wpf.StripeProbe/MainWindow.xaml.cs     (replaced [McpRoot("...")] with parameterless [McpRoot])
.phase1/1b-sourcegen.md                               (NEW — this report)
```

Files deliberately not touched: `MASTERPLAN.md`, `README.md`, `LICENSE`,
`Directory.Build.props`, `global.json`, `build/Marionette.NET.props`,
`build/Marionette.NET.targets`, `build/Run-IlProbe.ps1`,
`src/Marionette.NET.Abstractions/*`, `src/Marionette.NET.Runtime/*`,
`src/Marionette.NET.Adapter.Wpf/*`, all of `.phase0/*`.

## Issues encountered

1. **`IsExternalInit` polyfill needed in the generator project.** Same
   ns2.0 problem Phase 1.a hit with Abstractions; resolved by duplicating
   the polyfill into `src/Marionette.NET.SourceGenerator/Internal/IsExternalInit.cs`
   (the generator cannot reference Abstractions, see "Trapdoors" in the
   Phase 1.b prompt).

2. **RS2008 — release tracking required when `EnforceExtendedAnalyzerRules=true`.**
   Resolved by adding `AnalyzerReleases.{Shipped,Unshipped}.md` and wiring
   them as `<AdditionalFiles Include="..." />` in the csproj. New rules go
   in Unshipped; on each major release Unshipped flows to Shipped.

3. **C# records are case-sensitive on positional ctor parameter names.** First
   emit pass used `name:`, `description:` (camelCase) which compiled the
   generator but failed downstream — the records' positional ctor exposes
   parameters as PascalCase. Fixed by using `Name:`, `Description:`, etc.
   throughout `Emitter.cs`.

4. **Initial `.Where(d => d is not null).Select(d => d!.Value)` failed for
   reference-typed nullables.** `DiagnosticInfo` is a reference type, so
   `DiagnosticInfo?` is a nullable reference, not `Nullable<T>`. Using
   `d!` instead of `d!.Value` (which is value-type only) fixed the pipeline.

5. **`pwsh` not on PATH in bash sandbox.** Same Phase-1.a issue. Run the IL
   probe via the dedicated PowerShell tool — `Run-IlProbe.ps1` itself
   unchanged.

## Phase 1.b Notes for Phase 1.2

The Runtime project `src/Marionette.NET.Runtime/Marionette.NET.Runtime.csproj`
currently references the source generator's outputs ONLY transitively via
the user assembly. Phase 1.2 should consider whether the Runtime itself
should emit its own descriptor table for built-in tools (`marionette_ping`,
`capture_screenshot`, `inspect_app_api`, …); the simplest model is to leave
those as hand-written `[McpServerToolType]` classes in Runtime alongside
the generated user-assembly manifest and merge them at registration.

Discovery code Phase 1.2 will need:

```csharp
public static IEnumerable<RootDescriptor> DiscoverRoots(IEnumerable<Assembly> assemblies)
{
    foreach (var asm in assemblies)
    {
        var t = asm.GetType("Marionette.Generated.GeneratedManifest", throwOnError: false);
        if (t is null) continue;
        var rootsProp = t.GetProperty("Roots", BindingFlags.Public | BindingFlags.Static);
        if (rootsProp?.GetValue(null) is not System.Collections.IEnumerable roots) continue;
        foreach (var r in roots) yield return /* cast via reflection-emitted bridge */;
    }
}
```

Trim/AOT note: the well-known type name + property name are constant strings
known at compile time. Annotating the discovery method with
`[DynamicallyAccessedMembers(...)]` for the manifest type is the standard
pattern and keeps the trim analyzer happy. Alternatively, Phase 1.2 can
extend `Emitter.cs` to emit a tiny `internal static class _MarionetteBridge`
in each user assembly that surfaces the same data via a shared interface
in `Marionette.NET.Abstractions` — fully AOT-clean with zero reflection.
That extension is a small follow-up, suitable for Phase 1.2 to design once
it knows what it actually needs.
