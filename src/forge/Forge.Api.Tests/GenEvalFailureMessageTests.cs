// -----------------------------------------------------------------------
// <copyright file="GenEvalFailureMessageTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// The halted banner shows this text. When the loop only replayed a verdict
/// already saved at the iteration cap, Retry can never make progress, so the
/// message must say no agent ran and point to the document's Restart.
/// </summary>
public sealed class GenEvalFailureMessageTests
{
    [Fact]
    public void A_Replayed_Capped_Verdict_Points_To_Restart_Not_Retry()
    {
        var replay = new DeliverableLoopResult("sloppy", 65, 0, Array.Empty<long>(), 0m, ReplayedFromHistory: true, IterationCap: 50);

        var msg = CrewStoryArtifactStrategy.FailedLoopMessage("plan:architecture.md", replay);

        Assert.StartsWith("architecture.md is already at its iteration cap (65/50) with verdict=sloppy", msg);
        Assert.Contains("No agent ran", msg);
        Assert.Contains("use Restart (Conversation tab)", msg);
        Assert.DoesNotContain("bump max-iterations", msg);
    }

    [Fact]
    public void A_Loop_That_Actually_Ran_Keeps_The_Original_Wording()
    {
        var ran = new DeliverableLoopResult("stalled", 2, 0, new long[] { 1, 2 }, 0.11m);

        var msg = CrewStoryArtifactStrategy.FailedLoopMessage("plan:architecture.md", ran);

        Assert.Equal(
            "Gen-eval loop for plan:architecture.md ended with verdict=stalled after 2 iterations (final score 0). "
            + "The plan PR will not be opened -- bump max-iterations or restart the architect crew.",
            msg);
    }
}