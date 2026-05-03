# Marionette.NET — Attributes & Runtime Reference

The canonical reference for the Marionette.NET v1 surface. Every skill in `skill-pack/claude-code/*/SKILL.md` cites this file. Adopters using a non-Claude agent (Cursor, Cline, Aider) can read this directly.

**Status:** Phase 1 (WPF). The attribute set is locked; subsequent phases add framework adapters and one new runtime tool surface (input simulation in Phase 3) without changing the core contract.

---

## Namespace conventions

| Symbol | Namespace |
|---|---|
| `[McpRoot]`, `[McpCallable]`, `[McpObservable]`, `[McpTriggerable]`, `TriggerStrategy` | `Marionette` |
| `Ai.Trigger`, `Ai.ScheduleTrigger`, `Ai.IsActive` | `Marionette` |
| `MarionetteWpf.AttachTo` (WPF adapter bootstrap) | `Marionette.Adapter.Wpf` |
| `MarionetteHost.RunAsync` (headless host entry) | `Marionette.Runtime` |
| `RootDescriptor`, `CallableDescriptor`, `ObservableDescriptor`, `TriggerableDescriptor`, `ParamDescriptor` | `Marionette.Runtime.Manifest` |
| `GeneratedManifest.Roots` (source-generator output) | `Marionette.Generated` |

Always `using Marionette;` in user code. The other namespaces are only relevant to the wiring snippet in `App.OnStartup` (or equivalent).

---

## The four attributes

### `[McpRoot]` — class

Marks a class as a Marionette manifest root. The source generator scans only `[McpRoot]` types and their members; it never reflects on the whole AppDomain.

```csharp
[McpRoot]                            // manifest name = "TodoListViewModel" (type name)
public sealed class TodoListViewModel { ... }

[McpRoot("settings")]                // manifest name = "settings" (explicit override)
public sealed class AppSettings { ... }
```

**Constraints (source-generator-enforced):**

- Class only. Static, generic, and non-class types produce error MAR001.
- The class needs at least one `[McpCallable]` / `[McpObservable]` / `[McpTriggerable]`, otherwise the generator emits info MAR008 and registers a placeholder root that does nothing.
- The runtime calls the class's parameterless ctor at startup unless the host is given a pre-bound instance. If there is no public parameterless ctor, the descriptor's factory is `null` and the runtime surfaces `root_unavailable` until an adapter binds an instance.

**Naming guidance:**

- Lowercase, ASCII, short. `Name` is used verbatim in tool ids, resource URIs, and LLM-facing manifest listings.
- If you have multiple roots, prefix them or use distinct words (`InventoryVm`, `OrdersVm`, not `MainVm` and `OtherVm`).

---

### `[McpCallable]` — method

Marks a method as callable through the `invoke_method` MCP tool. The source generator emits a typed dispatcher per method; reflection is never used at runtime.

```csharp
[McpCallable("Adds a new TODO with the given title.")]
public void AddTodo(string title) { ... }

[McpCallable("Reloads the dashboard from the API.",
             OffUiThread = true,           // run on threadpool, not UI thread
             TimeoutSeconds = 60)]         // cancel after 60 s
public Task<int> RefreshAsync() { ... }
```

**Properties:**

| Property | Type | Default | Meaning |
|---|---|---|---|
| `Description` (ctor) | `string` | required | LLM-facing tool description |
| `OffUiThread` | `bool` | `false` | When `true`, runtime invokes off the UI dispatcher |
| `TimeoutSeconds` | `int` | `0` | Per-invocation timeout; `0` = no timeout |

**Constraints:**

- Method must be `public` (non-public produces error MAR002).
- Containing class must have `[McpRoot]` (warning MAR003 otherwise).
- Parameter types must serialize as JSON. Forbidden types (error MAR004): `Stream`, delegate, pointer, `IntPtr`, `nint`.
- Return type can be anything (sync void, sync `T`, `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`). The runtime awaits async returns.

**Best practices:**

- One sentence per description, declarative voice.
- Use `OffUiThread = true` for I/O-bound methods that don't touch UI controls.
- Use `TimeoutSeconds` whenever the method might hang (network, file lock, etc.).
- For methods that change UI state, leave `OffUiThread = false` (default) so the WPF adapter dispatches them onto the UI thread.

---

### `[McpObservable]` — property

Marks a property as readable state for the LLM via the `read_observable` MCP tool. With `Watchable = true`, the property additionally becomes an MCP resource that supports `resources/subscribe` for push updates.

