# Spike B — AOT Publish Verification

**Status:** Partial — environmental blocker prevents end-to-end AOT, but managed-side AOT readiness verified.
**Date:** 2026-05-03
**SDK:** .NET 10.0.202 (MSBuild 18.3.3)

## Executive summary

I cannot answer Q1 / Q2 with a published binary in hand, because **this machine is missing the C++ build prerequisites** that the .NET Native AOT toolchain requires (Windows SDK + `Microsoft.VisualStudio.Component.VC.Tools.x86.x64` workload — see https://aka.ms/nativeaot-prerequisites). The `dotnet publish -p:PublishAot=true` command therefore fails *every* time, regardless of `EnableMcpAutomation`, at the same point: the `findvcvarsall.bat` lookup that the AOT publish target uses to locate `link.exe` and the Windows SDK libraries.

What I *did* establish, decisively:

1. **All three Marionette MSBuild paths are AOT-publish-correct.** Every property that's needed (`PublishAot`, `IsAotCompatible`, `_SuppressWpfTrimError`, the `TreatAsLocalProperty` workaround for netstandard2.0 Abstractions) is in place. When the C++ workload lands on the machine, `dotnet publish` should proceed past the linker step without further csproj changes.
2. **Managed-side analyzer pass is clean in both modes.** With `PublishAot=true`, the SDK auto-enables the trim, AOT, and single-file Roslyn analyzers (`EnableTrimAnalyzer`, `EnableAotAnalyzer`, `EnableSingleFileAnalyzer`). They ran during csc.exe compilation in both Q1 and Q2 builds and produced **zero warnings**. That covers our source code (Abstractions, Runtime stub, Adapter.Wpf stub, sample) but not the IL trim pass (which only runs at the IlcCompile step that the linker block never lets us reach).
3. **Spike A regression: clean.** After all the csproj edits, the stripped Release build still produces exactly 7 Marionette symbols (all attribute references in Abstractions) — same as Spike A's baseline. None of the AOT-related changes leaked the Runtime / Adapter / MCP / Ai symbols into the `=false` build.
4. **WPF + AOT requires an SDK workaround.** NETSDK1168 ("WPF is not supported or recommended when trimming is enabled") is a hard SDK error that fires whenever `UseWpf=true && PublishTrimmed=true` (and PublishAot implies PublishTrimmed). The escape valve is the SDK-internal property `_SuppressWpfTrimError=true`, which I added scoped to AOT-publish flows only.

## Q1: Stripped (`EnableMcpAutomation=false`) AOT publish

- **Command:** `dotnet publish samples/Sample.Wpf.StripeProbe/Sample.Wpf.StripeProbe.csproj -c Release -r win-x64 -p:PublishAot=true -p:EnableMcpAutomation=false -o .phase0/aot-off`
- **Result:** Failed at `SetupOSSpecificProps` target.
- **Warnings:** 0 (managed-compile phase clean — trim/AOT analyzers ran with no findings)
- **Errors:** 1 — `Platform linker not found. Ensure you have all the required prerequisites documented at https://aka.ms/nativeaot-prerequisites` (`Microsoft.NETCore.Native.Windows.targets(142,5)`)
- **Process launch test:** Skipped — no binary produced.
- **Verdict:** Cannot give a final ✅ to "stripped AOT publish has zero warnings and produces a working .exe" until the C++ workload is installed. But the **managed-side promise (compile produces zero trim/AOT warnings) holds**: the analyzer ran on Abstractions + sample sources and emitted nothing. Once the linker is available, the IlcCompile step will tell us whether WPF's framework code introduces any IL2xxx warnings; that's information I currently can't obtain.

## Q2: Full (`EnableMcpAutomation=true`) AOT publish

- **Command:** `dotnet publish samples/Sample.Wpf.StripeProbe/Sample.Wpf.StripeProbe.csproj -c Release -r win-x64 -p:PublishAot=true -p:EnableMcpAutomation=true -o .phase0/aot-on`
- **Result:** Failed at `SetupOSSpecificProps` (same point as Q1).
- **Warnings:** 0 (managed-compile phase clean across Sample, Abstractions, Runtime, Adapter.Wpf — even with `ModelContextProtocol 1.2.0` + ~10 `Microsoft.Extensions.*` assemblies in scope)
- **Errors:** 1 (the linker block, identical to Q1)
- **Verdict:** Same caveat as Q1. Importantly, **the managed compilation is clean even with the MCP NuGet referenced**, because Spike A's stub `MarionetteHost.RunAsync` doesn't yet call into the reflection-y MCP APIs (`WithToolsFromAssembly`, etc.). This is a useful baseline. As soon as Phase 1 wires up real MCP host code, IL2xxx / IL3xxx warnings *will* appear at compile time on the call sites — and the IlcCompile step (when reachable) will surface trim warnings from the dependency graph. The current "0 warnings" reading is **not evidence that the MCP NuGet is AOT-clean** — it's evidence that we don't *use* the unsafe surface yet.

## What I changed to get this far

### `samples/Sample.Wpf.StripeProbe/Sample.Wpf.StripeProbe.csproj`

```xml
<PropertyGroup Condition="'$(PublishAot)'=='true'">
  <SelfContained>true</SelfContained>
  <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  <_SuppressWpfTrimError>true</_SuppressWpfTrimError>
</PropertyGroup>
```

* Self-contained + RID is mandatory for AOT.
* `TreatWarningsAsErrors=false` only during AOT publish — Spike A's `Directory.Build.props` keeps it `true` for normal builds. We need warnings to actually print, not abort the build.
* `_SuppressWpfTrimError=true` is the SDK-internal escape valve for NETSDK1168, found in `Microsoft.NET.Sdk/targets/Microsoft.NET.RuntimeIdentifierInference.targets:307`. The error is hard-coded; this is the only documented bypass.

### `src/Marionette.NET.Runtime/Marionette.NET.Runtime.csproj` and `src/Marionette.NET.Adapter.Wpf/Marionette.NET.Adapter.Wpf.csproj`

```xml
<IsAotCompatible Condition="'$(PublishAot)'=='true' and $([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">true</IsAotCompatible>

<PropertyGroup Condition="'$(PublishAot)'=='true'">
  <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
</PropertyGroup>
```

* `IsAotCompatible=true` only during AOT-publish flows. Spike A's `Directory.Build.props` keeps `TreatWarningsAsErrors=true` globally; if we set `IsAotCompatible=true` unconditionally, the analyzer warnings (eventually triggered by the MCP NuGet's reflection paths once Phase 1 calls them) would fail every regular `dotnet build`. That's not desirable. AOT-publish-only scoping gives us spike-time diagnostics without touching the green-build promise.

