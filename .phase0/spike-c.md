# Spike C — stdout/JSON-RPC Isolation Verification

**Status:** Pass
**Date:** 2026-05-03
**SDK:** .NET 10.0.202
**MCP NuGet:** ModelContextProtocol 1.2.0 (verified protocol version negotiated as `2025-11-25`)

## Executive summary

All three questions answered decisively. `Sample.Wpf.StripeProbe.exe --mcp --headless` and `--mcp` (GUI) both complete a real MCP `initialize` handshake, list tools, and dispatch `tools/call` round-trips over stdio with **zero stdout pollution**. Logs and any accidental console writes are forced to stderr by an `StdoutGuardWriter` installed before the MCP server starts, plus an explicit empty-builder strategy that suppresses the default Console logging provider that would otherwise corrupt the JSON-RPC stream. Spike A's IL-stripping baseline is unchanged.

## Q1 — `--mcp --headless` handshake works

- **Test harness:** `.phase0/StdioTest/` (a tiny `net10.0` console app spawning the sample as a child process with redirected stdin/stdout/stderr).
- **Sequence executed against the child:**
  1. `initialize` — request `protocolVersion=2025-11-25`, capabilities `{}`, clientInfo `{name=spike-c-harness, version=0.0.1}`.
  2. `notifications/initialized` — fire-and-forget.
  3. `tools/list` — request, expect `marionette_ping` in the array.
  4. `tools/call` with `name=marionette_ping`, `arguments={}` — expect `pong`.
  5. Close stdin, expect clean exit.
- **Result:** all four checks PASS, child exited cleanly with code 0 in ~0.3 s.

| Step | Result |
|---|---|
| Initialize handshake | PASS — server identified itself as `Marionette.NET 0.0.1-spike-c, protocol 2025-11-25` |
| `tools/list` | PASS — `marionette_ping` present |
| `tools/call marionette_ping` -> `pong` | PASS — content array carried `{type: "text", text: "pong"}` |
| Stdout purity | PASS — exactly 3 JSON-RPC frames, **0 pollution lines** |
| Clean shutdown on stdin EOF | PASS — child exited with code 0 |

**stdout content (every line):**

```
{"result":{"protocolVersion":"2025-11-25","capabilities":{"logging":{},"tools":{"listChanged":true}},"serverInfo":{"name":"Marionette.NET","version":"0.0.1-spike-c"}},"id":2,"jsonrpc":"2.0"}
{"result":{"tools":[{"name":"marionette_ping","description":"Liveness probe. Always returns the string "pong".","inputSchema":{"type":"object","properties":{}}}]},"id":3,"jsonrpc":"2.0"}
{"result":{"content":[{"type":"text","text":"pong"}]},"id":4,"jsonrpc":"2.0"}
```

**stderr summary (11 lines, all expected):** the SDK's own `[Information]`-level traces from `ModelContextProtocol.Server.StdioServerTransport` and `ModelContextProtocol.Server.McpServer` reporting handler entry/exit and one tool execution. No errors, no warnings, no orphan output. Captured verbatim in `.phase0/stdio-handshake-final.log`.

## Q2 — stdout pollution sources

Verified by adding gated `#if`-free probe code to `MarionetteHost.RunAsync` behind the `MARIONETTE_STDOUT_PROBE=1` env var, then running the harness with `--probe`. The probe-mode log lives at `.phase0/stdio-handshake-clean.log` and shows the guard catching exactly the writes that would have corrupted the channel. **The probe code was removed from `MarionetteHost.cs` after the diagnosis — final source is clean.**

