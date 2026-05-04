// Marionette.NET — generator diagnostic / rejection tests
//
// Five small "ill-formed" inputs that should produce specific diagnostic IDs.
// Together they cover the diagnostic IDs called out as Phase 1.b focus:
// MAR001, MAR002, MAR003, MAR008. (MAR005, MAR006 follow the same shape;
// MAR004, MAR007 stay permissive in 1.b per the spec.)

using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Marionette.SourceGenerator.Tests;

public class DiagnosticTests
{
    [Fact]
    public void MAR001_StaticClassWithMcpRoot_IsRejected()
    {
        var source = """
            using Marionette;
            namespace Demo;

            [McpRoot]
            public static class StaticRoot
            {
                public static int Adder(int a) => a + 1;
            }
            """;

        var result = GeneratorRunner.Run(source);

        Assert.Contains(result.GeneratorDiagnostics,
            d => d.Id == "MAR001" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MAR001_GenericClassWithMcpRoot_IsRejected()
    {
        var source = """
            using Marionette;
            namespace Demo;

            [McpRoot]
            public class GenericRoot<T>
            {
                [McpCallable("Identity")]
                public T Echo(T value) => value;
            }
            """;

        var result = GeneratorRunner.Run(source);

        Assert.Contains(result.GeneratorDiagnostics,
            d => d.Id == "MAR001" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MAR002_InternalCallable_IsRejected()
    {
        var source = """
            using Marionette;
            namespace Demo;

            [McpRoot]
            public class Root
            {
                [McpCallable("Adds")]
                internal int Add(int a, int b) => a + b;
            }
            """;

        var result = GeneratorRunner.Run(source);

        Assert.Contains(result.GeneratorDiagnostics,
            d => d.Id == "MAR002" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MAR014_GenericCallableMethod_IsRejected()
    {
        var source = """
            using Marionette;
            namespace Demo;

            [McpRoot]
            public class Root
            {
                [McpCallable("Echo")]
                public T Echo<T>(T value) => value;
            }
            """;

        var result = GeneratorRunner.Run(source);

        Assert.Contains(result.GeneratorDiagnostics,
            d => d.Id == "MAR014" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MAR014_ByRefCallableParameter_IsRejected()
    {
        var source = """
            using Marionette;
            namespace Demo;

            [McpRoot]
            public class Root
            {
                [McpCallable("Mutate")]
                public void Mutate(ref int value) => value++;
            }
            """;

        var result = GeneratorRunner.Run(source);

        Assert.Contains(result.GeneratorDiagnostics,
            d => d.Id == "MAR014" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MAR003_CallableOnUnRootedClass_EmitsWarning()
    {
        var source = """
            using Marionette;
            namespace Demo;

            // No [McpRoot] on this class.
            public class Stray
            {
                [McpCallable("Stray method")]
                public int Add(int a, int b) => a + b;
            }
            """;

        var result = GeneratorRunner.Run(source);

        Assert.Contains(result.GeneratorDiagnostics,
            d => d.Id == "MAR003" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void MAR008_McpRootWithNoMembers_EmitsInfo()
    {
        var source = """
            using Marionette;
            namespace Demo;

            [McpRoot]
            public class EmptyRoot
            {
                public string Name { get; set; } = "";
            }
            """;

        var result = GeneratorRunner.Run(source);

        Assert.Contains(result.GeneratorDiagnostics,
            d => d.Id == "MAR008" && d.Severity == DiagnosticSeverity.Info);
    }

    // -------------------------------------------------------------------------
    // Sanity / regression: well-formed input must NOT produce any error
    // -------------------------------------------------------------------------

    [Fact]
    public void WellFormedInput_ProducesNoErrors()
    {
        var source = """
            using Marionette;
            namespace Demo;

            [McpRoot]
            public class GoodRoot
            {
                [McpCallable("Adds")]
                public int Add(int a, int b) => a + b;

                [McpObservable("Last")]
                public int Last { get; private set; }
            }
            """;

        var result = GeneratorRunner.Run(source);

        Assert.False(result.HasGeneratorErrors,
            string.Join("\n", result.GeneratorDiagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
        Assert.False(result.HasCompilationErrors,
            string.Join("\n", result.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    // -------------------------------------------------------------------------
    // Phase 1.6: [McpEvent] diagnostics
    // -------------------------------------------------------------------------

    [Fact]
    public void MAR010_McpEventOnNonStandardDelegate_IsRejected()
    {
        // [McpEvent] on an event whose delegate is Action<T> rather than
        // EventHandler<T>. Phase 1 only supports the standard EventHandler
        // family; the validator rejects with MAR010.
        var source = """
            using System;
            using Marionette;
            namespace Demo;

            public sealed class FooArgs { public string X { get; init; } = ""; }

            [McpRoot]
            public class Root
            {
                [McpEvent("custom delegate")]
                public event Action<FooArgs>? Bad;
            }
            """;

        var result = GeneratorRunner.Run(source);

        Assert.Contains(result.GeneratorDiagnostics,
            d => d.Id == "MAR010" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MAR011_McpEventOnUnRootedClass_EmitsWarning()
    {
        // [McpEvent] on an event whose declaring class lacks [McpRoot].
        // Mirror of MAR003 — silently ignored, warning emitted.
        var source = """
            using System;
            using Marionette;
            namespace Demo;

            // No [McpRoot] here.
            public class Stray
            {
                [McpEvent("orphan")]
                public event EventHandler? Pinged;
            }
            """;

        var result = GeneratorRunner.Run(source);

        Assert.Contains(result.GeneratorDiagnostics,
            d => d.Id == "MAR011" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void MAR012_McpEventInvalidThrottling_EmitsWarning()
    {
        // [McpEvent] with MaxQueueSize <= 0 should produce MAR012 and fall
        // back to defaults (the descriptor is still emitted).
        var source = """
            using System;
            using Marionette;
            namespace Demo;

            [McpRoot]
            public class Root
            {
                [McpEvent("bad sizes", MaxQueueSize = 0, CoalesceWindowMs = -1)]
                public event EventHandler? Pinged;
            }
            """;

        var result = GeneratorRunner.Run(source);

        Assert.Contains(result.GeneratorDiagnostics,
            d => d.Id == "MAR012" && d.Severity == DiagnosticSeverity.Warning);
        Assert.False(result.HasGeneratorErrors,
            "MAR012 must be a warning, not an error — the descriptor still emits with defaults.");
    }

    [Fact]
    public void WellFormedEvents_ProduceNoErrors()
    {
        var source = """
            using System;
            using Marionette;
            namespace Demo;

            public sealed class FooArgs : EventArgs
            {
                public string Name { get; init; } = "";
            }

            [McpRoot]
            public class Root
            {
                [McpEvent("a thing happened")]
                public event EventHandler<FooArgs>? Happened;

                [McpEvent("a generic ping")]
                public event EventHandler? Pinged;
            }
            """;

        var result = GeneratorRunner.Run(source);

        Assert.False(result.HasGeneratorErrors,
            string.Join("\n", result.GeneratorDiagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
        Assert.False(result.HasCompilationErrors,
            string.Join("\n", result.CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    [Fact]
    public void MAR013_PublicRootMethodWithoutMcpCallable_EmitsInfo()
    {
        var source = """
            using Marionette;
            namespace Demo;

            [McpRoot]
            public class Root
            {
                public void ResetAll()
                {
                }
            }
            """;

        var result = GeneratorRunner.Run(source);

        Assert.Contains(result.GeneratorDiagnostics,
            d => d.Id == "MAR013" && d.Severity == DiagnosticSeverity.Info);
        Assert.False(result.HasGeneratorErrors,
            string.Join("\n", result.GeneratorDiagnostics.Select(d => $"{d.Id}: {d.GetMessage()}")));
    }

    // -------------------------------------------------------------------------
    // Phase 1.2 gating: MCP_ENABLED off → no source emitted, but diagnostics
    // still flow so squigglies still appear in the IDE.
    // -------------------------------------------------------------------------

    [Fact]
    public void McpDisabled_EmitsNoSource_ButReplaysDiagnostics()
    {
        var source = """
            using Marionette;
            namespace Demo;

            [McpRoot]
            public class GoodRoot
            {
                [McpCallable("Adds")]
                public int Add(int a, int b) => a + b;

                [McpObservable("Last")]
                public int Last { get; private set; }
            }

            [McpRoot]
            public static class StaticRoot
            {
                public static int X(int a) => a;
            }
            """;

        var result = GeneratorRunner.Run(source, mcpEnabled: false);

        // No Marionette.g.cs was emitted (the generated text is empty).
        Assert.Equal(string.Empty, result.GeneratedText);

        // But MAR001 (static root) still surfaces as a Roslyn diagnostic so
        // adopters get the squiggly even in stripped builds.
        Assert.Contains(result.GeneratorDiagnostics,
            d => d.Id == "MAR001" && d.Severity == DiagnosticSeverity.Error);
    }
}
