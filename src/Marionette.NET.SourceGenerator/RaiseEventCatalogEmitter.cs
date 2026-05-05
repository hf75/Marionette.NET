// Marionette.NET — Phase 12.2 raise_event AOT catalog emitter
//
// Renders `Marionette.Generated.RaiseEventCatalog.TryRaise` — a typed
// dispatcher built from the assembly's `[McpRaisable(typeof(T), "Name")]`
// declarations. The runtime adapter calls
// `RaiseEventCatalog.TryRaise(control, eventName, args)` before falling back
// to its reflection-based path; the generator-emitted code keeps the static
// `<Name>Event` field reference alive into the trimmed binary.
//
// Emission shape:
//
//   namespace Marionette.Generated;
//
//   public static class RaiseEventCatalog
//   {
//       public static bool TryRaise(object control, string eventName, object? args)
//       {
//           switch (control)
//           {
//               case global::App.MyButton mb when eventName == "Click":
//                   ((global::System.Windows.UIElement)mb).RaiseEvent(
//                       args as global::System.Windows.RoutedEventArgs ??
//                       new global::System.Windows.RoutedEventArgs(
//                           global::App.MyButton.ClickEvent, mb));
//                   return true;
//               ...
//           }
//           return false;
//       }
//   }
//
// And a per-assembly module initializer that auto-registers the dispatcher
// with the runtime registry, so adopters don't need to call `Register` by
// hand. The initializer is gated on MCP_ENABLED via the same emit-skip the
// rest of the manifest uses.

using System.Collections.Generic;
using System.Text;
using Marionette.SourceGenerator.Model;

namespace Marionette.SourceGenerator;

internal static class RaiseEventCatalogEmitter
{
    /// <summary>
    /// Append the catalog class to <paramref name="sb"/>. No-op when
    /// <paramref name="entries"/> is empty — the runtime registry stays at
    /// its <c>null</c> default, callers fall through to reflection.
    /// </summary>
    public static void Emit(StringBuilder sb, IReadOnlyList<RaisableEventModel> entries)
    {
        if (entries is null || entries.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Phase 12.2: AOT-clean raise_event dispatcher generated from");
        sb.AppendLine("/// <c>[assembly: McpRaisable(typeof(T), \"Name\")]</c> declarations.");
        sb.AppendLine("/// The Marionette runtime calls <see cref=\"TryRaise\"/> from the");
        sb.AppendLine("/// adapter's <c>raise_event</c> path before falling back to reflection;");
        sb.AppendLine("/// each switch arm preserves a static-field reference to");
        sb.AppendLine("/// <c>&lt;Type&gt;.&lt;Name&gt;Event</c> so trimming/AOT keeps the metadata alive.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class RaiseEventCatalog");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Typed dispatcher for the assembly's <c>[McpRaisable]</c> set.");
        sb.AppendLine("    /// Returns <see langword=\"true\"/> when the (control, eventName) pair");
        sb.AppendLine("    /// matched a declaration and the framework's <c>RaiseEvent</c> was called;");
        sb.AppendLine("    /// <see langword=\"false\"/> otherwise.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static bool TryRaise(object control, string eventName, object? args)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (control is null) return false;");
        sb.AppendLine("        switch (control)");
        sb.AppendLine("        {");

        // Group by control type to produce one `case <Type> typed:` arm with
        // an inner `switch` over the eventName. Saves one cast per type.
        var byControl = new Dictionary<string, List<RaisableEventModel>>(System.StringComparer.Ordinal);
        var orderedControls = new List<string>();
        foreach (var entry in entries)
        {
            if (!byControl.TryGetValue(entry.ControlTypeFullName, out var list))
            {
                list = new List<RaisableEventModel>();
                byControl[entry.ControlTypeFullName] = list;
                orderedControls.Add(entry.ControlTypeFullName);
            }
            list.Add(entry);
        }

        foreach (var controlFq in orderedControls)
        {
            var bucket = byControl[controlFq];
            sb.Append("            case ").Append(controlFq).AppendLine(" typed:");
            sb.AppendLine("                switch (eventName)");
            sb.AppendLine("                {");
            foreach (var e in bucket)
            {
                sb.Append("                    case \"").Append(EscapeStringLiteral(e.EventName)).AppendLine("\":");
                // ((<RaiseEventTypeFullName>)typed).RaiseEvent(
                //     args as <RoutedEventArgsFullName> ??
                //     new <RoutedEventArgsFullName>(<DeclaringType>.<Name>Event, typed));
                sb.Append("                        ((").Append(e.RaiseEventTypeFullName).AppendLine(")typed).RaiseEvent(");
                sb.Append("                            args as ").Append(e.RoutedEventArgsFullName).AppendLine(" ??");
                sb.Append("                            new ").Append(e.RoutedEventArgsFullName).Append('(')
                    .Append(e.DeclaringTypeFullName).Append('.').Append(e.EventName).AppendLine("Event, typed));");
                sb.AppendLine("                        return true;");
            }
            sb.AppendLine("                }");
            sb.AppendLine("                break;");
        }

        sb.AppendLine("        }");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        // Auto-registration via a module initializer. The runtime registry
        // (Marionette.Runtime.Adapters.RaiseEventCatalog.Register) is a
        // process-wide pointer; calling it from a module initializer ensures
        // the typed dispatcher is wired before any adapter call.
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Phase 12.2: module initializer that wires the generated typed");
        sb.AppendLine("/// dispatcher into the runtime registry on assembly load. Adopters do");
        sb.AppendLine("/// not need to call <c>Register</c> by hand — the initializer fires");
        sb.AppendLine("/// before <c>MarionetteWpf.AttachTo</c> / <c>MarionetteAvalonia.AttachTo</c>");
        sb.AppendLine("/// can run user code.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("internal static class RaiseEventCatalogModuleInit");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Init()");
        sb.AppendLine("    {");
        sb.AppendLine("        global::Marionette.Runtime.Adapters.RaiseEventCatalog.Register(");
        sb.AppendLine("            global::Marionette.Generated.RaiseEventCatalog.TryRaise);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    private static string EscapeStringLiteral(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
