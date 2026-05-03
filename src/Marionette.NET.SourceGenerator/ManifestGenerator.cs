// Marionette.NET — Roslyn Incremental Source Generator entry point
//
// This generator scans the user assembly for [McpRoot]-decorated classes and
// emits a single Marionette.g.cs with a static manifest the runtime consumes
// to register MCP tools/resources without runtime reflection.
//
// Pipeline shape:
//
//   Sources (incremental nodes)
//     A. ForAttributeWithMetadataName("Marionette.McpRootAttribute")
//          → (RootModel?, ImmutableArray<DiagnosticInfo>) per root class
//     B. ForAttributeWithMetadataName("Marionette.McpCallableAttribute")
//          → ImmutableArray<DiagnosticInfo> for MAR003 (callables on un-rooted classes)
//     C. CompilationProvider.Select(c => c.AssemblyName)
//          → string  (one per assembly, never re-runs unless the name changes)
//
//   Combine
//     A.Collect()  ─┐
//     B.Collect()  ─┤── ManifestModel (per-compilation aggregate)
//     C            ─┘
//
//   Output
//     RegisterSourceOutput → emit Marionette.g.cs + replay diagnostics
//
// Why ForAttributeWithMetadataName (FAWMN) and not the older SyntaxProvider
// .CreateSyntaxProvider:
//   * FAWMN was added in Roslyn 4.3 specifically to make attribute-driven
//     generators efficient — the host pre-filters by attribute presence
//     before our delegate runs, instead of asking us to walk every node.
//   * It gives us a GeneratorAttributeSyntaxContext with the matched
//     INamedTypeSymbol already resolved, no need to re-bind via SemanticModel.
//   * Cache invalidation is per-class, not per-syntax-tree.

using System;
using System.Collections.Immutable;
using System.Linq;
using Marionette.SourceGenerator.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Marionette.SourceGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class ManifestGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // ----- Source A: roots -----
        var rootCandidates = context.SyntaxProvider.ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: Validator.McpRootAttribute,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => TransformRoot(ctx))
            // Drop the (null, empty-diags) fallback for non-class targets.
            .Where(static r => r.HasValue)
            .Select(static (r, _) => r!.Value);

        // ----- Source B: orphan callables (MAR003) -----
        // A method with [McpCallable] whose declaring class lacks [McpRoot].
        var orphanCallables = context.SyntaxProvider.ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: Validator.McpCallableAttribute,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, _) => TransformOrphanCallable(ctx))
            .Where(static d => d is not null)
            .Select(static (d, _) => d!);

        // ----- Source C: assembly name -----
        var assemblyName = context.CompilationProvider
            .Select(static (c, _) => c.AssemblyName ?? "Unknown");

        // ----- Combine into a single ManifestModel -----
        var combined = rootCandidates.Collect()
            .Combine(orphanCallables.Collect())
            .Combine(assemblyName)
            .Select(static (tuple, _) =>
            {
                var rootsAndDiags = tuple.Left.Left;
                var orphans = tuple.Left.Right;
                var asmName = tuple.Right;

                var diags = ImmutableArray.CreateBuilder<DiagnosticInfo>();
                var roots = ImmutableArray.CreateBuilder<RootModel>();

                foreach (var (root, rootDiags) in rootsAndDiags)
                {
                    if (root is not null) roots.Add(root);
                    diags.AddRange(rootDiags.AsEnumerable());
                }
                foreach (var orphan in orphans)
                {
                    diags.Add(orphan);
                }

                return new ManifestModel(
                    AssemblyName: asmName,
                    Roots: roots.ToImmutable().ToEquatableArray(),
                    Diagnostics: diags.ToImmutable().ToEquatableArray());
            });

        // ----- Output -----
        context.RegisterSourceOutput(combined, static (spc, model) =>
        {
            // Replay diagnostics on every run so they show in the IDE.
            foreach (var d in model.Diagnostics.AsEnumerable())
            {
                spc.ReportDiagnostic(Validator.ToRoslynDiagnostic(d));
            }

            // Always emit Marionette.g.cs so the runtime's
            // `Marionette.Generated.GeneratedManifest` symbol is reachable
            // even when the user assembly has no [McpRoot] yet. The empty
            // manifest is harmless and avoids a brittle runtime fallback.
            var src = Emitter.EmitManifest(model);
            spc.AddSource("Marionette.g.cs", src);
        });
    }

    // -------------------------------------------------------------------------
    // Transform helpers — move out of the closure for cache stability.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Transform a [McpRoot]-attributed type into a (RootModel?, diagnostics)
    /// tuple. Returns null only when the syntax is malformed.
    /// </summary>
    private static (RootModel? Root, EquatableArray<DiagnosticInfo> Diags)? TransformRoot(
        GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol) return null;

        var root = Validator.ValidateRoot(typeSymbol, out var diags);
        return (root, diags.ToEquatableArray());
    }

    /// <summary>
    /// Detect [McpCallable] methods whose declaring class lacks [McpRoot]
    /// (MAR003 warning).
    /// </summary>
    private static DiagnosticInfo? TransformOrphanCallable(
        GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method) return null;
        var containing = method.ContainingType;
        if (containing is null) return null;

        // Already covered by the root pipeline if the class has [McpRoot].
        foreach (var attr in containing.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == Validator.McpRootAttribute) return null;
        }

        return Validator.MakeDiagnostic(
            Diagnostics.CallableOnUnrootedClass,
            method.Locations.FirstOrDefault(),
            method.ToDisplayString());
    }
}