```csharp
[McpObservable("Total number of todos.", Watchable = true)]
public int TotalCount => _items.Count;

[McpObservable("Most recently added title or null.")]
public string? LastAddedTitle => _items.LastOrDefault()?.Title;

[McpObservable("Currently selected category.",
               Watchable = true,
               PollingIntervalMs = 1000)]    // polling fallback when no INPC
public string CurrentCategory { get; private set; }
```

**Properties:**

| Property | Type | Default | Meaning |
|---|---|---|---|
| `Description` (ctor) | `string` | required | LLM-facing description |
| `Watchable` | `bool` | `false` | Expose as `marionette://<root>/<property>` resource |
| `PollingIntervalMs` | `int` | `500` | Polling fallback (when class doesn't implement INPC) |

**Constraints:**

- Must have a public getter (error MAR005 if get-only-with-non-public-getter; MAR006 warning if getter is non-public).
- Property type must serialize as JSON. Primitives, strings, simple records, and arrays of those are safe; complex object graphs may produce STJ cycle errors at read time.

**Watchable update mechanism:**

1. Subscriber calls `resources/subscribe` with the URI.
2. Runtime checks: does the declaring class implement `INotifyPropertyChanged`?
   - **Yes** -> hook `PropertyChanged`; pushes happen synchronously off `OnPropertyChanged(nameof(TotalCount))`.
   - **No** -> start a `Timer` ticking at `PollingIntervalMs`; reads the value each tick.
3. Updates within a 200 ms coalesce window collapse to one `notifications/resources/updated` message.
4. The notification carries only the URI; the subscriber then issues `resources/read` to fetch the new value.

**For real-time push (recommended), implement INPC on the [McpRoot] class:**

```csharp
public sealed class TodoListViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [McpObservable("Total count.", Watchable = true)]
    public int TotalCount => _items.Count;

    public void AddTodo(string title)
    {
        _items.Add(new TodoItem(title));
        OnPropertyChanged(nameof(TotalCount));   // <-- fires push
    }
}
```

For derived properties (`PendingCount = TotalCount - CompletedCount`), fire `PropertyChanged` for **all** affected names whenever the underlying state changes.

**Don't:**

- Decorate getters that throw (the LLM gets `read_failed`; useless signal).
- Decorate properties with side-effects in the getter (the runtime may read speculatively for change-detection).
- Decorate high-frequency state (FPS, mouse position). The 200 ms coalesce can't keep up with > 10 Hz updates.

---

### `[McpTriggerable]` — property

Marks a property whose value is a UI control (`Button` / `ButtonBase` in WPF, equivalent in other frameworks) as a triggerable surface point. The LLM can fire its primary user interaction.

```csharp
[McpTriggerable("Reloads the dashboard.")]
public Button RefreshButton => _refreshButton;

[McpTriggerable("Submits the form.", Strategy = TriggerStrategy.InputSystem)]
public Button SubmitButton => _submitButton;
```

**Properties:**

| Property | Type | Default | Meaning |
|---|---|---|---|
| `Description` (ctor) | `string` | required | LLM-facing description |
| `Strategy` | `TriggerStrategy` | `Semantic` | How the runtime fires the trigger |

**`TriggerStrategy` values:**

- `Semantic` (Phase 1 default) -- invoke the control's primary command/click handler directly. Bypasses routed events and the input pipeline. Fastest.
- `EventSystem` (Phase 3) -- raise the framework's routed event (`Button.Click`); bubbling/tunneling preserved. Use when handlers inspect `e.OriginalSource`.
- `InputSystem` (Phase 3) -- pump synthetic input through the OS-level pipeline. Highest fidelity, slowest. Required for end-to-end test automation.

**Constraints:**

- Property type must be `Button`, `ButtonBase`, or any type with a public `Click` event (error MAR007 otherwise).
- Phase 1 only implements `Strategy.Semantic` for `Button`/`ButtonBase`; Phase 3 expands.

**Don't:**

- Decorate non-button controls (TextBox, ComboBox). Their interactions need richer input simulation (Phase 3+).
- Decorate buttons whose click handlers perform unsafe destructive actions without confirmation -- the LLM may fire them speculatively.

---

## The `Ai` channel API

```csharp
namespace Marionette;

public static class Ai
{
    [Conditional("MCP_ENABLED")]                              // <-- elided when MCP off
    public static void Trigger(string prompt) { ... }

    [Conditional("MCP_ENABLED")]
    public static void ScheduleTrigger(TimeSpan after, string prompt) { ... }

    public static bool IsActive { get; }                      // <-- always present
}
```

**`Ai.Trigger("prompt")`** sends a `notifications/marionette/channel` message to the connected MCP client (Claude). The payload is `{ prompt, hops }`. Use it to push state changes to the LLM in real-time:

```csharp
public void AddTodo(string title)
{
    _items.Add(new TodoItem(title));
    Ai.Trigger($"User added a TODO: '{title}'. List now has {_items.Count} items.");
}
```

**`Ai.ScheduleTrigger(TimeSpan, "prompt")`** fires the same notification after a delay (one-shot Timer). Useful for "remind Claude about this in 30 seconds" patterns.

**`Ai.IsActive`** returns `true` when the runtime is loaded and a server session is bound. Use this to gate non-conditional code paths:

```csharp
if (Ai.IsActive)
{
    // We can rely on the channel; do something proactive.
}
```

**Stripping behaviour:**

- `Trigger` and `ScheduleTrigger` are `[Conditional("MCP_ENABLED")]` -- in stripped Release builds (`EnableMcpAutomation=false`) every call site is elided by the compiler.
- `IsActive` is NOT conditional. In stripped builds it returns `false` always.
- This is what powers the "zero-cost in production" promise: a deployed Release build has no Marionette code paths and no MCP_ENABLED define.

**Loop protection:**

The runtime caps `invoke_method -> Ai.Trigger -> invoke_method` chains at 5 hops by default. Override via the `MARIONETTE_MAX_DEPTH` environment variable. When the limit is exceeded, `invoke_method` returns:

```json
{ "success": false, "errorCode": "loop_limit_exceeded", "hops": 6 }
```

The counter has a 30-second decay window; idle conversations don't accumulate.

---

## The four runtime MCP tools

### `inspect_app_api(rootName?)`

Returns the manifest. Without `rootName`, returns an array of every root entry. With `rootName`, returns just that root's shape (or a structured `unknown_root` error with the available names).

Each root entry:

```json
{
  "name": "TodoListViewModel",
  "typeName": "Sample.Wpf.TodoApp.TodoListViewModel",
  "instanceAvailable": true,
  "createError": null,
  "callables": [
    {
      "name": "AddTodo",
      "description": "Add a new TODO with the given title.",
      "offUiThread": false,
      "timeoutSeconds": 0,
      "isAsync": false,
      "parameters": [
        { "name": "title", "clrType": "string", "required": true }
      ]
    }
  ],
  "observables": [
    {
      "name": "TotalCount",
      "description": "Total number of todos.",
      "watchable": true,
      "pollingIntervalMs": 500,
      "clrType": "int",
      "resourceUri": "marionette://TodoListViewModel/TotalCount"
    }
  ],
  "triggerables": []
}
```

### `invoke_method(root, method, args?)`

Invokes a `[McpCallable]`. Marshalling, UI-thread dispatch, optional timeout, and `Task` awaiting are all handled internally. Returns the method's return value (boxed as JSON) or a structured error:

| `errorCode` | When |
|---|---|
| `loop_limit_exceeded` | Hop counter > `MARIONETTE_MAX_DEPTH` |
| `unknown_root` | No root with that name |
| `root_unavailable` | Root has no live instance (factory threw, or no factory wired) |
| `unknown_method` | Root has no method with that name |
| `argument_marshalling_failed` | JSON args couldn't be coerced to CLR types |
| `cancelled` | Host shutdown cancelled the call |
| `timeout` | Method exceeded `TimeoutSeconds` |
| `invocation_failed` | Method threw; `message` carries the exception text |

### `read_observable(root, property)`

Reads the current value of a `[McpObservable]`. Dispatches to the UI thread via the framework adapter, JSON-serialises the value, returns the text. Errors:

| `errorCode` | When |
|---|---|
| `unknown_root` | No root with that name |
| `root_unavailable` | No live instance |
| `unknown_observable` | No observable with that name |
| `read_failed` | Getter threw; `message` carries the exception text |

### `capture_screenshot(target?)`

Captures a screenshot of the application (full window when `target` is null; named control otherwise). Returns an MCP `image` content block (PNG, base64 in `data`). Errors:

| `errorCode` | When |
|---|---|
| `screenshot_not_supported` | NoOpAdapter (headless mode) |
| `screenshot_failed` | Adapter threw; `message` carries the exception text |

---

## Common mistakes (collected, with fixes)

### Don't put `[McpCallable]` on event handlers

Wrong:

```csharp
[McpCallable("Click handler.")]
private void AddButton_Click(object sender, RoutedEventArgs e) { ... }
```

Right: move the body to a public method on the ViewModel, decorate THAT, and call it from the click handler:

```csharp
[McpCallable("Add a todo.")]
public void AddTodo(string title) { ... }

private void AddButton_Click(object sender, RoutedEventArgs e) =>
    _viewModel.AddTodo(NewTodoTextBox.Text);
```

### Don't use `[McpObservable]` on volatile / high-frequency properties

Wrong:

```csharp
[McpObservable("Mouse position.", Watchable = true)]
public Point MousePosition { get; private set; }   // updated 60 Hz
```

Right: aggregate the volatile state into a low-frequency derived signal:

```csharp
[McpObservable("Whether the user is interacting.", Watchable = true)]
public bool IsInteracting => _idleTimer.LastInteractionAge < TimeSpan.FromSeconds(3);
```

### Don't forget `[McpRoot]` on the class

Wrong:

```csharp
public sealed class TodoListViewModel       // <-- no [McpRoot]
{
    [McpCallable("Add.")]
    public void AddTodo(string title) { ... }
}
```

The generator emits warning MAR003 and the method is invisible to the LLM.

Right:

```csharp
[McpRoot]
public sealed class TodoListViewModel
{
    [McpCallable("Add.")]
    public void AddTodo(string title) { ... }
}
```

### Don't use the wrong namespace

Wrong: `using Marionette.NET;` (does not exist).

Right: `using Marionette;` -- Phase 1 attribute namespace.

### Don't decorate static / generic classes

Wrong:

```csharp
[McpRoot]                        // MAR001 error
public static class HelperVm { ... }

[McpRoot]                        // MAR001 error
public sealed class GenericVm<T> { ... }
```

Right: instance-typed, non-generic class. If you need parameterised behaviour, use a non-generic root that takes the type discriminator as a callable arg.

### Don't decorate methods with non-serializable params

Wrong:

```csharp
[McpCallable("Process a stream.")]                  // MAR004 error
public void Process(Stream input) { ... }
```

Right: use a primitive-friendly signature; if the LLM needs to push bytes, base64-encode at the call boundary:

```csharp
[McpCallable("Process a base64-encoded payload.")]
public void Process(string base64Payload) { ... }
```

### Don't forget INPC for watchable observables

Wrong:

```csharp
public sealed class CounterVm
{
    [McpObservable("Counter.", Watchable = true)]   // works but polls
    public int Counter { get; private set; }
}
```

The runtime falls back to polling at `PollingIntervalMs` (default 500 ms). The push is timer-driven; the LLM sees a stale value for up to 500 ms after a change.

Right:

```csharp
public sealed class CounterVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [McpObservable("Counter.", Watchable = true)]
    public int Counter
    {
        get => _counter;
        set { _counter = value; OnPropertyChanged(); }
    }
    private int _counter;
}
```

Now updates are push-driven, sub-millisecond.

### Don't write to stdout from inside a `[McpCallable]`

The MCP host owns stdout for JSON-RPC frames. `Console.WriteLine` from inside a callable corrupts the wire protocol. Use `ILogger` injected via the runtime's host (logs go to stderr) or `System.Diagnostics.Trace.WriteLine` (goes to OutputDebugString, not stdout). The runtime's `StdoutGuardWriter` flags violations on stderr but the damage is already done by the time it sees the bytes.

---

## Wiring snippets

### WPF — App.OnStartup

```csharp
using System.Windows;

#if MCP_ENABLED
using Marionette.Adapter.Wpf;
using Marionette.Generated;
#endif

namespace MyApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if MCP_ENABLED
        MarionetteWpf.AttachTo(this, GeneratedManifest.Roots, e.Args);
#endif
    }
}
```

### WPF — Program.Main (custom STAThread entry)

Required when you want `--mcp --headless` mode (no Application). Set `<EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>` and `<StartupObject>MyApp.Program</StartupObject>` in the csproj. See `samples/Sample.Wpf.TodoApp/Program.cs` in the Marionette repo for the canonical template.

### Non-Window root binding (WPF)

When the `[McpRoot]` is a custom ViewModel (not a `Window`), the source-generator factory emits `() => new MyViewModel()` -- which creates a SECOND instance, separate from the one your DataContext binds. Rewrite the descriptor's factory before passing it to AttachTo:

```csharp
var roots = GeneratedManifest.Roots
    .Select(r => r.TypeName == typeof(MyViewModel).FullName
        ? r with { Create = static () => MyViewModel.Shared }
        : r)
    .ToList();

MarionetteWpf.AttachTo(this, roots, e.Args);
```

`MyViewModel.Shared` is a static singleton (or DI-resolved instance) that your MainWindow's DataContext also points to. See `samples/Sample.Wpf.TodoApp/App.xaml.cs` for the working reference.

---

## Where to file bugs / ask questions

The Marionette.NET repo. The skill-pack ships with the library and is versioned alongside it; if a skill's instructions diverge from the runtime's actual behaviour, the runtime is the source of truth and the skill is the bug.
