---
name: marionette-automate-flow
description: Automate a concrete user flow in a running Marionette.NET-instrumented .NET app by combining inspect_app_api, invoke_method, read_observable, simulate_input, raise_event, screenshots, and event/watchable resources. Use this skill when the user asks "/automate-this-flow", "automate this flow", "drive this workflow", "perform this task in the app", or gives a multi-step UI/app scenario to execute through Marionette.
---

# Marionette: Automate A Concrete Flow

You have a Marionette.NET MCP server connected and the user gave you a specific workflow to perform. Your job is to translate that workflow into the smallest reliable sequence of Marionette tool calls, verify state after each meaningful step, and report the final outcome.

## Trigger conditions

Use this skill when the user says any of:

- `/automate-this-flow`
- "automate this flow", "drive this workflow", "perform this task in the app"
- "open the app and do X", when a Marionette MCP server is already connected
- "use Marionette to create/update/remove/submit ..." followed by concrete app steps

## Procedure

### 1. Discover the control surface

Call `inspect_app_api()` first. Identify:

- the root that owns the workflow,
- the callables that perform durable state changes,
- observables that can verify each step,
- triggerables or input targets only when the user explicitly wants UI-path coverage,
- events or watchable resources that indicate completion.

If the manifest is empty or the relevant root has `instanceAvailable:false`, stop and report the structured failure. Do not invent tool calls.

### 2. Prefer semantic calls for business intent

Use `invoke_method` for the workflow's domain operations unless the user's goal is specifically to test real input fidelity.

Examples:

- "Add a todo named Buy milk" -> `invoke_method(root:"TodoListViewModel", method:"AddTodo", args:{ title:"Buy milk" })`
- "Clear completed items" -> `invoke_method(..., method:"ClearCompleted", args:{})`
- "Submit the form" -> `invoke_method(..., method:"Submit", args:{})`

Use `simulate_input` when the user asks to click/type through the UI or when the workflow depends on control-specific behavior. Use `raise_event` only for event-system validation or when the framework adapter documents it as the appropriate path.

### 3. Verify after each meaningful step

After every state-changing call:

1. Read the relevant observable(s) with `read_observable`.
2. Compare the returned value to the expected workflow state.
3. If a watchable resource or event exists for the transition, mention it and optionally subscribe when the user asked for ongoing automation.

If the result is ambiguous, read the broader manifest state before deciding the step failed.

### 4. Use screenshots as evidence, not as the source of truth

Capture screenshots:

- before a UI-path sequence,
- after the final step,
- after any step where the visual state is the only clear verification.

If `capture_screenshot` returns `screenshot_not_supported`, continue with semantic verification and note the limitation.

### 5. Handle structured errors directly

Marionette tool errors have `{ success:false, errorCode, message }`. Surface the code and message. Common recovery:

- `root_not_found`: call `inspect_app_api()` again and choose an existing root.
- `method_not_found`: use the manifest's exact method name.
- `root_unavailable`: report the `createError` / app wiring issue.
- `loop_limit_exceeded`: stop the recursive plan; do not keep retrying.
- `control_not_found`: report the missing automation id/name and fall back to a semantic callable if one exists.

### 6. Final report

Keep the report concrete:

```
Flow: Add todo and mark it done

[PASS] AddTodo(title:"Buy milk")
       TotalCount: 2 -> 3
       LastAddedTitle: "Buy milk"
[PASS] ToggleDone(index:2)
       CompletedCount: 0 -> 1

Final state: 3 todos, 1 completed.
Screenshot: attached / not supported / failed with <errorCode>.
```

## Things not to do

- Do not perform destructive workflow steps that the user did not ask for.
- Do not retry a failing tool call with guessed root/method names; re-read the manifest.
- Do not use screenshots alone to infer data that an observable exposes.
- Do not mutate app state just to "see what happens" unless that mutation is part of the requested flow.

## Reference

For full tool semantics, descriptor fields, and attributes, see `prompts/attributes-reference.md`.
