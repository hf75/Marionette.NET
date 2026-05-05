// Marionette.NET — Phase 10 AOT-clean per-method dynamic tools
//
// Subclass of Microsoft.Extensions.AI.AIFunction that lets us register
// MCP tools at runtime *without* the SDK going through its
// AIFunctionFactory.Create(Delegate) reflection path. That factory walks
// MethodInfo.GetParameters(), builds dynamic-codegen marshallers, and
// calls JsonSerializer.Deserialize(JsonElement, Type) with a runtime Type —
// none of which survive AOT for non-primitive parameter shapes.
//
// The SDK's AIFunctionMcpServerTool.Create(AIFunction, ...) overload takes
// a different path: it reads four properties (Name, Description, JsonSchema,
// UnderlyingMethod) and invokes the function via AIFunction.InvokeAsync. We
// supply a pre-built JSON schema (Phase 1.b source-generator output), set
// UnderlyingMethod to null so the SDK skips its MethodInfo / attribute
// reflection branches, and inject our own InvokeCoreAsync that runs the
// existing MarionetteDispatch pipeline. The SDK accepts a CallToolResult
// directly from InvokeCoreAsync so the IsError signal we already raise on
// structured-error JSON propagates unchanged.

using System;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.AI;

namespace Marionette.Runtime.Tools;

/// <summary>
/// Reflection-free <see cref="AIFunction"/> implementation used by
/// <see cref="DynamicToolRegistry"/> to register Marionette's per-method
/// dynamic tools through the AOT-clean
/// <see cref="ModelContextProtocol.Server.McpServerTool.Create(AIFunction, ModelContextProtocol.Server.McpServerToolCreateOptions?)"/>
/// overload.
/// </summary>
internal sealed class MarionetteAIFunction : AIFunction
{
    private readonly Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> _invoke;
    private readonly JsonElement _schema;

    public MarionetteAIFunction(
        string name,
        string description,
        JsonElement schema,
        Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> invoke)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description ?? string.Empty;
        _schema = schema;
        _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
    }

    public override string Name { get; }

    public override string Description { get; }

    public override JsonElement JsonSchema => _schema;

    /// <summary>
    /// Returning <see langword="null"/> tells the SDK there is no
    /// <see cref="MethodInfo"/> to scrape attributes / XML-doc metadata
    /// from. AIFunctionMcpServerTool.Create skips its reflection branches
    /// entirely on this signal — the load-bearing AOT contract.
    /// </summary>
    public override MethodInfo? UnderlyingMethod => null;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
        => _invoke(arguments ?? new AIFunctionArguments(), cancellationToken);
}
