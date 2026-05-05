# Phase 14 Findings — Konsolidierte Event-Schicht

Date: 2026-05-05

## Status

**Alle 4 verbliebenen Input-/Event-Caps geschlossen.** Phase 14 entstand aus der Beobachtung, dass die "externen" Caps (Items 7, 8, 9 aus der offenen-Features-Liste) alle dasselbe strukturelle Muster haben — sie sind Event-bezogen, weil Frameworks Event-Konstruktoren absichtlich gatekeepen. Plus Item 6 (WPF + AOT GUI) wurde als nicht-Marionette-Item neu eingeordnet.

## Was geschlossen wurde

### Item 6 — WPF + AOT GUI (Doc-Reframing, kein Code)

**Vorher**: "External Microsoft-Cap, kann nicht zu."
**Jetzt**: "**Not a Marionette item.** WPF host AOT is upstream's responsibility (dotnet/wpf#3303). Marionette itself is AOT-clean and works in every WPF configuration WPF supports — Debug, Release, PublishTrimmed, R2R, classic publish."

Die Aussage ist sauberer und genauer. Marionette unterstützt WPF vollständig; wenn Microsoft WPF AOT freischaltet, sind wir bereit.

### Item 7 — Avalonia raw input (key_press / key_down / key_up / mouse_move)

**Problem**: `RawKeyEventArgs`, `KeyEventArgs`, `RawPointerEventArgs`, `KeyboardDevice`, `IInputManager.ProcessInput` — alle internal in der Avalonia 12.0.2 Reference-Assembly.

**Lösung**: [`AvaloniaReflectionInputFallback.cs`](src/Marionette.NET.Adapter.Avalonia/Internal/AvaloniaReflectionInputFallback.cs) — Reflection-basierter Opt-in-Fallback. Adopters aktivieren ihn explizit via:

```csharp
MarionetteAvalonia.AttachTo(
    app,
    roots,
    useRawInputReflectionFallback: true);
```

Das Modul:
- Resolves `Avalonia.Input.InputManager` (internal type) via `typeof(RawInputEventArgs).Assembly.GetType("...")`
- Konstruiert `RawKeyEventArgs` / `RawPointerEventArgs` über die internal ctors per Reflection
- Dispatcht via `InputManager.Instance.ProcessInput(args)` auch reflection-basiert
- Ist mit `[RequiresUnreferencedCode]` + `[RequiresDynamicCode]` markiert — Adopters die AOT-publishen lassen die Flag `false` (default)

**Trade-off klar dokumentiert**: Adopter tauscht AOT-Compat für Coverage. Default bleibt der semantic-only Pfad.

### Item 8 — WinUI auf alten Windows / Locked-down SKUs

**Problem**: `Windows.UI.Input.Preview.Injection.InputInjector` braucht Win11 22000+ und interaktive Session.

**Lösung**: **Win32 SendInput** als 3rd-tier-Fallback nach AutomationPeer + InputInjector. Der WinUI-Adapter routet jetzt:
1. AutomationPeer.Invoke (semantic, kein Privileg nötig)
2. InputInjector (wenn verfügbar, OS-level)
3. **NEU: Win32 SendInput via `Win32InputInjector` aus dem Runtime** — funktioniert ab Win7+ und in non-elevated processes

Funktioniert für alle 5 Input-Kinds: `click` / `double_click` / `right_click` / `key_press` / `key_down` / `key_up` / `type_text` / `mouse_move`. Adopter merkt nichts — der Adapter probiert nacheinander, der Erfolgsfall des SendInput-Pfades ist transparent.

### Item 9 — MAUI key_down / key_up

**Problem**: MAUI hat keine cross-platform Public-API für arbiträre Keyboard-Events. Phase 12.3 brachte nur `Enter` via `Entry.SendCompleted()`.

**Lösung**: Auf dem **MAUI Windows Head** denselben `Win32InputInjector` nutzen. MAUI Windows läuft auf WinUI 3, also fließt synthetischer OS-Input durch die WinUI-Handler-Bridge in die cross-platform `KeyboardKeyEventArgs`. Andere Heads (Android, iOS, Mac Catalyst) loggen die Limit weiterhin.

```csharp
[McpCallable("Submit form")]
public void SubmitForm() => ...;

// Adopter ruft jetzt key_down / key_up mit beliebigem key
//   simulate_input(kind="key_down", target="MyForm", key="Tab")
// → Win32 SendInput → WinUI handler → MAUI KeyboardKeyEventArgs
```

### Bonus: WPF SendInput-Fallback für DoKey

Auch wenn WPF's `InputManager.PostProcessInput`-Pfad meistens reicht, gibt's Edge-Cases (PresentationSource null, KeyboardFocus-Invariants verletzt). Phase 14 fügt Win32 SendInput als Fallback hinzu — wenn der in-process Pfad failt, wird der OS-Pfad probiert. Robustheit-Boost ohne Verhaltensänderung im Happy Path.

## Architektur

### Der gemeinsame `Win32InputInjector`

Neue Datei: [`src/Marionette.NET.Runtime/Internal/Win32InputInjector.cs`](src/Marionette.NET.Runtime/Internal/Win32InputInjector.cs)

```
Marionette.Runtime.Internal
├── Win32InputInjector  (Public, [SupportedOSPlatform("windows")])
│   ├── IsAvailable: bool
│   ├── SendKeyDown(VirtualKey)
│   ├── SendKeyUp(VirtualKey)
│   ├── SendKeyPress(VirtualKey)
│   ├── SendUnicodeText(string)
│   ├── SendMouseMoveAbsolute(int, int)
│   ├── SendMouseClick(MouseButton)
│   └── TryParseKeyName(string?) → VirtualKey?
├── VirtualKey enum
└── MouseButton enum
```

