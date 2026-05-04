// Marionette.NET — Source Generator validator
//
// Converts a candidate INamedTypeSymbol (a class that carries [McpRoot]) into
// a RootModel + the diagnostics produced while validating it.
//
// Key invariants:
//   * The output records (RootModel, etc.) NEVER reference Microsoft.CodeAnalysis
//     types. ISymbol/Location are extracted as strings/LocationInfo here and
//     thereafter held only by value. This keeps the incremental pipeline
//     correctly cached — see Model/ManifestModel.cs comments.
//   * Attribute matching is by metadata-name string only. The generator runs
//     in the Roslyn host and cannot reference Marionette.NET.Abstractions
//     directly, so attribute types are not "known".
//   * The validator emits descriptors only for valid members. Diagnostics
//     point to the originating location so squigglies appear in-IDE.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Marionette.SourceGenerator.Model;
using Microsoft.CodeAnalysis;

namespace Marionette.SourceGenerator;

/// <summary>
/// Static helper class for converting symbol information into manifest models.
/// </summary>
internal static class Validator
{
    /// <summary>
    /// Fully-qualified attribute type names. Match-by-string because the
    /// generator cannot reference Abstractions.
    /// </summary>
    public const string McpRootAttribute = "Marionette.McpRootAttribute";
    public const string McpCallableAttribute = "Marionette.McpCallableAttribute";
    public const string McpObservableAttribute = "Marionette.McpObservableAttribute";
    public const string McpTriggerableAttribute = "Marionette.McpTriggerableAttribute";
    public const string McpEventAttribute = "Marionette.McpEventAttribute";

    /// <summary>
    /// Validate a candidate root class, harvesting its members. Returns null
    /// when the class itself is invalid (MAR001) — diagnostics are still
    /// returned via the out parameter.
    /// </summary>
    public static RootModel? ValidateRoot(
        INamedTypeSymbol typeSymbol,
        out ImmutableArray<DiagnosticInfo> diagnostics)
    {
        var diags = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        // ---- MAR001: root class shape ----
        // Reject static and generic types. Static types cannot be instantiated
        // (no `new T()`), and generics need a type-argument resolution we don't
        // do here.
        if (typeSymbol.IsStatic || typeSymbol.IsGenericType ||
            typeSymbol.TypeKind != TypeKind.Class)
        {
            diags.Add(MakeDiagnostic(
                Diagnostics.InvalidRootClass,
                typeSymbol.Locations.FirstOrDefault(),
                typeSymbol.ToDisplayString()));
            diagnostics = diags.ToImmutable();
            return null;
        }

        // Capture the (parameterless ctor exists) bit — drives whether the
        // emitter writes a `() => new T()` factory or `null` (the runtime can
        // still call the descriptor's other delegates by accepting an existing
        // instance).
        bool hasParameterlessCtor = typeSymbol.InstanceConstructors
            .Any(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == 0);

        // ---- Collect callables, observables, triggerables, events ----
        var callables = ImmutableArray.CreateBuilder<CallableModel>();
        var observables = ImmutableArray.CreateBuilder<ObservableModel>();
        var triggerables = ImmutableArray.CreateBuilder<TriggerableModel>();
        var events = ImmutableArray.CreateBuilder<EventModel>();

        foreach (var member in typeSymbol.GetMembers())
        {
            switch (member)
            {
                case IMethodSymbol method when HasAttribute(method, McpCallableAttribute):
                    var callable = ValidateCallable(method, diags);
                    if (callable is not null) callables.Add(callable);
                    break;

                case IPropertySymbol observableProp when HasAttribute(observableProp, McpObservableAttribute):
                    var observable = ValidateObservable(observableProp, diags);
                    if (observable is not null) observables.Add(observable);
                    break;

                case IPropertySymbol triggerableProp when HasAttribute(triggerableProp, McpTriggerableAttribute):
                    var triggerable = ValidateTriggerable(triggerableProp, diags);
                    if (triggerable is not null) triggerables.Add(triggerable);
                    break;

                case IEventSymbol ev when HasAttribute(ev, McpEventAttribute):
                    var evt = ValidateEvent(ev, diags);
                    if (evt is not null) events.Add(evt);
                    break;
            }
        }

        foreach (var method in typeSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            if (ShouldSuggestCallable(method))
            {
                diags.Add(MakeDiagnostic(
                    Diagnostics.PublicMethodCouldBeCallable,
                    method.Locations.FirstOrDefault(),
                    method.ToDisplayString(),
                    typeSymbol.ToDisplayString()));
            }
        }

        // ---- MAR008: root has no entrypoints ----
        if (callables.Count == 0 && observables.Count == 0 && triggerables.Count == 0 && events.Count == 0)
        {
            diags.Add(MakeDiagnostic(
                Diagnostics.RootHasNoMembers,
                typeSymbol.Locations.FirstOrDefault(),
                typeSymbol.ToDisplayString()));
        }

        // Resolve the manifest name: explicit [McpRoot(Name)] overrides the
        // type's short name. ManifestName uses the SHORT type name as fallback,
        // which is what users expect in tool ids.
        var rootAttr = typeSymbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == McpRootAttribute);
        var manifestName = TryGetExplicitName(rootAttr) ?? typeSymbol.Name;

