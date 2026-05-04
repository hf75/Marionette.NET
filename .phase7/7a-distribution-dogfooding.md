# Phase 7a - Distribution + Dogfooding

Date: 2026-05-04

## Scope

Phase 7 was implemented as a local release candidate. External publication is deliberately out of scope for this slice:

- No `git push`.
- No `dotnet nuget push`.
- No GitHub release creation.

## What Landed

- Version and package metadata moved to `0.1.0-preview.1`.
- Local package script: `.phase7/pack-local.ps1`.
- Meta-package: `src/Marionette.NET`.
- Individual packages pack locally:
  - `Marionette.NET`
  - `Marionette.NET.Abstractions`
  - `Marionette.NET.SourceGenerator`
  - `Marionette.NET.Runtime`
  - `Marionette.NET.Adapter.Wpf`
  - `Marionette.NET.Adapter.Avalonia`
  - `Marionette.NET.Adapter.WinUI`
  - `Marionette.NET.Adapter.Maui`
  - `Marionette.NET.Testing`
  - `Marionette.NET.Testing.Xunit`
  - `Marionette.NET.Testing.NUnit`
- Local package consumption smoke test: `.phase7/test-local-package-consumption.ps1`.
- Local showcase publish script: `.phase7/publish-showcases.ps1`.
- Showcase dogfood script: `.phase7/dogfood-showcases.ps1`.
- Demo GIF generation: `.phase7/New-DemoGifs.ps1`.
- One-command release candidate runner: `.phase7/release-local.ps1`.
- README now embeds generated demo GIFs.

## Packaging Note

The `Marionette.NET` meta-package includes the source generator directly under `analyzers/dotnet/cs` and depends on:

- `Marionette.NET.Abstractions`
- `Marionette.NET.Runtime` with runtime assets excluded

That shape lets generated descriptor code compile in consumer projects without forcing the runtime DLL into every stripped output through the meta-package alone. Apps still add the matching adapter package when they wire `AttachTo(...)`.

## Dogfood Matrix

| Artifact | Check |
|---|---|
| Local `.nupkg` source | Fresh WPF app references `Marionette.NET`, adds `[McpRoot]`, builds Debug. |
| WPF TodoApp publish | Headless MCP handshake passes with dynamic tools, resources, events. |
| Avalonia Dashboard publish | Headless MCP handshake passes with async callable, resources, events. |
| WinUI FormLab publish | Headless MCP handshake passes with form state, event resource. |

## Known Deliberate Deferrals

- No public NuGet push until manual user testing is complete.
- No Git push until manual user testing is complete.
- No GitHub release until pushed commit and public NuGet packages exist.
- Demo GIFs are generated dogfood transcripts, not recorded GUI sessions.
