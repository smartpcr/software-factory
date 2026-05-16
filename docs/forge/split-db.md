# Plan — per-product SQLite files (Forge multi-DB refactor)

## Outcome the user asked for

- `appsettings.json` specifies a **folder** (key: `Forge:Db:Folder`, default `E:\forge`).
- Per product: a separate SQLite file at `{folder}\{product.slug}.db` (e.g., product *failover cluster* → `E:\forge\failover-cluster.db`).
- A **control DB** at `{folder}\forge.db` holds cross-product entities (products, users, agents, crews, etc.).
- Routing key: `X-Forge-Product-Id` header (existing) → control DB lookup → product DB.
- Existing `E:\forge\forge.db` is **auto-migrated** on first boot: split into control DB + per-product DBs.

User-chosen scope (answers to the elicitation):
- `globalTables = control_db`
- `routing = per_request_factory`
- `migration = auto_split`
- `configKey = folder`
- `slugRule = product_slug_column`

## Reality check — surface area

| Surface | Count | Notes |
|---|---|---|
| Files taking `ForgeDbContext db` | ~95 | endpoints + services + background workers + migrations |
| Files hardcoding `E:\forge\forge.db` fallback | 11 | StoriesEndpoints, WorkstreamsEndpoints, AdminEndpoints, CrewStoryArtifactStrategy |
| Phase migrations (single-DB) | ~55 (Phase8Migration … Phase72Migration) | each will need to be classified as "control" or "product" or both |
| Tests using `Forge:Db:Path` | dozens (ForgeWebApplicationFactory + per-test specials) | test factory tripwire enforces single path today |
| In-band cross-table joins | many (e.g., `ProductScope.ResolveAsync` joins Products+Repos to filter Workstreams) | SQLite can't cross-DB JOIN; must either pre-resolve or use `ATTACH DATABASE` |

This is a multi-day refactor that touches almost every file under `src/forge/Forge.Api`. We must phase it.

## Boundary — what lives where

> Adjusted after rubber-duck pass. `agent_assignments` and `agent_pair_assignments`
> moved to product DB (they are work-item history). `agent_usage` stays control
> with denormalized `ProductId` + `RepoId` snapshots so analytics work without
> cross-DB joins. `idempotency` becomes product-aware on its key.

### Control DB (`{folder}\forge.db`)

| Table | Reason |
|---|---|
| `products` | the registry — dispatcher consults this |
| `repos` | every product-scoped query joins on Repo→Product; keeping repos here avoids cross-DB joins. RepoId stays as a string column on product-DB rows. **Mutable sync-cursor state stays here too** but writes that span repo cursor + product rows accept eventual consistency with a boot-time repair (see Phase E). |
| `users`, `user_settings`, `user_product_roles` | global identity + per-(user,product) role |
| `crews`, `crew_members`, `crew_history` | global (Phase 10.6 — crews are not scoped to products) |
| `agents`, `agent_history`, `agent_level_history` | global (Phase 11) |
| `agent_claims` | global busy/idle capacity. Add a `UNIQUE(AgentId)` partial index for `MaxConcurrentTasks=1` and use `BEGIN IMMEDIATE` + compare-and-swap in `AgentSlotService.TryClaimAsync` so two product workers can't both claim the same slot. |
| `agent_usage` | feeds the cross-product cost ledger / `/crews` salary view. **Must denormalize `ProductId`, `RepoId`, `WorkItemId` and `Epic`** at write time — current `AgentUsageRow` only has `TaskId`, which is insufficient for the analytics paths (`CostEndpoints.cs`, `PerformanceEndpoints.cs`, `PayrollEndpoints.cs`). |
| `domain_experts` | repo-scoped but small + read-mostly; control DB to avoid cross-DB |
| `audit` | cross-product reads; gains a `ProductId` column. |
| Pure config / catalog rows (e.g., model catalog if any) | global |

### Per-product DB (`{folder}\{slug}.db`)

Everything that is owned by one product and has high write volume:

`epics`, `columns`, `cards`, `workstreams`, `ws_events`, `ws_stats`, `prs`, `pr_comments`, `pr_comment_history`, `work_items`, `work_item_links`, `work_item_sync_runs`, `sync_run_items`, `deliverables`, `deliverable_questions`, `agent_memory`, `task_queue`, `task_queue_history`, `task_transcript_events`, `build_validation_runs`, `builds`, `build_stages`, `build_stage_logs`, `build_artifacts`, `build_artifact_files`, `build_failure_points`, `build_fix_pending`, `build_annotations`, `story_spawns`, `workstream_deps`, `product_stories`, `product_iterations`, `product_area_paths`, `product_saved_prefixes`, `repo_iteration_bindings`, `overview_activity`, `overview_agents`, **`agent_assignments`** (work-item history — moved from control per rubber-duck), **`agent_pair_assignments`** (keyed by WorkItemId — moved from control).

