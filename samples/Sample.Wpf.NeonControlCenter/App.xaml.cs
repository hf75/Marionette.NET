// Sample.Wpf.NeonControlCenter — App shell
//
// Wires Marionette to the WPF Application using the same pattern as
// Sample.Wpf.TodoApp: in --mcp GUI mode, OnStartup substitutes the
// MissionControlViewModel root's Create factory with the singleton
// instance the MainWindow uses as DataContext, so [McpCallable] mutations
// flip values the user can see and user actions update observables the
// LLM reads.

using System.Windows;

#if MCP_ENABLED
using System.Collections.Generic;
using Marionette.Adapter.Wpf;
using Marionette.Generated;
using Marionette.Runtime.Manifest;
#endif

namespace Sample.Wpf.NeonControlCenter;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

#if MCP_ENABLED
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "MarionetteWpf.AttachTo cascades raise_event reflection warning; acknowledged at sample entry.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Cascading IL3050 from STJ.")]
#endif
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if MCP_ENABLED
        // Substitute the root's Create factory so the manifest-side instance
        // is the same singleton the MainWindow's DataContext uses.
        var bridgedRoots = new List<RootDescriptor>(GeneratedManifest.Roots.Count);
        foreach (var root in GeneratedManifest.Roots)
        {
            if (root.TypeName == typeof(MissionControlViewModel).FullName)
            {
                bridgedRoots.Add(root with { Create = static () => MissionControlViewModel.Shared });
            }
            else
            {
                bridgedRoots.Add(root);
            }
        }

        MarionetteWpf.AttachTo(this, bridgedRoots, e.Args);
#endif
    }
}
