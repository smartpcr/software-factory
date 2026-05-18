# Phase 76 — Move `RepoRow` from Control DB to Product DBs

> **Status (2026-05-17): DEFERRED to a future sprint.** The skeleton
> migration `src/forge/Forge.Api/Persistence/Phase76Migration.cs`
> exists as a draft (data-move + drop logic only; not wired into boot,
> not callable). No production code or model swap has landed.
>
> Operator decision after weighing the trade-offs below: keep the
> 500-error hotfix (`TaskExecutionEndpoints.cs` commits endpoint now
> uses `control.Repos` instead of `db.Set<RepoRow>()`) and revisit the
> full move in a dedicated sprint.

## Trade-offs uncovered during audit (must be addressed before re-opening)

1. **Sync workers' hot loop regression.** `WorkItemSyncJob`,
   `PrSyncJob`, `BuildSyncJob` tick every 60 s with a single
   `SELECT * FROM repos WHERE LastSyncAt < …` on control. Post-76 each
   tick must fan out across every per-product DB (N+1 DB opens per
   tick). Mitigation: keep a slim `repo_index(RemoteUrl, ProductId,
   RepoId, LastSyncAt)` table on control as a directory, updated on
   AddRepo / EditRepo.
2. **Webhook scope-resolution chicken-and-egg.** `ProductScopeMiddleware`
   resolves the slug from incoming webhook payloads by looking up
   `repos` by `RemoteUrl`. Without `repos` on control, every webhook
   has to open every per-product DB to find the matching row. Same
   mitigation as (1) — the `repo_index` table is enough.
3. **Lost DB-level `UNIQUE(RemoteUrl)`.** Today the DB prevents the
   same git remote being attached to two products. Post-76 it becomes
   an app-layer check (scan + insert race window). Mitigation: enforce
   via the `repo_index` table on control (DB UNIQUE preserved on the
   directory row).

The three issues all point at the same shape: `repos` data moves to
per-product DBs but a small **directory** stays on control to serve
hot read paths + global invariants. That's halfway between Option A
and Option C from the discussion. When this sprint opens, **start by
designing the `repo_index` directory** rather than the data move; the
data move is the easy part once the directory exists.

## Why

Operator request 2026-05-17: "i want repo to move to productDb". Today
`RepoRow` lives on `ControlDbContext` (control DB / forge.db), but the
conceptual ownership is `Product 1:N Repo` and most queries that touch
a repo also touch product-scoped tables (workstreams, work_items, prs,
deliverables). Co-locating repos with the product-side workload:

- removes the cross-DB JOIN cost on the hot PR/workstream paths;
- simplifies the slug-extraction chain (workstream → repo →
  product → slug all stay inside one DB);
- aligns the boundary with the natural ownership graph that the
  operator already reasons about ("repos belong to products").

Trade-offs explicitly accepted by the operator:

- `UNIQUE(remoteUrl)` global constraint moves from DB index → app-layer
  check (`ProductsEndpoints.AddRepo` scans all product DBs before
  insert). The check still works under the existing rule "one repo
  per remote URL" (Q5).
- Cross-product enumerations (`PrSyncJob`, `BuildSyncJob`,
  `WorkItemSyncJob`, `AutoMergeWatcher` cross-product PR scans, the
  Pull Requests view) now fan out via `ProductFanout.ForEachProductAsync`
  instead of a single control query. Already the existing pattern for
  most product-scoped workers.

## Stages

### Stage 1 — Schema migration `Phase76Migration`

- [ ] **1.1** `src/forge/Forge.Api/Persistence/Phase76Migration.cs`
      (new, idempotent, Mixed kind):
      1. Bail if marker `phase76-repos-moved-to-product` already
         stamped in `forge_seed_state`.
      2. For each product slug (enumerated from `control.Products`):
         - ATTACH per-product DB.
         - Run the EF Core `RepoRow` CREATE TABLE statement on the
           per-product DB (idempotent — skip when table exists).
         - `INSERT INTO {slug}.repos SELECT * FROM repos WHERE
           ProductId = '{productId}'`.
         - DETACH.
      3. Verify per-product row sums equal control.repos count.
      4. On verification pass: `DROP TABLE repos` on control DB.
      5. Stamp `phase76-repos-moved-to-product` in `forge_seed_state`.
      6. VACUUM control DB.
- [ ] **1.2** Register Phase 76 in `PhaseMigrationCatalog.cs` as
      `Mixed` kind (control DROP + per-product INSERT span both DBs).
- [ ] **1.3** Add 76 to `PhaseMigrationRunner.ExecutionOrder` after
      Phase 73.
- [ ] **1.4** Tests: `Phase76MigrationTests` — fresh seed with 2
      products + 4 repos → 2 in product A's DB, 2 in product B's DB,
      0 in control; idempotent re-run no-ops; orphan repos (no
      matching product) raise an error.

### Stage 2 — DbContext + boundary model swap

- [ ] **2.1** `ControlDbContext.cs:34` — remove
      `public DbSet<RepoRow> Repos`. Remove `OnModelCreating`
      configuration in `DbModelControl.cs`.
- [ ] **2.2** `ProductDbContext.cs` — add
      `public DbSet<RepoRow> Repos => this.Set<RepoRow>();`.
      Configure in `DbModelProduct.cs` with `ProductId` as the
      partition key.
