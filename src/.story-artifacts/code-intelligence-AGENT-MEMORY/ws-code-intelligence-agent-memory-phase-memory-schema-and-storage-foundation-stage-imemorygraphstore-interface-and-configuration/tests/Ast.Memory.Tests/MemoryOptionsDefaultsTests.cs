// -----------------------------------------------------------------------
// <copyright file="MemoryOptionsDefaultsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using Ast.Memory.Models;
using Xunit;

/// <summary>
/// Scenario: MemoryOptions defaults —
/// Given default MemoryOptions,
/// When constructed,
/// Then SimilarityThreshold=0.85, TokenBudgetDefault=8000, TokenBudgetCeiling=32000,
///      DecayHalfLifeDays=1095, ArchiveConfidenceFloor=0.1, CentralityBoostFactor=0.5,
///      and scoring weights sum to 1.00.
/// </summary>
public class MemoryOptionsDefaultsTests
{
    private readonly MemoryOptions options = new();

    [Fact]
    public void SimilarityThreshold_DefaultsTo085()
    {
        Assert.Equal(0.85, this.options.SimilarityThreshold);
    }

    [Fact]
    public void StaleDecayFactor_DefaultsTo05()
    {
        Assert.Equal(0.5, this.options.StaleDecayFactor);
    }

    [Fact]
    public void MinorDecayFactor_DefaultsTo09()
    {
        Assert.Equal(0.9, this.options.MinorDecayFactor);
    }

    [Fact]
    public void ArchiveConfidenceFloor_DefaultsTo01()
    {
        Assert.Equal(0.1, this.options.ArchiveConfidenceFloor);
    }

    [Fact]
    public void DecayHalfLifeDays_DefaultsTo1095()
    {
        Assert.Equal(1095, this.options.DecayHalfLifeDays);
    }

    [Fact]
    public void TokenBudgetDefault_DefaultsTo8000()
    {
        Assert.Equal(8000, this.options.TokenBudgetDefault);
    }

    [Fact]
    public void TokenBudgetCeiling_DefaultsTo32000()
    {
        Assert.Equal(32000, this.options.TokenBudgetCeiling);
    }

    [Fact]
    public void IncrementalSyncDebounceMs_DefaultsTo1000()
    {
        Assert.Equal(1000, this.options.IncrementalSyncDebounceMs);
    }

    [Fact]
    public void CentralityBoostFactor_DefaultsTo05()
    {
        Assert.Equal(0.5, this.options.CentralityBoostFactor);
    }

    [Fact]
    public void Backend_DefaultsToSqlite()
    {
        Assert.Equal(MemoryBackend.Sqlite, this.options.Backend);
    }

    [Fact]
    public void SqlitePath_DefaultsToMemoryDb()
    {
        Assert.Equal("memory.db", this.options.SqlitePath);
    }

    [Fact]
    public void Neo4jUri_DefaultsToNull()
    {
        Assert.Null(this.options.Neo4jUri);
    }

    [Fact]
    public void QdrantEndpoint_DefaultsToNull()
    {
        Assert.Null(this.options.QdrantEndpoint);
    }

    [Fact]
    public void ConsolidationModelName_DefaultsToNull()
    {
        Assert.Null(this.options.ConsolidationModelName);
    }

    [Fact]
    public void ScoringWeights_SumToOne()
    {
        var weights = this.options.ScoringWeights;
        Assert.Equal(1.0, weights.Sum, precision: 10);
    }

    [Fact]
    public void ScoringWeights_IndividualDefaults()
    {
        var w = this.options.ScoringWeights;
        Assert.Equal(0.30, w.Structural);
        Assert.Equal(0.25, w.Semantic);
        Assert.Equal(0.20, w.Centrality);
        Assert.Equal(0.15, w.Recency);
        Assert.Equal(0.10, w.Expertise);
    }

    [Fact]
    public void ScoringWeights_Validate_Passes_WithDefaults()
    {
        this.options.ScoringWeights.Validate();
    }

    [Fact]
    public void ScoringWeights_Validate_Throws_WhenSumIsNotOne()
    {
        var bad = new ScoringWeights { Structural = 0.5, Semantic = 0.5, Centrality = 0.5 };
        Assert.Throws<InvalidOperationException>(() => bad.Validate());
    }

    [Fact]
    public void ScoringWeights_Validate_Throws_WhenNegative()
    {
        var bad = new ScoringWeights { Structural = -0.1, Semantic = 0.5, Centrality = 0.3, Recency = 0.2, Expertise = 0.1 };
        Assert.Throws<InvalidOperationException>(() => bad.Validate());
    }
}