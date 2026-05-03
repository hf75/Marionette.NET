// Marionette.NET — Abstractions
// Minimal sealed attribute set. These attributes are pure markup: they carry
// no behavior and never reach the runtime. The Source Generator (Phase 1)
// will read them at compile time to emit the manifest.

using System;

namespace Marionette;

/// <summary>
/// Marks a method as callable through the MCP automation surface.
/// Discovered at compile time; presence of this attribute does not pull in any runtime code.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpCallableAttribute : Attribute
{
    public McpCallableAttribute(string description)
    {
        Description = description;
    }

    /// <summary>Human-readable description shown to the LLM in tool listings.</summary>
    public string Description { get; }

    /// <summary>If true, the method may be invoked off the UI thread (no Dispatcher marshalling needed).</summary>
    public bool OffUiThread { get; set; }

    /// <summary>Per-call timeout in seconds. Zero or negative means runtime default.</summary>
    public int TimeoutSeconds { get; set; }
}

/// <summary>
/// Marks a property as observable via the MCP automation surface.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class McpObservableAttribute : Attribute
{
    public McpObservableAttribute(string description)
    {
        Description = description;
    }

    /// <summary>Human-readable description shown to the LLM.</summary>
    public string Description { get; }

    /// <summary>If true, exposed as an MCP resource that supports <c>resources/subscribe</c>.</summary>
    public bool Watchable { get; set; }
}

/// <summary>
/// Marks a method as triggerable from outside the app — the runtime may invoke it
/// in response to channel notifications or scheduled triggers.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpTriggerableAttribute : Attribute
{
    public McpTriggerableAttribute(string description)
    {
        Description = description;
    }

    /// <summary>Human-readable description.</summary>
    public string Description { get; }

    /// <summary>If true, the trigger may run off the UI thread.</summary>
    public bool OffUiThread { get; set; }
}

/// <summary>
/// Marks a class as a manifest root. Source generator scanning starts at types
/// decorated with this attribute, preventing accidental scanning of the entire AppDomain.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class McpRootAttribute : Attribute
{
    public McpRootAttribute(string description)
    {
        Description = description;
    }

    /// <summary>Human-readable description for the root.</summary>
    public string Description { get; }
}
