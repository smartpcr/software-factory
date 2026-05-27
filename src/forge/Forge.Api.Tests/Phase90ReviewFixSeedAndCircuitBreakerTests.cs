// -----------------------------------------------------------------------
// <copyright file="Phase90ReviewFixSeedAndCircuitBreakerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Forge.Api.Persistence;
using Forge.Api.Services.Stories;
using Forge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// 2026-05-26 — regressions for the "review-fix stuck for hours" failure
/// mode observed on <c>spawn-local-UPDATE-HISTORY-IN-CL</c> stage 1.2
/// (workstream <c>ws-local-update-history-in-cl-…-stage-implement-clusterdbstore-…</c>,
/// PR #15864664). The engineer agent was burning 5+ minutes of its 8-minute
/// budget on <c>Get-ChildItem -Recurse</c> scans of <c>E:\</c> before
/// locating the file under review, then ran out of time before producing a
/// fix; the infinite-retry loop in <c>RunReviewFixLoopAsync</c> then
/// re-attempted every orchestrator tick → 4× $30 burns + indefinite stall.
///
/// <para>Two-pronged fix:</para>
/// <list type="number">
/// <item>
///   <b>Phase 2</b> — <see cref="CrewStoryArtifactStrategy"/> now seeds the
///   file under review into the engineer's scratch workspace at the SAME
///   relative path the prompt mentions, and embeds a numbered ±25-line
///   slice around the comment anchor in the prompt. The agent no longer
///   needs to search the filesystem.
/// </item>
/// <item>
///   <b>Phase 3</b> — <see cref="StorySpawnOrchestrator"/> tracks per-
///   <c>(wsId, commentId)</c> consecutive budget-exhaustion failures of
///   <see cref="IStoryArtifactStrategy.ProduceReviewFixAsync"/>. After
///   <c>MaxReviewFixTimeouts</c> consecutive throws whose <c>ex.Message</c>
///   contains <c>"hard timeout"</c> OR (<c>"exceeded"</c> AND <c>"budget"</c>),
///   the loop posts a system reply, resolves the comment via
///   <see cref="IStoryProviderOps.ResolvePrCommentAsync"/> with
///   <c>ResolvedByUserId="system:review-fix-budget"</c>, and emits a
///   <c>review-fix.budget-exhausted</c> transcript event so an operator
///   can review the auto-resolution.
/// </item>
/// </list>
///
/// <para>The Phase 2 tests are pure-filesystem unit tests on the extracted
/// <see cref="CrewStoryArtifactStrategy.TrySeedReviewFileAsync"/> +
/// <see cref="CrewStoryArtifactStrategy.TryBuildSliceAroundLineAsync"/>
/// helpers (no DB, no factory). The Phase 3 tests use the
/// <see cref="ForgeWebApplicationFactory"/> + swap-strategy pattern from
/// <see cref="Phase88QaE2eIterCapRotationTests"/> so we can drive
/// <c>orchestrator.DriveAsync</c> repeatedly with a strategy whose
/// <c>ProduceReviewFixAsync</c> throws the exact dispatcher error shape.</para>
/// </summary>
[Collection("Phase28")]
public class Phase90ReviewFixSeedAndCircuitBreakerTests
{
    // -----------------------------------------------------------------------
    // Phase 2 — TryBuildSliceAroundLineAsync (pure unit)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Mid-file anchor: a numbered slice of ±<c>contextLines</c> rows around
    /// <c>line</c> must be returned, with each row prefixed by its
    /// 1-indexed source line number padded to 4 columns.
    /// </summary>
    [Fact]
    public async Task TryBuildSliceAroundLineAsync_Mid_File_Anchor_Returns_Window_Around_Line()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "src.cs");
            await File.WriteAllLinesAsync(path, Enumerable.Range(1, 100).Select(i => $"line {i}"));

            var slice = await CrewStoryArtifactStrategy.TryBuildSliceAroundLineAsync(
                path, line: 50, contextLines: 3, CancellationToken.None);

            Assert.NotNull(slice);
            // ±3 window centred on line 50 → lines 47..53, each numbered.
            Assert.Contains("  47: line 47", slice);
            Assert.Contains("  50: line 50", slice);
            Assert.Contains("  53: line 53", slice);
            // Out-of-window lines must be absent.
            Assert.DoesNotContain("  46: ", slice);
            Assert.DoesNotContain("  54: ", slice);
        }
        finally { CleanupTempDir(dir); }
    }

    /// <summary>
    /// <c>line == 0</c> models a file-level reviewer comment (no anchor).
    /// The slice anchors at the file head instead of returning null.
    /// </summary>
    [Fact]
    public async Task TryBuildSliceAroundLineAsync_Zero_Line_Anchors_At_File_Head()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "src.cs");
            await File.WriteAllLinesAsync(path, Enumerable.Range(1, 20).Select(i => $"L{i}"));

            var slice = await CrewStoryArtifactStrategy.TryBuildSliceAroundLineAsync(
                path, line: 0, contextLines: 5, CancellationToken.None);

            Assert.NotNull(slice);
            Assert.Contains("   1: L1", slice);
            Assert.Contains("   6: L6", slice);
            Assert.DoesNotContain("   7: ", slice);
        }
        finally { CleanupTempDir(dir); }
    }

    /// <summary>
    /// <c>line</c> past the last line clamps to the last line (no
    /// out-of-range read or null return).
    /// </summary>
    [Fact]
    public async Task TryBuildSliceAroundLineAsync_Line_Past_End_Clamps_To_Last_Line()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "src.cs");
            await File.WriteAllLinesAsync(path, new[] { "A", "B", "C" });

            var slice = await CrewStoryArtifactStrategy.TryBuildSliceAroundLineAsync(
                path, line: 9999, contextLines: 2, CancellationToken.None);

            Assert.NotNull(slice);
            // Anchor clamps to 3 → window [1..3].
            Assert.Contains("   1: A", slice);
            Assert.Contains("   3: C", slice);
        }
        finally { CleanupTempDir(dir); }
    }

    /// <summary>
    /// Missing file → returns null (best-effort; caller falls back to a
    /// prompt without the slice section).
    /// </summary>
    [Fact]
    public async Task TryBuildSliceAroundLineAsync_Missing_File_Returns_Null()
    {
        var dir = NewTempDir();
        try
        {
            var slice = await CrewStoryArtifactStrategy.TryBuildSliceAroundLineAsync(
                Path.Combine(dir, "does-not-exist.cs"), line: 1, contextLines: 5, CancellationToken.None);
            Assert.Null(slice);
        }
        finally { CleanupTempDir(dir); }
    }

    /// <summary>
    /// Empty file → returns null (no content to render).
    /// </summary>
    [Fact]
    public async Task TryBuildSliceAroundLineAsync_Empty_File_Returns_Null()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "empty.cs");
            await File.WriteAllTextAsync(path, string.Empty);

            var slice = await CrewStoryArtifactStrategy.TryBuildSliceAroundLineAsync(
                path, line: 1, contextLines: 5, CancellationToken.None);

            Assert.Null(slice);
        }
        finally { CleanupTempDir(dir); }
    }

    // -----------------------------------------------------------------------
    // Phase 2 — TrySeedReviewFileAsync (pure unit)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Happy path: worktree exists on disk + file under review exists →
    /// file is COPIED to the same relative path under <c>workspace</c>,
    /// slice is generated, and the returned tuple flags
    /// <c>SeededFileAbsent=false</c> so the prompt-builder picks the
    /// "## File context (already seeded)" + "## Code around line N"
    /// branch.
    /// </summary>
    [Fact]
    public async Task TrySeedReviewFileAsync_Happy_Path_Copies_File_And_Returns_Slice()
    {
        var dir = NewTempDir();
        try
        {
            var workspace = Path.Combine(dir, "workspace");
            var worktree = Path.Combine(dir, "worktree");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(worktree);

            // Seed a source file at src\Sub\Example.cs inside the worktree.
            var relativePath = Path.Combine("src", "Sub", "Example.cs");
            var sourceFile = Path.Combine(worktree, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            await File.WriteAllLinesAsync(sourceFile,
                Enumerable.Range(1, 40).Select(i => $"public void M{i}() {{ }}"));

            var (hint, absent, slice) = await CrewStoryArtifactStrategy.TrySeedReviewFileAsync(
                workspace, worktree, relativePath, line: 20, CancellationToken.None);

            Assert.Equal(worktree, hint);
            Assert.False(absent);
            Assert.NotNull(slice);
            Assert.Contains("  20: public void M20()", slice);

            // The file must be physically present in the workspace at the
            // same relative path. Any intermediate directories must have
            // been created.
            var seededTarget = Path.Combine(workspace, relativePath);
            Assert.True(File.Exists(seededTarget), $"expected seeded copy at {seededTarget}");
            var seededLines = await File.ReadAllLinesAsync(seededTarget);
            Assert.Equal(40, seededLines.Length);
            Assert.Equal("public void M1() { }", seededLines[0]);
        }
        finally { CleanupTempDir(dir); }
    }

    /// <summary>
    /// Worktree exists but the named file does NOT (file was renamed /
    /// moved / deleted on the branch tip) → returns
    /// <c>(WorktreeHintPath=path, SeededFileAbsent=true, SeededFileSlice=null)</c>
    /// and DOES NOT copy anything. The prompt-builder will render the
    /// "moved or renamed" warning section.
    /// </summary>
    [Fact]
    public async Task TrySeedReviewFileAsync_Worktree_Exists_But_File_Missing_Reports_Absent()
    {
        var dir = NewTempDir();
        try
        {
            var workspace = Path.Combine(dir, "workspace");
            var worktree = Path.Combine(dir, "worktree");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(worktree);
            // Intentionally do NOT create src/Renamed.cs inside the worktree.

            var (hint, absent, slice) = await CrewStoryArtifactStrategy.TrySeedReviewFileAsync(
                workspace, worktree, Path.Combine("src", "Renamed.cs"), line: 1, CancellationToken.None);

            Assert.Equal(worktree, hint);
            Assert.True(absent);
            Assert.Null(slice);

            // Workspace must contain no copies — the seed path was never
            // exercised.
            Assert.Empty(Directory.GetFiles(workspace, "*", SearchOption.AllDirectories));
        }
        finally { CleanupTempDir(dir); }
    }

    /// <summary>
    /// Worktree resolution returned null → returns
    /// <c>(WorktreeHintPath=null, …)</c> and copies nothing. Models the
    /// post-merge / pre-checkout state where the workstream branch has
    /// been cleaned up but a stray comment is still in the active queue.
    /// </summary>
    [Fact]
    public async Task TrySeedReviewFileAsync_Null_Worktree_Returns_All_Null_And_Copies_Nothing()
    {
        var dir = NewTempDir();
        try
        {
            var workspace = Path.Combine(dir, "workspace");
            Directory.CreateDirectory(workspace);

            var (hint, absent, slice) = await CrewStoryArtifactStrategy.TrySeedReviewFileAsync(
                workspace, resolvedWorktreePath: null, "src/Example.cs", line: 1, CancellationToken.None);

            Assert.Null(hint);
            Assert.False(absent);
            Assert.Null(slice);
            Assert.Empty(Directory.GetFiles(workspace, "*", SearchOption.AllDirectories));
        }
        finally { CleanupTempDir(dir); }
    }

    /// <summary>
    /// Worktree path is non-empty but doesn't exist on disk → same
    /// behaviour as null (treat as missing).
    /// </summary>
    [Fact]
    public async Task TrySeedReviewFileAsync_Worktree_Path_Does_Not_Exist_Returns_Null_Hint()
    {
        var dir = NewTempDir();
        try
        {
            var workspace = Path.Combine(dir, "workspace");
            Directory.CreateDirectory(workspace);

            var (hint, absent, slice) = await CrewStoryArtifactStrategy.TrySeedReviewFileAsync(
                workspace, Path.Combine(dir, "no-such-worktree"), "src/Example.cs", line: 1, CancellationToken.None);

            Assert.Null(hint);
            Assert.False(absent);
            Assert.Null(slice);
        }
        finally { CleanupTempDir(dir); }
    }

    // -----------------------------------------------------------------------
    // Phase 3 — circuit breaker (factory + swap-strategy integration)
    // -----------------------------------------------------------------------

    private const string SystemResolverId = "system:review-fix-budget";
    private const string BudgetExhaustedEventKind = "review-fix.budget-exhausted";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private const string WriterHeader = "X-Forge-User";
    private const string WriterValue = "po@forge.local";

    /// <summary>
    /// Test-only <see cref="IStoryArtifactStrategy"/> whose
    /// <c>ProduceReviewFixAsync</c> behaviour is configurable per
    /// invocation index. All other methods delegate to a wrapped
    /// <see cref="DeterministicStoryArtifactStrategy"/> so the rest of
    /// the orchestrator lifecycle (plan / decompose / impl) drives
    /// normally; only the review-fix step is intercepted.
    /// </summary>
    private sealed class ConfigurableReviewFixStrategy : IStoryArtifactStrategy
    {
        private readonly DeterministicStoryArtifactStrategy inner;
        private int callCount;

        public ConfigurableReviewFixStrategy(DeterministicStoryArtifactStrategy inner)
        {
            this.inner = inner;
            this.Behavior = _ => throw new InvalidOperationException(
                "Copilot SDK hard timeout: exceeded 8m budget");
        }

        public Func<int, Task<ReviewFix>> Behavior { get; set; }

        public int CallCount => this.callCount;

        public Task<ReviewFix> ProduceReviewFixAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, ReviewCommentContext comment, CancellationToken ct = default)
        {
            var n = System.Threading.Interlocked.Increment(ref this.callCount);
            return this.Behavior(n);
        }

        public Task<IReadOnlyDictionary<string, string>> ProducePlanArtifactsAsync(
            StorySpawnRow spawn, WorkItemRow story, bool forceResume = false, CancellationToken ct = default)
            => this.inner.ProducePlanArtifactsAsync(spawn, story, forceResume, ct);

        public Task<(string RepoPath, string Content)> ProduceSinglePlanArtifactAsync(
            StorySpawnRow spawn, WorkItemRow story, string docKey, string worktreePath,
            string planBranch, RepoRow? repo, bool forceResume = false, CancellationToken ct = default)
            => this.inner.ProduceSinglePlanArtifactAsync(spawn, story, docKey, worktreePath, planBranch, repo, forceResume, ct);

        public Task ResumeSingleArtifactAsync(
            StorySpawnRow spawn, WorkItemRow story, string key, bool restartFromScratch = false, CancellationToken ct = default)
            => this.inner.ResumeSingleArtifactAsync(spawn, story, key, restartFromScratch, ct);

        public Task<IReadOnlyList<DecompositionWorkstream>> ProduceDecompositionAsync(
            StorySpawnRow spawn, WorkItemRow story, CancellationToken ct = default)
            => this.inner.ProduceDecompositionAsync(spawn, story, ct);

        public Task<IReadOnlyDictionary<string, string>> ProduceWorkstreamChangesAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => this.inner.ProduceWorkstreamChangesAsync(spawn, workstream, ct);

        public Task<IReadOnlyDictionary<string, string>> ReadCachedWorkstreamFilesAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => this.inner.ReadCachedWorkstreamFilesAsync(spawn, workstream, ct);

        public Task<IReadOnlyList<ReviewComment>> ProduceReviewCommentsAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, string prId, int round, CancellationToken ct = default)
            => this.inner.ProduceReviewCommentsAsync(spawn, workstream, prId, round, ct);

        public Task<IReadOnlyDictionary<string, string>> ProduceStageE2eAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => this.inner.ProduceStageE2eAsync(spawn, workstream, ct);

