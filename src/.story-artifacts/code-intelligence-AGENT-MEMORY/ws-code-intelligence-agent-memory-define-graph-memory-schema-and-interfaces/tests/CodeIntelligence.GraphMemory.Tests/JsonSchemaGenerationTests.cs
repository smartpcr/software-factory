using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIntelligence.GraphMemory.Schema;
using CodeIntelligence.GraphMemory.Serialization;
using Xunit;

namespace CodeIntelligence.GraphMemory.Tests;

public class JsonSchemaGenerationTests
{
    [Fact]
    public void GeneratedSchema_IsValidJson()
    {
        var schema = JsonSchemaGenerator.Generate();

        Assert.False(string.IsNullOrWhiteSpace(schema));

        var doc = JsonDocument.Parse(schema);
        Assert.NotNull(doc);
    }

    [Fact]
    public void GeneratedSchema_ContainsExpectedDefinitions()
    {
        var schema = JsonSchemaGenerator.Generate();

        Assert.Contains("GraphNode", schema);
        Assert.Contains("GraphEdge", schema);
        Assert.Contains("SchemaVersion", schema);
        Assert.Contains("SourceLocation", schema);
        Assert.Contains("NodeKind", schema);
        Assert.Contains("EdgeKind", schema);
        Assert.Contains("PropertyBag", schema);
    }

    [Fact]
    public void GeneratedSchema_HasTitleAndDescription()
    {
        var schema = JsonSchemaGenerator.Generate();

        Assert.Contains("\"title\"", schema);
        Assert.Contains("\"description\"", schema);
        Assert.Contains("v1.0", schema);
    }

    [Fact]
    public void GeneratedSchema_EnumsAreStringType()
    {
        var schema = JsonSchemaGenerator.Generate();

        // Enums should use type: "string" for forward-compat
        // Known values should appear as examples, not strict enum constraints
        Assert.Contains("\"examples\"", schema);
        Assert.Contains("\"repo\"", schema);
        Assert.Contains("\"contains\"", schema);
        Assert.Contains("\"expertise\"", schema);
    }

    [Fact]
    public void GeneratedSchema_ExcludesUnknownFromEnumExamples()
    {
        var schema = JsonSchemaGenerator.Generate();
        var doc = JsonDocument.Parse(schema);

        var defs = doc.RootElement.GetProperty("$defs");
        var nodeKind = defs.GetProperty("NodeKind");
        var examples = nodeKind.GetProperty("examples");

        var values = new List<string>();
        foreach (var elem in examples.EnumerateArray())
            values.Add(elem.GetString()!);

        Assert.DoesNotContain("unknown", values);
        Assert.Contains("repo", values);
        Assert.Contains("file", values);
    }

    [Fact]
    public void GeneratedSchema_UsesCamelCasePropertyNames()
    {
        var schema = JsonSchemaGenerator.Generate();

        Assert.Contains("\"schemaVersion\"", schema);
        Assert.Contains("\"sourceNodeId\"", schema);
        Assert.Contains("\"qualifiedName\"", schema);
        Assert.Contains("\"filePath\"", schema);
    }

    [Fact]
    public void GeneratedSchema_DoesNotHaveAdditionalPropertiesFalse()
    {
        var schema = JsonSchemaGenerator.Generate();

        Assert.DoesNotContain("\"additionalProperties\": false", schema);
        Assert.DoesNotContain("\"additionalProperties\":false", schema);
    }

    [Fact]
    public void GeneratedSchema_DoesNotUseStrictEnumKeyword()
    {
        // Forward-compat: enums must NOT use the "enum" keyword,
        // only "examples". This ensures unknown values from newer
        // producers are accepted.
        var schema = JsonSchemaGenerator.Generate();
        var doc = JsonDocument.Parse(schema);
        var defs = doc.RootElement.GetProperty("$defs");

        foreach (var defName in new[] { "NodeKind", "EdgeKind" })
        {
            var def = defs.GetProperty(defName);
            Assert.False(
                def.TryGetProperty("enum", out _),
                $"$defs/{defName} must not use 'enum' keyword for forward-compatibility.");
            Assert.True(
                def.TryGetProperty("examples", out _),
                $"$defs/{defName} must use 'examples' to list known values.");
        }
    }

