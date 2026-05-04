# WinUI input-injection guide

> Status: Phase 9.3 (2026-05-04) reflects current Windows 11 behaviour.

`simulate_input` on the WinUI adapter dispatches through two paths:

1. **AutomationPeer / `IInvokeProvider`** — used for `click` /
   `double_click` and for the `type_text` shortcut on `TextBox`. Always
   works, no elevation, no manifest declaration. This is the recommended
   path for the most common LLM-driven UI flows.

2. **`Windows.UI.Input.Preview.Injection.InputInjector`** — used for
   `key_press` / `key_down` / `key_up` / `mouse_move` and for the
   `right_click` and arbitrary-target `type_text` paths. Real OS-level
   input that drives the entire framework input pipeline (focus changes,
   capture, hover, key-routing).

The adapter always tries path 1 first; path 2 is the fallback when
the target isn't an `IInvokeProvider` or the kind requires real input.

## InputInjector availability

`InputInjector.TryCreate()` returns a working injector handle on:

- **Windows 11 22H2 and newer (default behaviour).** Verified May 2026
  on a stock Windows 11 build 26200, .NET 10.0.6 — both an unpackaged
  console app and an unpackaged WinUI 3 process get a non-null
  injector with no manifest declarations and no elevation.
- **Windows 10 22H2 with elevated process** (`Run as administrator`).
- **Any Windows version when the app is MSIX-packaged with the
  `inputInjectionBrokered` restricted capability** (see below).

It returns `null` on:

- Older Windows 10 builds (≤ 21H2) without elevation.
- Windows Sandbox / Hyper-V VMs with input-injection blocked at the
  host level.
- Locked-down enterprise / education SKUs that explicitly disable
  the input injection broker.

## Adapter behaviour when injector is unavailable

The adapter probes once at construction time and writes a single log
line so adopters know the state up front:

```
[Information] WinUI input-injection probe: InputInjector available — full
simulate_input matrix (click / key_* / type_text / mouse_move) operational.
```

When the probe returns null:

```
[Information] WinUI input-injection probe: InputInjector unavailable.
simulate_input falls back to AutomationPeer for clicks and TextBox.Text
for type_text. Other kinds (key_press, mouse_move) will return success=false.
See docs/winui-input-injection.md.
```

Without the injector, `simulate_input(key_press|...|mouse_move)` returns
`{success:false, errorCode:"simulate_input_not_supported"}` so the LLM
knows to fall back to a `[McpCallable]` semantic action.

## Fix paths for adopters on constrained systems

### Path A — run elevated

The simplest path. The host process getting the injector is enough; the
adopter app itself doesn't need any manifest changes.

- **Dev:** start Visual Studio / `dotnet run` with admin rights.
- **End-user adopters:** include an admin-elevation manifest fragment
  in your app's `app.manifest`:

  ```xml
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  ```

  Costs: triggers a UAC prompt at every launch. Most users will
  reasonably push back on this.

### Path B — MSIX-packaged with `inputInjectionBrokered`

The "no UAC prompt, works for everyone" path, at the cost of
deploying as an MSIX rather than a loose EXE.

1. Switch the project from unpackaged to packaged WinUI:

   ```xml
   <PropertyGroup>
     <WindowsPackageType>MSIX</WindowsPackageType>
     <EnableMsixTooling>true</EnableMsixTooling>
   </PropertyGroup>
   ```

2. Add (or generate) a `Package.appxmanifest` and declare the
   restricted capability:

   ```xml
   <Package
     xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
     xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
     IgnorableNamespaces="uap rescap">
     ...
     <Capabilities>
       <rescap:Capability Name="inputInjectionBrokered" />
     </Capabilities>
   </Package>
   ```

3. Sign the MSIX with a code-signing certificate (development cert
   produced by Visual Studio is fine for sideloading; Store
   distribution requires a Trusted Publisher cert).

4. The adopter (or end user) installs the MSIX via the standard
   sideload flow (`Add-AppxPackage` / Microsoft Store).

Costs: more deployment plumbing, restricted capability requires
Microsoft approval if the adopter wants to publish via the Store
(sideloaded distributions don't need approval).

## Why doesn't the FormLab showcase use Path B?

`Sample.WinUI.FormLab` deliberately ships unpackaged so it can demo as
a single double-clickable EXE next to the WPF / Avalonia / MAUI samples.
Going to MSIX would diverge from that pattern. On the test machine
the Phase 9.3 probe confirms `InputInjector` is available
unpackaged-unelevated under current Windows 11, so the showcase exercises
the full `simulate_input` matrix without manifest changes.

Adopters who target older Windows builds, locked-down SKUs, or who
need the LLM to drive arbitrary-control keyboard input as a
hard-deployment guarantee should follow Path A or B in their own
project. The adapter behaviour is identical either way — what changes
is whether `InputInjector.TryCreate()` returns a non-null handle.

## Alternative: `[McpCallable]` semantic methods

Per masterplan tenet 2 ("semantic > visual"), the recommended pattern
for production LLM-driven UI is to expose semantic actions as
`[McpCallable]` methods on your `[McpRoot]` ViewModel — not to drive
keyboard input through `simulate_input`. The LLM calls
`invoke_method(MyVm.SubmitForm)` instead of typing keystrokes.

`simulate_input(key_press)` is the right tool when:

- Testing real keyboard handlers (key-down handlers that aren't
  reachable via a public ViewModel method);
- Driving framework controls whose keyboard semantics aren't
  observable to your ViewModel (focus management, key-bound shortcuts);
- Reproducing user-input bug repros faithfully.

For most "click submit", "type into form, press enter to submit"
flows, `[McpCallable]` is the cleaner contract — no injector
availability concerns, AOT-clean, deterministic.