#pragma warning disable CS0618 // Obsolete legacy-drain methods.
        public Task<IReadOnlyDictionary<string, string>> ProduceQaFeatureFilesAsync(
            StorySpawnRow spawn, WorkItemRow story, IReadOnlyList<WorkstreamRow> workstreams, CancellationToken ct = default)
            => this.inner.ProduceQaFeatureFilesAsync(spawn, story, workstreams, ct);

        public Task<IReadOnlyDictionary<string, string>> ProduceStepDefinitionsAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, CancellationToken ct = default)
            => this.inner.ProduceStepDefinitionsAsync(spawn, workstream, ct);
#pragma warning restore CS0618

        public Task<IReadOnlyDictionary<string, string>> ProduceConflictResolutionAsync(
            StorySpawnRow spawn, WorkstreamRow workstream, IReadOnlyList<ConflictedFile> conflictedFiles,
            string baseBranch, CancellationToken ct = default)
            => this.inner.ProduceConflictResolutionAsync(spawn, workstream, conflictedFiles, baseBranch, ct);
    }

    /// <summary>
    /// Subclass of <see cref="ForgeWebApplicationFactory"/> that runs the
    /// base host configuration (so the deterministic strategy + in-memory
    /// git/provider ops register) and THEN swaps
    /// <see cref="IStoryArtifactStrategy"/> for a
    /// <see cref="ConfigurableReviewFixStrategy"/> that wraps it. The
    /// wrapper is reachable via the test factory's services so each test
    /// can mutate <see cref="ConfigurableReviewFixStrategy.Behavior"/>
    /// between <c>DriveAsync</c> calls.
    /// </summary>
    private sealed class CircuitBreakerFactory : ForgeWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStoryArtifactStrategy>();
                services.AddSingleton<DeterministicStoryArtifactStrategy>();
                services.AddSingleton<ConfigurableReviewFixStrategy>(sp =>
                    new ConfigurableReviewFixStrategy(sp.GetRequiredService<DeterministicStoryArtifactStrategy>()));
                services.AddSingleton<IStoryArtifactStrategy>(sp =>
                    sp.GetRequiredService<ConfigurableReviewFixStrategy>());
            });
        }
    }

    private static (CircuitBreakerFactory factory, HttpClient client) NewCircuitFactory()
    {
        var factory = new CircuitBreakerFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(WriterHeader, WriterValue);
        return (factory, client);
    }

    private static async Task SeedStoryAsync(ForgeWebApplicationFactory factory, string id, string title)
    {
        using var scope = factory.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
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
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
    }

    private static async Task SetPlanPrAsync(ForgeWebApplicationFactory f, string storyId, string prId, string col)
    {
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
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
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
    }

    private static async Task SeedAllPlanDocsPassedAsync(ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var spawn = await db.StorySpawns.FirstAsync(x => x.StoryId == storyId);
        foreach (var fileName in new[] { "tech-spec.md", "architecture.md", "implementation-plan.md", "e2e-scenarios.md" })
        {
            db.Deliverables.Add(new DeliverableRow
            {
                WorkItemId = $"plan-doc:{spawn.Id}:{fileName}",
                CrewId = "crew-arch",
                GeneratorAgentId = "G",
                EvaluatorAgentId = "E",
                Iter = 4,
                Score = 92,
                DimsJson = "{}",
                Verdict = "pass",
                Notes = string.Empty,
                GeneratorSummary = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await control.SaveChangesAsync(); await db.SaveChangesAsync();
    }

    /// <summary>
    /// Stand up a workstream at <c>review</c> with an OPEN PR (not merged
    /// — Phase 59's short-circuit would otherwise auto-resolve and never
    /// invoke <c>ProduceReviewFixAsync</c>) and a single active reviewer
    /// comment. Mirrors <c>Phase55ActiveCommentsGuardTests.SeedReviewWorkstreamAsync</c>.
    /// </summary>
    private static async Task<(string wsId, string prId, long commentId)> SeedOpenReviewWorkstreamAsync(
        ForgeWebApplicationFactory f, string storyId)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(WriterHeader, WriterValue);

        await c.PostAsJsonAsync($"/api/stories/{storyId}/spawn", new { repoId = (string?)null }, JsonOpts);
        await SetPlanPrAsync(f, storyId, $"pr-plan-{storyId}", "merged");
        await c.PatchAsync($"/api/stories/{storyId}/spawn/phase",
            JsonContent.Create(new { targetPhase = "decompose" }, options: JsonOpts));
        await c.PostAsJsonAsync($"/api/stories/{storyId}/spawn/decompose",
            new { workstreams = new[] { new { title = "ws1" } } }, JsonOpts);
        await c.PostAsync($"/api/stories/{storyId}/spawn/decompose/approve", null);
        await c.PatchAsync($"/api/stories/{storyId}/spawn/phase",
            JsonContent.Create(new { targetPhase = "implement" }, options: JsonOpts));

        await SeedAllPlanDocsPassedAsync(f, storyId);

        var safeId = storyId.ToLowerInvariant();
        var wsId = $"ws-{safeId}-ws1";
        var prId = $"pr-ws1-{storyId}";

        long commentId;
        using (var scope = f.Services.CreateScope())
        {
            var control = scope.ServiceProvider.GetRequiredService<ControlDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var git = scope.ServiceProvider.GetRequiredService<IStoryGitOps>();
            var ws = await db.Workstreams.FirstAsync(w => w.Id == wsId);
            ws.Status = "review";
            ws.PrsJson = JsonSerializer.Serialize(new[] { prId });

            // Register the in-memory worktree for this branch so the Phase 59
            // `TrySkipReviewFixOnStalePrAsync` gate (worktree-missing branch)
            // does NOT auto-resolve via `system:post-merge-cleanup` before our
            // Phase 90 circuit breaker can fire. Mirrors Phase 59's happy-path
            // test setup (`DriveReview_Engineer_Resolves_Comments_When_Pr_Open_And_Worktree_Live`).
            await git.EnsureBranchAsync(repo: null, branch: ws.Branch, baseBranch: "main");
            db.Prs.Add(new PrRow
            {
                Id = prId,
                Col = "open",
                IsActive = true,
                Branch = ws.Branch,
                Base = "main",
                Title = $"[impl] {ws.Name}",
                Workstream = wsId,
            });

            var row = new PrCommentRow
            {
                PrId = prId,
                ProviderCommentId = $"upstream-{Guid.NewGuid():N}",
                ThreadId = $"thread-{Guid.NewGuid():N}",
                AuthorId = "human-reviewer",
                AuthorKind = "user",
                Body = "Please refactor this method.",
                FilePath = "src/Example.cs",
                Line = 42,
                State = "active",
                RequiresAttention = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.PrComments.Add(row);
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
            commentId = row.Id;
        }

        await c.PatchAsync($"/api/stories/{storyId}/spawn/phase",
            JsonContent.Create(new { targetPhase = "review" }, options: JsonOpts));

        return (wsId, prId, commentId);
    }

    private static async Task DriveAsync(ForgeWebApplicationFactory f, string storyId)
    {
        using var scope = f.Services.CreateScope();
        var orch = scope.ServiceProvider.GetRequiredService<StorySpawnOrchestrator>();
        await orch.DriveAsync(storyId);
    }

    private static async Task<string> CommentStateAsync(ForgeWebApplicationFactory f, long commentId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var row = await db.PrComments.AsNoTracking().FirstAsync(pc => pc.Id == commentId);
        return row.State;
    }

    private static async Task<string?> CommentResolverAsync(ForgeWebApplicationFactory f, long commentId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
        var row = await db.PrComments.AsNoTracking().FirstAsync(pc => pc.Id == commentId);
        return row.ResolvedByUserId;
    }

    /// <summary>
    /// Headline regression: three back-to-back budget-exhaustion throws
    /// for the SAME (wsId, commentId) → on the 3rd, the orchestrator
    /// posts a system reply, resolves the comment with
    /// <c>ResolvedByUserId="system:review-fix-budget"</c>, and emits a
    /// <c>review-fix.budget-exhausted</c> transcript event on
    /// <c>workstream.Id</c>. This is the loop break that turned hours of
    /// $30-per-tick burns into a single $90 ceiling.
    /// </summary>
    [Fact]
    public async Task Three_Consecutive_Hard_Timeouts_Trigger_Circuit_Breaker_And_System_Resolve()
    {
        using var f = new CircuitBreakerFactory();
        // Boot host so ConfigureTestServices runs and the throwing
        // strategy is wired in.
        using (f.CreateClient()) { /* boot */ }
        await SeedStoryAsync(f, "WI-90001", "phase-90-circuit-break");
        var (wsId, _, commentId) = await SeedOpenReviewWorkstreamAsync(f, "WI-90001");

        // Strategy is configured to always throw the dispatcher's
        // hard-timeout exception (default Behavior).
        for (var i = 0; i < 3; i++)
        {
            await DriveAsync(f, "WI-90001");
        }

        // Comment must be resolved with the system resolver id.
        Assert.Equal("resolved", await CommentStateAsync(f, commentId));
        Assert.Equal(SystemResolverId, await CommentResolverAsync(f, commentId));

        // The system reply must be persisted as a child of the original
        // comment so an operator viewing the PR can see the explanation.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var replies = await db.PrComments.AsNoTracking()
                .Where(pc => pc.ParentCommentId == commentId)
                .ToListAsync();
            Assert.Single(replies);
            Assert.Equal(SystemResolverId, replies[0].AuthorId);
            Assert.Contains("budget", replies[0].Body, StringComparison.OrdinalIgnoreCase);
        }

        // The transcript event must be emitted on the WORKSTREAM id so
        // the Conversation tab's Review subtab can render it.
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProductDbContext>();
            var evt = await db.TaskTranscriptEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.WorkItemId == wsId && e.Kind == BudgetExhaustedEventKind);
            Assert.NotNull(evt);
            Assert.Contains("\"attempts\":3", evt!.PayloadJson ?? string.Empty);
            Assert.Contains(commentId.ToString(), evt.PayloadJson ?? string.Empty);
        }
    }

    /// <summary>
    /// Below-threshold throws must NOT escalate. After 1 and 2 budget
    /// throws the comment is still <c>active</c> and has no
    /// <c>ResolvedByUserId</c> stamp. The system reply branch only fires
    /// once the counter hits <c>MaxReviewFixTimeouts</c>.
    /// </summary>
    [Fact]
    public async Task Two_Consecutive_Hard_Timeouts_Do_Not_Resolve_The_Comment()
    {
        using var f = new CircuitBreakerFactory();
        using (f.CreateClient()) { /* boot */ }
        await SeedStoryAsync(f, "WI-90002", "phase-90-below-threshold");
        var (_, _, commentId) = await SeedOpenReviewWorkstreamAsync(f, "WI-90002");

        for (var i = 0; i < 2; i++)
        {
            await DriveAsync(f, "WI-90002");
        }

        Assert.Equal("active", await CommentStateAsync(f, commentId));
        Assert.Null(await CommentResolverAsync(f, commentId));
    }

    /// <summary>
    /// "Exceeded ... budget" message variant (alternate dispatcher
    /// failure shape — same root cause as "hard timeout" but different
    /// surface text) must ALSO count toward the circuit breaker. Both
    /// substrings must co-occur in <c>ex.Message</c>; matching only one
    /// is insufficient.
    /// </summary>
    [Fact]
    public async Task Exceeded_Budget_Message_Variant_Also_Triggers_Circuit_Breaker()
    {
        using var f = new CircuitBreakerFactory();
        using (f.CreateClient()) { /* boot */ }
        await SeedStoryAsync(f, "WI-90003", "phase-90-budget-variant");
        var (_, _, commentId) = await SeedOpenReviewWorkstreamAsync(f, "WI-90003");

        // Swap to the "exceeded ... budget" message shape.
        var strategy = f.Services.GetRequiredService<ConfigurableReviewFixStrategy>();
        strategy.Behavior = _ => throw new InvalidOperationException(
            "Engineer agent exceeded its 8-minute wall-clock budget without writing fix-result.json.");

        for (var i = 0; i < 3; i++)
        {
            await DriveAsync(f, "WI-90003");
        }

        Assert.Equal("resolved", await CommentStateAsync(f, commentId));
        Assert.Equal(SystemResolverId, await CommentResolverAsync(f, commentId));
    }

    /// <summary>
    /// Non-budget exceptions must NOT count toward the circuit-breaker
    /// counter. A commit-failure or comment-post 5xx (legacy
    /// "log + retry next tick" path) is typically transient and
    /// self-heals; classifying it as a hard timeout would surface
    /// spurious system resolves on every flaky network blip.
    /// </summary>
    [Fact]
    public async Task Non_Budget_Exception_Does_Not_Trigger_Circuit_Breaker()
    {
        using var f = new CircuitBreakerFactory();
        using (f.CreateClient()) { /* boot */ }
        await SeedStoryAsync(f, "WI-90004", "phase-90-non-budget");
        var (_, _, commentId) = await SeedOpenReviewWorkstreamAsync(f, "WI-90004");

        var strategy = f.Services.GetRequiredService<ConfigurableReviewFixStrategy>();
        strategy.Behavior = _ => throw new InvalidOperationException(
            "git: commit failed with exit code 128 (cannot lock ref)");

        for (var i = 0; i < 5; i++)
        {
            await DriveAsync(f, "WI-90004");
        }

        // Comment must remain active — the legacy log-and-retry path is
        // responsible for this failure shape, not the new circuit
        // breaker.
        Assert.Equal("active", await CommentStateAsync(f, commentId));
        Assert.Null(await CommentResolverAsync(f, commentId));
    }

    /// <summary>
    /// Counter must clear on a successful fix so subsequent comments on
    /// the same workstream don't inherit stale failure counts. We
    /// simulate this by: 2× throws → 1× success → 2× throws. If the
    /// counter did not reset, a 4th total budget throw would tip the
    /// counter to 3 and trigger escalation; the test asserts the
    /// comment is NOT auto-resolved after the second cycle.
    /// </summary>
    [Fact]
    public async Task Successful_Fix_Resets_Counter_So_Subsequent_Timeouts_Restart_From_One()
    {
        using var f = new CircuitBreakerFactory();
        using (f.CreateClient()) { /* boot */ }
        await SeedStoryAsync(f, "WI-90005", "phase-90-counter-reset");
        var (_, _, commentId) = await SeedOpenReviewWorkstreamAsync(f, "WI-90005");

        var strategy = f.Services.GetRequiredService<ConfigurableReviewFixStrategy>();

        // Throws 1 & 2 — counter -> 2 (below threshold).
        for (var i = 0; i < 2; i++)
        {
            await DriveAsync(f, "WI-90005");
        }
        Assert.Equal("active", await CommentStateAsync(f, commentId));

        // Switch to a successful fix on the next call. ReplyOnly=true
        // avoids the orchestrator's commit path (in-memory git ops
        // accept it). The orchestrator's successful-fix branch will
        // post a reply, resolve the comment with the engineer id, and
        // CLEAR the counter for (wsId, commentId).
        strategy.Behavior = _ => Task.FromResult(new ReviewFix(
            FilePath: null,
            NewContent: null,
            ReplyBody: "Done — refactored the method."));
        await DriveAsync(f, "WI-90005");

        // Comment must now be resolved by the engineer (NOT the system
        // resolver), proving the success path ran and cleared the
        // counter rather than the circuit-breaker firing.
        Assert.Equal("resolved", await CommentStateAsync(f, commentId));
        var resolvedBy = await CommentResolverAsync(f, commentId);
        Assert.NotNull(resolvedBy);
        Assert.NotEqual(SystemResolverId, resolvedBy);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "forge-phase90-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupTempDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; tests must not fail on disk noise.
        }
    }
}
