# Phase 1.a — Abstractions Full + MSBuild Targets

**Status:** PASS
**Date:** 2026-05-03
**SDK:** .NET 10.0.202

## What changed

### `src/Marionette.NET.Abstractions/Marionette.NET.Abstractions.csproj` — multi-target

Replaced `<TargetFramework>netstandard2.0</TargetFramework>` with
`<TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>`. Removed the
Spike-B `TreatAsLocalProperty="PublishAot;IsAotCompatible;PublishTrimmed;IsTrimmable"`
on the `<Project>` element and the explicit `<PublishAot>false</PublishAot>` /
`<IsAotCompatible>false</IsAotCompatible>` / `<PublishTrimmed>false</PublishTrimmed>`
overrides. Per PHASE0_FINDINGS implication 2, the modern TFM picks up the
analyzer pack natively, so the workaround for AOT-property propagation into
the netstandard2.0-only project is no longer necessary. `IsTrimmable` and
`IsAotCompatible` are now set unconditionally inside a PropertyGroup gated on
`net8.0+` via `IsTargetFrameworkCompatible`.

Added `<InternalsVisibleTo Include="Marionette.NET.Runtime" />` so the runtime
can populate the new internal hooks on `Marionette.Ai`.

### `src/Marionette.NET.Abstractions/McpAttributes.cs` — production-ready attributes

Replaced the four Spike-A stub attributes with full sealed/immutable
implementations:

| Type | Targets | Members |
|---|---|---|
| `McpRootAttribute` | Class | `Name?` (optional override; null defaults to type name). Two ctors: parameterless and `(string name)`. |
| `McpCallableAttribute` | Method | `Description` (ctor), `OffUiThread` init-only bool (default false), `TimeoutSeconds` init-only int (default 0 = no timeout). |
| `McpObservableAttribute` | Property | `Description` (ctor), `Watchable` init-only bool (default false), `PollingIntervalMs` init-only int (default 500). |
| `McpTriggerableAttribute` | Property | `Description` (ctor), `Strategy` init-only `TriggerStrategy` (default `Semantic`). |
| `TriggerStrategy` (enum) | — | `Semantic = 0`, `EventSystem = 1`, `InputSystem = 2`. Same namespace `Marionette`. |

All attributes `sealed`, `Inherited = false`, `AllowMultiple = false`. All
settable members `init`-only. Full XML doc on every public member.

### `src/Marionette.NET.Abstractions/Internal/IsExternalInit.cs` — netstandard2.0 polyfill

`init`-only setters require `System.Runtime.CompilerServices.IsExternalInit`,
which ships in net5.0+ but not in netstandard2.0. Added an internal
file-scoped polyfill (`#if NETSTANDARD2_0`) so the multi-target build
compiles cleanly on both TFMs. On net10.0 the framework type wins.

### `src/Marionette.NET.Abstractions/Ai.cs` — runtime hooks + IsActive

Kept the `[Conditional("MCP_ENABLED")]` on `Trigger` and `ScheduleTrigger`.
Added two `internal static Action?`-typed hooks (`TriggerHook`,
`ScheduleTriggerHook`) populated by the runtime via `InternalsVisibleTo`. The
public methods invoke the hook when present. Added `public static bool
IsActive => TriggerHook is not null` (not `[Conditional]` — adopters need to
read it from non-conditional code). XML doc explicitly notes the stripping
behaviour and that `IsActive` returns `false` whenever the runtime is not
loaded.

### `build/Marionette.NET.props` — NEW

Sets `EnableMcpAutomation` default if not already set:
`<EnableMcpAutomation Condition="'$(EnableMcpAutomation)'==''">$(Configuration)=='Debug'</EnableMcpAutomation>`.
File header documents the auto-import-from-NuGet contract for Phase 7.

### `build/Marionette.NET.targets` — NEW

Two property contributions, both gated on `EnableMcpAutomation=true`:

1. `<DefineConstants>$(DefineConstants);MCP_ENABLED</DefineConstants>` — keeps
   `[Conditional("MCP_ENABLED")]` call sites and lights up `#if MCP_ENABLED`
   blocks in user code.
2. `<_SuppressWpfTrimError>true</_SuppressWpfTrimError>` — only when
   `UseWPF=true && PublishAot=true`. Per PHASE0_FINDINGS implication 3, this
   is the only documented bypass for NETSDK1168 on WPF AOT publish.

File header documents the NuGet auto-import contract.

### `samples/Sample.Wpf.StripeProbe/Sample.Wpf.StripeProbe.csproj` — wired to build/*

Replaced the inline `EnableMcpAutomation` default and `MCP_ENABLED` define
with `<Import Project="..\..\build\Marionette.NET.props" />` at the top and
`<Import Project="..\..\build\Marionette.NET.targets" />` at the bottom.
Removed the inline `_SuppressWpfTrimError=true` (now contributed by the
shared targets file). Kept project-specific bits: `UseWPF`,
`EnableDefaultApplicationDefinition=false`, `StartupObject`, the conditional
Adapter.Wpf reference, and the AOT-publish-only `SelfContained`/
`TreatWarningsAsErrors=false`.