- [ ] **2.3** `Split/DbBoundary.cs:124` — change `repos` from
      `DbKind.Control, Global` to
      `DbKind.Product, DirectProductId, "ProductId"`.

### Stage 3 — Call-site refactor (~70 sites across 26 files)

Categorize each call site by query pattern and rewrite:

- [ ] **3.1 Single-product reads/writes** (~50 sites — most
      endpoints + per-spawn orchestrator code). Replace
      `control.Repos.Where(r => r.Id == repoId)` with
      `db.Repos.Where(...)` after pushing the right slug onto
      `ProductDbContextFactory` (which most call sites already do via
      `ProductScopeMiddleware` / `ProductFanout`).
      Affected files:
      `AdminEndpoints.cs (1)`, `CostEndpoints.cs (1)`,
      `PerformanceEndpoints.cs (1)`, `PrCommentsEndpoints.cs (1)`,
      `PrsEndpoints.cs (1)`, `SprintEndpoints.cs (8)`,
      `StateEndpoints.cs (1)`, `StoriesEndpoints.cs (4)`,
      `TaskExecutionEndpoints.cs (2)`, `WorkItemsEndpoints.cs (3)`,
      `WorkstreamsEndpoints.cs (3)`, `SloppyReassignmentReconciler.cs (1)`,
      `CrewStoryArtifactStrategy.cs (2)`,
      `PlanReconciler.cs (1)`, `ProviderStoryProviderOps.cs (1)`,
      `StorySpawnOrchestrator.cs (1)`, `WorkItemBaseBranchResolver.cs (1)`,
      `WorktreePathResolver.cs (3)`, `AdoConfigResolver.cs (1)`,
      `BuildValidationWorker.cs (1)`,
      `CrewQueueWorker.cs (4 — already inside ProductFanout)`,
      `ForgeSeed.cs (2)`, `AutoMergeWatcher.cs`,
      `AdoPrCommentSyncClient.cs`, `GitHubPrCommentSyncClient.cs`,
      `PrMergeUtilities.cs`.
- [ ] **3.2 Cross-product enumerations** (~15 sites in sync
      workers, AutoMergeWatcher, the Pull Requests "All" view) — wrap
      the existing logic in `ProductFanout.ForEachProductAsync`,
      collect results per product, aggregate. Workers that ALREADY
      run inside `ForEachProductAsync` only need the
      `control.Repos` → `db.Repos` rename (the slug is already
      pushed). Files: `PrSyncJob.cs`, `BuildSyncJob.cs`,
      `WorkItemSyncJob.cs`, `AutoMergeWatcher.cs`.
- [ ] **3.3 Admin add-repo path** — `ProductsEndpoints.AddRepo`
      (21 sites): de-duplicate `remoteUrl` across products by
      iterating `ProductFanout` and querying each per-product
      `db.Repos`. Reject with 409 when the URL exists in ANY product.
      Single product DB write inside its own slug scope.
- [ ] **3.4 `ProductScope.cs`** — currently uses `control.Repos` to
      validate / expand the scope. Refactor to use the in-memory
      product slug list (from `control.Products`) and the matching
      per-product `Repos`. Worth caching since `ProductScope` is hot
      on every request.

### Stage 4 — Tests + verification

- [ ] **4.1** `Phase76MigrationTests.cs` (Stage 1.4 above).
- [ ] **4.2** `Phase76CrossProductRepoQueryTests.cs` — two products
      with their own repos; per-product write goes to the right DB;
      cross-product enumeration via `ProductFanout` returns the
      union; no remoteUrl collisions across products.
- [ ] **4.3** `Phase76AdminAddRepoDuplicateTests.cs` — adding a repo
      with a remoteUrl that already exists in a DIFFERENT product
      returns 409.
- [ ] **4.4** Update existing tests that seeded `control.Repos`
      directly (`SplitContextsTests`, `SplitResumeServiceTests`,
      `Phase73CrossProductWriteTests`, `ProductSlugExtractorTests`,
      `ProductFanoutTests`, `SplitApplyServiceTests`) — seed
      `db.Repos` (per-product) instead.

### Stage 5 — Run + cutover

- [ ] **5.1** `dotnet build src/forge/Forge.Api` — compile clean.
- [ ] **5.2** 🔒 Operator approves restart.
- [ ] **5.3** Stop Forge.Api, run new tests + the existing
      Phase73/74/75 suites, start Forge.Api.
- [ ] **5.4** Phase 76 migration runs on boot (idempotent — re-runs
      are no-op once marker is stamped).
- [ ] **5.5** Verify `forge.db` has NO `repos` table; per-product
      DBs each have their own `repos` table populated.
- [ ] **5.6** Live spawn smoke test — Reranker Trainer / Chaos and
      Stress workstreams continue iterating, no `Cannot create a
      DbSet for 'RepoRow'` errors.
- [ ] **5.7** 🔒 Operator approves commit.

## Out of scope

- The Phase 73 Stage 2D.3 bootstrap refactor (still pending) —
  separate concern. Many of those call sites become moot after
  Phase 76 (because they're querying repos against an unscoped
  ProductDbContext which is meaningless post-76 anyway). Re-audit
  after Phase 76 lands.
- Renaming `repos` table to `product_repos` for naming consistency
  with the other `product_*` per-product tables. Pure cosmetic;
  defer to a `forge_v2.md` candidate.
