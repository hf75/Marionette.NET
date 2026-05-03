# Phase-0 Follow-up F2 — CI Bootstrap

**Status:** Designed and committed-to-working-tree
**Date:** 2026-05-03
**Decision context:** Follow-up F2 from `PHASE0_FINDINGS.md` ("set up GitHub Actions in Phase 1, run on push to local branches without requiring a remote") — green-lit by the user mid-Phase-0 because the Phase-0 spike outcomes already give us strong, fast regression gates worth wiring up before any Phase-1 source-generator work begins.

This document is the build report. The workflow itself does not run yet because the repo is local-only — it will fire as soon as the repo gets pushed to GitHub (planned for Phase 7), and Phase-1 work is gated on the same checks running locally on demand.

## Files created

| Path | Role |
|---|---|
| `.github/workflows/ci.yml` | Single-workflow definition with two parallel jobs: `build-and-test` (30 min budget) and `aot-publish-smoke` (45 min budget). Triggers on push to `main`/`phase-*`/`feat/*`/`fix/*` and on every `pull_request`. |
| `build/Run-IlProbe.ps1` | PowerShell wrapper around `.phase0/ProbeIl/bin/Release/net10.0/ProbeIl.dll`. Iterates the four forbidden needles (`Marionette.NET.Runtime`, `Adapter.Wpf`, `Marionette.Ai`, `ModelContextProtocol`), captures full per-needle output to `ilprobe.log`, and returns a single aggregate exit code (0=PASS, 1=FAIL, 2=invocation error). |
| `global.json` | Pins SDK to `10.0.202` with `rollForward=latestFeature` so a 10.0.3xx CI runner SDK still satisfies. Matches the SDK Phase 0 used. |
| `.phase0/f2-ci.md` | This report (sibling to `spike-{a,b,c}.md`). |

## Jobs and what they gate

### `build-and-test`

| Step | Phase-0 finding it gates |
|---|---|
| `dotnet build Marionette.NET.sln -c Debug` | Solution-wide compile cleanliness with masterplan Debug-defaults (`EnableMcpAutomation=true` on the sample). TWAE in `Directory.Build.props` makes any compile warning fail. |
| `dotnet build Marionette.NET.sln -c Release` | Same, with masterplan Release-defaults (`EnableMcpAutomation=false`). |
| `dotnet build .../Sample.Wpf.StripeProbe.csproj -c Release -p:EnableMcpAutomation=false` | Spike A's primary build path — explicit clean rebuild of the stripped Release output. |
| `dotnet build .../Sample.Wpf.StripeProbe.csproj -c Release -p:EnableMcpAutomation=true` | Spike A counterpoint — MCP-on Release path also has to compile cleanly. |
| `dotnet build .phase0/ProbeIl/ProbeIl.csproj -c Release` | Phase-0 utility builds at all (verifies the IL probe source survives SDK updates). |
| `dotnet build .phase0/StdioTest/StdioTest.csproj -c Debug` | Same for the stdio harness. |
| `pwsh build/Run-IlProbe.ps1 ...` against `Sample.Wpf.StripeProbe.dll` | **Spike A's verdict gate** — zero hits for any of `Marionette.NET.Runtime`, `Adapter.Wpf`, `Marionette.Ai`, `ModelContextProtocol`. The probe targets the sample DLL directly (not the whole bin folder) because `Marionette.Ai` is a TypeDef in `Marionette.NET.Abstractions.dll`, which always ships — the strip check is about the SAMPLE assembly's symbols, not the always-shipped attribute assembly. ProbeIl exits 0/1 per needle; the wrapper aggregates and produces a unified pass/fail with a forensic log. |
| `dotnet ... StdioTest.dll <StripeProbe.exe>` against the `=true` Debug build | **Spike C's verdict gate** — full `initialize` -> `tools/list` -> `tools/call marionette_ping` handshake completes with exactly 3 JSON-RPC frames on stdout and 0 pollution lines, on a Microsoft-hosted Windows runner. |
| `actions/upload-artifact@v4` on failure (14-day retention) | `ilprobe.log`, `stdio-test.log`, `*.binlog`, `MSBuild_*.log` retained as `ci-logs-${run_id}` for forensic post-mortem. |

### `aot-publish-smoke`

This job runs in parallel to `build-and-test` (no `needs:`), and exists for one reason: Spike B's environmental block (missing C++ workload) is gone on `windows-latest`. The Microsoft-hosted image ships the "Desktop development with C++" workload, so for the first time we can verify the AOT publish path end-to-end.