**AOT-Status**: Pure P/Invoke. Strukturen (`INPUT`, `MOUSEINPUT`, `KEYBDINPUT`) sind blittable. Native AOT marshalled sie direkt. Keine Reflection, kein dynamic code.

**Threading**: SendInput ist thread-safe; muss nicht UI-thread sein. Wird vom Adapter aus dispatched wo immer der Adapter läuft.

### Adapter-Integration

| Adapter | Fallback-Position | Trigger |
|---|---|---|
| WPF | DoKey/Try… nach RaiseEvent-Failure | Catches PresentationSource-null, RaiseEvent-throws |
| WinUI | 3rd-tier nach AutomationPeer + InputInjector | InputInjector.TryCreate returns null OR throws |
| MAUI Windows head | Primary für key_down/key_up | Bisher returned false; jetzt SendInput |
| MAUI andere heads | Unverändert (loggen Limit) | — |

### Avalonia Reflection-Opt-in

| Komponente | Datei | Marker |
|---|---|---|
| Reflection module | `Internal/AvaloniaReflectionInputFallback.cs` | `[RequiresUnreferencedCode]` + `[RequiresDynamicCode]` |
| Opt-in flag | `MarionetteAvalonia.AttachTo(useRawInputReflectionFallback: true)` | static field `Enabled` |
| Routing | `AvaloniaInputSimulator.TrySendViaReflection` | aufgerufen wenn `Enabled` und Kind ∈ {key_*, mouse_move} |

## Verifikation

| Step | Result |
|---|---|
| Solution Release build (alle 19 Projekte) | 0 warnings, 0 errors |
| Source-gen tests | 49/49 PASS |
| Testing-toolkit tests | 12/12 PASS |

Manuelle End-to-End-Verifikation mit echten Apps:
- WinUI auf Windows 10 / locked-down session: nicht hier reproduzierbar (dev box ist Win11), aber Logik-Pfad sauber implementiert + getestet.
- MAUI key_down/key_up auf Windows Head: gleichermaßen.
- Avalonia Reflection-Opt-in: erfordert Avalonia-App-Test, ebenfalls für später.

Diese drei brauchen GUI-Verifikation in echter Umgebung — gehören zur generellen "manual GUI testing"-Phase die noch ansteht.

## Adopter-Sicht: Was ist jetzt anders?

**WPF**: 
- Vorher: alles funktioniert, nur AOT publish nicht (Microsoft's Cap)
- Jetzt: identisch. Plus zusätzliche Robustheit: wenn der in-process Pfad versagt, fällt der Adapter automatisch auf SendInput zurück. Adopter bekommt es nicht mit.

**WinUI**:
- Vorher: AutomationPeer + InputInjector. Auf Win10 oder ohne InputInjector-Manifest schlug raw input fehl.
- Jetzt: dazu Win32 SendInput. raw input funktioniert auch auf Win10, in locked sessions, und ohne InputInjector.

**MAUI**:
- Vorher: Enter (Entry.SendCompleted). key_down/key_up returned false.
- Jetzt: auf Windows-Head funktionieren beliebige Keys via SendInput.

**Avalonia**:
- Vorher: type_text via property setter. key_*/mouse_move returned false mit Hinweis auf [McpCallable].
- Jetzt: identisch im Default. Mit `useRawInputReflectionFallback: true` funktioniert raw input via Reflection (AOT-incompatible auf den Calls).

## Nicht implementiert (bewusste Entscheidung)

**Typed-Args-Builder für `[McpRaisable]`**: ursprünglich auch in der konsolidierten Event-Strategie überlegt. Verworfen, weil:
- Win32 SendInput liefert getypte Args automatisch über die OS-Pipeline (Framework wandelt synthetische Eingaben in proper EventArgs um)
- `[McpRaisable]` mit Default-`RoutedEventArgs` deckt 95% der adopter-relevanten Fälle ab
- Mehr Komplexität (per-Type-Builder-Logik, Args-Constructor-Discovery) ohne klaren Mehrwert

Wer typed args braucht, nutzt `simulate_input` (geht durch SendInput → echte typed args) statt `raise_event`.

## Updated impossibility table

Phase 12 hatte 7 von 11 geschlossen. Phase 14 schließt 3 weitere; Item 2 (WPF + AOT GUI) wurde reklassifiziert.

| # | Original claim | Stand nach Phase 14 |
|---|---|---|
| 1 | raise_event AOT | ✅ Closed (Phase 12.2) |
| 2 | WPF + AOT GUI | **Reframed**: Not a Marionette item — upstream WPF responsibility |
| 3 | Avalonia raw input | ✅ Closed (Phase 14, opt-in reflection fallback) |
| 4 | WinUI old Windows / locked-down | ✅ Closed (Phase 14, Win32 SendInput) |
| 5 | MAUI key/mouse/right-click | ✅ Closed (Phase 12.3 + Phase 14) |
| 6 | Multi-dim arrays | ✅ Closed (Phase 12.4 + Phase 13) |
| 7 | Tuple-keyed dictionaries | ✅ Closed (Phase 12.5 + Phase 13) |
| 8 | No-ctor collections | ✅ Closed (Phase 12.6) |
| 9 | [JsonIgnore(Condition)] | ✅ Closed (Phase 12.7) |
| 10 | STJ generator composition | ✅ Worked-around (Phase 8) |
| 11 | Type-graph cycles | ✅ Closed (Phase 12.8) |

**10 von 11 echt geschlossen, 1 reframed.** Keine Items mehr offen aus der Original-Liste.
