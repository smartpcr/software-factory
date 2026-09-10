// -----------------------------------------------------------------------
// <copyright file="PhaseMigrationRunner.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Persistence;

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase G.4 follow-up — dispatches phase migrations through
/// <see cref="PhaseMigrationCatalog"/> so each migration runs against the
/// right DB(s) for its <see cref="PhaseMigrationKind"/>:
///
/// <list type="bullet">
///   <item><see cref="PhaseMigrationKind.Control"/> — invokes the migration
///         once with <c>ControlDbContext</c> + the default
///         <c>ProductDbContext</c> (which during Phase D interim binds to
///         the same <c>forge.db</c> file).</item>
///   <item><see cref="PhaseMigrationKind.Product"/> — invokes the migration
///         once for each product DB. Pre-Phase-F: ProductDbContext falls
///         back to <c>forge.db</c> (no <c>{slug}.db</c> files exist yet) so
///         this collapses to one invocation against forge.db.
///         Post-Phase-F: invokes once per <c>{slug}.db</c> so each
///         per-product DB picks up the schema change.</item>
///   <item><see cref="PhaseMigrationKind.Mixed"/> — touches both. Behaves
///         like Product (invocations include the control side ops which
///         are idempotent <c>CREATE TABLE IF NOT EXISTS</c> /
///         <c>ALTER TABLE … ADD COLUMN</c> guarded by pragma probes).</item>
/// </list>
///
/// <para>
/// <b>Order is preserved.</b> The execution order in
/// <see cref="ExecutionOrder"/> mirrors the historical Program.cs sequence,
/// where some phase migrations (43, 45, 46, 47, 49, 50) must run BEFORE
/// Phase 38 because Phase 38's EF model projection references columns
/// they add. Re-ordering these would crash boot on legacy DBs.
/// </para>
/// </summary>
public sealed class PhaseMigrationRunner
{
    /// <summary>
    /// Explicit ordered list of phase migrations to run at boot.
    /// Mirrors the historical Program.cs invocation sequence. The order
    /// matters: Phase 42 first (EF model dependency), then 43/45/46/47/49/50
    /// before Phase 38's backfills, etc. New phase migrations should be
    /// appended at the end unless a specific dependency forces an earlier
    /// position.
    /// </summary>
    public static readonly IReadOnlyList<int> ExecutionOrder = new[]
    {
        // Phase 92 — workstreams.LastBuildGate / LastTestGate. Runs FIRST: the
        // columns are now in the WorkstreamRow EF model, so any later migration
        // that materializes WorkstreamRow (e.g. Phase38's full-entity query)
        // would hit "no such column" if the ALTER ran after it. Additive
        // nullable columns, table-existence guarded, no ordering dependency —
        // safe at the head of the chain.
        92,
        42, 8, 11, 12, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        30, 31, 32, 33, 34, 35, 36, 37,
        43, 45, 46, 47, 49, 50, 51, 52, 53, 54, 55,
        // Phase 59 — adds repos.TestImpactedOnly. Control-only,
        // additive nullable column; runs anywhere in the chain. Slotted
        // before 60 just to keep the numeric order monotonic post-55.
        59,
        60, 63, 64, 65, 72,
        // docs/forge/e2e.md Stage 1.1 — Phase 79 adds `workstreams.Category`,
        // which the EF model on `WorkstreamRow` references. Phase 38's
        // ApplyAsync issues an EF query against `workstreams` that
        // projects every mapped column, so 79 MUST run BEFORE 38 (same
        // constraint as 43/45/47/49/50 above).
        79,
        38, 39, 40, 41, 44, 48,
        77, 78,
        // Phase 80 (Stage 80.A) — additive, no dependency on the 38-ordering
        // constraints above. 80 (Control: repo_registry + products.DefaultRepoId)
        // then 81 (Product: product_stories.RepoId, fans out per {slug}.db).
        80, 81,
        // Phase 82 — iteration_prompt table for the per-iter prompt audit
        // trail (gen + eval). Product-only, fans out per {slug}.db.
        82,
        // Phase 83 — repos.PlanDocBaseDir. Product-only, fans out per {slug}.db.
        // Additive nullable column; no ordering dependency.
        83,
        // Phase 84 — repos.RunTestsElevated. Product-only, fans out per {slug}.db.
        // Additive nullable column; no ordering dependency.
        84,
        // Phase 87 — deliverables.PairAttempt. Product-only, fans out per
        // {slug}.db. Additive nullable column; no ordering dependency. Phases
        // 85 and 86 are non-migration code-phase numbers.
        87,
        // Phase 88 — prs merge metadata for already-merged PR evaluation fallback.
        88,
        // Phase 89 — agent_level_history.Actor for per-agent PO override
        // actions (promote / demote / hold). Control-only, additive nullable
        // column; no ordering dependency.
        89,
        // Phase 90 — deliverables.ReasoningText (generator reasoning/thinking
        // text per iteration). Product-only, fans out per {slug}.db. Additive
        // nullable column; no ordering dependency.
        90,
        // Phase 91 — deliverable_questions.ExplanationJson (Questions-tab
        // Explain popup rationale). Product-only, fans out per {slug}.db.
        // Additive nullable column; no ordering dependency.
        91,
        // Phase 93 — operator_prompts table (per-workstream human operator
        // steering prompts). Product-only, fans out per {slug}.db. New table;
        // no ordering dependency. (Renumbered from 92 on merge — local's
        // build/test-gate migration already holds 92, placed at the head of the
        // chain above for its WorkstreamRow EF-model ordering dependency.)
        93,
        // Phase 94 — repos.E2eUseBddPattern (per-repo qa-e2e test-style override).
        // Product-only, fans out per {slug}.db. Additive nullable column; no
        // EF-model ordering dependency (no migration EF-queries `repos`).
        94,
        // Phase 95 (chat bridge) — control-side chat_notifications +
        // chat_subscribers. New tables; no ordering dependency.
        95,
        // Phase 96 (chat bridge, Phase 4) — control-side chat_bridge_state
        // (inbound long-poll cursor). New table; no ordering dependency.
        96,
        // Phase 97 (chat bridge, Part B) — chat_spawn_topics + chat_notifications.SpawnId.
        97,
        // Stage 1 — additive control-side chat configuration; legacy remains selected.
        98,
    };

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IForgeDbPaths paths;
    private readonly ILogger<PhaseMigrationRunner> log;