| Source | Default destination on Windows .NET 10 | Reaches stdout in `--mcp --headless`? | Mitigation applied |
|---|---|---|---|
| `Console.WriteLine` | stdout | Would, but caught by `StdoutGuardWriter` (installed via `Console.SetOut` before MCP transport binds) | Guard intercepts, increments leak counter, logs first violation as `[Error]` once `ILogger` is online (and as raw stderr line before that). |
| `Console.Out.Write` | stdout | Same as above — same `Console.SetOut` chain | Same. |
| `Trace.WriteLine` | `OutputDebugString` (DefaultTraceListener) — **not stdout** | No | None needed in the headless path. We do *not* register a `TextWriterTraceListener` against `Console.Out`. |
| `Debug.WriteLine` | `OutputDebugString` (DefaultTraceListener) — **not stdout** | No | None needed. |
| `Microsoft.Extensions.Hosting` default Console logger | stdout (would corrupt the channel) | Would, **but suppressed** | Use `Host.CreateEmptyApplicationBuilder` instead of `CreateApplicationBuilder`; we register only a custom `StderrLoggerProvider`. |
| `ModelContextProtocol` SDK internal logging | Whatever provider is registered | stderr (because of the above) | Inherited from the empty builder + stderr provider. |
| `PresentationTraceSources` / WPF Binding errors | `DefaultTraceListener` (`OutputDebugString`) by default | N/A in headless mode (no WPF runtime loaded) | Confirmed by inspection: the headless path never constructs `App` or any `Window`, so the WPF Dispatcher and PresentationTraceSources are never initialized. |
| `EventSource` / `DiagnosticSource` | In-process listeners only | No (no listener registered against stdout) | None needed unless a future contributor wires up a stdout-attached listener. |

**Key insight:** the dominant risk in 1.2.0's hosting integration is the *default Console logger registered by `CreateApplicationBuilder`* — that one alone would have shipped JSON-prefixed log lines through stdout and broken every handshake. `CreateEmptyApplicationBuilder` is the necessary call.

The `StdoutGuardWriter` doubles as a Phase-1 development aid: any user code that accidentally uses `Console.WriteLine` from inside an `[McpCallable]` method will see one loud `[Error]` log entry on first violation, plus a final byte-count summary at shutdown — actionable evidence rather than a silent crash.

## Q3 — GUI mode (`--mcp` without `--headless`)

**Implemented and verified.** The harness was extended with a `--gui` switch that omits `--headless` from the child argv. With this switch, the sample:

- Pops up the WPF `MainWindow` (visible).
- Concurrently runs the MCP host on a background `Task`.
- Same handshake passes — initialize / tools/list / tools/call all returned valid JSON-RPC. Captured in `.phase0/stdio-handshake-gui.log`.
- Stdout still has exactly 3 JSON-RPC frames, **0 pollution**.
- Stderr gains one extra line: `Marionette WPF adapter initialized` from the existing `Adapter.Wpf` stub fired in `App.OnStartup`. That message goes to `Console.Error.WriteLine`, so it lands on stderr correctly — but it bypasses the `ILogger` channel because the Adapter currently has no logger plumbing. Phase 1 should route the adapter's diagnostics through `ILogger` rather than direct `Console.Error` for consistency with the host's logging policy.

The harness force-kills the GUI child after the handshake completes (the WPF `Application.Run` keeps the process alive forever on the message pump), which is expected behaviour for an automated probe and is logged as `INFO`, not `FAIL`.

## Spike A regression — clean

After all changes, with `samples/Sample.Wpf.StripeProbe/{bin,obj}` cleaned and rebuilt as `dotnet build ... -c Release -p:EnableMcpAutomation=false`:

- Output bin contains exactly 7 files, identical set to Spike A baseline.
- IL probe matches Spike A's table:

| Needle | Hits in `Sample.Wpf.StripeProbe.dll` |
|---|---|
| `Marionette` (any) | 7 (all attribute references in Abstractions only) |
| `Marionette.NET.Runtime` | **0** |
| `Adapter.Wpf` | **0** |
| `Marionette.Ai` (in Sample) | **0** |
| `ModelContextProtocol` | **0** |

