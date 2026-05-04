// -----------------------------------------------------------------------
// <copyright file="ContextBuilderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory.UnitTests;

using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class ContextBuilderTests
{
    private InMemoryAgentMemoryStore store = null!;
    private ContextBuilder builder = null!;

    [TestInitialize]
    public void Setup()
    {
        this.store = new InMemoryAgentMemoryStore();
        this.builder = new ContextBuilder(this.store);
    }

    [TestMethod]
    public async Task BuildContext_ReturnsEpisodesForMatchingFiles()
    {
        var ep = new Episode
        {
            Id = "ep-1",
            AgentId = "agent-1",
            TaskDescription = "Fix parser",
            Outcome = EpisodeOutcome.Success,
            TouchedFiles = ["src/Parser.cs"]
        };
        await this.store.RecordEpisodeAsync(ep);

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Update parser",
            FilePaths = ["src/Parser.cs"]
        });

        context.RelevantEpisodes.Should().ContainSingle(e => e.Id == "ep-1");
    }

    [TestMethod]
    public async Task BuildContext_ReturnsExpertiseForMatchingFiles()
    {
        await this.store.UpsertExpertiseAsync(new ExpertiseRecord
        {
            Id = "exp-1",
            Subject = "src/Parser.cs",
            Category = ExpertiseCategory.FileExpertise,
            Confidence = 0.8,
            Summary = "Parser handles ETL files"
        });

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Fix parser",
            FilePaths = ["src/Parser.cs"]
        });

        context.Expertise.Should().ContainSingle(e => e.Id == "exp-1");
    }

    [TestMethod]
    public async Task BuildContext_ReturnsExpertiseForMatchingSymbols()
    {
        await this.store.UpsertExpertiseAsync(new ExpertiseRecord
        {
            Id = "exp-sym",
            Subject = "MyNamespace.Parser",
            Category = ExpertiseCategory.TypeExpertise,
            Confidence = 0.7
        });

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Investigate parser",
            SymbolIds = ["MyNamespace.Parser"]
        });

        context.Expertise.Should().ContainSingle(e => e.Id == "exp-sym");
    }

    [TestMethod]
    public async Task BuildContext_IncludesSymbolFamiliarity()
    {
        await this.store.UpsertExpertiseAsync(new ExpertiseRecord
        {
            Id = "exp-1",
            Subject = "src/Parser.cs",
            Confidence = 0.85
        });

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Fix parser",
            FilePaths = ["src/Parser.cs"]
        });

        context.SymbolFamiliarity.Should().ContainKey("src/Parser.cs");
        context.SymbolFamiliarity["src/Parser.cs"].Should().BeApproximately(0.85, 0.01);
    }

    [TestMethod]
    public async Task BuildContext_AddsFreshnessWarnings_ForStaleExpertise()
    {
        await this.store.UpsertExpertiseAsync(new ExpertiseRecord
        {
            Id = "exp-stale",
            Subject = "src/OldCode.cs",
            Confidence = 0.7,
            LastValidated = DateTime.UtcNow.AddDays(-60)
        });

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Update old code",
            FilePaths = ["src/OldCode.cs"]
        });

        context.FreshnessWarnings.Should().Contain(w => w.Contains("OldCode.cs"));
    }

    [TestMethod]
    public async Task BuildContext_AddsDegradationWarning_WithoutGraphStore()
    {
        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Some task"
        });

        context.FreshnessWarnings.Should().Contain(w => w.Contains("Graph store unavailable"));
    }

    [TestMethod]
    public async Task BuildContext_RanksSuccessfulEpisodesHigher()
    {
        var success = new Episode
        {
            Id = "ep-success",
            AgentId = "a",
            TaskDescription = "Fixed parser",
            Outcome = EpisodeOutcome.Success,
            TouchedFiles = ["src/Parser.cs"],
            Timestamp = DateTime.UtcNow
        };
        var failed = new Episode
        {
            Id = "ep-failed",
            AgentId = "a",
            TaskDescription = "Tried to fix parser",
            Outcome = EpisodeOutcome.Failed,
            TouchedFiles = ["src/Parser.cs"],
            Timestamp = DateTime.UtcNow
        };

        await this.store.RecordEpisodeAsync(success);
        await this.store.RecordEpisodeAsync(failed);

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Fix parser again",
            FilePaths = ["src/Parser.cs"]
        });

        context.RelevantEpisodes.Should().HaveCount(2);
        context.RelevantEpisodes[0].Id.Should().Be("ep-success");
    }

    [TestMethod]
    public async Task BuildContext_RespectsMinConfidence()
    {
        await this.store.UpsertExpertiseAsync(new ExpertiseRecord
        {
            Id = "exp-low",
            Subject = "src/Weak.cs",
            Confidence = 0.1
        });

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Review code",
            FilePaths = ["src/Weak.cs"],
            MinConfidence = 0.5
        });

        context.Expertise.Should().BeEmpty();
    }

    [TestMethod]
    public async Task BuildContext_RespectsMaxEpisodes()
    {
        for (var i = 0; i < 20; i++)
        {
            await this.store.RecordEpisodeAsync(new Episode
            {
                Id = $"ep-{i}",
                AgentId = "agent-1",
                TaskDescription = $"Task {i}",
                Outcome = EpisodeOutcome.Success,
                TouchedFiles = ["src/Popular.cs"]
            });
        }

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "More work",
            FilePaths = ["src/Popular.cs"],
            MaxEpisodes = 5
        });

        context.RelevantEpisodes.Should().HaveCount(5);
    }

    [TestMethod]
    public async Task BuildContext_FindsExpertiseViaMemoryEdges()
    {
        // Expertise linked to file via edge, but not directly matching by subject
        await this.store.UpsertExpertiseAsync(new ExpertiseRecord
        {
            Id = "exp-linked",
            Subject = "Parser pattern",
            Category = ExpertiseCategory.PatternKnowledge,
            Confidence = 0.9
        });
        await this.store.AddMemoryEdgeAsync(new MemoryEdge
        {
            Id = "edge-1",
            EdgeType = MemoryEdgeType.ExpertiseAboutFile,
            SourceId = "exp-linked",
            TargetId = "src/Parser.cs"
        });

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Modify parser",
            FilePaths = ["src/Parser.cs"]
        });

        context.Expertise.Should().Contain(e => e.Id == "exp-linked");
    }

    [TestMethod]
    public async Task BuildContext_EmptyRequest_ReturnsEmptyContext()
    {
        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "General task"
        });

        context.TaskDescription.Should().Be("General task");
        context.RelevantEpisodes.Should().BeEmpty();
        context.RelevanceScore.Should().Be(0.0);
    }

    [TestMethod]
    public async Task BuildContext_IncludesHighConfidencePatternKnowledge()
    {
        await this.store.UpsertExpertiseAsync(new ExpertiseRecord
        {
            Id = "exp-pattern",
            Subject = "DI registration pattern",
            Category = ExpertiseCategory.PatternKnowledge,
            Confidence = 0.9,
            Summary = "Always register services in ConfigureServices"
        });

        var context = await this.builder.BuildContextAsync(new ContextRequest
        {
            TaskDescription = "Add new service",
            FilePaths = ["src/Startup.cs"]
        });

        // High-confidence pattern knowledge should be included even without direct file match
        context.Expertise.Should().Contain(e => e.Id == "exp-pattern");
    }

    [TestMethod]
    public void Constructor_NullStore_Throws()
    {
        var act = () => new ContextBuilder(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
