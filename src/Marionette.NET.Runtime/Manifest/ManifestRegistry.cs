// Marionette.NET — runtime manifest registry
//
// Holds the source-generator-emitted RootDescriptor list plus the live
// instance for each root, keyed by manifest name. Phase 1.2 keeps the
// registry small and read-only after RunAsync hands it the roots; Phase 2
// will add live-mutation support for hot-plug roots.
//
// Lifetime: registered as a singleton. The registry's `Build` call is the
// only mutation point and runs once before the MCP server starts.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Runtime.Manifest;

/// <summary>
/// In-memory store of every <see cref="RootDescriptor"/> the runtime knows
/// about, plus the per-root instance the runtime dispatches to. Built once
/// at host startup from the source-generator's
/// <c>Marionette.Generated.GeneratedManifest.Roots</c> array.
/// </summary>
public sealed class ManifestRegistry
{
    private readonly Dictionary<string, RegisteredRoot> _byName = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes the registry from the source-generated descriptor list.
    /// </summary>
    /// <param name="roots">The descriptor array from the user assembly's
    /// <c>Marionette.Generated.GeneratedManifest.Roots</c>.</param>
    /// <remarks>
    /// Each descriptor's <c>Create</c> factory is invoked once to materialise
    /// the root instance. Roots without a factory (the generator emits
    /// <see langword="null"/> when no public parameterless ctor exists) are
    /// registered with a null instance — the runtime surfaces an actionable
    /// error to the LLM rather than crashing when one is referenced.
    /// </remarks>
    public ManifestRegistry(IReadOnlyList<RootDescriptor> roots)
    {
        if (roots is null) throw new ArgumentNullException(nameof(roots));

        foreach (var root in roots)
        {
            // Skip duplicates silently — the generator should have prevented
            // them, but defensive registration keeps the registry stable in
            // future incremental scenarios (multi-assembly merge in Phase 2+).
            if (_byName.ContainsKey(root.Name)) continue;

            object? instance = null;
            string? createError = null;
            if (root.Create is not null)
            {
                try
                {
                    instance = root.Create();
                }
                catch (Exception ex)
                {
                    // Don't kill startup. The error is captured and surfaced
                    // when a tool tries to dispatch against this root.
                    createError = ex.Message;
                }
            }

            _byName[root.Name] = new RegisteredRoot(root, instance, createError);
        }
    }

    /// <summary>
    /// Replace the live instance for an existing root (e.g. when the WPF
    /// adapter binds to <c>Application.MainWindow</c> after the window has
    /// loaded). Phase 1.2 only uses this from the WPF adapter's bootstrap;
    /// Phase 2 may add MainWindow-changed listeners.
    /// </summary>
    public void BindInstance(string rootName, object instance)
    {
        if (rootName is null) throw new ArgumentNullException(nameof(rootName));
        if (instance is null) throw new ArgumentNullException(nameof(instance));

        if (_byName.TryGetValue(rootName, out var existing))
        {
            _byName[rootName] = existing with { Instance = instance, CreateError = null };
        }
    }

    /// <summary>All registered roots, in source-generator order.</summary>
    public IReadOnlyList<RegisteredRoot> Roots => _byName.Values.ToList();

    /// <summary>
    /// Look up a root by manifest name. Returns <see langword="null"/> when
    /// the name is unknown.
    /// </summary>
    public RegisteredRoot? Find(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return _byName.TryGetValue(name, out var r) ? r : null;
    }
}

/// <summary>
/// One entry in the <see cref="ManifestRegistry"/>: the source-gen descriptor
/// plus the live root instance plus an optional error captured during
/// instance creation.
/// </summary>
/// <param name="Descriptor">The source-gen descriptor.</param>
/// <param name="Instance">The materialised root instance, or <see langword="null"/> when not available.</param>
/// <param name="CreateError">
/// Error message captured if <see cref="RootDescriptor.Create"/> threw during
/// host startup. The runtime surfaces this in a structured tool error rather
/// than crashing.
/// </param>
public sealed record RegisteredRoot(
    RootDescriptor Descriptor,
    object? Instance,
    string? CreateError);
