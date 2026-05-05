# Stripping Guide

Marionette.NET is designed so Release builds can ship with no MCP runtime symbols while Debug builds remain fully automatable.

## Build Switch

The controlling MSBuild property is:

```xml
<EnableMcpAutomation>true</EnableMcpAutomation>
```

Project defaults come from `build/Marionette.NET.props`:

- Debug builds default to `true`.
- Release builds default to `false`.

When automation is disabled, the build should keep only `Marionette.NET.Abstractions` attribute metadata. Runtime, adapter, MCP transport, and generated manifest code should disappear from the final application closure.

## Conditional API

`Ai.Trigger(...)` and `Ai.ScheduleTrigger(...)` are compiled only when `MCP_ENABLED` is defined. The source generator also checks `MCP_ENABLED`:

- Enabled: emits `Marionette.g.cs`.
- Disabled: emits no source, but still reports diagnostics so IDE feedback remains available.

This means stripped builds keep useful authoring squiggles without accidentally referencing runtime descriptor types.

## Verification

Use the IL probe for regression checks:

```powershell
pwsh build/Run-IlProbe.ps1
```

The expected stripped result is zero references to:

- `Marionette.NET.Runtime`
- any `Marionette.NET.Adapter.*`
- `ModelContextProtocol`
- `Marionette.Generated.GeneratedManifest`

## AOT Publish

For Windows desktop publish checks, use the repository samples as templates. The current validated path is:

- WPF stripped publish succeeds.
- WPF full MCP publish succeeds for frozen/headless MCP mode.
- Avalonia publish plus stdio handshake is covered in CI.
- WinUI and MAUI use Windows-specific TFMs and should be validated on a Windows runner.

Native AOT on Windows requires the Visual Studio C++ workload. GitHub-hosted `windows-latest` includes it; self-hosted runners need it installed explicitly.

## AOT Entry Points (Phase 10 / 11)

The runtime offers two entry points on `MarionetteHost`. They differ in which tools they register and whether the method itself carries `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]`:

| Entry point | Tool surface | Annotations | When to use |
|---|---|---|---|
| `RunAsync` | full six tools incl. `raise_event` | `[RequiresUnreferencedCode]` + `[RequiresDynamicCode]` | adopters who use `raise_event` from MCP clients OR who have payload types not covered by the source generator |
| `RunAsyncSourceGenSafe` | five tools, **no `raise_event`** | annotation-free | adopters who do NOT call `raise_event` AND keep every payload type within the source-gen-eligible shape set |

The source-gen-eligible shapes (Phase 8 / 8.5 / 11):

- Primitives: every numeric, `bool`, `char`, `string`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`, `Uri`, `Version`.
- Enums (string-encoded via `JsonStringEnumConverter<TEnum>`).
- `Nullable<T>` over any supported value type.
- Plain user classes / records with public-getter properties (recursive).
- `T[]` (rank 1), `List<T>`, `Dictionary<K, V>` (any STJ-supported key type — string, primitives, enum, `DateTime`, `Guid`, …).
- Every standard collection interface: `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IList<T>`, `ICollection<T>`, `ISet<T>`, `IReadOnlySet<T>`, `HashSet<T>`, `Stack<T>`, `Queue<T>`.
- `IDictionary<K, V>`, `IReadOnlyDictionary<K, V>` across STJ-supported key types.
- **Phase 11 interface fallback:** any user-defined or concurrent collection that implements one of the above interfaces and has a public parameterless constructor — e.g. `class MyList<T> : IList<T>`, `ConcurrentDictionary<K, V>`, `ConcurrentQueue<T>`, `ConcurrentStack<T>`, `ConcurrentBag<T>`.

Out-of-scope shapes (force the runtime onto the legacy reflection path; AOT throws `InvalidOperationException`):

- Multi-dimensional arrays (`T[,]`, `T[,,]`) — STJ has no factory for them. Use jagged arrays `T[][]` instead.
- Tuple-keyed dictionaries (`Dictionary<(int, int), V>`) — STJ has no built-in tuple key converter. Serialise composite keys as a single string instead.
- Custom collection types lacking a public parameterless constructor — the source generator rejects them at registration; use `IList<T>` etc. for the property type if you need a custom backing collection.
- Abstract or interface-only declarations (e.g. `IFoo Bar { get; }` with multiple concrete implementations).

A complete adopter contract for the strict path looks like:

```csharp
public static async Task<int> Main(string[] args)
    => await Marionette.Runtime.MarionetteHost.RunAsyncSourceGenSafe(
        args,
        Marionette.Generated.GeneratedManifest.Roots,
        adapter: BuildAdapter());
```

If you AOT-publish and want to verify nothing reaches the legacy path at runtime, drive the app via the StdioTest handshake harness (`.phase0/StdioTest`) — the harness exercises every meta-tool, every observable read, every event resource, and at least one dynamic per-method tool invocation. PHASE10_FINDINGS.md and PHASE11_FINDINGS.md document the verified scorecard per adapter.

## Adapter Guidance

The source generator is the main reason stripping works. Avoid adding runtime reflection over user roots or attributes in adapter code. If an adapter needs reflection for framework event plumbing, isolate it to the adapter and annotate the method with a focused trim warning justification.

## Consumer Guidance

Use explicit properties when validating a package:

```powershell
dotnet publish .\samples\Sample.Wpf.TodoApp\Sample.Wpf.TodoApp.csproj -c Release -p:EnableMcpAutomation=false
dotnet publish .\samples\Sample.Wpf.TodoApp\Sample.Wpf.TodoApp.csproj -c Release -p:EnableMcpAutomation=true
```

The first command is the production footprint check. The second command is the frozen MCP tool check.
