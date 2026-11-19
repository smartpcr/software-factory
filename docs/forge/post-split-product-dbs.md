# Per-product databases for products created after the split

| Item | Status (2026-11-19) |
|---|---|
| Design | Written (this document) |
| D1 Provision `{slug}.db` on product create | Implemented; local tests pass; deployed 2026-11-19 **without** board-column seeding (added after that restart; the next restart deploys it); live check open ([E2E-1](#e2e-1-new-product-gets-its-own-db-on-the-live-host)) |
| D2 Fallback-aware read authorization | Implemented; local tests pass; deployed 2026-11-19; live check open ([E2E-2](#e2e-2-harnessdsh-detail-page-loads)) |
| D3 Offline product move command | Implemented; local tests pass ([T3](#t3)); failure and scale rehearsal passed on copies of the live files ([E2E-5](#e2e-5-forced-failure-against-a-copy)); not deployed |
| D4 Move `harness` and `release` into their own files | Not run on the live files; needs operator approval per run |

Row counts in [A](#a-what-we-want-to-achieve) were measured **read-only** against
the live control DB on 2026-11-19. They are historical evidence for planning;
each move re-measures with a dry run.

## Contents

- [A. What we want to achieve](#a-what-we-want-to-achieve)
- [B. How we get there](#b-how-we-get-there)
- [C. How we prove it](#c-how-we-prove-it)

---

## A. What we want to achieve

### Problem

Forge keeps shared state (users, products, grants, crews) in the **control DB**,
`E:\forge\split\forge.db`. Since the one-time **split** on 2026-05-17, each
product's work (repos, work items, spawns, workstreams, PRs, transcripts) lives
in its own **product DB**, `E:\forge\split\{slug}.db`.

The split only created files for the products that existed that day. Nothing
creates a file for a product added later. When the file is missing, Forge
silently **falls back** to the control DB for that product's rows. Two live
products are in that state:

| Product | Created | Rows in the control DB |
|---|---|---|
| `release` | 2026-07-15 | 119,493 product rows (117,318 of them transcript events) + 1 repo |
| `harness` | 2026-11-19 | 43 product rows + 1 repo (`agent-plugin`) |

This caused a visible failure. The read-permission check denied any story, spawn
or workstream lookup for a product without its own file, while the endpoints
happily read the fallback. Opening `harness:DSH` returned
`403 Capability denied` although the spawn existed and its agents were running.

### Vocabulary

- **Control DB** — `forge.db`. Holds host-wide tables. Because of the fallback it
  also has a full set of product tables, which every boot migrates.
- **Product DB** — `{slug}.db`. Holds one product's rows. Kept current at boot by
  the **migration fan-out**, which runs product migrations against every
  existing product DB.
- **Split marker** — the `phase73-split-applied` row in `forge_seed_state`.
  When it is present, Forge treats the host as split.
- **Fallback** — `ProductDbContextFactory.CreateFor` uses the control DB when a
  product has no file. This keeps a pre-split host working; on a split host it
  quietly mixes products in one file.
- **Template** — the existing DB whose table definitions a new product DB copies.
- **Partition predicate** — the rule in `DbBoundary` that ties a row to a product,
  directly (`ProductId`) or through a parent (`RepoId → repos.ProductId`,
  `WorkstreamId → workstreams.RepoId → …`).
- **Unattributed row** — a row in a product table whose predicate matches no
  product: a null parent key or a parent that no longer exists. 6,765 exist today.

### Audience

Operators running the Forge host, reviewers of the change, and engineers working
on Forge persistence.

### Outcomes

| ID | Observable outcome | Built by | Proved by |
|---|---|---|---|
| <a id="o1"></a>O1 | A product created on a split host gets its own `{slug}.db`, shaped like the existing product DBs, before any request can write to it. | [D1](#d1) | [T1](#t1), [E2E-1](#e2e-1-new-product-gets-its-own-db-on-the-live-host) |
| <a id="o2"></a>O2 | A product still on the fallback is readable by its owners: its story, spawn and workstream pages load. | [D2](#d2) | [T2](#t2), [E2E-2](#e2e-2-harnessdsh-detail-page-loads) |
| <a id="o3"></a>O3 | `harness` and `release` move into their own files with every attributable row copied unchanged, and Forge then reads them from those files. | [D3](#d3), [D4](#d4) | [T3](#t3), [E2E-3](#e2e-3-move-harness), [E2E-4](#e2e-4-move-release) |
| <a id="o4"></a>O4 | A failed move leaves the control DB exactly as it was and leaves no product DB file behind. | [D3](#d3) | [T3](#t3), [E2E-5](#e2e-5-forced-failure-against-a-copy) |

### Scope and non-goals

- In scope: products created after the split, the two existing ones, and the
  permission check.
- Not in scope: the 6,765 unattributed rows. They have no owner to move them to
  and stay in the control DB.
- Not in scope: removing the fallback. Pre-split hosts still need it.
- Not in scope: deleting a product's DB file when the product is deleted. That
  is a separate gap (`DeleteProduct` does not remove the file).
- Not in scope: reclassifying `repos` in `DbBoundary` (see [DEC-2](#dec-2)).

---

## B. How we get there

### Design decisions

<a id="dec-1"></a>**DEC-1 — Create the file during product creation, before the product row exists.**
- *Choice:* `POST /api/products` calls `ProductDbProvisioner.ProvisionAsync` before
  it adds the product row. If the file already exists, the request fails with
  `409` and nothing is saved. If the row fails to save, the new file is deleted.
- *Why:* once the row exists, any request for the product would fall back to the
  control DB and leave rows behind there.
- *Tradeoff:* an unrelated file with the same name blocks creation. The tool
  never adopts a file it did not create, because the file's contents are
  unknown. The operator moves it aside or picks another slug.
- *Constraint found in testing:* nothing may be tracked on the request's control
  context before the early `409`. Later pipeline steps save that context, and
  the first version committed the product row despite the `409`.
- *Supports:* [O1](#o1). *Verified by:* [T1](#t1).

<a id="dec-2"></a>**DEC-2 — Copy table definitions from an existing product DB.**
- *Choice:* the template is the first existing `{slug}.db` in product order. The
  control DB is used only when no product DB exists. The copy takes the
  `DbBoundary` product tables plus `repos`.
- *Why:* the two sources differ. Product DBs carry the split's raw DDL, with
  column defaults such as `task_queue.Kind DEFAULT 'work'`. The control DB's
  product tables come from EF's generated script, which drops 58 of those
  defaults. EF writes every column so it doesn't notice, but raw-SQL inserts
  that leave a column out would fail on an EF-shaped file. `repos` moved into
  product DBs in Phase 80.B, yet `DbBoundary` still calls it Control. A copy of
  `DbBoundary`'s list alone produced a DB with no `repos` table.
- *Tradeoff:* the template must be current. The migration fan-out keeps every
  product DB current at boot, and the existing product DBs were confirmed to
  have identical DDL for all product objects.
- *Supports:* [O1](#o1). *Verified by:* [T1](#t1) and a read-only comparison
  against `local.db`.

<a id="dec-3"></a>**DEC-3 — The permission check reads the same DB as the endpoints.**
- *Choice:* `ForgeCapabilityAuthorizer` no longer denies when the product's file
  is missing. It resolves the DB through the same factory.
- *Why:* the endpoint and its permission check must agree on where a product's
  rows are. Isolation does not come from which file is read. It comes from
  `OwnsRepoAsync`, which requires every spawn, workstream and work item to sit
  under a repo owned by the requested product. That check is unchanged.
- *Tradeoff:* none for split products. Fallback products depend on the repo
  ownership check, as they already did for list endpoints.
- *Supports:* [O2](#o2). *Verified by:* [T2](#t2).

<a id="dec-4"></a>**DEC-4 — Move a product offline, with Forge.Api stopped.**
- *Choice:* the move is a command run against the DB files with the server
  stopped: `Forge.Api.exe --db move-product --product-id <id> [--dry-run]`. It
  follows the existing `--operator` command pattern in `HostOperatorCommand`.
- *Why:* while the server runs, agent loops and sync workers keep writing the
  product's rows to the control DB. Copying under live writes would lose rows
  written after the copy. Creating the file also switches routing on the next
  request, before the data is there.
- *Tradeoff:* downtime. On copies of the live files the move took 7.0 s for
  `release` (119,494 rows) and 2.2 s for `harness` (73 rows), including the
  backup. The command refuses to run against the live folder while port 5180 is
  listening. With `--db-folder` (a scratch copy) it skips that check.
- *Supports:* [O3](#o3), [O4](#o4).

<a id="dec-5"></a>**DEC-5 — Copy with explicit, matching column lists.**
- *Choice:* for each table, read the column names from both files and copy
  `INSERT INTO dst (cols) SELECT cols FROM src …` with the shared list. The move
  fails if a source column has no destination column.
- *Why:* five tables (`deliverables`, `prs`, `repos`, `story_spawns`,
  `task_queue`) list the same columns in a different order in the two files.
  The split's `INSERT … SELECT *` would silently put values in the wrong
  columns. `RepoMoveToProductService` already uses the safe form.
- *Supports:* [O3](#o3). *Verified by:* [T3](#t3).

<a id="dec-6"></a>**DEC-6 — Choose rows with the `DbBoundary` partition predicates.**
- *Choice:* rows move when their table's predicate resolves to the product. The
  product's `repos` rows go too. Unattributed rows and `NoPartitionKey` tables
  (`columns`, `idempotency`) stay put.
- *Why:* the predicates are the same rules the split used and the dry-run
  analyzer reports. Rows with no owner cannot be assigned safely.
- *Supports:* [O3](#o3).

<a id="dec-7"></a>**DEC-7 — Back up, copy, verify, then delete by primary key.**
- *Choice:*
  1. Take an online SQLite backup of `forge.db`.
  2. Build `{slug}.db.moving` from the template and copy in one transaction.
  3. Verify that every table's destination count equals its source count, and
     that primary keys match.
  4. Rename the file to `{slug}.db`.
  5. Delete from `forge.db`, in one transaction, exactly the rows whose primary
     keys are in the new file. Each table's deleted count must equal its copied
     count.
- *Why:* each step is safe to stop after. Until step 4 nothing routes to the new
  file. Until step 5 commits, the control DB is unchanged. Matching on primary
  keys rather than re-running the predicates makes the delete independent of
  table order: a child's predicate joins through its parent, so a
  predicate-based delete would miss children whose parent went first.
- *Supports:* [O3](#o3), [O4](#o4).

<a id="dec-8"></a>**DEC-8 — The tool undoes only what it created.**
- *Ownership:* the move creates the product DB file, so it may delete it on
  failure, including after the rename when the delete step fails. That is safe
  because the server is stopped, so nothing else can have written to the file.
  It never touches an existing `{slug}.db`: if one exists, it refuses to run,
  the same rule as [DEC-1](#dec-1). It deletes control-DB rows only when a row
  with the same primary key is verified in the new file in this run.
- *Rollback after a completed move:* stop the server, restore `forge.db` from
  the backup taken by that run, and move `{slug}.db` aside. The backup path is
  printed and kept. The tool never deletes backups.
- *Supports:* [O4](#o4).

### Deliverables

<a id="d1"></a>**D1 — Provision `{slug}.db` on product create.** Priority P1. Status: built.
- *Result:* new products on a split host never touch the control DB.
- *How:* `ProductDbProvisioner` (new, `Forge.Api/Persistence/Split/ProductDbProvisioner.cs`)
  picks a template ([DEC-2](#dec-2)), builds `{slug}.db.provisioning` in one
  transaction, enables WAL, and renames it to `{slug}.db` without overwriting.
  It also seeds the five canonical board columns (`ForgeSeed.CanonicalBoardColumns`),
  which every product DB carries. Boot only backfills them on the next start, so
  without the seed a new product's Plan & Design board would be empty until a
  restart. `CreateProduct` in `Endpoints/ProductsEndpoints.cs` calls it before
  tracking anything ([DEC-1](#dec-1)). `SplitApplyService.ExtractProductSchemaStatementsAsync`
  becomes `internal` so it can be reused. The provisioner is registered as a
  singleton in `Program.cs`.
- *Depends on:* the split marker (runtime). No code dependencies.
- *Done when:* [T1](#t1) passes, the server is restarted, and
  [E2E-1](#e2e-1-new-product-gets-its-own-db-on-the-live-host) passes.

<a id="d2"></a>**D2 — Fallback-aware read authorization.** Priority P0. Status: built and deployed.
- *Result:* owners can open fallback products' detail pages.
- *How:* remove the missing-file denial and the unused `IForgeDbPaths` dependency
  from `ForgeCapabilityAuthorizer` (`Forge.Api/Auth/ForgeCapabilities.cs`) ([DEC-3](#dec-3)).
- *Depends on:* none.
- *Done when:* [T2](#t2) passes and [E2E-2](#e2e-2-harnessdsh-detail-page-loads) passes.

<a id="d3"></a>**D3 — Offline product move command.** Priority P1. Status: built; rehearsed on copies.
- *Result:* an operator can move one fallback product into its own file, with a
  dry run, verification and a printed backup path.
- *How:* `ProductDbMoveService` (new, `Forge.Api/Persistence/Split/ProductDbMoveService.cs`)
  does steps 1–5 of [DEC-7](#dec-7). It builds the file with D1's
  `ProductDbProvisioner.BuildAsync`, selects rows with the `DbBoundary`
  predicates plus `repos`, and copies with explicit column lists
  ([DEC-5](#dec-5)). `ProductDbMoveCommand` (new) is the CLI, dispatched from
  `Program.cs` before the web host starts, like `--operator`. It checks that the
  marker is present, that the product exists, that no `{slug}.db` exists, and,
  for the live folder, that port 5180 is free. `--dry-run` prints per-table
  counts and writes nothing.
- *Depends on:* D1 (template selection).
- *Done when:* [T3](#t3) passes and [E2E-5](#e2e-5-forced-failure-against-a-copy)
  passes in the build stage.

<a id="d4"></a>**D4 — Move `harness`, then `release`.** Priority P1. Status: not run.
- *Result:* both products read from their own files. The control DB holds none
  of their attributable rows.
- *How:* run D3 for `harness` (small; proves the procedure live), restart, then
  check. Repeat for `release`. Each run needs its own approval for the server
  stop and the move.
- *Depends on:* D3 built and accepted. D1 must be deployed first, so no new
  product lands on the fallback between moves.
- *Done when:* [E2E-3](#e2e-3-move-harness) and [E2E-4](#e2e-4-move-release) pass.

### Delivery order

D2 (deployed) and D1 do not depend on each other. D3 depends on D1's template
selection. D4 is an operation, not code. It runs only after D3 passes its tests
and the failure scenario, and after D1 is deployed. Deploying D1 needs only a
restart. Running D4 needs a planned stop of Forge.Api, which kills in-flight
agent loops, including the running `harness:DSH` plan-doc loop.

### Stages

**Stage 1 — Stop the visible failure and new fallback products (build).** Covers D1 and D2. Entry: none.
1. Remove the missing-file denial in `ForgeCapabilityAuthorizer` (D2).
2. Add a regression test where a split host's fallback product reads its own spawn and is denied another product's (T2).
3. Add `ProductDbProvisioner` with temp-file build and rename (D1).
4. Select the template from an existing product DB; fall back to the control DB (DEC-2).
5. Include `repos` in the copied schema (DEC-2).
6. Call the provisioner from `CreateProduct` before tracking anything; return `409` on an existing file; discard on save failure (DEC-1).
7. Add the T1 tests.
8. Run T1, T2 and the auth and split suites.
9. Compare the live template rule against `local.db` read-only (`manual`).
10. Deploy D2 (done 2026-11-19, restart approved).
11. Deploy D1 (done 2026-11-19, restart approved; the board-column seeding added afterwards deploys with the next restart).

Exit evidence: T1 and T2 pass (recorded below). E2E-1 and E2E-2 pass after deploy.

**Stage 2 — Build the move command (build).** Covers D3. Entry: Stage 1 steps 1–8.
1. `ProductDbMoveService.PlanAsync`: per-table source counts using the `DbBoundary` predicates, plus `repos`.
2. ~~Delete order derived from the predicate chain~~ — replaced by delete-by-primary-key ([DEC-7](#dec-7)).
3. Template build into `{slug}.db.moving` (reuse D1).
4. Copy with explicit, intersected column lists; fail on an unmatched source column.
5. Verify counts and primary keys per table.
6. Online backup of `forge.db` before any write; print the path.
7. Rename `.moving` to `{slug}.db` without overwriting.
8. Delete the copied rows from `forge.db` in one transaction, by verified primary key.
9. Failure handling: delete only `.moving`/the new file created in this run; never the backup.
10. `--db move-product` entry point with checks: port 5180 free, marker present, product exists, no `{slug}.db`.
11. `--dry-run` output.
12. T3 unit tests against fixture DBs, including the reordered-column case and the forced failure.
13. Run E2E-5 against scratch copies of the live files.

Exit evidence: T3 passes and E2E-5 passes (both recorded below). These failure scenarios run in this stage because a defect here could delete rows. Stage 2 is built; it is not deployed until the next approved restart.

**Stage 3 — Move the live products (acceptance).** Covers D4. Entry: Stage 2 exit and D1 deployed.
1. E2E-3 (`harness`).
2. E2E-4 (`release`).
3. Update `docs/forge/split-db.md` to point to this document for post-split products.

Exit evidence: E2E-3 and E2E-4 records in the evidence table.

---

## C. How we prove it

### Local tests

| ID | Proves | Tests | Status |
|---|---|---|---|
| <a id="t1"></a>T1 | D1 / O1 | `ProductDbProvisioningTests`: file created with product tables + `repos` + 5 board columns and routed to; a sibling product DB is used as template; an existing file gives `409` with nothing saved and the file untouched; a pre-split host creates no file | Pass (4/4, 2026-11-19) |
| <a id="t2"></a>T2 | D2 / O2 | `OperatorScopeSecurityTests.Split_Host_Product_Without_Own_Db_File_Reads_Its_Spawn_From_Control_Db`, plus the existing cross-product denial tests | Pass (auth + split suites, 81/81, 2026-11-19) |
| <a id="t3"></a>T3 | D3 / O3, O4 | `ProductDbMoveServiceTests`: dry run reports counts and writes no file; move copies only that product's rows, removes them from the control DB, leaves the other product's, and EF reads them from the new file; reordered template columns keep values in the right columns; an existing `{slug}.db` is refused before the backup; a failure after the copy or before the delete commits leaves every control-DB table count unchanged and no file; a non-split host is refused | Pass (7/7, 2026-11-19) |

Known unrelated failures: `ProductsEndpointsTests` (12) and `AuthAndMeTests` (4)
fail identically on clean `HEAD`. They predate the operator sign-in rework.

### Live scenarios

All scenarios run on the Forge host (this machine) against the real
`E:\forge\split` files, the running Forge.Api on `:5180` and the UI on `:5181`.
There is no remote lab. Mocked or fixture runs count as T-evidence, not as these
scenarios.

#### E2E-1 New product gets its own DB on the live host
- **Given:** D1 deployed; signed in as `xiaodoli@microsoft.com` with a product selected.
- **When:**
  1. Create product `zz-provision-check` in the UI.
  2. Attach any repo to it.
- **Then:**
  - `E:\forge\split\zz-provision-check.db` exists with the same tables as `local.db`.
  - The product is listed for you.
  - The repo row is in the new file, not in `forge.db`.
- **Cleanup:** delete the product in the UI. Then, at the next planned stop, move
  the leftover `.db` file aside (the delete does not remove it; see non-goals).

#### E2E-2 harness:DSH detail page loads
- **Given:** D2 deployed; `harness` on the fallback.
- **When:** open `localhost:5181/workstreams/harness%3ADSH`.
- **Then:** the page renders the spawn, with no `403` in the network log.
- **Cleanup:** none.

#### E2E-3 Move harness
- **Given:** D1 and D3 deployed. Expected: 72 product rows plus 1 repo at the
  12:07 dry run on a copy (up from 43 in the earlier scan; `iteration_prompt`
  grows while the plan-doc loop runs). Re-run the dry run for current numbers.
- **When:**
  1. Run `--dry-run` and record the counts.
  2. Stop Forge.Api (approval).
  3. Run the move and record the backup path.
  4. Start Forge.Api (approval).
- **Then:**
  - Counts in `harness.db` equal the dry run.
  - `forge.db` has 0 rows attributable to `harness`.
  - The `harness:DSH` page shows the same spawn and history.
  - The plan-doc loop resumes on the next tick.
- **Cleanup on failure:** follow [DEC-8](#dec-8) rollback. Keep the backup and
  the failed file for diagnosis.

#### E2E-4 Move release
- **Given:** E2E-3 passed. Expected about 119,493 product rows plus 1 repo.
  Re-run the dry run.
- **When:** same as E2E-3, for `release`. Record how long the server was down.
- **Then:**
  - Counts match the dry run.
  - Release's 57 workstreams and 60 PRs show in the UI.
  - `forge.db` has 0 rows attributable to `release`.
- **Cleanup on failure:** as in E2E-3.

#### E2E-5 Forced failure against a copy
- **Given:** online-backup copies of the live `forge.db` and `local.db` in a
  scratch folder; the command pointed at that folder with `--db-folder`.
- **When:** run the `release` move with a fault injected after the copy and
  before the rename. Then run it again with the fault after the rename and
  before the delete commits.
- **Then (both runs):** the move reports the injected error. Every table's row
  count in the `forge.db` copy is unchanged. No `release.db` or `.moving` file
  is left. The backup file is kept.
- **Also (scale rehearsal):** a real move of `release` and then `harness` with
  the CLI on the same copies. Each moved file passes `integrity_check`, is in WAL
  mode, has all 44 tables and the 5 board columns, and holds exactly the
  dry-run row count. The `forge.db` copy passes `integrity_check` with 0 rows and
  no repos left for either product. In the five reordered tables every row
  matches the source by column name.
- **Cleanup:** delete the scratch folder.

### Evidence record

| Scenario | Build/commit | Command | Result | Cleanup |
|---|---|---|---|---|
| T1 | working tree, 2026-11-19 | `dotnet test --filter ProductDbProvisioningTests` | 4/4 pass | n/a |
| T2 | working tree, 2026-11-19 | `dotnet test --filter Forge.Api.Tests.Auth.\|Split…\|Phase73HalfAppliedGuardTests` | 81/81 pass | n/a |
| Template check (`manual`) | live files, read-only | scratch rebuild vs `local.db` | 44/44 tables; control-DB template drops 58 defaults → DEC-2 | scratch file only |
| E2E-5 fault runs | working tree, 2026-11-19; copies of live `forge.db` + `local.db` (SQLite online backup, 12:07) | scratch harness: `release after-copy`, `release before-delete-commit` | pass: injected error reported; 0 control tables changed; no product DB or `.moving` file; backup kept | scratch folder only |
| E2E-5 scale rehearsal | same build and copies | `Forge.Api.exe --db move-product --product-id <release, harness> --db-folder <scratch>` (dry run, then move) | pass: release 119,494 rows in 7.0 s, harness 73 rows in 2.2 s; integrity ok; 0 rows left; by-name row match in reordered tables | scratch folder only |
| E2E-1…4 | — | — | open | — |

These results show that the listed behavior works for the tested build and
fixtures. They do not cover untested states, such as a host with no product DBs
at all, beyond the T1 fallback test.