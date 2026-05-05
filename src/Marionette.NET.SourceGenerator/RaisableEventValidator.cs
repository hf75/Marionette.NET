// Marionette.NET — Phase 12.2 [McpRaisable] catalog validator
//
// Walks a candidate (Type, EventName) pair drawn from the assembly-level
// `[assembly: McpRaisable(typeof(X), "Y")]` declaration:
//   * Type must be a reference type (concrete or abstract — abstract is
//     allowed because the cast happens against the inbound runtime instance).
//   * Type's base chain must declare a public static field named
//     `<EventName>Event` of type `System.Windows.RoutedEvent` (WPF) or
//     `Avalonia.Interactivity.RoutedEvent` (Avalonia). The field's *declaring*
//     type is preserved separately from the cast target so the emitted
//     reference (`<DeclaringType>.<EventName>Event`) compiles when the field
//     is inherited (e.g. `Button.ClickEvent` → declared on `ButtonBase`).
//   * The control type must extend `System.Windows.UIElement` (WPF) or
//     `Avalonia.Interactivity.Interactive` (Avalonia) so the cast in the
//     emitted dispatcher resolves to a type with a public `RaiseEvent`
//     method.
//
// Validation failures produce MAR015 diagnostics with a human-readable
// reason; the catalog entry is dropped and the runtime falls back to
// reflection on the (Type, EventName) pair.

using System.Collections.Immutable;
using Marionette.SourceGenerator.Model;
using Microsoft.CodeAnalysis;

namespace Marionette.SourceGenerator;

internal static class RaisableEventValidator
{
    private const string WpfRoutedEvent = "System.Windows.RoutedEvent";
    private const string WpfRoutedEventArgs = "System.Windows.RoutedEventArgs";
    private const string WpfUIElement = "System.Windows.UIElement";

    private const string AvaloniaRoutedEvent = "Avalonia.Interactivity.RoutedEvent";
    private const string AvaloniaRoutedEventArgs = "Avalonia.Interactivity.RoutedEventArgs";
    private const string AvaloniaInteractive = "Avalonia.Interactivity.Interactive";

    /// <summary>
    /// Validate a single <c>[assembly: McpRaisable(typeof(T), "Name")]</c>
    /// declaration. Returns the populated <see cref="RaisableEventModel"/>
    /// on success, or <see langword="null"/> after appending a MAR015 to
    /// <paramref name="diags"/>.
    /// </summary>
    public static RaisableEventModel? Validate(
        ITypeSymbol controlType,
        string eventName,
        Location? attributeLocation,
        ImmutableArray<DiagnosticInfo>.Builder diags)
    {
        if (controlType is IErrorTypeSymbol)
        {
            // Typo or missing reference — let the C# compiler diagnose it.
            return null;
        }

        if (controlType.TypeKind != TypeKind.Class)
        {
            diags.Add(Validator.MakeDiagnostic(
                Diagnostics.McpRaisableInvalid,
                attributeLocation,
                controlType.ToDisplayString(),
                eventName,
                "control type must be a reference type (class)"));
            return null;
        }

        if (controlType is INamedTypeSymbol named && named.IsUnboundGenericType)
        {
            diags.Add(Validator.MakeDiagnostic(
                Diagnostics.McpRaisableInvalid,
                attributeLocation,
                controlType.ToDisplayString(),
                eventName,
                "open generic types are not supported; declare each closed instantiation"));
            return null;
        }

        var fieldName = eventName + "Event";

        // Walk the base chain looking for `static RoutedEvent <Name>Event`.
        ITypeSymbol? declaring = null;
        string? routedEventFq = null;

        for (var cur = controlType; cur is not null; cur = cur.BaseType)
        {
            foreach (var member in cur.GetMembers(fieldName))
            {
                if (member is not IFieldSymbol field) continue;
                if (!field.IsStatic) continue;
                var ft = field.Type.ToDisplayString();
                if (ft == WpfRoutedEvent || ft == AvaloniaRoutedEvent)
                {
                    declaring = cur;
                    routedEventFq = ft;
                    break;
                }
            }
            if (declaring is not null) break;
        }

        if (declaring is null || routedEventFq is null)
        {
            diags.Add(Validator.MakeDiagnostic(
                Diagnostics.McpRaisableInvalid,
                attributeLocation,
                controlType.ToDisplayString(),
                eventName,
                $"no static field '{fieldName}' of type 'RoutedEvent' found on '{controlType.ToDisplayString()}' or any base type"));
            return null;
        }

        // Pin framework based on the matched RoutedEvent type. Cross-checks
        // the control type's base chain so we reject mismatches (a
        // hypothetical type carrying both a WPF RoutedEvent field and an
        // Avalonia.Interactive base — should never happen in practice).
        string framework;
        string routedEventArgsFq;
        string raiseEventTypeFq;
        if (routedEventFq == WpfRoutedEvent)
        {
            framework = "WPF";
            routedEventArgsFq = WpfRoutedEventArgs;
            raiseEventTypeFq = WpfUIElement;
            if (!ExtendsType(controlType, WpfUIElement))
            {
                diags.Add(Validator.MakeDiagnostic(
                    Diagnostics.McpRaisableInvalid,
                    attributeLocation,
                    controlType.ToDisplayString(),
                    eventName,
                    $"type does not extend '{WpfUIElement}' — RaiseEvent is not visible on it"));
                return null;
            }
        }
        else
        {
            framework = "Avalonia";
            routedEventArgsFq = AvaloniaRoutedEventArgs;
            raiseEventTypeFq = AvaloniaInteractive;
            if (!ExtendsType(controlType, AvaloniaInteractive))
            {
                diags.Add(Validator.MakeDiagnostic(
                    Diagnostics.McpRaisableInvalid,
                    attributeLocation,
                    controlType.ToDisplayString(),
                    eventName,
                    $"type does not extend '{AvaloniaInteractive}' — RaiseEvent is not visible on it"));
                return null;
            }
        }

        var controlFq = controlType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var declaringFq = declaring.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return new RaisableEventModel(
            ControlTypeFullName: controlFq,
            DeclaringTypeFullName: declaringFq,
            EventName: eventName,
            Framework: framework,
            RoutedEventArgsFullName: "global::" + routedEventArgsFq,
            RaiseEventTypeFullName: "global::" + raiseEventTypeFq);
    }

    private static bool ExtendsType(ITypeSymbol type, string baseFullName)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.ToDisplayString() == baseFullName) return true;
        }
        return false;
    }
}
