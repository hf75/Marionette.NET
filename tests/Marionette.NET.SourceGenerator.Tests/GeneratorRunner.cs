// Marionette.NET — generator test harness
//
// Lightweight in-memory runner that drives the source generator over a string
// of C# source. Returns:
//   * the generated output text (or "" if no output),
//   * the list of Roslyn diagnostics produced by the generator,
//   * compilation diagnostics (so we catch malformed generated code).
//
// We deliberately avoid pulling in Verify / Microsoft.CodeAnalysis.CSharp.Testing.XUnit
// to keep the snapshot mechanism transparent: the test writes a `.received.txt`
// next to the test file; CI compares against `.verified.txt` byte-for-byte.

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Marionette.SourceGenerator.Tests;

internal static class GeneratorRunner
{
    /// <summary>
    /// Runs the Marionette ManifestGenerator over the given C# source string.
    /// </summary>
    /// <param name="source">The C# source code to feed into the generator.</param>
    /// <param name="assemblyName">Logical assembly name for the synthetic compilation.</param>
    /// <param name="mcpEnabled">
    /// Phase 1.2: emission is gated on the MCP_ENABLED preprocessor symbol.
    /// Tests default to <c>true</c> (the typical adopter Debug build) because
    /// that's what the snapshot/diagnostic tests exercise. Set <c>false</c>
    /// to verify the no-emit-on-stripped-build path.
    /// </param>
    public static GeneratorRunResult Run(string source, string assemblyName = "TestAssembly", bool mcpEnabled = true)
    {
        // Phase 1.2: thread MCP_ENABLED through the parse options so the
        // generator's gate fires correctly. Without this, the generator
        // bails out (correct stripped behaviour) and the snapshot/diag tests
        // can't observe its output.
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        if (mcpEnabled)
        {
            parseOptions = parseOptions.WithPreprocessorSymbols("MCP_ENABLED");
        }

        var syntaxTree = CSharpSyntaxTree.ParseText(source, options: parseOptions);

        // Build the reference set: every assembly we expect the user code to
        // be able to talk about. Marionette.NET.Abstractions is the critical
        // one — without it the generator wouldn't see [McpRoot] etc. We keep
        // it minimal so test compilations are fast.
        var references = new[]
        {
            // Core BCL surface — System.Runtime / netstandard / mscorlib.
            // RuntimeMetadataReference is the canonical way to grab them.
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonSerializer).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.ComponentModel.Component).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.IReadOnlyDictionary<,>).Assembly.Location),
            // Marionette attributes.
            MetadataReference.CreateFromFile(typeof(global::Marionette.McpRootAttribute).Assembly.Location),
            // Phase 1.2: the generated Marionette.g.cs references descriptor
            // record types from Marionette.Runtime.Manifest. The synthetic test
            // compilation that consumes the generated source needs Runtime on
            // its reference list to resolve them — otherwise the post-gen
            // compilation diagnostics flag `RootDescriptor` etc. as missing.
            MetadataReference.CreateFromFile(typeof(global::Marionette.Runtime.Manifest.RootDescriptor).Assembly.Location),
        };

        // The implicit references for .NET 10 — System.Runtime + others. The
        // simplest path is to grab the netstandard reference assemblies that
        // ship with the SDK; failing that, we walk the loaded AppDomain.
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator)
            ?? Array.Empty<string>();
        var extraRefs = trustedAssemblies
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(p =>
            {
                var name = Path.GetFileNameWithoutExtension(p);
                return name == "System.Runtime" || name == "netstandard" || name == "System.Collections";
            })
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: new[] { syntaxTree },
            references: references.Concat(extraRefs),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var generator = new ManifestGenerator();
        var driver = CSharpGeneratorDriver
            .Create(generator)
            .WithUpdatedParseOptions(parseOptions);

        var updatedDriver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics);

        // Locate the generated tree (filename Marionette.g.cs by convention —
        // see Emitter.AddSource call site in ManifestGenerator).
        var runResult = updatedDriver.GetRunResult();
        var generatedSources = runResult.Results
            .SelectMany(r => r.GeneratedSources)
            .ToImmutableArray();

        var generatedText = generatedSources.Length > 0
            ? string.Join("\n", generatedSources.Select(s => s.SourceText.ToString()))
            : string.Empty;

        // Capture compilation diagnostics from the OUTPUT compilation
        // (post-generation). Errors here mean the generator emitted invalid
        // C# — a critical regression.
        var compilationDiagnostics = outputCompilation.GetDiagnostics();

        return new GeneratorRunResult(
            GeneratedText: generatedText,
            GeneratorDiagnostics: generatorDiagnostics.ToImmutableArray(),
            CompilationDiagnostics: compilationDiagnostics);
    }
}

internal sealed record GeneratorRunResult(
    string GeneratedText,
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<Diagnostic> CompilationDiagnostics)
{
    public bool HasGeneratorErrors => GeneratorDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    public bool HasGeneratorWarnings => GeneratorDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning);
    public bool HasCompilationErrors => CompilationDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public ImmutableArray<Diagnostic> GeneratorErrors => GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
    public ImmutableArray<Diagnostic> GeneratorWarnings => GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToImmutableArray();
    public ImmutableArray<Diagnostic> GeneratorInfos => GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Info).ToImmutableArray();
}
