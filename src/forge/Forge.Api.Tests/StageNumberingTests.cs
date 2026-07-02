// -----------------------------------------------------------------------
// <copyright file="StageNumberingTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Collections.Generic;
using Forge.Api.Services;
using Xunit;
using Ws = Forge.Api.Services.StageNumbering.StageWs;

/// <summary>
/// Server-side mirror of the UI's <c>stageNumbering.spec.ts</c> lock-in — the
/// chat alert must number a stage the same way the swarm board does.
/// </summary>
public class StageNumberingTests
{
    [Fact]
    public void Impl_Rows_Number_Positionally_Per_Phase()
    {
        var labels = StageNumbering.Compute(new List<Ws>
        {
            new("a", "phase-1", "phase-1/stage-a", "impl"),
            new("b", "phase-1", "phase-1/stage-b", "impl"),
            new("c", "phase-2", "phase-2/stage-c", "impl"),
        });

        Assert.Equal("1.1", labels["a"]);
        Assert.Equal("1.2", labels["b"]);
        Assert.Equal("2.1", labels["c"]);
    }

    [Fact]
    public void QaE2e_Row_Inherits_Impl_Sibling_Number_And_Does_Not_Consume_A_Slot()
    {
        var labels = StageNumbering.Compute(new List<Ws>
        {
            new("a", "phase-1", "phase-1/stage-a", "impl"),
            new("b", "phase-1", "phase-1/stage-b", "impl"),
            new("b-e2e", "phase-1", "phase-1/stage-b/e2e", "qa-e2e"),
            new("c", "phase-1", "phase-1/stage-c", "impl"),
        });

        Assert.Equal("1.1", labels["a"]);
        Assert.Equal("1.2", labels["b"]);
        Assert.Equal("1.2-e2e", labels["b-e2e"]); // inherits sibling, no slot bump
        Assert.Equal("1.3", labels["c"]);          // NOT bumped to 1.4 by the e2e row
    }

    [Fact]
    public void Reproduces_The_Net10_Stalled_Stage_Label()
    {
        // Mirrors the real stalled row: a qa-e2e stage under the de-risk phase,
        // second impl stage in that phase → "1.2-e2e".
        const string Phase = "phase-de-risk-and-feed-foundation";
        var labels = StageNumbering.Compute(new List<Ws>
        {
            new("impl-1", Phase, $"{Phase}/stage-foundation", "impl"),
            new("impl-2", Phase, $"{Phase}/stage-component-mapping-and-persistence-spike", "impl"),
            new("e2e-2", Phase, $"{Phase}/stage-component-mapping-and-persistence-spike/e2e", "qa-e2e"),
        });

        Assert.Equal("1.2-e2e", labels["e2e-2"]);
    }

    [Fact]
    public void Orphan_QaE2e_Without_Impl_Sibling_Is_Absent()
    {
        var labels = StageNumbering.Compute(new List<Ws>
        {
            new("lonely-e2e", "phase-1", "phase-1/stage-x/e2e", "qa-e2e"),
        });

        Assert.False(labels.ContainsKey("lonely-e2e"));
    }

    [Fact]
    public void DeltaSplit_Impl_Anchor_Still_Pairs_With_Its_E2e()
    {
        // A shipped stage re-anchored to "{anchor}@{hash}" must still pair with
        // its bare "{anchor}/e2e" companion (base-anchor match).
        var labels = StageNumbering.Compute(new List<Ws>
        {
            new("impl", "phase-1", "phase-1/stage-a@deadbeef", "impl"),
            new("e2e", "phase-1", "phase-1/stage-a/e2e", "qa-e2e"),
        });

        Assert.Equal("1.1", labels["impl"]);
        Assert.Equal("1.1-e2e", labels["e2e"]);
    }
}
