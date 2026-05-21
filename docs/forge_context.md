# Forge — Architecture & Context

> How Forge is built, organized **by subsystem** rather than by phase. Read this to gain
> context fast and know where to make a change. Per-phase checkbox status lives in
> [`forge_progress.md`](forge_progress.md); the original blow-by-blow spec (with every
> resolved decision Q-number) is preserved in [`forge_implementation.md`](forge_implementation.md).
>
> Cross-references like *(P16.18)* point at the phase/step in the original spec where the
> decision and detail live.

## What Forge is

A software-factory dashboard: promote `docs/examples/forge-board.html` into a working app
that drives Azure DevOps + GitHub repos and runs **real, supervised LLM agent crews** that
plan, implement, review, and QA work end-to-end.

- **`src/forge/Forge.UI`** — React + TypeScript + Vite. Ports the prototype's layout/CSS
  verbatim, then grows new views. Talks to the API over REST + SignalR. Package `@forge/ui`,
  CSS scope `.forge-*`, dev server `:5181`.
- **`src/forge/Forge.Api`** — ASP.NET Core 8 minimal-API. **Single host** for REST, the
  SignalR hub, SQLite persistence, the provider layer, all background sync jobs, the agent
  runtime, the task-queue worker, and the story orchestrator. Default `:5180`.
- **Tests** — `src/forge/Forge.Api.Tests` (xUnit + `WebApplicationFactory`) and
  `src/forge/Forge.UI.Tests` (Playwright + Vitest).

> **⚠ Operational rule (CLAUDE.md):** never restart `Forge.Api` without explicit per-restart
> approval — it hosts long-running agent loops + queue work. `dotnet build` to compile-check
> is fine; Vite/HMR for front-end is fine.

## Running & restarting — `scripts\run-forge-ui.ps1`

The single launcher for both tiers. **Always use it** to start/stop/rebuild rather than `dotnet run`
or ad-hoc `taskkill`: it finds processes by name **and** by listening port (`:5180` API / `:5181` UI),
cleans stale PowerShell jobs, and launches `Forge.Api.exe` detached (no stdio-forwarding wrapper holding
the caller's console).

| Command | Effect |
|---|---|
| `.\scripts\run-forge-ui.ps1` | Stop, then start **both** API (`:5180`) + UI (`:5181`) — incremental build |
| `.\scripts\run-forge-ui.ps1 -Rebuild` | **Clean rebuild of both**: `dotnet build` (API) + `npm ci && npm run build` (UI), then start |
| `.\scripts\run-forge-ui.ps1 -ServerOnly` / `-ServerOnly -Rebuild` | Just the API — leaves a running Vite UI untouched |
| `.\scripts\run-forge-ui.ps1 -UIOnly` / `-UIOnly -Rebuild` | Just the Vite UI |
| `.\scripts\run-forge-ui.ps1 -Stop` (add `-ServerOnly` / `-UIOnly` to scope) | Stop |
| `.\scripts\run-forge-ui.ps1 -Status` | Show what's running on each port |

- **Pick up backend (`.cs`) edits:** `-ServerOnly -Rebuild` — still needs **per-restart approval** (rule above).
- **Front-end edits:** no restart — Vite HMR reloads them.
- **Compile-check only:** plain `dotnet build` is fine without restarting; the `Forge.Api.exe` copy-lock
  error (`MSB3027`/`MSB3021`) while the server is running is expected and harmless.

## Where to make a change (quick index)

| I want to… | Go to area |
|---|---|
| Add/alter a DB table or column | **Persistence & migrations** |
| Change how an agent is invoked (model/backend/CLI) | **Agents, crews & economy** |
| Change where/how work starts executing | **Task queue & dispatch** (`CrewQueueWorker`) |
| Change the generator↔evaluator iterate-to-90 behaviour | **Deliverable loop & guards** |
| Change ADO/GitHub read/write | **Providers** |
| Change sync cadence/cursor/pagination | **Sync subsystem** |
| Change story spawn lifecycle / plan docs / decomposition | **Story-driven spawn** |
| Change how PRs get committed/pushed/created or gated | **PR delivery & gates** |
| Change scoring/promotion/payroll | **Scoring, promotion & payroll** |
| Add a realtime event | **Realtime / messaging** |
| Add a UI view/store | **Frontend** |
| Change sign-in / identity / roles | **Identity & auth** |

## Foundational decisions (apply everywhere)

These were resolved up front *(P "Resolved decisions" Q1–Q11)* and later phases must respect them:

- **Provider parity (Q1).** ADO and GitHub are equal first-class; ship together, no tiering.
- **Worktrees live under each repo (Q2):** `{repo.cloneRoot}\.worktree\{slug}`. Canonical slugs
  (`WorkstreamWorktreeSlug.Compute` + `PlanDocStateHelpers`): `stage-{phase}.{stage}-{title}`,
  `plan-{docKey}`, `qa-{safeStoryId}`, `qa-steps-{wsId}`, `ws-{workItemId}` (ad-hoc / fallback),
  `merge-{base}` / `rebase-{base}`. **The production agent path keys the worktree by the
  work-item id (`ws-{wi}`), not the workstream name** — each item runs isolated. Worker
  auto-appends `.worktree/` to the repo's `.gitignore` (Phase 28+ uses `.git/info/exclude` for
  story attachments). Falls back to `%TEMP%` if the worktree can't be made (soft failure — can't push).
- **Repo ↔ provider exclusivity (Q4).** A repo is exactly one provider; sprint/iteration follows
  that provider (ADO iteration paths, GitHub milestones). **No local Forge sprint.**
- **Product ↔ repo is strictly 1:N (Q5).** `repos.product_id` NOT NULL, `UNIQUE(remoteUrl)`.
  **(Phase 80, in progress — `forge_implementation.md` §"Phase 80")** the repo *row* now lives in the
  **per-product DB** (`{slug}.db`), and the **domain is repo-scoped**: a story, its spawn, its
  workstreams, its work items, its PRs, and its live-exec all bind to one repo (`RepoId` required).
  Global uniqueness of `remoteUrl`/`cloneRoot` and `repoId→product` routing are preserved by a thin
  control-side **`repo_registry`** index. See §1 (boundary) and §5 (products/repos) below.
- **Auto-clone silently (Q6).** Empty `cloneRoot` → `git clone` on first use.
- **Auth: WAM first, device-code disabled (Q7); GitHub OAuth app not GitHub App (Q8).**
- **Crews are flat + global (Q9), multi-org from day one (Q10), any PO can promote/demote but
  the system keeps ≥1 PO (Q11).**
- **Fidelity to the prototype** is a hard constraint for Phases 0–4 (pixel-diff screenshot
  gates). New views (Products, Crews, Work items, Inbox, Live Exec, Swarm) are *exempt* from
  the `forge-board.html` diff but must use existing CSS tokens; new tokens are documented in
  `docs/forge/design-tokens.md`.

