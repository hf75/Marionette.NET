# Testing Toolkit

`Marionette.NET.Testing` is the in-process harness for testing Marionette surfaces without launching a stdio MCP server or a real LLM client.

## Neutral Host

```csharp
using Marionette.Testing;

var host = MarionetteTestHost.Create(Marionette.Generated.GeneratedManifest.Roots);
host.BindInstance("TodoListViewModel", viewModel);

await host.InvokeMethodAsync(
    root: "TodoListViewModel",
    method: "AddTodo",
    args: new { title = "Write tests" });

var total = await host.ReadObservableAsync<int>(
    root: "TodoListViewModel",
    property: "TotalCount");
```

The host calls the same `MarionetteTools` implementation used by production MCP mode. Raw methods return the exact JSON shape the MCP client would receive. Typed methods throw `MarionetteToolException` when the runtime returns a structured Marionette error.

## xUnit

Add `Marionette.NET.Testing.Xunit` on top of the neutral package:

```csharp
using Marionette.Testing.Xunit;

public sealed class TodoTests
{
    [Fact]
    public async Task AddTodo_UpdatesCount()
    {
        var host = MarionetteXunit.CreateHost(GeneratedManifest.Roots);
        host.BindInstance("TodoListViewModel", new TodoListViewModel());

        await host.InvokeMethodAsync("TodoListViewModel", "AddTodo", new { title = "xunit" });

        Assert.Equal(1, await host.ReadObservableAsync<int>("TodoListViewModel", "TotalCount"));
    }

    [MarionetteGuiFact]
    public async Task ClickPath_UsesTheAdapter()
    {
        // Runs only when MARIONETTE_GUI_TESTS=1.
    }
}
```

## NUnit

Add `Marionette.NET.Testing.NUnit`:

```csharp
using Marionette.Testing.NUnit;

[TestFixture]
public sealed class TodoTests
{
    [Test]
    public async Task AddTodo_UpdatesCount()
    {
        var host = MarionetteNUnit.CreateHost(GeneratedManifest.Roots);
        host.BindInstance("TodoListViewModel", new TodoListViewModel());

        await host.InvokeMethodAsync("TodoListViewModel", "AddTodo", new { title = "nunit" });

        Assert.That(await host.ReadObservableAsync<int>("TodoListViewModel", "TotalCount"), Is.EqualTo(1));
    }

    [Test]
    public void GuiCase()
    {
        MarionetteNUnit.RequireGuiTestingEnabled();
        // Runs only when MARIONETTE_GUI_TESTS=1.
    }
}
```

## What To Test

Prefer small tests that verify the exposed contract:

- `inspect_app_api` contains the roots and descriptions you expect.
- Each non-trivial `[McpCallable]` accepts JSON-shaped arguments.
- `[McpObservable]` values change after the callable that should affect them.
- Structured errors are stable for unknown roots, unknown methods, and loop-limit cases.
- GUI adapter tests stay behind `MARIONETTE_GUI_TESTS=1`.

Use the runtime's integration test project for stdio/client behavior. Use `Marionette.NET.Testing` for fast contract tests around the generated manifest and runtime dispatcher.
