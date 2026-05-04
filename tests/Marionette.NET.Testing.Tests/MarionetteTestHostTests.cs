using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Marionette.Runtime.Loop;
using Marionette.Runtime.Manifest;

using Xunit;

namespace Marionette.Testing.Tests;

public sealed class MarionetteTestHostTests
{
    [Fact]
    public void InspectAppApi_ReturnsManifestJson()
    {
        var host = CreateHost();

        var manifest = host.InspectAppApi();

        Assert.Contains("TodoRoot", manifest, StringComparison.Ordinal);
        Assert.Contains("AddTodo", manifest, StringComparison.Ordinal);
        Assert.Contains("TotalCount", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeMethodAsync_MutatesRoot_AndReadObservableSeesChange()
    {
        var host = CreateHost();

        await host.InvokeMethodAsync("TodoRoot", "AddTodo", new { title = "buy milk" });

        Assert.Equal(1, await host.ReadObservableAsync<int>("TodoRoot", "TotalCount"));
        Assert.Equal("buy milk", await host.ReadObservableAsync<string>("TodoRoot", "LastAddedTitle"));
    }

    [Fact]
    public async Task InvokeMethodAsync_Generic_ReturnsTypedValue()
    {
        var host = CreateHost();

        var result = await host.InvokeMethodAsync<int>("TodoRoot", "Add", new { a = 2, b = 3 });

        Assert.Equal(5, result);
    }

    [Fact]
    public async Task InvokeMethodAsync_UsesDefaultParameterValues()
    {
        var host = CreateHost();

        var result = await host.InvokeMethodAsync<string>("TodoRoot", "Decorate", new { title = "done" });

        Assert.Equal("done!", result);
    }

    [Fact]
    public async Task InvokeMethodAsync_PreservesAnonymousObjectPropertyCasing()
    {
        var host = CreateHost();

        var result = await host.InvokeMethodAsync<string>("TodoRoot", "Greet", new { UserName = "Ada" });

        Assert.Equal("hello Ada", result);
    }

    [Fact]
    public async Task InvokeMethodAsync_HandlesGeneratedStyleEnumAndArrayArguments()
    {
        var host = CreateHost();
        using var doc = JsonDocument.Parse("""{"level":"High","tags":["ui","smoke"]}""");

        var result = await host.InvokeMethodAsync<string>("TodoRoot", "Tag", doc.RootElement.Clone());

        Assert.Equal("High:ui,smoke", result);
    }

    [Fact]
    public async Task InvokeMethodAsync_AwaitsGeneratorStyleAsyncResults()
    {
        var host = CreateHost();
        await host.InvokeMethodAsync("TodoRoot", "AddTodo", new { title = "a" });

        var result = await host.InvokeMethodAsync<int>("TodoRoot", "CountPlusAsync", new { delta = 4 });

        Assert.Equal(5, result);
    }

    [Fact]
    public async Task TypedHelpers_ThrowOnStructuredErrors()
    {
        var host = CreateHost();

        var ex = await Assert.ThrowsAsync<MarionetteToolException>(
            () => host.InvokeMethodAsync("TodoRoot", "Missing"));

        Assert.Equal("unknown_method", ex.ErrorCode);
        Assert.Contains("Missing", ex.RawJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoopProtection_CanBeConfiguredAndReset()
    {
        var host = CreateHost(new MarionetteTestHostOptions
        {
            LoopProtection = new LoopProtectionService(1, TimeSpan.FromMinutes(1)),
        });

        await host.InvokeMethodAsync("TodoRoot", "AddTodo", new { title = "first" });
        var ex = await Assert.ThrowsAsync<MarionetteToolException>(
            () => host.InvokeMethodAsync("TodoRoot", "AddTodo", new { title = "second" }));
        Assert.Equal("loop_limit_exceeded", ex.ErrorCode);

        host.ResetLoopProtection();
        await host.InvokeMethodAsync("TodoRoot", "AddTodo", new { title = "second" });
        Assert.Equal(2, await host.ReadObservableAsync<int>("TodoRoot", "TotalCount"));
    }

    [Fact]
    public async Task RawHelpers_ReturnRuntimeJsonWithoutThrowing()
    {
        var host = CreateHost();

        var raw = await host.InvokeMethodRawAsync("TodoRoot", "Missing");

        Assert.True(MarionetteAssert.TryGetError(raw, out var error));
        Assert.Equal("unknown_method", error.ErrorCode);
    }

    private static MarionetteTestHost CreateHost(MarionetteTestHostOptions? options = null)
    {
        var root = new TodoRoot();
        return MarionetteTestHost.Create(new[] { DescriptorFor(root) }, options);
    }

    private static RootDescriptor DescriptorFor(TodoRoot root)
    {
        return new RootDescriptor(
            Name: "TodoRoot",
            TypeName: typeof(TodoRoot).FullName ?? nameof(TodoRoot),
            Create: () => root,
            Callables: new[]
            {
                new CallableDescriptor(
                    Name: "AddTodo",
                    Description: "Add a todo.",
                    OffUiThread: false,
                    TimeoutSeconds: 0,
                    IsAsync: false,
                    Parameters: new[] { new ParamDescriptor("title", "string", IsRequired: true, DefaultValue: null) },
                    ParametersJsonSchema: "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"}},\"required\":[\"title\"]}",
                    Invoke: static (instance, args) =>
                    {
                        ((TodoRoot)instance).AddTodo((string)args["title"]!);
                        return null;
                    }),
                new CallableDescriptor(
                    Name: "Add",
                    Description: "Add two integers.",
                    OffUiThread: false,
                    TimeoutSeconds: 0,
                    IsAsync: false,
                    Parameters: new[]
                    {
                        new ParamDescriptor("a", "int", IsRequired: true, DefaultValue: null),
                        new ParamDescriptor("b", "int", IsRequired: true, DefaultValue: null),
                    },
                    ParametersJsonSchema: "{\"type\":\"object\"}",
                    Invoke: static (instance, args) => ((TodoRoot)instance).Add((int)args["a"]!, (int)args["b"]!)),
                new CallableDescriptor(
                    Name: "Decorate",
                    Description: "Decorate a title with a suffix.",
                    OffUiThread: false,
                    TimeoutSeconds: 0,
                    IsAsync: false,
                    Parameters: new[]
                    {
                        new ParamDescriptor("title", "string", IsRequired: true, DefaultValue: null),
                        new ParamDescriptor("suffix", "string", IsRequired: false, DefaultValue: "!"),
                    },
                    ParametersJsonSchema: "{\"type\":\"object\"}",
                    Invoke: static (instance, args) => ((TodoRoot)instance).Decorate((string)args["title"]!, (string)args["suffix"]!)),
                new CallableDescriptor(
                    Name: "Greet",
                    Description: "Greet a user.",
                    OffUiThread: false,
                    TimeoutSeconds: 0,
                    IsAsync: false,
                    Parameters: new[] { new ParamDescriptor("UserName", "string", IsRequired: true, DefaultValue: null) },
                    ParametersJsonSchema: "{\"type\":\"object\"}",
                    Invoke: static (instance, args) => ((TodoRoot)instance).Greet((string)args["UserName"]!)),
                new CallableDescriptor(
                    Name: "Tag",
                    Description: "Tag with severity.",
                    OffUiThread: false,
                    TimeoutSeconds: 0,
                    IsAsync: false,
                    Parameters: new[]
                    {
                        new ParamDescriptor("level", typeof(Priority).FullName ?? nameof(Priority), IsRequired: true, DefaultValue: null),
                        new ParamDescriptor("tags", "string[]", IsRequired: true, DefaultValue: null),
                    },
                    ParametersJsonSchema: "{\"type\":\"object\"}",
                    Invoke: static (instance, args) =>
                    {
                        var __raw_level = args["level"];
                        var level = __raw_level is JsonElement __json_level
                            ? (Priority)Enum.Parse(typeof(Priority),
                                __json_level.ValueKind == JsonValueKind.String
                                    ? __json_level.GetString()!
                                    : __json_level.GetRawText())
                            : (Priority)__raw_level!;
                        var __raw_tags = args["tags"];
                        var tags = __raw_tags is JsonElement __json_tags
                            ? JsonSerializer.Deserialize<string[]>(__json_tags.GetRawText())!
                            : (string[])__raw_tags!;
                        return ((TodoRoot)instance).Tag(level, tags);
                    }),
                new CallableDescriptor(
                    Name: "CountPlusAsync",
                    Description: "Return TotalCount plus a delta.",
                    OffUiThread: false,
                    TimeoutSeconds: 0,
                    IsAsync: true,
                    Parameters: new[] { new ParamDescriptor("delta", "int", IsRequired: true, DefaultValue: null) },
                    ParametersJsonSchema: "{\"type\":\"object\"}",
                    Invoke: static (instance, args) =>
                    {
                        var root = (TodoRoot)instance;
                        return Task.FromResult<object?>(root.TotalCount + (int)args["delta"]!);
                    }),
            },
            Observables: new[]
            {
                new ObservableDescriptor(
                    Name: "TotalCount",
                    Description: "Total number of todos.",
                    Watchable: true,
                    PollingIntervalMs: 500,
                    ClrTypeName: "int",
                    Read: static instance => ((TodoRoot)instance).TotalCount),
                new ObservableDescriptor(
                    Name: "LastAddedTitle",
                    Description: "Last added title.",
                    Watchable: false,
                    PollingIntervalMs: 500,
                    ClrTypeName: "string",
                    Read: static instance => ((TodoRoot)instance).LastAddedTitle),
            },
            Triggerables: Array.Empty<TriggerableDescriptor>(),
            Events: Array.Empty<EventDescriptor>());
    }

    private sealed class TodoRoot
    {
        private readonly List<string> _items = new();

        public int TotalCount => _items.Count;

        public string? LastAddedTitle => _items.LastOrDefault();

        public void AddTodo(string title) => _items.Add(title);

        public int Add(int a, int b) => a + b;

        public string Decorate(string title, string suffix = "!") => title + suffix;

        public string Greet(string UserName) => "hello " + UserName;

        public string Tag(Priority level, string[] tags) => level + ":" + string.Join(",", tags);
    }

    private enum Priority
    {
        Low,
        High,
    }
}
