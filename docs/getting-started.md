# Getting Started

This preview can be consumed either from project references inside this repository or from the local NuGet packages produced by Phase 7.

## Local NuGet Source

Build local packages:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\pack-local.ps1
```

Add the package source to a consuming app:

```powershell
dotnet nuget add source C:\Home\Code\nw.Automation\artifacts\nuget --name MarionetteLocal
```

Then reference the meta-package:

```powershell
dotnet add package Marionette.NET --version 0.1.0-preview.1 --source C:\Home\Code\nw.Automation\artifacts\nuget
```

The meta-package supplies:

- `Marionette.NET.Abstractions` transitively for attributes.
- The source generator as an analyzer.
- `EnableMcpAutomation` defaults: Debug on, Release off.
- Compile assets required by generated descriptors.
- Skill-pack and docs as package content.

For app hosting, add the adapter package that matches your UI stack:

```powershell
dotnet add package Marionette.NET.Adapter.Wpf --version 0.1.0-preview.1 --source C:\Home\Code\nw.Automation\artifacts\nuget
dotnet add package Marionette.NET.Adapter.Avalonia --version 0.1.0-preview.1 --source C:\Home\Code\nw.Automation\artifacts\nuget
dotnet add package Marionette.NET.Adapter.WinUI --version 0.1.0-preview.1 --source C:\Home\Code\nw.Automation\artifacts\nuget
dotnet add package Marionette.NET.Adapter.Maui --version 0.1.0-preview.1 --source C:\Home\Code\nw.Automation\artifacts\nuget
```

## Minimal Root

```csharp
using Marionette;

[McpRoot]
public sealed class TodoRoot
{
    [McpCallable("Add a new TODO with the given title.")]
    public void AddTodo(string title)
    {
        // mutate app state
    }

    [McpObservable("Total number of TODOs.", Watchable = true)]
    public int TotalCount => 0;
}
```

Debug builds emit `Marionette.Generated.GeneratedManifest`. Release builds default to stripped mode and emit no manifest unless `EnableMcpAutomation=true`.

## Host Wiring

Use the adapter guide for framework-specific `AttachTo(...)` calls:

- WPF: `Marionette.Adapter.Wpf.MarionetteWpf.AttachTo(...)`
- Avalonia: `Marionette.Adapter.Avalonia.MarionetteAvalonia.AttachTo(...)`
- WinUI: `Marionette.Adapter.WinUI.MarionetteWinUI.AttachTo(...)`
- MAUI: `Marionette.Adapter.Maui.MarionetteMaui.AttachTo(...)`

The samples are the canonical references for now:

- `samples/Sample.Wpf.TodoApp`
- `samples/Sample.Avalonia.Dashboard`
- `samples/Sample.WinUI.FormLab`
- `samples/Sample.Maui.PocketPlanner`

## Local Verification

Run the full local release check:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\release-local.ps1
```

This builds, tests, packs, publishes showcases, dogfoods the published executables in headless MCP mode, and regenerates README GIF assets. It does not push to Git, NuGet, or GitHub.
