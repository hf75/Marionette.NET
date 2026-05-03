// Marionette.NET — WPF adapter (Phase 1.2 placeholder)
//
// Phase 1.2 owns the IUiAutomationAdapter contract; Phase 1.3 will provide
// the production WPF implementation (Dispatcher marshalling via
// Application.Current.Dispatcher, RenderTargetBitmap screenshot, visual-tree
// FindByName for triggerable controls).
//
// This file is intentionally a stub:
//   * It exists so the assembly remains non-trivial and the IL probe still
//     finds a unique symbol in non-stripped builds.
//   * It implements IUiAutomationAdapter so the sample's `--mcp` GUI path can
//     compile against the Phase 1.2 contract today, and Phase 1.3 only
//     replaces the method bodies.
//   * Phase 1.3 will rename / remove the static `Initialize` helper —
//     adopters never reference it; the conditional ProjectReference in
//     Sample.Wpf.StripeProbe.csproj is the only consumer.

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using Marionette.Runtime.Adapters;

namespace Marionette.Adapter.Wpf;

/// <summary>
/// Phase 1.2 WPF adapter stub. Implements <see cref="IUiAutomationAdapter"/>
/// with the same fall-through behaviour as <see cref="NoOpAdapter"/> so the
/// solution builds cleanly. Phase 1.3 replaces this with the real WPF
/// implementation.
/// </summary>
public sealed class WpfUiAutomationAdapter : IUiAutomationAdapter
{
    /// <summary>
    /// Construct an adapter bound to a specific WPF <see cref="Application"/>.
    /// Phase 1.3 will use this reference for Dispatcher marshalling and
    /// MainWindow resolution; Phase 1.2 keeps the reference but does nothing
    /// with it.
    /// </summary>
    /// <param name="app">The WPF application instance, or <see langword="null"/> if not yet started.</param>
    public WpfUiAutomationAdapter(Application? app)
    {
        Application = app;
    }

    /// <summary>The associated WPF <see cref="Application"/>, when one is available.</summary>
    public Application? Application { get; }

    /// <inheritdoc />
    public Task DispatchAsync(Action action, CancellationToken ct)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        ct.ThrowIfCancellationRequested();
        // Phase 1.3 will dispatch via Application.Current.Dispatcher.InvokeAsync.
        action();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<T> DispatchAsync<T>(Func<T> func, CancellationToken ct)
    {
        if (func is null) throw new ArgumentNullException(nameof(func));
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(func());
    }

    /// <inheritdoc />
    public Task<byte[]> CaptureScreenshotAsync(string? targetName, CancellationToken ct)
    {
        _ = targetName;
        _ = ct;
        // Phase 1.3 will use RenderTargetBitmap + PngBitmapEncoder.
        throw new NotSupportedException(
            "WpfUiAutomationAdapter.CaptureScreenshotAsync is not implemented in Phase 1.2. " +
            "Phase 1.3 replaces this method with a RenderTargetBitmap-based capture.");
    }

    /// <inheritdoc />
    public Task<object?> ResolveControlAsync(string rootName, string controlName, CancellationToken ct)
    {
        _ = rootName;
        _ = controlName;
        _ = ct;
        // Phase 1.3 will FindByName on the bound MainWindow's visual tree.
        return Task.FromResult<object?>(null);
    }
}

/// <summary>
/// Compatibility shim for the Spike-C-era bootstrap. Adopters never call
/// this directly today; it is retained so the IL probe still finds a unique
/// symbol in non-stripped builds.
/// </summary>
public static class WpfMarionetteBootstrap
{
    /// <summary>
    /// Compatibility shim. Phase 1.3 will replace this with a host-builder
    /// extension method that registers the WPF adapter into the runtime DI
    /// container.
    /// </summary>
    public static WpfUiAutomationAdapter CreateAdapter(Application? app) => new(app);
}
