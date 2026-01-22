// -----------------------------------------------------------------------
// <copyright file="VectorModelsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.VectorDb.UnitTests;

[TestClass]
public class VectorModelsTests
{
    [TestMethod]
    public void VectorPoint_RequiredProperties_AreSet()
    {
        // Arrange & Act
        var point = new VectorPoint
        {
            Id = "test-id",
            Vector = new[] { 0.1f, 0.2f, 0.3f }
        };

        // Assert
        point.Id.Should().Be("test-id");
        point.Vector.Should().HaveCount(3);
        point.Payload.Should().NotBeNull().And.BeEmpty();
    }

    [TestMethod]
    public void VectorPoint_WithPayload_StoresMetadata()
    {
        // Arrange & Act
        var point = new VectorPoint
        {
            Id = "test-id",
            Vector = new[] { 0.1f },
            Payload = new Dictionary<string, object>
            {
                ["name"] = "TestClass",
                ["language"] = "C#",
                ["line_start"] = 10
            }
        };

        // Assert
        point.Payload.Should().HaveCount(3);
        point.Payload["name"].Should().Be("TestClass");
        point.Payload["language"].Should().Be("C#");
        point.Payload["line_start"].Should().Be(10);
    }

    [TestMethod]
    public void VectorSearchResult_Properties_AreSet()
    {
        // Arrange & Act
        var result = new VectorSearchResult
        {
            Id = "result-id",
            Score = 0.95f,
            Payload = new Dictionary<string, object>
            {
                ["name"] = "TestMethod"
            }
        };

        // Assert
        result.Id.Should().Be("result-id");
        result.Score.Should().BeApproximately(0.95f, 0.001f);
        result.Payload["name"].Should().Be("TestMethod");
        result.Vector.Should().BeNull();
    }

    [TestMethod]
    public void VectorFilter_Must_AddsConditions()
    {
        // Arrange & Act
        var filter = new VectorFilter
        {
            Must = new List<FilterCondition>
            {
                new FilterCondition
                {
                    Field = "language",
                    Operator = FilterOperator.Equals,
                    Value = "C#"
                },
                new FilterCondition
                {
                    Field = "node_type",
                    Operator = FilterOperator.In,
                    Value = new[] { "Class", "Method" }
                }
            }
        };

        // Assert
        filter.Must.Should().HaveCount(2);
        filter.Must[0].Field.Should().Be("language");
        filter.Must[0].Operator.Should().Be(FilterOperator.Equals);
        filter.Must[1].Operator.Should().Be(FilterOperator.In);
    }

    [TestMethod]
    public void VectorStoreOptions_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var options = new VectorStoreOptions();

        // Assert
        options.Host.Should().Be("localhost");
        options.HttpPort.Should().Be(6333);
        options.GrpcPort.Should().Be(6334);
        options.UseGrpc.Should().BeTrue();
        options.UseTls.Should().BeFalse();
        options.ApiKey.Should().BeNull();
        options.NodesCollection.Should().Be("code_nodes");
        options.EdgesCollection.Should().Be("code_edges");
        options.DefaultVectorSize.Should().Be(768);
        options.Distance.Should().Be(DistanceMetric.Cosine);
        options.TimeoutSeconds.Should().Be(30);
    }

    [TestMethod]
    [DataRow(FilterOperator.Equals)]
    [DataRow(FilterOperator.NotEquals)]
    [DataRow(FilterOperator.GreaterThan)]
    [DataRow(FilterOperator.LessThan)]
    [DataRow(FilterOperator.In)]
    [DataRow(FilterOperator.NotIn)]
    [DataRow(FilterOperator.Contains)]
    public void FilterOperator_AllValues_AreDefined(FilterOperator op)
    {
        // Assert
        Enum.IsDefined(typeof(FilterOperator), op).Should().BeTrue();
    }

    [TestMethod]
    [DataRow(DistanceMetric.Cosine)]
    [DataRow(DistanceMetric.Euclidean)]
    [DataRow(DistanceMetric.DotProduct)]
    public void DistanceMetric_AllValues_AreDefined(DistanceMetric metric)
    {
        // Assert
        Enum.IsDefined(typeof(DistanceMetric), metric).Should().BeTrue();
    }

    [TestMethod]
    public void CollectionInfo_Properties_AreSet()
    {
        // Arrange & Act
        var info = new CollectionInfo
        {
            Name = "test_collection",
            VectorSize = 768,
            PointCount = 1000,
            Distance = DistanceMetric.Cosine
        };

        // Assert
        info.Name.Should().Be("test_collection");
        info.VectorSize.Should().Be(768);
        info.PointCount.Should().Be(1000);
        info.Distance.Should().Be(DistanceMetric.Cosine);
    }
}