---

## 1. Persistence & migrations

**Stack:** `Microsoft.Data.Sqlite` + EF Core. **DB location is configured in
`Forge.Api/appsettings.json` under `Forge:Db` — NOT next to the binary.** Prefer
`Forge:Db:Folder` (default `E:\forge`): the **control DB** resolves to `{folder}\forge.db` and
**per-product split DBs** to `{folder}\{slug}.db`. `Forge:Db:Path` is **legacy** — when set it
wins and the control DB lives at that exact path (used by the test factory's per-test temp DB;
Docker sets `Forge__Db__Path=/data/forge.db`). `IForgeDbPaths`/`ForgeDbPaths` (singleton,
`Persistence/ForgeDbPaths.cs`) is the **only** path resolver — never inline a `forge.db` fallback.
The folder is the canonical "Forge state root" (`storyworkspaces/`, `attachments/` hang off it).
Entities in `Forge.Api/Persistence/Entities.cs`; `ForgeDbContext` registers them. Seeding in
`ForgeSeed` (idempotent on boot; reads `agents/allocation.json`).

**Per-product DB split (Phases A–G, `docs/forge/split-db.md`):** gated by `Forge:Db:SplitEnabled`
(+ `Forge:Db:MaxConcurrentProductDbs`, default 8). `ControlDbContext` binds the control DB
(cross-product entities — products, **`repo_registry`** (Phase 80; was the full `repos` table),
users, crews, agents, agent_usage, audit, idempotency); per-product entities route through
`IProductDbContextFactory` → `{slug}.db`. Product slugs are validated/sanitized
(`ForgeDbPaths.TryValidateSlug`, `[a-z0-9-]` + reserved-name guard) so they can't escape the folder
or shadow `forge.db`. Phase F runs the auto-split-on-first-boot migration; until then everything
stays in the control DB.