`idempotency` — per-product DB. Idempotency keys are tied to a specific (method, path, key) and the path itself routes via `X-Forge-Product-Id`; replays of the same key under a different product must not collide. Add the resolved `ProductId` to the key generation.

## Phased delivery

> Re-ordered after rubber-duck pass: build the splitter + dry-run as a shadow tool
> **before** rewriting endpoints, so we can validate partition correctness on real
> data first.

The plan splits into 7 phases. Phases A and B are pure config + tooling (no behavior change visible to UI). Phase C ships the migration in dry-run mode against the existing DB so we can verify partition correctness before touching code. Phases D–F refactor + route. Phase G hardens & cleans up.

### Phase A — Config + path resolver (no behavior change)

1. Add `Forge:Db:Folder` to `appsettings.json` (default `E:\forge`). Keep `Forge:Db:Path` for legacy compatibility — if set, takes precedence; if unset, resolve from `Forge:Db:Folder`.
2. New `ForgeDbPaths` service: `ControlDbPath()`, `ProductDbPath(string slug)`, `ProductDbExists(string slug)`. Folder is created on first access.
3. Update the 11 inline `?? @"E:\forge\forge.db"` fallbacks to call `ForgeDbPaths.ControlDbPath()` (StoriesEndpoints, WorkstreamsEndpoints, AdminEndpoints, CrewStoryArtifactStrategy — see the grep audit in §"Reality check").
4. **No DB split yet** — control DB path resolves to the same `forge.db` so all existing code still works.
5. Per-DB **WAL bootstrap** + shutdown checkpoint becomes per-file (not per-process). The current `bootstrapped` HashSet in `Program.cs:101` already keys on resolved path — we just call it from `ForgeDbPaths` each time a new DB is opened.

### Phase B — Splitter shadow tool (writes to staging, doesn't replace anything)

1. New CLI subcommand or admin endpoint `POST /api/admin/db/split-dry-run`:
   - Reads the existing `forge.db`.
   - Classifies every table as control / product / drop based on the boundary in §"Boundary".
   - Reports row counts per product (partitioned via direct `ProductId` column or `RepoId → ProductId` chain).
   - **Verifies orphans**: rows with null/unknown `RepoId` or `ProductId`, rows whose `RepoId` chains to a deleted product, rows in tables that were dropped in earlier Phase migrations.
   - Reports duplicate IDs across products for tables with provider-derived IDs (e.g., the same ADO work-item id under two products) — these need a uniqueness strategy in the product DBs.
   - Writes a JSON report and exits without writing any new DB files.
2. `POST /api/admin/db/split-apply` does the actual write: schema migrations on each new DB, partitioned copy, post-copy row-count verification, **drops product tables from the source `forge.db` only after** all targets are verified. Pre-flight backs up `forge.db` to `forge.db.pre-phase73.{timestamp}.bak`.
3. Both endpoints gated by `RequireRole("product-owner")` and behind a feature flag `Forge:Db:SplitEnabled` (default `false`) so they cannot run accidentally.
4. **The splitter is the source of truth for the boundary**. Phase D consumers must agree with it; mismatches fail at boot.

### Phase C — Validate the boundary on real data

1. Run the splitter dry-run against `E:\forge\forge.db` and review the report with the operator.
2. Iterate on the boundary until the report shows zero orphans and zero unexpected splits.
3. Apply the splitter in a staging copy of the folder; verify SQLite WAL/shm sidecar behavior, file size growth, query patterns.
4. Only proceed to Phase D once the operator signs off on the partition.

### Phase D — Introduce `ControlDbContext` + `ProductDbContext` (no façade)

> Replaces the original "façade" plan per rubber-duck #9. A façade DbContext
> would force two transactional saves into one `SaveChangesAsync` and confuse
> migrations that call `db.Database.GetDbConnection()` directly.