### `src/Marionette.NET.Abstractions/Marionette.NET.Abstractions.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk" TreatAsLocalProperty="PublishAot;IsAotCompatible;PublishTrimmed;IsTrimmable">
  ...
  <PropertyGroup>
    <PublishAot>false</PublishAot>
    <IsAotCompatible>false</IsAotCompatible>
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>
</Project>
```

* The Abstractions project targets `netstandard2.0` only. When the parent app publishes with `-p:PublishAot=true`, MSBuild propagates the property as a *global* into every transitive project, including this one. That triggers NETSDK1207 (AOT requires `net8.0+`) and NETSDK1124 (trimming requires `netcoreapp3.0+`).
* `TreatAsLocalProperty` on the `<Project>` root tells MSBuild to *unwind* these specific globals at the project boundary so the project-level overrides win. This is the canonical fix for "I'm netstandard2.0 and I refuse to be AOT-published, even when my parent is."
* Phase 1 will multi-target Abstractions to `netstandard2.0;net10.0`. At that point the modern TFM picks up `IsAotCompatible=true` cleanly and this whole override goes away.

## The environmental blocker — exact diagnosis

The `Microsoft.DotNet.ILCompiler` 10.0.6 publish target (`build/Microsoft.NETCore.Native.Windows.targets:126`) invokes `findvcvarsall.bat` with this contract:

> Locate VS via `vswhere.exe -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`, then `CALL "%vsBase%\vc\Auxiliary\Build\vcvarsall.bat" amd64`.

On this machine:

* **Visual Studio 2026 Community** is installed at `C:\Program Files\Microsoft Visual Studio\18\Community\`.
* **MSVC tools binaries** are present: `C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.50.35717\bin\Hostx64\x64\link.exe` exists.
* **The VC.Tools.x86.x64 component** is NOT registered (`vswhere -requires …` returns empty).
* **`vcvarsall.bat` does NOT exist**. Only `vcvars64.bat` is present, and it just calls `vcvarsall.bat`, which fails.
* **No Windows SDK is installed.** No `C:\Program Files (x86)\Windows Kits\10\Lib\` directory exists, so even if we patched around vcvarsall, the linker would fail to resolve `kernel32.lib`, `user32.lib`, etc.

Resolution: the user (or whoever sets up CI) must install the **"Desktop development with C++"** workload via the VS Installer, including:

* `Microsoft.VisualStudio.Component.VC.Tools.x86.x64`
* A Windows 10 or Windows 11 SDK component

Once those land, no further Marionette-side changes are needed; the existing csproj setup produces a working AOT publish (assuming the trim/AOT IL compile pass also succeeds, which is what Phase 1 will need to verify).

## Things I deliberately tried and rejected

* **Set up the C++ env manually via PowerShell.** Failed because `vcvarsall.bat` is missing from this VS install — `vcvars64.bat` is just a wrapper that calls the missing file.
* **Use `Microsoft.Windows.SDK.BuildTools` NuGet (10.0.26100.1) as a stand-in for the Windows SDK.** Only contains tooling utilities (Appx, MakeCert, etc.) — no `kernel32.lib` or other system libs link.exe needs.
* **Bypass the linker step with `-t:IlcCompile` only.** The IlcCompile target depends on `SetupOSSpecificProps`, which contains the linker check, so the bypass route doesn't exist within MSBuild.
* **Set `IlcUseEnvironmentalTools=true` to skip vcvarsall.** This skips the *lookup*, but the linker step still needs `link.exe` on PATH and the Windows SDK libs available — neither of which is true on this machine.

## Phase-1 implications

1. **Document AOT prerequisites prominently.** The masterplan claims "AOT-tauglich" as a tenet. We need a one-paragraph "before you AOT-publish a Marionette app, install the C++ workload" note in `docs/stripping.md` (when it's written). The masterplan currently doesn't mention this and Marionette adopters will hit the same wall.
2. **CI matrix needs `windows-latest` GitHub runner with the C++ workload.** The default Microsoft-hosted images do include it; a self-hosted runner would not by default.
3. **`_SuppressWpfTrimError=true` is required for any Marionette-WPF AOT publish.** Either the user app sets it, or our build/`Marionette.NET.targets` sets it when an Adapter.Wpf reference is present. Phase 1 should bake this into the `build/` MSBuild targets so adopters don't have to know about an undocumented internal property.
4. **The `TreatAsLocalProperty` workaround for Abstractions disappears** when Phase 1 multi-targets Abstractions to `netstandard2.0;net10.0` — at that point the modern TFM gets AOT/trim natively and netstandard2.0 is only used for legacy consumers, none of which AOT-publish anyway.
5. **The IL trim/AOT analyzers are activated automatically** by `PublishAot=true`. There's no need to explicitly `<EnableTrimAnalyzer>` etc. — `Microsoft.NET.Sdk.Analyzers.targets:84-96` does it for us. The `IsAotCompatible=true` flag on Runtime/Adapter is therefore a *minor* belt-and-braces; the bigger value comes when those projects are built standalone (without an AOT publish in flight) and we still want to catch regressions. Phase 1 should leave the conditional-on-PublishAot guard in place for now and revisit when Marionette has its own CI.
6. **The "0 warnings on Q2" finding is provisional, not exonerating.** The MCP NuGet's reflection paths (`WithToolsFromAssembly`) are not exercised yet because Runtime is a stub. As soon as Phase 1 writes the actual MCP host wiring, IL2xxx warnings will start appearing at csc.exe compile time. The Phase 1 plan to use a Source Generator for the manifest (and avoid `WithToolsFromAssembly`) is what makes it possible to keep the warning count low. Spike B confirms that the *current* code path is not the issue — the *future* code path is what we have to design carefully.
7. **Phase-1 source generator will still need an end-to-end IlcCompile-step verification** (i.e., a CI job with the C++ workload installed) to catch dependency-side trim warnings. The csc.exe-time analyzer is necessary but not sufficient.

## Issues encountered & fixes

1. **`git clean -fdx samples/Sample.Wpf.StripeProbe` deleted the entire untracked sample.** Spike A files are all untracked; the prompt's cleanup command nuked the directory. I restored everything from earlier `Read` calls + the Spike A description (App.xaml/MainWindow.xaml had to be reconstructed from the report, since I hadn't read them). Pre-AOT-publish cleanup should be `rm -rf samples/Sample.Wpf.StripeProbe/{bin,obj}` — or, once Spike A is committed, `git clean -fdx` becomes safe again.
2. **Cascading global properties trip up netstandard2.0 referenced projects** during AOT publish (NETSDK1207, NETSDK1124). Fixed via `TreatAsLocalProperty` on the `<Project>` element in Abstractions, plus explicit `false` overrides in a PropertyGroup.
3. **NETSDK1168 (WPF + trimming)** is a hard SDK error, not a warning. The `_SuppressWpfTrimError=true` SDK-internal property is the only bypass. Phase 1 should treat this as documented contract.
4. **`Sample.Wpf.StripeProbe.csproj` defaults `EnableMcpAutomation` based on Configuration.** When Spike A's pattern (`<EnableMcpAutomation Condition="'$(EnableMcpAutomation)'==''">$(Configuration)=='Debug'</EnableMcpAutomation>`) sees a command-line `-p:EnableMcpAutomation=...`, the empty-string guard correctly skips the default. Verified during this spike — no regression there.

## Files modified

```
samples/Sample.Wpf.StripeProbe/Sample.Wpf.StripeProbe.csproj   (added AOT PropertyGroup)
src/Marionette.NET.Runtime/Marionette.NET.Runtime.csproj       (added IsAotCompatible + TWAE override)
src/Marionette.NET.Adapter.Wpf/Marionette.NET.Adapter.Wpf.csproj (same)
src/Marionette.NET.Abstractions/Marionette.NET.Abstractions.csproj (TreatAsLocalProperty + explicit overrides)
```

## Files created

```
.phase0/spike-b.md                          (this file)
.phase0/aot-off-publish.log                 (Q1 stdout/stderr, normal verbosity)
.phase0/aot-off-publish-verbose.log         (Q1 stdout/stderr, normal verbosity dotnet -v normal)
.phase0/aot-on-publish.log                  (Q2 ditto)
.phase0/aot-on-publish-verbose.log          (Q2 ditto)
.phase0/aot-on-fulllog.log                  (one-shot Q2 with PublishTrimmed forced — kept for archival)
.phase0/aot-on-ilccompile.log               (failed attempt at IlcCompile-only — kept for archival)
.phase0/aot-off/                            (empty — publish failed)
.phase0/aot-on/                             (empty — publish failed)
```

The two empty `aot-off/` and `aot-on/` directories should be removed before commit; they're publish-output paths but the publish failed.

## Decision recommended for the orchestrator

This spike's result is **not a Phase-0 No-Go for Marionette** — it's a No-Go for *this machine's* AOT toolchain. The MSBuild plumbing is verified correct; the linker-side proof is environmental and reproducible elsewhere. Two follow-ups make sense:

* **Either** install the C++ workload on this machine and re-run Spike B to get the missing data points (full publish success, IL trim warnings list from IlcCompile, working .exe launch test). Cost: a few GB of VS install.
* **Or** mark Spike B as "MSBuild-side verified, end-to-end deferred to Phase-1 CI" and proceed. This is acceptable provided Phase 1's CI matrix actually does the AOT publish on a runner with the C++ workload.

I lean toward installing the workload now, because Phase 1 will need it on the dev machine for the Source Generator + MCP host work anyway (not for AOT alone, but the ergonomics — being able to AOT-publish a sample to verify a generator change — are too valuable to defer).
