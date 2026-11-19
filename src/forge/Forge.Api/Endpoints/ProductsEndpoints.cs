// -----------------------------------------------------------------------
// <copyright file="ProductsEndpoints.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Endpoints;

using Forge.Api.Auth;
using Forge.Api.Contracts;
using Forge.Api.Domain;
using Forge.Api.Persistence;
using Forge.Api.Providers;
using Forge.Api.Realtime;
using Forge.Api.Services;
using Forge.Api.Services.Git;
using Forge.Api.Services.Stories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase 8.4 — products + repos CRUD.
///
/// Q5 invariant (1:N) is enforced both at the DB layer (UNIQUE indexes on
/// repos.RemoteUrl / repos.CloneRoot) and here at the API layer with explicit
/// 409 responses that include the conflicting product / repo id so the UI can
/// offer "open existing product" as a recovery action.
/// </summary>
public static class ProductsEndpoints
{
    public static WebApplication MapProductsEndpoints(this WebApplication app)
    {
        var products = app.MapGroup("/api/products").WithTags("Products");
        products.MapGet("/", ListProducts).WithName("ListProducts");
        products.MapGet("/{id}", GetProduct).WithName("GetProduct");
        products.MapPost("/", CreateProduct).WithName("CreateProduct");
        products.MapPatch("/{id}", PatchProduct).WithName("PatchProduct");
        products.MapDelete("/{id}", DeleteProduct).WithName("DeleteProduct");
        products.MapGet("/{id}/repos", ListReposForProduct).WithName("ListProductRepos");
        products.MapPost("/{id}/repos", AttachRepo).WithName("AttachRepo");
        products.MapGet("/{id}/stats", GetProductStats).WithName("GetProductStats");

        var repos = app.MapGroup("/api/repos").WithTags("Repos");
        repos.MapGet("/", ListAllRepos).WithName("ListAllRepos");
        repos.MapGet("/{id}", GetRepo).WithName("GetRepo");
        repos.MapPatch("/{id}", PatchRepo).WithName("PatchRepo");
        repos.MapPost("/{id}/clone", CloneRepo).WithName("CloneRepo");
        repos.MapDelete("/{id}", DeleteRepo).WithName("DeleteRepo");

        // Phase 23 — scheduled sync endpoints.
        repos.MapPost("/{id}/sync-work-items", TriggerRepoSync).WithName("TriggerRepoSync");
        repos.MapGet("/{id}/sync-work-items/runs", GetRepoSyncRuns).WithName("GetRepoSyncRuns");
        repos.MapGet("/{id}/sync-work-items/runs/{runId}/items", GetSyncRunItems).WithName("GetSyncRunItems");
        repos.MapGet("/{id}/sync-work-items/runs/{runId}/items/{entityKind}/{entityId}/detail", GetSyncRunItemDetail).WithName("GetSyncRunItemDetail");
        repos.MapGet("/{id}/sync-work-items/status", GetRepoSyncStatus).WithName("GetRepoSyncStatus");
        repos.MapPost("/{id}/clear-sync-error", ClearSyncError).WithName("ClearRepoSyncError");

        // Phase 26/29 — build pipeline detection, validation & history.
        repos.MapPost("/{id}/detect-build", DetectBuild).WithName("DetectBuild");
        repos.MapPost("/{id}/validate-build", ValidateBuild).WithName("ValidateBuild");
        repos.MapGet("/{id}/validation-runs", GetValidationRuns).WithName("GetValidationRuns");
        repos.MapPost("/{id}/validation-runs/{runId}/cancel", CancelValidationRun).WithName("CancelValidationRun");
        repos.MapDelete("/{id}/validation-runs", ClearValidationRuns).WithName("ClearValidationRuns");
        repos.MapDelete("/{id}/sync-work-items/runs", ClearSyncRuns).WithName("ClearSyncRuns");

        // Phase 59 Stage A — dry-run the impacted-test resolver against
        // the repo's clone root + default branch so the operator can
        // sanity-check the matched test-project list before saving the
        // checkbox.
        repos.MapGet("/{id}/test-impacted-preview", PreviewImpactedTests).WithName("PreviewImpactedTests");

        return app;
    }

