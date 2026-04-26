# Forge — Implementation Plan

> Promote `docs/examples/forge-board.html` from a single-file mockup into a working software-factory dashboard with bidirectional sync against Azure DevOps (ADO) and GitHub.

## Goals (from the user)
1. Scrum board for plan & design.
2. Feature / branch timeline.
3. Workstream view (branch + worktree + agents + PRs).
4. PR view (implementation, critical review, build).
5. Sync work items from ADO / GitHub and operate on them locally.
6. Create / update work items, create / review PRs, trigger and inspect pipelines.

## Hard constraint — fidelity to the prototype

> **`docs/examples/forge-board.html` is the design specification.** The React port MUST match it pixel-for-pixel and behavior-for-behavior. **No deviation is allowed during initial implementation** — not in layout, not in spacing, not in CSS variables, not in component composition, not in icons, not in animation timings, not in the seed data, not in the drag-and-drop policy, not in copy, not in the column / view ordering. Any "improvement", "modernization", or "tidy-up" is **out of scope** for this plan and must wait for a follow-up refactor pass that ships only after every checkbox below is ticked.

What this means concretely:
- Copy the HTML/CSS verbatim into React components and CSS files; rename classes only when React forces it (e.g. `class` → `className`). Do **not** rename `.col-head`, `.board-wrap`, `.detail-panel`, etc.
- Keep all CSS variables (`--bg-base`, `--accent`, `--epic-024`, `--shadow-drag`, …) and the exact `[data-theme="dark"]` overrides byte-for-byte.
- Keep all SVG icons exactly as inlined in the prototype — do not swap to an icon library.
- Keep the seed data (`epics`, `cards`, `columns`, `workstreams`, `wsEvents`, `prs`, `wsStats`, `overviewActivity`, `overviewAgents`) exactly as authored, including IDs, ordering, agent names, and timestamps. The API serves this same data in Phase 0 so the UI is indistinguishable from the prototype.
- Keep `checkDragPolicy` semantics identical (allow, warn, confirm, toast strings).
- Keep view ordering, eyebrow numbering (`00 Overview`, `01 Plan & Design`, …), page titles, button labels, and filter chip counts.
- Each phase must include a "fidelity check" step that diffs the rendered React view against the prototype (DOM screenshot diff via Playwright `toHaveScreenshot`) and fails if anything moved.

A short, separately-tracked **Phase R — Refactor** placeholder appears at the bottom; do not pull tasks forward into the implementation phases.

## Stack & layout

- **Frontend** — new project `src/forge/Forge.UI`: React + TypeScript + Vite. Ports the layout/CSS from the prototype but rebuilt as React components. Talks to `Forge.Api` over REST + SignalR.
- **Backend** — new project `src/forge/Forge.Api`: ASP.NET Core 8 minimal-API. Owns its own host, DI, persistence, and SignalR hub. Independent of `WorkflowUi.Server` — they may share nothing beyond conventions.
- **Solution** — both projects added to `Workflow.sln`. Test projects live next to them: `src/forge/Forge.UI.Tests` (Playwright + Vitest) and `src/forge/Forge.Api.Tests` (xUnit).
- **Persistence** — local SQLite cache (so the UI stays responsive when ADO/GitHub are slow or offline) under `src/forge/Forge.Api/Persistence/`.
- **Provider layer** — `IForgeProvider` abstraction in `Forge.Api` with `AdoProvider` and `GitHubProvider` implementations. PATs in user-secrets (dev) → DPAPI / Key Vault (later).
- **Naming** — namespace `Forge.Api.*` and `Forge.UI` package name `@forge/ui`; CSS scope `.forge-*`; route prefix `/api/*` (the API only serves forge — no need to re-prefix). Default ports: API `5180`, UI dev server `5181`.

## Phase legend
Each phase is meant to land as a small batch of PRs. Steps are checkbox items so progress is visible inline.

Checkbox states:
- `[ ]` not started
- `[~]` in progress
- `[x]` completed (merged + verified — not just "code written")

---

## Phase 0 — Foundations & scaffolding

Stand up the two new projects, the data shapes, and the static-data port of the prototype so every subsequent phase has somewhere to render into.

