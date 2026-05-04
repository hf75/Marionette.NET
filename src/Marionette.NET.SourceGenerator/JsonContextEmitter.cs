// Marionette.NET — JsonSerializerContext emitter (Phase 8.1)
//
// Renders a partial JsonSerializerContext-derived class with hand-written
// JsonTypeInfo<T> properties built via JsonMetadataServices factories. This
// path bypasses System.Text.Json's [JsonSerializable] source generator —
// Roslyn does not allow one source generator to see another's output, so
// emitting `[JsonSerializable]` attributes from our generator and expecting
// STJ to scan them does NOT work. We become our own JSON source generator.
//
// Emission shape (one-time per assembly with [McpEvent] args):
//
//   internal sealed class MarionetteEventArgsJsonContext : JsonSerializerContext
//   {
//       private static MarionetteEventArgsJsonContext? _default;
//       public static MarionetteEventArgsJsonContext Default
//           => _default ??= new MarionetteEventArgsJsonContext(new JsonSerializerOptions());
//       public MarionetteEventArgsJsonContext(JsonSerializerOptions options) : base(options) {}
//
//       // One JsonTypeInfo<T> property per encountered type:
//       private JsonTypeInfo<string>? _System_String;
//       public JsonTypeInfo<string> System_String => _System_String ??=
//           JsonMetadataServices.CreateValueInfo<string>(Options, JsonMetadataServices.StringConverter);
//
//       private JsonTypeInfo<Demo.AppointmentAddedEventArgs>? _Demo_AppointmentAddedEventArgs;
//       public JsonTypeInfo<Demo.AppointmentAddedEventArgs> Demo_AppointmentAddedEventArgs => ...;
//
//       protected override JsonSerializerOptions GeneratedSerializerOptions => Options;
//       public override JsonTypeInfo? GetTypeInfo(Type type) { /* dispatch */ }
//   }
//
// Each Object-kind type gets a private Create_<Name>() factory that builds a
// JsonObjectInfoValues<T> with PropertyMetadataInitializer pointing at typed
// property getters and PropertyTypeInfo references to the right context
// member.
//
// AOT contract: zero JsonSerializer.Serialize<T>(value, options) calls in
// the runtime path that flows through the emitted context. Every JsonTypeInfo
// is built via JsonMetadataServices factory methods documented as AOT-safe.

using System.Collections.Generic;
using System.Text;
using Marionette.SourceGenerator.Model;

namespace Marionette.SourceGenerator;

