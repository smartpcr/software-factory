// -----------------------------------------------------------------------
// <copyright file="TierPromotionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory.UnitTests;

using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class TierPromotionTests
{
    private InMemoryAgentMemoryStore store = null!;
    private MemoryEvolver evolver = null!;

    [TestInitialize]
    public void Setup()
    {
        this.store = new InMemoryAgentMemoryStore();
        this.evolver = new MemoryEvolver(this.store);
    }

    [TestMethod]
    public async Task Promote_InteractionToQuery_WhenGroupThresholdMet()
    {
        // 3 file expertise records in same directory with high confidence + episode count
        for (var i = 0; i < 3; i++)
        {
            await this.store.UpsertExpertiseAsync(new ExpertiseRecord
            {
                Id = $"exp-{i}",
                Subject = $"src/parser/File{i}.cs",
                Category = ExpertiseCategory.FileExpertise,
                Tier = MemoryTier.Interaction,
                Confidence = 0.9,
                EpisodeCount = 5,
                DerivedFromEpisodeIds = [$"ep-{i}-1", $"ep-{i}-2", $"ep-{i}-3", $"ep-{i}-4", $"ep-{i}-5"]
            });
        }

        var result = await this.evolver.PromoteAsync(new PromotionOptions
        {
            MinConfidence = 0.8,
            MinEpisodeCount = 5,
            MinGroupSize = 3
        });

        result.InteractionToQuery.Should().Be(1);
        result.CreatedIds.Should().HaveCount(1);

        // Verify the promoted record exists at Query tier
        var promoted = await this.store.GetExpertiseAsync(result.CreatedIds[0]);
        promoted.Should().NotBeNull();
        promoted!.Tier.Should().Be(MemoryTier.Query);
        promoted.Category.Should().Be(ExpertiseCategory.NamespaceExpertise);
        promoted.Subject.Should().Contain("src/parser");
    }

    [TestMethod]
    public async Task Promote_CreatesPromotedToEdges()
    {
        for (var i = 0; i < 3; i++)
        {
            await this.store.UpsertExpertiseAsync(new ExpertiseRecord
            {
                Id = $"exp-{i}",
                Subject = $"src/parser/File{i}.cs",
                Category = ExpertiseCategory.FileExpertise,
                Tier = MemoryTier.Interaction,
                Confidence = 0.85,
                EpisodeCount = 6
            });
        }

        var result = await this.evolver.PromoteAsync(new PromotionOptions());

        // Check edges from source records to promoted record
        var promotedId = result.CreatedIds[0];
        var edges = await this.store.GetIncomingEdgesAsync(promotedId, MemoryEdgeType.PromotedTo);
        edges.Should().HaveCount(3);
    }

    [TestMethod]
    public async Task Promote_PreservesOriginalRecords()
    {
        for (var i = 0; i < 3; i++)
        {
            await this.store.UpsertExpertiseAsync(new ExpertiseRecord
            {
                Id = $"exp-{i}",
                Subject = $"src/parser/File{i}.cs",
                Category = ExpertiseCategory.FileExpertise,
                Tier = MemoryTier.Interaction,
                Confidence = 0.85,
                EpisodeCount = 6
            });
        }

        await this.evolver.PromoteAsync(new PromotionOptions());

        // Original records should still exist at Interaction tier
        for (var i = 0; i < 3; i++)
        {
            var original = await this.store.GetExpertiseAsync($"exp-{i}");
            original.Should().NotBeNull();
            original!.Tier.Should().Be(MemoryTier.Interaction);
        }
    }

    [TestMethod]
    public async Task Promote_BelowGroupThreshold_NoPromotion()
    {
        // Only 2 records — below MinGroupSize=3
        for (var i = 0; i < 2; i++)
        {
            await this.store.UpsertExpertiseAsync(new ExpertiseRecord
            {
                Id = $"exp-{i}",
                Subject = $"src/parser/File{i}.cs",
                Category = ExpertiseCategory.FileExpertise,
                Tier = MemoryTier.Interaction,
                Confidence = 0.9,
                EpisodeCount = 5
            });
        }

        var result = await this.evolver.PromoteAsync(new PromotionOptions());

        result.InteractionToQuery.Should().Be(0);
        result.CreatedIds.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Promote_BelowConfidenceThreshold_NoPromotion()
    {
        for (var i = 0; i < 3; i++)
        {
            await this.store.UpsertExpertiseAsync(new ExpertiseRecord
            {
                Id = $"exp-{i}",
                Subject = $"src/parser/File{i}.cs",
                Category = ExpertiseCategory.FileExpertise,
                Tier = MemoryTier.Interaction,
                Confidence = 0.5, // below threshold
                EpisodeCount = 5
            });
        }

        var result = await this.evolver.PromoteAsync(new PromotionOptions { MinConfidence = 0.8 });

        result.InteractionToQuery.Should().Be(0);
    }

    [TestMethod]
    public async Task Promote_QueryToInsight_ForRepeatedQueryPatterns()
    {
        for (var i = 0; i < 3; i++)
        {
            await this.store.UpsertExpertiseAsync(new ExpertiseRecord
            {
                Id = $"exp-q-{i}",
                Subject = $"EtwIngest.Parsing.Component{i}",
                Category = ExpertiseCategory.NamespaceExpertise,
                Tier = MemoryTier.Query,
                Confidence = 0.9,
                EpisodeCount = 7
            });
        }

        var result = await this.evolver.PromoteAsync(new PromotionOptions());

        result.QueryToInsight.Should().Be(1);
        var promoted = await this.store.GetExpertiseAsync(result.CreatedIds[0]);
        promoted!.Tier.Should().Be(MemoryTier.Insight);
        promoted.Category.Should().Be(ExpertiseCategory.ArchitecturalKnowledge);
    }

    [TestMethod]
    public async Task Promote_AggregatesEpisodeCount()
    {
        for (var i = 0; i < 3; i++)
        {
            await this.store.UpsertExpertiseAsync(new ExpertiseRecord
            {
                Id = $"exp-{i}",
                Subject = $"src/etw/File{i}.cs",
                Category = ExpertiseCategory.FileExpertise,
                Tier = MemoryTier.Interaction,
                Confidence = 0.85,
                EpisodeCount = 5,
                DerivedFromEpisodeIds = [$"ep-{i}-a", $"ep-{i}-b"]
            });
        }

        var result = await this.evolver.PromoteAsync(new PromotionOptions());

        var promoted = await this.store.GetExpertiseAsync(result.CreatedIds[0]);
        promoted!.EpisodeCount.Should().Be(15); // 5 × 3
        promoted.DerivedFromEpisodeIds.Should().HaveCount(6); // 2 × 3
    }

    [TestMethod]
    public void ExtractGroupKey_FileExpertise_ReturnsDirectory()
    {
        var key = MemoryEvolver.ExtractGroupKey("src/parser/Tokenizer.cs", ExpertiseCategory.FileExpertise);
        key.Should().Be("src/parser");
    }

    [TestMethod]
    public void ExtractGroupKey_TypeExpertise_ReturnsNamespace()
    {
        var key = MemoryEvolver.ExtractGroupKey("MyApp.Parser.Tokenizer", ExpertiseCategory.TypeExpertise);
        key.Should().Be("MyApp.Parser");
    }
}
