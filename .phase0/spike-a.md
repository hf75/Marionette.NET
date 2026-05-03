# Spike A — IL-Stripping Verification

**Status:** Pass
**Date:** 2026-05-03
**SDK:** .NET 10.0.202

## What I built

A four-project scaffold matching the masterplan layout, plus one WPF probe sample:

| Project | TFM | Role |
|---|---|---|
| `src/Marionette.NET.Abstractions/` | `netstandard2.0` | Sealed attributes (`McpCallable`, `McpObservable`, `McpTriggerable`, `McpRoot`) and the `Ai` channel-push stub with `[Conditional("MCP_ENABLED")]` on every method. Always referenced. |
| `src/Marionette.NET.Runtime/` | `net10.0` | Stub `MarionetteHost.RunAsync(...)` that writes a stderr breadcrumb. References the `ModelContextProtocol 1.2.0` NuGet so a missing package would surface here. |
| `src/Marionette.NET.Adapter.Wpf/` | `net10.0-windows`, `UseWPF=true` | Stub `WpfMarionetteBootstrap.Initialize(Application)`. References Runtime. |
| `samples/Sample.Wpf.StripeProbe/` | `net10.0-windows`, `UseWPF=true`, `WinExe` | Minimal WPF app: `App.xaml`, `MainWindow.xaml` with a button + label. Code-behind has `[McpCallable] Add(int,int)`, `[McpObservable] Result`, and a button click handler that also calls `Ai.Trigger(...)` to verify the channel-push elision. The class is decorated `[McpRoot]`. |

Plus:

- `Marionette.NET.sln` with all four projects under `src/` and `samples/` solution folders.
- `Directory.Build.props` at solution root with `<LangVersion>latest</LangVersion>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<Deterministic>true</Deterministic>`.
- `.phase0/ProbeIl/` — a tiny `net10.0` console app using `System.Reflection.Metadata.PEReader` to enumerate `AssemblyReference`, `TypeReference`, `TypeDefinition`, and `MemberReference` rows whose names contain a needle. No assembly is loaded into the runtime, so this works even on `net10.0-windows` outputs we don't want to pin.

The probe is **outside** the solution intentionally (Phase-0 utility, not shipping code). Its `csproj` overrides `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` from the inherited Directory.Build.props.

## What I verified

Build commands (all from `C:\Home\Code\nw.Automation`):

```
dotnet build Marionette.NET.sln -c Debug                                             # 0 errors, 0 warnings
dotnet build Marionette.NET.sln -c Release                                           # 0 errors, 0 warnings
dotnet build samples\Sample.Wpf.StripeProbe\Sample.Wpf.StripeProbe.csproj -c Release -p:EnableMcpAutomation=false   # 0 errors, 0 warnings
dotnet build samples\Sample.Wpf.StripeProbe\Sample.Wpf.StripeProbe.csproj -c Release -p:EnableMcpAutomation=true    # 0 errors, 0 warnings
```

After each StripeProbe build I cleaned `samples/Sample.Wpf.StripeProbe/{bin,obj}` and rebuilt to make sure the inspected output reflected the current property and not stale incremental output. Snapshots were copied to:

- `.phase0/probe-off/` — the `EnableMcpAutomation=false` Release output
- `.phase0/probe-on/`  — the `EnableMcpAutomation=true`  Release output

The probe was then run against both:

```
dotnet .phase0/ProbeIl/bin/Release/net10.0/ProbeIl.dll Marionette                .phase0/probe-off
dotnet .phase0/ProbeIl/bin/Release/net10.0/ProbeIl.dll Marionette.NET.Runtime    .phase0/probe-off
dotnet .phase0/ProbeIl/bin/Release/net10.0/ProbeIl.dll Adapter.Wpf               .phase0/probe-off
dotnet .phase0/ProbeIl/bin/Release/net10.0/ProbeIl.dll Marionette.Ai             .phase0/probe-off
dotnet .phase0/ProbeIl/bin/Release/net10.0/ProbeIl.dll ModelContextProtocol      .phase0/probe-off
dotnet .phase0/ProbeIl/bin/Release/net10.0/ProbeIl.dll Marionette                .phase0/probe-on/Sample.Wpf.StripeProbe.dll
```

`deps.json` was also inspected — that is the file the .NET host actually reads at startup to resolve dependencies, so a clean `deps.json` is the strongest single guarantee that nothing leaks in at runtime either.

## Findings

### bin folder contents — `EnableMcpAutomation=false` (7 files)
```
Marionette.NET.Abstractions.dll
Marionette.NET.Abstractions.pdb
Sample.Wpf.StripeProbe.deps.json
Sample.Wpf.StripeProbe.dll
Sample.Wpf.StripeProbe.exe
Sample.Wpf.StripeProbe.pdb
Sample.Wpf.StripeProbe.runtimeconfig.json
```

