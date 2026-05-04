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
   - No runtime reflection is required to discover roots or invoke callables.

3. `Marionette.NET.Runtime`
   - Framework-neutral MCP host and tool implementation.
   - Owns `inspect_app_api`, `invoke_method`, `read_observable`, `capture_screenshot`, `simulate_input`, and `raise_event`.
   - Owns loop protection, dynamic per-method tools, event resources, watchable observable resources, and stdio isolation.

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
