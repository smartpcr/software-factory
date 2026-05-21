# Forge — Implementation Progress

> Status tracker. Checkbox state per implementation step + per-phase test status.
> **Architecture / "how it works" lives in [`forge_context.md`](forge_context.md).**
> Full original spec preserved in [`forge_implementation.md`](forge_implementation.md).

Checkbox states: `[ ]` not started · `[~]` in progress / partial · `[x]` done (merged + verified).

> **Note on migration numbering:** the on-disk EF migration class name often diverges
> from the phase number (slots get taken by earlier additive migrations). E.g. Phase 27 →
> `Phase31Migration`, Phase 28 → `Phase32Migration`, Phase 29 → `Phase34Migration`,
> Phase 30 → `Phase36Migration`, Phase 58 → `Phase79Migration`. See per-phase notes.

## Status at a glance

| Phase | Area | Status |
|---|---|---|
| 0–7 | UI shell, board, timeline, workstreams, PRs, providers, write-path, polish | shipped (a few `[~]` UI polish + `7.10` open) |
| 8–11 | Products/repos, sign-in, defaults/PO view, agent runtime & crew economy | shipped (`11.5b` dispatcher `[ ]`, several `[~]`) |
| 12–18 | WIT, crews, scoring/promotion, sprints, crew execution, queue, shifts | mixed — core shipped, scoring/exec layers partial |
| 19–27 | Exec status, iter cap, restart, AssignedAgent, sync (WI/PR/build), build gate | shipped (UI tails + many test gaps `[~]`/`[ ]`) |
| 28 | Story-driven spawn | end-to-end functional; `EX.1–EX.8`, `CC.7`, swarm-UI `[~]` |
| 29 | Sync-strategy refactor (2h cadence, upstream cursor) | shipped |
| 30 | Agent Communication Protocol (ACP) | **not started** (`30.1–30.17` all `[ ]`) |
| 43,45,46,47,48 | Attachments/accept, base branch, plan layout v2, pair diversity, plan-doc gate | shipped |
| 51,52 | Budget-cap auto-recovery, sloppy-iteration guard | shipped (52.7/52.8 scorecard wiring `[~]`) |
| 53,54,56,57b | Sequential plan docs, prompt-growth metric, gate-failure recovery, auto-merge race fix | shipped |
| 57 | Operator cap-override | **not started** (`57.1–57.16` all `[ ]`) |
| 58,79c,59 | E2E refactor, merge-based conflict recovery, test-gate impacted selection | shipped (59 Playwright `[~]`) |
| 73 | Per-product SQLite split (control `forge.db` + `{slug}.db`) | shipped |
| 80 | Repos → product DB + repo-scoped stories (registry index, topbar repo switcher) | shipped (80.A–80.H) |
| R | Post-impl refactor (incl. R.8 IForgeBus) | **deferred** (all `[ ]`) |

---

## Phase 0 — Foundations & scaffolding
Stand up Forge.Api + Forge.UI, static port of the prototype.

- [x] 0.1 Forge.Api project (ASP.NET 8 minimal-API, health, problem-details, CORS)
- [x] 0.2 Forge.UI (Vite + React + TS) + ESLint/Prettier
- [x] 0.3 App shell `<TopBar/> <SideBar/> <ViewSwitcher/>` (verbatim port)
- [x] 0.4 Extract prototype `<style>` into split CSS files
- [x] 0.5 Light/dark theme toggle (context + localStorage `forge.theme`)
- [x] 0.6 Routing (`/`, `/plan`, `/timeline`, `/workstreams`, `/prs`, `/settings`)
- [x] 0.7 Shared types (`types.ts`) + parallel C# `Contracts/`
- [x] 0.8 Seed data port verbatim (`sampleData.ts`)
- [x] 0.9 Zustand stores per view
- [x] 0.10 API client `forgeApi.ts`
- [x] 0.11 TS client gen from OpenAPI (`npm run gen:api`)
- [x] 0.12 `GET /api/state` skeleton
- [x] 0.13 SQLite + EF Core persistence, base migrations
- [x] 0.14 Idempotent seed on first boot
- [x] 0.15 `StateEndpointsSmokeTests`
- [x] 0.16 Playwright `/plan` smoke
- [x] 0.17 `dev.ps1` boots API:5180 + UI:5181
- [x] 0.18 Fidelity check (empty shell screenshot diff)

## Phase 1 — Plan & Design (scrum board)
Fully interactive board against the local cache.

- [x] 1.1 `<Board/>` columns (WIP, point totals, AI/human counts)
- [x] 1.2 `<Card/>` (epic color, type icon, agent chip, progress)
- [x] 1.3 `<FilterBar/>`
- [x] 1.4 dnd-kit DnD + `checkDragPolicy` (TS + server `DragPolicy.cs`)
- [x] 1.5 `<DetailPanel/>`
- [x] 1.6 `<CardModal/>`
- [x] 1.7 `CardsEndpoints` (GET/POST/PATCH/move)
- [x] 1.8 SignalR `ForgeHub` `card.changed`
- [x] 1.9 Optimistic moves + rollback
- [x] 1.10 `<Toast/>`
- [x] 1.11 Sprint pill burn-down
- [x] 1.12 Keyboard shortcuts
- [x] 1.13 Tests (policy parity, endpoint contract, Playwright DnD)
- [x] 1.14 Fidelity check

## Phase 2 — Feature / branch timeline
Horizontal timeline with epic + workstream lanes. **2.1–2.13 all `[x]`** (axis zoom, lanes, branch bars, event markers, now-line, forecast, click→workstream, tooltips, filters, `GET /api/timeline`, telemetry, Playwright, fidelity).

## Phase 3 — Workstreams
Workstream = epic + branch + worktree + agents + PRs. **3.1–3.13 all `[x]`** (stats strip, grid card, filters, sync-from-epics, new-ws modal, `WorkstreamsEndpoints`, `IGitWorktreeService`, ahead/behind/conflicts, conflict surface, cross-link, perm gate, tests, fidelity).