### bin folder contents — `EnableMcpAutomation=true` (22 files)
```
Marionette.NET.Abstractions.dll
Marionette.NET.Adapter.Wpf.dll
Marionette.NET.Runtime.dll
ModelContextProtocol.dll
ModelContextProtocol.Core.dll
Microsoft.Extensions.AI.Abstractions.dll
Microsoft.Extensions.Caching.Abstractions.dll
Microsoft.Extensions.Configuration.Abstractions.dll
Microsoft.Extensions.DependencyInjection.Abstractions.dll
Microsoft.Extensions.Diagnostics.Abstractions.dll
Microsoft.Extensions.FileProviders.Abstractions.dll
Microsoft.Extensions.Hosting.Abstractions.dll
Microsoft.Extensions.Logging.Abstractions.dll
Microsoft.Extensions.Options.dll
Microsoft.Extensions.Primitives.dll
Sample.Wpf.StripeProbe.{exe,dll,deps.json,runtimeconfig.json,pdb}
(+ pdbs for Adapter.Wpf, Runtime, Abstractions)
```

### Symbol counts

| Needle | probe-off hits in `Sample.Wpf.StripeProbe.dll` | probe-on hits in `Sample.Wpf.StripeProbe.dll` |
|---|---|---|
| `Marionette` (any) | 7 (all attribute references — Abstractions only) | 12 |
| `Marionette.NET.Runtime` | **0** | 0 (transitive only via Adapter) |
| `Adapter.Wpf` | **0** | 1 (`ASSEMBLY-REF Marionette.NET.Adapter.Wpf`) |
| `Marionette.Ai` | **0** | 2 (`TYPE-REF Marionette.Ai` + `MEMBER-REF Ai::Trigger`) |
| `ModelContextProtocol` | **0** | 0 in sample directly (transitive only — no direct usage) |

The full `Marionette.*` reference list in the **stripped** sample DLL is exactly the four attribute symbols and one assembly ref:

```
ASSEMBLY-REF  Marionette.NET.Abstractions, 0.0.1.0
TYPE-REF      Marionette.McpRootAttribute
TYPE-REF      Marionette.McpCallableAttribute
TYPE-REF      Marionette.McpObservableAttribute
MEMBER-REF    Marionette.McpRootAttribute::.ctor
MEMBER-REF    Marionette.McpCallableAttribute::.ctor
MEMBER-REF    Marionette.McpObservableAttribute::.ctor
```

`deps.json` for the stripped build only lists `Marionette.NET.Abstractions` — no Runtime, Adapter, MCP NuGet, or Microsoft.Extensions.* packages.

### Conclusion

- **Marionette symbols found in `=false` build (excluding Abstractions): 0.** Pass criterion met absolutely.
- The `[Conditional("MCP_ENABLED")]` attribute correctly elides `Ai.Trigger(...)` call sites in the sample, so user code can call the channel-push API without paying for it in stripped builds.
- The transitive Runtime + Adapter + ModelContextProtocol + ~10 `Microsoft.Extensions.*` DLLs all disappear cleanly in the `=false` build. No half-pulled references, no unresolved metadata.

The bonus question "can Abstractions also be omitted" is **no**, but that's by design and acceptable: the attributes need to live somewhere for source-gen scanning and IDE tooling, and Abstractions is `netstandard2.0` and tiny (no reflection, no IO, no allocations on hot paths). Phase 1 will measure its actual on-disk size as a sanity check.

## Issues encountered & fixes

1. **`IsTrimmable=true` / `IsAotCompatible=true` on `netstandard2.0` raises NETSDK1212 / NETSDK1210.**
   The .NET 10 SDK ships the trim/AOT analyzer pack starting at `net8.0`. Setting these on a netstandard2.0 project produces warnings even with `TreatWarningsAsErrors`. Fix: gate them on `$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))`. Once Phase 1 multi-targets Abstractions (`netstandard2.0;net10.0`) the modern TFM picks them up automatically.

2. **`-p:BaseOutputPath=...` causes duplicate AssemblyInfo errors in dependent projects.**
   First instinct was to redirect the StripeProbe build to an isolated `bin/probe-off/`. That confused the SDK: the dependent `Marionette.NET.Abstractions` project re-emitted AssemblyInfo into its alternate `obj/probe-off/` while the regular `obj/Release/` AssemblyInfo was still in the compile inputs, producing CS0579 dupes. Fix: don't reroute output paths — clean the sample's `bin/obj` between builds and copy the standard output to `.phase0/probe-{off,on}/` for archival.

