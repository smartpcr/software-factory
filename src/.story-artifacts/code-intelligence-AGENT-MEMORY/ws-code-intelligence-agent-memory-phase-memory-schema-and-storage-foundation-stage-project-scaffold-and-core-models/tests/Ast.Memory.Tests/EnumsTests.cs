// -----------------------------------------------------------------------
// <copyright file="EnumsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using Ast.Core;
using Ast.Memory.Models;

[TestClass]
public class EnumsTests
{
    [TestMethod]
    public void ExpertiseKind_HasAllExpectedValues()
    {
        Enum.GetValues<ExpertiseKind>().Should().HaveCount(4);
        Enum.IsDefined(ExpertiseKind.Pattern).Should().BeTrue();
        Enum.IsDefined(ExpertiseKind.Pitfall).Should().BeTrue();
        Enum.IsDefined(ExpertiseKind.Preference).Should().BeTrue();
        Enum.IsDefined(ExpertiseKind.Insight).Should().BeTrue();
    }

    [TestMethod]
    public void RetrievalMode_HasAllExpectedValues()
    {
        Enum.GetValues<RetrievalMode>().Should().HaveCount(5);
        Enum.IsDefined(RetrievalMode.StructuralDrill).Should().BeTrue();
        Enum.IsDefined(RetrievalMode.CallTrace).Should().BeTrue();
        Enum.IsDefined(RetrievalMode.SemanticSearch).Should().BeTrue();
        Enum.IsDefined(RetrievalMode.ExpertiseRecall).Should().BeTrue();
        Enum.IsDefined(RetrievalMode.Hybrid).Should().BeTrue();
    }

    [TestMethod]
    public void MemoryBackend_HasAllExpectedValues()
    {
        Enum.GetValues<MemoryBackend>().Should().HaveCount(2);
        Enum.IsDefined(MemoryBackend.Sqlite).Should().BeTrue();
        Enum.IsDefined(MemoryBackend.Neo4j).Should().BeTrue();
    }

    [TestMethod]
    public void HyperedgeType_MemoryRelation_IsDefined()
    {
        Enum.IsDefined(typeof(HyperedgeType), HyperedgeType.MemoryRelation).Should().BeTrue();
    }

    [TestMethod]
    public void HyperedgeType_AllPreExistingValues_StillDefined()
    {
        // Additive-only change: verify no regression on existing HyperedgeType values
        Enum.IsDefined(typeof(HyperedgeType), HyperedgeType.ClassDefinition).Should().BeTrue();
        Enum.IsDefined(typeof(HyperedgeType), HyperedgeType.MethodDefinition).Should().BeTrue();
        Enum.IsDefined(typeof(HyperedgeType), HyperedgeType.MethodInvocation).Should().BeTrue();
        Enum.IsDefined(typeof(HyperedgeType), HyperedgeType.Implements).Should().BeTrue();
        Enum.IsDefined(typeof(HyperedgeType), HyperedgeType.Extends).Should().BeTrue();
        Enum.IsDefined(typeof(HyperedgeType), HyperedgeType.DataFlow).Should().BeTrue();
        Enum.IsDefined(typeof(HyperedgeType), HyperedgeType.CrossLanguageMethodCall).Should().BeTrue();
    }

    [TestMethod]
    public void Hyperedge_CanBeCreatedWithMemoryRelationType()
    {
        var edge = new Hyperedge
        {
            Id = "mem-edge-1",
            EdgeType = HyperedgeType.MemoryRelation,
            NodeIds = ["node-1", "node-2"],
            Roles = new Dictionary<string, string>
            {
                ["node-1"] = "source",
                ["node-2"] = "target",
            },
            Metadata = new Dictionary<string, object?>
            {
                ["SemanticRelation"] = "CALLS",
            },
        };

        edge.EdgeType.Should().Be(HyperedgeType.MemoryRelation);
        edge.Cardinality.Should().Be(2);
    }
}
