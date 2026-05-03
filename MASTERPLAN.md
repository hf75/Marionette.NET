# Marionette.NET — Masterplan

> **Status:** Pre-alpha · Planning · Decisions locked 2026-05-03
> **Working dir:** `C:\Home\Code\nw.Automation`
> **License:** MIT
> **Target Framework:** `net10.0` (Runtime/Adapters), `netstandard2.0` (Abstractions)
> **Repo strategy:** Local git only — no push until v0.1 is functional

---

## Mission

In *einem Satz*: Drop ein NuGet in jede C#-Desktop-App, dekoriere Methoden / Properties / Controls mit Attributen — und du hast eine **AI-controllable, AI-testable, AI-observable** Anwendung, **über jedes C#-UI-Framework hinweg**, mit **null Footprint im Release**, wenn du das willst.

## Tenets — nicht verhandelbar

1. **In-process beats out-of-process.** Wir hängen uns nicht über Win32/UIA an die App, wir SIND die App. Direkter Zugriff auf jedes Objekt, jeden Thread, jedes Event — ohne IPC-Brücken.
2. **Semantic beats visual.** Claude ruft `ApplyFilter("electronics", 50)`, nicht `Click(x=137,y=42)`. Pixel-Coordinates sind Fallback, nicht Default.
3. **Manifest-frozen-on-startup.** Reflection einmal beim ersten Window. Hot-Path: keine Allocs.
4. **Source-Generators over runtime Reflection.** Wo möglich, Compile-Time. Drei Effekte: (a) Manifest statisch verifizierbar, (b) Trimming/AOT-tauglich, (c) Releasebuild kann den ganzen Apparat rauskompilieren.
5. **Same code, three modes:** Production (no MCP), Debug-with-MCP-on (`--mcp`), Always-MCP (deployed als MCP-Tool). Eine Codebasis.
6. **No framework lock-in.** WPF, Avalonia, WinUI, Uno, MAUI — gleiches User-API. Adapter ist Implementation-Detail.
7. **Bidirectional from day one.** Channel (App → Claude) ist nicht Phase 5, sondern Phase 1 — sonst ist die Library nur halb so spannend.
8. **Test-Automation-grade input fidelity.** `simulate_input` muss durch die echte Input-Pipeline gehen, nicht semantic-shortcuts. Damit ist Marionette Ersatz für FlaUI / Appium / WinAppDriver — nicht nur Ergänzung.
9. **Screenshot is a first-class tool, not an afterthought.** Claude muss zu jedem Zeitpunkt sehen können, was die App macht. Phase 1 liefert das mit.
10. **The skill-pack ships with the library.** Ohne `@marionette/skill-pack` ist LLM-Adoption das schwächste Glied. Skill-Pack ist Phase-1-Deliverable, nicht Phase-5-Polish.

## Konkurrenzanalyse — wo wir voraus sind

| Kontender | Ansatz | Schwächen | Wo wir gewinnen |
|---|---|---|---|
| **FlaUI / UIA** | UI Automation Tree | langsam, brittle, framework-blind | In-process, semantisch, schneller |
| **Appium / WinAppDriver** | WebDriver-Protokoll | Setup-Hölle, fragile Locators | NuGet drop-in, MCP-native |
| **Playwright** | Browser only | kein Desktop | Wir sind die Desktop-Antwort |
| **Coded UI** | abandoned by MS | — | mature replacement |
| **Power Automate / UiPath** | enterprise RPA, $$$ | proprietary, lock-in | Open, AI-native, dev-friendly |
| **Avalonia.Headless** | Test-API für Avalonia | nur Avalonia, nicht für Live-Apps | Wir laufen *im* Live-Prozess, jedes Framework |
| **Selenium-style Test-Frameworks** | imperativ, kein State | Polling, brittle | Watchable Observables, Loops abwickeln |
| **Mozilla Marionette** | Browser-Automation | Firefox-only, abandoned for Desktop | Namensgleichheit, semantisch nicht im Konflikt |

**Niemand bietet die Kombi:** *In-process* + *attribute-driven* + *cross-framework* + *MCP-native* + *bidirectional* + *strippable* + *AOT-tauglich*. Das ist die Lücke.

## Architektur — Schichtenmodell