- [x] **0.1** Create `src/forge/Forge.Api/Forge.Api.csproj` (ASP.NET Core 8 minimal-API) with `Program.cs`, health endpoint, problem-details, CORS for the dev UI port. Add to `Workflow.sln`.
- [x] **0.2** Create `src/forge/Forge.UI/` (Vite + React + TypeScript) via `npm create vite@latest -- --template react-ts`. Add ESLint + Prettier configs that match repo conventions.
- [x] **0.3** App shell components: `<TopBar/>`, `<SideBar/>`, `<ViewSwitcher/>` mirroring the prototype layout under `src/forge/Forge.UI/src/shell/`. **Verbatim port** — keep DOM structure, class names, inline SVGs, and grid template areas (`"topbar topbar" "sidebar main"`) exactly as in the prototype.
- [x] **0.4** Extract the prototype's `<style>` block verbatim into `src/forge/Forge.UI/src/styles/` split by the existing `/* ============= */` section banners (`reset.css`, `theme.css`, `shell.css`, `topbar.css`, `sidebar.css`, `main.css`, `filter-bar.css`, `board.css`, `timeline.css`, `workstream.css`, `pr.css`, `toast-modal.css`, `detail-panel.css`). **Do not** convert to CSS modules, Tailwind, or styled-components in this phase.
- [x] **0.5** Light/dark theme toggle (React context + `localStorage` key `forge.theme`). Toggle behavior, CSS variable swaps, and the `data-theme` attribute on `<html>` must match the prototype exactly.
- [x] **0.6** Routing with `react-router-dom`: `/`, `/plan`, `/timeline`, `/workstreams`, `/prs`, `/settings`. Deep-linkable.
- [x] **0.7** Define shared types in `src/forge/Forge.UI/src/types.ts`: `Epic`, `Card`, `Column`, `Workstream`, `WsEvent`, `Pr`, `Agent`, `BuildStatus`, `ReviewVerdict`. Keep a parallel C# record set in `Forge.Api/Contracts/` and document the source-of-truth direction (server-first).
- [x] **0.8** Port the prototype's seed data **verbatim** (same IDs, ordering, agent names, costs, timestamps, summaries) into `src/forge/Forge.UI/src/sampleData.ts`. The file is the source of truth for all visual fixtures used by fidelity checks.
- [x] **0.9** State management: pick Zustand (lean, fits vanilla feel) and create stores per view (`useBoardStore`, `usePrStore`, …) under `src/forge/Forge.UI/src/state/`.
- [x] **0.10** API client `src/forge/Forge.UI/src/api/forgeApi.ts` with typed fetch + problem-details handling. Honors `VITE_FORGE_API_URL`; falls back to sample data when `?seed=local`.
- [x] **0.11** Generate the TS client from the API's OpenAPI doc (`Forge.Api` exposes `/openapi.json` via `Microsoft.AspNetCore.OpenApi`); script `npm run gen:api` writes to `src/api/generated/`.
- [x] **0.12** Skeleton endpoints in `Forge.Api/Endpoints/StateEndpoints.cs`: `GET /api/state` returns the seed data shape so the UI can light up end-to-end.
- [x] **0.13** SQLite persistence (`Microsoft.Data.Sqlite` + EF Core) under `src/forge/Forge.Api/Persistence/`. Migrations: `epics`, `cards`, `workstreams`, `prs`, `events`. DB file at `forge.db` next to the binary, override via `Forge:Db:Path`.
- [x] **0.14** Idempotent seed on first boot importing `forge-board.html`'s sample data.
- [x] **0.15** Tests: `Forge.Api.Tests/StateEndpointsSmokeTests.cs` (xUnit + `WebApplicationFactory`) verifies `GET /api/state` returns 200 with the expected counts.
- [x] **0.16** Playwright spec in `Forge.UI.Tests/`: load `/plan`, assert the five columns render with at least one card.
- [x] **0.17** `dev.ps1` (or npm script) that boots `Forge.Api` on `:5180` and `Forge.UI` Vite dev server on `:5181` together.
- [x] **0.18** **Fidelity check** — Playwright `toHaveScreenshot` of the empty shell (topbar + sidebar + theme toggle) against a baseline rendered from `docs/examples/forge-board.html` itself. Pixel diff must be 0 in both light and dark themes.

## Phase 1 — Plan & Design (scrum board)

Make the board fully interactive against the local cache. No external sync yet — just a clean, fast scrum board.

