# Stripping Guide

Marionette.NET is designed so Release builds can ship with no MCP runtime symbols while Debug builds remain fully automatable.

## Build Switch

The controlling MSBuild property is:

```xml
<EnableMcpAutomation>true</EnableMcpAutomation>
```

Project defaults come from `build/Marionette.NET.props`:

- Debug builds default to `true`.
- Release builds default to `false`.

When automation is disabled, the build should keep only `Marionette.NET.Abstractions` attribute metadata. Runtime, adapter, MCP transport, and generated manifest code should disappear from the final application closure.

## Conditional API

`Ai.Trigger(...)` and `Ai.ScheduleTrigger(...)` are compiled only when `MCP_ENABLED` is defined. The source generator also checks `MCP_ENABLED`:

- Enabled: emits `Marionette.g.cs`.
- Disabled: emits no source, but still reports diagnostics so IDE feedback remains available.

This means stripped builds keep useful authoring squiggles without accidentally referencing runtime descriptor types.

## Verification

Use the IL probe for regression checks:

```powershell
pwsh build/Run-IlProbe.ps1
```

The expected stripped result is zero references to:

- `Marionette.NET.Runtime`
- any `Marionette.NET.Adapter.*`
- `ModelContextProtocol`
- `Marionette.Generated.GeneratedManifest`

## AOT Publish

For Windows desktop publish checks, use the repository samples as templates. The current validated path is:

- WPF stripped publish succeeds.
- WPF full MCP publish succeeds for frozen/headless MCP mode.
- Avalonia publish plus stdio handshake is covered in CI.
- WinUI and MAUI use Windows-specific TFMs and should be validated on a Windows runner.

Native AOT on Windows requires the Visual Studio C++ workload. GitHub-hosted `windows-latest` includes it; self-hosted runners need it installed explicitly.

## Adapter Guidance

The source generator is the main reason stripping works. Avoid adding runtime reflection over user roots or attributes in adapter code. If an adapter needs reflection for framework event plumbing, isolate it to the adapter and annotate the method with a focused trim warning justification.

## Consumer Guidance

Use explicit properties when validating a package:

```powershell
dotnet publish .\samples\Sample.Wpf.TodoApp\Sample.Wpf.TodoApp.csproj -c Release -p:EnableMcpAutomation=false
dotnet publish .\samples\Sample.Wpf.TodoApp\Sample.Wpf.TodoApp.csproj -c Release -p:EnableMcpAutomation=true
```

The first command is the production footprint check. The second command is the frozen MCP tool check.