```
+---------------------------------------------------------------+
|                        User-Code                              |
|   [McpCallable] [McpObservable] [McpTriggerable] [McpRoot]    |
|   Ai.Trigger("…")                                             |
+---------------------------------------------------------------+
              |                                |
              v                                v
+----------------------------+   +-----------------------------+
| Marionette.NET.Abstractions|   | Marionette.NET.SourceGen    |
| - Attributes (immer drin)  |   | - Manifest.g.cs             |
| - Ai-Stub (Cond./empty)    |   | - Trim-safe Dispatcher      |
| - tiny: <50 KB             |   | - DescriptorTable           |
| - netstandard2.0           |   | - Roslyn Incremental        |
+----------------------------+   +-----------------------------+
              ^
              | (only when MCP enabled)
              |
+---------------------------------------------------------------+
| Marionette.NET.Runtime  (Debug-only or always, MSBuild-ctrl)  |
|                                                               |
|  StdioMcpHost          ManifestRegistry      ChannelEmitter   |
|  ResourceProvider      LoopProtection        ToolRegistry     |
|  CliFlagDispatcher     ScreenshotService     SessionLog       |
|  net10.0                                                      |
+---------------------------------------------------------------+
              ^                ^                ^
              |                |                |
   +-------------+  +-------------+  +-------------+  +---------+
   | Adapter.Wpf |  | Adapter.   |  | Adapter.    |  | Adapter.|
   | (Phase 1)   |  | Avalonia   |  |   WinUI     |  |   Uno   |
   |             |  | (Phase 2)  |  | (Phase 3)   |  | (Ph. 4) |
   +-------------+  +-------------+  +-------------+  +---------+
                                                       +---------+
                                                       | Adapter.|
                                                       |  MAUI   |
                                                       | (Ph. 5) |
                                                       +---------+
       Dispatcher   Dispatcher       Dispatcher       Dispatcher
       Input-Sim    Input-Sim        Input-Sim        Input-Sim
       Visual-Tree  Visual-Tree      Visual-Tree      Visual-Tree
       Screenshot   Screenshot       Screenshot       Screenshot
```

**Schichtenkontrakt:**

- **Abstractions** (`netstandard2.0`): nur Attribute + Stub-Klassen, ~50 KB, auch in Release immer drin (Annotations bleiben als Doku-Quelle erhalten).
- **SourceGenerator**: scannt Abstractions-Attributes, schreibt `__Manifest.g.cs` mit statischer Lookup-Tabelle. Inkrementell (Roslyn Incremental Generator API). Trim/AOT-tauglich. Wird auch in Release ausgeführt — der generierte Code ist trimmable und fliegt raus, wenn die Runtime-DLL nicht referenziert.
- **Runtime** (`net10.0`): nur referenziert, wenn MSBuild-Property `<EnableMcpAutomation>true</EnableMcpAutomation>`. Hier liegt der ganze MCP-Server.
- **Adapter.{Framework}**: framework-spezifisch. Implementiert `IUiAutomationAdapter` (Dispatcher-Marshalling, FindByName, RaiseEvent, SimulateInput, Screenshot). Adapter zieht entsprechende UI-Lib transitiv.

## Compile-Time-Stripping — der Killer-Move

**Drei Stripping-Level**, wählbar per Build-Profile via MSBuild-Property:

### Level 0 — Always Production
`<EnableMcpAutomation>false</EnableMcpAutomation>`. Runtime-DLL gar nicht referenziert. Attribute bleiben (Markup), `Ai.Trigger` ist via `[Conditional("MCP_ENABLED")]` ein No-Op und wird vom Compiler weggestrichen. **Footprint: ~50 KB Attribute-DLL, kein Code-Pfad.**

### Level 1 — Debug-only (Default für Marionette-Templates)
```xml
<PropertyGroup Condition="'$(Configuration)'=='Debug'">
  <EnableMcpAutomation>true</EnableMcpAutomation>
  <DefineConstants>$(DefineConstants);MCP_ENABLED</DefineConstants>
</PropertyGroup>
```
Runtime + Adapter nur in Debug. Release-Build des Endprodukts ist sauber. CLI-Flag `--mcp` nur in Debug verfügbar.

### Level 2 — Always-On Production
`<EnableMcpAutomation>true</EnableMcpAutomation>` ohne Condition. App ist deployed *als* MCP-Tool (Live-MCP-Frozen-Mode-Style à la N.E.O.). `claude mcp add MyApp.exe -- --mcp`.

**Verifikations-Garantie:** Phase 0 prüft mit ILSpy, dass ein Release-Build mit `EnableMcpAutomation=false` *literal null* MCP-Symbole im IL hat. Wenn nicht → Konzept anpassen, bevor irgendetwas anderes weitergeht.

