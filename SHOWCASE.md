# Marionette.NET — Showcase

> Your C# desktop app, talking fluent MCP. In about three lines.

```csharp
[McpRoot("mission")]
public class MissionControlViewModel : INotifyPropertyChanged
{
    [McpCallable("Engage the reactor.")]
    public void Engage() { /* whatever your app does */ }

    [McpObservable("Reactor output 0..100 MW.", Watchable = true)]
    public double ReactorOutput { get; private set; }

    [McpEvent("Fires whenever an alert is raised.")]
    public event EventHandler<AlertRaisedEventArgs>? AlertRaised;
}
```

That's the entire ceremony. From there, an LLM (or anything that speaks MCP) can:

- Call `mission.Engage()` directly as an MCP tool
- Read `marionette://mission/ReactorOutput` and subscribe for live updates
- Receive every `AlertRaised` event with typed JSON payload
- Take a screenshot of your live window
- Drive `simulate_input` (click, double-click, key-press, type-text, mouse-move) through the framework's real input pipeline
- Discover your entire surface via `inspect_app_api`

No reflection at runtime. Source-generated dispatchers. AOT-clean. Production builds strip the MCP code to literal zero IL bytes when you flip a single MSBuild flag.

---

## The 90-second pitch

You're writing a real desktop application. WPF, Avalonia, WinUI 3, MAUI — pick one, it doesn't matter. You want an LLM to drive it: maybe to test it, maybe to demo it, maybe to give your end-users a "talk to your app" mode.

The traditional path:
1. Stand up a separate process
2. Define a JSON-RPC schema
3. Marshal arguments by hand, serialize results by hand
4. Watch the LLM ask "what can I do?" and have nothing good to answer
5. Realize you also wanted to drive the UI itself, not just the model
6. Discover that screenshot capture has framework-specific surface
7. Give up

**Marionette.NET path:** decorate three-to-eight methods with `[McpCallable]` / `[McpObservable]` / `[McpEvent]`, add the right NuGet package, call `MarionetteWpf.AttachTo(this, GeneratedManifest.Roots, args)` from `App.OnStartup`, ship.

The source generator builds a typed dispatcher table at compile time. The runtime hosts the MCP server in-process on stdio. The adapter handles the framework-specific input synthesis, screenshot capture, multi-window routing, and UI-thread marshalling.

Done.

---

## What it actually looks like

`Sample.Wpf.NeonControlCenter` (in this repo) is a synthwave-aesthetic mission-control app — animated radar sweep, glowing borders, live telemetry gauges. The ViewModel exercises every Marionette attribute. The `.exe` runs as a normal WPF app *or* as an MCP server *or* as both:

```bash
# Normal app, no MCP — for end-users
Sample.Wpf.NeonControlCenter.exe

# Headless MCP server — for CI / agent integrations / claude-desktop
Sample.Wpf.NeonControlCenter.exe --mcp --headless

# GUI + MCP — the LLM drives, the human watches
Sample.Wpf.NeonControlCenter.exe --mcp
```

In `--mcp --headless` mode the same .exe acts as a stdio MCP server. Drop it into your `claude_desktop_config.json` and Claude can drive your real app with no extra plumbing.

The end-of-phase integration test (`.phase14/NeonStdioTest`) runs the .exe headless and fires a 13-step JSON-RPC sequence against it:

```
===== NEON CONTROL CENTER ===== MCP DEBUG SESSION =====

  [ 1] initialize handshake ... PASS
  [ 2] tools/list ... PASS              -> tools advertised: 15
  [ 3] inspect_app_api ... PASS
  [ 4] read_observable(ReactorOutput) ... PASS  -> 47.5
  [ 5] mission.Snapshot() — record JSON ... PASS
       -> {"reactorOutput":47.5,"coolantPressure":124.7,"quantumFlux":4242,...}
  [ 6] mission.Engage() ... PASS
  [ 7] read_observable(SystemStatus) after Engage ... PASS  -> "ENGAGED"
  [ 8] mission.AdjustPower(delta=15) ... PASS    -> 80
  [ 9] mission.RunDiagnosticAsync() — async ... PASS
       -> "DIAGNOSTIC :: ALL SUBSYSTEMS NOMINAL"
  [10] mission.SnapshotMetrics() — Dictionary<string,double> ... PASS
  [11] mission.GetAlertFeed() — List<string> ... PASS
  [12] mission.ResetTelemetry() ... PASS
  [13] resources/list ... PASS                   -> resources advertised: 8

============== SESSION COMPLETE ==============
  PASS: 13   FAIL: 0
```