        diagnostics = diags.ToImmutable();
        return new RootModel(
            ManifestName: manifestName,
            TypeFullName: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            TypeKind: typeSymbol.TypeKind.ToString(),
            HasParameterlessCtor: hasParameterlessCtor,
            Callables: callables.ToImmutable().ToEquatableArray(),
            Observables: observables.ToImmutable().ToEquatableArray(),
            Triggerables: triggerables.ToImmutable().ToEquatableArray(),
            Events: events.ToImmutable().ToEquatableArray());
    }

    // -------------------------------------------------------------------------
    // CallableModel
    // -------------------------------------------------------------------------

    private static CallableModel? ValidateCallable(
        IMethodSymbol method,
        ImmutableArray<DiagnosticInfo>.Builder diags)
    {
        // ---- MAR002: must be public ----
        if (method.DeclaredAccessibility != Accessibility.Public)
        {
            diags.Add(MakeDiagnostic(
                Diagnostics.CallableNotPublic,
                method.Locations.FirstOrDefault(),
                method.ToDisplayString()));
            return null;
        }

        if (method.IsGenericMethod)
        {
            diags.Add(MakeDiagnostic(
                Diagnostics.CallableUnsupportedSignature,
                method.Locations.FirstOrDefault(),
                method.ToDisplayString(),
                "generic methods cannot be dispatched from a JSON-RPC call because no runtime type arguments are available"));
            return null;
        }

        foreach (var p in method.Parameters)
        {
            if (p.RefKind != RefKind.None)
            {
                diags.Add(MakeDiagnostic(
                    Diagnostics.CallableUnsupportedSignature,
                    p.Locations.FirstOrDefault(),
                    method.ToDisplayString(),
                    $"parameter '{p.Name}' uses {p.RefKind.ToString().ToLowerInvariant()} passing"));
                return null;
            }
        }

        // Pull the [McpCallable("description", OffUiThread = ?, TimeoutSeconds = ?)] data.
        var attr = method.GetAttributes()
            .First(a => a.AttributeClass?.ToDisplayString() == McpCallableAttribute);

        var description = attr.ConstructorArguments.Length > 0
            ? attr.ConstructorArguments[0].Value as string ?? string.Empty
            : string.Empty;

        bool offUiThread = TryGetNamedBool(attr, "OffUiThread", false);
        int timeoutSeconds = TryGetNamedInt(attr, "TimeoutSeconds", 0);

        // ---- MAR004: parameter type blacklist ----
        // Phase 1.b is permissive — only obvious blacklist hits trigger.
        var paramBuilder = ImmutableArray.CreateBuilder<ParameterModel>();
        // Phase 2.2: collect (name, ITypeSymbol, isRequired) tuples in
        // declaration order so JsonSchemaWriter.WriteParametersSchema gets
        // the same data the runtime sees. We deliberately walk Method.Parameters
        // here (Roslyn-side) rather than later from ParameterModel (which
        // does not retain the ITypeSymbol — that's intentional for the
        // pipeline equality contract).
        var schemaInputs = new List<(string Name, ITypeSymbol Type, bool IsRequired)>(method.Parameters.Length);

        foreach (var p in method.Parameters)
        {
            var typeFullName = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (IsBlacklistedParamType(p.Type))
            {
                diags.Add(MakeDiagnostic(
                    Diagnostics.UnsupportedParameterType,
                    p.Locations.FirstOrDefault(),
                    p.Name,
                    typeFullName,
                    method.ToDisplayString()));
                return null;
            }

            string? defaultLiteral = null;
            if (p.HasExplicitDefaultValue)
            {
                defaultLiteral = FormatLiteral(p.ExplicitDefaultValue, p.Type);
            }

            paramBuilder.Add(new ParameterModel(
                Name: p.Name,
                TypeFullName: typeFullName,
                IsRequired: !p.HasExplicitDefaultValue,
                DefaultLiteral: defaultLiteral,
                EnumTypeFullName: TryGetEnumTypeFullName(p.Type)));

            schemaInputs.Add((p.Name, p.Type, !p.HasExplicitDefaultValue));
        }

        // Return-shape analysis.
        // We classify the return type into three buckets:
        //   * void / non-Task                → ReturnsTask = false
        //   * Task (non-generic)             → ReturnsTask = true,  ReturnsTaskOfT = false
        //   * Task<T> / ValueTask<T>         → ReturnsTask = true,  ReturnsTaskOfT = true, TaskResultTypeFullName = T
        var returnTypeFullName = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var (returnsTask, returnsTaskOfT, taskResultTypeFullName) = ClassifyReturnType(method.ReturnType);

        // Phase 2.2: pre-compute the JSON schema for this callable's
        // parameter object. The runtime stamps it onto the per-method
        // McpServerTool's InputSchema; runtime never re-walks symbols.
        var parametersSchema = JsonSchemaWriter.WriteParametersSchema(schemaInputs);

        return new CallableModel(
            MethodName: method.Name,
            Description: description,
            OffUiThread: offUiThread,
            TimeoutSeconds: timeoutSeconds,
            ReturnTypeFullName: returnTypeFullName,
            ReturnsTask: returnsTask,
            ReturnsTaskOfT: returnsTaskOfT,
            TaskResultTypeFullName: taskResultTypeFullName,
            Parameters: paramBuilder.ToImmutable().ToEquatableArray(),
            ParametersJsonSchema: parametersSchema);
    }

    // -------------------------------------------------------------------------
    // ObservableModel
    // -------------------------------------------------------------------------

    private static ObservableModel? ValidateObservable(
        IPropertySymbol prop,
        ImmutableArray<DiagnosticInfo>.Builder diags)
    {
        // ---- MAR005: must have a getter ----
        if (prop.GetMethod is null)
        {
            diags.Add(MakeDiagnostic(
                Diagnostics.ObservableHasNoGetter,
                prop.Locations.FirstOrDefault(),
                prop.ToDisplayString()));
            return null;
        }

        // ---- MAR006: should be public (property AND getter) ----
        // Both the property declaration AND the getter must be reachable as
        // public; an internal property OR a private get-accessor on a public
        // property both fail.
        if (prop.DeclaredAccessibility != Accessibility.Public ||
            prop.GetMethod.DeclaredAccessibility != Accessibility.Public)
        {
            diags.Add(MakeDiagnostic(
                Diagnostics.ObservableNotPublic,
                prop.Locations.FirstOrDefault(),
                prop.ToDisplayString()));
            return null;
        }

        var attr = prop.GetAttributes()
            .First(a => a.AttributeClass?.ToDisplayString() == McpObservableAttribute);

        var description = attr.ConstructorArguments.Length > 0
            ? attr.ConstructorArguments[0].Value as string ?? string.Empty
            : string.Empty;
        bool watchable = TryGetNamedBool(attr, "Watchable", false);
        int pollingMs = TryGetNamedInt(attr, "PollingIntervalMs", 500);

        return new ObservableModel(
            PropertyName: prop.Name,
            Description: description,
            Watchable: watchable,
            PollingIntervalMs: pollingMs,
            PropertyTypeFullName: prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    // -------------------------------------------------------------------------
    // TriggerableModel
    // -------------------------------------------------------------------------

    private static TriggerableModel? ValidateTriggerable(
        IPropertySymbol prop,
        ImmutableArray<DiagnosticInfo>.Builder diags)
    {
        // For Phase 1.b we only require: it's a property, and the type *looks*
        // like a Button (or has a Click event). We're tolerant: if the type is
        // unresolved or the user is mid-edit, we skip MAR007 silently.
        if (!HasClickableShape(prop.Type))
        {
            diags.Add(MakeDiagnostic(
                Diagnostics.TriggerableUnsupportedType,
                prop.Locations.FirstOrDefault(),
                prop.ToDisplayString(),
                prop.Type.ToDisplayString()));
            return null;
        }

        var attr = prop.GetAttributes()
            .First(a => a.AttributeClass?.ToDisplayString() == McpTriggerableAttribute);
        var description = attr.ConstructorArguments.Length > 0
            ? attr.ConstructorArguments[0].Value as string ?? string.Empty
            : string.Empty;
        int strategy = TryGetNamedInt(attr, "Strategy", 0);

        return new TriggerableModel(
            PropertyName: prop.Name,
            Description: description,
            Strategy: strategy,
            ControlTypeFullName: prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    // -------------------------------------------------------------------------
    // EventModel
    // -------------------------------------------------------------------------

    private static EventModel? ValidateEvent(
        IEventSymbol ev,
        ImmutableArray<DiagnosticInfo>.Builder diags)
    {
        // ---- MAR010: must be EventHandler or EventHandler<T> ----
        // We accept the standard EventHandler (no T) AND any closed
        // EventHandler<TArgs>. The delegate type's display string carries the
        // full info; matching by OriginalDefinition keeps us tolerant of
        // T-binding edge-cases.
        var (isStandard, hasArgs, argsType) = ClassifyEventDelegate(ev.Type);
        if (!isStandard)
        {
            diags.Add(MakeDiagnostic(
                Diagnostics.McpEventUnsupportedDelegate,
                ev.Locations.FirstOrDefault(),
                ev.ToDisplayString(),
                ev.Type.ToDisplayString()));
            return null;
        }

        // Pull [McpEvent("description", MinIntervalMs=?, MaxQueueSize=?, CoalesceWindowMs=?)] data.
        var attr = ev.GetAttributes()
            .First(a => a.AttributeClass?.ToDisplayString() == McpEventAttribute);

        var description = attr.ConstructorArguments.Length > 0
            ? attr.ConstructorArguments[0].Value as string ?? string.Empty
            : string.Empty;

        int minIntervalMs = TryGetNamedInt(attr, "MinIntervalMs", 0);
        int maxQueueSize = TryGetNamedInt(attr, "MaxQueueSize", 100);
        int coalesceWindowMs = TryGetNamedInt(attr, "CoalesceWindowMs", 100);

        // ---- MAR012: throttling sanity ----
        if (maxQueueSize <= 0 || coalesceWindowMs < 0)
        {
            diags.Add(MakeDiagnostic(
                Diagnostics.McpEventInvalidThrottling,
                ev.Locations.FirstOrDefault(),
                ev.ToDisplayString(),
                maxQueueSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                coalesceWindowMs.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            // Fall back to defaults rather than dropping the event entirely.
            if (maxQueueSize <= 0) maxQueueSize = 100;
            if (coalesceWindowMs < 0) coalesceWindowMs = 100;
        }

        var argsTypeFullName = hasArgs
            ? argsType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : "global::System.EventArgs";

        // The schema is built from the args type's public properties. For the
        // base `EventArgs` (no public payload), we still emit a stable
        // empty-object schema rather than the "complex type" placeholder.
        var schema = hasArgs
            ? JsonSchemaWriter.WriteSchema(argsType)
            : JsonSchemaWriter.EmptyObjectSchema();

        return new EventModel(
            EventName: ev.Name,
            Description: description,
            HasArgsType: hasArgs,
            ArgsTypeFullName: argsTypeFullName,
            ArgsJsonSchema: schema,
            MinIntervalMs: minIntervalMs,
            MaxQueueSize: maxQueueSize,
            CoalesceWindowMs: coalesceWindowMs);
    }

    /// <summary>
    /// Classify an event's delegate type as
    /// <c>(isStandardEventHandler, hasArgsType, argsType?)</c>:
    /// <list type="bullet">
    /// <item><c>System.EventHandler</c> -> <c>(true, false, null)</c></item>
    /// <item><c>System.EventHandler&lt;T&gt;</c> -> <c>(true, true, T)</c></item>
    /// <item>anything else -> <c>(false, false, null)</c></item>
    /// </list>
    /// Tolerates <c>IErrorTypeSymbol</c> by treating it as unknown rather than
    /// crashing.
    /// </summary>
    private static (bool IsStandard, bool HasArgs, ITypeSymbol? ArgsType) ClassifyEventDelegate(ITypeSymbol delegateType)
    {
        if (delegateType is IErrorTypeSymbol) return (false, false, null);

        // Strip nullable annotation so `event EventHandler? Foo` matches the
        // same path as `event EventHandler Foo`. ToDisplayString() preserves
        // the `?` annotation otherwise, breaking the equality check.
        var unannotated = delegateType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        var fq = unannotated.ToDisplayString();
        if (fq == "System.EventHandler") return (true, false, null);

        if (unannotated is INamedTypeSymbol named && named.IsGenericType)
        {
            var def = named.OriginalDefinition.ToDisplayString();
            if (def == "System.EventHandler<TEventArgs>" && named.TypeArguments.Length == 1)
            {
                return (true, true, named.TypeArguments[0]);
            }
        }
        return (false, false, null);
    }

    // -------------------------------------------------------------------------
    // Helpers — attribute / type / literal handling
    // -------------------------------------------------------------------------

    private static bool HasAttribute(ISymbol symbol, string fqAttributeName)
    {
        foreach (var a in symbol.GetAttributes())
        {
            if (a.AttributeClass?.ToDisplayString() == fqAttributeName) return true;
        }
        return false;
    }

    private static string? TryGetExplicitName(AttributeData? rootAttr)
    {
        if (rootAttr is null) return null;
        if (rootAttr.ConstructorArguments.Length > 0 &&
            rootAttr.ConstructorArguments[0].Value is string nameFromCtor &&
            !string.IsNullOrWhiteSpace(nameFromCtor))
        {
            return nameFromCtor;
        }
        // Fall through if a future Phase adds a Name init-only property to
        // [McpRoot]. As of Phase 1.a, Name is only the ctor arg.
        foreach (var na in rootAttr.NamedArguments)
        {
            if (na.Key == "Name" && na.Value.Value is string nameFromProp &&
                !string.IsNullOrWhiteSpace(nameFromProp))
            {
                return nameFromProp;
            }
        }
        return null;
    }

    private static bool TryGetNamedBool(AttributeData attr, string key, bool fallback)
    {
        foreach (var na in attr.NamedArguments)
        {
            if (na.Key == key && na.Value.Value is bool b) return b;
        }
        return fallback;
    }

    private static int TryGetNamedInt(AttributeData attr, string key, int fallback)
    {
        foreach (var na in attr.NamedArguments)
        {
            if (na.Key == key && na.Value.Value is int i) return i;
        }
        return fallback;
    }

    /// <summary>
    /// Bare-bones blacklist for MAR004. Phase 1.b is intentionally permissive:
    /// we reject only types we KNOW we cannot marshal, never types we merely
    /// suspect.
    /// </summary>
    private static bool IsBlacklistedParamType(ITypeSymbol t)
    {
        if (t.TypeKind == TypeKind.Pointer) return true;
        if (t.TypeKind == TypeKind.FunctionPointer) return true;
        if (t.TypeKind == TypeKind.Delegate) return true;
        var fq = t.ToDisplayString();
        switch (fq)
        {
            case "System.IntPtr":
            case "nint":
            case "System.UIntPtr":
            case "nuint":
            case "System.IO.Stream":
            case "System.IO.FileStream":
            case "System.IO.MemoryStream":
                return true;
        }
        // Walk base chain for `Stream`-derived types.
        for (var b = t.BaseType; b is not null; b = b.BaseType)
        {
            if (b.ToDisplayString() == "System.IO.Stream") return true;
        }
        return false;
    }

    private static string? TryGetEnumTypeFullName(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        if (type is INamedTypeSymbol named &&
            named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            named.TypeArguments.Length == 1 &&
            named.TypeArguments[0].TypeKind == TypeKind.Enum)
        {
            return named.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        return null;
    }

    private static bool ShouldSuggestCallable(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary) return false;
        if (method.DeclaredAccessibility != Accessibility.Public) return false;
        if (method.IsStatic || method.IsAbstract || method.IsOverride) return false;
        if (method.IsGenericMethod) return false;
        if (HasAttribute(method, McpCallableAttribute)) return false;
        if (method.Parameters.Any(p => p.RefKind != RefKind.None)) return false;

        switch (method.Name)
        {
            case "Dispose":
            case "Initialize":
            case "OnInitialized":
            case "OnLoaded":
            case "OnUnloaded":
                return false;
        }

        if (method.IsAsync && method.ReturnsVoid) return false;

        foreach (var parameter in method.Parameters)
        {
            if (IsBlacklistedParamType(parameter.Type)) return false;
        }

        return true;
    }

    /// <summary>
    /// Heuristic for [McpTriggerable] target types. Phase 1.b: the Phase 1
    /// adapter (WPF) only knows how to dispatch System.Windows.Controls.Button-
    /// like surfaces. We accept any type that walks up to that base, OR any
    /// type that exposes a public instance event named "Click".
    /// </summary>
    private static bool HasClickableShape(ITypeSymbol type)
    {
        if (type is IErrorTypeSymbol) return true; // mid-edit; don't punish

        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.ToDisplayString() == "System.Windows.Controls.Button") return true;
            if (t.ToDisplayString() == "System.Windows.Controls.Primitives.ButtonBase") return true;
        }

        // Fallback: any type that exposes a public Click event.
        for (var t = type; t is not null; t = t.BaseType)
        {
            foreach (var member in t.GetMembers("Click"))
            {
                if (member is IEventSymbol ev && ev.DeclaredAccessibility == Accessibility.Public)
                    return true;
            }
        }
        return false;
    }

    private static (bool returnsTask, bool returnsTaskOfT, string? taskResultType) ClassifyReturnType(ITypeSymbol returnType)
    {
        var fq = returnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fq == "global::System.Threading.Tasks.Task" ||
            fq == "global::System.Threading.Tasks.ValueTask")
        {
            return (true, false, null);
        }
        if (returnType is INamedTypeSymbol named && named.IsGenericType)
        {
            var genericFq = named.ConstructUnboundGenericType().ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (genericFq == "global::System.Threading.Tasks.Task<>" ||
                genericFq == "global::System.Threading.Tasks.ValueTask<>")
            {
                return (true, true, named.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
        }
        return (false, false, null);
    }

    /// <summary>
    /// Produces a C# expression that evaluates to the given default-value
    /// constant. Handles primitives, strings, nulls, and enums.
    /// </summary>
    private static string FormatLiteral(object? value, ITypeSymbol type)
    {
        if (value is null) return "null";
        return value switch
        {
            bool b => b ? "true" : "false",
            string s => "\"" + EscapeString(s) + "\"",
            char c => "'" + EscapeChar(c) + "'",
            byte by => by.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sbyte sb => sb.ToString(System.Globalization.CultureInfo.InvariantCulture),
            short sh => sh.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ushort us => us.ToString(System.Globalization.CultureInfo.InvariantCulture),
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            uint ui => ui.ToString(System.Globalization.CultureInfo.InvariantCulture) + "u",
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L",
            ulong ul => ul.ToString(System.Globalization.CultureInfo.InvariantCulture) + "uL",
            float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f",
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d",
            decimal dec => dec.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m",
            _ => "default(" + type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")"
        };
    }

    private static string EscapeString(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string EscapeChar(char c) => c switch
    {
        '\\' => "\\\\",
        '\'' => "\\'",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\0' => "\\0",
        _ => c.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // -------------------------------------------------------------------------
    // Diagnostic helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Build an equatable <see cref="DiagnosticInfo"/> from a descriptor + a
    /// Roslyn <see cref="Location"/>. The location's syntax-tree reference is
    /// dropped — only file path + line/column survive — so the resulting
    /// DiagnosticInfo can be compared for cache hits without holding a syntax
    /// tree alive.
    /// </summary>
    public static DiagnosticInfo MakeDiagnostic(
        DiagnosticDescriptor descriptor,
        Location? location,
        params string[] messageArgs)
    {
        LocationInfo? locInfo = null;
        if (location is not null && location.IsInSource)
        {
            var lineSpan = location.GetLineSpan();
            locInfo = new LocationInfo(
                FilePath: lineSpan.Path ?? string.Empty,
                StartLine: lineSpan.StartLinePosition.Line,
                StartColumn: lineSpan.StartLinePosition.Character,
                EndLine: lineSpan.EndLinePosition.Line,
                EndColumn: lineSpan.EndLinePosition.Character);
        }

        return new DiagnosticInfo(
            Id: descriptor.Id,
            Title: descriptor.Title.ToString(),
            MessageFormat: descriptor.MessageFormat.ToString(),
            Category: descriptor.Category,
            DefaultSeverity: (int)descriptor.DefaultSeverity,
            MessageArgs: ImmutableArray.Create(messageArgs).ToEquatableArray(),
            Location: locInfo);
    }

    /// <summary>
    /// Reconstruct a Roslyn <see cref="Diagnostic"/> from <see cref="DiagnosticInfo"/>
    /// at the emit stage.
    /// </summary>
    public static Diagnostic ToRoslynDiagnostic(DiagnosticInfo info)
    {
        var descriptor = new DiagnosticDescriptor(
            id: info.Id,
            title: info.Title,
            messageFormat: info.MessageFormat,
            category: info.Category,
            defaultSeverity: (DiagnosticSeverity)info.DefaultSeverity,
            isEnabledByDefault: true);

        Location loc = Location.None;
        if (info.Location is { } li && !string.IsNullOrEmpty(li.FilePath))
        {
            // Build a Location from file + line span. We don't have the
            // original SyntaxTree, but ExternalFileLocation is the canonical
            // way to surface a location to a non-syntax-tree node.
            loc = Location.Create(
                li.FilePath,
                Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(0, 0),
                new Microsoft.CodeAnalysis.Text.LinePositionSpan(
                    new Microsoft.CodeAnalysis.Text.LinePosition(li.StartLine, li.StartColumn),
                    new Microsoft.CodeAnalysis.Text.LinePosition(li.EndLine, li.EndColumn)));
        }

        return Diagnostic.Create(descriptor, loc, info.MessageArgs.AsEnumerable().Cast<object?>().ToArray());
    }
}