## Solution-Layout

```
nw.Automation/    (working dir; repo-name extern: Marionette.NET)
├── Marionette.NET.sln
├── src/
│   ├── Marionette.NET.Abstractions/
│   ├── Marionette.NET.SourceGenerator/
│   ├── Marionette.NET.Runtime/
│   ├── Marionette.NET.Adapter.Wpf/
│   ├── Marionette.NET.Adapter.Avalonia/
│   ├── Marionette.NET.Adapter.WinUI/
│   ├── Marionette.NET.Adapter.Uno/
│   └── Marionette.NET.Adapter.Maui/
├── samples/
│   ├── Sample.Wpf.TodoApp/
│   ├── Sample.Avalonia.Dashboard/
│   ├── Sample.WinUI.FormLab/
│   ├── Sample.Uno.Calculator/
│   ├── Sample.Maui.PocketPlanner/
│   └── Sample.SelfTesting/             (Demo: Claude testet sich selbst)
├── tests/
│   ├── Marionette.NET.Runtime.Tests/
│   ├── Marionette.NET.SourceGenerator.Tests/
│   └── Marionette.NET.Integration/     (E2E mit echtem MCP-Client)
├── skill-pack/
│   ├── claude-code/                    (Skills für Claude Code CLI)
│   ├── prompts/                        (System-prompts pro Adapter)
│   └── examples/                       (Showcase-Conversations)
├── docs/
│   ├── getting-started.md
│   ├── architecture.md
│   ├── attributes-reference.md
│   ├── stripping.md
│   ├── adapter-authoring.md
│   └── skill-pack.md
├── build/
│   ├── Marionette.NET.props            (SDK-Style auto-include)
│   └── Marionette.NET.targets          (MSBuild-Conditional-Logic)
├── Directory.Build.props
├── Directory.Packages.props            (Central Package Management)
├── .gitignore
├── LICENSE
├── README.md
└── MASTERPLAN.md
```

NuGet-Distribution: Meta-Package `Marionette.NET` zieht Abstractions + SourceGenerator + den Adapter passend zum Projekt-SDK (WPF-SDK → Adapter.Wpf, Avalonia-SDK → Adapter.Avalonia, etc). Power-User können einzeln pinnen (`Marionette.NET.Abstractions`, …).

## Phasenplan — 10–12 Wochen, strikt sequenziell

### Phase 0 — Foundation Spike (3 Tage)
**Ziel:** Beweisen, dass das Stripping-Konzept trägt. Kein User-API, nur PoC.
- Minimal-WPF-App mit einem `[McpCallable]`-Attribut.
- Source-Generator emittiert Lookup-Tabelle.
- MSBuild-Property kippt Runtime an/aus.
- Verifizieren: Release-Build ohne Property hat **literal null** MCP-Symbole im IL (mit ILSpy prüfen).
- Verifizieren: AOT-Publish funktioniert mit `EnableMcpAutomation=true`.
- **Go/No-Go für Phase 1.** Wenn Stripping bricht oder AOT scheitert → Konzept anpassen, bevor weiter geht.

### Phase 1 — Core + WPF + Skill-Pack (3 Wochen)
- **`Marionette.NET.Abstractions`**: `[McpCallable]`, `[McpObservable]`, `[McpTriggerable]`, `[McpRoot]`. `Ai.Trigger` / `Ai.ScheduleTrigger` mit `[Conditional]`-Stripping.
- **`Marionette.NET.SourceGenerator`**: Inkrementelle Manifest-Generation, Validation-Diagnostics ("Methode hat `[McpCallable]` aber ist nicht public", "Parameter-Typ nicht serialisierbar", "stdout-Logging in MCP-Mode-aktivem Codepfad", …).
- **`Marionette.NET.Runtime`**: stdio-MCP-Server (offizielles `ModelContextProtocol`-NuGet), Tools: `inspect_app_api`, `invoke_method`, `read_observable`, `capture_screenshot`. Loop-Protection (Hop-Counter, default 5, env `MARIONETTE_MAX_DEPTH`).
- **`Marionette.NET.Adapter.Wpf`**: Dispatcher-Marshalling, Visual-Tree-Walker, FindByName über `AutomationProperties.AutomationId`/`x:Name`, `RenderTargetBitmap`-Screenshot.
- **Channel-Push** (`Ai.Trigger`) als stdio-Notification. Hop-Counter im Notification-Payload.
- **CLI-Dispatcher**: `MyApp.exe --mcp`, `--mcp --headless`, `--mcp-help`. stdout reserviert für JSON-RPC, alles andere auf stderr.
- **`build/Marionette.NET.targets`**: MSBuild-Property `EnableMcpAutomation` mit Default-Logic für Debug=on, Release=off.
- **Skill-Pack v1**: Claude-Code-Skills `marionette-explore`, `marionette-test`, `marionette-decorate`. System-Prompts, die Claude lehren, wie man Attribute setzt + `invoke_method` verwendet. Beispiel-Conversations für 3 Use-Cases.
- **`Sample.Wpf.TodoApp`** + 5 End-to-End-Eval-Cases als CI-Test.

