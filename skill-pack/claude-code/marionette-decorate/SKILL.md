---
name: marionette-decorate
description: Add Marionette.NET MCP-control attributes to an existing .NET desktop application's source code. Use this skill whenever the user asks to "make this Marionette-controllable", "add MCP attributes", "decorate this for Claude", "expose this app to Claude", "wire up Marionette in my app", or otherwise wants their existing C# WPF / Avalonia / WinUI / MAUI / Uno project to become driveable from an LLM. The skill identifies likely root classes, suggests [McpCallable] / [McpObservable] / [McpTriggerable] / [McpEvent] placements, edits the source files, and verifies the project still builds.
---

# Marionette: Decorate an Existing App

The user has an existing C# desktop app and wants to make it MCP-controllable. Your job: read the project, identify the right places to put attributes, edit the source, and verify the build. **Be conservative.** A single misplaced `[McpCallable]` on a method with side-effects you don't understand is worse than missing one obvious-looking decoration.

## Trigger conditions

Use this skill when the user says any of:

- "make this Marionette-controllable", "add MCP attributes", "decorate this for Claude"
- "expose this app to Claude", "wire up Marionette", "instrument this app"
- "add Marionette to my [WPF/Avalonia/WinUI/MAUI/Uno] app"
- After they've installed the `Marionette.NET` NuGet (Phase 7) or referenced the abstraction project locally and ask "now what?"

## Procedure

### 1. Read the project structure

Before editing anything, build a mental model:

- Find every `*.csproj`. Is it `<UseWPF>true</UseWPF>`, an Avalonia SDK, a WinUI SDK, a MAUI SDK?
- Find every class. ViewModels (often suffix `ViewModel`, sometimes `*Vm`). Code-behind classes (`*.xaml.cs` partials). Service classes (`*Service`, `*Manager`).
- Find every `public` method on those classes.
- Note whether the project already references `Marionette.NET.Abstractions` (or the meta-package `Marionette.NET`). If not, the user needs to add it before the attributes will resolve. The Abstractions package is multi-targeted (`netstandard2.0;net10.0`) so it works for any modern .NET app.

### 2. Identify candidate root classes

The right `[McpRoot]` candidate is **a class with public methods that perform meaningful state changes the user might want to drive from an LLM**. Strong candidates:

- **ViewModels** — virtually always the right answer in MVVM apps. The MainWindow's DataContext-bound ViewModel is the canonical Phase 1 root.
- **Service classes** — when the app routes user actions through an `IXxxService` and you want the LLM to call those services directly.
- **Code-behind classes** (`MainWindow.xaml.cs`, `Page1.xaml.cs`) — only when the app is anti-MVVM and code-behind is where the action verbs live. Less ideal because windows are framework-specific.

**Weak / wrong candidates:**

- Static utility classes (no `this`; the runtime needs an instance).
- Generic types (`MyClass<T>` — Phase 1 generator emits MAR001 errors).
- Static / sealed-helper classes you'd never want to hand to a user (the `[McpRoot]` is the LLM's entry point — make sure it represents user-meaningful actions).
- Background-service hosts whose `public` methods are framework callbacks (`OnStartup`, `OnDeactivated`).

When in doubt, ask the user: "I see a `TodoViewModel` and a `MainWindow` — should I decorate the ViewModel as the [McpRoot]?"

### 3. Suggest `[McpCallable]` placements

A method should get `[McpCallable]` when ALL these are true:

- It's `public` (the source generator rejects non-public callables — diagnostic MAR002).
- It's a verb the user might want to drive: "Add", "Remove", "Save", "Refresh", "Toggle", "Submit", "Compute".
- Its parameters serialize to JSON cleanly (primitives, strings, simple records).
- It's idempotent or its side-effect is clearly named.

Avoid `[McpCallable]` on:

- **Event handlers** (`OnButtonClick`, `*_Click`). The handler is wired by XAML; if you want LLM control of the action, move the body into a public method on the ViewModel and decorate that method.
- **Property getters/setters disguised as methods** (`GetCount()`, `SetVisible(bool)`). Use `[McpObservable]` on a property instead.
- **Async methods returning non-awaitable types** (e.g. raw `void` async). The runtime can await `Task` / `Task<T>` / `ValueTask` / `ValueTask<T>` but not `async void`.
- **Constructors / finalizers / explicit interface implementations.**
- **Methods that take `Stream`, delegates, pointers, `IntPtr` / `nint`** — the source generator rejects these (MAR004) because they don't serialize.

