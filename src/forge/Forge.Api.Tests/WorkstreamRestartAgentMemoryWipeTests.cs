// -----------------------------------------------------------------------
// <copyright file="WorkstreamRestartAgentMemoryWipeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Regression tests for the Phase 55 agent_memory leak across the two
/// "clean slate" code paths — operator-initiated Restart and the Phase
/// 52 sloppy auto-reassignment — that both used to leave
/// <see cref="AgentMemoryRow"/> rows for the prior pair intact.
///
/// <para>User-observed failure (2026-05-26):
/// <c>ws-local-http-memory-phase-foundations-stage-add-banned-api-analyzer-wiring</c>
/// stuck for 48+ hours despite 20+ Restart clicks. Each restart wiped
/// transcript / deliverables / questions / pair-assignments / agent_usage
/// but left 11 stale agent_memory rows (HD3=292K tokens LastIter=8,
/// HB=194K, LG=150K, JI=108K, …). When the orchestrator re-assigned
/// the workitem and the picker happened to pick a prior-pair agent
/// (Phase 52 PriorPairsJson exclusion is best-effort across crews), the
/// runner loaded the stale row via <see cref="AgentMemoryStore.GetHandleAsync"/>
/// and replayed the 100K-300K-token conversation as priorTurns into
/// Copilot/Claude SDK; the next iter blew the 24-min wall-clock budget
/// in <see cref="Services.Stories.CrewStoryArtifactStrategy.BudgetForPhase"/>
/// every time. Cost climbed past $360 with zero forward progress.</para>
///
/// <para>Three regressions pinned here:</para>
/// <list type="number">
///   <item>Restart endpoint must call
///         <see cref="AgentMemoryStore.DeleteForWorkItemAsync"/> so the
///         next attempt starts with no replayed conversation.</item>
///   <item>Restart endpoint must NOT touch agent_memory rows for OTHER
///         workitems (scoped to the row id, no over-deletion).</item>
///   <item>Phase 52 sloppy auto-reassignment (the auto-recovery path
///         that doesn't require operator input) must also wipe
///         agent_memory inside its Q52.4 clean-slate block.</item>
/// </list>
/// </summary>
public class WorkstreamRestartAgentMemoryWipeTests
{
    private const string WriterHeader = "X-Forge-User";
    private const string WriterValue = "po@forge.local";

    // ════════════════════════════════════════════════════════════════════
    //  1. Restart endpoint — wipes agent_memory for the workstream
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Restart_DeletesAllAgentMemoryRows_ForWorkstream()
    {
        using var f = new ForgeWebApplicationFactory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(WriterHeader, WriterValue);

        const string storyId = "WI-mem-restart-1";
        const string spawnId = "spawn-" + storyId;
        const string wsId = "ws-mem-restart-1-stage-1-1";
        await SeedWorkstreamAsync(f, storyId, spawnId, wsId);

        // Seed three agent_memory rows that mirror the production stuck
        // workitem: one generator row with a giant MessagesJson (Claude
        // SDK replay trap), one generator row with a stale SessionId
        // (Copilot SDK resume trap), and one evaluator row.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

            db.AgentMemory.Add(new AgentMemoryRow
            {
                WorkItemId = wsId,
                AgentId = "HD3",
                Role = AgentMemoryRoles.Generator,
                Backend = "claude",
                InvokeMode = "sdk",
                SessionId = null,
                MessagesJson = "[{\"role\":\"user\",\"content\":\"...stale 292K-token blob...\"}]",
                LastIter = 8,
                ApproxInputTokens = 292_000,
            });
            db.AgentMemory.Add(new AgentMemoryRow
            {
                WorkItemId = wsId,
                AgentId = "JI",
                Role = AgentMemoryRoles.Generator,
                Backend = "copilot",
                InvokeMode = "sdk",
                SessionId = "copilot-session-stale-abc123",
                MessagesJson = null,
                LastIter = 3,
                ApproxInputTokens = 108_000,
            });
            db.AgentMemory.Add(new AgentMemoryRow
            {
                WorkItemId = wsId,
                AgentId = "HB",
                Role = AgentMemoryRoles.Evaluator,
                Backend = "claude",
                InvokeMode = "sdk",
                SessionId = null,
                MessagesJson = "[{\"role\":\"assistant\",\"content\":\"...stale eval reasoning...\"}]",
                LastIter = 7,
                ApproxInputTokens = 194_000,
            });
            await db.SaveChangesAsync();
        }

