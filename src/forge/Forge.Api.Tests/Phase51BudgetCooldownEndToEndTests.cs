// -----------------------------------------------------------------------
// <copyright file="Phase51BudgetCooldownEndToEndTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 51 — end-to-end coverage for per-task budget-cap auto-recovery.
///
/// <para>Unlike <see cref="Phase51BudgetCooldownTests"/> which seeds the DB
/// directly and drives the reconciler in isolation, these tests exercise
/// the full flow:</para>
/// <list type="number">
///   <item>Drive <see cref="DeliverableLoop"/> until budget is exceeded —
///   the loop must return <c>verdict="stalled-budget"</c>.</item>
///   <item>Walk through the same code paths the production runner would —
///   parking the work item at <c>ExecutionStatus="cooldown-budget"</c>
///   with <see cref="WorkItemRow.LastPairAgentIdsJson"/> populated and
///   <see cref="WorkItemRow.BudgetCooldownUntilUtc"/> set.</item>
///   <item>Test 1 — PATCH <c>/api/crews/{id}</c> to raise
///   <c>MaxTaskBudgetUsd</c>; one reconciler tick must resume the row
///   with the same pair (cap-raised continuity).</item>
///   <item>Test 2 — fast-forward <c>BudgetCooldownUntilUtc</c> to the past;
///   one reconciler tick must resume the row AND the
///   <see cref="CrewQueueWorker"/> claim sweep must pick a generator
///   that is NOT the prior pair (rotation).</item>
/// </list>
/// </summary>
public class Phase51BudgetCooldownEndToEndTests
{
    private const string Fence = "```";
    private static string EvalJson(int score) => Fence + "json\n{ \"score\": " + score + " }\n" + Fence;