## Phase 4 — Pull Requests view
- [~] 4.1 4-col PR board (prototype has 3 — kept 3 per fidelity; spec↔prototype conflict)
- [x] 4.2 PR card
- [x] 4.3 Build status renderer
- [x] 4.4 Review verdict pill
- [~] 4.5 PR detail panel (desc + build-log are stubs until Phase 5/6)
- [~] 4.6 Filters (chips wired; ws/author selects decorative)
- [~] 4.7 `GET /api/prs` (cache-only; provider aggregation in Phase 5)
- [x] 4.8 Auto-merge toggle (flag stored locally)
- [~] 4.9 Re-run/cancel build (stubs flip cached state)
- [x] 4.10 Toast on `pr.changed`
- [x] 4.11 Playwright
- [x] 4.12 Fidelity check

## Phase 5 — ADO / GitHub providers (read path)
- [x] 5.1 `IForgeProvider`
- [x] 5.2 `AdoProvider` (REST via HttpClient, not TFS client lib)
- [x] 5.3 `GitHubProvider` (REST via HttpClient, not Octokit)
- [~] 5.4 `/settings` provider config (PAT entry intentionally NOT in UI; superseded by Phase 9 sign-in)
- [x] 5.5 ADO WI → Card mapper
- [x] 5.6 GitHub issue/PR → Card/Pr mapper
- [~] 5.7 Background sync (`IHostedService` 60s + jitter; ETag is Phase R; WI sync split to Phase 23)
- [x] 5.8 Webhook receiver + HMAC + poll fallback
- [~] 5.9 Conflict policy (server-side 409 + revision; UI badge/modal is Phase R)
- [x] 5.10 Cache invalidation + `card.changed` fan-out
- [x] 5.11 Provider health on `/settings`
- [x] 5.12 Tests (StubHttpMessageHandler instead of WireMock)

## Phase 6 — Write actions: WI, PR, pipelines
**6.1–6.13 all `[x]`** (create WI/issue, update, comment, create PR from workstream, PR review actions [extended by Phase 24], trigger pipeline, cancel/re-run + log tail, auto-merge watcher, human-gate, audit log `forge_actions`+`/audit`, per-user PAT model, idempotency keys, tests).

## Phase 7 — Polish, observability, packaging
- [x] 7.1 Command palette (Ctrl/Cmd-K)
- [x] 7.2 Keyboard map overlay
- [x] 7.3 Empty states
- [x] 7.4 Error boundaries + report-problem
- [~] 7.5 Telemetry (ILogger + client ring buffer ship; full OTel SDK → forge_v2)
- [~] 7.6 Auth (single-user secret ships; Azure AD skeleton; Phase 9 lands real sign-in)
- [x] 7.7 Dockerfiles + docker-compose
- [x] 7.8 User guide under `docs/forge/`
- [~] 7.9 Release checklist (shipped at `docs/forge/release.md`; actual `v0.1.0` tag is manual)
- [ ] 7.10 Backlog grooming + `forge_v2.md`

## Phase 8 — Products & repository management
- [x] 8.1 `Product` + `Repo` records
- [x] 8.2 EF migration products/repos + repo_id FK (NOT-NULL hardening → Phase R)
- [x] 8.3 Backfill default `Atlas` product
- [x] 8.4 `ProductsEndpoints` + repos endpoints (`ReposEndpoints.cs` split → Phase R)
- [~] 8.5 Add-repo picker (works against PAT; multi-org waits for Phase 9)
- [x] 8.6 Validation (URL/provider shape; disk pre-check non-blocking)
- [x] 8.7 `/products` view
- [x] 8.8 TopBar product switcher (scopes all views)
- [x] 8.9 Endpoints accept `productId` / `X-Forge-Product-Id`
- [x] 8.10 Auto-clone hook (`IRepoCloneService` + clone-progress SignalR)
- [x] 8.11 Cross-link parity
- [x] 8.12 Tests (12 xUnit; Playwright deferred)
- [x] 8.13 Fidelity note (exempt; forge-token-clean)

## Phase 9 — Sign-in & current user (Edge popup, real identity)
- [x] 9.1 `IAuthService` (landed as IAuthService not IIdentityService)
- [x] 9.2 ADO sign-in (MSAL + WAM broker)
- [x] 9.3 GitHub sign-in (OAuth auth-code + PKCE, localhost callback)
- [x] 9.4 Edge-first browser launch
- [x] 9.5 DPAPI token storage
- [x] 9.6 `GET /api/me` + `useCurrentUser()` (SignalR invalidation deferred)
- [x] 9.7 Replace hard-coded placeholder strings
- [x] 9.8 Extend `Role` enum
- [x] 9.9 First-run rule (first sign-in → product-owner)
- [x] 9.10 User-menu popover
- [x] 9.11 Migrate seed agent role
- [~] 9.12 Tests (7 xUnit smoke; deeper MSAL/OAuth tests deferred)
- [x] 9.13 Security review checklist

## Phase 10 — Defaults, preferences & PO supervisory view
- [x] 10.1 `forge_user_settings` table
- [x] 10.2 `GET/PATCH /api/me/settings`
- [~] 10.3 Defaults panel (text inputs ship; combo-box auto-populate → Phase R)
- [~] 10.4 Pre-fill create-flows (server defaults in 10.5; UI pre-fill follow-up)
- [~] 10.5 Server-side default resolution (ADO config-resolver wired; `defaultsApplied[]` deferred)
- [x] 10.6 Crew model (global, not product-scoped)
- [~] 10.7 `/crews` view (members/in-flight/model ship; live salary waits on Phase 11)
- [x] 10.8 Cross-product audit (schema ships; population waits on 10.5)
- [x] 10.9 Role-gating middleware (`RequireRole.Check`)
- [x] 10.10 UI gating (`useHasRole`)
- [~] 10.11 Tests (9 xUnit; UI pre-fill + cross-product audit deferred)
- [x] 10.12 Fidelity note

