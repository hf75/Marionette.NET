# Phase 6 Findings - Testing Toolkit + DX Polish

Date: 2026-05-04

## Status

GREEN for the local Phase 6 implementation scope. The repo now has an in-process testing toolkit, thin xUnit/NUnit adapters, a Roslyn DX hint for missing `[McpCallable]` decorations, skill-pack v2 workflow coverage, and adopter-facing docs for architecture, adapter authoring, stripping, and testing.

## Deliverables

| Masterplan item | Status | Notes |
|---|---|---|
| `Marionette.NET.Testing` NuGet | Done | Neutral in-process host over production `MarionetteTools`, assertion helpers, typed/raw APIs. |
| xUnit/NUnit adapter | Done | Thin helper packages: `Marionette.NET.Testing.Xunit` and `Marionette.NET.Testing.NUnit`. |
| App in-process, MCP calls simulated directly | Done | Tests bind generated descriptors to live instances and invoke runtime tools without stdio. |
| Assertion API | Done | `MarionetteAssert`, `MarionetteToolException`, structured error detection/deserialization. |
| VS Diagnostic Analyzer hint | Done | `MAR013` reports info diagnostics for public root methods that could be `[McpCallable]`. |
| Skill-Pack v2 | Done | Added slash-command triggers and `/automate-this-flow` workflow. |
| README polish | Done | Status, package list, docs, and test commands updated. |
| Architecture doc | Done | `docs/architecture.md`. |
| Adapter Authoring Guide | Done | `docs/adapter-authoring.md`. |
| Stripping Guide | Done | `docs/stripping.md`. |
| Testing guide | Done | `docs/testing.md`. |

## Validation

- `dotnet build Marionette.NET.sln -c Debug`
- `dotnet test tests\Marionette.NET.Testing.Tests\Marionette.NET.Testing.Tests.csproj -c Debug --no-restore`
- `dotnet test tests\Marionette.NET.SourceGenerator.Tests\Marionette.NET.SourceGenerator.Tests.csproj -c Debug --no-restore`
- `dotnet test tests\Marionette.NET.Integration\Marionette.NET.Integration.csproj -c Debug --no-restore`

The integration suite keeps GUI-only cases behind `MARIONETTE_GUI_TESTS=1`.

## Remaining Phase 7 Work

- NuGet package publishing.
- Meta-package distribution.
- Automated skill-pack installation.
- Showcase publishing and release media.