## Build matrix results

All commands run from `C:\Home\Code\nw.Automation` after a clean of all
`bin`/`obj` directories. .NET 10.0.202.

| # | Command | Result |
|---|---|---|
| 1 | `dotnet build Marionette.NET.sln -c Debug` | PASS — 0 warnings, 0 errors |
| 2 | `dotnet build Marionette.NET.sln -c Release` | PASS — 0 warnings, 0 errors |
| 3 | `dotnet build samples/Sample.Wpf.StripeProbe/... -c Release -p:EnableMcpAutomation=false` | PASS — 0 warnings, 0 errors; 7-file stripped output |
| 4 | `dotnet build samples/Sample.Wpf.StripeProbe/... -c Release -p:EnableMcpAutomation=true` | PASS — 0 warnings, 0 errors; 42-file MCP-on output |
| 5 | `dotnet build samples/Sample.Wpf.StripeProbe/... -c Debug -p:EnableMcpAutomation=true` | PASS — 0 warnings, 0 errors |
| 6 | `pwsh build/Run-IlProbe.ps1 -ProbeDll ... -Target ...Sample.Wpf.StripeProbe.dll` (cmd 3 output) | PASS — 0 hits across all 4 needles |
| 7 | `dotnet build .phase0/StdioTest/StdioTest.csproj -c Debug` | PASS — 0 warnings, 0 errors |
| 8 | `dotnet .phase0/StdioTest/.../StdioTest.dll <Sample.Wpf.StripeProbe.exe>` | PASS — 4/4 handshake checks |

Multi-target verification: after rebuild, both
`src/Marionette.NET.Abstractions/bin/Release/netstandard2.0/Marionette.NET.Abstractions.dll`
and `src/Marionette.NET.Abstractions/bin/Release/net10.0/Marionette.NET.Abstractions.dll`
are produced.

Additional sanity check (not in the matrix but worth recording): running
`dotnet msbuild ... -getProperty:DefineConstants` for both modes confirms
the targets file's `MCP_ENABLED` contribution flows through correctly:

| Configuration | -p:EnableMcpAutomation | Resolved DefineConstants |
|---|---|---|
| Debug   | true  | `TRACE;MCP_ENABLED;DEBUG` |
| Release | false | `TRACE;RELEASE` (no MCP_ENABLED) |
| Debug   | false (override) | `TRACE;DEBUG` (no MCP_ENABLED) |

## IL probe result + stdio handshake result

### IL probe (cmd 6) — `Sample.Wpf.StripeProbe.dll` from cmd 3

```
[PASS] Marionette.NET.Runtime: TOTAL hits across 1 file(s): 0
[PASS] Adapter.Wpf:            TOTAL hits across 1 file(s): 0
[PASS] Marionette.Ai:          TOTAL hits across 1 file(s): 0
[PASS] ModelContextProtocol:   TOTAL hits across 1 file(s): 0
PASS — stripped build contains zero forbidden symbols.
```

The masterplan's load-bearing stripping promise (Phase 0 Spike A) survives
Phase 1.a unchanged.

### Stdio handshake (cmd 8) — Debug build with MCP_ENABLED

```
PASS - initialize handshake (server: Marionette.NET 0.0.1-spike-c, protocol 2025-11-25)
PASS - tools/list contains marionette_ping
PASS - tools/call marionette_ping returned "pong"
PASS - child exited cleanly with code 0
stdout summary: 3 JSON-RPC frames, 0 pollution lines
stderr total:   11 lines (SDK information logs only)
```

Identical behaviour to Spike C baseline. The stripped/MCP-on build output
locations and counts (7 vs 42 files) match the post-Spike-C state.

## Files changed

```
src/Marionette.NET.Abstractions/Marionette.NET.Abstractions.csproj   (multi-target, removed TreatAsLocalProperty workaround, added InternalsVisibleTo)
src/Marionette.NET.Abstractions/McpAttributes.cs                      (production attribute set + TriggerStrategy enum)
src/Marionette.NET.Abstractions/Ai.cs                                 (runtime hooks + IsActive property)
src/Marionette.NET.Abstractions/Internal/IsExternalInit.cs            (NEW — netstandard2.0 polyfill)
build/Marionette.NET.props                                             (NEW — EnableMcpAutomation default)
build/Marionette.NET.targets                                           (NEW — MCP_ENABLED + _SuppressWpfTrimError)
samples/Sample.Wpf.StripeProbe/Sample.Wpf.StripeProbe.csproj          (Imports build/*; removed inline default + define + _SuppressWpfTrimError)
.phase1/1a-foundation.md                                               (NEW — this report)
```

Files deliberately not changed: `MASTERPLAN.md`, `README.md`, `LICENSE`,
`.gitignore`, `global.json`, `Directory.Build.props`, all of `.phase0/*`,
`src/Marionette.NET.Runtime/*`, `src/Marionette.NET.Adapter.Wpf/*`,
`samples/Sample.Wpf.StripeProbe/{App.xaml,App.xaml.cs,MainWindow.xaml,MainWindow.xaml.cs,Program.cs}`.