1. New `ControlDbContext` (control tables only) + `ProductDbContext` (product tables only). Both are real `DbContext` subclasses with independent `OnModelCreating`.
2. The legacy `ForgeDbContext` stays compilable as a typedef-style alias to `ControlDbContext` during the migration window so partially-migrated files keep compiling.
3. New `IProductDbContextFactory` (scoped service): given `productId` or `productSlug`, returns a `ProductDbContext` bound to `{folder}\{slug}.db`. Caches the slug→path lookup per request.
4. New middleware reads `X-Forge-Product-Id`, looks up the slug in the control DB, attaches a scoped `ProductDbContext` to DI.
5. Endpoints migrate file-by-file from `ForgeDbContext db` to:
   - `ControlDbContext control` — endpoints touching only global entities (auth, crews, agents, products list, settings),
   - `ControlDbContext control, ProductDbContext product` — endpoints touching both,
   - `IProductDbContextFactory factory` — endpoints that need cross-product reads (`?productId=all`).
6. `ProductScope.ResolveAsync` simplifies to "read header, return slug + productId" — no in-DB join needed (Repos live in control DB).

### Phase E — Background services iterate per product

> Tightened per rubber-duck #14 — use control-side "due/dirty" indexes
> to avoid opening every product DB every tick.

1. Each worker tick reads the control DB to find the set of products that are *due*:
   - `PrSyncJob` / `WorkItemSyncJob` / `BuildSyncJob` — read `repos.LastSyncStatus` cursor + cadence; only open product DBs for repos whose cursor is stale.
   - `CrewQueueWorker` — read `agent_claims` + `agent_usage` (control) for capacity; for queue items, only open product DBs that have pending rows (cache `last_pending_at` on a control-side `product_runtime` row, updated on enqueue).
   - `AutoMergeWatcher` — read open PRs from product DBs via a control-side `active_pr_count` watermark.
   - `StorySpawnOrchestrator` / `PlanReconciler` / `PlanPrMergeReactor` / `LeadAutoDispatcher` — same product-due pattern.
