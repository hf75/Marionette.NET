// Marionette.NET — generator snapshot tests
//
// Snapshot strategy:
//   * Each test calls Snapshot.Verify(actual, "<TestName>") which:
//       1. Writes <TestName>.received.txt to the Snapshots/ folder.
//       2. Reads <TestName>.verified.txt (if it exists) and compares.
//       3. Asserts equality.
//   * The first run produces a .received.txt; rename to .verified.txt to bless.
//   * On CI, missing .verified.txt is a hard failure (no auto-bless).
//   * .gitignore covers *.received.txt — only .verified.txt is committed.
//
// We deliberately don't depend on Verify.SourceGenerators or other off-the-shelf
// snapshot frameworks: the tooling is one screen of code and avoids a network
// dependency this dev machine doesn't have cached.

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Marionette.SourceGenerator.Tests;

public class SnapshotTests
{
    [Fact]
    public void GoldenInput_EmitsExpectedManifest()
    {
        // A "real-world" sample: a root with a callable, an observable, plus
        // [McpCallable] async / void variations. Acts as the canary for any
        // emitter regression — every shape we care about is exercised.
        var source = """
            using Marionette;
            using System.Threading.Tasks;

            namespace Demo;

            [McpRoot("calculator")]
            public class Calculator
            {
                [McpCallable("Adds two integers and returns the sum.")]
                public int Add(int a, int b) => a + b;

                [McpCallable("Divides two doubles, off the UI thread.", OffUiThread = true, TimeoutSeconds = 30)]
                public double Divide(double numerator, double denominator) => numerator / denominator;

                [McpCallable("Resets the calculator to zero.")]
                public void Reset() { }

                [McpCallable("Loads a value from a remote source.")]
                public Task<int> LoadAsync() => Task.FromResult(42);

                [McpObservable("Most recent computed result.")]
                public double LastResult { get; private set; }

                [McpObservable("Total operations performed.", Watchable = true, PollingIntervalMs = 250)]
                public int OperationCount { get; private set; }
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        // Normalize line endings so the snapshot is portable across Win/Linux.
        Snapshot.Verify(Normalize(result.GeneratedText), "GoldenInput_EmitsExpectedManifest");
    }

    [Fact]
    public void GoldenOverloads_EmitsBothCallables()
    {
        // Phase 2.2 trapdoor verification: when two [McpCallable] methods on
        // the same root share a name (overloads with different signatures),
        // the source generator must emit BOTH CallableDescriptors. The
        // runtime's DynamicToolRegistry then disambiguates the dynamic tool
        // names via the 8-hex hash suffix. The descriptors themselves carry
        // the full signature information needed for hash computation.
        var source = """
            using Marionette;

            namespace Demo;

            [McpRoot("overloadroot")]
            public class OverloadRoot
            {
                [McpCallable("Add two integers.")]
                public int Add(int a, int b) => a + b;

                [McpCallable("Add three integers.")]
                public int Add(int a, int b, int c) => a + b + c;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        // Both Add descriptors must be present in the emitted manifest.
        // The generator emits them in source order; the runtime then
        // disambiguates the dynamic tool names. We verify by string match
        // rather than a full snapshot (the parameter schemas differ between
        // the two-arg and three-arg overloads).
        var generated = result.GeneratedText;
        Assert.Contains("Name: \"Add\"", generated);
        // Two ParametersJsonSchema lines, each with a different shape.
        Assert.Contains("\\\"a\\\":{\\\"type\\\":\\\"integer\\\"},\\\"b\\\":{\\\"type\\\":\\\"integer\\\"}}", generated);
        Assert.Contains("\\\"a\\\":{\\\"type\\\":\\\"integer\\\"},\\\"b\\\":{\\\"type\\\":\\\"integer\\\"},\\\"c\\\":{\\\"type\\\":\\\"integer\\\"}}", generated);
        // Two distinct Invoke lambdas (different arg counts).
        Assert.Contains("typed.Add(a, b);", generated);
        Assert.Contains("typed.Add(a, b, c);", generated);
    }

    [Fact]
    public void GoldenParametersSchema_EmitsExpectedManifest()
    {
        // Phase 2.2 snapshot: a root with [McpCallable] methods exercising
        // every shape the JsonSchemaWriter.WriteParametersSchema helper has
        // to cover — required scalars, an optional with default, an enum
        // arg, an array. Verifies the per-method ParametersJsonSchema string
        // emitted into Marionette.g.cs.
        var source = """
            using Marionette;
            using System;

            namespace Demo;

            public enum Severity { Info, Warning, Error }

            [McpRoot("paramsroot")]
            public class ParamsRoot
            {
                [McpCallable("Required scalars + optional with default.")]
                public int Combo(string name, int count, bool flag = true) => count;

                [McpCallable("Enum + array params.")]
                public void Tagged(Severity level, string[] tags) { }

                [McpCallable("Zero parameters.")]
                public string Ping() => "pong";
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        Snapshot.Verify(Normalize(result.GeneratedText), "GoldenParametersSchema_EmitsExpectedManifest");
    }

    [Fact]
    public void GoldenEventInput_EmitsExpectedManifest()
    {
        // Phase 1.6 snapshot: a root with both event shapes (EventHandler and
        // EventHandler<T>), a callable, plus a watchable observable, plus an
        // event with custom throttling. Verifies the EventDescriptor emit
        // including the schema string and the Subscribe lambda, for both the
        // typed-args path and the no-args path.
        var source = """
            using System;
            using Marionette;

            namespace Demo;

            public sealed class ItemAddedEventArgs : EventArgs
            {
                public ItemAddedEventArgs(string itemName, int count, DateTime addedAt)
                {
                    ItemName = itemName;
                    Count = count;
                    AddedAt = addedAt;
                }
                public string ItemName { get; }
                public int Count { get; }
                public DateTime AddedAt { get; }
            }

            [McpRoot("evroot")]
            public class EventRoot
            {
                [McpCallable("Add an item.")]
                public void AddItem(string name) { }

                [McpObservable("Total item count.", Watchable = true)]
                public int Count { get; private set; }

                [McpEvent("An item was added.")]
                public event EventHandler<ItemAddedEventArgs>? ItemAdded;

                [McpEvent("Generic refresh signal.", MinIntervalMs = 50, MaxQueueSize = 250, CoalesceWindowMs = 200)]
                public event EventHandler? Refreshed;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        Snapshot.Verify(Normalize(result.GeneratedText), "GoldenEventInput_EmitsExpectedManifest");
    }

    [Fact]
    public void GoldenCollectionShapes_EmitsExpectedManifest()
    {
        // Phase 8.5: every collection shape the generator newly supports.
        // The fixture exercises:
        //   - IEnumerable<T> / IReadOnlyList<T> / IReadOnlyCollection<T>
        //     (all three dispatch through CreateIEnumerableInfo)
        //   - IList<T> / ICollection<T>
        //   - ISet<T> / IReadOnlySet<T> / HashSet<T>
        //   - Stack<T> / Queue<T>
        //   - Dictionary<K,V> with non-string K (int and an enum)
        //   - IDictionary<K,V> / IReadOnlyDictionary<K,V>
        // The shapes are exposed via [McpEvent] args so they participate in
        // the JSON-context emission. We assert the generated text references
        // each STJ factory at least once.
        var source = """
            using System;
            using System.Collections.Generic;
            using Marionette;

            namespace Demo;

            public enum Severity { Low, Medium, High }

            public sealed class CollectionEventArgs : EventArgs
            {
                public CollectionEventArgs() { }

                public IEnumerable<int> EnumerableInts { get; init; } = System.Linq.Enumerable.Empty<int>();
                public IReadOnlyList<string> ReadOnlyListOfStrings { get; init; } = System.Array.Empty<string>();
                public IReadOnlyCollection<int> ReadOnlyCollectionOfInts { get; init; } = System.Array.Empty<int>();
                public IList<string> ListOfStrings { get; init; } = new List<string>();
                public ICollection<int> CollectionOfInts { get; init; } = new List<int>();
                public ISet<string> SetOfStrings { get; init; } = new HashSet<string>();
                public IReadOnlySet<int> ReadOnlySetOfInts { get; init; } = new HashSet<int>();
                public HashSet<string> HashSetOfStrings { get; init; } = new HashSet<string>();
                public Stack<int> StackOfInts { get; init; } = new Stack<int>();
                public Queue<string> QueueOfStrings { get; init; } = new Queue<string>();
                public Dictionary<int, string> IntKeyedDictionary { get; init; } = new();
                public Dictionary<Severity, int> EnumKeyedDictionary { get; init; } = new();
                public IDictionary<string, int> IDict { get; init; } = new Dictionary<string, int>();
                public IReadOnlyDictionary<string, int> IReadOnlyDict { get; init; } = new Dictionary<string, int>();
            }

            [McpRoot("collroot")]
            public class CollectionRoot
            {
                [McpEvent("Every Phase 8.5 collection shape on one event.")]
                public event EventHandler<CollectionEventArgs>? CollectionFired;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;

        // Each STJ factory must appear at least once. We don't pin the exact
        // emission shape (a verified-snapshot would be brittle as the
        // emitter evolves) — what matters is that every Phase-8.5 shape
        // dispatches to its expected factory.
        Assert.Contains("CreateIEnumerableInfo", generated);
        Assert.Contains("CreateIListInfo", generated);
        Assert.Contains("CreateICollectionInfo", generated);
        Assert.Contains("CreateISetInfo", generated);
        Assert.Contains("CreateStackInfo", generated);
        Assert.Contains("CreateQueueInfo", generated);
        Assert.Contains("CreateIDictionaryInfo", generated);
        Assert.Contains("CreateIReadOnlyDictionaryInfo", generated);
        // Non-string dictionary keys: int + enum. The known-shape path
        // substitutes canonical CLR full names into its template (so the
        // emitted output uses System.Int32 / System.String, not the C#
        // keyword aliases). The interface-fallback path on Phase-11 picks
        // up display strings instead — that case is exercised in the
        // GoldenInterfaceFallbackShapes fixture.
        Assert.Contains("Dictionary<global::System.Int32, global::System.String>", generated);
        Assert.Contains("Dictionary<global::Demo.Severity, global::System.Int32>", generated);
        // ObjectCreator should pick HashSet for set-like interfaces.
        Assert.Contains("new global::System.Collections.Generic.HashSet<global::System.String>()", generated);
    }

    [Fact]
    public void GoldenMultiDimArrayRank2_RegistersWithCustomConverter()
    {
        // Phase 12.4: rank-2 multi-dim arrays of primitive elements now
        // register via the runtime MultiDimArrayRank2Converter<T>. The
        // emitted context wires JsonMetadataServices.CreateValueInfo<T[,]>
        // with the converter, which itself delegates per-element write to
        // the primitive's own JsonConverter (read from the inner
        // JsonTypeInfo's .Converter property).
        var source = """
            using System;
            using Marionette;

            namespace Demo;

            public sealed class MatrixEventArgs : EventArgs
            {
                public MatrixEventArgs() { }
                public int[,] Pixels { get; init; } = new int[0,0];
                public double[,] Heatmap { get; init; } = new double[0,0];
            }

            [McpRoot("matrixroot")]
            public class MatrixRoot
            {
                [McpEvent("Args carrying rank-2 multi-dim arrays.")]
                public event EventHandler<MatrixEventArgs>? Fired;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        // Both rank-2 arrays land as JsonTypeInfo properties.
        Assert.Contains("MultiDimArray2_System_Int32", generated);
        Assert.Contains("MultiDimArray2_System_Double", generated);
        // The runtime converter is wired with the element's typed converter.
        Assert.Contains("MultiDimArrayRank2Converter<global::System.Int32>", generated);
        Assert.Contains("MultiDimArrayRank2Converter<global::System.Double>", generated);
    }

    [Fact]
    public void GoldenUnsupportedShapes_FallsBackToReflection()
    {
        // Phase 12.4 + 13.E.13 update: ranks 2/3/4 are now supported (see
        // GoldenMultiDimArrayRank2 / Rank3And4). Rank 5+ remains unsupported.
        var source = """
            using System;
            using Marionette;

            namespace Demo;

            public sealed class Rank5EventArgs : EventArgs
            {
                public Rank5EventArgs() { }
                public int[,,,,] HyperVolume { get; init; } = new int[0,0,0,0,0];
            }

            [McpRoot("unsupported")]
            public class UnsupportedRoot
            {
                [McpEvent("Args type the generator cannot register (rank-5 array).")]
                public event EventHandler<Rank5EventArgs>? Fired;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        // Rank-5+ array forced rollback of the args type.
        Assert.DoesNotContain("Demo_Rank5EventArgs", generated);
        // Event still surfaces in the manifest with a string-based fallback.
        Assert.Contains("\"Fired\"", generated);
    }

    [Fact]
    public void GoldenInterfaceFallbackShapes_EmitsExpectedManifest()
    {
        // Phase 11: every type the interface-fallback path picks up. The
        // fixture exercises:
        //   - ConcurrentDictionary<K,V>          → IDictionary kind
        //   - ConcurrentQueue<T>                 → IEnumerable kind
        //   - ConcurrentStack<T>                 → IEnumerable kind
        //   - ConcurrentBag<T>                   → IEnumerable kind
        //   - A user-defined sealed class       MyCustomList<T> : IList<T>
        //                                          → IList kind
        //   - A user-defined sealed class       MyCustomSet<T>  : ISet<T>
        //                                          → ISet kind
        //   - A user-defined sealed class       MyCustomDict<K,V> : IDictionary<K,V>
        //                                          → IDictionary kind
        // The shapes are exposed via [McpEvent] args. We assert the generated
        // text references the matching factory and uses the user/concurrent
        // type as the ObjectCreator's concrete container (not a default
        // substitute).
        var source = """
            using System.Collections.Concurrent;
            using System.Collections.Generic;
            using System;
            using Marionette;

            namespace Demo;

            // Stand-in user types implementing the standard collection
            // interfaces. The generator does NOT inspect their bodies — only
            // their interface implementation list and the public parameterless
            // ctor. We provide trivial wrappers that delegate to a backing
            // collection so the test compiles cleanly.
            public sealed class MyCustomList<T> : List<T> { public MyCustomList() { } }
            public sealed class MyCustomSet<T>  : HashSet<T> { public MyCustomSet() { } }
            public sealed class MyCustomDict<K, V> : Dictionary<K, V> where K : notnull { public MyCustomDict() { } }

            public sealed class FallbackEventArgs : EventArgs
            {
                public FallbackEventArgs() { }
                public ConcurrentDictionary<string, int> ConcDict { get; init; } = new();
                public ConcurrentQueue<string> ConcQueue { get; init; } = new();
                public ConcurrentStack<int> ConcStack { get; init; } = new();
                public ConcurrentBag<string> ConcBag { get; init; } = new();
                public MyCustomList<int> CustomList { get; init; } = new();
                public MyCustomSet<string> CustomSet { get; init; } = new();
                public MyCustomDict<string, int> CustomDict { get; init; } = new();
            }

            [McpRoot("fallbackroot")]
            public class FallbackRoot
            {
                [McpEvent("Custom + concurrent collections via interface fallback.")]
                public event EventHandler<FallbackEventArgs>? Fired;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;

        // Each user/concurrent type must be registered AND its ObjectCreator
        // must reference the user/concurrent type itself (not a Dictionary /
        // List / HashSet substitute). The "Custom_" prefix on the encoded
        // property name is what the interface-fallback path emits. C#
        // FullyQualifiedFormat uses keyword aliases inside generic arg lists
        // (e.g. `<string, int>` rather than `<System.String, System.Int32>`).
        Assert.Contains("Custom_System_Collections_Concurrent_ConcurrentDictionary", generated);
        Assert.Contains("new global::System.Collections.Concurrent.ConcurrentDictionary<string, int>()", generated);
        Assert.Contains("Custom_System_Collections_Concurrent_ConcurrentQueue", generated);
        Assert.Contains("new global::System.Collections.Concurrent.ConcurrentQueue<string>()", generated);
        Assert.Contains("Custom_System_Collections_Concurrent_ConcurrentStack", generated);
        Assert.Contains("new global::System.Collections.Concurrent.ConcurrentStack<int>()", generated);
        Assert.Contains("Custom_System_Collections_Concurrent_ConcurrentBag", generated);
        Assert.Contains("new global::System.Collections.Concurrent.ConcurrentBag<string>()", generated);
        Assert.Contains("Custom_Demo_MyCustomList", generated);
        Assert.Contains("new global::Demo.MyCustomList<int>()", generated);
        Assert.Contains("Custom_Demo_MyCustomSet", generated);
        Assert.Contains("new global::Demo.MyCustomSet<string>()", generated);
        Assert.Contains("Custom_Demo_MyCustomDict", generated);
        Assert.Contains("new global::Demo.MyCustomDict<string, int>()", generated);
    }

    [Fact]
    public void GoldenNoCtorCustomCollection_RegistersSerializeOnly()
    {
        // Phase 12.6: types that implement a supported interface but lack
        // a public parameterless ctor are registered as SERIALIZE-ONLY —
        // the JsonTypeInfo lands on the context but ObjectCreator = null
        // so deserialisation by callers will fail clearly. The serialise
        // direction enumerates the existing instance (no ctor needed).
        // Adopters who only use the type as an event/observable/return
        // payload get full source-gen coverage; adopters who try to
        // round-trip get a runtime InvalidOperationException at exactly
        // the call site that needed deserialisation.
        var source = """
            using System.Collections.Generic;
            using System;
            using Marionette;

            namespace Demo;

            // Lacks a public parameterless ctor.
            public sealed class CtorRequiredList<T> : List<T>
            {
                public CtorRequiredList(int capacity) : base(capacity) { }
            }

            public sealed class NoCtorEventArgs : EventArgs
            {
                public NoCtorEventArgs() { }
                public CtorRequiredList<int> Items { get; init; } = new(0);
            }

            [McpRoot("noctorroot")]
            public class NoCtorRoot
            {
                [McpEvent("Args type that implements IList<T> but lacks a public parameterless ctor.")]
                public event EventHandler<NoCtorEventArgs>? Fired;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        // The args type DOES appear (Phase 12.6 — serialise-only registration).
        Assert.Contains("Demo_NoCtorEventArgs", generated);
        Assert.Contains("Custom_Demo_CtorRequiredList", generated);
        // ObjectCreator is null for the no-ctor user collection — the
        // emitter would otherwise render `static () => new CtorRequiredList<int>()`
        // which wouldn't compile.
        Assert.Contains("ObjectCreator = null", generated);
    }

    [Fact]
    public void GoldenJsonIgnoreConditions_EmitsExpectedManifest()
    {
        // Phase 12.7: [JsonIgnore(Condition = …)] sub-modes:
        //   * (no attribute)             → no IgnoreCondition emitted (default null)
        //   * [JsonIgnore]               → property dropped (Always semantic)
        //   * [JsonIgnore(Always)]       → property dropped (explicit)
        //   * [JsonIgnore(Never)]        → property included, no IgnoreCondition (acts like no attribute)
        //   * [JsonIgnore(WhenWritingDefault)] → property included, IgnoreCondition.WhenWritingDefault emitted
        //   * [JsonIgnore(WhenWritingNull)]    → property included, IgnoreCondition.WhenWritingNull emitted
        var source = """
            using System;
            using System.Text.Json.Serialization;
            using Marionette;

            namespace Demo;

            public sealed class IgnoreEventArgs : EventArgs
            {
                public IgnoreEventArgs() { }

                public string Plain { get; init; } = "";

                [JsonIgnore]
                public string DroppedDefault { get; init; } = "secret";

                [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
                public string DroppedExplicit { get; init; } = "secret";

                [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
                public string AlwaysInclude { get; init; } = "kept";

                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
                public int? OptionalCount { get; init; }

                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                public string? OptionalNote { get; init; }
            }

            [McpRoot("ignoreroot")]
            public class IgnoreRoot
            {
                [McpEvent("Args type with every JsonIgnore mode.")]
                public event EventHandler<IgnoreEventArgs>? Fired;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;

        // Plain + AlwaysInclude must appear as JsonPropertyInfo entries.
        Assert.Contains("PropertyName = \"Plain\"", generated);
        Assert.Contains("PropertyName = \"AlwaysInclude\"", generated);
        Assert.Contains("PropertyName = \"OptionalCount\"", generated);
        Assert.Contains("PropertyName = \"OptionalNote\"", generated);

        // Always-dropped properties must NOT appear (PropertyName = "DroppedX" never emitted).
        Assert.DoesNotContain("PropertyName = \"DroppedDefault\"", generated);
        Assert.DoesNotContain("PropertyName = \"DroppedExplicit\"", generated);

        // Plain + AlwaysInclude (Never condition) get IgnoreCondition = null.
        // OptionalCount + OptionalNote get IgnoreCondition.WhenWritingDefault / WhenWritingNull.
        Assert.Contains("IgnoreCondition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault", generated);
        Assert.Contains("IgnoreCondition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull", generated);
    }

    [Fact]
    public void GoldenDeepGraph_RegistersUpTo64Levels()
    {
        // Phase 12.8: deeply-nested-but-acyclic graphs survive registration
        // even past the previous depth-6 cap. The visiting HashSet still
        // catches true cycles. We exercise an 8-deep chain (would have
        // failed under the old cap) and assert every level lands in the
        // generated context.
        var source = """
            using System;
            using Marionette;

            namespace Demo;

            public sealed class L1 { public L2? Next { get; init; } }
            public sealed class L2 { public L3? Next { get; init; } }
            public sealed class L3 { public L4? Next { get; init; } }
            public sealed class L4 { public L5? Next { get; init; } }
            public sealed class L5 { public L6? Next { get; init; } }
            public sealed class L6 { public L7? Next { get; init; } }
            public sealed class L7 { public L8? Next { get; init; } }
            public sealed class L8 { public string Leaf { get; init; } = ""; }

            public sealed class DeepEventArgs : EventArgs
            {
                public DeepEventArgs() { }
                public L1? Chain { get; init; }
            }

            [McpRoot("deeproot")]
            public class DeepRoot
            {
                [McpEvent("Eight-level-deep object graph.")]
                public event EventHandler<DeepEventArgs>? Fired;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        // Every level must land as a JsonTypeInfo property on the context.
        Assert.Contains("Demo_DeepEventArgs", generated);
        Assert.Contains("Demo_L1", generated);
        Assert.Contains("Demo_L8", generated);
    }

    [Fact]
    public void GoldenSelfReferencingType_FallsBackToReflection()
    {
        // Phase 12.8 — true cycles are still caught by the visiting HashSet.
        // A node type that references itself must NOT crash the generator;
        // it falls back to runtime serialisation.
        var source = """
            using System;
            using System.Collections.Generic;
            using Marionette;

            namespace Demo;

            public sealed class TreeNode
            {
                public string Label { get; init; } = "";
                public List<TreeNode> Children { get; init; } = new();
            }

            public sealed class CyclicEventArgs : EventArgs
            {
                public CyclicEventArgs() { }
                public TreeNode? Root { get; init; }
            }

            [McpRoot("treeroot")]
            public class TreeRoot
            {
                [McpEvent("Self-referencing tree.")]
                public event EventHandler<CyclicEventArgs>? Fired;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        // The cyclic args type rolls back via the visiting HashSet — no
        // JsonTypeInfo property should land for it.
        Assert.DoesNotContain("Demo_CyclicEventArgs", generated);
        Assert.DoesNotContain("Demo_TreeNode", generated);
    }

    [Fact]
    public void GoldenValueTupleKey_RegistersWithRuntimeConverter()
    {
        // Phase 12.5: rank-2 and rank-3 ValueTuple keys on Dictionary
        // properties register with a runtime ValueTupleKeyConverter<T1,T2>
        // (or T1,T2,T3). The converter delegates per-component read/write
        // to the typed primitive converter from each element's own
        // JsonTypeInfo.Converter — so we just need to assert the
        // conversion wiring lands in the generated code.
        var source = """
            using System;
            using System.Collections.Generic;
            using Marionette;

            namespace Demo;

            public sealed class TupleKeyEventArgs : EventArgs
            {
                public TupleKeyEventArgs() { }
                public Dictionary<(int X, int Y), string> Grid { get; init; } = new();
                public Dictionary<(string Region, int Year, int Quarter), double> Sales { get; init; } = new();
            }

            [McpRoot("tupleroot")]
            public class TupleRoot
            {
                [McpEvent("Tuple-keyed dictionary payloads.")]
                public event EventHandler<TupleKeyEventArgs>? Fired;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        // Both tuple key types land as JsonTypeInfo properties.
        Assert.Contains("ValueTuple2_System_Int32_System_Int32", generated);
        Assert.Contains("ValueTuple3_System_String_System_Int32_System_Int32", generated);
        // Runtime converters are wired with the element JsonTypeInfo's
        // Converter members.
        Assert.Contains("ValueTupleKeyConverter<global::System.Int32, global::System.Int32>", generated);
        Assert.Contains("ValueTupleKeyConverter<global::System.String, global::System.Int32, global::System.Int32>", generated);
        // The dictionaries reference the tuple key types as their KeyInfo.
        Assert.Contains("KeyInfo = ValueTuple2_System_Int32_System_Int32", generated);
        Assert.Contains("KeyInfo = ValueTuple3_System_String_System_Int32_System_Int32", generated);
    }

    [Fact]
    public void GoldenMcpRaisable_EmitsTypedDispatcher()
    {
        // Phase 12.2: [assembly: McpRaisable(typeof(T), "Click")] declarations
        // produce a Marionette.Generated.RaiseEventCatalog static class with a
        // typed switch arm per declaration. The test stubs the WPF surface
        // inside the user assembly so the validator's string-name lookup
        // ("System.Windows.RoutedEvent" etc.) hits — production user code
        // gets these from PresentationCore, but a netstandard test harness
        // would have to drag in WindowsDesktop refs to do the same. The
        // stub-by-namespace pattern exercises the generator against the
        // exact API surface it cares about.
        var source = """
            using System;
            using Marionette;

            [assembly: McpRaisable(typeof(App.Controls.MyButton), "Click")]
            [assembly: McpRaisable(typeof(App.Controls.MyButton), "Submitted")]

            namespace System.Windows
            {
                public class RoutedEvent { }

                public class RoutedEventArgs
                {
                    public RoutedEventArgs() { }
                    public RoutedEventArgs(RoutedEvent routedEvent, object source) { }
                }

                public class UIElement
                {
                    public void RaiseEvent(RoutedEventArgs e) { }
                }
            }

            namespace App.Controls
            {
                public class MyButton : System.Windows.UIElement
                {
                    public static readonly System.Windows.RoutedEvent ClickEvent = new();
                    public static readonly System.Windows.RoutedEvent SubmittedEvent = new();
                }
            }

            namespace Demo
            {
                [McpRoot("rootraiser")]
                public class Raiser
                {
                    [McpCallable("noop")] public void Noop() { }
                }
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        // The catalog class must be present.
        Assert.Contains("public static class RaiseEventCatalog", generated);
        // Both arms must wire up. Match the case label, the cast, and the
        // static-field reference — that's the trim-preservation core.
        Assert.Contains("case global::App.Controls.MyButton typed:", generated);
        Assert.Contains("case \"Click\":", generated);
        Assert.Contains("case \"Submitted\":", generated);
        Assert.Contains("global::App.Controls.MyButton.ClickEvent", generated);
        Assert.Contains("global::App.Controls.MyButton.SubmittedEvent", generated);
        // The framework-specific RoutedEventArgs type must appear.
        Assert.Contains("global::System.Windows.RoutedEventArgs", generated);
        Assert.Contains("((global::System.Windows.UIElement)typed).RaiseEvent(", generated);
        // The module initializer auto-registers with the runtime registry.
        Assert.Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]", generated);
        Assert.Contains("global::Marionette.Runtime.Adapters.RaiseEventCatalog.Register(", generated);
    }

    [Fact]
    public void GoldenMcpRaisable_InvalidEntryEmitsMar015()
    {
        // Phase 12.2: when a [McpRaisable] declaration references a control
        // type without a matching `<EventName>Event` static field, the
        // validator drops the entry and emits MAR015. The catalog (if any
        // valid entries exist) still emits — the bad entry is silently
        // skipped except for the diagnostic.
        var source = """
            using System;
            using Marionette;

            [assembly: McpRaisable(typeof(App.Controls.MyButton), "DoesNotExist")]

            namespace System.Windows
            {
                public class RoutedEvent { }
                public class RoutedEventArgs
                {
                    public RoutedEventArgs() { }
                    public RoutedEventArgs(RoutedEvent routedEvent, object source) { }
                }
                public class UIElement
                {
                    public void RaiseEvent(RoutedEventArgs e) { }
                }
            }

            namespace App.Controls
            {
                public class MyButton : System.Windows.UIElement
                {
                    public static readonly System.Windows.RoutedEvent ClickEvent = new();
                }
            }

            namespace Demo
            {
                [McpRoot("rootraiser")]
                public class Raiser
                {
                    [McpCallable("noop")] public void Noop() { }
                }
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        // No errors — MAR015 is a Warning.
        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        // The MAR015 diagnostic must surface.
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "MAR015");
        // No catalog should be emitted (no valid entries).
        Assert.DoesNotContain("public static class RaiseEventCatalog", result.GeneratedText);
    }

    [Fact]
    public void GoldenValueTupleKey_Rank4And5_RegistersWithRuntimeConverter()
    {
        // Phase 13.E.14: rank 4 + rank 5 tuple keys.
        var source = """
            using System;
            using System.Collections.Generic;
            using Marionette;

            namespace Demo;

            public sealed class WideTupleEventArgs : EventArgs
            {
                public WideTupleEventArgs() { }
                public Dictionary<(int A, int B, int C, int D), string> Quad { get; init; } = new();
                public Dictionary<(string K, int A, int B, int C, int D), double> Penta { get; init; } = new();
            }

            [McpRoot("widetuple")]
            public class WideTupleRoot
            {
                [McpEvent("Wide tuple-keyed dicts.")]
                public event EventHandler<WideTupleEventArgs>? Fired;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        Assert.Contains("ValueTuple4_System_Int32_System_Int32_System_Int32_System_Int32", generated);
        Assert.Contains("ValueTuple5_System_String_System_Int32_System_Int32_System_Int32_System_Int32", generated);
        Assert.Contains("ValueTupleKeyConverter<global::System.Int32, global::System.Int32, global::System.Int32, global::System.Int32>", generated);
        Assert.Contains("ValueTupleKeyConverter<global::System.String, global::System.Int32, global::System.Int32, global::System.Int32, global::System.Int32>", generated);
    }

    [Fact]
    public void GoldenMultiDimArray_Rank3And4_RegistersWithRuntimeConverter()
    {
        // Phase 13.E.13: rank 3 + rank 4 multi-dim arrays register with the
        // matching runtime converter via JsonMetadataServices.CreateValueInfo.
        var source = """
            using System;
            using Marionette;

            namespace Demo;

            public sealed class CubeEventArgs : EventArgs
            {
                public CubeEventArgs() { }
                public int[,,] Voxels { get; init; } = new int[0,0,0];
                public double[,,,] Tensor { get; init; } = new double[0,0,0,0];
            }

            [McpRoot("cuberoot")]
            public class CubeRoot
            {
                [McpEvent("Multi-dim payloads.")]
                public event EventHandler<CubeEventArgs>? Fired;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        Assert.Contains("MultiDimArray3_System_Int32", generated);
        Assert.Contains("MultiDimArray4_System_Double", generated);
        Assert.Contains("MultiDimArrayRank3Converter<global::System.Int32>", generated);
        Assert.Contains("MultiDimArrayRank4Converter<global::System.Double>", generated);
    }

    [Fact]
    public void GoldenCustomJsonConverter_TypeLevel_BridgesUserConverter()
    {
        // Phase 13.E.19: type-level [JsonConverter(typeof(X))] redirects the
        // entire JsonTypeInfo<T> creation to use the user's converter. The
        // type's own properties are not walked — the converter owns the
        // round-trip.
        var source = """
            using System;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using Marionette;

            namespace Demo;

            public sealed class MoneyJsonConverter : JsonConverter<Money>
            {
                public override Money Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => default;
                public override void Write(Utf8JsonWriter w, Money v, JsonSerializerOptions o) => w.WriteStringValue("$" + v.Amount);
            }

            [JsonConverter(typeof(MoneyJsonConverter))]
            public readonly struct Money
            {
                public Money(decimal amount) { Amount = amount; }
                public decimal Amount { get; }
            }

            public sealed class CartEventArgs : EventArgs
            {
                public Money Total { get; init; }
            }

            [McpRoot("cartroot")]
            public class CartRoot
            {
                [McpEvent("Cart total updated.")]
                public event EventHandler<CartEventArgs>? TotalChanged;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        // The Money type's JsonTypeInfo wires through CreateValueInfo +
        // a fresh user converter instance.
        Assert.Contains("CreateValueInfo<global::Demo.Money>(", generated);
        Assert.Contains("new global::Demo.MoneyJsonConverter()", generated);
    }

    [Fact]
    public void GoldenCustomJsonConverter_PropertyLevel_OverridesPerProperty()
    {
        // Phase 13.E.19: property-level [JsonConverter(typeof(X))] overrides
        // per-property in JsonPropertyInfoValues.Converter.
        var source = """
            using System;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using Marionette;

            namespace Demo;

            public sealed class HexInt32Converter : JsonConverter<int>
            {
                public override int Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => 0;
                public override void Write(Utf8JsonWriter w, int v, JsonSerializerOptions o) => w.WriteStringValue(v.ToString("X"));
            }

            public sealed class ColorEventArgs : EventArgs
            {
                [JsonConverter(typeof(HexInt32Converter))]
                public int Rgb { get; init; }

                public int Alpha { get; init; }
            }

            [McpRoot("colorroot")]
            public class ColorRoot
            {
                [McpEvent("Color update.")]
                public event EventHandler<ColorEventArgs>? Changed;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        // Rgb property has its converter overridden per-property; Alpha
        // stays on the default converter (Converter = null at property
        // level).
        Assert.Contains("Converter = new global::Demo.HexInt32Converter(),", generated);
        // Alpha must still appear with Converter = null.
        Assert.Contains("Converter = null,", generated);
    }

    [Fact]
    public void GoldenClosedGenericRoot_RegistersClosedInstantiation()
    {
        // Phase 13.E.15: [assembly: McpClosedRoot(typeof(Counter<int>))]
        // produces a manifest root for the closed generic, with the explicit
        // Name (or short type name as fallback). The open generic's [McpRoot]
        // attribute is silently skipped — closed-root pipeline takes over.
        var source = """
            using Marionette;

            [assembly: McpClosedRoot(typeof(Demo.Counter<int>), Name = "intCounter")]
            [assembly: McpClosedRoot(typeof(Demo.Counter<long>), Name = "longCounter")]

            namespace Demo;

            [McpRoot]
            public class Counter<T> where T : struct
            {
                [McpCallable("Bump.")]
                public T Bump() => default;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        // Both closed instantiations land as RootDescriptors with their
        // explicit names.
        Assert.Contains("Name: \"intCounter\"", generated);
        Assert.Contains("Name: \"longCounter\"", generated);
        // Each carries a `new Counter<X>()` factory. Roslyn's
        // FullyQualifiedFormat uses C# keyword aliases (int, long) for
        // primitives, so the closed-type rendering reflects that.
        Assert.Contains("new global::Demo.Counter<int>()", generated);
        Assert.Contains("new global::Demo.Counter<long>()", generated);
    }

    [Fact]
    public void GoldenGenericCallable_WithClosedTypes_EmitsPerInstantiation()
    {
        // Phase 13.E.16: a generic [McpCallable] with `ClosedTypes` produces
        // one descriptor per closed type. The descriptor name is mangled
        // (`Echo_Int32`); the call site uses the typed form
        // (`typed.Echo<int>(value)`).
        var source = """
            using Marionette;

            namespace Demo;

            [McpRoot("genroot")]
            public class GenericCallableRoot
            {
                [McpCallable("Echo a typed value.", ClosedTypes = new[] { typeof(int), typeof(string) })]
                public T Echo<T>(T value) => value;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        // Both manifest entries appear, mangled by closed-type short name.
        Assert.Contains("Name: \"Echo_int\"", generated);
        Assert.Contains("Name: \"Echo_string\"", generated);
        // Call sites carry the closed type-arg literal so C# binds the
        // right instantiation.
        Assert.Contains("typed.Echo<int>(value)", generated);
        Assert.Contains("typed.Echo<string>(value)", generated);
    }

    [Fact]
    public void GoldenGenericCallable_NoClosedTypes_EmitsMar017()
    {
        // Phase 13.E.16: generic [McpCallable] WITHOUT ClosedTypes is silently
        // skipped — the runtime cannot infer type arguments from a JSON arg
        // bag. MAR017 (Warning) surfaces the requirement to opt in via
        // ClosedTypes.
        var source = """
            using Marionette;

            namespace Demo;

            [McpRoot("genroot")]
            public class GenericCallableRoot
            {
                [McpCallable("Echo without closures.")]
                public T Echo<T>(T value) => value;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "MAR017");
        // No descriptor for the generic method.
        Assert.DoesNotContain("Name: \"Echo\"", result.GeneratedText);
    }

    [Fact]
    public void GoldenStreamParameter_BridgedViaBase64()
    {
        // Phase 13.E.18: Stream / MemoryStream params are no longer
        // blacklisted. The dispatcher decodes a base64 JSON string into a
        // fresh MemoryStream at call time.
        var source = """
            using System.IO;
            using Marionette;

            namespace Demo;

            [McpRoot("streamroot")]
            public class StreamRoot
            {
                [McpCallable("Hash a base64-encoded payload.")]
                public int CountBytes(Stream payload) { return (int)payload.Length; }

                [McpCallable("Same with concrete MemoryStream.")]
                public long ConcreteSize(MemoryStream payload) => payload.Length;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        // Both Stream and MemoryStream params hit the base64 wrap path.
        Assert.Contains("new global::System.IO.MemoryStream(global::System.Convert.FromBase64String(", generated);
        // Schema must use the byte format.
        Assert.Contains("\\\"format\\\":\\\"byte\\\"", generated);
        // The dispatcher still calls the user method with the local Stream.
        Assert.Contains("typed.CountBytes(payload)", generated);
        Assert.Contains("typed.ConcreteSize(payload)", generated);
    }

    [Fact]
    public void GoldenInParameter_AcceptedByValidator()
    {
        // Phase 13.E.17: `in` parameters are JSON-RPC-compatible (caller
        // supplies a value, callee sees a readonly reference). The validator
        // accepts them; ref/out remain refused.
        var source = """
            using Marionette;

            namespace Demo;

            public readonly struct BigStruct
            {
                public int A { get; init; }
                public int B { get; init; }
            }

            [McpRoot("inroot")]
            public class InRoot
            {
                [McpCallable("Adds the components of an in-passed struct.")]
                public int Sum(in BigStruct value) => value.A + value.B;

                [McpCallable("Plain by-value still works alongside in.")]
                public int Echo(int x) => x;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        Assert.Contains("Name: \"Sum\"", generated);
        Assert.Contains("typed.Sum(value)", generated);
    }

    [Fact]
    public void GoldenRefOutParameter_RefusedWithMar014()
    {
        // Sister test: ref/out remain refused (no JSON-RPC story).
        var source = """
            using Marionette;

            namespace Demo;

            [McpRoot("refroot")]
            public class RefRoot
            {
                [McpCallable("ref refused.")]
                public void Bump(ref int x) => x++;

                [McpCallable("out refused.")]
                public bool TryGet(out int x) { x = 0; return true; }
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        // Two MAR014 diagnostics expected (one per refused method).
        Assert.Equal(2, result.GeneratorDiagnostics.Count(d => d.Id == "MAR014"));
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n");

    private static string FormatDiagnostics(System.Collections.Generic.IEnumerable<Microsoft.CodeAnalysis.Diagnostic> diags)
        => string.Join("\n", diags.Select(d => $"  [{d.Severity}] {d.Id}: {d.GetMessage()}"));
}

internal static class Snapshot
{
    private static readonly string SnapshotDirectory = ResolveSnapshotDirectory();

    public static void Verify(string actual, string testName)
    {
        Directory.CreateDirectory(SnapshotDirectory);
        var receivedPath = Path.Combine(SnapshotDirectory, testName + ".received.txt");
        var verifiedPath = Path.Combine(SnapshotDirectory, testName + ".verified.txt");

        // Always write the .received.txt — useful diff target during local dev.
        File.WriteAllText(receivedPath, actual);

        if (!File.Exists(verifiedPath))
        {
            Assert.Fail(
                $"No verified snapshot found at {verifiedPath}.\n" +
                $"To bless this output, copy {receivedPath} to {verifiedPath}.");
            return;
        }

        var expected = File.ReadAllText(verifiedPath).Replace("\r\n", "\n");
        if (expected != actual)
        {
            Assert.Fail(
                $"Snapshot mismatch.\n" +
                $"Expected (verified): {verifiedPath}\n" +
                $"Actual (received):   {receivedPath}\n" +
                $"Diff first chars:\n{FirstDiffPreview(expected, actual)}");
        }
    }

    /// <summary>
    /// Find the snapshot directory next to the test's source files. The
    /// AppContext.BaseDirectory is the test bin/ folder; the project root is
    /// 4 levels up (bin/Debug/net10.0 → project root).
    /// </summary>
    private static string ResolveSnapshotDirectory()
    {
        // Walk up from BaseDirectory looking for a Snapshots/ sibling.
        var probe = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(probe!, "Snapshots");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(probe!)?.FullName;
            if (parent is null) break;
            probe = parent;
        }
        // Fallback: emit alongside the dll. CI will fail loudly if the
        // verified file isn't present — correct behaviour.
        return Path.Combine(AppContext.BaseDirectory, "Snapshots");
    }

    private static string FirstDiffPreview(string expected, string actual)
    {
        var min = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < min; i++)
        {
            if (expected[i] != actual[i])
            {
                int start = Math.Max(0, i - 50);
                int end = Math.Min(min, i + 50);
                return
                    $"  expected: ...{Escape(expected.Substring(start, end - start))}...\n" +
                    $"  actual:   ...{Escape(actual.Substring(start, end - start))}...";
            }
        }
        return $"  Lengths differ (expected={expected.Length}, actual={actual.Length}).";
    }

    private static string Escape(string s) =>
        s.Replace("\n", "\\n").Replace("\t", "\\t");
}