`deps.json` for the stripped build still references only `Marionette.NET.Abstractions/0.0.1-spike` — no Runtime, no Adapter, no MCP NuGet, no `Microsoft.Extensions.Hosting` (which Spike C newly added to Runtime).

## What I built / changed

Files created:

| File | Role |
|---|---|
| `src/Marionette.NET.Runtime/PingTool.cs` | Minimal `[McpServerToolType]` class with one `[McpServerTool(Name="marionette_ping")]` method returning `"pong"`. The canonical AOT-friendly registration path; no assembly scanning. |
| `samples/Sample.Wpf.StripeProbe/Program.cs` | Custom `[STAThread]` static `Main(string[])` with three branches: GUI (default), `--mcp` (GUI + MCP on background task), `--mcp --headless` (pure stdio MCP, no `Application`). |
| `.phase0/StdioTest/StdioTest.csproj` | `net10.0` console probe project, outside the solution. |
| `.phase0/StdioTest/Program.cs` | Test harness: spawns the sample, exchanges JSON-RPC frames, validates stdout purity, force-kills GUI mode, returns exit code 0/1. |
| `.phase0/stdio-handshake-final.log` | Clean handshake run output (post-cleanup). |
| `.phase0/stdio-handshake-clean.log` | Probe-mode run output (with `MARIONETTE_STDOUT_PROBE=1`). |
| `.phase0/stdio-handshake-gui.log` | GUI-mode run output. |

Files modified:

| File | Change |
|---|---|
| `src/Marionette.NET.Runtime/MarionetteHost.cs` | Replaced the stub `RunAsync` with a real `Host.CreateEmptyApplicationBuilder` + `AddMcpServer` + `WithStdioServerTransport` + `WithTools<PingTool>` bootstrap. Installs `StdoutGuardWriter` before the transport runs. Adds custom `StderrLoggerProvider`. |
| `src/Marionette.NET.Runtime/Marionette.NET.Runtime.csproj` | Added `<PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.1" />`. |
| `samples/Sample.Wpf.StripeProbe/Sample.Wpf.StripeProbe.csproj` | Added `<EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>` and `<StartupObject>Sample.Wpf.StripeProbe.Program</StartupObject>` so MSBuild does not synthesize a `Main` from `App.xaml`. App.xaml is auto-promoted to `<Page>` by the SDK in this configuration; no explicit item needed. |
| `samples/Sample.Wpf.StripeProbe/App.xaml` | Removed `StartupUri="MainWindow.xaml"` — the new `Program.Main` constructs and runs the `MainWindow` imperatively. |

Files NOT changed (per constraints): `MASTERPLAN.md`, `README.md`, `LICENSE`, `.gitignore`, AOT-related properties from Spike B, all of Spike A's stripping setup.

## API choice — explicit tool registration in 1.2.0

The 1.2.0 surface offers four tool-registration entry points; we chose deliberately:

| API | Semantics | Use? |
|---|---|---|
| `WithTools<T>(JsonSerializerOptions)` (generic) | Reflects on the *named* type for `[McpServerTool]` methods. Type is known at compile time, AOT-friendly. | **YES — this is what we use.** |
| `WithTools<T>(T target, ...)` | Like above but binds a pre-built instance. Useful when the tool needs constructor args. | Defer — Phase 1 may need this when ManifestRegistry threads state through. |
| `WithTools(IEnumerable<McpServerTool>)` | Pre-constructed `McpServerTool.Create(MethodInfo, ...)` instances. The lowest-level path. | Defer — relevant when the SourceGen emits explicit `MethodInfo` bindings. |
| `WithToolsFromAssembly(Assembly, ...)` | Scans an assembly for `[McpServerToolType]`. Reflection-heavy, **explicitly flagged as not AOT-clean** by the SDK docs. | **NO — never.** Spike B noted this; the doc explicitly recommends the generic `WithTools<T>` path for AOT. |

