# Skill-Pack

The skill-pack teaches LLM agents how to inspect, decorate, test, and automate Marionette.NET apps through MCP.

## Skills

| Slash command | Skill | Purpose |
|---|---|---|
| `/explore-this-app` | `marionette-explore` | Summarize `inspect_app_api`, observables, events, and next tool calls. |
| `/decorate-app` | `marionette-decorate` | Add `[McpRoot]`, `[McpCallable]`, `[McpObservable]`, `[McpEvent]`, and adapter wiring to an app. |
| `/test-this-app` | `marionette-test` | Smoke-test a running app by invoking callables and verifying observables. |
| `/automate-this-flow` | `marionette-automate-flow` | Execute a concrete user workflow with semantic calls and optional UI-path verification. |

## Location

Repository source:

```text
skill-pack/
```

NuGet meta-package content:

```text
contentFiles/any/any/skill-pack/
```

## Manual Install

Until the final installer exists, copy the skill directories into the agent's skill path. For Claude Code on Windows:

```powershell
xcopy /E /I skill-pack\claude-code\marionette-explore "%USERPROFILE%\.claude\skills\marionette-explore"
xcopy /E /I skill-pack\claude-code\marionette-decorate "%USERPROFILE%\.claude\skills\marionette-decorate"
xcopy /E /I skill-pack\claude-code\marionette-test "%USERPROFILE%\.claude\skills\marionette-test"
xcopy /E /I skill-pack\claude-code\marionette-automate-flow "%USERPROFILE%\.claude\skills\marionette-automate-flow"
```

Copy `skill-pack\prompts\attributes-reference.md` alongside each skill if the agent does not resolve shared prompt files from the repository root.

## Validation

The published showcase dogfood checks exercise the same tool shapes the skill-pack uses:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .phase7\dogfood-showcases.ps1
```

This verifies `inspect_app_api`, `invoke_method`, `read_observable`, dynamic per-method tools, event resources, and screenshot unsupported errors in headless mode.
