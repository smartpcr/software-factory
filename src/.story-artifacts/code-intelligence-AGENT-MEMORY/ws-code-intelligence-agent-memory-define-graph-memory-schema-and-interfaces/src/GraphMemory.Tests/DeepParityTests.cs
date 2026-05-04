using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphMemory.Models;
using GraphMemory.Serialization;

namespace GraphMemory.Tests;

/// <summary>
/// Deep parity tests that verify C# types match the JSON Schema at the
/// property level — names, required sets, and structural shape.
/// </summary>
public class DeepParityTests
{
    private static readonly JsonDocument SchemaDoc;

    static DeepParityTests()
    {
        var schemaPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "json-schema", "graph-memory.schema.json");
        SchemaDoc = JsonDocument.Parse(File.ReadAllText(schemaPath));
    }

    // -----------------------------------------------------------------------
    // Per-kind property name parity
    // -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(NodeKindData))]
    public void Node_Property_Names_Match_Schema(NodeKind kind, Type propsType, string wireKind)
    {
        var schemaProps = GetSchemaNodePropertiesFields(wireKind);
        var csharpProps = GetJsonPropertyNames(propsType);

        // Every C# property must appear in schema
        foreach (var prop in csharpProps.Keys)
        {
            Assert.True(schemaProps.ContainsKey(prop),
                $"NodeKind.{kind}: C# property '{prop}' not found in JSON Schema properties for '{wireKind}'");
        }

        // Every schema property must appear in C#
        foreach (var prop in schemaProps.Keys)
        {
            Assert.True(csharpProps.ContainsKey(prop),
                $"NodeKind.{kind}: JSON Schema property '{prop}' not found in C# type {propsType.Name}");
        }
    }

    [Theory]
    [MemberData(nameof(EdgeKindData))]
    public void Edge_Property_Names_Match_Schema(EdgeKind kind, Type propsType, string wireKind)
    {
        var schemaProps = GetSchemaEdgePropertiesFields(wireKind);
        var csharpProps = GetJsonPropertyNames(propsType);

        foreach (var prop in csharpProps.Keys)
        {
            Assert.True(schemaProps.ContainsKey(prop),
                $"EdgeKind.{kind}: C# property '{prop}' not found in JSON Schema properties for '{wireKind}'");
        }

        foreach (var prop in schemaProps.Keys)
        {
            Assert.True(csharpProps.ContainsKey(prop),
                $"EdgeKind.{kind}: JSON Schema property '{prop}' not found in C# type {propsType.Name}");
        }
    }

    // -----------------------------------------------------------------------
    // Required field parity
    // -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(NodeKindData))]
    public void Node_Required_Fields_Match_Schema(NodeKind kind, Type propsType, string wireKind)
    {
        var schemaRequired = GetSchemaNodeRequiredFields(wireKind);
        var csharpRequired = GetRequiredProperties(propsType);

        Assert.True(schemaRequired.SetEquals(csharpRequired),
            $"NodeKind.{kind}: Required field mismatch.\n" +
            $"  Schema requires: [{string.Join(", ", schemaRequired.OrderBy(x => x))}]\n" +
            $"  C# requires:    [{string.Join(", ", csharpRequired.OrderBy(x => x))}]");
    }

    [Theory]
    [MemberData(nameof(EdgeKindData))]
    public void Edge_Required_Fields_Match_Schema(EdgeKind kind, Type propsType, string wireKind)
    {
        var schemaRequired = GetSchemaEdgeRequiredFields(wireKind);
        var csharpRequired = GetRequiredProperties(propsType);

        Assert.True(schemaRequired.SetEquals(csharpRequired),
            $"EdgeKind.{kind}: Required field mismatch.\n" +
            $"  Schema requires: [{string.Join(", ", schemaRequired.OrderBy(x => x))}]\n" +
            $"  C# requires:    [{string.Join(", ", csharpRequired.OrderBy(x => x))}]");
    }

    // -----------------------------------------------------------------------
    // Common node/edge fields parity
    // -----------------------------------------------------------------------

    [Fact]
    public void GraphNode_Common_Fields_Match_Schema()
    {
        // Get common node fields from schema (every node variant has them)
        var firstNodeVariant = SchemaDoc.RootElement
            .GetProperty("$defs").GetProperty("GraphNode").GetProperty("oneOf")[0];
        var schemaFields = new HashSet<string>();
        foreach (var prop in firstNodeVariant.GetProperty("properties").EnumerateObject())
            schemaFields.Add(prop.Name);

        var csharpFields = GetJsonPropertyNames(typeof(GraphNode));

        Assert.True(schemaFields.SetEquals(csharpFields.Keys.ToHashSet()),
            $"GraphNode common fields mismatch.\n" +
            $"  Schema: [{string.Join(", ", schemaFields.OrderBy(x => x))}]\n" +
            $"  C#:     [{string.Join(", ", csharpFields.Keys.OrderBy(x => x))}]");
    }

    [Fact]
    public void GraphEdge_Common_Fields_Match_Schema()
    {
        var firstEdgeVariant = SchemaDoc.RootElement
            .GetProperty("$defs").GetProperty("GraphEdge").GetProperty("oneOf")[0];
        var schemaFields = new HashSet<string>();
        foreach (var prop in firstEdgeVariant.GetProperty("properties").EnumerateObject())
            schemaFields.Add(prop.Name);

        var csharpFields = GetJsonPropertyNames(typeof(GraphEdge));

        Assert.True(schemaFields.SetEquals(csharpFields.Keys.ToHashSet()),
            $"GraphEdge common fields mismatch.\n" +
            $"  Schema: [{string.Join(", ", schemaFields.OrderBy(x => x))}]\n" +
            $"  C#:     [{string.Join(", ", csharpFields.Keys.OrderBy(x => x))}]");
    }

    [Fact]
    public void GraphMetadata_Fields_Match_Schema()
    {
        var schemaMeta = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("GraphMetadata");
        var schemaFields = new HashSet<string>();
        foreach (var prop in schemaMeta.GetProperty("properties").EnumerateObject())
            schemaFields.Add(prop.Name);

        var csharpFields = GetJsonPropertyNames(typeof(GraphMetadata));

        Assert.True(schemaFields.SetEquals(csharpFields.Keys.ToHashSet()),
            $"GraphMetadata fields mismatch.\n" +
            $"  Schema: [{string.Join(", ", schemaFields.OrderBy(x => x))}]\n" +
            $"  C#:     [{string.Join(", ", csharpFields.Keys.OrderBy(x => x))}]");
    }

    [Fact]
    public void Provenance_Fields_Match_Schema()
    {
        var schemaDef = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("Provenance");
        var schemaFields = new HashSet<string>();
        foreach (var prop in schemaDef.GetProperty("properties").EnumerateObject())
            schemaFields.Add(prop.Name);

        var csharpFields = GetJsonPropertyNames(typeof(Provenance));

        Assert.True(schemaFields.SetEquals(csharpFields.Keys.ToHashSet()),
            $"Provenance fields mismatch.\n" +
            $"  Schema: [{string.Join(", ", schemaFields.OrderBy(x => x))}]\n" +
            $"  C#:     [{string.Join(", ", csharpFields.Keys.OrderBy(x => x))}]");
    }

    [Fact]
    public void VersionRange_Fields_Match_Schema()
    {
        var schemaDef = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("VersionRange");
        var schemaFields = new HashSet<string>();
        foreach (var prop in schemaDef.GetProperty("properties").EnumerateObject())
            schemaFields.Add(prop.Name);

        var csharpFields = GetJsonPropertyNames(typeof(VersionRange));

        Assert.True(schemaFields.SetEquals(csharpFields.Keys.ToHashSet()),
            $"VersionRange fields mismatch.\n" +
            $"  Schema: [{string.Join(", ", schemaFields.OrderBy(x => x))}]\n" +
            $"  C#:     [{string.Join(", ", csharpFields.Keys.OrderBy(x => x))}]");
    }

    [Fact]
    public void CodeGraph_Root_Fields_Match_Schema()
    {
        var schemaFields = new HashSet<string>();
        foreach (var prop in SchemaDoc.RootElement.GetProperty("properties").EnumerateObject())
            schemaFields.Add(prop.Name);

        var csharpFields = GetJsonPropertyNames(typeof(CodeGraph));

        Assert.True(schemaFields.SetEquals(csharpFields.Keys.ToHashSet()),
            $"CodeGraph root fields mismatch.\n" +
            $"  Schema: [{string.Join(", ", schemaFields.OrderBy(x => x))}]\n" +
            $"  C#:     [{string.Join(", ", csharpFields.Keys.OrderBy(x => x))}]");
    }

    // -----------------------------------------------------------------------
    // Supporting defs parity
    // -----------------------------------------------------------------------

    [Fact]
    public void FunctionParameter_Fields_Match_Schema()
    {
        var schemaDef = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("FunctionParameter");
        var schemaFields = new HashSet<string>();
        foreach (var prop in schemaDef.GetProperty("properties").EnumerateObject())
            schemaFields.Add(prop.Name);

        var csharpFields = GetJsonPropertyNames(typeof(FunctionParameter));

        Assert.True(schemaFields.SetEquals(csharpFields.Keys.ToHashSet()),
            $"FunctionParameter mismatch: schema=[{string.Join(",", schemaFields.OrderBy(x => x))}] c#=[{string.Join(",", csharpFields.Keys.OrderBy(x => x))}]");
    }

    [Fact]
    public void CallSite_Fields_Match_Schema()
    {
        var schemaDef = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("CallSite");
        var schemaFields = new HashSet<string>();
        foreach (var prop in schemaDef.GetProperty("properties").EnumerateObject())
            schemaFields.Add(prop.Name);

        var csharpFields = GetJsonPropertyNames(typeof(CallSite));

        Assert.True(schemaFields.SetEquals(csharpFields.Keys.ToHashSet()),
            $"CallSite mismatch: schema=[{string.Join(",", schemaFields.OrderBy(x => x))}] c#=[{string.Join(",", csharpFields.Keys.OrderBy(x => x))}]");
    }

    [Fact]
    public void ImportedSymbol_Fields_Match_Schema()
    {
        var schemaDef = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("ImportedSymbol");
        var schemaFields = new HashSet<string>();
        foreach (var prop in schemaDef.GetProperty("properties").EnumerateObject())
            schemaFields.Add(prop.Name);

        var csharpFields = GetJsonPropertyNames(typeof(ImportedSymbol));

        Assert.True(schemaFields.SetEquals(csharpFields.Keys.ToHashSet()),
            $"ImportedSymbol mismatch: schema=[{string.Join(",", schemaFields.OrderBy(x => x))}] c#=[{string.Join(",", csharpFields.Keys.OrderBy(x => x))}]");
    }

    // -----------------------------------------------------------------------
    // Enum value parity
    // -----------------------------------------------------------------------

    [Fact]
    public void Layer_Enum_Values_Match_Schema()
    {
        var schemaDef = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("Layer");
        var schemaValues = new HashSet<string>();
        foreach (var val in schemaDef.GetProperty("enum").EnumerateArray())
            schemaValues.Add(val.GetString()!);

        var csharpValues = new HashSet<string>(SnakeCaseEnumMaps.LayerToString.Values);

        Assert.True(schemaValues.SetEquals(csharpValues),
            $"Layer enum mismatch: schema=[{string.Join(",", schemaValues.OrderBy(x => x))}] c#=[{string.Join(",", csharpValues.OrderBy(x => x))}]");
    }

    [Fact]
    public void InheritanceMechanism_Enum_Values_Match_Schema()
    {
        var schemaDef = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("InheritanceMechanism");
        var schemaValues = new HashSet<string>();
        foreach (var val in schemaDef.GetProperty("enum").EnumerateArray())
            schemaValues.Add(val.GetString()!);

        var csharpValues = new HashSet<string>(SnakeCaseEnumMaps.InheritanceMechanismToString.Values);

        Assert.True(schemaValues.SetEquals(csharpValues),
            $"InheritanceMechanism enum mismatch: schema=[{string.Join(",", schemaValues.OrderBy(x => x))}] c#=[{string.Join(",", csharpValues.OrderBy(x => x))}]");
    }

    [Fact]
    public void SymbolKind_Enum_Values_Match_Schema()
    {
        var schemaDef = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("SymbolKind");
        var schemaValues = new HashSet<string>();
        foreach (var val in schemaDef.GetProperty("enum").EnumerateArray())
            schemaValues.Add(val.GetString()!);

        var csharpValues = new HashSet<string>(SnakeCaseEnumMaps.SymbolKindToString.Values);

        Assert.True(schemaValues.SetEquals(csharpValues),
            $"SymbolKind enum mismatch: schema=[{string.Join(",", schemaValues.OrderBy(x => x))}] c#=[{string.Join(",", csharpValues.OrderBy(x => x))}]");
    }

    // -----------------------------------------------------------------------
    // Schema disallows additional properties
    // -----------------------------------------------------------------------

    [Fact]
    public void Schema_Disallows_Additional_Properties_On_All_Node_Variants()
    {
        var graphNode = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("GraphNode");
        foreach (var variant in graphNode.GetProperty("oneOf").EnumerateArray())
        {
            var kindConst = variant.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString()!;
            Assert.True(variant.GetProperty("additionalProperties").GetBoolean() == false,
                $"Node kind '{kindConst}' does not set additionalProperties: false");

            // Also check properties bag
            var propsSchema = variant.GetProperty("properties").GetProperty("properties");
            if (propsSchema.TryGetProperty("additionalProperties", out var ap))
            {
                Assert.False(ap.GetBoolean(),
                    $"Node kind '{kindConst}' properties bag allows additional properties");
            }
        }
    }

    [Fact]
    public void Schema_Disallows_Additional_Properties_On_All_Edge_Variants()
    {
        var graphEdge = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("GraphEdge");
        foreach (var variant in graphEdge.GetProperty("oneOf").EnumerateArray())
        {
            var kindConst = variant.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString()!;
            Assert.True(variant.GetProperty("additionalProperties").GetBoolean() == false,
                $"Edge kind '{kindConst}' does not set additionalProperties: false");

            var propsSchema = variant.GetProperty("properties").GetProperty("properties");
            if (propsSchema.TryGetProperty("additionalProperties", out var ap))
            {
                Assert.False(ap.GetBoolean(),
                    $"Edge kind '{kindConst}' properties bag allows additional properties");
            }
        }
    }

    // -----------------------------------------------------------------------
    // MemberData providers
    // -----------------------------------------------------------------------

    public static IEnumerable<object[]> NodeKindData()
    {
        foreach (var (kind, type) in NodePropertiesConverter.KindToType)
        {
            var wire = SnakeCaseEnumMaps.NodeKindToString[kind];
            yield return [kind, type, wire];
        }
    }

    public static IEnumerable<object[]> EdgeKindData()
    {
        foreach (var (kind, type) in EdgePropertiesConverter.KindToType)
        {
            var wire = SnakeCaseEnumMaps.EdgeKindToString[kind];
            yield return [kind, type, wire];
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Dictionary<string, PropertyInfo> GetJsonPropertyNames(Type type)
    {
        var result = new Dictionary<string, PropertyInfo>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Skip non-serialized properties (like CurrentSchemaVersion const)
            if (prop.GetMethod == null) continue;

            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            var wireName = attr?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            result[wireName] = prop;
        }
        return result;
    }

    private static HashSet<string> GetRequiredProperties(Type type)
    {
        var result = new HashSet<string>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetMethod == null) continue;

            var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>();
            var wireName = attr?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(prop.Name);

            // Check if property has 'required' keyword
            var isRequired = prop.GetCustomAttributes()
                .Any(a => a.GetType().Name == "RequiredMemberAttribute")
                || prop.PropertyType.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) == null
                    && prop.GetCustomAttribute<JsonPropertyNameAttribute>() != null
                    && !prop.PropertyType.IsEnum;

            // More reliable: check for 'required' modifier via custom attributes
            var requiredAttr = prop.CustomAttributes
                .Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute");

            if (requiredAttr)
                result.Add(wireName);
        }
        return result;
    }

    private Dictionary<string, JsonElement> GetSchemaNodePropertiesFields(string wireKind)
    {
        var graphNode = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("GraphNode");
        foreach (var variant in graphNode.GetProperty("oneOf").EnumerateArray())
        {
            var kindConst = variant.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString();
            if (kindConst == wireKind)
            {
                var propsSchema = variant.GetProperty("properties").GetProperty("properties");
                var result = new Dictionary<string, JsonElement>();
                if (propsSchema.TryGetProperty("properties", out var innerProps))
                {
                    foreach (var p in innerProps.EnumerateObject())
                        result[p.Name] = p.Value;
                }
                return result;
            }
        }
        throw new InvalidOperationException($"Node kind '{wireKind}' not found in schema");
    }

    private Dictionary<string, JsonElement> GetSchemaEdgePropertiesFields(string wireKind)
    {
        var graphEdge = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("GraphEdge");
        foreach (var variant in graphEdge.GetProperty("oneOf").EnumerateArray())
        {
            var kindConst = variant.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString();
            if (kindConst == wireKind)
            {
                var propsSchema = variant.GetProperty("properties").GetProperty("properties");
                var result = new Dictionary<string, JsonElement>();
                if (propsSchema.TryGetProperty("properties", out var innerProps))
                {
                    foreach (var p in innerProps.EnumerateObject())
                        result[p.Name] = p.Value;
                }
                return result;
            }
        }
        throw new InvalidOperationException($"Edge kind '{wireKind}' not found in schema");
    }

    private HashSet<string> GetSchemaNodeRequiredFields(string wireKind)
    {
        var graphNode = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("GraphNode");
        foreach (var variant in graphNode.GetProperty("oneOf").EnumerateArray())
        {
            var kindConst = variant.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString();
            if (kindConst == wireKind)
            {
                var propsSchema = variant.GetProperty("properties").GetProperty("properties");
                if (propsSchema.TryGetProperty("required", out var req))
                {
                    var result = new HashSet<string>();
                    foreach (var r in req.EnumerateArray())
                        result.Add(r.GetString()!);
                    return result;
                }
                return [];
            }
        }
        throw new InvalidOperationException($"Node kind '{wireKind}' not found in schema");
    }

    private HashSet<string> GetSchemaEdgeRequiredFields(string wireKind)
    {
        var graphEdge = SchemaDoc.RootElement.GetProperty("$defs").GetProperty("GraphEdge");
        foreach (var variant in graphEdge.GetProperty("oneOf").EnumerateArray())
        {
            var kindConst = variant.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString();
            if (kindConst == wireKind)
            {
                var propsSchema = variant.GetProperty("properties").GetProperty("properties");
                if (propsSchema.TryGetProperty("required", out var req))
                {
                    var result = new HashSet<string>();
                    foreach (var r in req.EnumerateArray())
                        result.Add(r.GetString()!);
                    return result;
                }
                return [];
            }
        }
        throw new InvalidOperationException($"Edge kind '{wireKind}' not found in schema");
    }
}