For each suggestion, provide a one-sentence description (this becomes the LLM-facing tool description). Infer it from the method name when possible:

```csharp
[McpCallable("Add a new todo with the given title.")]
public void AddTodo(string title) { ... }
```

If the method has a long-running pattern, set `OffUiThread = true` (runs on the threadpool) and `TimeoutSeconds = 60` (or a value matching the user's tolerance):

```csharp
[McpCallable("Reload data from the API.", OffUiThread = true, TimeoutSeconds = 60)]
public Task<int> RefreshFromApi() { ... }
```

### 4. Suggest `[McpObservable]` placements, especially derived ones

Properties get `[McpObservable]` when:

- They expose **UI state the LLM should be able to read**: counts, statuses, the current selection, summary aggregates.
- They have a public getter (the generator rejects observables with no getter — MAR005 error — or non-public getter — MAR006 warning).

**Strongly prefer derived / computed properties** (`Count`, `Sum`, `Status`, `IsValid`) over raw fields. They give the LLM a high-signal view without leaking internal storage.

For watchable push updates, set `Watchable = true`:

```csharp
[McpObservable("Total number of todos.", Watchable = true)]
public int TotalCount => _items.Count;
```

The runtime exposes `Watchable = true` properties as MCP resources at `marionette://<root>/<property>`. For push updates to work in real-time (rather than 500 ms polling fallback), the declaring class **must implement `INotifyPropertyChanged`**. If you decorate a derived property (`PendingCount = TotalCount - CompletedCount`), be sure the class fires `PropertyChanged` for ALL the derived names whenever the underlying state changes — this is the standard MVVM pattern. **If the class doesn't implement INPC yet, add it as part of the same edit** — the watchable promise depends on it.

Avoid `[McpObservable]` on:

- **Properties that throw** (e.g. lazy-init that may fail). The runtime reads the value on every observe; a thrown getter surfaces as a structured error, not a useful signal.
- **Properties on a disposed-or-soon-to-be-disposed object.**
- **Properties with side-effects** (the getter mutates state). The runtime may read the property speculatively (initial baseline + change-detection re-reads); side effects in a getter become visible churn.
- **High-frequency properties** (FPS counters, mouse position, animation progress). Watchable observables aren't designed for >10 Hz updates; the 200 ms coalesce window will still flood the channel.

### 5a. Suggest `[McpEvent]` placements (Phase 1.6)

Walk every `public event` on each candidate root. Suggest `[McpEvent]` when ALL these are true:

- The delegate is `EventHandler` or `EventHandler<TArgs>` (other shapes produce error MAR010).
- The event represents a **meaningful domain transition** the LLM should react to: "form submitted", "model changed", "validation failed", "long-running task completed", "user logged in".
- Firing rate is bounded (or you set `MinIntervalMs` to throttle it).

```csharp
[McpEvent("A new TODO was added to the list.")]
public event EventHandler<TodoAddedEventArgs>? TodoAdded;

[McpEvent("Validation completed.", MinIntervalMs = 100)]
public event EventHandler<ValidationCompletedEventArgs>? ValidationCompleted;
```

For args types, prefer **sealed classes inheriting from `EventArgs`** with read-only properties of primitives + nested records:

```csharp
public sealed class TodoAddedEventArgs : EventArgs
{
    public TodoAddedEventArgs(string title, DateTime addedAt)
    { Title = title; AddedAt = addedAt; }
    public string Title { get; }
    public DateTime AddedAt { get; }
}
```

Records cannot inherit from `EventArgs` (CS8864 — "records can only inherit from object or another record"). Use a plain sealed class.

**Throttling examples:**
- Mouse-move at 50ms min interval (~20 fps): `MinIntervalMs = 50`.
- Bursty file-watcher events, want one notification/sec: `CoalesceWindowMs = 1000`.
- Backpressure-bounded log streams: `MaxQueueSize = 50`.

**Don't decorate:**
- `Disposed` / `Closed` / framework lifecycle events.
- Events on third-party objects you don't own (the runtime can't detach reliably on shutdown).
- Very-high-frequency events without `MinIntervalMs` (the buffer fills before the LLM reads).
- Generic events whose args are heavy object graphs — the schema generator falls back to `{"description": "complex type"}`.

### 5. Suggest `[McpTriggerable]` placements

Triggerables are properties that EXPOSE a UI control (`Button`, `ButtonBase`, anything with a public `Click` event). Use them when the canonical user interaction with that control is the click, e.g.:

```csharp
[McpTriggerable("Reloads the dashboard.")]
public Button RefreshButton => _refreshButton;
```

