// -----------------------------------------------------------------------
// <copyright file="Neo4jMetadataRoundTripTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System.Text.Json;
using Ast.Memory.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests that Neo4j metadata round-trip (JSON serialize → deserialize) produces
/// native .NET types (string, int, bool) rather than <see cref="JsonElement"/> boxes.
/// This validates the fix for the production issue where
/// <c>Metadata["MemoryLayer"] == "semantic"</c> failed because the value was a
/// <see cref="JsonElement"/> instead of a <see cref="string"/>.
/// </summary>
[TestClass]
public class Neo4jMetadataRoundTripTests
{
    /// <summary>
    /// Given Topic node metadata serialized to JSON and then deserialized via
    /// <see cref="Neo4jMemoryGraphStore.DeserializeMetadata"/>,
    /// When metadata values are inspected,
    /// Then <c>Metadata["MemoryLayer"]</c> is a native <see cref="string"/>,
    /// <c>Metadata["Summary"]</c> is a native <see cref="string"/>,
    /// and <c>Metadata["MemberCount"]</c> is a numeric type that can be cast to int.
    /// </summary>
    [TestMethod]
    public void DeserializeMetadata_TopicNodeMetadata_ProducesNativeTypes()
    {
        // Arrange: simulate what Neo4j stores and returns — JSON string
        var originalMetadata = new Dictionary<string, object?>
        {
            ["MemoryLayer"] = "semantic",
            ["Summary"] = "LLM-generated summary for payment processing cluster",
            ["MemberCount"] = 7
        };

        var json = JsonSerializer.Serialize(originalMetadata);

        // Simulate Neo4j property bag
        var props = new Dictionary<string, object>
        {
            ["metadata"] = json
        };

        // Act
        var deserialized = Neo4jMemoryGraphStore.DeserializeMetadata(
            props,
            "metadata",
            NullLogger.Instance,
            "topic:test1234");

        // Assert — values must be native .NET types, NOT JsonElement
        deserialized["MemoryLayer"].Should().BeOfType<string>()
            .Which.Should().Be("semantic");

        deserialized["Summary"].Should().BeOfType<string>()
            .Which.Should().Be("LLM-generated summary for payment processing cluster");

        // MemberCount: JSON numbers from STJ polymorphic serialization may be int or double
        var memberCountValue = deserialized["MemberCount"];
        memberCountValue.Should().NotBeNull();
        memberCountValue.Should().NotBeOfType<JsonElement>(
            "metadata values must not be JsonElement after round-trip");
        Convert.ToInt32(memberCountValue).Should().Be(7,
            "MemberCount must round-trip to a value convertible to int");

        // Direct equality checks that would fail with JsonElement
        (deserialized["MemoryLayer"]!.Equals("semantic")).Should().BeTrue(
            "metadata string values must support direct == comparison after round-trip");
    }

    /// <summary>
    /// Validates <see cref="Neo4jMemoryGraphStore.MaterializeJsonElement"/> converts
    /// all JSON value kinds to appropriate .NET types.
    /// </summary>
    [TestMethod]
    public void MaterializeJsonElement_AllValueKinds_ReturnsNativeTypes()
    {
        // String
        var strElement = JsonSerializer.Deserialize<JsonElement>("\"hello\"");
        Neo4jMemoryGraphStore.MaterializeJsonElement(strElement).Should().Be("hello");

        // Integer
        var intElement = JsonSerializer.Deserialize<JsonElement>("42");
        Neo4jMemoryGraphStore.MaterializeJsonElement(intElement).Should().Be(42);

        // Double
        var doubleElement = JsonSerializer.Deserialize<JsonElement>("3.14");
        Neo4jMemoryGraphStore.MaterializeJsonElement(doubleElement).Should().Be(3.14);

        // Boolean true
        var trueElement = JsonSerializer.Deserialize<JsonElement>("true");
        Neo4jMemoryGraphStore.MaterializeJsonElement(trueElement).Should().Be(true);

        // Boolean false
        var falseElement = JsonSerializer.Deserialize<JsonElement>("false");
        Neo4jMemoryGraphStore.MaterializeJsonElement(falseElement).Should().Be(false);

        // Null
        var nullElement = JsonSerializer.Deserialize<JsonElement>("null");
        Neo4jMemoryGraphStore.MaterializeJsonElement(nullElement).Should().BeNull();
    }

    /// <summary>
    /// Given BELONGS_TO_CLUSTER edge metadata serialized to JSON,
    /// When deserialized via <see cref="Neo4jMemoryGraphStore.DeserializeMetadata"/>,
    /// Then <c>Metadata["SemanticRelation"]</c> equals <c>"BELONGS_TO_CLUSTER"</c>
    /// as a native string (not JsonElement).
    /// </summary>
    [TestMethod]
    public void DeserializeMetadata_ClusterEdgeMetadata_SemanticRelationIsString()
    {
        var metadata = new Dictionary<string, object?>
        {
            ["SemanticRelation"] = "BELONGS_TO_CLUSTER",
            ["MemoryLayer"] = "semantic"
        };

        var json = JsonSerializer.Serialize(metadata);
        var props = new Dictionary<string, object> { ["metadata"] = json };

        var deserialized = Neo4jMemoryGraphStore.DeserializeMetadata(
            props, "metadata", NullLogger.Instance, "edge:test");

        deserialized["SemanticRelation"].Should().BeOfType<string>()
            .Which.Should().Be("BELONGS_TO_CLUSTER");

        // This is the exact comparison that would fail with JsonElement
        var sr = deserialized["SemanticRelation"];
        (sr is string s && s == "BELONGS_TO_CLUSTER").Should().BeTrue(
            "SemanticRelation must pass 'is string' pattern matching after Neo4j round-trip");
    }

    /// <summary>
    /// Given malformed JSON in the metadata property,
    /// When deserialized via <see cref="Neo4jMemoryGraphStore.DeserializeMetadata"/>,
    /// Then an empty dictionary is returned (error is logged, not thrown).
    /// </summary>
    [TestMethod]
    public void DeserializeMetadata_MalformedJson_ReturnsEmptyDict()
    {
        var props = new Dictionary<string, object> { ["metadata"] = "not-valid-json{" };

        var deserialized = Neo4jMemoryGraphStore.DeserializeMetadata(
            props, "metadata", NullLogger.Instance, "broken:node");

        deserialized.Should().BeEmpty(
            "malformed JSON should produce an empty dict, not throw");
    }

    /// <summary>
    /// Given no metadata property present,
    /// When deserialized via <see cref="Neo4jMemoryGraphStore.DeserializeMetadata"/>,
    /// Then an empty dictionary is returned.
    /// </summary>
    [TestMethod]
    public void DeserializeMetadata_MissingProperty_ReturnsEmptyDict()
    {
        var props = new Dictionary<string, object>();

        var deserialized = Neo4jMemoryGraphStore.DeserializeMetadata(
            props, "metadata", NullLogger.Instance, "no:meta");

        deserialized.Should().BeEmpty();
    }
}