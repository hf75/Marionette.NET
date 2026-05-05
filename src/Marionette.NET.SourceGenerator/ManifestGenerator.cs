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
using Microsoft.CodeAnalysis.CSharp;
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

        // ----- Source B2: orphan events (MAR011) -----
        // An event with [McpEvent] whose declaring class lacks [McpRoot].
        // Mirrors the orphan-callable shape; ForAttributeWithMetadataName works
        // against EventFieldDeclarationSyntax (field-style events) and
        // EventDeclarationSyntax (custom add/remove); we accept both.
        var orphanEvents = context.SyntaxProvider.ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: Validator.McpEventAttribute,
                predicate: static (node, _) =>
                    node is EventFieldDeclarationSyntax ||
                    node is EventDeclarationSyntax ||
                    node is VariableDeclaratorSyntax,
                transform: static (ctx, _) => TransformOrphanEvent(ctx))
            .Where(static d => d is not null)
            .Select(static (d, _) => d!);

        // ----- Source C: assembly name -----
        var assemblyName = context.CompilationProvider
            .Select(static (c, _) => c.AssemblyName ?? "Unknown");

        // ----- Source D: MCP_ENABLED gate -----
        // Phase 1.2 requirement: when the user csproj does not define
        // MCP_ENABLED (i.e. EnableMcpAutomation=false in build/Marionette.NET.targets),
        // the generator emits NOTHING. This keeps stripped Release builds at
        // zero generator output and preserves the IL-stripping promise from
        // Phase 0 Spike A. Diagnostics still flow because they are useful in
        // any configuration; only the .g.cs source emission is gated.
        var mcpEnabled = context.ParseOptionsProvider
            .Select(static (po, _) =>
                po is CSharpParseOptions csOpts &&
                csOpts.PreprocessorSymbolNames.Contains("MCP_ENABLED"));

        // ----- Source E: [assembly: McpRaisable] catalog -----
        // Phase 12.2: scan the compilation's assembly-level attributes for
        // McpRaisableAttribute and validate each (Type, EventName) pair. Use
        // CompilationProvider rather than ForAttributeWithMetadataName because
        // FAWMN doesn't surface assembly-target attributes — only attributes
        // attached to declarations (types, methods, properties) appear there.
        // The CompilationProvider re-runs whenever assembly attributes change,
        // which is exactly the cache invalidation we want.
        var raisableEntries = context.CompilationProvider
            .Select(static (compilation, _) =>
            {
                var diags = ImmutableArray.CreateBuilder<DiagnosticInfo>();
                var entries = ImmutableArray.CreateBuilder<RaisableEventModel>();
                var frameworkSet = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
                var frameworkOrdered = ImmutableArray.CreateBuilder<string>();

                foreach (var attr in compilation.Assembly.GetAttributes())
                {
                    if (attr.AttributeClass?.ToDisplayString() != Validator.McpRaisableAttribute) continue;
                    if (attr.ConstructorArguments.Length != 2) continue;

                    if (attr.ConstructorArguments[0].Value is not ITypeSymbol typeArg) continue;
                    if (attr.ConstructorArguments[1].Value is not string eventName ||
                        string.IsNullOrEmpty(eventName)) continue;

                    var loc = attr.ApplicationSyntaxReference?.GetSyntax().GetLocation();
                    var entry = RaisableEventValidator.Validate(typeArg, eventName, loc, diags);
                    if (entry is null) continue;

                    entries.Add(entry);
                    if (frameworkSet.Add(entry.Framework)) frameworkOrdered.Add(entry.Framework);
                }

                return (
                    Entries: entries.ToImmutable().ToEquatableArray(),
                    Frameworks: frameworkOrdered.ToImmutable().ToEquatableArray(),
                    Diagnostics: diags.ToImmutable().ToEquatableArray());
            });

        // ----- Source F: [assembly: McpClosedRoot] -----
        // Phase 13.E.15: scan assembly-level McpClosedRoot attributes and
        // emit one RootModel per closed generic instantiation. Same
        // CompilationProvider pattern as raisableEntries.
        var closedRoots = context.CompilationProvider
            .Select(static (compilation, _) =>
            {
                var diags = ImmutableArray.CreateBuilder<DiagnosticInfo>();
                var roots = ImmutableArray.CreateBuilder<RootModel>();

                foreach (var attr in compilation.Assembly.GetAttributes())
                {
                    if (attr.AttributeClass?.ToDisplayString() != Validator.McpClosedRootAttribute) continue;
                    if (attr.ConstructorArguments.Length != 1) continue;
                    if (attr.ConstructorArguments[0].Value is not INamedTypeSymbol typeArg) continue;

                    string? explicitName = null;
                    foreach (var na in attr.NamedArguments)
                    {
                        if (na.Key == "Name" && na.Value.Value is string s)
                        {
                            explicitName = s;
                            break;
                        }
                    }

                    var loc = attr.ApplicationSyntaxReference?.GetSyntax().GetLocation();
                    var model = Validator.ValidateClosedGenericRoot(typeArg, explicitName, loc, out var rootDiags);
                    if (model is not null) roots.Add(model);
                    foreach (var d in rootDiags) diags.Add(d);
                }

                return (
                    Roots: roots.ToImmutable().ToEquatableArray(),
                    Diagnostics: diags.ToImmutable().ToEquatableArray());
            });

        // ----- Combine into a single ManifestModel -----
        var combined = rootCandidates.Collect()
            .Combine(orphanCallables.Collect())
            .Combine(orphanEvents.Collect())
            .Combine(assemblyName)
            .Combine(mcpEnabled)
            .Combine(raisableEntries)
            .Combine(closedRoots)
            .Select(static (tuple, _) =>
            {
                var rootsAndDiags = tuple.Left.Left.Left.Left.Left.Left;
                var orphans = tuple.Left.Left.Left.Left.Left.Right;
                var orphanEvts = tuple.Left.Left.Left.Left.Right;
                var asmName = tuple.Left.Left.Left.Right;
                var mcpOn = tuple.Left.Left.Right;
                var raisable = tuple.Left.Right;
                var closedRoot = tuple.Right;

                var diags = ImmutableArray.CreateBuilder<DiagnosticInfo>();
                var roots = ImmutableArray.CreateBuilder<RootModel>();

                foreach (var (root, rootDiags) in rootsAndDiags)
                {
                    if (root is not null) roots.Add(root);
                    diags.AddRange(rootDiags.AsEnumerable());
                }
                // Phase 13.E.15: closed generic roots flow in alongside the
                // FAWMN-discovered ones. They're full RootModels — they carry
                // their own callables / observables / events — and the JSON
                // type union below picks them up like any other root.
                foreach (var r in closedRoot.Roots.AsEnumerable())
                {
                    roots.Add(r);
                }
                foreach (var d in closedRoot.Diagnostics.AsEnumerable())
                {
                    diags.Add(d);
                }
                foreach (var orphan in orphans)
                {
                    diags.Add(orphan);
                }
                foreach (var orphan in orphanEvts)
                {
                    diags.Add(orphan);
                }

                // Phase 12.2: replay raisable validation diagnostics
                // (MAR015) at the same emit-stage as the rest.
                foreach (var d in raisable.Diagnostics.AsEnumerable())
                {
                    diags.Add(d);
                }

                // Phase 8.1/8.2: union the per-root JSON-type sets into two
                // assembly-wide collections — one per generated context.
                // Deduplicate by encoded property name; ordered alphabetically
                // so the generated source is deterministic and snapshot tests
                // stay stable.
                var allArgsTypes = new System.Collections.Generic.SortedDictionary<string, JsonTypeModel>(System.StringComparer.Ordinal);
                var argsRootNames = ImmutableArray.CreateBuilder<string>();
                var argsRootSeen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

                var allValueTypes = new System.Collections.Generic.SortedDictionary<string, JsonTypeModel>(System.StringComparer.Ordinal);
                var valueRootNames = ImmutableArray.CreateBuilder<string>();
                var valueRootSeen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

                foreach (var root in roots)
                {
                    foreach (var jt in root.EventArgsJsonTypes.AsEnumerable())
                    {
                        if (!allArgsTypes.ContainsKey(jt.PropertyName))
                            allArgsTypes[jt.PropertyName] = jt;
                    }
                    foreach (var ev in root.Events.AsEnumerable())
                    {
                        if (ev.JsonRootContextName is { } name && argsRootSeen.Add(name))
                            argsRootNames.Add(name);
                    }

                    foreach (var jt in root.ValueJsonTypes.AsEnumerable())
                    {
                        if (!allValueTypes.ContainsKey(jt.PropertyName))
                            allValueTypes[jt.PropertyName] = jt;
                    }
                    foreach (var obs in root.Observables.AsEnumerable())
                    {
                        if (obs.JsonValueContextName is { } name && valueRootSeen.Add(name))
                            valueRootNames.Add(name);
                    }
                    foreach (var call in root.Callables.AsEnumerable())
                    {
                        if (call.JsonReturnContextName is { } name && valueRootSeen.Add(name))
                            valueRootNames.Add(name);
                    }
                }

                return (Model: new ManifestModel(
                            AssemblyName: asmName,
                            Roots: roots.ToImmutable().ToEquatableArray(),
                            Diagnostics: diags.ToImmutable().ToEquatableArray(),
                            EventArgsJsonTypes: allArgsTypes.Values.ToImmutableArray().ToEquatableArray(),
                            EventArgsRootTypes: argsRootNames.ToImmutable().ToEquatableArray(),
                            ValueJsonTypes: allValueTypes.Values.ToImmutableArray().ToEquatableArray(),
                            ValueRootTypes: valueRootNames.ToImmutable().ToEquatableArray(),
                            RaisableEvents: raisable.Entries,
                            RaisableFrameworks: raisable.Frameworks),
                        McpEnabled: mcpOn);
            });

        // ----- Output -----
        context.RegisterSourceOutput(combined, static (spc, tuple) =>
        {
            var model = tuple.Model;
            var mcpEnabled = tuple.McpEnabled;

            // Replay diagnostics on every run so they show in the IDE
            // regardless of MCP_ENABLED — adopters need to see MAR001/002/…
            // squigglies even when the runtime is stripped out.
            foreach (var d in model.Diagnostics.AsEnumerable())
            {
                spc.ReportDiagnostic(Validator.ToRoslynDiagnostic(d));
            }

            // Phase 1.2 gate: only emit Marionette.g.cs when the user csproj
            // has defined MCP_ENABLED (typically via
            // build/Marionette.NET.targets when EnableMcpAutomation=true). In
            // stripped builds the runtime is not referenced, the descriptor
            // types are not reachable, and emitting them here would fail to
            // compile. Skipping the emission entirely keeps stripped builds
            // at zero generator output and preserves Phase 0's IL-stripping
            // promise.
            if (!mcpEnabled) return;

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

    /// <summary>
    /// Detect [McpEvent] events whose declaring class lacks [McpRoot]
    /// (MAR011 warning) — mirror of <see cref="TransformOrphanCallable"/>.
    /// </summary>
    private static DiagnosticInfo? TransformOrphanEvent(
        GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IEventSymbol ev) return null;
        var containing = ev.ContainingType;
        if (containing is null) return null;

        foreach (var attr in containing.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == Validator.McpRootAttribute) return null;
        }

        return Validator.MakeDiagnostic(
            Diagnostics.McpEventOnUnrootedClass,
            ev.Locations.FirstOrDefault(),
            ev.ToDisplayString());
    }
}