    public PhaseMigrationRunner(
        IServiceScopeFactory scopeFactory,
        IForgeDbPaths paths,
        ILogger<PhaseMigrationRunner> log)
    {
        this.scopeFactory = scopeFactory;
        this.paths = paths;
        this.log = log;
    }

    /// <summary>
    /// Apply every catalogued phase migration in <see cref="ExecutionOrder"/>.
    /// Uses the supplied <paramref name="control"/> + <paramref name="defaultDb"/>
    /// for the default invocation (Phase D interim: both bind to forge.db).
    /// </summary>
    public async Task ApplyAllAsync(
        ControlDbContext control,
        ProductDbContext defaultDb,
        CancellationToken ct = default)
    {
        var assembly = typeof(PhaseMigrationCatalog).Assembly;
        foreach (var phase in ExecutionOrder)
        {
            await this.ApplyOneAsync(assembly, phase, control, defaultDb, ct);
        }
    }

    /// <summary>
    /// Invoke a single migration. Looks up its <see cref="PhaseMigrationSpec"/>
    /// in the catalog and dispatches based on <see cref="PhaseMigrationKind"/>.
    /// </summary>
    private async Task ApplyOneAsync(
        Assembly assembly,
        int phase,
        ControlDbContext control,
        ProductDbContext defaultDb,
        CancellationToken ct)
    {
        if (!PhaseMigrationCatalog.ByPhase.TryGetValue(phase, out var spec))
        {
            throw new InvalidOperationException(
                $"PhaseMigrationRunner.ExecutionOrder references Phase{phase} " +
                $"but PhaseMigrationCatalog has no entry for it. Add a " +
                $"PhaseMigrationSpec or remove the phase from ExecutionOrder.");
        }

        var typeName = $"Forge.Api.Persistence.Phase{phase}Migration";
        var type = assembly.GetType(typeName)
            ?? throw new InvalidOperationException(
                $"PhaseMigrationRunner could not find type '{typeName}' in the Forge.Api assembly. " +
                $"PhaseMigrationCatalog claims it exists.");
        var method = type.GetMethod("ApplyAsync", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"PhaseMigrationRunner could not find a public static ApplyAsync method on '{typeName}'.");

        this.log.LogDebug(
            "Applying Phase{Phase} migration ({Kind}) — control:[{ControlTables}] product:[{ProductTables}]",
            phase, spec.Kind,
            string.Join(",", spec.ControlTables),
            string.Join(",", spec.ProductTables));

        // Always invoke at least once with the default contexts. This matches
        // pre-G.4 behaviour: every migration sees the control DB on the
        // ControlDbContext side and the (forge.db-fallback) ProductDbContext.
        // Post-Phase-F: for Product migrations, the per-product invocation
        // loop below also runs against each {slug}.db.
        await this.InvokeAsync(method, control, defaultDb, ct);

        // Phase F+ fan-out: when per-product DBs exist on disk, also apply
        // Product-only migrations to each so their schema stays in lockstep
        // with what EF expects.
        //
        // Control-only migrations skip this (their tables don't exist in
        // {slug}.db).
        //
        // Mixed migrations ALSO skip this: their migration body interleaves
        // control + product table ops (e.g. Phase8Migration calls
        // EnsureColumn(forge_actions, ...) AFTER EnsureColumn(cards, ...)) —
        // running the whole body against a per-product DB would crash on the
        // first control-table reference because forge_actions doesn't exist
        // there. For fresh post-split per-product DBs, the schema is laid
        // down by ProductDbContext.EnsureCreatedAsync (which projects the
        // full EF model including all Mixed migration column additions), so
        // skipping fan-out for Mixed is safe. Refactoring each Mixed
        // migration into separate Control + Product passes is a follow-up.
        if (spec.Kind != PhaseMigrationKind.Product)
        {
            return;
        }

        var productSlugs = await this.ListSlugsWithPerProductDbAsync(control, ct);
        if (productSlugs.Count == 0)
        {
            return;
        }

        foreach (var slug in productSlugs)
        {
            using var slugScope = this.scopeFactory.CreateScope();
            var productFactory = slugScope.ServiceProvider.GetRequiredService<IProductDbContextFactory>();
            var perProductDb = productFactory.CreateFor(slug);
            this.log.LogDebug(
                "Fan-out: applying Phase{Phase} ({Kind}) to product '{Slug}' DB.",
                phase, spec.Kind, slug);
            await this.InvokeAsync(method, control, perProductDb, ct);
        }
    }

