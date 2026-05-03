# Phase 0 — Findings

> **Status:** ✅ Concept verified — proceed to Phase 1 (with two minor follow-ups)
> **Date:** 2026-05-03
> **SDK:** .NET 10.0.202
> **MCP NuGet:** ModelContextProtocol 1.2.0 (protocol version negotiated: `2025-11-25`)

Phase 0 was scoped to a 3-day reality check: prove that the masterplan's three load-bearing claims (compile-time stripping, AOT compatibility, clean stdio MCP host) survive contact with .NET 10 and the actual `ModelContextProtocol` NuGet. Detailed per-spike reports live in `.phase0/spike-{a,b,c,d}.md` (Spike D's findings are inlined below since it was research-only with no artifacts).

## Verdict per spike

| Spike | Question | Status |
|---|---|---|
| **A** | IL-stripping: zero Marionette runtime symbols in stripped Release builds? | ✅ Pass |
| **B** | AOT publish: managed-side clean, library setup correct? | ⚠️ Partial — managed-side verified clean; native-linker test blocked by missing C++ workload on this machine |
| **C** | stdio MCP server: real handshake + 0 stdout pollution? | ✅ Pass |
| **D** | ModelContextProtocol NuGet on net10.0: usable, AOT-aware path exists? | ✅ Pass — version 1.2.0, `WithTools<T>()` is the AOT-friendly registration path |

**Go/No-Go for Phase 1: GO.** No load-bearing assumption was falsified. Both partial points are environmental (linker workload missing) or already-anticipated limitations of MCP NuGet 1.2.0's reflection paths — neither blocks the masterplan's strategy, both are concretely actionable.

## Cross-cutting findings