        var resp = await c.PostAsync($"/api/workstreams/{wsId}/restart", content: null);
        Assert.True(resp.IsSuccessStatusCode,
            $"restart failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var remaining = await db.AgentMemory.AsNoTracking()
                .Where(m => m.WorkItemId == wsId)
                .ToListAsync();
            Assert.Empty(remaining);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  2. Restart endpoint — does NOT wipe memory for sibling workstreams
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Restart_LeavesOtherWorkstreams_AgentMemory_Intact()
    {
        using var f = new ForgeWebApplicationFactory();
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(WriterHeader, WriterValue);

        const string storyId = "WI-mem-restart-2";
        const string spawnId = "spawn-" + storyId;
        const string targetWsId = "ws-mem-restart-2-stage-1-1";
        const string siblingWsId = "ws-mem-restart-2-stage-1-2";
        await SeedWorkstreamAsync(f, storyId, spawnId, targetWsId, siblingWsId);

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            db.AgentMemory.Add(new AgentMemoryRow
            {
                WorkItemId = targetWsId,
                AgentId = "agent-target",
                Role = AgentMemoryRoles.Generator,
                Backend = "claude",
                InvokeMode = "sdk",
                MessagesJson = "[]",
                LastIter = 1,
                ApproxInputTokens = 1_000,
            });
            db.AgentMemory.Add(new AgentMemoryRow
            {
                WorkItemId = siblingWsId,
                AgentId = "agent-sibling",
                Role = AgentMemoryRoles.Generator,
                Backend = "claude",
                InvokeMode = "sdk",
                MessagesJson = "[]",
                LastIter = 1,
                ApproxInputTokens = 1_000,
            });
            await db.SaveChangesAsync();
        }

        var resp = await c.PostAsync($"/api/workstreams/{targetWsId}/restart", content: null);
        Assert.True(resp.IsSuccessStatusCode,
            $"restart failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var targetRows = await db.AgentMemory.AsNoTracking()
                .Where(m => m.WorkItemId == targetWsId)
                .ToListAsync();
            Assert.Empty(targetRows);

            var siblingRows = await db.AgentMemory.AsNoTracking()
                .Where(m => m.WorkItemId == siblingWsId)
                .ToListAsync();
            Assert.Single(siblingRows);
            Assert.Equal("agent-sibling", siblingRows[0].AgentId);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  3. SloppyReassignmentReconciler — wipes agent_memory in Q52.4
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SloppyReassignment_DeletesAgentMemory_OnCleanSlate()
    {
        await using var f = new ForgeWebApplicationFactory();
        var disposable = f.CreateClient();
        disposable.Dispose();

        using var scope = f.Services.CreateScope();
        var opts = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<SloppyReassignmentOptions>>();
        opts.CurrentValue.ResetWorktreeOnReassign = false;
        opts.CurrentValue.Enabled = false; // we drive RunOnceAsync manually

        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Roster with enough fresh agents that the reconciler can pick a
        // pair that excludes the prior pair (PriorPairsJson).
        await SeedRosterAsync(control, db, "crew-mem", lead: "agent-lead",
            "agent-a", "agent-b", "agent-c", "agent-d");

        const string wiId = "wi-mem-sloppy-reassign";
        var wi = new WorkItemRow
        {
            Id = wiId, Type = "step", State = "active", Title = "T",
            CreatedAt = System.DateTimeOffset.UtcNow,
            UpdatedAt = System.DateTimeOffset.UtcNow,
            ExecutionStatus = ExecutionStatuses.PendingReassign,
            PairAttemptCount = 1,
            PriorPairsJson = System.Text.Json.JsonSerializer.Serialize(
                new[] { new[] { "agent-a", "agent-b" } }),
        };
        db.WorkItems.Add(wi);
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = wi.Id, CrewId = "crew-mem",
            GeneratorAgentId = "agent-a", EvaluatorAgentId = "agent-b",
            Iter = 4, Score = 78, Verdict = "sloppy",
            DimsJson = "{}", CreatedAt = System.DateTimeOffset.UtcNow,
        });
        db.AgentPairAssignments.Add(new AgentPairAssignmentRow
        {
            WorkItemId = wi.Id, SequenceNo = 1,
            GeneratorAgentId = "agent-a", EvaluatorAgentId = "agent-b",
            CrewId = "crew-mem",
            StartedAt = System.DateTimeOffset.UtcNow.AddMinutes(-10),
            EndedAt = System.DateTimeOffset.UtcNow,
            TotalIters = 4, FinalScore = 78,
            Outcome = AgentPairAssignmentOutcomes.ReassignedSloppy,
            SloppyReason = SloppyReasons.ScorePlateau,
        });

        // Two prior-pair agent_memory rows that must NOT survive the
        // clean slate. Without this fix, the reassigned pair (agent-c /
        // agent-d) would coexist with these stale rows; if the picker
        // ever lands back on agent-a or agent-b for any reason, the
        // 24-min-timeout loop reactivates.
        db.AgentMemory.Add(new AgentMemoryRow
        {
            WorkItemId = wi.Id,
            AgentId = "agent-a",
            Role = AgentMemoryRoles.Generator,
            Backend = "claude",
            InvokeMode = "sdk",
            MessagesJson = "[{\"role\":\"user\",\"content\":\"stale gen turn\"}]",
            LastIter = 4,
            ApproxInputTokens = 220_000,
        });
        db.AgentMemory.Add(new AgentMemoryRow
        {
            WorkItemId = wi.Id,
            AgentId = "agent-b",
            Role = AgentMemoryRoles.Evaluator,
            Backend = "claude",
            InvokeMode = "sdk",
            MessagesJson = "[{\"role\":\"assistant\",\"content\":\"stale eval turn\"}]",
            LastIter = 4,
            ApproxInputTokens = 150_000,
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var reconciler = scope.ServiceProvider.GetRequiredService<SloppyReassignmentReconciler>();
        var progressed = await reconciler.RunOnceAsync(System.Threading.CancellationToken.None);
        Assert.Equal(1, progressed);

        using var readScope = f.Services.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var remaining = await readDb.AgentMemory.AsNoTracking()
            .Where(m => m.WorkItemId == wiId)
            .ToListAsync();
        Assert.Empty(remaining);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a story + spawn + one-or-more workstream rows so the
    /// Restart endpoint can run end-to-end against a real ProductDbContext.
    /// Branch is left empty so the endpoint's git-cleanup section is
    /// skipped (no real worktree on disk in tests).
    /// </summary>
    private static async Task SeedWorkstreamAsync(
        ForgeWebApplicationFactory f, string storyId, string spawnId, params string[] wsIds)
    {
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        db.ProductStories.Add(new ProductStoryRow
        {
            Id = storyId,
            ProductId = "prod-x",
            Name = "memory wipe regression",
            Description = "memory wipe regression",
            CreatedAt = System.DateTimeOffset.UtcNow,
        });
        db.StorySpawns.Add(new StorySpawnRow
        {
            Id = spawnId,
            StoryId = storyId,
            StoryTitle = "memory wipe regression",
            Phase = StorySpawnPhases.Implement,
            RepoId = "repo-x",
            CreatedAt = System.DateTimeOffset.UtcNow,
            UpdatedAt = System.DateTimeOffset.UtcNow,
            StoryPoints = 3,
        });
        foreach (var wsId in wsIds)
        {
            db.Workstreams.Add(new WorkstreamRow
            {
                Id = wsId,
                Sort = 0,
                RepoId = "repo-x",
                Name = wsId,
                Branch = string.Empty, // empty → restart skips git-branch cleanup
                Base = "main",
                Epic = storyId,
                Status = "stalled",
                WorktreeJson = "[]",
                AgentsJson = "[]",
                PrsJson = "[]",
                CardsJson = "[]",
                Summary = wsId,
                StorySpawnId = spawnId,
                Category = WorkstreamCategories.Impl,
            });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = wsId,
                Provider = "local",
                Track = "dev",
                Type = "task",
                State = "active",
                Title = wsId,
                DescriptionMd = "memory wipe regression",
                Points = 3,
                Priority = "med",
                ExecutionStatus = ExecutionStatuses.Idle,
                CreatedAt = System.DateTimeOffset.UtcNow,
                UpdatedAt = System.DateTimeOffset.UtcNow,
            });
        }
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
    }

    /// <summary>
    /// Mirrors <c>Phase52SloppyReassignmentTests.SeedRosterAsync</c> —
    /// seeds a crew with enough agents that the reconciler's diversity
    /// picker can find a pair outside the prior-pair exclusion set.
    /// </summary>
    private static async Task SeedRosterAsync(
        ControlDbContext control, ProductDbContext db, string crewId, string lead, params string[] memberIds)
    {
        control.Crews.Add(new CrewRow
        {
            Id = crewId, Name = crewId, Summary = string.Empty,
            Lead = lead, Model = "claude-sonnet-4-6",
        });
        foreach (var member in memberIds)
        {
            control.Agents.Add(new AgentRow { Id = member, Name = member, Role = "eng" });
            control.CrewMembers.Add(new CrewMemberRow { CrewId = crewId, AgentId = member, Role = "eng" });
        }
        if (!memberIds.Contains(lead))
        {
            control.Agents.Add(new AgentRow { Id = lead, Name = lead, Role = "tech-lead" });
        }
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
    }
}