| Step | Phase-0 finding it gates |
|---|---|
| `dotnet publish ... -p:PublishAot=true -p:EnableMcpAutomation=false` | **Spike B Q1 closure** — the stripped AOT publish that couldn't complete locally now runs on CI. `_SuppressWpfTrimError=true` (already in csproj) bypasses NETSDK1168. The `.exe` should be produced. Captured `aot-off.binlog`. |
| `dotnet publish ... -p:PublishAot=true -p:EnableMcpAutomation=true` | **Spike B Q2 closure** — the full AOT publish with `ModelContextProtocol` reflection paths in scope. Per Phase 0 findings, IL2xxx/IL3xxx warnings are tolerated for now (`-warnaserror:false`); the binlog preserves them for review. Phase 1's source generator + `WithTools<T>()` registration is what drives the warning count toward zero. |
| Stripped binary launch test (3 s + force-kill) | The `.exe` doesn't crash on first launch. WPF's message pump keeps the process alive; we don't expect graceful exit. |
| AOT-on binary through `StdioTest.dll` | Reuses the Spike C harness. Tests that the AOT-published binary still completes the MCP handshake — the MCP NuGet's reflection paths must survive the IL trim pass for this to work. **This is the strongest end-to-end AOT verification we have.** |
| `actions/upload-artifact@v4` on failure | `aot-off.binlog`, `aot-on.binlog`, `stdio-aot.log`, plus the publish output directories themselves, kept as `ci-aot-logs-${run_id}` for 14 days. |

### Workflow-level concerns

- `name: CI` — kept short for the GitHub Actions tab.
- `concurrency.cancel-in-progress: true` keyed on `${workflow}-${ref}` — successive pushes to the same branch cancel queued runs, both saving runner minutes and giving fast feedback on the latest tip.
- `permissions: contents: read` only — no write, no packages. Phase 7 will widen this when releases start.
- `timeout-minutes: 30 / 45` per job — generous for a Phase-1 codebase but keeps a hung run from chewing the runner queue.
- `DOTNET_NOLOGO`, `DOTNET_CLI_TELEMETRY_OPTOUT`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE` set workflow-wide — Phase 0 noted that any byte on stdout from `dotnet` could pollute logs in unexpected ways; this scrubs the obvious sources at the runner level.
- `actions/checkout@v5`, `actions/setup-dotnet@v5`, `actions/cache@v4`, `actions/upload-artifact@v4` are the canonical pins as of 2026-05-03. None require a token beyond the implicit read scope.

## How the Phase-0 findings map to CI checks

| Phase-0 finding | CI manifestation |
|---|---|
| Spike A — IL stripping is total | `Run-IlProbe.ps1` runs four needles against the `=false` Release output. Any non-zero hit fails the job. |
| Spike B — managed-side AOT clean, native AOT blocked locally only | `aot-publish-smoke` runs both AOT publishes on `windows-latest` (which has the C++ workload) and tolerates IL2xxx/IL3xxx warnings via `-warnaserror:false`. The binlog is captured for review. |
| Spike C — stdio handshake passes, zero stdout pollution | `StdioTest.dll` runs against a `=true` Debug build of the sample. The harness already enforces the "exactly 3 JSON-RPC frames, 0 pollution" invariant. |
| Spike D — `ModelContextProtocol 1.2.0` works on `net10.0` | Implicit — every step that builds the Runtime project verifies the package restores and links. |
| `Host.CreateEmptyApplicationBuilder` is the canonical pattern (Phase-0 cross-cutting) | Indirectly verified: the stdio handshake test would fail loudly if `MarionetteHost.RunAsync` regressed to `CreateApplicationBuilder` (default Console logger would corrupt the channel). |
| `_SuppressWpfTrimError=true` is required for WPF + AOT | Indirectly verified: removing it from the csproj would break `aot-publish-smoke` with NETSDK1168. |

## Caveats and explicit non-goals

These are intentionally deferred to later phases per the masterplan:

- **No NuGet pack / push.** Phase 7 deliverable. Adding it now would require version bookkeeping that Phase 0 deliberately skipped.
- **No code-coverage upload (codecov, coveralls).** Phase 1 doesn't have unit tests yet. Phase 6's `Marionette.NET.Testing` is when test coverage becomes meaningful.
- **No GitHub Pages / docs deploy.** Phase 6 (DX polish) and Phase 7 (release) own this.
- **No status-badge generation.** Trivial to add when Phase 7 rewrites the README.
- **No matrix.** Single Windows runner. Phases 2-5 will widen to Avalonia (Linux/macOS feasible), WinUI (Windows-only), Uno (cross-target), and MAUI (full mobile matrix). The workflow is structured so adding a `strategy.matrix:` block is a minimal change.
- **No release / tag / draft-release wiring.** Phase 7.
- **No signing.** Phase 7 — likely a separate workflow (`release.yml`) anyway.
- **No SourceLink / debug symbol publish.** Phase 7.
- **No CodeQL / dependency-review.** Worth adding in Phase 1 if the user wants a security baseline; deferred for now to keep this CI bootstrap minimal.
- **No self-hosted-runner support.** Microsoft-hosted `windows-latest` only. Spike B documented that self-hosted runners need the C++ workload installed manually; if the user moves to self-hosted, that note in `docs/stripping.md` (when it lands) becomes the install reference.

## How to test the workflow locally

The GitHub Actions YAML cannot be executed by `dotnet` directly, but every step maps to a single shell command that you can run from `C:\Home\Code\nw.Automation`. Local reproduction recipe:

```pwsh
# 1. Restore + Debug + Release solution build (mirrors first 3 build-and-test steps).
dotnet restore Marionette.NET.sln
dotnet build Marionette.NET.sln -c Debug   --no-restore
dotnet build Marionette.NET.sln -c Release --no-restore

