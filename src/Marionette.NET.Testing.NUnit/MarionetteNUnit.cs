using System;
using System.Collections.Generic;

using Marionette.Runtime.Manifest;

using NUnit.Framework;

namespace Marionette.Testing.NUnit;

/// <summary>
/// NUnit helpers over the neutral Marionette test host.
/// </summary>
public static class MarionetteNUnit
{
    /// <summary>
    /// Environment variable that enables GUI-backed Marionette test cases.
    /// </summary>
    public const string GuiTestsEnvironmentVariable = "MARIONETTE_GUI_TESTS";

    /// <summary>
    /// Create a neutral in-process Marionette test host.
    /// </summary>
    public static MarionetteTestHost CreateHost(
        IReadOnlyList<RootDescriptor> roots,
        MarionetteTestHostOptions? options = null)
        => MarionetteTestHost.Create(roots, options);

    /// <summary>
    /// Returns true when GUI-backed tests should execute.
    /// </summary>
    public static bool IsGuiTestingEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable(GuiTestsEnvironmentVariable),
            "1",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Mark the current NUnit test ignored unless
    /// <c>MARIONETTE_GUI_TESTS=1</c>.
    /// </summary>
    public static void RequireGuiTestingEnabled()
    {
        if (!IsGuiTestingEnabled())
        {
            Assert.Ignore("Set MARIONETTE_GUI_TESTS=1 to run Marionette GUI adapter tests.");
        }
    }
}
