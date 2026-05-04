# Marionette.NET — Skill-Pack v2

The skill-pack teaches Claude (and adapter-compatible LLM agents) how to use Marionette.NET. Without it, models tend to fumble the attribute placement (wrong target, wrong namespace, missing `[McpRoot]` on the class) and the runtime's tool-call patterns (wrong root name, missing `args`, dead-letter on `loop_limit_exceeded`). The skill-pack bakes in the conventions so adopters get fast, correct LLM-driven flows out of the box.

## Layout

```
skill-pack/
|-- README.md                              <-- you're here
|-- claude-code/
|   |-- marionette-explore/SKILL.md        <-- "what does this app expose?"
|   |-- marionette-decorate/SKILL.md       <-- "add MCP attributes to my app"
|   |-- marionette-test/SKILL.md           <-- "smoke-test the decoration"
|   `-- marionette-automate-flow/SKILL.md  <-- "perform this workflow"
`-- prompts/
    `-- attributes-reference.md            <-- attribute spec + Ai channel API
                                               (cited by all four skills)
```

The four skills share the canonical attribute reference at `prompts/attributes-reference.md`. Adopters using a non-Claude agent (Cursor, Cline, Aider, etc.) can read that reference directly — it's the same source-of-truth.

## Install — Phase 1 (manual)

For Phase 1 (this release), distribution is manual. Copy the SKILL.md folders into your Claude Code skills directory:

```
# Windows
xcopy /E /I skill-pack\claude-code\marionette-explore   %USERPROFILE%\.claude\skills\marionette-explore
xcopy /E /I skill-pack\claude-code\marionette-decorate  %USERPROFILE%\.claude\skills\marionette-decorate
xcopy /E /I skill-pack\claude-code\marionette-test      %USERPROFILE%\.claude\skills\marionette-test
xcopy /E /I skill-pack\claude-code\marionette-automate-flow %USERPROFILE%\.claude\skills\marionette-automate-flow

# macOS / Linux
cp -R skill-pack/claude-code/marionette-explore   ~/.claude/skills/marionette-explore
cp -R skill-pack/claude-code/marionette-decorate  ~/.claude/skills/marionette-decorate
cp -R skill-pack/claude-code/marionette-test      ~/.claude/skills/marionette-test
cp -R skill-pack/claude-code/marionette-automate-flow ~/.claude/skills/marionette-automate-flow
```

Restart Claude Code. The four skills should appear when the user says one of the trigger phrases listed in each `SKILL.md`.

For the attributes reference, drop a copy alongside the skills (Claude Code skills with relative path imports look in the skill directory first, then the user's plugin directory):

```
cp skill-pack/prompts/attributes-reference.md  ~/.claude/skills/marionette-explore/
# (and into marionette-decorate / marionette-test / marionette-automate-flow as well)
```

## Install — Phase 7 (automated)

Phase 7 of the Marionette.NET roadmap ships a NuGet meta-package that auto-installs the skill-pack when `claude install Marionette.NET` runs. The directory layout above is preserved 1:1, so the manual paths above will continue to work for direct copies.

## Slash-command triggers

Skill-Pack v2 recognizes these user-facing command phrases:

- `/explore-this-app` -> `marionette-explore`
- `/decorate-app` -> `marionette-decorate`
- `/test-this-app` -> `marionette-test`
- `/automate-this-flow` -> `marionette-automate-flow`

## The four skills

### `marionette-explore`

**Trigger:** `/explore-this-app`, "explore this app", "what can this app do?", "show me what's there", "list the manifest". Use this when the user has a Marionette MCP server connected and wants a tour.

**Does:**

1. Calls `inspect_app_api()` to get the full root list.
2. For each root, prints callables / observables / triggerables in human-readable form.
3. Optionally captures a screenshot.
4. Suggests concrete next-step tool calls.

### `marionette-decorate`

**Trigger:** `/decorate-app`, "make this Marionette-controllable", "add MCP attributes", "decorate this for Claude". Use this when the user has an existing C# WPF/Avalonia/WinUI/MAUI/Uno app that has not yet been instrumented.

**Does:**

1. Reads the project's class structure.
2. Identifies likely root classes (typically ViewModels or service classes).
3. Suggests `[McpCallable]` / `[McpObservable]` / `[McpTriggerable]` placements with inferred descriptions.
4. Edits the relevant files to add the attributes plus `INotifyPropertyChanged` plumbing.
5. Reminds the user to add `[McpRoot]` to the class.
6. Verifies the project still builds (and surfaces any MAR001-MAR008 diagnostics).
7. Shows the canonical `App.OnStartup` wiring snippet for the framework.

Includes an extensive "things NOT to decorate" section: getters that throw, methods on disposed objects, async methods returning non-awaitable types, etc.

### `marionette-test`

**Trigger:** `/test-this-app`, "test this app", "verify the app works", "smoke-test the Marionette app". Use this when the user wants confidence the decoration holds end-to-end.

**Does:**

1. Calls `inspect_app_api()` to discover the surface.
2. Generates a sensible test invocation per `[McpCallable]` (heuristic, not exhaustive).
3. Reads each `[McpObservable]` before and after each invocation; asserts meaningful reaction.
4. Captures a screenshot at the end.
5. Reports a structured PASS / SKIP / FAIL summary.

Acknowledges in the report that test generation is heuristic — it's a smoke test, not a property test.

### `marionette-automate-flow`

**Trigger:** `/automate-this-flow`, "automate this flow", "drive this workflow", "perform this task in the app". Use this when the user gives a concrete app workflow to execute through Marionette.

**Does:**

1. Calls `inspect_app_api()` and maps the requested flow to exact root/callable/control names.
2. Prefers semantic `invoke_method` calls for domain operations.
3. Uses `simulate_input` / `raise_event` when the user asks for real UI-path coverage.
4. Verifies each step through observables, events, resources, and screenshots where useful.
5. Reports a concise PASS / FAIL flow transcript.

## Reference

`prompts/attributes-reference.md` is the canonical reference for:

- The four attributes (`McpRoot`, `McpCallable`, `McpObservable`, `McpTriggerable`).
- The `Ai` channel API (`Ai.Trigger`, `Ai.ScheduleTrigger`).
- The four runtime MCP tools (`inspect_app_api`, `invoke_method`, `read_observable`, `capture_screenshot`).
- Common decoration mistakes and how to spot them.

Treat that document as the spec; the skills are workflows on top of it.