    // PATCH /api/repos/{id} — Phase 10 per-repo override editor.
    private static async Task<IResult> PatchRepo(
        HttpContext ctx,
        string id,
        [FromBody] UpdateRepoRequest req,
        ControlDbContext control,
        Forge.Api.Persistence.IProductDbContextFactory factory,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var row = await db.Repos.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        if (req.DisplayName is not null) row.DisplayName = req.DisplayName.Trim();
        if (req.CloneRoot is not null)
        {
            var clean = req.CloneRoot.Trim();
            if (!Path.IsPathFullyQualified(clean))
            {
                return Results.Problem(title: "cloneRoot must be an absolute path", statusCode: 400);
            }
            // Q2 — same clone root cannot host two repos (GLOBAL — checked
            // against the control registry, the cross-product repo index).
            if (await control.RepoRegistry.AnyAsync(r => r.CloneRoot == clean && r.Id != id && r.DeletedAt == null))
            {
                return Results.Problem(title: "Clone root already in use", statusCode: 409);
            }
            row.CloneRoot = clean;
        }
        if (req.DefaultBranch is not null) row.DefaultBranch = string.IsNullOrWhiteSpace(req.DefaultBranch) ? "main" : req.DefaultBranch.Trim();
        if (req.AdoOrg is not null)     row.AdoOrg     = NullIfEmpty(req.AdoOrg);
        if (req.AdoProject is not null) row.AdoProject = NullIfEmpty(req.AdoProject);
        if (req.GhOwner is not null)    row.GhOwner    = NullIfEmpty(req.GhOwner);
        if (req.GhRepo is not null)     row.GhRepo     = NullIfEmpty(req.GhRepo);

        // Phase 23 — area-path scope + agent-ready default team.
        if (req.AreaPaths is not null)
        {
            row.AreaPathsJson = req.AreaPaths.Count == 0
                ? null
                : System.Text.Json.JsonSerializer.Serialize(req.AreaPaths);
        }
        if (req.AgentReadyDefaultTeam is not null)
        {
            row.AgentReadyDefaultTeam = NullIfEmpty(req.AgentReadyDefaultTeam);
        }

        // Phase 26/29 — build pipeline gate configuration.
        if (req.InitCommand is not null)
            row.InitCommand = NullIfEmpty(req.InitCommand);
        if (req.RestoreCommand is not null)
            row.RestoreCommand = NullIfEmpty(req.RestoreCommand);
        if (req.BuildCommand is not null)
            row.BuildCommand = NullIfEmpty(req.BuildCommand);
        if (req.TestCommand is not null)
            row.TestCommand = NullIfEmpty(req.TestCommand);
        if (req.BuildTimeoutSec is not null)
            row.BuildTimeoutSec = req.BuildTimeoutSec > 0 ? req.BuildTimeoutSec : null;
        if (req.TestTimeoutSec is not null)
            row.TestTimeoutSec = req.TestTimeoutSec > 0 ? req.TestTimeoutSec : null;
        // Phase 59 Stage A — per-repo override for impacted-test
        // selection. Tri-state: null clears the override (inherit
        // global Forge:Test:ImpactedOnly); true / false pin the
        // behaviour for this repo. The request DTO's bool? naturally
        // round-trips JSON null vs. true vs. false.
        if (req.TestImpactedOnly is { } testImpactedOnly)
            row.TestImpactedOnly = testImpactedOnly;

        // Per-repo build/test elevation requirement. true/false pin it; the
        // request DTO's bool? round-trips JSON null (leave unchanged) vs. the
        // explicit value. The validation worker fails fast if this is true but
        // Forge.Api is not elevated.
        if (req.RunTestsElevated is { } runTestsElevated)
            row.RunTestsElevated = runTestsElevated;

        // Phase 94 — per-repo qa-e2e test-style override. true / false pin
        // BDD/Gherkin vs plain idiomatic test methods for this repo; the
        // request DTO's bool? round-trips JSON null (leave unchanged) vs. the
        // explicit value. Null on the row inherits the global
        // Forge:Stories:E2eUseBddPattern default (true).
        if (req.E2eUseBddPattern is { } e2eUseBddPattern)
            row.E2eUseBddPattern = e2eUseBddPattern;

        // Phase 83 — per-repo plan-doc base dir. Empty string clears the
        // override (inherit the default docs/stories); any other value is
        // normalized (forward slashes, no leading/trailing slash, no
        // traversal) so an absolute / `..` path can't escape the repo when
        // fed into a commit path.
        if (req.PlanDocBaseDir is not null)
            row.PlanDocBaseDir = string.IsNullOrWhiteSpace(req.PlanDocBaseDir)
                ? null
                : Forge.Api.Services.Stories.StoryPaths.NormalizePlanDocBaseDir(req.PlanDocBaseDir);

        // Phase 33 — persisted sync settings.
        if (req.WiSyncAssignedTo is not null)
            row.WiSyncAssignedTo = NullIfEmpty(req.WiSyncAssignedTo);
        if (req.WiSyncDateFromDays is not null)
            row.WiSyncDateFromDays = req.WiSyncDateFromDays > 0 ? req.WiSyncDateFromDays : null;
        if (req.WiSyncFromDate is not null)
            row.WiSyncFromDate = NullIfEmpty(req.WiSyncFromDate);
        if (req.WiSyncToDate is not null)
            row.WiSyncToDate = NullIfEmpty(req.WiSyncToDate);
        if (req.PrSyncCreatedBy is not null)
            row.PrSyncCreatedBy = NullIfEmpty(req.PrSyncCreatedBy);
        if (req.PrSyncStatus is not null)
            row.PrSyncStatus = NullIfEmpty(req.PrSyncStatus);
        if (req.BuildPipelineIds is not null)
            row.BuildPipelineIdsJson = req.BuildPipelineIds.Count == 0
                ? null
                : System.Text.Json.JsonSerializer.Serialize(req.BuildPipelineIds);

        // Phase 34 — per-repo sync cadence overrides. Zero / negative clears
        // the override and falls back to the global default.
        if (req.WorkItemSyncIntervalSeconds is not null)
            row.WorkItemSyncIntervalSeconds = req.WorkItemSyncIntervalSeconds > 0 ? req.WorkItemSyncIntervalSeconds : null;
        if (req.PrSyncIntervalSeconds is not null)
            row.PrSyncIntervalSeconds = req.PrSyncIntervalSeconds > 0 ? req.PrSyncIntervalSeconds : null;
        if (req.BuildSyncIntervalSeconds is not null)
            row.BuildSyncIntervalSeconds = req.BuildSyncIntervalSeconds > 0 ? req.BuildSyncIntervalSeconds : null;

        // Phase 26 — auto-detect build/test commands when cloneRoot changes
        // and the new directory already exists on disk.
        if (req.CloneRoot is not null && Directory.Exists(row.CloneRoot))
        {
            var detected = BuildSystemDetector.Detect(row.CloneRoot);
            BuildSystemDetector.MergeInto(row, detected);
        }

        // Phase 80.B — keep the control registry mirror in lock-step with the
        // fields it carries (display name, clone root, cadence overrides).
        var reg = await control.RepoRegistry.FirstOrDefaultAsync(r => r.Id == id);
        if (reg is not null)
        {
            reg.DisplayName = row.DisplayName;
            reg.CloneRoot = row.CloneRoot;
            reg.WorkItemSyncIntervalSeconds = row.WorkItemSyncIntervalSeconds;
            reg.PrSyncIntervalSeconds = row.PrSyncIntervalSeconds;
            reg.BuildSyncIntervalSeconds = row.BuildSyncIntervalSeconds;
        }

        await control.SaveChangesAsync(); await db.SaveChangesAsync();
        var repo = MapRepo(row);
        await hub.Clients.All.SendAsync("repo.changed", new { kind = "patched", repo });
        return Results.Ok(repo);
    }

