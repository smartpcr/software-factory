// -----------------------------------------------------------------------
// <copyright file="FileSystemAgentMemoryStoreTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory.UnitTests;

using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class FileSystemAgentMemoryStoreTests
{
    private string tempDir = null!;
    private FileSystemAgentMemoryStore store = null!;

    [TestInitialize]
    public void Setup()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"agent-memory-test-{Guid.NewGuid():N}");
        this.store = new FileSystemAgentMemoryStore(this.tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RecordAndRetrieve_Episode_RoundTrips()
    {
        var episode = new Episode
        {
            Id = "ep-1",
            AgentId = "agent-test",
            TaskDescription = "Fix parser",
            Type = EpisodeType.BugFix,
            Outcome = EpisodeOutcome.Success,
            TouchedFiles = ["src/Parser.cs"],
            TouchedSymbolIds = ["MyApp.Parser"]
        };

        await this.store.RecordEpisodeAsync(episode);
        var retrieved = await this.store.GetEpisodeAsync("ep-1");

        retrieved.Should().NotBeNull();
        retrieved!.AgentId.Should().Be("agent-test");
        retrieved.TouchedFiles.Should().Contain("src/Parser.cs");
    }

    [TestMethod]
    public async Task Episode_PersistsAcrossStoreInstances()
    {
        var episode = new Episode
        {
            Id = "ep-persist",
            AgentId = "agent-1",
            TaskDescription = "Persist test",
            Type = EpisodeType.Feature,
            Outcome = EpisodeOutcome.Success
        };

        await this.store.RecordEpisodeAsync(episode);

        // Create a new store instance pointing to same directory
        var store2 = new FileSystemAgentMemoryStore(this.tempDir);
        var retrieved = await store2.GetEpisodeAsync("ep-persist");

        retrieved.Should().NotBeNull();
        retrieved!.TaskDescription.Should().Be("Persist test");
    }

    [TestMethod]
    public async Task Expertise_PersistsAcrossStoreInstances()
    {
        var record = new ExpertiseRecord
        {
            Id = "exp-persist",
            Subject = "src/Parser.cs",
            Category = ExpertiseCategory.FileExpertise,
            Confidence = 0.85,
            Summary = "Parser handles ETL files"
        };

        await this.store.UpsertExpertiseAsync(record);

        var store2 = new FileSystemAgentMemoryStore(this.tempDir);
        var retrieved = await store2.GetExpertiseAsync("exp-persist");

        retrieved.Should().NotBeNull();
        retrieved!.Confidence.Should().Be(0.85);
        retrieved.Summary.Should().Be("Parser handles ETL files");
    }

    [TestMethod]
    public async Task MemoryEdge_PersistsAcrossStoreInstances()
    {
        await this.store.AddMemoryEdgeAsync(new MemoryEdge
        {
            Id = "edge-persist",
            EdgeType = MemoryEdgeType.ExpertiseAboutFile,
            SourceId = "exp-1",
            TargetId = "src/Parser.cs",
            Weight = 0.9
        });

        var store2 = new FileSystemAgentMemoryStore(this.tempDir);
        var edges = await store2.GetOutgoingEdgesAsync("exp-1", MemoryEdgeType.ExpertiseAboutFile);

        edges.Should().ContainSingle(e => e.Id == "edge-persist");
        edges[0].Weight.Should().Be(0.9);
    }

    [TestMethod]
    public async Task CallChain_PersistsAcrossStoreInstances()
    {
        var chain = new CallChain
        {
            Id = "cc-persist",
            Description = "Parser chain",
            EntrySymbolId = "MyApp.Parser.Parse",
            TerminalSymbolId = "MyApp.Tokenizer.Tokenize",
            Steps =
            [
                new CallChainStep { SymbolId = "MyApp.Parser.Parse", StepIndex = 0 },
                new CallChainStep { SymbolId = "MyApp.Tokenizer.Tokenize", StepIndex = 1 }
            ],
            Confidence = 0.75
        };

        await this.store.UpsertCallChainAsync(chain);

        var store2 = new FileSystemAgentMemoryStore(this.tempDir);
        var retrieved = await store2.GetCallChainAsync("cc-persist");

        retrieved.Should().NotBeNull();
        retrieved!.Steps.Should().HaveCount(2);
        retrieved.Confidence.Should().Be(0.75);
    }

    [TestMethod]
    public async Task GetEpisodes_FilterByTouchedFiles()
    {
        await this.store.RecordEpisodeAsync(new Episode
        {
            Id = "ep-a",
            AgentId = "a",
            TaskDescription = "A",
            TouchedFiles = ["src/A.cs"]
        });
        await this.store.RecordEpisodeAsync(new Episode
        {
            Id = "ep-b",
            AgentId = "a",
            TaskDescription = "B",
            TouchedFiles = ["src/B.cs"]
        });

        var results = await this.store.GetEpisodesAsync(new EpisodeFilter
        {
            TouchedFiles = ["src/A.cs"]
        });

        results.Should().ContainSingle(e => e.Id == "ep-a");
    }

    [TestMethod]
    public async Task DeleteEpisode_AlsoDeletesEdges()
    {
        await this.store.RecordEpisodeAsync(new Episode
        {
            Id = "ep-del",
            AgentId = "a",
            TaskDescription = "To delete"
        });
        await this.store.AddMemoryEdgeAsync(new MemoryEdge
        {
            Id = "edge-del",
            EdgeType = MemoryEdgeType.EpisodeTouchedFile,
            SourceId = "ep-del",
            TargetId = "src/File.cs"
        });

        await this.store.DeleteEpisodeAsync("ep-del");

        var episode = await this.store.GetEpisodeAsync("ep-del");
        episode.Should().BeNull();

        var edges = await this.store.GetOutgoingEdgesAsync("ep-del");
        edges.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Clear_RemovesAllData()
    {
        await this.store.RecordEpisodeAsync(new Episode
        {
            Id = "ep-clear",
            AgentId = "a",
            TaskDescription = "Clear test"
        });
        await this.store.UpsertExpertiseAsync(new ExpertiseRecord
        {
            Id = "exp-clear",
            Subject = "test"
        });

        await this.store.ClearAsync();

        var stats = await this.store.GetStatisticsAsync();
        stats.EpisodeCount.Should().Be(0);
        stats.ExpertiseCount.Should().Be(0);
    }

    [TestMethod]
    public async Task GetStatistics_ReturnsAccurateCounts()
    {
        await this.store.RecordEpisodeAsync(new Episode
        {
            Id = "ep-stat",
            AgentId = "a",
            TaskDescription = "Stats test",
            Type = EpisodeType.BugFix,
            Outcome = EpisodeOutcome.Success
        });
        await this.store.UpsertExpertiseAsync(new ExpertiseRecord
        {
            Id = "exp-stat",
            Subject = "test",
            Category = ExpertiseCategory.FileExpertise,
            Confidence = 0.7
        });

        var stats = await this.store.GetStatisticsAsync();

        stats.EpisodeCount.Should().Be(1);
        stats.ExpertiseCount.Should().Be(1);
        stats.EpisodesByType.Should().ContainKey(EpisodeType.BugFix);
        stats.AverageConfidence.Should().BeApproximately(0.7, 0.01);
    }

    [TestMethod]
    public async Task TieredExpertise_QueriesByTier()
    {
        await this.store.UpsertExpertiseAsync(new ExpertiseRecord
        {
            Id = "exp-int",
            Subject = "src/A.cs",
            Category = ExpertiseCategory.FileExpertise,
            Tier = MemoryTier.Interaction,
            Confidence = 0.8
        });
        await this.store.UpsertExpertiseAsync(new ExpertiseRecord
        {
            Id = "exp-query",
            Subject = "MyApp.Services",
            Category = ExpertiseCategory.NamespaceExpertise,
            Tier = MemoryTier.Query,
            Confidence = 0.9
        });

        var interaction = await this.store.GetExpertiseByTierAsync(MemoryTier.Interaction);
        interaction.Should().ContainSingle(e => e.Id == "exp-int");

        var query = await this.store.GetExpertiseByTierAsync(MemoryTier.Query);
        query.Should().ContainSingle(e => e.Id == "exp-query");
    }

    [TestMethod]
    public async Task HandleCorruptedFile_StartsEmpty()
    {
        // Write corrupted JSON
        await File.WriteAllTextAsync(Path.Combine(this.tempDir, "episodes.json"), "not valid json {{");

        var store2 = new FileSystemAgentMemoryStore(this.tempDir);
        var stats = await store2.GetStatisticsAsync();

        stats.EpisodeCount.Should().Be(0);
    }

    [TestMethod]
    public void Constructor_CreatesDirectory()
    {
        var newDir = Path.Combine(this.tempDir, "subdir", "memory");
        _ = new FileSystemAgentMemoryStore(newDir);

        Directory.Exists(newDir).Should().BeTrue();
    }
}
