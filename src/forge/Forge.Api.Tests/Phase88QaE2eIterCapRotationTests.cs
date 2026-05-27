// -----------------------------------------------------------------------
// <copyright file="Phase88QaE2eIterCapRotationTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// Phase 88 — when <see cref="IStoryArtifactStrategy.ProduceStageE2eAsync"/>
/// throws <see cref="SloppyVerdictException"/> (the producer's
/// <see cref="DeliverableLoop"/> exited with verdict="sloppy" — iter-cap,
/// agent-refusal, score-plateau, token-cap), the orchestrator's
/// <c>DriveOneQaE2eWorkstreamAsync</c> catch block MUST mirror the
/// impl-path behavior at <c>CrewQueueWorker.cs:2848-2884</c>:
/// <list type="number">
/// <item><c>wi.ExecutionStatus = PendingReassign</c> — picked up by
///   <see cref="SloppyReassignmentReconciler"/> for fresh pair rotation.</item>
/// <item><c>PriorPairsJson</c> appends the current generator+evaluator
///   so the reconciler's pair-exclusion filter doesn't re-pick the
///   refusing pair.</item>
/// <item><c>ws.Status = Stalled</c> + descriptive <c>StallReason</c>.</item>
/// </list>
///
/// <para>Pre-Phase-88 the orchestrator only set <c>ws.Status = Stalled</c>
/// — the reconciler queries <c>WHERE ExecutionStatus = PendingReassign</c>
/// (<c>SloppyReassignmentReconciler.cs:140</c>), so a stalled row with the
/// default <c>queued</c> status was never picked up → PairAttemptCount
/// stayed at 1 → no rotation → operator had to manually restart to break
/// the deadlock. This bug burned $458.91 on code-intelligence:CLEAN-CODE
/// stage 6.1-e2e (2026-05-25).</para>
///
/// <para>A separate fact pins the non-sloppy generic-exception path
/// (e.g., transport failure rethrown as a non-transient
/// <see cref="System.InvalidOperationException"/>) — those MUST NOT
/// set <c>PendingReassign</c> because they're not a pair-quality
/// signal; the orchestrator's other failure handling is responsible.
/// </para>
/// </summary>
[Collection("Phase28")]
public class Phase88QaE2eIterCapRotationTests
{
    private const string ProductId = "prod-phase88-rotate";
    private const string RepoId = "repo-phase88-rotate";

    /// <summary>
    /// Test-only strategy that throws on
    /// <see cref="ProduceStageE2eAsync"/> with the configured exception
    /// (either <see cref="SloppyVerdictException"/> or a generic
    /// <see cref="InvalidOperationException"/>). All other interface
    /// methods throw <see cref="NotImplementedException"/> because the
    /// Stage 51 test pattern (pre-seeded plan-doc passes + complete
    /// impl rows + Phase=Implement spawn) drives the orchestrator
    /// directly into <c>DriveOneQaE2eWorkstreamAsync</c> without
    /// invoking plan/decompose/impl paths.
    /// </summary>
    private sealed class ThrowOnE2eStrategy : IStoryArtifactStrategy
    {
        public Exception Thrown { get; }

        public ThrowOnE2eStrategy(Exception thrown)
        {
            this.Thrown = thrown;
        }

        public Task<IReadOnlyDictionary<string, string>> ProduceStageE2eAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
        {
            throw this.Thrown;
        }

        // All remaining methods are unreachable for the qa-e2e drive
        // path under the Stage 51 test seed pattern.
        public Task<IReadOnlyDictionary<string, string>> ProducePlanArtifactsAsync(
            StorySpawnRow spawn, WorkItemRow story, bool forceResume = false, CancellationToken ct = default)
            => throw new NotImplementedException("Phase 88 qa-e2e rotation test must not invoke plan path");

