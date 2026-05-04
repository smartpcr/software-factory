// -----------------------------------------------------------------------
// <copyright file="Phase18WorkerIntegrationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Phase 18.6 (Q18.3) — wiring tests for <see cref="LeadAutoDispatcher"/>
/// and <see cref="CrewQueueWorker"/>. The pure tests in
/// <c>Phase18LeadExemptionTests</c> verify the underlying
/// <see cref="ScheduleGate.EvaluateAgent"/> + <see cref="PayrollCalculator"/>
/// math; this suite verifies the *callers* actually use it: a crew with a
/// 24×7 lead must dispatch / claim even when its members would otherwise
/// be off-shift.
/// </summary>
public class Phase18WorkerIntegrationTests
{
    private static async Task<(ForgeWebApplicationFactory f, IServiceScope scope, ForgeDbContext db)> BootAsync()
    {
        var f = new ForgeWebApplicationFactory();
        using (f.CreateClient()) { /* trigger boot + migration */ }
        var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        return (f, scope, db);
    }

    /// <summary>
    /// Pick an IANA timezone whose local hour right now is in the dead zone
    /// [22..05] AND whose local day-of-week is Mon..Sat. Members of a day
    /// crew with that TZ are guaranteed off-shift. Returns null if no such
    /// TZ exists at the wall-clock instant — the test must skip in that
    /// case (rare: roughly the middle eight UTC hours of a Sunday).
    /// </summary>
    private static string? ChooseDayCrewOffShiftTimezone()
    {
        var utcNow = DateTimeOffset.UtcNow;
        for (var off = -12; off <= 14; off++)
        {
            var local = utcNow.ToOffset(TimeSpan.FromHours(off));
            var hour = local.Hour;
            if (hour < 22 && hour > 5) continue;
            var dow = local.DayOfWeek;
            if (dow == DayOfWeek.Sunday) continue;
            // Etc/GMT signs are inverted: Etc/GMT-5 = UTC+5, Etc/GMT+5 = UTC-5.
            if (off == 0) return "Etc/UTC";
            return off > 0 ? $"Etc/GMT-{off}" : $"Etc/GMT+{-off}";
        }
        return null;
    }

    /// <summary>
    /// Pick an IANA timezone whose local time is INSIDE the day band
    /// (10..20) AND whose local day-of-week is Mon..Sat. Used as the
    /// regression "still on shift" case.
    /// </summary>
    private static string? ChooseDayCrewOnShiftTimezone()
    {
        var utcNow = DateTimeOffset.UtcNow;
        for (var off = -12; off <= 14; off++)
        {
            var local = utcNow.ToOffset(TimeSpan.FromHours(off));
            var hour = local.Hour;
            if (hour < 10 || hour > 20) continue;
            var dow = local.DayOfWeek;
            if (dow == DayOfWeek.Sunday) continue;
            if (off == 0) return "Etc/UTC";
            return off > 0 ? $"Etc/GMT-{off}" : $"Etc/GMT+{-off}";
        }
        return null;
    }

    // ── LeadAutoDispatcher: lead exemption changes outcome=off-shift → dispatched