**Demo Phase 1:** Generic-WPF-Calculator. Claude liest Manifest, ruft `Add(2, 3)`, liest `Result`-Observable, macht Screenshot zur Verifikation. **Kein Mensch hat geklickt.** Tweetbar.

### Phase 2 — Avalonia + Watchable + Dynamic Tools (2 Wochen)
- **`Marionette.NET.Adapter.Avalonia`**: Avalonia-Visual-Tree, `Dispatcher.UIThread`, `RenderTargetBitmap`-Äquivalent, FindByName über `Name`-Property + `AutomationId`.
- `[McpObservable(Watchable=true)]` → MCP-Resource (`marionette://<root>/<prop>`), `resources/subscribe`, 200 ms Coalesce-Window.
- INotifyPropertyChanged-Detection, configurable Polling-Fallback (default 500 ms).
- Per-Method-Tools (`<root>.<method>`) statt nur Meta-Tools, mit `tools/list_changed` Push bei Hot-Plug-Roots.
- Idempotente Tool-Identity (deterministisches Hashing über class+method+signature).
- **`Sample.Avalonia.Dashboard`** + Skill-Pack-Update mit Avalonia-Beispielen.

### Phase 3 — WinUI + Real Input (2 Wochen)
- **`Marionette.NET.Adapter.WinUI`**: WinAppSDK-Visual-Tree, `DispatcherQueue`, `InputInjector` für simulate_input.
- **`simulate_input(target, kind, args)`**: echter Input durch die jeweilige Pipeline:
  - WPF: `InputManager.ProcessInput`
  - Avalonia: `IInputDevice`-pump
  - WinUI: `InputInjector`
- **`raise_event(target, eventName, args)`**: framework-spezifische RoutedEvent-Mechanik (mit Bubbling/Tunneling).
- **Multi-Window-Routing** (windowId-Suffix only when needed).
- **`Sample.WinUI.FormLab`**.

**Demo Phase 3:** Selbsttestende App. Claude bekommt eine bestehende WPF-Solution, fügt `[McpCallable]`-Attribute hinzu wo sie helfen, baut, startet `--mcp`, klickt sich durch alle Forms via `simulate_input`, verifiziert State, schreibt Bug-Report. **Ein Befehl, ein durchgetestetes Produkt.** Mind blowing tweetbar.

### Phase 4 — Uno Platform (1.5 Wochen)
- **`Marionette.NET.Adapter.Uno`**: cross-target (WinAppSDK, GTK, WASM-skip in v1, MacOS, Linux).
- Reusing chunks of WinUI-Adapter (Uno mirror WinUI-API).
- **`Sample.Uno.Calculator`** mit Multi-Target-Build.

### Phase 5 — MAUI + AOT/Trimming-Härtung (1.5 Wochen)
- **`Marionette.NET.Adapter.Maui`**: MAUI-Handler-System, `Microsoft.Maui.Dispatching.IDispatcher`.
- AOT-Tauglichkeit gehärtet: Generator emittiert AOT-friendly Code (kein `MakeGeneric*`, kein `Type.GetType(string)` zur Laufzeit).
- Trimming-Hints (`DynamicallyAccessedMembers`, `RequiresUnreferencedCode`) auf allen Public-APIs.
- Single-file-Publish-Verifikation pro Adapter.
- **`Sample.Maui.PocketPlanner`**.

### Phase 6 — Testing-Toolkit + DX-Polish (1 Woche)
- **`Marionette.NET.Testing`** NuGet: xUnit/NUnit-Adapter, App in-process gestartet, MCP-Aufrufe direkt simuliert (ohne echten Claude), Assertion-API.
- VS-Diagnostic-Analyzer: rote Squigglies bei "diese public Methode könnte `[McpCallable]` haben".
- Skill-Pack v2: `/test-this-app`, `/explore-this-app`, `/automate-this-flow`, `/decorate-app`.
- Polish: README, Architektur-Doc, Adapter-Authoring-Guide, Stripping-Guide.