Phase 1's WPF adapter implements `Strategy.Semantic` only (the runtime resolves the control and dispatches its `Click` semantically). `Strategy.EventSystem` and `Strategy.InputSystem` ship in Phase 3.

If you find a code-behind property like `public Button AddButton { get; }` that's the obvious user-facing action, decorate it. Otherwise prefer a `[McpCallable]` on the underlying ViewModel method.

**Don't** add `[McpTriggerable]` to controls whose interaction is more than a click (TextBox typing, ComboBox selection, DataGrid editing). Those need richer input simulation that doesn't ship until Phase 3.

### 6. Edit the source files

Apply the suggested attributes. Always include the `using Marionette;` namespace import at the top of the file (or `using Marionette.NET;` does NOT work — the namespace is `Marionette`).

For each candidate root class, ALSO add `[McpRoot]` to the class itself. Without it, the source generator emits MAR003 warnings on every callable inside ("class lacks [McpRoot]") and the methods are invisible to the LLM.

```csharp
using Marionette;

[McpRoot]                                        // <-- this is required
public sealed class TodoViewModel : INotifyPropertyChanged
{
    [McpCallable("Add a new todo.")]
    public void AddTodo(string title) { ... }

    [McpObservable("Number of todos.", Watchable = true)]
    public int Count => _items.Count;
}
```

If the ViewModel doesn't implement `INotifyPropertyChanged` yet and you're adding `Watchable = true` observables, implement it now. Standard pattern:

```csharp
public event PropertyChangedEventHandler? PropertyChanged;

private void OnPropertyChanged([CallerMemberName] string? name = null) =>
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
```

Then call `OnPropertyChanged(nameof(Count))` from every method that mutates the underlying state.

### 7. Verify the project still builds

Run `dotnet build` against the project. The source generator emits diagnostics with the `MAR001`–`MAR012` prefix:

| ID | Severity | Meaning | Common fix |
|---|---|---|---|
| MAR001 | Error | `[McpRoot]` on static / generic class / non-class | Move attribute to a regular instance class |
| MAR002 | Error | `[McpCallable]` on non-public method | Make the method `public` or remove the attribute |
| MAR003 | Warning | `[McpCallable]` method's class lacks `[McpRoot]` | Add `[McpRoot]` to the class |
| MAR004 | Error | `[McpCallable]` parameter type is blacklisted | Replace `Stream`/`delegate`/pointer with primitives |
| MAR005 | Error | `[McpObservable]` property has setter but no getter | Add a getter or remove the attribute |
| MAR006 | Warning | `[McpObservable]` property/getter is non-public | Make property + getter `public` |
| MAR007 | Error | `[McpTriggerable]` not a Button/ButtonBase and exposes no public `Click` event | Pick a different control type or remove |
| MAR008 | Info | `[McpRoot]` class declares no MCP entrypoints | Add at least one `[McpCallable]`/`[McpObservable]`/`[McpTriggerable]`/`[McpEvent]` (or remove `[McpRoot]`) |
| MAR009 | Error | `[McpEvent]` on a non-event member | Move attribute to a C# event declaration |
| MAR010 | Error | `[McpEvent]` event delegate is not `EventHandler` or `EventHandler<T>` | Change delegate type or remove attribute |
| MAR011 | Warning | `[McpEvent]` event's class lacks `[McpRoot]` | Add `[McpRoot]` to the class |
| MAR012 | Warning | `[McpEvent]` throttling parameter out of range | Use `MaxQueueSize > 0` and `CoalesceWindowMs >= 0` |

Surface every diagnostic to the user. Don't silently strip attributes to make the build green.

### 8. Wire the host (one-line in App.OnStartup for WPF, App.OnFrameworkInitializationCompleted for Avalonia, or App.OnLaunched for WinUI)

The attributes alone don't start the MCP server — the host has to be wired. Detect the framework from the user's csproj first:

- `<UseWPF>true</UseWPF>` -> WPF -> use `MarionetteWpf.AttachTo`.
- `<PackageReference Include="Avalonia"` -> Avalonia -> use `MarionetteAvalonia.AttachTo`.
- `<UseWinUI>true</UseWinUI>` OR `<PackageReference Include="Microsoft.WindowsAppSDK"` -> WinUI 3 -> use `MarionetteWinUI.AttachTo`.
- MAUI / Uno -> not in Phase 3.2 yet.

Show the user the canonical wiring snippet for their framework:

**WPF:**