2. **Bounded parallelism**: cap concurrent open product DBs per tick (`Forge:Db:MaxConcurrentProductDbs`, default 8) so 50-product installs don't exhaust SQLite handles or DBContext pool size.
3. **Write-path repair** (per rubber-duck #5):
   - Critical write paths that span control + product (work item create, story spawn, agent claim+assignment, build sync) commit **product first, control second** with idempotent natural keys.
   - Boot-time repair job `Phase73Reconciler.cs`: scans for "product committed, control missing" (e.g., a queued task with no claim, an agent_usage row whose product table has no work item) and either heals or quarantines them.

### Phase F — Auto-split on first boot

1. `Phase73Migration_SplitPerProduct.cs` runs on every boot:
   - Detect if `forge.db` still contains product-scoped tables with data.
   - For each `products` row → derive `{folder}\{product.slug}.db`; run the full product-DB schema migration on it.
   - Copy product-scoped rows into each product DB partitioned by `ProductId` (direct) or `RepoId → product_id` (FK chain) — **reuse the splitter shadow tool from Phase B**.
   - Verify row counts pre vs post; abort with a clear error if mismatch.
   - Drop product-scoped tables from `forge.db` (control DB keeps control-only tables).
   - Stamp `Phase73Applied = 1` in a meta row so the migration is idempotent.
2. Pre-migration: copy `forge.db` to `forge.db.pre-phase73.{timestamp}.bak`.
3. Verification additions per rubber-duck #11: orphan-row check, `RepoId → ProductId` chain check, duplicate-id-across-products check, dropped-column residue check.

### Phase G — Tests + cleanup

1. `ForgeWebApplicationFactory`: per-test temp **folder** (replacing per-test temp file). Both control + product DBs land under that folder. Tripwire asserts:
   - control DB path is under `%TEMP%`,
   - every product DB path is under `%TEMP%`,
   - no SQLite connection opens `E:\forge\forge.db`,
   - post-test scan of the temp folder shows only expected DB files.
2. Add a default `atlas` product fixture + a `MultiProductFactory` variant that seeds two products so accidental default-product-only access is caught (per rubber-duck #12).
3. Audit the ~55 phase migrations: classify each as control-DB or product-DB; route the runners accordingly. Phases that touched both must split into a control half and a product half.
4. Delete the legacy `ForgeDbContext` alias once every consumer is migrated.
5. Re-run all `dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj` — green is the bar.

## Risks & open seams

1. **Cross-DB JOINs** are impossible in SQLite without `ATTACH DATABASE`. The plan avoids them by putting "lookup" tables (Products, Repos, Crews, Agents, Users) in the control DB. Any analytics that needs to JOIN across product DBs (e.g., "show every workstream across all products") must enumerate products and union results client-side or in-app. **Most of the analytics surface (`CostEndpoints`, `PerformanceEndpoints`, `PayrollEndpoints`) is preserved by denormalizing `ProductId`, `RepoId`, `WorkItemId`, `Epic` onto `agent_usage` at write time** — see rubber-duck #3.

2. **Transactional boundaries** — today a single `SaveChangesAsync` commits both control + product writes. After the split, the two DBs are separate transactions. Critical paths (per rubber-duck #5):
   - **Work item create** (`WorkItemsEndpoints.cs:369-386` + `TaskQueueService.cs:45-95`) → write product first (WorkItem + TaskQueue), then control (audit row). Idempotent natural key on `(repoId, providerWorkItemId)`.
   - **Crew claim + assignment** (`CrewQueueWorker.cs:724-833`) → product writes WorkItem.AssignedAgentJson + agent_assignments; control writes agent_claims with `UNIQUE(AgentId)` constraint. Repair: a claim with no assignment is released by `Phase73Reconciler`.
   - **Story spawn** (`StoriesEndpoints.cs:281-323`) → product writes StorySpawn + WorkItem; control writes audit. No control state to repair if the audit row goes missing — accept loss.
   - **Build sync** (`BuildSyncJob.cs:220-292`) → product writes Builds + stages + logs; control updates `repos.LastSyncStatus` cursor. Repair: cursor may roll back to the last verified product write; sync is idempotent so re-running picks up only missing rows.
   - **Sloppy reassignment** (`SloppyReassignmentReconciler.cs`) → product writes WorkItem state + deliverable purge + new agent_pair_assignment; control writes new agent_usage row + audit. Repair: a paired control row with no product row is quarantined.

3. **`agent_usage` denormalization** — `ProductId`, `RepoId`, `WorkItemId`, `Epic` columns added so cost analytics don't need to open every product DB. Cost-attribution writes (DeliverableLoop, CrewQueueWorker) must pass these through.

4. **Idempotency middleware** — key generation must include resolved productId (or raw `X-Forge-Product-Id`). Otherwise replays of the same client key under a different product return the wrong product's cached response.

5. **`X-Forge-Product-Id=all`** — analytics endpoints accept the latency cost of enumerating every product DB; we add a helper `IProductDbContextFactory.ForEachProductAsync(...)`. Cap concurrent open DBs via `Forge:Db:MaxConcurrentProductDbs`.

6. **Migration safety** — auto-split runs once; failure must not corrupt the source. Backup first, write the new DBs side-by-side, only then drop product tables from the old file. Splitter dry-run (Phase B/C) catches partition issues *before* the apply step touches data.

7. **WAL / checkpoint scheduling** (rubber-duck #13) — every DB gets its own WAL/shm sidecar. Per-DB bootstrap + `PRAGMA wal_checkpoint(TRUNCATE)` on shutdown for the control DB and every opened product DB. Idle product DBs that weren't opened during a session don't need a checkpoint.

8. **AgentSlotService atomicity** (rubber-duck #6) — `TryClaimAsync` currently does a read-then-insert that's not atomic across product workers. Add `UNIQUE(AgentId)` partial index for `MaxConcurrentTasks=1` agents, plus `BEGIN IMMEDIATE` + post-insert verification.

9. **Repos sync state in control DB** (rubber-duck #8) — `repos.LastSyncStatus` mutates with product rows in sync jobs. Accept eventual consistency: product DB writes first, control cursor updates second. If the cursor advance fails after product writes succeed, the next sync re-fetches the same window — idempotency on the product writes prevents duplicates.

## Acceptance criteria

- `dotnet test src/forge/Forge.Api.Tests/Forge.Api.Tests.csproj` is green.
- Boot against an existing `E:\forge\forge.db` produces `forge.db` (control) + one `{slug}.db` per existing product, with row counts matching pre-migration totals.
- A fresh boot against an empty `E:\forge\` folder creates `forge.db` with the seeded `atlas` product + `atlas.db` for product-scoped tables.
- UI flows that today work against the single DB (board, workstream view, PR view, story spawn, builds, queue) continue to work with `X-Forge-Product-Id` driving the routing.
- No file still references `@"E:\forge\forge.db"` as a hardcoded fallback.

## What I'd ship first (recommended minimal first-PR slice)

**Phase A** alone — config + path resolver. This is **invisible to the user** and **safely no-op**:

- adds `Forge:Db:Folder` config,
- new `ForgeDbPaths` service centralizes the path resolution,
- removes the 11 hardcoded `E:\forge\forge.db` fallbacks,
- keeps control DB pointing at the same file as today,
- makes the WAL bootstrap per-file so Phases B–G can introduce more DBs without rework.

It's mergeable in isolation, fully test-covered, and unblocks every subsequent phase. Phases B–G follow as separate PRs once Phase A is in.

## Status / progress

| Phase | State | Notes |
|---|---|---|
| **A — Config + path resolver** | ✅ shipped (commit `f2aeaab1`) + tests green | `Forge:Db:Folder`, `ForgeDbPaths`, 12 fallback sites removed, slug validator (server + UI), 93/93 tests pass on Phase-A surface |
| **B — Splitter shadow tool** | ✅ code-complete, smoke-tested on live 1.3 GB DB | `POST /api/admin/db/split-dry-run` gated by `Forge:Db:SplitEnabled` (default `false`) + product-owner role. 11/11 unit tests + 50/50 combined Phase A+B tests green. Live report: 100,756 rows → 17,338 control + 74,849 product (across 4 products) + 8,569 cascade-delete orphans + 0 unknown tables. |
| C — Boundary validation on real data | ▶ ready for review | Operator inspects B's report and signs off; remaining decision: what to do with the 8,569 broken-FK orphan rows (drop on apply? route to default product?). |
| **D.1 — Split DbContexts + factory + middleware** | ✅ code-complete + 8/8 unit tests + 70/70 broad surface | Two new contexts: `ControlDbContext` (15 DbSets) + `ProductDbContext` (37 DbSets). `IProductDbContextFactory` (scoped, slug-keyed cache, falls back to forge.db when per-product file missing). `ProductScopeMiddleware` reads `X-Forge-Product-Id` → resolves slug → stashes on `HttpContext.Items`. `ForgeDbContext.OnModelCreating` delegates to `DbModelControl.Configure` + `DbModelProduct.Configure` (zero behavior change). **Bonus**: `RequireRole.Check` upgraded to accept `DbContext` base class so callers using either `ForgeDbContext` or `ControlDbContext` compile cleanly — pre-requisite for D.2. |
| **D.2 — Migrate consumers off `ForgeDbContext`** | ▶ in progress | **Step 1 (helpers, partial)**: `RequireRole.Check`, `CrewsEndpoints.TodaysSpendAsync`, `BusyAgentsHelper.LoadBusyAgentsAsync`/`ReapAllOpenOnBootAsync`/`ReapStaleAsync`, `WorkItemsEndpoints.LookupPrimaryPrAsync`/`RecordAssignmentTransition` — all now take `DbContext` base. **Step 2 (Control-only endpoints, partial)**: 5/8 migrated — `MeEndpoints`, `MeSettingsEndpoints`, `AgentsEndpoints`, `AuditEndpoints`, `PayrollEndpoints` now take `ControlDbContext`. **Lesson**: "Control-only" is rarer than expected — `CrewsEndpoints`/`ProductsEndpoints`/`PerformanceEndpoints` touch both sides (e.g., `BuildValidationRuns`, `WorkItems`) and need a dual-context refactor (request both `ControlDbContext` and a `ProductDbContext` via factory). 183/183 tests pass on the migrated surface. |
| E — Background services per-product | pending | Worker fan-out + write-path repair |
| F — Auto-split on first boot | pending | `Phase73Migration_SplitPerProduct` |
| G — Tests + cleanup | pending | Test factory + ~55 phase migration audit + drop legacy `ForgeDbContext` |

## Estimated effort

| Phase | Files touched | Risk | Effort |
|---|---|---|---|
| A — config + path resolver | ~15 (11 fallbacks + Program.cs + new ForgeDbPaths + tests) | low | ~half day |
| B — splitter shadow tool | ~5 new files + admin endpoint | medium (data correctness) | ~1 day |
| C — boundary validation on real data | none | low (review-heavy) | ~half day |
| D — split contexts + per-request factory | ~95 (every endpoint + service) | high (compile churn) | ~3–4 days |
| E — background services | ~13 worker files | high (correctness across product fanout) | ~2 days |
| F — auto-split on first boot | ~3 new migration files + Program.cs | medium | ~1 day |
| G — tests + cleanup | test factory + ~50 migration audit | medium | ~2 days |
| **Total** | ~125 files | | **~10 days** |