- [x] **1.1** `<Board/>` component renders columns with WIP limits, point totals, AI/human counts (port `renderBoard`).
- [x] **1.2** `<Card/>` component with epic color, type icon (story/task/bug), agent chip, points, priority, branch, live progress bar.
- [x] **1.3** `<FilterBar/>`: All / Needs approval / AI working / Blocked / Watching, plus epic / agent / priority / group-by selects and search.
- [x] **1.4** Drag-and-drop with `dnd-kit`; reuse the prototype's `checkDragPolicy` rules (skip-phase confirm, human-gate confirm, re-open done) — port the function to TS in `src/forge/Forge.UI/src/board/policy.ts` and mirror it in `Forge.Api/Domain/DragPolicy.cs` for server-side enforcement.
- [x] **1.5** `<DetailPanel/>` right rail: description, branch, agent timeline, "Auto-promote when…" rules, action buttons.
- [x] **1.6** `<CardModal/>` for new / edit (epic, type, title, points, priority, agent, tags).
- [x] **1.7** API endpoints in `Forge.Api/Endpoints/CardsEndpoints.cs`: `GET/POST /api/cards`, `PATCH /api/cards/{id}`, `POST /api/cards/{id}/move`.
- [x] **1.8** SignalR `ForgeHub` (`/hubs/forge`) emits `card.changed` so a card moved in one tab updates other tabs. Wire `@microsoft/signalr` on the React side.
- [x] **1.9** Optimistic UI for moves with rollback on server reject.
- [x] **1.10** `<Toast/>` component (Zustand-backed queue) for transient messages ("Moved to Build", "Re-opened", etc.).
- [x] **1.11** Sprint pill in page head wired to real burn-down (points done / total) — selector over board store.
- [x] **1.12** Keyboard shortcuts via `react-hotkeys-hook`: `n` new card, `/` focus search, `esc` close panel, `1..5` switch columns focus.
- [x] **1.13** Tests: `policy.spec.ts` (drag-policy parity, run on both client and server), endpoint contract tests in `Forge.Api.Tests`, one Playwright DnD spec.
- [x] **1.14** **Fidelity check** — Playwright screenshot diff of `/plan` (light + dark, with the sample data, default filters) against the prototype. Diff must be 0. Repeat for: a card hovered, a card dragging, the detail panel open on `#234`, the new-card modal open.

## Phase 2 — Feature / branch timeline

Visualises the same data as a horizontal timeline with epic and workstream lanes.

- [x] **2.1** Render time axis with Day / Week / Sprint zoom toggles.
- [x] **2.2** Render epic lanes (group rows) with collapse/expand.
- [x] **2.3** Render branch bars (`barStart` / `barEnd` from workstreams) with status color (active / merging / review / planning).
- [x] **2.4** Overlay event markers: PR opened, merged, review, conflict, block (`wsEvents`).
- [x] **2.5** Vertical "now" line and a `Now` button that scrolls to it.
- [x] **2.6** "Forecast" toggle that extends planned bars (`planned: true`) with a hatched style.
- [x] **2.7** Click a bar → opens the workstream detail (Phase 3).
- [x] **2.8** Hover tooltip with branch name, ahead/behind, last-active, agents.
- [x] **2.9** Filter chips: All / Active / Merging / Planning, plus lane toggle (Workstream vs Epic).
- [x] **2.10** API `GET /api/timeline?from=…&to=…` returning normalized bars + events.
- [x] **2.11** Telemetry: count timeline opens, time-on-view; structured logs `forge.timeline.*`.
- [x] **2.12** Playwright: zoom toggle changes pixel density, "Now" scrolls to today.
- [x] **2.13** **Fidelity check** — screenshot diff of `/timeline` against the prototype across all three zoom levels (Day / Week / Sprint), light + dark, with the sample data. Diff must be 0.

## Phase 3 — Workstreams

A workstream = epic + branch + worktree + agents + active PRs. Operate on them as first-class entities.

- [ ] **3.1** Stats strip (active count, open PRs, merged today, 24h cost).
- [ ] **3.2** Workstream grid card: branch, base, ahead/behind, conflicts, last-active, summary, agents, linked PRs / cards.
- [ ] **3.3** Filter chips: All / Active / Merging / Conflicts / Idle.
- [ ] **3.4** "Sync from epics" button: re-derive workstreams from cards in Build/Review.
- [ ] **3.5** "New workstream" modal (epic, name, base branch, worktree path).
- [ ] **3.6** API endpoints in `Forge.Api/Endpoints/WorkstreamsEndpoints.cs`: `GET /api/workstreams`, `POST /api/workstreams`, `POST /api/workstreams/{id}/sync`, `DELETE` (with safety prompt).
- [ ] **3.7** Local git worktree wrapper service `IGitWorktreeService` in `Forge.Api/Services/Git/` (`git worktree add/list/remove`); guard against operating outside the configured root.
- [ ] **3.8** Compute ahead/behind/conflicts via `git rev-list --count base...branch` and `git merge-tree`.
- [ ] **3.9** Conflict surface: badge on card + "Open conflict" action that opens the file path returned by `git diff --name-only --diff-filter=U`.
- [ ] **3.10** Cross-link: clicking a card on the board with a `branch` jumps to the matching workstream; vice versa from workstream → cards.
- [ ] **3.11** Permission gate: writes (`POST/DELETE`) require an authenticated user (placeholder until Phase 5 auth).
- [ ] **3.12** Tests: `IGitWorktreeService` unit tests against a temp repo; endpoint integration tests; Playwright "create workstream" flow.
- [ ] **3.13** **Fidelity check** — screenshot diff of `/workstreams` against the prototype: stats strip, full grid, each filter chip active, and the new-workstream modal open. Diff must be 0.