3. **MSBuild evaluation of the `EnableMcpAutomation` default.**
   The pattern in the prompt — `<EnableMcpAutomation Condition="'$(EnableMcpAutomation)'==''">$(Configuration)=='Debug'</EnableMcpAutomation>` — works correctly. MSBuild evaluates `$(Configuration)=='Debug'` as a string-comparison expression and assigns the resulting `"true"` / `"false"` text into the property. No `<Choose>` / `<When>` workaround needed. Verified by observing that the solution-level Release build (no explicit property) produced exactly the same 7-file output as the explicit `-p:EnableMcpAutomation=false` build.

4. **`Sample.Wpf.StripeProbe.exe` has no metadata.**
   The .NET host emits a `.exe` shim (apphost) alongside the real managed `.dll`. The shim is unmanaged, which surprised the probe at first. The probe now reports `[no-metadata]` and counts zero, which is correct: the actual managed code lives in `Sample.Wpf.StripeProbe.dll`.

5. **Note for the orchestrator about `.phase0/`.**
   `.phase0/` is **not** ignored by `.gitignore`, so the report (`spike-a.md`), the probe sources (`ProbeIl/Program.cs`, `ProbeIl/ProbeIl.csproj`), and the snapshot folders (`probe-off/`, `probe-on/`) are all currently tracked content. The probe-snapshot folders contain compiled DLLs / EXEs which are not normally committed. Consider either (a) adding `.phase0/probe-off/`, `.phase0/probe-on/`, and `.phase0/ProbeIl/{bin,obj}/` to `.gitignore` before commit, or (b) deleting them after consolidation. The probe sources themselves are worth keeping — same script can be reused by Phase-1 CI.

## Recommendation for Phase 1

**Concept verified — proceed.** Stripping is total. The masterplan's "literal null MCP symbols in IL when EnableMcpAutomation=false" claim holds. Phase 1 can build out the SourceGenerator, real MCP host, and Adapter.Wpf in confidence that the runtime side is genuinely opt-in.

Two small refinements to carry forward:

- **CI-gate the IL probe.** Re-run `ProbeIl` in CI on every PR with the `=false` build of every sample, fail-closed on any non-Abstractions match. The probe binary is tiny and fast.
- **Phase 1 should re-validate after the Source Generator lands.** The generator emits a `__Manifest.g.cs` into the user assembly. Need to confirm that file references *only* Abstractions types (or compiles to constant data) — otherwise it could pull Runtime in by accident. The probe is the right tool for this check too.

## Files created

```
C:\Home\Code\nw.Automation\Directory.Build.props
C:\Home\Code\nw.Automation\Marionette.NET.sln

C:\Home\Code\nw.Automation\src\Marionette.NET.Abstractions\Marionette.NET.Abstractions.csproj
C:\Home\Code\nw.Automation\src\Marionette.NET.Abstractions\McpAttributes.cs
C:\Home\Code\nw.Automation\src\Marionette.NET.Abstractions\Ai.cs

C:\Home\Code\nw.Automation\src\Marionette.NET.Runtime\Marionette.NET.Runtime.csproj
C:\Home\Code\nw.Automation\src\Marionette.NET.Runtime\MarionetteHost.cs

C:\Home\Code\nw.Automation\src\Marionette.NET.Adapter.Wpf\Marionette.NET.Adapter.Wpf.csproj
C:\Home\Code\nw.Automation\src\Marionette.NET.Adapter.Wpf\WpfMarionetteBootstrap.cs

C:\Home\Code\nw.Automation\samples\Sample.Wpf.StripeProbe\Sample.Wpf.StripeProbe.csproj
C:\Home\Code\nw.Automation\samples\Sample.Wpf.StripeProbe\App.xaml
C:\Home\Code\nw.Automation\samples\Sample.Wpf.StripeProbe\App.xaml.cs
C:\Home\Code\nw.Automation\samples\Sample.Wpf.StripeProbe\MainWindow.xaml
C:\Home\Code\nw.Automation\samples\Sample.Wpf.StripeProbe\MainWindow.xaml.cs

C:\Home\Code\nw.Automation\.phase0\spike-a.md
C:\Home\Code\nw.Automation\.phase0\ProbeIl\ProbeIl.csproj
C:\Home\Code\nw.Automation\.phase0\ProbeIl\Program.cs
C:\Home\Code\nw.Automation\.phase0\probe-off\          (snapshot of stripped Release build)
C:\Home\Code\nw.Automation\.phase0\probe-on\           (snapshot of MCP-on Release build)
```