# 2. Stripped Release build of the sample.
Remove-Item -Recurse -Force samples\Sample.Wpf.StripeProbe\bin, samples\Sample.Wpf.StripeProbe\obj -ErrorAction SilentlyContinue
dotnet build samples\Sample.Wpf.StripeProbe\Sample.Wpf.StripeProbe.csproj -c Release -p:EnableMcpAutomation=false

# 3. Build the IL probe and run the stripping regression check.
dotnet build .phase0\ProbeIl\ProbeIl.csproj -c Release
pwsh -NoProfile -File build\Run-IlProbe.ps1 `
    -ProbeDll .phase0\ProbeIl\bin\Release\net10.0\ProbeIl.dll `
    -Target   samples\Sample.Wpf.StripeProbe\bin\Release\net10.0-windows\Sample.Wpf.StripeProbe.dll

# 4. MCP-on Debug build + stdio handshake.
Remove-Item -Recurse -Force samples\Sample.Wpf.StripeProbe\bin, samples\Sample.Wpf.StripeProbe\obj -ErrorAction SilentlyContinue
dotnet build samples\Sample.Wpf.StripeProbe\Sample.Wpf.StripeProbe.csproj -c Debug -p:EnableMcpAutomation=true
dotnet build .phase0\StdioTest\StdioTest.csproj -c Debug
$exe = Resolve-Path samples\Sample.Wpf.StripeProbe\bin\Debug\net10.0-windows\Sample.Wpf.StripeProbe.exe
dotnet .phase0\StdioTest\bin\Debug\net10.0\StdioTest.dll "$exe"

# 5. AOT publish (only if the C++ workload is installed locally).
dotnet publish samples\Sample.Wpf.StripeProbe\Sample.Wpf.StripeProbe.csproj `
    -c Release -r win-x64 -p:PublishAot=true -p:EnableMcpAutomation=false `
    -warnaserror:false -o publish-aot-off -bl:aot-off.binlog
```

Alternative: `act` (https://github.com/nektos/act) can simulate GitHub Actions locally with Docker, but Windows runners require a Windows host — `act` on Linux/macOS won't help here. The shell-command recipe above is the practical path.

## Phase-7 unblockers (when push happens)

When the repo gets pushed to GitHub:

1. The workflow runs immediately on the first push to `main`. Existing branches are not retroactively triggered.
2. PR-event triggers fire on opening a PR against `main`.
3. The status checks should appear in branch protection settings — recommend setting `build-and-test` and `aot-publish-smoke` as required checks before merge.
4. If the `aot-publish-smoke` job's IL2xxx/IL3xxx warning count grows beyond a level the user is comfortable with, switching `-warnaserror:false` to `-warnaserror:true` (or omitting the flag entirely) becomes the gate. Spike B noted this is currently provisional — waiting for the Phase-1 source-generator to land so the noisy reflection paths get replaced with `WithTools<T>()` registrations.

## Verification before push (when the time comes)

The user (or this orchestrator) should sanity-check the workflow end-to-end before the first push by:

1. Running the local reproduction recipe above and confirming all steps PASS.
2. Inspecting `aot-off.binlog` / `aot-on.binlog` from a local AOT publish (once the C++ workload is installed) to confirm warning counts match expectations.
3. Ensuring `global.json`'s SDK version is still satisfied by what `windows-latest` ships at the time of push (Spike B used 10.0.202; `latestFeature` rollForward gives forward compat to 10.0.3xx and beyond).

## Summary

Two parallel jobs on `windows-latest`, end-to-end:

- **`build-and-test`** — solution builds + Spike A's IL stripping promise + Spike C's stdio handshake. Fast (~3-5 min target).
- **`aot-publish-smoke`** — Spike B's deferred AOT publish + smoke launch + AOT-stdio handshake. Slower (~10-15 min target due to AOT compile times).

Combined, every Phase-0 finding that has an automatable check is wired up. Phase 1 source-generator work can proceed with the confidence that any regression of the three load-bearing claims surfaces in CI within minutes of a push.
