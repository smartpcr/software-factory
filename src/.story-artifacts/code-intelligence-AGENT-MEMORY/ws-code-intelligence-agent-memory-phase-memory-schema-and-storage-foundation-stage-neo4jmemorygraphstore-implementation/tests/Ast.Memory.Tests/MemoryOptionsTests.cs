// -----------------------------------------------------------------------
// <copyright file="MemoryOptionsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using Ast.Memory.Models;

[TestClass]
public class MemoryOptionsTests
{
    [TestMethod]
    public void DefaultSimilarityThreshold_Is085()
    {
        new MemoryOptions().SimilarityThreshold.Should().Be(0.85);
    }

    [TestMethod]
    public void DefaultStaleDecayFactor_Is05()
    {
        new MemoryOptions().StaleDecayFactor.Should().Be(0.5);
    }

    [TestMethod]
    public void DefaultMinorDecayFactor_Is09()
    {
        new MemoryOptions().MinorDecayFactor.Should().Be(0.9);
    }

    [TestMethod]
    public void DefaultArchiveConfidenceFloor_Is01()
    {
        new MemoryOptions().ArchiveConfidenceFloor.Should().Be(0.1);
    }

    [TestMethod]
    public void DefaultDecayHalfLifeDays_Is1095()
    {
        new MemoryOptions().DecayHalfLifeDays.Should().Be(1095);
    }

    [TestMethod]
    public void DefaultTokenBudgetDefault_Is8000()
    {
        new MemoryOptions().TokenBudgetDefault.Should().Be(8_000);
    }

    [TestMethod]
    public void DefaultTokenBudgetCeiling_Is32000()
    {
        new MemoryOptions().TokenBudgetCeiling.Should().Be(32_000);
    }

    [TestMethod]
    public void DefaultIncrementalSyncDebounceMs_Is1000()
    {
        new MemoryOptions().IncrementalSyncDebounceMs.Should().Be(1_000);
    }

    [TestMethod]
    public void DefaultCentralityBoostFactor_Is05()
    {
        new MemoryOptions().CentralityBoostFactor.Should().Be(0.5);
    }

    [TestMethod]
    public void DefaultBackend_IsSqlite()
    {
        new MemoryOptions().Backend.Should().Be(MemoryBackend.Sqlite);
    }

    [TestMethod]
    public void ConsolidationModelName_DefaultsToNull()
    {
        new MemoryOptions().ConsolidationModelName.Should().BeNull();
    }

    [TestMethod]
    public void EmbeddingDimension_DefaultsTo768()
    {
        new MemoryOptions().EmbeddingDimension.Should().Be(768);
    }

    [TestMethod]
    public void EmbeddingDimension_IsConfigurable()
    {
        var opts = new MemoryOptions { EmbeddingDimension = 1536 };
        opts.EmbeddingDimension.Should().Be(1536);
    }

    [TestMethod]
    public void ScoringWeights_SumToOne()
    {
        var weights = new MemoryOptions().Scoring;
        weights.Sum.Should().BeApproximately(1.0, 1e-10);
    }

    [TestMethod]
    public void ScoringWeights_DefaultValues()
    {
        var w = new ScoringWeights();
        w.Structural.Should().Be(0.30);
        w.Semantic.Should().Be(0.25);
        w.Centrality.Should().Be(0.20);
        w.Recency.Should().Be(0.15);
        w.Expertise.Should().Be(0.10);
    }

    [TestMethod]
    public void ScoringWeights_CustomValues_SumCorrectly()
    {
        var w = new ScoringWeights
        {
            Structural = 0.40,
            Semantic = 0.20,
            Centrality = 0.15,
            Recency = 0.15,
            Expertise = 0.10,
        };

        w.Sum.Should().BeApproximately(1.0, 1e-10);
    }

    [TestMethod]
    public void MemoryGraphStatistics_HasSafeDefaults()
    {
        var stats = new MemoryGraphStatistics();
        stats.TotalNodes.Should().Be(0);
        stats.TotalEdges.Should().Be(0);
        stats.TotalExpertiseEntries.Should().Be(0);
        stats.ArchivedExpertiseEntries.Should().Be(0);
        stats.LastUpdated.Should().BeNull();
        stats.LayerStats.Should().NotBeNull().And.BeEmpty();
    }
}
