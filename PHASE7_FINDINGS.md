# Phase 7 Findings - Distribution + Dogfooding

Date: 2026-05-04

## Status

GREEN for local release-candidate readiness. External publishing was intentionally not performed.

## Deliverables

| Masterplan item | Status | Notes |
|---|---|---|
| NuGet prerelease packages | Local done | 11 `.nupkg` packages generated under `artifacts\nuget`; no push. |
| Showcase apps published | Local done | WPF, Avalonia, and WinUI publish to `artifacts\showcases`. |
| 90-second demo video | Local substitute | Reproducible GIF dogfood transcripts generated under `docs\media`; no manual video recording. |
| README with animated GIF demos | Done | README embeds WPF, Avalonia, WinUI demo GIFs. |
| Git push / GitHub release | Skipped by request | User will test first and push manually. |

## Validation

Commands run locally:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\pack-local.ps1 -Configuration Release -Version 0.1.0-preview.1
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\test-local-package-consumption.ps1 -Version 0.1.0-preview.1
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\publish-showcases.ps1 -Configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\dogfood-showcases.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\New-DemoGifs.ps1
```

Current pass criteria:

- Package build: all 11 local packages created.
- Package consumption: fresh WPF app references `Marionette.NET` and builds with a generated manifest.
- WPF TodoApp published EXE: headless MCP handshake PASS.
- Avalonia Dashboard published EXE: headless MCP handshake PASS.
- WinUI FormLab published EXE: headless MCP handshake PASS.
- Demo GIFs generated:
  - `docs\media\wpf-todo.gif`
  - `docs\media\avalonia-dashboard.gif`
  - `docs\media\winui-formlab.gif`

## Release Gate

Before public release:

1. User manually runs the local showcase apps in GUI mode.
2. User confirms NuGet package contents from `artifacts\nuget`.
3. User decides whether to publish packages.
4. User performs `git push origin main`.
5. GitHub release is created after package availability is confirmed.
