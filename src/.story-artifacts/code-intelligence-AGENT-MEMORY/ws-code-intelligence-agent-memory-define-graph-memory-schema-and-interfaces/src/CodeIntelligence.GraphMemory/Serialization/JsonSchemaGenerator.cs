using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIntelligence.GraphMemory.Schema;

namespace CodeIntelligence.GraphMemory.Serialization;

/// <summary>
/// Generates a JSON Schema document derived from the C# model by recursively
/// walking <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/>
/// obtained from <see cref="GraphJsonDefaults.Options"/>. This guarantees the
/// schema always matches the actual wire format.
///
/// <para>Types are discovered automatically by traversing the property graph
/// starting from <see cref="CodeKnowledgeGraph"/>. No hardcoded type list
/// is maintained — adding a new property or record to the model automatically
/// updates the schema.</para>
///
/// <para>Key design choices for forward-compatibility:</para>
/// <list type="bullet">
///   <item>Enum definitions use <c>type: "string"</c> with known values listed in
///         <c>examples</c>, not a strict <c>enum</c> constraint. This allows newer
///         producers to emit new members that older schema versions still accept.</item>
///   <item><c>additionalProperties</c> is not set to <c>false</c> on object types,
///         enabling consumers to ignore unknown fields from newer producers.</item>
///   <item><c>Unknown</c> is excluded from enum examples since producers must never emit it.</item>
/// </list>
/// </summary>
public static class JsonSchemaGenerator
{
    /// <summary>
    /// Primitive CLR types mapped to their JSON Schema type keywords.
    /// </summary>
    private static readonly Dictionary<Type, (string type, string? format)> PrimitiveMap = new()
    {
        [typeof(string)] = ("string", null),
        [typeof(int)] = ("integer", null),
        [typeof(long)] = ("integer", null),
        [typeof(short)] = ("integer", null),
        [typeof(byte)] = ("integer", null),
        [typeof(double)] = ("number", null),
        [typeof(float)] = ("number", null),
        [typeof(decimal)] = ("number", null),
        [typeof(bool)] = ("boolean", null),
        [typeof(DateTimeOffset)] = ("string", "date-time"),
        [typeof(DateTime)] = ("string", "date-time"),
    };

    /// <summary>
    /// Generate the JSON Schema string for <see cref="CodeKnowledgeGraph"/>.
    /// </summary>
    public static string Generate()
    {
        var schema = BuildSchema();
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        return schema.ToJsonString(options);
    }

