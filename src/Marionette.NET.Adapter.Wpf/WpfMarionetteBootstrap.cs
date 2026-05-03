// Marionette.NET — WPF adapter (Spike A stub)
// Replaces the eventual Dispatcher-marshalling, visual-tree-walker, and
// FindByName implementation. Spike A only needs a unique symbol so the
// IL probe can confirm none of it leaks into a stripped Release build.

using System;
using System.Windows;

namespace Marionette.Adapter.Wpf;

/// <summary>
/// Hook the WPF Application object into the Marionette runtime. Spike A stub —
/// just emits a stderr breadcrumb so the assembly is non-trivial.
/// </summary>
public static class WpfMarionetteBootstrap
{
    /// <summary>
    /// Initialize the WPF adapter against the given <see cref="Application"/>.
    /// Phase 1 will install a Dispatcher hook + DispatcherUnhandledException handler.
    /// </summary>
    public static void Initialize(Application app)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        Console.Error.WriteLine("Marionette WPF adapter initialized");
    }
}
