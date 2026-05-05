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
        // Non-string dictionary keys: int + enum.
        Assert.Contains("Dictionary<global::System.Int32, global::System.String>", generated);
        Assert.Contains("Dictionary<global::Demo.Severity, global::System.Int32>", generated);
        // ObjectCreator should pick HashSet for set-like interfaces.
        Assert.Contains("new global::System.Collections.Generic.HashSet<global::System.String>()", generated);
    }

    [Fact]
    public void GoldenUnsupportedShapes_FallsBackToReflection()
    {
        // Phase 8.5: types the generator deliberately does NOT cover (multi-
        // dim arrays, abstract bases, exotic generics) leave the descriptor's
        // typed Serialize lambda null. The runtime then takes the legacy
        // reflection path. This fixture verifies that unsupported types do
        // not appear as JsonTypeInfo properties on the emitted context.
        var source = """
            using System;
            using Marionette;

            namespace Demo;

            public sealed class UnsupportedEventArgs : EventArgs
            {
                public UnsupportedEventArgs() { }
                public int[,] MultiDimArray { get; init; } = new int[0,0];
            }

            [McpRoot("unsupported")]
            public class UnsupportedRoot
            {
                [McpEvent("Args type the generator cannot register.")]
                public event EventHandler<UnsupportedEventArgs>? Fired;
            }
            """;

        var result = GeneratorRunner.Run(source, assemblyName: "Demo");

        Assert.False(result.HasGeneratorErrors,
            $"Generator emitted errors:\n{FormatDiagnostics(result.GeneratorDiagnostics)}");
        Assert.False(result.HasCompilationErrors,
            $"Compilation of generated code failed:\n{FormatDiagnostics(result.CompilationDiagnostics)}");

        var generated = result.GeneratedText;
        // The MarionetteEventArgsJsonContext should NOT register the
        // unsupported type — the descriptor's SerializeArgs stays null and
        // runtime serialisation handles it.
        Assert.DoesNotContain("Demo_UnsupportedEventArgs", generated);
        // The descriptor must still emit (events surface in the manifest);
        // the assertion is just that it did not get a JSON-context entry.
        Assert.Contains("\"Fired\"", generated);
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