    [Fact]
    public void GeneratedSchema_HasSchemaId()
    {
        var schema = JsonSchemaGenerator.Generate();
        Assert.Contains("\"$id\"", schema);
        Assert.Contains("code-intelligence.dev/schemas/graph-memory/v1.0", schema);
    }

    [Fact]
    public void GeneratedSchema_PropertyBagHasKeyPattern()
    {
        var schema = JsonSchemaGenerator.Generate();
        Assert.Contains("propertyNames", schema);
        Assert.Contains("pattern", schema);
    }

    // ── Model-completeness: schema covers all STJ-serialized properties ──

    [Fact]
    public void GeneratedSchema_CoversAllGraphNodeProperties()
    {
        AssertSchemaCoversType("GraphNode", typeof(GraphNode));
    }

    [Fact]
    public void GeneratedSchema_CoversAllGraphEdgeProperties()
    {
        AssertSchemaCoversType("GraphEdge", typeof(GraphEdge));
    }

    [Fact]
    public void GeneratedSchema_CoversAllSourceLocationProperties()
    {
        AssertSchemaCoversType("SourceLocation", typeof(SourceLocation));
    }

    [Fact]
    public void GeneratedSchema_CoversAllSchemaVersionProperties()
    {
        AssertSchemaCoversType("SchemaVersion", typeof(SchemaVersion));
    }

    [Fact]
    public void GeneratedSchema_CoversAllRootProperties()
    {
        var schema = JsonSchemaGenerator.Generate();
        var doc = JsonDocument.Parse(schema);
        var schemaProps = doc.RootElement.GetProperty("properties");
        var typeInfo = GraphJsonDefaults.Options.GetTypeInfo(typeof(CodeKnowledgeGraph));

        foreach (var prop in typeInfo.Properties)
        {
            if (prop.Get is null && prop.Set is null) continue;
            Assert.True(schemaProps.TryGetProperty(prop.Name, out _),
                $"Root property '{prop.Name}' is missing from the JSON Schema.");
        }
    }

    [Fact]
    public void GeneratedSchema_DefinitionsDiscoveredAutomatically()
    {
        // Verify that all referenced types are discovered recursively —
        // the generator should not need a hardcoded list.
        var schema = JsonSchemaGenerator.Generate();
        var doc = JsonDocument.Parse(schema);
        var defs = doc.RootElement.GetProperty("$defs");

        // All types reachable from CodeKnowledgeGraph should have $defs entries.
        var expectedTypes = new[]
        {
            "SchemaVersion", "NodeKind", "EdgeKind",
            "SourceLocation", "PropertyBag", "GraphNode", "GraphEdge"
        };

        foreach (var typeName in expectedTypes)
        {
            Assert.True(defs.TryGetProperty(typeName, out _),
                $"$defs/{typeName} should be auto-discovered from CodeKnowledgeGraph.");
        }
    }

    private static void AssertSchemaCoversType(string defName, Type clrType)
    {
        var schema = JsonSchemaGenerator.Generate();
        var doc = JsonDocument.Parse(schema);
        var def = doc.RootElement.GetProperty("$defs").GetProperty(defName);
        var schemaProps = def.GetProperty("properties");

        var typeInfo = GraphJsonDefaults.Options.GetTypeInfo(clrType);
        foreach (var prop in typeInfo.Properties)
        {
            if (prop.Get is null && prop.Set is null) continue;
            Assert.True(schemaProps.TryGetProperty(prop.Name, out _),
                $"$defs/{defName}: property '{prop.Name}' (from C# type {clrType.Name}) is missing from the schema.");
        }
    }
}