internal static class JsonContextEmitter
{
    /// <summary>
    /// Emit a <c>MarionetteEventArgsJsonContext</c> partial class containing
    /// hand-written <c>JsonTypeInfo&lt;T&gt;</c> properties for every type in
    /// <paramref name="types"/>. Returns nothing when the input is empty —
    /// the generator skips emission and the runtime keeps its legacy
    /// reflection-based serialisation path.
    /// </summary>
    public static void Emit(StringBuilder sb, IReadOnlyList<JsonTypeModel> types)
    {
        if (types.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Phase 8.1: AOT-clean JSON serialisation context for [McpEvent] args.");
        sb.AppendLine("/// All <c>JsonTypeInfo&lt;T&gt;</c> properties are built via");
        sb.AppendLine("/// <c>JsonMetadataServices</c> factories at compile time — no runtime");
        sb.AppendLine("/// reflection. Property names encode the originating CLR full name with");
        sb.AppendLine("/// '.' replaced by '_' so that the registered set is collision-free.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("internal sealed class MarionetteEventArgsJsonContext : global::System.Text.Json.Serialization.JsonSerializerContext");
        sb.AppendLine("{");
        sb.AppendLine("    private static MarionetteEventArgsJsonContext? _default;");
        sb.AppendLine("    public static MarionetteEventArgsJsonContext Default");
        sb.AppendLine("        => _default ??= new MarionetteEventArgsJsonContext(new global::System.Text.Json.JsonSerializerOptions());");
        sb.AppendLine();
        sb.AppendLine("    public MarionetteEventArgsJsonContext(global::System.Text.Json.JsonSerializerOptions options)");
        sb.AppendLine("        : base(options) { }");
        sb.AppendLine();

        foreach (var t in types)
        {
            EmitTypeInfoProperty(sb, t);
        }

        sb.AppendLine();
        sb.AppendLine("    protected override global::System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => Options;");
        sb.AppendLine();
        sb.AppendLine("    public override global::System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(global::System.Type type)");
        sb.AppendLine("    {");
        foreach (var t in types)
        {
            sb.Append("        if (type == typeof(global::");
            sb.Append(StripGlobalPrefix(t.TypeFullName));
            sb.Append(")) return ");
            sb.Append(t.PropertyName);
            sb.AppendLine(";");
        }
        sb.AppendLine("        return null;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    private static void EmitTypeInfoProperty(StringBuilder sb, JsonTypeModel type)
    {
        var fqn = StripGlobalPrefix(type.TypeFullName);

        // Backing field + lazy-init property.
        sb.Append("    private global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<global::");
        sb.Append(fqn);
        sb.Append(">? _");
        sb.Append(type.PropertyName);
        sb.AppendLine(";");

        sb.Append("    public global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<global::");
        sb.Append(fqn);
        sb.Append("> ");
        sb.Append(type.PropertyName);
        sb.Append(" => _");
        sb.Append(type.PropertyName);
        sb.AppendLine(" ??= ");

        switch (type.Kind)
        {
            case JsonTypeKind.Primitive:
                EmitPrimitiveCreation(sb, type);
                break;
            case JsonTypeKind.Nullable:
                EmitNullableCreation(sb, type);
                break;
            case JsonTypeKind.Object:
                EmitObjectCreation(sb, type);
                break;
        }

        sb.AppendLine();
    }

    private static void EmitPrimitiveCreation(StringBuilder sb, JsonTypeModel type)
    {
        var fqn = StripGlobalPrefix(type.TypeFullName);
        sb.Append("        global::System.Text.Json.Serialization.Metadata.JsonMetadataServices.CreateValueInfo<global::");
        sb.Append(fqn);
        sb.Append(">(Options, global::System.Text.Json.Serialization.Metadata.JsonMetadataServices.");
        sb.Append(type.PrimitiveConverter);
        sb.AppendLine(");");
    }

    private static void EmitNullableCreation(StringBuilder sb, JsonTypeModel type)
    {
        // CreateNullableInfo<TInner> takes the inner value-type's JsonTypeInfo.
        // We've already registered the inner under its own property name; the
        // nullable wrapper's PropertyName is "Nullable_<innerEncoded>".
        var inner = StripGlobalPrefix(type.UnderlyingTypeFullName!);
        var innerProp = type.PropertyName.Substring("Nullable_".Length);
        sb.Append("        global::System.Text.Json.Serialization.Metadata.JsonMetadataServices.CreateNullableInfo<global::");
        sb.Append(inner);
        sb.Append(">(Options, ");
        sb.Append(innerProp);
        sb.AppendLine(");");
    }

    private static void EmitObjectCreation(StringBuilder sb, JsonTypeModel type)
    {
        var fqn = StripGlobalPrefix(type.TypeFullName);
        sb.Append("        Create_");
        sb.Append(type.PropertyName);
        sb.AppendLine("();");

        // Emit the per-type factory method right after the property.
        sb.Append("    private global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<global::");
        sb.Append(fqn);
        sb.Append("> Create_");
        sb.Append(type.PropertyName);
        sb.AppendLine("()");
        sb.AppendLine("    {");
        sb.Append("        var info = new global::System.Text.Json.Serialization.Metadata.JsonObjectInfoValues<global::");
        sb.Append(fqn);
        sb.AppendLine(">");
        sb.AppendLine("        {");
        sb.AppendLine("            ObjectCreator = null,");
        sb.AppendLine("            ObjectWithParameterizedConstructorCreator = null,");
        sb.AppendLine("            ConstructorParameterMetadataInitializer = null,");
        sb.AppendLine("            SerializeHandler = null,");
        sb.AppendLine("            NumberHandling = default,");
        sb.AppendLine("            PropertyMetadataInitializer = (_) => new global::System.Text.Json.Serialization.Metadata.JsonPropertyInfo[]");
        sb.AppendLine("            {");
        foreach (var prop in type.Properties.AsEnumerable())
        {
            EmitPropertyInfo(sb, fqn, prop);
        }
        sb.AppendLine("            },");
        sb.AppendLine("        };");
        sb.Append("        return global::System.Text.Json.Serialization.Metadata.JsonMetadataServices.CreateObjectInfo<global::");
        sb.Append(fqn);
        sb.AppendLine(">(Options, info);");
        sb.AppendLine("    }");
    }

    private static void EmitPropertyInfo(StringBuilder sb, string declaringTypeFqn, JsonPropertyModel prop)
    {
        var propTypeFqn = StripGlobalPrefix(prop.PropertyTypeFullName);
        sb.Append("                global::System.Text.Json.Serialization.Metadata.JsonMetadataServices.CreatePropertyInfo<global::");
        sb.Append(propTypeFqn);
        sb.AppendLine(">(");
        sb.Append("                    Options,");
        sb.AppendLine();
        sb.Append("                    new global::System.Text.Json.Serialization.Metadata.JsonPropertyInfoValues<global::");
        sb.Append(propTypeFqn);
        sb.AppendLine(">");
        sb.AppendLine("                    {");
        sb.AppendLine("                        IsProperty = true,");
        sb.AppendLine("                        IsPublic = true,");
        sb.AppendLine("                        IsVirtual = false,");
        sb.Append("                        DeclaringType = typeof(global::");
        sb.Append(declaringTypeFqn);
        sb.AppendLine("),");
        sb.AppendLine("                        Converter = null,");
        sb.Append("                        Getter = static (obj) => ((global::");
        sb.Append(declaringTypeFqn);
        sb.Append(")obj).");
        sb.Append(prop.Name);
        sb.AppendLine(",");
        sb.AppendLine("                        Setter = null,");
        sb.AppendLine("                        IgnoreCondition = null,");
        sb.AppendLine("                        HasJsonInclude = false,");
        sb.AppendLine("                        IsExtensionData = false,");
        sb.AppendLine("                        NumberHandling = null,");
        sb.Append("                        PropertyName = \"");
        sb.Append(prop.Name);
        sb.AppendLine("\",");
        sb.AppendLine("                        JsonPropertyName = null,");
        sb.AppendLine("                    }),");
    }

    private static string StripGlobalPrefix(string s) =>
        s.StartsWith("global::", System.StringComparison.Ordinal) ? s.Substring("global::".Length) : s;
}