    // POST /api/repos/{id}/clear-sync-error — operator-acknowledge a failed
    // sync. Resets LastSyncStatus → "idle" and clears LastSyncError on both
    // the product-DB row and the control-registry mirror, without running a
    // sync. The sync cursor (LastSyncCursor / LastUpstreamChangedDate) is
    // preserved so the next sync resumes where it left off. Useful when the
    // failure is known (e.g. a too-broad WIQL) and the operator wants to silence
    // the red indicator until they fix the filters.
    private static async Task<IResult> ClearSyncError(
        HttpContext ctx,
        string id,
        ControlDbContext control,
        Forge.Api.Persistence.IProductDbContextFactory factory,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var row = await db.Repos.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        // Only meaningful when not mid-run; if a sync is currently running, leave
        // it alone (the run will set the terminal state itself).
        if (string.Equals(row.LastSyncStatus, "running", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(title: "A sync is currently running for this repo; wait for it to finish.", statusCode: 409);
        }

        row.LastSyncStatus = "idle";
        row.LastSyncError = null;

        var reg = await control.RepoRegistry.FirstOrDefaultAsync(r => r.Id == id);
        if (reg is not null)
        {
            reg.LastSyncStatus = "idle";
            reg.LastSyncError = null;
        }

        await control.SaveChangesAsync(); await db.SaveChangesAsync();
        var repo = MapRepo(row);
        await hub.Clients.All.SendAsync("repo.changed", new { kind = "sync-error-cleared", repo });
        return Results.Ok(repo);
    }

    // POST /api/repos/{id}/clone — ensure the repo's cloneRoot is checked out.
    // Used by the Products view "Edit repo" modal so that updating the local
    // path triggers an automatic clone when the directory is missing.
    //
    // If the cloneRoot directory already exists we trust the operator's choice
    // and skip — no clone, no remote-URL match check. Workstream creation
    // (WorkstreamsEndpoints.Create) still uses the stricter EnsureClonedAsync
    // path that validates the existing checkout.
    private static async Task<IResult> CloneRepo(
        HttpContext ctx,
        string id,
        ControlDbContext control,
        Forge.Api.Persistence.IProductDbContextFactory factory,
        IRepoCloneService cloneSvc,
        IHubContext<ForgeHub> hub,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var row = await db.Repos.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        if (Directory.Exists(row.CloneRoot))
        {
            // Phase 26 — still run detection even if already cloned (operator may
            // have manually cloned without going through Forge).
            var det = BuildSystemDetector.Detect(row.CloneRoot);
            if (BuildSystemDetector.MergeInto(row, det))
            {
                await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
                await hub.Clients.All.SendAsync("repo.changed", new { kind = "build-detected", repo = MapRepo(row) }, ct);
            }
            return Results.Ok(new { outcome = "skipped", message = "cloneRoot already exists", detectedBuild = MapDetected(det) });
        }

        var result = await cloneSvc.EnsureClonedAsync(row, ct);

        // Phase 26 — auto-detect after successful clone.
        BuildSystemDetector.DetectedCommands? detected = null;
        if (result.Outcome == RepoCloneOutcome.Cloned && Directory.Exists(row.CloneRoot))
        {
            detected = BuildSystemDetector.Detect(row.CloneRoot);
            if (BuildSystemDetector.MergeInto(row, detected))
            {
                await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
                await hub.Clients.All.SendAsync("repo.changed", new { kind = "build-detected", repo = MapRepo(row) }, ct);
            }
        }

        return result.Outcome switch
        {
            RepoCloneOutcome.Cloned => Results.Ok(new { outcome = "cloned", message = result.Message, detectedBuild = MapDetected(detected) }),
            RepoCloneOutcome.Skipped => Results.Ok(new { outcome = "skipped", message = result.Message }),
            RepoCloneOutcome.Mismatched => Results.Problem(title: "Repo cloneRoot mismatch", detail: result.Message, statusCode: 409),
            RepoCloneOutcome.Failed => Results.Problem(title: "Clone failed", detail: result.Message, statusCode: 502),
            _ => Results.Problem(title: "Unknown clone outcome", statusCode: 500),
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Phase 80 — resolve a repo's OWN product DB from the control registry,
    /// independent of the request's active product. A repo action
    /// (<c>/api/repos/{id}/...</c>) targets a globally-unique repo id and must
    /// work regardless of which product is active in the UI — the Edit-repo
    /// modal can target a repo in any product. Resolving via the request's
    /// active-product <c>ProductDbContext</c> 404s when the edited repo lives
    /// in a different product (observed: clone/detect/validate on the `update`
    /// repo while the active product was `code-intelligence`). Returns null
    /// when the repo isn't in the registry (caller returns 404).
    /// </summary>
    private static async Task<ProductDbContext?> ResolveRepoDbAsync(
        string repoId, ControlDbContext control,
        Forge.Api.Persistence.IProductDbContextFactory factory, CancellationToken ct = default)
    {
        var reg = await control.RepoRegistry.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == repoId && r.DeletedAt == null, ct);
        if (reg is null || string.IsNullOrEmpty(reg.ProductSlug)) return null;
        return factory.CreateFor(reg.ProductSlug);
    }

    // ── Phase 26 — build/test detection & validation ─────────────────────

    /// <summary>
    /// POST /api/repos/{id}/detect-build — scan the repo root for known
    /// build markers and return the detected commands WITHOUT saving.
    /// The UI shows these to the operator who can accept or override.
    /// </summary>
    private static async Task<IResult> DetectBuild(
        HttpContext ctx, string id, ControlDbContext control,
        Forge.Api.Persistence.IProductDbContextFactory factory, CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var row = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);
        if (string.IsNullOrEmpty(row.CloneRoot) || !Directory.Exists(row.CloneRoot))
            return Results.Problem(title: "Repo has no local clone", detail: "Set a cloneRoot and clone the repo first.", statusCode: 422);

        var detected = BuildSystemDetector.Detect(row.CloneRoot);
        return Results.Ok(new
        {
            detected.Ecosystem,
            detected.InitCommand,
            detected.RestoreCommand,
            detected.BuildCommand,
            detected.TestCommand,
            detected.Ambiguous,
            detected.AmbiguityDetail,
            current = new
            {
                row.InitCommand,
                row.RestoreCommand,
                row.BuildCommand,
                row.TestCommand,
                row.BuildTimeoutSec,
                row.TestTimeoutSec,
            },
        });
    }

    /// <summary>
    /// Phase 59 Stage A — GET /api/repos/{id}/test-impacted-preview.
    /// Dry-runs the impacted-test resolver against the repo's clone
    /// root + default branch so the operator can sanity-check the
    /// matched test-project set before saving the
    /// <c>TestImpactedOnly</c> checkbox. Read-only: never writes the
    /// row, never invokes the test command. The actual gate path
    /// re-runs the resolver inside the workstream's worktree at gate
    /// time — this preview is "what would happen if I ran the gate
    /// against HEAD right now?".
    /// </summary>
    private static async Task<IResult> PreviewImpactedTests(
        string id, ControlDbContext control, Forge.Api.Persistence.RepoLookup repoLookup, CancellationToken ct)
    {
        var row = await repoLookup.GetByIdAsync(id, ct);
        if (row is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);
        if (string.IsNullOrEmpty(row.CloneRoot) || !Directory.Exists(row.CloneRoot))
            return Results.Problem(
                title: "Repo has no local clone",
                detail: "Clone the repo first; the resolver needs a worktree to enumerate csproj files and diff against the base branch.",
                statusCode: 422);

        var testCommand = string.IsNullOrWhiteSpace(row.TestCommand) ? "dotnet test" : row.TestCommand;
        var baseRef = string.IsNullOrWhiteSpace(row.DefaultBranch) ? "main" : row.DefaultBranch;
        var changedFiles = await Services.Stories.ImpactedTestResolver.DiscoverChangedFilesAsync(
            row.CloneRoot, baseRef, ct);
        var resolution = Services.Stories.ImpactedTestResolver.Resolve(
            workspace: row.CloneRoot,
            changedFiles: changedFiles,
            originalCommand: testCommand,
            impactedOnly: true);

        return Results.Ok(new
        {
            repoId = row.Id,
            cloneRoot = row.CloneRoot,
            baseRef,
            testCommand,
            changedFileCount = changedFiles.Count,
            resolution.Rewritten,
            resolution.Skipped,
            resolution.WholeSuiteFallback,
            resolution.Command,
            resolution.ImpactedProjects,
            resolution.AllTestProjectCount,
            resolution.Reason,
        });
    }

    /// <summary>
    /// POST /api/repos/{id}/validate-build — enqueues an async validation run
    /// (init → restore → build → test). Returns 202 with a run ID.
    /// Completion is notified via SignalR <c>repo.validation-complete</c>.
    /// </summary>
    private static async Task<IResult> ValidateBuild(
        HttpContext ctx, string id, ControlDbContext control, Forge.Api.Persistence.IProductDbContextFactory factory, BuildValidationWorker worker, CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var row = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);
        if (string.IsNullOrEmpty(row.CloneRoot) || !Directory.Exists(row.CloneRoot))
            return Results.Problem(title: "Repo has no local clone", statusCode: 422);

        var runId = Guid.NewGuid().ToString("N")[..12];
        var triggeredBy = Forge.Api.Auth.ForgeUser.UserId(ctx);

        // Reclaim ORPHANED runs first. A queued/running row that this worker
        // instance isn't actually tracking was left behind by a server
        // restart (the in-memory job channel is lost on restart, but the DB
        // row survives). Such a row would otherwise (a) make every future
        // validate-build return a false "already running" 409 forever and
        // (b) sit at "Preparing pipeline…" with no log because the worker
        // never picks it up. Mark it failed so this repo can validate again.
        // A genuinely in-flight run IS tracked (worker.IsRepoActive) and is
        // preserved by the 409 below.
        if (!worker.IsRepoActive(id))
        {
            var orphans = await db.BuildValidationRuns
                .Where(r => r.RepoId == id && (r.Status == "queued" || r.Status == "running"))
                .ToListAsync(ct);
            if (orphans.Count > 0)
            {
                var now = DateTime.UtcNow.ToString("O");
                foreach (var o in orphans)
                {
                    o.Status = "failed";
                    o.FinishedAt = now;
                    o.ErrorMessage = "Interrupted by a server restart (in-memory job queue lost); superseded by a new run.";
                }
                await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
            }
        }

        // Check for active run BEFORE persisting to avoid orphaned "queued" rows.
        var hasActive = await db.BuildValidationRuns
            .AnyAsync(r => r.RepoId == id && (r.Status == "queued" || r.Status == "running"), ct);
        if (hasActive)
        {
            return Results.Problem(
                title: "A validation is already running for this repo",
                statusCode: 409);
        }

        var run = new Persistence.BuildValidationRunRow
        {
            Id = runId,
            RepoId = id,
            Status = "queued",
            StartedAt = DateTime.UtcNow.ToString("O"),
            TriggeredBy = triggeredBy,
        };
        db.BuildValidationRuns.Add(run);
        await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);