## Phase 11 — Agent runtime & crew economy
- [x] 11.1 `IAgentRunner`
- [x] 11.2 Model registry (`models.json`, `GET /api/models`)
- [~] 11.3 soul.md authoring (FS service + endpoints + YAML parser; CodeMirror editor → Phase R)
- [x] 11.4 Cost ledger `agent_usage` (in-memory roll-up vs SQL views)
- [x] 11.5 `ClaudeAgentRunner` (HttpClient + Anthropic REST)
- [ ] 11.5b **Dispatcher across 2×2 cells** (`AgentRunnerDispatcher`, Copilot/Claude × SDK/CLI) — see `AgentRunnerDispatcher.cs`
- [x] 11.6 Sandboxing (`IAgentToolHost`, in-process Tasks)
- [x] 11.7 Live status streaming (`agent.{agentId}`; in-place UI hooks follow-up)
- [x] 11.8 Pause/resume/reassign
- [x] 11.9 Salary readouts (`/products` roll-up follow-up)
- [x] 11.10 Budgets & alerts (402 + `agent.budget_exceeded`)
- [~] 11.11 Tests (24 xUnit; Playwright E2E deferred)
- [x] 11.12 Fidelity

## Phase 12 — Work Item Tracking (WIT)
- [x] 12.1 `WorkItemRow` domain model + TS mirror
- [~] 12.2 `Phase12Migration` (backfill shipped; cards-as-projection deferred to Phase R)
- [x] 12.3 `WorkItemsEndpoints` (GET/POST/PATCH/DELETE/move/parent/sync; `/bulk` deferred)
- [x] 12.4 Provider sync extensions (ADO WIQL, GitHub REST)
- [x] 12.5 Sidebar `Work items` nav + count badge
- [x] 12.6 `/work-items` view-mode toggle (board/timeline/tree/list; lasso multi-select → Phase R)
- [~] 12.7 Detail panel (inline-edit title/state/points live; iteration combo/assignee/tags/markdown/comments/history follow-up)
- [~] 12.8 Bulk ops (shift-select + change-state + delete ship; iteration/reassign/tag wait on 12.7)
- [x] 12.9 Filter bar
- [x] 12.10 Sync UX (SyncDialog + throttle; auto-poll/conflict-modal/push-local → Phase R; superseded by Phase 23)
- [~] 12.11 Tests (50 xUnit; Playwright E2E substituted by lifecycle test)
- [x] 12.12 Fidelity (zero new tokens)