### Stripping is total
With `<EnableMcpAutomation>false</EnableMcpAutomation>` (the masterplan's Release-build default), the sample WPF probe ships with **7 files** vs **22 files** in the MCP-on build. The stripped DLL contains **0 references to `Marionette.NET.Runtime`, `Adapter.Wpf`, `ModelContextProtocol`, or `Marionette.Ai`** — only the four attribute types from `Abstractions` survive (by design). `deps.json` confirms only `Marionette.NET.Abstractions` ships. `[Conditional("MCP_ENABLED")]` correctly elides every `Ai.Trigger(...)` call site. This is the masterplan's strongest guarantee and it holds without compromise.

### Managed-side AOT is clean
With `PublishAot=true` cascading the trim/AOT/single-file Roslyn analyzers, `csc.exe` produces **zero warnings** across all four projects, including with `ModelContextProtocol 1.2.0` referenced. This covers our source code; the dependency-side IL trim pass (`IlcCompile`) couldn't run on this machine because the native linker isn't available. Caveat: 0 warnings on the MCP-on build is *provisional* — current Runtime is a stub, so `ModelContextProtocol`'s reflection paths aren't exercised yet. Phase 1's source generator must avoid those paths (`WithToolsFromAssembly`); the canonical AOT-friendly entry is `WithTools<T>()`, confirmed in Spike C.

### Stdio MCP host works end-to-end with zero pollution
A real `Sample.Wpf.StripeProbe.exe --mcp --headless` completes the full handshake (`initialize` → `notifications/initialized` → `tools/list` → `tools/call marionette_ping → "pong"` → clean shutdown on stdin EOF) in ~0.3 s with **exactly 3 JSON-RPC frames on stdout, 0 pollution lines**. All logs route to stderr. GUI mode (`--mcp` without `--headless`) works the same way — handshake passes while a WPF window is concurrently visible.

The non-obvious requirement: `Host.CreateEmptyApplicationBuilder` (NOT `CreateApplicationBuilder`) — the default Console logger registered by the latter would otherwise corrupt the JSON-RPC stream. This is the single most important pattern adopters need to know about.

### Stdout-pollution sources mapped

| Source | Default destination | Caught? |
|---|---|---|
| `Console.WriteLine` / `Console.Out.Write` | stdout | ✅ caught by `StdoutGuardWriter` (installed via `Console.SetOut` before transport) |
| `Microsoft.Extensions.Hosting` default Console logger | stdout | ✅ suppressed by using empty builder + custom `StderrLoggerProvider` |
| `Trace.WriteLine` / `Debug.WriteLine` | `OutputDebugString` (Windows) | ✅ never reaches stdout — no listener wired |
| `PresentationTraceSources` (WPF Bindings) | `OutputDebugString` | ✅ same |
| `EventSource` / `DiagnosticSource` | in-process listeners | ✅ no stdout listener registered |

The `StdoutGuardWriter` should be **permanent** in Phase 1, not just a Phase-0 diagnostic. It's the cheapest insurance against regressions and provides actionable telemetry (byte-count summaries) when violators slip through.

## Phase-1 implications (concrete)

These are derived from the spike findings and should be carried into Phase 1 planning:

1. **Source Generator must register tools via `WithTools<T>()`**, never `WithToolsFromAssembly`. The generator emits per-app `[McpServerToolType]` partial classes, each registered explicitly. This keeps the AOT door open.
2. **Multi-target Abstractions to `netstandard2.0;net10.0`.** The modern TFM picks up `IsAotCompatible=true` natively; the `TreatAsLocalProperty` workaround required for netstandard2.0-only goes away.
3. **Bake `_SuppressWpfTrimError=true` into `build/Marionette.NET.targets`** when an Adapter.Wpf reference is present. WPF + AOT is otherwise blocked by the hard SDK error NETSDK1168; this internal property is the documented escape valve.
4. **Document the C++ workload prerequisite for AOT publish** in `docs/stripping.md`. Adopters will hit the same wall this machine did. The note is one paragraph.
5. **CI matrix needs a Windows runner with the "Desktop development with C++" workload installed.** The default `windows-latest` GitHub-hosted image includes it; self-hosted runners need it added.
6. **Promote `StdoutGuardWriter` to Runtime proper** with: lock-free `Interlocked.Add` byte counter (already there), stderr-only logging once `ILogger` is online, and a Roslyn diagnostic that flags `Console.Out.Write*` calls inside `[McpCallable]` methods at compile time.
7. **Hoist `Console.SetOut(StdoutGuardWriter)` before the WPF `Application` constructor in GUI mode.** The current sample's `App` ctor is empty so no race surfaces, but the contract should be formalized.
8. **Re-run the IL probe in CI on every PR** as a regression gate against the stripping promise. The probe is fast (~50 ms) and unforgiving in the right way.
9. **Adapter.Wpf bootstrap should accept `ILogger`** rather than writing directly to `Console.Error` — current Spike-A stub bypasses the host's logging policy.
10. **Document `Host.CreateEmptyApplicationBuilder` loudly** in adopter-facing docs. Anyone copying a "hello world" hosting sample will use the wrong builder and ship a broken MCP server.

## Open follow-ups

Two items the user must decide before Phase 1 starts:

### F1. Install the C++ workload on this dev machine

Currently missing: `Microsoft.VisualStudio.Component.VC.Tools.x86.x64` and a Windows 10/11 SDK. Install via the Visual Studio Installer ("Desktop development with C++" workload) — adds ~6 GB. **Without this, Phase 1 cannot AOT-publish a sample to verify a generator change locally.** The managed-side analyzer still catches most issues, but end-to-end IlcCompile diagnostics require the linker.

Recommendation: install before Phase 1 starts. The ergonomic value (being able to AOT-publish locally rather than waiting for CI) is high.

### F2. Decide CI strategy timing

Phase 7 is the masterplan's "distribution + dogfooding" step where push happens. CI itself is mentioned in the masterplan's risk table but has no explicit phase. The IL-stripping probe and AOT publish are already candidate gates — should CI be set up at the start of Phase 1 (so every Phase-1 commit is gated) or deferred until Phase 5/6?

Recommendation: set up GitHub Actions in Phase 1, run on `push` to local branches *without* requiring a remote. Once the repo gets pushed in Phase 7, the CI just attaches to whichever GitHub Actions / Azure DevOps target you choose.

## What Phase 0 produced (artifacts in repo)

Tracked:
- Solution + 4 projects + 1 sample (the actual scaffold from Spike A)
- `Directory.Build.props`
- `MarionetteHost.cs`, `PingTool.cs` — real (minimal) MCP host wiring
- `Sample.Wpf.StripeProbe/Program.cs` — custom Main with `--mcp [--headless]` branches
- `.phase0/spike-{a,b,c}.md` — full per-spike reports
- `.phase0/ProbeIl/{ProbeIl.csproj, Program.cs}` — IL-symbol probe (reusable in CI)
- `.phase0/StdioTest/{StdioTest.csproj, Program.cs}` — handshake test harness (reusable)

Ignored (in `.gitignore`):
- `.phase0/probe-off/`, `.phase0/probe-on/` — binary snapshots from Spike A
- `.phase0/**/bin/`, `.phase0/**/obj/` — build output of probe + harness
- `.phase0/*.log` — verbose AOT publish logs and stdio handshake logs (kept locally for forensic value, not committed)

## Recommendation

**Proceed to Phase 1.** The masterplan's foundational claims survived contact with reality. The two open items (F1 install, F2 CI timing) are decisions, not blockers — Phase 1 work can begin in parallel with the C++ install.

The skeleton built during Phase 0 is **not** intended to be the foundation of Phase 1's production code — Spikes wrote stubs and disposable test harnesses. Phase 1 should cleanly rewrite `MarionetteHost`, the source generator, the `Ai` channel implementation, the Adapter.Wpf, and add a real `Sample.Wpf.TodoApp` per the masterplan layout. The Phase-0 probe (`ProbeIl`) and stdio harness (`StdioTest`) however are reusable and should be promoted into proper `tests/` projects.
