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
    public static GeneratorRunResult Run(string source, string assemblyName = "TestAssembly")
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

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
            MetadataReference.CreateFromFile(typeof(System.ComponentModel.Component).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.IReadOnlyDictionary<,>).Assembly.Location),
            // Marionette attributes.
            MetadataReference.CreateFromFile(typeof(global::Marionette.McpRootAttribute).Assembly.Location),
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
            .WithUpdatedParseOptions(new CSharpParseOptions(LanguageVersion.Latest));

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