### Phase 7 — Distribution + Dogfooding (1 Woche)
- NuGet-Prerelease-Pakete pushen.
- Drei Showcase-Apps publizieren (WPF / Avalonia / WinUI).
- 90-Sekunden-Demo-Video.
- README mit Animated-GIF-Demos.
- **Erst hier `git push origin main`** und GitHub-Release.

## Killer-Features (selling points)

1. **„NuGet drop-in, no code changes"** — bestehende Apps bekommen Live-Inspection ohne Refactor.
2. **„Self-testing apps"** — Claude generiert Code, Tests UND führt sie aus.
3. **„Zero-cost in production"** — ein MSBuild-Switch und alles ist weg, IL-verifiziert.
4. **„One API, every framework"** — kein Tool-Wechsel zwischen WPF und Avalonia.
5. **„Bidirectional from day one"** — App pusht zurück, Claude reagiert. Echte Conversational Apps.
6. **„AOT-ready"** — passt in Single-File-Publish, kein Reflection-Footprint.
7. **„Skill-pack included"** — Claude weiß sofort, wie es die Library benutzt.
8. **„See what the app sees"** — `capture_screenshot` als first-class Tool, nicht als Plugin.

## Risiken & Mitigation

| Risiko | Wirkung | Mitigation |
|---|---|---|
| Source-Generator-Komplexität (Inkrementelle Generation, IDE-Performance) | DX leidet, Adoption-Killer | Roslyn Incremental Generator API von Tag 1, Benchmarks pro PR |
| AOT/Trimming bricht in einem Framework | Pflicht-Feature kaputt | Phase-0-Spike pro Adapter, CI-Job pro Framework |
| Input-Simulation-Pipeline-Unterschiede | API-Lecks oder verfälschte Events | Adapter-Test-Suite mit Reference-Behaviors, früher Spike pro Pipeline |
| stdout-Pollution (Logs überlappen JSON-RPC) | Cryptic Protocol-Crashes | Bootstrap erzwingt stderr-Routing, Source-Gen-Diagnostic-Linter erkennt `Console.WriteLine` in Roots |
| Reentrancy/Loops mit Channel + invoke_method | Endlosschleifen, Cost-Explosion | Hop-Counter aus Tag 1 (nicht Phase 5 wie bei N.E.O.), default 5, env-konfigurierbar |
| LLM nutzt Attribute nicht zuverlässig | Library wird ignoriert | Skill-Pack ab Phase 1, VS-Analyzer-Hints "could be `[McpCallable]`?" |
| Naming-Conflicts zwischen Frameworks | Bug-Hölle | Name-Mangling im Generator, deterministisch + dokumentiert |
| Mozilla Marionette Naming-Verwechslung | Branding-Verwässerung | "Marionette.NET" als Paket-Prefix; ".NET" disambiguiert klar |
| .NET 10 als so-frisches LTS | Ökosystem-Risiken | Bewusste Entscheidung; Rückfall auf net9.0 möglich falls AOT/Source-Gen-Bugs |

## Decisions — Locked 2026-05-03

| Punkt | Entscheidung |
|---|---|
| Name | **Marionette.NET** — Repo, Namespace, NuGet-Prefix. Brand-Display: "Marionette". |
| Lizenz | **MIT** |
| Working dir | `C:\Home\Code\nw.Automation` |
| Git | **Lokales Repo nur**, kein push bis v0.1 funktional (Ende Phase 7) |
| TFM Runtime/Adapters | **net10.0** |
| TFM Abstractions | **netstandard2.0** (max Kompatibilität) |
| Adapter-Reihenfolge | **WPF → Avalonia → WinUI → Uno → MAUI** (alle nacheinander, nicht parallel) |
| MCP-Transport | **stdio only** in v1 (Pipe/HTTP frühestens v2) |
| Always-on Default | **Debug-Build = on, Release-Build = off** (via Project-Template-Property) |
| Skill-Pack | **Phase 1 deliverable**, nicht Phase 5 |
| Screenshot | **Phase 1 deliverable** als first-class Tool |
| Channel | **Phase 1 deliverable** (`Ai.Trigger`) |

## Nicht-Ziele — explicit out of scope