Run the same sequence with `--watch`: the GUI opens, Marionette sends commands at 2-second intervals, you watch the values flip live in the window. The status field jumps to `ENGAGED`. The power slider slides to 80. The alert feed gains a new entry. The diagnostic phase puts the status briefly into `DIAGNOSTIC` and then back. ResetTelemetry returns everything to baseline.

That's the sales pitch. Your real app, your real ViewModel, driven by something that wasn't even running in the same process — through one stdio pipe.

---

## Why this and not [X]

| Compared to … | Marionette.NET |
|---|---|
| **Custom JSON-RPC server** | We're an MCP server. Claude Desktop, every MCP-aware client, every Anthropic SDK already speaks our protocol. You write zero protocol code. |
| **UI Automation / Inspect.exe** | We work *with the model*, not against the visual tree. `[McpObservable]` reads your real properties; `[McpCallable]` invokes your real methods. UI Automation has its place, but it's a fragile foundation for "LLM understands your app". |
| **Selenium / Playwright** | Those drive web. We drive native desktop, in-process. |
| **AccessibilityService / UI Test Frameworks** | Those drive *clicks*. We drive *intent*. The LLM doesn't need to know your button is at (482, 391). It calls `mission.Engage()`. |
| **Hand-rolled MCP server in your app** | You'd write one file of attribute scanning, one source generator, one runtime, four framework adapters, a JSON source-gen pipeline, a stdout guard, loop protection, multi-window routing, and an AOT-clean dispatcher table. We did. |

---

## The four killer guarantees

### 1. NuGet drop-in, no code changes (mostly)

Existing app? Add the NuGet, call `AttachTo` from one place, decorate three-to-five methods. Your app ships unchanged from the user's perspective; behind the scenes it now speaks MCP.

### 2. Self-testing apps

`Marionette.NET.Testing` runs the same MCP surface in-process — call `read_observable`, `invoke_method` directly from xUnit / NUnit. Claude can write the tests, write the implementation, *and run them*.

### 3. Zero-cost in production, IL-verified

```xml
<EnableMcpAutomation>false</EnableMcpAutomation>
```

That MSBuild property strips every byte of MCP-related IL from the Release build. The source generator emits nothing. The Runtime DLL is gone. The Adapter DLL is gone. Even the attribute metadata becomes naturally collectible by the trimmer. We have IL-verified this across all four framework samples.

### 4. AOT-clean across the board

Native AOT publish works for: WPF (where Microsoft supports it), Avalonia, WinUI 3, MAUI. Source-gen-emitted JSON contexts cover every primitive, every standard collection (List, Dictionary across all key shapes, IEnumerable / IList / ICollection / ISet / HashSet / Stack / Queue / IReadOnlyDictionary, etc.), enums, multi-dim arrays up to rank 4, value-tuple keys up to rank 5, and now even custom `[JsonConverter]`s. Generic `[McpRoot]` classes work via `[assembly: McpClosedRoot(typeof(MyGen<int>))]`. Generic `[McpCallable]` methods work via the `ClosedTypes` named-arg.

---

## Quickstart

```bash
git clone <this-repo>
cd nw.Automation

# Build everything
dotnet build Marionette.NET.sln -c Debug

# Run a sample as a normal WPF app
samples/Sample.Wpf.NeonControlCenter/bin/Debug/net10.0-windows/Sample.Wpf.NeonControlCenter.exe

# Drive it via MCP (headless: no GUI, just stdio JSON-RPC)
.phase14/NeonStdioTest/bin/Debug/net10.0/NeonStdioTest.exe \
  samples/Sample.Wpf.NeonControlCenter/bin/Debug/net10.0-windows/Sample.Wpf.NeonControlCenter.exe

# Drive it visibly (GUI + commands at 2-second intervals)
.phase14/NeonStdioTest/bin/Debug/net10.0/NeonStdioTest.exe \
  samples/Sample.Wpf.NeonControlCenter/bin/Debug/net10.0-windows/Sample.Wpf.NeonControlCenter.exe \
  --watch
```

For your own app:

```xml
<!-- YourApp.csproj -->
<Import Project="path-to-Marionette/build/Marionette.NET.props" />
<ItemGroup>
  <ProjectReference Include="path-to-Marionette/src/Marionette.NET.Abstractions/..." />
  <ProjectReference Include="path-to-Marionette/src/Marionette.NET.SourceGenerator/..."
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
<ItemGroup Condition="'$(EnableMcpAutomation)'=='true'">
  <ProjectReference Include="path-to-Marionette/src/Marionette.NET.Adapter.Wpf/..." />
</ItemGroup>
```