This choice keeps the door open for the Phase-1 source-generator: the generator will emit a per-app `__Manifest.g.cs` containing one or more `[McpServerToolType]` partial classes (or wrappers around user code), each registered explicitly via `WithTools<TGeneratedFooBarTools>()`. No assembly scanning at runtime.

## Issues encountered & fixes

1. **XML-comment double-dash.** Two of my csproj comments contained `--mcp` and `--headless`. MSBuild parses the XML strictly (XML 1.0 §2.5: `--` may not appear in comments); MSBuild reports `MSB4025`. Fixed by removing the dashes from the comment text.
2. **NETSDK1022: duplicate Page items.** With `EnableDefaultApplicationDefinition=false`, the WPF SDK auto-promotes `App.xaml` from ApplicationDefinition to Page. My initial csproj re-included it explicitly, producing a dup. Removed the explicit `<Page Include="App.xaml" />` — the SDK glob handles it.
3. **GUI-mode child does not exit on stdin EOF.** Expected: `Application.Run()` keeps the process alive on the WPF message pump, even after the MCP host loop has ended. The harness now branches on `--gui` and force-kills after a 2 s grace window; this is logged as `INFO`, not `FAIL`.
4. **Process.Start with relative path.** `ProcessStartInfo.FileName` is resolved against the *user's PATH and current dir at OS-level*, not against the .NET process's `Environment.CurrentDirectory`. Using a relative path failed with `Win32Exception 2 (file not found)`. Always pass an absolute path. Documented in the harness comments and the run command examples below.
5. **Stdout-guard wraps writes that come `before` the logger is built.** I wired the guard at the start of `RunAsync` (so it catches *very* early violations) but the host isn't built yet, so there's no `ILogger`. The guard tracks the logger via `AttachLogger` after `host.Build()`. First violations before that point fall back to direct `Console.Error.WriteLine`. This is the right ordering — silent stdout pollution during early init is the worst case, so the guard goes up first.

## Reproducibility — exact command sequence

```sh
# build
dotnet build samples/Sample.Wpf.StripeProbe/Sample.Wpf.StripeProbe.csproj -c Debug -p:EnableMcpAutomation=true
dotnet build .phase0/StdioTest/StdioTest.csproj -c Debug

# clean handshake (Q1)
dotnet .phase0/StdioTest/bin/Debug/net10.0/StdioTest.dll \
  "C:/Home/Code/nw.Automation/samples/Sample.Wpf.StripeProbe/bin/Debug/net10.0-windows/Sample.Wpf.StripeProbe.exe"

# Q2 violator probe (only meaningful with the temporary probe code reinstated; see git history)
dotnet .phase0/StdioTest/bin/Debug/net10.0/StdioTest.dll \
  "C:/Home/Code/nw.Automation/samples/Sample.Wpf.StripeProbe/bin/Debug/net10.0-windows/Sample.Wpf.StripeProbe.exe" --probe

# Q3 GUI mode (will pop up a WPF window)
dotnet .phase0/StdioTest/bin/Debug/net10.0/StdioTest.dll \
  "C:/Home/Code/nw.Automation/samples/Sample.Wpf.StripeProbe/bin/Debug/net10.0-windows/Sample.Wpf.StripeProbe.exe" --gui

# Spike A regression
rm -rf samples/Sample.Wpf.StripeProbe/{bin,obj}
dotnet build samples/Sample.Wpf.StripeProbe/Sample.Wpf.StripeProbe.csproj -c Release -p:EnableMcpAutomation=false
dotnet .phase0/ProbeIl/bin/Release/net10.0/ProbeIl.dll Marionette.NET.Runtime samples/Sample.Wpf.StripeProbe/bin/Release/net10.0-windows/
# expect: TOTAL hits across 3 file(s): 0
```

## Recommendations for Phase 1