> **⚠ Phase 80 boundary change (in progress, `forge_implementation.md` §"Phase 80"):** the full
> **`repos`** row (config: clone fields, ADO/GitHub coords, build-gate commands, area paths, sync
> filters, `AgentReadyDefaultTeam`) **moves out of the control DB into each `{slug}.db`** so a repo's
> identity and its product live in the same file. The control DB keeps only a thin **`repo_registry`**
> index: `(repoId PK, productId, productSlug, kind, remoteUrl UNIQUE, cloneRoot UNIQUE, displayName,
> deletedAt)` **plus the mutable sync-due cursor** (`lastSyncStatus/lastSyncAt/isRunningSince/
> lastUpstreamChangedDate/lastSyncCursor` + per-repo cadence overrides). The registry is the source of
> truth for **global uniqueness** (Q2/Q5 — SQLite can't enforce `UNIQUE` across `{slug}.db` files) and
> for **`repoId→product` routing** (so `X-Forge-Repo-Id` alone resolves a product DB) and for **cheap
> sync enumeration** (workers scan one control query for due repos, not every product DB). It's
> maintained in lockstep on repo create/update/delete (**write product `repos` first, then upsert the
> registry mirror**, eventual-consistency repair like Phase E). Same denormalization philosophy as
> `agent_usage` (`ProductId/RepoId` snapshots) and the `product_runtime` watermark.

**Migrations** are hand-written idempotent `PhaseNNMigration.cs` classes (each guards with
`pragma_table_info` / `CREATE ... IF NOT EXISTS`), chained in `Program.cs` after
`EnsureCreatedAsync`, in order.

> **⚠ Migration class names diverge from phase numbers** because slots get taken by earlier
> additive migrations. Map: P27→`Phase31Migration`, P28→`Phase32Migration`, P29→`Phase34Migration`,
> P30→`Phase36Migration`, P58→`Phase79Migration`, P43 runs *before* Phase 38. When adding a
> migration, pick the next free `PhaseNN` slot on disk and wire it at the end of the chain — the
> number is a filename, not the phase.

**Key tables** (by subsystem): `epics/cards/columns/prs/events` (board), `work_items` +
`work_item_links` (WIT), `products` (control) + `repo_registry` (control, Phase 80) + `repos`
(per-product `{slug}.db`, Phase 80), `forge_user_settings`, `crews` + `crew_members` +
`crew_history`, `agents`, `agent_usage` (cost ledger), `models.json` (file, not table),
`domain_experts`, `deliverables` + `agent_level_history` + `agent_monthly_review` (scoring),
`task_queue` + `task_queue_history`, `task_transcript_event`, `agent_assignments`,
`product_iterations/product_area_paths/repo_iteration_bindings/product_saved_prefixes` (sprints),
`work_item_sync_runs` + `sync_run_items`, `pr_comments` + `pr_comment_history`,
`builds/build_stages/build_stage_logs/build_artifacts/build_artifact_files/build_failure_points/build_fix_pending`,
`story_spawns` + `workstream_deps` + `workstreams`, `agent_pair_assignment`,
`work_item_override_history` (Phase 57, **not yet created**), `forge_actions` (audit).

**SQLite gotchas (important):**
- EF SQLite **cannot translate `DateTimeOffset` comparisons / `SUM` over decimal-converter
  columns**. Crew salary / spend roll-ups are computed **in-memory** (`CrewsEndpoints.GetSalary`),
  not via SQL views. When filtering/ordering on a `DateTimeOffset`, materialize then filter, or
  coalesce nullables before comparing. *(matches user-memory rule)*
- `ALTER TABLE` can't add CHECK constraints in place — caps like `expected_file_changes <= 20`
  are enforced at the API layer instead *(P16.1)*.
- Atomic state transitions use compare-and-swap: `UPDATE … SET state=? WHERE id=? AND state=?`
  (e.g. `task_queue`) so concurrent workers never double-claim.

---

## 2. Realtime / messaging

**Today:** SignalR. `Forge.Api/Realtime/ForgeHub.cs` (`/hubs/forge`); clients join groups via a
generic `Subscribe("group")` invocation. `@microsoft/signalr` on the React side
(`useForgeHub.ts`). Events are stringly-typed and fanned out via `IHubContext<ForgeHub>` from
~70 call sites across endpoints, jobs, and `Persistence/ProviderCache.cs`.

**Channels / groups:**
- `card.changed`, `pr.changed`, `workitem.changed`, `story-spawn.changed`, `repo.changed`,
  `repo.validation-complete`, `queue.changed`, `queue.strained` — domain state.
- `agent.{agentId}` *(P11.7)* — token/tool/status stream per agent.
- `task.{workItemId}` *(P16.7, `TaskChannel.cs`)* — per-work-item aggregated live stream;
  **persisted** to `task_transcript_event` (10k-row lazy eviction) so the Live Exec view can
  replay on open. Events: `task.iteration.*`, `task.token`, `task.tool-call/result`,
  `task.commit`, `task.worktree-tick`, `task.pr-opened`, `task.build-*`, `task.sloppy*`,
  `task.delivery-failed`, etc.
- `pr.{prId}` — PR comment / state / build events.
- `user.{userId}` *(P17.6)* — per-user notifications (queue strain, await timeouts, open questions).

**Pattern to know:** the UI often listens to an event only to call `store.bump()` / `load()` and
re-fetch via REST (events aren't always authoritative payloads). `TaskChannel`/`ProviderCache`
sends are wrapped in `try/catch { log.Debug }` — send failures are currently swallowed.

**Planned refactor (Phase R.8, not started):** replace the ~70 direct hub calls with a typed,
durable, replayable **`IForgeBus`** + SQLite transactional **outbox** + **SSE** (`/api/events/stream`
with `Last-Event-ID` cursor). Bridge-first migration (`Forge:Realtime:Mode = legacy|dual|bus`),
Roslyn analyzer bans `IHubContext<ForgeHub>` outside the bus, `/admin/queue` DLQ UI. Events become
records under `Forge.Api/Contracts/Events/`. Until then, add a realtime event by sending from the
producing site and subscribing in the relevant Zustand store.

---

## 3. Frontend (`Forge.UI`)

**Architecture:** Vite + React + TS + react-router. Zustand stores per view under `src/state/`
and `src/realtime/`. API client `src/api/forgeApi.ts` (typed fetch, problem-details, honors
`VITE_FORGE_API_URL`, falls back to `sampleData.ts` with `?seed=local`). TS client generated from
OpenAPI via `npm run gen:api` → `src/api/generated/`.

**CSS:** the prototype's `<style>` block was split verbatim into `src/styles/*.css` (reset/theme/
shell/topbar/sidebar/main/filter-bar/board/timeline/workstream/pr/toast-modal/detail-panel). Not
CSS-modules/Tailwind (that's Phase R.1). Theme via `data-theme` on `<html>` + `localStorage forge.theme`.
New design tokens are catalogued in `docs/forge/design-tokens.md` (e.g. `--graph-line` P16,
swarm-control `--sc-*` palette P28).

**Views & their stores:**
- `/plan` `<Board/>` (`useBoardStore`), `<DetailPanel/>`, `<CardModal/>` — Phase 1.
- `/timeline` — Phase 2.
- `/workstreams` — Phase 3; now also the **swarm-control surface** (story index + per-story
  drill-down) *(P28.9)* via `SwarmIndexView`/`SwarmStoryView`/`SwarmMasterPanel` + `useSwarmUiStore`.
- `/prs` `<PrBoard/>`/`<PrCard/>`/`<PrDetailPanel/>` — Phase 4, comment list P24, build chip P27.
- `/products`, `/settings`, `/crews` (`CrewsView.tsx` ~2,100 lines), `/work-items`, `/inbox`
  (`InboxView.tsx` P17), `/work-items/{id}/live` (`LiveExecutionView.tsx` P16).
- Swarm stage numbering: `swarm/stageNumbering.ts` (`computeStageNumbers`) is the single source
  used by **both** `SwarmMasterPanel` (phase board) and `SwarmDetailPanel` (DAG/right pane) so their
  `{phase}.{stage}` labels match. qa-e2e rows are labelled `{implNum}-e2e` and **don't consume a
  stage slot** (fixes the `1.1 → 1.1-e2e → 1.3` gap). Pairing helper: `swarm/qaE2eSelector.ts`.
- `useTaskLiveStore` / `useTaskLive(workItemId)` *(P16.13)* — ref-counted attach to `task.{wid}`.
- `useQueueStore` / `useQueueChannel` *(P17.9)*, `useSpawnStore` *(P28)*, `useCurrentUserStore` *(P9)*.
- **Repo scope (Phase 80, in progress):** TopBar adds a cascading **repo switcher** beside the product
  switcher; `useReposStore.activeRepoId` (+ `localStorage forge.activeRepoId`) drives the
  `X-Forge-Repo-Id` header `forgeApi.ts` sends on every request. Board / `/workstreams` (incl. swarm) /
  `/prs` / Live-Exec / Spawn all filter to the active repo; an "all repos" selection clears the header
  for cross-repo views. See §5 "Repo scoping".

**`useForgeHub.ts`** holds a process-lifetime SignalR connection (a StrictMode workaround) and
dispatches events into stores. This becomes `useForgeStream.ts` (EventSource) under R.8.

---

## 4. Identity & auth (Phase 9)

`Forge.Api/Auth/` — landed as **`IAuthService`** (not `IIdentityService`); ADO + GitHub are
independent singletons; `/api/me` aggregates current user.

- **ADO:** MSAL.NET with **WAM broker** (`WithBroker(true)`); default client id = Azure CLI
  well-known (override `Forge:Auth:AdoClientId`); scope `499b84ac-…/.default`. **Device-code is
  banned** (Q7).
- **GitHub:** OAuth **auth-code + PKCE**, localhost callback `127.0.0.1:53682/forge/oauth/github/callback`,
  scopes `repo read:user read:org`. Operator must register an OAuth app + set `Forge:Auth:GitHubClientId`.
- **Browser launch:** Edge-first on Windows (`msedge.exe --app=` → `msedge.exe` → system default);
  `Forge:Auth:BrowserPreference`.
- **Token storage:** DPAPI-encrypted blobs under `%LOCALAPPDATA%\Forge\` (`msal-cache.bin`,
  `github-token.bin`). Never SQLite plaintext. Multi-org: tokens key on `(provider, accountId)` (Q10).
- **Roles:** `product-owner | tech-lead | arch | eng | rev | qa | human`. First sign-in to a fresh
  DB → `product-owner`. Gating via `RequireRole.Check(ctx, db, "product-owner")` minimal-API helper
  used inside handlers (single-user dev installs pass through). `useHasRole()` gates UI.
- Headless/CI fallback only: PAT in user-secrets (`Forge:Ado:Pat`, `Forge:GitHub:Pat`).

---

## 5. Products, repos & sprints

**Products/repos (Phase 8):** `Product { id, name, slug, ownerUserId, defaultRepoId? (Phase 80) }`
and `Repo { id, productId, kind:'ado'|'github', remoteUrl, displayName, defaultBranch, cloneRoot,
adoOrg?, adoProject?, ghOwner?, ghRepo? }`. `ProductsEndpoints.cs` (repos endpoints live here too,
not a separate file). Auto-clone via `IRepoCloneService` streams `forge.repo.clone.*` SignalR. The
`Repo` row also carries the sync-scope and build-gate config (see below).

**Repo carries three independent shapes** *(P23.3)*: git fields (`remoteUrl/cloneRoot/defaultBranch`
for worktrees), ADO work-item-sync scope (`adoOrg/adoProject/AreaPathsJson`), and build-gate config
(`InitCommand/RestoreCommand/BuildCommand/TestCommand` + timeouts, P26). Any set may be empty.

**Repo scoping (Phase 80, in progress — `forge_implementation.md` §"Phase 80").** A product owns
**many** repos and every work item / story / spawn / workstream / PR / live-exec binds to exactly one
(`RepoId` required, FK conceptually to the per-product `repos` row). Two layers of scope now stack:
- **DB routing** stays product-keyed — `{slug}.db` is the file, selected by `X-Forge-Product-Id`
  (header) or resolved from `X-Forge-Repo-Id` via the control `repo_registry`.
- **Domain filtering** is repo-keyed — a new **`X-Forge-Repo-Id`** header + `RepoScope` helper +
  `RepoScopeMiddleware` stash the active repo on `HttpContext.Items`; repo-scoped endpoints filter
  `WHERE RepoId = …`. Absent / `all` = every repo in the product (cross-repo views).
- **UI:** TopBar gains a **cascading repo switcher** beside the product switcher
  (`localStorage forge.activeProductId` + `forge.activeRepoId`); `useReposStore.activeRepoId` drives
  the `X-Forge-Repo-Id` header in `forgeApi.ts`. Board / Workstreams / PRs / Live-Exec / Spawn all
  read the active repo. `Product.defaultRepoId` seeds the initial selection and is the backfill
  target for legacy null-`RepoId` rows.
- The full `repos` row lives in `{slug}.db`; `control.repo_registry` is the routing + uniqueness +
  sync-due mirror (see §1 boundary note). **Code that read `control.Repos` now reads the per-product
  `repos` set (config) or `repo_registry` (routing/sync enumeration).**

**Sprints / area paths (Phase 15):** product-scoped registries `product_iterations`,
`product_area_paths`, `repo_iteration_bindings`, `product_saved_prefixes`. Read-only mirror of the
provider (never writes upstream nodes). `IAdoClassificationService.DiscoverAsync` walks ADO
classification nodes by **prefix** (the headline UX — `Atlas\Sprint*`); GitHub uses milestones.
`SprintManager.tsx` modal is the shared surface (reused from Plan & Design and Products). Orphan
nodes flip to `provider-orphan` but keep bindings. **No local Forge sprint** (Q4 holds).

---

## 6. Providers (Phase 5/6)

`Forge.Api/Providers/` — `IForgeProvider` with `AdoProvider` + `GitHubProvider`. **Both use
`HttpClient` + REST directly** (not the TFS client lib, not Octokit, not GraphQL except where
forced) — "keeps deps small." Tests use a ~50-line `StubHttpMessageHandler` (not WireMock).

- **Read path (P5):** map ADO work item / GitHub issue+PR → `Card`/`Pr`/`WorkItem`. State model
  normalized to four states *(P12.3)*. `RemoteRevision` drives last-writer-wins conflict policy
  (server-side 409 + revision; UI badge is Phase R).
- **Write path (P6):** create/update WI, comment, create PR, review actions, trigger/cancel/re-run
  pipelines, auto-merge watcher, human-gate, audit (`forge_actions`), idempotency keys on every write.
- **`PrCoordsResolver.Resolve(repo, kind)`** is the single canonical repo-coordinate resolver —
  extracts the slug after `_git/` for ADO (NOT the display name — `TF401019` if you use the
  friendly alias), `GhOwner/GhRepo` for GitHub. Used by `CrewQueueWorker`, `WorkItemsEndpoints`,
  and story-spawn PR creation so a new provider kind is wired in one place *(P45)*.
- **`AdoProvider.CreatePullRequestAsync`** reads the response body before throwing so the actual
  `TF…` code reaches the live-exec view *(P16.9)*.
- Webhook intake `POST /api/webhooks/{provider}` (HMAC, poll fallback). Per-repo GitHub webhooks
  installed on attach (Q8).

---

## 7. Sync subsystem (Phases 23, 24, 27, 29)

Three independent scheduled hosted services, all sharing the same shape (cadence + jitter +
per-repo eligibility gate + delta cursor + cursor-resume pagination + `SyncConstants` spec-test):

| Job | Watches | Cadence | Cursor |
|---|---|---|---|
| `WorkItemSyncJob` *(P23)* | repos' work items | **2 h** (P29; was 20 min) | `LastUpstreamChangedDate` (P29; was `LastSyncAt`) |
| `PrSyncJob` *(P24)* | active PRs (`IsActive=1`) | 2 h | `LastUpstreamUpdatedAt` |
| `BuildSyncJob` *(P27)* | builds on active PRs' branches | 2 h | per-build state |

**`SyncConstants.cs`** locks cadence, window, page sizes, retry caps, the `agent-ready` literal,
build-failure regex patterns — asserted verbatim by per-phase spec-consistency tests
(`Phase23SyncConstantsTests`, etc.). Initial window 30 days both providers (P29).

**Cadence/cursor strategy (P29):** global tick at 7200s ±60s jitter; per-repo overrides
(`RepoRow.{WorkItem,Pr,Build}SyncIntervalSeconds`) can only *slow* a repo. Eligibility gates
`IsDueForSync`/`IsPrDueForSync`/`IsBuildDueForSweep` filter inside the tick; stale `running` rows
(>19 min via `IsRunningSince`) always pass (recovery beats cadence). Cursor anchors to the **MAX
upstream changedate observed** (local clock drifts and races with in-flight writes); advances only
on a successful run that saw ≥1 item.

**Pagination:** ADO = WIQL refs (≤20k) then `?ids=` batches of **200**, cursor = batch index;
GitHub = `Link: rel="next"` walk, cursor = next URL. Mid-run failure leaves the cursor at the last
committed page. `MaxParallel = 4` (WI) / `8` (PR/Build).

**`agent-ready` auto-assign (P23.8, `AgentReadyResolver`):** upstream tag/label `agent-ready` →
sets `AssignedTo = {kind:'team', id: repo.AgentReadyDefaultTeam ?? 'Engineering'}` **only when
`AssignedTo` is null or legacy-runtime-shaped** (operator intent wins). One-way ratchet; writes an
audit row; **never touches `AssignedAgent`** (Phase 22 boundary).

**PR comment lifecycle (P24):** `pr_comments` mirror threads; **agents post, only humans resolve**
(`POST .../resolve` → 403 `comment.agent_cannot_resolve` for agents). `PrCommentLinker` links
comment→commit (body tags `Addresses comment <id>` / position-and-time heuristic) and
comment→reply; `RequiresAttention` drives a `comment-response` queue task (pinned to the original
generator, Q24.14). Worktree cleanup on PR terminal state (`PrSyncJob` removes `.worktree/ws-{wi}`
+ local branch).

**Build repair (P27):** failed builds → `build_stages`/`build_stage_logs` (256 KB tail) →
`BuildFailureParser` (regex pass, artifact-descent pass, else `blocked` for human triage) →
`build-fix` queue task (same iterate-to-90 loop). Push re-triggers CI naturally (no manual rerun).
`BuildArtifactExpander` has a strict zip-slip guard.

---

## 8. Work items & WIT (Phases 12, 19, 22)

`WorkItemRow` *(P12.1)* — the first-class work item, superset of the legacy `Card`. Two parallel
three-level ladders tagged by `Track`: dev `epic → feature → task|bug`, design `phase → stage → step`.
Four normalized states `proposed|active|resolved|closed`. `WorkItemsEndpoints.cs` (GET/POST/PATCH/
DELETE/move/parent/sync); `/work-items` view has board/timeline/tree/list modes. Cards still serve
the active-sprint slice side-by-side (cards-as-projection deferred to Phase R).

> **Repo binding (Phase 80, in progress).** `WorkItemRow.RepoId` becomes **required** (was nullable);
> create/sync must set it (defaulting to `Product.defaultRepoId`), endpoints filter by the active
> `X-Forge-Repo-Id`, and a backfill stamps legacy null rows to the product's default repo. The
> worktree path (`{repo.cloneRoot}\.worktree\ws-{wi}`) and PR coordinates (`PrCoordsResolver`)
> already key off `wi.RepoId`; the change is making it non-null + filtered, not re-plumbing it.

**Two assignee fields — they never auto-sync** *(P22, critical):*
- **`AssignedTo`** (`AssignedToJson`) — **WIT semantic**: who *owns* it in the tracker. Set by
  operator / provider sync / `agent-ready`. Survives every execution transition. `kind ∈ agent|human|team`.
- **`AssignedAgent`** (`AssignedAgentJson`) — **runtime fact**: who is *executing* now. Set by
  `CrewQueueWorker` on claim (writes `{agents[], team, crewId, role, assignedAt, assignedBy}`),
  cleared on Restart / close. `agents[0]` is the canonical driver (pinned for comment-response /
  build-fix continuity). Every change appends an `agent_assignments` history row. **The queue
  worker stopped writing `AssignedTo`** — runtime routing flows through `AssignedAgent` only.
  (P22 backend complete; the UI strips 22.8–22.10 are not built yet.)

**Execution status & trial counter (P19):** `WorkItemRow.ExecutionStatus` ∈
`idle|queued|live|completed|failed` (+`cooldown-budget`/`pending-reassign` from P51/P52) is the
cheap projection that drives the `<ExecBadge>` and `● N live` pills, kept in lockstep with the
queue state machine by `CrewQueueWorker`. `TrialCount` bumps on **claim**, not enqueue. No
automatic retry — `failed` stays failed until operator Restart.

---

## 9. Agents, crews & economy (Phases 11, 13, 18)

**Runner:** `Forge.Api/Agents/` — `IAgentRunner.StartAsync(...)` returns an `AgentInvocation`
streaming `AgentEvent`s (token/tool/status). The single registration is **`AgentRunnerDispatcher`**
(renamed from `ClaudeAgentRunner`), which routes each call through one of **four (backend × mode)
cells** picked from `AgentContext.Backend`/`InvokeMode` (defaults **Copilot · SDK**) *(P11.5b)*:
- **Copilot·SDK** — in-process `CopilotClient` (GitHub.Copilot.SDK). **Fully tool-capable.**
- **Copilot·CLI** / **Claude·CLI** — shell out, **text-only (no tools)**.
- **Claude·SDK** — POST `/v1/messages` streaming + ephemeral prompt cache on the soul.md block.
- `local-stub` model bypasses all cells with a deterministic token stream for tests/offline.

> **⚠ Two open gaps:** **11.5b** (the dispatcher abstraction) and **13.4a** (Claude·SDK doesn't
> declare `tools:` to Anthropic) are both `[ ]`. Until 13.4a lands, **only `(copilot, sdk)` can
> actually call tools** (`create_work_item`, `git_commit`, …); `(claude, sdk)` emits text only.
> Tool-using task kinds (`plan`, `coding`) refuse CLI crews with `crew.tools_unavailable_in_cli`.

**Model registry** (`models.json`, `GET /api/models`): id/vendor/pricing/contextWindow/supportsTools.
Pricing is **denormalized onto each `Crew` row** at assignment so historical salary is reproducible.

**soul.md** *(P11.3, P13.3)*: per-agent system prompt at `%LOCALAPPDATA%\Forge\agents\{id}\soul.md`
(path on the agent row). YAML front-matter declares `capabilities:`, `tools:`, and (planned, P30)
`acp_topics:`. Capability = content-addressed `domain-expert.md` ids; system prompt =
`<preamble> + <soul body> + <capabilities sorted>`. File-watcher hot-reloads.

**Cost ledger** `agent_usage(agentId, crewId, taskId, model, in/outTokens, costUsd, started/endedAt,
status, cost_category)` — source of truth for spend. `cost_category ∈ generator|evaluator|judge`
(P16.2). Copilot is subscription-billed but Forge computes an **estimated** USD from the registry
price (`est.` pill in UI). Roll-ups are **in-memory** (SQLite can't `SUM` the decimal-converter
column over `DateTimeOffset` filters).

**Crews** *(P10.6, P13.1)*: **global, not product-scoped** — any PO manages any crew. `CrewRow`
carries `ParentCrewId/Depth` (cap 3), `LeadAgentId`, members (`crew_members`), `Model`, `Backend`,
`InvokeMode`, `Capabilities`, `MaxConcurrent`, `WeeklyCap`, `dailyBudgetUsd`, `MaxTaskBudgetUsd`,
`Shift`, `pair_role`/`paired_with_crew_id`, `timezone`/`schedule_enabled`. Changes write
`crew_history`. Lead routes (`POST /api/crews/{id}/assign`), only the lead/PO can assign.

**Paired crews (P16.2):** arch+eng crews pair generator-crew ↔ evaluator-crew (symmetric, one pair
max). The evaluator crew is pinned per deliverable (preserves P14.3 same-evaluator invariant).
Review/QA crews stay self-evaluating.

**Schedule gate & shifts (P14.2, P18):** `ScheduleGate` — Mon–Sat `[09,21)` day band / `[21,09)`
night band in the crew TZ. `EvaluateAgent(crew, agentId, now)` exempts the **lead 24×7** (Q18.3) so
a Sunday/off-band ticket routes to the lead instead of parking. Off-shift dispatch returns 409
`crew.off_shift` (lead `?override=true`). Locked numbers in `ScoringConstants`.

**Role taxonomy (P13.1 expanded):** 91 agents across three axes — **Team** (`Architect|Engineer|
Review|QA`), **Pod** (`leadership|plan-design|coding|ai-worker|reserve`), **Role bucket**
(fine-grained, drives soul.md + prompt template). Single source `agents/allocation.json`
(regenerated by `scripts/gen-crew-allocation.py`, TS bridge `src/agentAllocation.ts`); `ForgeSeed`
+ `AgentAllocationLoader.ForTeam(...)` read it on boot.

---

## 10. Scoring, promotion & payroll (Phase 14)

> Source spec: `docs/forge/scoring-and-promotion.md`. **`ScoringConstants.cs` is the locked source
> of truth** (rubric weights, level table, hourly curve `4 + level*3`, iteration cap 5, schedule
> window, thresholds). `Phase14SpecConsistencyTests` asserts code == spec verbatim — never re-derive
> a number, edit the constant and the spec together.

- **`deliverables`** row per iteration: `{score, dims(correctness≤40/quality≤25/tests≤15/scope≤10/
  risk≤10), verdict(pass|iterate|reject|stalled|...), notes, gen_cost_usd, eval_cost_usd, judge_*}`.
- **`EvaluatorScoreParser`** parses a fenced ```json``` block from the evaluator's last message;
  verdict is *derived* from score + work-item points (reject threshold scales down for bigger items);
  parse-fail → safe `iterate` with `score=0`.
- **Promotion** (`PromotionPolicy` + `PromotionRunner`): trailing-20 deliverables → promote at
  ≥92 + zero rejects + 30-day cooldown; demote at <75 (or sloppy-rate >0.15, P52.7). Lead floor
  level ≥7. Nightly scheduler + monthly `agent_monthly_review` snapshot are **`[ ]` (14.7/14.15)**.
- **Payroll vs spend (Q14.7):** `CrewSpend` = token cost (existing); `CrewPayroll` =
  `hourly × on-shift hours` (`PayrollCalculator`, TZ-aware, lead 168 h/wk vs member ≤72 h/wk).
  TopBar shows both chips.
- **KPIs** (`IAgentKpiService`/`AgentKpiCalculator`, partial): nine KPIs + sloppy-rate, per window
  (`trailing20|mtd|lastMonth|monthOf`). UI Performance tab + `AgentScorecardDrawer` partly built.

---

## 11. Task queue & dispatch (Phases 16, 17) — **the execution backbone**

This is the most important runtime area. **`CrewQueueWorker` is the SOLE place agents start.**

- **`task_queue`** *(P17.1)* — one row per active routing. State machine `awaiting-ack →
  awaiting-route → queued → picked-up → completed|cancelled|reassigned|blocked` (+`awaiting-context`
  planned P30). Atomic CAS transitions. Partial-unique `UNIQUE(workItemId) WHERE state IN (open
  states)` = at most one open row per work item (idempotency, Q17.6). `task_queue_history` is the
  append-only audit. Carries `PriorityRank`, `MaxIterOverride` (P20), `SkipSoulPrompt` (P21),
  `PreferredAgentId` (P24/P30), `Kind ∈ coding|comment-response|build-fix|plan`.
- **`ITaskQueueService`** — `Enqueue/Acknowledge/RouteToCrew/ClaimNext/Complete/Reassign/Cancel/
  Block/GetCrewStats`. `ClaimNextAsync` is the atomic head-of-queue pop.
- **`LeadAutoDispatcher`** *(P16.3/P17.12)* — **producer only**: 5s loop scans
  `work_items WHERE assignedTo.id ∈ lead_agents AND auto_dispatched_at IS NULL`, applies schedule
  gate + capacity + pair resolution, then **enqueues** (`EnqueueAsync(wi, "crew", generatorCrew)`).
  Stamps `AutoDispatchedAt` only after a successful enqueue. **Never invokes the runner.**
- **`CrewQueueWorker`** *(P17.4)* — single 5s tick, **sole consumer**, with a boot-recovery pass
  (`picked-up → queued`) + five `SafeSweepAsync`-wrapped sweeps:
  1. **ack-timeout** (auto-ack stale `awaiting-ack`),
  2. **claim head** — capacity check, pick generator+evaluator via `PairDiversityResolver`
     (filters busy + off-shift + prior pairs; lead is the off-shift fallback per Q18.3; honors
     `PreferredAgentId` pin), stamp `AutoDispatchedAt`/`ExecutionStatus=live`/`TrialCount++`, write
     `AssignedAgent` + `agent_assignments`, then `Task.Run(RunAgentAsync)`,
  3. **strain detection** (`queue.strained` to the lead),
  4. **abandoned recovery** (24h `picked-up`),
  5. **live-badge reconciliation** (heals lying `ExecutionStatus`).
  `RunOnceAsync` returns per-sweep counts for deterministic tests.
- **`RunAgentAsync`** = the per-task entry point. Branches on `Kind`, builds the prompt, runs
  `DeliverableLoop`, then the **server-owned delivery pipeline** (commit+push+PR, see area 13).
  Has a re-execution guard (skips items already `in-review/resolved/closed`).
- **`/inbox`** *(P17.7)* surfaces pending-acks (10s countdown), active queue per crew (depth/ETA),
  and the strain banner (Confirm / Reassign).

---

## 12. Deliverable loop & guards

**`DeliverableLoop` (`Services/DeliverableLoop.cs`, `IDeliverableLoop`)** *(P14.4, P16.4)* — the
generator→evaluator **iterate-to-90** engine. Pure: takes generator/evaluator delegates so it's
testable without a live runner. `RunAsync(workItemId, generatorCrewId, evaluatorCrewId?, kind)`.
Per iteration: generator runs → evaluator scores → persist `deliverable` → derive verdict:
`≥90 → pass` (close item), `[reject..89] → iterate` (re-enter with notes), `<reject → reject`
(bounce to lead), 5-iter / budget / time caps → `stalled`/`stalled-budget`/`paused-eod`. Evaluator
agent + crew pinned across iterations (Q14.3/P16.2). Cost split generator/evaluator. Emits
`task.iteration.*`. `kind` drives the evaluator preamble (comment-response scope = union of comments;
build-fix = "did the fix address the parsed failure points").

**Guards layered on the loop:**
- **Per-row max-iter override** *(P20)* — `task_queue.MaxIterOverride` clamp [1,10] (P57 adds a
  work-item-level `MaxIterOverride` that wins over the queue value — **P57 not started**).
- **Restart / skip-soul** *(P21)* — `DELETE /work-items/{id}/transcript` wipes transcript +
  deliverables (iter resets); `task_queue.SkipSoulPrompt` zeros the soul prefix for diagnosis;
  Copilot `maxTurns = 30 × points`, wall-clock `30 min × points`.
- **Pair diversity** *(P47, `PairDiversityResolver`)* — parallel deliverables on one crew get
  distinct (gen, eval) pairs. Busy = `agent_usage WHERE EndedAt IS NULL` (24h reaper for crashed
  runners). No free pair → skip the claim (`no-free-pair-member`), don't silently serialize on the lead.
- **Plan-doc completion gate** *(P48, `ArePlanDocsAllPassedAsync`)* — four orchestrator entry points
  refuse to advance / regress to `plan` until every plan doc's latest verdict = pass.
- **Budget-cap auto-recovery** *(P51, `BudgetCooldownReconciler` 15s)* — `stalled-budget` parks in
  `cooldown-budget`; resumes on cap-raise (same pair) or cooldown-elapse (rotate pair, reset budget
  window via `BudgetWindowStartUtc`).
- **Sloppy-iteration guard** *(P52, `SloppyDetector` + `SloppyReassignmentReconciler`)* — was
  score-plateau + 500K-token triggers; **both disabled 2026-05-13** (cumulative-token meter was
  noise on large rewrites). The 5-iter cap + `MaxTaskBudgetUsd` dollar cap remain the guards.
  Reassignment = full clean slate (wipe transcript/deliverables/budget window, `git reset --hard`),
  3-pair-attempt cap (`PairAttemptCount`/`PriorPairsJson`), then terminal failure. `agent_pair_assignment`
  is the per-pair audit + KPI feed. Restart resets all P52 + cost-ledger state (P52.12.2).
- **Prompt-growth metric** *(P54)* — emits `task.iteration.prompt-growth` / `task.prompt-bloat`
  (>30K input-token iter-over-iter growth); `agent_usage.TurnSamplesJson`.
- **Gate-failure recovery** *(P56)* — per-iter gate chains build→quality→test feed failures back
  into the next generator prompt; post-pass `OnPassValidate` callback demotes pass→iterate (cap 3)
  instead of stalling; `BuildFailedException` carries `Stage`+`Command`.
- **Operator cap-override** *(P57, NOT STARTED)* — planned non-destructive sibling of Restart
  (`POST /api/work-items/{id}/override`) that bumps max-iter / resets budget window / clears
  sloppy-terminal while **preserving** deliverables/transcript/worktree.

---

## 13. PR delivery & gates

**Server owns git delivery, agent owns content (Q16.9).** The agent edits files in the worktree,
runs build/tests, and ends with a `## Change Summary`. The agent is now **forbidden from
`git commit/push`** (P45) — it inconsistently committed/skipped PRs. `CrewQueueWorker` (post-pass
branch) does it:
1. **Build/test gate** *(P26)* — before push, run repo's `Init→Restore→Build→Test` commands
   (`RunCommandAsync`, no shell, process-tree kill on timeout, 16 KB output cap). Failure aborts
   delivery, marks the item failed, opens no PR. `BuildSystemDetector` auto-discovers commands on
   repo attach; `BuildValidationWorker` runs async validation from the Edit-Repo modal.
2. **Auto-commit** dirty tree, **`git fetch` + `git push --force-with-lease`** (was; **P79c switched
   conflict recovery to merge-based plain push** so reviewer comments stay anchored to reachable SHAs).
3. **PR create** server-side via `provider.CreatePullRequestAsync` (rich body, work-item link,
   evaluator review, score; clamped to 3900 chars). Pre-flight validation refuses empty/no-context PRs.
   Emits `task.branch-pushed` / `task.pr-failed` / `task.delivery-failed`.

**Conflict recovery (P79c, merge-based):** `IStoryGitOps.MergeBaseIntoBranchAndDetectConflictsAsync`
→ agent resolves (`ProduceConflictResolutionAsync`) → `ContinueMergeAndPushAsync` (defense-in-depth
marker scan, plain push). `MaxResolveAttempts=3`.

**Auto-merge race fix (P57b):** `AutoMergeWatcher` refuses to merge a workstream PR with a null
Review verdict (`reviewer-not-run`); `DriveReviewAsync` stamps `verdict="pending"` on first sight.

**Test-gate impacted selection + logs (P59):** the test gate narrows `dotnet test` to **impacted
test projects/methods** via a provider chain `IImpactedTestProvider`: Roslyn (C#, in-process via
`Microsoft.CodeAnalysis.Workspaces.MSBuild` + `MSBuildLocator`) → LSP (Rust/Go/Python/TS via
`MinimalLspClient`) → heuristic fallback. `mode: roslyn|lsp|heuristic` is stamped on gate events.
Combined gate logs persist to `.worktree/{slug}/.forge/logs/` and stream via
`GET /api/workstreams/{wsId}/logs/{*relPath}` (Range + traversal guard); UI renders a `📎 view log` chip.

---

## 14. Story-driven spawn / orchestration (Phases 28, 43, 45, 46, 53, 58; ACP 30 planned)

Replaces ad-hoc "spawn workstream". The operator selects a **story** (ADO feature/PBI) and Forge
runs the full lifecycle across Architect/Engineer/Review/QA crews.

> **Story is under a repo (Phase 80, in progress).** Today `ProductStoryRow` is product-scoped and
> the *spawn* picks the repo (`StorySpawnRow.RepoId`, set from `SpawnRequest.RepoId`); Phase 80 pushes
> the repo binding **up to the story**: `ProductStoryRow` gains a required `RepoId`, the story index
> and spawn UI filter by the active `X-Forge-Repo-Id`, and `StorySpawnRow.RepoId` is inherited from
> the story (no longer operator-chosen at spawn time). `StorySpawnRow.RepoId` and the orchestrator's
> `repo` lookup become non-null; the base-branch propagation that today loops "every repo of the
> product" narrows to the story's repo. All plan-doc / workstream / qa-e2e branches + worktrees +
> PRs already key off that one repo's `cloneRoot` — Phase 80 makes the binding required, not new.

**`StorySpawnOrchestrator`** is the state machine: `DriveAsync(storyId)` is a pure function over
`(spawn, story, repo, state)`; every step idempotent (re-tick = no-op). Driven by the
`SpawnDependencyScheduler` background tick (fans out per spawn via `Task.WhenAll`) and inline after
each transition. Phases: `plan → decompose → implement → review → done` (P58 dissolved the batch
`qa` phase into per-stage `qa-e2e` workstreams; `qa` retained only for legacy drain).

**Three swappable interfaces (the test/prod seam):**
- **`IStoryArtifactStrategy`** — produces content. Tests bind `DeterministicStoryArtifactStrategy`
  (runs without LLMs); **production swaps one DI binding** to `CrewStoryArtifactStrategy` (real
  crew agents via the runner). Methods: `ProduceSinglePlanArtifactAsync` (P53), `ProduceDecomposition`,
  `ProduceWorkstreamChanges`, `ProduceReviewFix`, `ProduceStageE2e` (P58), `ProduceConflictResolution`.
- **`IStoryGitOps`** — `InMemoryStoryGitOps` (tests) vs `RealStoryGitOps` (git CLI, branch-level
  locks). Engineer agents now **edit real repo files in-place in the worktree** (no `src/.story-
  artifacts/` sandbox prefix, P45); `GetChangedFilesAsync` combines `git status` + committed-but-
  unpushed `git diff origin/{branch}..HEAD` so agent self-commits flow through.
- **`IStoryProviderOps`** — `LocalStoryProviderOps` (writes `PrRow`/`PrCommentRow`/`WorkItemRow`
  + SignalR, tests) vs `ProviderStoryProviderOps` (real ADO/GitHub via `IForgeProvider`).

**Plan docs are sequential (P53):** `architecture → tech-spec → implementation-plan → e2e-scenarios`,
one branch + worktree + PR each; doc N+1 waits for doc N's PR merged AND verdict=pass.
`StorySpawnRow.PlanDocPrsJson` discriminates new (`DrivePlanSequentialAsync`) vs legacy
(`DrivePlanLegacyAsync`, `null`). `PlanDocStateHelpers.PlanDocOrder` is the canonical order.
Multiple plan docs run with **distinct evaluators** (P47).

**Dependencies (P46 + e2e gate):** `DecompositionDepsResolver.ResolveAll` projects per-stage +
per-phase deps into title-space; **both** `ProduceDecompositionAsync` and `PlanReconciler` consume
it (the reconciler re-derives `DepsJson` every tick, so any dep transform must live in the resolver
or it'll fight the orchestrator). Operator gate `Forge:Stories:GateImplOnPriorStageE2e` (**default
on**, `DecompositionDepsResolver.GateImplDepsOnPriorStageE2e`) rewrites an impl dep `D` → `"D — E2E"`
when `D` has a qa-e2e companion, so a stage waits for the prior stage's impl **and** e2e (not just
its impl PR). Off = legacy parallel behaviour. See `docs/forge/e2e.md` §2.1.

**Plan layout v2 (P46):** `implementation-plan.md` uses H1=Phase / H2=Stage / H3=subsection +
YAML front-matter; per-stage + per-phase `### Dependencies`; two-level deps (phase deps expand to a
stage cross-product); `workstreams.yaml` (replaced `.json`). `PlanParser` keeps v1 back-compat.
DAG view (`DecompositionView.tsx`) renders ELK phase containers + flags floating cards. Caps:
≤20 stages/phase, ≤20 steps/stage, ≤20 files/step (`PlanRubricConstants`, enforced API-side P16.5).

**Attachments & operator ACCEPT (P43):** `StorySpawnRow.AttachmentsJson` + on-disk files copied to
`{worktree}/.forge-attachments/` (excluded via `.git/info/exclude` + `ReadAllFiles` filter so they
don't leak into PRs). **ACCEPT** (`POST .../artifacts/{key}/accept`) flips a sub-90 plan-doc
deliverable to pass, cancels in-flight work, and opens a PR against the current base.

**Base branch (P45):** `StorySpawnRow.BaseBranch` + `ProductStoryRow.BaseBranch` (default `main`).
Changing it mid-flight rebases the new base on main, replays merged PRs onto it, and retargets open
PRs (`UpdatePrBaseBranchAsync` — local cache only; upstream PR-edit is forge_v2). Story-level PATCH
runs the same full retarget when a spawn exists.

**ACP (Phase 30, NOT STARTED):** planned cross-agent bus (`IAcpBus`, `acp_*` tables, BeeAI ACP v0.1
wire format, `/acp/*` endpoints) for cross-stream context, prompt-preamble injection of shared
contexts, a silo auditor, and a false-parallel planner. None of `30.1–30.17` exists yet.

---

## 15. Telemetry & audit

- **Audit:** `forge_actions(who, what, when, request body, response status)` surfaced read-only at
  `/audit` *(P6.10)*. Per-crew audit also in `crew_history`; per-queue in `task_queue_history`;
  per-assignment in `agent_assignments`; per-comment in `pr_comment_history`. Cross-product PO
  actions tagged `supervising=true`.
- **OTel-ish metrics:** stable-name `ILogger` messages + counters/histograms on a shared
  `Forge.Api.Phase16` meter (re-used to avoid dashboard fragmentation): `forge.dispatch.*`,
  `forge.queue.*`, `forge.sync.{workitem,pr,build}.*`, `forge.deliverable.*`, `forge.agent.*`,
  `forge.sloppy.*`, `forge.budget.cooldown.*`. Full OTel SDK exporter is deferred to forge_v2.
- **Spec-consistency tests** are the "fidelity gate" for post-prototype phases:
  `Phase14SpecConsistencyTests`, `Phase23SyncConstantsTests`, etc., parse the spec and assert
  `ScoringConstants`/`SyncConstants`/`PlanRubricConstants` match verbatim — drift fails CI.

---

## 16. Testing conventions

- **Backend:** xUnit + `WebApplicationFactory<Program>`; per-phase files `PhaseNN…Tests.cs`. HTTP
  is faked with `StubHttpMessageHandler` (no WireMock). Hosted services expose `RunOnceAsync`/
  `TickOnceAsync` for deterministic single-tick driving. Orchestrator tests bind the in-memory
  story-ops + deterministic strategy and serialize the DB via `[Collection("Phase28")]`.
- **Frontend:** Vitest (pure logic + `expectTypeOf` contracts) and Playwright (live dev UI).
  Playwright specs that need a live agent run **SKIP** with a clear message until a real
  `logUrl`/transcript event lands (e.g. P59.T18–T21).
- **Forge.Api.Tests build copies `Forge.Api.exe`** — stop the running server before
  `dotnet test src/forge/Forge.Api.Tests`, then restart it (with operator approval).

---

## Known not-started / partial (read before assuming something exists)

- **Phase 80 (repos → product DB + repo registry + repo-scoped domain + topbar repo switcher)** —
  **in progress, driven by `forge_implementation.md` §"Phase 80".** Until it lands: the full `repos`
  row still lives in the **control DB** (`ControlDbContext.Repos`), there is **no `repo_registry`**, no
  `X-Forge-Repo-Id` header / `RepoScopeMiddleware`, no topbar repo switcher, `ProductStoryRow` has no
  `RepoId`, `Product` has no `defaultRepoId`, and `WorkItemRow.RepoId` / `StorySpawnRow.RepoId` remain
  nullable. Code still reads `control.Repos`. Don't assume any Phase 80 surface exists yet.
- **Phase 30 (ACP)** — entirely `[ ]`.
- **Phase 57 (operator cap-override)** — entirely `[ ]`; `Phase57Migration` + `work_item_override_history`
  table do **not** exist yet.
- **Phase R** (incl. R.8 `IForgeBus`/outbox/SSE) — deferred; realtime is still direct SignalR.
- **11.5b / 13.4a** — the 2×2 dispatcher abstraction and Claude·SDK tool surface; today only
  `(copilot, sdk)` is fully tool-capable.
- **Phase 28 EX.1–EX.8** — Fibonacci budgets, 3-trial model rotation, four typed plan WI kinds,
  per-Phase test gate: spec-confirmed, not wired.
- Various UI tails are `[~]` (P22.8–22.10 assignment strips, P52.7/52.8 scorecard wiring, P14
  performance/KPI surfaces) — backends ship ahead of the UI.
