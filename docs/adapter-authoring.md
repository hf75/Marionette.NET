# Adapter Authoring Guide

An adapter is the only framework-specific part of Marionette.NET. It implements `IUiAutomationAdapter` and keeps the runtime ignorant of WPF, Avalonia, WinUI, MAUI, or future frameworks.

## Required Contract

Implement these behaviors with the framework's public surface where possible:

- `DispatchAsync(Action)` and `DispatchAsync<T>(Func<T>)`
  - Marshal work to the UI thread when the framework requires it.
  - Execute inline only when already on the correct thread and that is safe for the framework.
  - Honor cancellation before queueing and while waiting for queued work.

- `CaptureScreenshotAsync(targetName, windowId, ct)`
  - Return PNG bytes.
  - Prefer target-bounded capture when the framework supports it.
  - Return a structured unsupported result through runtime tools when capture is impossible; do not throw for normal headless mode.

- `ResolveControlAsync(rootName, controlName, windowId, ct)`
  - Search by automation id first, then framework name.
  - Keep the lookup deterministic. The first match should not depend on hash-map ordering.
  - Avoid retaining visual-tree nodes longer than necessary.

- `SimulateInputAsync(...)`
  - Use the real input or automation pipeline for the framework.
  - Semantic fallbacks are acceptable only when the framework has no public event-args/input injection path; document the limitation in code and docs.
  - Return `false` for unsupported input kinds instead of pretending the input happened.

- `RaiseEventAsync(...)`
  - Prefer public framework APIs.
  - Reflection is trim-fragile. If reflection is unavoidable, isolate it and add `RequiresUnreferencedCode` justification.

- `GetWindowIds(rootName)` and `GetRootInstance(rootName, windowId)`
  - Back dynamic multi-window tool registration.
  - Return stable ids for the lifetime of a window.

## AttachTo Shape

Every adapter should expose one canonical attach entry point:

```csharp
public static IDisposable AttachTo(
    TApplication app,
    IReadOnlyList<RootDescriptor> roots,
    string[]? args = null,
    ILoggerFactory? loggerFactory = null)
```

Adapters with a separate main-window object can accept it explicitly. The method should:

1. Build the adapter-specific `IUiAutomationAdapter`.
2. Rewrite descriptor factories when the live UI instance should replace a generated `new T()` factory.
3. Start `MarionetteHost.RunAsync(...)` only when command-line args request MCP mode.
4. Return an `IDisposable` that cancels the host task and detaches instance tracking.

## Root Instance Rules

The runtime must invoke the same object the user sees on screen. For MVVM apps, this normally means binding the generated root descriptor to the singleton ViewModel used as `DataContext` or `BindingContext`.

Do not let the generated parameterless factory silently create a second ViewModel when the UI is already bound to another instance. That produces tests that pass while the visible app never changes.

## AOT And Trimming

Adapter packages are leaf packages and are allowed to reference UI frameworks, but they must not pull adapter dependencies into stripped builds. Keep these rules:

- Public adapter APIs that use trim-fragile reflection need explicit annotations and justifications.
- Prefer framework automation peers, dispatcher APIs, and public visual-tree APIs over private reflection.
- Never require runtime reflection to discover `[McpRoot]` members. That is the source generator's job.
- Verify full and stripped publish paths when adding a new adapter capability.

## Testing Checklist

For every adapter change, cover:

- `inspect_app_api` can see roots.
- `invoke_method` mutates the live UI-bound instance.
- `read_observable` sees the mutation.
- `capture_screenshot` either returns PNG bytes or a documented unsupported error.
- `simulate_input(kind: "click")` reaches the same handler path a user click would.
- `raise_event` returns either a real event result or a documented limitation.
- Multi-window roots produce stable `windowIds`.