1. **Bake the `StdoutGuardWriter` into the host as a permanent feature** (not just a Spike-C diagnostic). It is the cheapest insurance against stdout regressions, and the per-byte counter is useful telemetry for the source-gen analyzer to show in its "you wrote to stdout from inside an MCP tool" diagnostic. Phase 1's analyzer should treat any direct `Console.Out.Write*` call inside an `[McpCallable]` method as an error-by-default Roslyn diagnostic — the guard catches the rest.
2. **The `App.OnStartup` branch in the sample currently fires `Marionette.Adapter.Wpf.WpfMarionetteBootstrap.Initialize(this)` unconditionally when MCP_ENABLED is defined, including in `--mcp --headless` mode where no `Application` is constructed.** That works only because `RunGui()` is the only path that creates `App`. But once Phase 1's adapter does *real* WPF work (Dispatcher hooks, visual-tree walker), we need to make sure the headless path doesn't accidentally pull adapter code in. The current shape is safe but fragile — Phase 1 should formalize the "Adapter init happens only when an `Application` instance exists" contract.
3. **The Adapter.Wpf stub writes its breadcrumb directly to `Console.Error`** (a holdover from Spike A). It should be reworked to take an `ILogger` from DI in Phase 1, so all diagnostics flow through the same channel that the host uses.
4. **Force `Console.SetOut(StdoutGuardWriter)` *before* the WPF `Application` constructor runs.** In GUI mode our current `Program.Main` runs `MarionetteHost.RunAsync` on a background Task — the WPF `Application` is built on the foreground thread *concurrently*. There's a race window where `Application` (and any logger registered by user code in `App` ctor) can write to the still-real `Console.Out`. Spike C didn't trip this because the sample's `App` ctor is empty, but Phase 1 should hoist the guard install into Program.Main *before* `App.Run`.
5. **The MCP SDK negotiates protocol `2025-11-25` correctly.** Phase 1 should pin this version explicitly in the SDK options once `McpServerOptions` exposes it; current behaviour relies on SDK default.
6. **Adopt `Host.CreateEmptyApplicationBuilder` as the canonical pattern in `docs/stripping.md`** (whenever it lands). Adopters reading any standard .NET tutorial will copy `CreateApplicationBuilder` and ship a broken stdio MCP server. The doc needs to be loud about this.
7. **The `StdoutGuardWriter` is currently lock-free / `Interlocked.Add`-based, so it is safe under concurrent writes from multiple threads.** That matches our needs — a real WPF app will have at least the UI thread, the MCP transport thread, and any `Task.Run` continuations all potentially writing.

## Files modified / created

```
src/Marionette.NET.Runtime/MarionetteHost.cs              (rewritten — was stub, now real)
src/Marionette.NET.Runtime/PingTool.cs                    (new — minimal smoke tool)
src/Marionette.NET.Runtime/Marionette.NET.Runtime.csproj  (added Microsoft.Extensions.Hosting 10.0.1)

samples/Sample.Wpf.StripeProbe/Program.cs                 (new — custom Main with --mcp / --headless branches)
samples/Sample.Wpf.StripeProbe/App.xaml                   (removed StartupUri)
samples/Sample.Wpf.StripeProbe/Sample.Wpf.StripeProbe.csproj (EnableDefaultApplicationDefinition=false + StartupObject)

.phase0/StdioTest/StdioTest.csproj                        (new — net10.0 console)
.phase0/StdioTest/Program.cs                              (new — handshake harness with --gui / --probe flags)
.phase0/spike-c.md                                        (this file)
.phase0/stdio-handshake-final.log                         (clean run capture)
.phase0/stdio-handshake-clean.log                         (probe-mode run capture)
.phase0/stdio-handshake-gui.log                           (GUI-mode run capture)
```

Files deliberately not changed: `MASTERPLAN.md`, `README.md`, `LICENSE`, `.gitignore`, `Directory.Build.props`, `src/Marionette.NET.Abstractions/*`, `src/Marionette.NET.Adapter.Wpf/*`, all AOT properties from Spike B, all of Spike A's `samples/Sample.Wpf.StripeProbe/MainWindow.xaml{,.cs}`.
