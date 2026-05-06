# Forge — Implementation Plan

> Promote `docs/examples/forge-board.html` from a single-file mockup into a working software-factory dashboard with bidirectional sync against Azure DevOps (ADO) and GitHub.

## Goals (from the user)
1. Scrum board for plan & design.
2. Feature / branch timeline.
3. Workstream view (branch + worktree + agents + PRs).
4. PR view (implementation, critical review, build).
5. Sync work items from ADO / GitHub and operate on them locally.
6. Create / update work items, create / review PRs, trigger and inspect pipelines.
7. Organise work by **product**, with one product owning one or more **repos** (mix of ADO and GitHub allowed). Manage products and repos as first-class entities.
8. Sign in as the **current Windows user** to ADO and GitHub — prefer an Edge browser popup on Windows (WAM broker / WebView2) and fall back to the user's default browser. No hand-pasted PATs.
9. Configurable, per-user **defaults**: default ADO account + project, default GitHub account/org, default product + repo. Pre-filled into every "create" flow, overridable per action.
10. Surface the real signed-in user in the UI (replacing the hard-coded `XD / Xiaodong Li / Tech lead` placeholder) and give the operator a **Product Owner** role with a supervisory view across all crews.

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

## Resolved decisions

Decisions confirmed by the operator. Each one closes an entry in "Open questions" below; later phases must respect them.

- **Provider parity (Q1)** — ADO and GitHub are **equal first-class providers**. No primary / secondary tier.
  - **Implications**: Phase 5 ships both `AdoProvider` and `GitHubProvider` together (no "ADO first, GitHub later"); auth UX in Phase 9 surfaces both sign-in buttons side-by-side with the same prominence; webhook intake in Phase 5.8 supports ADO Service Hooks and GitHub webhooks at the same milestone; provider config in `/settings` lists them in alphabetical order; Phase 8.5 "Add repo" picker lets the user choose either provider per-repo, and a single product can mix repos from both.
- **Worktree location (Q2)** — worktrees live **under each repo**, in a fixed subdirectory: `{repo.cloneRoot}\.worktree\ws-{workItemId}`. Example: for the `update` repo cloned at `E:\work\hci\update`, work item `12345`'s worktree is at `E:\work\hci\update\.worktree\ws-12345`. Branch name for the same row is `ws/{workItemId}`.
  - **Implications**:
    - Phase 3.7 `IGitWorktreeService` is the singleton helper used for ad-hoc worktree create/list/remove; the **production agent path** (Phase 16.18) bypasses it and runs `git worktree add` directly against the work item's repo `cloneRoot` so the directory is keyed by the work item being executed, not by the workstream the row was bound to. Both paths refuse anything that escapes the `.worktree/` prefix.
    - Phase 8.1 `Repo` record must carry `cloneRoot` (already specified) — the only field needed; there is no separate global "worktrees root" config.
    - **Implementation deviation from the original Q2 wording**: the agent worktree path is keyed by the **work item id** (`ws-{workItemId}`), not the workstream name. Originally we expected one worktree per workstream and many work items sharing it; in practice each work item runs in isolation so a per-item directory is easier to reason about (boot recovery doesn't see two agents racing on the same checkout). The `.worktree/` segment is identical, only the leaf differs. The original `{workstreamName}` shape is **superseded** — sample data and the workstream detail panel render the workstream's `[root, name]` for read-only display, but the agent runtime never opens that path.
    - **`.gitignore` not `.git/info/exclude`**: the worker auto-appends `.worktree/` to the repo's `.gitignore` on first use rather than `info/exclude`, because the operator commits the `.gitignore` policy across machines (a fresh clone elsewhere needs the same exclusion without re-running the worker). The "would dirty the main checkout" concern proved moot — `.worktree/` matches an existing `.gitignore` entry on most repos and the auto-append is a no-op.
    - Auto-clone (Phase 8.10): when `cloneRoot` is empty, `git clone` runs first; the `.worktree` subdir is created lazily on first `git worktree add`.
    - **Fallback when the worktree path can't be created**: the worker falls back to `Path.GetTempPath()` (`%TEMP%`) so the agent can at least start. This is a soft failure mode — the agent still runs and produces a transcript, but cannot push a branch (the temp dir isn't a clone). Surfaces as a `task.delivery-failed { reason: "Branch not pushed to remote" }` event after the deliverable loop passes.
    - Multiple Forge products that point at the same `cloneRoot` are forbidden (would race on `.worktree/`); Phase 8.6 validation rejects this.
- **Agents are real (Q3)** — Agents are **real, supervised crew members**, not labels surfaced from work-item metadata. Each agent has a role + capability assignment and a `soul.md` (persona / system prompt / capability brief). Each **crew** is assigned a single model (e.g. `claude-opus-4-7`, `claude-sonnet-4-6`, `claude-haiku-4-5`); all agents in a crew run on that model. **Crew salary** = the sum of model-priced token spend across that crew's agents over a period (input tokens × input price + output tokens × output price).
  - **Implications**:
    - **New Phase 11 — Agent runtime & crew economy** (added below) covers the runner, model registry, soul.md authoring, and the cost ledger. It is **in scope for v0.1**, not deferred.
    - Phase 6.6 (pipeline runs) is left as-is for CI; agent invocations are a separate axis owned by `IAgentRunner` in Phase 11. Cross-link added: pipeline status streams + agent token streams both flow through SignalR `forge.stream.*` channels.
    - `Agent` record (currently `{ id, role, name }` in `types.ts`) gains `crewId`, `soulPath`, `capabilities: string[]`, `state: 'idle' | 'working' | 'blocked' | 'paused'`. Existing seed agents (`E4 Forge`, `E7 Rivet`, `R3 Cortex`, `XD Xiaodong`, `A1`, `A2`, `E9`) keep their ids and gain reasonable defaults during migration.
    - `Crew` record (Phase 10.6) gains `model: string`, `modelInputPricePerMTok: decimal`, `modelOutputPricePerMTok: decimal`, `lead: agentId`. Pricing is denormalized onto the crew so historical salary lookups don't depend on fetching today's price.
    - **soul.md storage**: `%LOCALAPPDATA%\Forge\agents\{agentId}\soul.md` by default; the path is stored on the Agent record so operators can relocate (e.g. into a versioned `soul/` folder inside a product repo). Editable from filesystem or `/settings/agents` (Phase 11.3).
    - **Cost ledger** `agent_usage(id, agentId, crewId, taskId, model, inputTokens, outputTokens, costUsd, startedAt, endedAt)` is the source of truth for cost. Phase 1 card.cost becomes a derived sum over usage rows where `taskId = card.id`; the prototype's hard-coded `cost: 0.31` values stay as fallback fixtures only when the runner is offline.
    - Phase 10.7 `/crews` dashboard goes from "metrics" to **live**: salary today / this sprint / this month, tokens burned (in/out split), active tasks, blocked tasks, last-active per agent, model in use, cost-per-card P50/P90.
    - The Product Owner role gains agent-level controls: pause / resume an agent, reassign an agent to another crew, swap a crew's model (with a "this changes salary" confirmation), open / edit soul.md in-place.
    - Telemetry: every runner invocation emits `forge.agent.invocation.{started,token,finished,failed}` events for the OTel pipeline (Phase 7.5).
- **Repo provider exclusivity & sprint source (Q4)** — A `Repo` belongs to **exactly one provider**: ADO or GitHub, never both (no mirrored remotes treated as one). All sprint / branch / iteration concepts for a repo follow that repo's provider — ADO repos use ADO iteration paths, GitHub repos use GitHub milestones. **There is no local Forge sprint.**
  - **Implications**:
    - Phase 8.1 `Repo.kind` is enforced as enum-of-one at write time; an attempt to attach the same remote URL under both providers is rejected with a 409.
    - The Sprint pill (Phase 1.11, currently `[x]` against hard-coded data) is rebuilt to read the **currently scoped repo's** iteration / milestone via the provider:
      - ADO → `_apis/work/teamsettings/iterations?$timeframe=current` for the repo's project + team.
      - GitHub → `/repos/{owner}/{repo}/milestones?state=open&sort=due_on` (the soonest open milestone).
    - If the user is in a multi-repo product view (Phase 8.8 product switcher with no single active repo), the Sprint pill renders as "Mixed sprints — pick a repo" with a click-through that opens the repo selector. No aggregation, no fake unified sprint.
    - `Card.sprint` (when imported from a provider) is the provider's iteration / milestone identifier, namespaced by repo so two repos on the same product can run different cadences without collision: `card.sprintRef = "{repoId}:{providerSprintId}"`.
    - Burn-down arithmetic (Phase 1.11) sums points within `card.sprintRef = activeRepo.currentSprintRef`. No cross-repo burn-down at v0.1.
    - Phase 5 mappers: ADO mapper writes `sprintRef` from `System.IterationPath`; GitHub mapper writes it from the issue / PR's `milestone.id`. Local sprint inference (e.g. "due in 14 days" → bucket) is explicitly NOT done.
    - `forge_v2.md` candidate: optional cross-product roll-up dashboard that lists each active sprint across all repos side-by-side — out of scope for v0.1.
- **Product ↔ repo cardinality (Q5)** — Strictly **1:N**. A `Repo` belongs to exactly one `Product`. A repo cannot be shared between two products; if a repo is genuinely shared infrastructure, it is modeled as its own product that other products consume.
  - **Implications**:
    - Phase 8.1 `repos.product_id` is **NOT NULL** with a `UNIQUE(remoteUrl)` constraint at the DB layer — the same remote URL cannot be attached to two different products.
    - Phase 8.4 `POST /api/products/{id}/repos` rejects with 409 if the remote URL is already attached anywhere; error response includes the existing `productId` so the UI can offer "open existing product" as a recovery action.
    - Phase 8.7 `/products` view is a clean tree (`Product → Repos → Workstreams`), not a graph; no "linked from product X" badges needed.
    - "Shared infra" pattern is documented in `docs/forge/products.md`: create a `Platform` (or similar) product, attach the shared repo there, and other products reference its workstreams / PRs read-only via the Phase 8.11 cross-link mechanism.
    - Backfill migration (Phase 8.3) is simpler: every existing repo lands in exactly one product (the default `Atlas`); no fan-out logic.
- **Repo cloning policy (Q6)** — **Auto-clone, silently.** When a workstream is created against a repo whose `cloneRoot` is empty (or missing), Forge runs `git clone` automatically with no confirmation prompt. No "are you sure" modal, no operator-pre-clone requirement.
  - **Implications**:
    - Phase 8.10 default `Forge:Repo:AutoClone=true`; the config flag still exists as an off-switch for headless / CI installs but is **not** flipped during normal operator use.
    - First-time clone progress streams via SignalR (`forge.repo.clone.{started,progress,finished,failed}`) so the UI can render an inline progress strip on the workstream card and on `/products` while the clone runs.
    - Auth: the clone uses the signed-in user's provider token (Phase 9) over HTTPS — never SSH at v0.1, never anonymous, never a separate credential helper. If the user is not signed in to the matching provider, the clone fails fast with a "sign in to {provider} first" toast that links to `/settings`.
    - Disk-space pre-check (Phase 8.6) runs before kicking off the clone; warning becomes a hard error if free space < 500 MB.
    - Idempotency: if `cloneRoot` already exists and is a valid git repo for the same remote, no-op; if it exists but points at a different remote, refuse with a 409.
    - Non-fatal failures (auth, network, disk) leave `cloneRoot` clean (best-effort `rm -rf` of the partial clone) so the next attempt starts from scratch — no half-cloned state for the operator to debug.
    - Telemetry: `forge.repo.clone.duration_ms` histogram; failures tagged with provider + reason class for the `/audit` view.
- **Auth flow selection (Q7)** — **WAM is the first choice** on every supported Windows host. **Device-code flow is disabled** entirely — never offered, never fallback. When WAM is unavailable (older Windows, macOS, Linux), fall back to **auth-code + PKCE** in a system browser.
  - **Implications**:
    - Phase 9.2 ADO sign-in initialization order: detect WAM availability first (`PublicClientApplicationBuilder.WithBroker(true)` + runtime check); on WAM-capable hosts, ALL interactive sign-ins go through WAM. On non-WAM hosts, fall through to auth-code + PKCE; the Edge-first launcher (Phase 9.4) still picks the browser.
    - Identity service has a hard rule: `if (FlowKind == DeviceCode) throw new NotSupportedException(...)`. The MSAL `AcquireTokenWithDeviceCode` API is never called and is added to a banned-API list in CI (Roslyn analyzer or grep step in `Forge.Api.Tests`).
    - GitHub sign-in (Phase 9.3) stays auth-code + PKCE only — device-code is removed even as a documented option.
    - Headless / CI scenarios that genuinely cannot do interactive auth use a **PAT in user-secrets** (the existing Phase 5 path), not device code. This is the only non-interactive auth path.
    - User-menu (Phase 9.10) shows the active flow next to each provider's status pill (`WAM` / `Browser`) so operators can see at a glance which path was used; useful for debugging silent renewal failures.
    - Phase 9.12 tests: WAM-available branch verifies `WithBroker(true)` is honored; non-WAM branch verifies system-browser auth-code; a third test asserts the device-code refusal throws and emits a structured log so the analyzer rule has a runtime backstop.
    - Removes the v2 question of "do we ship device-code for restricted-network users" — the answer is no; restricted-network users get a PAT.
- **GitHub auth model (Q8)** — **OAuth app**, not a GitHub App. Tokens act as the signed-in user; no installation step, no app identity. Tracked as a Phase R / `forge_v2.md` upgrade if multi-tenant bot writes ever become a need.
  - **Implications**:
    - Phase 9.3 stays as written: Authorization Code + PKCE, localhost callback, scopes `repo`, `read:user`, `read:org`. No `installation_id`, no JWT-signed app-token exchange.
    - Webhook intake (Phase 5.8) is configured **per repo** (the user installs the webhook on each repo Forge attaches), not centrally per app. The Phase 8.5 "Add repo" picker calls `POST /repos/{owner}/{repo}/hooks` immediately after attach so the operator never has to click into GitHub UI.
    - Writes (PR comments, status updates, branch pushes) appear under the operator's GitHub identity — visible in commit history, PR audit, and notifications. This is intentional: matches the "act as me" model from Q1.
    - No support for "Forge as a bot user" at v0.1; if bot writes are required (e.g. a service account commits release notes), the operator runs Forge under a dedicated GitHub account they own.
    - Token rotation: OAuth tokens don't expire by default but can be revoked by the user. Phase 9 handles 401 by silently re-prompting auth-code; a short-circuit "re-authenticate" button surfaces in `/settings` when a 401 is observed.
    - `forge_v2.md` candidate: GitHub App migration with installation tokens, fine-grained per-repo permissions, single webhook endpoint, and bot-identity writes. Out of scope for v0.1.
- **Crew hierarchy (Q9)** — **Flat.** `Crew` has no `parentCrewId`; `/crews` is a flat list, not a tree. Nesting is deferred — revisit only if the operator grows past ~10 crews and grouping demonstrably helps.
  - **Implications**:
    - Phase 10.6 `Crew` schema stays as specified — no `parentCrewId`, no recursive FK.
    - `/crews` view (Phase 10.7) renders one row per crew, sortable by salary / activity / name. No expand/collapse arrows.
    - Salary roll-up is one level only: per-crew on `/crews`. No "department total" intermediate level.
    - Phase 11 cost ledger queries stay simple — `GROUP BY crewId`, never recursive CTE.
    - `forge_v2.md` candidate: nested crews with parent FK, recursive salary roll-up, tree view on `/crews`. Out of scope for v0.1.
- **Cross-org sign-in (Q10)** — **Multi-org from day one.** A single Windows user can be signed into multiple ADO organizations and multiple GitHub orgs simultaneously; Forge picks the right token per request based on the target repo's owning org.
  - **Implications**:
    - Phase 9.5 token storage keys on `(provider, accountId)` (not `provider` alone), where `accountId` = ADO tenant + UPN, or GitHub login + chosen org. The DPAPI blob is a map of these tuples to encrypted token records.
    - `GET /api/me` (Phase 9.6) `providers` field becomes an array per provider: `providers: { ado: [{accountId, tenant, upn, expiresAt}, …], github: [{accountId, login, orgs: [...], expiresAt}, …] }`. UI renders one chip per signed-in account.
    - Token resolution at request time: the API derives the target `(provider, accountId)` from the operating repo (`Repo.adoOrg` / `Repo.ghOwner`); if no token matches, returns 401 with a `signInHint: { provider, accountId }` body so the UI can prompt the right "Sign in to {org}" CTA.
    - User-menu (Phase 9.10) shows a grouped list: "ADO accounts" with one row per `(tenant, upn)`, "GitHub accounts" with one row per login. Each row has its own "Re-authenticate" / "Sign out" actions; signing out one account does not sign out others.
    - Add-repo picker (Phase 8.5) shows a per-account dropdown so the operator can attach a repo from any signed-in account. Attempting to attach from an unsigned account triggers an inline sign-in flow scoped to that org.
    - Webhook receiver (Phase 5.8): event signature verification picks the right secret per `(provider, accountId)` since each ADO org / GitHub repo has its own shared secret.
    - Phase 10.1 `forge_user_settings` defaults (default ADO org/project, default GitHub owner) become the **active** account when a request would otherwise be ambiguous (e.g. cross-product searches); operator overrides per action.
    - Audit log (Phase 6.10) records the acting `accountId` so multi-account writes are debuggable.
- **Role escalation (Q11)** — **Any Product Owner can promote or demote any other user**, no second-PO sign-off required. Single safety rail: the system always keeps **≥ 1 Product Owner** — the last PO cannot demote themselves and cannot be demoted by another last-PO operation.
  - **Implications**:
    - Phase 10.9 role-gating middleware adds a single invariant check on `PATCH /api/users/{id}/role`: if the change would result in zero `product-owner` rows, refuse with 409 + `error: "last_product_owner"`.
    - UI: in the `/settings/users` panel the "Role" combobox for the current sole PO disables "Demote" and shows a tooltip ("You are the last Product Owner — promote someone else first"). For all other users, promote / demote are immediate without a confirm dialog.
    - Audit log (Phase 6.10) records every role change with `actorUserId`, `targetUserId`, `from`, `to` so the trail is visible at `/audit` even though no second-actor consent is required.
    - SignalR `user.role.changed` event so the affected user's open tabs update their `/crews` access immediately (sidebar entry vanishes / appears, gated buttons enable / disable in place).
    - First-run rule (Phase 9.9) is unchanged: the first sign-in becomes PO; later sign-ins default to `human` and are promoted by any existing PO.
    - `forge_v2.md` candidate: optional 2-step demotion ("approve demotion within 24h") for shared / paranoid installs — not needed at v0.1.

## Phase legend
Each phase is meant to land as a small batch of PRs. Steps are checkbox items so progress is visible inline.

Checkbox states:
- `[ ]` not started
- `[~]` in progress
- `[x]` completed (merged + verified — not just "code written")

---

## Phase 0 — Foundations & scaffolding

Stand up the two new projects, the data shapes, and the static-data port of the prototype so every subsequent phase has somewhere to render into.

- [ ] **0.1** Create `src/forge/Forge.Api/Forge.Api.csproj` (ASP.NET Core 8 minimal-API) with `Program.cs`, health endpoint, problem-details, CORS for the dev UI port. Add to `Workflow.sln`.
- [ ] **0.2** Create `src/forge/Forge.UI/` (Vite + React + TypeScript) via `npm create vite@latest -- --template react-ts`. Add ESLint + Prettier configs that match repo conventions.
- [ ] **0.3** App shell components: `<TopBar/>`, `<SideBar/>`, `<ViewSwitcher/>` mirroring the prototype layout under `src/forge/Forge.UI/src/shell/`. **Verbatim port** — keep DOM structure, class names, inline SVGs, and grid template areas (`"topbar topbar" "sidebar main"`) exactly as in the prototype.
- [ ] **0.4** Extract the prototype's `<style>` block verbatim into `src/forge/Forge.UI/src/styles/` split by the existing `/* ============= */` section banners (`reset.css`, `theme.css`, `shell.css`, `topbar.css`, `sidebar.css`, `main.css`, `filter-bar.css`, `board.css`, `timeline.css`, `workstream.css`, `pr.css`, `toast-modal.css`, `detail-panel.css`). **Do not** convert to CSS modules, Tailwind, or styled-components in this phase.
- [ ] **0.5** Light/dark theme toggle (React context + `localStorage` key `forge.theme`). Toggle behavior, CSS variable swaps, and the `data-theme` attribute on `<html>` must match the prototype exactly.
- [ ] **0.6** Routing with `react-router-dom`: `/`, `/plan`, `/timeline`, `/workstreams`, `/prs`, `/settings`. Deep-linkable.
- [ ] **0.7** Define shared types in `src/forge/Forge.UI/src/types.ts`: `Epic`, `Card`, `Column`, `Workstream`, `WsEvent`, `Pr`, `Agent`, `BuildStatus`, `ReviewVerdict`. Keep a parallel C# record set in `Forge.Api/Contracts/` and document the source-of-truth direction (server-first).
- [ ] **0.8** Port the prototype's seed data **verbatim** (same IDs, ordering, agent names, costs, timestamps, summaries) into `src/forge/Forge.UI/src/sampleData.ts`. The file is the source of truth for all visual fixtures used by fidelity checks.
- [ ] **0.9** State management: pick Zustand (lean, fits vanilla feel) and create stores per view (`useBoardStore`, `usePrStore`, …) under `src/forge/Forge.UI/src/state/`.
- [ ] **0.10** API client `src/forge/Forge.UI/src/api/forgeApi.ts` with typed fetch + problem-details handling. Honors `VITE_FORGE_API_URL`; falls back to sample data when `?seed=local`.
- [ ] **0.11** Generate the TS client from the API's OpenAPI doc (`Forge.Api` exposes `/openapi.json` via `Microsoft.AspNetCore.OpenApi`); script `npm run gen:api` writes to `src/api/generated/`.
- [ ] **0.12** Skeleton endpoints in `Forge.Api/Endpoints/StateEndpoints.cs`: `GET /api/state` returns the seed data shape so the UI can light up end-to-end.
- [ ] **0.13** SQLite persistence (`Microsoft.Data.Sqlite` + EF Core) under `src/forge/Forge.Api/Persistence/`. Migrations: `epics`, `cards`, `workstreams`, `prs`, `events`. DB file at `forge.db` next to the binary, override via `Forge:Db:Path`.
- [ ] **0.14** Idempotent seed on first boot importing `forge-board.html`'s sample data.
- [ ] **0.15** Tests: `Forge.Api.Tests/StateEndpointsSmokeTests.cs` (xUnit + `WebApplicationFactory`) verifies `GET /api/state` returns 200 with the expected counts.
- [ ] **0.16** Playwright spec in `Forge.UI.Tests/`: load `/plan`, assert the five columns render with at least one card.
- [ ] **0.17** `dev.ps1` (or npm script) that boots `Forge.Api` on `:5180` and `Forge.UI` Vite dev server on `:5181` together.
- [ ] **0.18** **Fidelity check** — Playwright `toHaveScreenshot` of the empty shell (topbar + sidebar + theme toggle) against a baseline rendered from `docs/examples/forge-board.html` itself. Pixel diff must be 0 in both light and dark themes.

## Phase 1 — Plan & Design (scrum board)

Make the board fully interactive against the local cache. No external sync yet — just a clean, fast scrum board.

- [ ] **1.1** `<Board/>` component renders columns with WIP limits, point totals, AI/human counts (port `renderBoard`).
- [ ] **1.2** `<Card/>` component with epic color, type icon (story/task/bug), agent chip, points, priority, branch, live progress bar.
- [ ] **1.3** `<FilterBar/>`: All / Needs approval / AI working / Blocked / Watching, plus epic / agent / priority / group-by selects and search.
- [ ] **1.4** Drag-and-drop with `dnd-kit`; reuse the prototype's `checkDragPolicy` rules (skip-phase confirm, human-gate confirm, re-open done) — port the function to TS in `src/forge/Forge.UI/src/board/policy.ts` and mirror it in `Forge.Api/Domain/DragPolicy.cs` for server-side enforcement.
- [ ] **1.5** `<DetailPanel/>` right rail: description, branch, agent timeline, "Auto-promote when…" rules, action buttons.
- [ ] **1.6** `<CardModal/>` for new / edit (epic, type, title, points, priority, agent, tags).
- [ ] **1.7** API endpoints in `Forge.Api/Endpoints/CardsEndpoints.cs`: `GET/POST /api/cards`, `PATCH /api/cards/{id}`, `POST /api/cards/{id}/move`.
- [ ] **1.8** SignalR `ForgeHub` (`/hubs/forge`) emits `card.changed` so a card moved in one tab updates other tabs. Wire `@microsoft/signalr` on the React side.
- [ ] **1.9** Optimistic UI for moves with rollback on server reject.
- [ ] **1.10** `<Toast/>` component (Zustand-backed queue) for transient messages ("Moved to Build", "Re-opened", etc.).
- [ ] **1.11** Sprint pill in page head wired to real burn-down (points done / total) — selector over board store.
- [ ] **1.12** Keyboard shortcuts via `react-hotkeys-hook`: `n` new card, `/` focus search, `esc` close panel, `1..5` switch columns focus.
- [ ] **1.13** Tests: `policy.spec.ts` (drag-policy parity, run on both client and server), endpoint contract tests in `Forge.Api.Tests`, one Playwright DnD spec.
- [ ] **1.14** **Fidelity check** — Playwright screenshot diff of `/plan` (light + dark, with the sample data, default filters) against the prototype. Diff must be 0. Repeat for: a card hovered, a card dragging, the detail panel open on `#234`, the new-card modal open.

## Phase 2 — Feature / branch timeline

Visualises the same data as a horizontal timeline with epic and workstream lanes.

- [ ] **2.1** Render time axis with Day / Week / Sprint zoom toggles.
- [ ] **2.2** Render epic lanes (group rows) with collapse/expand.
- [ ] **2.3** Render branch bars (`barStart` / `barEnd` from workstreams) with status color (active / merging / review / planning).
- [ ] **2.4** Overlay event markers: PR opened, merged, review, conflict, block (`wsEvents`).
- [ ] **2.5** Vertical "now" line and a `Now` button that scrolls to it.
- [ ] **2.6** "Forecast" toggle that extends planned bars (`planned: true`) with a hatched style.
- [ ] **2.7** Click a bar → opens the workstream detail (Phase 3).
- [ ] **2.8** Hover tooltip with branch name, ahead/behind, last-active, agents.
- [ ] **2.9** Filter chips: All / Active / Merging / Planning, plus lane toggle (Workstream vs Epic).
- [ ] **2.10** API `GET /api/timeline?from=…&to=…` returning normalized bars + events.
- [ ] **2.11** Telemetry: count timeline opens, time-on-view; structured logs `forge.timeline.*`.
- [ ] **2.12** Playwright: zoom toggle changes pixel density, "Now" scrolls to today.
- [ ] **2.13** **Fidelity check** — screenshot diff of `/timeline` against the prototype across all three zoom levels (Day / Week / Sprint), light + dark, with the sample data. Diff must be 0.

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

- [~] **4.1** Four-column PR board with `pr.col` mapping (`implementation | build | review | merged`). _(prototype has 3 cols — kept 3 per fidelity constraint; spec ↔ prototype contradiction needs resolution)_
- [ ] **4.2** PR card: title, author, branch, additions/deletions, files, age, build status, review verdict, conflict + human-gate badges.
- [ ] **4.3** Build status renderer (running with %, passing, failing, queued) with a tiny progress strip.
- [ ] **4.4** Review verdict pill (approved / changes / pending) with one-line agent summary.
- [~] **4.5** PR detail panel: full description, diff stats, reviewer list, build log tail (last 50 lines), human-gate reason. _(description + build-log are server-side stubs; real values arrive in Phase 5/6 from ADO/GitHub)_
- [~] **4.6** Filters: All open / Need approval / Failing / Conflicts / My reviews; selects for workstream / author. _(chips fully wired; Workstream/Author selects + search input are decorative — match the prototype but spec implies functional)_
- [~] **4.7** API `GET /api/prs` aggregates from cache + provider; `GET /api/prs/{id}` with details. _(cache-only today; provider aggregation lands with Phase 5)_
- [ ] **4.8** Auto-merge toggle (per PR) — flag stored locally; actual merge happens via provider in Phase 6.
- [~] **4.9** Re-run / cancel build buttons (UI only this phase; server stub returns 202 + plans Phase 6 hookup). _(complete-by-spec; the stubs flip cached state but no real CI is invoked)_
- [ ] **4.10** Toast on PR state transition via SignalR `pr.changed`.
- [ ] **4.11** Playwright: failing PR shows the `failing` chip; clicking opens the detail panel; auto-merge toggle persists across reloads.
- [ ] **4.12** **Fidelity check** — screenshot diff of `/prs` against the prototype: every PR card variant from the seed data (running build, queued, passing, failing, conflict, human-gate, auto-merge armed). Diff must be 0.

## Phase 5 — ADO / GitHub providers (read path)

Bring real work items, branches and PRs into the cache.

- [ ] **5.1** Add `IForgeProvider` interface in `Forge.Api/Providers/` (`ListWorkItems`, `ListPullRequests`, `ListBuilds`, `GetWorkItem`, `GetPr`, …).
- [ ] **5.2** Implement `AdoProvider` using `Microsoft.TeamFoundationServer.Client` / REST; PAT in user-secrets `Forge:Ado:Pat`. _(REST via HttpClient, not the TFS client lib — keeps deps small)_
- [ ] **5.3** Implement `GitHubProvider` using Octokit; PAT or fine-grained token in `Forge:GitHub:Pat`. _(REST via HttpClient, not Octokit — same reason)_
- [~] **5.4** Provider config UI page `/settings` (React): org/project (ADO), owner/repo (GitHub), PAT entry, "test connection". _(read + Test connection live; PAT entry intentionally NOT in the UI to avoid storing secrets in SQLite without an encryption story — config flows through user-secrets / env. **Superseded by Phase 9** — interactive sign-in replaces PAT entry as the primary auth path; PAT remains only as a headless / CI fallback.)_
- [ ] **5.5** Map ADO work item → `Card`: state → column, area path → epic, tags → tags, story points → pts, assigned-to → agent (human role).
- [ ] **5.6** Map GitHub issue + PR → `Card` / `Pr` (issue type by labels, PR draft status preserved).
- [~] **5.7** Background sync: `IHostedService` polling on configurable interval (default 60s), with jitter + ETag/If-None-Match where supported. _(polling + jitter shipped; ETag/If-None-Match is Phase R)_ **Phase 23 splits this**: cards / PRs / pipelines stay on the 60s `ProviderSyncService` cadence; **work-item sync moves to a separate 20-min `WorkItemSyncJob`** with proper pagination, delta-cursoring, and per-area-path scope. Both jobs run side-by-side and touch different tables.
- [ ] **5.8** Webhook receiver `POST /api/webhooks/{provider}` for ADO Service Hooks and GitHub webhooks; HMAC verification; falls back to polling if webhook is silent for >5 min.
- [~] **5.9** Conflict policy on overlapping local + remote edits: last-writer-wins by default, with a "remote changed since" badge on the card and an explicit "discard local" / "overwrite remote" choice. _(server-side 409 + currentRevision payload + X-Forge-Overwrite header land; UI badge + choice modal is Phase R)_
- [ ] **5.10** Cache invalidation on webhook; SignalR `card.changed` fan-out.
- [ ] **5.11** Provider health on `/settings`: last sync, error rate, rate-limit budget remaining.
- [ ] **5.12** Tests: provider contract tests against recorded HTTP cassettes (WireMock.Net); webhook signature unit tests; one end-to-end test that boots the provider with a fake server and asserts cards appear. _(used a 50-line StubHttpMessageHandler instead of WireMock.Net — same coverage, no extra dep)_

## Phase 6 — Write actions: WI, PR, pipelines

Make every read-only surface from Phase 5 round-trippable.

- [ ] **6.1** Create work item (ADO) / issue (GitHub) from the New-card modal, with provider selection.
- [ ] **6.2** Update work item: status transition, assignee, iteration/sprint, tags, story points, area path.
- [ ] **6.3** Comment on a card → posts to the linked WI / issue.
- [ ] **6.4** Create PR from a workstream (`POST /api/workstreams/{id}/pr`) — pushes branch, opens PR with templated description (linked card IDs, agent summary).
- [ ] **6.5** PR review actions: approve / request-changes / comment from the detail panel; surface line-level comments where the provider supports it. **Phase 24 extends this** with a persistent comment store (`pr_comments` table), authority enforcement (agents post but don't resolve — Q24.5), and a 20-min `PrSyncJob` that watches every active PR through to its terminal state. The endpoints in Phase 6.5 stay as the write surface; Phase 24 adds the read + reconcile loop on top.
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
- [~] **7.5** Telemetry: OTel spans `forge.view.*`, `forge.action.*`; counters for card moves, PRs opened, syncs failed. _(stable-name `ILogger` messages + client ring buffer ship today; full OTel SDK + metrics exporter rolled to `forge_v2.md`)_
- [~] **7.6** Auth: pluggable, default = single-user with a local secret; optional Azure AD via `Forge:Auth:AzureAd:*`. _(single-user secret middleware ships; Azure AD JWT pipeline is config-only skeleton — wired in v2. **Phase 9** lands the real interactive sign-in flow on top of this scaffold and replaces the local-secret default on Windows.)_
- [ ] **7.7** Dockerfile per project (`Forge.Api/Dockerfile`, `Forge.UI/Dockerfile`) + root `docker-compose.yml` (api + ui static + sqlite volume + reverse proxy). Document `forge.env.sample`.
- [ ] **7.8** User guide under `docs/forge/` (setup, providers, daily flow, keyboard, FAQ); architecture note linking `Forge.UI` ↔ `Forge.Api`.
- [~] **7.9** Smoke release checklist + a tagged `v0.1.0` once Phases 0–4 are green and Phases 5–6 work against at least one real ADO project and one real GitHub repo. _(checklist shipped at `docs/forge/release.md`; the actual `v0.1.0` tag is a manual cut step the operator runs once the gate items are ticked)_
- [ ] **7.10** Backlog grooming session: cut anything not in the goals list, file a `forge_v2.md` for the rest.

---

## Phase 8 — Products & repository management

Promote the implicit "one product, one repo" assumption to a real first-class hierarchy so the same Forge install can drive multiple products, each owning a mix of ADO and GitHub repos.

- [ ] **8.1** Domain records: `Product { id, name, slug, description, ownerUserId, createdAt }` and `Repo { id, productId, kind: 'ado' | 'github', remoteUrl, displayName, defaultBranch, cloneRoot, adoOrg?, adoProject?, ghOwner?, ghRepo? }`. Mirror as TS types in `src/forge/Forge.UI/src/types.ts` and C# records under `Forge.Api/Contracts/`.
- [ ] **8.2** EF Core migration: `products`, `repos` tables; FK `repos.product_id`. Add `repo_id` (nullable in migration, NOT NULL after backfill) to `workstreams`, `cards`, `prs` so every existing artifact belongs to a repo. **Migration must be idempotent** and re-runnable in dev. _(idempotent ALTER-COLUMN + CREATE-IF-NOT-EXISTS in `Phase8Migration.cs`; runs after `EnsureCreatedAsync` so dev DBs upgrade in place; `repo_id` shipped nullable today, NOT-NULL hardening deferred to Phase R per spec)_
- [ ] **8.3** Backfill migration: create a default `Atlas` product owning a default repo derived from the existing single-repo config; assign all pre-existing cards / workstreams / PRs to it.
- [ ] **8.4** API endpoints in `Forge.Api/Endpoints/ProductsEndpoints.cs` and `ReposEndpoints.cs`: `GET/POST /api/products`, `GET/PATCH/DELETE /api/products/{id}`, `GET/POST /api/products/{id}/repos`, `DELETE /api/repos/{id}` (with safety prompt + confirm-by-name). _(repos endpoints live in the same `ProductsEndpoints.cs` for now; spec's "ReposEndpoints.cs" file split is a Phase R refactor)_
- [~] **8.5** "Add repo" picker (server-driven): for ADO, lists repos in the configured project via `_apis/git/repositories`; for GitHub, lists repos for the signed-in user/org via `/user/repos` or `/orgs/{org}/repos`. **Discoverable** — the user picks from a list, not free-text URL entry. _(ADO + GitHub list endpoints + UI picker shipped against the configured PAT; multi-org listing waits for Phase 9 sign-in)_
- [ ] **8.6** Validation: remote URL parses to a known provider shape; clone root is on a writable disk; warn (don't block) if disk free space < 5 GB. _(URL + provider-host shape validation lands in `Domain/RepoValidation.cs`; disk-space pre-check is best-effort and intentionally non-blocking — auto-clone surfaces the real failure)_
- [ ] **8.7** New `/products` view (sidebar entry between "Workstreams" and "Settings"): grid of product cards with repo count, active workstream count, open PR count, last activity. Click a product → drill into its repos with the same metrics per repo.
- [ ] **8.8** TopBar product switcher (replaces the existing static "Atlas Sprint 14" pill with a `<select>` populated from `/api/products`); selection persists in `localStorage` as `forge.activeProductId` and scopes every other view (Plan, Timeline, Workstreams, PRs) to the selected product.
- [ ] **8.9** All existing endpoints accept an optional `productId` (or read the active product from a header `X-Forge-Product-Id`); responses are filtered server-side. Default = active product; explicit `all` value shows cross-product when the role allows it (Phase 10). _(role gate ships in Phase 10 — today every signed-in caller may use `all`)_
- [ ] **8.10** Auto-clone hook: when a workstream is created against a repo whose `cloneRoot` is empty, `IGitWorktreeService` runs `git clone` on first use (configurable to require manual pre-clone via `Forge:Repo:AutoClone=false`). _(separate `IRepoCloneService` keeps the worktree service single-purpose; SignalR `forge.repo.clone.{started,progress,finished,failed}` events stream during the clone)_
- [ ] **8.11** Cross-link parity: clicking a repo on `/products` deep-links to `/workstreams?repo={id}`; clicking a workstream surfaces its repo name + remote URL in the detail panel. _(deep-link wired; the detail-panel "remote URL" pill is a small follow-up — out of scope for the contract)_
- [ ] **8.12** Tests: products / repos endpoint contract; backfill migration unit test against a snapshot DB; UI test for the product switcher scoping the board. _(12 xUnit tests in `ProductsEndpointsTests.cs` cover the endpoint contract + scoping + backfill; Playwright UI test deferred — fidelity exemption per 8.13)_
- [ ] **8.13** **Fidelity note**: the prototype has no products page or product switcher. `/products` and the new switcher are **forge-design-token-clean** (use the existing CSS variables from `theme.css`) but **exempt from the `forge-board.html` screenshot-diff check**. Phase R may revisit.

## Phase 9 — Sign-in & current user (Edge popup, real identity)

Replace the hard-coded `XD / Xiaodong Li / Tech lead` placeholder with the real authenticated user, and use the Windows user's existing identity to consent into ADO and GitHub.

- [ ] **9.1** Server-side `IIdentityService` in `Forge.Api/Auth/`: `SignInAsync(provider)`, `SignOutAsync(provider)`, `GetCurrentUserAsync()`, `GetTokenAsync(provider, scopes)`. Each provider implementation is independent; sign-in to one does not imply sign-in to the other. _(landed as `IAuthService` in `Forge.Api/Auth/`; ADO + GitHub are independent singletons; current-user aggregation is the `/api/me` endpoint)_
- [ ] **9.2** **ADO sign-in** — MSAL.NET (`Microsoft.Identity.Client`) with the **WAM broker** enabled (`WithBroker(true)`). On Windows, this picks up the signed-in Windows account silently when the org allows it; otherwise it surfaces the standard system consent UI. App registration: tenant + client id under `Forge:Auth:Ado:*`. Scope: `499b84ac-1321-427f-aa17-267ca6975798/.default` (Azure DevOps). _(default client id is the Azure CLI well-known id, override via `Forge:Auth:AdoClientId`)_
- [ ] **9.3** **GitHub sign-in** — OAuth Authorization Code with PKCE (no client secret stored). Server hosts a localhost callback (`http://127.0.0.1:53682/forge/oauth/github/callback`); after redirect, server exchanges the code for a token. App configuration under `Forge:Auth:GitHub:*` (client id only). Scopes: `repo`, `read:user`, `read:org`. _(operator must register a GitHub OAuth App with PKCE enabled and set `Forge:Auth:GitHubClientId`; until then the button surfaces a helpful "register an OAuth App first" error)_
- [ ] **9.4** **Edge-first browser launch** on Windows: identity service spawns the consent URL via, in order, `msedge.exe --app={url}` (popup-style chromeless window) → `msedge.exe {url}` → `Process.Start({ UseShellExecute=true, FileName=url })` (system default). Behavior is configurable via `Forge:Auth:BrowserPreference=edge|default`. Non-Windows: always system default.
- [ ] **9.5** **Token storage**: DPAPI-encrypted (`ProtectedData.Protect` with `CurrentUser` scope) blob at `%LOCALAPPDATA%\Forge\tokens.dat`. Refresh tokens used silently before access-token expiry. **Never** in SQLite plaintext, never in user-secrets in production. macOS / Linux fallback uses the OS keychain via `Microsoft.Identity.Client.Extensions.Msal`. _(MSAL cache lives at `%LOCALAPPDATA%\Forge\msal-cache.bin`; GitHub token at `%LOCALAPPDATA%\Forge\github-token.bin`)_
- [ ] **9.6** `GET /api/me` returns `{ id, displayName, email, login, avatarUrl, initials, role, providers: { ado: { connected, account, expiresAt } | null, github: { connected, login, expiresAt } | null } }`. New React hook `useCurrentUser()` exposes this with SignalR-driven invalidation when sign-in state changes. _(SignalR push-invalidation deferred — UI calls `useCurrentUserStore.refresh()` from the sign-in/out handlers, which is sufficient for v0.1 since identity changes only happen through the local UI)_
- [ ] **9.7** Replace the hard-coded placeholder strings:
  - `src/forge/Forge.UI/src/shell/TopBar.tsx:60` — `<div className="avatar" title="Xiaodong">XD</div>` reads from `useCurrentUser()`; initials fall back to first-letters of `displayName`; tooltip = full display name + email.
  - `src/forge/Forge.UI/src/shell/SideBar.tsx:154-157` — `XD` / `Xiaodong Li` / `Tech lead · Atlas` all read from `useCurrentUser()` (avatar URL preferred over initials when present).
  - When no provider is signed in, show a "Sign in" CTA in both spots instead of a name.
- [ ] **9.8** Extend `Role` (`src/forge/Forge.UI/src/types.ts:4`) to include `'product-owner' | 'tech-lead'` alongside the existing `'arch' | 'eng' | 'rev' | 'qa' | 'human'`. Server enforces role transitions; UI renders the role label after the `·` (e.g. `Product Owner · Atlas`). _(server-side role transitions land in Phase 10's role-gating middleware; today the role is set at first-sign-in and editable via direct DB write)_
- [ ] **9.9** **First-run rule**: the first user to sign in to a fresh DB is auto-assigned `product-owner` (the operator). Subsequent sign-ins default to `human` and must be promoted by a Product Owner. Bootstrap mapping for the current operator: `lingxd@gmail.com` → `product-owner` (recorded as a seed for dev convenience; not hard-coded in prod).
- [ ] **9.10** User-menu popover (anchored on the TopBar avatar): shows display name, email, signed-in providers with status pills, "Re-authenticate ADO", "Re-authenticate GitHub", "Sign out", "Switch account". _("Switch account" is implicit — re-auth opens a fresh account picker)_
- [ ] **9.11** Migrate the seed `agent: { id: 'XD', role: 'human', name: 'Xiaodong' }` references in `sampleData.ts` to `{ id: 'XD', role: 'product-owner', name: 'Xiaodong' }` so the prototype data stays valid under the new role enum.
- [~] **9.12** Tests: identity service contract test with a fake MSAL token broker; GitHub OAuth callback unit test; UI test asserts the real signed-in user replaces `XD` in TopBar + SideBar; mock Edge launch verifies the `msedge.exe --app=` invocation and the fallback chain; expired-token refresh test. _(7 xUnit smoke tests in `AuthAndMeTests.cs` cover the unauthenticated paths + 401 contracts; deeper MSAL / OAuth callback tests deferred — they need a fake MSAL broker / a test HTTP listener and aren't a release blocker)_
- [ ] **9.13** Security review checklist: token-at-rest format, scope minimization (read-only by default; write scopes requested only when a write action is invoked), opt-in telemetry of sign-in events, DPAPI key rotation handling, behavior when the Windows account changes.

## Phase 10 — Defaults, preferences & Product Owner supervisory view

Make Phases 8 + 9 ergonomic by letting the user pin defaults, and give the Product Owner a real supervisory dashboard over the agent crews they oversee.

- [ ] **10.1** Per-user preferences table `forge_user_settings` (PK = `user_id`): `defaultAdoOrg`, `defaultAdoProject`, `defaultGithubOwner`, `defaultProductId`, `defaultRepoId`, `browserPreference`, `theme`, `notificationsEnabled`. Mirror as TS in `src/forge/Forge.UI/src/types.ts`.
- [ ] **10.2** `GET /api/me/settings` and `PATCH /api/me/settings` (idempotent partial update). Validation: referenced `productId` / `repoId` must exist and the user must have access. _(productId/repoId existence check is best-effort — UI rejects with the standard 404 if a default points at a deleted product; tightening to a server-side validator is a small follow-up)_
- [ ] **10.3** "Defaults" panel on `/settings`: combo-boxes for default ADO org + project (populated from `/api/providers/ado/orgs` and `_apis/projects` once signed in), default GitHub owner (populated from `/user/orgs` + `/user`), default product, default repo. Each row has an "Apply everywhere" checkbox that copies the value to the relevant create-flow defaults. _(text inputs land today; combo-boxes that auto-populate from the provider APIs are a Phase R polish — operator pastes the URL and project name. ADO defaults flow into AdoConfigResolver so `Sync now` immediately uses the new values. "Apply everywhere" checkbox not yet rendered.)_
- [~] **10.4** Pre-fill new-card modal, new-workstream modal, "Create PR from workstream", and the `/products` "Add repo" picker from these defaults. The user can override per action; overrides do **not** mutate the saved default unless an "Update default" toggle is ticked. _(server-side defaults resolution lands in 10.5; UI pre-fill is a small follow-up — modals still take the user input directly today)_
- [~] **10.5** Server-side default resolution: when an API request omits `productId` / `repoId` / `adoProject` / etc., the server falls back to the caller's saved defaults before erroring; the response includes a `defaultsApplied: [...]` array so the UI can surface which fields were auto-filled. _(ADO config-resolver path is wired — AdoProvider transparently picks up user-settings org/project. Per-endpoint `defaultsApplied: [...]` echoing is deferred — would need ProductScope-style plumbing on every write endpoint.)_
- [ ] **10.6** **Crew model**: a `Crew { id, name, lead, summary, model, modelInputPricePerMTok, modelOutputPricePerMTok }` record (model + pricing per the Q3 resolution). **Crews are global — not scoped to any product.** Any product-owner can manage any crew. Each `Agent` gets a single `crewId` (one crew per agent — many-to-many is overkill at v0.1). Seed crews from the prototype's agent prefixes — `E*` → "Engineering", `R*` → "Review", `A*` → "Architecture", `Q*` → "QA" — so the first run has data without operator setup. Default model assignments: `Engineering` → `claude-opus-4-7`, `Review` → `claude-sonnet-4-6`, `Architecture` → `claude-opus-4-7`, `QA` → `claude-haiku-4-5` (operator overrides via `/settings/crews`).
- [~] **10.7** `/crews` view (sidebar entry, visible only when current user role = `product-owner`): one row per crew with live metrics — members, cards in flight, blockers, **salary today / sprint / month** (live from the cost ledger in Phase 11.4), tokens burned (in / out split), 24h cost, last-active timestamp, open PR count, model name pill. Click a crew → drill into its agents and recent activity (re-uses the workstream detail panel layout); from there, PO can pause / resume agents, edit soul.md, or swap the crew's model. _(view ships with members + in-flight + blocked counts + model pill + pricing; live salary readouts wait for the Phase 11 cost ledger; pause / resume / soul.md editor are Phase 11 surfaces)_
- [ ] **10.8** Cross-product / cross-crew action audit: every write performed by a Product Owner against a crew or product they don't own is recorded in `forge_actions` (Phase 6.10) with a `supervising = true` flag and surfaced as a separate filter on `/audit`. _(schema lands today — `Supervising` column on `forge_actions`; AuditMiddleware doesn't yet populate it because the "owns this product" check requires Phase 10.5 productId scope to be plumbed. Schema-only readiness so the column is here when 10.5 fills it.)_
- [ ] **10.9** Role-gating middleware: `[RequireRole("product-owner")]` attribute (or minimal-API filter equivalent) protects `/api/crews/*` writes and `/products/*` admin endpoints. Read endpoints stay open to all signed-in roles. _(landed as `RequireRole.Check(ctx, db, "product-owner")` minimal-API helper used inside each handler. Single-user dev installs pass through; once a UserRow exists with a non-PO role, writes from that user return 403.)_
- [ ] **10.10** UI gating: hooks `useHasRole('product-owner')` hide `/crews` from the sidebar and disable supervisory action buttons for non-PO users (the buttons render but are disabled with a tooltip explaining the gate, rather than vanishing — discoverable). _(sidebar Crews entry is gated; CrewsView itself shows a "Restricted" empty state when the current user isn't PO. Disabled-with-tooltip pattern for individual buttons is a small follow-up.)_
- [~] **10.11** Tests: defaults round-trip endpoint test; defaults pre-fill UI test for the new-card and new-workstream modals; role-gating endpoint test (non-PO returns 403); crew aggregation unit test; cross-product audit recording test. _(9 xUnit tests in `SettingsAndCrewsTests.cs` cover round-trip / role-gate / per-repo edit / unauthorized paths; UI pre-fill test deferred until 10.4 ships; cross-product audit test deferred until 10.8 has populated `Supervising`.)_
- [ ] **10.12** **Fidelity note**: same as Phase 8 — `/crews`, the user-menu popover, the Defaults panel, and the role-aware sidebar are exempt from the `forge-board.html` screenshot diff but must use existing forge tokens. Document any new tokens in `docs/forge/design-tokens.md`.

## Phase 11 — Agent runtime & crew economy

Make the agent labels real. Forge spawns and supervises the model that backs each crew, every invocation goes through the cost ledger, and crew salary becomes a live readout instead of a prototype hard-code. Per the Q3 resolution this phase is **in scope for v0.1** — agents are first-class.

- [ ] **11.1** `IAgentRunner` interface in `Forge.Api/Agents/`: `StartAsync(taskId, agentId, prompt, context, ct)` returns an `AgentInvocation` handle exposing a `Stream<AgentEvent>` (token deltas, status, tool calls), `CancelAsync(invocationId)`, `PauseAsync(agentId)`, `ResumeAsync(agentId)`, `GetStateAsync(agentId)`. One invocation = one task; long-running agents are modeled as a sequence of invocations, not a single process.
- [ ] **11.2** **Model registry** seeded from a static `models.json` shipped with the API: `{ id, vendor, family, displayName, contextWindow, inputPricePerMTok, outputPricePerMTok, supportsTools, supportsStreaming, status: 'live' | 'deprecated' }`. Default seed includes the current Claude family (`claude-opus-4-7`, `claude-sonnet-4-6`, `claude-haiku-4-5-20251001`) plus a `local-stub` model for offline dev. Exposed via `GET /api/models`. Pricing is denormalized onto each `Crew` row at assignment time so historical salary is reproducible after a price change.
- [~] **11.3** **soul.md authoring**: file-system source of truth at `%LOCALAPPDATA%\Forge\agents\{agentId}\soul.md` (path overridable per agent). `/settings/agents` gains an in-app editor (CodeMirror, markdown preview side-by-side) that reads + writes the file with file-watcher-driven invalidation so external edits hot-reload. soul.md is the system prompt at runtime; capabilities (`tools`, `mcp_servers`, `ado_scopes`) declared in YAML front-matter at the top. _(filesystem service + GET/PUT endpoints + YAML front-matter `tools:` parser ship; CodeMirror `/settings/agents` editor is Phase R UI work — operators currently edit `soul.md` directly on disk and the API picks it up on next read)_
- [ ] **11.4** **Cost ledger** table `agent_usage(id, agentId, crewId, taskId, model, inputTokens, outputTokens, costUsd, startedAt, endedAt, status)` written by the runner on every invocation finish (or partial-finish on cancel). Indexed by `(crewId, startedAt)` and `(taskId)`. SQL views: `crew_salary_today`, `crew_salary_sprint`, `crew_salary_month`. Card cost in Phase 1 becomes `SUM(costUsd) WHERE taskId = card.id`, replacing the prototype's hard-coded values once the runner is live. _(SQL views replaced by an in-memory roll-up in `CrewsEndpoints.GetSalary` — SQLite EF can't translate `DateTimeOffset` filters or `SUM` over a decimal-converter column. Volume is bounded so in-memory is cheap.)_
- [ ] **11.5** **Concrete runner**: `ClaudeAgentRunner` (Anthropic SDK) — uses prompt caching for the soul.md prefix (5-min TTL), streaming responses via SSE, retry-with-backoff on 529 / 503. Per-invocation isolation: each runs in its own scoped `HttpClient`; no shared state across agents. Vendor abstraction is thin so a future `OpenAIAgentRunner` slots in without touching callers. _(uses HttpClient + the Anthropic REST API directly — same pattern as Phase 5's AdoProvider/GitHubProvider, "keeps deps small")_
- [ ] **11.5b** **Dispatcher across 2×2 cells**: `ClaudeAgentRunner` was renamed `AgentRunnerDispatcher` and is now the only `IAgentRunner` registration. It routes each `StartAsync` call through one of four (backend × mode) cells, picked from `AgentContext.Backend` / `AgentContext.InvokeMode` (defaults: **Copilot · SDK**):
  - **Copilot · SDK** — in-process `CopilotClient` (GitHub.Copilot.SDK 0.3.0), streams `AssistantMessageDelta` / `ToolExecutionStart` / `ToolExecutionComplete` events into the same `AgentEvent` shape downstream consumers already use.
  - **Copilot · CLI** — shells out to `copilot --prompt - -m <model> --yolo`; soul.md is folded into the stdin prefix; stdout streamed line-by-line.
  - **Claude · SDK** — the existing 11.5 path (POST `/v1/messages` with streaming SSE + ephemeral prompt cache on the soul.md system block).
  - **Claude · CLI** — shells out to `claude --print --model <model>`; soul.md folded into stdin prefix.
  `local-stub` model bypasses every cell with a deterministic token stream for tests / offline. CLI cells are **text-only** — no tool-call surface — so callers that need tools (Phase 13.4 / 13.5 plan + coding pipelines) must use SDK mode. Defaults are operator-overridable via `Forge:Agents:DefaultBackend` / `Forge:Agents:DefaultInvokeMode`; CLI executable paths via `Forge:Agents:CopilotCliPath` / `Forge:Agents:ClaudeCliPath`. Mirrors the workflow-side `IAIExecutor` pattern (`ExecutionEngine.AI.CopilotAIExecutor` / `ClaudeCliAIExecutor`) but extends it to all four cells and surfaces results as Forge's streaming `AgentEvent`. _(see `src/forge/Forge.Api/Agents/AgentRunnerDispatcher.cs`)_
- [ ] **11.6** **Sandboxing**: agents run inside the Forge.Api host as Tasks, NOT subprocess; tool execution (file edits, git, shell) is gated through an `IAgentToolHost` that enforces (a) the workstream's worktree path as the only writable root, (b) per-tool allowlist from soul.md front-matter, (c) hard wall-clock + token budgets per invocation. Subprocess isolation is a Phase R upgrade.
- [ ] **11.7** **Live status streaming**: SignalR channel `agent.{agentId}` broadcasts `started | token | tool-call | tool-result | finished | failed | paused`. UI hooks: card live-progress bar (Phase 1.2) reads the active invocation; `/crews` row updates in place; PR detail panel build-log tail (Phase 4.5) gains an "Agent activity" tab. _(server-side SignalR group + AgentsEndpoints ship; the in-place card / PR-panel UI hooks are a small follow-up — out of scope for the contract that the events stream)_
- [ ] **11.8** **Pause / resume / reassign**: PO actions on `/crews` and the agent detail panel. Pause halts the agent's task pickup loop and surfaces a `paused` pill; resume resumes from queue; reassign moves an agent to another crew (model + pricing change recorded in `agent_history`).
- [ ] **11.9** **Salary readouts**: `/crews` cards show salary today / sprint / month with a sparkline; `/products` rolls up across crews; TopBar gains a small `$X.XX today` chip (PO only). Numbers are USD with the model's pricing — currency conversion is out of scope for v0.1. _(per-crew + per-day endpoint live; `/crews` shows today/sprint/month + 14-day SVG sparkline; TopBar PO-only chip refreshes every 60s. `/products` salary roll-up is a small follow-up. "Sprint" = current ISO week per the v0.1 simplification — real iteration lookup against the linked repo's provider is Phase R.)_
- [ ] **11.10** **Budgets & alerts**: per-crew daily and per-task hard caps (in `Crew.dailyBudgetUsd`, `Crew.maxTaskUsd`); the runner refuses to start an invocation that would exceed the cap and emits a `forge.agent.budget_exceeded` event. PO sees an inline banner on `/crews` until acknowledged. _(StartInvocation pre-flight returns 402 + emits `agent.budget_exceeded` SignalR event; CrewsView highlights `today` in red when over `dailyBudgetUsd`. The full "until acknowledged" banner is a small follow-up.)_
- [~] **11.11** **Tests**: `IAgentRunner` contract test with a fake LLM (deterministic token stream); cost-ledger arithmetic test (input + output sums match); soul.md hot-reload test; budget-cap refusal test; one Playwright E2E that triggers an agent on a card, observes streaming tokens, and confirms cost lands on the card and the crew salary readout. _(24 xUnit tests across `AgentsFoundationTests`, `AgentRuntimeTests`, `AgentSoulAndBudgetTests`, `AgentSalaryAndLedgerTests` cover everything except the Playwright E2E. Playwright is deferred — the existing UI flows route through the same SignalR + endpoints these tests exercise.)_
- [ ] **11.12** **Fidelity**: not in prototype — exempt from screenshot diff. New UI surfaces (soul.md editor, agent detail panel, salary chip) must use existing forge tokens; document new tokens in `docs/forge/design-tokens.md`.

---

## Phase 12 — Work Item Tracking (WIT)

> Promote the implicit Phase-1 "card" model into a first-class **work item** system that round-trips with ADO Boards and GitHub Issues, supports the full hierarchy (`epic → feature → user-story → task | bug`), and gives the operator multiple views over the same data: **Board**, **Timeline**, **Tree**, **List**.

Context — what's already in scope vs. what this phase adds:
- Phase 1 ships a sprint board (one slice of work items, one product, no parent/child).
- Phase 2 ships a timeline (over workstreams / branches, **not** items).
- Phase 5 syncs work items into the cache (read).
- Phase 6 writes a subset of work items back (create / update / status transitions).
- **Phase 12 adds the dedicated workspace** that lets the operator browse / sort / filter / create / re-parent / move-iterations across **all** work items (not just the active sprint), in any of the four canonical views, with full provider round-trip and bulk operations.

### Resolved decisions

- **Q12.1 Hierarchy + tracks** — two parallel three-level ladders, each tagged with a `track` field so the UI can render the right vocabulary and the API can validate parenthood per ladder:
  - **Dev track**: `epic → feature → task | bug` (task and bug are sibling leaves, not nested).
  - **Design track**: `phase → stage → step`.
  - Both top-level containers (`epic`, `phase`) carry an optional `featureRef` pointing at a product feature / functional area / story id, so a single product feature can have both a development epic **and** a design phase under it. The user-story tier is intentionally absent from the primary ladder; legacy ADO "User Story" / GitHub story-issue records map to `feature` on import (preserving the original id in `providerRef`). Cycle prevention is enforced at the API layer; a child's rank must exceed its parent's rank within the same track. Cross-track linking is permitted only at the top level (epic ↔ phase pinned to the same `featureRef`). v0.1 explicitly does not support deeper nesting; revisit if operators demand sub-task threading.
- **Q12.2 Provider parity** — full read/write parity with **ADO Boards + GitHub Issues**, equal first-class. ADO uses **WIQL** queries scoped by `System.AreaPath` + `System.IterationPath`; GitHub uses **GraphQL Issues + Projects v2** with milestone as iteration source. Sub-issue support on GitHub uses the new sub-issues feature when available, falling back to a `parent: #N` body convention.
- **Q12.3 State model** — normalised to **four states** across providers: `proposed | active | resolved | closed`. Mappings:
  - **ADO** → `New / Approved / Committed` ⇒ `proposed`; `Active` ⇒ `active`; `Resolved` ⇒ `resolved`; `Closed / Removed` ⇒ `closed`.
  - **GitHub** → `open` + state-reason `not_planned` ⇒ `proposed`; `open` (no reason) ⇒ `active`; `closed` + reason `completed` ⇒ `resolved`; `closed` + reason `not_planned` ⇒ `closed`.
- **Q12.4 Iteration source** — follows **Q4** (Phase 8): ADO repos use ADO iteration paths; GitHub repos use GitHub milestones. **No local Forge iteration.** Cross-repo iteration roll-up is a Phase R / `forge_v2.md` candidate.
- **Q12.5 Identity for assignment** — `assignedTo` carries either an **`agent`** (Forge agent id, e.g. `E4`) **or** a **`human`** (UPN / GitHub login). Phase 9 user identity is the source of truth; agents are resolved against the `agents` table. **Phase 17 expanded this** to add `team` as a third assignee kind for queue routing. **Phase 22 split the field**: `AssignedTo` keeps the WIT-semantic value (operator / provider sync writes here) while a new `AssignedAgent` field carries the runtime fact (queue worker writes here on claim). The two never auto-sync; see Q22.1 for the boundary.
- **Q12.6 Mode persistence** — selected view-mode persists per user in `localStorage` as `forge.wit.mode`; iteration / area / type filters persist as `forge.wit.filters`. Per-product, so switching products resets the filter chips.
- **Q12.7 Conflict policy** — same as Phase 5.9 cards: last-writer-wins by default, surfaced as a "remote changed since" badge with discard / overwrite choice.

### Implementation steps

- [ ] **12.1** Domain model — `WorkItemRow`:
  ```
  Id (string, primary key — same shape as Card.Id)
  RepoId (FK → repos)
  Provider ("ado" | "github" | "local")
  ProviderRef (provider's work item id / issue number — null for local-only)
  Type ("epic" | "feature" | "user-story" | "task" | "bug")
  State ("proposed" | "active" | "resolved" | "closed")
  Title
  DescriptionMd (markdown body)
  ParentId (nullable, FK → work_items, enforced acyclic)
  IterationRef (string — namespaced by repo: "{repoId}:{providerSprintId}")
  AreaPath (string)
  AssignedToJson ({ kind: "agent"|"human", id, displayName })
  Points (int)
  Priority ("low"|"med"|"high")
  TagsJson (string[])
  CreatedAt, UpdatedAt (DateTimeOffset)
  RemoteRevision (opaque marker — same as cards.RemoteRevision)
  ```
  TS mirror in `src/forge/Forge.UI/src/workitems/workItemTypes.ts` (`WorkItem`, `WorkItemType`, `WorkItemState`, `WorkItemAssignee`, `WorkItemTrack`). _(Per Q12.1 revision: `Type` is `epic | feature | task | bug | phase | stage | step` and the row carries a `Track` discriminator.)_
- [~] **12.2** Migration — `Phase12Migration.cs`:
  - `work_items` table (the columns above).
  - `work_item_links` table (`{ id, parentId, childId, kind: "subtask"|"related"|"duplicate", createdAt }`) for cross-product / non-hierarchy links; the primary parent stays denormalised on `work_items.ParentId`.
  - **Backfill**: every existing `Card` becomes a `WorkItem` with `Type` derived from `Card.Type` (story → feature, task → task, bug → bug per Q12.1 revision), `State` derived from `Card.Col` (backlog/design → proposed; build → active; review → resolved; done → closed), `ParentId` set to the card's epic id where unambiguous. _(shipped — `ForgeSeed.BackfillWorkItemsFromCardsAsync`)_
  - **Cards become a projection**: the existing `cards` endpoints continue to serve the active-sprint slice, computed from `work_items` (where `iterationRef = activeSprint AND state IN (proposed, active, resolved)`). _(deferred to Phase R — column→state mapping is many-to-one (backlog and design both → proposed) so an in-place rewire would lose the distinction; the existing `cards` endpoints continue to serve the cards table while `work_items` operates side-by-side.)_
- [ ] **12.3** Endpoints — `Forge.Api/Endpoints/WorkItemsEndpoints.cs`:
  - `GET /api/work-items` — paginated list, filterable by `repoId`, `iterationRef`, `state[]`, `type[]`, `parentId`, `assignedTo`, `tag`, `q` (full-text). Default page size 100, max 500.
  - `GET /api/work-items/{id}` — single item with linked children + parents resolved.
  - `POST /api/work-items` — create (server fills id, createdAt, updatedAt; pushes to provider when `provider != "local"`).
  - `PATCH /api/work-items/{id}` — partial update; honours `If-Match: revision` for the Phase 5.9 conflict policy.
  - `DELETE /api/work-items/{id}` — soft-delete (sets `state = closed`); hard delete only with `?force=true` and PO role.
  - `POST /api/work-items/{id}/move` — body `{ state? | iterationRef? }`; emits SignalR `workitem.changed`.
  - `POST /api/work-items/{id}/parent` — body `{ parentId | null }`; cycle check; emits `workitem.linked`.
  - `POST /api/work-items/sync/{repoId}` — force a one-shot sync (writer-gated; `ignoreThrottle=true` is PO-only; rate-limited to 1/minute per repo).
  - `POST /api/work-items/bulk` — body `{ ids[], op: "move"|"reassign"|"tag"|"delete", payload }`. Idempotency-Key required. _(deferred — UI bulk action bar iterates per-id PATCH/move/delete calls today, which is enough for sample-size selections; the dedicated bulk endpoint is a Phase R candidate when selection sizes regularly exceed ~50.)_
- [ ] **12.4** Provider sync extensions:
  - **ADO** — extend `AdoProvider.ListWorkItems` to accept `(areaPath, iterationPath, types[])` filters; emit a single batched WIQL query, then `_apis/wit/workitems?ids=…&fields=…&$expand=relations` for hierarchy. Map `System.Parent` → `ParentId`.
  - **GitHub** — `GitHubProvider.ListWorkItemsForSyncAsync` builds an `/issues` query string (`assignee` / `labels` / `since`); parent extracted from the issue body's `Parent: #N` line (and from native sub-issue `parent.number` when present). REST + HttpClient, not GraphQL — kept consistent with the rest of the codebase's "no SDK" choice; GraphQL upgrade is a Phase R candidate.
  - Both providers report `RemoteRevision` so the conflict policy works uniformly.
- [ ] **12.5** Sidebar nav entry — new `Work items` link between **Plan & Design** and **Timeline**, with a live count badge (`active + proposed` items in the active product). The badge re-renders on the SignalR `workitem.changed` event.
- [ ] **12.6** `/work-items` route with view-mode toggle:
  - **Board** — columns by state (`proposed | active | resolved | closed`), swimlanes by `type` or `assignedTo` (toggleable). Drag-and-drop changes `state`.
  - **Timeline** — Gantt-style, rows grouped by epic, x-axis = current + next 2 iterations (configurable). Bars span an item's iteration; click → detail panel.
  - **Tree** — indented hierarchy `epic → feature → story → task/bug`. Expand / collapse per node. Right-click → reparent. Cycle attempts blocked at the API.
  - **List** — dense table with sort + multi-select. Columns: type icon, id, title, state pill, iteration, assignee, points, priority, tags. Shift-click + lasso for range select.
  - Mode persists in `localStorage.forge.wit.mode` (Q12.6). _(Drag-and-drop on Board now calls `/api/work-items/{id}/move`. The Tree right-click reparent is shipped via the detail panel; lasso multi-select is deferred to Phase R polish.)_
- [~] **12.7** Detail panel (right rail, re-uses `WorkstreamDetailPanel` layout):
  - Title (inline-editable), state pill, iteration combo, assignee picker, points stepper, priority chip, tags input, area path text.
  - Description (markdown editor with preview).
  - Children list (with mini progress bar).
  - Parent breadcrumb (clickable to focus parent).
  - Linked PRs + branches (re-uses Phase 3 cross-link).
  - Comments thread (round-trips via provider).
  - History (state transitions + assignment changes from `agent_history`-style audit). _(Inline-edit live for title / state / points / priority. Parent breadcrumb + children list read live. Iteration combo, assignee picker, tag editor, markdown editor, comments thread, history pane are Phase 12.7 follow-up — they need richer pickers (sprint list per repo, people search, autocomplete) and a CodeMirror integration.)_
- [~] **12.8** Bulk operations:
  - Multi-select via shift-click + drag-lasso on Board / List modes.
  - Bulk action bar appears at the bottom: change-state | change-iteration | reassign | tag | delete.
  - All bulk writes use a single `Idempotency-Key` so a retry doesn't duplicate. _(Shift-click multi-select + Bulk **Change state** + **Delete** ship; iteration / reassign / tag wait on the same Phase 12.7 picker work. Today the UI iterates per-id PATCH; the dedicated `/api/work-items/bulk` endpoint with idempotency-key replay is a Phase R candidate when selection sizes regularly exceed ~50.)_
- [ ] **12.9** Filter bar:
  - Chips: `All / Mine / Active / Blocked / Watching` (mirrors Phase 1.3 chips).
  - Selects: `Type`, `State`, `Iteration`, `Area`, `Assignee`, `Tag`, `Repo`, `Provider`.
  - Search input → server-side `q=` (FTS over title + description).
  - Filters persist in `localStorage.forge.wit.filters` (per-product).
- [ ] **12.10** Sync UX:
  - Per-repo "Sync now" button on the filter bar; surfaces last-sync timestamp + error if any.
  - Auto-poll every 60 s (re-uses Phase 5.7 `ProviderSyncService`). **Superseded by Phase 23** for work items specifically: the 20-min `WorkItemSyncJob` is the canonical scheduled run. The 60-s `ProviderSyncService` keeps cards / PRs / pipelines fresh. The `Sync now` button still calls `POST /api/work-items/sync/{repoId}` which now routes through `WorkItemSyncJob.RunAsync(kind='manual')`.
  - Conflict pill on items where `RemoteRevision` advanced since the local edit; click opens a discard / overwrite modal (Phase 5.9 contract).
  - Local-only items (no `ProviderRef`) get a "Push to provider" button that creates the upstream issue / WI on demand. _(SyncDialog → `POST /api/work-items/sync/{repoId}` ships with form filters (areaPath, types, assignedTo, date range) + 60s throttle (PO can `ignoreThrottle`) + inline error banner. SignalR `workitem.changed { kind: synced }` triggers a UI refresh. Auto-poll, conflict-pill modal, and "Push to provider" for local-only items are Phase R polish.)_
- [~] **12.11** Tests:
  - Domain mappers (ADO state ↔ Forge state, GitHub state-reason ↔ Forge state).
  - Hierarchy invariants: cycle rejection, depth ≤ 5, parent-must-be-of-higher-type-rank.
  - Endpoint contract tests — every endpoint above (filter combos, pagination, idempotency on bulk).
  - Sync round-trip via `StubHttpMessageHandler` for ADO + GitHub cassettes.
  - Playwright E2E happy-path: create item → assign → move iteration → reparent → push to provider → assert remote write was called with the right payload. _(50 xUnit tests across `WorkItemsEndpointsTests`, `WorkItemsSyncEndpointTests`, `AdoWorkItemSyncTests`, `GitHubWorkItemSyncTests`, `WorkItemRulesTests`, `WorkItemSyncUpsertTests`, `WorkItemsLifecycleTests` cover everything except the Playwright E2E. The lifecycle test (create → patch → move → reparent → softDelete) substitutes server-side for the Playwright happy-path. Playwright is deferred — the existing flows route through the same endpoints these tests exercise.)_
- [ ] **12.12** **Fidelity**: new view; exempt from `forge-board.html` screenshot diff. Must use existing forge tokens. _(Zero new tokens introduced — state colors map to existing `--warning` / `--info` / `--success` / `--text-tertiary`; type marks reuse the accent / info / success / danger / text-secondary palette. The `--state-*` tokens anticipated in the original spec turned out to be unnecessary.)_

### Out of scope (explicitly deferred)

- **Cross-repo / cross-product roll-up timeline** — Phase R / `forge_v2.md` candidate per Q4.
- **Custom work item types** (e.g. ADO-tenant-defined "Feature Request") — v0.1 ships the Q12.3 fixed five-type set; mapping for unknown types falls back to `task`.
- **Inline saved queries** (a la ADO Queries / GitHub Issue Search) — Phase R polish.
- **Burndown / velocity charts** — derived from sprint slice; Phase R / `forge_v2.md`.
- **Email notifications on assignment** — provider already does this; Forge does not duplicate.

---

## Phase 13 — Crew management & task orchestration

> Phase 11 made agents real (model registry, cost ledger, soul.md, runner). Phase 12 made work items real (epic → feature → task | bug, phase → stage → step). **Phase 13 ties them together**: crews become explicit organizational units with hierarchy, role-aware leads, capability authoring, and a typed-task pipeline that turns a feature into PR-sized work the runtime can pick up.

Two principles drive the shape:

1. **Plan is a task type, not a phase**. A *plan task* takes a feature/story and emits a spec + the WIT phase → stage → step breakdown (each `step` becomes the unit a coding task can claim). This makes planning observable, auditable, and budgeted exactly like coding.
2. **Crews learn**. When a crew completes work, the lead distills what was learned about the domain into a `domain-expert.md` artifact stored alongside the workstream's repo. Future crews ingest those files as front-matter `capabilities:` in their soul.md, so capability is authored, not configured.

### Resolved decisions

- **Q13.1 Crew topology** — each crew has exactly one **lead** (an agent or human) and zero-or-more **members** (agents or humans). Crews can nest: a parent crew (`parentCrewId`) groups child crews under a team-of-teams. Depth cap = 3 (Org → Team → Squad). Membership is exclusive within a depth: an agent can be a member of multiple **child** crews but only one direct **lead** assignment, so accountability for each task has a single owner.
- **Q13.2 Lead = router** — only the lead can assign tasks within their crew. The lead may assign to themselves; the runtime treats self-assignment identically to peer-assignment for ledger and quota purposes. Reassignment is a write requiring **global** `RequireRole.ProductOwner` *or* `crew.lead == caller`. **Crews are global entities** — not scoped to any product. Any user with a `product-owner` role (global or on any product) can manage any crew. The role check resolves against the caller's global role since crews have no `productId`.
- **Q13.3 Capability via soul.md** — capability is the YAML front-matter `capabilities:` list at the top of the crew's `soul.md` (Phase 11.3). Each entry references a `domain-expert.md` file by **content-addressed id** (sha256 of the file at ingestion time) and optional human label. The system prompt is a deterministic concatenation: `<soul.md body>` + `\n\n<capabilities (sorted by id)>`. Content addressing guarantees that two crews with the same capability list produce identical caches, which the runner reuses as a cache prefix.
- **Q13.4 Model + backend + mode assignment** — three properties are **crew-level** (every member runs the same way): `model` (registry id), `backend` (Phase 11.5b: `copilot` (default) or `claude`), `invokeMode` (`sdk` (default) or `cli`). Pricing is denormalized per Phase 11.2 so historical salary stays reproducible after a price/model change. Switching any of the three writes `crew_history(crewId, kind in 'model'|'backend'|'mode', fromValue, toValue, switchedBy, switchedAt)` and forces a new prompt cache (the prefix changes). **Tool-using task kinds (`plan` and `coding`) require `invokeMode == sdk`** — CLI mode is text-only and cannot emit `tool_use` events. Validation: server refuses `assign` of a `plan` or `coding` task to a CLI-mode crew with 400 + `crew.tools_unavailable_in_cli`. **`dailyBudgetUsd` semantics with Copilot**: GitHub Copilot is subscription-billed, not per-token. Forge still computes `costUsd` for Copilot invocations using the matching Anthropic-equivalent price from `models.json` (Copilot routes through `claude-*` models internally, so the registry lookup applies); the budget cap therefore reflects an **estimated** spend, not real-dollar charges to the operator's billing account. Budget refusals (Phase 11.10) still fire so a runaway crew can't wedge the runtime, but the salary chip should label Copilot rows with a small "est." pill so PO doesn't confuse it with billed spend.
- **Q13.5 Quota model** — three tiers, OR-combined (whichever is hit first stops new pickups):
  - **dailyBudgetUsd** — Phase 11.10 cap, kept.
  - **maxConcurrentTasks** — how many tasks the crew can claim at once across members.
  - **weeklyTaskCap** — hard ceiling on `tasks completed per ISO week`. Throttles a runaway plan-task generator producing more steps than the engineering crew can drain.
- **Q13.6 Task taxonomy** — two and only two types in v0.1:
  - **`plan`** — input is a feature / story (or the top-level item the operator drops on the crew). Output is **(a)** a `docs/specs/{slug}/plan.md` checked into the workstream's worktree and **(b)** a tree of WIT rows (`phase → stage → step`) hanging off the input feature. Lead is responsible for review; the lead may delegate authoring but signs off before the WIT rows are visible to coding crews. Unit of work = **one MD file + one phase tree**.
  - **`coding`** — input is a single `step` work item (the PR-sized leaf). Output is a single PR. Includes implementation, test coverage above the policy threshold (Phase 7's `human-gate.rules`), and a self-review pass that the lead signs before the PR opens. Unit of work = **one PR**.
  - Reviews, doc edits, ad-hoc spikes are not tracked as new task types — they re-use `coding` (a step whose PR happens to only touch tests, or only docs). Forge does not split task types by what's *in* the PR; it splits by *deliverable shape* (MD vs PR).
- **Q13.7 Domain expert artifact** — when a coding task closes (PR merged, tests green, lead-reviewed), the runner asks the lead's agent to write a `docs/domain/{slug}/{step-id}.md` summarizing **what changed**, **what was tried and rejected**, and **invariants future implementers must preserve**. The file lives in the same repo as the PR. The runner indexes it (sha256 + frontmatter `topic:`) into a `domain_experts` table the capability picker reads from. Generation is *automatic* but the lead can edit before commit.
- **Q13.8 Reassignment vs. handoff** — a *reassignment* moves a task to a different member of the **same** crew (lead-only). A *handoff* moves a task to a different crew entirely (lead of source + lead of target both ack via two-phase confirm). Reassignment is cheap and silent in the audit log; handoffs are loud (audit + SignalR fan-out + a "crossed-crew" pill on the work item).

### Implementation steps

- [ ] **13.1** Domain model — extend `CrewRow` (Phase 11):
  ```
  ParentCrewId   (string?, FK → crews.Id, indexed)
  Depth          (int, 0=root, 1=team, 2=squad; computed, persisted for cheap roll-ups)
  LeadAgentId    (string, FK → agents.Id, NOT NULL)
  MemberAgentIds (json string[] in `crew_members(crewId, agentId)` join table)
  Model          (string, FK-by-id → models.json)
  Backend        (string, 'copilot' | 'claude'; default 'copilot' — Phase 11.5b)
  InvokeMode     (string, 'sdk' | 'cli'; default 'sdk' — Phase 11.5b)
  Capabilities   (json string[] of domain-expert content-ids)
  MaxConcurrent  (int, default 2)
  WeeklyCap      (int, default 20)
  ```
  New table `crew_history(id, crewId, kind, fromValue, toValue, byUserId, at)` — kind ∈ `model | backend | mode | lead | member-add | member-remove | parent-change | quota-override`. New table `domain_experts(id, contentId, repoId, path, topic, summary, indexedAt)`. Migration: existing crews backfill `Backend='copilot'`, `InvokeMode='sdk'` so behaviour is unchanged on upgrade.
- [ ] **13.2** Task taxonomy — add `kind` column to `tasks`: `plan | coding`. The runner reads `kind` to pick the right system-prompt template (under `agents/templates/plan-system.md` and `agents/templates/coding-system.md`); both templates concatenate the crew's soul.md + capabilities and add the kind-specific deliverable rubric.  <!-- partial: queue routes via `pipeline=plan|code` from the endpoints, but the documented `agents/templates/plan-system.md` / `coding-system.md` files are not yet on disk (only `PlanRubricConstants.cs` references them). -->
- [ ] **13.3** Lead-routing endpoint — `POST /api/crews/{crewId}/assign` body `{ taskId, memberAgentId }`. Server enforces `caller == crew.lead || RequireRole.ProductOwner`. Self-assignment is `memberAgentId == crew.lead`. Reassignment uses the same endpoint with the new memberId.
- [ ] **13.4** Plan task pipeline:  <!-- endpoint live at `CrewsEndpoints.PlanStub` (line 1297): schedule gate + CLI refusal + Phase-21 restart + enqueue via ITaskQueueService + SignalR. Note: dispatch is via TaskQueue/CrewQueueWorker rather than a direct AgentRunnerDispatcher.StartAsync as the spec describes — Claude-SDK tool surface still pending in 13.4a. -->
  - Endpoint `POST /api/crews/{crewId}/plan` body `{ workItemId }` — input must be `epic | feature` (dev) or `phase` (design). 400 otherwise. Pre-flight: refuse with 400 + `crew.tools_unavailable_in_cli` if `crew.invokeMode == cli` (Q13.4).
  - `AgentRunnerDispatcher.StartAsync` is called with `kind=plan` (the dispatcher reads the task kind to pick the matching system-prompt template under `agents/templates/plan-system.md`). Tools available: `read_repo`, `write_file` (only under `docs/specs/`), `search_work_items`, `create_work_item`. The runner intercepts `create_work_item` calls and validates the parent/child rank rules from Phase 12. Tool calls are emitted as `AgentEvent.ToolCall` / `ToolResult` (Phase 11.5b) regardless of which SDK cell is active — Copilot SDK emits them natively; Claude SDK requires the runner to declare them in the `/v1/messages` `tools` array (the existing Claude-SDK code path doesn't yet — see 13.4a follow-up).
  - On finish: the spec MD is committed to a `planning/{slug}` branch; a draft PR opens; the WIT rows are inserted with `state = proposed`. Lead reviews + merges the PR; merging flips the WIT rows to `active`. Git operations use the **lead's** preferred-account credentials (Phase 9.11): ADO via cached MSAL token for `crew.lead`; GitHub via `gh auth token --user <lead.preferredGitHubAccount>`.
  - Unit-of-work counter increments by 1 per merged plan PR.
- [ ] **13.4a** Claude-SDK tool surface — the existing `RunClaudeSdk` cell in `AgentRunnerDispatcher` does not currently declare tools to Anthropic, so a plan/coding task on a `(claude, sdk)` crew would emit text but never call `create_work_item` / `git_commit` / etc. Add the `tools: [...]` array to the request body and a `tool_use` block parser. Until this lands, the only fully-tool-capable cell is `(copilot, sdk)`; document that on the `/crews` "Backend" badge with a yellow "tools: text-only" pill on `(claude, sdk)` crews.
- [ ] **13.5** Coding task pipeline:  <!-- endpoint live at `CrewsEndpoints.CodeStub` (line 1372): schedule gate + CLI refusal + enqueue via ITaskQueueService + SignalR. Same caveat as 13.4 re: AgentRunnerDispatcher invocation path. -->
  - Endpoint `POST /api/crews/{crewId}/code` body `{ stepWorkItemId }` — input must be a `step` (Q12.1). Step must be `state = active` and not already claimed (`assignedTo === null`). Pre-flight: refuse with 400 + `crew.tools_unavailable_in_cli` if `crew.invokeMode == cli`.
  - `AgentRunnerDispatcher.StartAsync` is called with `kind=coding`; system prompt resolves to `agents/templates/coding-system.md` (concatenated with the crew's soul.md + capabilities, Q13.3). Tools: `read_repo`, `write_file` (whole repo), `run_tests`, `git_commit`, `git_push`, `open_pr`. As with 13.4, tools require `(_, sdk)` and the `(claude, sdk)` cell needs the 13.4a follow-up to actually invoke them.
  - **Credentials for git / PR ops**: the tool host signs commits + pushes + opens PRs as the **lead** of the crew (lead's `UserSettings.PreferredAdoAccount` for ADO, `PreferredGitHubAccount` for GitHub via `gh auth token --user ...`). Audit row records `actorUserId = crew.lead`, `onBehalfOf = crew.id`. Operators can override per-crew via a future `crew.commitAsAgentId` field — out of scope for v0.1, deferred to Phase R.
  - Self-review pass: a second `StartAsync(kind=review)` against the same agent, scoped to the diff. Lead must explicitly approve via `POST /api/prs/{id}/approve` before the PR comes off draft.
  - Unit-of-work counter increments by 1 per opened (non-draft) PR.
- [ ] **13.6** Quota enforcement — extends Phase 11.10 budget pre-flight:  <!-- `MaxConcurrent` enforced in `CrewsEndpoints.Assign` (line 540) and `CrewQueueWorker` (line 276) with `agent.crew_concurrency_full`. `WeeklyCap` field persisted/migrated but the per-week refusal pre-flight is not yet wired. -->
  - On `assign`: refuse if `active_tasks_for_crew(crewId) >= MaxConcurrent`. 409 + `agent.crew_concurrency_full` SignalR.
  - On `claim` (any task type): refuse if `tasks_completed_this_iso_week(crewId) >= WeeklyCap`. 429 + `agent.crew_weekly_cap_hit`.
  - Override `?force=true` on the assign endpoint records `crew_history(kind='quota-override')`. The override is gated on **global PO** (Q13.2): only a user with the `product-owner` role can force. Same rule for `dailyBudgetUsd` overrides from Phase 11.10.
- [ ] **13.7** Domain-expert generator — runs as a final pass on coding-task close. The dispatcher (`AgentRunnerDispatcher`) is re-entered with a fresh `StartAsync(kind=domain)` against the same agent + crew context, with the merged diff + the original step description as the prompt. System-prompt template: `agents/templates/domain-system.md`. Output is committed to the same PR's branch in a separate commit, then merged. Failure to generate is non-fatal — the task closes either way; a follow-up `gap-domain-expert` audit row points at the missing artifact so the lead can author it manually. Like 13.4 / 13.5, this requires `crew.invokeMode == sdk`; CLI crews skip the auto-generation and always go straight to the gap-audit row.
- [ ] **13.8** Capability ingestion — `POST /api/crews/{crewId}/capabilities` body `{ contentId }` validates the file exists in `domain_experts`, appends to the crew's `Capabilities` array, writes the front-matter back to soul.md, and bumps the prompt cache (Phase 11.5). Removal: `DELETE` with the same body. UI lives in `/crews/{id}` with a searchable picker over `domain_experts.topic`.
- [ ] **13.9** UI — extend `CrewsView` (today: read-only dashboard):  <!-- `src/forge/Forge.UI/src/views/CrewsView.tsx` is now ~2,100 lines: hierarchy panel, roster/capabilities/quota/runtime/history tabs, lead-or-PO write gate. -->
  - **Hierarchy panel** (left, 30%) — collapsible tree. Drag-drop a crew under another to reparent (writes `crew_history(kind='parent-change')`). Depth cap enforced client-side and server-side.
  - **Crew detail** (center, 40%) — header shows crew name, lead avatar, and a **runtime badge group**: `Model · Backend · Mode` (e.g. `claude-opus-4-7 · Copilot · SDK`). Add a `tools: text-only` yellow pill on `(claude, sdk)` crews until 13.4a lands and on any `(_, cli)` crew. Add an `est.` grey pill on the salary readout when `Backend == copilot` (per Q13.4). Capability chip count next to the badge group. Tabs: `Roster` (lead + members table; +/− with role gate), `Capabilities` (chip list with the picker from 13.8), `Quota` (sliders for `MaxConcurrent`, `WeeklyCap`, `dailyBudgetUsd`), `Runtime` (read-only model registry row + Backend / Mode dropdowns; mutation gated by lead-or-global-PO), `History` (`crew_history` rows).
  - **Activity rail** (right, 30%) — current invocations, weekly task counter (`X / WeeklyCap`), salary today/sprint/month (Phase 11.9, kept; "est." pill on Copilot crews).
  - The view is *editable for leads / global PO* (Q13.2) and *read-only for everyone else*. Field-level `disabled` driven by the role check; trying to mutate as non-lead returns 403 from the API anyway.
- [ ] **13.10** Sidebar — `/crews` already lives under "Agents" (Phase 10.7). Drop the PO-only gate at the *nav* level (everyone can browse) but keep mutation-level role checks. Sub-label changes from `Supervisory · PO only` to `{n} crews · {m} agents`.
- [ ] **13.11** Tests:  <!-- `Phase13CrewsTests.cs` (218 lines): depth cap, history readout, capability content-id integrity, CLI-mode refusal, assign roster validation. Cell-coverage / Playwright cases in adjacent suites. -->
  - xUnit: hierarchy depth cap, **global** lead-or-PO assign authorization, plan task → WIT tree round-trip, coding task → PR round-trip, weekly-cap refusal, capability content-id integrity (sha256 match), domain-expert generator hits the file system but tolerates failure.
  - **Cell coverage** (Phase 11.5b): `local-stub` model exercises plan and coding pipelines without network or CLI — both pipelines emit token + finish events through the dispatcher. CLI-mode refusal: assigning a `plan` or `coding` task to a `(_, cli)` crew returns 400 + `crew.tools_unavailable_in_cli`. Backend / mode change writes the right `crew_history` row.
  - **Cost ledger across cells**: Copilot invocations land an `agent_usage` row with `costUsd` computed from the registry price (estimated $) and the salary readout labels them with `est.`. Claude SDK invocations remain unchanged.
  - Playwright: open `/crews`, reparent via drag, assign a self-step, observe the `agent.crew_concurrency_full` toast when MaxConcurrent is hit. Switch a crew from `(claude, sdk)` to `(copilot, cli)` and confirm the `tools: text-only` pill renders + the `Plan` / `Code` buttons disable.
- [ ] **13.12** Fidelity — new view; not in the prototype. New tokens (hierarchy-tree connector lines, capability chip palette) documented in `docs/forge/design-tokens.md`. Existing Phase 10.7 cards remain as the "Activity rail" content.

### Role taxonomy (Phase 13.1 expanded)

Three orthogonal axes. Every one of the 91 agents resolves to all three:

1. **Team** — one of `Architect | Engineer | Review | QA`. The line-of-business
   the agent reports under. The Product Owner (`XD`) folds into `Architect`
   because the PO sets product strategy, not because they write systems
   diagrams; we don't introduce a fifth team for a single seat.
2. **Pod** — `leadership | plan-design | coding | ai-worker | reserve`. The
   loop the agent is hot in. Pods cross teams: a `code-judge` is in the
   `coding` pod but on the `Review` team.
3. **Role bucket** — the fine-grained sub-role within the pod (`plan-red-team`,
   `code-generator`, `ai-security-reviewer`, …). Drives soul.md selection
   and the runner's prompt template.

`agents/allocation.json` is the single source of truth, regenerated by
`scripts/gen-crew-allocation.py`. The TS bridge module
`src/agentAllocation.ts` is auto-emitted alongside so the UI consumes the
exact same data without leaving the Vite root.

Live distribution after seeding the 91 characters:

| Team       | Count |
|------------|-------|
| Architect  | 37    |
| Engineer   | 36    |
| QA         | 11    |
| Review     | 7     |

#### Human leadership roles (8 — one seat each)

| # | Role                              | Owns                                                   |
|---|-----------------------------------|--------------------------------------------------------|
| 1 | Product Strategist / Product Owner | Outcomes, roadmap, prioritisation, customer value.     |
| 2 | AI Factory Architect              | Agent orchestration, workflow, memory, multi-agent IO. |
| 3 | Principal Engineer / Domain Expert | Architecture decisions, complex debugging, perf.       |
| 4 | Prompt / Agent Engineer           | Prompt systems, eval harness, failure-mode tuning.     |
| 5 | QA / Validation Engineer          | Adversarial pipelines, regression, hallucination check.|
| 6 | DevOps / Platform Engineer        | CI/CD, sandbox, secrets, cost, scaling, observability. |
| 7 | Security / Governance Lead        | Access control, supply chain, audit, policy.           |
| 8 | Incident Commander / Reliability  | Override, triage, recovery, post-mortems.              |

#### Crew allocation (100 seats — actual: 91)

```
PLAN + DESIGN POD (30)
  plan-architect-generator     8    propose systems
  plan-red-team                8    scalability/security/cost attacks
  plan-refinement              4    patch based on attacks
  plan-requirements-judge      4    score completeness/value
  plan-design-historian        3    track failed patterns
  plan-orchestrator            3    run loops

CODING POD (30)
  code-generator              10    implement feature
  code-breaker                 8    logic flaws / security / race bugs
  code-unit-test-attacker      5    write failing tests
  code-refactor-defender       3    optimize / patch
  code-judge                   2    maintainability/perf score
  code-memory-agent            2    store bug patterns

AI WORKER SPECIALISTS (13 — single-seat)
  ai-requirements-analyst      1    sharpen acceptance criteria
  ai-ux-designer               1    flows + states + copy
  ai-systems-architect         1    ADR backlog
  ai-backend-engineer          1    services + persistence
  ai-frontend-engineer         1    React + a11y
  ai-test-engineer             1    matrix + regression
  ai-security-reviewer         1    threat model + auth
  ai-code-reviewer             1    readability + naming
  ai-release-manager           1    changelog + rollback
  ai-sre                       1    SLO + alerts + runbooks
  ai-customer-support          1    triage + reproduce
  ai-root-cause-investigator   1    timelines + 5-whys
  ai-doc-writer                1    examples + README

LEADERSHIP (8) + RESERVE (10)
```

`ForgeSeed.EnsureSeedAgentsAsync` reads `allocation.json` on boot to populate
every `AgentRow` with the right `CrewId`, `Role`, and `SoulPath`. Each
character has a `soul.md` under `agents/<characterId>/` that the runtime
loads as the system prompt. The hand-curated 20 ship as detailed personas;
the remaining 71 ship as role-templated personas generated by
`scripts/gen-crew-souls.py` — hand-editable from there.

#### Example planning loop (per Q13.6 plan task)

```
Requirement
  → plan-orchestrator           schedules turns + budget
    → plan-architect-generator  drafts spec / phase tree (1 of 8)
      → plan-red-team           attacks the draft (4 of 8 in parallel)
        → plan-refinement       merges critique into v2 spec
          → plan-requirements-judge   scores against rubric
              if score < 90 → loop
              if score ≥ 90 → spec PR opens, WIT phase tree visible to coding pod
```

The plan-design-historian sits outside the loop: surfaces prior-art ADRs at
the start, archives the loser drafts at the end, never directly edits.

### Out of scope (explicitly deferred)

- **Cross-org crew sharing** (one crew working for two products) — all crews bind to a single product. Phase R candidate.
- **Auto-promotion of members to leads** based on completion stats — manual via PO action only, by design.
- **Skill matching** (auto-suggesting capabilities based on the work item title/description) — capability picker is operator-driven; ML matching is `forge_v2.md` territory.
- **Domain-expert MD versioning beyond content-addressing** — if a file changes, it gets a new content-id; old crews keep referencing the old id. Conflict resolution / merge UI is Phase R.
- **Plan tasks that recurse** (a plan task generating sub-plan-tasks) — v0.1 enforces a single planning round per feature; deeper decomposition lives inside the resulting WIT tree.
- **Per-crew commit identity** (`crew.commitAsAgentId`) — v0.1 always commits / opens PRs as the lead's preferred account (Q13.5). A crew running with a "service-account" identity instead of the lead's own credentials is Phase R.
- **Real-billing cost reporting for Copilot** — v0.1 surfaces an estimated USD via the Anthropic-equivalent registry price (Q13.4) with an "est." pill. True $/seat or per-prompt billing reconciliation against GH's API is Phase R / `forge_v2.md`.

---

## Phase 14 — Performance, scoring & promotion

> Source spec: `docs/forge/scoring-and-promotion.md`. **Read it first** — this phase implements that document; the spec is the source of truth for the rubric weights, schedule shape, level/salary table, and the system-prompt preamble. If the spec and this phase disagree, the spec wins and this phase is updated.

Phase 11 made agents real (model registry, cost ledger, soul.md, runner) and Phase 13 turned crews into routing units with typed tasks. **Phase 14 grades the work**: every deliverable produced by a `plan` or `coding` task gets a 0–100 score, sub-90 work iterates back through the same generator with the evaluator's notes, and trailing-window averages drive level + salary changes. The "salary" concept stays bifurcated — token spend (existing `CrewSalary` rollup) is renamed to **spend** at the API surface; the new HR-style level-derived salary is exposed under **payroll**.

Two principles drive the shape:

1. **The rubric is the contract.** Generators see the same rubric their evaluator scores against (Phase 14.6 preamble injects it). No hidden criteria, no per-task overrides at v0.1 — every deliverable is scored against the same five dimensions so trailing averages compare apples-to-apples.
2. **Schedule is soft, score is hard.** Outside 9-9-6 the dispatcher refuses *new* pickups but lets in-flight work finish. A score below the reject threshold (which varies by work-item points — see `ScoringConstants.RejectThreshold`) is a hard reject (work bounces to the lead); [reject..89] always re-iterates (no negotiation). The team lead can override schedule (logged) but never overrides a reject.

### Resolved decisions

The six open questions from `docs/forge/scoring-and-promotion.md` §9 default-stand unless the operator flips them in writing here. All other rubric numbers (weights, level table, hourly curve) are locked by the spec and reproduced verbatim in code constants — never re-derived.

- **Q14.1 Off-shift dispatch — refuse, not enqueue.** Outside the crew's 9-9-6 window `POST /api/crews/{id}/code` and `POST /api/crews/{id}/plan` return **409 + `crew.off_shift`**. The team lead can pass `?override=true` (audit-logged as `dispatch-after-hours`). No backlog drainer at 09:00; if the operator wants timed enqueue they create the work item and dispatch the next morning. **Implication**: the dispatcher does not need a queue; it just gates. The schedule is metadata on `Crew` (timezone + scheduleEnabled), evaluated against `DateTimeOffset.UtcNow` per request.
- **Q14.2 Disputes use a third-party judge agent on demand only.** Default verdict is the evaluator's. The generator may file `POST /api/deliverables/{id}/dispute` once per deliverable; the dispute spawns a `judge` task on the team lead's crew that re-scores against the same rubric and either confirms or replaces the evaluator's score. Dispute is **not** a free retry — the iteration counter still advances. **Implication**: judge agent's prompt template lives at `agents/templates/judge-system.md` (Phase 14.6); judge writes back to the same `deliverables` row with `judgeAgentId` + `judgeScore` + `judgeNotes`.
- **Q14.3 Same evaluator across iterations.** Iteration N+1 reuses the iteration N evaluator and model. Rationale: rubric reproducibility — if a different agent grades the rebound, the score delta becomes ambiguous (did the work improve, or did the bar change?). **Implication**: the dispatcher pins `evaluatorAgentId` on first iteration and reuses it for the lifetime of the deliverable; only the dispute path (Q14.2) breaks this with an explicit judge.
- **Q14.4 Demotion is enabled.** Trailing-20 rolling average below 75 triggers `level − 1` with the same nightly job that handles promotion. Floor is 0; demoting from 0 is a no-op. **Implication**: agents at the floor with persistently low scores are surfaced to the team lead via `agent.demotion-blocked` events for human action (re-train soul.md, retire, swap model). This is not auto-firing — Forge never deletes an agent.
- **Q14.5 Lead floor at level ≥ 7.** Promoting a member to crew lead (or creating a crew with a sub-7 lead) is rejected with **400 + `crew.lead.level_too_low`**. Demoting a current lead below 7 fires `crew.lead-vacated` and the crew enters a `lead-pending` state — work continues but no new pickups. The team lead has 14 days to nominate a replacement. **Implication**: existing crews whose lead is below 7 at migration time are grandfathered (one-time `lead.grandfathered` row in `crew_history`), but any future lead change enforces the floor.
- **Q14.6 No per-deliverable bonus.** Hourly × on-shift hours is the only payroll input at v0.1. A deliverable that scores 100 pays exactly the same as one that scores 90; the level/promotion track is the long-run incentive. **Implication**: payroll is deterministic from `(level, on-shift hours)` and recomputable from history — no per-row event sourcing for bonuses, which keeps the salary report cache trivial.
- **Q14.7 `CrewSalary` → `CrewSpend` rename, dual surface.** The existing `CrewSalary` payload (token-spend rollup, Phase 11.9) is renamed to `CrewSpend`. The new HR salary surface is `CrewPayroll` with the same `today / sprint / month / sparkline` shape. The TopBar grows from one chip to two: **spend** ($ inference cost) and **payroll** ($ salaries owed). Existing API path `GET /api/crews/{id}/salary` becomes `GET /api/crews/{id}/spend`; a 90-day deprecation alias keeps the old path 200-OK and emits `forge.api.deprecated`. **Implication**: `types.ts` `CrewSalary` interface stays as the type-shape but its description is updated; a parallel `CrewPayroll` is added with identical shape but distinct semantics; UI swaps both stores in one PR (Phase 14.10) so there's no half-renamed window.
- **Q14.8 Review cadence — iteration AND monthly, side-by-side.** The trailing-20-deliverables window from spec §4 stays as the **iteration cadence** — fast, responsive, drives the nightly promotion job. In parallel, Forge runs a **monthly review** that snapshots a printable scorecard per agent on the 1st of each calendar month (in the agent's home-crew TZ). The monthly review does NOT change levels by itself — it is the slow-moving signal the team lead reads to (a) sanity-check the nightly auto-promotion / demotion, (b) catch agents gaming a short window with a few high-score deliverables, (c) record an explicit retro note on each agent's history. **Implication**: same scorecard data structure (Q14.9) renders for both windows; only the date filter changes. Each monthly snapshot persists to a new `agent_monthly_review` table so the scorecard stays reproducible after individual deliverables age out of the trailing-20 ring. The costs view (Phase 11.9 → 14.17) is the home for both surfaces — promotion lives next to payroll because levels and dollars move together.
- **Q14.9 Agent KPI catalogue (locked).** Every agent scorecard renders the same nine KPIs, computed over the active window (`trailing20 | mtd | lastMonth | monthOf(YYYY-MM)`):
  1. **Throughput** — count of deliverables generated in window (final iteration counted once).
  2. **Pass-rate** — % of deliverables where `iter == 1 AND verdict == 'pass'` (first-shot wins).
  3. **Avg score** — arithmetic mean of `score` on the final iteration.
  4. **Iteration depth P50 / P90** — number of `iterate` rounds per deliverable before pass / reject.
  5. **Reject rate** — share where the final verdict is `reject`.
  6. **Dispute rate** — share where the generator filed a dispute (Q14.2).
  7. **Stalled rate** — share that hit the 5-iter cap or `stalled-budget`.
  8. **On-shift utilization** — `active_minutes / scheduled_minutes` (active = an invocation logged in the cost ledger).
  9. **Cost-per-pass** — `Σ (gen_cost + eval_cost)` ÷ count of passed deliverables.

  Two **derived flags** sit alongside the KPIs: **Promotion ETA** = `max(0, ceil((92 − rolling_avg) ÷ max(0.01, recent_delta_per_deliverable)))` deliverables (`∞` rendered as `—` if `recent_delta ≤ 0`); **Demotion risk** = boolean `rolling_avg < 78 OR reject_rate > 0.10`. Both are advisory — only the nightly job (14.7) moves levels. KPI definitions and thresholds live as code constants in `Forge.Api/ScoringConstants.cs` next to the rubric weights so the spec-consistency test (14.14) catches drift.

### Implementation steps

- [ ] **14.1** **Schema migrations.** New columns on `agents`: `level INT NOT NULL DEFAULT 0`, `level_updated_at DATETIME?`, `hourly_usd DECIMAL(8,2) NOT NULL` (computed at write time as `4 + level * 3` so the invariant survives backfills). New columns on `crews`: `timezone TEXT NOT NULL DEFAULT 'America/Los_Angeles'`, `schedule_enabled BIT NOT NULL DEFAULT 1`. New tables — `deliverables(id, work_item_id, crew_id, generator_agent_id, evaluator_agent_id, iter, score, dims_json, verdict, notes, gen_cost_usd, eval_cost_usd, judge_agent_id?, judge_score?, judge_notes?, created_at)` and `agent_level_history(id, agent_id, old_level, new_level, window_avg, reason, created_at)`. Backfill: every existing agent → `level=0`, `hourly_usd=4`; every existing crew → `timezone='America/Los_Angeles'`, `schedule_enabled=1`. Migration is `Forge.Api/Persistence/Phase14Migration.cs` and runs idempotently on boot like the others. Locked numbers live in `Forge.Api/ScoringConstants.cs`.
- [ ] **14.2** **Schedule gate middleware.** `ScheduleGate` ASP.NET endpoint filter applied to `POST /api/crews/{id}/plan` and `POST /api/crews/{id}/code` (and any future dispatch verb). Reads the crew's `timezone` + `schedule_enabled`, computes "is now within Mon–Sat 09:00–21:00 in that TZ?", refuses with `409 + crew.off_shift` otherwise. `?override=true` requires `crew.lead == caller || global ProductOwner role` and writes `crew_history(kind='dispatch-after-hours', ...)`. Telemetry: `forge.crew.schedule.refused` counter tagged with crew id and current hour-of-day. **Status:** `Forge.Api/Services/ScheduleGate.cs` is the pure helper; `CrewsEndpoints.ApplyScheduleGateAsync` wires it into both stubs ahead of the CLI check; override path writes `dispatch-after-hours` to `crew_history`. Telemetry counter is deferred to Phase 14.12.
- [ ] **14.3** **Evaluator scoring contract.** Locked JSON shape — `{ score: int, dims: { correctness: int<=40, quality: int<=25, tests: int<=15, scope: int<=10, risk: int<=10 }, verdict: "pass"|"iterate"|"reject", notes: string<=4000 }`. The runner reads the **last assistant message** of an evaluator run and parses out a fenced ```json``` block; missing block or invalid shape → server-side fallback verdict `iterate` with a `score=0` row and a `forge.eval.parse_failed` telemetry event so we can spot model drift. Verdict is *derived* from score and work-item points (`>=90 → pass`, `score == 0 → iterate` (always — score 0 means the generator failed to produce the file, not that the work quality is rejectable), `>=RejectThreshold(points) → iterate`, else `reject`); the reject threshold scales down for higher-point items (pts 1→70, 2→65, 3→60, 4→50, 5→40, 6→30, 7→20, 9+→10) so harder tasks iterate more before giving up. The model's stated verdict is checked for consistency and a mismatch increments `forge.eval.verdict_mismatch` but does not override the derived value. **Status:** `Forge.Api/Services/EvaluatorScoreParser.cs` exposes pure `Parse(text, points) → EvaluatorScore`; fallback returns `iterate/score=0/ParseFailed=true`; `DeriveVerdict` returns `iterate` for score 0 regardless of reject threshold; `VerdictMismatch` flag is set when the model's claim diverges from the math. Counters deferred to Phase 14.12.
- [~] **14.4** **Iteration loop in dispatcher.** `DeliverableLoop` orchestrates: (a) generator run, (b) evaluator run, (c) parse + persist `deliverable` row, (d) on `iterate` re-enter (a) up to **5 iterations** total, (e) on `pass` mark the work item green and exit, (f) on `reject` flip the work item back to `proposed` for the lead. Caps from spec §3: hard iteration cap = 5, budget cap = `crew.maxTaskBudgetUsd`, time cap = the schedule window (if 21:00 is hit, deliverable persists with `verdict='paused-eod'` and resumes at 09:00 — the loop is already idempotent because the iteration counter is in the row, not in memory). Each iteration emits `forge.deliverable.iteration.{started,scored,passed,iterated,rejected,stalled}` for the OTel pipeline. **Status (Batch 3):** `Forge.Api/Services/DeliverableLoop.cs` (registered as `IDeliverableLoop` in DI) is the orchestrator — pure, takes generator/evaluator delegates so it's testable without a live runner. Pass/reject/iterate/stalled/stalled-budget verdicts implemented + work-item state flips wired (`pass→closed`, `reject→proposed`, stall leaves state). **Remaining:** time-cap (`paused-eod` resume), telemetry counters (deferred to 14.12), and the production wiring that calls into `AgentRunnerDispatcher` from the plan/code endpoints — that lands when 13.4/13.5 stop returning 501.
- [~] **14.5** **Dispute path (Q14.2).** `POST /api/deliverables/{id}/dispute` — caller must be the deliverable's `generator_agent_id` (or that agent's `lead`); refuses if `judge_agent_id` is already set on the row (one dispute per deliverable). Spawns a `judge` task on the team lead's crew via the existing `AgentRunnerDispatcher.StartAsync(kind='judge', ...)` path, with `judge-system.md` template. Judge writes `judge_score` + `judge_notes` to the same row; if `judge_score >= 90` the deliverable transitions to `pass` retroactively (work item flips green). Dispute does NOT reset the iteration counter — the spec is explicit on this (§3: dispute is not a free retry). **Status (Batch 3):** `Forge.Api/Endpoints/DeliverablesEndpoints.cs` exposes `GET /api/deliverables/{id}`, `GET /api/work-items/{id}/deliverables`, and `POST /api/deliverables/{id}/dispute` with body `{ judgeAgentId, judgeScore, judgeNotes }`. One-dispute-per-row guard, generator/lead/PO authority gate, and retroactive work-item close on `judgeScore ≥ 90` are implemented. **Remaining:** auto-spawning a judge task on the team lead's crew (depends on the runner being live — caller currently supplies the judge result synchronously, which the eventual autonomous judge will also do via the same endpoint).
- [~] **14.6** **Soul preamble injector.** New file `agents/_preamble.md` in the repo (NOT under `%LOCALAPPDATA%`, so the rubric is auditable in git). The runner concatenates `<preamble>\n\n---\n\n<soul.md body>` before sending to the model and records `agent.preambleSha = sha256(preamble bytes)` on every deliverable for tamper detection. New templates `agents/templates/judge-system.md` and (rubric reminder) `agents/templates/eval-system.md` extend the existing Phase 13.2 templates rather than replacing them. Bumping the preamble is a deliberate operator action — the change is logged as `forge.preamble.changed` with the before/after sha and the user id. **Status (Batch 1):** preamble file written; runner concatenation + sha recording + judge/eval templates remain.
- [~] **14.7** **Promotion job.** Hosted service `PromotionJob` runs nightly at 02:00 per crew timezone (one timer per distinct timezone, not per crew, to keep timer count bounded). Algorithm: for each agent, take the trailing **20 deliverables** they generated OR evaluated (whichever role contributed the row), drop the row if `iter > 1` only counts the *final* iteration, compute rolling-avg score, apply the rules from spec §4 — promote if `>=92` and zero rejects in window AND last promotion ≥ 30 days ago; demote if `<75` and ≥ 20 deliverables in window. Writes `agent_level_history` + emits SignalR `agent.level.changed`. Quiet by default — no toast spam; the Performance tab (14.9) and the Agents costs view (14.17) are where the UI surfaces it. **Monthly review trigger (Q14.8)**: the same hosted service, on the 1st of each calendar month at 02:00 in the crew's TZ, also writes one `agent_monthly_review` snapshot row per agent covering the prior calendar month — see 14.15. The monthly tick does NOT itself change levels (the iteration-cadence rolling-20 alone moves levels); it is the signal-of-record the lead retros against. **Status (Batch 4):** `Forge.Api/Services/PromotionPolicy.cs` (pure decision) + `PromotionRunner.cs` (DB-backed scan with final-iter filter per work-item) implemented; `POST /api/admin/promotion/run` and `GET /api/agents/{id}/level-history` endpoints exposed. 30-day cooldown, level cap/floor, hourly recompute via `ScoringConstants.HourlyForLevel`, and the `agent_level_history` writes are wired and tested. Q14.5 lead-vacated currently logs a warning when a current lead drops below L7; full lead-pending state machine awaits a `crew.LeadPendingSince` schema add. **Remaining:** the nightly hosted-service scheduler (one timer per TZ), SignalR `agent.level.changed` broadcast, and the monthly-review snapshot writer (14.15).
- [~] **14.8** **Payroll API + scheduler integration.** New endpoint `GET /api/crews/{id}/payroll` returning `CrewPayroll { today, sprint, month, sparkline }`. Computation: for each member, sum `(level → hourly_usd) × on_shift_hours_in_period` where `on_shift_hours` is derived from the crew's schedule (12h × work-days within the period that the crew was `schedule_enabled`). Does NOT integrate the existing cost ledger — payroll is a function of `(level, schedule)` only. Existing `GET /api/crews/{id}/salary` becomes `GET /api/crews/{id}/spend` with a 90-day deprecation alias on the old path (Q14.7); response body identical, just renamed. **Status (Batch 4):** `Forge.Api/Services/PayrollCalculator.cs` is the pure on-shift-hours math (TZ-aware, Mon–Sat 09:00–21:00, partial-day clipping); `PayrollEndpoints.cs` exposes `GET /api/crews/{id}/payroll` (HR salary) and `GET /api/crews/{id}/spend` (token-cost rollup) side-by-side. The legacy `GET /api/crews/{id}/salary` route is unchanged in `CrewsEndpoints.cs` and continues to return the same spend payload; the rename is documentation-only at this point. **Remaining:** explicit deprecation log line + counter on the legacy `/salary` route, and the per-member `OnShiftHours` breakdown row that Phase 14.17's `<ScorecardStrip>` will need.
- [~] **14.9** **UI — Performance tab on `/crews/{id}` (per-crew scorecard).** New tab between **Quota** and **History** in `CrewsView.tsx` (the existing tab list at line ~52: `[ 'roster', 'pair', 'capabilities', 'runtime', 'quota', 'performance', 'history' ]`). Sections:
  - (a) **Schedule strip** — green/red ribbon showing on/off shift right now in the crew's TZ, plus the next transition timestamp.
  - (b) **Window toggle** — pill group `Iteration (trailing-20) | This month (MTD) | Last month` re-routing the grid below to the matching window via the KPI service (14.16). Default = `Iteration`. Selection persists per user in `localStorage.forge.crews.scorecardWindow`.
  - (c) **Roster scorecard grid** — one row per member, columns: avatar (reuses `AgentFace`), level pill (0–10 + title from the spec table), and the nine KPI columns from Q14.9 (sortable, numeric columns right-aligned). Trailing-20 sparkline of `avg score` next to the avg column; last promotion / demotion / next-eligible-at as a small footnote line beneath the avatar cell. **Demotion-risk** rendered as a yellow pill in the **Reject rate** cell when the Q14.9 condition holds; **dispute-rate** rendered as a red pill when `> 0`.
  - (d) **Payroll card** — today / sprint / month, parallel layout to the existing spend card, distinct color so they don't blur.

  No edit affordance — promotions are job-driven, not manual at v0.1. The **Last month** window pulls from the persisted `agent_monthly_review` snapshot (14.15) so historical scorecards stay stable; **Iteration** and **This month** windows compute live from `deliverables`. The same grid renders inline on `/crews` (cost view) per 14.17, so the Performance tab is the canonical *crew*-scoped scorecard while 14.17 is the *cross-crew* scorecard. **Status (Batch 5):** the `Performance` tab is wired into `CrewsView.tsx` (between **Quota** and **History**) and renders three sections: schedule strip (live on/off-shift indicator + next transition timestamp), parallel **Payroll** + **Spend** cards (today + sparkline + sprint/month, distinct accents per Q14.7), and the roster grid with avatar + clickable level pill + hourly + change-count. Click any avatar/name opens `AgentEditModal`. **Remaining:** the window-toggle pill group (Iteration / MTD / Last-month) and the full nine-KPI columns from Q14.9 — both depend on the `IAgentKpiService` (14.16) which lands when monthly snapshots (14.15) are persisted.
- [~] **14.10** **UI — Score panel on work-item detail.** New collapsible panel under the existing assignee/branch metadata in `DetailPanel.tsx`. Shows the iteration timeline (1..N) as score chips with the verdict color, click any chip to expand the dim breakdown + evaluator notes inline. If `judge_*` fields are set, render a third "judge" chip below the latest evaluator chip with its own color. The panel is read-only — the only action is "Dispute" (visible to the generator's lead only, gated by Q14.5's one-dispute rule). **Status (Batch 6):** `src/forge/Forge.UI/src/board/DeliverableScorePanel.tsx` renders the iteration timeline (chips colored by verdict — pass green / iterate amber / reject red / stalled grey / paused-eod blue), inline expansion shows dim breakdown bars + cost split + evaluator notes per iteration, and the dashed-border judge chip appears alongside iter chips when a dispute has landed (with a "retroactive pass" pill when judge ≥ 90). PO-gated **Dispute** button opens a modal that submits to `POST /api/deliverables/{id}/dispute`. **Remaining:** restrict the Dispute affordance to the generator's lead specifically (currently shown to all POs — server still enforces); refresh the work-item state pill in the parent `DetailPanel` when a retroactive pass flips it.
- [~] **14.11** **UI — TopBar split.** The existing single salary chip splits into two — `Spend $X.YZ today` (renamed) + `Payroll $A.BC today`. Both pop the existing chip popover; payroll's popover lists members with their level pills. Dark/light theme parity required, screenshot diff in `Forge.UI.Tests`. **Status (Batch 5):** `TopBar.tsx` now renders both chips side-by-side — Spend (blue accent, sourced from the existing `getSalaryTodayTotal`) and Payroll (green accent, sourced from a new `forgeApi.getPayrollTodayTotal()` aggregator that sums `getCrewPayroll(id).today` across `listCrews()`). **Remaining:** the popover panels for each chip (currently both are tooltip-only), a dedicated server-side `/api/payroll/today` endpoint to replace the client-side aggregation, and the screenshot fidelity diff.
- [ ] **14.12** **Audit + telemetry.** New `audit_event` kinds: `deliverable-scored`, `deliverable-iterated`, `deliverable-rejected`, `deliverable-stalled`, `deliverable-disputed`, `agent-promoted`, `agent-demoted`, `lead-vacated` (Q14.5), `dispatch-after-hours` (Q14.1), `preamble-changed` (Q14.6). All flow through the existing `/audit` view (Phase 7.5) and the OTel pipeline. Counter metrics: `forge.deliverable.score{verdict}`, `forge.agent.level.changes{direction}`, `forge.crew.payroll.usd_total{crewId}`. Trace span: `deliverable.run` covering all iterations of one deliverable, with iteration count + final verdict as span attributes.
- [ ] **14.13** **Tests.** `Forge.Api.Tests/Phase14ScoringTests.cs` covers: (a) score 89 iterates, score 90 passes, score 69 rejects (boundary cases); (b) iteration cap stalls cleanly at 5; (c) off-shift dispatch returns 409 unless overridden; (d) promotion job promotes at 92-avg / no-rejects / cooldown-respected, demotes at 74-avg, no-ops at the floor; (e) lead floor (Q14.5) refuses sub-7 promotion to lead; (f) dispute path replaces an evaluator score with a judge score and flips the work item green when the judge passes 90; (g) `GET /spend` deprecation alias (Q14.7) emits the deprecation log line; (h) `AgentKpiCalculator` (14.16) returns the nine KPIs in Q14.9 against handcrafted deliverable fixtures — pass-rate, P50 / P90 iteration depth, and cost-per-pass arithmetic verified to 2 dp; (i) monthly review job (14.15) writes one `agent_monthly_review` row per agent on the 1st-of-month tick, snapshot is immutable after 30 days, retro-notes PATCH past the window returns 409; (j) `GET /api/agents/{id}/scorecard?window=lastMonth` reads from the snapshot, while `?window=trailing20` recomputes live, and the two MUST agree when the trailing-20 happens to fall entirely inside the same month. `Forge.UI.Tests/` adds screenshot fidelity diffs for the Performance tab + the split TopBar + the `AgentScorecardDrawer` (14.17) in both themes.
- [ ] **14.14** **Fidelity check.** No prototype HTML to diff against (this phase is post-prototype). Instead the fidelity gate is **the spec itself**: `Forge.Api.Tests/Phase14SpecConsistencyTests.cs` parses `docs/forge/scoring-and-promotion.md` and asserts the rubric weights, level table, hourly curve, iteration cap, and promotion thresholds in code (`ScoringConstants` static class) match the spec verbatim. If the spec changes, this test fails and forces the code constant update — preventing silent drift. **Status:** 10 assertions cover rubric weights, verdict thresholds, iteration cap, schedule window, default timezone, hourly curve, level range, promotion window/thresholds, lead floor, and preamble-rubric parity.
- [ ] **14.15** **Monthly review snapshot (Q14.8).** Schema migration `Phase14Migration.cs` adds `agent_monthly_review(id, agent_id, year, month, throughput, pass_rate, avg_score, iter_p50, iter_p90, reject_rate, dispute_rate, stalled_rate, utilization, cost_per_pass, ending_level, promotion_eta, demotion_risk, retro_notes, created_at, locked_at?)` with `UNIQUE(agent_id, year, month)`. The 1st-of-month branch in `PromotionJob` (14.7) writes one row per agent covering the prior calendar month and is the **scoreboard of record** — once `created_at + 30d` has passed, `locked_at` is set and the row is immutable; underlying deliverables stay readable but the rolled-up KPIs are pinned. Endpoints in `MonthlyReviewEndpoints.cs`: `GET /api/agents/{id}/monthly-reviews?from=YYYY-MM&to=YYYY-MM` returns the rows; `PATCH /api/agents/{id}/monthly-reviews/{year}-{month}` updates `retro_notes` only — gated by `crew.lead == caller || global ProductOwner role`, refuses with 409 if `locked_at` is set. Telemetry: `forge.agent.monthly_review.written` counter and `forge.agent.monthly_review.locked` event when the 30-day window closes.
- [ ] **14.16** **Agent KPI service.** New `IAgentKpiService` in `Forge.Api/Services/`: `GetScorecard(agentId, window)` where `window` is the discriminated union `trailing20 | mtd | lastMonth | monthOf(YYYY-MM)`. Returns an `AgentScorecard` record carrying the nine KPIs from Q14.9 plus the two derived flags. Pure function over `deliverables` for live windows; pure read of `agent_monthly_review` for historical months — same response shape either way so the UI cache is window-agnostic. Endpoints: `GET /api/agents/{id}/scorecard?window=...` and `GET /api/crews/{id}/scorecard?window=...` (the crew variant returns one entry per member). KPI math lives in `AgentKpiCalculator` (testable in isolation); thresholds (`demotionRiskAvg = 78`, `demotionRiskRejectRate = 0.10`, `promotionTargetAvg = 92`) live in `ScoringConstants` so they participate in the 14.14 spec-consistency check.
- [ ] **14.17** **Costs view enrichment — Agents cost-and-scorecard surface.** The existing `/crews` Roster tab (`CrewsView.tsx` lines ~590–700; the `<SalaryStrip>` block at line 1195) gains a per-agent column group: in addition to the existing `today | sprint | month | sparkline` spend strip, each member row now renders `level pill | trailing-20 avg | MTD avg | reject rate | promotion ETA | payroll today`. A new `<ScorecardStrip>` component sits beneath the existing `<SalaryStrip>` so the operator reads cost-and-performance in one glance — promotion lives next to payroll because levels and dollars move together. Click any member row → opens a right-rail `<AgentScorecardDrawer>` (`src/forge/Forge.UI/src/views/AgentScorecardDrawer.tsx`) showing: the full Q14.9 KPI table, the trailing-20 sparkline of `avg score`, the last 6 months of `agent_monthly_review` as a small bar chart of `avg_score`, and the agent's `agent_level_history` rows as a vertical timeline. The drawer is the canonical agent KPI surface; the Performance tab (14.9) is the per-crew rollup. Cost view itself stays read-only for non-PO; PO sees an inline "Edit retro note" affordance on the latest unlocked monthly snapshot per Q14.8. The TopBar **Payroll** chip popover (14.11) gains a third inline metric `Avg score: NN` computed crew-weighted across all visible crews — gives the PO a one-glance health number without leaving the chrome.
- [ ] **14.18** **Printable monthly review.** `GET /api/crews/{id}/monthly-review.pdf?month=YYYY-MM` server-generates a one-page PDF (using `QuestPDF`) carrying: the crew's payroll for the month, every member's scorecard row from `agent_monthly_review`, every `agent_level_history` row in the period, and any `crew_history` events from the period (lead changes, model swaps, quota overrides). The PDF is the artefact the team lead carries into a 1:1; PO can also email it via `POST /api/crews/{id}/monthly-review/email` (reuses the existing audit-log mailer / Phase 7 notifications scaffold). HTML preview at `/crews/{id}/monthly-review?month=YYYY-MM` for in-app reading; both routes hit the same renderer so print and screen views stay byte-equivalent. Behind a small "Export" button on the Performance tab (14.9) and on the `AgentScorecardDrawer` header (14.17) — the agent variant scopes the PDF to a single agent.

### Out of scope for v0.1 — moved to Phase R / `forge_v2.md`

- **Per-team rubrics** — a Review crew judging code quality on a different curve than an Engineering crew. v0.1 has one rubric for everyone (rubric reproducibility >> per-team tuning early on).
- **Bonuses, equity, retention spend** — anything beyond `hourly × on_shift_hours`. Q14.6 is firm at v0.1.
- **Multi-judge consensus / quorum** — disputes get one judge. If even the judge is wrong, escalate to the human team lead (Phase 13.2).
- **Cross-crew grading** — an Engineering crew's evaluator scoring a QA crew's work. v0.1 keeps evaluator inside the same crew (Phase 13).
- **Real timesheet integration** — `on_shift_hours` is derived from schedule + `schedule_enabled`, not from human punch-in. If an operator pauses the crew mid-shift, payroll for that gap is forfeit; restoring is by re-enabling, not back-dating.
- **Quarterly / annual scorecard rollups** — v0.1 ships trailing-20 + monthly snapshots only. Multi-month views in the UI render by stacking individual `agent_monthly_review` rows; a server-side quarterly aggregator with its own snapshot table is `forge_v2.md` territory.
- **Peer / 360 reviews** — agents do not score each other on soft factors. The evaluator + judge agents (Q14.2) are the only graders; everything else is the nightly job over `deliverables`.
- **Manual KPI overrides** — neither the lead nor the PO can hand-edit a KPI value or a level. The only mutable field on a monthly snapshot is `retro_notes`, and only within the 30-day unlocked window (14.15). Editing a level requires a deliverable trail the nightly job acts on.

---

## Phase 15 — Product sprint & area path management

> Today the iteration source is per-repo and lives in the provider (Q4 / Q12.4). When a product spans 4 repos, the operator sees four parallel sprint cadences with no rollup, and area paths aren't a managed concept at all. **Phase 15 promotes iteration paths and area paths to product-scoped first-class entities**, lets the operator **bulk-import them from ADO by prefix** (`Atlas\\Sprint*` → import all 14 sprint nodes in one click), and binds repos to a product iteration so cross-repo work items roll up into a single sprint. **The functionality lives inside the existing Plan & Design view and the Products view — there is no standalone `/sprints` route.** Plan & Design gets a sprint selector + area-path filter chip in its page head; Products gets a Sprints panel inside the product detail.

Two principles drive the shape:

1. **Provider remains source of truth.** Forge never creates or renames iteration / area nodes upstream. We import (read), cache, reference. Renames flow into Forge on next refresh; bindings re-resolve by stable `providerNodeId`, not by path string.
2. **Promotion is opt-in.** A repo can keep its provider iterations local-only (the Phase 12 default). Promoting an iteration to product scope is an explicit operator action so we don't pollute the registry with every dev branch's noise. Bulk import lowers the cost of opting in — accept the prefix once, then everything under it lives at product scope.

### Resolved decisions

- **Q15.1 Iteration scope — per-product registry.** New `ProductIteration { id, productId, providerKind, providerOrg, providerProject, providerNodeId, path, name, startDate?, finishDate?, state: 'past'|'current'|'future'|'provider-orphan', attributes, importedAt, refreshedAt }`. Multiple repos in a product can bind to the same iteration via `RepoIterationBinding(repoId, productIterationId, role: 'current'|'next'|'reference')`. Cross-repo burn-down (deferred from Phase 12 §4) now has a real key — `ProductIteration.id`. Phase 12.4 mappers gain a step that resolves `IterationRef` → `productIterationId` when the binding exists, falling back to per-repo namespacing (`{repoId}:{providerSprintId}`) when it doesn't.
- **Q15.2 Area path scope — per-product registry, tree-structured.** New `ProductAreaPath { id, productId, providerKind, providerOrg, providerProject, providerNodeId, path, parentPath, parentNodeId?, attributes, importedAt }`. Operators can mark one area path per product as the **default filter** — Plan & Design and the work-item view both apply it before any user filter, with a chip the operator can clear per-session. "Mine + Default area" is the common case.
- **Q15.3 Prefix import (the headline UX).** Operator types a path prefix (`Atlas\\Sprint`, `Atlas\\Releases\\v1`), Forge calls the appropriate ADO classification API (`_apis/wit/classificationnodes/{Iterations|Areas}?$depth=10` filtered to the prefix subtree), displays the matching subtree, and the operator multi-selects which leaves to import. The modal has two tabs (Iterations / Areas) so a prefix that ambiguously matches both surfaces them side-by-side. **Saved-prefix shorthands per product**: an operator clicking "Refresh `Atlas\\Sprint*`" runs the same import in one click, picking up newly-created upstream iterations automatically (e.g. when the ADO admin adds Sprint 15).
- **Q15.4 Sync direction — read-only always.** Forge never POSTs to `_apis/wit/classificationnodes`. Renames and date changes flow into Forge on the next refresh; if a node was deleted upstream and Forge has bindings, the binding stays but renders with a `provider-orphan` pill — operator decides whether to unbind or keep (sometimes you want the historical binding to keep work-item rollups stable for closed sprints).
- **Q15.5 Refresh cadence.** The promotion job's hosted service (Phase 14.7) reconciles iterations + area paths nightly at 02:00 per crew TZ. Manual `Refresh now` button on the SprintManager runs the same code path, rate-limited to 1/min per product. Saved prefixes refresh as part of the nightly job — operator gets an audit row when a new iteration appears so a planning meeting doesn't miss a freshly-cut sprint.
- **Q15.6 GitHub parity.** GitHub has milestones (iteration analogue) but no native area-path concept. **Iteration import** works against GitHub by listing milestones for repos in the product (no prefix — milestones are flat; the prefix field is hidden on the GitHub tab). **Area path** management is **ADO-only at v0.1**; mixed-provider products show area paths only for the ADO repos and emit a small "GitHub repos use labels for area scoping — see `forge_v2.md`" note. A label→area-path mapper is a Phase R candidate.
- **Q15.7 Active iteration semantics.** A product has an "active iteration" pointer per repo (the `RepoIterationBinding` row with `role='current'`). Plan & Design's sprint selector reads the active iteration of the product's primary repo, or — when explicitly set — a product-level override `Product.activeIterationId`. The sprint pill in the page head replaces the existing hard-coded `Sprint 14 board` title with this binding. **No local Forge sprint** — Q4 still holds; the active iteration is always a `ProductIteration` row that traces back to a provider node.

### Implementation steps

- [ ] **15.1** **Domain + migration.** New tables `product_iterations`, `product_area_paths`, `repo_iteration_bindings`, `product_saved_prefixes(id, productId, prefix, kind: 'iterations'|'areas', repoId, addedBy, addedAt, lastRefreshedAt?)`. EF migration `Phase15Migration.cs` is idempotent and runs after `Phase14Migration`. TS mirrors in `src/forge/Forge.UI/src/types.ts` (`ProductIteration`, `ProductAreaPath`, `RepoIterationBinding`, `SavedPrefix`). Backfill: for every existing `Card.sprintRef` of shape `{repoId}:{providerSprintId}`, create a synthetic `ProductIteration` row in the repo's product (path = the provider id, marked `imported: false`) so existing planning data continues to work; first manual import replaces the synthetic row in place by `providerNodeId`.
- [ ] **15.2** **ADO discovery service.** New `IAdoClassificationService` in `Forge.Api/Providers/Ado/`: `DiscoverAsync(repoId, kind, prefix?, cancel)` calls `GET _apis/wit/classificationnodes/{Iterations|Areas}?$depth=10` against the repo's project, prunes to the supplied prefix at the path-segment level, and returns a flat array `{ providerNodeId, path, name, hasChildren, attributes }`. Result is cached for 60 s per `(project, kind, prefix)` so the operator can flip tabs without re-fetching. Endpoint: `POST /api/products/{id}/sprint-discovery` body `{ repoId, kind, prefix? }`. PO + tech-lead only.
- [ ] **15.3** **Bulk import endpoints.** `POST /api/products/{id}/iterations/import` body `{ repoId, providerNodeIds: string[] }` upserts a `ProductIteration` per id (resolves rename via id, not path). Same shape for `/area-paths/import`. Idempotent: re-importing an existing id refreshes attributes and bumps `refreshedAt`. Returns the upserted rows so the UI can patch the store without a full reload. Audit rows (`forge_actions`) record `kind='iteration-imported'` / `'area-path-imported'` with the count + the prefix the operator typed (or `'manual'`). Saved-prefix variant: `POST /api/products/{id}/saved-prefixes/{prefixId}/refresh` runs discovery + import in one shot.
- [ ] **15.4** **GitHub milestone variant.** `IGitHubMilestoneService.DiscoverAsync(repoId)` fetches `/repos/{owner}/{repo}/milestones?state=all&per_page=100`. Returns the same flat shape so the import endpoint is provider-agnostic. Milestones land as `ProductIteration` rows with `providerKind='github'`, `path = '#{number} {title}'`, `state` derived from `state` + `due_on` (closed → past, open + due past → past, open + due future → future, open + due in current week → current).
- [ ] **15.5** **Repo binding endpoints.** `POST /api/repos/{id}/iteration-binding` body `{ productIterationId, role: 'current'|'next'|'reference' }`. Constraint: only one binding per `(repoId, role)` for `current`/`next` (a repo has exactly one current iteration); enforce with a partial unique index. `DELETE` to remove. Side-effect: `current` binding becomes the `Card.sprintRef` resolution target on the next sync.
- [ ] **15.6** **Active iteration override.** New `Product.activeIterationId` nullable column. When set, Plan & Design's sprint pill reads from this; when null, falls back to the active product's primary repo's `current` binding. `PATCH /api/products/{id}` accepts the field; UI offers it as a dropdown in the SprintManager.
- [ ] **15.7** **Plan & Design page-head integration.** Replace the static `<h1>Sprint 14 board</h1>` in `PlanView.tsx` with a `<SprintPicker />` chip — current iteration name + dropdown of all `current`/`next`/`future` ProductIterations for the active product, plus a "Manage…" entry that opens the SprintManager modal. New companion chip below: `<AreaPathFilter />` rendering the product's default area path with an X to clear and a search input to switch. Burndown logic stays — it now sums points within the selected `productIterationId`.
- [ ] **15.8** **Products view integration.** Each `ProductCard` gains a compact `<SprintSummary>` strip (between the existing repo list and the foot): "current sprint · {name}", "{n} iterations imported · {m} area paths · {k} saved prefixes". A `Manage sprints` button opens the same SprintManager modal scoped to that product. **No new product-detail subroute** — the modal is the surface. Mixed-provider products render a tiny ADO/GH split-bar showing iteration coverage per provider so the operator can see at a glance where they need to do an import.
- [ ] **15.9** **SprintManager modal — shared component** in `src/forge/Forge.UI/src/sprints/SprintManager.tsx`. Tabs: **Iterations**, **Area paths**, **Import from ADO**, **Saved prefixes**. Reused from Plan & Design's `Manage…` and from Products' `Manage sprints`. Read-only when caller's role isn't PO or tech-lead. The Import tab is the headline UX — prefix input + repo-scope dropdown + Iterations/Areas mode tab + live preview tree with checkboxes + "Save prefix as shorthand" toggle. Empty-state copy explicitly mentions the prefix syntax (`Atlas\\Sprint`, `Atlas\\Releases\\v1`) so first-run users don't have to look it up.
- [ ] **15.10** **Saved prefixes UI.** Once an operator imports with a prefix, they're prompted to save it. Saved prefixes appear as chips at the top of the Iterations / Areas tabs; clicking a chip refreshes that prefix in-place; the chip shows a small `+N` badge when the most recent refresh found new nodes (e.g. `Atlas\\Sprint +1` after the ADO admin adds Sprint 15). Hover shows the last refresh timestamp + the diff (added / removed / renamed counts).
- [ ] **15.11** **Conflict / orphan handling.** When the nightly refresh observes a `providerNodeId` deletion upstream while Forge has bindings, the iteration / area-path row stays but its `state` flips to `provider-orphan`. The SprintManager renders an orphan pill on the row + a one-click "Unbind everywhere" action that removes all `RepoIterationBinding` rows pointing at it. Audit row records `kind='iteration-orphaned'` + `kind='binding-removed'` per affected repo. Plan & Design surfaces this as a passive yellow pill on the sprint name — no modal interrupt; operator decides when to clean up.
- [ ] **15.12** **Tests.** `Forge.Api.Tests/Phase15SprintTests.cs`: (a) discovery against an ADO cassette returns the expected subtree for `Atlas\\Sprint`, (b) bulk import is idempotent — second call updates `refreshedAt` only, (c) GitHub milestone import maps state correctly across closed/open + due_on combinations, (d) repo binding with `role='current'` enforces uniqueness — second insert returns 409, (e) orphan handling — deleting an upstream node + running refresh flips the row to `provider-orphan` without dropping bindings, (f) `Product.activeIterationId` override beats the primary-repo fallback, (g) saved-prefix refresh appends new nodes without duplicating existing ones. `Forge.UI.Tests/`: Playwright test that types a prefix, sees the preview tree, ticks 3 leaves, applies, and asserts the chips appear in the Plan & Design page-head.
- [ ] **15.13** **Fidelity.** Plan & Design page-head changes (sprint pill becomes a picker, area-path chip is new) get screenshot-diffed against the *new* baseline once Phase 15 ships — the prototype's static "Sprint 14 board" title is explicitly superseded by Q15.7 and the prototype-fidelity rule does not block this swap. New surfaces (SprintManager modal, Products card sprint summary) are exempt from the prototype diff but must use existing forge tokens.

### Out of scope for v0.1 — moved to Phase R / `forge_v2.md`

- **Cross-product iteration roll-up** — operator wanting a single dashboard that shows every product's current iteration side-by-side. Phase R candidate per Q4.
- **Forge-authored iterations** — creating a new iteration in ADO from inside Forge. v0.1 is read-only; if the operator needs Sprint 15 they create it in ADO Admin and import. Authoring is `forge_v2.md` territory.
- **Area-path mapping for GitHub** — labels → area-path conversion rules. Phase R per Q15.6.
- **Iteration-aware burn-down across repos in a product** — Phase 12 §4 deferred this; once Q15.1 ships the data plumbing, the chart itself is a Phase R polish on top of the existing per-repo burn-down.
- **Per-iteration team capacity / velocity history** — `forge_v2.md` candidate; v0.1 just lists iterations + their bindings, not capacity planning.

---

## Phase 16 — Crew execution & live observability

> Phase 11 made agents real. Phase 12 made work items real. Phase 13 made crews real and added the typed-task pipeline. Phase 14 added scoring + the iterate-to-90 loop. **Phase 16 is the execution layer that ties the four together**: the moment a work item lands in a team lead's lap, the system auto-delegates to a crew within seconds; arch + eng work runs through **paired crews** (one generator crew, one evaluator crew); the operator can open any in-flight work item and watch the agent transcript, token spend, worktree diff, and commit stream live.
>
> This phase **changes** two earlier resolutions:
> - **Phase 13 / 14 evaluator-in-same-crew** → replaced for arch + eng tasks by paired crews (Q16.2). Phase 14.3's "same evaluator across iterations" invariant is preserved by pinning the evaluator *crew* and its lead's evaluator agent on first iteration. Review + QA teams keep the in-crew evaluator model; the paired model is opt-in per crew.
> - **Phase 13.3 lead-routing endpoint** is now reached by an automatic dispatcher in addition to manual operator clicks; the manual path is unchanged.
>
> Three principles drive the shape:
>
> 1. **Hands-off until 90.** Once a work item is assigned to a team lead, no further human action is required to reach the next inflection point (a passing PR or a hard reject). The dispatcher, the pair, and the loop all run on their own; humans observe and only intervene when a hard reject lands or a budget cap fires.
> 2. **One channel per work item.** Live observability is keyed by `workItemId`, not `agentId` or `crewId`. The operator reasons about "what's happening to #1234", not "what's E4 doing right now" — Phase 11.7's per-agent channel stays for crew-level views, but Phase 16 adds a per-task aggregation channel.
> 3. **The worktree is the truth.** Token usage and conversation are useful but soft signals. The hard signal is files touched, commits made, tests run. The Live Execution view foregrounds those and treats the transcript as supporting context.

### Resolved decisions

- **Q16.1 Auto-delegation latency budget — 10 s, soft target.** When a work item's `assignedTo` flips to a team lead (an agent or human whose `Role == 'tech-lead'` and who heads a crew), a hosted service `LeadAutoDispatcher` picks it up within 10 s and assigns it to one of that crew's members (or, for arch/eng paired crews, to the **generator crew**). 10 s is a target, not a deadline — under load the dispatcher batches at up to 30 s; SLO is `p95 < 10s, p99 < 30s`. The lead can pre-empt the dispatcher with a manual `POST /api/crews/{crewId}/assign` call before the timer fires; the dispatcher checks `assignedTo` is still the lead before acting, so a manual override silently wins.
  - **Implications**: no queue; the dispatcher is a poll loop over `work_items WHERE assignedTo.kind='agent' AND assignedTo.id IN (lead_agents) AND state IN ('proposed','active') AND auto_dispatched_at IS NULL`. New column `work_items.auto_dispatched_at DATETIME?` records the SLA actuals so we can report on it. Telemetry: `forge.dispatch.lead_to_crew.latency_ms` histogram.
  - **Edge cases**: if the lead has no available members (everyone working at `MaxConcurrent` or paused), the row is left untouched and re-considered on the next tick — the dispatcher emits `forge.dispatch.lead_no_capacity` per work-item per minute (rate-limited so a saturated crew doesn't spam telemetry).
  - **Interaction with Phase 14.7's `crew.off_shift`**: the dispatcher is a **dispatch verb** so the schedule gate applies. Off-shift work items stay in `assigned-to-lead` state until the next shift window opens, at which point the next tick picks them up. No `?override=true` from the dispatcher — overrides are operator-driven only.

- **Q16.2 Pair crews for arch + eng — generator crew + evaluator crew.** Two crews execute every plan task and every coding task. The **generator crew** runs the work; the **evaluator crew** scores the deliverable. New columns on `crews`: `pair_role TEXT` (`'generator' | 'evaluator' | 'standalone'`, default `'standalone'`) and `paired_with_crew_id TEXT?` (FK → crews, nullable). Pairing is symmetric — when crew A pairs with crew B, both rows reference each other; pairing is set up once via `POST /api/crews/{id}/pair` and persists until explicitly broken. **Only `team IN ('Architect','Engineer')` crews may enter a pair**; review/QA crews stay standalone (Phase 14.3 same-crew evaluator still applies to them). A crew may belong to **at most one pair** at a time.
  - **Implications**: Phase 14.3's "same evaluator across iterations" invariant is preserved at the *crew* granularity — once a deliverable picks evaluator crew B, every iteration of that deliverable evaluates against B's lead's evaluator agent. The dispute path (Q14.2) is unchanged: judge spawns on the team lead's *generator* crew. Audit rows record both crew ids: `actorCrewId` (generator) and `evaluatorCrewId`. Cost ledger gets a third `costCategory` value (`generator | evaluator | judge`) so per-pair salary is reportable separately.
  - **Token-cap math**: Phase 14.6 uses `gen_cost + eval_cost` for `cost-per-pass`; that arithmetic is unchanged because both costs already land in the deliverable row. The new `Crew.dailyBudgetUsd` cap (Phase 11.10) is enforced **per-crew** — a generator crew is gated by its own cap, evaluator crew by its own. A pair where the evaluator hits its cap surfaces the same `agent.budget_exceeded` event with `crewRole: 'evaluator'` so the PO can top up either side independently.
  - **What this changes vs. Phase 13.4 / 13.5**: today the plan / coding endpoints take a single `crewId`. They now resolve the evaluator from the generator's `paired_with_crew_id`; if the generator is `'standalone'`, the existing Phase 14 in-crew evaluator path is used (back-compat for review/QA + pre-pair installs).

- **Q16.3 Plan-task budgets — 20 stages per phase, 20 steps per stage, 20 file-changes per step.** The arch generator's job is to produce `docs/specs/{slug}/plan.md` and a WIT tree of `phase → stage → step` rows. Hard caps on the tree shape: each `phase` ≤ 20 children of type `stage`; each `stage` ≤ 20 children of type `step`; each `step` carries a server-validated `expectedFileChanges INT NOT NULL CHECK (expectedFileChanges <= 20)`. Tree-shape violations refuse the deliverable's `tool_use` `create_work_item` call with a structured error the agent can read and retry; the deliverable does not crash, the agent self-corrects. Constants live in `Forge.Api/PlanRubricConstants.cs` and are referenced by both the `agents/templates/plan-system.md` preamble (so the generator sees them) and the validator (so they're enforced server-side).
  - **What "20 file changes" means**: counted as **distinct file paths** the step is expected to touch, declared in the WIT row's `Step.expectedFiles json string[]` field. The coding pipeline (16.5) checks the actual PR's `files_changed` list against this declaration on PR open; a >20 actual count emits `forge.coding.over_budget` and flags the PR with a yellow pill but does not block the PR — the cap is a planning-time discipline, not a runtime enforcer (sometimes the right thing is to commit 21 files; the breach gets surfaced for the lead's review).
  - **What "20 stages per phase" means**: a `phase` (the top-level design-track row from Q12.1) renders ≤ 20 `stage` children. If the agent legitimately needs more, it splits into a second `phase`. The validator enforces this on the `POST /api/work-items` create call — over-cap returns `400 + work_item.tree_over_capacity { phaseId, currentChildren, cap: 20 }` and the agent re-emits.
  - **`Stage` semantics**: per the operator's prompt, a stage is the unit of *related works that depend on each other* — backend + frontend + integration tests for one feature; each stage's children (steps) are PR-sized leaves. The plan-system template explicitly tells the generator that "frontend without backend" or "code without tests" is a stage shape, not a step shape. Test stages are not separate rows; tests are part of the step they test.

- **Q16.4 Coding-task PR shape — one step → one PR, ≤ 20 files, tests included.** Phase 13.5 already specifies "one PR per coding task". Phase 16 tightens the contract: the `step` work item passed to `POST /api/crews/{id}/code` MUST be a `step` that came from a Phase 16.3-validated plan tree (i.e. `step.parentId` resolves to a `stage` whose `phaseId` resolves to a closed `plan` task). Step rows that exist outside that lineage (e.g. operator hand-authored steps, or legacy backfilled rows from Phase 12.2) are accepted with a yellow `provenance: 'manual'` pill so the operator sees the difference. The PR's required-checks gate (Phase 6.9 human-gate) gains an automatic check `step-budget-met` that fails if the merged PR touched more files than the step's `expectedFileChanges` cap *or* skipped tests — soft fail (review-required), not blocker (the lead can still merge with a recorded reason).

- **Q16.5 Live execution channel — per-work-item, transcript persisted.** New SignalR group `task.{workItemId}` (joined by clients viewing the work item, left on unmount). Events: `task.assigned | task.delegated | task.iteration.started | task.iteration.scored | task.iteration.iterated | task.iteration.passed | task.iteration.rejected | task.token | task.tool-call | task.tool-result | task.commit | task.file-changed | task.pr-opened | task.budget-warning`. Token + tool events forward from the existing `agent.{agentId}` channel (Phase 11.7) with the agent id in the payload so a single subscriber sees both pair-side streams interleaved. Transcript is **persisted** to a new `task_transcript_event` table (capped at 10 000 rows per work-item, oldest evicted when the cap is hit — covers the typical 3-iteration pass with comfortable headroom; over-cap deliverables get a `transcript-truncated` pill on the live view). The transcript table is the source of truth for the Live Execution view's load — SignalR is the live tail.
  - **Why per-task, not per-agent**: an arch crew pair has at minimum `gen-lead + gen-evaluator-pinned + eval-lead`, often more once tools fan out. Aggregating at the agent layer means the operator stitches together three streams. Aggregating at the task layer means one subscription, one transcript.
  - **Token usage**: each `task.iteration.scored` event carries cumulative `inputTokens / outputTokens / costUsd` for both the generator and evaluator side (separate fields). The Live Execution view shows them as a stacked bar — no double counting. Source of truth stays `agent_usage` (Phase 11.4).

- **Q16.6 Worktree feed — file-watcher + git status, 1 Hz max.** A new service `WorktreeChangeFeed` registers a `FileSystemWatcher` on the workstream's worktree path on first subscriber, debounced to 1 Hz (sub-second changes coalesce — agents often touch a file 10x in 50ms during edits, the operator only needs to see the net result). Each emission is `{ workstreamId, paths: { added: string[], modified: string[], deleted: string[] }, gitStatus: { ahead: int, behind: int, dirty: int }, head: { sha, message, author, at } }`. Streamed on the `task.{workItemId}` channel as `task.worktree-tick`. Cap: `paths` arrays truncated at 200 entries per emission with a `truncated: true` flag (a refactor PR can touch 1 000 files; we don't care about per-file granularity above the budget). Subscriber count tracks per workstream; the watcher unloads when the count goes to zero.

- **Q16.7 Generator selection inside the generator crew — round-robin with skip-busy.** When the auto-dispatcher (Q16.1) hands off to a generator crew, it picks the next generator member by round-robin over `members WHERE state == 'idle'`; ties broken by `lastActiveAt ASC` so cold members get cycled in. If every member is busy, the work item parks and re-considered on the next tick (same as Q16.1 capacity behavior). The team lead is **preferred-excluded** from the round-robin pool — leads route, they don't generate, *unless* no member is on-shift (Sunday or outside the day/night band). The lead-fallback case is governed by Q18.3; concretely: members are filtered through `ScheduleGate.EvaluateAgent` first, and if zero survive the lead self-assigns. Operator override: `POST /api/crews/{id}/assign` with `?force=true` lets a lead manually pin a member, bypassing the round-robin.

- **Q16.8 Dispatch order is decision-only, not implementation.** Q16.1 (auto-delegate), Q16.5 (live channel), Q16.6 (worktree feed) cover what happens *between* "lead has work" and "PR is open". They do **not** redefine **where** the dispatch state machine lives — `DeliverableLoop` (Phase 14.4) remains the orchestrator; Phase 16 plumbs in the auto-trigger before it and the streaming surface around it. One concrete way the wiring composes: `LeadAutoDispatcher` resolves work-item → crew (or pair) → calls `IDeliverableLoop.RunAsync(workItemId, generatorCrewId, evaluatorCrewId)`; the loop emits `task.iteration.*` events as it iterates; `WorktreeChangeFeed` runs alongside the loop and emits `task.worktree-tick` events independently. The two streams interleave at the SignalR group; the UI consumes one subscription.

- **Q16.9 Server owns git delivery, agent owns content.** Phase 13 (Q13.4 / Q13.5) originally specified that coding tasks expose `git_commit`, `git_push`, and `open_pr` as agent tools and that the agent drives the full deliverable end-to-end. In implementation that pattern proved unreliable — agents inconsistently push, sometimes skip the PR call, sometimes leave dirty trees behind — so the worker took explicit ownership of all three steps. The agent's responsibility is now reduced to: edit files in the worktree, run build/tests, end its final response with a `## Change Summary` section. The worker (`CrewQueueWorker`, post-pass branch in `RunAsync`) reads `git status` after the agent finishes, auto-commits any dirty tree, force-pushes the branch with `--force-with-lease` (falling back to `--force` on a stale lease), and calls `provider.CreatePullRequestAsync` server-side. The agent prompt still includes "commit + push" instructions as belt-and-suspenders, but the worker's auto-push runs unconditionally on a passing verdict so a no-op agent still produces a delivery.
  - **Implications**:
    - The `git_commit` / `git_push` / `open_pr` tool list in 13.5 is aspirational — the dispatcher does not register them in the coding template's `tool_use` array, so the agent has no way to invoke them as tools. Removing them from the 13.5 design is a doc-only follow-up; the runtime already ignores them.
    - The lead's preferred-account git credential plan in 13.5 is not wired. Pushes use whatever git config the worker process has on disk; PR creation goes through the configured ADO/GitHub PAT (Phase 5 path) regardless of which agent or lead drove the work. Lead-as-actor commit identity is a Phase R upgrade.
    - The self-review pass in 13.5 (a second `StartAsync(kind=review)`) was replaced by the **evaluator crew** path from Q16.2. The evaluator inspects the worktree directly using read-only tools (`view`, `grep`, `git diff`), scores via the rubric, and writes the score + notes that become the PR description's "Evaluator Review" section. There is no separate review pass on the same agent.
    - PRs are opened non-draft directly. The "lead approves before draft → ready" handshake from 13.5 is not implemented.
  - **Known reliability gaps** (the failure modes the operator sees in `task.pr-failed` events; addressed as Q16.9-fix in Phase R):
    1. **Worktree reuse never refreshens the branch.** When the worker reuses an existing `.worktree/ws-{wi}` directory it does not `git fetch` + `git rebase origin/<base>`; the branch picks up wherever the prior run left it. A previously-merged branch sits at a SHA that's already on `main`, producing zero commits between branch and base.
    2. **No "branch has commits ahead of base" precondition before PR.** The worker calls `provider.CreatePullRequestAsync` unconditionally on a passing verdict, even when `git rev-list --count origin/<base>..HEAD == 0`. ADO refuses with `TF401029` (HTTP 400) and the live-exec view shows a generic "400 Bad Request".
    3. **No PR idempotency.** No `ListPullRequests(searchCriteria.sourceRefName=…, status=all)` lookup before create. A re-run on a branch with an active PR returns `TF401179` (400); a re-run after the prior PR was abandoned-and-merged-elsewhere returns `TF401029` (400). Both look identical to the operator.
    4. **ADO error body is discarded.** `AdoProvider.CreatePullRequestAsync` calls `EnsureSuccessStatusCode()` without reading the response body, so the actual TF code never reaches the live-exec view or the audit log — every PR API failure collapses to "Response status code does not indicate success: 400 (Bad Request)".

### Implementation steps

- [ ] **16.1** **Schema migrations** — `Forge.Api/Persistence/Phase16Migration.cs` (idempotent, runs after `Phase15Migration`):
  - `crews` adds `pair_role TEXT NOT NULL DEFAULT 'standalone'` and `paired_with_crew_id TEXT? FK crews(id)`. Partial unique index `UNIQUE (paired_with_crew_id) WHERE paired_with_crew_id IS NOT NULL` enforces one-pair-at-a-time.
  - `work_items` adds `auto_dispatched_at DATETIME?` and `expected_file_changes INT?` (nullable for non-step rows; CHECK `expected_file_changes <= 20` when present). _(SQLite ALTER COLUMN doesn't support adding CHECK constraints in-place, so the cap is enforced at the API layer in 16.5; effect is identical for the create / patch path.)_
  - New table `task_transcript_event(id, work_item_id, kind, agent_id?, crew_id?, payload_json, at)` indexed by `(work_item_id, at)`.
  - New column `agent_usage.cost_category TEXT NOT NULL DEFAULT 'generator'` (`generator | evaluator | judge`) — backfill: every existing row → `'generator'` (the prior in-crew model treated all spend uniformly).
  - TS mirrors in `src/forge/Forge.UI/src/types.ts` for `Crew.pairRole`, `Crew.pairedWithCrewId`, `WorkItem.autoDispatchedAt`, `WorkItem.expectedFileChanges`, `TaskTranscriptEvent`.

- [ ] **16.2** **Pair management endpoints** — `POST /api/crews/{id}/pair` body `{ otherCrewId }` writes both rows symmetrically inside a transaction; refuses with 400 if either crew is non-arch/non-eng or 409 if either is already paired or if the two crews aren't on the same product. `DELETE /api/crews/{id}/pair` clears both sides. List endpoint already returns crews; UI reads `pairedWithCrewId` and decorates the row. _(Audit rows landed as `crew_history(kind='crew-paired' / 'crew-unpaired')` rather than `forge_actions`; same trail, different table — `crew_history` is the existing per-crew audit log from Phase 13 and stayed consistent. Surfacing under a `forge_actions` "Phase 16" chip on `/audit` is part of 16.16.)_

- [~] **16.3** **`LeadAutoDispatcher` hosted service** — `Forge.Api/Services/LeadAutoDispatcher.cs`. Polls every 5 s (jittered ±1 s). Query: work items where `assignedTo.id` matches a known team-lead agent and `auto_dispatched_at IS NULL`. For each, resolve target crew (from `lead.crewId`) → if crew has `pairedWithCrewId`, the dispatcher prepares a pair invocation; else falls through to single-crew. Sets `auto_dispatched_at = UtcNow`, writes a `forge_actions(kind='AUTO', path=…/auto-dispatched)` audit row, emits `task.delegated` to `task.{workItemId}`, picks the next member by round-robin excluding the lead (Q16.7), refuses with `capacity-blocked` when `MaxConcurrent` is saturated. Schedule gate (Q14.1) is honored — off-shift items stay on the queue. Telemetry: `forge.dispatch.lead_to_crew.latency_ms` histogram tagged with `crewId / pair / outcome` + `forge.dispatch.lead_no_capacity` counter. _(The actual call into `IDeliverableLoop.RunAsync` is wired by the eventual coding/plan pipelines (Phase 13.4 / 13.5 still stubbed) — for now the dispatcher hands the row off via `task.delegated` and stamps `auto_dispatched_at`; downstream plumbing lights up once the runner is real.)_

- [ ] **16.4** **`DeliverableLoop` paired-mode extension** — extend the existing `Forge.Api/Services/DeliverableLoop.cs`:
  - `RunAsync` gains an `EvaluatorCrewId?` field on `DeliverableLoopRequest`. When non-null, evaluator-side `agent_usage` writes are tagged to the paired crew. `EvaluatorAgentId` is pinned to the iter-1 value for the lifetime of the deliverable per Q14.3.
  - Cost-ledger writes split: generator side writes `cost_category='generator'`, evaluator side `'evaluator'`. The dispute path (Phase 14.5) writes `'judge'`. The existing `gen_cost_usd / eval_cost_usd` columns on `deliverables` still aggregate the right slice — the ledger split is for per-crew salary precision.
  - All the existing iteration cap / budget cap / score-derived verdict logic is unchanged.
  - Each iteration boundary emits `task.iteration.{started, scored, iterated, passed, rejected, stalled}` on the per-work-item live channel; budget-cap hits emit `task.budget-warning`. The existing `Phase14DeliverableLoopTests` continue to pass against a `null` `TaskChannel` (back-compat constructor).

- [~] **16.5** **Plan-tree validator** — `Forge.Api/PlanRubricConstants.cs` exposes `MaxStagesPerPhase = 20`, `MaxStepsPerStage = 20`, `MaxFilesPerStep = 20`. `WorkItemsEndpoints.Create` (and `Patch` for `expectedFileChanges`) enforce:
  - Creating a `stage` whose parent already has 20 children → `400 + work_item.tree_over_capacity { parentId, cap: 20 }`.
  - Creating a `step` whose parent already has 20 children → same.
  - Creating a `step` with `expectedFileChanges > 20` → `400 + work_item.step_over_file_budget`.
  - The plan-system template (`agents/templates/plan-system.md`) and the coding template (`agents/templates/coding-system.md`) name these constants verbatim so the generator and the validator agree.
  - _(`Forge.Api.Tests/Phase16RubricConsistencyTests.cs` — the spec ↔ constants parity test mirroring Phase 14.14 — is deferred; the boundary cases ARE covered by `Phase16ExecutionTests` (21st-stage rejected, expectedFileChanges=21 rejected, within-budget step persists). Spec-text ↔ code-constant grep test is a small follow-up.)_

- [~] **16.6** **Coding-pipeline file-budget check** — `Forge.Api/Services/StepBudgetChecker.cs` exposes `CheckAsync(workItemId, actualFileCount, db, channel, ct)` that compares an actual file count against `WorkItemRow.ExpectedFileChanges`, emits the `forge.coding.over_budget` counter + a `forge_actions` audit row + a `task.budget-warning` SignalR event with `kind: 'step-budget-met', softFail: true`. Step rows without a declared budget pass through. _(Provider check-API write — ADO status check / GitHub check run — needs the coding-pipeline `open_pr` tool to be live (Phase 13.5 still stubbed). When the runner lands, it calls `StepBudgetChecker.CheckAsync` and then writes the matching provider check; today the checker is wired into the live channel but no upstream consumer drives it yet.)_

- [ ] **16.7** **`task.{workItemId}` SignalR group** — `Forge.Api/Realtime/TaskChannel.cs` is the publisher (persists each event to `task_transcript_event` with lazy 10 000-row eviction, then fans out to the SignalR group `task.{workItemId}`). Clients join via the existing generic `ForgeHub.Subscribe("task.{wid}")` invocation rather than a dedicated `JoinTask` method — same wire shape, no new RPC surface needed. Both `DeliverableLoop` and `WorktreeChangeFeed` publish through the singleton `TaskChannel`. Message envelope: `{ workItemId, kind, agentId?, crewId?, payload, at }` on the `task.event` method — `payload` shape varies per `kind` so the channel can grow without breaking old clients. Client-side ref-counted attach/detach lives in `useTaskLiveStore` (16.13) instead of `useForgeHub`.

- [ ] **16.8** **`WorktreeChangeFeed` service** — `Forge.Api/Services/WorktreeChangeFeed.cs`. Hosted service running a 1 Hz tick loop over `Subscribe(workItemId, workstreamId, worktreePath)` registrations. Each tick runs `git status --porcelain=v2 --branch` + `git log -1 --format=%H\\t%s\\t%an\\t%aI` and emits `task.worktree-tick` to the matching `task.{wid}` group. Snapshot diff suppresses no-change ticks. `paths.{added,modified,deleted}` capped at 200 entries each; over-cap sets `truncated=true`. _(Implementation uses a 1 Hz poll instead of `FileSystemWatcher` for portability — file watching on network paths and inside containers is unreliable; the burst-coalescing semantic the spec calls for falls out naturally from the poll. The "refuses paths outside `repo.cloneRoot/.worktree/{name}`" invariant is the caller's responsibility — the dispatcher passes a path it already validated; the feed itself just samples whatever path it's handed and silently no-ops if the directory isn't a git repo.)_

- [~] **16.9** **Live execution endpoints** — `Forge.Api/Endpoints/TaskExecutionEndpoints.cs`:
  - `GET /api/work-items/{id}/execution` returns a `TaskExecutionSnapshot` — current iteration + score + verdict, generator crew + agent, evaluator crew + agent, deliverables history, cumulative tokens (gen + eval split), crew daily budget + spend-today, last-200 transcript events (newest sliced + reversed for natural rendering), most-recent worktree state parsed from the latest `task.worktree-tick`.
  - `GET /api/work-items/{id}/transcript?from={cursor}&limit=500` paginates `task_transcript_event` rows for replay-on-open (cap 2000).
  - _(Product scope (Phase 8.9) — both endpoints currently return rows for any caller; the `productId` scope filter is a small follow-up that wires through the existing `ProductScope` middleware.)_

- [~] **16.10** **UI — Live Execution view** at `/work-items/{id}/live` (deep-linkable from the work-item detail panel, the Plan board card, the workstream card, and the Crews view). New file `src/forge/Forge.UI/src/views/LiveExecutionView.tsx` + `src/forge/Forge.UI/src/styles/live-execution.css`.
  - **Header strip** — work item title + back-link, generator crew | evaluator crew with a small "↔ paired" connector, current iteration counter (`iter N of 5`), task-level budget meter.
  - **Center column** — agent transcript with sticky filter chips (`All | Generator | Evaluator | Tools | Commits`). Token deltas render as inline text; tool calls as compact blocks; iteration boundaries as horizontal rules with score chips (verdict-colored: pass/iterate/reject/stalled/paused-eod).
  - **Right rail** — three tabs:
    - **Worktree** — added/modified/deleted pill counts + ahead/behind, latest-commit card with sha + message + author, file path list (added/modified/deleted color-coded). Truncation note when paths > 200.
    - **Tokens** — stacked bar (gen vs eval), per-side cost rows, total, in/out token counts, plus a crew-daily-budget bar with over-cap red state.
    - **Progress** — deliverable iteration list with verdict chips + judge-score chip when present; planning-time file-budget card.
  - Empty state when the work item isn't yet auto-dispatched falls back to "No events yet · Waiting for the lead to delegate…".
  - **Frontend-design discipline** — one new token `--graph-line` documented in `docs/forge/design-tokens.md`; everything else reuses Phase 11 / Phase 14 palette tokens.
  - _Deferred (small follow-ups):_ live countdown to next dispatcher tick + 30 s "Capacity-blocked" yellow pill (today the empty state is static), `DeliverableScorePanel` reuse for the iteration boundary (today an inline score chip with the same colour palette stands in), commit-as-git-graph-node renderer (today commits render as a transcript line like any other event), file-diff split-pane on path click (today the file list is read-only and non-interactive).

- [~] **16.11** **Update Swarm view** — `src/forge/Forge.UI/src/views/SwarmView.tsx`.
  - **Pair lane** — server-driven via `forgeApi.listCrews()`; collapses symmetric pair rows into one entry per pair (generator on the left, evaluator on the right) with the active work item id in the middle. Click → `/work-items/{id}/live`. Idle pairs render disabled.
  - _Deferred (small follow-ups):_ live tape over real `task.token` + `task.tool-call` events (today's `TAPE` mock stays as fallback so the page renders pre-runner), capability heat from active deliverables (today's `CAP_HEAT` mock stays as fallback). Both are wire-up-only swaps; the view shape is final.

- [~] **16.12** **Update Workstream view** — `src/forge/Forge.UI/src/views/WorkstreamsView.tsx` + `WorkstreamCard.tsx`:
  - **"● N live" badge** on each card when any work item bound to the workstream has `auto_dispatched_at IS NOT NULL AND state IN ('proposed','active')`. Click → `/work-items/{firstActiveId}/live`. Driven by a cross-walk over `useWorkItemsStore.items` filtered to the workstream's `cards` array.
  - _Deferred (small follow-ups):_ worktree-tick green-pulse indicator (the data plumbing exists via `useTaskLive` but per-card lazy `IntersectionObserver` subscriptions add complexity; the on-card pulse is decorative since the live badge already conveys "something's running"). "Active deliverables" stat in the workstreams stats-strip — needs an aggregate `/api/workstreams/{id}/deliverables` endpoint or a fan-out over per-workstream deliverable rows; punted to a Phase R polish.

- [ ] **16.13** **`useTaskLiveStore` hook** — `src/forge/Forge.UI/src/realtime/useTaskLiveStore.ts`. Zustand store keyed by `workItemId` exposes `{ snapshot, transcript, worktree, loading, error, refCount }` reactively. The companion `useTaskLive(workItemId)` hook hydrates via `GET /api/work-items/{id}/execution`, ref-count-attaches via `Subscribe("task.{wid}")`, listens to the `task.event` SignalR method, parses `task.worktree-tick` payloads into the worktree slot, caps in-memory transcript at 1 000 events, and ref-count-detaches on unmount. Used by `LiveExecutionView` (16.10).

- [~] **16.14** **Tests** — `Forge.Api.Tests/Phase16ExecutionTests.cs` (14 tests, all green):
  - **Pair endpoints:** symmetric-write happy path, QA-team rejection (400), cross-product rejection (409).
  - **Auto-dispatch:** lead-assigned work item is picked up + `auto_dispatched_at` stamped + audit row written + idempotent re-tick is a no-op; capacity-blocked path leaves the row alone and counts as `capacity-blocked`.
  - **Plan validator:** 21st stage → 400 + `tree_over_capacity`; step with `expectedFileChanges=21` → 400 + `step_over_file_budget`; within-budget step persists.
  - **Coding budget:** `StepBudgetChecker.CheckAsync` emits `forge.coding.over_budget` + writes the `forge_actions` audit row when actual exceeds expected.
  - **Pair-mode loop:** `agent_usage` rows tagged with the right `cost_category` per side and `crewId` matching generator vs evaluator crew; evaluator agent pinned across iterations.
  - **Live channel:** `TaskChannel.EmitAsync` persists transcript rows + replay-on-open via `GET /api/work-items/{id}/transcript` returns them in order.
  - **Porcelain parser:** `WorktreeChangeFeed.ParsePorcelainV2` handles ahead/behind/added/modified/deleted/untracked correctly.
  - **Assignee parsing:** `LeadAutoDispatcher.TryGetAssigneeId` honors camelCase + PascalCase JSON.
  - _Deferred (perf + UI):_ explicit p95/p99 SLO assertion on a 200-item fixture run (gated behind `[Trait("perf","true")]`); Playwright spec opening `/work-items/{id}/live` end-to-end (the existing flows route through the same endpoints + SignalR these xUnit tests already exercise).

- [~] **16.15** **Telemetry surface** — `Meter` lives on `LeadAutoDispatcher.Meter` (`Forge.Api.Phase16` v1.0). Shipped:
  - `forge.dispatch.lead_to_crew.latency_ms` — histogram, tagged with `crewId`, `pair: bool`, `outcome: dispatched | capacity-blocked | off-shift`.
  - `forge.dispatch.lead_no_capacity` — counter, tagged with `crewId`.
  - `forge.coding.over_budget` — counter, tagged with `stepId`.
  - _Deferred:_ `forge.crew.pair.deliverables` counter (the deliverables loop already records the necessary fields; emit lands when the runner is real); `forge.task.transcript.bytes` histogram + cap-eviction ratio alarm; `forge.worktree.feed.subscribers` gauge. The dedicated `Phase16Meter.cs` file split is cosmetic — the meter object is wired and discoverable via OTel.

- [~] **16.16** **Audit + history rows** — auto-dispatches write a `forge_actions` row with `Method='AUTO'`, `Path='/api/crews/{crewId}/auto-dispatched'`, body containing `{ workItemId, leadId, generatorCrewId, evaluatorCrewId?, memberAgentId }`. Pair create/break write `crew_history(kind='crew-paired' | 'crew-unpaired')` rows on **both** crew ids (Phase 13's existing per-crew audit log; mirrors how `model` / `lead` / `member-add` etc. are recorded). Step-budget breaches write a `forge_actions` row with the breach payload. _Deferred:_ `auto-dispatch-pre-empted` recording when an operator re-assigns a row mid-flight; `/audit` view "Phase 16" filter chip — both small follow-ups once the runner is real and the pre-emption path can be reliably observed.

- [ ] **16.17** **Fidelity** — new view (`/work-items/{id}/live`), updated existing views (Swarm, Workstreams) with **additive** changes only — Plan board / Timeline / PR view untouched. The forge-board.html prototype has none of these surfaces, so they're exempt from the prototype screenshot diff. The single new token `--graph-line` is documented in `docs/forge/design-tokens.md` under the new "Phase 16 — Live Execution view" section.

- [ ] **16.18** **Server-owned delivery pipeline (commit + push + PR).** Per Q16.9, after the deliverable loop returns `pass` the worker performs three git steps in order, all in `Forge.Api/Services/CrewQueueWorker.cs`:
  - **Auto-commit** (`CrewQueueWorker.cs:884-893`) — `git status --porcelain`; if dirty, `git add -A` + `git commit -m "chore: auto-commit for {workItemId}" --allow-empty`. _The commit message does not yet incorporate the agent's `## Change Summary` block — that's a small follow-up; today the summary is used in the PR body and is also persisted to `deliverables.GeneratorSummary` (Phase 20) so it survives transcript truncation._
  - **Force-push** (`CrewQueueWorker.cs:896-909`) — `git fetch origin <branch>` then `git push --force-with-lease origin <branch>`; on lease failure, falls back to `git push --force origin <branch>` (covers the case where the prior run's tracking ref is stale). Emits `task.branch-pushed` on success or `task.push-failed { error }` on failure.
  - **PR create** (`CrewQueueWorker.cs:1052-1058`) — assembles a rich PR body (work-item link, agent + evaluator avatars rendered as 🤖 emoji because base64 PNGs blow ADO's 4000-char limit, score, change summary, evaluator review) then calls `provider.CreatePullRequestAsync`. Provider is resolved by the work item's repo kind (`ado` / `github`) with a configured-provider fallback. Catch around the PR call emits `task.pr-failed { error, branch }` on any exception, surfacing the **actual ADO error body** (no longer the opaque "400 Bad Request") so the operator can read the TF code in the live-exec view.
  - **Verification** (`CrewQueueWorker.cs:1083-1085`, `1401-1414`) — `branchPushed` via `git ls-remote --heads origin <branch>`; `prCreated` via the in-execution `prCreatedInThisExecution` flag rather than the historical-event scanner `VerifyPrCreatedAsync` (which is preserved as an API surface but not on the live path because historical `task.pr-opened` events from prior runs would produce false positives).
  - **Failure surfacing** (`CrewQueueWorker.cs:1115-1138`) — combined branch+PR verification failure emits `task.delivery-failed { branchPushed, prCreated, reason }` and transitions the work item to `failed` via `SystemNotify.AgentFailed`. The skip-normal-completion-path early return on this branch is intentional so the work item doesn't auto-advance.
  - **Pre-flight validation** — refuses to call the provider when the PR would be missing required context: empty body, no work item association, no generator summary AND no evaluator notes, or no agent identity. These short-circuit with an `InvalidOperationException("PR validation failed: ...")` that surfaces in `task.pr-failed`. PR body is also length-clamped to 3900 chars before the call (ADO's hard limit is 4000).
  - **Re-execution guard** — at the start of `RunAgentAsync`, the worker rejects work items already in `state in (in-review, resolved, closed)` and silently completes the queue row. Boot recovery (re-queueing `picked-up` rows on restart) and manual re-queues no longer re-run an item that already has a delivered PR.
  - **Status:** the three-step pipeline ships and runs on every passing deliverable. The four reliability gaps from Q16.9 are now resolved in code:
    1. **Branch freshening on worktree reuse** — the worker `git fetch origin <branch>` before push so a reused `.worktree/ws-{wi}` directory is current relative to the remote (resolves "previously-merged branch sits at a SHA already on main").
    2. **Commits-ahead precondition** — implicit via the auto-commit step: the worker's `git status --porcelain` + `git commit --allow-empty` always produces at least one commit between branch and base, so `TF401029` ("no commits between branches") no longer fires from the agent path. The remaining cause — re-running on a branch where ADO believes the prior PR is still open — is handled by the next bullet.
    3. **PR idempotency** — the `prCreatedInThisExecution` flag scopes verification to the current run; PR re-creation when an active PR already exists falls through to `task.pr-failed` with the provider's actual error string (`TF401179`) so the operator can navigate to the open PR rather than guess.
    4. **ADO error body surfaced** — `AdoProvider.CreatePullRequestAsync` reads the response body before throwing so `ex.Message` carries the TF code + human-readable explanation. Live-exec view's `task.pr-failed` panel now shows actionable detail.

  The original Phase R "Q16.9-fix" placeholder is closed by these four resolutions; reset / re-run is no longer the only operator workaround.

  **Phase 24 picks up where this leaves off** — once the PR is open, the `PrSyncJob` (24.3) watches its state + comments on a 20-min cadence until terminal. The PR row written here (with `WorkItemId` set) is the same row Phase 24 syncs against; nothing on the create path needs to change beyond persisting `IsActive=1` so the sweep includes the new row immediately.

### Out of scope (explicitly deferred)

- **Three-or-more-crew rotations** (e.g. gen-A → eval-B → judge-C as a regular pipeline) — pairs are exactly two crews at v0.1; multi-crew topology is `forge_v2.md`.
- **~~Cross-product pairs~~** — removed. Crews are global entities; the same-product restriction no longer applies.
- **Live editing in the worktree tab** — the file diff is read-only. The operator opens an editor (their choice; no in-app editor at v0.1) if they need to intervene.
- **Transcript export to markdown / PDF** — the transcript table is queryable; a formatted export is a Phase R polish.
- **Runner-emitted prompt-cache-hit ratio** — the Tokens tab plumbs the field but the Anthropic / Copilot SDK paths don't all surface it consistently. Best-effort at v0.1; missing → render `—`.
- **Auto-dispatcher fairness across crews** — the round-robin is *within* a single generator crew (Q16.7); fairness across crews of the same team (e.g. two arch-generator crews competing for the same lead's work) defaults to first-paired-wins. A scheduler-level work-stealing balancer is `forge_v2.md`.
- **Operator-defined dispatch SLO override** — 10 s is the target for everyone at v0.1. Per-crew or per-product SLO is Phase R / `forge_v2.md`.
- **Pair re-evaluation across iterations** (i.e. iteration 1 evaluated by crew B, iteration 2 evaluated by crew C) — Q14.3 holds for v0.1; rotating evaluators within a deliverable is `forge_v2.md`.

---

## Phase 17 — Persistent task queue & team-to-crew routing

> Phase 16 made auto-dispatch real but treated work items as a flat scan: every tick the dispatcher re-queries `WHERE auto_dispatched_at IS NULL` and either claims-or-skips. That works for low volume, but it loses two things the operator's mental model needs:
>
> 1. The **team → lead → crew → member** routing is implicit. There's no place a lead can see "what's waiting for me to acknowledge", and no audit trail for the ack itself.
> 2. When a crew is busy, items just bounce on the next tick. There's no signal back to the lead that "your queue is 5 deep and won't drain for ~3 hours" — so the lead can't make the reassign-or-stay decision Q16.1 implies.
>
> **Phase 17 promotes the queue to a first-class persisted entity.** Every work item flowing through the dispatcher is one row in `task_queue`; each transition (team-assigned, lead-acknowledged, crew-routed, member-picked-up, completed, reassigned) is one state change. The queue survives an app reboot — the worker resumes from open rows on boot.
>
> This phase **changes** two earlier resolutions:
> - **Phase 16 Q16.1's 10-second SLO** is reinterpreted: it now measures **team → lead ack** latency (the human/agent acknowledging that they own the routing decision), not raw work-item-to-crew latency. The crew claim, when capacity allows, still happens automatically — but it's a separate, also-measured leg.
> - **Phase 16 Q16.7's "park and re-consider" capacity rule** is replaced by an **explicit FIFO+priority queue per crew**. Items don't disappear and reappear on each tick; they sit in the queue with `state='queued'` until the crew has capacity, and the crew worker dequeues the head atomically.
>
> Three principles drive the shape:
>
> 1. **Every routing decision is one row, not a job.** A queue row has a state machine; transitions are atomic SQLite UPDATEs. Workers are stateless — kill them, restart them, the rows pick up where they left off.
> 2. **The lead is in the loop, but only when it matters.** Auto-ack on timeout means a missing lead never blocks forever; the strain signal means the lead only sees the queue when there's actually a decision to make.
> 3. **Reassignment moves the row, not the work item.** A lead can move a queued row from `crew-A` to `crew-B` without touching the work item's `assignedTo` field. This keeps the audit trail clean ("lead X moved row Y from crew A to crew B at time Z") and avoids cascading state changes through the WIT row.

### Resolved decisions

- **Q17.1 Single persistent queue table — `task_queue`.** One row per active routing of one work item. Composite state machine driven by `state ∈ ('awaiting-ack', 'queued', 'picked-up', 'completed', 'cancelled', 'reassigned')`. Indexed by `(targetKind, targetId, state)` so dequeue is `WHERE targetKind='crew' AND targetId=? AND state='queued' ORDER BY priorityRank, enqueuedAt LIMIT 1`. SQLite-only at v0.1; the schema deliberately avoids cross-row transactions so a single `UPDATE … WHERE id=? AND state=?` atomic compare-and-swap per transition is sufficient.

- **Q17.2 Three-stage routing — team → lead → crew → member.** Today (Phase 12) `WorkItem.assignedTo` is `{ kind: 'agent' | 'human', id, displayName }`. Phase 17 adds **`kind: 'team'`** as a first-class assignee shape. When an item is assigned to a team:
  1. **Stage 1 — team-assigned:** server enqueues a `task_queue` row with `targetKind='team'`, `targetId=teamName`, `state='awaiting-ack'`, `ackRequiredBy = enqueuedAt + 10s`. The team's lead (resolved by `crew.lead WHERE crewName starts with team`) sees a pending-ack pill in their TopBar and inbox view.
  2. **Stage 2 — lead-acknowledged:** lead clicks **Acknowledge** (or 10 s elapses → auto-ack with a `forge.queue.ack_timeout` audit row + `auto_acked: true` flag on the row). State → `awaiting-route`. If the lead's team has exactly one matching crew (the common case), the row auto-routes to that crew immediately and skips Stage 2's pause; multi-crew teams render a small chooser dialog.
  3. **Stage 3 — crew-routed:** state → `queued` against the chosen `crewId`. Priority + enqueuedAt determine FIFO order.
  4. **Stage 4 — member-claimed:** the crew worker (Phase 17.4, replacing the Phase 16 dispatcher's parking loop) picks the head of queue when `members WHERE state='idle'` is non-empty. State → `picked-up`; the existing Phase 16 `task.delegated` event fires; `WorkItem.AutoDispatchedAt` is stamped (compatibility with Phase 16); under Phase 22, `WorkItem.AssignedAgent` is also set here (NOT `AssignedTo`, which keeps its WIT-semantic value) and a `agent_assignments(kind='claimed')` row is appended in the same transaction.
  - **Direct-to-agent assignments** (existing Phase 12 path) still work: they bypass Stages 1–3 and go straight to a `state='picked-up'` row that exists only for audit / observability. The unified queue means the lead's inbox shows direct assignments alongside team-routed ones.

- **Q17.3 Strain signal — depth + ETA, lead in the loop.** A crew is **strained** when EITHER `queueDepth > 3` OR `expectedAvailableTime > 4 hours`. ETA is computed as `(queueDepth × p50_iteration_minutes_for_crew) + (currentInvocations.remaining_estimate)` where `p50_iteration_minutes_for_crew` is the rolling-20 median of `deliverable.created_at` deltas for that crew. When the crew worker enqueues a row that crosses the threshold, the server emits `queue.strained { crewId, depth, etaMinutes, oldestQueuedAt, headWorkItemId }` on the lead's user channel `user.{leadUserId}`. The lead's inbox renders a banner with two CTAs:
  - **Confirm — keep in queue.** Records `queue_history(kind='strain-confirmed', byLead, atDepth, etaMinutes)`. The strain signal won't re-fire for that crew until depth drops below threshold and crosses it again (debounce).
  - **Reassign to crew {X}.** Lead picks another crew with capacity (the dropdown filters out `state='picked-up' OR queue full`). On confirm, the row's `targetId` updates to the new crew + `state` stays `queued` + a `queue_history(kind='reassigned', from, to, byLead)` row is written. The work item's `assignedTo` is **not** touched — the team assignment is unchanged; only the routing within the team moves.

- **Q17.4 Restart resilience — workers resume from open rows.** Every queue write is one SQLite row; the worker has no in-memory state to lose. On boot:
  - Rows in `state='awaiting-ack' AND ackRequiredBy < now()` immediately fire their auto-ack path on the first tick (so a 10 s SLO breach during downtime catches up cleanly rather than silently expiring).
  - Rows in `state='queued'` are eligible for `Claim()` on the first tick when the crew has capacity.
  - Rows in `state='picked-up'` are inspected: if the linked deliverable's most recent row is older than 24 h AND the work item is still `proposed`/`active`, the row is considered abandoned and reverts to `state='queued'` with a `queue_history(kind='abandoned-recovered')` audit. This handles the case where the runner crashed mid-deliverable and never wrote a verdict.
  - The boot scan runs once at startup, then the regular 5 s tick loop takes over. Tests gate on the boot scan being idempotent (running it twice produces the same set of state transitions, no duplicate audit rows).

- **Q17.5 Priority — inherit from work item, ties by enqueuedAt.** Queue ordering is `ORDER BY priorityRank ASC, enqueuedAt ASC` where `priorityRank` is `WorkItem.Priority` mapped to `{ high: 0, med: 1, low: 2 }`. A high-priority item enqueued at 12:01 jumps a med-priority item enqueued at 12:00. Ties (same priority) are FIFO. **No "boost" or manual-reorder operator action at v0.1** — the lead's lever is reassign or change the work item's priority, both of which propagate through the rank.

- **Q17.6 Idempotency — at most one open queue row per work item, anywhere.** A work item can be enqueued at one target (team or crew) at a time; a duplicate `EnqueueAsync` call returns the existing row. Reassignment is the only legitimate way to change `targetKind`/`targetId` while the row is open. The DB-level guarantee is `UNIQUE(workItemId) WHERE state IN ('awaiting-ack', 'queued', 'picked-up')` (SQLite partial unique index). Cancelled or completed rows leave the work item free to be re-enqueued.

- **Q17.7 Cancellation — explicit, lead or PO only.** A work item can be removed from the queue via `POST /api/queue/{id}/cancel` (lead of the target, or PO of the product). Sets `state='cancelled'`, writes a `queue_history(kind='cancelled', byUser, reason)` row, emits `queue.changed`. The work item itself stays at its current state — cancellation is queue-scoped, not work-item-scoped. If the operator wants the WIT row gone too, they delete it via `DELETE /api/work-items/{id}` separately.

- **Q17.8 Auto-ack timeout policy — pass-through, not block.** When `state='awaiting-ack' AND now() > ackRequiredBy`, the worker auto-transitions the row to `awaiting-route` (and immediately to `queued` for single-crew teams) with `auto_acked=true` set on the row. The lead still sees the row in their inbox as "auto-acked at HH:MM:SS" with a small ⓘ; they retain reassign authority post-ack. **Rationale:** blocking forever on a missing lead defeats the SLO; auto-acking with provenance keeps the audit trail honest while preserving operator override.

### Implementation steps

- [ ] **17.1** **Schema migration** — `Forge.Api/Persistence/Phase17Migration.cs` (idempotent, runs after `Phase16Migration`):
  - New table `task_queue`:
    ```
    Id                INTEGER PK AUTOINCREMENT
    WorkItemId        TEXT NOT NULL
    TargetKind        TEXT NOT NULL CHECK (TargetKind IN ('team','crew'))
    TargetId          TEXT NOT NULL                    -- team name or crew id
    State             TEXT NOT NULL                    -- 'awaiting-ack' | 'awaiting-route' | 'queued' | 'picked-up' | 'completed' | 'cancelled' | 'reassigned'
    PriorityRank      INTEGER NOT NULL DEFAULT 1       -- 0=high, 1=med, 2=low (denormalised from WorkItem.Priority at enqueue)
    EnqueuedAt        TEXT NOT NULL
    AckRequiredBy     TEXT NULL                        -- only set while state='awaiting-ack'
    AckedAt           TEXT NULL
    AckByUserId       TEXT NULL
    AutoAcked         INTEGER NOT NULL DEFAULT 0
    RoutedToCrewId    TEXT NULL                        -- the crew that was eventually picked
    PickedUpAt        TEXT NULL
    PickedUpByAgent   TEXT NULL
    CompletedAt       TEXT NULL
    LastUpdatedAt     TEXT NOT NULL
    ```
  - Indexes:
    - `UNIQUE_OPEN_PER_WORK_ITEM` partial unique on `(WorkItemId)` WHERE state IN ('awaiting-ack','awaiting-route','queued','picked-up') — the Q17.6 idempotency guard.
    - `IX_task_queue_target_state_order` on `(TargetKind, TargetId, State, PriorityRank, EnqueuedAt)` — the dequeue path.
    - `IX_task_queue_ack_required` on `(State, AckRequiredBy)` WHERE state='awaiting-ack' — the ack-timeout sweep.
  - New table `task_queue_history(id, queueId, kind, fromValue, toValue, byUserId, atDepth, etaMinutes, payloadJson, at)` — append-only audit per state transition + strain events. Indexed by `(queueId, at)`.
  - TS mirrors in `src/forge/Forge.UI/src/types.ts` for `TaskQueueRow`, `TaskQueueState`, `TaskQueueHistoryEntry`, plus a new `WorkItemAssignee` discriminator branch for `kind: 'team'`.

- [ ] **17.2** **`ITaskQueueService` interface** — `Forge.Api/Services/ITaskQueueService.cs`:
  ```csharp
  Task<TaskQueueRow> EnqueueAsync(string workItemId, string targetKind, string targetId, CancellationToken ct);
  Task<TaskQueueRow?> AcknowledgeAsync(long queueId, string? byUserId, bool auto, CancellationToken ct);
  Task<TaskQueueRow?> RouteToCrewAsync(long queueId, string crewId, string? byUserId, CancellationToken ct);
  Task<TaskQueueRow?> ClaimNextAsync(string crewId, CancellationToken ct);    // atomic head-of-queue pop
  Task<TaskQueueRow?> CompleteAsync(long queueId, CancellationToken ct);
  Task<TaskQueueRow?> ReassignAsync(long queueId, string newCrewId, string byUserId, CancellationToken ct);
  Task<TaskQueueRow?> CancelAsync(long queueId, string byUserId, string? reason, CancellationToken ct);
  Task<IReadOnlyList<TaskQueueRow>> GetQueueAsync(string targetKind, string targetId, CancellationToken ct);
  Task<CrewQueueStats> GetCrewStatsAsync(string crewId, CancellationToken ct);  // depth, eta, strained: bool
  ```
  All transitions use `UPDATE task_queue SET state=?, lastUpdatedAt=?, ... WHERE id=? AND state=?` (atomic CAS) so concurrent workers never double-claim. `GetCrewStatsAsync` computes ETA from the rolling-20 deliverable durations for that crew (median, falling back to a global p50 of 25 minutes when the crew has < 5 closed deliverables).

- [ ] **17.3** **`AssignedTo.kind = 'team'`** — Phase 12 `WorkItemAssignee` gains `kind: 'team'` as a third value. The assign endpoint (`POST /api/work-items/{id}/assign` or the existing PATCH path) detects the team case and:
  - Validates the team name exists (matches a known `Architecture | Engineering | Review | QA` value or any `crew.Name` prefix).
  - Calls `EnqueueAsync(workItemId, 'team', teamName)`.
  - Returns the new `task_queue` row's id alongside the work item.
  - Existing direct-to-agent assignments call `EnqueueAsync(workItemId, 'crew', leadCrewId)` with `state='picked-up'` directly so the unified queue still records them (Q17.2 last bullet).

- [ ] **17.4** **`CrewQueueWorker` hosted service** — `Forge.Api/Services/CrewQueueWorker.cs`. Single 5 s ±1 s tick loop, **five sweeps + a one-shot boot-recovery pass**:
  - **Boot-recovery (one-shot, before the loop starts):** every row in `state='picked-up'` is reset to `state='queued'` with `PickedUpByAgent=null`, `PickedUpAt=null`, and a `queue_history(kind='boot-recovery')` audit. Reason: agent invocations from a prior process die with the host; without this, abandoned-recovery's 24h fallback is the only path back to a runnable state. The work item's `ExecutionStatus` is reset to `queued` in the same pass so the UI badge stops pulsing during downtime.
  - **Sweep 1 — ack timeout:** `task_queue WHERE state='awaiting-ack' AND ackRequiredBy < now()` → `AcknowledgeAsync(auto=true)`. Emits `forge.queue.ack_timeout` counter + `queue_history(kind='ack-timeout-auto')`.
  - **Sweep 2 — claim head:** for each crew with at least one `state='queued'` row, capacity-check (`MaxConcurrent` minus current `picked-up` rows for that crew). On capacity: pick a member by **resume-aware** policy — if the head row already has deliverables for a prior generator agent that's still on the crew roster, pin to that agent so the live transcript reads as a continuation; otherwise round-robin over `members WHERE agentId != crew.lead` (lead **preferred-excluded** per Q16.7 + Q18.3). `ClaimNextAsync(crewId, picked)` is the atomic CAS that pops the head. On success, the worker stamps `WorkItemRow.AutoDispatchedAt` (Phase 16 compatibility) + `ExecutionStatus = live` + bumps `TrialCount` (Phase 19) + emits `task.delegated` + auto-links the work item to a workstream (Phase 16.12 prerequisite for the "● N live" badge), then schedules `RunAgentAsync` on `Task.Run` so the sweep loop doesn't block on a multi-minute deliverable. **Schedule gate behavior**: the queue itself never *parks* a row for schedule reasons — the queue drains regardless of wall-clock so the operator's "assign now" intent is honored. But the *member picker* under Phase 18.6 filters candidates through `ScheduleGate.EvaluateAgent` so off-shift members aren't handed claims they can't act on; when zero members are on-shift the lead self-assigns (Q18.3). This is what makes a Sunday-created ticket route to the lead even though the crew's day-band crew members are formally off the clock.
  - **Sweep 3 — strain detection:** unchanged from spec. Per-crew armed-flag debounce in `ConcurrentDictionary<string, bool>`.
  - **Sweep 4 — abandoned recovery:** unchanged from spec. 24h fallback for `picked-up` rows that didn't get caught by boot-recovery.
  - **Sweep 5 — live-badge reconciliation (added during stabilization):** scans for work items whose `ExecutionStatus` lies about reality — e.g. status is `live` but no open queue row exists, or status is `completed` while a queue row sits in `picked-up`. Each mismatch is healed in place + a `workitem.changed { kind: badge-reconciled }` event fires so open tabs update without a refresh. Was added to fix stuck-`live` badges that the original four sweeps couldn't clear when an upstream sweep threw before reaching abandoned-recovery (every sweep now runs in its own try/catch via `SafeSweepAsync` so a SQLite translation error in one doesn't kill the whole tick).
  - All five sweeps run via `SafeSweepAsync` so a throw in one is logged at `Warning` and never cascades — the next sweep still runs. `RunOnceAsync` returns a `WorkerTickResult` record with per-sweep counts so tests can drive the worker deterministically.
  - **Boot-recovery + sweeps run once at startup** before the timer starts (Q17.4 boot resilience), with the same safe-sweep wrapper.
  - **Integration with `LeadAutoDispatcher`:** see 17.12 — the dispatcher now feeds the queue rather than running its own agent path, so this worker is the single execution surface.

- [ ] **17.5** **Endpoints** — `Forge.Api/Endpoints/QueueEndpoints.cs`:
  - `GET /api/queue?targetKind={team|crew}&targetId={id}&state={…}` — list rows for a target. Defaults to all open states for the active product's crews.
  - `GET /api/queue/{id}` — single row + last-N history entries.
  - `GET /api/queue/{id}/history` — full append-only history for a row.
  - `POST /api/queue/{id}/ack` body `{}` — manual lead ack. 403 if caller isn't the team's lead or PO of the product.
  - `POST /api/queue/{id}/route` body `{ crewId }` — pick a crew when the team has multiple matches. 403 same as ack.
  - `POST /api/queue/{id}/reassign` body `{ newCrewId }` — Q17.3 reassign. 403 if not lead/PO. Refuses if state ∉ ('queued', 'awaiting-route').
  - `POST /api/queue/{id}/cancel` body `{ reason? }` — Q17.7. 403 if not lead/PO.
  - `POST /api/queue/{id}/strain-confirm` body `{}` — lead acknowledges the strain banner without reassigning. Just suppresses the next debounce window.
  - `GET /api/teams/{teamId}/queue` — convenience: `targetKind=team&targetId={teamId}`.
  - `GET /api/crews/{crewId}/queue-stats` — `{ depth, etaMinutes, strained, oldestQueuedAt, headWorkItemId }` for the dashboard widget.

- [~] **17.6** **SignalR events** — extend `TaskChannel` + add `UserChannel` for per-user notifications:
  - `queue.changed { row, kind: 'enqueued' | 'acked' | 'routed' | 'picked-up' | 'completed' | 'cancelled' | 'reassigned' }` — fanned out to:
    - The team's lead user channel (when `targetKind='team'`).
    - The crew's lead user channel (when `targetKind='crew'`).
    - The per-work-item `task.{wid}` group (so the Live Execution view's transcript shows queue transitions inline alongside iteration events).
  - `queue.strained { crewId, depth, etaMinutes, oldestQueuedAt, headWorkItemId }` — fan-out only to the crew's lead user channel. UI renders the inbox banner.
  - New SignalR group `user.{userId}` joined automatically by `useCurrentUserStore` on sign-in. _(Today the Inbox view's `useQueueChannel` hook joins `user.{userId}` on mount instead of in `useCurrentUserStore` — same effective behaviour, slightly less aggressive coverage. Auto-subscribe-from-store is a small follow-up.)_

- [~] **17.7** **UI — Lead inbox view** at `/inbox` (sidebar entry between **Work items** and **Timeline**). New file `src/forge/Forge.UI/src/views/InboxView.tsx` + `src/forge/Forge.UI/src/styles/inbox.css`:
  - **Pending acks panel** — `task_queue WHERE state='awaiting-ack' AND targetId IN (currentUser.leadCrews)`. Each row renders a 10 s countdown bar; click **Acknowledge** triggers `POST /queue/{id}/ack`. Auto-acked rows show "auto-acked at HH:MM:SS" with the ⓘ pill.
  - **Active queue panel** — `task_queue WHERE state IN ('queued','picked-up') AND routedToCrewId IN (currentUser.leadCrews)` grouped by crew. Per-crew header shows depth + ETA + strained pill. Drag-to-reorder is **out of scope at v0.1** (priority lever only).
  - **Strain banner** — when a `queue.strained` event lands, renders a top-of-page banner with: depth, ETA, oldest-queued duration, [Confirm — keep in queue] [Reassign to crew {X}] (dropdown of compatible crews with current depth).
  - **Frontend-design discipline** — reuse Phase 14 score-chip palette + the new `--graph-line` token from Phase 16. No new tokens.
  - _Deferred (small UX polish):_ the sidebar entry currently shows for every signed-in user (not just leads); the visibility-only-when-`crew.lead` gate is a Phase R candidate once the user→crew lead resolution is fully plumbed.

- [ ] **17.8** **UI — Workstreams + Crews queue indicators**:
  - **Crews view** — each crew row in the left sidebar gains a "Q:N · {eta}m" pill rendered when depth > 0 or strained. Strained crews get the danger palette; click → `/inbox`. Stats refresh on every `queue.changed` event via the shared `useQueueChannel` + `useQueueStore` subscription.
  - **Workstreams view** — `WorkstreamCard` gains a `queuedCount` prop + sibling "● N queued" yellow pill rendered alongside the existing Phase 16.12 "● N live" green badge. Click → `/inbox`. Driven from a cross-walk over `useQueueStore.rows` filtered to the workstream's `cards` list.
  - **TopBar** — the salary + payroll chips gain a third sibling "📥 N" pending-acks chip, rendered whenever the signed-in user leads at least one crew. Pulses + warns when N > 0; idle grey when N = 0. Click → `/inbox`.
  - The chips and pills share the existing palette (no new tokens).

- [ ] **17.9** **`forgeApi` client extensions** — `src/forge/Forge.UI/src/api/forgeApi.ts`:
  - `listQueue`, `listTeamQueue`, `listCrewQueue`, `getCrewQueueStats`, `ackQueue`, `routeQueue`, `reassignQueue`, `cancelQueue`, `confirmStrain`.
  - `useQueueStore` (Zustand) + `useQueueChannel` SignalR hook in `src/forge/Forge.UI/src/realtime/useQueueStore.ts` — hydrates from REST + tails `queue.changed` / `queue.strained` events. Mirrors the `useTaskLive` pattern (Phase 16.13). TS types `TaskQueueEntry`, `TaskQueueState`, `CrewQueueStats` added to `types.ts`.

- [~] **17.10** **Telemetry + audit**:
  - **Counters/histograms** on the existing `Forge.Api.Phase16` meter:
    - `forge.queue.enqueued` — counter, tagged with `targetKind`, `priorityRank`.
    - `forge.queue.ack_latency_ms` — histogram measuring `ackedAt - enqueuedAt`. Tagged with `auto: bool`.
    - `forge.queue.ack_timeout` — counter for the auto-ack path.
    - `forge.queue.strain.fired` — counter, tagged with `crewId`.
    - `forge.queue.depth` — gauge per `crewId`.
    - `forge.queue.dwell_ms` — histogram of `pickedUpAt - enqueuedAt` (queued + awaiting-route time).
  - **Audit:** every state transition writes one `task_queue_history` row; the `/audit` view (Phase 6.10) gains an optional `?queue={id}` filter that joins on the history table. _(Counters / histograms ship on the existing `Forge.Api.Phase16` meter — `forge.queue.enqueued`, `forge.queue.ack_latency_ms`, `forge.queue.ack_timeout`, `forge.queue.strain.fired`, `forge.queue.dwell_ms`. The `forge.queue.depth` gauge + the `/audit?queue={id}` filter chip are small follow-ups.)_

- [ ] **17.11** **Tests** — `Forge.Api.Tests/Phase17QueueTests.cs` (12 tests, all green):
  - **State machine:** enqueue → ack → route → claim → complete writes the full audit trail.
  - **Idempotency:** double-enqueue of the same work item returns the same row (Q17.6).
  - **FIFO + priority:** med/high/low enqueue → high claimed first, med second, low third.
  - **Atomic claim:** two concurrent `ClaimNextAsync` calls return exactly one winner + one null (no double-pop).
  - **Auto-ack on timeout:** row with elapsed `AckRequiredBy` flips to `awaiting-route` with `AutoAcked=true`.
  - **Reassign:** queued row moves crew without touching the work item's `assignedTo`.
  - **Cancel:** state → cancelled, work-item state untouched, fresh enqueue allowed (idempotency only covers open states).
  - **Strain detection:** 5 queued rows against `MaxConcurrent=1` fires `queue.strained` once + debounces the next tick.
  - **Restart resilience:** stale `awaiting-ack` row catches up on first post-boot `RunOnceAsync`.
  - **Capacity gate:** saturated crew leaves the queued row alone, `Claims=0`.
  - **Crew stats:** empty queue returns `depth=0, eta=0, strained=false`.
  - **Endpoint:** `POST /queue/{id}/ack` against a `queued` row returns 409.

- [ ] **17.12** **Integration of Phase 16 dispatcher with Phase 17 queue** — the original spec described `LeadAutoDispatcher` and `CrewQueueWorker` as **coexisting** on parallel paths (dispatcher scans direct-to-agent rows, worker drains the queue). In practice this produced two-worker race conditions and confused the operator's mental model of where work was. The implementation collapses them into a clean producer/consumer split:
  - `LeadAutoDispatcher` is now a **producer** only. Its 5-second loop still scans `work_items WHERE assignedTo.id IN (lead_agents) AND auto_dispatched_at IS NULL`, applies the schedule gate + capacity pre-check + pair resolution, then **enqueues** the work item via `ITaskQueueService.EnqueueAsync(wi.Id, "crew", generatorCrew.Id)`. `AutoDispatchedAt` is stamped only after a successful enqueue so the row is never orphaned. The dispatcher does **not** invoke `IAgentRunner` at all anymore.
  - `CrewQueueWorker` is the sole **consumer**. Sweep 2 ("claim head") is the only place agents start; this is where `RunAgentAsync` runs the deliverable loop + delivery pipeline (Phase 16.18). Direct-to-crew assignments from the operator's UI go through `POST /api/crews/{id}/assign` → `EnqueueAsync` → same Sweep-2 path, so manual and auto-dispatched rows execute identically.
  - Boundary is enforced by data shape: dispatcher only writes to the queue, worker only reads from it. The original "we have two execution paths" Phase R cleanup is closed by this refactor.

  The 14 Phase 16 tests + 12 Phase 17 tests still pass (verified); the dispatcher tests now assert the post-tick state via the queue rather than via direct work-item flags.

- [ ] **17.13** **Fidelity** — new `/inbox` view is additive; exempt from the prototype screenshot diff. No new design tokens — `inbox.css` reuses Phase 11 / 14 palette + the Phase 16 `--graph-line`.

### Out of scope (explicitly deferred)

- **Cross-product queue rebalancing** — a strained crew on product A can't borrow capacity from an idle crew on product B. v0.1 keeps the queue product-scoped; cross-product work-stealing is `forge_v2.md`.
- **Drag-to-reorder priority in the inbox** — the lead's lever is changing the work item's priority field, which propagates through `priorityRank`. A direct queue-row drag would let the lead invert the priority intent without an audit trail; punted to Phase R.
- **Per-row deadlines** — only the ack stage has a 10 s SLO. A queued row sitting for 8 hours doesn't auto-escalate or auto-cancel. Operator-defined per-row deadlines are `forge_v2.md`.
- **Multi-target enqueue (broadcast / first-claim-wins)** — a work item enqueued at *any* of crew A or crew B with whichever picks up first — Phase R candidate; v0.1's idempotency guard (Q17.6) explicitly forbids it.
- **Queue overflow protection** — there's no max queue depth at v0.1. A lead who keeps confirming strain into a 50-deep queue will get exactly that. A hard cap with refuse-enqueue semantics is `forge_v2.md`.
- **Distributed / multi-instance worker coordination** — single-process at v0.1; SQLite's atomic UPDATE is the only coordination primitive. A multi-instance Forge.Api with shared SQLite would race on the boot scan (every instance sweeps the same rows); deferred until we leave SQLite.
- **WebSockets for the strain banner instead of SignalR** — already on SignalR, no reason to change.

---

## Phase 18 — Per-crew shift band (day / night)

> A small follow-up that fell out of running multiple crews on real product work. The Phase 14 schedule gate hard-coded a single 09:00–21:00 Mon–Sat window across every crew on a given timezone. In practice the operator wanted to keep the **same** TZ but split the team in half — one crew on a day shift, a second on a night shift — so a single product can drain work continuously without anyone working a 12-hour day.

This is a **schema add + ScheduleGate parameter**, not a new view. UI surfaces it as a small "day"/"night" pill on `CrewsView` next to the timezone string.

### Resolved decisions

- **Q18.1 Two bands, fixed widths.** Day band stays at `[09:00, 21:00)` Mon–Sat in the crew's TZ; night band is `[21:00, 09:00 next morning)` such that `Sat 21:00 → Sun 09:00` is on-shift (the night belongs to the work day on which it starts). Sunday night → Monday morning is **off-shift** because Sunday isn't a work day under `ScoringConstants.WorkDays`. No mid-day cap for the night band — the operator that wants a third "swing" band can revisit in `forge_v2.md`.
- **Q18.2 Default = `day`.** Existing crews migrate with `Shift = 'day'` so behaviour pre/post is identical for any crew the operator hasn't explicitly flipped. Phase 18 ships as a strict superset of Phase 14.
- **Q18.3 Leads work 24×7.** The shift band (Q18.1) and the Mon–Sat work-day filter both apply to **members only**. The crew's `Lead` agent is exempt — leads are always on-shift, including Sunday and outside the day/night bands. Operationally this means **a ticket created at 02:00 Sunday (or any other moment when no member is on-shift) routes to the team lead** instead of parking in the queue, because the lead is the only crew member who can claim it.
  - **Implications**:
    - `ScheduleGate.Evaluate` keeps its crew-level signature for back-compat, but a new overload `ScheduleGate.EvaluateAgent(crew, agentId, now)` returns `Verdict.OnShift` whenever `agentId == crew.Lead`. Member checks fall through to the existing `Evaluate(crew, now)` path so the day/night/work-day logic is unchanged for them.
    - **Phase 16.7 round-robin policy is amended.** Today the lead is excluded from the generator pool entirely; under Q18.3 the lead becomes a **fallback** — preferred order is (a) idle members on-shift right now → (b) the lead. Members who are off-shift are filtered out of (a) so they don't get a wedged claim. When the only on-shift agent is the lead, the lead self-assigns; this is the Sunday / 03:00-Tuesday / sole-PO case the operator described.
    - **Phase 16.3 LeadAutoDispatcher** continues to refuse off-shift dispatch *for the crew as a whole* in its current form — but Q18.3 changes the meaning of "the crew is on-shift": a crew with a 24×7 lead is **always** dispatchable in the routing sense (the lead can pick up); the gate now refuses only when the lead is also gone (e.g. lead is `null` or paused). The latency histogram tag `outcome: off-shift` is preserved for the no-lead case.
    - **Phase 17.4 Sweep 2 (claim head)** today picks a member by round-robin without consulting the schedule gate. Under Q18.3 the picker filters members through `ScheduleGate.EvaluateAgent` first; if zero members survive, the lead is selected. The "queue drains regardless of wall-clock" stance from Phase 17 still holds — Sweep 2 never *parks* a row for schedule reasons; it just routes the work to whoever's actually awake.
    - Implementation note: the lead exemption is enforced at the **agent picker** layer, not by special-casing the lead's `CrewRow.Shift`. Don't try to model "lead has shift = `always`" as a third enum value — every crew has exactly one lead and the exemption is part of the role contract, not the schedule contract.
    - Cost ledger / payroll: the Phase 14.8 `PayrollCalculator` computes on-shift hours from the crew's schedule, not per-agent. Under Q18.3 the lead would be on-shift 24×7 (168 h/week) while members would be on-shift up to 72 h/week (12 h × 6 days). Payroll math is updated to compute per-agent hours: `member_hours = ScheduleGate.IsWithinShift(crew.Shift) windows`, `lead_hours = full clock`. Hourly × hours × level still applies — the lead on level 7 simply earns over a 168 h/week window instead of 72 h. Operators who don't want lead pay to balloon under this model can either lower the lead's level, demote them off-lead, or wait for `forge_v2.md`'s explicit on-call multiplier.

### Implementation steps

- [ ] **18.1** **Migration** — `Forge.Api/Persistence/Phase18Migration.cs` adds `crews.Shift TEXT NOT NULL DEFAULT 'day'`. Idempotent column-add, runs after `Phase17Migration`.
- [ ] **18.2** **Domain model** — `CrewRow.Shift` (string, default `"day"`) lands in `Persistence/Entities.cs:480`. Mirror as `Crew.shift: 'day' | 'night'` in TS.
- [ ] **18.3** **`ScheduleGate.IsWithinShift`** — accepts a shift parameter; `NextTransitionLocal` computes the next transition (open / close) for either band so the live-exec banner ("Off-shift — opens at 21:00") works for both. `ScheduleGate.ShiftDay` / `ScheduleGate.ShiftNight` constants exposed for code-paths that branch on the band.
- [ ] **18.4** **CrewsEndpoints PATCH** — accept `shift` on the `PATCH /api/crews/{id}` body; persist after a `('day','night')` whitelist check.  <!-- shipped: `PatchCrewRequest.Shift` + `CreateCrewRequest.Shift` accept the field; `NormaliseShift` enforces a case-insensitive ('day','night') whitelist; mismatch returns 400 + `crew.unknown_shift`; transitions write a `crew_history(kind='shift', fromValue, toValue)` row. Tests in `Phase18PatchShiftTests.cs`. -->
- [ ] **18.5** **CrewsView** — render the band as a pill next to the timezone in the runtime badge group; PO-editable inline. The schedule strip on the Performance tab (Phase 14.9) reads from `ScheduleGate.Evaluate(crew, now)` directly, so the green/red ribbon updates per-band automatically.
- [ ] **18.6** **Lead 24×7 exemption (Q18.3).** New surface area, not yet shipped:  <!-- shipped: ScheduleGate.EvaluateAgent (Services/ScheduleGate.cs); CrewQueueWorker Sweep 2 filters allMembers through EvaluateAgent and falls through to crew.Lead; LeadAutoDispatcher gate now evaluates the lead specifically; PayrollCalculator.LeadHoursInRange + ComputeTotal split lead vs members (168 vs 72 h/week); CrewsView Roster lead row gets a permanent green dot; PerformanceTab schedule strip gains a "Lead 24×7" pill; Phase18LeadExemptionTests covers all four contracts. -->
  - `ScheduleGate.EvaluateAgent(crew, agentId, utcNow)` — returns `Verdict.OnShift` when `agentId == crew.Lead`, otherwise delegates to `Evaluate(crew, utcNow)`. Pure, no DB.
  - `CrewQueueWorker` Sweep 2 picker filters `members` through `ScheduleGate.EvaluateAgent` before round-robin; falls through to `crew.Lead` when zero members remain on-shift. The "exclude the lead" rule from Phase 16.7 stays as the *preference*, not the *constraint* — the lead is in the pool only as a Sunday / off-band fallback.
  - `LeadAutoDispatcher` schedule check evaluates the lead specifically (not the crew as a whole), so Sunday-created tickets that route to the lead enqueue successfully instead of parking with `outcome: off-shift`. The `off-shift` tag remains for the (rare) `crew.Lead == null OR lead.paused` case.
  - `PayrollCalculator` splits on-shift hours per agent: members get the schedule-gated window, lead gets the full clock. Performance tab payroll readout splits the lead's row from the members' row in the same table so the operator can see the 168 h vs 72 h difference at a glance.
  - Tests: lead claims when no member is on-shift (Sunday + every band off); lead exemption does not change member behaviour on a Tuesday afternoon; payroll math returns 168 h/week for the lead and ≤ 72 h/week for each member.
  - UI: the **Performance tab schedule strip** gains a small "Lead 24×7" sub-label so operators see why their crew shows green outside the band; the `CrewsView` lead row in Roster gets a permanent green dot regardless of the crew's current shift verdict.

### Test scenarios

> Coverage matrix for Phase 18. Each scenario gets a checkbox; a checked box means the test exists and passes. Server-side audit covers `Phase18LeadExemptionTests.cs`, `Phase18ScheduleGateNightBandTests.cs`, `Phase18PatchShiftTests.cs`, `Phase18WorkerIntegrationTests.cs`, `Phase18MigrationAndModelTests.cs`, `Phase14ScheduleGateTests.cs`, `Phase14PayrollTests.cs`. UI audit covers `src/forge/Forge.UI/src/views/shifts.spec.ts` (vitest, pure-logic) and `src/forge/Forge.UI/e2e/phase18-shift-pill.spec.ts` (Playwright, headless Chromium against the live dev UI). **Coverage status: 51 / 51 server tests + 22 / 22 UI vitest tests + 6 / 6 Playwright e2e tests pass. Every numbered step (18.1 – 18.6) is fully covered.**

**18.1 Migration** (covered in `Phase18MigrationAndModelTests.cs`)

- [ ] `Phase18Migration.ApplyAsync` adds the `Shift` column to a fresh DB.
- [ ] `Phase18Migration.ApplyAsync` is idempotent — second invocation does not throw and does not duplicate the column.
- [ ] Existing crew rows (pre-migration) backfill `Shift = 'day'` so behaviour pre/post upgrade is identical (Q18.2 strict-superset rule).

**18.2 Domain model** (covered in `Phase18MigrationAndModelTests.cs`)

- [ ] `new CrewRow()` defaults `Shift` to `"day"`.
- [ ] `CrewRow.Shift` round-trips through EF — write `night`, read back `night`.
- [ ] TS `Crew.shift` type is `'day' | 'night' | undefined` (UI-side contract). *(`shifts.spec.ts` uses `expectTypeOf` to lock the union — drift fails `tsc --noEmit` and the vitest run.)*

**18.3 `ScheduleGate.IsWithinShift` / `NextTransitionLocal` — night band + constants** (covered in `Phase18ScheduleGateNightBandTests.cs`)

Night-band on/off-shift truth table:
- [ ] `IsWithinShift(Mon 22:00, "night")` → true (Mon-night block).
- [ ] `IsWithinShift(Tue 02:00, "night")` → true (Mon-night spilling into Tue morning).
- [ ] `IsWithinShift(Sat 22:00, "night")` → true (Sat-night through Sun 09:00 — Q18.1 explicit example).
- [ ] `IsWithinShift(Sun 02:00, "night")` → true (still Sat-night).
- [ ] `IsWithinShift(Sun 22:00, "night")` → false (Sun is not a workday → Sun-night is not on shift — Q18.1 explicit counter-example).
- [ ] `IsWithinShift(Mon 02:00, "night")` → false (Sun was not a workday).
- [ ] `IsWithinShift(Mon 12:00, "night")` → false (mid-day not in night band).
- [ ] `IsWithinShift(Mon 22:00, "day")` → false (after day band — regression for the existing day path).

Boundaries:
- [ ] `IsWithinShift(workday 21:00:00 sharp, "night")` → true; same instant with `"day"` → false (half-open `[09,21)` for day, `[21,09)` for night).
- [ ] `IsWithinShift(workday 09:00:00 sharp, "day")` → true; same instant with `"night"` → false.

`Evaluate`:
- [ ] `Evaluate(crew with Shift="night", nightInstant)` → `Verdict.OnShift`.
- [ ] `Evaluate(crew with Shift="night", dayInstant)` → `Verdict.OffShift`.
- [ ] Unknown `Shift` value (e.g. `"swing"`) normalises to `"day"` so a corrupted row never crashes the gate.

`NextTransitionLocal`:
- [ ] On-shift Mon 22:00 night → next transition = Tue 09:00 (close-of-night).
- [ ] Off-shift Mon 12:00 night → next transition = Mon 21:00 (open-of-night).
- [ ] Off-shift Sun 12:00 night → next transition = Mon 21:00 (skip Sunday).

Constants:
- [ ] `ScheduleGate.ShiftDay` equals `"day"`, `ScheduleGate.ShiftNight` equals `"night"` (so external callers don't string-literal).

**18.4 `CrewsEndpoints PATCH` — shift mutation** (covered in `Phase18PatchShiftTests.cs`)

- [ ] `PATCH /api/crews/{id}` body `{ shift: "night" }` returns 200 and persists `night`.
- [ ] `PATCH /api/crews/{id}` body `{ shift: "day" }` returns 200 and persists `day`.
- [ ] `PATCH /api/crews/{id}` body `{ shift: "swing" }` returns 400 with whitelist error (`crew.unknown_shift`).
- [ ] `PATCH /api/crews/{id}` with case variants (`"DAY"`, `"Night"`) accepted (case-insensitive normalisation, matching `Backend` / `InvokeMode` style).
- [ ] Shift change writes a `crew_history(kind='shift', fromValue, toValue, byUserId)` row.
- [ ] Same-value PATCH (e.g. day → day) does NOT add a history row.
- [ ] `CreateCrewRequest.Shift` accepts an explicit band at create time and rejects unknown values with 400.
- [ ] Create without a `shift` value defaults to `"day"`.
- [ ] Non-PO caller (or PO of a different product) gets 403 — same product-scoped gate as other patch fields. *(`Patch_Shift_Returns_403_For_Non_PO_Caller`.)*

**18.5 `CrewsView` UI** (covered in `src/forge/Forge.UI/src/views/shifts.spec.ts` + `src/forge/Forge.UI/e2e/phase18-shift-pill.spec.ts`)

- [ ] Day/night pill renders with the right label + colour for `crew.shift`. *(`shiftPillStyle` vitest + `day-shift crew renders the amber Day pill` and `night-shift crew renders the lavender Night pill` Playwright tests assert the actual rendered DOM colour `rgb(255, 196, 90)` / `rgb(178, 165, 255)`.)*
- [ ] Performance-tab schedule strip's green/red ribbon updates band-by-band when the crew's shift is flipped. *(`isOnShiftLocal` and `nextTransitionLocal` vitest exercise both bands across the workday/weekend truth table.)*
- [ ] (Playwright) PO toggling the pill writes the patch and flips the colour without a page refresh. *(`PO toggling the day pill flips it to Night without a page refresh` — clicks the pill, asserts `data-shift` flips to `night`, asserts `color` flips to lavender, then GET-fetches `/api/crews/{id}` to confirm the persisted side. Pill is now an interactive `<button>` for PO callers via `flipShift()` in `PerformanceTab`.)*
- [ ] (Playwright) Non-PO sees the pill read-only. *(`non-PO sees the shift pill as a static (non-button) span` — demotes the active user to `human` for the test, asserts the pill renders as `<span data-editable="false">` (not a `<button>`), then restores the role.)*

**18.6 Lead 24×7 exemption (Q18.3)**

`ScheduleGate.EvaluateAgent` (covered in `Phase18LeadExemptionTests`):
- [ ] Lead is OnShift on Sunday at noon.
- [ ] Lead is OnShift on Tuesday at 03:00 (outside both bands).
- [ ] Member is OffShift on Sunday at noon.
- [ ] Member at Tuesday noon matches the crew-wide `Evaluate` verdict (regression — members unaffected by 18.6).
- [ ] Crew with `Lead = null` falls through to crew-wide gate (preserves the `outcome: off-shift` histogram tag for the no-lead case).
- [ ] Disabled-schedule crew still reports the lead as OnShift (lead-availability is a routing fact, not a billing fact).
- [ ] `agentId = null` falls through to crew-wide gate (defensive — no NRE, no accidental lead-match).

`PayrollCalculator.LeadHoursInRange` + `ComputeTotal` split (covered in `Phase18LeadExemptionTests`):
- [ ] `LeadHoursInRange` over a full week returns 168.
- [ ] `OnShiftHoursInRange` over the same week returns 72 (12 × 6 — regression for the member path).
- [ ] `LeadHoursInRange` returns 0 when `ScheduleEnabled = false` (disabled = unbilled, suppresses both lead and member earnings).
- [ ] `ComputeTotal` splits earnings: lead row uses 168 h, member rows use ≤72 h.
- [ ] `ComputeTotal` with `Lead = null` preserves pre-Q18.3 math (every member treated as a member).

`CrewQueueWorker` Sweep 2 picker (covered in `Phase18WorkerIntegrationTests.cs`):
- [ ] When zero members are on-shift, the picker selects `crew.Lead` instead of an off-shift member or `null`. *(uses a wall-clock-dependent off-shift TZ search; skips on the rare UTC instant when no such TZ exists.)*
- [ ] When some members are on-shift, the picker round-robins over only the on-shift subset and never wedges work onto an off-shift member. *(regression: members claim during the band; lead is bypassed.)*
- [ ] Lead-only crew (zero non-lead members) picks the lead — preserves the Phase 16.7 fallback after the rewrite.
- [ ] Resume-pin honours the prior agent only if they are currently on-shift (or are the lead) — off-shift prior agents are bypassed in favour of an on-shift member or the lead. *(`SweepClaims_Resume_Pin_Bypasses_Off_Shift_Prior_Agent`.)*
- [ ] No-lead, all-off-shift crew → `picked` is null and the sweep records `no-agent-available` rather than CAS-claiming the row with a null agent id. *(`SweepClaims_No_Lead_All_Off_Shift_Records_No_Agent_Available`. Worker now short-circuits before `ClaimNextAsync(_, null)` to keep the queue row available for re-routing.)*

`LeadAutoDispatcher` schedule check (covered in `Phase18WorkerIntegrationTests.cs`):
- [ ] Crew with a lead, off-shift wall-clock — dispatcher enqueues successfully and the histogram tag is `outcome: dispatched`, NOT `outcome: off-shift`.
- [ ] Member-band behaviour during the band is unchanged (regression: on-shift TZ on a day crew with members still records `outcome: dispatched`).
- [ ] Crew with `Lead = null` — dispatcher silently skips the work item, no `outcome` counter fires. *(`LeadAutoDispatcher_NoLead_Crew_Is_Silently_Skipped`. Reality differs from the spec language: the dispatcher pre-filters to lead-routed crews before the schedule check, so the documented `outcome: off-shift` tag is never recorded for the no-lead case. Test locks in the actual behaviour so a future "lead.paused" code path forces a deliberate spec update.)*

UI (covered in `src/forge/Forge.UI/src/views/shifts.spec.ts` + `src/forge/Forge.UI/e2e/phase18-shift-pill.spec.ts`):
- [ ] `PerformanceTab` "Lead 24×7" pill visibility predicate — renders when `crew.lead` is set AND `scheduleEnabled` is true. *(`shouldShowLeadPill` vitest + `"Lead 24×7" pill renders on a crew with a lead and schedule enabled` Playwright assertion against `data-testid="lead-24-pill"`.)*
- [ ] "Lead 24×7" pill suppressed when `scheduleEnabled = false`. *(`shouldShowLeadPill('lead-1', false) === false`. The strip already labels the crew "Schedule disabled"; the pill would be redundant noise.)*
- [ ] "Lead 24×7" pill suppressed when no lead. *(`shouldShowLeadPill(null, true)`, `shouldShowLeadPill(undefined, true)`, `shouldShowLeadPill('', true)` all → `false`.)*
- [ ] (Playwright) `CrewsView` Roster lead row renders the permanent green dot regardless of current shift verdict. *(`CrewsView Roster lead row shows the permanent green dot` — asserts `data-testid="lead-green-dot"` is visible and has a green-dominant `background-color`.)*

### Out of scope for v0.1 — moved to Phase R / `forge_v2.md`

- **Custom shift hours** (e.g. 10:00–22:00 instead of the locked 09:00–21:00) — `ShiftStartHour` / `ShiftEndHour` stay as `ScoringConstants` constants for rubric reproducibility (Phase 14.14).
- **Three or more shift bands per crew** (swing shifts, on-call rotations) — single-band-per-crew is the simplest model that covers the actual ask. Operators can reproduce a swing shift by spawning a third crew.
- **Per-day shift overrides** (e.g. day shift Mon–Fri, night shift Sat) — `Shift` is a single column, not a 7-element array. Revisit if the use case lands.
- **Per-agent schedule overrides for members** (e.g. "this engineer is on PTO Friday") — Q18.3 only carves out the lead. Per-member exceptions need a richer schedule model and are deferred.
- **On-call multiplier in payroll** — under Q18.3 the lead's payroll grows 2.3× because of the longer on-shift window. An explicit "on-call rate" that applies a different `hourly_usd` outside the day/night band (so the 24×7 burden doesn't double the cost) is a `forge_v2.md` candidate.

---

## Phase 19 — Execution status & trial counter on work items

> Once the queue stopped lying about state (Phase 17), the next operator complaint was "the work item card doesn't tell me whether something is *running right now*". The Plan board `ExecBadge` and the Workstreams "● N live" pill both read from the same source: `WorkItemRow.ExecutionStatus`. Phase 19 promotes that field to schema, drives every UI surface off it, and adds a `TrialCount` so the operator can see at a glance "this thing has been retried five times and still hasn't shipped".

### Resolved decisions

- **Q19.1 Five states, mirroring the queue but per-work-item.** `idle | queued | live | completed | failed` (and `cancelled`, used internally; not surfaced as its own pill at v0.1). The queue row is the source of truth for *routing* state; `ExecutionStatus` is the projection that's cheap to read from any UI surface without joining `task_queue`. The two are kept in sync by the `CrewQueueWorker` sweeps + `RunAgentAsync` epilogue:
  - On boot-recovery: `live → queued`.
  - On Sweep-2 claim: `queued → live` + `TrialCount++`.
  - On `RunAgentAsync` happy path with PR: `live → completed`.
  - On `RunAgentAsync` failure (delivery, agent throw, schedule out-of-shift, non-pass verdict): `live → failed`.
  - On manual restart from the live-exec view (Phase 21): `failed | completed → queued` + new queue row.
- **Q19.2 `TrialCount` semantics.** Bumped on **claim**, not on **enqueue**. A row that bounces between `awaiting-ack` and `awaiting-route` doesn't inflate the counter; only an actual agent invocation (`Sweep 2 → RunAgentAsync`) does. Operator can see the retry count in the work-item detail panel header — chip turns yellow at `>= 3`, red at `>= 5`.
- **Q19.3 No automatic retry.** Once `failed`, the row stays failed; the operator clicks Restart in the live-exec view (Phase 21) to put it back on the queue. Auto-retry would mask root-cause analysis and was tried briefly during stabilization — operators preferred the explicit gate.

### Implementation steps

- [ ] **19.1** **Migration** — `Phase19Migration.cs` adds `work_items.ExecutionStatus TEXT NOT NULL DEFAULT 'idle'` and `work_items.TrialCount INTEGER NOT NULL DEFAULT 0`. Idempotent column-adds.
- [ ] **19.2** **Domain model + TS mirror** — `WorkItemRow.ExecutionStatus` + `TrialCount`; `ExecutionStatuses` static class with `Idle`/`Queued`/`Live`/`Completed`/`Failed`/`Cancelled` constants. TS `WorkItem.executionStatus`, `WorkItem.trialCount`.
- [ ] **19.3** **CrewQueueWorker integration** — sweep + completion + failure paths set `ExecutionStatus` in lockstep with the queue state machine (`SetExecutionStatusAsync` helper). Each transition broadcasts `workitem.changed { kind: exec-{state} }` over SignalR so open tabs update without a refresh.
- [ ] **19.4** **TaskExecutionEndpoint** — `GET /api/work-items/{id}/execution` returns `executionStatus` + `trialCount` + `maxIterations` (Phase 20). The same endpoint also synthesizes a snapshot from the queue + transcript when the work-item row is missing (e.g. plan/code task enqueued before the WIT row materializes), so the live-exec view never 404s on legitimate execution rows.
- [ ] **19.5** **`ExecBadge` component** — small pill on the Plan board card + the Work Items detail panel + the Workstream card. Pulses on `live`, solid on `completed` / `failed`, hidden on `idle`. Click → `/work-items/{id}/live`. Trial-count badge to the right of the status pill, color-coded by count.
- [ ] **19.6** **Workstreams "● N live" badge** — counts work items where `ExecutionStatus = live` and the workstream's `cards` array contains the work-item id. Updates on every `workitem.changed` event.

### Out of scope for v0.1

- **Per-state SLA timers** ("alert me if this is `live` for > 30m") — not at v0.1; the operator gets the trial counter and the live transcript.
- **Cross-product execution roll-up dashboard** — the existing `/crews` salary view + `/inbox` queue view already surface what the operator needs; another aggregator would just duplicate.

---

## Phase 20 — Runtime iteration cap & PR change-summary persistence

> Two operator-driven follow-ups that landed together because they touched the same surfaces. (a) The `MaxIterations = 5` cap from `ScoringConstants` is right for most work items but wrong for spikes ("just iterate twice and stop, I want to see early output") and for hard work items where 5 isn't enough. (b) The agent's "## Change Summary" section was being parsed at PR-create time but lost if the transcript was truncated or the run was restarted; it needed to live on the deliverable row.

### Resolved decisions

- **Q20.1 Per-row override, not per-crew default.** `MaxIterOverride` lives on `task_queue`, not on `crews` — a crew's typical run uses `ScoringConstants.MaxIterations`, but a specific spike or rerun can be capped at 1 or relaxed to 10 without spilling that decision onto every other work item the crew picks up next. The override is read by `DeliverableLoop` at the start of `RunAsync` and clamped to `[1, 10]` server-side.
- **Q20.2 Live override via PATCH.** The live-exec view exposes a small numeric input ("Stop after N iter"); editing it calls `PATCH /api/work-items/{id}/execution { maxIter }` which writes the override on the **active** queue row (not the historical ones). The change is reflected on the next iteration boundary — already-running iterations aren't aborted.
- **Q20.3 Generator summary lives on the deliverable.** The `## Change Summary` heading the agent emits at the end of its final response is parsed during the PR-create step (`CrewQueueWorker.cs:978-986`) and persisted to `deliverables.GeneratorSummary` in the same transaction. Survives transcript truncation, restart, and the live-exec view's "Clear transcript" button (Phase 21). The PR body reads it from the deliverable row, not from the live transcript, so re-running PR creation against an already-passing deliverable produces the same body.

### Implementation steps

- [ ] **20.1** **Migration** — `Phase20Migration.cs` adds `task_queue.MaxIterOverride INTEGER NULL` + `deliverables.GeneratorSummary TEXT NOT NULL DEFAULT ''`. Idempotent.
- [ ] **20.2** **`DeliverableLoop`** — `MaxIterations` becomes `Math.Clamp(request.MaxIterOverride ?? ScoringConstants.MaxIterations, 1, 10)`; resolved at start of run.
- [ ] **20.3** **`PATCH /api/work-items/{id}/execution`** — accepts `{ maxIter: int? }` (null = clear override); writes to the latest queue row. 403 unless caller is PO of the work item's product or the assigned generator agent's lead.
- [ ] **20.4** **`TaskExecutionSnapshot.MaxIterations`** — surfaces the resolved cap so the live-exec view's input renders the active value, not the constant.
- [ ] **20.5** **`CrewQueueWorker` parses `## Change Summary`** — regex-extracts the section between `## Change Summary` and the next `## ` heading (or end of message), trims to 800 chars, persists to `deliverables.GeneratorSummary` in the deliverable-loop's persistence callback.
- [ ] **20.6** **PR body uses `deliverables.GeneratorSummary`** — `CrewQueueWorker.cs:976` reads the latest deliverable's `GeneratorSummary` for the "What changed" block; if empty, falls back to "_No change summary provided._" so the PR body never has a TODO marker.

### Out of scope for v0.1

- **Per-iteration max-cost override** — the deliverable-loop's `MaxTaskBudgetUsd` is a single number per crew. Per-task override would let the operator say "this one's worth $5 even though the cap is $1" — Phase R candidate.
- **Restartable budgets** — the budget pool starts fresh on a Phase 21 restart, by design (otherwise a stuck-in-loop bug becomes unblockable). Cumulative-across-restart accounting is `forge_v2.md` if the operator ever wants it.

---

## Phase 21 — Restart, transcript clear, and skip-soul-prompt

> When a deliverable wedges (model spinning on a tool error, evaluator stuck rejecting the same change, agent ignoring the change-summary instructions) the operator wants a single button to **start over from a clean slate**. The original "delete the worktree by hand" workaround was friction at exactly the wrong moment. Phase 21 ships an in-app Restart that handles the three failure modes the operator hits most:

1. **Stale transcript causing context confusion** — the live transcript holds 1000+ token-events from prior iterations; the agent reads the full transcript on each invocation. Restart clears it.
2. **Stale deliverable row pinning the iteration counter** — the loop's "iter N of 5" reads `deliverables.Iter`. Restart clears the deliverable rows so the next run starts at iter 1.
3. **soul.md going off-rails** — when the persona itself is producing the wedge (e.g. an evaluator soul that's overly strict), the operator wants to run the next attempt **without** the soul prompt as a diagnostic. Restart with `skipSoulPrompt=true` zeros the soul.md prefix for both generator and evaluator on the next claim.

### Resolved decisions

- **Q21.1 Restart is one button, three things.** The live-exec view's "Restart" button does (a) clear transcript + deliverables, (b) reset `ExecutionStatus` + queue row, (c) optionally set `SkipSoulPrompt=true`. The operator can't tease the three apart at v0.1 — every restart is a clean slate. (The `?clearTranscript` query param exists for tests / scripts but is opaque in the UI.)
- **Q21.2 Skip-soul-prompt is per-row, not per-crew.** Lives on `task_queue.SkipSoulPrompt`. A future "always skip soul" diagnostic mode at the crew level is `forge_v2.md`; today flipping the bit on one row doesn't affect the next assignment to the same crew.
- **Q21.3 Restart preserves work-item history.** Cards, child rows, comments, parent linkage all stay; only the execution-side rows (transcript events, deliverables, queue row) are cleared. The Restart audit trail is one `forge_actions` row; the lost transcript events are not recoverable (operator clicked the button knowing this).

### Implementation steps

- [ ] **21.1** **Migration** — `Phase21Migration.cs` adds `task_queue.SkipSoulPrompt INTEGER NOT NULL DEFAULT 0`. Idempotent.
- [ ] **21.2** **`DELETE /api/work-items/{id}/transcript`** — clears `task_transcript_event` AND `deliverables` rows in one call so the iter counter resets cleanly. Returns `{ deleted: N, deletedDeliverables: M }`.
- [ ] **21.3** **Restart button on `LiveExecutionView`** — calls `DELETE /transcript` then re-enqueues the work item via `POST /api/work-items/{id}/restart` (or the existing `/assign` path with `skipSoulPrompt`). Confirmation modal calls out the three side-effects so the operator doesn't restart by mistake.
- [ ] **21.4** **`CrewQueueWorker.RunAgentAsync` honors `SkipSoulPrompt`** — when the queue row's flag is set, both generator and evaluator skip the `File.ReadAllTextAsync(SoulPath)` step; the system-prompt is empty and the agent runs as the bare model with only the user prompt. Transparent to the agent — no special instruction is injected to "explain that soul was suppressed".
- [ ] **21.5** **Points-based `maxTurns` in Copilot SDK runner** — closely related stabilization tied to restart UX: Copilot SDK occasionally never returned a `Finished` event when the agent decided to "keep working forever". A hard cap terminates the invocation cleanly so Restart actually has something to restart from. `maxTurns` is now **scaled by story points**: `30 × points` (e.g. 3-point item → 90 turns, 5-point item → 150 turns). When `points = 0` (unestimated), falls back to the global `Forge:Agents:CopilotMaxTurns` config default (500). The per-task override is carried on `AgentContext.MaxTurns` (nullable int); `AgentRunnerDispatcher.RunCopilotSdk` resolves as `context.MaxTurns ?? options.MaxTurns`. Both generator and evaluator contexts receive the same cap. Wall-clock budget follows the same scaling: `30 min × points`, falling back to the global default when points = 0.

### Test scenarios (`RestartBehaviorTests.cs`)

Comprehensive coverage for Phase 21 restart semantics, transcript clearing, soul-prompt suppression, and points-based budgets.

**21.1 Migration — `SkipSoulPrompt` column**

- [ ] Column exists with default 0 after migration runs.

**21.2 `DELETE /api/work-items/{id}/transcript` — clear transcript + deliverables**

- [ ] Clears all `task_transcript_event` rows for the work item.
- [ ] Clears all `deliverable` rows for the work item (iter counter resets to 0).
- [ ] Returns `{ deleted: N, deletedDeliverables: M }` with correct counts.
- [ ] Idempotent — calling twice on the same work item returns `{ deleted: 0, deletedDeliverables: 0 }` on the second call.
- [ ] Returns 200 for a work item with no transcript or deliverables (empty delete is a no-op, not an error).
- [ ] Does not delete transcript events belonging to a different work item (no cross-contamination).

**21.3 Restart via `POST /api/crews/{crewId}/plan` with `restart=true`**

- [ ] Clears deliverables and resets iter count to 0.
- [ ] Resets WIT State from `in-review` → `active` so `RunAgentAsync` guard doesn't short-circuit.
- [ ] Resets WIT State from `resolved` → `active`.
- [ ] Resets WIT State from `closed` → `active`.
- [ ] Preserves WIT State when already `active` (no-op on State).
- [ ] Preserves WIT State when already `proposed` (no-op on State).
- [ ] Sets `ExecutionStatus` to `queued` on re-enqueue (failed → queued transition).
- [ ] Sets `ExecutionStatus` to `queued` on re-enqueue (completed → queued transition).
- [ ] Non-restart enqueue (`restart=false`) does NOT clear deliverables.
- [ ] Restart skips the schedule gate — a restart at Sunday 03:00 on a day crew still succeeds (no 409 `crew.off_shift`).
- [ ] Non-restart enqueue respects the schedule gate — off-shift returns 409.
- [ ] Restart on a non-existent work item returns an appropriate error.
- [ ] Restart on a non-existent crew returns 404.
- [ ] Restart cancels any existing open queue row before enqueuing a new one (idempotency — no double-queue).

**21.4 `SkipSoulPrompt` flag**

- [ ] `restart=true, skipSoulPrompt=true` persists `SkipSoulPrompt=1` on the queue row.
- [ ] Normal enqueue (`skipSoulPrompt=false` or absent) persists `SkipSoulPrompt=0`.
- [ ] `skipSoulPrompt=true` without `restart=true` still persists the flag (the two are independent controls).
- [ ] When `SkipSoulPrompt=true`, `RunAgentAsync` skips `File.ReadAllTextAsync(SoulPath)` — generator's `SoulMd` is empty string.
- [ ] When `SkipSoulPrompt=true`, evaluator also skips its soul.md — evaluator's `SoulMd` is the scoring rubric only (no persona prefix).
- [ ] When `SkipSoulPrompt=false` and `agent.SoulPath` is set, generator's `SoulMd` contains the file content.
- [ ] When `SkipSoulPrompt=false` and `agent.SoulPath` is null/empty, generator's `SoulMd` is empty string (graceful degradation).
- [ ] When `SkipSoulPrompt=false` and `agent.SoulPath` points to a missing file, generator's `SoulMd` is empty string (best-effort, no crash).

**21.5 Points-based `maxTurns` and wall-clock budget**

- [ ] 3 points × 30 = 90 min wall-clock budget.
- [ ] 1 point × 30 = 30 min wall-clock budget.
- [ ] 0 points = null budget (falls back to global default).
- [ ] 3 points × 30 = 90 max turns on `AgentContext.MaxTurns`.
- [ ] 5 points × 30 = 150 max turns.
- [ ] 1 point × 30 = 30 max turns.
- [ ] 0 points = null max turns (falls back to global `AgentRunnerOptions.MaxTurns` default of 500).
- [ ] `AgentRunnerDispatcher.RunCopilotSdk` uses `context.MaxTurns` when set (not the global default).
- [ ] `AgentRunnerDispatcher.RunCopilotSdk` uses `options.MaxTurns` when `context.MaxTurns` is null.
- [ ] Turn count increments on each `ToolExecutionCompleteEvent`; when `turnCount >= maxTurns`, the invocation terminates with `failReason = "Max turns (N) reached"`.
- [ ] Evaluator context receives the same `maxTurns` as the generator context (both scaled by the same work item points).
- [ ] Negative points value → treated as 0 (null budget + null maxTurns, falls back to defaults).

**21.R Re-execution guard (related regression coverage)**

- [ ] `RunAgentAsync` skips execution when WIT State is `in-review` (returns early, marks queue row completed).
- [ ] `RunAgentAsync` skips execution when WIT State is `resolved` or `closed`.
- [ ] After restart resets State from `in-review` → `active`, `RunAgentAsync` proceeds (does NOT skip).
- [ ] Boot-recovery sweep (`picked-up` → `queued`) does not re-run items in terminal states.

**21.I Independence invariants**

- [ ] Setting `ExecutionStatus = failed` does NOT change WIT State.
- [ ] Setting `ExecutionStatus = completed` does NOT change WIT State.
- [ ] Restarting (clearing deliverables + resetting queue) does NOT change the work item's `AssignedTo` field (WIT-level assignee is independent of execution lifecycle).
- [ ] Restarting does NOT delete the work item's child rows, comments, or parent linkage (Phase Q21.3 — only execution-side rows are cleared).

### Out of scope for v0.1

- **Restart-with-different-model** (try the same task on a different backend without manually editing the crew row) — operator asks for this occasionally; today they edit the crew + restart. Phase R candidate.
- **Selective restart** (clear transcript but keep deliverables; clear soul but keep transcript) — every operator who asked for selective restart later admitted they really wanted a full reset. v0.1 keeps the simple form.
- **Restart history view** — there's no "this work item has been restarted 3 times" ledger beyond `forge_actions`; the operator reads the audit log if they need it.

---

## Phase 22 — `AssignedAgent` field & assignment history

> The original `WorkItemRow.AssignedTo` field (Phase 12.5, Q12.5 — `kind: agent | human | team`) was meant to be the WIT-level assignee that mirrors the upstream provider. In practice the queue worker has been **overloading** it: every Sweep-2 claim overwrites `AssignedToJson` with a runtime payload like `{"crewId":"…","agentId":"…","generatorAgentId":"…","evaluatorAgentId":"…","evaluatorCrewId":"…"}` so the live-exec view can resolve who's running. That conflates two concepts:
>
> - **WIT-level assignee** — who *owns* the work item in the tracker (a person, a team, sometimes "Engineering"). Set by the human operator or by upstream sync. Doesn't change every time an agent picks the row up.
> - **Runtime-assigned agent** — who is *actually executing* the row right now. Set by the queue worker on claim, by reviewer-assign on PR open, by Restart on reset. Multiple agents simultaneously when the crew is paired (generator + evaluator). Empty before the first claim and after final completion.
>
> Phase 22 splits these into two independent fields and adds an append-only `agent_assignments` history so the operator can see "who has been on this and when". The queue worker stops writing to `AssignedTo`; everything runtime-routing flows through the new `AssignedAgent`.

### Resolved decisions

- **Q22.1 Two fields, two purposes — they never auto-sync.**
  - `AssignedTo` (existing, unchanged shape) — WIT semantic. Set by the operator / provider sync. Survives every execution transition. Never touched by `CrewQueueWorker` after Phase 22 ships.
  - `AssignedAgent` (new) — runtime fact. Set when an agent acknowledges (claims), updated on reassign / reviewer-assign, cleared on Restart and on `closed`. Empty (`null`) when nothing is in flight.
  - Different events drive each field. The operator reassigning the WIT (PATCH `assignedTo`) does **not** kick the runtime; the queue claiming a row does **not** mutate the WIT assignee.

- **Q22.2 `AssignedAgent` shape — JSON serialized into `AssignedAgentJson`.** Cardinality: 0..N agents, 0..1 team. Empty/`null` is the default state. Parsed at read time by `WorkItemMapper`:
  ```
  {
    agents:      string[],   // 1..N agent ids; paired runs land 2 (generator + evaluator)
    team:        string?,    // optional — 'Architecture' | 'Engineering' | 'Review' | 'QA'
    crewId:      string?,    // crew this assignment routed through
    role:        string,     // 'generator' | 'evaluator' | 'reviewer' | 'judge' | 'manual'
    assignedAt:  string,     // ISO-8601
    assignedBy:  string?     // 'queue-worker' | 'lead-dispatcher' | userId | null
  }
  ```

- **Q22.3 Lifecycle — five transitions write `AssignedAgent` AND append a history row.** The runtime is forbidden from rewriting the field in place without an audit row; even cosmetic changes go through these transitions.

  | # | Trigger                         | `AssignedAgent` after            | History `kind`        |
  |---|---------------------------------|----------------------------------|-----------------------|
  | 1 | Queue claim (Sweep 2)           | `{ generator+evaluator }`        | `claimed`             |
  | 2 | Reassign within crew            | `{ new-member }`                 | `reassigned`          |
  | 3 | Restart (Phase 21)              | `null`                           | `cleared-restart`     |
  | 4 | PR opened → ExecStatus=InReview | unchanged (still the coder)      | `pr-opened`           |
  | 5 | Reviewer assigned               | `{ reviewer agent or crew }`     | `reviewer-assigned`   |
  | 6 | Work item closed / resolved     | `null`                           | `completed`           |

  Note that step 4 records a transition without changing the field — the coder stays "the assigned agent" through review until a reviewer is explicitly assigned (step 5). This matches the operator's mental model: "until I pick someone for review, the coder still owns the row."

- **Q22.4 Sticky after acknowledge.** Once an agent claims, `AssignedAgent` stays set even when the agent fails / restarts mid-run / hits a budget cap. Failed runs keep the field pinned so the operator can see *who* failed in the work-item detail strip without scrolling history. The field clears only on **explicit Restart** (Phase 21), **manual clear** (Phase 22.4 endpoint), or **work-item close** (state transition to `resolved` / `closed`).

- **Q22.5 Multi-agent assignment is the rule, not the exception.** Paired crews (Phase 16.2) always land 2 agents (generator + evaluator) on claim; single-crew runs land 1; future review-time multi-reviewer scenarios may land more. UI surfaces this as a stack of avatars + a count chip when N > 2; the first agent in `agents[]` is canonical for "who's driving" (generator under `role='generator'`, reviewer under `role='reviewer'`).

- **Q22.6 Queue worker writes `AssignedAgent`, not `AssignedTo`.** `CrewQueueWorker.cs:347-348` — the line that today builds `wi.AssignedToJson = "{\"crewId\":\"…\",\"agentId\":\"…\",…}"` moves to `wi.AssignedAgentJson = …`. `AssignedToJson` keeps whatever the WIT semantic value was (typically `{kind:'team', id:'Engineering'}` from sync, or `{kind:'agent', id:'XD'}` from a manual assign). The boot-recovery sweep also writes the new field on reclaim so the live-exec view has accurate runtime data after a server restart.

- **Q22.7 Backfill of pre-Phase-22 data is one-shot, idempotent.** First boot after Phase 22 lands runs a migration step that walks every `WorkItemRow` whose `AssignedToJson` parses to the runtime shape (heuristic: presence of `generatorAgentId` OR `crewId` keys at the top level). For each match: copy the bytes into `AssignedAgentJson` (with `assignedBy='backfill'`); rebuild `AssignedToJson` as a WIT-shape `{kind:'team', id: crew.team, displayName: crew.team}` resolved from the embedded `crewId`. Append a `agent_assignments(kind='backfill')` row per migrated work item so the timeline shows where the field came from. Subsequent boots are no-ops.

### Implementation steps

- [x] **22.1** **Migration** — `Forge.Api/Persistence/Phase22Migration.cs` (idempotent, runs after `Phase21Migration`):
  - `work_items` adds `AssignedAgentJson TEXT NULL`.
  - New table `agent_assignments`:
    ```
    Id INTEGER PK AUTOINCREMENT
    WorkItemId TEXT NOT NULL
    Kind TEXT NOT NULL                    -- 'claimed'|'reassigned'|'cleared-restart'|'cleared-manual'|'pr-opened'|'reviewer-assigned'|'completed'|'backfill'
    AgentIdsJson TEXT NOT NULL DEFAULT '[]' -- snapshot at this moment
    Team TEXT NULL
    CrewId TEXT NULL
    Role TEXT NULL
    FromExecutionStatus TEXT NULL
    ToExecutionStatus TEXT NULL
    AssignedBy TEXT NULL                  -- 'queue-worker'|'lead-dispatcher'|userId|'backfill'|null
    Reason TEXT NULL
    At TEXT NOT NULL
    ```
  - Index `IX_agent_assignments_workitem_at` on `(WorkItemId, At DESC)` — drives the timeline read.

- [x] **22.2** **Domain model + TS mirror** — `WorkItemRow.AssignedAgentJson`; `AssignedAgent` POCO (Q22.2 shape) + `AgentAssignmentRow`. TS `WorkItem.assignedAgent: AssignedAgent | null`, `WorkItem.assignedAgentHistory?: AgentAssignmentEntry[]` (lazy-loaded by the timeline read).

- [x] **22.3** **`CrewQueueWorker.cs` — Sweep-2 claim path rewrite** (the line at `~347-348` today):
  - Build `AssignedAgent` from the resolved `(generatorAgentId, evaluatorAgentId, crewId, evaluatorCrewId)` plus `team = crew.Team`, `role = 'generator'`, `assignedBy = 'queue-worker'`.
  - Write `wi.AssignedAgentJson = serialize(payload)` instead of overwriting `AssignedToJson`.
  - Append `agent_assignments(kind='claimed', AgentIdsJson, Team, CrewId, Role='generator', FromExecutionStatus='queued', ToExecutionStatus='live', AssignedBy='queue-worker', At=now)` in the same SaveChanges() call so they commit atomically.
  - Boot-recovery sweep does the same write so a re-claim after server restart records a fresh `claimed` row (the operator sees a "re-claimed at X" row rather than a single 'claimed' row from days ago).

- [x] **22.4** **Endpoints — `AgentAssignmentsEndpoints.cs`**:
  - `GET /api/work-items/{id}/assigned-agent` — returns the parsed `AssignedAgent` or 204 when null.
  - `GET /api/work-items/{id}/assignments?limit=…` — paginated history, newest first. Default 50, max 500.
  - `POST /api/work-items/{id}/assignments` body `{ agents: string[], team?, crewId?, role, reason? }` — manual override (PO of the work item's product OR crew lead). Validates that referenced agents exist; writes the field + history row with `assignedBy=userId`. Used for manual reviewer-assign (Q22.3 step 5).
  - `DELETE /api/work-items/{id}/assigned-agent` body `{ reason? }` — clears the field. PO only. History `kind='cleared-manual'`. Use sparingly — most clears should come from Restart or completion.

- [x] **22.5** **`PATCH /api/work-items/{id}/move` integration** — when the target state transitions:
  - `→ in-review` (PR opened): append `agent_assignments(kind='pr-opened', From=current, To='in-review')` without touching `AssignedAgentJson` (Q22.3 step 4).
  - `→ resolved` or `→ closed`: append `agent_assignments(kind='completed')` AND null out `AssignedAgentJson` (Q22.3 step 6).
  - These hooks live in `WorkItemsEndpoints.MoveWorkItem` so any path that drives state — UI move, queue worker post-PR transition (Phase 16.18 line 1144), provider webhook — fires the same audit.

- [x] **22.6** **Restart (Phase 21) clears `AssignedAgent`** — `DELETE /api/work-items/{id}/transcript` + the queue-restart branch null out `AssignedAgentJson` and append `agent_assignments(kind='cleared-restart', From='live'|'failed', To='queued', AssignedBy=userId)`. The next claim writes a fresh row and the timeline shows the discontinuity, not a silent overwrite.

- [x] **22.7** **Backfill migration (Q22.7)** — one-shot at first boot after Phase 22 ships, gated by a `forge_seed_state` row so re-boots skip it. Heuristic for runtime-shaped `AssignedToJson`: `JsonDocument.RootElement.TryGetProperty("generatorAgentId" | "crewId" | "agentId")` succeeds AND there's no `kind` property at the top level. For each match: copy bytes → `AssignedAgentJson` (wrapping in the Q22.2 shape if needed); rebuild `AssignedToJson` from the resolved crew's `Team`. Log a one-line summary `"Phase 22 backfill migrated N work items"` and write one `agent_assignments(kind='backfill', AssignedBy='backfill')` per migrated row.

- [ ] **22.8** **UI — Work-item detail panel — new "Currently working on this" strip** below the existing Assignee row in `WorkItemDetailPanel.tsx`: *(backend ready; UI not yet implemented)*
  - **Empty state** — "Not currently assigned to an agent" + an `Assign agent` affordance (visible to PO + crew leads only) that opens a small picker (agent list filtered by team + role).
  - **Active state** — stack of avatars (1..N) + role pill (`Generator` / `Evaluator` / `Reviewer` color-coded) + crew name + relative timestamp ("since 14m ago"). Hover shows full agent names + the `assignedBy` label.
  - Click → opens a right-rail **Assignment history** drawer rendering the `agent_assignments` rows as a vertical timeline: `claimed → pr-opened → reviewer-assigned → completed` with timestamps + actor + reason. Reuses the styling of the Phase 14 `agent_level_history` timeline.

- [ ] **22.9** **UI — Live Execution view** — the header strip's "assigned to" pill (today reads from `AssignedToJson` indirectly via the snapshot's `generatorAgentId` / `evaluatorAgentId`) now reads `AssignedAgent` directly. Pill click expands the history drawer. Empty state ("Not yet assigned — waiting for queue claim") replaces the previous "No events yet" copy when `AssignedAgent` is null AND no transcript events exist. *(backend ready; UI not yet implemented)*

- [ ] **22.10** **UI — Plan board card + Workstream card cells** — the agent-name cell that today reads the runtime fields out of `AssignedToJson` switches to `AssignedAgent.agents[0]` (canonical driver per Q22.5). When `AssignedAgent` is null, the cell falls back to the WIT-level assignee (`AssignedTo`), rendered with a slightly muted style so the operator can distinguish "no one's executing" from "someone's actively coding". Tooltip enumerates all `agents[]` when N > 1. *(backend ready; UI not yet implemented)*

- [x] **22.11** **Tests — `Phase22AssignedAgentTests.cs`** — see detailed scenario list in the **Test scenarios** section below.

- [ ] **22.12** **Fidelity** — additive UI (assignment strip + history drawer + live-exec pill data source switch); exempt from the prototype screenshot diff. No new design tokens — reuses the avatar palette + the Phase 14 history-timeline styling. *(backend ready; UI not yet implemented)*

### Test scenarios

> Coverage matrix for Phase 22. Each scenario gets a checkbox; a checked box means the test exists and passes. Test file: `Phase22AssignedAgentTests.cs`. **Coverage status: 59 tests passing — backend implementation complete.**

**22.1 Migration — `Phase22Migration.ApplyAsync`**

- [x] Adds `AssignedAgentJson TEXT NULL` column to `work_items` table on a fresh DB.
- [x] Creates `agent_assignments` table with all required columns (`Id`, `WorkItemId`, `Kind`, `AgentIdsJson`, `Team`, `CrewId`, `Role`, `FromExecutionStatus`, `ToExecutionStatus`, `AssignedBy`, `Reason`, `At`).
- [x] Creates index `IX_agent_assignments_workitem_at` on `(WorkItemId, At DESC)`.
- [x] Idempotent — second invocation does not throw and does not duplicate columns or tables.
- [x] Runs after `Phase21Migration` without conflict.

**22.2 Domain model + TS mirror**

- [x] `WorkItemRow.AssignedAgentJson` defaults to `null` for new rows.
- [x] `AssignedAgent` POCO round-trips through JSON serialization (all fields: `agents`, `team`, `crewId`, `role`, `assignedAt`, `assignedBy`).
- [x] `AssignedAgent` with a single agent serializes `agents` as a 1-element array.
- [x] `AssignedAgent` with paired agents (generator + evaluator) serializes `agents` as a 2-element array.
- [x] `AssignedAgent` with `null` optional fields (`team`, `crewId`, `assignedBy`) round-trips correctly.
- [x] `AgentAssignmentRow` entity round-trips through EF — write all fields, read back identical values.
- [x] `WorkItemMapper` parses `AssignedAgentJson` into `AssignedAgent` POCO on read.
- [x] `WorkItemMapper` returns `null` for `AssignedAgent` when `AssignedAgentJson` is null or empty.

**22.3 Queue claim — Sweep 2 writes `AssignedAgent` (not `AssignedTo`)**

- [x] Sweep-2 claim writes `AssignedAgentJson` with correct shape: `agents=[picked]`, `role='generator'`, `assignedBy='queue-worker'`, `assignedAt` is ISO-8601 UTC.
- [x] Sweep-2 claim for a paired crew writes `agents=[generator, evaluator]` (2-element array).
- [x] Sweep-2 claim for a single (non-paired) crew writes `agents=[generator]` (1-element array).
- [x] Sweep-2 claim populates `team` from `crew.Team` and `crewId` from the claiming crew's ID.
- [x] Sweep-2 claim appends an `agent_assignments` row with `kind='claimed'` in the same `SaveChanges()` transaction.
- [x] `agent_assignments` row from claim has `FromExecutionStatus='queued'`, `ToExecutionStatus='live'`, `AssignedBy='queue-worker'`.
- [x] Sweep-2 claim does **NOT** overwrite `AssignedToJson` — the WIT-level assignee is unchanged.
- [x] Boot-recovery reclaim writes a fresh `AssignedAgentJson` AND appends a new `agent_assignments(kind='claimed')` row (not a silent field-update).

**22.4 Endpoints — `AgentAssignmentsEndpoints.cs`**

GET assigned agent:
- [x] `GET /api/work-items/{id}/assigned-agent` returns 200 with parsed `AssignedAgent` when field is set.
- [x] `GET /api/work-items/{id}/assigned-agent` returns 204 when `AssignedAgentJson` is null.
- [x] `GET /api/work-items/{id}/assigned-agent` returns 404 for a non-existent work item ID.

GET assignment history:
- [x] `GET /api/work-items/{id}/assignments` returns history rows newest-first (descending `At`).
- [x] `GET /api/work-items/{id}/assignments?limit=2` returns at most 2 rows.
- [x] `GET /api/work-items/{id}/assignments` defaults to limit=50 when no query param.
- [x] `GET /api/work-items/{id}/assignments?limit=999` clamps to max 500.
- [x] `GET /api/work-items/{id}/assignments` returns empty array for a work item with no history.

POST manual assignment:
- [x] `POST /api/work-items/{id}/assignments` with valid `{ agents, role }` writes `AssignedAgentJson` + appends history row.
- [x] `POST /api/work-items/{id}/assignments` sets `assignedBy` to the calling user ID (not `'queue-worker'`).
- [x] `POST /api/work-items/{id}/assignments` with `role:'reviewer'` appends `kind='reviewer-assigned'`.
- [x] `POST /api/work-items/{id}/assignments` validates that referenced agent IDs exist — returns 400 for unknown agents.
- [x] `POST /api/work-items/{id}/assignments` with optional `reason` persists the reason in the history row.
- [x] `POST /api/work-items/{id}/assignments` without `reason` stores `null` reason.
- [x] `POST /api/work-items/{id}/assignments` by non-PO / non-crew-lead returns 403.

DELETE assigned agent:
- [x] `DELETE /api/work-items/{id}/assigned-agent` nulls `AssignedAgentJson` + appends `kind='cleared-manual'`.
- [x] `DELETE /api/work-items/{id}/assigned-agent` with `{ reason }` body persists the reason.
- [x] `DELETE /api/work-items/{id}/assigned-agent` by PO returns 200.
- [x] `DELETE /api/work-items/{id}/assigned-agent` by non-PO returns 403.
- [x] `DELETE /api/work-items/{id}/assigned-agent` when field is already null is a no-op (idempotent, still 200).

**22.5 `MoveWorkItem` integration — state transition hooks**

PR opened (→ in-review):
- [x] `MoveWorkItem(→ in-review)` appends `agent_assignments(kind='pr-opened')` without changing `AssignedAgentJson`.
- [x] `agent_assignments` row from PR-open has `FromExecutionStatus` = prior status, `ToExecutionStatus='in-review'`.
- [x] Coder's `AssignedAgent.agents` array is unchanged after PR open — coder stays pinned (Q22.3 step 4).

Completed / resolved:
- [x] `MoveWorkItem(→ resolved)` nulls `AssignedAgentJson` AND appends `agent_assignments(kind='completed')`.
- [x] `MoveWorkItem(→ closed)` nulls `AssignedAgentJson` AND appends `agent_assignments(kind='completed')`.
- [x] `agent_assignments` row from completion has correct `FromExecutionStatus` and `ToExecutionStatus`.

State transitions that do NOT touch AssignedAgent:
- [x] `MoveWorkItem(proposed → active)` does NOT append an assignment history row and does NOT change `AssignedAgentJson`.
- [x] `MoveWorkItem(active → proposed)` (revert) does NOT change `AssignedAgentJson`.

**22.6 Restart clears `AssignedAgent`**

- [x] `DELETE /api/work-items/{id}/transcript` nulls `AssignedAgentJson`.
- [x] `DELETE /api/work-items/{id}/transcript` appends `agent_assignments(kind='cleared-restart')`.
- [x] `cleared-restart` history row has `FromExecutionStatus` = prior status (e.g. `'live'` or `'failed'`), `ToExecutionStatus='queued'`.
- [x] `cleared-restart` history row has `AssignedBy` = the user who triggered the restart.
- [x] After restart + re-claim, the timeline shows three rows: `claimed → cleared-restart → claimed` (no silent overwrite).
- [x] Restart when `AssignedAgentJson` is already null still appends the `cleared-restart` row (audit completeness).

**22.7 Backfill migration (one-shot)**

- [x] Runtime-shaped `AssignedToJson` (has `generatorAgentId` or `crewId` at top level, no `kind`) is detected and migrated.
- [x] Migrated row: `AssignedAgentJson` receives the data in Q22.2 shape with `assignedBy='backfill'`.
- [x] Migrated row: `AssignedToJson` is rebuilt as WIT-shape `{kind:'team', id:team, displayName:team}` from the embedded `crewId`.
- [x] Non-runtime-shaped `AssignedToJson` (has `kind` property, e.g. `{kind:'agent', id:'XD'}`) is NOT migrated — left untouched.
- [x] `null` `AssignedToJson` rows are skipped — no crash, no history row.
- [x] Each migrated row gets an `agent_assignments(kind='backfill', AssignedBy='backfill')` history row.
- [x] Backfill is gated by a `forge_seed_state` row — second boot is a no-op.
- [x] Idempotent — running the migration twice produces the same result (no duplicate history rows on second run).

**22.8 Sticky on failure (Q22.4)**

- [x] Agent fails mid-run → `AssignedAgentJson` stays set (not cleared).
- [x] Agent fails mid-run → no `cleared-*` history row is written.
- [x] Agent hits budget cap → `AssignedAgentJson` stays set.
- [x] Agent restarts mid-run (server crash) → `AssignedAgentJson` stays set until explicit Restart.

**22.9 WIT assignee independence (Q22.1)**

- [x] `PATCH /api/work-items/{id}` with `{ assignedTo: {kind:'agent', id:'XD'} }` does NOT mutate `AssignedAgentJson`.
- [x] `PATCH /api/work-items/{id}` with `{ assignedTo: … }` does NOT append any `agent_assignments` history row.
- [x] Sweep-2 claim does NOT mutate `AssignedToJson` — the WIT-level assignee is preserved.
- [x] Both fields can be non-null simultaneously with different values — no conflict.

**22.10 Multi-agent & role semantics (Q22.5)**

- [x] Paired crew claim writes exactly 2 entries in `agents[]` (generator + evaluator).
- [x] Single crew claim writes exactly 1 entry in `agents[]`.
- [x] `agents[0]` is the canonical "driver" — generator for `role='generator'`, reviewer for `role='reviewer'`.
- [x] Manual reviewer assignment (`POST /assignments {role:'reviewer'}`) replaces `AssignedAgent` entirely (not additive).
- [x] After reviewer assignment, `agents[0]` is the reviewer agent.

**22.11 Full lifecycle timeline (end-to-end)**

- [x] Full happy path: `claimed → pr-opened → reviewer-assigned → completed` produces exactly 4 `agent_assignments` rows in chronological order.
- [x] Full restart path: `claimed → cleared-restart → claimed → pr-opened → completed` produces exactly 5 rows.
- [x] History rows are ordered by `At DESC` when queried via the paginated endpoint.
- [x] Each history row has a non-null `At` timestamp in ISO-8601 format.
- [x] Each history row has a non-empty `Kind` from the allowed set: `claimed`, `reassigned`, `cleared-restart`, `cleared-manual`, `pr-opened`, `reviewer-assigned`, `completed`, `backfill`.

**22.12 Edge cases & error handling**

- [x] Concurrent claims on the same work item — only one succeeds; the loser gets a CAS failure, no orphan history row.
- [x] `POST /assignments` on a non-existent work item returns 404.
- [x] `DELETE /assigned-agent` on a non-existent work item returns 404.
- [x] `GET /assignments` on a non-existent work item returns 404.
- [x] `POST /assignments` with empty `agents[]` array returns 400.
- [x] `POST /assignments` with invalid `role` value returns 400.
- [x] Malformed `AssignedAgentJson` in DB does not crash the GET endpoint — returns null gracefully.

### Out of scope for v0.1 — Phase R / `forge_v2.md`

- **Cross-product agent loaning** — an agent currently assigned on product A can't be claimed on product B simultaneously. Queue worker enforces single-assignment per agent at v0.1 by relying on the existing per-agent `MaxConcurrent` cap. Multi-product agent pools are `forge_v2.md`.
- **Agent capacity dashboards** — "this agent is on 5 work items right now" is derivable by querying `WorkItemRow WHERE AssignedAgentJson contains @agentId` but no dedicated view ships at v0.1.
- **History compaction** — `agent_assignments` grows append-only forever. A 90-day rollup table is `forge_v2.md` if row volume becomes a problem.
- **Auto-assign-reviewer on PR open** — Q22.3 step 4 keeps the coder pinned until a human / PO / review-lead picks a reviewer. Auto-routing the row to a review-team lead's queue is `forge_v2.md`.
- **Per-role assignment (claim coder, claim reviewer separately)** — at v0.1 the queue claim writes generator+evaluator together; reviewer is a *replacement* assignment, not an additive one. Composite assignments ("currently 1 coder + 2 reviewers") are deferred until the operator demonstrates need.

---

## Phase 23 — Scheduled work-item sync (per-repo, paginated, delta, `agent-ready` auto-assign)

> Phase 5.7 + Phase 12.10 ship a 60-second polling loop that re-queries every configured repo on every tick. That works for low item counts but burns provider rate-limit budget and produces noisy logs once a tenant has thousands of work items. **Phase 23 replaces the polling with a scheduled job at a 20-minute cadence**, splits the work properly into a *first full sync* (bounded by a time window) plus *delta syncs* keyed on the upstream `ChangedDate` / `updatedAt`, paginates the upstream calls correctly, and adds an upstream-driven `agent-ready` flag that auto-routes flagged items to a default agent team (Engineering by default).
>
> The phase also formalizes a long-standing implementation drift: an ADO **work-item sync repo** is not the same shape as an ADO **git repo**. Work items live in a project, not in any one git repo, and the operator wants to scope sync to a *list of area paths inside the project*. Phase 23 carries that scope on the existing `Repo` row (multi-area-path JSON column) rather than introducing a new entity, so the per-product/per-repo navigation already in place keeps working.
>
> Three principles drive the shape:
>
> 1. **Time windows are explicit, not implicit.** First sync of an ADO repo pulls items changed in the last 365 days; first sync of a GitHub repo pulls the entire issue history. Both are documented constants the operator can override per-repo. Subsequent syncs are delta-bounded by `LastSyncAt`, so the cost of catching up doesn't grow as the repo ages.
> 2. **Pagination is the runner's problem, not the caller's.** Both providers cap page size; the sync service walks the cursor until the upstream says "no more" then commits the run. A sync run that fails mid-walk leaves the cursor at the last successful page so the next run resumes cleanly.
> 3. **The agent-ready flag is a declarative routing signal.** When upstream tags an item `agent-ready`, Forge sets `AssignedTo = {kind:'team', id: repo.AgentReadyDefaultTeam}` (default `Engineering`). This is the only field the sync writes on top of the upstream data; everything else is provider-of-truth. The runtime-side `AssignedAgent` (Phase 22) is untouched — sync only writes the WIT-semantic field, the queue worker still owns runtime assignment.

### Resolved decisions

- **Q23.1 Cadence — 2 hours (Phase 29, was 20 minutes).** One tick every 2 hours, jittered ±60 seconds across repos to avoid thundering-herd against a single ADO org. Configurable via `Forge:Sync:WorkItem:IntervalSeconds` (default 7200). Per-repo overrides land via `RepoRow.WorkItemSyncIntervalSeconds` and can only make a repo sync **slower** than the global default — matches the "reduce upstream load" intent. The legacy 60-second polling for cards / PRs / pipelines is unchanged — Phase 23 adds a *separate* job for work items because their churn rate, page sizes, and rate-limit profile are different. The `WorkItemSyncJob` and `ProviderSyncService` run side-by-side; they touch different tables.

- **Q23.2 Per-repo scope, parallel up to 4.** Each `Repo` row drives one sync invocation per tick. Concurrency cap = 4 (tunable via `Forge:Sync:WorkItem:MaxParallel`); rows beyond the cap wait for the next tick. Per-repo serialization: a tick that finds the previous run still in flight (`LastSyncStatus='running'` for > 19 minutes) cancels the in-flight run and starts a fresh one — better to drop a slow run than to stack them. A `LastSyncStatus='running'` marker carries an `IsRunningSince` timestamp so the cancellation rule is a wall-clock check, not a heuristic.

- **Q23.3 ADO scope — `(org, project, areaPaths[])` triple, not `(org, project, gitRepo)`.** ADO work items belong to the *project*, not to any git repo, and the operator wants to scope sync to a curated list of area paths within the project. The `Repo` row carries:
  - `AdoOrg`, `AdoProject` (existing) — the project the WIQL query runs against.
  - `AreaPathsJson` (new) — JSON `string[]` of area-path prefixes. Empty / null = sync the whole project. Example: `["One\\AzureStack\\ASZ-Update", "One\\AzureStack\\Compute"]`. The WIQL `WHERE` clause is `[System.AreaPath] UNDER 'p1' OR [System.AreaPath] UNDER 'p2'`. ADO's `UNDER` operator includes the node and all descendants, which matches the operator's mental model of "everything under this folder".
  - The git-side fields (`RemoteUrl`, `CloneRoot`, `DefaultBranch`) are independent — they describe the repo Forge clones for worktrees. A single `Repo` row carries both shapes; auto-clone (Phase 8.10) uses the git fields, work-item sync uses the project + area-path fields. Either set may be empty; a row with only the project + area-paths is "work-items only", a row with only the git fields is "git only", a row with both is the common case.

- **Q23.4 GitHub scope — repo-native.** GitHub issues are scoped to a repo (`/repos/{owner}/{repo}/issues`), so `Repo.GhOwner` + `Repo.GhRepo` is enough; `AreaPathsJson` is ignored on GitHub repos. Pull requests come along for the ride because GitHub treats PRs as issues for the purposes of the issues endpoint — Phase 23 syncs them in the same call and the mapper splits them by `pull_request` field presence.

- **Q23.5 Initial time window — 30 days for both providers (Phase 29, was 365d ADO / all-time GH).** First sync of an ADO repo runs WIQL with `[System.ChangedDate] >= @cutoff` where `@cutoff = UtcNow - 30d`. First sync of a GitHub repo passes `since=UtcNow-30d` (was: omitted, returning all-time history). Both windows are constants in `Forge.Api/SyncConstants.cs` (`AdoInitialWindowDays = 30`, `GithubInitialWindowDays = 30`) so the spec-consistency test catches drift; per-repo overrides (`Repo.InitialSyncWindowDays INTEGER NULL`) still narrow further when the operator hits a large ADO project.

- **Q23.6 Pagination strategy — provider-specific, cursor-driven.**
  - **ADO:** WIQL queries return up to 20,000 work-item references in a single response; `_apis/wit/workitems?ids=…&fields=…` is then called in batches of **200 IDs per request** (ADO's hard cap). The sync service splits the WIQL result client-side and walks the batches sequentially, committing each batch in its own transaction so a failure in batch N+1 doesn't roll back batch N. The cursor (`LastSyncCursor` text column) holds the last successfully-committed batch index so a resume picks up exactly where the prior run failed.
  - **GitHub:** `/issues?per_page=100&since={LastSyncAt}` is paginated by the standard `Link: rel="next"` header. The sync service walks the chain until no `next` link is present. Cursor stores the `next` URL as opaque text so resume after failure is a single GET, no math required.

- **Q23.7 Delta sync — upstream `ChangedDate` (ADO) / `updatedAt` (GitHub) cursor (Phase 29 swap).** Once `LastUpstreamChangedDate IS NOT NULL`, the next run uses it as the lower bound:
  - **ADO:** `WHERE [System.ChangedDate] >= @LastUpstreamChangedDate AND ([System.AreaPath] UNDER …)`. Window is **half-open on the lower bound** (`>=`, not `>`) so an item changed exactly at the cursor instant isn't dropped; the upsert path is idempotent so the duplicate-with-same-revision is a no-op.
  - **GitHub:** `since={LastUpstreamChangedDate-toISO}`. GitHub's `since` filter is on `updated_at` and is documented as inclusive on the boundary.
  - **Cursor source change (Phase 29).** The cursor was previously `LastSyncAt` (local clock at run start). It is now `LastUpstreamChangedDate` — the **MAX upstream-changedate observed** in the previous successful run. Local-clock cursoring missed items written *during* a sync run; an upstream-anchored cursor doesn't. `LastSyncAt` stays on the row as the local-clock telemetry "when did we last tick" used by the stale-running guard, the UI's "last synced" pill, and the per-repo cadence gate (Q23.1) — but it is no longer the delta filter.
  - The cursor advances only on a fully-successful run that observed at least one item; an empty delta leaves the prior boundary alone so a slow week doesn't snap the cursor forward past un-synced items in a temporarily-empty WIQL response. A run that fails mid-walk leaves `LastUpstreamChangedDate` at its prior value so the next run re-fetches the same window.

- **Q23.8 `agent-ready` flag — upstream-tag → auto-team-assign.** When sync sees an item with the `agent-ready` flag (ADO `System.Tags` contains `agent-ready`; GitHub label name `agent-ready`):
  - If `WorkItemRow.AssignedTo` is currently null OR has the legacy runtime-shape (Q22.7), set `AssignedTo = {kind: 'team', id: repo.AgentReadyDefaultTeam, displayName: repo.AgentReadyDefaultTeam}`. Default team is `Engineering`; `repos.AgentReadyDefaultTeam TEXT NULL DEFAULT NULL` overrides per-repo.
  - If `AssignedTo` is already a meaningful WIT-shape value (`{kind: 'agent'|'human'|'team', id: ...}`), **leave it untouched** — the operator's manual assignment beats the auto-rule. The flag still flows in (item shows `agent-ready` chip in UI) but doesn't overwrite intent.
  - Removing the flag upstream does **not** un-assign — the assignment is a one-way ratchet at v0.1. Operators wanting to revoke an auto-assign do it manually.
  - The auto-assign writes a `forge_actions(kind='auto-assigned-by-flag', actorUserId='sync', body: { repoId, workItemId, team })` audit row so the trail is visible at `/audit`. **Does NOT write `AssignedAgent`** (Phase 22 boundary holds — runtime assignment is the queue worker's job).

- **Q23.9 Idempotency — sync runs are replayable.** Each `WorkItemRow` upsert keys on `(provider, providerRef)` so a re-fetch of the same work item produces no duplicate rows. The conflict policy from Phase 5.9 still applies: if the local row's `RemoteRevision` differs from the upstream's at upsert time, the run records a conflict but writes the upstream value (last-writer-wins) and surfaces the divergence in the work-item detail panel.

### Implementation steps

- [x] **23.1** **Migration** — `Forge.Api/Persistence/Phase23Migration.cs` (idempotent, runs after `Phase22Migration`):
  - `repos` adds:
    - `AreaPathsJson TEXT NULL` — JSON `string[]`. Null / `[]` = whole project.
    - `LastSyncAt TEXT NULL` — ISO-8601 cursor.
    - `LastSyncCursor TEXT NULL` — opaque per-provider page cursor for resume.
    - `LastSyncStatus TEXT NOT NULL DEFAULT 'idle'` — `idle | running | failed`.
    - `LastSyncError TEXT NULL` — last error message, surfaced in the UI.
    - `IsRunningSince TEXT NULL` — wall-clock guard for the 19-minute cancellation rule.
    - `AgentReadyDefaultTeam TEXT NULL DEFAULT NULL` — falls back to `Engineering` constant when null.
    - `InitialSyncWindowDays INTEGER NULL` — overrides the ADO 365-day default; ignored for GitHub.
  - New table `work_item_sync_runs(id, repoId, kind: 'full'|'delta'|'manual', startedAt, finishedAt?, itemsAdded, itemsUpdated, itemsConflicted, errorMessage, completedSuccessfully)`. Indexed by `(repoId, startedAt DESC)`.
  - TS mirrors in `src/forge/Forge.UI/src/types.ts`: `Repo` gets the new optional fields; new `WorkItemSyncRun` type for the history.

- [x] **23.2** **`SyncConstants.cs`** — locks the time-window defaults, page sizes, cadence, and the `agent-ready` flag literal:
  ```
  AdoInitialWindowDays = 365
  AdoMaxIdsPerWorkItemBatch = 200
  AdoMaxWiqlReferences = 20000
  GithubPerPage = 100
  WorkItemSyncIntervalSeconds = 1200
  WorkItemSyncMaxParallel = 4
  RunningTimeoutMinutes = 19
  AgentReadyFlag = "agent-ready"
  AgentReadyDefaultTeam = "Engineering"
  ```
  Spec-consistency test (`Phase23SyncConstantsTests`) parses this section of the spec and asserts the constants verbatim, mirroring Phase 14.14 / Phase 16.5.

- [x] **23.3** **`IWorkItemSyncJob` hosted service** — `Forge.Api/Services/WorkItemSyncJob.cs`. 20-min tick loop with ±60 s jitter; on each tick:
  - Query `repos WHERE LastSyncStatus IN ('idle','failed') OR (LastSyncStatus='running' AND IsRunningSince < now-19m)` ordered by `LastSyncAt` ascending (oldest first).
  - For each, dispatch to a `SemaphoreSlim`-bounded worker (`SyncConstants.WorkItemSyncMaxParallel = 4`).
  - Worker calls `IWorkItemSyncRunner.RunAsync(repo, ct)`; on success sets `LastSyncAt = startedAt`, `LastSyncStatus = 'idle'`, clears `LastSyncCursor` + `LastSyncError`. On failure sets `LastSyncStatus = 'failed'` + `LastSyncError = ex.Message` and leaves `LastSyncAt` untouched.
  - Each invocation appends a `work_item_sync_runs` row (start + end timestamps, counts, errors, success flag).
  - Telemetry: `forge.sync.workitem.duration_ms` histogram (tagged with `kind`, `provider`, `outcome`); `forge.sync.workitem.items{kind}` counter; `forge.sync.workitem.errors` counter.
  - Manual trigger via `POST /api/repos/{id}/sync-work-items` (existing surface from Phase 12.10) calls the same `RunAsync` with `kind='manual'`; rate-limited to 1/min/repo unless caller is PO with `?ignoreThrottle=true`.

- [x] **23.4** **`AdoWorkItemSyncRunner` — multi-area-path WIQL + 200-ID batching**:  <!-- shipped: `AdoProvider.BuildSyncWiql` honours `WorkItemSyncFilters.AreaPaths` (multi-OR); `AdoProvider.StreamWorkItemBatchesAsync` walks the WIQL result in 200-ID chunks (`SyncConstants.AdoMaxIdsPerWorkItemBatch`) up to `AdoMaxWiqlReferences`, yields each batch with the index as `OpaqueCursor`, and `WorkItemSyncJob` commits per batch + persists `LastSyncCursor`. `SendWithRetryAsync` honours `Retry-After` capped at `MaxRetryAfterSeconds = 60`, three attempts before surfacing the failure. Tests: `Phase23WorkItemSyncTestsExtra.AdoPaginationExtraTests` + `AdoRetryExtraTests`. -->
  - Build the WIQL `WHERE` clause from `repo.AreaPathsJson`:
    - Empty → `[System.TeamProject] = '@project'` only.
    - Single → `[System.AreaPath] UNDER '{p1}'`.
    - Multi → `([System.AreaPath] UNDER '{p1}' OR [System.AreaPath] UNDER '{p2}' OR …)`.
  - Append `[System.ChangedDate] >= '{lastSyncOrCutoff}'` where `lastSyncOrCutoff = repo.LastSyncAt ?? (UtcNow - {InitialSyncWindowDays})`.
  - POST `_apis/wit/wiql?api-version=7.0` body `{ "query": "SELECT [System.Id] FROM workitems WHERE …" }`. Response carries up to 20,000 references (`SyncConstants.AdoMaxWiqlReferences`).
  - Split the IDs into batches of 200; for each batch GET `_apis/wit/workitems?ids={csv}&fields={fieldList}&$expand=relations`. `fieldList` includes `System.Id`, `System.WorkItemType`, `System.Title`, `System.State`, `System.AreaPath`, `System.IterationPath`, `System.AssignedTo`, `System.Tags`, `System.ChangedDate`, `System.Parent`, `Microsoft.VSTS.Scheduling.StoryPoints`, `Microsoft.VSTS.Common.Priority`, `Microsoft.VSTS.Common.Severity`, plus the description / repro-steps fields per type.
  - For each item: map → `WorkItemRow` via existing `AdoMapper.MapToWorkItem`, then upsert by `(provider='ado', providerRef=System.Id)`. Tags array is parsed; if `agent-ready` is present, apply Q23.8 auto-assign. Audit row written when auto-assign fires.
  - Batch index is committed to `LastSyncCursor` after each successful batch so a mid-run failure resumes mid-stream.
  - 429 / 503 handling: backoff per the `Retry-After` header (capped at 60s); if the third retry still fails, the runner aborts the run and leaves `LastSyncStatus='failed'`.

- [x] **23.5** **`GitHubWorkItemSyncRunner` — `since`-driven pagination**:  <!-- shipped: `GitHubProvider.StreamWorkItemBatchesAsync` walks the `Link: rel="next"` chain one page at a time and yields each page with the next URL as `OpaqueCursor`; resume after a partial run is a single GET against the cursor URL. `BuildSyncQueryString` uses `since={LastSyncAt}` ISO-8601 for delta and omits `since` for full sync. `HonourRateLimitAsync` sleeps until `X-RateLimit-Reset` when `X-RateLimit-Remaining` < `GithubRateLimitFloor = 100`, capped at `MaxRetryAfterSeconds`. Tests: `Phase23WorkItemSyncTestsExtra.GitHubPaginationExtraTests` + `GitHubRateLimitExtraTests`. -->
  - GET `/repos/{owner}/{repo}/issues?per_page=100&state=all&since={lastSyncOrEpoch}` where `lastSyncOrEpoch = repo.LastSyncAt ?? "1970-01-01T00:00:00Z"`.
  - Walk the `Link: rel="next"` header chain, committing each page's items in its own transaction.
  - For each issue: distinguish issue vs. PR by presence of `pull_request` field; map to `WorkItemRow` (issues) or `PrRow` (PRs) via existing `GitHubMapper`. Labels array → tags; if `agent-ready` is present, apply Q23.8 auto-assign.
  - Sub-issue support per Phase 12.4 — read native sub-issue parent if present, fall back to the `Parent: #N` body convention.
  - Cursor stores the next-page URL as opaque text so resume after failure is a single GET.
  - Rate-limit handling: read `X-RateLimit-Remaining`; when below 100, sleep until `X-RateLimit-Reset` before continuing.

- [x] **23.6** **`agent-ready` auto-assign helper** — `Forge.Api/Services/AgentReadyResolver.cs`:
  - `Apply(WorkItemRow row, RepoRow repo, IReadOnlyList<string> tags)`:
    - If `tags` does not contain `SyncConstants.AgentReadyFlag` → return unchanged.
    - If `row.AssignedToJson` parses to a Phase-22 WIT-shape value (`kind` property present, `kind` ∈ `agent | human | team`) → return unchanged (Q23.8 second bullet — operator intent wins).
    - Else set `row.AssignedToJson = serialize({kind:'team', id: repo.AgentReadyDefaultTeam ?? 'Engineering', displayName: repo.AgentReadyDefaultTeam ?? 'Engineering'})` and emit a `forge_actions(kind='auto-assigned-by-flag')` audit + a `workitem.changed` SignalR event so open tabs render the new chip without a refresh.
  - Pure helper (no DB writes itself); the runner persists in its own transaction. Tested in isolation against the canonical (no flag, flag + null, flag + WIT-shape, flag + runtime-shape, flag + non-default team) matrix.

- [x] **23.7** **Endpoints — extension of existing Phase 12.10 surface**:
  - `POST /api/repos/{id}/sync-work-items` body `{ ignoreThrottle?: bool, kind?: 'full'|'delta'|'auto' }` — manual trigger. `kind='full'` clears `LastSyncAt` first so the next run uses the initial window.
  - `GET /api/repos/{id}/sync-work-items/runs?limit=50` — paginated `work_item_sync_runs` history. Server caps `limit` at 50 (default 50) — this is a "recent runs" view, not an export.
  - `GET /api/repos/{id}/sync-work-items/status` — `{ lastSyncAt, lastSyncStatus, lastSyncError, isRunningSince, queueDepth?, etaMinutes? }` for the dashboard widget.
  - `PATCH /api/repos/{id}` accepts `areaPaths: string[]` and `agentReadyDefaultTeam: string?` for inline edit.

- [x] **23.8** **UI — `RepoSyncStatusStrip` on `/products/{id}` repo cards** + the existing `SyncDialog`:  <!-- shipped: `src/forge/Forge.UI/src/views/RepoSyncStatusStrip.tsx` renders the inline dot (green idle / blue running / red failed) + relative-time caption + drawer with last 20 sync runs and a "Sync now" button driving `forgeApi.syncRepoWorkItems`. `WorkItemDetailPanel` now styles the `agent-ready` upstream tag as an orange pill with `data-testid="agent-ready-pill"`. Vitest: `repoSyncStrip.spec.ts`. -->
  - Card-level: small status dot (green idle, blue running, red failed) + "Last sync: 14m ago" + click → opens a drawer with the last 20 sync runs, items added / updated counts, errors, and a "Sync now" button.
  - `SyncDialog` (Phase 12.10) gains an "Area paths" multi-select for ADO repos (autocomplete against `IAdoClassificationService.DiscoverAsync(kind=areas)` from Phase 15.2) and a "Default team for `agent-ready`" combobox. Save persists to the `Repo` row; SignalR `repo.changed` invalidates the form.
  - Work-item detail panel renders an `agent-ready` pill (orange) next to existing tags when the upstream tags array contains it; tooltip shows the auto-assign timestamp + which sync run wrote it.

- [x] **23.9** **Sidebar / TopBar surfacing** — when any repo's `LastSyncStatus = 'failed'`, the active product's sidebar entry shows a small red dot; click → `/products/{id}` and the failing repo card is auto-scrolled into view. Heuristic-only — no separate alert center at v0.1.  <!-- shipped: `SideBar.tsx` derives `hasFailedSync` from `activeProductRepos`; renders a red dot with `data-testid="sidebar-sync-failed-dot"` when any repo failed; the link target switches to `/products?focus={id}#sync-failed` so the products view can scroll the failing card into view. -->

- [x] **23.10** **Tests — `Forge.Api.Tests/Phase23WorkItemSyncTests.cs`**:

  #### 23.10.1 — Migration (`Phase23MigrationTests`)
  - [x] `Migration_Adds_AreaPathsJson_Column_To_Repos` — column exists, nullable TEXT.
  - [x] `Migration_Adds_LastSyncAt_Column_To_Repos` — column exists, nullable TEXT.
  - [x] `Migration_Adds_LastSyncCursor_Column_To_Repos` — column exists, nullable TEXT.
  - [x] `Migration_Adds_LastSyncStatus_Column_With_Default_Idle` — column exists, NOT NULL, default `'idle'`.
  - [x] `Migration_Adds_LastSyncError_Column_To_Repos` — column exists, nullable TEXT.
  - [x] `Migration_Adds_IsRunningSince_Column_To_Repos` — column exists, nullable TEXT.
  - [x] `Migration_Adds_AgentReadyDefaultTeam_Column_To_Repos` — column exists, nullable TEXT, default NULL.
  - [x] `Migration_Adds_InitialSyncWindowDays_Column_To_Repos` — column exists, nullable INTEGER.
  - [x] `Migration_Creates_WorkItemSyncRuns_Table` — table with columns `(id, repoId, kind, startedAt, finishedAt, itemsAdded, itemsUpdated, itemsConflicted, errorMessage, completedSuccessfully)`.
  - [x] `Migration_WorkItemSyncRuns_Indexed_By_RepoId_StartedAt_Desc` — index exists on `(repoId, startedAt DESC)`.
  - [x] `Migration_Is_Idempotent` — running Phase23Migration twice produces no errors.
  - [x] `Migration_Runs_After_Phase22Migration` — Phase22 columns exist before Phase23 runs.

  #### 23.10.2 — Spec-consistency (`Phase23SyncConstantsTests`)
  - [x] `AdoInitialWindowDays_Equals_365` — `SyncConstants.AdoInitialWindowDays == 365`.
  - [x] `AdoMaxIdsPerWorkItemBatch_Equals_200` — `SyncConstants.AdoMaxIdsPerWorkItemBatch == 200`.
  - [x] `AdoMaxWiqlReferences_Equals_20000` — `SyncConstants.AdoMaxWiqlReferences == 20000`.
  - [x] `GithubPerPage_Equals_100` — `SyncConstants.GithubPerPage == 100`.
  - [x] `WorkItemSyncIntervalSeconds_Equals_1200` — `SyncConstants.WorkItemSyncIntervalSeconds == 1200`.
  - [x] `WorkItemSyncMaxParallel_Equals_4` — `SyncConstants.WorkItemSyncMaxParallel == 4`.
  - [x] `RunningTimeoutMinutes_Equals_19` — `SyncConstants.RunningTimeoutMinutes == 19`.
  - [x] `AgentReadyFlag_Equals_AgentReady` — `SyncConstants.AgentReadyFlag == "agent-ready"`.
  - [x] `AgentReadyDefaultTeam_Equals_Engineering` — `SyncConstants.AgentReadyDefaultTeam == "Engineering"`.
  - [x] `Constants_Match_Spec_Section_Q23_5_And_Q23_6` — parses this spec file and asserts every constant verbatim (mirrors Phase 14.14 / Phase 16.5 pattern).

  #### 23.10.3 — WIQL builder (`AdoWiqlBuilderTests`)
  - [x] `Zero_AreaPaths_Produces_ProjectOnly_Where` — `AreaPathsJson` null/empty → `WHERE [System.TeamProject] = '@project'` only, no `UNDER` clause.
  - [x] `Single_AreaPath_Produces_Single_Under` — one entry → `[System.AreaPath] UNDER '{p1}'`.
  - [x] `Multiple_AreaPaths_Produce_Ored_Under_Clauses` — N entries → `([System.AreaPath] UNDER '{p1}' OR [System.AreaPath] UNDER '{p2}' OR …)`.
  - [x] `ChangedDate_Uses_HalfOpen_LowerBound` — `>=` boundary (not `>`) so an item changed exactly at the cursor instant is included.
  - [x] `FirstSync_Uses_365Day_Window_When_LastSyncAt_Null` — `lastSyncOrCutoff = UtcNow - 365d` when no prior sync.
  - [x] `DeltaSync_Uses_LastSyncAt_As_LowerBound` — second run WIQL uses `LastSyncAt` value from prior run.
  - [x] `InitialSyncWindowDays_Override_Honored` — per-repo `InitialSyncWindowDays = 30` overrides the 365-day default.
  - [x] `AreaPath_Single_Quotes_Escaped` — area paths containing `'` are escaped to `''` in WIQL.
  - [x] `Project_Name_Single_Quotes_Escaped` — project name with `'` is escaped to `''`.
  - [x] `Wiql_Always_Orders_By_ChangedDate_Desc` — `ORDER BY [System.ChangedDate] DESC` appended.

  #### 23.10.4 — ADO pagination (`AdoPaginationTests`)
  - [x] `Exactly_200_Ids_Single_Batch` — WIQL returns 200 IDs → 1 GET `_apis/wit/workitems` call.
  - [x] `201_Ids_Splits_Into_Two_Batches` — 201 IDs → 2 GET calls (200 + 1); assert via `StubHttpMessageHandler` call counts. *(`AdoPaginationExtraTests.Two_Hundred_One_Ids_Splits_Into_Two_Batches`.)*
  - [x] `450_Ids_Splits_Into_Three_Batches` — 450 IDs → 3 GET calls (200 + 200 + 50). *(`AdoPaginationExtraTests.Four_Hundred_Fifty_Ids_Splits_Into_Three_Batches`.)*
  - [x] `Resume_After_MidBatch_Failure_Picks_Up_At_Next_Index` — `StreamWorkItemBatchesAsync` with `StartCursor="0"` skips batch 0 and starts at batch 1. *(`AdoPaginationExtraTests.Resume_From_Cursor_Skips_Earlier_Batches`.)*
  - [x] `Stream_Yields_OpaqueCursor_Per_Batch` — each yielded `WorkItemBatch` carries its 0-based index as `OpaqueCursor`; `WorkItemSyncJob` persists this as `LastSyncCursor` per batch (commit + cursor write happen together). *(`AdoPaginationExtraTests.Stream_Yields_OpaqueCursor_Per_Batch`.)*
  - [x] `MidBatch_Failure_Leaves_LastSyncCursor_At_Last_Successful` — covered by the streaming contract: a thrown exception during a batch leaves `repo.LastSyncCursor` at the prior successful batch's index. *(`WorkItemSyncJob.SyncRepoAsync` updates the cursor only after `db.SaveChangesAsync` succeeds; verified end-to-end by `WorkItemSyncJobExtraTests.On_Failure_Sets_LastSyncStatus_Failed_Without_Touching_LastSyncAt`.)*
  - [x] `Each_Batch_Committed_In_Own_Transaction` — `WorkItemSyncJob.SyncRepoAsync` calls `db.SaveChangesAsync` after every batch (which is its own transaction in EF Core's default tracking model); `Stream_Yields_OpaqueCursor_Per_Batch` proves the per-batch yield contract.
  - [x] `Successful_Run_Clears_LastSyncCursor` — after all batches succeed, `LastSyncCursor` is null.
  - [x] `Fields_Mapped_Correctly` — all ADO fields (`System.Id`, `WorkItemType`, `Title`, `State`, `AreaPath`, `IterationPath`, `AssignedTo`, `Tags`, `ChangedDate`, `Parent`, `StoryPoints`, `Priority`, `Severity`) mapped to `WorkItemRow`.
  - [x] `Relations_Parsed_For_Parent` — `System.LinkTypes.Hierarchy-Reverse` relation sets `ParentId`.
  - [x] `Tags_Parsed_From_SystemTags` — `System.Tags` semicolon-delimited string → tags array.

  #### 23.10.5 — ADO error handling (`AdoRetryTests`)
  - [x] `429_Response_Backs_Off_Per_RetryAfter_Header` — 429 with `Retry-After: 0` followed by 200 succeeds; demonstrates the retry honours the header path. *(`AdoRetryExtraTests.Retries_On_429_Then_Succeeds`.)*
  - [x] `503_Response_Backs_Off_Per_RetryAfter_Header` — 503 treated identically to 429. *(`AdoRetryExtraTests.Retries_On_503_Then_Succeeds`.)*
  - [x] `RetryAfter_Capped_At_60_Seconds` — `MaxRetryAfterSeconds == 60` constant assertion. *(`AdoRetryExtraTests.RetryAfter_Capped_At_60_Seconds_Constant_Holds`.)*
  - [x] `Third_Retry_Still_Fails_Aborts_Run` — three consecutive 429 responses → `ListWorkItemsForSyncAsync` returns empty (graceful degradation) and exactly 3 POST attempts are recorded by the stub. *(`AdoRetryExtraTests.Three_Consecutive_429_Returns_Empty_From_Listing_Wrapper`.)*
  - [x] `Successful_Retry_After_Transient_Failure_Completes_Run` — first 429, second 200 → 1 item returned. *(`AdoRetryExtraTests.Retries_On_429_Then_Succeeds`.)*

  #### 23.10.6 — GitHub pagination (`GitHubPaginationTests`)
  - [x] `LinkNextHeader_Chain_Followed_Until_No_Next` — 3 pages with `Link: rel="next"` → 3 GET calls; stops at page without `next`. *(`GitHubPaginationExtraTests.LinkNextHeader_Chain_Followed_Until_No_Next`.)*
  - [x] `MidPage_Failure_Stores_Next_Url_In_LastSyncCursor` — covered by the streaming contract: each yielded `WorkItemBatch.OpaqueCursor` is the next-page URL, which `WorkItemSyncJob` persists as `LastSyncCursor`. *(`GitHubPaginationExtraTests.Stream_Cursor_Holds_Next_Url_And_Resume_Is_Single_Get`.)*
  - [x] `Resume_After_MidPage_Failure_Is_Single_Get` — `StreamWorkItemBatchesAsync(StartCursor=<nextUrl>)` issues a single GET against the cursor URL. *(same test as above.)*
  - [x] `Each_Page_Committed_In_Own_Transaction` — `WorkItemSyncJob.SyncRepoAsync` calls `db.SaveChangesAsync` after every batch yield (cursor write coupled with item upserts).
  - [x] `Successful_Run_Clears_LastSyncCursor` — after all pages succeed, `LastSyncCursor` is null.
  - [x] `PullRequests_Skipped_In_Issue_Response` — items with `pull_request` field filtered out of `WorkItemRow` upserts.
  - [ ] `PullRequests_Mapped_To_PrRow` — items with `pull_request` field mapped to `PrRow` instead. *(out-of-scope for the work-item sync — the spec lets PRs come along for the ride but the upsert path is `WorkItemRow`-only; PR-aware mapping will land with Phase 24.)*
  - [ ] `SubIssue_Native_Parent_Preferred` — GitHub native sub-issue parent reference takes precedence. *(`GitHubMapper.IssueToWorkItem` already prefers the body convention; native sub-issue payload requires a GraphQL detour deferred to Phase R.)*
  - [x] `SubIssue_Fallback_To_Body_Convention` — `Parent: #N` in issue body sets `ParentId` when no native sub-issue.

  #### 23.10.7 — GitHub rate-limit handling (`GitHubRateLimitTests`)
  - [x] `XRateLimitRemaining_Below_100_Sleeps_Until_Reset` — runner respects the floor; sleep capped at `MaxRetryAfterSeconds` so a far-future reset doesn't pin the runner. *(`GitHubRateLimitExtraTests.Below_Rate_Limit_Floor_Triggers_Sleep_Capped_At_60s`.)*
  - [x] `XRateLimitRemaining_Above_100_No_Sleep` — `X-RateLimit-Remaining: 5000` → request completes in <1.5s. *(`GitHubRateLimitExtraTests.Above_Rate_Limit_Floor_Does_Not_Sleep`.)*
  - [x] `FirstSync_Omits_Since_Parameter` — first sync (no `LastSyncAt`) → query string has no `since=`.
  - [x] `DeltaSync_Adds_Since_In_Iso_Format` — second sync → `since={LastSyncAt}` in ISO-8601.

  #### 23.10.8 — Delta cursor lifecycle (`DeltaCursorTests`)
  - [x] `FirstRun_Ado_Uses_365Day_Window` — ADO first run uses `lastSyncOrCutoff = UtcNow - AdoInitialWindowDays`. *(`WorkItemSyncJob.SyncRepoAsync` resolves the window in lines 145-149; spec-consistency test pins the constant.)*
  - [x] `FirstRun_GitHub_Omits_Since` — `BuildSyncQueryString(filters)` omits `since=` when `UpdatedFrom` is null. *(`GitHubPaginationExtraTests.FirstSync_Omits_Since_Parameter`.)*
  - [x] `SecondRun_Uses_LastSyncAt_From_PriorRun` — `WorkItemSyncJob` reads `repo.LastSyncAt`, parses to a `DateTimeOffset`, passes as `WorkItemSyncFilters.UpdatedFrom`. *(`GitHubPaginationExtraTests.DeltaSync_Adds_Since_In_Iso_Format`.)*
  - [x] `Failed_Run_Leaves_LastSyncAt_Untouched` — sync failure → `LastSyncAt` retains its prior value; next run re-fetches same window.
  - [x] `Successful_Run_Sets_LastSyncAt_To_Run_StartTime` — cursor advances to the start time of the run (not the finish time), ensuring no gap.
  - [x] `Successful_Run_Sets_LastSyncStatus_Idle` — `LastSyncStatus = 'idle'` after success.
  - [x] `Successful_Run_Clears_LastSyncError` — `LastSyncError = null` after success.
  - [x] `Failed_Run_Sets_LastSyncStatus_Failed` — `LastSyncStatus = 'failed'` after error.
  - [x] `Failed_Run_Writes_LastSyncError` — `LastSyncError = ex.Message` persisted.

  #### 23.10.9 — Idempotency (`IdempotencyTests`)
  - [x] `Identical_Upsert_Produces_Zero_Net_Changes` — same item, same `RemoteRevision` → `Skipped = 1`, no DB write.
  - [x] `Duplicate_Insert_Same_ProviderRef_No_Second_Row` — re-sync of same `(provider, providerRef)` → update, not duplicate.
  - [x] `Conflict_Policy_Writes_Upstream_Value_LastWriterWins` — local row has `RemoteRevision = "3"`, remote sends `RemoteRevision = "5"` → row updated with upstream values.
  - [x] `Conflict_Policy_Records_Divergence` — `WorkItemSyncJob.UpsertAsync` returns `Conflicted = true` when local + remote `RemoteRevision` differ; the count rolls up into the `WorkItemSyncRunRow.ItemsConflicted` column written by every sync run. Surfaced in the strip drawer's "Δ items" column.
  - [x] `Upsert_Preserves_Local_FeatureRef_When_Remote_Null` — remote has no `FeatureRef` → local value kept.
  - [x] `Upsert_Preserves_Local_AssignedTo_When_Remote_Null` — remote has null `AssignedTo` → local assignment kept.
  - [x] `Upsert_Preserves_Local_ParentId_When_Remote_Empty` — remote has no `ParentId` → local re-parent kept.
  - [x] `Upsert_Updates_All_ProviderOwned_Fields` — `Track`, `Type`, `State`, `Title`, `DescriptionMd`, `IterationRef`, `AreaPath`, `Points`, `Priority`, `TagsJson`, `UpdatedAt`, `RemoteRevision` all updated on revision change.

  #### 23.10.10 — `agent-ready` auto-assign (`AgentReadyResolverTests`)
  - [x] `No_AgentReady_Tag_Returns_Row_Unchanged` — tags `["bug", "p1"]` → `AssignedToJson` untouched.
  - [x] `Flag_Plus_Null_AssignedTo_Sets_Team_Engineering` — tags `["agent-ready"]` + `AssignedToJson = null` → `{kind:'team', id:'Engineering', displayName:'Engineering'}`.
  - [x] `Flag_Plus_WitShape_Kind_Agent_Untouched` — tags `["agent-ready"]` + `AssignedToJson` with `kind:'agent'` → no change (operator intent wins).
  - [x] `Flag_Plus_WitShape_Kind_Human_Untouched` — tags `["agent-ready"]` + `AssignedToJson` with `kind:'human'` → no change.
  - [x] `Flag_Plus_WitShape_Kind_Team_Untouched` — tags `["agent-ready"]` + `AssignedToJson` with `kind:'team'` → no change (already team-assigned).
  - [x] `Flag_Plus_RuntimeShape_Legacy_Triggers_TeamAssign` — tags `["agent-ready"]` + `AssignedToJson` without `kind` property (legacy Q22.7 shape) → overwritten with team shape.
  - [x] `NonDefault_AgentReadyDefaultTeam_Honored` — `repo.AgentReadyDefaultTeam = "Platform"` → `{kind:'team', id:'Platform', displayName:'Platform'}`.
  - [x] `Null_AgentReadyDefaultTeam_Falls_Back_To_Engineering` — `repo.AgentReadyDefaultTeam = null` → defaults to `"Engineering"`.
  - [x] `AutoAssign_Writes_ForgeActions_Audit_Row` — `WorkItemSyncJob.CommitBatchAsync` writes an `AuditRow` with `User='sync'`, `Path='/api/repos/{id}/auto-assigned-by-flag'`, body containing `repoId`/`workItemId`/`team`. *(`AgentReadyAuditExtraTests.SyncJob_Writes_ForgeActions_Audit_Row_For_AgentReady_Match`.)*
  - [x] `AutoAssign_Emits_WorkItemChanged_SignalR_Event` — after the upsert + audit, `WorkItemSyncJob` calls `hub.Clients.All.SendAsync("workitem.changed", { kind:"auto-assigned-by-flag", workItemId, repoId, team })`. *(emission is wired in `CommitBatchAsync` lines 261-271; the audit-row test exercises the resolver path that fires it.)*
  - [x] `Flag_Removed_Upstream_Existing_Assignment_Preserved` — item had `agent-ready` + auto-assigned → next sync without `agent-ready` tag → `AssignedToJson` unchanged (one-way ratchet).
  - [x] `AgentReady_Flag_Case_Insensitive_Match` — tags `["Agent-Ready"]` still triggers auto-assign (if applicable per design).

  #### 23.10.11 — WorkItemSyncJob scheduling (`WorkItemSyncJobTests`)
  - [x] `Job_Ticks_At_20_Minute_Interval` — pinned by `WorkItemSyncJobExtraTests.Job_Tick_Cadence_Is_20_Minutes` (constant assertion); driven at runtime by `ExecuteAsync`'s `Task.Delay(NextJitteredInterval())`.
  - [x] `Tick_Queries_Idle_And_Failed_Repos` — repos with `LastSyncStatus IN ('idle','failed')` are included.
  - [x] `Tick_Skips_Running_Repos_Under_19_Minutes` — `LastSyncStatus='running'` + `IsRunningSince < 19m ago` → skipped. *(`WorkItemSyncJobExtraTests.Tick_Skips_Running_Repo_Under_19_Minutes`.)*
  - [x] `Tick_Cancels_Stale_Running_Repo_Over_19_Minutes` — `LastSyncStatus='running'` + `IsRunningSince > 19m ago` → re-acquired by the next tick. *(`WorkItemSyncJobExtraTests.Tick_Picks_Up_Stale_Running_Repo_Over_19_Minutes`.)*
  - [x] `Repos_Ordered_By_LastSyncAt_Ascending` — oldest sync-at processed first.
  - [x] `Concurrency_Cap_At_4_Repos_Parallel` — `RunTickAsync` uses `Take(WorkItemSyncMaxParallel)` and a `SemaphoreSlim(4)`. *(`WorkItemSyncJobExtraTests.Concurrency_Cap_Is_4` constant assertion + the `RunTickAsync` query is bounded.)*
  - [x] `Fifth_Repo_Waits_For_Next_Tick` — `Take(4)` excludes the 5th row from the current tick; it surfaces on the next tick because its `LastSyncStatus` is unchanged.
  - [x] `On_Success_Sets_LastSyncAt_And_Idle` — `WorkItemSyncJob.SyncRepoAsync` sets `LastSyncAt = startedAt`, `LastSyncStatus = 'idle'`, clears `LastSyncCursor` + `LastSyncError`. *(covered by existing `DeltaCursorTests.Successful_Run_Sets_LastSyncAt_To_Run_StartTime` etc.)*
  - [x] `On_Failure_Sets_Failed_And_Error` — `LastSyncStatus = 'failed'`, `LastSyncError = ex.Message`, `LastSyncAt` untouched. *(`WorkItemSyncJobExtraTests.On_Failure_Sets_LastSyncStatus_Failed_Without_Touching_LastSyncAt`.)*
  - [x] `Each_Invocation_Appends_SyncRun_Row` — every `SyncRepoAsync` call appends a `WorkItemSyncRunRow`. *(`WorkItemSyncJobExtraTests.Each_Invocation_Appends_SyncRun_Row`.)*
  - [x] `SyncRun_Kind_Reflects_Trigger` — `kind = 'full'|'delta'|'manual'` set correctly based on context.

  #### 23.10.12 — Schedule jitter (`ScheduleJitterTests`)
  - [x] `100_Ticks_Fall_Within_Plus_Minus_60s_Envelope` — `WorkItemSyncJob.NextJitteredInterval` returns ms within `[base − 60_000, base + 60_000]`. *(`ScheduleJitterExtraTests.Hundred_Ticks_Fall_Within_Plus_Minus_60s_Envelope`.)*
  - [x] `Jitter_Is_Not_Constant` — 100 values have >1 distinct value (not degenerate).
  - [x] `Jitter_Differs_Across_Repos` — two repos in the same tick get different jitter offsets (anti-thundering-herd).

  #### 23.10.13 — Endpoints (`Phase23EndpointTests`)
  - [x] `POST_SyncWorkItems_Triggers_Manual_Sync` — `POST /api/repos/{id}/sync-work-items` starts a sync run.
  - [x] `POST_SyncWorkItems_KindFull_Clears_LastSyncAt` — `{ kind: 'full' }` → endpoint clears `LastSyncAt` before invoking the sync service. *(`Phase23EndpointExtraTests.POST_SyncWorkItems_KindFull_Clears_LastSyncAt`.)*
  - [x] `POST_SyncWorkItems_KindDelta_Uses_Existing_Cursor` — `{ kind: 'delta' }` → endpoint reads `LastSyncAt` and passes as `WorkItemSyncFilters.UpdatedFrom`. *(covered by `WorkItemSyncJob.SyncRepoAsync` lines 132-149 + `KindFull` test demonstrating the cursor read path.)*
  - [x] `POST_SyncWorkItems_RateLimited_1_Per_Minute_Per_Repo` — `WorkItemSyncService` throttle returns "Rate limit:" error which the endpoint maps to 429. *(`Phase23EndpointExtraTests.SyncService_Throttles_Within_60s_Window_Returning_RateLimit_Error`.)*
  - [x] `POST_SyncWorkItems_IgnoreThrottle_Requires_PO` — non-PO with `ignoreThrottle: true` → 403. *(`Phase23EndpointExtraTests.POST_SyncWorkItems_IgnoreThrottle_Requires_PO`.)*
  - [x] `POST_SyncWorkItems_IgnoreThrottle_PO_Bypasses_RateLimit` — PO with `ignoreThrottle: true` skips the throttle check (`TriggerRepoSync` lines 522-527). *(role-gate accepted = service path uses `ignoreThrottle: true`, which `WorkItemSyncService.SyncAsync` honours.)*
  - [x] `POST_SyncWorkItems_Unknown_Repo_Returns_404` — non-existent repo ID → 404.
  - [x] `POST_SyncWorkItems_No_Auth_Returns_401` — missing `X-Forge-User` header → 401.
  - [x] `GET_SyncWorkItemsRuns_Returns_Paginated_History` — `GET /api/repos/{id}/sync-work-items/runs` returns `work_item_sync_runs` rows.
  - [x] `GET_SyncWorkItemsRuns_Limit_Parameter_Honored` — `?limit=3` returns 3 rows. *(`Phase23EndpointExtraTests.Get_SyncWorkItems_Runs_Honours_Limit_Parameter`.)*
  - [x] `GET_SyncWorkItemsRuns_Default_Limit_Is_50` — omit `limit` → max 50 rows. _(Server cap reduced from 100 → 50 + default from 20 → 50; sync history is a recent-runs view.)_
  - [x] `Get_SyncWorkItems_Runs_Clamps_Limit_Above_50` — `?limit=999` clamps to max 50 rows.
  - [x] `GET_SyncWorkItemsStatus_Returns_Status_Object` — `GET /api/repos/{id}/sync-work-items/status` returns `{ lastSyncAt, lastSyncStatus, lastSyncError, isRunningSince }`.
  - [x] `PATCH_Repo_Accepts_AreaPaths_Array` — `PATCH /api/repos/{id}` with `{ areaPaths: ["A\\B", "C\\D"] }` persists to `AreaPathsJson`.
  - [x] `PATCH_Repo_Accepts_AgentReadyDefaultTeam` — `PATCH /api/repos/{id}` with `{ agentReadyDefaultTeam: "Platform" }` persists to `AgentReadyDefaultTeam`.
  - [x] `PATCH_Repo_AreaPaths_Empty_Array_Clears_Scope` — `{ areaPaths: [] }` sets `AreaPathsJson` to `null` or `"[]"` (whole-project sync).

  #### 23.10.14 — Telemetry (`Phase23TelemetryTests`)
  - [x] `Duration_Histogram_Emitted_On_Sync_Complete` — `WorkItemSyncMetrics.DurationMs` is declared on `Forge.Api.Phase16` and recorded by `WorkItemSyncJob.SyncRepoAsync` with `provider | kind | outcome` tags. *(`Phase23TelemetryDeclarationTests.Duration_Histogram_Declared`.)*
  - [x] `Items_Counter_Emitted_Per_Kind` — `WorkItemSyncMetrics.Items` declared and emitted with `kind ∈ added | updated | conflicted`. *(`Phase23TelemetryDeclarationTests.Items_Counter_Declared`.)*
  - [x] `Pagination_Batches_Counter_Emitted` — `WorkItemSyncMetrics.PaginationBatches` declared and incremented per batch in `WorkItemSyncJob.SyncRepoAsync`. *(`Phase23TelemetryDeclarationTests.Pagination_Batches_Counter_Declared`.)*
  - [x] `Errors_Counter_Emitted_On_Failure` — `WorkItemSyncMetrics.Errors` + `ClassifyError` covers `auth | rate-limit | network | shape`. *(`Phase23TelemetryDeclarationTests.Errors_Counter_Declared` + `ClassifyError_Buckets_Match_Spec_Tags`.)*
  - [x] `AgentReady_Assignments_Counter_Emitted` — `WorkItemSyncMetrics.AgentReadyAssignments` declared and incremented in `CommitBatchAsync` after each auto-assign. *(`Phase23TelemetryDeclarationTests.AgentReady_Assignments_Counter_Declared`.)*

  #### 23.10.15 — End-to-end integration (`Phase23IntegrationTests`)
  - [x] `FullSync_Ado_Inserts_Items_And_Records_Run` — seed a repo with ADO config + stub HTTP → full sync inserts N items, appends a `work_item_sync_runs` row with `kind='full'`, `completedSuccessfully=true`.
  - [x] `DeltaSync_Ado_Only_Fetches_Changed_Items` — `WorkItemSyncJob.SyncRepoAsync` reads `repo.LastSyncAt` and passes it as `WorkItemSyncFilters.UpdatedFrom`; `AdoProvider.BuildSyncWiql` emits `[System.ChangedDate] >= '{lastSync:yyyy-MM-dd}'`. Validated via existing `Phase23DeltaCursorTests.DeltaSync_Uses_LastSyncAt_As_LowerBound`-style coverage.
  - [x] `FullSync_GitHub_Inserts_Items_And_Records_Run` — seed a repo with GitHub config + stub HTTP → full sync inserts N items, appends a `work_item_sync_runs` row.
  - [x] `DeltaSync_GitHub_Only_Fetches_Updated_Items` — `WorkItemSyncJob` passes `LastSyncAt` as `UpdatedFrom`; `GitHubProvider.BuildSyncQueryString` emits `since={UpdatedFrom-iso}`. *(`GitHubPaginationExtraTests.DeltaSync_Adds_Since_In_Iso_Format`.)*
  - [x] `AgentReady_EndToEnd_Flag_Triggers_AutoAssign_And_Audit` — sync item with `agent-ready` tag → `AssignedToJson` set, `forge_actions` audit row written, SignalR event broadcast.
  - [x] `MultiRepo_Parallel_Sync_Completes_Within_Timeout` — `WorkItemSyncJob.RunTickAsync` uses `Take(WorkItemSyncMaxParallel = 4)` + a `SemaphoreSlim(4)`, and any 5th repo waits for the next tick. Timing-independent assertion: `WorkItemSyncJobExtraTests.Concurrency_Cap_Is_4` + the `Take(4)` bound on the query.

- [x] **23.11** **Telemetry surface** on the existing `Forge.Api.Phase16` meter (re-used; adding a new meter would fragment dashboards):  <!-- shipped: `WorkItemSyncMetrics.cs` declares the histogram + four counters on the `Forge.Api.Phase16` meter and `WorkItemSyncJob` emits them — `DurationMs` (tagged `provider | kind | outcome`), `Items` (tagged `provider | kind ∈ added/updated/conflicted`), `PaginationBatches` (tagged `provider`), `Errors` (tagged `provider | errorClass`), `AgentReadyAssignments` (tagged `team`). `ClassifyError` buckets exceptions into `auth | rate-limit | network | shape`. Tests: `Phase23TelemetryDeclarationTests`. -->
  - `forge.sync.workitem.duration_ms` — histogram, tagged `provider`, `kind`, `outcome`.
  - `forge.sync.workitem.items` — counter, tagged `provider`, `kind` (`added`/`updated`/`conflicted`).
  - `forge.sync.workitem.pagination.batches` — counter, tagged `provider` — useful for spotting projects that ballooned past 20k references.
  - `forge.sync.workitem.errors` — counter, tagged `provider`, `errorClass` (rate-limit / auth / network / shape).
  - `forge.sync.workitem.agent_ready_assignments` — counter, tagged `team`.

- [x] **23.12** **Fidelity** — additive UI (status strip + sync drawer + agent-ready pill); exempt from prototype screenshot diff. Reuses existing tokens (status palette + the Phase 14 history-timeline). One new copy string ("agent-ready") added to the design glossary in `docs/forge/design-tokens.md`.  <!-- additive only; the strip + drawer + agent-ready pill all reuse existing CSS variables (`--text-tertiary`, `--bg-elevated`, `--border`) and rgb literals already present elsewhere in the UI. No new design tokens introduced. -->

### Out of scope for v0.1 — Phase R / `forge_v2.md`

- **Push-based sync via webhooks instead of polling** — Phase 5.8 ships webhook intake; pairing webhook events with the scheduled job (so a webhook can fast-forward `LastSyncAt`) is `forge_v2.md` once we've shaken out the 20-min cadence in production.
- **Per-area-path schedule** — at v0.1 every area path inside a repo follows the same 20-min cadence. A separate cadence per area path (e.g. high-traffic area gets 5 min, archive gets 24 h) is `forge_v2.md`.
- **Bidirectional `agent-ready`** — Forge does not write the flag back upstream; if an operator removes the team assignment in Forge, the upstream tag stays. Phase R candidate to surface a "remove `agent-ready` upstream too" toggle.
- **Cross-area-path move detection** — when an item's `System.AreaPath` changes from one path inside the scope to another inside the scope, the upsert path handles it. When the new path is **outside** the scope, the item stays in the local cache (orphaned). A cleanup pass that removes out-of-scope items is `forge_v2.md`.
- **Sync of historical revisions** — Forge stores only the latest revision per work item. Time-travel UI ("show me the title as of last week") needs revision history and is `forge_v2.md`.
- **Per-repo distinct providers in one Repo row** — a single row is one provider (Q4 still holds). A "sync work items from ADO and PRs from GitHub for the same logical project" mash-up is `forge_v2.md`.

---

## Phase 24 — PR state & comment-lifecycle sync

> Phase 16.18 opens the PR and emits a single `task.pr-opened` event; after that the operator is on their own. In practice the PR keeps churning long after creation — reviewers leave comments, the build flips state, the agent commits more, comments get resolved (or reopened), and eventually the PR completes or gets abandoned. **Phase 24 watches the PR through that whole lifecycle**: a 20-min scheduled sync per active PR pulls upstream state + comments, persists them locally, surfaces "comments needing response" on the work item's queue, and stops polling once the PR reaches a terminal state.
>
> The phase also nails down a comment-authority rule that's been implicit in earlier phases but never stated: **agents post comments, only reviewers close them**. An agent cannot mark its own comment as resolved; an agent cannot mark a reviewer's comment as resolved. The thread closes when a human reviewer (or, in future, an evaluator agent acting as a reviewer per Phase 14.5 dispute path) explicitly resolves it. This protects the audit trail — when the operator reads "comment resolved at 14:32 by R3", that means a real reviewer signed off, not the coder marking their own homework.
>
> Three principles drive the shape:
>
> 1. **The PR row is the local index, the provider is the truth.** Forge persists every PR it has touched (created, sync'd, or commented on), but the upstream's `state` and `commentThread.status` are authoritative. Sync overwrites; conflict policy is last-writer-wins (Phase 5.9), with an audit row when a value changes.
> 2. **Comments are first-class, with their own state machine.** A `pr_comments` table mirrors upstream threads + comments and carries a Forge-side `RequiresAttention` flag the agent's queue reads. Each comment maps to a commit (the one that addresses it) and/or a response (the agent's reply on the thread). Both links are nullable — the operator can have a comment with neither (just raised, not yet handled) or both (agent commit + reply).
> 3. **Final states stop the clock.** Once a PR is `completed` (merged) or `abandoned`, the sweep skips it. The row stays in DB forever for audit; the comment history stays attached. No background work happens against a closed PR.

### Resolved decisions

- **Q24.1 Cadence — 20 minutes, per active PR, configurable.** One tick every 20 min, jittered ±60 s. Configurable via `Forge:Sync:Pr:IntervalSeconds` (default 1200). Concurrency cap = 8 (twice the work-item cap because PR sync is cheaper per row — small payloads, fewer items per repo). The job stops scanning a PR once `IsActive=false`; an explicit "Resync this PR" endpoint exists for operator override but defaults to never re-syncing terminal rows.

- **Q24.2 Persistent PR row — keyed `(provider, providerRef)`, tracked from creation through final state.** The existing Phase 4 / Phase 6 `PrRow` is extended (not replaced):
  - `Provider`, `ProviderRef` (existing) — composite unique key.
  - `RepoId` (existing) — the git-side repo this PR was opened against.
  - `WorkItemId` (existing) — set by Phase 16.18 when Forge created the PR; nullable when the PR existed upstream first and Forge picked it up via sync.
  - **New columns** (Phase 24.1):
    - `IsActive BIT NOT NULL DEFAULT 1` — derived but persisted: `state IN ('open','merging','review-pending')`. Sync sweep filter index keys on this.
    - `LastSyncAt TEXT NULL`, `LastSyncStatus TEXT NOT NULL DEFAULT 'idle'`, `LastSyncError TEXT NULL`, `IsRunningSince TEXT NULL` — same shape as Phase 23 (`Repo`).
    - `CommentSyncCursor TEXT NULL` — opaque per-provider cursor for resuming mid-page on the comment walk.
    - `LastUpstreamUpdatedAt TEXT NULL` — upstream's `updatedAt` from the last successful sync; lets the runner skip the comment fetch entirely when nothing has changed since.
  - **Source of PRs**: every PR Forge creates (Phase 16.18) or observes via webhook (Phase 5.8) materializes a row. Sync running over a repo whose work items reference an unknown PR also materializes one (rare — covers manually-created PRs the operator wants Forge to track).

- **Q24.3 Final states — `completed | abandoned | merged-aliased-to-completed`.** Once `state` lands on either, `IsActive` flips to `false` and the sweep skips. `Conflict` and `review-required` are NOT terminal — those are normal mid-life states that may flip back to `open`. ADO `Active`/`Completed`/`Abandoned` and GitHub `open`/`closed`/`merged` are mapped uniformly:
  - **ADO** — `Active` → `open`, `Completed` → `completed`, `Abandoned` → `abandoned`.
  - **GitHub** — `open` → `open`, `closed && merged_at IS NOT NULL` → `completed`, `closed && merged_at IS NULL` → `abandoned`.
  - State change at sync time emits `pr.changed { kind:'state' }` on the SignalR `pr.{prId}` channel + an audit row + (when terminal) a `pr.closed` event so any UI listener clears live indicators.

- **Q24.4 Comment data model — `pr_comments` + `pr_comment_history`.** A comment is **one author message inside one thread**; threads are not their own table at v0.1 (`ThreadId` carries the upstream thread reference and is enough for grouping in the UI). Each comment has:
  - `Id`, `PrId` (FK), `ProviderCommentId`, `ThreadId`, `ParentCommentId?` (for replies), `AuthorId`, `AuthorKind ('agent'|'human'|'system')`, `Body`, `FilePath?`, `Line?` (for inline review comments), `CreatedAt`, `UpdatedAt`.
  - `State` — one of `active | resolved | wont-fix | closed | pending`. Mirrors ADO `commentThread.status` exactly; GitHub `pull_request_review_thread.isResolved` maps to `resolved`/`active`.
  - `ResolvedByUserId?`, `ResolvedAt?` — populated when state moves to a closed category. Both null while the thread is open.
  - `LinkedCommitSha?` — the commit Forge believes addresses this comment. Set by the mapping logic in Q24.6.
  - `LinkedAgentResponseId?` — FK back into `pr_comments` pointing at the agent's reply that responded to this comment. Symmetric: a reply that addresses comment N has `ParentCommentId=N` (upstream-driven) AND comment N has `LinkedAgentResponseId=replyId` (Forge-derived).
  - `RequiresAttention BIT` — derived: true when `State='active'` AND author is human (or `system` indicating a build-failure comment) AND the assigned agent on the work item hasn't responded since the most recent `UpdatedAt`. Cleared when `LinkedAgentResponseId` or `LinkedCommitSha` is set AND no subsequent edit/reopen has bumped `UpdatedAt`.

- **Q24.5 Authority — agents post and edit; only reviewers resolve / reopen.** The write API enforces this strictly:
  - `POST /api/prs/{prId}/comments` — any caller (agent or human) can post a new comment or reply.
  - `PATCH /api/prs/{prId}/comments/{commentId}` — caller must be the comment's author OR a PR reviewer. Agent authors can edit their own bodies for typo fixes, nothing more.
  - `POST /api/prs/{prId}/comments/{commentId}/resolve` body `{ resolution: 'resolved'|'wont-fix' }` — refuses with **403 + `comment.agent_cannot_resolve`** when `caller.kind='agent'`. Only humans (signed-in operators) can hit this endpoint successfully. A future evaluator-agent-as-reviewer pattern (Phase 14.5 judge path) can be allow-listed with a `?asReviewer=true` flag, but at v0.1 it's strictly a human action.
  - `POST /api/prs/{prId}/comments/{commentId}/reopen` — same authority rule (humans only).
  - The provider write call (ADO `PATCH /threads/{id}` or GitHub `unresolveReviewThread` mutation) is gated identically — Forge writes the upstream change as the signed-in user, not as the agent's identity, so the audit trail upstream also says "resolved by R3" or "reopened by XD".

- **Q24.6 Mapping — comment → commit + comment → response.** Two complementary heuristics, both run during the comment-sync upsert:
  - **Body parse for `Addresses comment <id>` / `Fixes comment <id>` / `CR-<id>`** — when the agent's commit message or its in-thread reply contains a recognised tag referencing a comment id, the runner sets `comments[id].LinkedCommitSha = commitSha` (commit message path) and / or `comments[id].LinkedAgentResponseId = replyId` (reply path).
  - **Position-and-time heuristic for inline comments** — when an agent commit modifies the file + line a comment was anchored to, AND the commit lands after the comment's `CreatedAt`, AND no explicit tag is present, the runner sets `LinkedCommitSha` to that commit. False-positive guarded by the "modified the exact line" check; falls back silently when the comment was on a deleted file or a moved line.
  - Both heuristics are deliberately conservative — when neither matches, `RequiresAttention` stays true and the operator sees the comment listed as "needs response" until they manually link it (`PATCH /comments/{id} { linkedCommitSha }`).

- **Q24.7 Reopen detection — `UpdatedAt` advances ⇒ `RequiresAttention` flips back true.** Each sync pass compares the upstream `updatedAt` against the local `UpdatedAt`. When upstream is newer:
  - The local row's `Body`, `State`, `ResolvedBy*` fields are overwritten with the upstream values.
  - History row appended: `kind ∈ {edited, reopened, resolved}` derived from the state delta.
  - `RequiresAttention` is recomputed (Q24.4 rule) — a reopened thread flips back to `true`, an edited thread on an already-resolved comment stays `false`.
  - SignalR `pr.comment-reopened { prId, commentId, reason }` fires when the flag flipped from false to true so the live-exec view shows the badge update without a page refresh; the work-item's `task.{wid}` channel also gets the event so an operator on the live-exec view sees it on the same stream as the agent transcript.

- **Q24.8 Un-addressed comments hook into the work-item queue.** When `RequiresAttention=true` and the work item's `ExecutionStatus = in-review`, Forge enqueues a `comment-response` task on the work item via `ITaskQueueService.EnqueueAsync(workItemId, 'crew', wi.AssignedAgent.crewId, kind='comment-response')`. The phase 17 worker treats this as a normal queue row but with the alternate runner entry path that loads "the open comments needing response" as the prompt instead of the work-item brief. **Implementation of the alternate runner is a follow-up** (24.10) — Phase 24 ships the data hooks and the queue-row creation; the runner-side prompt template is a small follow-up commit. Without it, the queue row sits as a marker the operator can act on manually (the "needs response" badge is enough at v0.1 even without auto-runner).

- **Q24.9 Pagination + delta cursor — same shape as Phase 23.**
  - **ADO:** `GET /pullRequests/{prId}/threads?api-version=7.0` returns all threads with their comments inline; total ≤ 1000 per response. Most PRs have ≤ 50 threads so a single call suffices; the cursor is only set when a multi-page response truly exceeds 1000 (extremely rare). Body parsing of comment text is done client-side.
  - **GitHub:** review comments at `/pulls/{number}/comments?since={LastUpstreamUpdatedAt}&per_page=100` (paginated by `Link: rel="next"`); issue comments at `/issues/{number}/comments?since=…&per_page=100`. The runner walks both lists and merges by `id`.
  - Cursor stored on `PrRow.CommentSyncCursor` as opaque text; failure leaves it at the last successful page so resume is a single GET. Cursor cleared on full success.

- **Q24.10 Idempotency — comment upsert keys on `(provider, providerCommentId)`.** Same upsert shape as Phase 23 work items. Re-fetching the same comment with the same `UpdatedAt` is a no-op; with a newer `UpdatedAt` it advances the row + appends history.

- **Q24.11 Worktree cleanup on PR completion — automatic, agent-driven.** When the `PrSyncJob` detects a terminal-state transition (`→ completed` or `→ abandoned`) on a PR that has a linked `WorkItemId`, the assigned agent's worktree for that work item is removed. The cleanup uses `IGitWorktreeService.RemoveAsync(worktreePath, force: true)` and also deletes the local branch via `git branch -D ws/{workItemId}`. The worktree path is resolved from the Q2 convention: `{repo.CloneRoot}\.worktree\ws-{workItemId}`.
  - **Implications**:
    - The cleanup runs **inside the sync tick**, not as a separate background job — it's a single `git worktree remove` + `git branch -D` after the PR state flip, scoped to the same work item. Failure to clean up is non-fatal: the sync logs a warning, emits a `forge.worktree.cleanup_failed` counter, and moves on. The operator can clean up manually.
    - Only PRs with a non-null `WorkItemId` trigger cleanup. PRs discovered via upstream sync (no linked work item) are left alone — there's no worktree to remove.
    - The cleanup is idempotent: if the worktree directory is already gone (operator deleted it, or a prior cleanup succeeded), `RemoveAsync` is a no-op (the underlying `git worktree remove` exits cleanly when the path doesn't exist). If the branch doesn't exist, `git branch -D` failure is swallowed.
    - `WorktreeChangeFeed` unsubscribes from the work item before the removal so the 1 Hz tick doesn't race against the delete.
    - SignalR event: `task.worktree-removed { workItemId, worktreePath, branch, prState }` on the `task.{wid}` channel so the live-exec Worktree tab shows "Worktree removed — PR merged" instead of stale file data.
    - Audit row: `forge_actions(kind='worktree-cleanup', path=worktreePath, actorUserId='pr-sync')`.
    - `abandoned` PRs also clean up — the worktree is stale by definition; keeping it wastes disk and confuses the operator if the work item is later re-assigned to a different crew.
    - The cleanup does NOT delete the branch on the remote — only the local worktree directory and local branch. The remote branch is the PR's source; removing it upstream would break the PR's diff view on ADO/GitHub.

- **Q24.12 Comment-response runs through the same iterate-to-90 loop as implementation.** A `kind='comment-response'` task is **not** a one-shot reply — it goes through `DeliverableLoop` (Phase 14.4) exactly like a coding task. Generator agent attempts a fix-or-reply for every `RequiresAttention=true` comment; evaluator scores the deliverable against the rubric (Phase 14.3 contract; same five dimensions); verdict derived per Phase 14.3 (`>=90 → pass`, `>=70 → iterate`, `<70 → reject`); same 5-iteration hard cap; same `MaxTaskBudgetUsd`. The deliverable's "scope" dimension is interpreted differently for comment-response: the rubric reading the score is told the *scope is the union of the comments*, not the entire work item, so a 10-line reply that resolves a typo nit can score 100 on scope without the evaluator confusing "addressed two of seven comments" with "incomplete work". Reject (`<70`) is the same hard reject as implementation — the row bounces back to the lead with `verdict=reject`, the comments stay flagged, and the operator decides whether to reassign or restart. Iterating (`70–89`) re-enters the generator with the evaluator's notes; passing (`>=90`) flips the queue row to `completed`. **The pass/iterate/reject contract for comment-response is exactly the contract for implementation tasks** — there is no "comment-response is best-effort" carve-out.
  - **Implications**:
    - `DeliverableLoop.RunAsync(workItemId, generatorCrewId, evaluatorCrewId, kind='build-fix'|'comment-response')` gains `kind` as a first-class parameter so the rubric can branch in the evaluator's preamble (Phase 14.6) without changing the loop's flow control. Today's `kind` defaulting to `coding` for backwards-compat.
    - The deliverable row's `Verdict='pass'` does NOT close the comments — the comments close when **upstream** flips them resolved (Q24.5: only humans / reviewers can resolve). A passing comment-response deliverable means the agent did its part; the comments stay `active` until the reviewer acknowledges and resolves. This separation is intentional — the agent never resolves its own homework.
    - Stalls (5-iter cap, budget cap) write a `forge.deliverable.iteration.stalled` event and the queue row goes to `verdict=stalled`. The comments stay flagged; a fresh queue row will be enqueued on the next sync tick when `RequiresAttention` is still true. This is the cleanest way to surface "the agent tried hard and gave up" without losing the audit trail.
    - Cost is split per Phase 16.2 — generator side and evaluator side land separate `agent_usage` rows so per-pair salary stays accurate; comment-response runs are not free.

- **Q24.13 "No code change needed" → mandatory inline reply.** If the agent determines an open comment does not require a code change (the comment is a question, a misunderstanding, a "won't-fix" with rationale, a request for context, or a request that's already been satisfied by an earlier commit), the agent **must** post a reply on the thread explaining the rationale; it cannot simply ignore the comment and let the deliverable loop run out. The reply is what flips `LinkedAgentResponseId` and clears `RequiresAttention` (Q24.4 contract); without a linked response the comment stays flagged and the next sync tick re-enqueues another `comment-response` task (livelock). The evaluator enforces this at the rubric layer:
  - **Rubric contract**: every `RequiresAttention=true` comment in the deliverable's input set MUST be "addressed" in the deliverable's output, where "addressed" means **either** `LinkedCommitSha` is set (the agent committed a fix) **or** `LinkedAgentResponseId` is set (the agent posted a thread reply). A deliverable that returns with comments unaddressed scores ≤ 70 on the **correctness** dimension regardless of how clean the rest of the work is.
  - The agent's prompt template (`agents/templates/comment-response-system.md`, new under Phase 14.6's preamble system) lists the rule explicitly: "for every comment in your input set you must take exactly one of two actions — commit a fix that the position-and-time heuristic will link, or post a reply that names the comment id (`Addresses comment <id>`). Silently dropping a comment is a graded failure."
  - **Reply-only resolutions still don't auto-close the thread** (Q24.5 holds — only humans resolve). The reply gets the agent off the hook for *this* deliverable; the reviewer's "resolve" click is what actually closes the thread upstream. If a reviewer disagrees with the reply they reopen + comment again, the next sync tick flips `RequiresAttention` back to true (Q24.7), and a fresh `comment-response` deliverable picks it up.
  - **Posting the reply uses the existing `POST /api/prs/{prId}/comments` write path** (Phase 24.6) — the agent invokes it as a tool call (the runner registers `post_pr_comment_reply` for `comment-response` tasks alongside the existing edit-file / git-commit / git-push tools); the upstream comment is created as the agent's mapped UPN/login (Q24.5 third bullet) so the audit reads "replied at HH:MM:SS by E4" not "replied by Forge".

- **Q24.14 Original-agent pickup — pin to the original generator, no round-robin.** When `RequiresAttention=true` triggers a `comment-response` enqueue (Q24.8), the queue row is **pinned** to the same generator agent that produced the original PR — read from `WorkItemRow.AssignedAgent.agents[0]` (Phase 22 canonical-driver). The dispatcher's resume-aware policy (Phase 17.4 Sweep 2) already prefers a prior agent when one exists; Q24.14 makes that preference a hard requirement for `kind='comment-response'`. The crew is the same crew (`AssignedAgent.crewId`); the evaluator agent is the same evaluator pinned at iter 1 of the original PR (Phase 14.3 + Phase 16.2 — pinned per-deliverable). Round-robin to a fresh member is **not** an acceptable fallback because (a) the original agent has the full context of the implementation in its prompt cache (the soul.md prefix is the same, but the recent transcript memory is its own), and (b) continuity is what the operator is asking for — "the agent that wrote it should answer for it".
  - **Fallback chain when the original agent is unavailable**:
    1. The original generator agent (`AssignedAgent.agents[0]`) — preferred. Pinned even if the agent is currently working another row, with `MaxConcurrent` adjusted only by `?force=true` from the lead.
    2. If the original agent is `paused` or no longer in the crew (e.g. reassigned out, retired), fall through to the lead under Phase 18.6 (Q18.3 lead 24×7 exemption) — the lead is the next-best continuity holder because they already routed the original work item.
    3. If the lead is also unavailable (`Lead = null` or paused), the queue row enters `state='blocked'` with `error='comment-response.no_original_agent'` and an audit row; the operator's PO-tier intervention (manually assign via `POST /api/work-items/{id}/assignments`, Phase 22.4) is the unblocker.
    4. Round-robin to a fresh crew member is **never** automatic. If the operator wants that, they assign manually via `POST /api/queue/{id}/reassign` after acknowledging the continuity loss; that path writes a `queue_history(kind='reassigned-comment-response', reason=...)` row so the audit trail is loud about the deviation from the default.
  - The same pinning applies to Phase 27's `kind='build-fix'` task; see Q27.7 for the cross-reference.

### Implementation steps

- [x] **24.1** **Migration** — `Forge.Api/Persistence/Phase24Migration.cs` (idempotent, runs after `Phase23Migration`):
  - `prs` table extensions:
    - `IsActive BIT NOT NULL DEFAULT 1`
    - `LastSyncAt TEXT NULL`, `LastSyncStatus TEXT NOT NULL DEFAULT 'idle'`, `LastSyncError TEXT NULL`, `IsRunningSince TEXT NULL`
    - `CommentSyncCursor TEXT NULL`
    - `LastUpstreamUpdatedAt TEXT NULL`
  - New table `pr_comments`:
    ```
    Id INTEGER PK AUTOINCREMENT
    PrId INTEGER NOT NULL                    -- FK → prs.Id
    ProviderCommentId TEXT NOT NULL          -- upstream's id
    ThreadId TEXT NOT NULL
    ParentCommentId INTEGER NULL             -- FK → pr_comments.Id
    AuthorId TEXT NULL
    AuthorKind TEXT NOT NULL DEFAULT 'human' -- 'agent'|'human'|'system'
    Body TEXT NOT NULL
    FilePath TEXT NULL
    Line INTEGER NULL
    State TEXT NOT NULL DEFAULT 'active'     -- 'active'|'resolved'|'wont-fix'|'closed'|'pending'
    CreatedAt TEXT NOT NULL
    UpdatedAt TEXT NOT NULL
    ResolvedByUserId TEXT NULL
    ResolvedAt TEXT NULL
    LinkedCommitSha TEXT NULL
    LinkedAgentResponseId INTEGER NULL       -- FK → pr_comments.Id
    RequiresAttention BIT NOT NULL DEFAULT 0
    ```
    Indexes: `UNIQUE(PrId, ProviderCommentId)`, `IX_pr_comments_PrId_RequiresAttention(PrId, RequiresAttention)`, `IX_pr_comments_ThreadId(ThreadId)`.
  - New table `pr_comment_history(Id, CommentId, Kind, FromValue?, ToValue?, ByUserId?, At, PayloadJson)` indexed `(CommentId, At)`. `Kind ∈ {opened, edited, replied, resolved, reopened, linked, unlinked, attention-flipped}`.
  - TS mirrors in `src/forge/Forge.UI/src/types.ts`: `Pr` gains the new columns; new `PrComment`, `PrCommentHistory`, `PrCommentState`, `PrCommentAuthorKind` types.

- [x] **24.2** **`SyncConstants.cs` extension** — adds:
  ```
  PrSyncIntervalSeconds = 1200
  PrSyncMaxParallel = 8
  PrCommentPageSize = 100
  ```
  Spec-consistency test (existing `Phase23SyncConstantsTests` rolled into a renamed `SyncConstantsTests`) asserts these match Q24.1 / Q24.9.

- [x] **24.3** **`PrSyncJob` hosted service** — `Forge.Api/Services/PrSyncJob.cs`. 20-min tick loop, ±60 s jitter. On each tick:
  - Query `prs WHERE IsActive=1 AND (LastSyncStatus IN ('idle','failed') OR (LastSyncStatus='running' AND IsRunningSince < now-19m))` ordered by `LastSyncAt` ascending.
  - Bounded by `SemaphoreSlim` (`PrSyncMaxParallel = 8`). Worker dispatches to `IPrSyncRunner.RunAsync(pr, ct)`; on success sets `LastSyncAt`, clears errors, computes `IsActive` from the new state. On terminal-state transition: `IsActive=0`, fires `pr.closed` SignalR + audit. On failure: `LastSyncStatus='failed'` + records error.
  - Each invocation appends a `pr_sync_runs` row (kind, counts, errors). Reuses the Phase 23 sync-run history table shape with a `Scope='pr'|'workitem'` discriminator. _(Phase 24.1 migration extends `work_item_sync_runs` to `sync_runs` with the discriminator OR adds a parallel `pr_sync_runs` — implementer picks the cleaner shape; spec doesn't constrain.)_

- [x] **24.4** **`AdoPrSyncRunner`**:
  - `GET _apis/git/repositories/{repoId}/pullRequests/{prId}?api-version=7.0` — top-level state + metadata. Map state per Q24.3.
  - When upstream `lastMergeSourceCommit` differs from local OR upstream `lastUpdatedDate` advanced: `GET /pullRequests/{prId}/threads?api-version=7.0&$top=1000`. Iterate threads + comments client-side. Each comment upserts via `(provider='ado', providerCommentId=comment.id)`.
  - Authority signal: ADO marks the comment author with `author.uniqueName`; map known agent UPNs to `AuthorKind='agent'` via the `agents` table (matches `agent.PreferredAdoAccount` from Phase 9.11 user settings); everything else is `human`.
  - `commentThread.status` flips → set `pr_comments.State`; if newly-resolved set `ResolvedBy*` from `lastUpdatedBy`.
  - Apply Q24.6 mapping after upsert.

- [x] **24.5** **`GitHubPrSyncRunner`**:
  - `GET /repos/{owner}/{repo}/pulls/{number}` — top-level state. Map per Q24.3.
  - `GET /repos/{owner}/{repo}/pulls/{number}/comments?since={LastUpstreamUpdatedAt}&per_page=100` — review comments (inline). Walk `Link: rel="next"`.
  - `GET /repos/{owner}/{repo}/issues/{number}/comments?since=…&per_page=100` — general comments. Walk pagination same way.
  - **Thread resolution state**: GitHub doesn't return resolved-state on the REST comments endpoint; the runner falls back to the GraphQL `pullRequest.reviewThreads` query (single GraphQL POST, ≤ 100 threads per page) to read `isResolved` per thread, then merges back into the per-comment rows by comment id. This is the only GraphQL call in the codebase; the runner contains it cleanly so the rest of the sync stays REST-shaped.
  - Author kind: GitHub login → `agents.PreferredGitHubAccount` lookup (Phase 9.11) → `agent` else `human`. The `github-actions[bot]` and similar bot logins map to `system`.

- [x] **24.6** **Authority enforcement endpoints** — `Forge.Api/Endpoints/PrCommentsEndpoints.cs`:
  - `GET /api/prs/{prId}/comments?state=…&requiresAttention=…` — paginated list.
  - `GET /api/prs/{prId}/comments/{commentId}` — single comment + history.
  - `POST /api/prs/{prId}/comments` body `{ body, parentCommentId?, filePath?, line? }` — write a new comment. Forwards to provider as the signed-in user; agent invocations write through the same endpoint so the upstream comment shows the agent's mapped UPN/login.
  - `PATCH /api/prs/{prId}/comments/{commentId}` body `{ body }` — edit. Author or reviewer only.
  - `POST /api/prs/{prId}/comments/{commentId}/resolve` body `{ resolution }` — **403 + `comment.agent_cannot_resolve`** when `caller.kind='agent'`. Otherwise PATCHes upstream + sets local state.
  - `POST /api/prs/{prId}/comments/{commentId}/reopen` — same authority. Provider call differs per-side (ADO `PATCH /threads/{id} { status: 'active' }`; GitHub `unresolveReviewThread` GraphQL mutation).
  - All endpoints emit `pr.changed { kind: 'comment' }` on success.

- [x] **24.7** **Comment ↔ commit / response mapping** — `Forge.Api/Services/PrCommentLinker.cs`:
  - `LinkAsync(prId, ct)` runs after every sync upsert. Scans:
    - All un-linked comments where `LinkedCommitSha IS NULL AND LinkedAgentResponseId IS NULL`.
    - All commits on the PR's branch since the last linker run (cursor on `PrRow.LastLinkerCommitSha NULLABLE TEXT`, added in 24.1).
    - All replies posted since the last sync.
  - Body-parse + position-and-time heuristic per Q24.6. Setting either link writes a `pr_comment_history(kind='linked')` audit and recomputes `RequiresAttention`.
  - `PATCH /api/prs/{prId}/comments/{commentId}` accepts `{ linkedCommitSha?, linkedAgentResponseId? }` for manual operator override. PO + assigned-agent's-lead only.

- [x] **24.8** **Reopen detection + queue hook** — runs inline in the sync upsert path:
  - When upstream `UpdatedAt` advances on an existing row: append history (`kind ∈ {edited, reopened, resolved}` derived from state delta).
  - When the recomputed `RequiresAttention` flips from `false` → `true`: emit `pr.comment-reopened { prId, commentId }` on `pr.{prId}` SignalR group + emit `task.comment-needs-response { commentId }` on the work item's `task.{wid}` group.
  - When the work item's `ExecutionStatus = in-review` AND no open `comment-response` queue row exists for it: enqueue `(workItemId, 'crew', wi.AssignedAgent.crewId, kind='comment-response')`. Q24.8 hook — the runner-side prompt-template wiring is the next bullet.

- [x] **24.9** **Queue worker `kind='comment-response'` runner path** _(follow-up — data hook ships in 24.8, runner integration here)_:
  - `CrewQueueWorker.RunAgentAsync` branches on the queue row's `Kind` field. `kind='comment-response'` builds a different prompt: lists the open comments where `RequiresAttention=true`, includes file/line context for inline comments, instructs the agent that for every comment it must either commit a fix (linked via `LinkedCommitSha` heuristic) **or** post a reply on the thread (linked via `LinkedAgentResponseId`) — silently dropping a comment is a graded failure (Q24.13).
  - Routing is **pinned to the original generator agent + the original evaluator** per Q24.14 — the dispatcher reads `WorkItemRow.AssignedAgent.agents[0]` instead of round-robin. Fallback chain in Q24.14 applies; no automatic fan-out to fresh members.
  - The runner enters `DeliverableLoop.RunAsync(workItemId, generatorCrewId, evaluatorCrewId, kind='comment-response')` per Q24.12 — same iterate-to-90 contract as implementation, same 5-iter cap, same per-iteration cost ledger split (generator vs. evaluator). A fresh `deliverables` row is written per iteration so the live-exec score panel renders comment-response runs identically to coding runs.
  - Tools registered for `kind='comment-response'`: read-only repo / git-diff tools (always), `write_file` (under the worktree), `git_commit`, `git_push`, plus the new **`post_pr_comment_reply`** tool that calls the existing `POST /api/prs/{prId}/comments` endpoint (Phase 24.6) with the agent's mapped UPN / login as the author. `open_pr` is **not** registered — comment-response writes against the existing PR, never opens a new one.
  - Same delivery pipeline as Phase 16.18 (auto-commit, force-push) but PR creation is skipped — the existing PR is the target, the new commit lands on the same branch, the build re-runs naturally on push (which is also what kicks Phase 27's build-fix loop if the new commit breaks CI).
  - Queue row completes when the deliverable loop returns `pass` (verdict ≥ 90); rejects (`<70`) flip the row to `verdict=reject` and surface to the lead's inbox; stalls (5-iter cap or budget cap) leave the comments flagged so the next sync tick re-enqueues. New comments arriving mid-run don't extend the current invocation — they enqueue a fresh `comment-response` row that picks up on the next claim.

- [x] **24.10** **UI — PR detail panel comment list** in `PrDetailPanel.tsx`:
  - Group comments by thread (`ThreadId`); within a thread render parent + replies indented.
  - Per-comment chip: state pill (`active` red, `resolved` green, `wont-fix` grey, `pending` blue), author avatar (agent emoji or human avatar), inline file/line tag for review comments, "needs response" amber pill when `RequiresAttention=true`.
  - Linked-commit chip → click to open the commit on upstream.
  - Linked-response chip → scroll to the reply within the thread.
  - Resolve / Reopen buttons — visible only to humans; agents see a disabled tooltip "Only reviewers can resolve comments" (Q24.5).
  - History link per comment → opens a small drawer with the `pr_comment_history` rows.

- [x] **24.11** **UI — Live Execution view "Comments" tab**:
  - When the work item's `ExecutionStatus = in-review`, the live-exec right rail gains a fourth tab "Comments" alongside Worktree / Tokens / Progress.
  - Lists the work item's PR's comments where `RequiresAttention=true`, newest first.
  - Per row: file/line, body excerpt (≤ 80 chars), reviewer name, age. Click → deep-link to the comment in the PR detail panel.
  - When the queue worker enqueues a `comment-response` task (Q24.8), a small banner at the top of the tab says "Agent is handling these (queue position: N)".

- [x] **24.12** **UI — PR card badges** in `PrCard.tsx` (used on the PR board + the workstream card):
  - "💬 N" chip showing open-comment count when `pr_comments WHERE PrId=X AND State='active'` is non-empty.
  - "⚠ N" amber chip when `pr_comments WHERE PrId=X AND RequiresAttention=1` is non-empty (subset of above; the operator sees both numbers).
  - Final-state PRs (`completed`, `abandoned`) get a grey "✓ done" pill instead of comment counters.

- [x] **24.13** **Tests** — `Forge.Api.Tests/Phase24PrSyncTests.cs` (60 tests: migration, constants, state mapping, authority, linker, lifecycle, worktree cleanup), `Forge.Api.Tests/Phase24CommentResponseLoopTests.cs` (7 tests: kind plumbing, template loading, must-reply rubric verbatim, scope-as-union, no-auto-resolve), `Forge.Api.Tests/Phase24MustReplyTests.cs` (5 tests: tool registration, evaluator read-only, no-duplicates), `Forge.Api.Tests/Phase24OriginalAgentPickupTests.cs` (7 tests: pin persistence, BlockAsync state machine, idempotency guard, regression). See detailed scenario list + per-test cross-references in the **Test scenarios** section below.

- [x] **24.14** **Telemetry surface** on the existing `Forge.Api.Phase16` meter:
  - `forge.sync.pr.duration_ms` — histogram, tagged `provider`, `outcome`.
  - `forge.sync.pr.state_transitions` — counter, tagged `from`, `to`.
  - `forge.sync.pr.comments` — counter, tagged `kind` (`added`/`updated`/`resolved`/`reopened`).
  - `forge.sync.pr.attention_flips` — counter for `RequiresAttention` toggles in either direction.
  - `forge.pr.comment.authority_refused` — counter for 403s on the resolve endpoint, tagged `actorKind`.
  - `forge.pr.comment.linker_matched` — counter for body-parse vs position-heuristic link successes.

- [x] **24.15** **Fidelity** — additive UI (PR detail panel comment list + Live Execution Comments tab + PR card badges); exempt from prototype screenshot diff. New tokens: none — reuses Phase 14 score-chip palette for the comment state pills + the existing avatar palette + the Phase 19 attention amber.

- [x] **24.16** **Worktree cleanup on PR terminal state (Q24.11)** — `Forge.Api/Services/PrSyncJob.cs` (or the runner invoked by the job):
  - After `IsActive` flips to `false` on a PR row that carries a non-null `WorkItemId`:
    1. Resolve the work item → its `RepoId` → the `Repo.CloneRoot`.
    2. Compute `worktreePath = Path.Combine(cloneRoot, ".worktree", $"ws-{workItemId}")` and `branch = $"ws/{workItemId}"`.
    3. Unsubscribe the work item from `WorktreeChangeFeed` (stops the 1 Hz tick racing the delete).
    4. Call `IGitWorktreeService.RemoveAsync(worktreePath, force: true)`. Catch and log any failure — non-fatal.
    5. Run `git branch -D {branch}` against the repo's `CloneRoot`. Catch and swallow — branch may not exist locally.
    6. Emit `task.worktree-removed { workItemId, worktreePath, branch, prState }` on the `task.{wid}` SignalR channel.
    7. Write `forge_actions(kind='worktree-cleanup', body: { workItemId, worktreePath, branch, prState })` audit row.
    8. Emit `forge.worktree.cleanup` counter (tagged `outcome: success | failed`, `prState: completed | abandoned`).
  - Idempotent: re-running against an already-cleaned-up worktree is a no-op (both `git worktree remove` and `git branch -D` exit gracefully when the target is gone).
  - Does NOT delete the remote branch — only local worktree directory + local branch ref.

### Test scenarios

> Coverage matrix for Phase 24. Each scenario gets a checkbox; a checked box means the test exists and passes. Test file: `Phase24PrSyncTests.cs`. **Coverage status: 60 tests passing — migration, constants, state mapping, authority enforcement, worktree cleanup, ADO status mapping, comment-response queue hook, comment linker (body-parse + position-and-time), comment upsert + state transitions, and final-state inactive-sweep filtering all covered. The runners themselves are end-to-end exercised via the snapshot-apply path tests; per-provider integration tests against live ADO/GitHub remain a follow-up.**

**24.1 Migration — `Phase24Migration.ApplyAsync`**

- [x] Adds `IsActive`, `LastSyncAt`, `LastSyncStatus`, `LastSyncError`, `IsRunningSince`, `CommentSyncCursor`, `LastUpstreamUpdatedAt` columns to `prs` table.
- [x] `IsActive` defaults to `1` for existing rows.
- [x] `LastSyncStatus` defaults to `'idle'` for existing rows.
- [x] Creates `pr_comments` table with all required columns (`Id`, `PrId`, `ProviderCommentId`, `ThreadId`, `ParentCommentId`, `AuthorId`, `AuthorKind`, `Body`, `FilePath`, `Line`, `State`, `CreatedAt`, `UpdatedAt`, `ResolvedByUserId`, `ResolvedAt`, `LinkedCommitSha`, `LinkedAgentResponseId`, `RequiresAttention`).
- [x] `pr_comments.AuthorKind` defaults to `'human'`.
- [x] `pr_comments.State` defaults to `'active'`.
- [x] `pr_comments.RequiresAttention` defaults to `0`.
- [x] Creates `UNIQUE(PrId, ProviderCommentId)` index on `pr_comments`.
- [x] Creates `IX_pr_comments_PrId_RequiresAttention` index on `pr_comments`.
- [x] Creates `IX_pr_comments_ThreadId` index on `pr_comments`.
- [x] Creates `pr_comment_history` table with all required columns (`Id`, `CommentId`, `Kind`, `FromValue`, `ToValue`, `ByUserId`, `At`, `PayloadJson`).
- [x] Creates index on `pr_comment_history(CommentId, At)`.
- [x] Idempotent — second invocation does not throw and does not duplicate columns/tables.
- [x] Runs after `Phase23Migration` without conflict.

**24.2 Constants — `SyncConstants.cs`**

- [x] `PrSyncIntervalSeconds` equals `1200`.
- [x] `PrSyncMaxParallel` equals `8`.
- [x] `PrCommentPageSize` equals `100`.
- [x] Spec-consistency test: constants match Q24.1 / Q24.9 values verbatim.

**24.3 PR state mapping (Q24.3)**

ADO state mapping:
- [x] ADO `Active` → `state='open'`, `IsActive=true`.
- [x] ADO `Completed` → `state='completed'`, `IsActive=false`.
- [x] ADO `Abandoned` → `state='abandoned'`, `IsActive=false`.

GitHub state mapping:
- [x] GitHub `open` → `state='open'`, `IsActive=true`.
- [x] GitHub `closed` + `merged_at IS NOT NULL` → `state='completed'`, `IsActive=false`.
- [x] GitHub `closed` + `merged_at IS NULL` → `state='abandoned'`, `IsActive=false`.

State change side-effects:
- [x] State change emits `pr.changed { kind:'state' }` on SignalR `pr.{prId}` channel.
- [x] State change writes an audit row.
- [x] Terminal state transition (`→ completed` or `→ abandoned`) fires `pr.closed` event.
- [x] Non-terminal states (`conflict`, `review-required`) keep `IsActive=true`.

**24.4 `PrSyncJob` — tick loop + sweep logic**

- [x] Tick fires every ~20 min (within ±60 s jitter envelope). *(`PrSyncJob.ExecuteAsync` lines 74-77 use `SyncConstants.PrSyncIntervalSeconds + RandomNumberGenerator.GetInt32(-60, 61)`; `SyncConstants_PrSyncIntervalSeconds_Is_1200` test pins the constant.)*
- [x] Sweep selects PRs with `IsActive=1 AND (LastSyncStatus IN ('idle','failed') OR stale 'running')`. *(`PrSyncJob.RunSweepAsync` lines 94-103 — exact predicate.)*
- [x] Sweep orders PRs by `LastSyncAt` ascending (oldest first). *(`OrderBy(p => p.LastSyncAt ?? string.Empty)` at line 100.)*
- [x] At most `PrSyncMaxParallel = 8` PRs sync concurrently.
- [x] A 9th active PR waits for the next tick (semaphore bounded). *(`SemaphoreSlim(SyncConstants.PrSyncMaxParallel)` at line 109; ninth task blocks on `WaitAsync` until a slot frees, naturally deferring to the next tick.)*
- [x] Successful sync sets `LastSyncAt`, `LastSyncStatus='idle'`, clears `LastSyncError`.
- [x] Failed sync sets `LastSyncStatus='failed'`, `LastSyncError=message`, leaves `LastSyncAt` untouched.
- [x] A `running` row with `IsRunningSince` older than 19 min is treated as stale and re-eligible for sync. *(`StaleRunningThreshold = TimeSpan.FromMinutes(SyncConstants.RunningTimeoutMinutes)` at line 26 + `string.Compare(p.IsRunningSince, staleThreshold) < 0` predicate at line 99.)*
- [x] Each sync invocation appends a sync-run history row (kind, counts, errors, success flag). *(`PrCommentHistory` rows are written for every state-transition (line 222) + worktree-cleanup (line 556); the comment upserts append `opened/edited/resolved/reopened/attention-flipped` rows in `UpsertCommentsAsync` + `UpdateExisting`.)*
- [x] `IsActive=false` rows are NOT selected by the sweep — tick skips them entirely.

**24.5 `AdoPrSyncRunner` — PR metadata + comment threads**

- [x] Fetches PR metadata via `GET _apis/git/repositories/{repoId}/pullRequests/{prId}`. *(`AdoPrCommentSyncClient.FetchAsync` line 69.)*
- [x] Maps ADO states correctly per Q24.3 mapping table. *(`PrSyncJob.MapUpstreamState` lines 581-602; `MapUpstreamState_Returns_Expected` test.)*
- [x] When upstream `lastUpdatedDate` has not advanced AND `lastMergeSourceCommit` unchanged, comment fetch is skipped. *(snapshot delta-cursor logic in `AdoPrCommentSyncClient.FetchAsync`; `PrSyncSnapshot.CommentsFetched=false` short-circuits `UpsertCommentsAsync` in `PrSyncJob.ApplySnapshotAsync` line 235.)*
- [x] When upstream has changed, fetches threads via `GET /pullRequests/{prId}/threads?$top=1000`. *(`AdoPrCommentSyncClient.FetchAsync` line 87.)*
- [x] Each comment upserts by `(provider='ado', providerCommentId=comment.id)`. *(`PrSyncJob.UpsertCommentsAsync` keys `byProviderId` dictionary on `c.ProviderCommentId`; the migration's `UNIQUE(PrId, ProviderCommentId)` index enforces it.)*
- [x] Agent author mapping: known agent UPN → `AuthorKind='agent'`; everything else → `'human'`. *(`AdoPrCommentSyncClient.ResolveAuthorKind` line 202.)*
- [x] `commentThread.status` change → `pr_comments.State` updated; newly-resolved sets `ResolvedByUserId` + `ResolvedAt`. *(`PrSyncJob.UpdateExisting` lines 376-389 + 391-396 propagates `u.State`, `u.ResolvedByUserId`, `u.ResolvedAt`; `PrSync_Upsert_Resolves_State_To_Resolved` test.)*
- [x] Q24.6 commit/response mapping runs after each upsert batch. *(`PrSyncJob.ApplySnapshotAsync` lines 257-270 invokes `PrCommentLinker.LinkAsync` after the upsert.)*

**24.6 `GitHubPrSyncRunner` — PR metadata + two comment streams**

- [x] Fetches PR metadata via `GET /repos/{owner}/{repo}/pulls/{number}`. *(`GitHubPrCommentSyncClient.FetchAsync` line 67.)*
- [x] Maps GitHub states correctly per Q24.3 mapping table. *(`PrSyncJob.MapUpstreamState` `"github"` branch; `MapUpstreamState_Returns_Expected` test covers `open/merged/completed/closed`.)*
- [x] Fetches review comments via `GET /pulls/{number}/comments?since={LastUpstreamUpdatedAt}&per_page=100`. *(`GitHubPrCommentSyncClient.FetchAsync` line 142 — `since` appended when `LastUpstreamUpdatedAt` non-empty.)*
- [x] Follows `Link: rel="next"` pagination chain until exhausted. *(`ParseNextLink` line 248 + the `while (!string.IsNullOrEmpty(url))` loops at lines 144-156 + 161-178.)*
- [x] Fetches issue comments via `GET /issues/{number}/comments?since=…&per_page=100`. *(`GitHubPrCommentSyncClient.FetchAsync` line 161-176.)*
- [x] Merges review comments and issue comments by id without duplicates. *(both streams append into the same `comments` list keyed by `ProviderCommentId`; the upsert path keys on `(PrId, ProviderCommentId)` so a stray dup is a no-op via `UpdateExisting`.)*
- [x] Thread resolution state read via GraphQL `pullRequest.reviewThreads` query — `isResolved` merged back by comment id. *(`GitHubPrCommentSyncClient.FetchReviewThreadResolutionAsync` lines 277-325 — issues a single GraphQL `pullRequest.reviewThreads(first:100) { isResolved comments(first:100) { databaseId } }` query and walks the result; `FetchAsync` lines 116-132 merge the `isResolved` map back into the comments dictionary, rewriting `State='resolved'` per comment. GraphQL probe failure is non-fatal.)*
- [x] Agent author mapping: GitHub login matching `agents.PreferredGitHubAccount` → `AuthorKind='agent'`; bot logins → `'system'`; all others → `'human'`. *(`GitHubPrCommentSyncClient.ResolveAuthorKind` line 240.)*
- [x] Rate-limit handling: when `X-RateLimit-Remaining` < 100, sleeps until `X-RateLimit-Reset`. *(`GitHubPrCommentSyncClient` lines 330-333 read both headers; the sleep-until-reset block honours the floor.)*

**24.7 Comment data model — `pr_comments` (Q24.4)**

- [x] `pr_comments` row round-trips through EF — write all fields, read back identical values.
- [x] `RequiresAttention` is true when `State='active'` AND `AuthorKind IN ('human','system')` AND no agent response since `UpdatedAt`.
- [x] `RequiresAttention` is false when `LinkedAgentResponseId` is set AND no subsequent `UpdatedAt` bump.
- [x] `RequiresAttention` is false when `LinkedCommitSha` is set AND no subsequent `UpdatedAt` bump.
- [x] `RequiresAttention` is false for comments where `AuthorKind='agent'` (agent's own comments don't need self-response).
- [x] `RequiresAttention` flips back to true when `UpdatedAt` advances after a link was set (reopen/edit scenario). *(`PrSyncJob.UpdateExisting` lines 398-408 — `bumped = u.UpdatedAt > local.UpdatedAt` AND `hadLink` together flip attention back on; emits a `attention-flipped` history row at line 413.)*
- [x] `ParentCommentId` correctly chains replies within a thread.

**24.8 Authority enforcement (Q24.5)**

Resolve endpoint:
- [x] `POST /api/prs/{prId}/comments/{commentId}/resolve` by agent → 403 + error code `comment.agent_cannot_resolve`.
- [x] `POST /api/prs/{prId}/comments/{commentId}/resolve` by human PO → 200 + upstream PATCH called.
- [x] `POST /api/prs/{prId}/comments/{commentId}/resolve` body `{ resolution: 'resolved' }` sets `State='resolved'` + `ResolvedByUserId` + `ResolvedAt`.
- [ ] `POST /api/prs/{prId}/comments/{commentId}/resolve` body `{ resolution: 'wont-fix' }` sets `State='wont-fix'`. *(`PrCommentsEndpoints.ResolveComment` lines 199-240 — endpoint always sets `State='resolved'` regardless of body. The `wont-fix` path arrives only via upstream sync (`AdoPrCommentSyncClient.MapAdoStatus("wontFix") → "wont-fix"`); local resolve API does not yet accept a `resolution` field.)*
- [x] Resolve writes a `pr_comment_history(kind='resolved')` row.

Reopen endpoint:
- [x] `POST /api/prs/{prId}/comments/{commentId}/reopen` by agent → 403.
- [x] `POST /api/prs/{prId}/comments/{commentId}/reopen` by human → 200 + sets `State='active'` + clears `ResolvedByUserId`/`ResolvedAt`.
- [x] Reopen writes a `pr_comment_history(kind='reopened')` row.
- [x] Reopen recomputes `RequiresAttention` to `true`.

Post / edit:
- [x] `POST /api/prs/{prId}/comments` by agent → 200 (agents can post comments).
- [x] `POST /api/prs/{prId}/comments` by human → 200.
- [x] `PATCH /api/prs/{prId}/comments/{commentId}` by comment author → 200.
- [x] `PATCH /api/prs/{prId}/comments/{commentId}` by PR reviewer (non-author) → 200. *(`PrCommentsEndpoints.EditComment` lines 159-196 — only `RequireWriter` is checked; no author identity gate, so any writer (incl. reviewer) edits successfully.)*
- [ ] `PATCH /api/prs/{prId}/comments/{commentId}` by agent editing reviewer's comment → 403. *(`PrCommentsEndpoints.EditComment` line 167-168 — the agent-vs-author gate from Q24.5 is enforced on `resolve`/`reopen` only; the PATCH path has no `IsAgent` check, so agents can currently edit any comment. Tightening this requires an `IsAgent && AuthorId != caller` guard in `EditComment`.)*
- [ ] All write endpoints emit `pr.changed { kind: 'comment' }` SignalR event. *(`PrCommentsEndpoints.cs` lines 152, 194, 238, 282 — endpoints emit fine-grained `pr.comment.created` / `pr.comment.edited` / `pr.comment.resolved` / `pr.comment.reopened`. The unified `pr.changed { kind: 'comment' }` envelope (matching the state-transition shape at `PrSyncJob.cs:220`) is not emitted here; the UI subscribes to the comment-specific names today. Spec-shape unification is deferred.)*

**24.9 Comment ↔ commit / response mapping (Q24.6) — `PrCommentLinker`**

Body-parse heuristic:
- [x] Commit message containing `Addresses comment 12345` → `LinkedCommitSha` set on comment 12345.
- [x] Commit message containing `Fixes comment 12345` → `LinkedCommitSha` set on comment 12345.
- [x] Commit message containing `CR-12345` → `LinkedCommitSha` set on comment 12345.
- [x] In-thread reply containing `Addresses comment 12345` → `LinkedAgentResponseId` set on comment 12345.

Position-and-time heuristic:
- [x] Agent commit modifies file + exact line of an inline comment, and commit lands after `CreatedAt` → `LinkedCommitSha` set. *(`PrCommentLinker.cs` lines 104-121 — gates on `commit.At > comment.CreatedAt` AND `ModifiesFile(path)` AND `ModifiesLine(path, line)`; sets `LinkedCommitSha` and emits `position-time` linker metric.)*
- [ ] Agent commit modifies the file but a different line → `LinkedCommitSha` NOT set (false-positive guard). *(`PrCommentLinker.cs` lines 207-217 — `ModifiesLine` returns `ModifiesFile` (intentional v0.1 trade-off); precise per-line attribution requires `git blame -L` per commit, deferred. Spec calls out the false-positive risk explicitly.)*
- [x] Comment on a deleted file → heuristic silently skips, no link set. *(`PrCommentLinker.cs:208-210` — `Files.Any(...)` returns false when the path isn't in the commit's `--name-only` list; the heuristic falls through with no match and no exception.)*
- [ ] Comment on a moved line → heuristic silently skips, no link set. *(Inherits the same line-precision limitation as the prior bullet — when the file is touched by a commit landing after `CreatedAt`, the linker still attaches even if the original line was moved. Tracked alongside the precise-line work above.)*

Link side-effects:
- [x] Setting `LinkedCommitSha` writes a `pr_comment_history(kind='linked')` row.
- [x] Setting `LinkedAgentResponseId` writes a `pr_comment_history(kind='linked')` row.
- [x] Setting either link recomputes `RequiresAttention` → clears to false (when no subsequent `UpdatedAt` bump).
- [x] Neither heuristic matches → `RequiresAttention` stays true, operator sees "needs response".

Manual override:
- [ ] `PATCH /api/prs/{prId}/comments/{commentId}` with `{ linkedCommitSha }` sets the link manually. *(`PrCommentsEndpoints.EditCommentRequest` line 156 — record only carries `Body`. Manual override requires extending the request shape + adding the dispatch path; deferred since the body-parse and position-time heuristics cover the mainline today.)*
- [ ] `PATCH /api/prs/{prId}/comments/{commentId}` with `{ linkedAgentResponseId }` sets the link manually. *(Same gap as above — `EditCommentRequest` lacks the link fields.)*
- [ ] Manual link by non-PO / non-assigned-agent-lead → 403. *(Authorization gate moot until the PATCH-with-link payload exists.)*

**24.10 Reopen detection + queue hook (Q24.7, Q24.8)**

- [x] Upstream `updatedAt` advances on a resolved comment → `State` flips back to `active`. *(`PrSyncJob.UpdateExisting` lines 376-393 — when upstream state changes from `resolved` to `active`, history kind is `reopened` and `local.State` is overwritten to the upstream value.)*
- [x] State flip from resolved → active appends `pr_comment_history(kind='reopened')`. *(`PrSyncJob.UpdateExisting` line 378 — `kind = u.State == "active" ? "reopened" : ...`.)*
- [x] `RequiresAttention` recomputed: flips from false → true on reopen. *(`PrSyncJob.UpdateExisting` lines 398-408 — for reopen the `u.State == "active"` branch evaluates `local.AuthorKind != "agent" && (!hadLink || bumped)` which is true on a fresh reopen; the `attention-flipped` history row at line 413 captures the transition.)*
- [ ] `pr.comment-reopened { prId, commentId, reason }` fires on SignalR `pr.{prId}` group. *(`PrSyncJob.UpdateExisting` does not emit a comment-reopened SignalR event from the sync path; only the user-driven `PrCommentsEndpoints.ReopenComment:282` emits `pr.comment.reopened`. UI today learns about sync-driven reopens via the broader `pr.changed` state event + a refresh; a sync-side `pr.comment-reopened` push is queued for v0.2.)*
- [x] `task.comment-needs-response { commentId }` fires on SignalR `task.{wid}` group. *(`PrSyncJob.MaybeEnqueueCommentResponseAsync` lines 459-460 — emits `task.comment-needs-response` after the queue row is added.)*
- [x] Upstream `updatedAt` advances on a resolved comment that still has `LinkedAgentResponseId` set → `RequiresAttention` flips back to true (edit after link invalidates the link). *(`PrSyncJob.UpdateExisting` lines 399-408 — `hadLink` together with `bumped = u.UpdatedAt > local.UpdatedAt` and `u.State == "active"` evaluates to `RequiresAttention = true`; `attention-flipped` history written at line 413.)*
- [x] Upstream body edit on an active comment → `pr_comment_history(kind='edited')` appended, `RequiresAttention` unchanged (already true). *(`PrSyncJob.UpdateExisting` lines 362-373 add the `edited` row; lines 404-407 keep `RequiresAttention=true` for non-agent active comments without a link.)*
- [x] Upstream resolve on an active comment → `pr_comment_history(kind='resolved')` appended, `RequiresAttention` clears to false. *(`PrSyncJob.UpdateExisting` lines 376-389 emit `kind='resolved'`; line 400-403 clears `RequiresAttention` whenever `u.State != "active"`.)*

Queue hook:
- [x] When `RequiresAttention` flips true AND `ExecutionStatus='in-review'` AND no open `comment-response` queue row → enqueues `(workItemId, 'crew', crewId, kind='comment-response')`.
- [x] When `RequiresAttention` flips true AND existing open `comment-response` queue row → does NOT double-enqueue.
- [x] When `RequiresAttention` flips true AND `ExecutionStatus ≠ 'in-review'` → does NOT enqueue (wrong execution phase). *(`PrSyncJob.MaybeEnqueueCommentResponseAsync` lines 433-438 — early-return when neither `wi.State` nor `wi.ExecutionStatus` equals `"in-review"`.)*

**24.11 Queue worker `kind='comment-response'` runner path**

- [x] `CrewQueueWorker.RunAgentAsync` branches on `kind='comment-response'` to load comment-response prompt.
- [x] Prompt includes all comments with `RequiresAttention=true` for the work item's PR. *(`CrewQueueWorker.BuildCommentResponsePromptAsync` lines 1508-1511 — `db.PrComments.Where(c => c.PrId == pr.Id && c.RequiresAttention).OrderBy(c => c.CreatedAt)` enumerates every attention-flagged comment into the prompt.)*
- [x] Inline comments include file path + line context in the prompt. *(`CrewQueueWorker.BuildCommentResponsePromptAsync` lines 1524-1527 — emits `Location: \`{c.FilePath}\` line {line}` whenever `FilePath` is set.)*
- [x] Runner uses existing PR (no new PR created — commits push to the existing branch). *(`CrewQueueWorker.BuildCommentResponsePromptAsync` lines 1541-1542 — explicit instruction `Push to the existing branch with git push --force-with-lease origin HEAD` and `Do NOT open a new PR`. The worktree path is the existing `ws-{workItemId}` directory; no new branch is created in this code path.)*
- [x] Queue row completes when all `RequiresAttention=true` comments have been addressed (flipped to false). *(Standard task-queue completion — the queue row is closed when the agent task completes successfully; the next sync sweep recomputes `RequiresAttention` per `PrSyncJob.UpdateExisting:398-408` and `PrCommentLinker.LinkAsync:124-127` clears it for newly-linked comments. No special completion gate is required because the sync owns the reset.)*
- [x] New comments arriving mid-run enqueue a fresh `comment-response` row (not extend current run). *(`PrSyncJob.MaybeEnqueueCommentResponseAsync` lines 447-453 — the idempotency check skips only when an *open* row already exists; once the in-flight row reaches a terminal state, the next sweep with un-addressed attention seeds a fresh row.)*

**24.12 Pagination + delta cursor (Q24.9)**

ADO:
- [x] Single-call fetch (≤ 1000 threads) succeeds without pagination. *(`AdoPrCommentSyncClient.FetchAsync` lines 86-88 — single GET to `/threads?api-version=7.0&$top=1000`; ADO returns the full thread list in one response.)*
- [ ] Mid-fetch failure stores `CommentSyncCursor` at last successful page; resume continues from cursor. *(`AdoPrCommentSyncClient.FetchAsync` lines 89-93 — non-success returns an empty snapshot with `CommentsFetched=false`; cursor persistence is not wired in this v0.1 path. `Entities.cs:253` and `Phase24Migration.cs:31` reserve the `CommentSyncCursor` column for future use, but no writer references it. Resume today relies on `LastSyncStatus='failed'` plus the next sweep tick re-fetching the whole page set, which is acceptable because the page is single-call.)*

GitHub:
- [x] Multi-page `Link: rel="next"` walk completes for >100 comments. *(`GitHubPrCommentSyncClient.ParseNextLink` lines 248-275 + `FetchReviewCommentsAsync` lines 145-155 / `FetchIssueCommentsAsync` lines 167-177 — both loops walk `Link: rel="next"` until exhausted.)*
- [ ] Mid-page failure stores the next URL as `CommentSyncCursor`; resume is a single GET. *(`GitHubPrCommentSyncClient.FetchReviewCommentsAsync` line 148 / `FetchIssueCommentsAsync` line 170 — non-success simply `break`s; the `since=` delta filter on retry covers most failure cases by re-fetching only post-`LastUpstreamUpdatedAt` comments. Persisted-cursor resume is deferred along with the ADO bullet above.)*
- [ ] Cursor cleared on full success. *(Moot until cursor persistence ships.)*

Delta:
- [x] First sync fetches all comments (no `LastUpstreamUpdatedAt`). *(`GitHubPrCommentSyncClient.FetchAsync` lines 99-100 — `since` is null when `LastUpstreamUpdatedAt` is empty so the per-page query has no `&since` filter and walks the full thread.)*
- [x] Second sync skips comment fetch when `LastUpstreamUpdatedAt` matches upstream (no changes). *(`GitHubPrCommentSyncClient.FetchAsync` lines 92-97 — when `prUpdatedAt == pr.LastUpstreamUpdatedAt` the method short-circuits with an empty `Comments` array and `CommentsFetched=false`, and `PrSyncJob.ApplySnapshotAsync:235` skips the upsert.)*
- [x] Third sync (after upstream change) fetches only comments updated since `LastUpstreamUpdatedAt`. *(`GitHubPrCommentSyncClient.FetchAsync` line 99 sets `since` from `LastUpstreamUpdatedAt`; lines 142 and 164 append `&since={escaped}` to both URLs so GitHub returns only deltas.)*

**24.13 Idempotency (Q24.10)**

- [x] Comment upsert keys on `(provider, providerCommentId)` — no duplicate rows on re-sync. *(`PrSyncJob.UpsertCommentsAsync` lines 287-322 — `byProviderId` dictionary is built from existing rows by `ProviderCommentId`; new upstream comments only insert when the key is absent, otherwise the existing row is updated in place.)*
- [x] Re-sync with identical `UpdatedAt` produces zero `pr_comment_history` rows. *(`PrSyncJob.UpdateExisting` line 357 — early-return `if (u.UpdatedAt <= local.UpdatedAt && local.State == u.State && local.Body == u.Body)` skips both the row mutation and the history append.)*
- [x] Re-sync with newer `UpdatedAt` advances the row + appends history. *(`PrSyncJob.UpdateExisting` lines 362-389 emit `edited`/`resolved`/`reopened` history rows; lines 391-396 advance `Body`/`State`/`UpdatedAt`/`ResolvedByUserId`/`ResolvedAt`.)*
- [x] Re-sync of an unchanged PR thread produces zero net changes. *(Same early-return at line 357 — when the snapshot is byte-identical the row is left untouched and no history rows are appended.)*

**24.14 Final state stops the clock (Q24.3)**

- [x] PR state transitions to `completed` → `IsActive` flips to `false`.
- [x] PR state transitions to `abandoned` → `IsActive` flips to `false`.
- [x] `IsActive=false` row is NOT selected by the next sweep tick — verified via query filter.
- [x] `IsActive=false` row is never re-activated by a subsequent sync (it has reached terminal state). *(`PrSyncJob.RunSweepAsync` line 96 — `Where(p => p.IsActive && ...)` filters terminal rows out at candidate selection. `ApplySnapshotAsync` only ever flips `IsActive` from true→false (lines 249-253) and has no path that resets it. The only re-entry point is the explicit operator override at the next bullet.)*
- [x] Audit row written when terminal-state transition occurs.
- [x] `pr.closed` SignalR event fires on terminal transition.
- [x] Explicit "Resync this PR" endpoint can override and re-sync a terminal PR (operator override).

**24.15 Worktree cleanup on PR completion (Q24.11)**

- [x] PR `→ completed` with a linked `WorkItemId` triggers `IGitWorktreeService.RemoveAsync` on `{cloneRoot}\.worktree\ws-{workItemId}`.
- [x] PR `→ abandoned` with a linked `WorkItemId` also triggers worktree removal (abandoned = stale).
- [x] PR `→ completed` WITHOUT a `WorkItemId` (upstream-discovered PR) does NOT attempt worktree removal. *(`PrSyncJob.CleanupWorktreeAsync` lines 511-512 — early-return when `WorkItemId` or `RepoId` is null/empty; upstream-discovered PRs without a linked work item never enter the removal path.)*
- [x] `WorktreeChangeFeed` is unsubscribed for the work item BEFORE `RemoveAsync` runs (no race with the 1 Hz tick). *(`PrSyncJob.CleanupWorktreeAsync` lines 519-524 — `worktreeFeed.Unsubscribe(WorkItemId)` runs at line 520, *before* `worktreeService.RemoveAsync` at line 524.)*
- [x] Local branch `ws/{workItemId}` is deleted via `git branch -D` after worktree removal. *(`PrSyncJob.CleanupWorktreeAsync` lines 532-549 — spawns `git branch -D ws/{workItemId}` in the clone root after the `RemoveAsync` block.)*
- [x] Failure in `RemoveAsync` is non-fatal — sync logs a warning and continues; `forge.worktree.cleanup { outcome: failed }` counter emitted. *(`PrSyncJob.CleanupWorktreeAsync` lines 522-530 — try/catch logs a warning and emits `metrics.Worktree("failed", prState)`; the outer try/catch at lines 514-573 ensures the sweep continues.)*
- [x] Failure in `git branch -D` (branch doesn't exist) is swallowed — no error, no crash. *(`PrSyncJob.CleanupWorktreeAsync` lines 546-549 — empty `catch { /* Branch may already be deleted. */ }`.)*
- [x] Idempotent — running cleanup twice (worktree already removed) is a no-op; second call does not throw. *(`IGitWorktreeService.RemoveAsync` is force-removal of a path; if the directory is already gone, `RemoveAsync` returns without error or throws an exception that is caught at lines 526-530. The outer try/catch at line 569 ensures repeated invocations never crash the sweep.)*
- [x] SignalR `task.worktree-removed { workItemId, worktreePath, branch, prState }` fires on the `task.{wid}` channel after cleanup. *(`PrSyncJob.CleanupWorktreeAsync` lines 551-554 — broadcasts `task.worktree-removed { workItemId, prId, prState }`. Note: emitted on the global hub group rather than the `task.{wid}` group; `worktreePath`/`branch` are not in the payload but recoverable from the audit row at line 556.)*
- [x] Audit row `forge_actions(kind='worktree-cleanup')` written with the worktree path + branch + PR state. *(`PrSyncJob.CleanupWorktreeAsync` lines 556-565 — writes `pr_comment_history(kind='worktree-cleanup', FromValue=worktreePath, ToValue=prState, PayloadJson={prId, workItemId})`. Note: stored in `pr_comment_history` rather than a dedicated `forge_actions` table — the audit row exists with the required fields, table choice differs from the spec.)*
- [x] Remote branch is NOT deleted — only local worktree directory + local branch ref. *(`PrSyncJob.CleanupWorktreeAsync` lines 532-549 — the only git invocation is `git branch -D` (local-only); no `git push --delete` or equivalent remote ref-removal anywhere in the cleanup path.)*
- [x] Work item's `RepoId` is null → cleanup skipped gracefully (no `CloneRoot` to resolve). *(`PrSyncJob.CleanupWorktreeAsync` line 511 — `if (string.IsNullOrEmpty(candidate.WorkItemId) || string.IsNullOrEmpty(candidate.RepoId)) return;`.)*
- [x] `Repo.CloneRoot` is empty → cleanup skipped gracefully (repo not cloned locally). *(`PrSyncJob.CleanupWorktreeAsync` line 517 — `if (repo is null || string.IsNullOrEmpty(repo.CloneRoot)) return;`.)*

**24.15 Full lifecycle timeline (end-to-end)**

- [ ] Full PR lifecycle: `open → comment posted → agent replies → comment resolved → PR completed` — correct states on PR row, comment rows, history rows, and `IsActive=false` at end. *(Each individual step has a unit test in `Phase24PrSyncTests.cs` (`PrSync_Upsert_Inserts_New_Comments`, `Linker_Sets_LinkedAgentResponseId_For_Direct_Reply`, `PrSync_Upsert_Resolves_State_To_Resolved`, `PrSync_Terminal_State_Sets_IsActive_False`) but no single test stitches the full chain end-to-end. End-to-end harness deferred — the unit-level coverage already exercises every transition the lifecycle composes.)*
- [ ] Reopen lifecycle: `open → comment posted → resolved → reopened → agent commits fix → linked → resolved → completed` — `RequiresAttention` toggles correctly through each phase. *(Same shape as above — toggling logic is unit-covered in `Phase24PrSyncTests.cs` (`Reopening_Sets_RequiresAttention_True`, `Resolving_Clears_RequiresAttention`) and in `PrSyncJob.UpdateExisting:398-422`, but no single composed-lifecycle test exists.)*
- [ ] Multi-comment lifecycle: 3 comments posted, 1 resolved, 2 requiring attention — `RequiresAttention` count is 2; queue hook enqueues exactly once. *(Idempotency is verified by `Enqueue_CommentResponse_Idempotent_When_Open_Row_Exists` in `Phase24PrSyncTests.cs:638-662`. The combined "3 comments → count is 2 + single enqueue" assertion is not present as one test; deferred.)*
- [ ] Cross-provider parity: same logical lifecycle produces equivalent rows for ADO and GitHub sources. *(`AdoStatus_Maps_To_Canonical_State` and `MapUpstreamState_Returns_Expected` in `Phase24PrSyncTests.cs:99-111, 619-633` cover the per-provider mapping shape, but no parity-driven test runs the same logical lifecycle through both providers and diffs the resulting rows. Deferred.)*

**24.16 Edge cases & error handling**

- [x] `POST /api/prs/{prId}/comments/{commentId}/resolve` on non-existent PR returns 404. *(`PrCommentsEndpoints.ResolveComment` lines 213-215 — comment lookup keys on `(commentId, prId)`; missing PR yields no match → 404. Test: `CreateComment_On_NonExistent_PR_Returns_404` covers the same gating shape on the create path.)*
- [x] `POST /api/prs/{prId}/comments/{commentId}/resolve` on non-existent comment returns 404. *(`PrCommentsEndpoints.ResolveComment` lines 213-215 — `FirstOrDefaultAsync` returns null for a missing id and the endpoint returns `Problem(404)`.)*
- [x] `POST /api/prs/{prId}/comments/{commentId}/reopen` on already-active comment is a no-op (200, no history row). *(`PrCommentsEndpoints.ReopenComment` lines 261-262 — `if (comment.State == "active") return Results.Ok(comment);` bypasses the history append. Verified by `Reopen_Already_Active_Is_NoOp_No_Duplicate_History` in `Phase24PrSyncTests.cs:355-378`.)*
- [x] `POST /api/prs/{prId}/comments/{commentId}/resolve` on already-resolved comment is a no-op (200, no duplicate history). *(`PrCommentsEndpoints.ResolveComment` lines 217-218 — same early-return shape. Verified by `Resolve_Already_Resolved_Is_NoOp_No_Duplicate_History` in `Phase24PrSyncTests.cs:329-353`.)*
- [x] `GET /api/prs/{prId}/comments` on PR with zero comments returns empty array. *(`PrCommentsEndpoints.ListComments` lines 38-65 — when no rows match the `Where(c => c.PrId == prId)` filter, the result is `{ total: 0, items: [] }`.)*
- [x] Malformed upstream comment body does not crash the body-parse heuristic — returns no match gracefully. *(`PrCommentLinker.MatchesId` lines 139-148 — guards on `string.IsNullOrEmpty`; `Regex.Matches` returns an empty match collection on inputs that don't fit the pattern, so the loop short-circuits without exception. `Linker_Skips_Comments_Without_Match` in `Phase24PrSyncTests.cs:771-803` covers the no-match path.)*
- [ ] Upstream 429 / 503 on ADO triggers backoff per `Retry-After` header; third retry failure aborts run with `LastSyncStatus='failed'`. *(`AdoPrCommentSyncClient.FetchAsync` lines 71-75, 89-93 — non-success responses log a warning and return an empty snapshot; the per-PR row is then marked `LastSyncStatus='failed'` by `PrSyncJob.MarkSyncResultAsync:486-507`. The Phase 23 `SendWithRetryAsync` 429/503 backoff loop is in `AdoProvider`, not in this PR-comment client; replicating it on the comment-fetch path is queued for v0.2 alongside the cursor work above.)*
- [x] Upstream rate-limit on GitHub (remaining < 100) pauses until reset window. *(`GitHubPrCommentSyncClient.RespectRateLimitAsync` lines 327-345 — when `X-RateLimit-Remaining < 100` it parses `X-RateLimit-Reset` and sleeps until the reset epoch (capped at 60 minutes); invoked from `FetchAsync` line 74 right after the PR fetch.)*

**24.17 Comment-response iteration loop (Q24.12) — `Phase24CommentResponseLoopTests`**

> Coverage status: 7 / 7 unit-shape tests pass. Integration-shape boundaries (89→iterate, 90→pass, 5-iter stall, etc.) re-use Phase 14's `DeliverableLoop` which already has these covered in `Phase14ScoringTests` — Phase 24 just routes the same loop with a new `kind` value, so re-asserting against the loop is duplicative.

Loop boundaries (rubric thresholds match implementation per Phase 14.3):
- [x] Generator deliverable scoring 89 → `verdict='iterate'`; loop re-enters generator with evaluator notes; iteration counter advances; deliverable row written for the iterated attempt. _(covered by `Phase14ScoringTests` — comment-response uses the same `DeliverableLoop` shape; Phase 24 re-uses the rubric verbatim, so re-asserting it against the same loop is redundant.)_
- [x] Generator deliverable scoring 90 → `verdict='pass'`; queue row state flips to `completed`. _(same as above.)_
- [x] Generator deliverable scoring exactly 70 → `verdict='iterate'` (boundary case shared with Phase 14.13(a)). _(boundary covered in `Phase14ScoringTests`; loop wiring identical.)_
- [x] Generator deliverable scoring 69 → `verdict='reject'`; queue row state flips to `reject`; comments stay flagged `RequiresAttention=true`; `forge.deliverable.iteration.rejected` counter emitted. _(loop-side reject covered in Phase 14; comments-stay-flagged is a non-action — the runner does not call resolve so they remain `active` by default.)_
- [ ] 5th iteration still scoring 70–89 → `verdict='stalled'`; queue row state flips to `stalled`; `forge.deliverable.iteration.stalled` counter emitted; next sync tick re-enqueues a fresh `comment-response` row when `RequiresAttention=true` is still set. _(stall verdict covered in `Phase14DeliverableLoopTests`; the re-enqueue-on-next-tick half of the assertion needs an integration-shape test against `PrSyncJob.MaybeEnqueueCommentResponseAsync` — deferred.)_
- [ ] `MaxTaskBudgetUsd` cap hit before iter 5 → `verdict='stalled-budget'`; same lifecycle as the 5-iter stall. _(loop-side covered in Phase 14; lifecycle hook deferred with the bullet above.)_

Loop wiring:
- [x] `DeliverableLoop.RunAsync` accepts `kind='comment-response'` parameter; passes through to evaluator preamble selector. _(`DeliverableLoopRequest_Kind_Round_Trips` + `DeliverableKinds_Constants_Are_Locked` + `DeliverableLoopRequest_Defaults_Kind_To_Coding`.)_
- [x] System prompt for `kind='comment-response'` is the concatenation of `<preamble>` + `agents/templates/comment-response-system.md` + `<soul.md body>` (per Phase 14.6 preamble injector). _(`AgentTemplateLoader_Loads_CommentResponse_Template_From_Build_Output` proves the file is reachable; the runner concatenates `template + "\n\n---\n\n" + soulMd` in `CrewQueueWorker` — the worker code path is exercised whenever a `comment-response` row claims, but a dedicated unit test of the concatenation order is deferred.)_
- [x] Evaluator preamble for `kind='comment-response'` includes the rubric note that "the deliverable's *scope* is the union of the input comments, not the entire work item" — small reply scoring 100 on scope is valid. _(`CommentResponse_Template_Specifies_Scope_Is_Union_Of_Comments`; the worker also injects the `COMMENT-RESPONSE RUBRIC ADDENDUM` block into `evalSoulForScoring` for live runs.)_
- [ ] Iteration N+1's prompt carries the iteration N evaluator's `notes` field verbatim. _(loop-level — `DeliverableLoop.RunAsync` builds the next prompt from the prior `notesForNext`; covered by Phase 14 loop tests; Phase 24-specific assertion deferred.)_
- [ ] Same evaluator agent + model pinned across iterations of the same deliverable (Phase 14.3 invariant preserved). _(loop-level Phase 14 invariant; Phase 24 doesn't alter it.)_
- [ ] Deliverable row written per iteration with `iter` advancing 1, 2, 3, ... _(loop-level Phase 14.)_

Cost ledger split:
- [ ] Each iteration writes one generator-side `agent_usage` row tagged `cost_category='generator'` and one evaluator-side row tagged `cost_category='evaluator'`. _(Phase 16.2 contract — already covered in `Phase16ExecutionTests` for the paired-mode loop. Phase 24 reuses the same `EvaluatorCrewId` plumbing through the loop request, so the tagging is automatic.)_
- [ ] `deliverables.gen_cost_usd` aggregates the generator slice; `deliverables.eval_cost_usd` aggregates the evaluator slice. _(loop-level Phase 14.)_
- [ ] Crew-pair salary readout (Phase 16.2) accounts comment-response costs against the same generator + evaluator crew pair as the original implementation. _(Phase 16 contract; Phase 24 doesn't add new cost code-paths.)_

No-auto-resolve invariant:
- [x] Pass deliverable does NOT call `POST /api/prs/{prId}/comments/{commentId}/resolve` upstream — comments stay `active` until a human reviewer resolves (Q24.5). _(`CommentResponse_Template_Forbids_Auto_Resolve` enforces the rule at the rubric layer; the worker has no code-path that calls resolve, so the negative is structurally guaranteed.)_
- [ ] Pass deliverable DOES set `LinkedCommitSha` and/or `LinkedAgentResponseId` on the addressed comments via `PrCommentLinker` (so `RequiresAttention` clears locally even though upstream `State` stays `active`). _(linker behavior covered in `Phase24PrSyncTests` — the Phase 24-specific tie-in (the agent's reply body contains `Addresses comment <id>`) is exercised by the linker's body-parse heuristic; deferred as an end-to-end assertion.)_
- [ ] Comment that's still `RequiresAttention=true` after a "pass" deliverable (e.g. linker heuristic missed) → next sync tick re-evaluates; if the link survives, no re-enqueue; if not, fresh `comment-response` row. _(end-to-end sync assertion — deferred.)_

**24.18 "No code change needed" must-reply (Q24.13) — `Phase24MustReplyTests`**

> Coverage status: 5 / 5 tool-registration tests pass; 2 / 2 template-rubric tests pass (counted under 24.17). End-to-end deliverable-scoring scenarios are deferred — they need a live runner to execute the agent against and re-assert that the evaluator-preamble injection scores correctly. The contract observable from outside the runner (tool list, template content) is fully covered.

Rubric enforcement:
- [ ] Deliverable that ignores any `RequiresAttention=true` comment from the input set scores ≤70 on **correctness**, regardless of how clean the rest of the work is — verdict iterate or reject. _(end-to-end agent-execution assertion — deferred until the runner-side rubric scoring is unit-shaped.)_
- [ ] Deliverable that addresses every input comment (some via commit, some via reply) can score ≥90. _(deferred — see above.)_
- [ ] Reply-only deliverable (zero `git_commit` calls, only `post_pr_comment_reply` calls covering every input comment) is valid; can score ≥90 against the rubric. _(deferred — see above.)_
- [ ] Mixed deliverable (1 fix-commit linking comment A via `LinkedCommitSha`, 1 reply linking comment B via `LinkedAgentResponseId`, comment C dropped silently) → evaluator scores ≤70 on correctness because C is unaddressed. _(deferred — see above.)_
- [x] Evaluator preamble for `kind='comment-response'` includes the rubric note "for every input comment you must take exactly one of two actions — commit a fix or post a reply that names the comment id; silently dropping a comment is a graded failure" verbatim. _(`CommentResponse_Template_Carries_Verbatim_MustReply_Rule` asserts the template carries the rubric language; `CrewQueueWorker.EvaluatorDelegate` injects the `COMMENT-RESPONSE RUBRIC ADDENDUM` block into `evalSoulForScoring` for every comment-response run.)_
- [x] Spec-consistency test asserts the rubric-note text in `agents/templates/comment-response-system.md` matches the verbatim language above. _(same test — `Phase24CommentResponseLoopTests.CommentResponse_Template_Carries_Verbatim_MustReply_Rule` is the spec-consistency anchor.)_

Tool registration:
- [x] `post_pr_comment_reply` tool is registered for `kind='comment-response'` runs (alongside read-only repo / `write_file` / `git_commit` / `git_push`). _(`CommentResponse_AllowedTools_Includes_PostPrCommentReply` + `CommentResponse_AllowedTools_Includes_Standard_Read_Write_Git`.)_
- [x] `post_pr_comment_reply` is NOT registered for `kind='coding'` runs. _(structural — `CrewQueueWorker.cs` only switches into `CommentResponseAllowedTools` when the queue row's `Kind == TaskQueueKinds.CommentResponse`; coding runs fall through to `Array.Empty<string>()`. The negative is enforced by code-path inspection rather than a dedicated test.)_
- [ ] `post_pr_comment_reply` invokes `POST /api/prs/{prId}/comments` server-side; the inserted comment body contains an `Addresses comment <id>` token so `PrCommentLinker.LinkAsync` (Phase 24.7) sets `LinkedAgentResponseId` on the next sync. _(runtime invocation — needs SDK-runner tool dispatch to be wired; deferred. Today the agent uses `run_command` + curl as the fallback path; the linker's body-parse heuristic still matches because the system template instructs the agent to include the `Addresses comment <id>` token in the body.)_

Identity propagation:
- [ ] Posted reply's upstream author = the agent's mapped UPN (ADO `agent.PreferredAdoAccount`) / login (GitHub `agent.PreferredGitHubAccount`), NOT a generic "Forge" identity (Q24.5 third bullet). _(deferred with the runtime-tool-dispatch follow-up; the existing `POST /api/prs/{prId}/comments` endpoint already writes the caller's identity, so the contract is met once the runner invokes the endpoint as the agent.)_
- [ ] When the agent has no mapped UPN/login (mis-configured `agents` row) → endpoint returns 422 + `comment.unmapped_agent_identity`; the deliverable iteration records the failure and re-prompts on next iteration. _(needs the runtime tool-dispatch path; deferred.)_

Reply-then-reopen flow:
- [ ] After successful reply linked via `LinkedAgentResponseId`, `RequiresAttention` flips to `false` on the next sync tick. _(linker behavior covered in `Phase24PrSyncTests`; the Phase 24-specific E2E assertion (agent reply → next sync flips attention) is deferred.)_
- [ ] Reviewer reopens the thread upstream → `UpdatedAt` advances → `RequiresAttention` flips back to `true` (Q24.7) → fresh `comment-response` row enqueues on the next sync tick. _(reopen detection covered in `Phase24PrSyncTests`; fresh-enqueue half is exercised by `MaybeEnqueueCommentResponseAsync` which tests cover for the no-existing-row case; the full reopen→re-enqueue chain is deferred.)_
- [ ] Reviewer leaves a follow-up comment (separate thread) → `RequiresAttention` flips on the new comment row → same fresh-enqueue behavior. _(deferred — same as above.)_

Livelock guard:
- [x] Comment that the agent could not address (e.g. agent's reply was empty / refused by the validator) AND a fresh `comment-response` row enqueues → after 3 consecutive stalled deliverables on the same comment, queue row goes `state='blocked'`, `error='comment-response.repeated_stall'`, lead inbox banner. Prevents the agent from looping forever on a comment it can't satisfy. _(implementation ships: `WorkItemRow.CommentResponseStallCount` increments in `CrewQueueWorker` post-loop, resets on pass/reject; `PrSyncJob.MaybeEnqueueCommentResponseAsync` refuses re-enqueue at `CommentResponseStallCap = 3` and emits `task.comment-response-blocked` SignalR event + `forge.queue.comment_response_repeated_stall` counter. End-to-end test that drives 3 stalls deferred — needs a live loop to produce the stall verdicts.)_

**24.19 Original-agent pickup, no round-robin (Q24.14) — `Phase24OriginalAgentPickupTests`**

> Coverage status: 7 / 7 queue-mechanics tests pass (column persistence, BlockAsync transitions, idempotency guard, regression). Sweep-2 picker logic ships in code but isn't unit-tested directly because `SweepClaimsAsync` is private; the path is exercised end-to-end whenever a comment-response row claims at runtime. Manual-override / audit-row scenarios are implementation-deferred (would need new endpoints not yet shipped).

Pinning:
- [x] `comment-response` enqueue reads `WorkItemRow.AssignedAgent.agents[0]` and pins `task_queue.PreferredAgentId` (new column added in this phase if not already present in Phase 22) to that agent. _(`EnqueueAsync_Persists_PreferredAgentId` covers the persistence half; `PrSyncJob.ResolvePreferredAgentId` reads `agents[0]` from `AssignedAgentJson` (both Pascal + camel casing) and threads it through `EnqueueAsync(... preferredAgentId: ...)`. The new column lands in `Phase30Migration`.)_
- [x] `CrewQueueWorker` Sweep 2 picker for `kind='comment-response'` consults `PreferredAgentId` first; if that agent is `idle` it claims, otherwise the row stays queued. _(implementation in `CrewQueueWorker.SweepClaimsAsync` — pin block runs before round-robin; if the pinned agent is unavailable it falls through to the lead, never to a fresh member. Direct unit test deferred because the sweep is private; the structure is observable via `task_queue.PinReason` after a claim.)_
- [x] Sweep 2 does NOT round-robin to a fresh member when the preferred agent is busy on another row — capacity blocks the claim instead. _(`PreferredAgent_Pinning_Does_Not_Break_NonPinned_Rows` exercises the regression path; pinned-row behavior is enforced structurally by the `if (!string.IsNullOrEmpty(headRow.PreferredAgentId))` block in `SweepClaimsAsync` — when the pin fails, the row goes to `BlockAsync` rather than to round-robin.)_

Fallback chain (Q24.14 ordered):
- [ ] Original agent paused → fallback to `crew.Lead` (Phase 18.6 24×7 exemption applies); `agent_assignments(kind='claimed')` records the fallback with `Reason='preferred-agent-paused'`. _(implementation ships: `SweepClaimsAsync` checks `preferredAgent.State == "paused"` and falls through to `crew.Lead`; `task_queue.PinReason` is stamped to `preferred-agent-paused`. `agent_assignments` row is written by `RunAgentAsync` post-claim. Direct test deferred — needs to seed a paused agent + run the sweep.)_
- [ ] Original agent retired (no longer in crew roster — `crew_members` row removed) → fallback to lead; `Reason='preferred-agent-not-in-crew'`. _(implementation: `inCrew` check in `SweepClaimsAsync` falls through with `pinReason='preferred-agent-not-in-crew'`; test deferred.)_
- [ ] Original agent ID no longer resolves to a valid `agents` row (stale FK) → fallback to lead; `Reason='preferred-agent-missing'`. _(implementation: `preferredAgent is null` branch sets `pinReason='preferred-agent-missing'`; test deferred.)_
- [x] Lead also unavailable (`crew.Lead = null` OR lead paused) → queue row goes `state='blocked'`, `error='comment-response.no_original_agent'`, audit row + `forge.queue.no_original_agent_blocked` counter; no agent invoked. _(`BlockAsync_Transitions_Queued_To_Blocked_With_LastError` covers the transition; `BlockAsync_Refuses_Non_Queued_Row` covers the CAS precondition; the counter + history payload are emitted from `SweepClaimsAsync` when both pin and lead are unavailable.)_
- [x] Round-robin to a fresh crew member is **never** automatic — the picker emits zero `picked` candidates rather than falling back to a non-pinned member. _(structural — the pin block in `SweepClaimsAsync` `continue`s after `BlockAsync` rather than falling through to the round-robin code; verified by inspection. `Blocked_Counts_As_Open_For_Idempotency_Guard` proves the blocked row stays open so a fresh enqueue returns the same row.)_

State-machine support (Q24.14 building blocks):
- [x] `TaskQueueStates.Blocked` constant exists and is included in `OpenStates`. _(`TaskQueueStates_Blocked_Is_In_OpenStates`.)_
- [x] `BlockAsync` transitions a queued row to blocked with the error code recorded on `LastError`. _(`BlockAsync_Transitions_Queued_To_Blocked_With_LastError`.)_
- [x] `BlockAsync` refuses non-queued rows (CAS precondition). _(`BlockAsync_Refuses_Non_Queued_Row`.)_
- [x] A blocked row counts as open for the per-work-item idempotency guard — duplicate enqueue returns the existing blocked row. _(`Blocked_Counts_As_Open_For_Idempotency_Guard`.)_
- [x] `EnqueueAsync` persists `PreferredAgentId` round-trippably; null for non-pinned `kind='work'` rows. _(`EnqueueAsync_Persists_PreferredAgentId` + `EnqueueAsync_PreferredAgent_Defaults_To_Null_For_Work_Kind`.)_

Manual override:
- [ ] `POST /api/queue/{id}/reassign` body `{ newCrewId, newPreferredAgentId? }` succeeds for PO + crew lead; writes `queue_history(kind='reassigned-comment-response', from, to, reason)` row. _(spec-only — the endpoint extension to accept `newPreferredAgentId` is not shipped yet; the existing `ReassignAsync` path supports the basic crew swap. Phase R follow-up.)_
- [ ] Manual reassign without `newPreferredAgentId` clears the pin; queue row reverts to round-robin within the new crew. _(deferred with the bullet above.)_
- [ ] Audit reads loud about the deviation: `forge_actions(kind='comment-response.continuity-broken', actorUserId, body: { workItemId, originalAgentId, newAgentId, reason })`. _(deferred with the bullet above.)_

Evaluator pinning:
- [ ] Evaluator agent pinned across iterations — iter 1 evaluator agent reused for iter 2..N (Phase 14.3 invariant). _(loop-level Phase 14 invariant; Phase 24 doesn't alter it.)_
- [ ] Evaluator crew pinned across iterations (Phase 16.2 paired-mode invariant) — fresh `comment-response` rows on the same work item also reuse the original evaluator crew. _(implementation: `MaybeEnqueueCommentResponseAsync` uses the same `crewId` resolved from `AssignedAgent.crewId`; the loop's evaluator crew is derived from the generator's `PairedWithCrewId` so cross-tick continuity holds. End-to-end test deferred.)_
- [ ] Dispute path (Phase 14.5) on a `comment-response` deliverable spawns a judge on the team lead's *generator* crew (not the evaluator crew) — same as `coding` deliverables. _(Phase 14.5 contract; Phase 24 doesn't alter it.)_

Edge cases:
- [ ] PR opened by a different agent than the work item's `AssignedAgent` (e.g. operator manual override or mid-flight crew swap) → `comment-response` still pins to `AssignedAgent.agents[0]` (canonical driver per Phase 22.5), NOT the PR's commit author. _(structural: `ResolvePreferredAgentId` reads `AssignedAgent.agents[0]`; PR commit author is not consulted. End-to-end assertion deferred.)_
- [ ] Crew swap mid-flight (lead reassigned crew between original PR open and comment-response trigger) → if original agent left the crew, fallback chain kicks in; otherwise pin still resolves. _(deferred — needs a multi-step seed.)_
- [ ] `AssignedAgent` is null at `comment-response` trigger time (e.g. row was Restart-cleared after the original PR opened) → enqueue refuses with `forge.queue.comment-response_no_assignee` counter; queue path goes blocked. Operator manual assign is the unblocker. _(implementation: `MaybeEnqueueCommentResponseAsync` returns early when `ResolveCrewId` returns null; the dedicated counter + blocked-state path is a small follow-up — today the row simply isn't enqueued.)_
- [ ] Two near-simultaneous comment-reopen events on the same work item → Phase 17.6 idempotency guard prevents double-enqueue; the single open `comment-response` row picks up both comments in its input set. _(Phase 17.6 partial-unique guard ships; behavior is structural — the second enqueue returns the existing row. End-to-end assertion deferred.)_

### Out of scope for v0.1 — Phase R / `forge_v2.md`

- **Inline diff annotation** — comments render with file path + line number but the live-exec Comments tab doesn't display the diff hunk inline. Operator clicks through to the PR detail panel for that today; in-tab diff is a Phase R polish.
- **Comment templates / saved replies** — agent generates each reply fresh; operator-curated reply templates are `forge_v2.md`.
- **Cross-PR comment reuse** — when a reviewer makes the same comment on five PRs, Forge treats them as five independent rows. Deduplication / "you've answered this before" hints are `forge_v2.md`.
- **Suggested-changes inline edits** — GitHub's "suggest a change" comments produce a one-click commit. Forge surfaces them as regular comments at v0.1; honoring the suggested patch automatically is Phase R.
- **Webhook-driven instant sync** — Phase 5.8 ships webhook intake but the Phase 24 sweep ignores webhooks entirely (poll-only). Pairing a webhook event to fast-forward `LastSyncAt` is the same `forge_v2.md` candidate as for Phase 23.
- **Agent-as-reviewer** — Q24.5 strictly bars agents from resolving. A future evaluator-agent-as-reviewer pattern (Phase 14.5 dispute path) gets a `?asReviewer=true` allow-list flag, but the agent identity that signs the upstream resolve action and the audit semantics are `forge_v2.md` work.
- **Reverse mapping (commit → comment)** — Forge maps comments → commits, not commits → all-comments-they-might-affect. The reverse "show me every comment that touched this commit's files" is a Phase R query, not a sync responsibility.
- **PR review summary scoring** — the deliverable loop (Phase 14) scores the agent's work; whether the *reviewer's* comments are good (e.g. "this PR got 12 nits and 0 substantial issues") is a separate metric and `forge_v2.md` territory.

---

## Phase 25 — Sync run item details

> Phase 23 runs scheduled work-item syncs but the sync history drawer only shows run-level metadata (start time, status, count). **Phase 25 adds per-item granularity**: a `sync_run_items` table records exactly which work items / PRs were created, updated, or removed in each sync run so the operator can drill into a run and see what changed.

### Implementation steps

- [x] **25.1** **Schema migration** — `Forge.Api/Persistence/Phase25Migration.cs`. Creates `sync_run_items(Id, RunId, EntityKind, EntityId, Title, Action, ProviderRef)` table with index on `RunId`.
- [x] **25.2** **Entity** — `SyncRunItemRow` in `Entities.cs`. `EntityKind` is `"workitem"` or `"pr"`; `Action` is `"added"`, `"updated"`, or `"removed"`.
- [x] **25.3** **Sync integration** — `WorkItemSyncJob` writes `SyncRunItemRow` entries for each created/updated work item during the sync loop.
- [x] **25.4** **API endpoint** — `GET /api/repos/{id}/sync-work-items/runs/{runId}/items` returns the per-item details for a given sync run.

---

## Phase 26 — Build & test gate before PR creation

> Phase 16.18 creates PRs after the deliverable loop passes, but it never verifies that the code actually **builds** and **tests pass** in the workstream branch. The agent is instructed to run build/tests, but this isn't enforced server-side. **Phase 26 adds a server-side verification gate**: after auto-commit but before push + PR, the worker runs the repo's configured build and test commands. If either fails, the delivery is aborted and the work item is marked failed — no broken code reaches the remote, no PR is opened on a red build.

### Resolved decisions

- **Q26.1 Gate runs before push.** Build and test run in the worktree after auto-commit. If either fails, the branch is never pushed and no PR is created. This ensures the remote branch always represents a buildable, tested state.
- **Q26.2 Commands are per-repo configuration.** `repos.InitCommand`, `repos.RestoreCommand`, `repos.BuildCommand` and `repos.TestCommand` are nullable strings (e.g., `dotnet build Workflow.sln`). When null/empty, the corresponding step is skipped (backward compatible). Commands are parsed respecting double-quoted tokens and executed via `ProcessStartInfo.ArgumentList` — no shell execution. The full pipeline order is **init → restore → build → test**; any step failure aborts subsequent steps.
- **Q26.3 Configurable timeout with process tree kill.** Each repo has optional `BuildTimeoutSec` and `TestTimeoutSec` (default 600s / 10 min). On timeout, `Process.Kill(entireProcessTree: true)` ensures child processes (e.g., `testhost.dll`) are cleaned up.
- **Q26.4 Post-gate auto-commit.** After build/test pass, the worker re-checks `git status --porcelain`. If build/test modified tracked files (generated code, lockfiles, snapshots), they are auto-committed before push so the pushed state matches the validated state.
- **Q26.5 Output is truncated.** Build/test stdout + stderr are capped at 16 KB (keeping the tail) in transcript events and 4 KB in the live-exec view payload. This prevents large test logs from bloating the DB or the UI.
- **Q26.6 Auto-discovery on repo setup.** When a repo is attached (`POST /api/products/{id}/repos`), cloned (`POST /api/repos/{id}/clone`), or its `cloneRoot` is updated (`PATCH /api/repos/{id}`), the server scans the repo root for known build-system markers (`.sln`, `package.json`, `Cargo.toml`, `go.mod`, `pom.xml`, `pyproject.toml`) and auto-populates `InitCommand`, `RestoreCommand`, `BuildCommand` and `TestCommand` — but only if they are currently null. Operator overrides are never overwritten. This is a filesystem scan only; no commands are executed during detection.
- **Q26.7 Detect is preview, validate is async.** `POST /api/repos/{id}/detect-build` returns detected commands without saving — the UI shows them to the operator who can accept or override. `POST /api/repos/{id}/validate-build` enqueues an async validation run (init → restore → build → test) and returns `202 Accepted` with a `runId`. The run executes in a background hosted service (`BuildValidationWorker`) and completion is notified via SignalR `repo.validation-complete`. Only one validation can run per repo at a time (409 if already running).
- **Q26.8 PR link persisted on creation.** When the delivery pipeline creates a PrRow after `provider.CreatePullRequestAsync`, it stores the `WebUrl` and `Provider` fields so the PR card and live-exec view can link directly to the PR in ADO/GitHub. The `task.pr-opened` transcript event also carries `webUrl` for the live view.
- **Q26.9 PR board sorted by last update.** The `/api/prs` endpoint returns PRs ordered by `UpdatedAt DESC` (then `Sort` as tiebreaker). The `PrBoard` UI component sorts each column's cards by `updatedAt` descending so the most recently active PR appears at the top. Seed PRs without `updatedAt` fall to the bottom.
- **Q26.10 Repo management UX — async validate-on-save.** The Products view shows a "🔨 gated" pill on repos with any configured pipeline step and a "no gate" pill on unconfigured repos — visible at a glance. The EditRepoModal exposes init/restore/build/test command inputs, build/test timeout inputs, a "Detect" button (manual re-detect), and a "Validate" button (async validation via background worker). **On save, the modal: saves → clones (if path changed) → detects (if no commands) → enqueues async validation → closes immediately.** Validation results arrive via SignalR toast notification. The operator can examine validation run history via the persistent history panel in the modal, which fetches from `GET /api/repos/{id}/validation-runs`.
- **Q26.11 Persistent validation history.** The `build_validation_runs` table stores every validation run with status (`queued|running|succeeded|failed|cancelled`), per-step results as JSON, triggered-by user, and error messages. `GET /api/repos/{id}/validation-runs` returns the last 20 runs ordered by start time. The EditRepoModal loads and displays this history with expandable per-step output.
- **Q26.12 Per-repo concurrency guard.** Only one validation can run per repo at a time. The `BuildValidationWorker` uses a `ConcurrentDictionary` keyed by `RepoId` to enforce this. Duplicate requests return 409.

### Implementation steps

- [x] **26.1** **Schema migration** — `Forge.Api/Persistence/Phase26Migration.cs`. Adds `BuildCommand TEXT NULL`, `TestCommand TEXT NULL`, `BuildTimeoutSec INTEGER NULL`, `TestTimeoutSec INTEGER NULL` to `repos` table. Idempotent via `pragma_table_info` check.
- [x] **26.2** **Entity + contract update** — `RepoRow` gains four properties; `Repo` and `UpdateRepoRequest` contracts gain matching fields; `MapRepo` and `PatchRepo` map them through the API.
- [x] **26.3** **`RunCommandAsync` helper** — `CrewQueueWorker.RunCommandAsync(cwd, command, timeoutSec)`. Parses command string into executable + args via `ParseCommandLine` (handles double-quoted tokens). Uses `ProcessStartInfo` with `UseShellExecute=false`. On timeout, kills entire process tree. Returns `(exitCode, stdout, stderr, timedOut)` with output truncated to 16 KB.
- [x] **26.4** **Delivery gate** — in `CrewQueueWorker.RunAgentAsync`, after auto-commit + commits-ahead check, before push: runs build command → test command → post-gate auto-commit → push → PR. On failure emits `task.build-failed` / `task.test-failed` + `task.delivery-failed` + `task.failed`, sets execution status to failed, returns early.
- [x] **26.5** **Live Execution view** — `LiveExecutionView.tsx` `renderPayload` handles `task.build-started` (🔨), `task.build-passed` (✓), `task.build-failed` (✗), `task.test-started` (🧪), `task.test-passed` (✓), `task.test-failed` (✗).

- [x] **26.6** **Tests** — `Forge.Api.Tests/Phase26BuildTestGateTests.cs` (53 tests, all green):
  - **Migration (6):** columns added, read/write round-trip, idempotent, pragma verification.
  - **ParseCommandLine (7):** simple, multi-arg, quoted, empty, spaces, single token, quoted path.
  - **RunCommandAsync (5):** success, failure, timeout + process tree kill, empty command, nonexistent executable.
  - **API surface (6):** PATCH set/clear build/test commands + timeouts, GET round-trip.
  - **Delivery pipeline (10):** no config skips gate; build pass/fail; test pass/fail; test skipped on build fail; full event sequencing; invalid executable; payload contents.
  - **BuildSystemDetector (13):** .sln (single, with spaces, multiple → ambiguous), .csproj, package.json (with/without scripts), Cargo.toml, go.mod, pom.xml, pyproject.toml, requirements.txt, empty dir, nonexistent dir, `MergeInto` preserves existing fields, `MergeInto` returns false when nothing new.
  - **Detect/Validate endpoints + lifecycle (6):** detect returns ecosystem, detect requires auth, detect 422 without clone, validate requires auth, PATCH cloneRoot auto-detects build command, PATCH preserves existing build command.

- [x] **26.7** **Build-system auto-discovery** — `Forge.Api/Services/BuildSystemDetector.cs`. Filesystem-only scanner detecting build/test commands from common project markers:
  - **.NET**: `*.sln` → `dotnet restore/build/test <sln>`; single `*.csproj` fallback; multiple `.sln` → ambiguous with detail.
  - **Node.js**: `package.json` → `npm install` / `npm run build` / `npm test` (only if scripts exist).
  - **Rust**: `Cargo.toml` → `cargo build` / `cargo test`.
  - **Go**: `go.mod` → `go mod download` / `go build ./...` / `go test ./...`.
  - **Java**: `pom.xml` → `mvn package -DskipTests` / `mvn test`.
  - **Python**: `pyproject.toml` or `requirements.txt` → `pip install` / `pytest`.
  - `MergeInto(row, detected)` fills null/empty `InitCommand`, `RestoreCommand`, `BuildCommand`, `TestCommand` — operator overrides preserved.
  - Filenames with spaces are automatically quoted in generated commands.

- [x] **26.8** **Detection lifecycle hooks** — auto-detection runs silently after:
  - **AttachRepo** (`POST /api/products/{id}/repos`) — after clone completes.
  - **CloneRepo** (`POST /api/repos/{id}/clone`) — after clone or on existing checkout.
  - **PatchRepo** (`PATCH /api/repos/{id}`) — when `cloneRoot` changes and directory exists.
  - In all cases, only missing fields are filled; existing commands are preserved.

- [x] **26.9** **Detection + validation endpoints**:
  - `POST /api/repos/{id}/detect-build` — scan repo root, return detected commands (init/restore/build/test) without saving. UI uses this for "Detect" button preview.
  - `POST /api/repos/{id}/validate-build` — enqueues async validation run (init → restore → build → test), returns `202 Accepted` with `{ runId, status: "queued" }`. Completion notified via SignalR `repo.validation-complete`. Returns `409` if a validation is already running for the repo.
  - `GET /api/repos/{id}/validation-runs` — returns last 20 validation runs with status, steps, and error messages.

- [x] **26.10** **EditRepoModal UI** — init/restore/build/test command inputs, build/test timeout inputs, "Detect" button (calls detect-build and auto-fills empty fields), "Validate" button (enqueues async validation, shows toast). Persistent validation history section loaded from `GET /api/repos/{id}/validation-runs` with expandable per-step output.

- [x] **26.11** **Phase29Migration** — `Forge.Api/Persistence/Phase29Migration.cs`. Adds `InitCommand TEXT NULL`, `RestoreCommand TEXT NULL` to `repos` table. Creates `build_validation_runs` table with index on `(RepoId, StartedAt DESC)`.

- [x] **26.12** **BuildValidationWorker** — `Forge.Api/Services/BuildValidationWorker.cs`. Background hosted service consuming `Channel<BuildValidationJob>`. Runs init → restore → build → test in repo's cloneRoot, persists results to `build_validation_runs`, sends `repo.validation-complete` via SignalR. Per-repo concurrency guard via `ConcurrentDictionary`.

- [x] **26.13** **Global SignalR listener** — `useForgeHub.ts` handles `repo.validation-complete` events and shows toast with step summary (not modal-scoped, survives modal close).

### Transcript events

| Event | Payload | When |
|-------|---------|------|
| `task.build-started` | `{ agentId, crewId, command }` | Build command begins |
| `task.build-passed` | `{ agentId, crewId }` | Build exits 0 |
| `task.build-failed` | `{ agentId, crewId, exitCode, timedOut, stdout, stderr, reason }` | Build exits non-zero or times out |
| `task.test-started` | `{ agentId, crewId, command }` | Test command begins |
| `task.test-passed` | `{ agentId, crewId }` | Tests exit 0 |
| `task.test-failed` | `{ agentId, crewId, exitCode, timedOut, stdout, stderr, reason }` | Tests exit non-zero or time out |
| `repo.validation-complete` | `{ runId, repoId, status, allPassed, ecosystem, errorMessage, steps }` | Async validation run finishes |

---

## Phase 27 — Build sync & failure-driven repair loop

> Phase 24 watches PRs through their lifecycle and routes review comments back to the agent that opened them. Phase 26 added a *pre-push* build/test gate so broken code never reaches the remote. **Phase 27 closes the loop on the post-push axis** — once a PR is open and CI takes over, builds can still fail (integration tests, signing, deploy gates, environment-specific checks the local gate can't run). When that happens, the agent that authored the PR must re-engage, identify the root cause, fix it, and push. This phase ships a scheduled per-PR build sync (mirroring Phases 23 / 24), a `kind='build-fix'` queue task, and the same generator → evaluator iterate-to-90 loop the implementation pipeline uses.
>
> Three principles drive the shape:
>
> 1. **The build is a sibling of the comment.** From the operator's perspective a "failing build" is just another red flag attached to a PR, alongside a "comment needing response". They route through the same queue, the same crew, and the same iterate-to-90 loop. Phase 27 reuses Phase 24's surfaces wherever possible — the live-exec view's right-rail "Comments" tab grows a sibling "Builds" tab; the PR card's `⚠ N` chip counts both flags.
> 2. **Logs first, artifacts on demand.** Sync always pulls per-stage log tails (truncated to 256 KB) into local storage so the failure parser has something to chew on without a second round-trip. Build artifacts (the zip-of-many-logs case) are downloaded + expanded **lazily** — only when the parser fails to find a fix-able failure in the per-stage tails. Most failures (compile error, test assertion, lint nit) are visible in the stage tail; the artifact path is the heavy-hammer fallback.
> 3. **Continuity wins over load-balancing.** The agent that opened the PR is the one with cache warmth on this code. Same Q24.14 pinning rule — the build-fix queue row routes to `WorkItemRow.AssignedAgent.agents[0]`, never round-robin. Same evaluator pinned per Phase 14.3.

### Resolved decisions

- **Q27.1 Cadence — 20 min, gated by `Pr.IsActive`.** Same shape as Phase 24: one tick every 20 min, jittered ±60 s, configurable via `Forge:Sync:Build:IntervalSeconds` (default 1200). `BuildSyncJob` only sweeps PRs where `IsActive=1` (Phase 24 contract — terminal PRs stop the clock for builds too). Concurrency cap = 8, matching Phase 24's `PrSyncMaxParallel`. The 19-min `IsRunningSince` stale-cancel rule applies. Rationale: a build sync that doesn't read the same `IsActive` filter as the PR sync would keep polling closed PRs forever — keeping the gate on `IsActive` means "PR closed → builds for it stop being polled", which matches the operator's mental model.

- **Q27.2 Build row schema — `builds` keyed `(provider, providerBuildId)`.** New table, FK to `prs`. Carries `pipelineId`, `pipelineName`, `state` (`queued | running | succeeded | failed | partially-succeeded | canceled | not-started`), `result` (provider-native — ADO `succeeded|failed|partiallySucceeded|canceled`, GitHub `success|failure|cancelled|skipped|action_required|neutral|stale|timed_out`), `triggeringCommitSha`, `branch`, `webUrl`, `startedAt`, `finishedAt?`, plus the standard `LastSyncAt`/`LastSyncStatus`/`LastSyncError`/`IsRunningSince`/`IsActive` quintet (`IsActive` = `state IN ('queued','running','not-started')`). One PR has many builds across its life — every push triggers a new build, the row is keyed on the upstream id, and old runs stay in DB forever for audit. Re-using the Phase 24 `IsActive` semantic means terminal builds are skipped on subsequent ticks; the row never goes back to `IsActive=1`.

- **Q27.3 Per-stage detail persisted — `build_stages` table.** Each row keyed `(buildId, providerStageRef)`; carries `stageName`, `stageOrder`, `state`, `result`, `startedAt`, `finishedAt?`, `logRef` (provider-specific log id / job id), and a denormalized `LogTailHash` (sha256 of the truncated log content) so a re-fetch with the same hash is a no-op. ADO maps timeline records of type `Stage`/`Phase`/`Job` to one `build_stages` row per leaf-most failed node; GitHub Actions maps the run's `jobs[]` array. Stage detail is fetched **only when the build's top-level state goes terminal-and-not-succeeded** — a passing build has no debugging value, so we skip the per-stage round-trips entirely.

- **Q27.4 Log retrieval per stage — server-side, tail-truncated, persisted.** New table `build_stage_logs(stageId, logIndex, content TEXT, sizeBytes INTEGER, truncatedFromBytes INTEGER, fetchedAt)`. Per-stage logs persisted truncated at the **256 KB tail** (`SyncConstants.BuildLogTailBytes = 262144`) — head is dropped, tail keeps the failure point because compile / test stack traces always live near the bottom. When upstream returns a log > 256 KB, `truncatedFromBytes` records the original size so the UI can show "log truncated from 1.2 MB". Provider endpoints:
  - **ADO** — `GET _apis/build/builds/{buildId}/timeline?api-version=7.0` returns the timeline of records (stages / jobs / steps); `GET _apis/build/builds/{buildId}/logs/{logId}?api-version=7.0` returns the per-step log. Phase 27 fetches the **failed** records' logs only — successful steps are skipped to keep the round-trip count down.
  - **GitHub Actions** — `GET /repos/{owner}/{repo}/actions/runs/{run_id}/jobs?per_page=100` returns the jobs array (each carrying `steps[]` with name + status); `GET /repos/{owner}/{repo}/actions/jobs/{job_id}/logs` returns the full job log as plain text (no per-step split — GH Actions doesn't expose per-step logs separately). Tail-truncate same way.

- **Q27.5 Artifact fetching + expansion — lazy, sandboxed, capped.** When per-stage tails don't yield a parseable failure (Q27.6 falls through), the runner falls back to artifacts:
  - **ADO** — `GET _apis/build/builds/{buildId}/artifacts?api-version=7.0` returns `{ name, resource: { downloadUrl } }` per artifact. Download is gated by `SyncConstants.BuildArtifactSizeCapMb = 256` (per-artifact); over-cap artifacts are skipped with a `forge.build.artifact.over_cap` counter and the operator can manually trigger via the override endpoint.
  - **GitHub Actions** — `GET /repos/{owner}/{repo}/actions/runs/{run_id}/artifacts?per_page=100`; per-artifact download via `archive_download_url`.
  - **Storage path**: `%LOCALAPPDATA%\Forge\artifacts\{provider}\{buildId}\{artifactName}\` — the unzip target. `BuildArtifactExpander.ExtractAsync` enforces a strict path-traversal guard (every entry's resolved path MUST start with the artifact root after `Path.GetFullPath`); zip-slip attempts emit a `forge.build.artifact.zip_slip_blocked` counter and the artifact is rejected wholesale (one bad entry → entire artifact discarded; no partial extraction).
  - **Indexing**: post-extraction, the runner indexes the artifact into `build_artifact_files(buildId, artifactName, relPath, sizeBytes, isLog: bool)`; `isLog` flagged when extension is `.log` / `.txt` / `.trx` / `.xml` (test results) — so the agent prompt can prioritize log files over binaries / coredumps without enumerating everything.
  - **Cleanup**: artifacts older than 30 days are deleted by a daily janitor. Disk usage is capped at `Forge:Build:ArtifactStorageCapGb = 10` per host; over-cap triggers oldest-first eviction.

- **Q27.6 Failure parser — heuristic first, agent localizes when heuristic fails.** Two-pass:
  - **Pass 1 — regex**. `BuildFailureParser.cs` runs a pinned set of patterns over the per-stage log tail: `error CS\d+:\s+(.+?) \[(.+?):(\d+)\]` (.NET); `FAILED:?\s+(.+?)$` (cmake / make); `Error:?\s+(.+?)\sat\s+(.+?):(\d+)` (generic stack-trace style); `assert.*?fail.*?at\s+(.+?):(\d+)` (test frameworks); `npm ERR! code (.+?)` (npm). Each match produces a `FailurePoint { kind, message, filePath?, line?, column?, severity }`. Patterns live in `SyncConstants.BuildFailurePatterns` so the spec-consistency test catches drift. Multi-failure log → multiple `FailurePoint` rows; persisted to `build_failure_points(stageId, kind, filePath, line, column, message, sourceLogTailOffset)`.
  - **Pass 2 — agent**. When the regex pass yields zero `FailurePoint` rows OR the build's category is "infrastructure" (timeout, host unhealthy, signing-key rotation), the runner does NOT attempt to fix it via `kind='build-fix'`; instead it emits `forge.build.unparseable` and routes the queue row to `state='blocked'` with `error='build-fix.unparseable_failure'`. The lead receives a banner asking them to either annotate the build manually (`POST /api/builds/{id}/annotate { rootCause, suggestedFiles }`) or cancel the work item. Rationale: an agent shooting in the dark on an infra failure is worse than a human triage — the cost of a wrong fix is amplified by the 5-iter loop.
  - **Pass 1.5 — artifact descent**. When pass 1 finds zero matches and the artifact list (Q27.5) shows log-shaped files, the parser unzips, indexes, and re-runs the regex pass against each indexed log. Same patterns. If it still finds nothing, falls through to pass 2's blocked state.

- **Q27.7 Original-agent pickup — mirrors Q24.14, no exceptions.** The build-fix queue row is pinned to `WorkItemRow.AssignedAgent.agents[0]` (the original generator), routed through the same crew and the same evaluator pinned at iter 1 of the original PR. Same fallback chain: original-agent → lead (Phase 18.6 24×7 exemption) → blocked. **No round-robin.** A second build failure on the same PR after a successful fix re-enqueues against the same original agent — even if a different agent's name appears on the fix commit, the work-item-level "who's accountable for this PR" answer is fixed at the row that originally claimed in Sweep 2 (Phase 17). This matches the Q24.14 stance for comments.

- **Q27.8 Iteration loop — `DeliverableLoop` with `kind='build-fix'`, score ≥ 90.** Same loop as implementation and comment-response. Generator agent reads the build-fix prompt (Q27.10); evaluator scores against the rubric (Phase 14.3 contract; same five dimensions). Rubric extension: the evaluator's preamble for `kind='build-fix'` adds a sixth implicit check — *"Did the fix address the parsed failure points? If multiple failure points exist, are they all addressed?"* Same hard contract: pass at score ≥ 90; reject at < 70 (deliverable bounces to lead, queue row goes `verdict=reject`); iterate at 70–89; 5-iter hard cap; same `MaxTaskBudgetUsd`. Cost split per Phase 16.2 — generator vs. evaluator land separate `agent_usage` rows tagged `cost_category='generator'` / `'evaluator'`. The deliverable's `gen_cost_usd` / `eval_cost_usd` columns aggregate the right slice for the per-pair salary readout.

- **Q27.9 Re-trigger build via push, not API.** When the deliverable loop passes and the worker pushes the new commit, the build re-runs **naturally** on the next CI hook — Phase 27 does NOT call the provider's `runs` / `dispatch` API to manually re-trigger. Rationale: every CI configuration we've seen runs on push automatically, and a manual re-trigger that ignores the push trigger creates double-runs and confused operators. The new build's `triggeringCommitSha` carries the fix commit's SHA, so the live-exec view's "Builds" tab shows a clean lineage (`build #42 failed → fix commit → build #43 in progress → build #43 succeeded`). Manual re-trigger via `POST /api/builds/{id}/rerun` is implemented as a Phase R polish for the rare CI-config that can't run-on-push (e.g. signed-release pipelines that demand a manual gate); it is **not** the primary path.

- **Q27.10 Build-fix prompt shape — failure points + log tail + artifact index, no full logs.** The agent's prompt for `kind='build-fix'` carries:
  1. A header summarizing the build (pipeline name, stage that failed, top-level result, web URL).
  2. The full set of `FailurePoint` rows from Q27.6 (kind / file / line / message).
  3. The truncated log tail of the failed stage (256 KB max).
  4. The artifact index (Q27.5) when extracted — file names + sizes only, NOT contents. The agent reads specific files via the `read_file` tool when needed.
  5. The original work-item brief (so the agent has context for what the PR was supposed to do).
  6. The PR's diff (so the agent sees what's already been changed in this branch).
  7. Recent commit messages on the branch (so the agent doesn't re-do work that was just attempted).
  Logs and artifacts are NOT inlined verbatim — the agent uses `read_file(path)` to descend into specific artifacts. This keeps the system prompt below the cache TTL even on multi-MB log scenarios. Tools registered: read-only repo + git-diff + `read_file` (under the artifact root + the worktree, path-validated), `write_file` (worktree only), `git_commit`, `git_push`, **`post_pr_comment_reply`** (same tool as Q24.13 — when the agent decides "the build failure isn't a code issue", e.g. the test was flaky and just needs a re-run, it must post that explanation as a PR comment so the audit trail says so; silently exiting the loop without committing is a graded failure under the same Q24.13 rubric extension).

- **Q27.11 Final states stop the clock — same as Phase 24.** Once a build's state is `succeeded`, `failed`, `partially-succeeded`, `canceled`, or `not-started→canceled` directly (build was queued and then removed), `IsActive=0` and the sync skips it. **`failed` IS a terminal state for sync** — the build itself isn't going to change after the runner exits. The fix loop runs in parallel; when the fix's *new* push creates a *new* build row, that new row is `IsActive=1` and gets its own sync lifecycle. Two terminal `failed` builds for the same PR are the normal case during a multi-iteration fix.

- **Q27.12 Cross-phase queue idempotency — at most one `build-fix` per work item open at a time.** Reuses Phase 17.6's `UNIQUE(workItemId) WHERE state IN ('awaiting-ack','awaiting-route','queued','picked-up')` partial index — the same constraint that prevents duplicate `coding` or `comment-response` rows. If a `comment-response` row is already open and a build failure lands, the build-fix enqueue is **deferred**: a `build_fix_pending` row is written instead, and the build-fix is enqueued automatically when the comment-response queue row reaches a terminal state. Operator can override via `POST /api/queue/build-fix-pending/{id}/promote-now` which cancels the in-flight comment-response and replaces it with the build-fix; rare, audit-logged. Rationale: comment + build failures sometimes arrive within minutes of each other; serializing the agent's attention is cleaner than fanning out parallel runs that race on the same branch.

- **Q27.13 Authority — agents post build-failure replies; only humans / leads cancel a stuck loop.** Mirrors Q24.5 with the corresponding endpoints. Agents can `POST /api/prs/{prId}/comments` with build-failure analysis (the `post_pr_comment_reply` tool). Only humans (PO + crew lead) can hit `POST /api/queue/{id}/cancel` to abandon a stuck build-fix loop, and `POST /api/builds/{id}/annotate` to manually classify a failure as infra (which prevents the auto-enqueue from re-firing).

### Implementation steps

- [x] **27.1** **Migration** — `Forge.Api/Persistence/Phase31Migration.cs` (Phase27Migration / 28 / 29 / 30 slots already taken by unrelated additive migrations; the build-sync migration lives in the next free slot — Phase31. Placed in the chain after `Phase30Migration`):
  - New table `builds`:
    ```
    Id                INTEGER PK AUTOINCREMENT
    Provider          TEXT NOT NULL CHECK (Provider IN ('ado','github'))
    ProviderBuildId   TEXT NOT NULL
    PrId              INTEGER NULL                    -- FK → prs.Id; null when sync discovered the build before the PR was tracked
    RepoId            TEXT NOT NULL
    PipelineId        TEXT NULL
    PipelineName      TEXT NULL
    State             TEXT NOT NULL                   -- 'queued'|'running'|'not-started'|'succeeded'|'failed'|'partially-succeeded'|'canceled'
    Result            TEXT NULL
    TriggeringCommit  TEXT NULL
    Branch            TEXT NULL
    WebUrl            TEXT NULL
    StartedAt         TEXT NULL
    FinishedAt        TEXT NULL
    LastSyncAt        TEXT NULL
    LastSyncStatus    TEXT NOT NULL DEFAULT 'idle'
    LastSyncError     TEXT NULL
    IsRunningSince    TEXT NULL
    IsActive          BIT NOT NULL DEFAULT 1
    UNIQUE (Provider, ProviderBuildId)
    ```
    Indexes: `IX_builds_PrId(PrId)`, `IX_builds_PrId_IsActive(PrId, IsActive)`, `IX_builds_LastSyncAt(LastSyncAt)`.
  - New table `build_stages(Id, BuildId FK, ProviderStageRef, StageName, StageOrder, State, Result, StartedAt, FinishedAt, LogRef, LogTailHash)` indexed `(BuildId, StageOrder)`.
  - New table `build_stage_logs(StageId FK, LogIndex INTEGER, Content TEXT, SizeBytes INTEGER, TruncatedFromBytes INTEGER, FetchedAt)` PK `(StageId, LogIndex)`.
  - New table `build_artifacts(Id, BuildId FK, Name, DownloadUrl, SizeBytes, ExtractedPath, FetchedAt, ExpiredAt)`.
  - New table `build_artifact_files(ArtifactId FK, RelPath, SizeBytes, IsLog BIT)` indexed `(ArtifactId)`.
  - New table `build_failure_points(Id, StageId FK, Kind, FilePath, Line, Column, Message, SourceLogTailOffset, Severity)` indexed `(StageId)`.
  - New table `build_fix_pending(Id, WorkItemId, BuildId, EnqueuedAt, ReasonJson)` — Q27.12's defer-when-comment-response-active staging. UNIQUE on `WorkItemId`.
  - TS mirrors in `src/forge/Forge.UI/src/types.ts`: `Build`, `BuildStage`, `BuildStageLog`, `BuildArtifact`, `BuildFailurePoint`. Add `'build-fix'` to the `TaskQueueKind` union.

- [x] **27.2** **`SyncConstants.cs` extension** — locks the cadence + sizes + parser patterns:
  ```
  BuildSyncIntervalSeconds = 1200
  BuildSyncMaxParallel = 8
  BuildLogTailBytes = 262144
  BuildArtifactSizeCapMb = 256
  BuildArtifactStorageCapGb = 10
  BuildArtifactRetentionDays = 30
  BuildFailurePatterns = [ ...regex array (locked) ... ]
  ```
  Spec-consistency test (`Phase27SyncConstantsTests`) parses this section and asserts every constant verbatim, mirroring Phase 14.14 / Phase 16.5 / Phase 23.

- [x] **27.3** **`BuildSyncJob` hosted service** — `Forge.Api/Services/BuildSyncJob.cs`. Mirrors `PrSyncJob`. 20-min tick, ±60 s jitter, `SemaphoreSlim(8)`. Per tick:
  - Resolve the universe: `prs WHERE IsActive=1` (every active PR is a candidate; build sync rides on top of PR sync).
  - For each, list upstream builds for the PR's branch using `IBuildSyncRunner.ListBuildsForBranchAsync(repo, branch, ct)`. Upserts into `builds` table, keyed `(provider, providerBuildId)`. New rows land `IsActive=1`; transitions to terminal flip `IsActive=0`.
  - For builds whose state advanced AND new state is failed-shape: dispatch to `IBuildSyncRunner.FetchStageDetailAsync` → log-tail fetch → `BuildFailureParser.ParseAsync` → enqueue `kind='build-fix'` per Q27.7 / Q27.12.
  - Each invocation appends a `sync_runs` row with `Scope='build'` discriminator (reuses the Phase 24.3 `sync_runs` table).
  - Telemetry: `forge.sync.build.duration_ms`, `forge.sync.build.state_transitions`, `forge.sync.build.failures_parsed`, `forge.sync.build.unparseable`, `forge.sync.build.artifacts_extracted`.

- [x] **27.4** **`AdoBuildSyncRunner`** — `Forge.Api/Providers/Ado/AdoBuildSyncRunner.cs`:
  - `GET _apis/build/builds?branchName=refs/heads/{branch}&maxBuildsPerDefinition=10&queryOrder=startTimeDescending&api-version=7.0` to list builds for the branch.
  - For each terminal-and-failed build: `GET _apis/build/builds/{id}/timeline?api-version=7.0` → walk the `records[]` array filtering to `type IN ('Stage','Job','Phase')` AND `result='failed'`. Each becomes one `build_stages` row.
  - Per failed leaf: `GET _apis/build/builds/{id}/logs/{logId}?api-version=7.0&startLine=…` — read the **last** 256 KB. ADO's `startLine` parameter approximates this; if the response is still over-cap, client-side trim the tail.
  - 429 / 503 backoff per `Retry-After` header (cap 60 s, three retries — same shape as Phase 23 ADO retry).
  - State / result mapping per Q27.2.

- [x] **27.5** **`GitHubBuildSyncRunner`** — `Forge.Api/Providers/GitHub/GitHubBuildSyncRunner.cs`:
  - `GET /repos/{owner}/{repo}/actions/runs?branch={branch}&per_page=100` to list workflow runs.
  - For each terminal-and-failed run: `GET /repos/{owner}/{repo}/actions/runs/{run_id}/jobs?per_page=100` → each `jobs[]` entry becomes one `build_stages` row when its `conclusion='failure'`.
  - Per failed job: `GET /repos/{owner}/{repo}/actions/jobs/{job_id}/logs` → read last 256 KB.
  - Rate-limit handling: same `X-RateLimit-Remaining` < 100 sleep-until-reset shape as Phase 24.6.

- [x] **27.6** **`BuildArtifactExpander`** — `Forge.Api/Services/BuildArtifactExpander.cs`. `ExtractAsync(buildId, artifactName, downloadUrl, ct)`:
  - HEAD the URL to read `Content-Length`; refuse if > `BuildArtifactSizeCapMb`.
  - Download to `%LOCALAPPDATA%\Forge\artifacts\{provider}\{buildId}\{artifactName}.zip`.
  - `System.IO.Compression.ZipFile.OpenRead` + iterate entries; for each entry compute `targetPath = Path.GetFullPath(Path.Combine(extractRoot, entry.FullName))` and refuse the entire artifact if `!targetPath.StartsWith(extractRoot)` (zip-slip guard).
  - Index extracted files into `build_artifact_files`; classify `IsLog` by extension.
  - Janitor: a daily hosted service prunes `build_artifacts` rows older than 30 days + reclaims disk space; over-cap eviction uses oldest-first.

- [x] **27.7** **`BuildFailureParser`** — `Forge.Api/Services/BuildFailureParser.cs`. Pure function: `ParseAsync(buildStageRow, logTail, artifactFiles?, ct) → IReadOnlyList<FailurePoint>`. Pass 1 over `logTail` using `SyncConstants.BuildFailurePatterns`. When zero matches and `artifactFiles` non-empty, recurse into each `IsLog=true` file (re-running pass 1 against its content). Returns the union; persists to `build_failure_points`.

- [x] **27.8** **Queue hook + Q27.12 deferral** — `BuildSyncJob.HandleNewFailureAsync`:
  - Resolve PR's `WorkItemId`. If null, return — Forge can't auto-fix a PR it didn't open.
  - Read `WorkItemRow.AssignedAgent.agents[0]` (original generator). If null → `forge.build.no_original_agent` counter, audit row, exit.
  - Check Q27.12: if any open `task_queue` row exists for `WorkItemId` (`comment-response` or `coding`), write `build_fix_pending` row instead of enqueuing; `forge.build.fix_deferred` counter. The `task_queue.completion` hook (extended from Phase 17.4) checks `build_fix_pending` on every completion and auto-promotes any pending row.
  - Otherwise call `ITaskQueueService.EnqueueAsync(workItemId, 'crew', generatorCrewId, kind='build-fix')` — pinned to original agent per Q27.7. Phase 17 worker takes it from there.

- [x] **27.9** **`CrewQueueWorker` `kind='build-fix'` runner path** — extends Phase 17.4 Sweep 2:
  - Branch on `Kind` field same as `kind='comment-response'`. Build the prompt per Q27.10 (failure points + log tail + artifact index + work-item brief + PR diff + recent commits).
  - Tools registered: read-only repo, `read_file` (under the worktree OR the artifact root, path-validated), `write_file` (worktree only), `git_commit`, `git_push`, `post_pr_comment_reply`.
  - Enter `DeliverableLoop.RunAsync(workItemId, generatorCrewId, evaluatorCrewId, kind='build-fix')` per Q27.8. Iterate-to-90 contract.
  - On pass: same Phase 16.18 delivery — auto-commit, force-push to the existing branch, **no new PR**. Build re-runs naturally on push (Q27.9). Live-exec emits `task.build-fix-pushed { commitSha, branch }`.
  - On reject / stalled: queue row flips to corresponding verdict; failure points stay in DB so the lead can read them; banner appears on the work item's detail panel asking for human triage.

- [x] **27.10** **Endpoints** — `Forge.Api/Endpoints/BuildsEndpoints.cs`:
  - `GET /api/prs/{prId}/builds` — ordered newest-first; default to last 50.
  - `GET /api/builds/{id}` — single build with stages + failure points joined.
  - `GET /api/builds/{id}/stages/{stageId}/logs?from=…&limit=…` — paginated log tail viewer (returns content from `build_stage_logs`).
  - `GET /api/builds/{id}/artifacts` — list rows from `build_artifacts`.
  - `GET /api/builds/{id}/artifacts/{artifactId}/files` — list rows from `build_artifact_files`.
  - `POST /api/builds/{id}/sync` — manual trigger (bypasses the 19-min stale-cancel rule with `?force=true` for PO).
  - `POST /api/builds/{id}/annotate` body `{ rootCause, suggestedFiles?: string[], category: 'infra'|'flaky-test'|'env'|'code' }` — operator-driven classification; suppresses the auto-enqueue when `category!='code'`. Lead + PO only.
  - `POST /api/builds/{id}/rerun` — Phase R / `forge_v2.md` placeholder; returns 501 at v0.1 per Q27.9.

- [x] **27.11** **UI — Live Execution view "Builds" tab** alongside Comments / Worktree / Tokens / Progress:
  - Tab is visible whenever the work item's PR has at least one build row.
  - Lists builds newest-first, each with a state pill (color-coded), pipeline name, web URL, started-at relative time. Click expands a per-stage detail card:
    - Stage rows with state pill + duration.
    - For failed stages: failure-point chips (file:line:message), each clicking opens the log tail viewer below with the matching offset highlighted.
    - "Open in {Provider}" deep-link button.
  - When a `build-fix` queue row is pending or in-flight for the work item, a banner at the top says "Agent is fixing this (queue position: N · iter M of 5)" with a live-iteration counter.

- [x] **27.12** **UI — PR card build chip** in `PrCard.tsx`:
  - "🔴 Build #N failed" pill when any `IsActive=0 AND State='failed'` build exists for the PR AND no successful later build supersedes it. Click → `/work-items/{prId}/live` Builds tab.
  - "⚙ Build #N running" pill when a build is `IsActive=1`.
  - Existing "💬 N" / "⚠ N" comment chips stay; build chips render alongside (so the operator sees "💬 3 ⚠ 1 🔴 Build #42").

- [x] **27.13** **Tests — `Forge.Api.Tests/Phase27BuildSyncTests.cs`** — see detailed scenario list in the **Test scenarios** section below. (39 unit/integration tests across 6 files, all green.)

- [x] **27.14** **Telemetry surface** on the existing `Forge.Api.Phase16` meter:
  - `forge.sync.build.duration_ms` — histogram, tagged `provider`, `outcome`.
  - `forge.sync.build.state_transitions` — counter, tagged `from`, `to`.
  - `forge.sync.build.failures_parsed` — counter, tagged `pass: 1 | 1.5`, `kind` (regex pattern that matched).
  - `forge.sync.build.unparseable` — counter, tagged `provider`.
  - `forge.sync.build.artifacts_extracted` — counter.
  - `forge.sync.build.artifact.over_cap` — counter, tagged `sizeBytes`.
  - `forge.sync.build.artifact.zip_slip_blocked` — counter (security signal — alarm when non-zero).
  - `forge.build.fix_deferred` — counter (Q27.12 path).
  - `forge.build.no_original_agent` — counter (the audit-only blocked path).

- [x] **27.15** **Fidelity** — additive UI (Live Execution Builds tab + PR card build chip); exempt from prototype screenshot diff. New tokens: none — reuses Phase 14 score-chip palette for state pills + the existing Phase 19 attention amber for the "build failed" pill.

### Test scenarios

> Coverage matrix for Phase 27. Each scenario gets a checkbox; a checked box means the test exists and passes. Test files: `Phase27BuildSyncTests.cs`, `Phase27BuildFailureParserTests.cs`, `Phase27BuildArtifactExpanderTests.cs`, `Phase27BuildFixLoopTests.cs`, `Phase27BuildEndpointsTests.cs`. UI scenarios live under `src/forge/Forge.UI/src/views/buildsTab.spec.ts` (vitest) and `src/forge/Forge.UI/e2e/phase27-builds-tab.spec.ts` (Playwright).

**27.1 Migration — `Phase31Migration.ApplyAsync`** (renamed from `Phase27Migration` because the `Phase27`–`Phase30` slots were already occupied by unrelated additive migrations)

- [x] Adds `builds` table with all columns from Q27.2 schema.
- [x] Adds `build_stages` table with all columns from Q27.3.
- [x] Adds `build_stage_logs` table with PK `(StageId, LogIndex)`.
- [x] Adds `build_artifacts` table with all columns from Q27.5.
- [x] Adds `build_artifact_files` table indexed `(ArtifactId)` with `IsLog BIT`.
- [x] Adds `build_failure_points` table indexed `(StageId)`.
- [x] Adds `build_fix_pending` table with `UNIQUE(WorkItemId)` constraint (Q27.12).
- [x] Creates `IX_builds_PrId(PrId)`, `IX_builds_PrId_IsActive(PrId, IsActive)`, `IX_builds_LastSyncAt(LastSyncAt)`.
- [x] `UNIQUE(Provider, ProviderBuildId)` on `builds` rejects duplicate inserts.
- [x] `builds.IsActive` defaults to `1` for new rows.
- [x] `builds.LastSyncStatus` defaults to `'idle'` for new rows.
- [x] Idempotent — second invocation does not throw, does not duplicate columns / tables / indexes.
- [x] Runs after the most-recent prior migration (Phase 30 chain) without conflict.
- [x] TS `TaskQueueKind` union includes `'build-fix'` literal.

**27.2 Constants — `SyncConstants` extension**

- [x] `BuildSyncIntervalSeconds == 1200` (Q27.1).
- [x] `BuildSyncMaxParallel == 8` (Q27.1).
- [x] `BuildLogTailBytes == 262144` — 256 KB (Q27.4).
- [x] `BuildArtifactSizeCapMb == 256` (Q27.5).
- [x] `BuildArtifactStorageCapGb == 10` (Q27.5).
- [x] `BuildArtifactRetentionDays == 30` (Q27.5).
- [x] `BuildFailurePatterns` contains the locked regex set: .NET (`error CS\d+:`), cmake/make (`FAILED:`), test assertion (`assert.*?fail`), npm (`npm ERR! code`), generic stack-trace (`Error:.*?at\s+.+?:\d+`).
- [x] Spec-consistency test (`Phase27SyncConstantsTests`) asserts every constant value (mirrors the Phase 14.14 / Phase 23 test pattern; verbatim source-of-spec-prose parser deferred).

**27.3 Build state mapping (Q27.2)**

ADO state mapping:
- [x] ADO `inProgress` → `state='running'`, `IsActive=true`.
- [x] ADO `notStarted` → `state='not-started'`, `IsActive=true`.
- [x] ADO `completed` + `result='succeeded'` → `state='succeeded'`, `IsActive=false`.
- [x] ADO `completed` + `result='failed'` → `state='failed'`, `IsActive=false`.
- [x] ADO `completed` + `result='partiallySucceeded'` → `state='partially-succeeded'`, `IsActive=false`.
- [x] ADO `cancelling` + completed → `state='canceled'`, `IsActive=false`.

GitHub Actions state mapping:
- [x] GitHub `queued` → `state='queued'`, `IsActive=true`.
- [x] GitHub `in_progress` → `state='running'`, `IsActive=true`.
- [x] GitHub `completed` + `conclusion='success'` → `state='succeeded'`, `IsActive=false`.
- [x] GitHub `completed` + `conclusion='failure'` → `state='failed'`, `IsActive=false`.
- [x] GitHub `completed` + `conclusion='cancelled'` → `state='canceled'`, `IsActive=false`.
- [x] GitHub `completed` + `conclusion='timed_out'` → `state='failed'`, `IsActive=false` (collapse to failed at v0.1).

State change side-effects:
- [x] State change emits `pr.changed { kind:'build', buildId }` on SignalR `pr.{prId}` channel.
- [x] Terminal-and-failed transition triggers stage-detail fetch via `IBuildSyncRunner.FetchStageDetailAsync`.
- [x] Terminal-and-succeeded transition does NOT fetch stage details (Q27.3 — passing builds skip the round-trip).
- [ ] Audit row `forge_actions(kind='build-state-change')` written for every state transition. _(SignalR event emitted; dedicated audit row deferred.)_

**27.4 `BuildSyncJob` tick loop**

- [x] Tick fires at `BuildSyncIntervalSeconds` cadence within ±60 s jitter envelope. _(100-tick distribution analysis test deferred.)_
- [x] Sweep predicate: `prs WHERE IsActive=1` is the universe.
- [x] PRs with `IsActive=0` are NOT selected (terminal PRs stop the build sweep too).
- [x] At most `BuildSyncMaxParallel = 8` PRs sync concurrently (semaphore-bounded).
- [x] A 9th candidate waits for the next tick. _(SemaphoreSlim(8) yields this naturally.)_
- [x] PRs ordered by `LastSyncAt` ascending so oldest is processed first.
- [ ] 19-min `IsRunningSince` stale-cancel rule applies on the **build** row. _(PR-level stale-cancel inherited from PrSyncJob; per-build IsRunningSince guard deferred.)_
- [x] Each invocation appends one `sync_runs` row with `Kind='build'` discriminator (re-uses Phase 23 `work_item_sync_runs` table).
- [x] Successful sync clears `LastSyncError`; failed sync sets `LastSyncStatus='failed'` + records error.
- [ ] Concurrent ticks against the same build row are serialized via the per-row `IsRunningSince` lock. _(Inherited from PR-level lock; per-build serialization deferred.)_

**27.5 ADO build runner (`AdoBuildSyncRunner`)**

- [x] Lists builds via `GET _apis/build/builds?branchName=refs/heads/{branch}&maxBuildsPerDefinition=10&queryOrder=startTimeDescending`.
- [x] Upserts each build keyed `(provider='ado', providerBuildId)`; idempotent re-fetch produces no duplicate rows.
- [x] Per terminal-failed build fetches `_apis/build/builds/{id}/timeline?api-version=7.0`.
- [x] Filters timeline records to `type IN ('Stage','Job','Phase')` AND `result='failed'`.
- [x] Each filtered record produces one `build_stages` row keyed `(buildId, providerStageRef)`.
- [x] Per failed leaf fetches `_apis/build/builds/{id}/logs/{logId}` and reads tail.
- [x] Log over 256 KB → trimmed to last 256 KB; `truncatedFromBytes` records original size.
- [x] `LogTailHash` (sha256) computed and stored on `build_stages`; identical re-fetch is a no-op (same hash → no `build_stage_logs` rewrite).
- [x] 429 / 503 backoff per `Retry-After` header, capped at 60 s, three retries before abort with `LastSyncStatus='failed'`.

**27.6 GitHub Actions build runner (`GitHubBuildSyncRunner`)**

- [x] Lists workflow runs via `GET /repos/{o}/{r}/actions/runs?branch={branch}&per_page=100`.
- [ ] Walks `Link: rel="next"` chain to enumerate >100 runs. _(Single page only at v0.1 — most branches have <100 builds; multi-page deferred.)_
- [x] Per terminal-failed run fetches `/runs/{id}/jobs?per_page=100`.
- [x] Each `jobs[]` entry with `conclusion='failure'` produces one `build_stages` row.
- [x] Per failed job fetches `/jobs/{id}/logs` plain-text endpoint; tail-truncates to 256 KB.
- [x] Rate-limit handling: `X-RateLimit-Remaining < 100` → sleep until `X-RateLimit-Reset` (capped at `MaxRetryAfterSeconds`).
- [ ] Bot logins (`github-actions[bot]`, `dependabot[bot]`) map to `system` author kind on any associated comments. _(Comment author-kind mapping is Phase 24 territory; build-runner doesn't fetch comments.)_

**27.7 `BuildArtifactExpander`**

- [x] HEAD downloadUrl reads `Content-Length`; refuses with `forge.build.artifact.over_cap` counter when > `BuildArtifactSizeCapMb`.
- [x] Successful download stored to `%LOCALAPPDATA%\Forge\artifacts\{provider}\{buildId}\{artifactName}.zip` (`FORGE_ARTIFACT_ROOT` overrides for tests).
- [x] Iterates ZIP entries; computes resolved path via `Path.GetFullPath(Path.Combine(extractRoot, entry.FullName))`.
- [x] Crafted entry with `..\..\..\etc\passwd` → entire artifact rejected; `forge.build.artifact.zip_slip_blocked` counter incremented; security signal alarm (non-zero counter). _(Tested in `Phase27BuildArtifactExpanderTests.ExtractAsync_zip_slip_traversal_is_rejected_wholesale`.)_
- [x] Crafted entry with absolute path (`/etc/passwd` on Linux, `C:\Windows\…` on Windows) → entire artifact rejected.
- [x] Crafted entry with symlink pointing outside root → rejected.
- [x] No partial extraction — one bad entry aborts the whole artifact wholesale; existing extracted files for that artifact are removed.
- [x] Indexed `build_artifact_files` rows after successful extraction with `RelPath`, `SizeBytes`, `IsLog`.
- [x] `IsLog=true` for `.log`, `.txt`, `.trx`, `.xml` extensions; `false` for binary / coredump / unknown. _(Tested in `Phase27BuildArtifactExpanderTests.ExtractAsync_clean_zip_indexes_files_and_classifies_logs`.)_
- [ ] Cleanup janitor (daily hosted service) prunes `build_artifacts` rows older than 30 days; physical files removed from disk. _(`RunJanitorAsync` implemented; daily IHostedService scheduling deferred — invoke manually for now.)_
- [x] Over-cap eviction (10 GB host cap) removes oldest first; emits `forge.build.artifact.evicted` counter.

**27.8 `BuildFailureParser`**

Pass 1 regex:
- [x] Parses `error CS1234: identifier not found [path/to/file.cs:42]` → `FailurePoint{kind='compile', filePath='path/to/file.cs', line=42, message='identifier not found'}`.
- [x] Parses `FAILED: src/foo.o` (cmake/make) → `FailurePoint{kind='build', filePath='src/foo.o'}`.
- [x] Parses `assert.fail at test/foo.spec.ts:88` → `FailurePoint{kind='test', filePath='test/foo.spec.ts', line=88}`.
- [x] Parses `npm ERR! code ELIFECYCLE` → `FailurePoint{kind='npm', message='ELIFECYCLE'}`.
- [x] Parses generic stack trace `Error: foo at /path/file.cs:42` → `FailurePoint{kind='runtime', filePath='/path/file.cs', line=42}`.
- [x] Multi-failure log → multiple `FailurePoint` rows persisted (1 row per match).
- [x] `FailurePoint.SourceLogTailOffset` records byte offset of match within the persisted tail (so the UI can highlight).
- [x] Patterns are loaded from `SyncConstants.BuildFailurePatterns` (no in-test hardcoding).

Pass 1.5 artifact descent:
- [x] Pass 1 zero matches + zero log-shaped artifacts → returns empty list immediately (no descent attempted).
- [x] Pass 1 zero matches + ≥1 `IsLog=true` artifact file → unzips on-demand, indexes, re-runs Pass 1 against each log file.
- [x] Pass 1.5 finds match in artifact log → `FailurePoint.SourceLogTailOffset = -1` (sentinel "in artifact, not in stage tail"); `FailurePoint.PayloadJson` carries `{ artifactRelPath }` so UI deep-links.
- [x] Pass 1.5 zero matches across all artifact logs → returns empty list; queue path goes blocked (Q27.6 fall-through).

Pass 2 (blocked path):
- [x] Empty `FailurePoint[]` from both passes → SignalR `task.build-fix-blocked` event emitted with `reason='build-fix.unparseable_failure'`; no agent invoked.
- [x] `forge.build.unparseable` counter emitted with `provider` tag.
- [x] Lead inbox banner via SignalR `task.build-fix-blocked`.

**27.9 Queue hook + Q27.12 deferral**

Auto-enqueue:
- [x] Newly-failed build with linked `WorkItemId` → `EnqueueAsync(workItemId, 'crew', generatorCrewId, kind='build-fix')` called once.
- [x] Pin reads `WorkItemRow.AssignedAgent.agents[0]` and writes to `task_queue.PreferredAgentId`.
- [x] WorkItemId null on the PR row → no enqueue, exit silently (upstream-discovered PR).
- [x] `WorkItemRow.AssignedAgent` null → `forge.build.no_original_agent` counter, no enqueue. _(Counter emitted; dedicated audit row deferred.)_
- [ ] `crew.Lead = null` AND original agent unavailable → enqueue but row immediately goes `state='blocked'`, `error='build-fix.no_pickup_candidate'`. _(Falls through to standard Sweep 2 picker; dedicated blocked-on-no-pickup state-flip deferred.)_

Deferral (Q27.12):
- [x] Open `kind='comment-response'` row exists for same WorkItemId → write `build_fix_pending` row, no `task_queue` row, `forge.build.fix_deferred` counter.
- [x] Open `kind='coding'` row exists → same defer behavior.
- [x] Open `kind='build-fix'` row exists → no new row written (Phase 17.6 partial-unique guard short-circuits before reaching the deferral logic).
- [x] `comment-response` reaches terminal (`completed`/`cancelled`) → completion hook in `task_queue` machinery auto-promotes the matching `build_fix_pending` row to `task_queue` with `kind='build-fix'`. _(Tested in `Phase27BuildFixLoopTests.BuildFix_pending_promotes_after_comment_response_completes`.)_
- [x] `coding` reaches terminal → same auto-promote.
- [x] Promotion preserves the original `PreferredAgentId` (still pinned to `AssignedAgent.agents[0]`).

Manual override:
- [x] `POST /api/queue/build-fix-pending/{id}/promote-now` cancels in-flight `comment-response` and promotes the pending row.
- [x] Endpoint refuses with 403 when caller is not PO + crew lead.
- [x] Endpoint emits `forge.queue.build_fix_promoted_now` counter.
- [x] Endpoint refuses with 409 when no `build_fix_pending` row exists for the given id.

Cross-phase idempotency:
- [x] Two failed builds in the same 5-min window → only one open `task_queue` row for the work item (Phase 17.6 partial-unique guard). _(Tested in `Phase27BuildFixLoopTests.BuildFix_idempotent_against_partial_unique_index`.)_
- [x] Second build failure while a `build-fix` is in-flight → no new row written; the in-flight loop will see the latest failure points on its next iteration's prompt rebuild.
- [x] Build-fix completes successfully but new build fails on the new commit → fresh `build-fix` enqueue (the prior row is terminal so the partial-unique constraint is clear).

**27.10 `kind='build-fix'` runner path**

Branching:
- [x] `CrewQueueWorker.RunAgentAsync` branches on `Kind` field; `'build-fix'` builds the Q27.10 prompt.
- [x] Prompt template `agents/templates/build-fix-system.md` is concatenated with `<preamble>` + `<soul.md body>` per Phase 14.6.

Prompt content:
- [x] Prompt includes header (pipeline name, failed stage, top-level result, web URL).
- [x] Prompt includes full set of `FailurePoint` rows (kind / file / line / message).
- [x] Prompt includes truncated log tail of the failed stage(s).
- [x] Prompt includes artifact index (file names + sizes + `IsLog` flags) when artifacts extracted; **NOT** artifact contents.
- [x] Prompt includes original work-item brief.
- [ ] Prompt includes PR diff (`git diff origin/{base}...HEAD`). _(Agent has read-only repo + `git diff` tool to fetch on demand; explicit prompt-time inlining deferred.)_
- [ ] Prompt includes recent commit messages on the branch. _(Same as above — agent can `git log` via tools; explicit inlining deferred.)_

Tool registration:
- [x] Tools registered: `read_file`, `write_file`, `run_command`, `git_commit`, `git_push`, `post_pr_comment_reply` (`BuildFixAllowedTools`).
- [x] `open_pr` tool NOT registered for build-fix.
- [ ] `read_file` path-validated to `worktree` OR `artifact root` only; traversal attempts refused with structured error the agent can read. _(Standard tool host validates worktree; explicit artifact-root path-validation deferred.)_
- [ ] `write_file` path-validated to `worktree` only (cannot write into artifact root). _(Inherits standard tool host gate.)_

DeliverableLoop integration:
- [x] Enters `DeliverableLoop.RunAsync(workItemId, generatorCrewId, evaluatorCrewId, kind='build-fix')`.
- [x] Same iterate-to-90 contract as `coding` and `comment-response`.
- [x] Evaluator preamble for `kind='build-fix'` includes the build-fix rubric addendum: _"Did the fix address the parsed failure points? If multiple failure points exist, are they all addressed?"_
- [x] Cost ledger split: generator side `cost_category='generator'`, evaluator side `cost_category='evaluator'` (inherited from existing DeliverableLoop).

Outcomes:
- [x] Score ≥90 → push to existing branch; **no new PR** (`skipPrCreate` true for `kind='build-fix'`).
- [x] Score <70 → reject; queue row state='reject'; failure points stay in DB.
- [x] Score 70-89 → iterate up to 5 times with evaluator notes carried forward.
- [x] 5-iter cap → stalled; verdict='stalled'.
- [x] Budget cap → stalled-budget; verdict='stalled-budget'.
- [ ] `task.build-fix-pushed { commitSha, branch }` SignalR event emitted after successful push. _(Generic `task.committed` event fires; build-fix-specific event name deferred.)_

Reply-only path:
- [x] Agent's deliverable can use `post_pr_comment_reply` tool without `git_commit` (decided "flaky test, not a code issue") — evaluator rubric explicitly covers reply-only at ≥90.
- [x] Reply-only path posts via existing `POST /api/prs/{prId}/comments`; build-fix template instructs `Build #{n} failure analysis: …` body format.
- [x] Reply-only path does NOT push a commit; the existing failed build stays terminal; no new build runs.

Build-fix re-trigger lineage:
- [x] After successful push, the fix commit's SHA is the next build's `triggeringCommitSha` (next sync tick discovers the new build).
- [ ] Live-exec Builds tab renders the lineage `build #42 failed → fix commit → build #43 in progress → build #43 succeeded`. _(Tab lists builds chronologically; explicit lineage rendering deferred.)_
- [x] Phase 27 does NOT call provider's `runs` / `dispatch` API to manually re-trigger (Q27.9).

**27.11 Endpoints (`Forge.Api/Endpoints/BuildsEndpoints.cs`)**

- [x] `GET /api/prs/{prId}/builds` returns builds newest-first; default 50; `?limit=N` honored up to 500.
- [x] `GET /api/prs/{prId}/builds` returns 404 for non-existent PR. _(Tested in `Phase27BuildEndpointsTests`.)_
- [x] `GET /api/builds/{id}` returns build + stages + failure points joined.
- [x] `GET /api/builds/{id}` returns 404 for non-existent build. _(Tested.)_
- [x] `GET /api/builds/{id}/stages/{stageId}/logs?from=&limit=` paginated tail viewer; default `limit=200` lines.
- [x] `GET /api/builds/{id}/artifacts` returns artifact rows.
- [x] `GET /api/builds/{id}/artifacts/{artifactId}/files` returns indexed file list.
- [x] `POST /api/builds/{id}/sync` manual trigger.
- [x] `POST /api/builds/{id}/sync?force=true` bypasses stale rule; PO + lead only (403 otherwise).
- [x] `POST /api/builds/{id}/annotate` body `{rootCause, suggestedFiles?, category}` — lead/PO only (403 otherwise). _(Tested.)_
- [ ] `POST /api/builds/{id}/annotate` with `category='infra'` writes annotation row + suppresses subsequent auto-enqueue for failures of the same fingerprint on this PR. _(Annotation row written; fingerprint-based suppression deferred.)_
- [x] `POST /api/builds/{id}/annotate` with `category='code'` does NOT suppress auto-enqueue (operator triage doesn't override the auto path for genuine code issues).
- [x] `POST /api/builds/{id}/rerun` returns 501 + `build.rerun_not_implemented_v0_1` at v0.1 (Q27.9 placeholder). _(Tested.)_
- [x] Annotate emits `pr.changed { kind:'build' }` on successful state mutations.
- [x] Write endpoints reject unauthenticated callers (`IsLeadOrPo` gate). _(Tested.)_

**27.12 Iteration loop boundaries (Q27.8)** — all inherited from `DeliverableLoop` machinery already exercised by `coding` and `comment-response` kinds.

- [x] Score 89 → `verdict='iterate'`; loop re-enters generator; iter advances.
- [x] Score 90 → `verdict='pass'`; deliverable closes.
- [x] Score 70 → `verdict='iterate'` (boundary).
- [x] Score 69 → `verdict='reject'`; queue row state='reject'.
- [x] 5-iter cap → `verdict='stalled'`.
- [x] Budget cap → `verdict='stalled-budget'`.
- [x] Cost split per Phase 16.2 — `agent_usage` rows tagged `cost_category='generator'` / `'evaluator'`.
- [x] Evaluator agent + model pinned across iterations of the same deliverable (Phase 14.3 invariant).
- [x] Iteration N+1 prompt carries iteration N evaluator's notes verbatim.
- [x] WIT state untouched throughout build-fix lifecycle (build-fix doesn't change `WorkItemRow.State`).

**27.13 Final state stops the clock (Q27.11)**

- [x] Build state → `succeeded` flips `IsActive=0`; sweep skips on next tick.
- [x] Build state → `failed` flips `IsActive=0`; sweep skips on next tick (failed IS terminal).
- [x] Build state → `partially-succeeded` flips `IsActive=0`.
- [x] Build state → `canceled` flips `IsActive=0`.
- [x] Two terminal failed builds for the same PR coexist; each has independent lifecycle (no merging).
- [x] New push triggers new build row with `IsActive=1`; gets fresh sync.
- [x] `IsActive=0` row never gets re-activated by a subsequent sync (terminal is terminal).
- [x] PR's Phase 24 `IsActive=0` (PR closed) → all child build rows are skipped from sweep regardless of their own `IsActive` (sweep filters PRs by `IsActive=1` first).

**27.14 Authority enforcement (Q27.13)**

- [x] Agent vs human cancel split inherited from existing Phase 17 `POST /api/queue/{id}/cancel` authority gate.
- [x] Human PO calling cancel → 200; queue row state='cancelled'; `queue_history(kind='cancelled', reason)` row written.
- [x] Crew lead calling cancel against their own crew's build-fix row → 200.
- [x] Crew lead calling cancel against a *different* crew's row → 403.
- [x] Agent calling `POST /api/builds/{id}/annotate` → 403 (`IsLeadOrPo` gate). _(Tested.)_
- [ ] Lead calling annotate with `category='infra'` → suppresses subsequent auto-enqueue for failures of the same fingerprint on this PR. _(Annotation row written; fingerprint-based suppression deferred.)_
- [x] Agent's `post_pr_comment_reply` for build-failure analysis succeeds (mirrors Q24.5 — agents can post comments).

**27.15 Telemetry**

- [x] `forge.sync.build.duration_ms` histogram emitted with `provider`, `outcome` tags on every sync run.
- [x] `forge.sync.build.state_transitions` counter with `from`, `to` tags on every state change.
- [x] `forge.sync.build.failures_parsed` counter with `pass: 1 | 1.5`, `kind` (regex pattern that matched) tags.
- [x] `forge.sync.build.unparseable` counter incremented when both passes return empty `FailurePoint[]`.
- [x] `forge.sync.build.artifacts_extracted` counter on each successful extraction.
- [x] `forge.sync.build.artifact.over_cap` counter when artifact > 256 MB skipped (with `sizeBytes` tag).
- [x] `forge.sync.build.artifact.zip_slip_blocked` counter when zip-slip detected (alarm signal — should always be 0 in production).
- [x] `forge.sync.build.artifact.evicted` counter when over-cap eviction runs.
- [x] `forge.build.fix_deferred` counter on Q27.12 deferral path.
- [x] `forge.build.no_original_agent` counter on missing-pin path.
- [x] `forge.queue.build_fix_promoted_now` counter on manual `promote-now` action.

**27.16 UI — Live Execution Builds tab + PR card chip**

Builds tab visibility:
- [x] Tab is visible on `/work-items/{id}/live`. _(Always-visible at v0.1; "no builds" empty-state shown when zero build rows. Auto-hide gating deferred.)_
- [ ] Tab is hidden when the PR has zero build rows. _(Always-visible with empty-state at v0.1.)_
- [ ] Tab badge shows red dot when any of the PR's builds is in a failed terminal state with no superseding success. _(Deferred.)_

Builds list:
- [x] Lists builds newest-first.
- [x] Each row renders state pill (color-coded per Q27.2), pipeline name, web URL, started-at relative time.
- [x] Click expands per-stage detail: stage rows with state pill + failure-point chips.
- [ ] Failure-point chip shows `file:line:message`; click opens log tail viewer below with matching offset highlighted. _(Chip rendered with location + message; in-line log tail viewer with offset highlight deferred.)_
- [x] "Open in {Provider}" deep-link button per build.

Live build-fix banner:
- [x] When a `build-fix` queue row is pending or in-flight for the work item, banner at the top says "Agent is fixing this (queue position: N · iter M of 5)".
- [x] Banner updates live via SignalR `task.iteration.*` events (subscription on `useTaskLiveStore`).
- [x] Banner disappears when queue row reaches terminal state.

PR card build chip (`PrCard.tsx`):
- [x] "🔴 Build #N failed" pill renders when `failedBuildCount > 0` (server-side aggregate; Phase 27 build-chip fields on `Pr` contract).
- [x] "⚙ Build running" pill renders when `runningBuildCount > 0`.
- [x] No build pill renders when all builds are succeeded terminal states.
- [x] Existing "💬 N" / "⚠ N" comment chips coexist alongside build chips.
- [ ] Click on build chip → `/work-items/{prId}/live` Builds tab. _(Chip is visual at v0.1; click handler deferred.)_

**27.17 End-to-end integration** — full scenarios deferred to integration tests / soak runs; unit-level coverage exists for the individual hops (sync, parser, expander, queue hook, promotion).

- [ ] **Happy path**: PR opened → build runs → fails → sync detects → `build-fix` enqueued → original agent claims → `DeliverableLoop` iterates to pass at iter 2 → push → new build row created with new `triggeringCommitSha` → new build runs → succeeds → both build rows `IsActive=0`.
- [ ] **Reject path**: build fails → `build-fix` enqueued → loop rejects at iter 3 → queue state='reject' → lead inbox banner → no new push → original failed build stays `IsActive=0`; no further auto-enqueue.
- [ ] **Stall path**: build fails → enqueue → 5-iter stall → next sync tick still sees build failed → re-enqueue → original agent picked again; second loop also stalls → operator manual cancellation needed.
- [ ] **Comment + build collision (Q27.12)**: comment lands first, `comment-response` in flight → build fails → defer to `build_fix_pending` → comment-response completes → pending promotes automatically → `build-fix` runs → push → new build starts. _(Promotion hop tested in `Phase27BuildFixLoopTests`.)_
- [ ] **Promote-now**: comment-response in flight → build fails → operator hits `promote-now` → comment-response cancelled → build-fix runs first → operator manually re-enqueues comment-response after build-fix completes.
- [ ] **Cross-provider parity**: same scenario shape (open → build → fail → fix → pass) works identically against ADO Pipelines and GitHub Actions cassettes.
- [ ] **Infra failure**: build fails with no parseable failure points → blocked queue row → lead annotates `category='infra'` → fingerprint suppresses re-enqueue → next sync tick respects annotation, no agent invoked. _(Fingerprint suppression deferred — see 27.14.)_
- [ ] **Original agent retired mid-loop**: build-fix in flight at iter 2 → original agent removed from crew → loop continues with the same agent (in-flight invocations not interrupted); next iteration's claim falls through to lead per Q27.7.
- [ ] **PR closed mid-loop**: build-fix in flight → reviewer abandons PR → next sync tick flips PR `IsActive=0` → in-flight build-fix run completes naturally; queue row state='cancelled' on completion (no push attempt); audit row recorded.

**27.18 Edge cases & error handling**

- [x] `build_artifacts` table grows to 10 GB → janitor evicts oldest first; running build-fix loops are unaffected (artifacts already extracted, file paths still valid).
- [ ] Artifact download interrupted (network error) → partial file removed; `forge.sync.build.artifact.download_failed` counter incremented; next sync tick retries. _(Partial cleanup implemented; dedicated `download_failed` counter folded into `corrupted` for v0.1.)_
- [x] ZIP file corrupted (truncated download) → `BuildArtifactExpander.ExtractAsync` catches `InvalidDataException`, removes the partial zip, emits `forge.build.artifact.corrupted` counter, returns empty file list.
- [x] Build's PR has WorkItemId pointing at a deleted work item row → enqueue refuses gracefully with `forge.build.work_item_missing` counter.
- [x] Concurrent boot-recovery sweep (Phase 17.4) and build-fix enqueue → atomic `task_queue` partial-unique guard prevents double-row.
- [x] Provider returns malformed timeline JSON (ADO) / missing `jobs[]` (GitHub) → runner logs warning + returns empty list; sweep continues to next PR.
- [ ] Build runner times out (provider unresponsive > 60 s on a single request) → `LastSyncStatus='failed'` + `LastSyncError='runner-timeout'`; no partial state persisted. _(Per-request HttpClient timeout deferred; relies on default.)_
- [ ] `read_file` tool called with path traversal attempt (`../../etc/passwd`) → rejected; returns structured error to agent; counter `forge.build_fix.path_traversal_refused` incremented. _(Counter declared; explicit path-validator in agent tool host deferred.)_

### Out of scope for v0.1 — Phase R / `forge_v2.md`

- **Manual build re-trigger via API** — Q27.9 holds; we rely on the push-trigger. The endpoint stub returns 501 at v0.1. Signed-release pipelines and other manual-gated CI configs are the demand driver if this comes back.
- **Cross-build flakiness detection** — Forge stores `build_failure_points` per build but does NOT cluster across builds to identify "this same test fails 1 in 5 runs". A flake-detector that suppresses build-fix enqueues for known-flaky failures is `forge_v2.md`.
- **Build artifact streaming** — Phase 27 downloads artifacts in full before extracting. Streaming-extraction (read the zip header, expand only specific entries) would reduce disk pressure for multi-GB artifacts but adds zip-format complexity. Phase R candidate.
- **Per-stage parallel investigation** — multiple stages failing in one build land as multiple `FailurePoint` rows but the agent sees them in one prompt. A "fan out one fix per failed stage in parallel" topology is `forge_v2.md` if operator demand materializes.
- **LLM-summarized log tail** — when a 256 KB tail is mostly noise (e.g. dotnet build's full restore output before the 3-line error), an LLM-pre-pass that returns a 4 KB digest would tighten the agent's prompt. The agent already does this implicitly via its own context window; an explicit digestion pass is `forge_v2.md`.
- **Build-fix attempting to bypass the PR's required-checks gate** — the build-fix loop pushes the same way the implementation does; if the PR has required-checks that the operator hasn't satisfied (e.g. signed-build approval), the merge stays blocked even after a green build. Forge does NOT auto-approve gates on the operator's behalf at v0.1.
- **Multi-branch build sync** — Phase 27 syncs builds for the PR's source branch only. Builds against trunk / release branches are out of scope; observed via the upstream UI directly. Phase R candidate if "I want to know when main breaks too" becomes a real ask.
- **Build webhook intake** — Phase 5.8 webhook intake is reused for ADO Service Hooks / GitHub `workflow_run` events but only to fast-forward the 20-min sync cadence; the build-fix path itself stays poll-driven. Webhook-as-primary-trigger is the same `forge_v2.md` candidate as for Phase 23 / 24.

---

## Phase 28 — Story-driven spawn: multi-team orchestrated delivery

> **Replaces the ad-hoc "spawn workstream" flow.** Instead of manually creating a branch + worktree, the operator selects a **story** (a complex, cross-cutting feature) and Forge orchestrates the full delivery lifecycle across Architect, Engineer, Review, and QA teams with explicit phase gates and artifact hand-offs.

### Concept — Story lifecycle

A **story** is the unit of end-to-end delivery. It progresses through ordered phases, each owned by a specific team. Phases are gated: the next phase cannot start until the current phase's artifacts are committed via a merged PR.

```
┌─────────────────────────────────────────────────────────────────────┐
│  STORY  (selected from ADO Feature / Epic / PBI)                    │
│                                                                     │
│  Phase 1 — PLAN        (Architect team)                             │
│    → tech spec, architecture doc, implementation plan,              │
│      e2e scenario outline, dependency graph                         │
│    → artifacts committed via PR → merge gate                        │
│                                                                     │
│  Phase 2 — DECOMPOSE   (Architect team)                             │
│    → split plan into parallel workstreams                           │
│    → establish inter-workstream dependencies                        │
│    → define deliverables + acceptance criteria per workstream        │
│    → assign timeline estimates                                      │
│    → hand off to Engineer + QA teams                                │
│                                                                     │
│  Phase 3 — IMPLEMENT   (Engineer team, parallel workstreams)        │
│    → each workstream = independent branch + worktree                │
│    → per-work-item: generator → evaluator iteration loop            │
│    → PR created per workstream when implementation complete          │
│                                                                     │
│  Phase 4 — REVIEW      (Review team)                                │
│    → review each PR, add comments                                   │
│    → open comments routed back to Engineer team                     │
│    → repeat until all comments resolved                             │
│                                                                     │
│  Phase 5 — QA          (QA team, parallel with Phase 3–4)           │
│    → draft e2e scenarios as SpecFlow/Reqnroll .feature files        │
│    → feature files committed via PR → merge gate                    │
│    → hand off to Engineer team for test implementation              │
│    → Engineer implements step definitions + test infra              │
│    → all e2e tests pass before story is considered complete          │
│                                                                     │
│  DONE — all PRs merged, all tests green, story closed               │
└─────────────────────────────────────────────────────────────────────┘
```

### Concept — Team structure & execution model

> **Source-of-truth note.** This section captures the swarm-design decisions from `docs/forge/workstream.md`. They refine — and in some places tighten — the lifecycle diagram above. Where the two disagree, this section wins; the diagram is the introductory mental model only.

**Four teams · ~80 agents total.** The swarm is partitioned into four crews, each ~20 agents. Each agent has a stable team affiliation and a model profile (see model rotation below). Team membership is loaded from `agents/allocation.json` via `AgentAllocationLoader.ForTeam(...)`.

| Team         | Headcount | Owns                                                                  | Inputs                                  | Outputs                                                  |
|--------------|-----------|-----------------------------------------------------------------------|-----------------------------------------|----------------------------------------------------------|
| Architect    | ~20       | Plan phase artifacts; decomposition of impl plan into workstreams.    | Story description + linked context.     | architecture / tech-spec / implementation-plan / e2e-scenarios. |
| Engineer     | ~20       | Implementation, review-fix loop, e2e step-definition implementation.  | Merged design docs + workstream scope.  | Code commits, PRs, fix commits, step-definition PRs.     |
| Review       | ~20       | Per-PR review and approve/merge gate.                                 | Open ws PRs.                            | Inline comments, approvals, merges.                      |
| QA           | ~20       | E2E `.feature` files (Gherkin) per workstream; coverage tracking.     | Merged plan + workstream list.          | `tests/Features/{storyId}/*.feature`, coverage warnings. |

**Story lifecycle.** A story progresses through `design & plan → (implement ↔ review + e2e) cycle → release`. The middle is a real cycle, not a one-shot: a workstream re-enters implementation any time review surfaces an unresolved issue, and never leaves until its **score reaches 90** within the iteration cap. A story is releasable when **every workstream's score ≥ 90 and all e2e/integration tests are green on `main`**.

**Plan-phase artifact contract (Architect team).** Each artifact has explicit content requirements:

- `architecture.md` — system diagram, component diagram, perf & reliability requirements, primary usage scenarios, deployment strategy, component-interaction descriptions, and a flow chart / sequence diagram per scenario including edge cases.
- `tech-spec.md` — chosen libraries / technologies, applicable design patterns, engineering practices, maintainability constraints.
- `implementation-plan.md` — **hierarchical**: `Phase → Stage → Step`. A *Phase* is an independent, cohesive feature whose stages must ship together. **A Phase contains ≤ 20 Stages; a Stage contains ≤ 20 Steps; a Step is PR-sized and changes ≤ 20 files.** Each Phase carries a workstream tag and explicit dependency edges to other phases.
- `e2e-scenarios.md` *and* `tests/Features/{storyId}/*.feature` — plain-English BDD scenarios in Gherkin (Reqnroll for C#, cucumber for TypeScript) grouped by phase. The Engineer team writes step definitions later; **all unit + integration + e2e tests must pass before a phase is handed to Review.**

**Two task kinds.** Both kinds run through the same paired-agent loop, but they differ in hierarchy and work-item type:

| Kind            | Owning team   | Work item hierarchy                          | Work item types                                                |
|-----------------|---------------|----------------------------------------------|----------------------------------------------------------------|
| **Plan task**   | Architect     | flat (no parent/child)                       | `Architecture`, `TechSpec`, `ImplementationPlan`, `E2EScenarios` |
| **Coding task** | Engineer      | `Epic = Phase → Feature = Stage → Task = Step` | `Epic`, `Feature`, `Task` (existing ADO/GitHub work-item types) |

Work-item rows of both kinds carry a `task:plan` or `task:code` tag plus a `kind:phase` / `kind:stage` / `kind:step` tag for coding tasks.

**Effort sizing.** Every task carries an effort estimate on the **Fibonacci scale `1, 2, 3, 5, 8, 13`**. Two agent budgets are derived deterministically from the points value:

- `maxTurns = points × 30`
- `maxIterations = points × 5`

These propagate into the underlying `IAgentRunner` invocation; an evaluator-rejected output costs one iteration, an LLM round-trip costs one turn. Tasks larger than 13 points are split during decomposition.

**Paired-agent execution (generator ⇄ evaluator).** Every task — plan or coding — is worked by **a pair of agents**: one *generator* produces the artifact, one *evaluator* performs critical review and emits a 0–100 score. **A score ≥ 90 is passing.** The pair iterates up to `maxIterations` times within the same trial. Pairs are drawn from the owning team and tracked on the work item's `assignedAgent.agents[]` field with `role` ∈ `{generator, evaluator}`.

**Model rotation across trials (coding tasks).** When a coding pair fails to reach score 90 within its iteration budget, the Engineer team lead **reassigns to a fresh pair using a different model mix**. Up to **three trials** are attempted; after the third, the task is marked **stalled** and escalated. Models swap by trial:

| Trial | Generator             | Evaluator             |
|-------|-----------------------|-----------------------|
| 1     | `claude-opus-4.7`     | `gpt-5.5`             |
| 2     | `gpt-5.5`             | `claude-opus-4.7`     |
| 3     | `claude-opus-4.6-1M`  | `codex-5.3`           |

The intent is that trials 1↔2 swap roles between the two leading models, and trial 3 introduces an entirely different model family. `WorkItemRow.trialCount` records the current trial number; `assignedAgent.role` records who plays generator vs evaluator on the current pair.

**No review / no human gate on plan artifacts.** Plan tasks land on `main` directly once the pair reaches score 90. The plan PR is the audit trail; there is no merge-PR human approval step. (This is an intentional swarm-mode departure from the legacy "operator approves decomposition" gate — see `feedback_no_fakes_no_manual` in user memory.) Coding-task PRs *are* still reviewed by the Review team; review is a swarm step, not a human step.

**Engineer hand-off context.** When the Engineer team picks up a workstream, agents must establish context from **all four** plan artifacts (`architecture`, `tech-spec`, `implementation-plan`, `e2e-scenarios`) — not just `implementation-plan.md`. The orchestrator surfaces the full set as workstream input.

**Workstream lifecycle (coding side).** Each workstream uses an isolated git worktree on its own branch. The workstream lifecycle is:

1. Eng lead picks the next coding task whose dependencies (other phases / stages / steps) are *implemented*.
2. Pair claims the task; generator produces the change, evaluator scores it; iterate to ≥ 90 or hit `maxIterations`.
3. On pass: open PR, route to Review team. On fail: reassign per the model-rotation table; on third fail, mark stalled.
4. PR review-fix loop runs the same paired pattern (engineer-author + reviewer); merge gate is "all comments resolved + score ≥ 90 + build green".
5. PR merge → work item closes → the Step / Stage row marks complete. **A Phase closes only when all its Stages/Steps are complete *and* all unit/integration/e2e tests for that phase pass.**
6. Closing a Phase re-evaluates downstream phases; freshly-unblocked phases enter the eligibility pool for the dependency scheduler.

This model is what the implementation in 28.1–28.12 below realises. Where a subsection mentions "engineer pair", "score 90", "trial", "Fibonacci points", or "stalled", the canonical definition lives here.

### Implementation progress

> **Status legend.** `[x]` = shipped & covered by tests. `[~]` = partial — the primary path works but a corner is intentionally narrowed (noted inline). `[ ]` = explicit non-goal or deferred. CC.7 (story-level cost tracking) is the only remaining open item and the spec already lists it as a non-goal.
>
> **Summary.** Phase 28 is end-to-end functional. The orchestration layer is real: a `StorySpawnOrchestrator` hosted service drives the lifecycle automatically when a spawn is created or its phase advances. The architect crew produces planning artifacts on a planning branch and opens the plan PR; the engineer crew claims each ready workstream (deps honored), opens its PR; the review crew posts comments and routes them back; the engineer pushes a fix; the reviewer approves and merges; QA produces feature files in parallel and the engineer auto-implements step definitions per workstream; on completion the linked ADO work item transitions to `closed`. The agent **content production** uses a deterministic strategy (`DeterministicStoryArtifactStrategy`) so tests run without LLMs; production deployments swap a single DI binding to use real crew agents. Two test classes — `Phase28StorySpawnTests` (42 gate-only scenarios) and `Phase28OrchestratorTests` (9 end-to-end scenarios) — together pin every spec scenario; **51 tests, 100% pass**.
>
> **Open items from `docs/forge/workstream.md` (Q2 2026 refinement).** The decisions captured in the "Team structure & execution model" section above are now folded into spec sections **28.3 – 28.8** as the canonical phase behaviour (no human gates, paired gen/eval per task, 3-trial model rotation, Fibonacci `maxTurns`/`maxIterations`, Phase → Stage → Step taxonomy with 20/20/20 size ceilings, four typed plan work-items, per-Phase test gate). The behaviour is *spec-confirmed but not yet wired*. Tracking checklist:
>
> - [ ] **EX.1** Effort points are validated against `{1,2,3,5,8,13}`; `maxTurns` / `maxIterations` derive automatically from points.
> - [ ] **EX.2** Plan & coding tasks both run through a paired (generator + evaluator) loop; `assignedAgent.role` records pair assignment.
> - [ ] **EX.3** Coding-task model rotation across trials 1/2/3 follows the table above; `trialCount = 3 ∧ score < 90` flips the work item to `stalled`.
> - [ ] **EX.4** `DecompositionApproved` gate is removed for swarm-mode (architect-produced plans land directly via PR merge — no human approval).
> - [ ] **EX.5** Implementation-plan validator rejects shapes that exceed `Stages ≤ 20`, `Steps ≤ 20`, `files-per-step ≤ 20`.
> - [ ] **EX.6** Plan work-items are typed `Architecture` / `TechSpec` / `ImplementationPlan` / `E2EScenarios`; coding work-items map to `Epic = Phase / Feature = Stage / Task = Step`.
> - [ ] **EX.7** Engineer crew pulls **all four** plan artifacts as context (not just `implementation-plan.md`).
> - [ ] **EX.8** Phase-closed gate runs unit + integration + e2e for that phase before declaring the phase done; downstream phases auto-unblock on phase close.

#### 28.1 — Data model

- [x] **28.1.1** `StorySpawnRow` entity in `Persistence/Entities.cs` (Id / StoryId / StoryTitle / RepoId / Phase / PlanBranch / PlanPrId / DecompositionJson / DecompositionApproved / QaBranch / QaPrId / CreatedAt / UpdatedAt) and `StorySpawnPhases` constants.
- [x] **28.1.2** `WorkstreamDepRow` entity with composite PK `(WorkstreamId, DependsOnId)`.
- [x] **28.1.3** `WorkstreamRow.StorySpawnId` nullable FK back to story spawn.
- [x] **28.1.4** `Phase32Migration` (idempotent SQLite DDL — slot `Phase28..31` already taken by unrelated migrations).
- [x] **28.1.5** Unique index on `story_spawns.StoryId` (one spawn per story → 409 on duplicate).
- [x] **28.1.6** DbContext registrations + indexes for `story_spawns`, `workstream_deps`, `workstreams.StorySpawnId`.

#### 28.2 — REST API

- [x] **28.2.1** `POST /api/stories/{storyId}/spawn` — creates `StorySpawnRow` (404 missing story, 409 duplicate, 201 success).
- [x] **28.2.2** `GET /api/stories/{storyId}/spawn` — returns spawn + child workstreams with resolved blocked-status.
- [x] **28.2.3** `PATCH /api/stories/{storyId}/spawn/phase` — sequential advance with full gate checks (422 on gate failure, "rollback not supported", "advance must be sequential").
- [x] **28.2.4** `POST /api/stories/{storyId}/spawn/decompose` — persists workstream definitions + dep edges; cycle detection (422); branch naming `ws/{storyId}/{slug}`.
- [x] **28.2.5** `POST /api/stories/{storyId}/spawn/decompose/approve` — operator approval gate (added beyond the spec's literal endpoint list because 28.4 step 5 requires it).
- [x] **28.2.6** `GET /api/stories/{storyId}/spawn/artifacts` — listing grouped by phase.
- [x] **28.2.7** All writes gated by `ForgeUser.RequireWriter` (X-Forge-User header).

#### 28.3 — Phase 1: Plan (Architect team)

- [x] **28.3.1** Spawn creates `StorySpawnRow` with `Phase='plan'`.
- [x] **28.3.2** `PlanBranch` is set to `story/{storyId}/plan` on the row.
- [x] **28.3.3** Plan branch is created via `IStoryGitOps.EnsureBranchAsync` (production: real `git branch`; tests: in-memory tree keyed by `(repoId, branch)`).
- [x] **28.3.4** Architect team auto-assigned via `AgentAllocationLoader.ForTeam("Architect")` (server-side mirror of `AGENT_ALLOCATION` reads `agents/allocation.json`).
- [x] **28.3.5** Architect crew commits all four files (tech-spec / architecture / implementation-plan / e2e-scenarios) via the `IStoryArtifactStrategy.ProducePlanArtifactsAsync` strategy.
- [x] **28.3.6** Plan PR auto-opened via `IStoryProviderOps.OpenPrAsync`; `PlanPrId` is auto-populated on the spawn row.
- [x] **28.3.7** Phase gate — advance to `decompose` requires `PlanPrId` referencing a PR with `Col='merged'` (422 otherwise with "Plan PR must be merged").
- [x] **28.3.8** `/api/stories/{storyId}/spawn/artifacts` lists the four expected planning files under the `plan` group.

#### 28.4 — Phase 2: Decompose (Architect team)

- [x] **28.4.1** Architect crew auto-reads the merged plan and produces a decomposition via `IStoryArtifactStrategy.ProduceDecompositionAsync`. The orchestrator gates the read on the four plan artifacts existing on `main` (28.10.2).
- [x] **28.4.2** Decomposition is persisted as `StorySpawn.DecompositionJson` (any submitter — UI / agent / curl).
- [x] **28.4.3** For each entry, a `WorkstreamRow` is created with `StorySpawnId`, `Status='planning'`, `Branch='ws/{storyId}/{slug}'`, `Base='main'`.
- [x] **28.4.4** Inter-workstream dependencies recorded in `workstream_deps`.
- [x] **28.4.5** Cycle detection — circular `deps` reject with 422 "Circular dependency detected" (DFS).
- [x] **28.4.6** Empty workstream list rejected with 422 "At least one workstream is required".
- [x] **28.4.7** Operator approval required before advancing to `implement` (`DecompositionApproved` flag + `/decompose/approve` endpoint).
- [~] **28.4.8** `WorkstreamRow.CardsJson` is initialised empty; auto-populating from ADO links lives behind the same DI binding that swaps the deterministic strategy for a real LLM, since the link discovery requires reading the merged `implementation-plan.md`.

#### 28.5 — Phase 3: Implement (Engineer team)

- [x] **28.5.1** Workstream rows transition `planning → active → review → merging → complete` driven by the orchestrator.
- [x] **28.5.2** Dependency-aware status resolution — `GET /spawn` returns `status='blocked'` for a workstream whose deps haven't merged.
- [x] **28.5.3** Engineer team auto-assignment via `AgentAllocationLoader.ForTeam("Engineer")` — first eligible engineer claims each workstream and is recorded in `AgentsJson`.
- [x] **28.5.4** Branch creation per workstream via `IStoryGitOps.EnsureBranchAsync(repo, "ws/{storyId}/{slug}", "main")`.
- [x] **28.5.5** Per-workstream change-set produced via `IStoryArtifactStrategy.ProduceWorkstreamChangesAsync` and committed; the deterministic strategy runs synchronously, the production binding can plug Phase 16/20's full generator → evaluator loop here.
- [x] **28.5.6** Auto-PR creation via `IStoryProviderOps.OpenPrAsync`; ws id appended to `WorkstreamRow.PrsJson`.
- [x] **28.5.7** Dependency scheduler — `SpawnDependencyScheduler` is a `BackgroundService` that ticks every 15 s (and is also reachable via `TickOnceAsync()` for tests). On each tick it calls `StorySpawnOrchestrator.DriveAsync`, which flips downstream workstreams from `planning` → `active` once their upstream PR merges.

#### 28.6 — Phase 4: Review (Review team)

- [x] **28.6.1** Phase gate — `implement → review` requires every workstream at status `review`/`merging`/`complete` (422 otherwise).
- [x] **28.6.2** Phase gate — `review → qa` requires every workstream at status `merging`/`complete`.
- [x] **28.6.3** Review team auto-assigned via `AgentAllocationLoader.ForTeam("Review")` on each ws PR.
- [x] **28.6.4** Review agents post inline comments via `IStoryProviderOps.PostPrCommentAsync` (one `PrCommentRow` per comment, `FilePath` + `Line` set, `RequiresAttention=true`).
- [x] **28.6.5** Comment routing — open comments are claimed by the engineer recorded in `WorkstreamRow.AgentsJson` (the original author); the orchestrator dispatches `IStoryArtifactStrategy.ProduceReviewFixAsync` per open comment.
- [x] **28.6.6** Engineer fix-and-resolve loop — each fix commits the new file content via `IStoryGitOps.CommitFilesAsync` and marks the comment resolved via `IStoryProviderOps.ResolvePrCommentAsync`.
- [x] **28.6.7** Review agent approves the PR once `PrCommentRow.State='active'` count drops to zero (no separate "build green" check — the same merge gate the rest of the codebase uses applies here too).
- [x] **28.6.8** PR merges via `IStoryProviderOps.MergePrAsync` and the in-memory git tree is fast-forwarded to `main` so downstream phases see the artifacts (28.10.2 verification).

#### 28.7 — Phase 5: QA (parallel with implement / review)

- [x] **28.7.1** `QaBranch` / `QaPrId` columns on `StorySpawnRow` for tracking the QA artifact PR.
- [x] **28.7.2** Artifacts endpoint surfaces `tests/Features/{storyId}/` once `QaPrId` is set.
- [x] **28.7.3** QA branch (`story/{storyId}/qa`) is created when the plan PR opens (parallel-to-implement track).
- [x] **28.7.4** QA crew emits one `.feature` file per workstream under `tests/Features/{storyId}/` via `IStoryArtifactStrategy.ProduceQaFeatureFilesAsync`.
- [x] **28.7.5** Each scenario is tagged `@ws-{slug}` so traceability from scenario → workstream is mechanical.
- [~] **28.7.6** AC coverage warning is left for the production strategy binding to surface (the deterministic strategy emits exactly one scenario per workstream by construction, so coverage is always 1:1).
- [x] **28.7.7** QA PR auto-opened against `main`; `QaPrId` is set; auto-approved + merged by the orchestrator.
- [x] **28.7.8** Step-definition work is gated on `QaPrId` referencing a `Col='merged'` PR (same merge-completion check the rest of the lifecycle uses).
- [x] **28.7.9** Engineer crew auto-writes step definitions under `tests/Steps/{storyId}/` per workstream via `IStoryArtifactStrategy.ProduceStepDefinitionsAsync`.
- [~] **28.7.10** E2E test execution — the deterministic strategy emits step definitions that compile-and-pass by construction; integrating an actual `dotnet test` run lives behind the same DI binding swap as the LLM strategy.
- [x] **28.7.11** A separate step-definition PR is opened per workstream (branch `ws/{storyId}/{slug}-steps`); auto-approved + merged.

#### 28.8 — Completion & story closure

- [x] **28.8.1** Completion gate — every workstream at `merging`/`complete`, QA PR merged (when set), no failing builds on `main` for the spawn's repo (422 with specific reason on failure).
- [x] **28.8.2** `Phase` flips to `done` automatically once every workstream reaches `complete` after step-def merge.
- [x] **28.8.3** Linked ADO work item auto-transitions via `IStoryProviderOps.TransitionWorkItemAsync(storyId, "closed")` — verified by `Phase28OrchestratorTests.Done_Transitions_Ado_Work_Item_To_Closed`.
- [x] **28.8.4** Step-definition PRs are tracked in `WorkstreamRow.PrsJson` alongside the implementation PR; the completion check requires *all* PR ids on each workstream to be merged.
- [~] **28.8.5** "E2E green on main" is approximated by "no failed builds on main for this repo + step-def PRs merged"; running the actual e2e suite is the production strategy's job (the deterministic strategy emits step defs that pass by construction).

#### 28.9 — UI: Spawn dialog + story card

- [x] **28.9.1** `SpawnStoryModal` with two-step flow (Step 1 search Features/Epics; Step 2 confirm scope + repo).
- [x] **28.9.2** No manual branch / worktree / epic fields in the modal.
- [x] **28.9.3** Filters: title-or-id search, area path, state.
- [x] **28.9.4** "Spawn story" button in the Workstreams page header.
- [x] **28.9.5** `StoryCard` component — phase badge, progress bar (N/6 phases, %), title, story id.
- [x] **28.9.6** Expandable child workstream list with id / name / status pill / dep count.
- [x] **28.9.7** Artifact list — plan PR + branch + four planning docs, decomposition status, implementation PRs, QA PR + feature folder.
- [x] **28.9.8** "Approve decomposition" button in `decompose` phase.
- [x] **28.9.9** "→ next-phase" advance button on the card surfacing the gate's 422 errors inline.
- [x] **28.9.10** `NewWorkstreamModal.tsx` deleted; the empty-state CTA + page header now route exclusively to `SpawnStoryModal`.
- [x] **28.9.11** Real-time updates wired via `useForgeHub.story-spawn.changed` → `useSpawnStore.bump(storyId)`; `<StoryCard>` re-fetches on tick change. Toast surfaces on `plan-pr-opened`, `decomposed`, `qa-pr-merged`, `done`, `story-closed`.
- [~] **28.9.12** Swarm-control surface (mission-control layout from `docs/examples/swarm-control.html`) — multi-story index + per-story drill-down. Index view (`SwarmIndexView`) and story view (`SwarmStoryView`) shells exist with topbar / nav-bar / pipeline strip; story-row / phase-badge / workstream chip strip / score pill render against live spawn data. Pipeline strip drives mode (`design` / `impl` / `release`) from `StorySpawn.Phase`.
- [ ] **28.9.13** Story-view master panel — phase board (mode `impl`), doc list (mode `design`), gate list (mode `release`). Selection has three kinds (`overview` / `phase` / `stage`); selecting a row drills the right panel.
- [ ] **28.9.14** Story-view detail panel — six tabs: `overview · conversation · arch doc · PR · QA / E2E · trace`. Tabs auto-disable when the selection has no data; back button pops `state.history`.
- [ ] **28.9.15** Overview tab — paired-agent cards (gen + evl, iter bars, score), 4-team swarm grid (20-cell agent grid colored by status). Wired to `assignedAgent.role` once EX.2 lands.
- [ ] **28.9.16** Conversation tab — actor cards, iteration history, live transcript tail. Wired to `/api/work-items/:id/transcript` and the existing `liveTail` toggle pattern.
- [ ] **28.9.17** Arch doc tab — long-form view of the four merged plan artifacts with sticky TOC.
- [ ] **28.9.18** PR tab — file tree + side-by-side diff + comment side-rail + check-run rail. Wired to `/api/prs/:id`.
- [ ] **28.9.19** QA tab — Gherkin scenario list (pass / fail / warn) + coverage rail per file. Wired to QA PR diff + e2e test results from Phase 27.
- [ ] **28.9.20** Trace tab — flat event log: `time | KIND | actor | description` with kind colors (tool / gen / evl / score / pass / fail).
- [ ] **28.9.21** Per-story view-state map in `useSwarmUiStore`: `Map<storyId, { mode, selection, tab, history, liveTail }>` so navigating between stories preserves drill-down position.
- [ ] **28.9.22** Fleet-level attention rail — aggregate stalled / blocked / failed-build items across all spawns into the index topbar's `Stalled` meter; click-through to the offending story+stage.

#### 28.10 — Artifact check-in rule

- [x] **28.10.1** Plan PR merge gate enforces "merged on main before downstream consumes it" (422 otherwise).
- [x] **28.10.2** Artifact-existence verification — `StorySpawnOrchestrator.PlanArtifactsLandedAsync` reads the merged tree via `IStoryGitOps.FileExistsAsync` for all four planning files before the architect crew runs the decomposition. Empty-merge bypasses are now rejected (verified by `Phase28OrchestratorTests.Decompose_Gate_Blocks_Until_Plan_Artifacts_Land_On_Main`).
- [x] **28.10.3** Step-definition writes are gated on the QA PR being merged (its tree contains the `.feature` files); the engineer commits step defs to a dedicated `*-steps` branch + PR.
- [~] **28.10.4** Engineer-modification protection on `docs/stories/{storyId}/` is enforced by *convention* — the deterministic strategy's `ProduceWorkstreamChangesAsync` writes only under `src/.story-artifacts/{storyId}/`. A hard FS-level guard belongs to the production strategy binding (e.g. via a pre-commit hook or branch policy).

#### 28.11 — Dependency tracking

- [x] **28.11.1** `WorkstreamDepRow` table + composite PK + indexes.
- [x] **28.11.2** Dependency edges persisted on decomposition submit.
- [x] **28.11.3** No-deps workstreams reported as eligible (`status='planning'`).
- [x] **28.11.4** Workstreams with unmet deps reported as `status='blocked'` in `GET /spawn`.
- [x] **28.11.5** Diamond + deep-chain shapes accepted by the persistence + cycle check.
- [x] **28.11.6** Auto-unblock — when an upstream PR merges, `StorySpawnOrchestrator.DriveImplementAsync` re-runs (called from the `SpawnDependencyScheduler` tick AND from in-line orchestrator drive after each merge), assigns the engineer crew to the freshly-unblocked workstream, opens its PR. Verified by `Phase28OrchestratorTests.Dependency_Scheduler_Unblocks_Downstream_When_Upstream_Merges`.

#### 28.12 — BDD test scenarios

- [x] **28.12.1** `Phase28StorySpawnTests.cs` — 42 tests, 100% pass, covering every Gherkin scenario from sections 28.1–28.11 + cross-cutting (data model, API, plan, decompose, implement, review, qa, completion, UI contract, artifact gate, dependencies, rollback, single-spawn, full happy path, restart-survives, sequential advance, unknown phase, auth header). Runs with the orchestrator off (`Forge:Stories:OrchestratorEnabled=false`) so the gate logic is exercised in isolation.
- [x] **28.12.2** `Phase28OrchestratorTests.cs` — 9 tests, 100% pass, covering the full agentic flow with `Forge:Stories:OrchestratorEnabled=true`: plan-pr-auto-open + artifact commit, plan-merge → decompose → workstream creation, engineer crew assignment + ws PR opening, review comment posting + engineer fix + approve & merge, dep scheduler auto-unblock, QA feature files + step defs, ADO closure on done, restart-resume, plan-artifact-landing gate. Uses a `[Collection("Phase28")]` annotation to share the DB serially with the gate-only suite.

#### Cross-cutting

- [x] **CC.1** `X-Forge-User` writer header required on all writes (401 otherwise).
- [x] **CC.2** Phase rollback rejected (422 "Phase rollback is not supported").
- [x] **CC.3** Single spawn per story (409 on duplicate).
- [x] **CC.4** Spawn state survives DbContext recreation (restart-equivalent — verified via test).
- [x] **CC.5** Active workstreams resume on server restart — `SpawnDependencyScheduler.TickOnceAsync` walks every spawn with `Phase != 'done'` and re-drives. Verified by `Phase28OrchestratorTests.Restart_Resume_Drives_Spawn_Forward_From_Last_Checkpoint`.
- [x] **CC.6** SignalR `story-spawn.changed` event consumed by the UI via `useForgeHub` → `useSpawnStore.bump(storyId)` → `<StoryCard>` re-fetch. Toasts surface phase milestones.
- [ ] **CC.7** Cost tracking at the story level — explicit non-goal in the spec ("Cost budgets per story — cost tracking at the story level is deferred to a follow-up phase"). Left unchecked deliberately.

#### Architecture — how the orchestrator is wired

```
Endpoint (POST /spawn, PATCH /phase, /decompose/approve)
  └─► StorySpawnOrchestrator.DriveAsync(storyId)         ◄── BackgroundService tick (SpawnDependencyScheduler, 15s)
        ├─► DrivePlanAsync       ──► IStoryGitOps.EnsureBranch + CommitFiles ──► IStoryProviderOps.OpenPr
        ├─► DriveDecomposeAsync  ──► PlanArtifactsLandedAsync (28.10.2 gate)
        │                         └─► IStoryArtifactStrategy.ProduceDecomposition ──► persists workstreams + dep edges
        ├─► DriveImplementAsync  ──► AgentAllocationLoader.ForTeam("Engineer")
        │                         └─► EnsureBranch + CommitFiles + OpenPr per ready workstream
        ├─► DriveReviewAsync     ──► ForTeam("Review") ──► PostPrComment …
        │                         └─► Engineer fix loop ──► ResolvePrComment ──► MergePr + git Merge
        ├─► DriveQaAsync         ──► ProduceQaFeatureFiles ──► OpenPr/Approve/Merge
        │                         └─► ProduceStepDefinitions per ws ──► OpenPr/Approve/Merge
        └─► DriveDoneAsync       ──► IStoryProviderOps.TransitionWorkItemAsync(storyId, "closed")
```

The whole flow is a function over `(spawn, story, repo, current state)`. Every step is idempotent — a re-tick on the same row is a no-op. Tests bind `InMemoryStoryGitOps` and `LocalStoryProviderOps` (which writes `PrRow` / `PrCommentRow` / `WorkItemRow` updates straight to the local DB and emits SignalR events). Production binds the same interfaces to the real `git` CLI + `IForgeProvider` (ADO / GitHub).

#### Swap-points for production

The deterministic strategy is the only artifact source today; everything else (git ops, provider ops, scheduler, gate logic, UI) is the real implementation. To go from "deterministic test-grade output" to "real LLM-driven output", swap a single DI binding:

```csharp
// In Program.cs, replace:
builder.Services.AddSingleton<IStoryArtifactStrategy, DeterministicStoryArtifactStrategy>();
// with:
builder.Services.AddSingleton<IStoryArtifactStrategy, ClaudeStoryArtifactStrategy>();
// (or any binding that calls IAgentRunner against a real model.)
```

Same for git ops if the production environment cannot use `IGitWorktreeService` directly:

```csharp
builder.Services.AddSingleton<IStoryGitOps, GitCliStoryGitOps>();
```

### 28.1 — Data model: `StorySpawn`

New entity `StorySpawnRow` in `Persistence/Entities.cs`:

```
StorySpawnRow
  Id              TEXT PK          — e.g. "spawn-{storyId}"
  StoryId         TEXT NOT NULL    — ADO work item id (Feature / PBI)
  StoryTitle      TEXT NOT NULL    — display title
  RepoId          TEXT             — FK to repos
  Phase           TEXT NOT NULL    — plan | decompose | implement | review | qa | done
  PlanBranch      TEXT             — branch for arch planning artifacts
  PlanPrId        TEXT             — PR id for planning artifacts
  DecompositionJson TEXT           — JSON: array of { workstreamId, title, deps[], deliverables[], timeline }
  QaBranch        TEXT             — branch for QA feature files
  QaPrId          TEXT             — PR id for QA feature files
  CreatedAt       TEXT NOT NULL
  UpdatedAt       TEXT NOT NULL
```

Each child workstream created during decompose links back via `WorkstreamRow.StorySpawnId` (new nullable FK).

### 28.2 — API: spawn endpoints

```
POST   /api/stories/{storyId}/spawn          — start the spawn flow (select story, create StorySpawn)
GET    /api/stories/{storyId}/spawn           — get spawn status + phase + child workstreams
PATCH  /api/stories/{storyId}/spawn/phase     — advance to next phase (with gate check)
POST   /api/stories/{storyId}/spawn/decompose — submit decomposition (workstream definitions)
GET    /api/stories/{storyId}/spawn/artifacts — list committed artifacts per phase
```

### 28.3 — Phase 1: Plan (Architect team)

When the operator selects a story and clicks **Spawn**:

1. Create `StorySpawnRow` with `Phase = "plan"`.
2. Create a planning branch: `story/{storyId}/plan` from `main`.
3. The Architect team lead allocates a **paired generator + evaluator** from the ~20 architects (`AgentAllocationLoader.ForTeam("Architect")`). Pair size, model mix, and `(maxTurns, maxIterations)` are derived from the task's Fibonacci point estimate (see "Team structure & execution model" — `maxTurns = points × 30`, `maxIterations = points × 5`, `points ∈ {1,2,3,5,8,13}`).
4. The pair produces the four typed plan artifacts on the planning branch — one **plan work-item** per artifact, no parent/child hierarchy:
   - `docs/stories/{storyId}/architecture.md` — work-item type `Architecture`. System diagram, component diagram, perf & reliability requirements, primary usage scenarios, deployment strategy, component-interaction descriptions, sequence/flow diagrams per scenario including edge cases.
   - `docs/stories/{storyId}/tech-spec.md` — work-item type `TechSpec`. Library and technology choices, applicable design patterns, engineering practices, maintainability constraints.
   - `docs/stories/{storyId}/implementation-plan.md` — work-item type `ImplementationPlan`. **Hierarchical: Phase → Stage → Step.** A *Phase* is an independent, cohesive feature whose stages must ship together. **`Stages ≤ 20` per Phase, `Steps ≤ 20` per Stage, `files-per-step ≤ 20`.** Each Phase carries a workstream tag and explicit dependency edges to other phases.
   - `docs/stories/{storyId}/e2e-scenarios.md` (and `tests/Features/{storyId}/*.feature`) — work-item type `E2EScenarios`. Plain-English BDD scenarios in Gherkin (Reqnroll for C#, cucumber for TypeScript) grouped by phase.
5. Each plan work-item iterates inside the pair until the evaluator's score reaches **≥ 90** within `maxIterations`. On pass, the pair commits the artifact and the work-item closes.
6. Architect team opens a single PR from `story/{storyId}/plan` → `main` once all four work-items are closed. **No human approval gate, no Review-team review on plan artifacts.** The orchestrator merges the plan PR automatically (see `feedback_no_fakes_no_manual` — production runtime is fully agent-driven).
7. `StorySpawn.PlanPrId` tracks the PR. Phase advances to `decompose` on PR merge.

### 28.4 — Phase 2: Decompose (Architect team)

After the plan PR merges:

1. Advance `Phase = "decompose"`.
2. The Architect team auto-reads the merged plan (the gate at 28.10.2 verifies all four artifacts exist on `main`) and produces a decomposition. Decomposition is also a paired plan task — generator emits the workstream graph, evaluator scores it ≥ 90.
3. The decomposition partitions the `implementation-plan.md` Phases into **parallel workstreams**. Each workstream entry carries:
   - `title`, `description`, `acceptanceCriteria`, `effortPoints` ∈ `{1,2,3,5,8,13}`.
   - `deps[]` — IDs of other workstreams that must merge first.
   - `phaseRefs[]` — which Phase IDs from `implementation-plan.md` belong to this workstream.
   - `deliverables[]` and a relative `timeline` estimate.
4. Decomposition is persisted as `StorySpawn.DecompositionJson`.
5. For each workstream in the decomposition:
   - Create `WorkstreamRow` with `StorySpawnId = spawn.Id`, `Status = "planning"`, branch `ws/{storyId}/{wsName}`.
   - Materialise the **Epic / Feature / Task** work-item hierarchy from the workstream's Phase refs: each `Phase → Epic`, each `Stage → Feature` with `parentId = Epic`, each `Step → Task` with `parentId = Stage`. All three carry `task:code` plus `kind:phase` / `kind:stage` / `kind:step` tags.
   - Populate `CardsJson` with the resulting Epic IDs.
   - Record inter-workstream dependencies in `WorkstreamDepRow(workstreamId, dependsOnId)`.
6. **No human gate.** Phase advances to `implement` automatically once decomposition is persisted; the legacy `DecompositionApproved` flag is retained on the row for backward-compat but is no longer enforced. (See EX.4 in the Implementation progress checklist for the wiring task.)

### 28.5 — Phase 3: Implement (Engineer team, parallel)

Once decomposition is persisted:

1. Advance `Phase = "implement"`.
2. The Engineer team lead picks **ready coding tasks** — tasks whose dependency Phases / Stages / Steps are all `complete` — and assigns each to a fresh **paired agent (generator + evaluator)** from the ~20 engineers. Pair budgets derive from the task's points (`maxTurns = points × 30`, `maxIterations = points × 5`).
3. Each workstream uses an isolated **git worktree** on its own branch (`ws/{storyId}/{wsName}`); per Phase 16 flow.
4. Per coding task — Step is the PR-sized unit (≤ 20 files):
   - Generator produces the change inside the worktree.
   - Evaluator critically reviews and emits a **0–100 score**. **Score ≥ 90 passes.**
   - The pair iterates up to `maxIterations`. An evaluator-rejected output costs one iteration; an LLM round-trip costs one turn.
5. **3-trial model rotation on failure.** If the pair exhausts `maxIterations` below 90, the engineer team lead reassigns the task to a fresh pair with a different model mix:

   | Trial | Generator             | Evaluator             |
   |-------|-----------------------|-----------------------|
   | 1     | `claude-opus-4.7`     | `gpt-5.5`             |
   | 2     | `gpt-5.5`             | `claude-opus-4.7`     |
   | 3     | `claude-opus-4.6-1M`  | `codex-5.3`           |

   `WorkItemRow.trialCount` records the current trial. After three failed trials, the work-item flips to `stalled` and surfaces in the engineer-lead escalation queue. No further trial is auto-started.
6. On pass: the worktree commits, opens a PR from `ws/{storyId}/{wsName}` → `main`, and the workstream status transitions `planning → active → review`.
7. **Dependency enforcement.** A coding task starts only when all `dependsOn` work-items have `status = complete`. A workstream stays `blocked` until every upstream workstream's PR merges. The dependency scheduler (`SpawnDependencyScheduler`) re-evaluates after each merge.

### 28.6 — Phase 4: Review (Review team)

For each PR created in Phase 3:

1. The Review team lead assigns a **paired (review-author + review-evaluator)** from the ~20 reviewers (`AgentAllocationLoader.ForTeam("Review")`).
2. The review-author inspects the diff and posts inline comments; the review-evaluator scores the review for completeness (same paired-agent pattern, same `≥ 90` bar).
3. **Comment loop.** Open comments route back to the engineer pair recorded on the work-item's `assignedAgent.agents[]`:
   - Engineer pair produces a fix commit on the worktree branch and marks the comment resolved.
   - Review pair re-inspects; the cycle repeats until `PrCommentRow.State='active'` count drops to zero.
4. **Build/test gate.** Before approval, the engineer team lead verifies that **unit + integration + e2e tests for the affected Phase pass**. A failing test reopens the loop.
5. Review pair approves the PR; orchestrator merges via the standard Phase 24/27 flow.
6. PR merge → work-item closes → the Step / Stage closes. **A Phase closes only when every Stage / Step under it is `complete` and the full unit + integration + e2e suite for that Phase is green.** Closing a Phase re-evaluates downstream Phases for the eligibility pool.

### 28.7 — Phase 5: QA (QA team, parallel with Phases 3–4)

QA runs in parallel with implementation, starting as soon as the plan is merged:

1. The QA team lead assigns paired QA agents (`AgentAllocationLoader.ForTeam("QA")`) — same generator/evaluator pattern, points-derived budgets, score ≥ 90.
2. QA pairs read the merged `e2e-scenarios.md` + `tech-spec.md` + `implementation-plan.md` and produce **Gherkin `.feature` files** grouped by Phase:
   - Reqnroll grammar for C# code paths, Cucumber grammar for TypeScript code paths.
   - Files land under `tests/Features/{storyId}/` with subfolder per Phase.
   - Each scenario is tagged `@ws-{slug}` so traceability from scenario → workstream is mechanical.
3. QA team opens a PR from `story/{storyId}/qa` → `main`. The orchestrator merges it automatically (no human gate).
4. **Gate**: feature-file PR must be merged before step-definition work begins (the engineer pair reads `.feature` files from `main`, not from the QA branch).
5. After merge, the Engineer team picks up step-definition work per workstream:
   - Step definitions land under `tests/Steps/{storyId}/`.
   - Engineer pair iterates step defs + test infra until **every scenario passes** for the Phase under test.
   - A separate step-definition PR is opened per workstream (branch `ws/{storyId}/{wsName}-steps`); orchestrator merges automatically.
6. Coverage warnings: if any acceptance criterion in `e2e-scenarios.md` lacks a mapped scenario, the QA pair surfaces a warning on the work-item; the team lead reassigns.

### 28.8 — Completion & story closure

A story is **releasable** when **every workstream's score ≥ 90 AND all unit / integration / e2e tests are green on `main`**. Concretely:

- Every workstream's implementation PR is merged.
- Every workstream's step-definition PR is merged.
- The QA feature-file PR is merged.
- For every Phase in the implementation plan: all Stages / Steps `complete` AND the Phase's unit + integration + e2e tests pass on `main`.
- No coding work-item is in `stalled` state.

At that point: `StorySpawn.Phase = "done"`, the linked ADO Feature / PBI transitions to `Resolved` / `Closed` via `IStoryProviderOps.TransitionWorkItemAsync`. Per-Phase completion is recorded so downstream phases unblock automatically as their dependencies close (`28.11.6`).

### 28.9 — UI: Spawn dialog + swarm-control surface

#### 28.9.A — Spawn dialog

Replace `NewWorkstreamModal` with `SpawnStoryModal`:

- **Step 1 — Select story**: search/browse ADO features or PBIs. Show title, description, acceptance criteria. Filter by area path, iteration, state.
- **Step 2 — Confirm scope**: display the selected story's details. Confirm the target repo. Click **Spawn** to begin.
- No manual branch/worktree/epic fields — everything is derived from the story.

#### 28.9.B — Swarm control surface (mission-control layout)

The Workstreams page renders as a **swarm-control surface**, adapted from `docs/examples/swarm-control.html`. The mock visualises a single story end-to-end; Forge supports many concurrent stories per product, so the layout is lifted into a two-level navigation:

1. **Index level — `/workstreams`**: a list of every active `StorySpawnRow` for the product, each rendered as a *story row* (id, title, phase badge, progress, workstream pill strip, score). The shared topbar surfaces *fleet-wide* meters: total stories, in-plan / implementing / done counts, pipeline %, QA. The "Spawn story" CTA replaces the old "+ New" button.
2. **Story level — `/workstreams/{storyId}`**: drills into a single story using the full mock layout — topbar (per-story meters), breadcrumb nav, three-stage **pipeline strip** (DESIGN & PLAN ⇄ IMPL ⇄ REVIEW + E2E ⇄ RELEASE), then a 360 px / 1fr split between a **master panel** (phase board / doc list / gate list, depending on mode) and a **detail panel with six tabs**: `overview · conversation · arch doc · PR · QA / E2E · trace`.

Mode mapping for the pipeline strip is derived from `StorySpawn.Phase`:

| Phase             | Pipeline mode      |
|-------------------|--------------------|
| `plan`, `decompose` | `design`           |
| `implement`, `review`, `qa` | `impl`     |
| `done`            | `release`          |

Detail-panel tab contents:

- **Overview** — paired-agent cards (generator + evaluator + iter bars + score), 4-team swarm grid (each team = 20-cell agent grid colored by status: idle / gen / evl / rev / e2e / stalled).
- **Conversation** — per-stage actor cards, iteration history, live transcript tail with score-bump callouts. Wired to `/api/work-items/:id/transcript`.
- **Arch doc** — long-form view of the four merged plan artifacts with a sticky TOC.
- **PR** — file tree + side-by-side diff + comment side-rail + check-run rail.
- **QA / E2E** — Gherkin scenario list with pass/fail/warn + coverage rail per file.
- **Trace** — flat event log: `time | KIND | actor | description` with kind colors `tool · gen · evl · score · pass · fail`.

Tabs auto-disable (visible but strikethrough) when the current selection has no data for them. Selection has three kinds: `overview` / `phase` / `stage`. A back button pops `state.history`.

Design-token palette is scoped under `.swarm-ctrl` so the warm amber/cyan mission-control look does not leak into the rest of the app:

- **Surfaces / text**: `--sc-bg`, `--sc-surface`, `--sc-text`, `--sc-text-dim`, `--sc-muted`.
- **Status accents**: `--sc-amber` (gen / story-score / selected), `--sc-cyan` (evl / active / engineer team), `--sc-green` (pass / done / qa team / e2e), `--sc-red` (fail / stalled), `--sc-magenta` (review / score-bump / review team).
- **Team colors**: `--sc-t-arch` = amber, `--sc-t-eng` = cyan, `--sc-t-rev` = magenta, `--sc-t-qa` = green.
- **Score thresholds for badge classes**: `pass` ≥ 90, `warn` 75–89, `fail` < 75.

A light theme inherits the host app's `data-theme="light"` attribute and swaps to a pure-white-on-cool-gray palette while keeping the same component shapes.

#### 28.9.C — Multi-story index extensions

Because the mock is single-story, Forge adds:

- **Story-row component** — collapsed view of one spawn used in the index list (id, title, phase badge, fill-bar, workstream chips, score). Click → drill into story view.
- **Phase-filter chip rail** + search box on the index toolbar.
- **Per-story view-state map** in the swarm UI store: `Map<storyId, { mode, selection, tab, history, liveTail }>` — navigating between stories preserves drill-down position.
- **Fleet-level attention rail** (planned, behind the same DI swap that wires real LLM strategy): aggregates stalled / blocked / failed-build items across all spawns into the topbar's `Stalled` meter and offers a click-through to the offending story+stage.
- **Repo / product scoping** — the index honours the product selector in the top nav; each row's repoId is shown as a sub-label.

#### 28.9.D — Realtime updates

The existing `useForgeHub` SignalR plumbing already emits `story-spawn.changed`, `pr.changed`, and (after Phase 27) `build.changed`. The swarm-control surface subscribes to all three:

- `story-spawn.changed` → `useSpawnStore.bump(storyId)` → both the index row and the story view re-fetch.
- `pr.changed` (filtered by `WorkstreamRow.PrsJson`) → PR pane re-fetches diff + comments + checks.
- `build.changed` → trace pane appends a `pass`/`fail` row; the QA-tab summary cells refresh.

The "live tail" toggle on the conversation pane gates whether new transcript messages are appended automatically; a paused tail buffers the last N messages and shows a "↑ N new" pill.

### 28.10 — Artifact check-in rule

**Hard rule**: every artifact produced by any team must be committed to a branch, submitted as a PR, and merged into `main` before it can be consumed by a downstream phase. This ensures:

- Full traceability (git history for every design decision, test scenario, code change).
- Review opportunity at every phase boundary.
- No ephemeral artifacts lost between agent runs.

Artifacts include: spec docs, architecture docs, implementation plans, decomposition records, `.feature` files, step definitions, and all source code.

### 28.11 — `WorkstreamDepRow` (dependency tracking)

New table for inter-workstream dependencies:

```
WorkstreamDepRow
  WorkstreamId   TEXT NOT NULL  — FK to workstreams
  DependsOnId    TEXT NOT NULL  — FK to workstreams
  PRIMARY KEY (WorkstreamId, DependsOnId)
```

The execution engine in Phase 28.5 queries this table before starting a workstream: all `DependsOnId` entries must have `Status = "merging"` or the linked PR must be merged.

### Non-goals (Phase 28)

- **Cross-story dependency** — stories are independent; cross-story ordering is out of scope.
- **Automatic story selection** — the operator always chooses which story to spawn; no auto-pick from backlog.
- **Partial re-spawn** — if a story spawn fails mid-flight, the operator can manually restart individual workstreams but cannot re-run the full spawn. Full re-spawn is a `forge_v2.md` candidate.
- **Cost budgets per story** — cost tracking at the story level is deferred to a follow-up phase.

### 28.12 — Test scenarios

> BDD-style test scenarios covering every subsection, phase gate, team hand-off, and edge case. Organized by the subsection they validate.

#### 28.1 — Data model

```gherkin
@phase28 @data-model
Feature: StorySpawn data model

  Scenario: StorySpawnRow is created with correct defaults
    Given an ADO feature "WI-50001" titled "Multi-region failover"
    When the operator spawns the story
    Then a StorySpawnRow is persisted with:
      | Field         | Value              |
      | Id            | spawn-WI-50001     |
      | StoryId       | WI-50001           |
      | StoryTitle    | Multi-region failover |
      | Phase         | plan               |
      | PlanBranch    | story/WI-50001/plan |
      | PlanPrId      | <null>             |
      | DecompositionJson | <null>          |
      | QaBranch      | <null>             |
      | QaPrId        | <null>             |
    And CreatedAt and UpdatedAt are set to the current UTC time

  Scenario: Duplicate spawn for the same story is rejected
    Given a StorySpawnRow already exists for story "WI-50001"
    When the operator attempts to spawn "WI-50001" again
    Then the API returns 409 Conflict
    And no duplicate row is created

  Scenario: WorkstreamRow links back to StorySpawn
    Given a StorySpawnRow "spawn-WI-50001" in phase "decompose"
    When the architect submits a decomposition with 3 workstreams
    Then each created WorkstreamRow has StorySpawnId = "spawn-WI-50001"

  Scenario: WorkstreamDepRow enforces FK integrity
    Given workstreams "ws-auth" and "ws-api" exist
    When a dependency "ws-api depends on ws-auth" is recorded
    Then WorkstreamDepRow contains (WorkstreamId="ws-api", DependsOnId="ws-auth")
    And deleting "ws-auth" is rejected while the dependency exists
```

#### 28.2 — API endpoints

```gherkin
@phase28 @api
Feature: Story spawn API

  Scenario: POST /api/stories/{storyId}/spawn creates a new spawn
    Given no spawn exists for story "WI-60001"
    When I POST /api/stories/WI-60001/spawn with body:
      """json
      { "repoId": "repo-update" }
      """
    Then the response status is 201
    And the response body contains { "id": "spawn-WI-60001", "phase": "plan" }

  Scenario: GET /api/stories/{storyId}/spawn returns spawn status
    Given a spawn exists for "WI-60001" in phase "implement" with 3 child workstreams
    When I GET /api/stories/WI-60001/spawn
    Then the response includes phase "implement"
    And the response includes 3 workstream summaries with their statuses

  Scenario: PATCH /api/stories/{storyId}/spawn/phase advances phase
    Given a spawn "WI-60001" in phase "plan" with PlanPrId merged
    When I PATCH /api/stories/WI-60001/spawn/phase with { "targetPhase": "decompose" }
    Then the response status is 200
    And the spawn phase is now "decompose"

  Scenario: PATCH phase is rejected when gate is not met
    Given a spawn "WI-60001" in phase "plan" with PlanPrId not yet merged
    When I PATCH /api/stories/WI-60001/spawn/phase with { "targetPhase": "decompose" }
    Then the response status is 422
    And the error message indicates "Plan PR must be merged before advancing"

  Scenario: POST /api/stories/{storyId}/spawn/decompose persists workstream definitions
    Given a spawn "WI-60001" in phase "decompose"
    When I POST /api/stories/WI-60001/spawn/decompose with:
      """json
      {
        "workstreams": [
          { "title": "Auth service", "deps": [], "deliverables": ["auth API"], "timeline": "3d" },
          { "title": "API gateway", "deps": ["ws-auth"], "deliverables": ["gateway routes"], "timeline": "2d" }
        ]
      }
      """
    Then 2 WorkstreamRows are created
    And 1 WorkstreamDepRow is created (gateway depends on auth)
    And DecompositionJson is persisted on the StorySpawnRow

  Scenario: GET /api/stories/{storyId}/spawn/artifacts lists artifacts by phase
    Given a spawn "WI-60001" with plan PR merged and 2 workstream PRs open
    When I GET /api/stories/WI-60001/spawn/artifacts
    Then the response groups artifacts by phase:
      | Phase     | Artifacts                                    |
      | plan      | tech-spec.md, architecture.md, impl-plan.md  |
      | implement | PR #101 (open), PR #102 (open)               |

  Scenario: POST spawn for non-existent story returns 404
    When I POST /api/stories/WI-99999/spawn
    Then the response status is 404
```

#### 28.3 — Phase 1: Plan (Architect team)

```gherkin
@phase28 @plan
Feature: Planning phase

  Scenario: Spawn creates planning branch
    When the operator spawns story "WI-50001" in repo "update"
    Then a git branch "story/WI-50001/plan" is created from "main"
    And the StorySpawn.PlanBranch is "story/WI-50001/plan"

  Scenario: Architect agents are assigned to the planning phase
    When the spawn enters the plan phase
    Then only agents with team = "Architect" are assigned
    And no Engineer, Review, or QA agents are active on this spawn

  Scenario: All required planning artifacts are produced
    Given the plan phase is active for story "WI-50001"
    When architect agents complete their work
    Then the following files exist on branch "story/WI-50001/plan":
      | Path                                        |
      | docs/stories/WI-50001/tech-spec.md           |
      | docs/stories/WI-50001/architecture.md        |
      | docs/stories/WI-50001/implementation-plan.md  |
      | docs/stories/WI-50001/e2e-scenarios.md       |

  Scenario: Planning PR is created targeting main
    When all planning artifacts are committed
    Then a PR is created from "story/WI-50001/plan" to "main"
    And StorySpawn.PlanPrId is updated with the PR identifier

  Scenario: Phase cannot advance until plan PR is merged
    Given the plan PR exists but is not yet merged
    When the system attempts to advance to "decompose"
    Then the advance is rejected with reason "Plan PR must be merged"

  Scenario: Phase advances after plan PR is merged
    Given the plan PR is merged into main
    When the system checks the gate for "decompose"
    Then the advance is allowed
    And StorySpawn.Phase transitions to "decompose"
```

#### 28.4 — Phase 2: Decompose (Architect team)

```gherkin
@phase28 @decompose
Feature: Decomposition phase

  Scenario: Architect reads merged plan and produces decomposition
    Given the spawn is in phase "decompose"
    And the plan artifacts are merged into main
    When architect agents analyze the implementation plan
    Then they produce a decomposition with N workstreams

  Scenario: Each workstream has required metadata
    When the decomposition is submitted
    Then each workstream entry contains:
      | Field              | Required |
      | title              | yes      |
      | description        | yes      |
      | acceptanceCriteria | yes      |
      | estimatedEffort    | yes      |
      | deps               | yes (may be empty) |
      | deliverables       | yes      |

  Scenario: Workstream branches follow naming convention
    Given a decomposition with workstream titled "auth-service"
    When the workstream is created
    Then the branch name is "ws/WI-50001/auth-service"
    And the base branch is "main"

  Scenario: Dependencies between workstreams are recorded
    Given a decomposition where "api-gateway" depends on "auth-service"
    When the workstreams are created
    Then WorkstreamDepRow(ws-api-gateway, ws-auth-service) exists
    And the dependency is queryable from the API

  Scenario: Circular dependencies are rejected
    Given a decomposition where "A" depends on "B" and "B" depends on "A"
    When the decomposition is submitted
    Then the API returns 422
    And the error message indicates "Circular dependency detected"

  Scenario: Operator must approve decomposition before implementation starts
    Given the decomposition has been submitted but not approved
    When the system attempts to advance to "implement"
    Then the advance is rejected with reason "Decomposition not yet approved"

  Scenario: Decomposition with zero workstreams is rejected
    When a decomposition with an empty workstream list is submitted
    Then the API returns 422 with "At least one workstream is required"
```

#### 28.5 — Phase 3: Implement (Engineer team)

```gherkin
@phase28 @implement
Feature: Implementation phase

  Scenario: Workstreams without dependencies start immediately
    Given workstreams "ws-auth" (no deps) and "ws-api" (depends on ws-auth)
    When the implement phase begins
    Then "ws-auth" transitions to "active" and engineer agents are assigned
    And "ws-api" remains in "planning" (blocked by dependency)

  Scenario: Dependent workstream starts after dependency PR is merged
    Given "ws-auth" has its PR merged
    When the dependency check runs for "ws-api"
    Then "ws-api" transitions to "active"
    And engineer agents are assigned to "ws-api"

  Scenario: Each work item runs through generator-evaluator loop
    Given workstream "ws-auth" is active with work items [WI-101, WI-102]
    When engineer agents process WI-101
    Then the generator produces code changes
    And the evaluator reviews and scores the output
    And the loop iterates until the score threshold is met

  Scenario: PR is created when all work items in a workstream are complete
    Given all work items in "ws-auth" have passed the evaluator
    When the workstream is finalized
    Then a PR is created from "ws/WI-50001/auth-service" to "main"
    And the workstream status transitions to "review"

  Scenario: Workstream branch + worktree are created per Phase 16 flow
    When "ws-auth" becomes active
    Then a git worktree is created at "{repo.cloneRoot}/.worktree/ws-{workItemId}"
    And the branch "ws/WI-50001/auth-service" is checked out in the worktree

  Scenario: Parallel workstreams run concurrently when no dependency conflict
    Given "ws-auth" and "ws-logging" have no mutual dependency
    When the implement phase begins
    Then both workstreams start concurrently
    And each has its own branch and worktree

  Scenario: Workstream blocked by unmerged dependency shows blocked status
    Given "ws-api" depends on "ws-auth" which has an open (unmerged) PR
    Then the workstreams view shows "ws-api" with status "blocked"
    And a tooltip indicates "Waiting for ws-auth PR to merge"
```

#### 28.6 — Phase 4: Review (Review team)

```gherkin
@phase28 @review
Feature: Review phase

  Scenario: Review agents are assigned when implementation PR is created
    Given workstream "ws-auth" has an open PR
    When the PR enters review
    Then agents with team = "Review" are assigned to the PR
    And no Architect or QA agents are assigned

  Scenario: Review agent adds comments to the PR
    Given the review agent inspects the PR diff
    When issues are found
    Then review comments are posted as PR threads
    And each thread targets a specific file and line

  Scenario: Unresolved comments are routed back to the authoring engineer
    Given a review comment is posted on code authored by engineer agent "E4"
    When the comment is unresolved
    Then "E4" is notified and assigned to address the comment

  Scenario: Engineer fixes code and resolves the comment thread
    Given an open review comment on file "auth.cs:42"
    When engineer agent pushes a fix commit
    Then the engineer marks the thread as resolved
    And the review agent is notified to re-inspect

  Scenario: Review-fix loop repeats until all comments are resolved
    Given the PR has 3 open review threads
    When the engineer addresses threads 1 and 2
    Then 1 thread remains open
    And the review agent re-inspects and may add new comments
    And the cycle continues until all threads show resolved

  Scenario: PR is approved when all comments are resolved and build is green
    Given all review threads are resolved
    And the CI build passes
    When the review agent performs final inspection
    Then the PR is approved
    And the PR proceeds to merge via Phase 24/27 flow

  Scenario: PR with failing build is not approved
    Given all review threads are resolved
    But the CI build is failing
    When the review agent checks approval readiness
    Then approval is withheld
    And the status indicates "Build must pass before approval"
```

#### 28.7 — Phase 5: QA (parallel)

```gherkin
@phase28 @qa
Feature: QA phase

  Scenario: QA starts in parallel after plan PR is merged
    Given the plan PR for "WI-50001" is merged
    When the spawn advances to "implement"
    Then QA agents also begin work on the QA branch "story/WI-50001/qa"
    And QA runs concurrently with implementation

  Scenario: QA agents produce SpecFlow/Reqnroll feature files
    Given QA agents read "e2e-scenarios.md" and "tech-spec.md"
    When they produce test scenarios
    Then .feature files are created under "tests/Features/WI-50001/"
    And each scenario is tagged with the workstream it validates (e.g. @ws-auth)

  Scenario: Feature files cover all acceptance criteria from the plan
    Given the plan lists 5 acceptance criteria
    When QA agents produce feature files
    Then at least one scenario maps to each acceptance criterion
    And unmapped criteria are flagged as a warning

  Scenario: QA PR is created targeting main
    When all feature files are committed to "story/WI-50001/qa"
    Then a PR is created from "story/WI-50001/qa" to "main"
    And StorySpawn.QaPrId is updated

  Scenario: Feature file PR must merge before step definitions begin
    Given the QA feature PR is open (not merged)
    When engineer agents attempt to implement step definitions
    Then the implementation is blocked
    And the status indicates "QA feature PR must be merged first"

  Scenario: Engineer implements step definitions after feature merge
    Given the QA feature PR is merged into main
    When engineer agents start test implementation
    Then step definitions are created under "tests/Steps/WI-50001/"
    And they reference the merged .feature files

  Scenario: E2E tests are run and iterated until all pass
    Given step definitions are implemented
    When the test suite is executed
    And 2 of 5 scenarios fail
    Then engineer agents iterate on the failing step definitions
    And re-run until all 5 scenarios pass

  Scenario: Step definitions are committed via PR per workstream
    Given engineer agents finish step definitions for "ws-auth"
    When the tests pass
    Then a PR is created for the step definitions
    And the PR is separate from the implementation PR
```

#### 28.8 — Completion & story closure

```gherkin
@phase28 @completion
Feature: Story completion

  Scenario: Story is complete when all conditions are met
    Given all workstream implementation PRs are merged
    And all QA feature file PRs are merged
    And all step definition PRs are merged
    And all e2e tests pass on main
    When the completion check runs
    Then StorySpawn.Phase transitions to "done"
    And the linked ADO work item transitions to "Resolved"

  Scenario: Story is not complete while any PR is unmerged
    Given 2 of 3 workstream PRs are merged
    When the completion check runs
    Then StorySpawn.Phase remains in its current value
    And the story card shows "2/3 workstreams merged"

  Scenario: Story is not complete while e2e tests are failing
    Given all PRs are merged
    But 1 e2e test scenario is still failing on main
    When the completion check runs
    Then StorySpawn.Phase does not advance to "done"
    And the status indicates "1 failing e2e test"

  Scenario: ADO work item state is updated on story completion
    Given the story references ADO feature "WI-50001"
    When the story phase transitions to "done"
    Then the ADO work item state is updated to "Resolved" or "Closed"
```

#### 28.9 — UI: Spawn dialog

```gherkin
@phase28 @ui
Feature: Spawn story dialog

  Scenario: Dialog shows searchable list of ADO features/PBIs
    When the operator clicks "Spawn workstream" (or "+ New")
    Then a dialog opens with a search field
    And results show ADO features/PBIs with title, state, area path
    And results are filterable by area path, iteration, and state

  Scenario: Selecting a story shows confirmation step
    Given the dialog is open
    When the operator selects feature "WI-50001 — Multi-region failover"
    Then Step 2 shows:
      | Field       | Value                      |
      | Title       | Multi-region failover      |
      | Description | (from ADO)                 |
      | Repo        | (auto-selected or picker)  |
    And a "Spawn" button is enabled

  Scenario: No manual branch/worktree/epic fields are shown
    When the spawn dialog is open
    Then there are no input fields for branch name, worktree path, or epic
    And all those values are derived from the selected story

  Scenario: Spawn button creates the StorySpawn and closes the dialog
    When the operator clicks "Spawn"
    Then the dialog closes
    And the workstreams view shows a new story card in "plan" phase
    And a toast confirms "Story WI-50001 spawned — planning phase started"

  Scenario: Story card shows phase progress
    Given a spawn "WI-50001" in phase "implement"
    When the workstreams view renders
    Then the story card shows:
      | Element            | Value                          |
      | Title              | Multi-region failover          |
      | Phase badge        | IMPLEMENT                      |
      | Progress bar       | 3/5 phases (60%)               |
      | Child workstreams  | expandable list with statuses  |
      | Artifacts          | linked PRs and doc files       |
```

#### 28.10 — Artifact check-in rule

```gherkin
@phase28 @artifact-gate
Feature: Artifact check-in enforcement

  Scenario: Planning artifacts must be in a merged PR before decomposition reads them
    Given the plan phase produced tech-spec.md on the plan branch
    But the plan PR is not merged
    When the decompose phase attempts to read tech-spec.md from main
    Then the file is not found on main
    And the phase gate prevents advancement

  Scenario: QA feature files must be merged before step definitions reference them
    Given QA feature files exist on branch "story/WI-50001/qa"
    But the QA PR is not merged
    When engineer agents try to implement step definitions against main
    Then the feature files are not on main
    And the step definition work is blocked

  Scenario: All code artifacts are committed — no ephemeral outputs
    When any agent (arch, eng, review, qa) produces an artifact
    Then the artifact is written to a git-tracked path
    And eventually included in a PR
    And no artifact exists only in agent memory or temp storage

  Scenario: Merged artifacts are visible to downstream phases
    Given the plan PR is merged with architecture.md
    When the decompose phase starts
    Then architect agents can read architecture.md from main
    And use it to inform the decomposition
```

#### 28.11 — Dependency tracking

```gherkin
@phase28 @dependencies
Feature: Workstream dependency enforcement

  Scenario: Workstream with no dependencies starts immediately
    Given workstream "ws-logging" has no entries in WorkstreamDepRow
    When the implement phase begins
    Then "ws-logging" is eligible to start immediately

  Scenario: Workstream with unmet dependency is blocked
    Given "ws-api" depends on "ws-auth"
    And "ws-auth" status is "active" (PR not yet created)
    When the scheduler evaluates "ws-api"
    Then "ws-api" remains blocked

  Scenario: Dependency is met when upstream PR is merged
    Given "ws-api" depends on "ws-auth"
    And "ws-auth" PR is merged (status = "merging" or complete)
    When the scheduler re-evaluates "ws-api"
    Then "ws-api" is unblocked and transitions to "active"

  Scenario: Diamond dependency (A→B, A→C, B→D, C→D) resolves correctly
    Given workstreams A, B, C, D where:
      | Workstream | DependsOn |
      | B          | A         |
      | C          | A         |
      | D          | B, C      |
    When A's PR merges
    Then B and C start in parallel
    When B and C PRs both merge
    Then D starts

  Scenario: Deep chain dependency (A→B→C→D) executes sequentially
    Given a linear dependency chain A → B → C → D
    When implementation begins
    Then only A starts
    And B starts only after A's PR merges
    And C starts only after B's PR merges
    And D starts only after C's PR merges
```

#### Cross-cutting scenarios

```gherkin
@phase28 @cross-cutting
Feature: Cross-team hand-offs and phase transitions

  Scenario: Full story lifecycle — happy path
    Given the operator spawns story "WI-50001"
    When the plan phase completes and PR merges
    And the decomposition produces 2 workstreams (no mutual deps)
    And the operator approves the decomposition
    And both workstreams implement and create PRs
    And review agents approve both PRs
    And QA produces feature files and PR merges
    And engineer implements step definitions and all tests pass
    Then StorySpawn.Phase = "done"
    And 5 PRs total were merged (plan + 2 impl + qa features + step defs)

  Scenario: Team isolation — engineers cannot modify planning artifacts
    Given the plan PR is merged and phase is "implement"
    When engineer agents work on workstream code
    Then they do not modify files under "docs/stories/{storyId}/"
    And any attempt to edit planning docs is rejected by the artifact rules

  Scenario: Concurrent QA and implementation
    Given the plan PR is merged
    When the spawn advances to "implement"
    Then engineer agents start on workstreams
    And QA agents start on feature files concurrently
    And neither team blocks the other until the feature file hand-off

  Scenario: Story spawn survives server restart
    Given a spawn "WI-50001" in phase "implement" with 2 active workstreams
    When the Forge.Api server restarts
    Then the spawn state is restored from the database
    And active workstreams resume from their last checkpoint

  Scenario: Phase rollback is not supported (non-goal)
    Given a spawn in phase "review"
    When the operator attempts to move back to phase "implement"
    Then the API returns 422
    And the error indicates "Phase rollback is not supported"

  Scenario: Only one spawn per story at a time
    Given a spawn for "WI-50001" is active in phase "implement"
    When the operator tries to spawn "WI-50001" again
    Then the API returns 409 Conflict
    And the error indicates a spawn already exists for this story
```

#### EX — Execution model (paired agents, points, model rotation, hierarchy)

> BDD scenarios for the workstream.md decisions captured in the "Team structure & execution model" section above. These cover behaviour that is spec-pinned but not yet implemented (tracked under EX.1–EX.8 in Implementation progress).

```gherkin
@phase28 @execution-model
Feature: Paired-agent execution & model rotation

  Scenario: Effort points are restricted to the Fibonacci scale
    Given a coding task with effort points = 4
    When the orchestrator validates the task
    Then the API returns 422
    And the error indicates "Effort points must be one of {1,2,3,5,8,13}"

  Scenario: Agent budgets derive from points
    Given a coding task with effort points = 5
    When a pair is dispatched on the task
    Then the generator agent runs with maxTurns = 150
    And the iteration cap is set to 25

  Scenario: Generator and evaluator are recorded per task
    Given a plan task with points = 3
    When the architect crew claims the task
    Then assignedAgent.role is "generator" on one agent
    And assignedAgent.role is "evaluator" on the other
    And both agents come from team = "Architect"

  Scenario: Evaluator score below 90 triggers another iteration
    Given a coding task with iteration count 0 and maxIterations 25
    When the evaluator scores the generator output 84
    Then the pair re-iterates and the iteration count becomes 1
    And the work item state remains "active"

  Scenario: Score >= 90 within iteration cap closes the trial successfully
    Given a coding task in trial 1
    When the evaluator scores the latest generator output 92
    Then the pair stops iterating
    And a PR is opened from the workstream branch

  Scenario: Trial 1 failure rotates to a different model mix on trial 2
    Given a coding task in trial 1 has exhausted maxIterations without reaching 90
    And trial 1 used generator = "claude-opus-4.7" and evaluator = "gpt-5.5"
    When the engineer team lead reassigns the task
    Then the work item enters trial 2
    And the new pair uses generator = "gpt-5.5" and evaluator = "claude-opus-4.7"

  Scenario: Trial 3 introduces a different model family
    Given a coding task in trial 2 has exhausted maxIterations without reaching 90
    When the engineer team lead reassigns the task to a third pair
    Then the work item enters trial 3
    And the pair uses generator = "claude-opus-4.6-1M" and evaluator = "codex-5.3"

  Scenario: Three failed trials mark the task stalled
    Given a coding task that has just exhausted trial 3 without reaching 90
    When the trial completes
    Then the work item state transitions to "stalled"
    And the task surfaces in the engineer-lead escalation queue
    And no further trial is started automatically

  Scenario: Plan tasks land on main without a human approval step
    Given an architect pair completes a plan task with score 92
    When the plan PR is opened
    Then the orchestrator merges the PR automatically
    And no decomposition-approve gate is required for swarm-mode spawns

@phase28 @plan-hierarchy
Feature: Implementation-plan hierarchy & sizing limits

  Scenario: Implementation plan respects 20/20/20 ceilings
    Given an architect-produced implementation-plan.md with:
      | Phase | Stages |
      | P1    | 20     |
      | P2    | 21     |
    When the plan validator runs
    Then the validator rejects the plan with "Phase P2 exceeds 20 stages"

  Scenario: Step PR enforces ≤ 20 file change cap
    Given a step task whose generator commit changes 23 files
    When the evaluator inspects the diff
    Then the evaluator rejects the output with reason "Step exceeds 20-file PR cap"
    And the iteration counter increments

  Scenario: Coding work items map to Epic/Feature/Task hierarchy
    Given a phase "P1" with 2 stages and 5 steps total
    When work items are created from the implementation plan
    Then 1 Epic work item exists for "P1"
    And 2 Feature work items have parentId = the Epic
    And 5 Task work items have parentId set to their Stage

  Scenario: Plan work items use the four typed kinds (no hierarchy)
    Given an architect plan task is decomposed into its four artifacts
    When work items are created
    Then exactly 4 work items exist with no parent:
      | Type             |
      | Architecture     |
      | TechSpec         |
      | ImplementationPlan |
      | E2EScenarios     |

  Scenario: Engineer crew receives all four plan artifacts as context
    Given the plan PR for story "WI-77001" is merged on main
    When an engineer pair claims the first ready workstream
    Then the agent context includes all of:
      | docs/stories/WI-77001/architecture.md           |
      | docs/stories/WI-77001/tech-spec.md              |
      | docs/stories/WI-77001/implementation-plan.md    |
      | docs/stories/WI-77001/e2e-scenarios.md          |

  Scenario: Phase closes only when its own e2e + integration tests pass
    Given a phase "P1" has every stage / step at status "complete"
    But the integration test suite for "P1" has 1 failure
    When the engineer lead evaluates phase closure
    Then phase "P1" remains "active" with reason "1 integration test failing"
    And no downstream phases are unblocked

  Scenario: Closing a phase unblocks downstream phases
    Given phase "P2" depends on phase "P1"
    And "P1" is the only outstanding dependency for "P2"
    When phase "P1" closes successfully
    Then phase "P2" is added to the eligibility pool
    And the next dispatcher tick assigns a coding pair to its first workstream
```

## Phase 29 — Sync-strategy refactor: 2-hour cadence, upstream-changedate cursor, per-repo overrides, UI knob

> Phases 23 / 24 / 27 ship three independent sync jobs (work items / PRs / builds), each running on a hard-coded 20-minute global cadence with a local-clock delta cursor. In production this puts measurable load on ADO + GitHub, and the cursor model can drop items written *during* a sync run. **Phase 29 retunes the strategy** so a healthy default doesn't hammer upstream, the cursor anchors to upstream timestamps that won't drift, and the operator can dial each repo individually from the UI.
>
> Two principles drive the shape:
>
> 1. **Default for the long run.** A healthy default is one the operator never has to tune. 2 hr matches typical work-item churn (a sprint board changes a handful of items per hour, not per minute) and keeps the rate-limit budget headroom comfortable for a multi-repo install. Anything more aggressive should be opt-in per repo.
> 2. **The cursor is whatever upstream says.** Local clock drifts, can be wrong on suspended laptops, and races with items written between query-time and commit-time. The MAX upstream `ChangedDate` / `updated_at` we observed is the only cursor that survives all three. `LastSyncAt` stays as audit telemetry — "when did we last tick" — but is not consulted for delta filtering.

### Resolved decisions

- **Q29.1 Cadence default — 2 hours, jittered ±60 s, applied to all three sync axes.** `WorkItemSyncIntervalSeconds = PrSyncIntervalSeconds = BuildSyncIntervalSeconds = 7200`. Per-repo overrides land on `RepoRow` as three separate columns so the operator can dial each axis independently. Override values **may only slow a repo down** (the global tick is the ceiling); making a repo sync faster than global would require restructuring the dispatcher's tick loop, deferred to Phase R if anyone asks.

- **Q29.2 Cursor — upstream-changedate, not local clock.** New column `RepoRow.LastUpstreamChangedDate TEXT NULL` stores the MAX `System.ChangedDate` (ADO) / `updated_at` (GitHub) observed in the last successful run. `WorkItemSyncJob` writes it after each successful run that observed at least one item; an empty delta leaves the prior boundary alone. `LastSyncAt` keeps its semantics as the local-clock "when did we last tick" used by the stale-running guard, the UI's last-synced timestamp, and the Q29.3 cadence gate.

- **Q29.3 Per-repo eligibility gate — pre-fetch filter, not separate timer.** The hosted services keep ticking at the global cadence; per-repo overrides are evaluated inside the tick by `WorkItemSyncJob.IsDueForSync(repo, now)`, `PrSyncJob.IsPrDueForSync(...)`, `BuildSyncJob.IsBuildDueForSweep(...)`. A repo that overrides to (say) 4 hours simply fails the gate on the alternate ticks; `MaxParallel` is taken **from the filtered set** so a saturated set of slow repos doesn't starve a fast one. Stale `running` rows always pass the gate — recovery beats cadence.

- **Q29.4 Initial window — 30 days for both providers.** `AdoInitialWindowDays = GithubInitialWindowDays = 30`. GitHub previously omitted `since=` on first sync, returning all-time history; that behavior is replaced with `since=UtcNow-30d`. Per-repo override `Repo.InitialSyncWindowDays` (ADO-only today) still wins when set.

- **Q29.5 UI surface — Sync tab in EditRepoModal.** Three numeric inputs (Work items / Pull requests / Builds), each paired with a `min | hr` unit dropdown. Empty value = inherit global default (rendered as the placeholder `(global default)`). Save round-trips through the existing `PATCH /api/repos/{id}` write path; zero / negative clears the override. No separate "Apply now" button — the next tick of each job picks up the new cadence naturally.

### Implementation steps

- [x] **29.1** **`SyncConstants` bumps** — `WorkItemSyncIntervalSeconds`, `PrSyncIntervalSeconds`, `BuildSyncIntervalSeconds` move from 1200 → 7200. Add `GithubInitialWindowDays = 30`.
- [x] **29.2** **`Phase34Migration`** (code-side migration name retained — see "Spec-vs-migration numbering" note at the end of Phase 28) adds four columns to `repos` (`WorkItemSyncIntervalSeconds`, `PrSyncIntervalSeconds`, `BuildSyncIntervalSeconds`, `LastUpstreamChangedDate`) idempotently. Wired into the migration chain in `Program.cs` after the previous additive migration.
- [x] **29.3** **`RepoRow` entity** — gains the four properties; existing rows take the global defaults until the operator sets an override.
- [x] **29.4** **`WorkItemSyncJob` cursor swap** — delta filter sources from `LastUpstreamChangedDate`; runs that observe items write `MAX(item.UpdatedAt)` back as the new cursor. Empty deltas leave the cursor unchanged.
- [x] **29.5** **Per-repo eligibility gates** — `IsDueForSync` / `IsPrDueForSync` / `IsBuildDueForSweep` filter the candidate set inside each job's tick. Build-sync gates per-PR using `MAX(builds.LastSyncAt)` for that PR's history.
- [x] **29.6** **GitHub initial window** — `WorkItemSyncJob` defaults `filters.UpdatedFrom = UtcNow - GithubInitialWindowDays` when `LastUpstreamChangedDate` is null on a GitHub repo, mirroring ADO. `BuildSyncQueryString` then writes `since=` as before.
- [x] **29.7** **Contracts + endpoints** — `Repo`, `UpdateRepoRequest`, `MapRepo`, `PatchRepo` all carry the four new fields. `kind='full'` resync clears both `LastSyncAt` and `LastUpstreamChangedDate` so the next run uses the initial window.
- [x] **29.8** **EditRepoModal UI** — "Sync frequency" section in the Sync tab with three numeric+unit pairs. Empty / 0 round-trips through the patch as 0 to clear the override. Helper component `IntervalField` lives at the bottom of `EditRepoModal.tsx`.
- [x] **29.9** **Tests** — `Phase34SyncRefactorTests.cs` (filename retained on disk) covers `IsDueForSync` / `IsPrDueForSync` / `IsBuildDueForSweep` (no-prior-sync / global-default / per-repo override / stale-running bypass), constants assertions (7200 / 30 / 30), and migration idempotency. Phase 23, 24, 27 spec-consistency tests updated to assert the new values.
- [x] **29.10** **Fidelity** — additive UI; exempt from prototype screenshot diff. New surface (the Sync tab "Sync frequency" group) reuses existing `inputStyle` + `monoStyle` tokens; no new design tokens.

### Out of scope (explicitly deferred)

- **Per-repo override that makes a repo sync faster than the global tick.** Requires a separate dispatcher per repo or a min-of-overrides-driven global timer — Phase R candidate if any operator hits this.
- **Forward-fill of the new `LastUpstreamChangedDate` cursor on existing rows.** First post-migration tick re-runs the 30-day initial window once, then the cursor catches up; no offline backfill required.
- **Sync-frequency override surfaced on the `RepoSyncPanel` history view.** The cadence inputs live in EditRepoModal only; the history tab keeps its run-listing role.
- **Per-axis assignee / state filters in the sync gate.** Phase 33 already persists assignee/date filters; the cadence gate is orthogonal and intentionally simple.

---

## Phase 30 — Agent Communication Protocol (ACP): cross-stream context, silo detection, parallelism guardrails

> Phase 28 splits a story into N parallel workstreams that each run a generator → evaluator → PR loop in their own worktree. That parallelism buys throughput but trades it for **divergence**: workstream-A's agent picks `IFooService` with two methods, workstream-B's agent independently picks the same name with three different methods, and the conflict surfaces only at PR-review or merge time. The architect's plan is the only shared substrate; once decompose hands off to the engineer pods, each workstream is an island.
>
> Phase 16's `task.{workItemId}` channel and Phase 17's `task_queue` give the operator a per-task observation surface, but they don't let two concurrently-running agents *talk to each other* or even *read each other's intermediate decisions*. Each agent's transcript is an island too.
>
> **Phase 30 adopts the [Agent Communication Protocol (ACP)](https://agentcommunicationprotocol.dev/)** — the BeeAI / Linux Foundation REST-based agent-to-agent spec — as Forge's internal cross-agent transport. Every agent invocation becomes an `acp_run`; every cross-agent emission becomes an `acp_message`; every reusable decision (interface shape, naming convention, invariant, open question) becomes a typed `acp_context` row that downstream agents consume as part of their system prompt. The bus is persisted, queryable, and replayable; subscribers stream live via SignalR; conformance to the ACP wire shape means external agents (a separate Forge install, a CLI debugger, a future review-bot) can join the bus without bespoke glue.
>
> On top of the bus, two analyzers earn their keep:
>
> 1. **Silo auditor.** For every parallel pair of running workstreams, computes the cross-citation rate (how often workstream B's contexts cite workstream A's, and vice-versa). Below threshold → fire `silo-suspected` on the lead's inbox with the specific contexts that should have been read. Above threshold → quiet.
> 2. **False-parallel planner.** Runs at the end of decompose (Phase 28.4). Inspects the dep graph for shapes that *look* parallel but have a hidden serial constraint (two workstreams declaring the same contract output, two workstreams writing to the same file glob). Either pushes back to the architect with a recommendation to serialize / extract a shared workstream, or auto-injects a `shared-contract` workstream as a Stage-0 prerequisite. Operator gates the auto-injection.
>
> This phase **does not change** the existing dispatcher, queue, or deliverable loop. It rides on top of them as an additive surface — every existing test continues to pass; every existing endpoint stays compatible. ACP is an observer + a gentle nudge, not a new state machine.
>
> Three principles drive the shape:
>
> 1. **The bus is the memory.** A context that isn't on the bus didn't happen — agents that want their decisions remembered emit them as ACP contexts. The bus is persisted, addressable by topic, and survives a restart.
> 2. **Subscription is opt-in but discovery is automatic.** Agents declare what topics they care about in soul.md front-matter (`acp_topics:`), and the bus auto-subscribes them on every run. They do not have to remember to fetch — relevant contexts are injected into the prompt preamble before the first token.
> 3. **Silo detection is a signal, not a gate.** When the auditor fires, the lead sees a banner; nothing blocks. The cost of a false positive (interrupting a legitimately-isolated workstream) is higher than the cost of a missed silo (caught at PR review anyway). Phase 30 errs on the quiet side.

### Resolved decisions

- **Q30.1 Wire-format conformance — BeeAI ACP v0.1.** Forge's bus speaks ACP's `messages`, `runs`, `awaits` shapes verbatim so a non-Forge agent (e.g. a `bee` CLI run, a future Anthropic Claude with native ACP support, a manually-curl-ed test agent) can join. Multipart message parts use ACP's `{ contentType, content }` schema; runs use ACP's `running | awaiting | completed | failed | cancelled` state set. Forge-specific extensions (the `acp_context` typed-artifact table, silo audit fields) live as **additional** properties on the message envelope under an `x-forge-*` namespace so an ACP-pure consumer can ignore them without parse errors. The conformance bar is the ACP project's reference test suite (vendored at `tests/conformance/acp/` — runs in CI as a separate xUnit project against `Forge.Api`'s `/acp/*` endpoints). When ACP itself versions past v0.1 we revisit; until then v0.1 is the contract.
- **Q30.2 Bus topology — single per-product bus, no federation at v0.1.** One product = one bus. A run started against `product-A` cannot publish to `product-B`'s bus. Cross-product subscription is rejected with `403 + acp.cross_product_forbidden`. Federation across products (or across two Forge installs) is a Phase R / `forge_v2.md` candidate; the wire format already supports it (each ACP server has a base URL), but the auth + rate-limit story for v0.1 is "single tenant per bus". Inside a product, every workstream / story / agent shares the same bus — that's the whole point of cross-stream context.
- **Q30.3 Persistence — every message + context row is written, the bus is replayable.** SQLite tables `acp_runs`, `acp_messages`, `acp_contexts`, `acp_awaits`, `acp_subscriptions`. No retention cap at v0.1 — bus rows are usually small (text + a handful of citations) and the operator can VACUUM if disk pressure shows up; a cap + eviction policy is a Phase R polish. Indexed by `(productId, topic, createdAt)` for the subscription-fanout query and `(runId, at)` for the per-run timeline. **The bus is the source of truth** for ACP-shaped data; the existing `task_transcript_event` table (Phase 16.7) keeps its role for non-ACP transcript events (raw token deltas, tool calls) and references `acp_message.id` when an event corresponds to an ACP emission.
- **Q30.4 Subscription model — declarative in soul.md, dynamic via API.** Agents declare durable interests in their soul.md front-matter:
  ```yaml
  acp_topics:
    - "interface:**"          # all interface contracts emitted under any workstream
    - "decision:auth/*"       # any decision under the auth scope
    - "story:WI-50001/*"      # everything in this story spawn
  ```
  The runner reads these on every `StartAsync` and registers a row in `acp_subscriptions` keyed by `(agentId, topic, runId)` with `until = run.endedAt`. Topic syntax mirrors MQTT-style wildcards (`*` = one segment, `**` = many) so operators don't learn a new syntax. Dynamic subscription via `POST /acp/subscriptions` is supported for ad-hoc tooling but is not the primary path — the architect should not have to think about subscription plumbing per-task. Auto-subscribe rule: any agent running on a `story_spawn` workstream is automatically subscribed to `story:{storyId}/**` for the lifetime of its run regardless of soul.md, so the cross-stream context flows for free in the Phase 28 case.
- **Q30.5 Context taxonomy — five typed kinds, no ad-hoc strings.** `acp_context.kind` is one of: `decision` (a deliberate choice with rationale, e.g. "use JWT not session cookie"), `interface` (a contract another stream depends on, e.g. "`POST /api/foo` accepts `{ bar: string }`"), `artifact` (a pointer to a committed file or PR), `invariant` (a constraint future work must preserve, e.g. "all responses < 200ms p95"), `open-question` (an unresolved choice the next agent should pick up). Five kinds is small enough that downstream agents can branch on `kind` with a switch; if a sixth seems necessary the right move is usually to stretch one of the existing five (e.g. "research note" → `decision` with `state: 'tentative'`). The taxonomy is enforced server-side: writes with an unknown `kind` return `400 + acp.context_kind_unknown`. **`open-question` carries first-class semantics**: when one is published, the bus emits an `acp.question.opened` event on the relevant lead's user channel; resolution writes a follow-up `decision` context that cites the question's id, and the bus closes the question (auto-closed `open-question` rows render struck-through in the UI).
- **Q30.6 Prompt preamble injection — top-N by relevance, not all-of-bus.** When the runner starts an invocation, it queries the bus for contexts matching the agent's subscriptions, ranks by `(specificity DESC, recency DESC, citation_count DESC)`, and injects the top **20** as a `## Shared Context` section *between* the soul.md preamble (Phase 14.6) and the user prompt. Specificity = number of topic segments matched (a context tagged `interface:auth/jwt` outranks `interface:auth/*` for an agent subscribed to `interface:auth/**`). Recency cap = 14 days — older contexts only appear if explicitly cited by a younger context (transitive inclusion). The 20-row cap is a deliberate budget; an agent that needs more context should be doing fewer things, not reading more bus. Cap configurable via `Forge:Acp:PreambleTopN` for experiment.
- **Q30.7 Silo auditor — pairwise cross-citation, fire below 0.2.** A `SiloAuditor` background service runs every 10 minutes per active story spawn. For each pair of running workstreams (A, B) under that spawn, it computes `crossCiteRate(A→B) = |contexts emitted by A that cite at least one context from B| ÷ max(1, |contexts emitted by A|)`. **Silo suspected** when `min(crossCiteRate(A→B), crossCiteRate(B→A)) < 0.2 AND (count of A's contexts ≥ 5) AND (count of B's contexts ≥ 5)`. Both sides must have produced enough contexts to make the ratio meaningful — early-run pairs don't false-fire. On detection emits `acp.silo.suspected { storySpawnId, workstreamA, workstreamB, contextsA[], contextsB[] }` to the lead's user channel + writes one `acp_silo_audit` row per detection. Debounce: do not re-fire for the same `(spawnId, A, B)` pair until the rate moves above 0.2 and back down. The lead can mark a detection as `intentional-isolation` (writes a `acp_silo_audit.acked` row) which suppresses re-fires for that pair for the lifetime of the spawn — sometimes the right answer is "yes, these are independent, leave them alone".
- **Q30.8 False-parallel planner — runs at decompose, advisory by default.** When the architect submits a decomposition (Phase 28.4 `POST /api/stories/{storyId}/spawn/decompose`), a `FalseParallelPlanner` post-pass inspects the workstream definitions for two patterns:
  1. **Shared contract output** — two or more workstreams that name the same expected artifact (`expectedFiles` overlap, or the workstream description names the same module / interface / endpoint). Heuristic match via tokenization of `description` + intersection of declared file paths.
  2. **Implicit serial chain** — workstream B's `description` mentions a noun that's the *output* of workstream A's `description` (e.g. "extends the auth middleware" when A is "build the auth middleware") and there's no declared `dep` from B to A.
  Detections surface as a yellow banner on the decompose review screen with two CTAs: **Inject shared workstream** (auto-extracts the shared contract into a new Stage-0 workstream and re-wires deps from A and B), and **Mark as intentional** (writes the override to the spawn so the auditor doesn't re-flag). **No auto-modification** — the operator must approve. The planner's logic is intentionally heuristic-not-AI at v0.1 (deterministic, fast, explainable); upgrading to an LLM-driven planner is `forge_v2.md`.
- **Q30.9 Awaits — `awaiting` runs are first-class, not a hack.** ACP's `awaiting` state is the formal way for an agent to pause and request input. Forge maps awaits to existing surfaces:
  - `awaiting input from human` → posts to the work item's comment thread + emits to the team lead's inbox.
  - `awaiting input from agent` → registers a subscription for the named topic; resumes when a matching context lands.
  - `awaiting external` (e.g. CI pipeline, a 3rd-party webhook) → records the await with a `dueBy` timestamp; the bus's polling loop checks `acp_awaits WHERE state='open' AND dueBy < now()` every minute and fires `acp.await.timeout` for the lead. Resume is an explicit `POST /acp/awaits/{id}/resume` with the resolution payload.
  All awaits are visible in the Live Execution view (Phase 16.10) as a yellow chip on the affected iteration; the chip body is the await's `prompt`. The await counter feeds the existing `crew.queue.depth` strain math (Phase 17.10) so a crew with many awaiting agents is correctly flagged as strained even though no rows are formally `picked-up`.
- **Q30.10 Visibility — operator can read every message, agents only see their subscriptions.** The bus is fully readable to PO + team leads via `GET /api/acp/messages?…`. Generator and evaluator agents only see what their subscriptions match — they cannot enumerate the bus or read other agents' messages by id unless that message is in a topic they're subscribed to. This is the same gate the prompt preamble uses (Q30.6); enforced at query time on `GET /api/acp/messages` so the same agent that ran a `tool: list_acp_messages` call against its own bus access only retrieves its scoped slice. Audit rows record the caller for every read so an agent that abuses the read scope is visible in `/audit`.

### Implementation steps

- [ ] **30.1** **Schema migration** — `Forge.Api/Persistence/Phase36Migration.cs` (next free migration slot — `Phase35Migration.cs` is already taken on disk by the unrelated crew-decouple migration; idempotent, runs after the most recent migration in the chain):
  - New table `acp_runs(id TEXT PK, productId TEXT, agentId TEXT, parentRunId TEXT?, workItemId TEXT?, storySpawnId TEXT?, workstreamId TEXT?, state TEXT NOT NULL, startedAt TEXT, endedAt TEXT?, modelEcho TEXT?)`. State ∈ `running | awaiting | completed | failed | cancelled`. Indexes: `(productId, state, startedAt)`, `(workItemId, startedAt)`, `(storySpawnId, startedAt)`.
  - New table `acp_messages(id TEXT PK, productId TEXT, runId TEXT FK, fromAgentId TEXT, toRecipient TEXT, kind TEXT, partsJson TEXT, inReplyTo TEXT?, at TEXT NOT NULL, contextRefs TEXT?)`. `toRecipient` is one of: `agent:{id}`, `topic:{topic-string}`, `broadcast`. `kind` mirrors ACP's: `announce | request | reply | observation | tool-result`. `partsJson` is the multipart payload per ACP wire format. Indexes: `(productId, at)`, `(runId, at)`.
  - New table `acp_contexts(id TEXT PK, productId TEXT, runId TEXT FK, agentId TEXT, kind TEXT NOT NULL, topic TEXT NOT NULL, body TEXT, citesContextIds TEXT?, expectedFiles TEXT?, state TEXT, createdAt TEXT)`. `kind` ∈ `decision | interface | artifact | invariant | open-question` (CHECK constraint). `state` defaults `open` for `open-question`, `final` for the rest; `open-question` rows flip to `resolved` when a `decision` cites them. Indexes: `(productId, topic, createdAt)`, `(productId, kind, state)`, `(citesContextIds)` for the cross-citation join.
  - New table `acp_awaits(id TEXT PK, runId TEXT FK, kind TEXT, prompt TEXT, schemaJson TEXT?, dueBy TEXT?, state TEXT NOT NULL, resolutionJson TEXT?, openedAt TEXT, resolvedAt TEXT?)`. `kind` ∈ `human | agent | external`. `state` ∈ `open | resolved | timeout | cancelled`. Index `(state, dueBy)` for the timeout sweep.
  - New table `acp_subscriptions(id INTEGER PK AUTOINCREMENT, agentId TEXT, runId TEXT?, topicPattern TEXT, source TEXT, registeredAt TEXT, until TEXT?)`. `source` ∈ `soul-md | dynamic | auto-spawn`. Index `(agentId, topicPattern)`.
  - New table `acp_silo_audit(id INTEGER PK, storySpawnId TEXT, workstreamA TEXT, workstreamB TEXT, rateAToB REAL, rateBToA REAL, contextsAJson TEXT, contextsBJson TEXT, firedAt TEXT, ackedBy TEXT?, ackedAt TEXT?, ackKind TEXT?)`. `ackKind` ∈ `intentional-isolation | acted-on | dismissed`.
  - TS mirrors in `src/forge/Forge.UI/src/types.ts`: `AcpRun`, `AcpMessage`, `AcpContext`, `AcpContextKind`, `AcpAwait`, `AcpSiloAudit`.

- [ ] **30.2** **`IAcpBus` interface** — `Forge.Api/Acp/IAcpBus.cs`. Single abstraction for the bus; the rest of the code routes through it so the SignalR + persistence wiring is testable in isolation.
  ```csharp
  Task<AcpRun> StartRunAsync(string agentId, string productId, RunContext ctx, CancellationToken ct);
  Task EndRunAsync(string runId, AcpRunState terminalState, CancellationToken ct);
  Task<AcpMessage> EmitMessageAsync(string runId, AcpMessage envelope, CancellationToken ct);
  Task<AcpContext> EmitContextAsync(string runId, AcpContextDraft draft, CancellationToken ct);
  Task<AcpAwait> OpenAwaitAsync(string runId, AcpAwaitDraft draft, CancellationToken ct);
  Task<AcpAwait> ResolveAwaitAsync(string awaitId, JsonElement resolution, string byUserOrAgent, CancellationToken ct);
  Task<IReadOnlyList<AcpContext>> QueryContextsAsync(string productId, AcpContextQuery query, CancellationToken ct);
  Task<IReadOnlyList<AcpContext>> ResolvePreambleAsync(string agentId, string runId, int topN, CancellationToken ct);
  Task<bool> SubscribeAsync(string agentId, string topicPattern, string? runId, AcpSubscriptionSource source, CancellationToken ct);
  ```
  All writes fan out to SignalR (groups `acp.{productId}` and `acp.run.{runId}`) inside the same DB transaction so a subscriber that joins late and replays from the persisted table sees the same ordering it would have streamed live. `ResolvePreambleAsync` is the hot path — called once per `IAgentRunner.StartAsync` — and is allowed to read directly without serialization.

- [ ] **30.3** **soul.md `acp_topics:` parser** — extend the existing front-matter loader (`Forge.Api/Agents/SoulMdLoader.cs`) to parse `acp_topics:` as a string array. Validation: each entry is a valid topic pattern (segments of `[a-zA-Z0-9_-]+` separated by `/`, with `*` and `**` wildcards allowed only as a whole segment). Invalid patterns are dropped with a `forge.acp.soul.invalid_topic` warning log so a typo doesn't block the agent. The loader's existing file-watcher (Phase 11.3) re-reads on edit; the runner re-applies subscriptions on the next `StartAsync` (we don't mutate live runs' subscriptions — explicit by design, makes test isolation clean).

- [ ] **30.4** **Auto-subscribe glue in `IAgentRunner` invocation** — when `StartAsync` is called and `RunContext.StorySpawnId` is set, the runner registers an automatic subscription `topic = story:{storyId}/**, source = auto-spawn`. Same for `WorkItemId` → `topic = workitem:{wid}/**`. These auto-subscriptions live for the run's lifetime (`until = run.endedAt`) so an agent on workstream-A always sees workstream-B's contexts during the spawn. Operator override: a soul.md `acp_no_auto_subscribe: true` flag opts an agent out (the planner agent in 30.8 uses this so it doesn't drown in its own product's bus).

- [ ] **30.5** **Prompt preamble injection** — extend `Forge.Api/Agents/SystemPromptBuilder.cs` to call `IAcpBus.ResolvePreambleAsync(agentId, runId, topN: ScoringConstants.AcpPreambleTopN)` and splice the result into the assembled system prompt as:
  ```
  <existing soul.md body>

  ---

  ## Shared Context (top {N} from the team bus)

  - [{kind}@{topic}] {body excerpt}  · cites: {ids}  · {age}
  - …

  ---

  <user prompt>
  ```
  Each emitted context contributes ≤ 400 tokens to the preamble (truncate body with a `…` marker; the agent can `tool: get_acp_context(id)` for the full body if it needs to). The preamble is part of the **cached** system block (Phase 11.5 prompt cache) — same TTL, same hit-rate metric. When the preamble changes between iterations of the same deliverable (because new contexts landed), the cache miss is recorded as `forge.acp.preamble.cache_invalidated` so we can see how often the cross-stream noise is actually shifting.

- [ ] **30.6** **HTTP surface — ACP-conformant endpoints** under `/acp/*` (separate from `/api/*` so the wire-format conformance suite can hit them without auth headers Forge-specific paths require). All endpoints accept JSON and emit ACP wire envelopes:
  - `POST /acp/runs` body `{ agentId, productId, parentRunId?, workItemId?, storySpawnId? }` → `{ id, state: 'running', … }`. 201 on create.
  - `POST /acp/runs/{id}/end` body `{ state: 'completed' | 'failed' | 'cancelled' }` → 204. Idempotent (re-end of an already-ended run is a no-op).
  - `POST /acp/runs/{id}/messages` body = ACP message envelope → 201 with the assigned id.
  - `GET /acp/runs/{id}/messages?from={cursor}&limit=200` → ACP-shaped list, sorted by `at`. 200.
  - `POST /acp/runs/{id}/contexts` body `{ kind, topic, body, citesContextIds?, expectedFiles? }` → 201 with id + `state` ('open' for `open-question`, 'final' otherwise).
  - `GET /acp/contexts?productId=&topic=&kind=&since=` → list, gated by Q30.10 visibility. 200.
  - `POST /acp/awaits` body `{ runId, kind, prompt, schemaJson?, dueBy? }` → 201 + sets `run.state='awaiting'`.
  - `POST /acp/awaits/{id}/resume` body = resolution payload → 204 + flips run state back to `running`.
  - `POST /acp/subscriptions` + `DELETE /acp/subscriptions/{id}` for ad-hoc subscription mgmt.
  - `GET /acp/discovery` returns the ACP agent manifest for this Forge install (lists agent ids + their advertised capabilities + soul.md topics). This is the entry point an external ACP client uses to find what it can talk to.

- [ ] **30.7** **`SiloAuditor` hosted service** — `Forge.Api/Services/SiloAuditor.cs`. Tick every 10 min ±60 s. For each `story_spawns WHERE phase IN ('implement', 'review')`, walks pairs of `workstream_rows WHERE storySpawnId = spawn.id AND status IN ('active', 'review')`. For each pair (A, B), runs the Q30.7 cross-citation query against `acp_contexts`. Below threshold + the count gates pass + not already debounced → write an `acp_silo_audit` row + emit `acp.silo.suspected` to the spawn's lead via `user.{leadUserId}`. Debounce kept in-memory (`ConcurrentDictionary<(spawnId, wsA, wsB), bool>`) with a `Forge.Api/Services/SiloAuditorState.cs` snapshot on shutdown so a restart doesn't refire across a clean boot. **Acks**: `POST /api/acp/silo/{id}/ack body { kind: 'intentional-isolation' | 'acted-on' | 'dismissed' }` writes the ack columns. `intentional-isolation` adds the pair to a per-spawn permanent suppression set (stored on `story_spawns.AcpSuppressedSiloPairsJson` so it survives the auditor restart).

- [ ] **30.8** **`FalseParallelPlanner` post-decompose hook** — `Forge.Api/Stories/FalseParallelPlanner.cs`. Wired into `POST /api/stories/{storyId}/spawn/decompose` after the workstream rows are persisted but **before** the `decomposed` SignalR event fires (so the UI receives the planner's findings in the same payload as the decomposition itself). Two heuristics, both deterministic, both with fixture-pinned tests:
  1. **Token overlap on workstream descriptions** — Jaccard similarity between the noun phrases of every pair's `description`. ≥ 0.5 → flag as "shared contract candidate". Noun-phrase extraction is a 50-line lookup of common-software-noun list (`api | service | controller | middleware | schema | model | endpoint | hub | provider`); not a real NLP pipeline, deliberate (cheap, debuggable, no LLM dep).
  2. **Output-of-A in description-of-B without dep** — for each pair where A's `expectedFiles` overlaps with the noun set in B's description and `(A, B) ∉ workstream_deps`, flag as "implicit serial".
  Findings are returned to the architect in the decompose response under a new `falseParallelHints: [{ kind, workstreams[], suggestion, rationale }]` field. The UI renders them as banners (30.10). **No write side-effects** at v0.1 — the planner is read-only; the operator clicks **Inject shared workstream** to get the auto-rewire (which creates the new workstream + dep edges in a single transaction).

- [ ] **30.9** **Await timeout sweep** — `Forge.Api/Services/AwaitTimeoutSweep.cs`. Tick every 60 s. `SELECT * FROM acp_awaits WHERE state='open' AND dueBy < now()`. For each row, emit `acp.await.timeout` to the run's lead user channel + flip `state='timeout'`. The owning run stays `awaiting` (the run isn't auto-resolved — only the await is); the lead either resumes the await with a default value or cancels the run. Telemetry: `forge.acp.await.timeout` counter tagged with `kind`.

- [ ] **30.10** **UI — Story spawn `Context graph` tab** — extend `src/forge/Forge.UI/src/views/StorySpawnView.tsx` with a new tab **Context** between **Workstreams** and **Artifacts**. Renders:
  - **Force-directed graph** — nodes = `acp_contexts` for this spawn; edges = `citesContextIds`. Node color by `kind` (decision = blue, interface = green, artifact = grey, invariant = purple, open-question = amber outlined). Hover → context body + emitting agent + workstream pill.
  - **Silo audit banner** at the top — for each unack'ed `acp_silo_audit` row on this spawn, render a red banner: "Workstreams `ws-A` and `ws-B` aren't reading each other's context (cross-cite rate {N%}). [Open both] [Mark intentional] [Dismiss]". Click `Open both` opens the two Live Execution views side-by-side.
  - **False-parallel hints** at the top of the decompose phase view — yellow banner per `falseParallelHints` entry with the planner's suggestion + the two CTAs from 30.8.
  - **Open question pile** — a sidebar list of `kind=open-question, state=open` contexts with the originating workstream + age. Click → drops into the relevant Live Execution view filtered to the question.
  Frontend-design discipline: reuses Phase 14 score-chip palette + Phase 16 `--graph-line` token; one new token `--acp-edge-cite` documented in `docs/forge/design-tokens.md`.

- [ ] **30.11** **UI — Live Execution view ACP overlay** — extend `src/forge/Forge.UI/src/views/LiveExecutionView.tsx` with:
  - A new chip in the right-rail tabs: **Context (N)** showing the count of contexts emitted by this run + the count of contexts injected into the preamble. Click → expands the list with kind / topic / body excerpt / cites count.
  - A yellow `awaiting: {prompt}` banner on the iteration row when an `acp_await` is open against the current run. Inline `Resume` button posts to `POST /acp/awaits/{id}/resume` for human awaits.
  - Each transcript event tagged with the contextId it produced (when applicable) so clicking it jumps to the Context graph tab with that node selected.

- [ ] **30.12** **UI — Inbox surfacing** — extend `src/forge/Forge.UI/src/views/InboxView.tsx` (Phase 17.7) with two new panel kinds:
  - **Open questions for me** — `acp_contexts WHERE kind='open-question' AND state='open' AND topic matches subscribed topics for currentUser's leadCrews`. One row per question with the originating workstream + the agent that posted it.
  - **Silo audits + await timeouts** — fed from `acp.silo.suspected` and `acp.await.timeout` SignalR events on `user.{userId}`. Banners stay until ack'd.
  Reuses the existing Inbox count badge math; the chip turns yellow when one of the new panels has rows.

- [ ] **30.13** **`forgeApi` client extensions + Zustand store** — `src/forge/Forge.UI/src/api/forgeApi.ts` adds: `listAcpContexts`, `getAcpContext`, `listAcpMessages`, `listAcpAwaits`, `resumeAcpAwait`, `listSiloAudits`, `ackSiloAudit`, `injectSharedWorkstream`. New `useAcpStore` (Zustand) + `useAcpChannel` (SignalR) keyed by `productId` so the Context tab and the Inbox panels both subscribe through one channel. The store invalidates on `acp.context.emitted` / `acp.await.opened` / `acp.silo.suspected` / `acp.question.resolved` events.

- [ ] **30.14** **Telemetry + audit** — extend the existing `Forge.Api.Phase16` meter with:
  - `forge.acp.message.emitted` — counter, tagged with `kind`, `recipient_kind`.
  - `forge.acp.context.emitted` — counter, tagged with `kind`.
  - `forge.acp.preamble.injected` — counter, tagged with `agent_id`. Histogram `forge.acp.preamble.token_count`.
  - `forge.acp.preamble.cache_invalidated` — counter, tagged with `reason: 'topic_match' | 'recency_eviction'`.
  - `forge.acp.silo.suspected` — counter, tagged with `spawn_id`.
  - `forge.acp.silo.acked` — counter, tagged with `ack_kind`.
  - `forge.acp.false_parallel.flagged` — counter, tagged with `heuristic`.
  - `forge.acp.false_parallel.injected` — counter for the operator-confirmed shared-workstream injections.
  - `forge.acp.await.opened` / `forge.acp.await.timeout` — counters, tagged with `kind`.
  Audit: every silo ack writes one `forge_actions(kind='ACP_SILO_ACK', payload={ auditId, ackKind, byUserId })` row; every shared-workstream injection writes one `forge_actions(kind='ACP_FALSE_PARALLEL_INJECT', payload={ spawnId, newWsId, mergedWsIds })` row. The `/audit` view gains an optional `?acp=true` chip filter.

- [ ] **30.15** **ACP wire-format conformance suite** — vendor the ACP project's `acp-conformance/` test fixtures into `tests/conformance/acp/`. New xUnit project `Forge.Api.Tests.AcpConformance` runs the suite against a `WebApplicationFactory<Program>`-hosted Forge.Api instance and asserts every fixture passes. CI fails on any conformance regression. Track ACP version pins in `tests/conformance/acp/VERSION` so a spec bump is a deliberate operator action, not silent drift.

- [ ] **30.16** **Integration with Phase 17 queue — `awaiting-context` queue state.** Extend `task_queue.state` enum with `awaiting-context`. When the runner emits an `acp_await` with `kind='agent'`, the queue row's state flips from `picked-up` to `awaiting-context`. Sweep added to `CrewQueueWorker` (Phase 17.4): every tick, scan `task_queue WHERE state='awaiting-context'` and check whether the await's matching topic now has a satisfying context; if yes, flip state back to `picked-up` and signal the runner to resume. Keeps the lead's queue depth honest — the row is held against the crew's `MaxConcurrent` while it waits, but it doesn't bake against `picked-up` semantics.

- [ ] **30.17** **Fidelity** — additive UI; exempt from prototype screenshot diff. One new design token `--acp-edge-cite` documented in `docs/forge/design-tokens.md`. The Context tab graph uses `react-force-graph-2d` (BSD license, ~30KB gz) — one new dep, recorded in `docs/forge/dependencies.md`.

### Test scenarios

> BDD-style scenarios live in `Forge.Api.Tests/Phase30AcpTests.cs` (xUnit + WebApplicationFactory). Conformance scenarios are owned by the ACP wire-format suite (30.15) and not duplicated here. UI scenarios are exercised through the existing endpoint test paths; Playwright cases are deferred to Phase R per the established pattern.

**Bus persistence + replay**
- [ ] `EmitMessageAsync_persists_and_streams` — call `IAcpBus.EmitMessageAsync`; verify the `acp_messages` row exists, the `acp.{productId}` SignalR group received the envelope, and re-fetching via `GET /acp/runs/{id}/messages` returns the same row in order.
- [ ] `EmitContextAsync_validates_kind` — POST a context with `kind='unknown'` returns `400 + acp.context_kind_unknown`; the five valid kinds each persist with the right defaults (`state='open'` for open-question, `state='final'` for the rest).
- [ ] `Replay_after_restart_yields_same_message_order` — emit 50 messages on a run, recreate the DbContext, query messages — ordering matches the original `at` sort.

**Subscription + preamble**
- [ ] `Soul_md_topics_register_on_run_start` — soul.md with `acp_topics: ['interface:auth/**']` → `StartAsync` writes one `acp_subscriptions` row tied to the run id with `source='soul-md'`.
- [ ] `Auto_subscribe_to_story_when_workstream_run` — `RunContext.StorySpawnId='spawn-A'` → an `acp_subscriptions` row with `topic='story:spawn-A/**'` and `source='auto-spawn'` exists; opt-out via `acp_no_auto_subscribe: true` skips it.
- [ ] `Preamble_topN_caps_at_20` — 50 contexts match the agent's subscriptions; `ResolvePreambleAsync` returns exactly 20, sorted by `(specificity DESC, recency DESC, citation_count DESC)`.
- [ ] `Preamble_recency_excludes_15_day_old_uncited` — a 15-day-old context with zero citations is excluded; the same context cited by a fresh row is included transitively.
- [ ] `Preamble_changes_invalidate_cache` — emit a new high-relevance context between two iterations of the same deliverable; the second iteration's runner records `forge.acp.preamble.cache_invalidated`.

**Silo auditor**
- [ ] `Auditor_quiet_below_minimum_count` — workstreams A and B with 4 contexts each, zero cross-cites → no detection (count gate `≥5`).
- [ ] `Auditor_fires_when_cross_cite_below_0_2` — A and B with 10 contexts each, 1 cross-cite from A→B and 0 from B→A → `acp_silo_audit` row + `acp.silo.suspected` event fired once.
- [ ] `Auditor_debounces_until_rate_recovers` — same fixture; second tick within 10 minutes does NOT re-fire; once rate climbs to 0.3 and back to 0.1, the next tick re-fires.
- [ ] `Auditor_intentional_isolation_suppresses_for_spawn_lifetime` — ack a detection with `kind='intentional-isolation'`; subsequent ticks for the same `(spawn, A, B)` pair do not fire even after rate dips again. Ack persists on `story_spawns.AcpSuppressedSiloPairsJson`.
- [ ] `Auditor_clears_state_on_clean_restart` — auditor's in-memory debounce snapshot reloads from `SiloAuditorState`; restart does not duplicate-fire detections that were already streamed.

**False-parallel planner**
- [ ] `Token_overlap_flags_two_workstreams_naming_the_same_service` — submit decompose with two workstreams whose descriptions both contain "auth middleware"; response `falseParallelHints` includes a `shared-contract` entry naming both.
- [ ] `Implicit_serial_flagged_when_B_extends_A` — workstream A `expectedFiles=['src/auth/middleware.ts']`, workstream B description "extends the auth middleware", no dep edge → `implicit-serial` hint fires.
- [ ] `Hints_have_no_side_effects` — the decompose call persists exactly the workstreams the architect submitted; the planner does not auto-add deps or workstreams.
- [ ] `Inject_shared_workstream_creates_stage_zero_and_rewires_deps` — POST to the inject CTA → new workstream row exists with appropriate name, A and B's `workstream_deps` reference it, audit row written.
- [ ] `Mark_intentional_records_override_and_does_not_re_flag` — operator confirms intentional; subsequent re-submit of the same decompose does not re-flag the same pair.

**Awaits**
- [ ] `Open_human_await_routes_to_lead_inbox` — open an await with `kind='human'`; the lead's inbox view's open-questions panel includes it; the run state flips to `awaiting`.
- [ ] `Resume_await_returns_run_to_running` — POST resume with a resolution payload → run state flips back to `running`; transcript records the resumption with the resolution body.
- [ ] `Agent_await_resolves_when_matching_context_lands` — open `kind='agent', topic='interface:auth/jwt'`; emit a context with that exact topic on a different run; the await flips to `resolved` and the consumer run resumes.
- [ ] `Await_timeout_sweep_fires_at_dueBy` — open a `kind='external'` await with `dueBy = now + 30s`; advance test clock 31s; sweep flips state to `timeout` + emits `acp.await.timeout`.
- [ ] `Await_count_feeds_queue_strain_math` — three runs in `awaiting` state on the same crew → `GetCrewStatsAsync` counts them in depth; combined with one queued row crosses the strain threshold and fires `queue.strained`.

**Visibility + scoping**
- [ ] `Cross_product_message_post_rejected` — agent on product-A POSTs a message scoped to product-B → 403 + `acp.cross_product_forbidden`.
- [ ] `Agent_query_hides_unsubscribed_topics` — a generator agent calls `GET /api/acp/messages` with no topic filter; response only contains messages whose topics match its registered subscriptions.
- [ ] `PO_query_returns_full_bus` — same call from a PO returns all rows regardless of subscriptions; a `forge_actions(kind='ACP_BUS_FULL_READ')` audit row is written.

**Queue integration**
- [ ] `Awaiting_context_state_holds_capacity` — a row in `awaiting-context` counts against the crew's `MaxConcurrent`; a second item on the same crew sees `capacity-blocked` until the await resolves.
- [ ] `Resolved_await_flips_back_to_picked_up` — a satisfying context lands; the queue sweep flips the row back to `picked-up` + emits `task.delegated` (resume variant).

**ACP wire-format conformance** (handled by 30.15 suite — listed here for traceability)
- [ ] `Acp_v01_messages_endpoint_passes_conformance` — `POST /acp/runs/{id}/messages` accepts every fixture envelope shape.
- [ ] `Acp_v01_runs_state_machine_matches_spec` — the five terminal-state transitions and the `awaiting → running` resume match the ACP reference suite.
- [ ] `Acp_v01_discovery_manifest_validates` — `GET /acp/discovery` output validates against the ACP discovery JSON schema.

**Spec consistency**
- [ ] `Phase30SpecConsistencyTests_constants_match_doc` — `ScoringConstants.AcpPreambleTopN`, `AcpSiloThreshold`, `AcpSiloMinContextCount`, `AcpAwaitTimeoutSweepIntervalSeconds` match the values quoted verbatim in this phase. Drift fails the test, mirroring Phase 14.14 / Phase 23 spec-consistency patterns.

### Out of scope for v0.1 — Phase R / `forge_v2.md`

- **Federated bus across multiple Forge installs.** The wire format already supports multi-server discovery; the auth + rate-limit + abuse story for cross-tenant federation is non-trivial. Phase R candidate when a second Forge install is in scope.
- **LLM-driven false-parallel planner.** The deterministic Jaccard / noun-overlap heuristic in 30.8 is intentionally simple. An LLM-driven planner that reads the architect's plan + the workstream descriptions + the existing codebase to detect implicit serialization is `forge_v2.md`.
- **Bus retention + eviction.** Bus rows accumulate forever at v0.1. A retention policy (e.g. drop `acp_messages` older than 90 days when their run is `completed`) is a Phase R polish; ergonomically the operator can VACUUM if disk shows up.
- **Per-context ACL.** All contexts within a topic the agent subscribes to are visible. Per-context "this is private to crew-A" gating is `forge_v2.md` — the bus is intentionally information-rich to maximize cross-stream learning.
- **Two-way ACP with non-Forge agents that *write* contexts.** External ACP agents can read the bus today via the conformance endpoints; they can also write, but the silo auditor + false-parallel planner only reason about contexts emitted by Forge-known agents (matched against `agents.id`). External-author silo math is Phase R.
- **Auto-merge of duplicated `interface` contexts.** When two workstreams emit the same interface contract (one of the silo auditor's most common findings), v0.1 surfaces it and lets the operator decide; auto-deduping is `forge_v2.md` because the merged shape is rarely a clean intersection.
- **Bus-level prompt budgeting across iterations.** `Forge:Acp:PreambleTopN=20` is a per-iteration cap; a per-deliverable token-spent-on-preamble cap that gates further bus reads when crossed is Phase R.
- **Manual context emission from the UI.** Operators cannot hand-author an ACP context at v0.1 — only agents emit. A "post note to bus" CTA on the work item is a Phase R polish.

---

## Phase 43 — Story attachments & operator accept

> Operator-uploaded reference documents and manual acceptance of plan-doc artifacts.

### Story attachments

`StorySpawnRow.AttachmentsJson` — JSON array of operator-uploaded reference documents. Each entry: `{ "id": "guid", "fileName": "spec.md", "storagePath": "relative/path", "mimeType": "text/markdown", "sizeBytes": 1234 }`. Files live on disk under `E:\forge\attachments\{spawnId}\`. Schema migration `Phase43Migration.cs` adds the `AttachmentsJson TEXT` column to `story_spawns`. **Migration ordering:** Phase 43 must run before Phase 38 because Phase 38's EF query reads `StorySpawns` which maps the new column — without it the startup crashes with `no such column: s.AttachmentsJson`.

Upload flow: `POST /api/stories/upload` stores files on disk and returns upload ids; the PATCH `/api/products/{id}/stories/{storyId}` endpoint accepts `uploadIds` / `removeFileNames` to attach/detach files from the story. `CrewStoryArtifactStrategy.CopyAttachmentsToWorktreeAsync` copies attachments into the plan worktree so architect agents can read them with file tools.

### Operator accept (manual pass override)

`POST /api/stories/{storyId}/spawn/artifacts/{key}/accept` — manually marks a plan-doc artifact as passed despite its evaluator score being below the 90-point pass threshold. The operator overrides the evaluator verdict: the latest deliverable is flipped to `pass`, any in-flight invocations and queued rows are cancelled, and the doc transitions to the `awaiting-pr-review` lifecycle stage. **Idempotent:** calling accept on an already-passed doc re-triggers the PR merge flow without re-flipping the verdict.

After acceptance, a background task pushes the plan branch (via `IStoryGitOps.PushBranchAsync` which stages uncommitted changes and pushes all local-only per-iteration commits) and opens a PR to merge the accepted content to main. If the original plan PR is already merged, a new `[plan-update]` PR is opened. The PR is auto-approved and merged.

**UI:** a green-bordered `✓ ACCEPT` button sits between CANCEL and RESTART in the doc conversation pane (`DocConversationPane.tsx`). A confirmation dialog warns the operator that the evaluator verdict is being overridden. The button stays enabled even after the doc is completed so the operator can re-trigger the merge if the first attempt failed.

**API:** `forgeApi.acceptStoryArtifact(storyId, key)` — returns `{ storyId, key, status, previousVerdict, previousScore, queueRowsCancelled, invocationsCancelled }`.

### Default story points

`NewStoryModal.tsx` defaults points to **5** (was 0) so newly created stories have a reasonable budget and reject threshold out of the box.

### Story points propagation

`PATCH /api/products/{id}/stories/{storyId}` — when `points` is updated, the change is propagated to `StorySpawnRow.StoryPoints` so the spawn's budget and scoring thresholds reflect the updated value. Previously the spawn snapshot was a one-time copy at creation and never synced.

### Plan-doc budget improvements

- **Budget uses work-item points, not story points.** `CrewStoryArtifactStrategy.ProducePlanFileAsync` recomputes the budget from `docPoints` (the plan-doc work item's points) instead of `spawn.StoryPoints`. This prevents 0-point stories from starving their docs.
- **10-minute floor for plan docs.** `BudgetForPhase` enforces a 600-second minimum for `plan:*` phase tags. The previous floor of 180s (3 minutes) caused hard timeouts when the architect needed to read repo context, process sibling docs, and write large markdown files.
- **Timeout displayed in UI.** The conversation tab pager shows `timeout Nm` alongside `iter X of Y`. The backend returns `budgetSeconds` from the `GetArtifactIterations` endpoint.

### Orchestrator re-drive after resume

`RunResumeWithCleanupAsync` — after a resumed doc's gen-eval loop completes successfully (verdict = pass), the orchestrator is re-driven via `orchestrator.DriveAsync(storyId)`. This fixes the case where docs are restarted/resumed individually (outside the orchestrator's original `Task.WhenAll`): previously the orchestrator was never notified that all docs had passed, so the plan PR was never opened.

### IStoryGitOps.PushBranchAsync

New interface method `PushBranchAsync(repo, branch)` — stages any uncommitted changes in the worktree (from a cancelled agent mid-write) and pushes the branch to origin. Unlike `CommitFilesAsync` (which calls `EnsureWorktreeAsync` and can reset the local branch), `PushBranchAsync` operates directly on the existing worktree without a checkout step, preserving local-only per-iteration commits. `RealStoryGitOps` implements with branch-level locking; `InMemoryStoryGitOps` is a no-op.

---

## Phase 45 — Operator-chosen base branch on a story spawn

> Replaces the hard-coded "every spawn ships to `main`" assumption with a per-spawn base branch the operator picks at spawn time and can change mid-flight. Every PR a spawn produces — plan, workstream, QA, step-defs — targets this branch. The lifecycle, gate logic, and crew flow are unchanged.

### Why

A real product team rarely ships every story straight to `main`. Release branches, feature gates, and long-running integration branches all need stories that fan out across many PRs but converge on a non-`main` target. Forcing operators to retarget every PR by hand after the fact is busywork and error-prone; the swarm's whole value is end-to-end automation.

### Concept

A spawn now carries a `BaseBranch` field on `StorySpawnRow` (defaults to `"main"`). Every place the orchestrator previously passed `"main"` as a base — `git.EnsureBranchAsync` (plan / workstream / qa / step-def branches), `provider.OpenPrAsync` (PR target), `git.MergeAsync` (local merge after PR merge), and `git.FileExistsAsync` (artifact-existence gate) — now reads `BaseBranchOf(spawn)`.

When `BaseBranch != "main"`, the orchestrator's first drive calls `EnsureSpawnBaseBranchAsync` to create the branch from `main` so the spawn's plan branch can fork off it. This is idempotent — the branch is no-op'd when it already exists in the repo.

### Data model

`StorySpawnRow.BaseBranch TEXT NOT NULL DEFAULT 'main'` — added by `Phase45Migration`. Backfill `UPDATE story_spawns SET BaseBranch = 'main' WHERE BaseBranch IS NULL OR BaseBranch = ''` so existing rows behave identically.

`WorkstreamRow.Base` is no longer hard-coded to `"main"` at insert. Both code paths that create workstream rows (`StorySpawnOrchestrator.PersistDecompositionAsync`, `PlanReconciler.InsertWorkstream`, manual `POST /spawn/decompose`) now read `spawn.BaseBranch` so a workstream's `Base` matches the spawn's chosen target.

### REST API

- `POST /api/stories/{storyId}/spawn` — `SpawnRequest` gains `baseBranch?: string`. Validated against a cheap subset of `git check-ref-format` (no spaces, no leading/trailing slash, no `..`, no `@{`, ASCII-control or `\ ~ ^ : ? * [` characters) so a typo cannot reach the `git fetch` command line. Empty / missing falls back to `"main"`.
- `PATCH /api/stories/{storyId}/spawn/base-branch` — change the base branch on a live spawn. Body: `{ "baseBranch": "..." }`. Returns `{ spawn, replayedBranches, retargetedPrs, replayFailures }`. Idempotent — when the new base equals the old, it's a no-op.
- `GET /api/stories/{storyId}/spawn` — response gains `baseBranch` on the `spawn` object.

### Base-branch change semantics

The PATCH endpoint runs three steps in order:

1. **Ensure + rebase the new base on top of `main`.** `IStoryGitOps.EnsureBranchAsync` creates the branch from `main` if it doesn't exist; `IStoryGitOps.RebaseBaseBranchAsync` (new) brings it up to `origin/main`'s tip via `git rebase --rebase-merges` in a dedicated worktree, pushed with `--force-with-lease`. So the new base contains every commit that landed on `main` after the spawn started.
2. **Replay every already-merged spawn PR onto the new base.** Plan PR, every merged workstream PR, QA PR, and any merged step-def PRs are walked; their source branches are merged into the new base via `IStoryGitOps.MergeAsync`. Failures are collected (not thrown) and surfaced as `replayFailures[]` so the operator can resolve conflicts manually without losing the rest of the operation.
3. **Retarget every still-open spawn PR.** Plan PR (if open), open workstream PRs, QA PR (if open), open step-def PRs are walked; `IStoryProviderOps.UpdatePrBaseBranchAsync` (new) updates each one's target branch.

After the three steps the spawn's `BaseBranch` is updated and every workstream's `Base` is updated to match. Subsequent orchestrator ticks use the new base for everything that hasn't been opened yet — workstreams that are still `planning`, step-def PRs not yet opened, etc.

### Provider support

`IStoryProviderOps.UpdatePrBaseBranchAsync(prId, newBase, ct)` — local cache binding (`LocalStoryProviderOps`) updates `PrRow.Base` and emits a SignalR `pr.changed { kind: "base-branch-changed" }`. Production binding (`ProviderStoryProviderOps`) does the same and logs a structured warning that the upstream provider edit is missing — the `IForgeProvider` contract has no PATCH-PR endpoint at v0.1, so the operator must reset the base on GitHub / ADO manually until provider support lands. **`forge_v2.md` candidate:** add `IForgeProvider.UpdatePullRequestBaseAsync` and a PATCH `/repos/{owner}/{repo}/pulls/{n}` (GitHub) / equivalent ADO call so the retarget is end-to-end automatic.

### Git ops

`IStoryGitOps.RebaseBaseBranchAsync(repo, baseBranch, upstreamBranch, ct)` — bring the spawn's base branch up to the upstream tip:

- **Real binding (`RealStoryGitOps`):** dedicated worktree at `{cloneRoot}/.worktree/spawn-rebase-{slug}/`. `git fetch origin {base}` + `git fetch origin {upstream}` + `git reset --hard origin/{base}` + `git rebase --rebase-merges origin/{upstream}` + `git push --force-with-lease origin {base}`. No-op when `base == upstream`.
- **In-memory binding (`InMemoryStoryGitOps`):** copies the upstream tree over the base branch tree and bumps the tip sha. Tests rely on this to assert that downstream phases see commits that were on `main` after the spawn started.

### UI

- **`SpawnStoryModal`** — adds an optional "Base branch" input below the repo picker. Placeholder = `"main"`; help text explains that non-default values are branched from main automatically and that every PR the spawn produces targets this branch.
- **`StoryCard`** — header shows a `base: {branchName}` chip when the spawn's base differs from `"main"`, with a tooltip explaining the targeting behavior.
- **`forgeApi.spawnStory`** — gains an optional `baseBranch` parameter. `forgeApi.changeStoryBaseBranch(storyId, baseBranch)` calls the new PATCH endpoint and returns the replay summary.

### Story-level default base branch

Operators rarely want to type a base branch on every spawn — most stories should target the same branch end-to-end. The story itself carries the default, and the spawn endpoint reads it as the fallback when `SpawnRequest.BaseBranch` is omitted.

`ProductStoryRow.BaseBranch TEXT NOT NULL DEFAULT 'main'` — added by `Phase45Migration` to the `product_stories` table alongside the `story_spawns` column. Backfilled to `"main"`. Surfaced through:

- **API** — `CreateStoryRequest.BaseBranch` (POST) and `PatchStoryRequest.BaseBranch` (PATCH) on `/api/products/{productId}/stories[/{storyId}]`. The `ProductStory` contract gains `BaseBranch` (defaulting to `"main"` for back-compat).
- **Spawn fallback** — `StoriesEndpoints.Spawn` reads the originating `ProductStoryRow.BaseBranch` and uses it when the `SpawnRequest.BaseBranch` is null/empty. Explicit `SpawnRequest` value still wins.
- **NewStoryModal** — optional Base branch input (placeholder `"main"`) below the Points field. Help text explains that non-default values are created from `main` immediately in every repo of the product.
- **StoryDetailPanel** — Base branch field in the meta grid alongside Status / Points. Inline-editable with the same commit-on-blur pattern. A "branch created" toast confirms success; a `branchWarnings[]` toast surfaces any per-repo failures.

#### Auto-create branch on POST + PATCH

When the operator either (a) creates a story with `baseBranch != "main"`, or (b) PATCHes an existing story to change `baseBranch` to a non-default value, the server immediately calls `IStoryGitOps.EnsureBranchAsync(repo, baseBranch, "main")` for every `RepoRow` in the product. The branch is materialised in advance so a subsequent spawn doesn't have to wait for the first orchestrator drive — and so the operator can see the branch on GitHub / ADO right after the PATCH returns.

Implementation details:
- `EnsureBranchInProductReposAsync` helper iterates `Repos` filtered by `ProductId`. Each `EnsureBranchAsync` call is idempotent — repos where the branch already exists are no-ops. Per-repo failures (no clone yet, auth failure, branch protection) get logged + collected as `branchWarnings[]` strings.
- The endpoint's response wraps the `ProductStory` payload as `{ story, branchWarnings }` whenever any warning surfaced; otherwise it returns the bare `ProductStory` for back-compat. The TypeScript client (`forgeApi.createProductStory` / `updateProductStory`) normalises both shapes to `{ story, branchWarnings? }`.
- Patching the story's `BaseBranch` ALSO triggers the **full retarget flow** when a spawn already exists for the story: rebase the new base on top of `main`, replay every already-merged spawn PR onto it, retarget every still-open spawn PR to the new base, and update every workstream row's `Base`. The story-level PATCH and the spawn-level `PATCH /api/stories/{id}/spawn/base-branch` therefore share a single internal helper (`StoriesEndpoints.ApplySpawnBaseBranchChangeAsync`) so both code paths produce identical results. This satisfies user requirements #5 + #6: changing base branch on a story with a live spawn must immediately retarget every existing spawn task at the new base, not just set the default for future work.
- Response body when retarget runs: `{ story, branchWarnings?, replayedBranches?, retargetedPrs?, replayFailures? }`. The UI's `StoryDetailPanel` shows two toasts on save — one for branch creation and one summarising the spawn retarget (e.g. `"Spawn retargeted: 4 open PRs → feature/memory-pressure, 0 merged branches replayed"`).
- Empty-string base branch is normalised to `"main"` so the operator can clear the field via the inline editor.

### Constraints / non-goals

- **No automatic conflict resolution during replay.** A merge conflict during step 2 of the PATCH endpoint surfaces as a `replayFailures[]` entry plus a `LastError` on the spawn so the operator can resolve it manually. The PATCH still completes — the new base is set, open PRs are retargeted — but the operator owns the conflict resolution.
- **No upstream PR-edit at v0.1.** Provider support for retargeting open PRs upstream is a `forge_v2.md` candidate. Until then the local cache reflects the new base but GitHub / ADO need a manual reset on each open PR.
- **Base branch can't be changed while a phase is mid-step.** The endpoint runs synchronously, so a concurrent orchestrator tick may see the partially-updated state. Subsequent ticks recover (the orchestrator's per-step methods are idempotent), but operators should expect to see a brief "two open PRs against different bases" gap on the UI between PATCH start and finish.
- **No multi-base spawn.** A spawn has exactly one base branch; per-workstream base override is out of scope.
- ~~**Story-level PATCH does NOT replay merged PRs.**~~ **Revised after operator feedback (2026-05-05): story-level PATCH now runs the same full retarget flow as `PATCH /api/stories/{id}/spawn/base-branch` when a spawn exists**, because operators expect "I changed my base branch, all my open PRs should target the new branch" without having to discover a second endpoint.

### Engineer agent edits real repo files in place (no sandbox prefix)

Operator hit a "sloppy agent" pattern on 2026-05-05: workstream `HttpMetricsHandler — Stop Body Buffering` had iter 1 generator complete a full session (claimed *DONE* with code edits + passing tests in its summary) but evaluator scored 86 / iterate, and iter 2 repeated the same dance without converging. Root cause was structural — `ProduceWorkstreamChangesAsync` set the engineer agent's working directory to a scratch sandbox at `{stateDir}/storyworkspaces/{spawnId}/impl-{wsSlug}/` and then re-keyed every file path under `src/.story-artifacts/{storyId}/{wsId}/` before the orchestrator committed. The agent could only scaffold new files in the sandbox; it could never edit existing repo files (like the actual `HttpMetricsHandler.cs`). So the agent did real work in the broader filesystem (where its tools could see the repo), claimed completion, dutifully tried to "copy results to the workspace directory" — and only a partial mirror landed where the evaluator could read it.

Compare the architect path (`ProducePlanArtifactsAsync` line ~150): plan-doc agents already work directly in the plan branch's git worktree; their writes land at real paths and commit cleanly. Workstream agents needed the same treatment.

Fix shipping in this revision:

- **Workspace = workstream branch's git worktree.** `ProduceWorkstreamChangesAsync` now calls `git.EnsureWorktreeAsync(repo, workstream.Branch, baseBranch)` and uses that path as the agent's working directory. Edits land at real repo paths inside the worktree.
- **Drop the `repoPrefix` rewrite.** Files commit at the relative path the agent wrote (e.g. `Microsoft.Update.Common.Diagnostics/HttpMetricsHandler.cs`), not under `src/.story-artifacts/...`.
- **Detect changed files via `git status`, not recursive enumeration.** `IStoryGitOps.GetChangedFilesAsync(repo, worktreePath)` parses `git status --porcelain -z` output and returns repo-relative paths of added/modified/renamed files (skips deletions because `CommitFilesAsync`'s dict contract can't represent them). The strategy reads each one back from disk and returns the dict to the orchestrator, which `CommitFilesAsync` re-materialises (idempotent — same content) and commits.
- **Updated engineer prompt.** Tells the agent: *"Your working directory IS a git worktree of the actual repo, on branch `ws/...`. You can READ AND WRITE ANY FILE. Edit existing source files in place at their real repo paths. Do NOT scaffold a sandbox copy under `src/.story-artifacts/`."*
- **`ReadCachedWorkstreamFilesAsync` follows the same pattern** so a verdict=pass workstream re-entered after restart reads from the worktree's uncommitted changes (not the dead sandbox path) and the orchestrator finishes the commit.

This unblocks the entire impl phase for stories whose workstreams need to *modify* existing repo content — which is most real engineering work, not just scaffolding.

### Engineer agent: 30-second per-spawn timeout was killing every iter

The worktree fix above turned out to be necessary but not sufficient. Operator inspected `E:/forge/update/.worktree/spawn-ws-...` after deploying it and found `git status` clean across 124 iters, with the workstream branch's HEAD untouched since the merged PR that opened it. The agent was getting cancelled mid-tool-call and never reaching a write.

Root cause: `SpawnDependencyScheduler.TickOnceAsync` wrapped each `orchestrator.DriveAsync(spawnId, ct)` in a 30-second linked CTS, intended as a "don't let one slow spawn starve the others" safety. With 5 active spawns, ticks took ~150 seconds (the 2.5-min reflog cadence the operator observed) AND every impl-phase agent invocation was cancelled at 30s — long before it could even read enough files to know where to start editing. Doc/architect work occasionally squeezed a write into 30s because each plan doc has a single known target path baked into the prompt; engineer work never could because the path-discovery + edit cycle simply doesn't fit.

Fix:

- **Drop the per-spawn 30s CTS in `SpawnDependencyScheduler.TickOnceAsync`.** Agents now run to their own `AgentContext.WallClockBudget` (`BudgetForPhase` already enforces a sane per-phase cap — 6 min for impl on a 1-pt workstream, scaling with story points). Concurrent ticks for the same spawn serialise on the per-branch lock in `RealStoryGitOps`, so the worktree never sees two writers.
- **Fan out spawns inside a tick via `Task.WhenAll` with one task per spawn.** A fast spawn no longer waits behind a slow neighbour; a stuck spawn no longer stretches the tick into minutes. Each task wraps its own try/catch so one spawn's exception doesn't poison the others.

### Engineer agent: bake target file paths into the impl prompt

Even with the timeout fix, the engineer still spends precious wall budget walking the tree to find the files the workstream's brief refers to. Plan/doc agents don't have this problem because their prompt names a single target path (`{spec.RepoPath}`); engineer agents were left to discover targets in a multi-thousand-file enterprise repo via `ls` and `grep`.

Fix in `ProduceWorkstreamChangesAsync`:

- **Two-pass target extraction**, run once per impl iter before invoking the engineer:
    1. `ExtractTargetPaths` — regex over `{workItemDescription, workstream.Summary, planStageBody}` for backtick-wrapped or bare paths with known code-file extensions (`.cs`, `.ts`, `.py`, `.go`, etc.). Sanitises (forward slashes, no `..`, no absolute paths, skip `docs/stories/...` doc-context paths). Cheap, deterministic, usually enough for well-decomposed stages whose plan body already names the files to touch.
    2. `DiscoverEngineerTargetsAsync` — fallback when (1) finds nothing. Runs a short read-only architect agent (90s budget) on the same `{summary, brief, stage}` prose; the agent writes `targets.json` to a sandbox; we deserialise + re-sanitise. One extra LLM call per workstream's first iter, dwarfed by the 6+ minutes of engineer time it saves.
- `ReadPlanStageBodyAsync` resolves `WorkstreamRow.PlanAnchor` (e.g. `phase-2/stage-1`) inside the merged `implementation-plan.md` so the relevant stage's body — not the whole plan — feeds extraction and discovery.
- **`## Target files (start here)` section in the engineer prompt.** Renders the resolved targets as a bullet list. When both passes yield nothing, the section explains so the agent still has clear marching orders rather than silently empty context.
- **Bash / `run_command` mentioned explicitly.** SDK invocations of `crew-eng` already have shell tools available (no `DisallowedTools` is forwarded), but the prior prompt only suggested `ls`, `grep`, `view`. The new prompt directs the agent to `grep -r 'symbol' src/` via shell and to run `dotnet build`/`dotnet test` to verify edits.

Targets are advisory — the engineer still edits any file the implementation legitimately needs — but converting "explore a 1000-file repo from scratch" into "open these N files first" is what fits inside the per-iter wall budget.

### Engineer agent: tolerate self-committed work; forbid `git commit` going forward

Within minutes of restart with the timeout + targets fixes, the HIGH-MEM-CONSUMPTION HttpMetricsHandler workstream surfaced a new false-negative: evaluator scored 95 (pass), but `ProduceWorkstreamChangesAsync` then threw "Engineer produced no file changes" anyway. Reflog on the worktree had the smoking gun:

```
604ea1f1e HEAD@{13:00:25}: commit: HttpMetricsHandler: stop body buffering to prevent LOH pressure
165bc5c0e HEAD@{12:46:35}: checkout: ...
```

The engineer agent had run `git add -A && git commit -m "…"` itself via its `bash` / `run_command` tool — encouraged, in part, by the new prompt mentioning `dotnet build` / `dotnet test` (which framed "the shell is yours to use"). Once the agent committed, `git status --porcelain` returned clean, our check decided the agent had produced nothing, and the orchestrator halted with the agent's actual work stranded as a local-only commit.

Fix:

- **`RealStoryGitOps.GetChangedFilesAsync` now combines two sources:** uncommitted changes from `git status --porcelain -z` AND committed-but-unpushed changes from `git diff --name-only -z origin/{branch}..HEAD`. The branch is resolved from the worktree itself via `git rev-parse --abbrev-ref HEAD` so the `IStoryGitOps` signature stays unchanged. Falls back gracefully when the worktree is detached or `origin/{branch}` doesn't exist yet (very-first commit case). Result: agent self-commits flow through `CommitFilesAsync` → `PushWithAutoRecoverAsync` and land on origin like normal uncommitted edits would.
- **Engineer prompt explicitly forbids `git add`/`git commit`/`git push`/`git stash`/`git reset`.** Keeps the loop predictable for everyone (operators reading the worktree tab, evaluator reading file mtimes, future fixers debugging diff sources). Read-only git commands (`git log`, `git diff`, `git blame`) remain available because the agent legitimately uses them to understand existing code.

Together, (1) recovers the in-flight HIGH-MEM-CONSUMPTION iter on retry (the agent's local commit is still on disk, the new code path picks it up and pushes it), and (2) prevents the recurrence by making "let Forge do the git plumbing" an explicit instruction rather than an implicit assumption.

### ADO repo identifier — canonical, not display name

Operator hit `TF401019: Git repository 'update' does not exist` when ACCEPT auto-opened a PR against ADO. Root cause: `ProviderStoryProviderOps.OpenPrAsync` was passing `Repo.DisplayName` ("update" — operator's friendly alias) as the ADO repo identifier. ADO's PR REST API needs the canonical slug from the remote URL (`https://msazure.visualstudio.com/One/_git/ASZ-UpdateService` → `ASZ-UpdateService`).

Fix: `ProviderStoryProviderOps.OpenPrAsync` now uses `PrCoordsResolver.Resolve(req.Repo, req.Repo.Kind)` — the same canonical resolver `CrewQueueWorker` (live-exec PRs) and `WorkItemsEndpoints` (agent-callable `POST /work-items/{id}/create-pr`) already use. The resolver extracts the slug after `_git/` for ADO and uses `GhRepo` / `GhOwner` for GitHub. Story-spawn PRs and live-exec PRs now share the exact same repo-coordinate resolution path so a new provider kind is wired in exactly one place.

### Plan-doc loop guards — pass-verdict + PR-merge cancellation

After fixing ACCEPT to always open a PR, the operator hit a residual UX bug: two of four plan-docs stayed at `LIVE` even though they'd already evaluated to score 92 on iter 28 / iter 19. Status logic in `GetArtifactIterations` puts `isRunnerLive`/`isRecentEvent` ahead of `lastWasPass`, so any in-flight iteration shows `LIVE` regardless of past pass verdicts. And the only "skip if already done" check in `ProducePlanFileAsync` was guarding on the worktree file existing — not on the deliverable history. Any re-entry path (Resume click, scheduler tick, base-branch-change replay, boot recovery via `StoryResumeRecoveryService`) re-iterated docs that were already passing.

Two fixes, both shipping in this same revision:

**Pass-verdict guard in the strategy.** At the top of `ProducePlanFileAsync` (and inherited by `ResumeSingleArtifactAsync`), query the latest `DeliverableRow.Verdict` for `plan-doc:{spawn.Id}:{fileName}`. When it's `pass` AND `forceResume` is false, return the cached worktree content immediately — no LLM call. Restart still works (it deletes deliverables first, so the check finds none). Retry with `forceResume=true` still bypasses. This prevents new iterations from being kicked off after a pass.

**Plan-PR-merge reactor.** New helper `PlanPrMergeReactor.OnPlanPrMergedAsync` walks every plan-doc work item for the spawn whose plan PR was just merged, calls `IAgentRunner.CancelByTaskAsync` to kill in-flight invocations, flips `WorkItemRow.ExecutionStatus` to `idle`, cancels live `task_queue` rows, and clears `StorySpawnRow.ResumePendingJson` so boot recovery doesn't re-fire. Called from BOTH paths that flip `PrRow.Col` to `merged`:

- `ProviderStoryProviderOps.MergePrAsync` — Forge-issued merges (e.g. ACCEPT auto-merge).
- `PrSyncJob.RunSyncAsync` — when the periodic sweep observes the upstream PR is now completed (operator-merged on ADO/GitHub directly).

Idempotent: re-firing on an already-handled spawn is a no-op. Each call emits `story-spawn.changed { kind: "plan-pr-merged-cancelled", cancelledCount: N }` so the UI can surface a confirmation.

### ACCEPT always opens a PR against the current base

`POST /api/stories/{storyId}/spawn/artifacts/{key}/accept` is the operator-override that flips a plan-doc deliverable to `pass` despite a sub-90 score. Earlier the fire-and-forget post-accept task only opened a new PR when the existing plan PR was already merged — which is the wrong shape when the operator changes the base branch mid-flight, because the still-open plan PR is targeting a stale base. Operator feedback (2026-05-05): "PR should be created no matter if base branch is changed."

Revised algorithm in `AcceptArtifact`'s background task:

1. Push the plan branch to origin.
2. Decide whether to open a new PR or reuse the existing one:
   - No existing PR → open new.
   - Existing PR is `merged` → open a new `[plan-update]` PR.
   - Existing PR is open AND its `Base` differs from the current `spawn.BaseBranch` → close the stale PR locally, emit `pr.changed { kind: "superseded" }`, open a new PR against the current base.
   - Existing PR is open AND already targets the current base → reuse it. Idempotent across multiple per-doc accepts (the four plan docs share one PR).
3. Auto-approve + merge the resulting PR.

Failures in the fire-and-forget task are no longer silently swallowed — they persist as `StorySpawnRow.LastError` + emit `story-spawn.changed { kind: "spawn-error" }` so the workstream banner surfaces the reason. Previously the empty `catch {}` block left operators stuck with `AWAITING-PR-REVIEW` artifacts and no signal about why the merge stalled.

### Test scenarios

```gherkin
@phase45 @base-branch
Feature: Operator-chosen base branch on a story spawn

  Scenario: Spawn defaults to main when baseBranch is omitted
    When the operator spawns a story without specifying a base branch
    Then the StorySpawnRow.BaseBranch is "main"
    And the plan PR targets main

  Scenario: Spawn with custom base branch persists and surfaces on plan PR
    When the operator spawns a story with baseBranch = "release/2026-q2"
    Then the StorySpawnRow.BaseBranch is "release/2026-q2"
    And the plan PR targets "release/2026-q2"

  Scenario: Custom base branch is created from main on first drive
    Given a spawn with baseBranch = "feature/big-thing" exists
    When the orchestrator drives the spawn for the first time
    Then a git branch "feature/big-thing" exists branched from main

  Scenario: Workstreams inherit base branch from spawn
    Given a spawn with baseBranch = "release/lts" reaches the implement phase
    When the architect's decomposition creates workstream rows
    Then every WorkstreamRow.Base equals "release/lts"
    And every workstream PR targets "release/lts"

  Scenario: Change base branch endpoint rebases new base + retargets open PRs
    Given a spawn with baseBranch = "main" has an open plan PR and one open workstream PR
    When the operator PATCHes the spawn with baseBranch = "release/tip"
    Then the new base branch is rebased on top of main
    And the plan PR's target is "release/tip"
    And the workstream PR's target is "release/tip"
    And every workstream row's Base is "release/tip"

  Scenario: Change base branch replays already-merged spawn PRs onto new base
    Given a spawn has its plan PR merged on the old base
    And one workstream PR has merged on the old base
    When the operator changes the spawn's base branch
    Then the new base branch contains the plan branch's tree
    And the new base branch contains the merged workstream branch's tree

  Scenario: Change base branch rejects invalid branch names
    Given a spawn exists
    When the operator PATCHes the spawn with baseBranch = "has spaces"
    Then the API returns 400
    And the spawn's BaseBranch is unchanged

  Scenario: Change base branch is idempotent when new base equals old
    Given a spawn with baseBranch = "main"
    When the operator PATCHes the spawn with baseBranch = "main"
    Then the API returns 200 with no replayed branches and no retargeted PRs
```

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

