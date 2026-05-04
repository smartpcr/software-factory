// -----------------------------------------------------------------------
// <copyright file="InMemoryAgentMemoryStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory.UnitTests;

using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class InMemoryAgentMemoryStoreTests
{
    private InMemoryAgentMemoryStore store = null!;

    [TestInitialize]
    public void Setup()
    {
        this.store = new InMemoryAgentMemoryStore();
    }

    // ── Episodes ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task RecordEpisode_StoresAndRetrieves()
    {
        var episode = CreateEpisode("ep-1", "agent-a", EpisodeType.BugFix);

        await this.store.RecordEpisodeAsync(episode);
        var retrieved = await this.store.GetEpisodeAsync("ep-1");

        retrieved.Should().NotBeNull();
        retrieved!.AgentId.Should().Be("agent-a");
        retrieved.Type.Should().Be(EpisodeType.BugFix);
    }

    [TestMethod]
    public async Task GetEpisode_UnknownId_ReturnsNull()
    {
        var result = await this.store.GetEpisodeAsync("nonexistent");
        result.Should().BeNull();
    }

    [TestMethod]
    public async Task GetEpisodes_FilterByAgentId()
    {
        await this.store.RecordEpisodeAsync(CreateEpisode("ep-1", "agent-a"));
        await this.store.RecordEpisodeAsync(CreateEpisode("ep-2", "agent-b"));
        await this.store.RecordEpisodeAsync(CreateEpisode("ep-3", "agent-a"));

        var results = await this.store.GetEpisodesAsync(new EpisodeFilter { AgentId = "agent-a" });

        results.Should().HaveCount(2);
        results.Should().OnlyContain(e => e.AgentId == "agent-a");
    }

    [TestMethod]
    public async Task GetEpisodes_FilterByTouchedFiles()
    {
        var ep = CreateEpisode("ep-1", "agent-a");
        ep.TouchedFiles.Add("src/Foo.cs");
        await this.store.RecordEpisodeAsync(ep);
        await this.store.RecordEpisodeAsync(CreateEpisode("ep-2", "agent-a"));

        var results = await this.store.GetEpisodesAsync(
            new EpisodeFilter { TouchedFiles = ["src/Foo.cs"] });

        results.Should().ContainSingle(e => e.Id == "ep-1");
    }

    [TestMethod]
    public async Task GetEpisodes_FilterByOutcome()
    {
        var ep1 = CreateEpisode("ep-1", "agent-a");
        ep1.Outcome = EpisodeOutcome.Success;
        var ep2 = CreateEpisode("ep-2", "agent-a");
        ep2.Outcome = EpisodeOutcome.Failed;

        await this.store.RecordEpisodeAsync(ep1);
        await this.store.RecordEpisodeAsync(ep2);

        var results = await this.store.GetEpisodesAsync(
            new EpisodeFilter { Outcome = EpisodeOutcome.Success });

        results.Should().ContainSingle(e => e.Id == "ep-1");
    }

    [TestMethod]
    public async Task DeleteEpisode_RemovesAssociatedEdges()
    {
        await this.store.RecordEpisodeAsync(CreateEpisode("ep-1", "agent-a"));
        await this.store.AddMemoryEdgeAsync(new MemoryEdge
        {
            Id = "edge-1",
            EdgeType = MemoryEdgeType.EpisodeTouchedFile,
            SourceId = "ep-1",
            TargetId = "src/Foo.cs"
        });

        await this.store.DeleteEpisodeAsync("ep-1");

        var episode = await this.store.GetEpisodeAsync("ep-1");
        episode.Should().BeNull();

        var edges = await this.store.GetOutgoingEdgesAsync("ep-1");
        edges.Should().BeEmpty();
    }

    // ── Expertise ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task UpsertExpertise_CreatesAndUpdates()
    {
        var record = CreateExpertise("exp-1", "src/Foo.cs", 0.5);
        await this.store.UpsertExpertiseAsync(record);

        var retrieved = await this.store.GetExpertiseAsync("exp-1");
        retrieved.Should().NotBeNull();
        retrieved!.Confidence.Should().Be(0.5);

        record.Confidence = 0.9;
        await this.store.UpsertExpertiseAsync(record);

        retrieved = await this.store.GetExpertiseAsync("exp-1");
        retrieved!.Confidence.Should().Be(0.9);
    }

    [TestMethod]
    public async Task GetExpertiseForSubject_CaseInsensitive()
    {
        await this.store.UpsertExpertiseAsync(CreateExpertise("exp-1", "src/Foo.cs", 0.8));

        var results = await this.store.GetExpertiseForSubjectAsync("SRC/FOO.CS");
        results.Should().ContainSingle();
    }

    [TestMethod]
    public async Task GetExpertiseByCategory_RespectsMinConfidence()
    {
        await this.store.UpsertExpertiseAsync(CreateExpertise("exp-1", "a", 0.3));
        await this.store.UpsertExpertiseAsync(CreateExpertise("exp-2", "b", 0.8));

        var results = await this.store.GetExpertiseByCategoryAsync(
            ExpertiseCategory.FileExpertise, minConfidence: 0.5);

        results.Should().ContainSingle(e => e.Id == "exp-2");
    }

    // ── Edges ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetOutgoingEdges_FiltersByType()
    {
        await this.store.AddMemoryEdgeAsync(new MemoryEdge
        {
            Id = "e1",
            EdgeType = MemoryEdgeType.EpisodeTouchedFile,
            SourceId = "ep-1",
            TargetId = "file-a"
        });
        await this.store.AddMemoryEdgeAsync(new MemoryEdge
        {
            Id = "e2",
            EdgeType = MemoryEdgeType.EpisodeTouchedSymbol,
            SourceId = "ep-1",
            TargetId = "sym-a"
        });

        var fileEdges = await this.store.GetOutgoingEdgesAsync(
            "ep-1", MemoryEdgeType.EpisodeTouchedFile);
        fileEdges.Should().ContainSingle();

        var allEdges = await this.store.GetOutgoingEdgesAsync("ep-1");
        allEdges.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task GetIncomingEdges_Works()
    {
        await this.store.AddMemoryEdgeAsync(new MemoryEdge
        {
            Id = "e1",
            EdgeType = MemoryEdgeType.ExpertiseAboutFile,
            SourceId = "exp-1",
            TargetId = "src/Foo.cs"
        });

        var edges = await this.store.GetIncomingEdgesAsync("src/Foo.cs");
        edges.Should().ContainSingle(e => e.SourceId == "exp-1");
    }

    // ── Statistics ────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetStatistics_ReflectsStoredData()
    {
        await this.store.RecordEpisodeAsync(CreateEpisode("ep-1", "a", EpisodeType.BugFix));
        await this.store.RecordEpisodeAsync(CreateEpisode("ep-2", "a", EpisodeType.Feature));
        await this.store.UpsertExpertiseAsync(CreateExpertise("exp-1", "f", 0.7));
        await this.store.AddMemoryEdgeAsync(new MemoryEdge
        {
            Id = "e1",
            EdgeType = MemoryEdgeType.EpisodeTouchedFile,
            SourceId = "ep-1",
            TargetId = "f"
        });

        var stats = await this.store.GetStatisticsAsync();

        stats.EpisodeCount.Should().Be(2);
        stats.ExpertiseCount.Should().Be(1);
        stats.EdgeCount.Should().Be(1);
        stats.EpisodesByType.Should().ContainKey(EpisodeType.BugFix);
        stats.AverageConfidence.Should().Be(0.7);
    }

    [TestMethod]
    public async Task Clear_RemovesEverything()
    {
        await this.store.RecordEpisodeAsync(CreateEpisode("ep-1", "a"));
        await this.store.UpsertExpertiseAsync(CreateExpertise("exp-1", "f", 0.5));

        await this.store.ClearAsync();

        var stats = await this.store.GetStatisticsAsync();
        stats.EpisodeCount.Should().Be(0);
        stats.ExpertiseCount.Should().Be(0);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static Episode CreateEpisode(
        string id,
        string agentId,
        EpisodeType type = EpisodeType.Investigation)
    {
        return new Episode
        {
            Id = id,
            AgentId = agentId,
            TaskDescription = $"Test task for {id}",
            Type = type,
            Outcome = EpisodeOutcome.InProgress
        };
    }

    private static ExpertiseRecord CreateExpertise(
        string id, string subject, double confidence)
    {
        return new ExpertiseRecord
        {
            Id = id,
            Subject = subject,
            Category = ExpertiseCategory.FileExpertise,
            Confidence = confidence
        };
    }
}