    private Task InvokeAsync(MethodInfo method, ControlDbContext control, ProductDbContext db, CancellationToken ct)
    {
        var task = (Task?)method.Invoke(null, new object?[] { control, db, ct })
            ?? throw new InvalidOperationException(
                $"Phase migration {method.DeclaringType?.Name}.ApplyAsync returned null instead of a Task.");
        return task;
    }

    /// <summary>
    /// Enumerate product slugs whose <c>{slug}.db</c> file exists on disk.
    /// Pre-Phase-F this returns an empty list (no per-product DBs yet),
    /// keeping the fan-out a no-op. After <c>POST /api/admin/db/split-apply</c>
    /// has produced per-product files, this returns each slug so the runner
    /// can apply product/mixed migrations to every per-product DB.
    /// </summary>
    private async Task<IReadOnlyList<string>> ListSlugsWithPerProductDbAsync(
        ControlDbContext control, CancellationToken ct)
    {
        var products = await control.Products
            .AsNoTracking()
            .Select(p => p.Slug)
            .ToListAsync(ct);
        var withDb = new List<string>(products.Count);
        foreach (var slug in products)
        {
            if (string.IsNullOrWhiteSpace(slug)) continue;
            if (this.paths.ProductDbExists(slug)) withDb.Add(slug);
        }
        return withDb;
    }
}
