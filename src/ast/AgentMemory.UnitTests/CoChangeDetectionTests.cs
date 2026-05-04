// -----------------------------------------------------------------------
// <copyright file="CoChangeDetectionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory.UnitTests;

using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CoChangeDetectionTests
{
    private InMemoryAgentMemoryStore store = null!;
    private MemoryEvolver evolver = null!;
    private EpisodeRecorder recorder = null!;

    [TestInitialize]
    public void Setup()
    {
        this.store = new InMemoryAgentMemoryStore();
        this.evolver = new MemoryEvolver(this.store);
        this.recorder = new EpisodeRecorder(this.store);
    }

    [TestMethod]
    public async Task Consolidate_DetectsCoChangePairs()
    {
        // Create 3 episodes where files A and B are modified together
        for (var i = 0; i < 3; i++)
        {
            var builder = await this.recorder.StartEpisodeAsync(
                "agent-1", $"Task {i}", EpisodeType.BugFix);

            builder
                .TouchedFile("src/Parser.cs", TouchType.Modified)
                .TouchedFile("src/ParserTests.cs", TouchType.Modified);

            await builder.CompleteAsync(EpisodeOutcome.Success, "Done");
        }

        var result = await this.evolver.ConsolidateAsync(new ConsolidationOptions
        {
            MinCoChangeOccurrences = 3,
            MinCoChangeJaccard = 0.3
        });

        result.CoChangeEdgesCreated.Should().BeGreaterThanOrEqualTo(1);

        // Verify a CoChangedWith edge exists
        var edges = await this.store.GetOutgoingEdgesAsync(
            "src/Parser.cs", MemoryEdgeType.CoChangedWith);
        edges.Should().ContainSingle(e => e.TargetId == "src/ParserTests.cs");
    }

    [TestMethod]
    public async Task Consolidate_CoChange_IgnoresReadOnlyTouches()
    {
        // Create episodes where files are only READ together (not modified)
        for (var i = 0; i < 5; i++)
        {
            var builder = await this.recorder.StartEpisodeAsync(
                "agent-1", $"Investigate {i}", EpisodeType.Investigation);

            builder
                .TouchedFile("src/Parser.cs", TouchType.Read)
                .TouchedFile("src/ParserTests.cs", TouchType.Read);

            await builder.CompleteAsync(EpisodeOutcome.Success, "Investigated");
        }

        var result = await this.evolver.ConsolidateAsync(new ConsolidationOptions
        {
            MinCoChangeOccurrences = 3,
            MinCoChangeJaccard = 0.3
        });

        result.CoChangeEdgesCreated.Should().Be(0);
    }

    [TestMethod]
    public async Task Consolidate_CoChange_BelowOccurrenceThreshold_NoEdge()
    {
        // Only 2 co-modifications — below threshold of 3
        for (var i = 0; i < 2; i++)
        {
            var builder = await this.recorder.StartEpisodeAsync(
                "agent-1", $"Task {i}", EpisodeType.Feature);

            builder
                .TouchedFile("src/Parser.cs", TouchType.Modified)
                .TouchedFile("src/ParserTests.cs", TouchType.Modified);

            await builder.CompleteAsync(EpisodeOutcome.Success, "Done");
        }

        var result = await this.evolver.ConsolidateAsync(new ConsolidationOptions
        {
            MinCoChangeOccurrences = 3
        });

        result.CoChangeEdgesCreated.Should().Be(0);
    }

    [TestMethod]
    public async Task Consolidate_CoChange_BelowJaccardThreshold_NoEdge()
    {
        // File A modified in 10 episodes, file B modified only in 3 of them
        // Jaccard = 3/10 = 0.3 — below 0.5 threshold
        for (var i = 0; i < 10; i++)
        {
            var builder = await this.recorder.StartEpisodeAsync(
                "agent-1", $"Task {i}", EpisodeType.Feature);

            builder.TouchedFile("src/Parser.cs", TouchType.Modified);

            if (i < 3)
            {
                builder.TouchedFile("src/Rare.cs", TouchType.Modified);
            }

            await builder.CompleteAsync(EpisodeOutcome.Success, "Done");
        }

        var result = await this.evolver.ConsolidateAsync(new ConsolidationOptions
        {
            MinCoChangeOccurrences = 3,
            MinCoChangeJaccard = 0.5 // high threshold
        });

        result.CoChangeEdgesCreated.Should().Be(0);
    }

    [TestMethod]
    public async Task Consolidate_CoChange_CreatesCoChangeGroupExpertise()
    {
        // 3 files always modified together → should form a co-change group
        for (var i = 0; i < 4; i++)
        {
            var builder = await this.recorder.StartEpisodeAsync(
                "agent-1", $"Task {i}", EpisodeType.Feature);

            builder
                .TouchedFile("src/Model.cs", TouchType.Modified)
                .TouchedFile("src/Controller.cs", TouchType.Modified)
                .TouchedFile("src/View.cs", TouchType.Modified);

            await builder.CompleteAsync(EpisodeOutcome.Success, "Done");
        }

        var result = await this.evolver.ConsolidateAsync(new ConsolidationOptions
        {
            MinCoChangeOccurrences = 3,
            MinCoChangeJaccard = 0.3
        });

        result.CoChangeGroupsCreated.Should().BeGreaterThanOrEqualTo(1);
    }

    [TestMethod]
    public async Task Consolidate_CoChange_StoresJaccardInEdgeMetadata()
    {
        for (var i = 0; i < 3; i++)
        {
            var builder = await this.recorder.StartEpisodeAsync(
                "agent-1", $"Task {i}", EpisodeType.BugFix);

            builder
                .TouchedFile("src/A.cs", TouchType.Modified)
                .TouchedFile("src/B.cs", TouchType.Modified);

            await builder.CompleteAsync(EpisodeOutcome.Success, "Done");
        }

        await this.evolver.ConsolidateAsync(new ConsolidationOptions
        {
            MinCoChangeOccurrences = 3,
            MinCoChangeJaccard = 0.3
        });

        var edges = await this.store.GetOutgoingEdgesAsync("src/A.cs", MemoryEdgeType.CoChangedWith);
        edges.Should().NotBeEmpty();
        edges[0].Metadata.Should().ContainKey("jaccard");
        edges[0].Metadata.Should().ContainKey("coOccurrences");
        edges[0].Weight.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public async Task Consolidate_CoChange_DisabledByOption()
    {
        for (var i = 0; i < 5; i++)
        {
            var builder = await this.recorder.StartEpisodeAsync(
                "agent-1", $"Task {i}", EpisodeType.Feature);

            builder
                .TouchedFile("src/A.cs", TouchType.Modified)
                .TouchedFile("src/B.cs", TouchType.Modified);

            await builder.CompleteAsync(EpisodeOutcome.Success, "Done");
        }

        var result = await this.evolver.ConsolidateAsync(new ConsolidationOptions
        {
            DetectCoChanges = false
        });

        result.CoChangeEdgesCreated.Should().Be(0);
    }
}
