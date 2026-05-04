# Phase 6a - Testing Toolkit + DX Polish

> Status: in progress
> Date: 2026-05-04
> Scope: first production slice of the masterplan's Phase 6, after the Phase 4 reordering that moved MAUI/AOT hardening earlier.

## Goal

Ship a small, stable `Marionette.NET.Testing` surface that lets adopters test their Marionette contracts in-process, without launching a stdio MCP child process and without needing Claude in the loop.

The testing toolkit should exercise the same runtime paths the MCP server uses:

- `inspect_app_api` through `MarionetteTools.InspectAppApi`.
- `invoke_method` through `MarionetteTools.InvokeMethodAsync`, which reuses `MarionetteDispatch`.
- `read_observable` through `MarionetteTools.ReadObservableAsync`.
- The same `ManifestRegistry`, `IUiAutomationAdapter`, and `LoopProtectionService` contracts as production.

## Deliverables For This Slice

- Add `src/Marionette.NET.Testing`.
- Add a neutral `MarionetteTestHost` API:
  - construct from source-generator roots (`GeneratedManifest.Roots`);
  - invoke callables by root/method;
  - read observables by root/property;
  - expose raw JSON and typed convenience methods;
  - expose loop-reset and instance binding helpers for tests.
- Add a neutral assertion layer:
  - detect Marionette structured errors;
  - throw `MarionetteToolException` with `ErrorCode`, `Message`, and raw JSON;
  - deserialize successful JSON results for xUnit/NUnit/MSTest users.
- Add `tests/Marionette.NET.Testing.Tests` covering the first vertical slice.
- Wire both projects into `Marionette.NET.sln`.

## Out Of Scope For This Slice

- NUnit-specific package. The core API is test-framework-neutral; thin xUnit/NUnit adapters can be added once the neutral API settles.
- GUI runtime-skip migration for EC-8/EC-9/EC-10. That depends on the xUnit v3 move and should be a separate, tightly scoped change.
- VS analyzer suggestions for "could be `[McpCallable]`". That belongs after the testing API is in place.
- Full docs set (`docs/getting-started.md`, adapter-authoring, stripping guide). This slice only updates the repo-level status enough to make the new project discoverable.

## Acceptance

- `dotnet build Marionette.NET.sln -c Debug` passes.
- `dotnet test tests/Marionette.NET.Testing.Tests/Marionette.NET.Testing.Tests.csproj -c Debug` passes.
- Existing SourceGenerator and Integration suites continue to pass.