## Phase 4 — Pull Requests view

PR board (Implementation → Build → Review → Merged) with build, review and gate state.

- [ ] **4.1** Four-column PR board with `pr.col` mapping (`implementation | build | review | merged`).
- [ ] **4.2** PR card: title, author, branch, additions/deletions, files, age, build status, review verdict, conflict + human-gate badges.
- [ ] **4.3** Build status renderer (running with %, passing, failing, queued) with a tiny progress strip.
- [ ] **4.4** Review verdict pill (approved / changes / pending) with one-line agent summary.
- [ ] **4.5** PR detail panel: full description, diff stats, reviewer list, build log tail (last 50 lines), human-gate reason.
- [ ] **4.6** Filters: All open / Need approval / Failing / Conflicts / My reviews; selects for workstream / author.
- [ ] **4.7** API `GET /api/prs` aggregates from cache + provider; `GET /api/prs/{id}` with details.
- [ ] **4.8** Auto-merge toggle (per PR) — flag stored locally; actual merge happens via provider in Phase 6.
- [ ] **4.9** Re-run / cancel build buttons (UI only this phase; server stub returns 202 + plans Phase 6 hookup).
- [ ] **4.10** Toast on PR state transition via SignalR `pr.changed`.
- [ ] **4.11** Playwright: failing PR shows the `failing` chip; clicking opens the detail panel; auto-merge toggle persists across reloads.
- [ ] **4.12** **Fidelity check** — screenshot diff of `/prs` against the prototype: every PR card variant from the seed data (running build, queued, passing, failing, conflict, human-gate, auto-merge armed). Diff must be 0.

## Phase 5 — ADO / GitHub providers (read path)

Bring real work items, branches and PRs into the cache.

- [ ] **5.1** Add `IForgeProvider` interface in `Forge.Api/Providers/` (`ListWorkItems`, `ListPullRequests`, `ListBuilds`, `GetWorkItem`, `GetPr`, …).
- [ ] **5.2** Implement `AdoProvider` using `Microsoft.TeamFoundationServer.Client` / REST; PAT in user-secrets `Forge:Ado:Pat`.
- [ ] **5.3** Implement `GitHubProvider` using Octokit; PAT or fine-grained token in `Forge:GitHub:Pat`.
- [ ] **5.4** Provider config UI page `/settings` (React): org/project (ADO), owner/repo (GitHub), PAT entry, "test connection".
- [ ] **5.5** Map ADO work item → `Card`: state → column, area path → epic, tags → tags, story points → pts, assigned-to → agent (human role).
- [ ] **5.6** Map GitHub issue + PR → `Card` / `Pr` (issue type by labels, PR draft status preserved).
- [ ] **5.7** Background sync: `IHostedService` polling on configurable interval (default 60s), with jitter + ETag/If-None-Match where supported.
- [ ] **5.8** Webhook receiver `POST /api/webhooks/{provider}` for ADO Service Hooks and GitHub webhooks; HMAC verification; falls back to polling if webhook is silent for >5 min.
- [ ] **5.9** Conflict policy on overlapping local + remote edits: last-writer-wins by default, with a "remote changed since" badge on the card and an explicit "discard local" / "overwrite remote" choice.
- [ ] **5.10** Cache invalidation on webhook; SignalR `card.changed` fan-out.
- [ ] **5.11** Provider health on `/settings`: last sync, error rate, rate-limit budget remaining.
- [ ] **5.12** Tests: provider contract tests against recorded HTTP cassettes (WireMock.Net); webhook signature unit tests; one end-to-end test that boots the provider with a fake server and asserts cards appear.

## Phase 6 — Write actions: WI, PR, pipelines

Make every read-only surface from Phase 5 round-trippable.

