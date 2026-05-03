---
name: marionette-explore
description: Discover what a running Marionette.NET-instrumented .NET desktop app exposes through MCP. Use this skill whenever the user asks to "explore this app", "what can this app do", "show me what's there", "list the manifest", "what's controllable", or whenever a Marionette MCP server (stdio) is connected and the user wants a tour of its surface. The skill calls inspect_app_api, formats the manifest in human-readable form, optionally captures a screenshot, and suggests concrete next-step tool calls.
---

# Marionette: Explore an Instrumented App

You have a Marionette.NET MCP server connected (transport: stdio). The user wants a guided tour of the app's exposed surface — every `[McpRoot]` class, every `[McpCallable]` method, every `[McpObservable]` property, every `[McpTriggerable]` button, every `[McpEvent]` event. Be concrete: name the methods, name the observables, suggest exact tool calls.

## Trigger conditions

Use this skill when the user says any of:

- "explore this app", "what can this app do", "show me what's there", "what's exposed"
- "list the manifest", "list the tools", "what's controllable", "tour the app"
- After the user runs `claude mcp add ...` against a Marionette EXE and asks "now what?"

## Procedure

Follow these steps in order. Stop and report findings if any step fails — don't try to recover by inventing data.

### 1. Call `inspect_app_api()` with no arguments

This returns a JSON array of every `[McpRoot]`-decorated class in the app. Each entry has the shape:

```json
{
  "name": "TodoListViewModel",
  "typeName": "Sample.Wpf.TodoApp.TodoListViewModel",
  "instanceAvailable": true,
  "callables": [...],
  "observables": [...],
  "triggerables": [...],
  "events": [...]
}
```

If `instanceAvailable` is `false`, surface the `createError` field — the app's wiring is broken and no tool calls will work against this root.

### 2. For each root, summarize its surface

Print a structured report with these sections (one block per root). Keep it scannable — bullet lists, not prose. Example shape:

```
Root: TodoListViewModel  (Sample.Wpf.TodoApp.TodoListViewModel)

  Callables (5):
    - AddTodo(title: string)         — Add a new TODO with the given title
    - RemoveTodo(index: int)         — Remove the TODO at the given index
    - ToggleDone(index: int)         — Flip the done flag
    - ClearCompleted()               — Remove every done TODO
    - RenameTodo(index: int, newTitle: string) — Rename in place

  Observables (4):
    - TotalCount: int      [watchable]   — Total number of todos
    - CompletedCount: int  [watchable]   — Number of completed todos
    - PendingCount: int    [watchable]   — Number of pending todos
    - LastAddedTitle: string             — Most-recently-added title

  Triggerables: (none)

  Events (1):
    - TodoAdded(args: TodoAddedEventArgs { Title: string, AddedAt: date-time })
        watch URI: marionette://TodoListViewModel/events/TodoAdded
        — A new TODO was added to the list (queue=100, coalesce=100ms)
```

For watchable observables, append the resource URI on a separate line so the user sees the subscribe target:

```
    - TotalCount  watch URI: marionette://TodoListViewModel/TotalCount
```

For each event, render the args type as a single-line schema summary using the `argsSchema.properties` field. Include the `resourceUri` on a separate line. Highlight the throttling values when they're non-default (`MinIntervalMs > 0` or non-100ms `CoalesceWindowMs`). Suggest subscribing when an event looks like a meaningful domain transition the user would want to react to.

### 3. If the user passed `--screenshot` (or said "and take a screenshot"), call `capture_screenshot`

The tool returns either:

- An MCP image content block (`type: "image"`, `mimeType: "image/png"`, base64 in `data`). Embed it inline in your report.
- A structured `screenshot_not_supported` error. This means the app is running headless (NoOpAdapter); explain that the user needs to start the app with `--mcp` (no `--headless`) for screenshots to work, or use a framework adapter that supports screenshot.

### 4. Suggest concrete next steps

End the report with 2-4 bullet-pointed suggestions tailored to what you saw. Use exact tool calls:

- "Try calling a method: `invoke_method` with `root: 'TodoListViewModel', method: 'AddTodo', args: { title: 'Buy milk' }`"
- "Watch a count: subscribe to `marionette://TodoListViewModel/TotalCount` (you'll get pushed updates when the list changes)"
- "Watch an event: subscribe to `marionette://TodoListViewModel/events/TodoAdded` and react when the args show a new title"
- "Take a screenshot: `capture_screenshot()` — captures the current MainWindow"
- "Filter the manifest: `inspect_app_api(rootName: 'TodoListViewModel')` to scope to one root"

### 5. Don't over-explain

Skip the "Marionette is a library that..." preamble. The user has the server connected — they know what they have. Lead with the manifest summary; the suggestions go at the end.

## Format hints

- Render tool names as `code` to make them copy-pasteable.
- Group observables by watchable / non-watchable when there are more than 5.
- For roots with `instanceAvailable: false`, render the entry struck-through and explain `createError`.
- Don't truncate the description text — it's what makes the manifest useful.

## Things NOT to do

- Don't infer methods that aren't in the manifest. If `inspect_app_api` returned 5 callables, list 5; don't add a sixth from imagination.
- Don't call `invoke_method` to "test" things proactively — that's the `marionette-test` skill's job.
- Don't suggest framework-specific tool calls (e.g. "click the AddButton") unless `triggerables` is non-empty for that root. Phase 1 only ships `Strategy.Semantic`; Phase 3 adds input simulation.
- Don't paste raw JSON in the report unless the user asks for it. Format the manifest into bullet lists.

## Compatible apps

Phase 2.1 validated this skill against both **WPF** and **Avalonia** Marionette adopters. The same MCP tool surface (`inspect_app_api`, `invoke_method`, `read_observable`, `capture_screenshot`) works against both frameworks unchanged - the adapter is invisible at the protocol level.

## Reference

For attribute semantics and full descriptor schema, see `prompts/attributes-reference.md` in the skill-pack root. The four MCP tools the runtime exposes (`inspect_app_api`, `invoke_method`, `read_observable`, `capture_screenshot`) are documented there alongside the Ai channel push.