- ❌ **Keine Code-Generation, kein Roslyn-Compile zur Laufzeit** — N.E.O.s Domäne.
- ❌ **Kein Headless-Browser** — Playwright deckt Web ab.
- ❌ **Kein Mobile-Native ohne MAUI/Uno** — Xamarin-Klassik wird nicht supported.
- ❌ **Keine Sandboxing-Verantwortung** — `mcp add` ist Endnutzer-Trust-Decision.
- ❌ **Kein Web-UI-Builder, kein Designer-Mode** — wir sind Plumbing, nicht Plattform.
- ❌ **Kein Cowork-/Claude-Desktop-Support in v1** — Claude Code CLI only. Spätere Phase ggf. nachziehen.
- ❌ **Kein Remote-MCP-Transport** — stdio-only, alles bleibt auf der lokalen Maschine.

## Offene Punkte für Phase 0 (Spike-Tasks)

1. **Spike A — IL-Stripping verifizieren.** WPF-Min-App mit `[McpCallable]`. Build Release mit `EnableMcpAutomation=false`. Mit ILSpy prüfen: keine Marionette-Symbole im Output. Wenn doch → Welcher Pfad zieht Code rein? Anpassen.
2. **Spike B — AOT-Tauglichkeit.** Selbe App mit `EnableMcpAutomation=true`, `PublishAot=true`. Funktioniert der Source-Gen-Manifest-Path? Wenn nicht → Generator umbauen.
3. **Spike C — stdout/JSON-RPC-Trennung in WPF.** WPF schreibt diverse Diagnostics auf stdout (`TraceSource` etc.). Funktioniert die stderr-Umleitung sauber im `--mcp`-Mode? Spike: WPF-App mit `--mcp` starten, JSON-RPC-Tooling von Claude Code dranhängen, prüfen ob Frame-Parsing sauber läuft.
4. **Spike D — `ModelContextProtocol`-NuGet auf net10.0.** Welche Version ist aktuell stabil? Inkompatibilitäten? Der bestehende N.E.O.-Code nutzt v1.1.0 — ist das auch net10-tauglich?

Phase-0-Output: ein PR (lokal), `PHASE0_FINDINGS.md` mit ✓/✗ pro Spike, Go/No-Go-Entscheidung für Phase 1.

---

## Glossar

- **Hop-Counter / Loop-Protection**: Zählt, wie viele Channel→invoke→Channel-Hops ein Aufruf-Trace schon gesehen hat. Default-Limit 5, env-konfigurierbar via `MARIONETTE_MAX_DEPTH`. Übergrenze → `MethodResult { Success=false, ErrorCode="loop_limit_exceeded" }` + stderr-Log.
- **McpRoot**: Klasse, die als Einstiegspunkt für Manifest-Scanning markiert ist (`[McpRoot]`). Verhindert ungewollte Reflection auf den ganzen AppDomain.
- **Manifest-frozen-on-startup**: Beim Boot wird einmalig per Source-Gen-Tabelle das Manifest aufgebaut. Keine Laufzeit-Mutation außer bei explizitem `RefreshManifest()`-Aufruf.
- **Watchable Observable**: `[McpObservable(Watchable=true)]`-Property → wird als MCP-Resource exponiert, Claude kann via `resources/subscribe` Updates anfordern.
- **Frozen-Mode (geliehen aus N.E.O.)**: App ist deployed *als* MCP-Server-EXE, kein Dev-Sandbox nötig.

## Referenzen aus N.E.O.

Anregungen, nicht Kopier-Vorlagen — die Marionette-Codebasis ist neu und unabhängig:

| Konzept | N.E.O.-Referenz |
|---|---|
| Attribute-Set | `Neo.App.Api/McpAttributes.cs` |
| Manifest-Builder-Pattern | `Neo.App.Mcp/Internal/AppManifestBuilder.cs` |
| Hop-Counter / Loop-Protection-Skizze | `LIVE_MCP_VISION.md` § Spielregeln 3, `LIVE_MCP_PHASE0_FINDINGS.md` § Task 3 |
| Watchable Resources Pattern | `LIVE_MCP_VISION.md` § Spielregeln 8 |
| Channel-Push (`Ai.Trigger`) | `Neo.App.Api/Ai.cs` |
| stdio + GUI Mode-Switching | `LIVE_MCP_VISION.md` § Frozen-Mode |

**Wichtig:** Marionette importiert *keinen* N.E.O.-Code direkt. Alle Konzepte werden frisch implementiert, sauber namespaced, ohne `Neo.*`-Abhängigkeiten.