- [ ] **6.1** Create work item (ADO) / issue (GitHub) from the New-card modal, with provider selection.
- [ ] **6.2** Update work item: status transition, assignee, iteration/sprint, tags, story points, area path.
- [ ] **6.3** Comment on a card → posts to the linked WI / issue.
- [ ] **6.4** Create PR from a workstream (`POST /api/workstreams/{id}/pr`) — pushes branch, opens PR with templated description (linked card IDs, agent summary).
- [ ] **6.5** PR review actions: approve / request-changes / comment from the detail panel; surface line-level comments where the provider supports it.
- [ ] **6.6** Trigger pipeline run (ADO Pipelines `runs` API; GitHub Actions `dispatch` / `rerun`); show queued state and stream status into the PR card.
- [ ] **6.7** Cancel and re-run failing job; tail logs into the detail panel via SignalR.
- [ ] **6.8** Auto-merge: when toggled, server enqueues a watcher that merges the PR once required checks pass and required reviewers approve.
- [ ] **6.9** Human-gate enforcement: a server-side rule list (paths, labels, file globs) that blocks merge until a human approves; surfaced as the existing `humanGate` badge.
- [ ] **6.10** Audit log table `forge_actions` (who, what, when, request body, response status). Surfaced read-only at `/audit`.
- [ ] **6.11** Permission model: per-user PAT only writes on behalf of that user; a server-wide PAT is allowed only for read-only sync.
- [ ] **6.12** Idempotency keys on every write (`Idempotency-Key` header) to make retries safe.
- [ ] **6.13** Tests: write-path provider tests against cassettes; one Playwright E2E that creates a card, opens a PR, triggers a build, and observes status changes via SignalR.

## Phase 7 — Polish, observability, packaging

Make it feel like a product and easy to ship.

- [ ] **7.1** Command palette (`Ctrl/Cmd-K`): jump to view, jump to card / PR / workstream by ID, run actions ("Open PR for ws/oidc-rotation").
- [ ] **7.2** Keyboard map overlay (`?`) listing all shortcuts.
- [ ] **7.3** Empty states for every view (no cards, no PRs, no workstreams, no provider configured).
- [ ] **7.4** Error boundaries per view + a global "report problem" button that captures the last 50 actions + correlation id.
- [ ] **7.5** Telemetry: OTel spans `forge.view.*`, `forge.action.*`; counters for card moves, PRs opened, syncs failed.
- [ ] **7.6** Auth: pluggable, default = single-user with a local secret; optional Azure AD via `Forge:Auth:AzureAd:*`.
- [ ] **7.7** Dockerfile per project (`Forge.Api/Dockerfile`, `Forge.UI/Dockerfile`) + root `docker-compose.yml` (api + ui static + sqlite volume + reverse proxy). Document `forge.env.sample`.
- [ ] **7.8** User guide under `docs/forge/` (setup, providers, daily flow, keyboard, FAQ); architecture note linking `Forge.UI` ↔ `Forge.Api`.
- [ ] **7.9** Smoke release checklist + a tagged `v0.1.0` once Phases 0–4 are green and Phases 5–6 work against at least one real ADO project and one real GitHub repo.
- [ ] **7.10** Backlog grooming session: cut anything not in the goals list, file a `forge_v2.md` for the rest.

---

## Phase R — Post-implementation refactor (deferred)

> **Do not start until every checkbox in Phases 0–7 is ticked and all fidelity checks are green.** This phase exists only to record the deferred design debt so reviewers know it is acknowledged, not forgotten.

Candidate refactors to consider once the prototype is faithfully reproduced and shipping:
- [ ] **R.1** Decompose the monolithic prototype CSS into CSS Modules / Tailwind / vanilla-extract — pick one and migrate a section at a time, screenshot-diff each.
- [ ] **R.2** Replace inline SVGs with an icon component or sprite, keeping pixel parity.
- [ ] **R.3** Re-evaluate the column / view ordering and copy with real users; only then change anything visible.
- [ ] **R.4** Extract a design-token layer (Style Dictionary or similar) from the existing CSS variables.
- [ ] **R.5** A11y pass: keyboard focus rings, ARIA roles for the board, color-contrast audit.
- [ ] **R.6** Performance: virtualize long lists (cards in Backlog, audit log) once they exceed ~200 rows.
- [ ] **R.7** Storybook for components, fed by the same `sampleData.ts`.

Anything else that surfaces during Phases 0–7 goes into a `Refactor backlog` section here — **not** into the implementation steps above.

---

## Open questions to resolve before Phase 5

- Which is the primary provider — ADO, GitHub, or both equally? Drives auth + webhook ordering.
- Where do worktrees live? Single root configurable per user, or per-workstream? (Phase 3.7 needs the answer.)
- Are agents (`A1`, `E4`, …) real running processes or only labels surfaced from work-item metadata? If real, Phase 6.6 needs an `IAgentRunner` too.
- Sprint / iteration source of truth: ADO iteration paths, GitHub milestones, or local? Affects burn-down and the sprint pill in Phase 1.11.
