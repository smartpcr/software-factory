// -----------------------------------------------------------------------
// <copyright file="Phase51BudgetCooldownTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 51 — auto-recovery for per-task budget-cap stalls.
///
/// <para>Verifies <see cref="BudgetCooldownReconciler.RunOnceAsync"/> resumes
/// a work item parked at <c>ExecutionStatus="cooldown-budget"</c> under both
/// recovery paths:</para>
/// <list type="bullet">
///   <item><b>cap-raised</b> — operator raised
///   <see cref="CrewRow.MaxTaskBudgetUsd"/> so cumulative cost drops back
///   under cap. Reconciler resumes immediately on the next tick, clears
///   <see cref="WorkItemRow.LastPairAgentIdsJson"/> (continuity — same
///   pair), enqueues with <c>preferredAgentId = prior generator</c>.</item>
///   <item><b>cooldown-elapsed</b> — 10 m window passed without a cap
///   raise. Reconciler resumes and KEEPS <c>LastPairAgentIdsJson</c> set
///   so the claim picker rotates to a fresh pair on the next claim.</item>
/// </list>
/// </summary>
public class Phase51BudgetCooldownTests
{
    private static async Task<(ForgeWebApplicationFactory f, ControlDbContext control, ProductDbContext db, BudgetCooldownReconciler r)>
        SetupAsync(string workItemId, decimal? cap, decimal cumulativeCost, DateTimeOffset cooldownUntil, string[] priorPair)
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* triggers boot + migrations */ }

        var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        // Seed a crew with the supplied cap.
        control.Crews.Add(new CrewRow
        {
            Id = "crew-budget-test", Name = "Budget Test", Summary = string.Empty,
            Lead = "agent-gen", Model = "claude-sonnet-4-6",
            MaxTaskBudgetUsd = cap,
        });

        // Seed the work item already parked in cooldown.
        db.WorkItems.Add(new WorkItemRow
        {
            Id = workItemId, Type = "step", State = "active", Title = "T",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ExecutionStatus = ExecutionStatuses.CooldownBudget,
            BudgetCooldownUntilUtc = cooldownUntil,
            LastPairAgentIdsJson = JsonSerializer.Serialize(priorPair),
        });

        // Seed a deliverable so the reconciler can resolve the crew.
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = workItemId, CrewId = "crew-budget-test",
            GeneratorAgentId = priorPair[0], EvaluatorAgentId = priorPair[1],
            Iter = 25, Score = 80, Verdict = "stalled-budget",
            DimsJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
            GenCostUsd = cumulativeCost / 2, EvalCostUsd = cumulativeCost / 2,
        });

        // Seed an agent_usage row carrying the cumulative cost so the
        // reconciler's recomputation matches the seeded scenario.
        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = priorPair[0], CrewId = "crew-budget-test",
            TaskId = workItemId, Model = "claude-sonnet-4-6",
            CostUsd = cumulativeCost, CostCategory = "generator",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
        });

        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var reconciler = scope.ServiceProvider.GetRequiredService<BudgetCooldownReconciler>();
        return (f, control, db, reconciler);
    }

    [Fact]
    public async Task CapRaised_Resumes_Immediately_With_Same_Pair()
    {
        // Cumulative cost $0.50, cap raised to $5.00 ($0.50 < $5.00) →
        // capRaisedRecovery is true regardless of the cooldown timer.
        var (f, control, db, r) = await SetupAsync(
            workItemId: "wi-cap-raised",
            cap: 5.00m,
            cumulativeCost: 0.50m,
            cooldownUntil: DateTimeOffset.UtcNow.AddMinutes(8), // still in the window
            priorPair: new[] { "agent-gen", "agent-eval" });
        try
        {
            var resumed = await r.RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, resumed);

            // AsNoTracking — the reconciler writes via its own DI scope's
            // DbContext; the test's tracked entity would otherwise return
            // the pre-resume snapshot.
            var wi = await db.WorkItems.AsNoTracking()
                .FirstAsync(w => w.Id == "wi-cap-raised");
            Assert.Equal(ExecutionStatuses.Queued, wi.ExecutionStatus);
            Assert.Null(wi.BudgetCooldownUntilUtc);
            // cap-raised path clears the pair so the claim picker uses
            // its prior-agent preference (continuity, same pair).
            Assert.Null(wi.LastPairAgentIdsJson);

            // A new task queue row was inserted for this work item.
            var queueRow = await db.TaskQueue.AsNoTracking()
                .FirstOrDefaultAsync(q => q.WorkItemId == "wi-cap-raised");
            Assert.NotNull(queueRow);
            Assert.Equal("crew-budget-test", queueRow!.RoutedToCrewId);
            // preferredAgentId is the prior generator so the picker
            // keeps the same pair.
            Assert.Equal("agent-gen", queueRow.PreferredAgentId);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task CooldownElapsed_Resumes_And_Keeps_Pair_For_Rotation()
    {
        // Cumulative cost $5.00, cap still $1.00 ($5.00 NOT < $1.00) →
        // capRaisedRecovery is false. Cooldown timer expired (cooldownUntil
        // in the past) → cooldownElapsed is true. Reconciler resumes
        // and KEEPS LastPairAgentIdsJson set so the claim picker rotates.
        var (f, control, db, r) = await SetupAsync(
            workItemId: "wi-cooldown-elapsed",
            cap: 1.00m,
            cumulativeCost: 5.00m,
            cooldownUntil: DateTimeOffset.UtcNow.AddMinutes(-1), // already passed
            priorPair: new[] { "agent-gen", "agent-eval" });
        try
        {
            var resumed = await r.RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, resumed);

            var wi = await db.WorkItems.AsNoTracking()
                .FirstAsync(w => w.Id == "wi-cooldown-elapsed");
            Assert.Equal(ExecutionStatuses.Queued, wi.ExecutionStatus);
            Assert.Null(wi.BudgetCooldownUntilUtc);
            // cooldown-elapsed path KEEPS the prior pair so the claim
            // picker can exclude it on the next claim (rotation).
            Assert.NotNull(wi.LastPairAgentIdsJson);
            var pair = JsonSerializer.Deserialize<string[]>(wi.LastPairAgentIdsJson!)!;
            Assert.Equal(new[] { "agent-gen", "agent-eval" }, pair);

            var queueRow = await db.TaskQueue.AsNoTracking()
                .FirstOrDefaultAsync(q => q.WorkItemId == "wi-cooldown-elapsed");
            Assert.NotNull(queueRow);
            Assert.Equal("crew-budget-test", queueRow!.RoutedToCrewId);
            // No preferred agent — the picker uses round-robin minus
            // the excluded pair.
            Assert.Null(queueRow.PreferredAgentId);
        }
        finally { f.Dispose(); }
    }

    [Fact]
    public async Task StillParked_When_Cap_Not_Raised_And_Cooldown_Not_Elapsed()
    {
        // Cumulative $5, cap $1 (over), cooldown still in the future. No
        // resume condition met — reconciler leaves the row alone.
        var (f, control, db, r) = await SetupAsync(
            workItemId: "wi-still-parked",
            cap: 1.00m,
            cumulativeCost: 5.00m,
            cooldownUntil: DateTimeOffset.UtcNow.AddMinutes(5),
            priorPair: new[] { "agent-gen", "agent-eval" });
        try
        {
            var resumed = await r.RunOnceAsync(CancellationToken.None);

            Assert.Equal(0, resumed);

            var wi = await db.WorkItems.AsNoTracking()
                .FirstAsync(w => w.Id == "wi-still-parked");
            Assert.Equal(ExecutionStatuses.CooldownBudget, wi.ExecutionStatus);
            Assert.NotNull(wi.BudgetCooldownUntilUtc);
            Assert.NotNull(wi.LastPairAgentIdsJson);

            // No queue row inserted.
            var any = await db.TaskQueue.AsNoTracking()
                .AnyAsync(q => q.WorkItemId == "wi-still-parked");
            Assert.False(any);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Legacy / carry-forward path: a work item that's in
    /// <c>ExecutionStatus="failed"</c> (because the OLD CrewQueueWorker
    /// marked every non-pass verdict as Failed) but whose deliverable chain
    /// contains a <c>stalled-budget</c> verdict somewhere and no <c>pass</c>.
    /// When the operator raises the crew cap, the reconciler's next tick
    /// must recover this row exactly like a cooldown-budget row would.
    /// </summary>
    [Fact]
    public async Task FailedRow_With_BudgetStall_In_Chain_Recovers_When_Cap_Raised()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrations */ }
        var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        control.Crews.Add(new CrewRow
        {
            Id = "crew-legacy", Name = "Legacy", Summary = string.Empty,
            Lead = "agent-gen", Model = "claude-sonnet-4-6",
            MaxTaskBudgetUsd = 5.00m, // raised — was $1 when the row stalled
        });
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "wi-legacy-failed", Type = "step", State = "active", Title = "T",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ExecutionStatus = ExecutionStatuses.Failed,
            // No BudgetCooldownUntilUtc, no LastPairAgentIdsJson — these are
            // the new-fix-era columns; legacy rows wouldn't have them set.
        });
        // Deliverable chain: stalled-budget at iter 10 (mid-chain), then
        // iter 11 hit something else (stalled iter-cap). Last verdict is
        // NOT stalled-budget — the user's scenario.
        var baseTime = DateTimeOffset.UtcNow.AddHours(-1);
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = "wi-legacy-failed", CrewId = "crew-legacy",
            GeneratorAgentId = "agent-gen", EvaluatorAgentId = "agent-eval",
            Iter = 10, Score = 80, Verdict = "stalled-budget",
            DimsJson = "{}", CreatedAt = baseTime,
        });
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = "wi-legacy-failed", CrewId = "crew-legacy",
            GeneratorAgentId = "agent-gen", EvaluatorAgentId = "agent-eval",
            Iter = 11, Score = 82, Verdict = "stalled",
            DimsJson = "{}", CreatedAt = baseTime.AddMinutes(5),
        });
        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-gen", CrewId = "crew-legacy", TaskId = "wi-legacy-failed",
            Model = "claude-sonnet-4-6", CostUsd = 1.20m, CostCategory = "generator",
            StartedAt = baseTime,
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var r = scope.ServiceProvider.GetRequiredService<BudgetCooldownReconciler>();
        try
        {
            var resumed = await r.RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, resumed);

            var wi = await db.WorkItems.AsNoTracking()
                .FirstAsync(w => w.Id == "wi-legacy-failed");
            Assert.Equal(ExecutionStatuses.Queued, wi.ExecutionStatus);

            // The cap-raised branch picks the prior generator as preferred
            // so the picker keeps the same pair (continuity).
            var queueRow = await db.TaskQueue.AsNoTracking()
                .FirstOrDefaultAsync(q => q.WorkItemId == "wi-legacy-failed");
            Assert.NotNull(queueRow);
            Assert.Equal("crew-legacy", queueRow!.RoutedToCrewId);
            Assert.Equal("agent-gen", queueRow.PreferredAgentId);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Same legacy scenario but the operator hasn't raised the cap yet
    /// (cumulative cost still ≥ cap). The reconciler must leave the row
    /// alone — no auto-rotation timer applies to Failed rows; recovery
    /// requires an explicit cap raise.
    /// </summary>
    [Fact]
    public async Task FailedRow_Stays_Parked_When_Cap_Still_Too_Low()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrations */ }
        var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        control.Crews.Add(new CrewRow
        {
            Id = "crew-legacy-low", Name = "Legacy Low", Summary = string.Empty,
            Lead = "agent-gen", Model = "claude-sonnet-4-6",
            MaxTaskBudgetUsd = 1.00m, // still low — cost $1.20 is over
        });
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "wi-legacy-low", Type = "step", State = "active", Title = "T",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ExecutionStatus = ExecutionStatuses.Failed,
        });
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = "wi-legacy-low", CrewId = "crew-legacy-low",
            GeneratorAgentId = "agent-gen", EvaluatorAgentId = "agent-eval",
            Iter = 10, Score = 80, Verdict = "stalled-budget",
            DimsJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-gen", CrewId = "crew-legacy-low", TaskId = "wi-legacy-low",
            Model = "claude-sonnet-4-6", CostUsd = 1.20m, CostCategory = "generator",
            StartedAt = DateTimeOffset.UtcNow,
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var r = scope.ServiceProvider.GetRequiredService<BudgetCooldownReconciler>();
        try
        {
            var resumed = await r.RunOnceAsync(CancellationToken.None);

            Assert.Equal(0, resumed);

            var wi = await db.WorkItems.AsNoTracking()
                .FirstAsync(w => w.Id == "wi-legacy-low");
            Assert.Equal(ExecutionStatuses.Failed, wi.ExecutionStatus);

            var any = await db.TaskQueue.AsNoTracking()
                .AnyAsync(q => q.WorkItemId == "wi-legacy-low");
            Assert.False(any);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// On the cooldown-elapsed recovery path the reconciler must:
    ///  1) set <see cref="WorkItemRow.BudgetWindowStartUtc"/> = UtcNow so
    ///     subsequent cap checks count only post-window cost,
    ///  2) leave the pre-window agent_usage rows untouched (ledger stays
    ///     intact for crew salary roll-ups).
    /// </summary>
    [Fact]
    public async Task CooldownElapsed_Resets_Budget_Window()
    {
        var (f, control, db, r) = await SetupAsync(
            workItemId: "wi-window-reset",
            cap: 1.00m,
            cumulativeCost: 5.00m, // way over the $1 cap
            cooldownUntil: DateTimeOffset.UtcNow.AddSeconds(-1),
            priorPair: new[] { "agent-gen", "agent-eval" });
        try
        {
            var before = await r.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, before);

            var wi = await db.WorkItems.AsNoTracking()
                .FirstAsync(w => w.Id == "wi-window-reset");
            Assert.Equal(ExecutionStatuses.Queued, wi.ExecutionStatus);
            Assert.NotNull(wi.BudgetWindowStartUtc); // ← window now set
            // The ledger row is preserved (lifetime spend stays for
            // salary roll-ups); only the meter is windowed at read time.
            var ledgerCount = await control.AgentUsage.AsNoTracking()
                .Where(u => u.TaskId == "wi-window-reset")
                .CountAsync();
            Assert.Equal(1, ledgerCount);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// After a cooldown-elapsed reset, a NEW cap-vs-cost evaluation must
    /// use the windowed view. Seed a row with $5 lifetime spend, $1 cap,
    /// a window-start AFTER the lifetime spend, then re-park it as
    /// cooldown-budget with a fresh deliverable: the reconciler should
    /// resume because windowed cost is $0 even though lifetime is $5.
    /// Without windowing the cap-raised branch would mis-fire here.
    /// </summary>
    [Fact]
    public async Task WindowedCost_Wins_When_Lifetime_Exceeds_Cap()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot */ }
        var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var windowStart = DateTimeOffset.UtcNow.AddMinutes(-1);

        control.Crews.Add(new CrewRow
        {
            Id = "crew-win", Name = "Window", Summary = string.Empty,
            Lead = "agent-gen", Model = "claude-sonnet-4-6",
            MaxTaskBudgetUsd = 1.00m, // tight cap
        });
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "wi-win", Type = "step", State = "active", Title = "T",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ExecutionStatus = ExecutionStatuses.CooldownBudget,
            BudgetCooldownUntilUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            LastPairAgentIdsJson = JsonSerializer.Serialize(new[] { "agent-gen", "agent-eval" }),
            BudgetWindowStartUtc = windowStart, // ← reset boundary
        });
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = "wi-win", CrewId = "crew-win",
            GeneratorAgentId = "agent-gen", EvaluatorAgentId = "agent-eval",
            Iter = 25, Score = 80, Verdict = "stalled-budget",
            DimsJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        // Pre-window spend $5 (BEFORE windowStart) — should be ignored.
        control.AgentUsage.Add(new AgentUsageRow
        {
            AgentId = "agent-gen", CrewId = "crew-win", TaskId = "wi-win",
            Model = "claude-sonnet-4-6", CostUsd = 5.00m, CostCategory = "generator",
            StartedAt = windowStart.AddMinutes(-10),
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var r = scope.ServiceProvider.GetRequiredService<BudgetCooldownReconciler>();
        try
        {
            var resumed = await r.RunOnceAsync(CancellationToken.None);
            // Windowed cost $0 < cap $1 → capRaisedRecovery fires.
            Assert.Equal(1, resumed);

            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-win");
            Assert.Equal(ExecutionStatuses.Queued, wi.ExecutionStatus);
            // cap-raised path — pair cleared for continuity.
            Assert.Null(wi.LastPairAgentIdsJson);
        }
        finally { f.Dispose(); }
    }

    /// <summary>
    /// Failed rows whose chain has NO stalled-budget verdict must not be
    /// touched (most Failed rows are reject / build-fail / iter-cap — not
    /// budget). The reconciler's chain-scan is the gate.
    /// </summary>
    [Fact]
    public async Task FailedRow_Without_BudgetStall_In_Chain_Is_Left_Alone()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* boot + migrations */ }
        var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        control.Crews.Add(new CrewRow
        {
            Id = "crew-nbg", Name = "Not Budget", Summary = string.Empty,
            Lead = "agent-gen", Model = "claude-sonnet-4-6",
            MaxTaskBudgetUsd = 5.00m,
        });
        db.WorkItems.Add(new WorkItemRow
        {
            Id = "wi-no-budget-stall", Type = "step", State = "active", Title = "T",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ExecutionStatus = ExecutionStatuses.Failed,
        });
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = "wi-no-budget-stall", CrewId = "crew-nbg",
            GeneratorAgentId = "agent-gen", EvaluatorAgentId = "agent-eval",
            Iter = 1, Score = 50, Verdict = "reject",
            DimsJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        var r = scope.ServiceProvider.GetRequiredService<BudgetCooldownReconciler>();
        try
        {
            var resumed = await r.RunOnceAsync(CancellationToken.None);

            Assert.Equal(0, resumed);

            var wi = await db.WorkItems.AsNoTracking()
                .FirstAsync(w => w.Id == "wi-no-budget-stall");
            Assert.Equal(ExecutionStatuses.Failed, wi.ExecutionStatus);
        }
        finally { f.Dispose(); }
    }
}
