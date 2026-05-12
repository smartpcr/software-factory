// -----------------------------------------------------------------------
// <copyright file="Phase52SloppyReassignmentTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Phase 52 — integration tests for <see cref="SloppyReassignmentReconciler"/>.
/// Covers: reassignment under cap (clean slate + new pair), terminal failure
/// at cap, prior-pair exclusion via PriorPairsJson, and the
/// no-eligible-pair park path.
/// </summary>
public class Phase52SloppyReassignmentTests
{
    private static async Task<ForgeWebApplicationFactory> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        // Disable worktree reset for tests — we don't want to touch a real
        // filesystem. The reconciler's TryResetWorktreeAsync is gated on
        // SloppyReassignmentOptions.ResetWorktreeOnReassign.
        var disposable = f.CreateClient();
        disposable.Dispose();
        using var scope = f.Services.CreateScope();
        var opts = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<SloppyReassignmentOptions>>();
        opts.CurrentValue.ResetWorktreeOnReassign = false;
        opts.CurrentValue.Enabled = false; // tests drive RunOnceAsync manually
        return f;
    }

    private static async Task SeedRosterAsync(
        ForgeDbContext db, string crewId, string lead, params string[] memberIds)
    {
        db.Crews.Add(new CrewRow
        {
            Id = crewId, Name = crewId, Summary = string.Empty,
            Lead = lead, Model = "claude-sonnet-4-6",
        });
        foreach (var member in memberIds)
        {
            db.Agents.Add(new AgentRow
            {
                Id = member, Name = member, Role = "eng",
            });
            db.CrewMembers.Add(new CrewMemberRow
            {
                CrewId = crewId, AgentId = member, Role = "eng",
            });
        }
        // Lead also needs an agent row.
        if (!memberIds.Contains(lead))
        {
            db.Agents.Add(new AgentRow
            {
                Id = lead, Name = lead, Role = "tech-lead",
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Reassigns_Under_Cap_With_Fresh_Pair_And_Clean_Slate()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        await SeedRosterAsync(db, "crew-sl", lead: "agent-lead", "agent-a", "agent-b", "agent-c", "agent-d");

        // Work item parked in pending-reassign on its FIRST sloppy trigger:
        // PairAttemptCount = 1, PriorPairsJson contains the agent-a/agent-b pair.
        var wi = new WorkItemRow
        {
            Id = "wi-reassign-under-cap", Type = "step", State = "active", Title = "T",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ExecutionStatus = ExecutionStatuses.PendingReassign,
            PairAttemptCount = 1,
            PriorPairsJson = JsonSerializer.Serialize(new[] { new[] { "agent-a", "agent-b" } }),
        };
        db.WorkItems.Add(wi);
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = wi.Id, CrewId = "crew-sl",
            GeneratorAgentId = "agent-a", EvaluatorAgentId = "agent-b",
            Iter = 4, Score = 78, Verdict = "sloppy",
            DimsJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        // Pair-assignment row from the loop's stamping.
        db.AgentPairAssignments.Add(new AgentPairAssignmentRow
        {
            WorkItemId = wi.Id, SequenceNo = 1,
            GeneratorAgentId = "agent-a", EvaluatorAgentId = "agent-b",
            CrewId = "crew-sl",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            EndedAt = DateTimeOffset.UtcNow,
            TotalIters = 4, FinalScore = 78,
            Outcome = AgentPairAssignmentOutcomes.ReassignedSloppy,
            SloppyReason = SloppyReasons.ScorePlateau,
        });
        // Seed a transcript event + deliverable to verify they get wiped.
        db.TaskTranscriptEvents.Add(new TaskTranscriptEventRow
        {
            WorkItemId = wi.Id, Kind = "task.iteration.scored",
            PayloadJson = "{}", At = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var reconciler = scope.ServiceProvider.GetRequiredService<SloppyReassignmentReconciler>();
        var progressed = await reconciler.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, progressed);

        // Reconciler used its own scope/DbContext. Open a FRESH scope for
        // the readback so we hit the DB cleanly (avoids any per-DbContext
        // query cache or stale connection snapshot).
        using var readScope = f.Services.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var live = await readDb.WorkItems.AsNoTracking().FirstAsync(w => w.Id == wi.Id);
        Assert.Equal(ExecutionStatuses.Queued, live.ExecutionStatus);
        Assert.Equal(2, live.PairAttemptCount); // incremented 1 → 2
        Assert.NotNull(live.BudgetWindowStartUtc); // budget meter reset

        // Q52.4 clean-slate: prior pair's transcript events + deliverables
        // wiped. The reconciler's own `task.sloppy-reassigned` emit lands
        // as a fresh transcript row AFTER the wipe — that's the new pair's
        // intro context, not the wiped prior pair's history. Assert that
        // no prior-pair iteration events remain.
        var priorIterationEvent = await readDb.TaskTranscriptEvents.AsNoTracking()
            .AnyAsync(t => t.WorkItemId == wi.Id && t.Kind == "task.iteration.scored");
        Assert.False(priorIterationEvent);
        var deliv = await readDb.Deliverables.AsNoTracking()
            .AnyAsync(d => d.WorkItemId == wi.Id);
        Assert.False(deliv);

        // Queue row inserted with a generator NOT in {agent-a, agent-b}.
        var queueRow = await readDb.TaskQueue.AsNoTracking()
            .FirstOrDefaultAsync(q => q.WorkItemId == wi.Id);
        Assert.NotNull(queueRow);
        Assert.NotNull(queueRow!.PreferredAgentId);
        Assert.NotEqual("agent-a", queueRow.PreferredAgentId);
        Assert.NotEqual("agent-b", queueRow.PreferredAgentId);
        // Lead is also excluded (per Phase 47 — lead is not a generator at v0.1).
        Assert.NotEqual("agent-lead", queueRow.PreferredAgentId);
    }

    [Fact]
    public async Task Terminal_Fails_At_Max_Pair_Attempts()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        await SeedRosterAsync(db, "crew-cap", lead: "agent-lead", "agent-a", "agent-b", "agent-c", "agent-d", "agent-e", "agent-f");

        // Already at cap (PairAttemptCount == MaxPairAttempts).
        var wi = new WorkItemRow
        {
            Id = "wi-terminal", Type = "step", State = "active", Title = "T",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ExecutionStatus = ExecutionStatuses.PendingReassign,
            PairAttemptCount = ScoringConstants.MaxPairAttempts, // = 3
            PriorPairsJson = JsonSerializer.Serialize(new[]
            {
                new[] { "agent-a", "agent-b" },
                new[] { "agent-c", "agent-d" },
                new[] { "agent-e", "agent-f" },
            }),
        };
        db.WorkItems.Add(wi);
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = wi.Id, CrewId = "crew-cap",
            GeneratorAgentId = "agent-e", EvaluatorAgentId = "agent-f",
            Iter = 5, Score = 70, Verdict = "sloppy",
            DimsJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        db.AgentPairAssignments.Add(new AgentPairAssignmentRow
        {
            WorkItemId = wi.Id, SequenceNo = 3,
            GeneratorAgentId = "agent-e", EvaluatorAgentId = "agent-f",
            CrewId = "crew-cap",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            EndedAt = DateTimeOffset.UtcNow,
            TotalIters = 5, FinalScore = 70,
            Outcome = AgentPairAssignmentOutcomes.ReassignedSloppy,
            SloppyReason = SloppyReasons.ScorePlateau,
        });
        await db.SaveChangesAsync();

        var reconciler = scope.ServiceProvider.GetRequiredService<SloppyReassignmentReconciler>();
        var progressed = await reconciler.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, progressed);

        var live = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == wi.Id);
        Assert.Equal(ExecutionStatuses.Failed, live.ExecutionStatus);
        Assert.Equal(ScoringConstants.SloppyTerminalFailureReason, live.FailureReason);
        // PairAttemptCount unchanged — no fourth attempt.
        Assert.Equal(ScoringConstants.MaxPairAttempts, live.PairAttemptCount);

        // Latest pair-assignment stamped as terminal-failed.
        var latestPair = await db.AgentPairAssignments.AsNoTracking()
            .OrderByDescending(p => p.SequenceNo)
            .FirstAsync(p => p.WorkItemId == wi.Id);
        Assert.Equal(AgentPairAssignmentOutcomes.TerminalFailed, latestPair.Outcome);

        // No new queue row.
        var any = await db.TaskQueue.AsNoTracking().AnyAsync(q => q.WorkItemId == wi.Id);
        Assert.False(any);
    }

    [Fact]
    public async Task Prior_Pairs_Excluded_From_New_Pick()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Roster of exactly 5: lead + 4 members. After excluding 2 prior
        // pairs (4 agents), only `agent-e` is eligible for generator.
        await SeedRosterAsync(db, "crew-narrow", lead: "agent-lead",
            "agent-a", "agent-b", "agent-c", "agent-d", "agent-e");

        var wi = new WorkItemRow
        {
            Id = "wi-narrow", Type = "step", State = "active", Title = "T",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ExecutionStatus = ExecutionStatuses.PendingReassign,
            PairAttemptCount = 2,
            PriorPairsJson = JsonSerializer.Serialize(new[]
            {
                new[] { "agent-a", "agent-b" },
                new[] { "agent-c", "agent-d" },
            }),
        };
        db.WorkItems.Add(wi);
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = wi.Id, CrewId = "crew-narrow",
            GeneratorAgentId = "agent-c", EvaluatorAgentId = "agent-d",
            Iter = 4, Score = 80, Verdict = "sloppy",
            DimsJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var reconciler = scope.ServiceProvider.GetRequiredService<SloppyReassignmentReconciler>();
        var progressed = await reconciler.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, progressed);

        var queueRow = await db.TaskQueue.AsNoTracking()
            .FirstAsync(q => q.WorkItemId == wi.Id);
        // Only agent-e is left — must be the picked generator.
        Assert.Equal("agent-e", queueRow.PreferredAgentId);
    }

    [Fact]
    public async Task No_Eligible_Pair_Leaves_Row_Parked_And_Rolls_Back_Attempt_Counter()
    {
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // Roster of 2 (lead + 1 member). Prior pair excluded the only
        // non-lead member → no eligible pair.
        await SeedRosterAsync(db, "crew-stuck", lead: "agent-lead", "agent-a");

        var wi = new WorkItemRow
        {
            Id = "wi-stuck", Type = "step", State = "active", Title = "T",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ExecutionStatus = ExecutionStatuses.PendingReassign,
            PairAttemptCount = 1,
            PriorPairsJson = JsonSerializer.Serialize(new[] { new[] { "agent-a", "agent-a" } }),
        };
        db.WorkItems.Add(wi);
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = wi.Id, CrewId = "crew-stuck",
            GeneratorAgentId = "agent-a", EvaluatorAgentId = "agent-a",
            Iter = 4, Score = 75, Verdict = "sloppy",
            DimsJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var reconciler = scope.ServiceProvider.GetRequiredService<SloppyReassignmentReconciler>();
        var progressed = await reconciler.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, progressed);

        var live = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == wi.Id);
        // Still parked; attempt counter rolled back so we don't burn a
        // chance on a tick we couldn't fulfill.
        Assert.Equal(ExecutionStatuses.PendingReassign, live.ExecutionStatus);
        Assert.Equal(1, live.PairAttemptCount);
        // No queue row.
        var any = await db.TaskQueue.AsNoTracking().AnyAsync(q => q.WorkItemId == wi.Id);
        Assert.False(any);
    }

    [Fact]
    public async Task Three_Reassignments_Then_Terminal_Failure_End_To_End()
    {
        // Walks the full lifecycle: row hits sloppy at attempt 1 → reconciler
        // reassigns to attempt 2 → another sloppy → attempt 3 → another
        // sloppy → terminal-failed. Drives RunOnceAsync three times,
        // simulating the loop's sloppy emission between each.
        await using var f = await BootAsync();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        await SeedRosterAsync(db, "crew-3x", lead: "agent-lead",
            "agent-a", "agent-b", "agent-c", "agent-d", "agent-e", "agent-f", "agent-g", "agent-h");

        // Initial state: attempt 1 just sloppied, parked.
        var wi = new WorkItemRow
        {
            Id = "wi-3x", Type = "step", State = "active", Title = "T",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ExecutionStatus = ExecutionStatuses.PendingReassign,
            PairAttemptCount = 1,
            PriorPairsJson = JsonSerializer.Serialize(new[] { new[] { "agent-a", "agent-b" } }),
        };
        db.WorkItems.Add(wi);
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = wi.Id, CrewId = "crew-3x",
            GeneratorAgentId = "agent-a", EvaluatorAgentId = "agent-b",
            Iter = 4, Score = 78, Verdict = "sloppy",
            DimsJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var reconciler = scope.ServiceProvider.GetRequiredService<SloppyReassignmentReconciler>();

        // Reassignment 1 → attempt 2.
        await reconciler.RunOnceAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        var wi1 = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == wi.Id);
        Assert.Equal(2, wi1.PairAttemptCount);
        Assert.Equal(ExecutionStatuses.Queued, wi1.ExecutionStatus);

        // Simulate next pair going sloppy: append another pair, park.
        // Re-load (tracked) AFTER ChangeTracker.Clear so EF compares
        // against fresh original values.
        var wiUpdate1 = await db.WorkItems.FirstAsync(w => w.Id == wi.Id);
        wiUpdate1.ExecutionStatus = ExecutionStatuses.PendingReassign;
        var priorPairs1 = JsonSerializer.Deserialize<List<string[]>>(wiUpdate1.PriorPairsJson!)!;
        priorPairs1.Add(new[] { "agent-c", "agent-d" });
        wiUpdate1.PriorPairsJson = JsonSerializer.Serialize(priorPairs1);
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = wi.Id, CrewId = "crew-3x",
            GeneratorAgentId = "agent-c", EvaluatorAgentId = "agent-d",
            Iter = 4, Score = 80, Verdict = "sloppy",
            DimsJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        // Reassignment 2 → attempt 3.
        await reconciler.RunOnceAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        var wi2 = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == wi.Id);
        Assert.Equal(3, wi2.PairAttemptCount);
        Assert.Equal(ExecutionStatuses.Queued, wi2.ExecutionStatus);

        // Simulate the third pair going sloppy too.
        var wiUpdate2 = await db.WorkItems.FirstAsync(w => w.Id == wi.Id);
        wiUpdate2.ExecutionStatus = ExecutionStatuses.PendingReassign;
        var priorPairs2 = JsonSerializer.Deserialize<List<string[]>>(wiUpdate2.PriorPairsJson!)!;
        priorPairs2.Add(new[] { "agent-e", "agent-f" });
        wiUpdate2.PriorPairsJson = JsonSerializer.Serialize(priorPairs2);
        db.Deliverables.Add(new DeliverableRow
        {
            WorkItemId = wi.Id, CrewId = "crew-3x",
            GeneratorAgentId = "agent-e", EvaluatorAgentId = "agent-f",
            Iter = 4, Score = 79, Verdict = "sloppy",
            DimsJson = "{}", CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        // Third reassignment attempt — but PairAttemptCount is already 3,
        // so the reconciler terminal-fails instead.
        await reconciler.RunOnceAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        var wi3 = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == wi.Id);
        Assert.Equal(ExecutionStatuses.Failed, wi3.ExecutionStatus);
        Assert.Equal(ScoringConstants.SloppyTerminalFailureReason, wi3.FailureReason);
        Assert.Equal(3, wi3.PairAttemptCount); // unchanged
    }

    [Fact]
    public async Task Scoring_Constants_Are_Locked()
    {
        // Defense-in-depth: any drift on these literals will surface
        // immediately. Phase14SpecConsistencyTests should also catch this
        // in a deeper test pass.
        Assert.Equal(500_000L, ScoringConstants.SloppyGeneratorTokenCap);
        Assert.Equal(3, ScoringConstants.MaxPairAttempts);
        Assert.Equal(0.15, ScoringConstants.SloppyRiskRate);
        Assert.Equal(
            "Failed after 3 reassignments, didn't advance, consider refactor and break down the scope",
            ScoringConstants.SloppyTerminalFailureReason);
    }
}
