# Marionette.NET Architecture

Marionette.NET is split into leaf packages so production builds can strip the MCP runtime completely while Debug builds get an in-process MCP automation surface.

## Layers

1. `Marionette.NET.Abstractions`
   - Attribute-only package: `[McpRoot]`, `[McpCallable]`, `[McpObservable]`, `[McpTriggerable]`, `[McpEvent]`.
   - `Ai.Trigger` and `Ai.ScheduleTrigger` are guarded with `Conditional("MCP_ENABLED")`.
   - This package is the only one intended to remain in stripped production builds.

2. `Marionette.NET.SourceGenerator`
   - Roslyn incremental generator that scans the attributes and emits `Marionette.Generated.GeneratedManifest`.
   - Emits strongly typed descriptors, parameter JSON schemas, event schemas, and MAR diagnostics.
   - **AOT JSON source-gen (Phase 8 / 8.5 / 11):** also emits two `JsonSerializerContext`-derived classes (`MarionetteEventArgsJsonContext`, `MarionetteJsonContext`) populated via `JsonMetadataServices` factories at compile time. Every `[McpEvent]` args type, `[McpObservable]` value type, and `[McpCallable]` return / parameter type whose graph is source-gen-eligible gets a typed `JsonTypeInfo<T>` so runtime serialisation never reflects on user types. Eligible shapes: primitives, `Nullable<T>`, enums, plain user records / classes with public-getter properties, `T[]`, `List<T>`, and (Phase 8.5 + 11) every standard `IEnumerable` / `IList` / `IReadOnlyList` / `ICollection` / `IReadOnlyCollection` / `ISet` / `IReadOnlySet` / `HashSet` / `Stack` / `Queue` plus `Dictionary` / `IDictionary` / `IReadOnlyDictionary` across STJ-supported key types. Phase 11 also adds an interface-fallback walker that picks up any user / concurrent type implementing one of the supported interfaces (e.g. `ConcurrentDictionary<K,V>` registers via `IDictionary<K,V>`).
   - No runtime reflection is required to discover roots or invoke callables.

3. `Marionette.NET.Runtime`
   - Framework-neutral MCP host and tool implementation.
   - Owns `inspect_app_api`, `invoke_method`, `read_observable`, `capture_screenshot`, `simulate_input`, and `raise_event`. The first five live on `MarionetteTools`; `raise_event` lives on its own `MarionetteRaiseEventTools` class so adopters who do not use it can opt out of registration entirely (Phase 11).
   - Owns loop protection, dynamic per-method tools, event resources, watchable observable resources, and stdio isolation.
   - **AOT-clean dynamic per-method tools (Phase 10):** the `DynamicToolRegistry` registers tools via the SDK's reflection-free `McpServerTool.Create(AIFunction, …)` overload using an internal `MarionetteAIFunction : Microsoft.Extensions.AI.AIFunction` subclass. The subclass supplies `Name` / `Description` / `JsonSchema` / `UnderlyingMethod => null` / `InvokeCoreAsync` directly — no `MethodInfo` walking, no dynamic codegen, no reflection on user types at registration or invocation time.
   - **Two host entry points (Phase 11):**
     - `MarionetteHost.RunAsync` — full surface (six tools incl. `raise_event`), carries `[RequiresUnreferencedCode]` / `[RequiresDynamicCode]` because the `raise_event` path resolves event names by reflection on framework control types.
     - `MarionetteHost.RunAsyncSourceGenSafe` — five-tool surface (no `raise_event`), annotation-free. Adopters who confirm they neither call `raise_event` nor let unsupported JSON shapes reach the runtime path get a clean compile under AOT.

4. Adapter packages
   - `Marionette.NET.Adapter.Wpf`
   - `Marionette.NET.Adapter.Avalonia`
   - `Marionette.NET.Adapter.WinUI`
   - `Marionette.NET.Adapter.Maui`
   - Each adapter implements `IUiAutomationAdapter` for dispatcher marshalling, screenshot capture, control resolution, input simulation, event raising, and multi-window instance tracking.

5. `Marionette.NET.Testing`
   - In-process test harness over the same runtime tools.
   - Lets tests call generated manifests directly without launching a stdio MCP process or a real Claude client.
   - Thin `Marionette.NET.Testing.Xunit` and `Marionette.NET.Testing.NUnit` helpers layer framework-specific skip/convenience APIs on top.

## Runtime Flow

```
User code with attributes
        |
        v
Source generator emits RootDescriptor[]
        |
        v
Adapter AttachTo(...) binds live app instances
        |
        v
ManifestRegistry + IUiAutomationAdapter
        |
        v
MarionetteTools / dynamic MCP tools
        |
        v
MCP client, in-process tests, or skill-pack workflows
```

The same `RootDescriptor` graph drives production MCP calls, dynamic per-method tools, and the in-process test harness. That is the key contract: tests should exercise the runtime path users actually ship, not a duplicate fake dispatcher.

## Stdio Contract

When an app runs in `--mcp` mode, stdout belongs to JSON-RPC frames only. Marionette installs a stdout guard before the host comes up and routes diagnostics to stderr/logging. Any user logging that reaches stdout is a protocol bug because it can corrupt MCP framing.

## Window And Instance Routing

The registry tracks descriptors by root name. Adapters can also supply live root instances by `(rootName, windowId)`:

- Single-window apps usually bind the generated descriptor to the main window or its singleton ViewModel.
- Multi-window apps register each instance through the adapter tracker.
- Dynamic per-method tools gain `:<windowId>` suffixes only when more than one window is available for the same root.

## AOT contract

The runtime ships two reflection surfaces:

1. **`raise_event`** resolves event names against framework control type chains by reflection. Architectural — no source-gen workaround. Hosted on `MarionetteRaiseEventTools` so it can be excluded by entry-point choice.
2. **Legacy JSON fallback** for type graphs the source generator does NOT cover (multi-dimensional arrays, abstract bases, tuple-keyed dictionaries, types lacking a public parameterless ctor). Source-gen-eligible payloads bypass this path entirely.

Adopters' AOT contract:

| Path | Result under AOT |
|---|---|
| `RunAsync` + source-gen-eligible payloads + `raise_event` unused | works, but compile shows `[RequiresUnreferencedCode]` warning at the host call site (suppress or accept) |
| `RunAsyncSourceGenSafe` + source-gen-eligible payloads + `raise_event` unused | **annotation-free compile**, fully reflection-free runtime |
| Either entry point + non-source-gen-eligible payload type | runtime `InvalidOperationException` from `JsonSerializer` (reflection-disabled) at the actual offending call |
| `RunAsync` + `raise_event` invoked by an MCP client | works; trim may strip metadata for custom controls |

Phase 10's `MarionetteAIFunction` rewrite verified end-to-end via the StdioTest harness: 4/4 AOT-published samples (TodoApp / Avalonia / WinUI FormLab / MAUI PocketPlanner) pass `tools/list` enumeration AND explicit dynamic-tool invocation under AOT.

## Error Shape

Tool errors are structured JSON, not exceptions leaking to the MCP client:

```json
{
  "success": false,
  "errorCode": "method_not_found",
  "message": "Root 'TodoListViewModel' has no callable 'ArchiveTodo'."
}
```

The testing package preserves that raw shape for low-level tests and throws `MarionetteToolException` only from typed convenience helpers.