```csharp
// In App.xaml.cs
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
#if MCP_ENABLED
    Marionette.Adapter.Wpf.MarionetteWpf.AttachTo(
        this,
        Marionette.Generated.GeneratedManifest.Roots,
        e.Args);
#endif
}
```

**Avalonia:**

```csharp
// In App.axaml.cs
public override void OnFrameworkInitializationCompleted()
{
    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.MainWindow = new MainWindow();
    }
    base.OnFrameworkInitializationCompleted();
#if MCP_ENABLED
    Marionette.Adapter.Avalonia.MarionetteAvalonia.AttachTo(
        this,
        Marionette.Generated.GeneratedManifest.Roots);
#endif
}
```

**WinUI 3:**

```csharp
// In App.xaml.cs
private Window? _mainWindow;

protected override void OnLaunched(LaunchActivatedEventArgs args)
{
    _mainWindow = new MainWindow();
    _mainWindow.Activate();

#if MCP_ENABLED
    var argv = Environment.GetCommandLineArgs();
    var argsExceptExe = argv.Length > 1 ? argv[1..] : Array.Empty<string>();
    Marionette.Adapter.WinUI.MarionetteWinUI.AttachTo(
        this,
        _mainWindow,
        Marionette.Generated.GeneratedManifest.Roots,
        argsExceptExe);
#endif
}
```

For non-Window roots (custom ViewModels), see `samples/Sample.Wpf.TodoApp/App.xaml.cs` (WPF), `samples/Sample.Avalonia.Dashboard/App.axaml.cs` (Avalonia), or `samples/Sample.WinUI.FormLab/App.xaml.cs` (WinUI) in the Marionette repo for the descriptor-factory rewrite pattern that wires the runtime's instance to the same singleton your DataContext uses.

Also need:
- A `<StartupObject>` that handles `--mcp` / `--mcp --headless` flags (see `samples/Sample.Wpf.TodoApp/Program.cs` (WPF), `samples/Sample.Avalonia.Dashboard/Program.cs` (Avalonia), or `samples/Sample.WinUI.FormLab/Program.cs` (WinUI) as templates).
- WPF: `<EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>` so the SDK doesn't auto-emit a competing `Main`. Avalonia uses `<OutputType>Exe</OutputType>` (NOT WinExe) and a custom `Main` that wires `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)`. WinUI uses `<DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>` to suppress the XAML compiler's auto-emitted `Program.Main`.
- TFM: WPF requires `net10.0-windows`. Avalonia adopters should use `net10.0` (NOT `net10.0-windows`) because Avalonia is cross-platform. WinUI 3 requires `net10.0-windows10.0.<sdk>.0` (e.g. `net10.0-windows10.0.19041.0` for Windows App SDK 1.8.x) plus `<UseWinUI>true</UseWinUI>` and `<WindowsPackageType>None</WindowsPackageType>` for unpackaged deployment.
- The `EnableMcpAutomation` MSBuild property (defaults to Debug=on, Release=off via `build/Marionette.NET.props`).
- WinUI's `simulate_input` may need `inputInjectionBrokered` capability or elevation for full kind coverage; click variants work unpackaged + unelevated via the `ButtonAutomationPeer.Invoke` path.

### 9. Encourage running the test skill

Once the build is green, suggest the user run the `marionette-test` skill against their app to smoke-test the decoration. That skill calls every newly-added `[McpCallable]` with sensible defaults and verifies the observables react.

## Things NOT to decorate (collected, with reasons)

- **Event handlers** (`OnXxxClick`, `OnLoaded`). They take framework-specific args; the runtime can't supply them.
- **Methods that throw on certain inputs** without surfacing a structured error. The LLM gets the unhandled exception as a generic `invocation_failed` and can't recover.
- **Methods that block synchronously on long I/O.** Make them `async Task<T>` and set `OffUiThread = true` instead.
- **Properties whose getter mutates state** (lazy init that flips a flag, computes-and-caches with side-effects).
- **High-frequency tick state** (mouse position, FPS, render-loop counters). Watchable observables coalesce to one push per 200 ms but the polling fallback runs unconditionally; use a derived "current state" rollup instead.
- **Properties that surface secrets** (API keys, OAuth tokens, password fields). The LLM-facing manifest is plain text; observables will leak it.
- **Methods on a disposed object.** Check disposal-state inside the callable and return early; don't trust the LLM to remember not to call you.

## Reference

Full attribute spec, channel API (`Ai.Trigger`), runtime tool surface, and edge-case examples: see `prompts/attributes-reference.md` in the skill-pack root.