## Phase 13 — Crew management & task orchestration
- [x] 13.1 Extend `CrewRow` (parent/depth/lead/members/model/backend/mode/caps) + `crew_history` + `domain_experts`
- [~] 13.2 Task taxonomy `kind` (queue routes via pipeline; `plan-system.md`/`coding-system.md` templates not on disk)
- [x] 13.3 Lead-routing endpoint `POST /api/crews/{id}/assign`
- [~] 13.4 Plan task pipeline (endpoint live; dispatch via TaskQueue, not direct dispatcher — waits on 13.4a)
- [ ] 13.4a **Claude-SDK tool surface** (RunClaudeSdk doesn't declare tools — only Copilot·SDK is fully tool-capable)
- [~] 13.5 Coding task pipeline (endpoint live; same caveat as 13.4)
- [~] 13.6 Quota enforcement (MaxConcurrent enforced; WeeklyCap field persisted but pre-flight not wired)
- [~] 13.7 Domain-expert generator (path exists; needs runner live)
- [x] 13.8 Capability ingestion
- [x] 13.9 UI `CrewsView` (~2,100 lines: hierarchy/roster/capabilities/quota/runtime/history)
- [x] 13.10 Sidebar (drop PO-only nav gate, keep mutation gates)
- [x] 13.11 Tests (`Phase13CrewsTests`)
- [x] 13.12 Fidelity

> **Role taxonomy** (91 agents, Team/Pod/Role-bucket axes, `agents/allocation.json`) and the 100-seat crew allocation are documented in context — descriptive, not a tracked step.

## Phase 14 — Performance, scoring & promotion
> Source spec: `docs/forge/scoring-and-promotion.md`.
- [x] 14.1 Schema migrations (level/hourly on agents; timezone/schedule on crews; `deliverables`, `agent_level_history`)
- [x] 14.2 Schedule gate middleware (`ScheduleGate`; telemetry counter → 14.12)
- [x] 14.3 Evaluator scoring contract (`EvaluatorScoreParser`, points-scaled reject threshold)
- [~] 14.4 Iteration loop in dispatcher (`DeliverableLoop` ships; time-cap/`paused-eod` + prod wiring pending)
- [~] 14.5 Dispute path (`DeliverablesEndpoints` ship; auto-spawn judge needs runner)
- [~] 14.6 Soul preamble injector (`agents/_preamble.md` written; runner concat + sha + templates remain)
- [~] 14.7 Promotion job (`PromotionPolicy`+`PromotionRunner` + endpoints; nightly scheduler + SignalR + monthly writer remain)
- [~] 14.8 Payroll API (`PayrollCalculator` + endpoints; deprecation log + per-member breakdown remain)
- [~] 14.9 UI Performance tab (tab + schedule strip + payroll/spend cards + roster grid; window-toggle + 9 KPIs wait on 14.16)
- [~] 14.10 UI Score panel (`DeliverableScorePanel` ships; restrict-dispute-to-lead + parent-pill refresh remain)
- [~] 14.11 UI TopBar split (Spend+Payroll chips ship; popovers + server endpoint + screenshot remain)
- [ ] 14.12 Audit + telemetry (audit kinds + counters + trace span)
- [x] 14.13 Tests (`Phase14ScoringTests`)
- [x] 14.14 Fidelity = spec-consistency test (`Phase14SpecConsistencyTests`, 10 assertions)
- [ ] 14.15 Monthly review snapshot (`agent_monthly_review` + endpoints)
- [~] 14.16 Agent KPI service (`IAgentKpiService` partial; needs monthly snapshots)
- [ ] 14.17 Costs view enrichment (`<ScorecardStrip>` + `AgentScorecardDrawer`)
- [ ] 14.18 Printable monthly review (QuestPDF)

## Phase 15 — Product sprint & area path management
**15.1–15.13 all `[x]`** (domain+migration `product_iterations`/`product_area_paths`/`repo_iteration_bindings`/`product_saved_prefixes`; `IAdoClassificationService`; bulk import endpoints; GitHub milestone variant; repo binding; `Product.activeIterationId`; Plan&Design page-head `<SprintPicker>`+`<AreaPathFilter>`; Products `<SprintSummary>`; `SprintManager` modal; saved prefixes UI; orphan handling; tests; fidelity).

## Phase 16 — Crew execution & live observability
- [x] 16.1 Schema migrations (`Phase16Migration`: pair_role, paired_with, auto_dispatched_at, expected_file_changes, `task_transcript_event`, cost_category)
- [x] 16.2 Pair management endpoints (`/pair`; audit via `crew_history`)
- [~] 16.3 `LeadAutoDispatcher` hosted service (polls + stamps + `task.delegated`; `IDeliverableLoop.RunAsync` wiring waits on 13.4/13.5)
- [~] 16.4 `DeliverableLoop` paired-mode extension (EvaluatorCrewId + cost split + per-iter `task.iteration.*`)
- [~] 16.5 Plan-tree validator (20/20/20 caps enforced; spec↔constants grep test deferred)
- [~] 16.6 Coding-pipeline file-budget check (`StepBudgetChecker`; provider check-write waits on runner)
- [x] 16.7 `task.{workItemId}` SignalR group (`TaskChannel`, 10k-row eviction)
- [x] 16.8 `WorktreeChangeFeed` (1 Hz poll, not FileSystemWatcher)
- [~] 16.9 Live execution endpoints (`/execution`, `/transcript`; productId scope follow-up)
- [~] 16.10 UI Live Execution view (header/transcript/right-rail; several small follow-ups deferred)
- [x] 16.11 Update Swarm view (pair lane; live tape/cap-heat fallback mocks)
- [~] 16.12 Update Workstream view (`● N live` badge; pulse + active-deliverables stat deferred)
- [x] 16.13 `useTaskLiveStore` hook
- [x] 16.14 Tests (`Phase16ExecutionTests`, 14 green)
- [~] 16.15 Telemetry surface (latency/no-capacity/over-budget ship; others deferred)
- [~] 16.16 Audit + history rows (`forge_actions` AUTO + `crew_history` pair; pre-emption + `/audit` chip deferred)
- [ ] 16.17 Fidelity (`--graph-line` token)
- [~] 16.18 **Server-owned delivery pipeline** (commit+push+PR in `CrewQueueWorker`; 4 Q16.9 reliability gaps resolved; change-summary-in-commit follow-up)

## Phase 17 — Persistent task queue & team-to-crew routing
- [x] 17.1 Schema migration (`task_queue` + `task_queue_history`)
- [x] 17.2 `ITaskQueueService` (atomic CAS transitions)
- [x] 17.3 `AssignedTo.kind='team'`
- [~] 17.4 `CrewQueueWorker` (5 sweeps + boot-recovery; sole agent-start surface)
- [x] 17.5 `QueueEndpoints`
- [~] 17.6 SignalR events (`queue.changed`/`queue.strained`; auto-subscribe-from-store follow-up)
- [~] 17.7 UI Lead inbox `/inbox` (pending-acks/active-queue/strain panels; lead-only nav gate → Phase R)
- [~] 17.8 UI Workstreams + Crews queue indicators
- [x] 17.9 `forgeApi` extensions + `useQueueStore`
- [~] 17.10 Telemetry + audit (counters ship; `queue.depth` gauge + `/audit?queue` chip follow-up)
- [x] 17.11 Tests (`Phase17QueueTests`, 12 green)
- [~] 17.12 Integration of Phase 16 dispatcher (collapsed to producer/consumer split)
- [~] 17.13 Fidelity (`/inbox` additive)

## Phase 18 — Per-crew shift band (day / night)
- [x] 18.1 Migration (`crews.Shift` default `day`)
- [x] 18.2 Domain model `CrewRow.Shift`
- [x] 18.3 `ScheduleGate.IsWithinShift` + `NextTransitionLocal`
- [x] 18.4 CrewsEndpoints PATCH `shift` (whitelist + `crew_history`)
- [x] 18.5 CrewsView pill
- [x] 18.6 Lead 24×7 exemption (`EvaluateAgent` + worker picker + dispatcher + payroll split)

Tests: header says **51/51 server + 22/22 UI vitest + 6/6 Playwright pass**; the per-scenario checkboxes in the source are left `[ ]` (not flipped) but the suites (`Phase18*Tests`, `shifts.spec.ts`, `phase18-shift-pill.spec.ts`) pass.

## Phase 19 — Execution status & trial counter
**19.1–19.6 all `[x]`** (`ExecutionStatus`+`TrialCount` migration, domain+TS, `CrewQueueWorker` integration + `workitem.changed` broadcasts, `TaskExecutionEndpoint`, `<ExecBadge>`, Workstreams `● N live`).

## Phase 20 — Runtime iteration cap & PR change-summary persistence
**20.1–20.6 all `[x]`** (`task_queue.MaxIterOverride`+`deliverables.GeneratorSummary` migration, `DeliverableLoop` clamp[1,10], `PATCH /execution {maxIter}`, snapshot field, `## Change Summary` parse, PR body uses persisted summary).

## Phase 21 — Restart, transcript clear, skip-soul-prompt
- [x] 21.1 Migration (`task_queue.SkipSoulPrompt`)
- [x] 21.2 `DELETE /work-items/{id}/transcript` (clears transcript + deliverables)
- [x] 21.3 Restart button on `LiveExecutionView`
- [x] 21.4 `RunAgentAsync` honors `SkipSoulPrompt`
- [x] 21.5 Points-based `maxTurns` in Copilot SDK runner (`30 × points`)

Tests (`RestartBehaviorTests.cs`): comprehensive coverage authored (migration, transcript clear, restart-via-plan, skip-soul flag, points budgets, re-execution guard, independence invariants). Per-scenario checkboxes left `[ ]` in source but suite exists.

## Phase 22 — `AssignedAgent` field & assignment history
- [x] 22.1 Migration (`AssignedAgentJson` + `agent_assignments`)
- [x] 22.2 Domain + TS mirror
- [x] 22.3 `CrewQueueWorker` Sweep-2 writes `AssignedAgent` (not `AssignedTo`)
- [x] 22.4 `AgentAssignmentsEndpoints`
- [x] 22.5 `MoveWorkItem` integration (pr-opened / completed hooks)
- [x] 22.6 Restart clears `AssignedAgent`
- [x] 22.7 Backfill migration (one-shot, idempotent)
- [ ] 22.8 UI "Currently working on this" strip (backend ready)
- [ ] 22.9 UI Live Execution assigned-to pill (backend ready)
- [ ] 22.10 UI Plan board / Workstream card cells (backend ready)
- [x] 22.11 Tests (`Phase22AssignedAgentTests`, 59 passing)
- [ ] 22.12 Fidelity (UI not yet implemented)

Tests: **59 passing — backend complete**; all scenario checkboxes `[x]`. UI (22.8–22.10, 22.12) deferred.

## Phase 23 — Scheduled work-item sync (per-repo, paginated, delta, agent-ready)
**23.1–23.12 all `[x]`** (migration; `SyncConstants`; `WorkItemSyncJob` 20-min[→2h Phase 29]; `AdoWorkItemSyncRunner` multi-area WIQL + 200-id batching; `GitHubWorkItemSyncRunner` since-pagination; `AgentReadyResolver`; endpoints; `RepoSyncStatusStrip` UI; sidebar failed-sync dot; tests; telemetry; fidelity).

Tests: large matrix mostly `[x]`. Deferred `[ ]`: `PullRequests_Mapped_To_PrRow` (WI-sync upserts WorkItemRow only — PR mapping → Phase 24), `SubIssue_Native_Parent_Preferred` (needs GraphQL → Phase R).

## Phase 24 — PR state & comment-lifecycle sync
**24.1–24.16 all `[x]`** (migration `pr_comments`/`pr_comment_history`; `SyncConstants`; `PrSyncJob` 20-min; `Ado`/`GitHubPrSyncRunner`; authority endpoints [agents post, only humans resolve]; `PrCommentLinker`; reopen+queue hook; `comment-response` runner path; UI comment list/Comments tab/PR badges; tests; telemetry; fidelity; worktree cleanup on terminal).

Tests: **60 + 7 + 5 + 7 passing**. Notable deferred `[ ]`: `wont-fix` local resolve, agent-edits-reviewer-comment 403, unified `pr.changed{kind:comment}` envelope, manual link override PATCH, per-line precision (modifies-file == modifies-line v0.1), moved-line skip, sync-side `pr.comment-reopened` SignalR, ADO/GitHub cursor persistence, full lifecycle E2E, several comment-response loop-integration + must-reply E2E + original-agent fallback-chain scenarios (loop-level covered by Phase 14, Phase-24-specific E2E deferred).

## Phase 25 — Sync run item details
**25.1–25.4 all `[x]`** (`sync_run_items` migration, entity, `WorkItemSyncJob` integration, `GET .../runs/{runId}/items`).

## Phase 26 — Build & test gate before PR creation
**26.1–26.13 all `[x]`** (migration build/test cmd+timeout; entity/contract; `RunCommandAsync`; delivery gate init→restore→build→test before push; Live Exec events; tests 53 green; `BuildSystemDetector`; detection lifecycle hooks; detect/validate endpoints; `EditRepoModal`; `Phase29Migration` init/restore + `build_validation_runs`; `BuildValidationWorker`; global SignalR `repo.validation-complete`).

## Phase 27 — Build sync & failure-driven repair loop
**27.1–27.15 all `[x]`** (migration `Phase31Migration`: builds/build_stages/build_stage_logs/build_artifacts/build_artifact_files/build_failure_points/build_fix_pending; `SyncConstants`; `BuildSyncJob`; `Ado`/`GitHubBuildSyncRunner`; `BuildArtifactExpander` zip-slip guard; `BuildFailureParser`; queue hook + Q27.12 deferral; `build-fix` runner path; `BuildsEndpoints`; UI Builds tab + PR card chip; tests 39 green; telemetry; fidelity).

Tests: mostly `[x]`. Many deferred `[ ]`: `build-state-change` audit row, per-build 19-min stale guard, GH Link multi-page, bot-login author map, janitor daily scheduling, no-pickup blocked-state flip, PR-diff/commit-msgs inlined in prompt, read_file/write_file artifact-root path validation, `build-fix-pushed` event name, lineage rendering, fingerprint-suppression on infra-annotate, runner-timeout, path-traversal counter, and all `27.17` end-to-end scenarios (unit-level hops covered).

## Phase 28 — Story-driven spawn: multi-team orchestrated delivery
End-to-end functional (`StorySpawnOrchestrator` + deterministic strategy; production swaps `IStoryArtifactStrategy` DI binding). Migration `Phase32Migration`.

- **28.1 Data model** 28.1.1–28.1.6 `[x]`
- **28.2 REST API** 28.2.1–28.2.7 `[x]`
- **28.3 Plan** 28.3.1–28.3.8 `[x]`
- **28.4 Decompose** 28.4.1–28.4.7 `[x]`, 28.4.8 `[~]` (CardsJson auto-populate behind strategy swap)
- **28.5 Implement** 28.5.1–28.5.7 `[x]`
- **28.6 Review** 28.6.1–28.6.8 `[x]`
- **28.7 QA (legacy batch)** 28.7.1–28.7.5/7–9/11 `[x]`, 28.7.6/10 `[~]` — **superseded by Phase 58 per-stage qa-e2e; drained for back-compat**
- **28.8 Completion** 28.8.1–28.8.4 `[x]`, 28.8.5 `[~]`
- **28.9 UI** 28.9.1–28.9.11 `[x]`; 28.9.12–28.9.22 `[~]` (swarm-control surface shells + live data; deeper drill-down panels partial)
- **28.10 Artifact check-in** 28.10.1–28.10.3 `[x]`, 28.10.4 `[~]` (FS guard by convention)
- **28.11 Dependency tracking** 28.11.1–28.11.6 `[x]`
- **28.12 Tests** `Phase28StorySpawnTests` (42) + `Phase28OrchestratorTests` (9) = **51, 100% pass**
- **Cross-cutting** CC.1–CC.6 `[x]`, CC.7 `[ ]` (story-level cost tracking — explicit non-goal)
- **EX.1–EX.8 all `[ ]`** — spec-confirmed but **not wired**: Fibonacci points→budgets, paired gen/eval, 3-trial model rotation, remove decomposition-approve gate for swarm-mode, 20/20/20 validator, four typed plan WI kinds + Epic/Feature/Task mapping, all-four-artifact engineer context, per-Phase test gate + downstream auto-unblock.

## Phase 29 — Sync-strategy refactor (2-hour cadence, upstream cursor, per-repo overrides, UI knob)
**29.1–29.10 all `[x]`** (`SyncConstants` 1200→7200 + GithubInitialWindowDays=30; `Phase34Migration` 4 repo cols; `RepoRow`; `WorkItemSyncJob` cursor swap to `LastUpstreamChangedDate`; per-repo eligibility gates `IsDueForSync`/`IsPrDueForSync`/`IsBuildDueForSweep`; GitHub initial window; contracts+endpoints; EditRepoModal Sync tab; tests `Phase34SyncRefactorTests`; fidelity).

## Phase 30 — Agent Communication Protocol (ACP)
**NOT STARTED — `30.1–30.17` all `[ ]`** (migration `Phase36Migration`: acp_runs/messages/contexts/awaits/subscriptions/silo_audit; `IAcpBus`; soul.md `acp_topics:` parser; auto-subscribe glue; prompt preamble injection top-20; `/acp/*` ACP-conformant endpoints; `SiloAuditor`; `FalseParallelPlanner`; await timeout sweep; UI Context graph tab + Live Exec overlay + Inbox surfacing; `forgeApi`+`useAcpStore`; telemetry+audit; wire-format conformance suite; Phase 17 `awaiting-context` queue state; fidelity). All test scenarios `[ ]`.

## Phase 43 — Story attachments & operator accept
Shipped (prose spec, no checkboxes). `StorySpawnRow.AttachmentsJson` + `Phase43Migration` (must run before Phase 38); upload flow + `CopyAttachmentsToWorktreeAsync`; attachment propagation across all producers (`.git/info/exclude` guard + `.forge-attachments/` filter); operator **ACCEPT** (`POST .../artifacts/{key}/accept` — flips deliverable to pass, opens PR); default story points = 5; points propagation to spawn; plan-doc budget improvements (work-item points, 10-min floor); orchestrator re-drive after resume; `IStoryGitOps.PushBranchAsync`.

## Phase 45 — Operator-chosen base branch on a story spawn
Shipped. `StorySpawnRow.BaseBranch` + `ProductStoryRow.BaseBranch` (`Phase45Migration`, default `main`); spawn/PATCH endpoints (validate ref name); base-branch change semantics (rebase new base on main → replay merged PRs → retarget open PRs); `IStoryProviderOps.UpdatePrBaseBranchAsync` (local cache; upstream PR-edit → forge_v2); `IStoryGitOps.RebaseBaseBranchAsync`; UI (SpawnStoryModal/StoryCard/NewStoryModal/StoryDetailPanel); story-level default + auto-create branch on POST/PATCH + full retarget when spawn exists. Plus shipped fixes: engineer agent edits real repo files in-place (no sandbox prefix), drop 30s per-spawn timeout, bake target file paths into impl prompt, tolerate self-commits + forbid `git commit`, ADO canonical repo identifier, plan-doc loop guards (pass-verdict + PR-merge cancellation), ACCEPT always opens a PR.

## Phase 46 — Implementation-plan layout v2
**46.1–46.8 all `[x]`** (H1=Phase/H2=Stage/H3=subsection + YAML front-matter, back-compat parser; per-stage + per-phase `Dependencies`; two-level deps with phase-expansion; `workstreams.yaml` replaces `.json` (+legacy migration); DAG view rework (ELK phase containers, collapse, floating-card detector, YAML toggle); `Phase46Migration` `DepsJson`/`PhaseAnchor`; spec-consistency tests `Phase46PlanFormatTests`; fidelity).

## Phase 47 — Pair diversity for parallel deliverables
**47.1–47.6 all `[x]`** (`PairDiversityResolver` PickGenerator/PickEvaluator; `LoadBusyAgentsAsync` via `agent_usage WHERE EndedAt IS NULL` + 24h reaper; `CrewQueueWorker` impl-path wiring with `no-free-pair-member` skip; `CrewStoryArtifactStrategy` plan-doc rosters; tests `Phase47PairDiversityTests`; backend-only fidelity).

## Phase 48 — Plan-doc completion gate (regression fix)
**48.1–48.6 all `[x]`** (`ArePlanDocsAllPassedAsync` at four orchestrator entry points: DrivePlan Step1+Step3, DriveDecompose, DriveImplement — regress to Plan if any doc not passed; tests `Phase48PlanDocGateTests`; backend-only fidelity).

## Phase 51 — Per-task budget-cap auto-recovery
Shipped (prose spec). `verdict=stalled-budget` → `ExecutionStatus=cooldown-budget` (not terminal); `BudgetCooldownReconciler` (15s tick) resumes on cap-raise (same pair) or cooldown-elapse (rotate pair, reset budget window); `StorySpawnOrchestrator` mirror; picker rotation exclusion via `LastPairAgentIdsJson`; seed uses `??=` so operator overrides survive restart; config `Forge:Agents:BudgetCooldown:*`; telemetry; tests `Phase51BudgetCooldown*Tests`.

## Phase 52 — Sloppy-iteration guard & pair reassignment
- [x] 52.1 Migration (`PairAttemptCount`/`PriorPairsJson`/`FailureReason` + `agent_pair_assignment`)
- [x] 52.2 `SloppyDetector` (score-plateau + token-cap; **CheckAsync disabled 2026-05-13 — returns false**, see 52.12.1)
- [x] 52.3 `DeliverableLoop` integration (`sloppy` verdict → `pending-reassign`)
- [x] 52.4 `SloppyReassignmentReconciler` (clean-slate reassign, 3-attempt cap)
- [x] 52.5 `CrewQueueWorker` integration (prior-pair exclusion)
- [x] 52.6 `StorySpawnOrchestrator` mirror
- [~] 52.7 `AgentKpiCalculator` SloppyRate (constant + table ship; scorecard field + PromotionJob trigger remain)
- [~] 52.8 UI sloppy badges (data path ships; 4 decorative touchpoints + `/sloppy-history` endpoint remain)
- [x] 52.9 Audit + telemetry
- [~] 52.10 Tests (`Phase52SloppyDetectorTests`+`Phase52SloppyReassignmentTests`; KPI/PromotionJob cases pend 52.7)
- [~] 52.11 Fidelity (backend ships; 3 UI screenshots gate on 52.8)
- [x] 52.12.1 Disable token-cap + score-plateau triggers (cumulative-token meter was noise)
- [x] 52.12.2 Restart endpoint resets Phase 52 + cost-ledger state

## Phase R — Post-implementation refactor (deferred)
**All `[ ]`.** R.1 CSS modules · R.2 icon component · R.3 ordering/copy review · R.4 design-token layer · R.5 a11y · R.6 virtualize lists · R.7 Storybook · **R.8 Realtime+messaging refactor** (`IForgeBus` + transactional outbox + SSE; `R.8.1–R.8.13` all `[ ]`: schema, event contracts, `OutboxBus`, `OutboxDispatcher`, SSE endpoint+hook, `LegacyHubBridge`, per-call-site + per-store migration, `/admin/queue`, OTel, Roslyn analyzer, tests, decommission SignalR).

## Phase 53 — Sequential plan-doc tasks (one worktree + one PR per doc)
Shipped (prose spec). `architecture → tech-spec → implementation-plan → e2e-scenarios`, strict serial (doc N+1 waits for doc N's PR merged + verdict=pass). `Phase53Migration` `StorySpawnRow.PlanDocPrsJson`; discriminator: `null`→legacy `DrivePlanLegacyAsync`, non-null→`DrivePlanSequentialAsync`. Crash-recovery anchor + idempotent PR-open. `ProduceSinglePlanArtifactAsync`. Restart of merged upstream doc → 409.

## Phase 54 — Per-iteration prompt-size growth metric
Shipped (prose spec). `task.iteration.prompt-growth` + `task.prompt-bloat` (threshold `ScoringConstants.PromptBloatGrowthThreshold = 30_000`, fires iter≥2); `agent_usage.TurnSamplesJson` (`Phase54Migration`); Live Exec Tokens-tab sparklines; Workstream card `📈 NK · iter N` pill (IntersectionObserver-gated); snapshot `PromptGrowth[]`; tests `Phase54PromptGrowthTests`.

## Phase 56 — Gate-failure recovery loop
Shipped (prose spec). Per-iter gate chains build→quality→test; generator prompt prepend stage-aware; evaluator diff-header stage-aware; post-pass `OnPassValidate` callback demotes pass→iterate (cap 3) instead of stalling; post-pass gate suite removed from `ProduceWorkstreamChangesAsync`; `BuildFailedException` carries `Stage`+`Command`; tests `Phase56GateRecoveryTests` (8).

## Phase 57b — AutoMergeWatcher reviewer-race fix (hotfix)
- [x] 57b.1 `AutoMergeWatcher` workstream-membership guard (null Review verdict → skip `reviewer-not-run`)
- [x] 57b.2 `DriveReviewAsync` stamps `Review.verdict="pending"` on first sight
- [x] 57b.3 Doc append
- [x] 57b.4 Build + tests (4 green)
- [x] 57b.5 Restart (operator-approved)

Tests `AutoMergeReviewerRaceTests` 57b.T1–T4 `[x]`. Deferred: late-arriving comments on already-merged PRs (worktree gone) → lazy-rematerialize is forge_v2.

## Phase 57 — Operator cap-override (per-task max-iter / budget-window / sloppy-terminal reset)
**NOT STARTED — `57.1–57.16` all `[ ]`** (`Phase57Migration` `work_items` 3 cols + `work_item_override_history`; `WorkItemRow` ext + entity; `DeliverableLoop` WI-level override read + relaxed stalled-budget terminal; `StorySpawnOrchestrator` `OverrideContinuePair`; contracts; `POST/GET /api/work-items/{id}/override(s)`; tests; `forgeApi`; `OverrideModal.tsx`; 4 UI wires; Playwright; build/test; STOP-for-restart). Non-destructive sibling of Restart. All test scenarios `[ ]`.

## Phase 58 — E2E refactor: trailing per-stage QA workstreams
Shipped. **Source of truth: `docs/forge/e2e.md`** (all 188 internal steps `[x]`). Dissolves Phase 28's batch "Phase 5 QA": decomposition emits a `Category='qa-e2e'` workstream per impl stage; spawn phases collapse to `plan→decompose→implement→review→done` (`qa` retained for legacy drain). `e2e-scenarios.md` gains `### Setup` taxonomy (`inline|compose|lab-wsfc|lab-bare-metal`). Migration `Phase79Migration` (`workstreams.Category`). Rollout/back-out runbook `docs/forge/e2e-rollout.md`.

## Phase 79c — Merge-based conflict recovery (replaces rebase + force-push)
Shipped (prose spec). `IStoryGitOps` merge API (`MergeBaseIntoBranchAndDetectConflictsAsync`/`ContinueMergeAndPushAsync`/`AbortMergeAsync` + `MergeResult`/`MergeState`); plain `git push` (no force) so reviewer comments stay anchored; defense-in-depth marker scan; `MaxResolveAttempts=3`; Phase 79b rebase-state fallback retained; tests `Phase79cMergeBasedResolveTests` (3).

## Phase 59 — Test gate: impacted-test selection + full log attachments

**Stage A — heuristic (SUPERSEDED by Stage C, kept as fallback):**
- [~] 59.1 `ImpactedTestResolver` (→ `HeuristicImpactedTestProvider`)
- [~] 59.2 Wire into `StoryBuildRunner.RunBuildAsync`
- [~] 59.3 `Forge:Test:ImpactedOnly` config + `RepoRow.TestImpactedOnly` (`Phase59Migration`)
- [~] 59.4 `task.test-gate.skipped` + `impactedProjects[]`
- [~] 59.5 `/products` Validation panel checkbox + preview

**Stage B — log persistence + UI chip:** 59.6–59.13 all `[x]` (streamed log to `.worktree/{slug}/.forge/logs/`; `logUrl` on gate events; `GET /api/workstreams/{wsId}/logs/{*relPath}` w/ Range + traversal guard; retention; `TranscriptLine` + `BuildPane` `📎 view log` chip; "Has logs" filter; build+test green; restart).

**Stage C — Roslyn/LSP resolver:** C.1–C.7 `[x]` (`IImpactedTestProvider`+`ImpactedTestResolverChain`; `HeuristicImpactedTestProvider`; `Microsoft.CodeAnalysis.Workspaces.MSBuild`+`Build.Locator`; `RoslynImpactedTestProvider`+`TestAttributeDetector`; `impactedMode` field; tests — 3 heavy MSBuild integration tests `[Fact(Skip)]`).

**Stage S2 — LSP for Rust/Go/Python/TS:** S2.1–S2.3 `[x]` (`MinimalLspClient`; `LspImpactedTestProvider`+`ILspLanguageSupport`; per-language detectors; tests 31/31; live-server integration tests skip-on-missing-PATH).

Tests: heuristic 15/15, log persistence 12/12, endpoint 7/7, vitest 10/10, Stage C 16 written/8 running (8 skipped pending real-SDK harness), S2 31/31. Playwright **59.T18–T21 `[~]`** (authored; SKIP until a live gate run emits a `logUrl` event).

## Phase 80 — Repos move to product DB + repo-scoped stories
Shipped. **Decision:** when the DB is split per-product, the full `repos` table lives in each `{slug}.db` (not the control DB); control keeps only a thin `repo_registry` index (routing + global uniqueness + sync-due). A product owns many repos, and a story (and its spawn/workstreams/PRs/live-exec) binds to exactly one repo. Spec in [`forge_implementation.md`](forge_implementation.md) Phase 80; boundary amendment in [`docs/forge/split-db.md`](forge/split-db.md).

- [x] 80.A Schema — `RepoRegistryRow` (control) + `RepoRow` moved to `ProductDbContext`; `ProductRow.DefaultRepoId`; `ProductStoryRow.RepoId`; partial-unique RemoteUrl/CloneRoot on the registry (filtered `DeletedAt IS NULL`).
- [x] 80.B+D Data move — `RepoMoveToProductService` (ATTACH + explicit-column copy control→`{slug}.db`, drops control `repos`, marker-gated/idempotent) + `RepoRegistrySync` (builds registry, sets `DefaultRepoId`, backfills null `RepoId` on product tables, quarantines orphan registry rows). Verified on the live 4-product split env.
- [x] 80.C `RepoLookup` (registry→owning-product-DB routing) + ~75 `control.Repos` readers migrated; 8 hidden `control.Set<RepoRow>()` runtime sites fixed.
- [x] 80.E `X-Forge-Repo-Id` scope — `RepoScope` helper + `RepoScopeMiddleware` (validates vs registry, resolves product when repo-id sent alone, echoes `X-Forge-Repo-Resolved`); `ProductScope.ResolveAsync` narrows the 8 board/workstreams/PRs/live-exec endpoints to the active repo.
- [x] 80.F Required repo binding — story-create resolves repo (req → active → `DefaultRepoId`) or 400 `repo.required`; spawn inherits the story's repo; work-item spawn falls back to the product's `DefaultRepoId`; base-branch materialization narrowed to the story's repo.
- [x] 80.G UI — topbar repo switcher (`useProductsStore.activeRepoId` + `localStorage`, resets on product switch), `X-Forge-Repo-Id` on all requests (`buildScopeHeaders`), `SpawnStoryModal` drops the operator repo pick (inherits + shows read-only), `story.repoId` on the DTO. Vitest 9 new, UI suite 116/116.
- [x] 80.H Tests/cleanup — `MultiProductForgeWebApplicationFactory.SeedReposAsync` multi-repo fixture (+ 2 factory bugs fixed: SplitMarkerCache invalidation + post-split repo move); `Phase80MultiRepoTests` + boundary/backfill tripwires; deleted dead `Phase76Migration.cs`; `split-db.md` boundary updated.

Tests: deterministic suite green (`Category!=Integration`, run in batches given `T9_StreamingWrite` full-scale OOM that passes in isolation) — ~1,638 pass / 0 fail. Phase 80 suites: `Phase80SchemaTests`, `Phase80RepoScopeTests`, `Phase80StoryRepoTests`, `Phase80MultiRepoTests`, repo-move/registry/reboot 33/33. Integration tests (real Claude/Copilot) need auth, excluded per CI convention.