```csharp
[McpRoot]
public class MyViewModel
{
    [McpCallable("…")]
    public void DoTheThing() { … }
}

protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
#if MCP_ENABLED
    MarionetteWpf.AttachTo(this, GeneratedManifest.Roots, e.Args);
#endif
}
```

Three lines of attaching, zero protocol code. See [docs/getting-started.md](docs/getting-started.md) for the full walkthrough.

---

## What's actually in the box

```
Marionette.NET.Abstractions       ← attributes only, survives stripped Release
Marionette.NET.SourceGenerator    ← Roslyn IIncrementalGenerator
Marionette.NET.Runtime            ← MCP server, dispatchers, loop protection
Marionette.NET.Adapter.Wpf        ← WPF UI-thread + screenshot + raise_event + simulate_input
Marionette.NET.Adapter.Avalonia   ← cross-platform Avalonia 12.x
Marionette.NET.Adapter.WinUI      ← WinUI 3 + InputInjector + Win32 SendInput fallback
Marionette.NET.Adapter.Maui       ← MAUI Windows head + cross-platform handlers
Marionette.NET.Testing            ← in-process test harness
Marionette.NET.Testing.Xunit      ← xUnit-flavored adapter
Marionette.NET.Testing.NUnit      ← NUnit-flavored adapter
Marionette.NET                    ← meta-package
```

11 packages, ~30 KLOC, 49/49 source-gen tests + 12/12 testing-toolkit tests + 7/7 integration eval-cases all green.

Five samples ship with the repo:
- **Sample.Wpf.TodoApp** — canonical reference adopter
- **Sample.Wpf.StripeProbe** — minimal one-method showcase
- **Sample.Wpf.NeonControlCenter** — the synthwave end-of-phase showcase (this one)
- **Sample.Avalonia.Dashboard** — cross-platform Avalonia
- **Sample.WinUI.FormLab** — WinUI 3 + AutomationPeer
- **Sample.Maui.PocketPlanner** — MAUI on Windows head

---

## Five things you can do once your app is Marionette-decorated

1. **Talk to your app from Claude Desktop.** Drop the .exe into `claude_desktop_config.json` with `--mcp --headless`. Now any conversation can reach into your real app's state.

2. **Self-test.** Use `Marionette.NET.Testing` to drive the same surface from xUnit / NUnit. The LLM can write code AND tests AND run them in one loop.

3. **Build a "talk to your app" mode for end users.** Pipe stdio between the running app and an LLM session. Your users describe what they want; your app does it.

4. **Generate documentation from intent.** `inspect_app_api` returns the full structured manifest. Pipe it into a doc-generator. Your README is now derived from your `[McpCallable]` descriptions.

5. **Automate end-to-end test scenarios.** The `simulate_input` + `raise_event` + screenshot trio gives test-automation-grade fidelity. Pair with `Marionette.NET.Testing.Xunit` and you have a CI-ready GUI test harness.

---

## What's deliberately NOT here

- **Uno Platform adapter.** On the roadmap; not in v1. Adopters using Uno today get the same Marionette pattern as Avalonia + a sample we haven't shipped yet.
- **Cloud / web integration.** This is a desktop library. The MCP protocol can be transported over HTTP-SSE, but Marionette's transport is stdio.
- **Visual tree manipulation magic.** We do not "find the button by text" — that's the wrong abstraction. We semantically invoke the *intent*. UI tools that hunt the visual tree are the layer below this one.
- **A "no-attribute" mode.** Decoration is the whole pitch. If you don't want to decorate, this isn't the library you want.

---

## Where to go from here

- [docs/getting-started.md](docs/getting-started.md) — full walkthrough for your first decorated app
- [docs/architecture.md](docs/architecture.md) — how the layers fit together
- [docs/stripping.md](docs/stripping.md) — the zero-IL Release contract, IL-verified
- [docs/testing.md](docs/testing.md) — `Marionette.NET.Testing` patterns
- [docs/adapter-authoring.md](docs/adapter-authoring.md) — write your own framework adapter
- [skill-pack/](skill-pack/) — Claude Code prompts that turn the LLM into a Marionette adoption assistant
- [PHASE12_FINDINGS.md](PHASE12_FINDINGS.md) / [PHASE13_FINDINGS.md](PHASE13_FINDINGS.md) / [PHASE14_FINDINGS.md](PHASE14_FINDINGS.md) — the recent phase notes
- [MASTERPLAN.md](MASTERPLAN.md) — original design document, still mostly current

---

## License

[See LICENSE in the repo root.]

---

*Marionette.NET. Decorate, attach, ship.*
