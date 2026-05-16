// -----------------------------------------------------------------------
// <copyright file="WorkItemRulesTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Domain;
using Forge.Api.Persistence;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 12.11 — direct unit tests on <see cref="WorkItemRules"/>. These run
/// without HTTP / WebApplicationFactory so the parent-rank / cross-track /
/// cycle invariants are pinned independently of the endpoint plumbing.
/// </summary>
public class WorkItemRulesTests
{
    [Theory]
    [InlineData("epic",    "dev")]
    [InlineData("feature", "dev")]
    [InlineData("task",    "dev")]
    [InlineData("bug",     "dev")]
    [InlineData("phase",   "design")]
    [InlineData("stage",   "design")]
    [InlineData("step",    "design")]
    public void TrackOf_Routes_Types_To_The_Right_Ladder(string type, string expectedTrack)
    {
        Assert.Equal(expectedTrack, WorkItemRules.TrackOf(type));
    }

    [Theory]
    [InlineData("epic",    0)]
    [InlineData("phase",   0)]
    [InlineData("feature", 1)]
    [InlineData("stage",   1)]
    [InlineData("task",    2)]
    [InlineData("bug",     2)]
    [InlineData("step",    2)]
    public void Rank_Matches_Q12_1_Ladder_Depth(string type, int expectedRank)
    {
        Assert.Equal(expectedRank, WorkItemRules.Rank(type));
    }

    [Fact]
    public void ValidateParent_Allows_Top_Level_When_Parent_Null()
    {
        var child = new WorkItemRow { Id = "X", Type = "epic" };
        Assert.Null(WorkItemRules.ValidateParent(child, null));
    }

    [Fact]
    public void ValidateParent_Rejects_Self_Parent()
    {
        var item = new WorkItemRow { Id = "X", Type = "feature" };
        var err = WorkItemRules.ValidateParent(item, item);
        Assert.NotNull(err);
        Assert.Contains("its own parent", err);
    }

    [Fact]
    public void ValidateParent_Rejects_Same_Rank()
    {
        var child = new WorkItemRow { Id = "C", Type = "epic" };
        var parent = new WorkItemRow { Id = "P", Type = "epic" };
        var err = WorkItemRules.ValidateParent(child, parent);
        Assert.NotNull(err);
        Assert.Contains("rank", err, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateParent_Rejects_Cross_Track_Below_Top_Level()
    {
        // feature (dev, rank 1) under stage (design, rank 1) — same rank
        // would already trip; force the rank to differ by giving a phase
        // (design, rank 0) parent and a feature (dev, rank 1) child.
        var feature = new WorkItemRow { Id = "F", Type = "feature" };
        var phase = new WorkItemRow { Id = "P", Type = "phase" };
        var err = WorkItemRules.ValidateParent(feature, phase);
        Assert.NotNull(err);
        Assert.Contains("Cross-track", err, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateParent_Allows_Same_Track_Strictly_Higher_Rank()
    {
        var feature = new WorkItemRow { Id = "F", Type = "feature" };
        var epic = new WorkItemRow { Id = "E", Type = "epic" };
        Assert.Null(WorkItemRules.ValidateParent(feature, epic));

        var stage = new WorkItemRow { Id = "G", Type = "stage" };
        var phase = new WorkItemRow { Id = "P", Type = "phase" };
        Assert.Null(WorkItemRules.ValidateParent(stage, phase));
    }

    [Fact]
    public async Task DetectCycle_Walks_Up_The_Chain_And_Reports_Hit()
    {
        // Build a chain in a real DB: A → B → C; then ask if C can become A's parent.
        // The walk from A's proposed parent (C) walks C.parent = B, B.parent = A → cycle at A.
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        db.WorkItems.AddRange(
            new WorkItemRow { Id = "A", Type = "epic", Track = "dev", State = "active", Title = "A", ParentId = null },
            new WorkItemRow { Id = "B", Type = "feature", Track = "dev", State = "active", Title = "B", ParentId = "A" },
            new WorkItemRow { Id = "C", Type = "task", Track = "dev", State = "active", Title = "C", ParentId = "B" });
        await db.SaveChangesAsync();

        var hit = await WorkItemRules.DetectCycleAsync(db, "A", "C", default);
        Assert.NotNull(hit);
        Assert.Equal("A", hit);
    }

    [Fact]
    public async Task DetectCycle_Returns_Null_When_No_Cycle()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        db.WorkItems.Add(new WorkItemRow { Id = "Z", Type = "epic", Track = "dev", State = "active", Title = "Z" });
        await db.SaveChangesAsync();

        var hit = await WorkItemRules.DetectCycleAsync(db, "A", "Z", default);
        Assert.Null(hit);
    }

    [Fact]
    public async Task DetectCycle_Returns_Null_For_Empty_Parent_Proposal()
    {
        using var factory = new ForgeWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        Assert.Null(await WorkItemRules.DetectCycleAsync(db, "A", null, default));
        Assert.Null(await WorkItemRules.DetectCycleAsync(db, "A", string.Empty, default));
    }
}