        var job = new BuildValidationJob(runId, id);
        if (!worker.TryEnqueue(job))
        {
            // Race: another request got in between our check and enqueue.
            // Mark the orphan as cancelled so it doesn't pollute history.
            run.Status = "cancelled";
            run.FinishedAt = DateTime.UtcNow.ToString("O");
            run.ErrorMessage = "Superseded by concurrent request";
            await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
            return Results.Problem(
                title: "A validation is already running for this repo",
                statusCode: 409);
        }

        return Results.Accepted(value: new { runId, status = "queued" });
    }

    /// <summary>
    /// GET /api/repos/{id}/validation-runs — returns the most recent
    /// validation runs for the repo, ordered by start time descending.
    /// </summary>
    private static async Task<IResult> GetValidationRuns(
        HttpContext ctx, string id, ControlDbContext control, Forge.Api.Persistence.IProductDbContextFactory factory, CancellationToken ct)
    {
        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var row = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        // StartedAt is stored as ISO-8601 string, so lexicographic sort = chronological.
        var runs = await db.BuildValidationRuns
            .AsNoTracking()
            .Where(r => r.RepoId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(20)
            .Select(r => new
            {
                r.Id,
                r.RepoId,
                r.Status,
                r.StartedAt,
                r.FinishedAt,
                r.AllPassed,
                r.Ecosystem,
                r.StepsJson,
                r.TriggeredBy,
                r.ErrorMessage,
            })
            .ToListAsync(ct);

        return Results.Ok(runs);
    }

    /// <summary>
    /// POST /api/repos/{id}/validation-runs/{runId}/cancel — cancel a running validation.
    /// </summary>
    private static async Task<IResult> CancelValidationRun(
        HttpContext ctx, string id, string runId, ControlDbContext control, Forge.Api.Persistence.IProductDbContextFactory factory, BuildValidationWorker worker, CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var run = await db.BuildValidationRuns.FirstOrDefaultAsync(r => r.Id == runId && r.RepoId == id, ct);
        if (run is null) return Results.Problem(title: "Validation run not found", statusCode: 404);

        if (run.Status is not ("queued" or "running"))
            return Results.Problem(title: $"Run is already {run.Status}", statusCode: 409);

        // Signal the worker to cancel.
        worker.TryCancel(runId);

        // If still queued (not yet picked up by worker), mark cancelled directly.
        if (run.Status == "queued")
        {
            run.Status = "cancelled";
            run.FinishedAt = DateTime.UtcNow.ToString("O");
            run.ErrorMessage = "Cancelled by user";
            await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new { runId, status = "cancelled" });
    }

    /// <summary>DELETE /api/repos/{id}/validation-runs — clear all completed validation runs.</summary>
    private static async Task<IResult> ClearValidationRuns(
        HttpContext ctx, string id, ControlDbContext control, Forge.Api.Persistence.IProductDbContextFactory factory, CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var completed = await db.BuildValidationRuns
            .Where(r => r.RepoId == id && r.Status != "queued" && r.Status != "running")
            .ToListAsync(ct);
        db.BuildValidationRuns.RemoveRange(completed);
        await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
        return Results.Ok(new { deleted = completed.Count });
    }

    /// <summary>DELETE /api/repos/{id}/sync-work-items/runs — clear all sync run history.</summary>
    private static async Task<IResult> ClearSyncRuns(
        HttpContext ctx, string id, ControlDbContext control, Forge.Api.Persistence.IProductDbContextFactory factory, CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        // Phase 80 — resolve the per-product DB via repo_registry so this works
        // regardless of the active X-Forge-Product-Id scope (a cross-product
        // Products & Repos page polls every repo, not just the active product's).
        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        // Delete run items first (child rows), then runs.
        var runIds = await db.WorkItemSyncRuns
            .Where(r => r.RepoId == id)
            .Select(r => r.Id)
            .ToListAsync(ct);
        if (runIds.Count > 0)
        {
            var items = await db.SyncRunItems
                .Where(i => runIds.Contains(i.RunId))
                .ToListAsync(ct);
            db.SyncRunItems.RemoveRange(items);
            var runs = await db.WorkItemSyncRuns
                .Where(r => r.RepoId == id)
                .ToListAsync(ct);
            db.WorkItemSyncRuns.RemoveRange(runs);
            await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new { deleted = runIds.Count });
    }

    private static object? MapDetected(BuildSystemDetector.DetectedCommands? d) =>
        d is null ? null : new
        {
            d.Ecosystem, d.BuildCommand, d.TestCommand,
            d.RestoreCommand, d.Ambiguous, d.AmbiguityDetail,
        };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : "…" + s[^max..];

    // ----- /api/products ---------------------------------------------------

    private static async Task<IResult> ListProducts(ControlDbContext control, ForgeActionContext action, ForgeCapabilityAuthorizer authorizer)
    {
        var ids = (await authorizer.ReadableProductIdsAsync(await action.ActorAsync())).ToList();
        if (ids.Count == 0) return Results.Ok(Array.Empty<object>());
        return Results.Ok((await control.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).OrderBy(p => p.Sort).ThenBy(p => p.Name).ToListAsync())
            .Select(MapProduct).ToList());
    }

    private static async Task<IResult> GetProduct(string id, ControlDbContext control, ProductDbContext db)
    {
        var row = await control.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        return row is null
            ? Results.Problem(title: $"Product {id} not found", statusCode: 404)
            : Results.Ok(MapProduct(row));
    }

    private static async Task<IResult> CreateProduct(
        HttpContext ctx,
        [FromBody] CreateProductRequest req,
        ControlDbContext control,
        ProductDbContext db,
        ForgeActionContext action,
        Forge.Api.Persistence.Split.ProductDbProvisioner provisioner,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        if (req is null || string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.Problem(title: "id and name are required", statusCode: 400);
        }

        if (await control.Products.AnyAsync(p => p.Id == req.Id))
        {
            return Results.Problem(title: $"Product {req.Id} already exists", statusCode: 409);
        }

        var slug = string.IsNullOrWhiteSpace(req.Slug) ? Slugify(req.Name) : req.Slug;

        // Phase A — server-side validation. The per-product DB file is
        // named `{folder}\{slug}.db`, so slug must be a valid filename
        // and must not collide with the control DB (`forge.db`) or
        // Windows device names (CON, NUL, …). The UI is expected to
        // pre-validate but we belt-and-suspenders here so a hand-rolled
        // POST cannot create a product that later fails to open.
        if (!ForgeDbPaths.TryValidateSlug(slug, out var slugError))
        {
            return Results.Problem(
                title: "Invalid product slug",
                detail: slugError,
                statusCode: 400);
        }

        if (await control.Products.AnyAsync(p => p.Slug == slug))
        {
            return Results.Problem(title: $"Slug '{slug}' is already in use", statusCode: 409);
        }

        // On a split host every product owns {slug}.db. Create it before the
        // product row exists, so no request can route the new product to the
        // control DB fallback, and before anything is tracked on `control`:
        // later pipeline steps save that context, so an early return must
        // leave it clean. The file is dropped again if the row fails to save.
        var provisioned = await provisioner.ProvisionAsync(slug, ctx.RequestAborted);
        if (provisioned.ConflictPath is not null)
        {
            return Results.Problem(
                title: $"A database file for slug '{slug}' already exists",
                detail: $"'{provisioned.ConflictPath}' is not attached to any product. Move it aside or choose another slug.",
                statusCode: 409);
        }

        var maxSort = await control.Products.Select(p => (int?)p.Sort).MaxAsync() ?? -1;
        var row = new ProductRow
        {
            Id = req.Id,
            Sort = maxSort + 1,
            Name = req.Name,
            Slug = slug,
            Description = req.Description ?? string.Empty,
            OwnerUserId = req.OwnerUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        control.Products.Add(row);

        // Product reads require an explicit user_product_roles grant (see
        // ForgeCapabilityAuthorizer.ReadableProductIdsAsync). Without one the
        // creator could not see the product they just made, so the creating
        // operator becomes its product owner in the same save.
        var creator = await action.ActorAsync();
        if (creator is not null
            && creator.UserId != RequireRole.BuiltInProductOwnerId
            && await control.Users.AnyAsync(u => u.Id == creator.UserId))
        {
            control.UserProductRoles.Add(new UserProductRoleRow
            {
                UserId = creator.UserId,
                ProductId = row.Id,
                Role = RequireRole.ProductOwner,
                GrantedBy = creator.UserId,
            });
        }

        try
        {
            await control.SaveChangesAsync(); await db.SaveChangesAsync();
        }
        catch
        {
            if (provisioned.CreatedPath is not null) provisioner.Discard(provisioned.CreatedPath);
            throw;
        }

        var product = MapProduct(row);
        await hub.Clients.All.SendAsync("product.changed", new { kind = "created", product });
        return Results.Created($"/api/products/{row.Id}", product);
    }

    private static async Task<IResult> PatchProduct(
        HttpContext ctx,
        string id,
        [FromBody] UpdateProductRequest req,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await control.Products.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return Results.Problem(title: $"Product {id} not found", statusCode: 404);

        if (req.Name is not null) row.Name = req.Name;
        if (req.Description is not null) row.Description = req.Description;
        if (req.OwnerUserId is not null) row.OwnerUserId = req.OwnerUserId;
        // Phase 15.6 — explicit override for the active iteration. An empty
        // string is treated as "clear the override"; null leaves it unchanged.
        if (req.ActiveIterationId is not null)
        {
            var v = req.ActiveIterationId.Length == 0 ? null : req.ActiveIterationId;
            if (v is not null && !await db.ProductIterations.AnyAsync(i => i.Id == v && i.ProductId == row.Id))
            {
                return Results.Problem(title: $"Iteration {v} is not in product {row.Id}", statusCode: 400);
            }
            row.ActiveIterationId = v;
        }

        await control.SaveChangesAsync(); await db.SaveChangesAsync();
        var product = MapProduct(row);
        await hub.Clients.All.SendAsync("product.changed", new { kind = "patched", product });
        return Results.Ok(product);
    }

    private static async Task<IResult> DeleteProduct(
        HttpContext ctx,
        string id,
        bool? force,
        ControlDbContext control,
        ProductDbContext db,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var row = await control.Products.FirstOrDefaultAsync(p => p.Id == id);
        if (row is null) return Results.Problem(title: $"Product {id} not found", statusCode: 404);

        var repoCount = await control.RepoRegistry.CountAsync(r => r.ProductId == id && r.DeletedAt == null);
        if (repoCount > 0 && force != true)
        {
            return Results.Json(
                new
                {
                    requireConfirm = true,
                    title = "Delete product with attached repos?",
                    detail = $"Product {id} has {repoCount} repo(s). Re-call with ?force=true to delete the product and detach the repos.",
                },
                statusCode: 422);
        }

        // Cascade delete (FK with OnDelete.Cascade) removes repos automatically.
        control.Products.Remove(row);
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        await ctx.RequestServices.GetRequiredService<Forge.Api.Services.Chat.ChatScopeLifecycleService>().RetireProductAsync(id, ctx.RequestAborted);

        await hub.Clients.All.SendAsync("product.changed", new { kind = "deleted", id });
        return Results.NoContent();
    }

    // ----- /api/products/{id}/repos ---------------------------------------

    private static async Task<IResult> ListReposForProduct(string id, ControlDbContext control, Forge.Api.Persistence.RepoLookup repoLookup)
    {
        var product = await control.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (product is null) return Results.Problem(title: $"Product {id} not found", statusCode: 404);
        // Phase 80.B — repos live in the product's own DB; RepoLookup routes there.
        var rows = await repoLookup.ListByProductAsync(id);
        return Results.Ok(rows.Select(MapRepo).ToList());
    }

    private static async Task<IResult> AttachRepo(
        HttpContext ctx,
        string id,
        [FromBody] CreateRepoRequest req,
        ControlDbContext control,
        ProductDbContext db,
        Forge.Api.Persistence.IProductDbContextFactory factory,
        IHubContext<ForgeHub> hub,
        IRepoCloneService cloneSvc,
        ILoggerFactory loggerFactory)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        var product = await control.Products.FirstOrDefaultAsync(p => p.Id == id);
        if (product is null) return Results.Problem(title: $"Product {id} not found", statusCode: 404);

        var validation = RepoValidation.Validate(req);
        if (validation is not null) return validation;

        if (await control.RepoRegistry.AnyAsync(r => r.Id == req.Id && r.DeletedAt == null))
        {
            return Results.Problem(title: $"Repo id {req.Id} already exists", statusCode: 409);
        }

        // Q5 — same remote URL cannot live under two products. The DB index
        // would reject this anyway, but a hand-shaped 409 with the existing
        // productId lets the UI offer "open existing product" as recovery.
        var existingByRemote = await control.RepoRegistry.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RemoteUrl == req.RemoteUrl && r.DeletedAt == null);
        if (existingByRemote is not null)
        {
            return Results.Json(
                new
                {
                    title = "Remote URL already attached",
                    detail = $"This repo is already attached to product '{existingByRemote.ProductId}' as '{existingByRemote.Id}'.",
                    productId = existingByRemote.ProductId,
                    repoId = existingByRemote.Id,
                },
                statusCode: 409);
        }

        // Q2 — same clone root cannot host two repos.
        if (await control.RepoRegistry.AnyAsync(r => r.CloneRoot == req.CloneRoot && r.DeletedAt == null))
        {
            return Results.Problem(
                title: "Clone root already in use",
                detail: $"Another repo already targets {req.CloneRoot}. Pick a different folder.",
                statusCode: 409);
        }

        var maxSort = await control.RepoRegistry.Where(r => r.ProductId == id && r.DeletedAt == null).Select(r => (int?)r.Sort).MaxAsync() ?? -1;
        var row = new RepoRow
        {
            Id = req.Id,
            Sort = maxSort + 1,
            ProductId = id,
            Kind = req.Kind,
            RemoteUrl = req.RemoteUrl,
            DisplayName = req.DisplayName,
            DefaultBranch = string.IsNullOrWhiteSpace(req.DefaultBranch) ? "main" : req.DefaultBranch,
            CloneRoot = req.CloneRoot,
            AdoOrg = req.AdoOrg,
            AdoProject = req.AdoProject,
            GhOwner = req.GhOwner,
            GhRepo = req.GhRepo,
        };
        // Phase 80.B — the repo row lives in the owning product's DB; the
        // control registry holds the routing + global-uniqueness mirror.
        var repoDb = factory.CreateFor(product.Slug);
        repoDb.Repos.Add(row);
        await repoDb.SaveChangesAsync();
        control.RepoRegistry.Add(new RepoRegistryRow
        {
            Id = row.Id,
            Sort = row.Sort,
            ProductId = row.ProductId,
            ProductSlug = product.Slug,
            Kind = row.Kind,
            RemoteUrl = row.RemoteUrl,
            CloneRoot = row.CloneRoot,
            DisplayName = row.DisplayName,
        });
        await control.SaveChangesAsync();

        var repo = MapRepo(row);
        await hub.Clients.All.SendAsync("repo.changed", new { kind = "attached", repo });

        // Auto-clone: if the local clone root doesn't exist, clone now.
        // Progress is streamed via SignalR clone events.
        string? cloneOutcome = null;
        string? cloneError = null;
        if (!Directory.Exists(row.CloneRoot))
        {
            try
            {
                var result = await cloneSvc.EnsureClonedAsync(row);
                cloneOutcome = result.Outcome.ToString().ToLowerInvariant();
                if (result.Outcome == Forge.Api.Services.Git.RepoCloneOutcome.Failed
                    || result.Outcome == Forge.Api.Services.Git.RepoCloneOutcome.Mismatched)
                {
                    cloneError = result.Message;
                }
            }
            catch (Exception ex)
            {
                cloneOutcome = "failed";
                cloneError = ex.Message;
                var log = loggerFactory.CreateLogger("Forge.Api.Endpoints.ProductsEndpoints");
                log.LogWarning(ex, "Auto-clone failed for repo {RepoId}", row.Id);
            }
        }

        // Phase 26 — auto-detect build/test commands after clone completes.
        BuildSystemDetector.DetectedCommands? detected = null;
        if (Directory.Exists(row.CloneRoot))
        {
            detected = BuildSystemDetector.Detect(row.CloneRoot);
            if (BuildSystemDetector.MergeInto(row, detected))
            {
                await repoDb.SaveChangesAsync();
                repo = MapRepo(row);
                await hub.Clients.All.SendAsync("repo.changed", new { kind = "build-detected", repo });
            }
        }

        return Results.Created($"/api/repos/{row.Id}", new
        {
            repo,
            cloneOutcome,
            cloneError,
            detectedBuild = detected is not null ? new
            {
                detected.Ecosystem,
                detected.BuildCommand,
                detected.TestCommand,
                detected.RestoreCommand,
                detected.Ambiguous,
                detected.AmbiguityDetail,
            } : null,
        });
    }

    // ----- /api/products/{id}/stats ---------------------------------------

    private static async Task<IResult> GetProductStats(
        string id,
        ControlDbContext control,
        Forge.Api.Persistence.IProductDbContextFactory factory)
    {
        var product = await control.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (product is null) return Results.Problem(title: $"Product {id} not found", statusCode: 404);

        // Phase 73 follow-up — workstream + PR rows live in the *target*
        // product's per-product DB, not whichever DB the request scope
        // (X-Forge-Product-Id) resolved to. The Products & Repos page
        // polls /stats for every product card while exactly one product
        // is "active", so binding to the request-scope DB makes every
        // non-active card show 0 / 0. Resolve explicitly by slug.
        var db = factory.CreateFor(product.Slug);

        var repoIds = await control.RepoRegistry.Where(r => r.ProductId == id && r.DeletedAt == null).Select(r => r.Id).ToListAsync();

        // Workstream breakdown — done / active / stalled, ignoring the
        // historical `Planned` flag (which is set on virtually every row
        // and was excluding 100% of real workstreams).
        //   done    = complete + historic
        //   stalled = stalled
        //   active  = everything else (planning, active, review, merging…)
        var wsRows = repoIds.Count == 0
            ? new List<string>()
            : await db.Workstreams.Where(w => repoIds.Contains(w.RepoId!)).Select(w => w.Status).ToListAsync();
        var wsDone = wsRows.Count(s => s == "complete" || s == "historic");
        var wsStalled = wsRows.Count(s => s == WorkstreamStatuses.Stalled);
        var wsActive = wsRows.Count - wsDone - wsStalled;

        // PR breakdown — open vs closed (where closed === merged today;
        // Forge has no abandoned-without-merge column).
        var prCols = repoIds.Count == 0
            ? new List<string>()
            : await db.Prs.Where(p => repoIds.Contains(p.RepoId!)).Select(p => p.Col).ToListAsync();
        var prsClosed = prCols.Count(c => c == "merged");
        var prsOpen = prCols.Count - prsClosed;

        var lastActiveTicks = repoIds.Count == 0
            ? null
            : await db.Workstreams.Where(w => repoIds.Contains(w.RepoId!)).Select(w => (double?)w.LastActive).MinAsync();

        return Results.Ok(new
        {
            productId = id,
            repoCount = repoIds.Count,
            // New breakdowns (preferred).
            wsDone,
            wsActive,
            wsStalled,
            prsOpen,
            prsClosed,
            // Back-compat aliases for older callers — `activeWorkstreams`
            // now means "non-done, non-stalled" (formerly "non-merging,
            // non-planned" which excluded everything).
            activeWorkstreams = wsActive,
            openPrs = prsOpen,
            lastActivity = lastActiveTicks?.ToString("0.###"),
        });
    }

    // ----- /api/repos ------------------------------------------------------

    private static async Task<IResult> ListAllRepos(ControlDbContext control, Forge.Api.Persistence.RepoLookup repoLookup, string? productId, ForgeActionContext action, ForgeCapabilityAuthorizer authorizer)
    {
        var readable = await authorizer.ReadableProductIdsAsync(await action.ActorAsync());
        // Phase 80.B — repos live per-product. Route to one product or union
        // across all products (this is the cross-product repo list).
        List<RepoRow> rows;
        if (!string.IsNullOrWhiteSpace(productId))
        {
            rows = readable.Contains(productId) ? await repoLookup.ListByProductAsync(productId) : new();
        }
        else
        {
            rows = new List<RepoRow>();
            var productIds = readable;
            foreach (var pid in productIds)
            {
                rows.AddRange(await repoLookup.ListByProductAsync(pid));
            }
        }
        return Results.Ok(rows.Select(MapRepo).ToList());
    }

    private static async Task<IResult> GetRepo(string id, ControlDbContext control, Forge.Api.Persistence.RepoLookup repoLookup)
    {
        var row = await repoLookup.GetByIdAsync(id);
        return row is null
            ? Results.Problem(title: $"Repo {id} not found", statusCode: 404)
            : Results.Ok(MapRepo(row));
    }

    private static async Task<IResult> DeleteRepo(
        HttpContext ctx,
        string id,
        bool? force,
        ControlDbContext control,
        Forge.Api.Persistence.IProductDbContextFactory factory,
        IHubContext<ForgeHub> hub)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        // Phase 80 — resolve the per-product DB via repo_registry so deleting
        // a repo works regardless of the active X-Forge-Product-Id scope.
        var db = await ResolveRepoDbAsync(id, control, factory);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var row = await db.Repos.FirstOrDefaultAsync(r => r.Id == id);
        if (row is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var liveWs = await db.Workstreams.CountAsync(w => w.RepoId == id);
        var livePrs = await db.Prs.CountAsync(p => p.RepoId == id);
        if ((liveWs > 0 || livePrs > 0) && force != true)
        {
            return Results.Json(
                new
                {
                    requireConfirm = true,
                    title = "Repo still has workstreams or PRs",
                    detail = $"Repo {id} has {liveWs} workstream(s) and {livePrs} PR(s). Re-call with ?force=true to detach them.",
                },
                statusCode: 422);
        }

        // Detach: orphan rows fall back to no repo (UI scopes them out of any
        // product view). Operator can move them later if they want.
        if (force == true)
        {
            await foreach (var w in db.Workstreams.Where(w => w.RepoId == id).AsAsyncEnumerable())
            {
                w.RepoId = null;
            }
            await foreach (var p in db.Prs.Where(p => p.RepoId == id).AsAsyncEnumerable())
            {
                p.RepoId = null;
            }
        }

        db.Repos.Remove(row);
        // Phase 80.B — drop the control registry mirror so its remote-url /
        // clone-root uniqueness slot frees up for re-attach.
        var reg = await control.RepoRegistry.FirstOrDefaultAsync(r => r.Id == id);
        if (reg is not null) control.RepoRegistry.Remove(reg);
        await control.SaveChangesAsync(); await db.SaveChangesAsync();

        await hub.Clients.All.SendAsync("repo.changed", new { kind = "detached", id });
        return Results.NoContent();
    }

    // ----- helpers ---------------------------------------------------------

    private static Product MapProduct(ProductRow r) => new(
        r.Id, r.Name, r.Slug, r.Description, r.OwnerUserId, r.CreatedAt, r.ActiveIterationId);

    private static readonly System.Text.Json.JsonSerializerOptions RepoJsonOpts = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static Repo MapRepo(RepoRow r)
    {
        IReadOnlyList<string>? areaPaths = null;
        if (!string.IsNullOrWhiteSpace(r.AreaPathsJson))
        {
            try { areaPaths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.AreaPathsJson, RepoJsonOpts); }
            catch { /* malformed — null */ }
        }
        IReadOnlyList<string>? buildPipelineIds = null;
        if (!string.IsNullOrWhiteSpace(r.BuildPipelineIdsJson))
        {
            try { buildPipelineIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.BuildPipelineIdsJson, RepoJsonOpts); }
            catch { /* malformed — null */ }
        }
        return new Repo(
            r.Id, r.ProductId, r.Kind, r.RemoteUrl, r.DisplayName, r.DefaultBranch, r.CloneRoot,
            r.AdoOrg, r.AdoProject, r.GhOwner, r.GhRepo,
            areaPaths, r.LastSyncAt, r.LastSyncCursor, r.LastSyncStatus, r.LastSyncError,
            r.IsRunningSince, r.AgentReadyDefaultTeam, r.InitialSyncWindowDays,
            r.InitCommand, r.RestoreCommand,
            r.BuildCommand, r.TestCommand, r.BuildTimeoutSec, r.TestTimeoutSec,
            r.WiSyncAssignedTo, r.WiSyncDateFromDays, r.WiSyncFromDate, r.WiSyncToDate,
            r.PrSyncCreatedBy, r.PrSyncStatus, buildPipelineIds,
            r.WorkItemSyncIntervalSeconds, r.PrSyncIntervalSeconds, r.BuildSyncIntervalSeconds,
            r.LastUpstreamChangedDate,
            r.TestImpactedOnly,
            r.PlanDocBaseDir,
            r.RunTestsElevated,
            r.E2eUseBddPattern);
    }

    private static string Slugify(string name)
    {
        var s = name.Trim().ToLowerInvariant();
        var b = new System.Text.StringBuilder(s.Length);
        var lastWasHyphen = false;
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch))
            {
                b.Append(ch);
                lastWasHyphen = false;
            }
            else if (ch is ' ' or '-' or '_')
            {
                // Phase A — collapse runs of separator chars into a single
                // hyphen. `ForgeDbPaths.TryValidateSlug` rejects `--`, so
                // "Foo   Bar" must produce "foo-bar" (not "foo---bar").
                if (!lastWasHyphen)
                {
                    b.Append('-');
                    lastWasHyphen = true;
                }
            }
            // else: silently drop characters that are not letters/digits/
            // space/hyphen/underscore. The validator catches anything that
            // still snuck through.
        }
        return b.ToString().Trim('-');
    }

    // ----- Phase 23 — scheduled sync endpoints ----------------------------

    public sealed record TriggerSyncRequest(
        bool IgnoreThrottle = false,
        string Kind = "auto");

    private static async Task<IResult> TriggerRepoSync(
        HttpContext ctx,
        string id,
        [FromBody] TriggerSyncRequest? req,
        ControlDbContext control,
        Forge.Api.Persistence.IProductDbContextFactory factory,
        WorkItemSyncService syncService,
        CancellationToken ct)
    {
        var auth = ForgeUser.RequireWriter(ctx);
        if (auth is not null) return auth;

        // Phase 80 — resolve the per-product DB via repo_registry so this works
        // regardless of the active X-Forge-Product-Id scope.
        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var repo = await db.Repos.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (repo is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var ignoreThrottle = req?.IgnoreThrottle ?? false;
        if (ignoreThrottle)
        {
            var gate = RequireRole.Check(ctx, control, RequireRole.ProductOwner);
            if (gate is not null) return gate;
        }

        // kind='full' clears both delta cursors so the next run uses the initial window.
        var kind = req?.Kind ?? "auto";
        if (string.Equals(kind, "full", StringComparison.OrdinalIgnoreCase))
        {
            repo.LastSyncAt = null;
            repo.LastUpstreamChangedDate = null; // Phase 34
            await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);
        }

        // Phase 34 — delta cursor sources from upstream-changedate.
        DateTimeOffset? updatedFrom = null;
        if (!string.IsNullOrEmpty(repo.LastUpstreamChangedDate)
            && DateTimeOffset.TryParse(repo.LastUpstreamChangedDate, out var lastChanged))
        {
            updatedFrom = lastChanged;
        }
        else
        {
            var windowDays = repo.InitialSyncWindowDays
                ?? (string.Equals(repo.Kind, "github", StringComparison.OrdinalIgnoreCase)
                    ? Forge.Api.Services.SyncConstants.GithubInitialWindowDays
                    : Forge.Api.Services.SyncConstants.AdoInitialWindowDays);
            updatedFrom = DateTimeOffset.UtcNow.AddDays(-windowDays);
        }

        var filters = new WorkItemSyncFilters(UpdatedFrom: updatedFrom);
        var result = await syncService.SyncAsync(repo, filters, ignoreThrottle, ct);

        if (result.Error is not null && result.Pulled == 0 && result.Inserted == 0)
        {
            var status = result.Error.StartsWith("Rate limit", StringComparison.OrdinalIgnoreCase) ? 429
                : result.Error.Contains("not configured", StringComparison.OrdinalIgnoreCase) ? 400
                : 502;
            return Results.Problem(title: result.Error, statusCode: status);
        }

        // Record the sync run.
        db.WorkItemSyncRuns.Add(new WorkItemSyncRunRow
        {
            Id = $"sr-{Guid.NewGuid():N}",
            RepoId = id,
            Kind = "manual",
            StartedAt = DateTimeOffset.UtcNow.ToString("o"),
            FinishedAt = DateTimeOffset.UtcNow.ToString("o"),
            ItemsAdded = result.Inserted,
            ItemsUpdated = result.Updated,
            ErrorMessage = result.Error,
            CompletedSuccessfully = result.Error is null,
        });
        await control.SaveChangesAsync(ct); await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            repoId = id,
            pulled = result.Pulled,
            inserted = result.Inserted,
            updated = result.Updated,
            skipped = result.Skipped,
            error = result.Error,
        });
    }

    private static async Task<IResult> GetRepoSyncRuns(
        HttpContext ctx,
        string id,
        ControlDbContext control,
        Forge.Api.Persistence.IProductDbContextFactory factory,
        [FromQuery] int? limit,
        [FromQuery] string? target,
        CancellationToken ct)
    {
        // Phase 80 — resolve the per-product DB via repo_registry so the
        // Products & Repos page can pull sync history for repos outside the
        // currently active X-Forge-Product-Id scope (otherwise this 404s with
        // "Repo {id} not found" whenever the user is viewing a different
        // product than the repo's owning product).
        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (repo is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        // Sync history is a "recent runs" view — not an export. Cap at 50
        // (default and max) so the drawer/table never has to render more
        // than a screenful and the response stays small/fast.
        var max = Math.Clamp(limit ?? 50, 1, 50);
        var query = db.WorkItemSyncRuns.Where(r => r.RepoId == id);

        // Filter by target: "wi" = work-item runs, "pr" = PR runs.
        if (string.Equals(target, "pr", StringComparison.OrdinalIgnoreCase))
            query = query.Where(r => r.Kind.StartsWith("pr-"));
        else if (string.Equals(target, "wi", StringComparison.OrdinalIgnoreCase))
            query = query.Where(r => !r.Kind.StartsWith("pr-"));

        // StartedAt is stored as ISO-8601 string, so lexicographic sort = chronological.
        var runs = await query
            .OrderByDescending(r => r.StartedAt)
            .Take(max)
            .Select(r => new
            {
                r.Id,
                r.Kind,
                r.StartedAt,
                r.FinishedAt,
                r.ItemsAdded,
                r.ItemsUpdated,
                r.ItemsConflicted,
                r.ErrorMessage,
                r.CompletedSuccessfully,
            })
            .ToListAsync(ct);

        return Results.Ok(runs);
    }

    private static async Task<IResult> GetSyncRunItems(
        HttpContext ctx,
        string id,
        string runId,
        ControlDbContext control,
        Forge.Api.Persistence.IProductDbContextFactory factory,
        CancellationToken ct)
    {
        // Phase 80 — resolve the per-product DB via repo_registry so the
        // sync-run drawer can drill into items for any repo, not just one in
        // the active X-Forge-Product-Id scope.
        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var run = await db.WorkItemSyncRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId && r.RepoId == id, ct);
        if (run is null) return Results.Problem(title: $"Sync run {runId} not found", statusCode: 404);

        var items = await db.SyncRunItems
            .Where(i => i.RunId == runId)
            .OrderBy(i => i.Action)
            .ThenBy(i => i.EntityKind)
            .ThenBy(i => i.Title)
            .Select(i => new
            {
                i.EntityKind,
                i.EntityId,
                i.Title,
                i.Action,
                i.ProviderRef,
            })
            .ToListAsync(ct);

        return Results.Ok(items);
    }

    /// <summary>
    /// Phase 25 follow-up — return a richer detail payload for a specific
    /// sync-run item: latest entity snapshot (work item or PR), upstream
    /// last-update timestamp, and a structured summary the UI renders in the
    /// sync-history click-through popup.
    /// </summary>
    private static async Task<IResult> GetSyncRunItemDetail(
        HttpContext ctx,
        string id,
        string runId,
        string entityKind,
        string entityId,
        ControlDbContext control,
        Forge.Api.Persistence.IProductDbContextFactory factory,
        CancellationToken ct)
    {
        // Phase 80 — resolve the per-product DB via repo_registry so the
        // sync-run item detail endpoint works for any repo regardless of the
        // active X-Forge-Product-Id scope.
        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var run = await db.WorkItemSyncRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId && r.RepoId == id, ct);
        if (run is null) return Results.Problem(title: $"Sync run {runId} not found", statusCode: 404);

        var row = await db.SyncRunItems.AsNoTracking()
            .FirstOrDefaultAsync(i =>
                i.RunId == runId
                && i.EntityKind == entityKind
                && i.EntityId == entityId, ct);
        if (row is null) return Results.Problem(title: "Sync-run item not found", statusCode: 404);

        if (string.Equals(entityKind, "workitem", StringComparison.OrdinalIgnoreCase))
        {
            var wi = await db.WorkItems.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == entityId, ct);

            var summary = row.Action switch
            {
                "added"   => "New work item",
                "removed" => "Removed from sync scope",
                _ => wi is null
                    ? "Updated"
                    : $"State: {wi.State} · Type: {wi.Type}{(wi.Points > 0 ? $" · {wi.Points} pts" : "")}",
            };

            return Results.Ok(new
            {
                entityKind = "workitem",
                entityId,
                providerRef = row.ProviderRef,
                title = wi?.Title ?? row.Title,
                action = row.Action,
                lastUpdateDate = wi?.UpdatedAt,
                summary,
                workItem = wi is null ? null : new
                {
                    state = wi.State,
                    type = wi.Type,
                    points = wi.Points,
                    priority = wi.Priority,
                    areaPath = wi.AreaPath,
                    iterationRef = wi.IterationRef,
                    assignedTo = wi.AssignedToJson,
                    parentId = wi.ParentId,
                    tags = wi.TagsJson,
                },
            });
        }

        if (string.Equals(entityKind, "pr", StringComparison.OrdinalIgnoreCase))
        {
            var pr = await db.Prs.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == entityId, ct);

            var openComments = pr is null
                ? 0
                : await db.PrComments.CountAsync(c => c.PrId == pr.Id && c.State == "active", ct);
            var totalComments = pr is null
                ? 0
                : await db.PrComments.CountAsync(c => c.PrId == pr.Id, ct);
            var latestBuild = pr is null
                ? null
                : await db.Builds.AsNoTracking()
                    .Where(b => b.PrId == pr.Id)
                    .OrderByDescending(b => b.StartedAt ?? string.Empty)
                    .Select(b => new { b.State, b.Result, b.PipelineName, b.WebUrl, b.StartedAt, b.FinishedAt })
                    .FirstOrDefaultAsync(ct);

            string? lastUpdateDate = pr?.LastUpstreamUpdatedAt ?? pr?.LastSyncAt;

            string summary;
            if (row.Action == "added") summary = "New PR";
            else if (row.Action == "removed") summary = "PR removed from active sync";
            else
            {
                var parts = new List<string>();
                if (pr is not null)
                {
                    parts.Add($"State: {pr.Col}");
                    if (totalComments > 0)
                    {
                        parts.Add(openComments > 0
                            ? $"{totalComments} comment(s), {openComments} open"
                            : $"{totalComments} comment(s)");
                    }
                    if (latestBuild is not null)
                    {
                        var buildStr = string.IsNullOrEmpty(latestBuild.PipelineName)
                            ? $"build: {latestBuild.State}"
                            : $"{latestBuild.PipelineName}: {latestBuild.State}";
                        parts.Add(buildStr);
                    }
                }
                summary = parts.Count > 0 ? string.Join(" · ", parts) : "Updated";
            }

            return Results.Ok(new
            {
                entityKind = "pr",
                entityId,
                providerRef = row.ProviderRef,
                title = pr?.Title ?? row.Title,
                action = row.Action,
                lastUpdateDate,
                summary,
                pr = pr is null ? null : new
                {
                    col = pr.Col,
                    branch = pr.Branch,
                    @base = pr.Base,
                    additions = pr.Additions,
                    deletions = pr.Deletions,
                    files = pr.Files,
                    workItemId = pr.WorkItemId,
                    isActive = pr.IsActive,
                    openComments,
                    totalComments,
                },
                latestBuild,
            });
        }

        return Results.Problem(title: $"Unknown entityKind '{entityKind}'", statusCode: 400);
    }

    private static async Task<IResult> GetRepoSyncStatus(
        HttpContext ctx,
        string id,
        ControlDbContext control,
        Forge.Api.Persistence.IProductDbContextFactory factory,
        CancellationToken ct)
    {
        // Phase 80 — resolve the per-product DB via repo_registry so the
        // sync-status badge polls work for every repo card on the Products &
        // Repos page, not just the active product's repos.
        var db = await ResolveRepoDbAsync(id, control, factory, ct);
        if (db is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        var repo = await db.Repos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (repo is null) return Results.Problem(title: $"Repo {id} not found", statusCode: 404);

        return Results.Ok(new
        {
            lastSyncAt = repo.LastSyncAt,
            lastSyncStatus = repo.LastSyncStatus,
            lastSyncError = repo.LastSyncError,
            isRunningSince = repo.IsRunningSince,
        });
    }
}
