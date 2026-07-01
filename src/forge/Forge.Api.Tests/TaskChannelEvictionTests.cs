// -----------------------------------------------------------------------
// <copyright file="TaskChannelEvictionTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Realtime;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Regression guard for the per-work-item transcript cap
/// (<see cref="TaskChannel.EvictOverCapAsync"/>). A single tool-heavy
/// generator iteration can emit &gt;10 000 token/tool events on its own; the
/// original eviction deleted oldest-first with no exclusions, which threw
/// away THAT iteration's <c>task.prompt</c> (the first row of the iter)
/// mid-run and left the Overview pane's generator/evaluator prompt blank
/// even though the run was live and accruing cost. These tests lock in that
/// <see cref="TaskChannel.NeverEvictKinds"/> (prompts, iteration markers,
/// gate results) survive eviction while the bulky token rows are trimmed.
/// </summary>
public class TaskChannelEvictionTests
{
    private const string WorkItemId = "wi-evict-guard-1";

    [Fact]
    public async Task Evict_KeepsPromptRow_TrimsTokenRowsToCap()
    {
        using var f = new ForgeWebApplicationFactory();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Oldest row is the iteration's task.prompt (mirrors production: the
        // prompt is emitted at the top of each iter, before any token/tool
        // event), followed by 10 bulky task.token rows.
        db.TaskTranscriptEvents.Add(Row("task.prompt"));
        for (var i = 0; i < 10; i++) db.TaskTranscriptEvents.Add(Row("task.token"));
        await db.SaveChangesAsync();

        // cap = 5 → 11 rows, 6 to evict. Only the 6 oldest NON-protected
        // (token) rows may be removed.
        await TaskChannel.EvictOverCapAsync(db, WorkItemId, cap: 5, ct: default);

        var rows = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == WorkItemId)
            .ToListAsync();

        // Total trimmed to cap, prompt survives, remainder are tokens.
        Assert.Equal(5, rows.Count);
        Assert.Single(rows, r => r.Kind == "task.prompt");
        Assert.Equal(4, rows.Count(r => r.Kind == "task.token"));
    }

    [Fact]
    public async Task Evict_NeverDeletesProtectedRows_EvenWhenTheyExceedCap()
    {
        using var f = new ForgeWebApplicationFactory();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // 5 protected rows (a prompt + gate/iteration markers) + 3 tokens.
        db.TaskTranscriptEvents.Add(Row("task.prompt"));
        db.TaskTranscriptEvents.Add(Row("task.iteration.scored"));
        db.TaskTranscriptEvents.Add(Row("task.build-gate.passed"));
        db.TaskTranscriptEvents.Add(Row("task.test-gate.failed"));
        db.TaskTranscriptEvents.Add(Row("task.iteration.passed"));
        for (var i = 0; i < 3; i++) db.TaskTranscriptEvents.Add(Row("task.token"));
        await db.SaveChangesAsync();

        // cap = 2 → 8 rows, 6 to evict — but only 3 are evictable (tokens).
        // The protected set must never be deleted even though the resulting
        // count (5) stays above the cap.
        await TaskChannel.EvictOverCapAsync(db, WorkItemId, cap: 2, ct: default);

        var rows = await db.TaskTranscriptEvents.AsNoTracking()
            .Where(e => e.WorkItemId == WorkItemId)
            .ToListAsync();

        Assert.Equal(5, rows.Count);
        Assert.Equal(0, rows.Count(r => r.Kind == "task.token"));
        Assert.All(rows, r => Assert.Contains(r.Kind, TaskChannel.NeverEvictKinds));
    }

    [Fact]
    public async Task Evict_NoOp_WhenUnderCap()
    {
        using var f = new ForgeWebApplicationFactory();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        db.TaskTranscriptEvents.Add(Row("task.prompt"));
        db.TaskTranscriptEvents.Add(Row("task.token"));
        await db.SaveChangesAsync();

        await TaskChannel.EvictOverCapAsync(db, WorkItemId, cap: 10, ct: default);

        var count = await db.TaskTranscriptEvents.AsNoTracking()
            .CountAsync(e => e.WorkItemId == WorkItemId);
        Assert.Equal(2, count);
    }

    private static TaskTranscriptEventRow Row(string kind) => new()
    {
        WorkItemId = WorkItemId,
        Kind = kind,
        PayloadJson = "{}",
        At = DateTimeOffset.UtcNow,
    };
}