        public Task<(string RepoPath, string Content)> ProduceSinglePlanArtifactAsync(
            StorySpawnRow spawn, WorkItemRow story, string docKey, string worktreePath,
            string planBranch, RepoRow? repo, bool forceResume = false, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task ResumeSingleArtifactAsync(
            StorySpawnRow spawn, WorkItemRow story, string key, bool restartFromScratch = false, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<DecompositionWorkstream>> ProduceDecompositionAsync(
            StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyDictionary<string, string>> ProduceWorkstreamChangesAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyDictionary<string, string>> ReadCachedWorkstreamFilesAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ReviewComment>> ProduceReviewCommentsAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, string prId, int round, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<ReviewFix> ProduceReviewFixAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, ReviewCommentContext comment, CancellationToken ct = default)
            => throw new NotImplementedException();

#pragma warning disable CS0618 // Obsolete legacy-drain methods.
        public Task<IReadOnlyDictionary<string, string>> ProduceQaFeatureFilesAsync(
            StorySpawnRow spawn, WorkItemRow story, IReadOnlyList<WorkstreamRow> workstreams, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyDictionary<string, string>> ProduceStepDefinitionsAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => throw new NotImplementedException();
#pragma warning restore CS0618

        public Task<IReadOnlyDictionary<string, string>> ProduceConflictResolutionAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, IReadOnlyList<ConflictedFile> conflictedFiles,
            string baseBranch, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Subclass of <see cref="ForgeWebApplicationFactory"/> that runs the
    /// base's <see cref="ForgeWebApplicationFactory.ConfigureWebHost"/>
    /// (which registers the deterministic strategy + in-memory git/provider
    /// ops) and THEN swaps <see cref="IStoryArtifactStrategy"/> for the
    /// throwing test fake. Subclassing is cleaner than
    /// <c>WithWebHostBuilder</c> here because the latter returns a new
    /// factory instance that callers easily forget to use.
    /// </summary>
    private sealed class ThrowingE2eFactory : ForgeWebApplicationFactory
    {
        private readonly Exception thrown;

        public ThrowingE2eFactory(Exception thrown)
            : base(new Dictionary<string, string?>
            {
                ["Forge:Stories:OrchestratorEnabled"] = "true",
                ["Forge:Stories:AutoProgressGates"] = "false",
                ["Forge:Stories:SpawnDriveInline"] = "true",
            })
        {
            this.thrown = thrown;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            var localThrown = this.thrown;
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStoryArtifactStrategy>();
                services.AddSingleton<IStoryArtifactStrategy>(new ThrowOnE2eStrategy(localThrown));
            });
        }
    }

    private static ForgeWebApplicationFactory NewFactory(Exception thrown)
        => new ThrowingE2eFactory(thrown);

    private static (string storyId, string spawnId, string implWsId, string qaWsId) Seed(
        ForgeWebApplicationFactory f, string suffix)
    {
        var storyId = $"WI-phase88-{suffix}";
        var spawnId = $"spawn-{storyId}";
        var implWsId = $"ws-phase88-{suffix}-impl";
        var qaWsId = $"ws-phase88-{suffix}-qa";

        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        if (!control.Products.Any(p => p.Id == ProductId))
        {
            control.Products.Add(new ProductRow
            {
                Id = ProductId,
                Name = "Phase 88 rotation",
                Slug = "phase88-rotate",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        if (!db.Repos.Any(r => r.Id == RepoId))
        {
            db.Repos.Add(new RepoRow
            {
                Id = RepoId,
                ProductId = ProductId,
                DisplayName = "Phase 88 rotation repo",
                Kind = "local",
                RemoteUrl = "file://phase88-rotate",
                CloneRoot = "/tmp/phase88-rotate",
                DefaultBranch = "main",
            });
        }
        db.WorkItems.Add(new WorkItemRow
        {
            Id = storyId,
            Provider = "ado",
            Track = "dev",
            Type = "feature",
            State = "active",
            Title = "Phase 88 rotation story",
            Priority = "med",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        // The qa-e2e workstream's own WorkItem row — this is the row
        // ExecutionStatus is set on (NOT the parent story).
        db.WorkItems.Add(new WorkItemRow
        {
            Id = qaWsId,
            Provider = "ado",
            Track = "dev",
            Type = "task",
            State = "active",
            Title = "Phase 88 rotation qa-e2e",
            Priority = "med",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            // Pair assignment so PriorPairsJson append can dedupe.
            PairAttemptCount = 1,
        });
        db.StorySpawns.Add(new StorySpawnRow
        {
            Id = spawnId,
            StoryId = storyId,
            StoryTitle = "Phase 88 rotation",
            Phase = StorySpawnPhases.Implement,
            PlanBranch = $"story/{storyId}/plan",
            PlanPrId = $"PR-phase88-{suffix}-plan",
            RepoId = RepoId,
            BaseBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            StoryPoints = 3,
            PlanDocPrsJson = null,
            AutoMerge = true,
        });
        // Impl row already merged → status=complete so dep gate releases.
        db.Workstreams.Add(new WorkstreamRow
        {
            Id = implWsId,
            Sort = 10,
            RepoId = RepoId,
            Name = "setup",
            Branch = $"ws/{implWsId}",
            Base = "main",
            Epic = string.Empty,
            Status = "complete",
            WorktreeJson = "[]",
            AgentsJson = "[]",
            PrsJson = JsonSerializer.Serialize(new[] { $"PR-phase88-{suffix}-impl" }),
            CardsJson = "[]",
            Summary = "Impl for setup",
            Planned = true,
            StorySpawnId = spawnId,
            PlanAnchor = "phase-foundation/stage-setup",
            PhaseAnchor = "phase-foundation",
            Category = WorkstreamCategories.Impl,
        });
        // qa-e2e workstream, ready to drive (dep satisfied above).
        db.Workstreams.Add(new WorkstreamRow
        {
            Id = qaWsId,
            Sort = 11,
            RepoId = RepoId,
            Name = "setup — E2E",
            Branch = $"ws/{qaWsId}",
            Base = "main",
            Epic = string.Empty,
            Status = "planning",
            WorktreeJson = "[]",
            // Pre-assigned a (refusing) pair so PriorPairsJson append
            // has a definite shape we can verify on the parking row.
            AgentsJson = JsonSerializer.Serialize(new[] { "HD3", "BH" }),
            PrsJson = "[]",
            CardsJson = "[]",
            Summary = "E2E for setup",
            Planned = true,
            StorySpawnId = spawnId,
            PlanAnchor = "phase-foundation/stage-setup" + CrewStoryArtifactStrategy.QaE2eAnchorSuffix,
            PhaseAnchor = "phase-foundation",
            Category = WorkstreamCategories.QaE2e,
            DepsJson = JsonSerializer.Serialize(new[] { "setup" }),
        });
        // Seed the merged impl PR so OpenPrAsync's back-reference resolves.
        db.Prs.Add(new PrRow
        {
            Id = $"PR-phase88-{suffix}-impl",
            RepoId = RepoId,
            Col = "merged",
            Title = "[impl] setup",
            Branch = $"ws/{implWsId}",
            Base = "main",
            Workstream = implWsId,
            Epic = string.Empty,
            EpicColor = string.Empty,
            AuthorJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            IsActive = false,
            LastSyncStatus = "idle",
            Provider = "local",
            WorkItemId = implWsId,
            Description = "seed impl PR (merged)",
        });
        // Plan-doc passes so ArePlanDocsAllPassedAsync releases.
        foreach (var fileName in new[] { "architecture.md", "tech-spec.md", "implementation-plan.md", "e2e-scenarios.md" })
        {
            var docId = $"plan-doc:{spawnId}:{fileName}";
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = docId,
                CrewId = "crew-arch",
                GeneratorAgentId = "G",
                EvaluatorAgentId = "E",
                Iter = 1,
                Score = 95,
                DimsJson = "{}",
                Verdict = "pass",
                Notes = string.Empty,
                GeneratorSummary = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        control.SaveChanges();
        db.SaveChanges();
        return (storyId, spawnId, implWsId, qaWsId);
    }

    private static async Task DriveAsync(ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var orch = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
        await orch.DriveAsync(storyId);
    }

    // ── 1. SloppyVerdictException → wi.ExecutionStatus = PendingReassign ─

    /// <summary>
    /// Headline fix: when the qa-e2e producer throws
    /// <see cref="SloppyVerdictException"/>, the catch block MUST park
    /// the workstream's WorkItem with
    /// <see cref="ExecutionStatuses.PendingReassign"/> AND record the
    /// refusing pair in <c>PriorPairsJson</c> so
    /// <see cref="SloppyReassignmentReconciler"/> picks it up and
    /// rotates to a fresh pair (mirroring impl-path behavior).
    /// </summary>
    [Fact]
    public async Task SloppyVerdictException_ParksWorkItemForReassignAndStallsWorkstream()
    {
        var sloppy = new SloppyVerdictException(
            new DeliverableLoopResult(
                FinalVerdict: "sloppy",
                Iterations: 8,
                FinalScore: 78,
                DeliverableRowIds: Array.Empty<long>(),
                TotalCostUsd: 5.42m,
                SloppyReason: SloppyReasons.AgentRefusal),
            "65 iter-cap on HD3/BH refusal");

        using var f = NewFactory(sloppy);
        // Boot the host explicitly so ConfigureTestServices runs.
        using (f.CreateClient()) { /* boot */ }
        var (storyId, spawnId, _, qaWsId) = Seed(f, "park-on-sloppy");

        await DriveAsync(f, storyId);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == qaWsId);
        Assert.Equal(WorkstreamStatuses.Stalled, ws.Status);
        Assert.False(string.IsNullOrEmpty(ws.StallReason));
        Assert.Contains("qa-e2e sloppy", ws.StallReason);
        Assert.Contains(SloppyReasons.AgentRefusal, ws.StallReason);

        var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == qaWsId);
        Assert.Equal(ExecutionStatuses.PendingReassign, wi.ExecutionStatus);

        // PriorPairsJson must contain the assigned [gen, eval] pair so
        // the reconciler's pair-exclusion filter doesn't re-pick it. We
        // don't assert specific names because the orchestrator's qa-e2e
        // routing (Phase 88 Part A) picks from crew-qa at run time —
        // we instead assert the recorded pair matches the pair on the
        // workstream's AgentsJson.
        Assert.False(string.IsNullOrEmpty(wi.PriorPairsJson),
            "PriorPairsJson must be populated for reconciler pair-exclusion filter.");
        var priorPairs = JsonSerializer.Deserialize<List<string[]>>(wi.PriorPairsJson!)!;
        Assert.NotEmpty(priorPairs);
        var last = priorPairs[priorPairs.Count - 1];
        Assert.Equal(2, last.Length);
        var assigned = JsonSerializer.Deserialize<string[]>(ws.AgentsJson)!;
        Assert.True(assigned.Length >= 2, "qa-e2e workstream should have gen+eval assigned");
        Assert.Equal(assigned[0], last[0]);
        Assert.Equal(assigned[1], last[1]);
    }

    // ── 2. Generic Exception → ws.Status=Stalled, WorkItem untouched ───

    /// <summary>
    /// Generic non-transient exception (e.g., evaluator crash,
    /// schema-validation failure inside the producer that isn't
    /// classified as transient transport) → only the workstream gets
    /// <c>Status=Stalled</c>. <c>wi.ExecutionStatus</c> must NOT flip
    /// to <see cref="ExecutionStatuses.PendingReassign"/> because the
    /// failure isn't a pair-quality signal — rotating to a fresh pair
    /// won't help.
    /// </summary>
    [Fact]
    public async Task NonSloppyException_StallsWorkstreamOnly_DoesNotParkWorkItem()
    {
        var generic = new InvalidOperationException("evaluator JSON schema invalid");

        using var f = NewFactory(generic);
        using (f.CreateClient()) { /* boot */ }
        var (storyId, _, _, qaWsId) = Seed(f, "stall-on-generic");

        await DriveAsync(f, storyId);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();

        var ws = await db.Workstreams.AsNoTracking().FirstAsync(w => w.Id == qaWsId);
        Assert.Equal(WorkstreamStatuses.Stalled, ws.Status);
        Assert.False(string.IsNullOrEmpty(ws.StallReason));
        Assert.Contains("InvalidOperationException", ws.StallReason);

        var wi = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == qaWsId);
        // Crucially NOT PendingReassign — non-sloppy failures don't
        // hand off to the reassignment reconciler.
        Assert.NotEqual(ExecutionStatuses.PendingReassign, wi.ExecutionStatus);
    }
}
