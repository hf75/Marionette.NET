using System;
using System.Collections.Generic;

using Marionette.Runtime.Manifest;

using Xunit;

namespace Marionette.Testing.Xunit;

/// <summary>
/// xUnit helpers over the neutral Marionette test host.
/// </summary>
public static class MarionetteXunit
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
}

/// <summary>
/// xUnit fact that is skipped unless <c>MARIONETTE_GUI_TESTS=1</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class MarionetteGuiFactAttribute : FactAttribute
{
    public MarionetteGuiFactAttribute()
    {
        if (!MarionetteXunit.IsGuiTestingEnabled())
        {
            Skip = "Set MARIONETTE_GUI_TESTS=1 to run Marionette GUI adapter tests.";
        }
    }
}

/// <summary>
/// xUnit theory that is skipped unless <c>MARIONETTE_GUI_TESTS=1</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class MarionetteGuiTheoryAttribute : TheoryAttribute
{
    public MarionetteGuiTheoryAttribute()
    {
        if (!MarionetteXunit.IsGuiTestingEnabled())
        {
            Skip = "Set MARIONETTE_GUI_TESTS=1 to run Marionette GUI adapter tests.";
        }
    }
}