    private static JsonObject BuildSchema()
    {
        var context = new GeneratorContext();
        var rootInfo = GetTypeInfo(typeof(CodeKnowledgeGraph));
        var (props, required) = BuildObjectProperties(rootInfo, context);

        var defs = new JsonObject();
        foreach (var (name, def) in context.Definitions.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            defs[name] = def;
        }

        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = $"https://code-intelligence.dev/schemas/graph-memory/v{SchemaVersion.CurrentMajor}.{SchemaVersion.CurrentMinor}",
            ["title"] = "CodeKnowledgeGraph",
            ["description"] = $"Code knowledge graph schema v{SchemaVersion.CurrentMajor}.{SchemaVersion.CurrentMinor}. " +
                              "Forward-compatible: consumers must tolerate unknown properties and enum values. See VERSIONING.md.",
            ["type"] = "object",
            ["required"] = required,
            ["properties"] = props,
            ["$defs"] = defs,
        };
    }

    /// <summary>
    /// Walks the STJ contract metadata for a type and produces schema properties
    /// and required array. Uses <see cref="GraphJsonDefaults.Options"/> so naming
    /// policy and ignore conditions match the actual wire format.
    /// </summary>
    private static (JsonObject Props, JsonArray Required) BuildObjectProperties(
        JsonTypeInfo typeInfo, GeneratorContext context)
    {
        var props = new JsonObject();
        var required = new JsonArray();

        foreach (var prop in typeInfo.Properties)
        {
            if (prop.Get is null && prop.Set is null) continue;

            var wireName = prop.Name; // already camelCase from the options
            var propSchema = MapType(prop.PropertyType, context);
            props[wireName] = propSchema;

            if (prop.IsRequired)
                required.Add(wireName);
        }

        return (props, required);
    }

    /// <summary>
    /// Maps a CLR type to its JSON Schema representation. Types are discovered
    /// recursively: complex types and enums are registered in <paramref name="context"/>
    /// and referenced via <c>$ref</c>.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when an unsupported type is encountered, preventing silent schema drift.
    /// </exception>
    private static JsonNode MapType(Type type, GeneratorContext context)
    {
        // Unwrap Nullable<T>
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
            type = underlying;

        // Primitives
        if (PrimitiveMap.TryGetValue(type, out var prim))
        {
            var obj = new JsonObject { ["type"] = prim.type };
            if (prim.format is not null)
                obj["format"] = prim.format;
            return obj;
        }

        // Enums — use forward-compatible examples definition
        if (type.IsEnum)
        {
            var defName = type.Name;
            if (!context.Definitions.ContainsKey(defName))
                context.Definitions[defName] = BuildEnumDef(defName, type);
            return Ref(defName);
        }

        // Converter-backed types with known wire shapes
        if (type == typeof(PropertyBag))
        {
            const string defName = "PropertyBag";
            if (!context.Definitions.ContainsKey(defName))
                context.Definitions[defName] = BuildPropertyBagDef();
            return Ref(defName);
        }

        // Collections: IReadOnlyList<T>, List<T>, IList<T>, IEnumerable<T>, arrays
        if (TryGetCollectionItemType(type, out var itemType))
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = MapType(itemType, context),
                ["default"] = new JsonArray(),
            };
        }

        // Complex object types — recurse via STJ metadata
        if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
        {
            var defName = type.Name;
            if (context.Visiting.Contains(type))
                return Ref(defName); // break cycles

            if (!context.Definitions.ContainsKey(defName))
            {
                // Guard against name collisions from different namespaces
                if (context.DefinitionSourceTypes.TryGetValue(defName, out var existing) && existing != type)
                    throw new NotSupportedException(
                        $"Schema $defs name collision: '{defName}' maps to both {existing.FullName} and {type.FullName}.");

                context.Visiting.Add(type);
                context.DefinitionSourceTypes[defName] = type;

                var info = GetTypeInfo(type);
                var (props, required) = BuildObjectProperties(info, context);

                var def = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                };
                if (required.Count > 0)
                    def["required"] = required;

                context.Definitions[defName] = def;
                context.Visiting.Remove(type);
            }

            return Ref(defName);
        }

        throw new NotSupportedException(
            $"Cannot generate JSON Schema for type '{type.FullName}'. " +
            "Add a mapping or converter to handle this type.");
    }

    /// <summary>
    /// Attempts to extract the item type from a collection type.
    /// </summary>
    private static bool TryGetCollectionItemType(Type type, out Type itemType)
    {
        // Arrays
        if (type.IsArray)
        {
            itemType = type.GetElementType()!;
            return true;
        }

        // Generic collections
        if (type.IsGenericType)
        {
            var genDef = type.GetGenericTypeDefinition();
            if (genDef == typeof(IReadOnlyList<>) || genDef == typeof(List<>) ||
                genDef == typeof(IList<>) || genDef == typeof(IEnumerable<>) ||
                genDef == typeof(IReadOnlyCollection<>) || genDef == typeof(ICollection<>))
            {
                itemType = type.GetGenericArguments()[0];
                return true;
            }
        }

        // Check interfaces (e.g. a type that implements IReadOnlyList<T>)
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            {
                itemType = iface.GetGenericArguments()[0];
                return true;
            }
        }

        itemType = null!;
        return false;
    }

    private static JsonObject Ref(string name) =>
        new() { ["$ref"] = $"#/$defs/{name}" };

    /// <summary>
    /// Builds a forward-compatible enum definition. Known values (excluding Unknown)
    /// are listed as <c>examples</c>; the type is <c>string</c> without a strict <c>enum</c>
    /// constraint, so unknown values from newer producers are accepted.
    /// </summary>
    private static JsonObject BuildEnumDef(string title, Type enumType)
    {
        var policy = JsonNamingPolicy.CamelCase;
        var examples = new JsonArray();
        foreach (var value in Enum.GetValues(enumType))
        {
            var name = value.ToString()!;
            if (name == "Unknown") continue; // producers must not emit Unknown
            examples.Add(policy.ConvertName(name));
        }

        return new JsonObject
        {
            ["title"] = title,
            ["type"] = "string",
            ["examples"] = examples,
            ["description"] = $"Known values: {string.Join(", ", examples.Select(e => e!.GetValue<string>()))}. " +
                              "Additional values may be added in future minor versions.",
        };
    }

    private static JsonObject BuildPropertyBagDef() => new()
    {
        ["type"] = "object",
        ["description"] = $"Extensible property bag. Keys must match pattern: {PropertyBag.KeyPattern}",
        ["propertyNames"] = new JsonObject { ["pattern"] = PropertyBag.KeyPattern },
        ["additionalProperties"] = true,
    };

    /// <summary>
    /// Gets STJ type info using the canonical <see cref="GraphJsonDefaults.Options"/>
    /// so naming, ignore-conditions, and converters match the real wire format.
    /// </summary>
    private static JsonTypeInfo GetTypeInfo(Type type)
        => GraphJsonDefaults.Options.GetTypeInfo(type);

    /// <summary>
    /// Tracks state during recursive schema generation: discovered definitions,
    /// cycle detection, and name collision guards.
    /// </summary>
    private sealed class GeneratorContext
    {
        public Dictionary<string, JsonObject> Definitions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Type> DefinitionSourceTypes { get; } = new(StringComparer.Ordinal);
        public HashSet<Type> Visiting { get; } = [];
    }
}
