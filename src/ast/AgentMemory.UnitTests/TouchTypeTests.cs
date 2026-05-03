// -----------------------------------------------------------------------
// <copyright file="TouchTypeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentMemory.UnitTests;

using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class TouchTypeTests
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
    public async Task TouchedFile_PreservesTouchTypeInEdge()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Modify file", EpisodeType.BugFix);

        builder.TouchedFile("src/Foo.cs", TouchType.Modified);

        var episode = await builder.CompleteAsync(EpisodeOutcome.Success, "Fixed");

        var edges = await this.store.GetOutgoingEdgesAsync(
            episode.Id, MemoryEdgeType.EpisodeTouchedFile);

        edges.Should().ContainSingle();
        edges[0].TouchType.Should().Be(TouchType.Modified);
    }

    [TestMethod]
    public async Task TouchedFile_DefaultsTouchTypeToRead()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Read file", EpisodeType.Investigation);

        builder.TouchedFile("src/Foo.cs");

        var episode = await builder.CompleteAsync(EpisodeOutcome.Success, "Investigated");

        var edges = await this.store.GetOutgoingEdgesAsync(
            episode.Id, MemoryEdgeType.EpisodeTouchedFile);

        edges[0].TouchType.Should().Be(TouchType.Read);
    }

    [TestMethod]
    public async Task TouchedSymbol_PreservesTouchTypeInEdge()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Test symbol", EpisodeType.Testing);

        builder.TouchedSymbol("MyApp.Parser.Parse", TouchType.Tested);

        var episode = await builder.CompleteAsync(EpisodeOutcome.Success, "Tested");

        var edges = await this.store.GetOutgoingEdgesAsync(
            episode.Id, MemoryEdgeType.EpisodeTouchedSymbol);

        edges.Should().ContainSingle();
        edges[0].TouchType.Should().Be(TouchType.Tested);
    }

    [TestMethod]
    public async Task TouchedFile_MultipleFiles_DifferentTouchTypes()
    {
        var builder = await this.recorder.StartEpisodeAsync(
            "agent-1", "Refactor", EpisodeType.Refactor);

        builder
            .TouchedFile("src/Old.cs", TouchType.Deleted)
            .TouchedFile("src/New.cs", TouchType.Created)
            .TouchedFile("src/Shared.cs", TouchType.Read);

        var episode = await builder.CompleteAsync(EpisodeOutcome.Success, "Refactored");

        var edges = await this.store.GetOutgoingEdgesAsync(
            episode.Id, MemoryEdgeType.EpisodeTouchedFile);

        edges.Should().HaveCount(3);

        var deletedEdge = edges.First(e => e.TargetId == "src/Old.cs");
        deletedEdge.TouchType.Should().Be(TouchType.Deleted);

        var createdEdge = edges.First(e => e.TargetId == "src/New.cs");
        createdEdge.TouchType.Should().Be(TouchType.Created);

        var readEdge = edges.First(e => e.TargetId == "src/Shared.cs");
        readEdge.TouchType.Should().Be(TouchType.Read);
    }
}
