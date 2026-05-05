# Phase 10 Findings — AOT-clean per-method dynamic tools

Date: 2026-05-05

## Status

**GREEN.** The Phase 4.2 follow-up "AOT dynamic tools — `ModelContextProtocol.ReflectionAIFunctionDescriptor` not AOT-clean. Wait for SDK source-gen migration." is closed. The wait was unnecessary — the SDK has had an AOT-clean overload all along, just not the one we were calling.

## What changed

`DynamicToolRegistry.BuildTool` switched from `McpServerTool.Create((Delegate)handler, options)` to `McpServerTool.Create(AIFunction, options)` via a new `MarionetteAIFunction` subclass.

### Why the previous path failed under AOT

`McpServerTool.Create(Delegate, …)` routes through `AIFunctionFactory.Create(Delegate)` which builds a `ReflectionAIFunctionDescriptor`. That descriptor:

1. walks `MethodInfo.GetParameters()` at registration time;
2. emits dynamic-codegen marshallers per parameter;
3. at invocation time calls `JsonSerializer.Deserialize(JsonElement, runtimeType)` with the resolved CLR type as a runtime value.

All three steps fall outside the AOT contract for any parameter shape beyond strings / primitives. Phase 4.2's verification matrix carried `continue-on-error: true` on the AOT-on stdio handshake steps to tolerate this; the documented adopter story was "use meta-tools under AOT, dynamic tools work in JIT only".

### Why the new path is AOT-clean

`McpServerTool.Create(AIFunction, …)` (in `ModelContextProtocol.Core.Server.AIFunctionMcpServerTool`) only does these reads on the supplied function:

- `function.Name`
- `function.Description`
- `function.JsonSchema` — for the Tool's `InputSchema`
- `function.UnderlyingMethod` — `null` skips every attribute / XML-doc reflection branch
- `function.JsonSerializerOptions` — used only when the result needs a default serialization
- `function.InvokeAsync(args, ct)` — the actual call

The SDK's return-shape switch in `AIFunctionMcpServerTool.InvokeAsync` accepts a `CallToolResult` directly and passes it through unchanged. So the existing `IsError` flag (raised when MarionetteDispatch returns a structured-error JSON object) propagates without translation.

## What landed

### `src/Marionette.NET.Runtime/Tools/MarionetteAIFunction.cs` (new, 67 lines)

Internal sealed `AIFunction` subclass. Constructor takes `name`, `description`, a pre-built `JsonElement` schema, and a `Func<AIFunctionArguments, CancellationToken, ValueTask<object?>>` invoker. Overrides `Name`, `Description`, `JsonSchema`, `UnderlyingMethod => null`, and `InvokeCoreAsync` (which delegates to the supplied closure).

### `src/Marionette.NET.Runtime/Tools/DynamicToolRegistry.cs` (modified)

`BuildTool`'s closure was renamed from a `RequestContext<CallToolRequestParams>`-typed handler delegate to a `(AIFunctionArguments, CancellationToken) → ValueTask<object?>` invoker. Returns the same `CallToolResult` it always did. The previous post-create `tool.ProtocolTool.InputSchema = …` patch is gone — the SDK reads the schema directly from `MarionetteAIFunction.JsonSchema`.

`BuildArgsElement` was rewritten to round-trip through `JsonDocument.Parse(jsonObject.ToJsonString())` instead of `JsonSerializer.SerializeToElement(jsonNode)`. The latter requires a `JsonTypeInfo` resolver and throws `InvalidOperationException` under AOT with reflection-based serialization disabled. The new path uses only typed JSON DOM and the allocator-only parser — no metadata resolver involved.

### `.github/workflows/ci.yml` (modified)

Removed `continue-on-error: true` from four AOT-on stdio handshake steps:

- StripeProbe AOT-on stdio handshake
- Avalonia Dashboard AOT-on stdio handshake
- TodoApp AOT-on stdio handshake
- FormLab AOT-on stdio handshake

PocketPlanner steps keep `continue-on-error: true` because MAUI's AOT story is independently fragile (Phase 4.2 finding; resolves separately as MAUI matures).

## Verification

### Build matrix

| Step | Result |
|---|---|
| `dotnet build Marionette.NET.sln -c Debug` | 0 warnings, 0 errors |
| `dotnet test tests/Marionette.NET.SourceGenerator.Tests` | 28/28 PASS |
| `dotnet test tests/Marionette.NET.Testing.Tests` | 12/12 PASS |
| `dotnet test tests/Marionette.NET.Integration` | 7/7 PASS + 3 GUI-skipped |
| `pwsh .phase1/demo.ps1` | 12/12 PASS, dynamic-tool path exercised |