    [Fact]
    public async Task LeadAutoDispatcher_OffShift_TZ_Still_Dispatches_When_Crew_Has_Lead()
    {
        var tz = ChooseDayCrewOffShiftTimezone();
        if (tz is null) return; // wall-clock corner case — skip

        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "ad-off-shift", Name = "OffShift",
                Lead = "lead-os", Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 5, ScheduleEnabled = true, // schedule ENABLED so the gate would refuse pre-18.6
                Timezone = tz, Shift = "day",
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "ad-off-shift", AgentId = "member-os", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-os",
                Type = "step", Track = "design", State = "active", Title = "OS",
                AssignedToJson = "{\"kind\":\"agent\",\"id\":\"lead-os\",\"displayName\":\"Lead\"}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var dispatcher = f.Services.GetRequiredService<LeadAutoDispatcher>();
            var result = await dispatcher.RunOnceAsync(CancellationToken.None);

            // Q18.3 — lead is on-shift 24×7, so the crew is dispatchable in
            // the routing sense. Pre-18.6 this would have been OffShift = 1.
            Assert.Equal(1, result.Dispatched);
            Assert.Equal(0, result.OffShift);

            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-os");
            Assert.NotNull(wi.AutoDispatchedAt);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task LeadAutoDispatcher_OnShift_Day_Behaviour_Unchanged()
    {
        var tz = ChooseDayCrewOnShiftTimezone();
        if (tz is null) return; // wall-clock corner case — skip

        var (f, scope, db) = await BootAsync();
        try
        {
            // Regression — during the band, dispatch counts behave the same as
            // pre-18.6: outcome=dispatched, no off-shift hit.
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "ad-on-shift", Name = "OnShift",
                Lead = "lead-on", Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 5, ScheduleEnabled = true,
                Timezone = tz, Shift = "day",
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "ad-on-shift", AgentId = "member-on", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-on",
                Type = "step", Track = "design", State = "active", Title = "ON",
                AssignedToJson = "{\"kind\":\"agent\",\"id\":\"lead-on\",\"displayName\":\"Lead\"}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var dispatcher = f.Services.GetRequiredService<LeadAutoDispatcher>();
            var result = await dispatcher.RunOnceAsync(CancellationToken.None);
            Assert.Equal(1, result.Dispatched);
            Assert.Equal(0, result.OffShift);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    // ── CrewQueueWorker SweepClaims: off-shift roster falls through to lead ──

    [Fact]
    public async Task SweepClaims_Picks_Lead_When_All_Members_Off_Shift()
    {
        var tz = ChooseDayCrewOffShiftTimezone();
        if (tz is null) return; // wall-clock corner case — skip

        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "wq-off", Name = "WQ Off",
                Lead = "lead-wq", Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 5, ScheduleEnabled = true,
                Timezone = tz, Shift = "day",
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "wq-off", AgentId = "m1", AddedAt = DateTimeOffset.UtcNow });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "wq-off", AgentId = "m2", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-wq", Type = "step", Track = "design", State = "active", Title = "WQ",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
            await queue.EnqueueAsync("wi-wq", "crew", "wq-off", default);

            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            var tick = await worker.RunOnceAsync(default);
            Assert.True(tick.Claims >= 1);

            // The picker resolved to the lead — m1 / m2 are both off-shift, so
            // the fallback fires. Inspect the picked-up row.
            var pickedRow = await db.TaskQueue.AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkItemId == "wi-wq" && r.State == TaskQueueStates.PickedUp);
            Assert.NotNull(pickedRow);
            Assert.Equal("lead-wq", pickedRow!.PickedUpByAgent);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task SweepClaims_Picks_Member_When_Some_Members_On_Shift()
    {
        var tz = ChooseDayCrewOnShiftTimezone();
        if (tz is null) return;

        var (f, scope, db) = await BootAsync();
        try
        {
            // Regression — during the band, members claim and the lead is
            // bypassed (Phase 16.7 preference still applies inside the band).
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "wq-on", Name = "WQ On",
                Lead = "lead-wqon", Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 5, ScheduleEnabled = true,
                Timezone = tz, Shift = "day",
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "wq-on", AgentId = "m-on", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-wqon", Type = "step", Track = "design", State = "active", Title = "On",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
            await queue.EnqueueAsync("wi-wqon", "crew", "wq-on", default);

            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            await worker.RunOnceAsync(default);

            var pickedRow = await db.TaskQueue.AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkItemId == "wi-wqon" && r.State == TaskQueueStates.PickedUp);
            Assert.NotNull(pickedRow);
            Assert.Equal("m-on", pickedRow!.PickedUpByAgent);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task SweepClaims_Resume_Pin_Bypasses_Off_Shift_Prior_Agent()
    {
        // Q18.3 — the resume-pin must not hand the work back to a member who
        // was on-shift when the deliverable was written but is now off-shift.
        // Off-shift TZ + a lead → expected pick is the lead, not the prior
        // member.
        var tz = ChooseDayCrewOffShiftTimezone();
        if (tz is null) return;

        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "wq-resume", Name = "Resume",
                Lead = "lead-resume", Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 5, ScheduleEnabled = true,
                Timezone = tz, Shift = "day",
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "wq-resume", AgentId = "prior-mem", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-resume", Type = "step", Track = "design", State = "active", Title = "R",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

            // A prior deliverable from `prior-mem` would normally pin the
            // resume back to that agent. Under Q18.3, the pin is honoured
            // only if the prior agent is currently on-shift.
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = "wi-resume",
                CrewId = "wq-resume",
                GeneratorAgentId = "prior-mem",
                EvaluatorAgentId = "prior-mem",
                Iter = 1,
                Score = 75,
                Verdict = "iterate",
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-3),
            });
            await db.SaveChangesAsync();

            var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
            await queue.EnqueueAsync("wi-resume", "crew", "wq-resume", default);

            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            await worker.RunOnceAsync(default);

