# Local Release Runbook

Phase 7 is prepared as a local release candidate. The user explicitly owns the final `git push`, NuGet push, and GitHub release.

## One Command

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\release-local.ps1
```

The script performs:

1. Debug solution build.
2. Source-generator tests.
3. Testing-toolkit tests.
4. Integration tests.
5. Local NuGet packing into `artifacts\nuget`.
6. Local package consumption smoke test with a fresh WPF app.
7. Showcase publish into `artifacts\showcases`.
8. Headless MCP dogfood checks for WPF, Avalonia, and WinUI published apps.
9. Demo GIF generation into `docs\media`.

It does not run:

- `git push`
- `dotnet nuget push`
- GitHub release creation
- GitHub Actions dispatch

## Individual Commands

Pack only:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\pack-local.ps1
```

Test local package consumption:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\test-local-package-consumption.ps1
```

Publish showcases:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\publish-showcases.ps1
```

Dogfood published showcases:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\dogfood-showcases.ps1
```

Regenerate GIFs:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\New-DemoGifs.ps1
```

## Artifacts

Generated artifacts are intentionally ignored by Git:

- `artifacts\nuget\*.nupkg`
- `artifacts\showcases\**`
- `artifacts\consume-wpf\**`

Tracked release assets:

- `docs\media\wpf-todo.gif`
- `docs\media\avalonia-dashboard.gif`
- `docs\media\winui-formlab.gif`
- `PHASE7_FINDINGS.md`

## Manual Release After Testing

After local testing, the publish sequence is:

```powershell
git status
dotnet nuget push artifacts\nuget\*.nupkg --api-key <key> --source https://api.nuget.org/v3/index.json
git push origin main
```

GitHub release creation should happen only after the pushed commit and NuGet package availability are confirmed.