    /// <summary>
    /// Seeds a 3-member crew so the rotation test has somewhere to rotate
    /// to. Members: agent-a (lead-eligible), agent-b, agent-c. CrewQueueWorker
    /// rosters non-lead members for round-robin, so the picker has agent-b
    /// + agent-c to pick from when agent-a is excluded.
    /// </summary>
    private static async Task SeedCrewAndAgentsAsync(ForgeDbContext db, string crewId, decimal? cap)
    {
        db.Crews.Add(new CrewRow
        {
            Id = crewId, Name = "Phase51 E2E", Summary = string.Empty,
            Lead = "agent-a", Model = "claude-sonnet-4-6", Backend = "copilot",
            MaxTaskBudgetUsd = cap, MaxConcurrent = 5,
            // Bypass the Phase 14 schedule gate so the test is independent
            // of the wall-clock — otherwise members evaluate OffShift outside
            // 09-21 local and the picker falls back to the lead via the
            // empty-roster Q18.3 exemption (defeating rotation).
            ScheduleEnabled = false,
        });
        foreach (var agentId in new[] { "agent-a", "agent-b", "agent-c", "agent-d" })
        {
            db.Agents.Add(new AgentRow
            {
                Id = agentId, Name = agentId, Role = "eng",
                CrewId = crewId, State = "idle",
            });
            db.CrewMembers.Add(new CrewMemberRow
            {
                CrewId = crewId, AgentId = agentId, Role = "coder",
                AddedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Drives <see cref="DeliverableLoop"/> until the budget cap trips,
    /// then walks the row through the same CrewQueueWorker exit path the
    /// production runner takes (manual mutation here since the tests
    /// don't spawn the runner) — sets ExecutionStatus = CooldownBudget,
    /// populates BudgetCooldownUntilUtc + LastPairAgentIdsJson.
    /// </summary>
    private static async Task<DeliverableLoopResult> RunLoopToBudgetStallAsync(
        ForgeDbContext db, DeliverableLoop loop,
        string workItemId, string crewId,
        string generator, string evaluator, decimal cap)
    {
        // Cost $0.40/iter from generator + $0.20 from evaluator = $0.60/iter.
        // Cap at $1.00 → loop stalls at iter 2 ($1.20 cumulative > $1.00).
        var req = new DeliverableLoopRequest(
            workItemId, crewId, generator, evaluator,
            Generator: (_, _, _) => Task.FromResult(new GeneratorRunOutput("out", 0.40m)),
            Evaluator: (_, _, _) => Task.FromResult(new EvaluatorRunOutput(EvalJson(82), 0.20m)),
            MaxTaskBudgetUsd: cap);
        var result = await loop.RunAsync(req, CancellationToken.None);
        Assert.Equal("stalled-budget", result.FinalVerdict);

        // Mirror CrewQueueWorker.RunAgentAsync's new cooldown-park branch
        // (the path under test runs inside the runner, but the runner needs
        // the agent runtime + lots of plumbing — bypass it by writing the
        // same cooldown markers the runner would).
        var wi = await db.WorkItems.FirstAsync(w => w.Id == workItemId);
        wi.ExecutionStatus = ExecutionStatuses.CooldownBudget;
        wi.BudgetCooldownUntilUtc = DateTimeOffset.UtcNow.AddMinutes(10);
        wi.LastPairAgentIdsJson = JsonSerializer.Serialize(new[] { generator, evaluator });
        wi.UpdatedAt = DateTimeOffset.UtcNow;

        // Cost ledger row so the reconciler's cumulative cost recompute
        // matches what the loop accumulated.
        db.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = generator, CrewId = crewId, TaskId = workItemId,
            Model = "claude-sonnet-4-6", CostUsd = result.TotalCostUsd,
            CostCategory = "generator", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();
        return result;
    }

    [Fact]
    public async Task E2E_BudgetExceeded_CapRaised_Resumes_WithSamePair()
    {
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        // Role-gated PATCH endpoints (Phase 10.9) require a known user.
        // po@forge.local is the seeded Product Owner — mirrors the auth
        // header pattern from AgentSoulAndBudgetTests.
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var loop = new DeliverableLoop(db);
        var reconciler = scope.ServiceProvider.GetRequiredService<BudgetCooldownReconciler>();

        await SeedCrewAndAgentsAsync(db, crewId: "crew-cap-raise", cap: 1.00m);
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "wi-cap-raise", Type = "step", State = "active", Title = "T",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        try
        {
            // Step 1 — run until the loop stalls on budget.
            var stalled = await RunLoopToBudgetStallAsync(
                db, loop, "wi-cap-raise", "crew-cap-raise",
                generator: "agent-a", evaluator: "agent-b", cap: 1.00m);
            Assert.True(stalled.TotalCostUsd >= 1.00m);

            var parked = await db.WorkItems.AsNoTracking()
                .FirstAsync(w => w.Id == "wi-cap-raise");
            Assert.Equal(ExecutionStatuses.CooldownBudget, parked.ExecutionStatus);
            Assert.NotNull(parked.BudgetCooldownUntilUtc);
            Assert.NotNull(parked.LastPairAgentIdsJson);

            // Step 2 — verify the reconciler does NOT resume yet (cap still
            // $1.00, cumulative cost $1.20 > cap, cooldown timer in the future).
            var resumedBefore = await reconciler.RunOnceAsync(CancellationToken.None);
            Assert.Equal(0, resumedBefore);

            // Step 3 — operator raises the cap via PATCH /api/crews/{id}.
            // Use the real HTTP path so we're testing the actual operator flow.
            var patch = await client.PatchAsJsonAsync(
                "/api/crews/crew-cap-raise",
                new { maxTaskBudgetUsd = 10.00m });
            Assert.True(patch.IsSuccessStatusCode, $"PATCH failed: {patch.StatusCode} {await patch.Content.ReadAsStringAsync()}");

            // Step 4 — one reconciler tick must resume the row.
            var resumedAfter = await reconciler.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, resumedAfter);

            var resumedWi = await db.WorkItems.AsNoTracking()
                .FirstAsync(w => w.Id == "wi-cap-raise");
            Assert.Equal(ExecutionStatuses.Queued, resumedWi.ExecutionStatus);
            Assert.Null(resumedWi.BudgetCooldownUntilUtc);
            // Cap-raised path clears the prior pair → picker uses
            // continuity (prefers prior generator agent-a).
            Assert.Null(resumedWi.LastPairAgentIdsJson);

            // Step 5 — verify a queue row was created preferring agent-a.
            var queueRow = await db.TaskQueue.AsNoTracking()
                .FirstOrDefaultAsync(q => q.WorkItemId == "wi-cap-raise");
            Assert.NotNull(queueRow);
            Assert.Equal("crew-cap-raise", queueRow!.RoutedToCrewId);
            Assert.Equal("agent-a", queueRow.PreferredAgentId);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task E2E_BudgetExceeded_CooldownElapsed_Resumes_WithDifferentPair()
    {
        var f = new ForgeWebApplicationFactory();
        using var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forge-User", "po@forge.local");

        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var loop = new DeliverableLoop(db);
        var reconciler = scope.ServiceProvider.GetRequiredService<BudgetCooldownReconciler>();
        var worker = scope.ServiceProvider.GetRequiredService<CrewQueueWorker>();

        await SeedCrewAndAgentsAsync(db, crewId: "crew-rotate", cap: 1.00m);
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "wi-rotate", Type = "step", State = "active", Title = "T",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        try
        {
            // Step 1 — burn budget, park at CooldownBudget with pair (a, b).
            var stalled = await RunLoopToBudgetStallAsync(
                db, loop, "wi-rotate", "crew-rotate",
                generator: "agent-a", evaluator: "agent-b", cap: 1.00m);
            Assert.True(stalled.TotalCostUsd >= 1.00m);

            // Step 2 — operator does NOT raise the cap. Fast-forward the
            // cooldown timer to "the past" so the reconciler's
            // cooldown-elapsed branch fires on the next tick.
            var parked = await db.WorkItems.FirstAsync(w => w.Id == "wi-rotate");
            parked.BudgetCooldownUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();

            // Step 3 — reconciler tick resumes the row WITH LastPairAgentIdsJson
            // still populated (so the claim picker rotates).
            var resumed = await reconciler.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, resumed);

            var resumedWi = await db.WorkItems.AsNoTracking()
                .FirstAsync(w => w.Id == "wi-rotate");
            Assert.Equal(ExecutionStatuses.Queued, resumedWi.ExecutionStatus);
            Assert.NotNull(resumedWi.LastPairAgentIdsJson);
            var preserved = JsonSerializer.Deserialize<string[]>(resumedWi.LastPairAgentIdsJson!)!;
            Assert.Equal(new[] { "agent-a", "agent-b" }, preserved);

            // Step 4 — claim sweep MUST NOT pick agent-a (prior generator).
            // Run the claim sweep through CrewQueueWorker's public driver.
            var queueRowBefore = await db.TaskQueue.AsNoTracking()
                .FirstAsync(q => q.WorkItemId == "wi-rotate");
            Assert.Null(queueRowBefore.PickedUpByAgent);

            await worker.RunOnceAsync(CancellationToken.None);

            var queueRowAfter = await db.TaskQueue.AsNoTracking()
                .FirstAsync(q => q.WorkItemId == "wi-rotate");
            Assert.NotNull(queueRowAfter.PickedUpByAgent);
            // The rotation guarantee: the picker MUST NOT pick agent-a
            // (the prior generator). agent-b is also excluded as prior
            // evaluator. The remaining roster candidates are agent-c and
            // agent-d — one of them must win.
            Assert.NotEqual("agent-a", queueRowAfter.PickedUpByAgent);
            Assert.NotEqual("agent-b", queueRowAfter.PickedUpByAgent);
            Assert.Contains(queueRowAfter.PickedUpByAgent, new[] { "agent-c", "agent-d" });

            // After a successful rotation pick the picker clears
            // LastPairAgentIdsJson so subsequent ticks (boot recovery
            // etc.) don't keep rotating away from the original pair.
            var rotatedWi = await db.WorkItems.AsNoTracking()
                .FirstAsync(w => w.Id == "wi-rotate");
            Assert.Null(rotatedWi.LastPairAgentIdsJson);
        }
        finally { f.Dispose(); }
    }
}