            var pickedRow = await db.TaskQueue.AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkItemId == "wi-resume" && r.State == TaskQueueStates.PickedUp);
            Assert.NotNull(pickedRow);
            // The off-shift prior agent must NOT be honoured; the lead is
            // the on-shift fallback (24×7).
            Assert.Equal("lead-resume", pickedRow!.PickedUpByAgent);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task SweepClaims_No_Lead_All_Off_Shift_Records_No_Agent_Available()
    {
        // Q18.3 — when there's no lead to fall back on AND all members are
        // off-shift, the picker resolves to null. The worker must skip
        // cleanly with `no-agent-available` rather than calling
        // ClaimNextAsync(crewId, null) and claiming the row with a null
        // PickedUpByAgent.
        var tz = ChooseDayCrewOffShiftTimezone();
        if (tz is null) return;

        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "wq-orphan", Name = "Orphan",
                Lead = null, // no lead — only on-shift members can pick up
                Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 5, ScheduleEnabled = true,
                Timezone = tz, Shift = "day",
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "wq-orphan", AgentId = "m-off", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-orphan", Type = "step", Track = "design", State = "active", Title = "O",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
            await queue.EnqueueAsync("wi-orphan", "crew", "wq-orphan", default);

            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            var tick = await worker.RunOnceAsync(default);

            // The row must remain queued (NOT picked-up with PickedUpByAgent=null).
            var queuedRow = await db.TaskQueue.AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkItemId == "wi-orphan");
            Assert.NotNull(queuedRow);
            Assert.Equal(TaskQueueStates.Queued, queuedRow!.State);

            // And the sweep must have recorded the skip with the right reason
            // so the operator's diagnostics surface the orphaned crew.
            Assert.NotNull(tick.ClaimSkips);
            Assert.Contains(tick.ClaimSkips!, s =>
                s.CrewId == "wq-orphan" && s.Reason == "no-agent-available");
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task LeadAutoDispatcher_NoLead_Crew_Is_Silently_Skipped()
    {
        // Q18.3 spec language: "the off-shift tag remains for the (rare)
        // crew.Lead == null OR lead.paused case." In the v0.1 dispatcher,
        // crews with crew.Lead == null are filtered out of `crewsByLead`
        // entirely (LeadAutoDispatcher only indexes lead-routed work),
        // so they never reach the schedule check — no `outcome: off-shift`
        // is recorded. This test locks in that observable behaviour so a
        // future refactor that *does* change it (e.g. adding a paused-lead
        // codepath) breaks here and forces a deliberate spec update.
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            // No-lead crew with a work item assigned via crewId. Schedule
            // disabled so wall-clock can't accidentally trigger a different
            // outcome — the dispatcher should ignore the row regardless.
            db.Crews.Add(new CrewRow
            {
                Id = "ad-no-lead", Name = "NoLead",
                Lead = null,
                Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 5, ScheduleEnabled = false,
            });
            db.CrewMembers.Add(new CrewMemberRow { CrewId = "ad-no-lead", AgentId = "m-only", AddedAt = DateTimeOffset.UtcNow });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-no-lead",
                Type = "step", Track = "design", State = "active", Title = "NL",
                AssignedToJson = "{\"kind\":\"crew\",\"crewId\":\"ad-no-lead\"}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var dispatcher = f.Services.GetRequiredService<LeadAutoDispatcher>();
            var result = await dispatcher.RunOnceAsync(CancellationToken.None);

            // Documented "silently skipped" behaviour — none of the four
            // counters fire because the crew is filtered before the loop.
            Assert.Equal(0, result.Dispatched);
            Assert.Equal(0, result.OffShift);
            Assert.Equal(0, result.CapacityBlocked);

            // The work item must remain unstamped — the dispatcher did
            // nothing with it.
            var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == "wi-no-lead");
            Assert.Null(wi.AutoDispatchedAt);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }

    [Fact]
    public async Task SweepClaims_Lead_Only_Crew_Picks_Lead_Regression()
    {
        // Schedule-disabled lead-only crew — not strictly a 18.6 case, but
        // the rewrite of the picker must preserve the pre-existing fallback
        // when the roster is empty.
        var (f, scope, db) = await BootAsync();
        try
        {
            var product = await db.Products.FirstAsync();
            db.Crews.Add(new CrewRow
            {
                Id = "wq-leadonly", Name = "Lead Only",
                Lead = "lone-lead", Model = "x",
                ModelInputPricePerMTok = 1m, ModelOutputPricePerMTok = 1m,
                MaxConcurrent = 5, ScheduleEnabled = false,
            });
            db.WorkItems.Add(new WorkItemRow
            {
                Id = "wi-lone", Type = "step", Track = "design", State = "active", Title = "Lone",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var queue = scope.ServiceProvider.GetRequiredService<ITaskQueueService>();
            await queue.EnqueueAsync("wi-lone", "crew", "wq-leadonly", default);

            var worker = f.Services.GetRequiredService<CrewQueueWorker>();
            await worker.RunOnceAsync(default);

            var pickedRow = await db.TaskQueue.AsNoTracking()
                .FirstOrDefaultAsync(r => r.WorkItemId == "wi-lone" && r.State == TaskQueueStates.PickedUp);
            Assert.NotNull(pickedRow);
            Assert.Equal("lone-lead", pickedRow!.PickedUpByAgent);
        }
        finally { scope.Dispose(); f.Dispose(); }
    }
}
