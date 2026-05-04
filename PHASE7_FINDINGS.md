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

1. ~~User manually runs the local showcase apps in GUI mode.~~ Done — all three showcases (WPF TodoApp, Avalonia Dashboard, WinUI FormLab) launch in GUI mode and render correctly. The WinUI FormLab follow-up below was discovered and fixed during this verification.
2. User confirms NuGet package contents from `artifacts\nuget`.
3. User decides whether to publish packages.
4. User performs `git push origin main`.
5. GitHub release is created after package availability is confirmed.

## Phase 7 follow-up — WinUI FormLab unpackaged-publish XAML crash

**Symptom:** the published `Sample.WinUI.FormLab.exe` (under `artifacts\showcases\winui-formlab\`) crashed at startup with exit code `0xc000041d` / `0xc000027b` (`STATUS_APPLICATION_INTERNAL_EXCEPTION`); WER reported `combase.dll` HRESULT `0x80004005` from `Microsoft.UI.Xaml.dll`. The dev `bin\Debug` and `bin\Release` builds ran fine. The headless MCP handshake (`dogfood-showcases.ps1`) also passed because it never instantiates the XAML root — only the GUI mode hit the crash.

**Root cause:** `dotnet publish` on an unpackaged WinUI 3 app (`<WindowsPackageType>None</WindowsPackageType>`) with `--self-contained false` does NOT forward the XAML compiler's binary outputs from the build output to the publish directory. Specifically:
- `App.xbf` and `MainWindow.xbf` (per-XAML compiled markup) — present in `@(_GeneratedXBFFiles)` but never enrolled in `@(ResolvedFileToPublish)`.
- `Sample.WinUI.FormLab.pri` (the leaf app's MRT resource index) — `_PriFiles` is empty and `_ResolvedCopyLocalPublishAssets` only carries dependency-project pri files. Without the leaf .pri the XAML loader cannot resolve `ms-appx:///` URIs.

**Fix:** custom `AfterTargets="Publish"` MSBuild target in `Sample.WinUI.FormLab.csproj` that copies the .xbf files and the leaf .pri from `$(OutDir)` to `$(PublishDir)` when `WindowsPackageType=None`. This is the pattern used by most unpackaged WinUI 3 sample projects on GitHub. The fix is leaf-app-local — `Marionette.NET.Adapter.WinUI` is a library and continues to publish via the existing flow.

**Verification post-fix:** publish output contains `App.xbf` + `MainWindow.xbf` + `Sample.WinUI.FormLab.pri`; published EXE launches with title "Marionette WinUI FormLab" and renders the full settings form; `.phase7\dogfood-showcases.ps1` headless smoke test continues to PASS for all three showcases.
