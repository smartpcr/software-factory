// -----------------------------------------------------------------------
// <copyright file="MemoryEvolverTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory.UnitTests;

using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class MemoryEvolverTests
{
    private InMemoryAgentMemoryStore store = null!;
    private MemoryEvolver evolver = null!;

    [TestInitialize]
    public void Setup()
    {
        this.store = new InMemoryAgentMemoryStore();
        this.evolver = new MemoryEvolver(this.store);
    }

    // ── Consolidation ─────────────────────────────────────────────────

    [TestMethod]
    public async Task Consolidate_CreatesFileExpertise_WhenThresholdMet()
    {
        // Arrange: 3 successful episodes touching the same file
        for (var i = 0; i < 3; i++)
        {
            var ep = new Episode
            {
                Id = $"ep-{i}",
                AgentId = "agent-1",
                TaskDescription = $"Task {i}",
                Type = EpisodeType.BugFix,
                Outcome = EpisodeOutcome.Success,
                TouchedFiles = ["src/Parser.cs"]
            };
            ep.Observations.Add(new EvidenceItem
            {
                Category = "finding",
                Content = $"Found issue {i}"
            });
            await this.store.RecordEpisodeAsync(ep);
        }

        // Act
        var result = await this.evolver.ConsolidateAsync(new ConsolidationOptions());

        // Assert
        result.ExpertiseCreated.Should().Be(1);
        result.AffectedExpertiseIds.Should().HaveCount(1);

        var expertise = await this.store.GetExpertiseForSubjectAsync("src/Parser.cs");
        expertise.Should().ContainSingle();
        expertise[0].Category.Should().Be(ExpertiseCategory.FileExpertise);
        expertise[0].EpisodeCount.Should().Be(3);
        expertise[0].Confidence.Should().BeGreaterThan(0.3);
        expertise[0].PositiveEvidence.Should().HaveCount(3);
    }

    [TestMethod]
    public async Task Consolidate_BelowThreshold_NoExpertiseCreated()
    {
        // Only 2 episodes — below default threshold of 3
        for (var i = 0; i < 2; i++)
        {
            await this.store.RecordEpisodeAsync(new Episode
            {
                Id = $"ep-{i}",
                AgentId = "agent-1",
                TaskDescription = $"Task {i}",
                Outcome = EpisodeOutcome.Success,
                TouchedFiles = ["src/Parser.cs"]
            });
        }

        var result = await this.evolver.ConsolidateAsync(new ConsolidationOptions());

        result.ExpertiseCreated.Should().Be(0);
    }

    [TestMethod]
    public async Task Consolidate_RequiresDistinctAgents()
    {
        // 3 episodes from same agent, but min distinct agents = 2
        for (var i = 0; i < 3; i++)
        {
            await this.store.RecordEpisodeAsync(new Episode
            {
                Id = $"ep-{i}",
                AgentId = "same-agent",
                TaskDescription = $"Task {i}",
                Outcome = EpisodeOutcome.Success,
                TouchedFiles = ["src/Parser.cs"]
            });
        }

        var options = new ConsolidationOptions { MinDistinctAgents = 2 };
        var result = await this.evolver.ConsolidateAsync(options);

        result.ExpertiseCreated.Should().Be(0);
    }

    [TestMethod]
    public async Task Consolidate_UpdatesExistingExpertise()
    {
        // Pre-existing expertise
        await this.store.UpsertExpertiseAsync(new ExpertiseRecord
        {
            Id = "exp-existing",
            Subject = "src/Parser.cs",
            Category = ExpertiseCategory.FileExpertise,
            Confidence = 0.4,
            EpisodeCount = 2,
            DerivedFromEpisodeIds = ["ep-old-1", "ep-old-2"]
        });

        // 3 new episodes
        for (var i = 0; i < 3; i++)
        {
            await this.store.RecordEpisodeAsync(new Episode
            {
                Id = $"ep-{i}",
                AgentId = "agent-1",
                TaskDescription = $"Task {i}",
                Outcome = EpisodeOutcome.Success,
                TouchedFiles = ["src/Parser.cs"]
            });
        }

        var result = await this.evolver.ConsolidateAsync(new ConsolidationOptions());

        result.ExpertiseUpdated.Should().Be(1);
        result.ExpertiseCreated.Should().Be(0);
    }

    [TestMethod]
    public async Task Consolidate_CreatesSymbolExpertise()
    {
        for (var i = 0; i < 3; i++)
        {
            await this.store.RecordEpisodeAsync(new Episode
            {
                Id = $"ep-{i}",
                AgentId = "agent-1",
                TaskDescription = $"Task {i}",
                Outcome = EpisodeOutcome.Success,
                TouchedSymbolIds = ["MyNamespace.Parser"]
            });
        }

        var result = await this.evolver.ConsolidateAsync(new ConsolidationOptions());

        result.ExpertiseCreated.Should().BeGreaterThanOrEqualTo(1);
        var expertise = await this.store.GetExpertiseForSubjectAsync("MyNamespace.Parser");
        expertise.Should().NotBeEmpty();
    }

    [TestMethod]
    public async Task Consolidate_CreatesFailurePatterns()
    {
        for (var i = 0; i < 2; i++)
        {
            var ep = new Episode
            {
                Id = $"fail-{i}",
                AgentId = "agent-1",
                TaskDescription = $"Failed task {i}",
                Outcome = EpisodeOutcome.Failed,
                Summary = $"Could not fix issue {i}",
                TouchedFiles = ["src/Broken.cs"]
            };
            ep.Evidence.Add(new EvidenceItem
            {
                Category = "error",
                Content = $"Error {i}"
            });
            await this.store.RecordEpisodeAsync(ep);
        }

        var result = await this.evolver.ConsolidateAsync(new ConsolidationOptions());

        result.FailurePatternsCreated.Should().Be(1);
        var expertise = await this.store.GetExpertiseForSubjectAsync("src/Broken.cs");
        expertise.Should().ContainSingle(e => e.Category == ExpertiseCategory.FailurePattern);
    }

    [TestMethod]
    public async Task Consolidate_IgnoresFailedEpisodes_ForPositiveExpertise()
    {
        // 3 failed episodes should not create FileExpertise
        for (var i = 0; i < 3; i++)
        {
            await this.store.RecordEpisodeAsync(new Episode
            {
                Id = $"ep-{i}",
                AgentId = "agent-1",
                TaskDescription = $"Task {i}",
                Outcome = EpisodeOutcome.Failed,
                TouchedFiles = ["src/Parser.cs"]
            });
        }

        var result = await this.evolver.ConsolidateAsync(new ConsolidationOptions());

        // Should not create FileExpertise from failed episodes
        var expertise = await this.store.GetExpertiseForSubjectAsync("src/Parser.cs");
        expertise.Should().NotContain(e => e.Category == ExpertiseCategory.FileExpertise);
    }

    [TestMethod]
    public async Task Consolidate_CreatesMemoryEdges()
    {
        for (var i = 0; i < 3; i++)
        {
            await this.store.RecordEpisodeAsync(new Episode
            {
                Id = $"ep-{i}",
                AgentId = "agent-1",
                TaskDescription = $"Task {i}",
                Outcome = EpisodeOutcome.Success,
                TouchedFiles = ["src/Parser.cs"]
            });
        }

        await this.evolver.ConsolidateAsync(new ConsolidationOptions());

        // Should have ExpertiseAboutFile edges
        var edges = await this.store.GetIncomingEdgesAsync(
            "src/Parser.cs", MemoryEdgeType.ExpertiseAboutFile);
        edges.Should().NotBeEmpty();
    }

    // ── Decay ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Decay_ReducesConfidence_AfterHalfLife()
    {
        var expertise = new ExpertiseRecord
        {
            Id = "exp-1",
            Subject = "src/Old.cs",
            Confidence = 0.8,
            LastValidated = DateTime.UtcNow.AddDays(-30) // one half-life ago
        };
        await this.store.UpsertExpertiseAsync(expertise);

        var result = await this.evolver.DecayAsync(new DecayOptions { HalfLifeDays = 30 });

        result.Decayed.Should().Be(1);
        var updated = await this.store.GetExpertiseAsync("exp-1");
        updated!.Confidence.Should().BeApproximately(0.4, 0.05);
    }

    [TestMethod]
    public async Task Decay_DeletesBelowMinConfidence()
    {
        var expertise = new ExpertiseRecord
        {
            Id = "exp-1",
            Subject = "src/Ancient.cs",
            Confidence = 0.1,
            LastValidated = DateTime.UtcNow.AddDays(-90)
        };
        await this.store.UpsertExpertiseAsync(expertise);

        var result = await this.evolver.DecayAsync(new DecayOptions
        {
            HalfLifeDays = 30,
            MinConfidence = 0.05
        });

        result.Deleted.Should().Be(1);
        result.DeletedIds.Should().Contain("exp-1");
        var deleted = await this.store.GetExpertiseAsync("exp-1");
        deleted.Should().BeNull();
    }

    [TestMethod]
    public async Task Decay_AccessCountSlowsDecay()
    {
        var frequentlyAccessed = new ExpertiseRecord
        {
            Id = "exp-frequent",
            Subject = "src/Core.cs",
            Confidence = 0.8,
            AccessCount = 20,
            LastValidated = DateTime.UtcNow.AddDays(-30)
        };
        var rarelyAccessed = new ExpertiseRecord
        {
            Id = "exp-rare",
            Subject = "src/Rare.cs",
            Confidence = 0.8,
            AccessCount = 0,
            LastValidated = DateTime.UtcNow.AddDays(-30)
        };

        await this.store.UpsertExpertiseAsync(frequentlyAccessed);
        await this.store.UpsertExpertiseAsync(rarelyAccessed);

        await this.evolver.DecayAsync(new DecayOptions { HalfLifeDays = 30 });

        var frequent = await this.store.GetExpertiseAsync("exp-frequent");
        var rare = await this.store.GetExpertiseAsync("exp-rare");

        // Frequently accessed should retain more confidence
        frequent!.Confidence.Should().BeGreaterThan(rare!.Confidence);
    }

    [TestMethod]
    public async Task Decay_RecentExpertise_NoChange()
    {
        var fresh = new ExpertiseRecord
        {
            Id = "exp-fresh",
            Subject = "src/New.cs",
            Confidence = 0.9,
            LastValidated = DateTime.UtcNow // just validated
        };
        await this.store.UpsertExpertiseAsync(fresh);

        var result = await this.evolver.DecayAsync(new DecayOptions { HalfLifeDays = 30 });

        result.Decayed.Should().Be(0);
        var unchanged = await this.store.GetExpertiseAsync("exp-fresh");
        unchanged!.Confidence.Should().Be(0.9);
    }

    [TestMethod]
    public void Constructor_NullStore_Throws()
    {
        var act = () => new MemoryEvolver(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