## Issues encountered

1. **CS0518 `IsExternalInit` not defined on netstandard2.0.** Init-only
   setters require this compiler-recognized type, which ships only in
   net5.0+. Resolved by adding a 6-line `internal static class IsExternalInit`
   polyfill gated on `#if NETSTANDARD2_0`. The framework type wins on net10.0.

2. **CS0649 on the new `Ai.TriggerHook` / `ScheduleTriggerHook` fields.** The
   compiler cannot see assignments performed reflectively from another
   assembly, so it warns that the field is never written. Resolved with a
   tightly-scoped `#pragma warning disable CS0649 / restore CS0649` around
   the two declarations and an `InternalsVisibleTo` to
   `Marionette.NET.Runtime` in the csproj so the runtime can write them
   directly without reflection (Phase 1.b/1.c will exercise this).

3. **`pwsh` not on PATH in the bash sandbox** — the IL probe script is a
   PowerShell script. Ran it via the dedicated PowerShell tool instead;
   `Run-IlProbe.ps1` itself is unchanged and works as documented.

4. **MCP-on file count is 42, not 22** as Spike A reported. Investigated and
   confirmed this is the post-Spike-C baseline: Spike C added the
   `Microsoft.Extensions.Hosting` package reference to `Marionette.Runtime`,
   which transitively pulls roughly 20 `Microsoft.Extensions.*` assemblies.
   Spike-B-followup also confirmed this set survives AOT cleanly. **Not a
   regression introduced by Phase 1.a** — the same count appeared in the
   pre-Phase-1.a state.

## Notes for Phase 1.b (Source Generator)

The Source Generator can rely on the following stable API surface:

### Fully-qualified attribute names (use these in `SyntaxFactory.ParseAttributeName` / `IsAttributeMatch`)

```
Marionette.McpRootAttribute
Marionette.McpCallableAttribute
Marionette.McpObservableAttribute
Marionette.McpTriggerableAttribute
```

### Fully-qualified enum

```
Marionette.TriggerStrategy { Semantic = 0, EventSystem = 1, InputSystem = 2 }
```

### Constructor signatures (all canonical)

| Attribute | Ctor(s) |
|---|---|
| `McpRootAttribute` | `()`, `(string name)` |
| `McpCallableAttribute` | `(string description)` |
| `McpObservableAttribute` | `(string description)` |
| `McpTriggerableAttribute` | `(string description)` |

### Init-only properties to read

| Attribute | Property | Type | Default |
|---|---|---|---|
| `McpRootAttribute`        | `Name`              | `string?`           | `null` (use type name) |
| `McpCallableAttribute`    | `Description`       | `string`            | required (ctor) |
| `McpCallableAttribute`    | `OffUiThread`       | `bool`              | `false` |
| `McpCallableAttribute`    | `TimeoutSeconds`    | `int`               | `0` (no timeout) |
| `McpObservableAttribute`  | `Description`       | `string`            | required (ctor) |
| `McpObservableAttribute`  | `Watchable`         | `bool`              | `false` |
| `McpObservableAttribute`  | `PollingIntervalMs` | `int`               | `500` |
| `McpTriggerableAttribute` | `Description`       | `string`            | required (ctor) |
| `McpTriggerableAttribute` | `Strategy`          | `TriggerStrategy`   | `TriggerStrategy.Semantic` |

### Namespace reservation

- All public surface lives in `Marionette`.
- Internals in `Marionette.Internal`.
- The Source Generator must emit code in `Marionette.Generated` — that
  namespace is reserved and no other code in the repo uses it.

### `Ai` channel hooks

The runtime populates `Marionette.Ai.TriggerHook` and
`Marionette.Ai.ScheduleTriggerHook` (both `internal static Action…`). They
are accessible from the runtime assembly thanks to the `InternalsVisibleTo`
on Abstractions. The Source Generator does not need to touch these — they
are runtime-only — but should be aware that emitting code that *does* touch
them must be in `Marionette.NET.Runtime`, not in the user assembly.

### Multi-target consideration

Abstractions ships both `netstandard2.0` and `net10.0` builds from the same
sources. The Source Generator should pin against the public attribute API
(which is identical across both TFMs) and not rely on framework-specific
features. The internal hook fields and `InternalsVisibleTo` matter only on
the Runtime side; the user assembly only ever sees the public attribute
contract.

### `_SuppressWpfTrimError` ownership

Phase 1.a moved this property from the sample's csproj into
`build/Marionette.NET.targets`. Phase 1.b's source generator must not assume
the user csproj sets it; instead, the meta-package's `targets` file owns the
WPF+AOT escape valve. If a future Avalonia / WinUI / Uno / MAUI adapter
needs analogous SDK escapes, those go alongside the WPF condition in the
same shared targets file, not into adapter-specific csprojs.
