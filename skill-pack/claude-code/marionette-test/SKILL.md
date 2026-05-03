---
name: marionette-test
description: Smoke-test a running Marionette.NET-instrumented .NET app by calling every [McpCallable] with sensible synthetic inputs and verifying the [McpObservable] properties react meaningfully. Use this skill whenever the user asks to "test this app", "verify the app works", "smoke-test the Marionette app", "run a sanity check", "make sure the decoration is right", or whenever the user wants confidence the manifest holds together end-to-end. The skill discovers the surface, generates plausible test invocations, asserts observable responses, captures a final screenshot, and reports a structured PASS / FAIL summary.
---

# Marionette: Smoke-Test an Instrumented App

You have a Marionette.NET MCP server connected (transport: stdio) and the user wants confidence that the app's decorated surface actually works. **Test generation is heuristic** — you'll pick plausible inputs from method-name semantics, not a property-test framework. Acknowledge that in the report. The goal is "no decoration is broken", not "this app is bug-free".

## Trigger conditions

Use this skill when the user says any of:

- "test this app", "verify the app works", "smoke-test", "sanity check"
- "run my Marionette decoration", "make sure the manifest holds together"
- "did I wire it up right?", "does it actually work end-to-end?"
- After running `marionette-decorate` — the natural next step.

## Procedure

Run every step in order. Track PASS/FAIL counts per step; the final report is one row per step.

### 1. Discover the manifest

Call `inspect_app_api()` with no arguments. Extract every root, every callable, every observable.

If the manifest is empty (no roots), report "no [McpRoot] classes found — did you add `[McpRoot]` to your ViewModel and rebuild?" and stop. Don't proceed.

If a root has `instanceAvailable: false`, report the `createError` and skip that root's callables (they'd all fail with `root_unavailable`).

### 2. Snapshot the observables (before)

For each root, call `read_observable` for each observable. Record `{root, name, valueBefore, type}`.

If any observable read returns a structured error (`{success:false, errorCode, message}`), record it as a "broken-getter" failure but keep going. The other observables likely still work.

### 3. Generate test invocations

For each callable, generate ONE plausible invocation. Inference rules (try them in order):

| Method name pattern | Synthesized args |
|---|---|
| `Add*(string …)` | `{ <param>: "test1" }` |
| `Remove*(int index)` / `Delete*(int index)` | `{ index: 0 }` (only if at least one observable suggests the list is non-empty; else skip) |
| `Toggle*(int index)` | `{ index: 0 }` (same caveat) |
| `Rename*(int index, string …)` | `{ index: 0, newTitle: "renamed" }` |
| `Clear*()` | `{}` |
| `Refresh*()` / `Reload*()` | `{}` |
| `Set*(bool)` | `{ <param>: true }` |
| `Set*(int)` | `{ <param>: 1 }` |
| `Compute*(int a, int b)` / `Add(int, int)` | `{ a: 2, b: 3 }` |
| Anything else with primitive params | Use the safest defaults: `0` for ints, `""` for strings, `false` for bools |
| Anything with non-primitive params | **Skip with reason** "complex parameter type — heuristic test generation declined" |

Order callables so list-growth verbs run before list-mutation verbs: `Add*` first, then `Toggle*`/`Rename*`, then `Remove*`/`Clear*`. This way the index-based methods have something to operate on.

### 4. Run each test invocation

For each generated `invoke_method` call:

1. Issue the call.
2. Record success / structured error.
3. If success, immediately re-read every observable that **could plausibly have changed** (heuristic: any observable whose name or description shares a substring with the called method's name; e.g. `AddTodo` likely affects `TotalCount`, `LastAddedTitle`).
4. Compare `valueAfter` to `valueBefore`. Record one of:
   - **PASS — observable reacted meaningfully** (value changed)
   - **NOTE — observable unchanged** (often correct, e.g. `CompletedCount` after `AddTodo`)
   - **FAIL — observable returned a structured error during re-read**

Do NOT treat "observable unchanged" as a hard failure — many methods are no-ops on bad indices, and many observables are correctly orthogonal to a given method. Record as NOTE.

### 5. Test loop protection (optional, only if `Ai.Trigger` is in scope)

Marionette's loop protection caps `invoke_method → Ai.Trigger → invoke_method` chains at 5 hops by default (configurable via `MARIONETTE_MAX_DEPTH`). To verify the guard fires:

- Call any callable at least 6 times in quick succession.
- Look for a structured `loop_limit_exceeded` error after the 5th hop.
- Record **PASS** if the guard fired, **NOTE — guard not exercised** if the test environment doesn't surface channel hops (most app sandboxes don't). Don't fail the suite on this.

### 6. Capture a final screenshot

Call `capture_screenshot()`. Three outcomes:

- **PNG image returned** — embed inline in the report with a "final state" caption.
- **`screenshot_not_supported`** — note that the app is in headless mode; suggest re-running with the GUI mode (`--mcp` without `--headless`) to verify visual changes.
- **Other error** — surface the message; flag as FAIL if the app's adapter is supposed to support screenshots (any non-NoOpAdapter).

### 7. Print the structured report

Format:

```
=== Marionette smoke test ===
App: <inspect_app_api summary — N roots, M callables, K observables>

[PASS] Manifest discovery: N roots, M callables, K observables
[PASS] Observable baseline: K/K reads succeeded
       (or [FAIL] 2 observables failed to read; see details below)

Per-callable results:
  [PASS] TodoListViewModel.AddTodo("test1") → succeeded
         TotalCount: 0 → 1     (PASS — observable reacted)
         LastAddedTitle: null → "test1"  (PASS)
  [PASS] TodoListViewModel.ToggleDone(index: 0) → succeeded
         CompletedCount: 0 → 1 (PASS)
         PendingCount: 1 → 0   (PASS)
  [SKIP] ServiceVm.UploadStream(stream: Stream) → skipped (complex parameter type)

Final screenshot: <inline image / screenshot_not_supported / error>

Summary: 5 PASS / 1 SKIP / 0 FAIL  →  smoke test PASSED
```

Always print the summary line. The user reads it first; the per-step detail is for forensics.

### 8. Be honest about heuristics

End the report with one bullet point acknowledging the limitation:

> **Note:** test generation is heuristic, not exhaustive. A passing smoke test means decoration is wired and the obvious paths work; it does NOT guarantee correctness for arbitrary input or that all edge cases are handled. For property-based testing, write xUnit/NUnit tests against the ViewModel directly (Phase 6's `Marionette.NET.Testing` package will support in-process MCP simulation for this).

## Things NOT to do

- **Don't test `[McpCallable]` methods that take stream / delegate / pointer params** — they'd fail at marshalling and the failure isn't a regression of the decoration. Skip with the "complex parameter type" reason.
- **Don't do destructive state changes without warning the user.** If an `[McpCallable]` looks like it `Save*`/`Send*`/`Submit*` something irreversible, skip it and record "skipped — potentially destructive; run manually if you want it tested".
- **Don't generate a test for every observable independently.** Observables are tested transitively through the callables that mutate them.
- **Don't assume timing.** After an `invoke_method` returns, the observable values may not have propagated through `INotifyPropertyChanged` if the user wired the events asynchronously. Wait ~200 ms (matching the resource-update coalesce window) before re-reading observables if a re-read returns the old value.
- **Don't run the suite against an app you didn't see decorated.** The user pointed Claude at an arbitrary EXE; honor their intent rather than asking for source-tree access. The MCP manifest is enough.

## Compatible apps

Phase 2.1 validated this skill against both **WPF** and **Avalonia** Marionette adopters. The MCP tool surface is framework-agnostic; the same heuristic test generation works for both.

## Reference

For the runtime's tool semantics, error codes, and the Ai channel push spec:
see `prompts/attributes-reference.md` in the skill-pack root.
