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
    public const string McpRaisableAttribute = "Marionette.McpRaisableAttribute";
    public const string McpClosedRootAttribute = "Marionette.McpClosedRootAttribute";

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
        // Reject static and non-class types. Phase 13.E.15: generic class
        // declarations no longer raise MAR001 — adopters who still want
        // them as roots opt in via `[assembly: McpClosedRoot(typeof(Foo<int>))]`,
        // which goes through ValidateClosedGenericRoot. The FAWMN pipeline
        // simply returns null without a diagnostic for the open-generic
        // declaration itself.
        if (typeSymbol.IsStatic || typeSymbol.TypeKind != TypeKind.Class)
        {
            diags.Add(MakeDiagnostic(
                Diagnostics.InvalidRootClass,
                typeSymbol.Locations.FirstOrDefault(),
                typeSymbol.ToDisplayString()));
            diagnostics = diags.ToImmutable();
            return null;
        }
        if (typeSymbol.IsGenericType)
        {
            // Silently skip — the closed-root pipeline picks up valid
            // instantiations declared via [assembly: McpClosedRoot]. No
            // diagnostic so the IDE stays quiet for adopters who legitimately
            // use the generic-root pattern.
            diagnostics = diags.ToImmutable();
            return null;
        }

        // Resolve the manifest name: explicit [McpRoot(Name)] overrides the
        // type's short name. ManifestName uses the SHORT type name as fallback,
        // which is what users expect in tool ids.
        var rootAttr = typeSymbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == McpRootAttribute);
        var manifestName = TryGetExplicitName(rootAttr) ?? typeSymbol.Name;

        var model = ValidateRootBody(typeSymbol, manifestName, diags);
        diagnostics = diags.ToImmutable();
        return model;
    }

    /// <summary>
    /// Phase 13.E.15: validate a closed generic instantiation as a manifest
    /// root. The MAR001 generic-class refusal is bypassed because the
    /// instantiation IS concrete (e.g. <c>Counter&lt;int&gt;</c>); the
    /// open-generic class itself can still carry <c>[McpRoot]</c> for IDE
    /// discovery, but this entry point is what produces the
    /// <see cref="RootModel"/>. The manifest name is read from
    /// <c>[McpClosedRoot(Name = ...)]</c>; if absent, falls back to the open
    /// type's short name (which is usually NOT what adopters want, hence the
    /// strong recommendation to set Name explicitly).
    /// </summary>
    public static RootModel? ValidateClosedGenericRoot(
        INamedTypeSymbol closedType,
        string? explicitName,
        Location? attributeLocation,
        out ImmutableArray<DiagnosticInfo> diagnostics)
    {
        var diags = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        if (closedType is IErrorTypeSymbol)
        {
            diagnostics = diags.ToImmutable();
            return null;
        }
        if (closedType.IsStatic ||
            closedType.TypeKind != TypeKind.Class ||
            !closedType.IsGenericType ||
            closedType.IsUnboundGenericType)
        {
            diags.Add(MakeDiagnostic(
                Diagnostics.McpClosedRootInvalid,
                attributeLocation,
                closedType.ToDisplayString(),
                "must be a closed generic class instantiation (e.g. typeof(Counter<int>))"));
            diagnostics = diags.ToImmutable();
            return null;
        }
        // Reject if any type argument is itself an open generic / type
        // parameter — `typeof(Counter<>)` already produces an unbound
        // generic but a hand-rolled half-closed thing might slip through.
        foreach (var ta in closedType.TypeArguments)
        {
            if (ta is ITypeParameterSymbol)
            {
                diags.Add(MakeDiagnostic(
                    Diagnostics.McpClosedRootInvalid,
                    attributeLocation,
                    closedType.ToDisplayString(),
                    "type arguments must be closed types, not open generic parameters"));
                diagnostics = diags.ToImmutable();
                return null;
            }
        }

        var manifestName = !string.IsNullOrWhiteSpace(explicitName)
            ? explicitName!
            : closedType.Name;

        var model = ValidateRootBody(closedType, manifestName, diags);
        diagnostics = diags.ToImmutable();
        return model;
    }

    /// <summary>
    /// Shared body for both <see cref="ValidateRoot"/> (non-generic) and
    /// <see cref="ValidateClosedGenericRoot"/> (closed generic). Walks the
    /// type's members, validates each, and assembles the
    /// <see cref="RootModel"/>. The MAR001 / MAR-something-else upfront
    /// shape checks are caller responsibility.
    /// </summary>
    private static RootModel ValidateRootBody(
        INamedTypeSymbol typeSymbol,
        string manifestName,
        ImmutableArray<DiagnosticInfo>.Builder diags)
    {
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
        var jsonTypes = new JsonTypeCollector();
        var valueTypes = new JsonTypeCollector();

        foreach (var member in typeSymbol.GetMembers())
        {
            switch (member)
            {
                case IMethodSymbol method when HasAttribute(method, McpCallableAttribute):
                    foreach (var callable in ValidateCallable(method, diags, valueTypes))
                    {
                        callables.Add(callable);
                    }
                    break;

                case IPropertySymbol observableProp when HasAttribute(observableProp, McpObservableAttribute):
                    var observable = ValidateObservable(observableProp, diags, valueTypes);
                    if (observable is not null) observables.Add(observable);
                    break;

                case IPropertySymbol triggerableProp when HasAttribute(triggerableProp, McpTriggerableAttribute):
                    var triggerable = ValidateTriggerable(triggerableProp, diags);
                    if (triggerable is not null) triggerables.Add(triggerable);
                    break;

                case IEventSymbol ev when HasAttribute(ev, McpEventAttribute):
                    var evt = ValidateEvent(ev, diags, jsonTypes);
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

        return new RootModel(
            ManifestName: manifestName,
            TypeFullName: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            TypeKind: typeSymbol.TypeKind.ToString(),
            HasParameterlessCtor: hasParameterlessCtor,
            Callables: callables.ToImmutable().ToEquatableArray(),
            Observables: observables.ToImmutable().ToEquatableArray(),
            Triggerables: triggerables.ToImmutable().ToEquatableArray(),
            Events: events.ToImmutable().ToEquatableArray(),
            EventArgsJsonTypes: jsonTypes.AllTypes.ToEquatableArray(),
            ValueJsonTypes: valueTypes.AllTypes.ToEquatableArray());
    }

    // -------------------------------------------------------------------------
    // CallableModel
    // -------------------------------------------------------------------------

    private static IEnumerable<CallableModel> ValidateCallable(
        IMethodSymbol method,
        ImmutableArray<DiagnosticInfo>.Builder diags,
        JsonTypeCollector valueTypes)
    {
        // ---- MAR002: must be public ----
        if (method.DeclaredAccessibility != Accessibility.Public)
        {
            diags.Add(MakeDiagnostic(
                Diagnostics.CallableNotPublic,
                method.Locations.FirstOrDefault(),
                method.ToDisplayString()));
            return System.Linq.Enumerable.Empty<CallableModel>();
        }

        // Phase 13.E.16: pull ClosedTypes from the [McpCallable] attribute
        // BEFORE the IsGenericMethod check so generic methods with closures
        // flow through the new branch instead of MAR014/MAR017.
        var callableAttr = method.GetAttributes()
            .First(a => a.AttributeClass?.ToDisplayString() == McpCallableAttribute);
        var closedTypes = TryGetClosedTypes(callableAttr);

        if (method.IsGenericMethod)
        {
            if (closedTypes is null || closedTypes.Value.Length == 0)
            {
                // No ClosedTypes — silently skip with MAR017 (Warning).
                diags.Add(MakeDiagnostic(
                    Diagnostics.GenericCallableMissingClosedTypes,
                    method.Locations.FirstOrDefault(),
                    method.ToDisplayString()));
                return System.Linq.Enumerable.Empty<CallableModel>();
            }
            if (method.TypeParameters.Length != 1)
            {
                diags.Add(MakeDiagnostic(
                    Diagnostics.CallableUnsupportedSignature,
                    method.Locations.FirstOrDefault(),
                    method.ToDisplayString(),
                    "[McpCallable] ClosedTypes only supports single-type-parameter methods; multi-type-parameter methods need separate per-arity overloads"));
                return System.Linq.Enumerable.Empty<CallableModel>();
            }
        }

        foreach (var p in method.Parameters)
        {
            // Phase 13.E.17: `in` is permitted because it has the same JSON-RPC
            // semantics as by-value (caller passes a value; callee sees a
            // readonly reference). The dispatcher emits the call-site `in`
            // modifier as part of the lambda body so the C# compiler binds
            // overload resolution correctly. `ref` and `out` remain refused
            // because they require call-site variables on both sides.
            if (p.RefKind == RefKind.Ref || p.RefKind == RefKind.Out)
            {
                diags.Add(MakeDiagnostic(
                    Diagnostics.CallableUnsupportedSignature,
                    p.Locations.FirstOrDefault(),
                    method.ToDisplayString(),
                    $"parameter '{p.Name}' uses {p.RefKind.ToString().ToLowerInvariant()} passing"));
                return System.Linq.Enumerable.Empty<CallableModel>();
            }
        }

        // Pull the [McpCallable("description", OffUiThread = ?, TimeoutSeconds = ?)] data.
        // (Phase 13.E.16: callableAttr was already resolved above to read ClosedTypes.)
        var attr = callableAttr;

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
                return System.Linq.Enumerable.Empty<CallableModel>();
            }

            string? defaultLiteral = null;
            if (p.HasExplicitDefaultValue)
            {
                defaultLiteral = FormatLiteral(p.ExplicitDefaultValue, p.Type);
            }

            // Phase 13.E.18: detect Stream / MemoryStream parameter types
            // BEFORE the JSON-context registration. Stream params don't go
            // through System.Text.Json — the dispatcher wraps a base64 JSON
            // string into a fresh MemoryStream at call site.
            bool isStreamParam = IsBase64StreamParamType(p.Type);

            // Phase 8.2: register the parameter type on the value collector
            // so the emitter can switch the JsonElement-deserialization path
            // from the reflection-based JsonSerializer.Deserialize<T>(string)
            // overload to the typed JsonTypeInfo<T> path. Failure leaves
            // jsonParamName null and the generator keeps emitting the
            // legacy untyped overload (with its IL2026/IL3050 warning).
            string? jsonParamName = null;
            if (!isStreamParam)
            {
                valueTypes.TryAdd(p.Type, out jsonParamName);
            }

            paramBuilder.Add(new ParameterModel(
                Name: p.Name,
                TypeFullName: typeFullName,
                IsRequired: !p.HasExplicitDefaultValue,
                DefaultLiteral: defaultLiteral,
                EnumTypeFullName: TryGetEnumTypeFullName(p.Type),
                JsonContextName: jsonParamName,
                IsBase64StreamParam: isStreamParam));

            schemaInputs.Add((p.Name, p.Type, !p.HasExplicitDefaultValue));
        }

        // Return-shape analysis.
        // We classify the return type into three buckets:
        //   * void / non-Task                → ReturnsTask = false
        //   * Task (non-generic)             → ReturnsTask = true,  ReturnsTaskOfT = false
        //   * Task<T> / ValueTask<T>         → ReturnsTask = true,  ReturnsTaskOfT = true, TaskResultTypeFullName = T
        var returnTypeFullName = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var (returnsTask, returnsTaskOfT, taskResultTypeFullName, taskResultSymbol) = ClassifyReturnType(method.ReturnType);

        // Phase 2.2: pre-compute the JSON schema for this callable's
        // parameter object. The runtime stamps it onto the per-method
        // McpServerTool's InputSchema; runtime never re-walks symbols.
        var parametersSchema = JsonSchemaWriter.WriteParametersSchema(schemaInputs);

        // Phase 8.2: register the serialisable return type on the value
        // collector so the emitted MarionetteJsonContext carries a typed
        // JsonTypeInfo<T>. Skip void / Task-without-T (no value to serialise);
        // for Task<T> and ValueTask<T> we register T (the awaited result).
        // Failure (unsupported transitive shape) leaves jsonReturnName null
        // and the runtime falls back to the legacy reflection-based path.
        string? jsonReturnName = null;
        if (returnsTaskOfT && taskResultSymbol is not null)
        {
            valueTypes.TryAdd(taskResultSymbol, out jsonReturnName);
        }
        else if (!returnsTask &&
                 method.ReturnType.SpecialType != SpecialType.System_Void)
        {
            valueTypes.TryAdd(method.ReturnType, out jsonReturnName);
        }

        // Phase 13.E.16: for generic methods with ClosedTypes, emit one
        // CallableModel per closed type-arg. Non-generic methods produce a
        // single descriptor as before.
        if (method.IsGenericMethod && closedTypes is { } ctVal && ctVal.Length > 0)
        {
            var emitted = new List<CallableModel>(ctVal.Length);
            foreach (var closedType in ctVal)
            {
                // Re-walk the parameter list against the CLOSED method
                // symbol so type parameters are substituted (`T value` →
                // `int value`). This keeps the JSON schema and the
                // typed-context wiring grounded in concrete types instead
                // of an unresolvable `T`.
                var closedMethod = method.Construct(closedType);
                var closedParamBuilder = ImmutableArray.CreateBuilder<ParameterModel>();
                var closedSchemaInputs = new List<(string Name, ITypeSymbol Type, bool IsRequired)>(closedMethod.Parameters.Length);
                bool closedOk = true;

                foreach (var p in closedMethod.Parameters)
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
                        closedOk = false;
                        break;
                    }

                    string? defLit = null;
                    if (p.HasExplicitDefaultValue)
                    {
                        defLit = FormatLiteral(p.ExplicitDefaultValue, p.Type);
                    }

                    bool isStream = IsBase64StreamParamType(p.Type);
                    string? jsonName = null;
                    if (!isStream)
                    {
                        valueTypes.TryAdd(p.Type, out jsonName);
                    }

                    closedParamBuilder.Add(new ParameterModel(
                        Name: p.Name,
                        TypeFullName: typeFullName,
                        IsRequired: !p.HasExplicitDefaultValue,
                        DefaultLiteral: defLit,
                        EnumTypeFullName: TryGetEnumTypeFullName(p.Type),
                        JsonContextName: jsonName,
                        IsBase64StreamParam: isStream));

                    closedSchemaInputs.Add((p.Name, p.Type, !p.HasExplicitDefaultValue));
                }

                if (!closedOk) continue;

                var closedReturnFqn = closedMethod.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var (cReturnsTask, cReturnsTaskOfT, cTaskResultFqn, cTaskResultSym) = ClassifyReturnType(closedMethod.ReturnType);

                string? cJsonReturnName = null;
                if (cReturnsTaskOfT && cTaskResultSym is not null)
                {
                    valueTypes.TryAdd(cTaskResultSym, out cJsonReturnName);
                }
                else if (!cReturnsTask &&
                         closedMethod.ReturnType.SpecialType != SpecialType.System_Void)
                {
                    valueTypes.TryAdd(closedMethod.ReturnType, out cJsonReturnName);
                }

                var closedParamsSchema = JsonSchemaWriter.WriteParametersSchema(closedSchemaInputs);

                var typeFqn = closedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var literal = "<" + typeFqn + ">";
                // Suffix: e.g. `int` → `_int`, `System.String` → `_System_String`.
                // Used as the manifest-name distinguisher.
                var suffix = "_" + EncodeForIdentifier(closedType);
                emitted.Add(new CallableModel(
                    MethodName: method.Name,
                    Description: description,
                    OffUiThread: offUiThread,
                    TimeoutSeconds: timeoutSeconds,
                    ReturnTypeFullName: closedReturnFqn,
                    ReturnsTask: cReturnsTask,
                    ReturnsTaskOfT: cReturnsTaskOfT,
                    TaskResultTypeFullName: cTaskResultFqn,
                    Parameters: closedParamBuilder.ToImmutable().ToEquatableArray(),
                    ParametersJsonSchema: closedParamsSchema,
                    JsonReturnContextName: cJsonReturnName,
                    ClosedTypeArgsLiteral: literal,
                    ManifestNameSuffix: suffix));
            }
            return emitted;
        }

        return new[]
        {
            new CallableModel(
                MethodName: method.Name,
                Description: description,
                OffUiThread: offUiThread,
                TimeoutSeconds: timeoutSeconds,
                ReturnTypeFullName: returnTypeFullName,
                ReturnsTask: returnsTask,
                ReturnsTaskOfT: returnsTaskOfT,
                TaskResultTypeFullName: taskResultTypeFullName,
                Parameters: paramBuilder.ToImmutable().ToEquatableArray(),
                ParametersJsonSchema: parametersSchema,
                JsonReturnContextName: jsonReturnName)
        };
    }

    /// <summary>
    /// Phase 13.E.16: build a manifest-name-safe identifier suffix for a
    /// closed type argument. Examples:
    /// <c>int</c> → <c>Int32</c>, <c>System.String</c> → <c>System_String</c>,
    /// <c>List&lt;int&gt;</c> → <c>System_Collections_Generic_List_int_</c>.
    /// </summary>
    private static string EncodeForIdentifier(ITypeSymbol type)
    {
        var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fqn.StartsWith("global::", System.StringComparison.Ordinal))
        {
            fqn = fqn.Substring("global::".Length);
        }
        var sb = new System.Text.StringBuilder(fqn.Length);
        foreach (var c in fqn)
        {
            sb.Append(c switch
            {
                '.' or ',' or '<' or '>' or ' ' or '?' or '[' or ']' => '_',
                _ => c,
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Phase 13.E.16: extract the <c>ClosedTypes</c> named-arg array from a
    /// <c>[McpCallable]</c> attribute, or <see langword="null"/> when absent.
    /// </summary>
    private static ImmutableArray<ITypeSymbol>? TryGetClosedTypes(AttributeData attr)
    {
        foreach (var na in attr.NamedArguments)
        {
            if (na.Key != "ClosedTypes") continue;
            var values = na.Value.Values;
            if (values.IsDefaultOrEmpty) return ImmutableArray<ITypeSymbol>.Empty;
            var list = ImmutableArray.CreateBuilder<ITypeSymbol>(values.Length);
            foreach (var v in values)
            {
                if (v.Value is ITypeSymbol ts && ts is not IErrorTypeSymbol)
                {
                    list.Add(ts);
                }
            }
            return list.ToImmutable();
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // ObservableModel
    // -------------------------------------------------------------------------

    private static ObservableModel? ValidateObservable(
        IPropertySymbol prop,
        ImmutableArray<DiagnosticInfo>.Builder diags,
        JsonTypeCollector valueTypes)
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

        // Phase 8.2: register the property's type on the value collector.
        // Failure (unsupported transitive shape — generic, array, abstract,
        // …) leaves jsonValueName null and the runtime keeps the legacy
        // reflection-based JsonSerializer.Serialize path.
        valueTypes.TryAdd(prop.Type, out var jsonValueName);

        return new ObservableModel(
            PropertyName: prop.Name,
            Description: description,
            Watchable: watchable,
            PollingIntervalMs: pollingMs,
            PropertyTypeFullName: prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            JsonValueContextName: jsonValueName);
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
        ImmutableArray<DiagnosticInfo>.Builder diags,
        JsonTypeCollector jsonTypes)
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

        // Phase 8.1: try to register the args type into the JSON-context
        // collector. Success means the entire transitive graph is
        // source-gen-eligible and the emitter can wire a typed SerializeArgs
        // lambda. Failure (any unsupported shape anywhere in the graph) leaves
        // jsonRootName null and the descriptor falls back to the legacy
        // reflection-based JsonSerializer.Serialize path.
        string? jsonRootName = null;
        if (hasArgs)
        {
            jsonTypes.TryAdd(argsType, out jsonRootName);
        }

        return new EventModel(
            EventName: ev.Name,
            Description: description,
            HasArgsType: hasArgs,
            ArgsTypeFullName: argsTypeFullName,
            ArgsJsonSchema: schema,
            MinIntervalMs: minIntervalMs,
            MaxQueueSize: maxQueueSize,
            CoalesceWindowMs: coalesceWindowMs,
            JsonRootContextName: jsonRootName);
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
    /// suspect. Phase 13.E.18 lifts <c>Stream</c> and <c>MemoryStream</c> from
    /// the blacklist — the dispatcher wraps a base64-encoded JSON string into
    /// a fresh <c>MemoryStream</c>. <c>FileStream</c> stays blacklisted (no
    /// sane base64 wrap; adopters who really want a file on disk should
    /// take a path string instead).
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
            case "System.IO.FileStream":
                return true;
        }
        return false;
    }

    /// <summary>
    /// Phase 13.E.18: detect <c>Stream</c> / <c>MemoryStream</c> parameter
    /// types so the emitter wraps a base64 JSON string. Also reaches
    /// <c>Stream</c>-derived user types (rare, but supported).
    /// </summary>
    private static bool IsBase64StreamParamType(ITypeSymbol t)
    {
        var fq = t.ToDisplayString();
        if (fq == "System.IO.Stream" || fq == "System.IO.MemoryStream") return true;
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

    private static (bool returnsTask, bool returnsTaskOfT, string? taskResultType, ITypeSymbol? taskResultSymbol) ClassifyReturnType(ITypeSymbol returnType)
    {
        var fq = returnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fq == "global::System.Threading.Tasks.Task" ||
            fq == "global::System.Threading.Tasks.ValueTask")
        {
            return (true, false, null, null);
        }
        if (returnType is INamedTypeSymbol named && named.IsGenericType)
        {
            var genericFq = named.ConstructUnboundGenericType().ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (genericFq == "global::System.Threading.Tasks.Task<>" ||
                genericFq == "global::System.Threading.Tasks.ValueTask<>")
            {
                var taskArg = named.TypeArguments[0];
                return (true, true, taskArg.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), taskArg);
            }
        }
        return (false, false, null, null);
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
