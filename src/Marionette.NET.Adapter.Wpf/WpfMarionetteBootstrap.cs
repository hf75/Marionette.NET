// Marionette.NET — WPF adapter compatibility shim
//
// Phase 1.3 split the original Phase-1.2 stub file into:
//
//   * `WpfUiAutomationAdapter.cs` — the real `IUiAutomationAdapter` impl.
//   * `MarionetteWpf.cs`          — the `AttachTo(Application, …)` entry point.
//   * `Internal/VisualTreeFinder.cs` — the named-element resolver helper.
//
// This file is retained because:
//
//   1. `Sample.Wpf.StripeProbe.csproj` references the assembly conditionally
//      and the Phase 1.2 hand-off documented `WpfMarionetteBootstrap.CreateAdapter`
//      as the public seam. We keep the API as an alias around the new entry
//      points so anyone (skill-pack, future docs) reading the Phase 1.2 spec
//      still finds it. Phase 2 may collapse it.
//   2. The IL probe needs at least one unique top-level symbol in the adapter
//      assembly to detect a regression in stripped Release builds; this static
//      class provides one (`Marionette.Adapter.Wpf.WpfMarionetteBootstrap`).

using System.Windows;

using Marionette.Runtime.Adapters;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marionette.Adapter.Wpf;

/// <summary>
/// Compatibility shim that exposes the Phase 1.2 <c>CreateAdapter</c> seam.
/// New adopters should call <see cref="MarionetteWpf.AttachTo"/> instead —
/// <c>AttachTo</c> handles host startup, lifecycle hooks, and shutdown.
/// </summary>
public static class WpfMarionetteBootstrap
{
    /// <summary>
    /// Construct a <see cref="WpfUiAutomationAdapter"/> bound to the supplied
    /// <see cref="Application"/>. Useful when an adopter wants to manage the
    /// Marionette host lifecycle by hand (e.g. integration tests) rather than
    /// going through <see cref="MarionetteWpf.AttachTo"/>.
    /// </summary>
    /// <param name="app">The WPF application instance. Must not be null.</param>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger{T}.Instance"/>.</param>
    /// <returns>A live adapter that can be passed to <see cref="MarionetteHost.RunAsync(string[], System.Collections.Generic.IReadOnlyList{Runtime.Manifest.RootDescriptor}, IUiAutomationAdapter?, System.Threading.CancellationToken)"/>.</returns>
    public static IUiAutomationAdapter CreateAdapter(
        Application app,
        ILogger<WpfUiAutomationAdapter>? logger = null)
        => new WpfUiAutomationAdapter(app, logger ?? NullLogger<WpfUiAutomationAdapter>.Instance);
}
