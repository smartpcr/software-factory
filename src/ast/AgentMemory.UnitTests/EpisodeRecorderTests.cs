// -----------------------------------------------------------------------
// <copyright file="EpisodeRecorderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory.UnitTests;

using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class EpisodeRecorderTests
{
    private InMemoryAgentMemoryStore store = null!;
    private EpisodeRecorder recorder = null!;

    [TestInitialize]
    public void Setup()
    {
        this.store = new InMemoryAgentMemoryStore();
        this.recorder = new EpisodeRecorder(this.store);
    }

    [TestMethod]
    public async Task StartEpisode_PersistsInProgressImmediately()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Fix bug in parser", EpisodeType.BugFix);

        // Episode should be in store right away
        var episode = await this.store.GetEpisodeAsync(builder.EpisodeId);
        episode.Should().NotBeNull();
        episode!.Outcome.Should().Be(EpisodeOutcome.InProgress);
        episode.AgentId.Should().Be("agent-1");
        episode.TaskDescription.Should().Be("Fix bug in parser");
    }

    [TestMethod]
    public async Task Builder_AccumulatesTouchedFiles()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Investigate", EpisodeType.Investigation);

        builder
            .TouchedFile("src/Foo.cs")
            .TouchedFile("src/Bar.cs")
            .TouchedFile("src/Foo.cs"); // duplicate — should be ignored

        var episode = await builder.CompleteAsync(EpisodeOutcome.Success, "Done");

        episode.TouchedFiles.Should().HaveCount(2);
        episode.TouchedFiles.Should().Contain("src/Foo.cs");
        episode.TouchedFiles.Should().Contain("src/Bar.cs");
    }

    [TestMethod]
    public async Task Builder_NormalizesPathSeparators()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Investigate", EpisodeType.Investigation);

        builder.TouchedFile(@"src\ast\Foo.cs");

        var episode = await builder.CompleteAsync(EpisodeOutcome.Success, "Done");

        episode.TouchedFiles.Should().Contain("src/ast/Foo.cs");
    }

    [TestMethod]
    public async Task Builder_AccumulatesObservationsAndEvidence()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Investigate", EpisodeType.Investigation);

        builder
            .WithObservation("pattern", "Uses DI extensively")
            .WithObservation("pattern", "Also uses options pattern") // same category OK
            .WithEvidence("error_message", "NullReferenceException at line 42");

        var episode = await builder.CompleteAsync(EpisodeOutcome.Success, "Done");

        episode.Observations.Should().HaveCount(2);
        episode.Evidence.Should().ContainSingle();
    }

    [TestMethod]
    public async Task Complete_CreatesEdgesForTouchedFilesAndSymbols()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Fix", EpisodeType.BugFix);

        builder
            .TouchedFile("src/Foo.cs")
            .TouchedSymbol("MyNamespace.MyClass");

        var episode = await builder.CompleteAsync(EpisodeOutcome.Success, "Fixed");

        // Check edges were created
        var fileEdges = await this.store.GetOutgoingEdgesAsync(
            episode.Id, MemoryEdgeType.EpisodeTouchedFile);
        fileEdges.Should().ContainSingle(e => e.TargetId == "src/Foo.cs");

        var symbolEdges = await this.store.GetOutgoingEdgesAsync(
            episode.Id, MemoryEdgeType.EpisodeTouchedSymbol);
        symbolEdges.Should().ContainSingle(e => e.TargetId == "MyNamespace.MyClass");
    }

    [TestMethod]
    public async Task Complete_SetsDuration()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Work", EpisodeType.Feature);

        // Small delay to ensure measurable duration
        await Task.Delay(50);

        var episode = await builder.CompleteAsync(EpisodeOutcome.Success, "Done");

        episode.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [TestMethod]
    public async Task Complete_IsIdempotent()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Work", EpisodeType.Feature);

        var first = await builder.CompleteAsync(EpisodeOutcome.Success, "Done");
        var second = await builder.CompleteAsync(EpisodeOutcome.Failed, "Changed mind");

        // Second call should return same episode, not modify it
        second.Outcome.Should().Be(EpisodeOutcome.Success);
        second.Summary.Should().Be("Done");
    }

    [TestMethod]
    public async Task Fail_SetsFailedOutcome()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Attempt", EpisodeType.BugFix);

        var episode = await builder.FailAsync("Could not reproduce");

        episode.Outcome.Should().Be(EpisodeOutcome.Failed);
        episode.Summary.Should().Be("Could not reproduce");
    }

    [TestMethod]
    public async Task Abandon_SetsAbandonedOutcome()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Low priority", EpisodeType.Refactor);

        var episode = await builder.AbandonAsync("Deprioritized");

        episode.Outcome.Should().Be(EpisodeOutcome.Abandoned);
    }

    [TestMethod]
    public async Task Builder_ThrowsAfterFinalized()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Work", EpisodeType.Feature);
        await builder.CompleteAsync(EpisodeOutcome.Success, "Done");

        var act = () => builder.TouchedFile("src/Late.cs");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already been finalized*");
    }

    [TestMethod]
    public async Task Builder_Tags_NoDuplicates()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Work", EpisodeType.Feature);

        builder
            .WithTag("etw")
            .WithTag("parser")
            .WithTag("ETW"); // case-insensitive duplicate

        var episode = await builder.CompleteAsync(EpisodeOutcome.Success, "Done");

        episode.Tags.Should().HaveCount(2);
    }

    [TestMethod]
    public void Constructor_NullStore_Throws()
    {
        var act = () => new EpisodeRecorder(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