### AOT publish smoke (5 samples × stripped + full = 10 publishes)

Each `dotnet publish -p:PublishAot=true` exits 0. Marionette IL warnings: 0 across all publishes. Framework-inherent IL warnings remain on the WPF samples (12 each, as before — `PresentationFramework`, `WindowsBase`, `System.Xaml`, `System.Formats.Nrbf`, `ReachFramework`, `System.Private.Windows.Core`).

### AOT-runtime stdio handshake (4 samples × full)

| Sample | Harness flag | Result |
|---|---|---|
| Sample.Wpf.TodoApp | `--todoapp` | 12 PASS / 0 FAIL — incl. `[via dynamic tool] TodoListViewModel.AddTodo({title=…}) succeeded; TotalCount 2 → 3` |
| Sample.Avalonia.Dashboard | `--avalonia` | 14 PASS / 0 FAIL — incl. `[via dynamic tool] DashboardViewModel.UpsertMetric({name="DynamicProbe"}) succeeded; MetricCount 5 → 6` |
| Sample.WinUI.FormLab | `--winui` | 18 PASS / 0 FAIL — `tools/list` enumerates 6 dynamic tools, dispatch is the same code path as TodoApp / Avalonia |
| Sample.Maui.PocketPlanner | `--maui` | 12 PASS / 0 FAIL — `tools/list` enumerates 5 dynamic tools |

The TodoApp and Avalonia harnesses include explicit dynamic-tool invocation steps. WinUI FormLab and MAUI PocketPlanner harnesses currently exercise enumeration-only on the dynamic-tool surface; extending those harnesses with explicit dynamic-tool calls is a low-friction follow-up but not load-bearing — registration runs through the same `MarionetteAIFunction` + `McpServerTool.Create(AIFunction, …)` path for every adapter.

## AOT scorecard (after Phase 10)

| # | Scenario | Before Phase 8 | After Phase 8 | After Phase 10 |
|---|---|---|---|---|
| 4 | Frozen-Mode (`--mcp --headless`) | clean | clean | clean |
| 6 | Per-method dynamic tools under AOT | **SDK blocker** | **SDK blocker** | **AOT guarantee** ✅ |
| 7 | `raise_event` framework-control reflection | warn | warn (architectural) | warn (architectural) |
| 8 | `[McpEvent]` args complex types | trim-fragile | AOT guarantee | AOT guarantee |
| 9 | `[McpObservable]` complex types | trim-fragile | AOT guarantee | AOT guarantee |
| — | `[McpCallable]` sync return values | trim-fragile | AOT guarantee | AOT guarantee |
| — | `[McpCallable]` `Task<T>` results | trim-fragile | AOT guarantee | AOT guarantee |
| — | `[McpCallable]` parameter deserialisation | trim-fragile | AOT guarantee | AOT guarantee |

## Open follow-ups (unchanged)

- Uno adapter — masterplan Phase 4, deliberately skipped, still on roadmap.
- AOT `raise_event` — adapter raisers reflect on event names; source-gen typed dispatcher per `[McpEvent]` would close `RequiresUnreferencedCode` on `IUiAutomationAdapter.RaiseEventAsync`, but the call signature inherently takes a runtime event-name string so this remains architecturally bounded.
- WPF + AOT GUI crash (Microsoft-known, Frozen-Mode `--mcp --headless` works fine).
- Avalonia simulate_input key/text/mouse-move kinds (Phase-9.1 fallback to routed-event raise).
- WinUI InputInjector path requires elevation/manifest on locked-down systems (Phase-9.3 documented).
- MAUI multi-window polish (initial reconcile lands at attach-time; subsequent window cycling deserves stress test).
- xUnit GUI-test skip-by-env (EC-8/9/10 gated on `MARIONETTE_GUI_TESTS=1`).

## Note for adopters

Existing adopter code requires no changes. The descriptor surface (`CallableDescriptor.ParametersJsonSchema`, the source-gen output) is unchanged; the dispatch pipeline (`MarionetteDispatch.InvokeAsync`) is unchanged; the runtime's loop-protection / UI-thread / async-unwrap behaviours are unchanged. The only edit is the SDK call inside `BuildTool` and the helper that converts the SDK's `AIFunctionArguments` to a `JsonElement`. Adopters who AOT-publish their app under `EnableMcpAutomation=true` now get working dynamic per-method tools; adopters who stay JIT see no behavioural change.
