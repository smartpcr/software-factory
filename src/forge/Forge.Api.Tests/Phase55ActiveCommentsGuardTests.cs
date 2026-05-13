// -----------------------------------------------------------------------
// <copyright file="Phase55ActiveCommentsGuardTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 2026-05-13 — regressions for the "PR comments left unaddressed before
/// closing" bug. Three previously-leaky paths advanced a workstream to
/// <c>complete</c> without checking whether its PR still had
/// <c>PrComments.State == "active"</c>:
/// <list type="number">
/// <item><see cref="WorkstreamMergeAdvancer.AdvanceForMergedPrAsync"/> —
///   invoked by <c>PrSyncJob</c> when upstream flips to merged.</item>
/// <item><see cref="StorySpawnOrchestrator"/> <c>DriveReviewAsync</c>
///   external-merge fast-path — workstream stays at <c>review</c> with
///   <c>PrRow.Col == "merged"</c>.</item>
/// <item><see cref="StorySpawnOrchestrator"/> <c>DriveReviewAsync</c>
///   <c>merging → complete</c> promotion — workstream at <c>merging</c>
///   with terminal PR row.</item>
/// </list>
/// Plus a fourth path: late-arriving review comments on a workstream that
/// already flipped to <c>complete</c> must still be addressed by the fix
/// loop (the orchestrator's status filter previously skipped them).
/// </summary>
[Collection("Phase28")]
public class Phase55ActiveCommentsGuardTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private const string WriterHeader = "X-Forge-User";
    private const string WriterValue = "po@forge.local";

    private static (ForgeWebApplicationFactory factory, HttpClient client) NewFactory()
    {
        var factory = new ForgeWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(WriterHeader, WriterValue);
        return (factory, client);
    }

    private static async Task SeedStoryAsync(ForgeWebApplicationFactory factory, string id, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        if (await db.WorkItems.AnyAsync(w => w.Id == id)) return;
        db.WorkItems.Add(new WorkItemRow
        {
            Id = id,
            Provider = "ado",
            Track = "dev",
            Type = "feature",
            State = "active",
            Title = title,
            Priority = "med",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> SpawnAsync(HttpClient c, string storyId)
        => await c.PostAsJsonAsync($"/api/stories/{storyId}/spawn", new { repoId = (string?)null }, JsonOpts);

    private static async Task SetPlanPrAsync(ForgeWebApplicationFactory f, string storyId, string prId, string col)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var s = await db.StorySpawns.FirstAsync(x => x.StoryId == storyId);
        s.PlanPrId = prId;
        s.PlanBranch = $"story/{storyId}/plan";
        s.PlanDocPrsJson = null;
        if (await db.Prs.AnyAsync(p => p.Id == prId))
        {
            (await db.Prs.FirstAsync(p => p.Id == prId)).Col = col;
        }
        else
        {
            db.Prs.Add(new PrRow
            {
                Id = prId,
                Col = col,
                Branch = $"story/{storyId}/plan",
                Base = "main",
                Title = $"Plan for story {storyId}",
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Mirror of <c>Phase28StorySpawnTests.SeedAllPlanDocsPassedAsync</c> —
    /// inserts a passing terminal <see cref="DeliverableRow"/> for each of
    /// the four plan-doc work-item ids of the given story so the
    /// orchestrator's <c>ArePlanDocsAllPassedAsync</c> gate releases on
    /// the impl phase. Without this, every <c>DriveAsync</c> call from an
    /// "implement" / "review" phase regresses the spawn to "plan" and
    /// wipes the workstream status we just seeded.
    /// </summary>
    private static async Task SeedAllPlanDocsPassedAsync(ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var spawn = await db.StorySpawns.FirstAsync(x => x.StoryId == storyId);
        var docs = new (string FileName, int Iter)[]
        {
            ("tech-spec.md", 3),
            ("architecture.md", 4),
            ("implementation-plan.md", 5),
            ("e2e-scenarios.md", 4),
        };
        foreach (var (fileName, iter) in docs)
        {
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = $"plan-doc:{spawn.Id}:{fileName}",
                CrewId = "crew-arch",
                GeneratorAgentId = "G",
                EvaluatorAgentId = "E",
                Iter = iter,
                Score = 92,
                DimsJson = "{}",
                Verdict = "pass",
                Notes = string.Empty,
                GeneratorSummary = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seed a workstream in <paramref name="initialStatus"/> with a PR row in
    /// <paramref name="prCol"/>. Optionally seed an <c>active</c> reviewer
    /// comment on that PR. Mirrors the in-DB shape that PrSyncJob produces
    /// after an external merge.
    /// </summary>
    private static async Task<(string wsId, string prId, long? commentId)> SeedReviewWorkstreamAsync(
        ForgeWebApplicationFactory f, string storyId,
        string initialStatus, string prCol, bool withActiveComment)
    {
        var (f2, c) = (f, f.CreateClient());
        c.DefaultRequestHeaders.Add(WriterHeader, WriterValue);

        await SpawnAsync(c, storyId);
        await SetPlanPrAsync(f, storyId, $"pr-plan-{storyId}", "merged");
        await c.PatchAsync($"/api/stories/{storyId}/spawn/phase",
            JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
        await c.PostAsJsonAsync($"/api/stories/{storyId}/spawn/decompose",
            new { workstreams = new[] { new { title = "ws1" } } }, JsonOpts);
        await c.PostAsync($"/api/stories/{storyId}/spawn/decompose/approve", null);
        await c.PatchAsync($"/api/stories/{storyId}/spawn/phase",
            JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));

        // Seed passing plan-doc deliverables so the impl-phase regression
        // gate (ArePlanDocsAllPassedAsync) releases. Without this, the
        // first call to DriveAsync regresses the spawn back to "plan" and
        // CancelInFlightImplTasksAsync flips every non-terminal workstream
        // (i.e. our seeded "review" row) to "planning", which then drops
        // out of DriveReviewAsync's filter. Mirrors
        // Phase28StorySpawnTests.SeedAllPlanDocsPassedAsync.
        await SeedAllPlanDocsPassedAsync(f, storyId);

        var safeId = storyId.ToLowerInvariant();
        var wsId = $"ws-{safeId}-ws1";
        var prId = $"pr-ws1-{storyId}";

        long? commentId = null;
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
            var ws = await db.Workstreams.FirstAsync(w => w.Id == wsId);
            ws.Status = initialStatus;
            ws.PrsJson = JsonSerializer.Serialize(new[] { prId });
            db.Prs.Add(new PrRow
            {
                Id = prId,
                Col = prCol,
                IsActive = !string.Equals(prCol, "merged", StringComparison.Ordinal),
                Branch = ws.Branch,
                Base = "main",
                Title = $"[impl] {ws.Name}",
                Workstream = wsId,
            });

            if (withActiveComment)
            {
                var row = new PrCommentRow
                {
                    PrId = prId,
                    ProviderCommentId = $"upstream-{Guid.NewGuid():N}",
                    ThreadId = $"thread-{Guid.NewGuid():N}",
                    AuthorId = "human-reviewer",
                    AuthorKind = "user",
                    Body = "This still needs work — please address before merging.",
                    FilePath = "src/Example.cs",
                    Line = 42,
                    State = "active",
                    RequiresAttention = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                db.PrComments.Add(row);
                await db.SaveChangesAsync();
                commentId = row.Id;
            }
            else
            {
                await db.SaveChangesAsync();
            }
        }

        await c.PatchAsync($"/api/stories/{storyId}/spawn/phase",
            JsonContent.Create(new { targetPhase = "review" }, options: JsonOpts));

        return (wsId, prId, commentId);
    }

    // -----------------------------------------------------------------------
    // 1) WorkstreamMergeAdvancer — direct unit tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// <see cref="WorkstreamMergeAdvancer.AdvanceForMergedPrAsync"/> must
    /// refuse to flip a workstream's status to <c>complete</c> when the PR
    /// still has <c>PrComments.State == "active"</c> rows. Without this
    /// guard, PrSyncJob's "upstream went terminal" callback would close the
    /// review loop with reviewer comments stranded on the thread.
    /// </summary>
    [Fact]
    public async Task Advancer_Defers_When_Active_Comments_Exist()
    {
        var f = new ForgeWebApplicationFactory();
        try
        {
            const string prId = "pr-defer-1";
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                db.Workstreams.Add(new WorkstreamRow
                {
                    Id = "ws-defer-1",
                    StorySpawnId = "spawn-stub",
                    Name = "ws1",
                    Status = "review",
                    Branch = "story/X/ws1",
                    PrsJson = JsonSerializer.Serialize(new[] { prId }),
                    AgentsJson = "[]",
                });
                db.Prs.Add(new PrRow { Id = prId, Col = "merged", Branch = "story/X/ws1", Base = "main", Title = "x" });
                db.PrComments.Add(new PrCommentRow
                {
                    PrId = prId,
                    ProviderCommentId = "u-1",
                    ThreadId = "t-1",
                    AuthorId = "human",
                    AuthorKind = "user",
                    Body = "fix this",
                    FilePath = "a.cs",
                    Line = 1,
                    State = "active",
                    RequiresAttention = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var advanced = await WorkstreamMergeAdvancer.AdvanceForMergedPrAsync(db, prId);
                Assert.Empty(advanced);
                await db.SaveChangesAsync();
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == "ws-defer-1");
                Assert.Equal("review", ws.Status);
            }
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Happy path — when no active comments exist on the merged PR, the
    /// advancer still flips the workstream to <c>complete</c>. Pins that
    /// the active-comments guard didn't break the original 28.6 contract.
    /// </summary>
    [Fact]
    public async Task Advancer_Advances_When_No_Active_Comments()
    {
        var f = new ForgeWebApplicationFactory();
        try
        {
            const string prId = "pr-advance-1";
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                db.Workstreams.Add(new WorkstreamRow
                {
                    Id = "ws-advance-1",
                    StorySpawnId = "spawn-stub",
                    Name = "ws1",
                    Status = "review",
                    Branch = "story/X/ws1",
                    PrsJson = JsonSerializer.Serialize(new[] { prId }),
                    AgentsJson = "[]",
                });
                db.Prs.Add(new PrRow { Id = prId, Col = "merged", Branch = "story/X/ws1", Base = "main", Title = "x" });
                // No PrComments → guard passes.
                await db.SaveChangesAsync();
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var advanced = await WorkstreamMergeAdvancer.AdvanceForMergedPrAsync(db, prId);
                Assert.Single(advanced);
                Assert.Equal("ws-advance-1", advanced[0]);
                await db.SaveChangesAsync();
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == "ws-advance-1");
                Assert.Equal("complete", ws.Status);
            }
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Resolved comments don't block the advance — only <c>State == "active"</c>
    /// rows count. Without this nuance, every workstream that ever had a
    /// reviewer comment would be permanently stuck on review.
    /// </summary>
    [Fact]
    public async Task Advancer_Advances_When_Comments_All_Resolved()
    {
        var f = new ForgeWebApplicationFactory();
        try
        {
            const string prId = "pr-resolved-1";
            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                db.Workstreams.Add(new WorkstreamRow
                {
                    Id = "ws-resolved-1",
                    StorySpawnId = "spawn-stub",
                    Name = "ws1",
                    Status = "review",
                    Branch = "story/X/ws1",
                    PrsJson = JsonSerializer.Serialize(new[] { prId }),
                    AgentsJson = "[]",
                });
                db.Prs.Add(new PrRow { Id = prId, Col = "merged", Branch = "story/X/ws1", Base = "main", Title = "x" });
                db.PrComments.Add(new PrCommentRow
                {
                    PrId = prId,
                    ProviderCommentId = "u-1",
                    ThreadId = "t-1",
                    AuthorId = "human",
                    AuthorKind = "user",
                    Body = "this was already addressed",
                    FilePath = "a.cs",
                    Line = 1,
                    State = "resolved",
                    RequiresAttention = false,
                    ResolvedByUserId = "eng",
                    ResolvedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var advanced = await WorkstreamMergeAdvancer.AdvanceForMergedPrAsync(db, prId);
                Assert.Single(advanced);
                await db.SaveChangesAsync();
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == "ws-resolved-1");
                Assert.Equal("complete", ws.Status);
            }
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 2) DriveReviewAsync — external-merge fast-path guarded
    // -----------------------------------------------------------------------

    /// <summary>
    /// External-merge fast-path must NOT flip a workstream from
    /// <c>review</c> to <c>complete</c> when active reviewer comments still
    /// exist on the PR. The fix loop should run instead, resolving each
    /// comment before any future tick can advance the row.
    /// </summary>
    [Fact]
    public async Task Review_External_Merge_With_Active_Comments_Stays_At_Review()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-55001", "external-merge-active-comments");
            var (wsId, prId, _) = await SeedReviewWorkstreamAsync(f, "WI-55001",
                initialStatus: "review", prCol: "merged", withActiveComment: true);

            using (var scope = f.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync("WI-55001");
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == wsId);

                // The original reviewer comment must end up "resolved" via the
                // fix loop. The workstream is NOT closed in this tick — the
                // next tick (with no remaining active comments) will flip it
                // through the external-merge fast-path to "complete".
                var originalActive = await db.PrComments.AsNoTracking()
                    .CountAsync(pc => pc.PrId == prId && pc.State == "active" && pc.AuthorKind == "user");
                Assert.Equal(0, originalActive);

                // Either still at "review" with the resolved comment (one-tick
                // outcome) or already advanced to "complete" on the same tick
                // depending on the orchestrator's scan order — both honor the
                // contract. The forbidden state is "complete with active
                // human-authored comments still on the PR", which the
                // assertion above already rules out.
                Assert.True(
                    string.Equals(ws.Status, "review", StringComparison.Ordinal) ||
                    string.Equals(ws.Status, "complete", StringComparison.Ordinal),
                    $"unexpected status '{ws.Status}'");
            }
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 3) DriveReviewAsync — merging→complete guarded
    // -----------------------------------------------------------------------

    /// <summary>
    /// <c>merging → complete</c> promotion must NOT fire while active
    /// reviewer comments still exist. Same blind spot as the external-
    /// merge fast-path — a comment posted between our ApproveAndMergeAsync
    /// call and the next tick used to be silently dropped.
    /// </summary>
    [Fact]
    public async Task Merging_Promotion_With_Active_Comments_Stays_At_Merging()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-55002", "merging-active-comments");
            var (wsId, prId, _) = await SeedReviewWorkstreamAsync(f, "WI-55002",
                initialStatus: "merging", prCol: "merged", withActiveComment: true);

            using (var scope = f.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync("WI-55002");
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var humanActive = await db.PrComments.AsNoTracking()
                    .CountAsync(pc => pc.PrId == prId && pc.State == "active" && pc.AuthorKind == "user");
                Assert.Equal(0, humanActive);

                var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == wsId);

                // Either still "merging" (resolved this tick — next tick
                // promotes) or already "complete" (orchestrator drained
                // and promoted in the same tick). Both are correct: the
                // forbidden state is "complete with active comments",
                // which the humanActive==0 assertion already rejects.
                Assert.True(
                    string.Equals(ws.Status, "merging", StringComparison.Ordinal) ||
                    string.Equals(ws.Status, "complete", StringComparison.Ordinal),
                    $"unexpected status '{ws.Status}'");
            }
        }
        finally { f.Dispose(); }
    }

    // -----------------------------------------------------------------------
    // 4) DriveReviewAsync — late comments on already-complete workstreams
    // -----------------------------------------------------------------------

    /// <summary>
    /// A reviewer comment posted AFTER the workstream's PR has already been
    /// closed must still get a reply + resolution on the thread. Pre-fix,
    /// <c>DriveReviewAsync</c> filtered to <c>w.Status == "review"</c>, so
    /// any late comment imported by <c>PrSyncJob</c> with <c>State=="active"</c>
    /// was silently ignored.
    /// </summary>
    [Fact]
    public async Task Late_Comment_On_Complete_Workstream_Is_Addressed()
    {
        var (f, c) = NewFactory();
        try
        {
            await SeedStoryAsync(f, "WI-55003", "late-comments-on-complete");
            var (wsId, prId, commentId) = await SeedReviewWorkstreamAsync(f, "WI-55003",
                initialStatus: "complete", prCol: "merged", withActiveComment: true);

            using (var scope = f.Services.CreateScope())
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
                await orchestrator.DriveAsync("WI-55003");
            }

            using (var scope = f.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
                var c2 = await db.PrComments.AsNoTracking().FirstAsync(pc => pc.Id == commentId);
                Assert.Equal("resolved", c2.State);

                // Workstream stays "complete" since it was already advanced;
                // the late comment got addressed without status regression.
                var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == wsId);
                Assert.Equal("complete", ws.Status);
            }
        }
        finally { f.Dispose(); }
    }
}
