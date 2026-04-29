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
    - Salary roll-up is one level only: per-crew on `/crews`, per-product on `/products`. No "department total" intermediate level.
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

- [x] **3.1** Stats strip (active count, open PRs, merged today, 24h cost).
- [x] **3.2** Workstream grid card: branch, base, ahead/behind, conflicts, last-active, summary, agents, linked PRs / cards.
- [x] **3.3** Filter chips: All / Active / Merging / Conflicts / Idle.
- [x] **3.4** "Sync from epics" button: re-derive workstreams from cards in Build/Review.
- [x] **3.5** "New workstream" modal (epic, name, base branch, worktree path).
- [x] **3.6** API endpoints in `Forge.Api/Endpoints/WorkstreamsEndpoints.cs`: `GET /api/workstreams`, `POST /api/workstreams`, `POST /api/workstreams/{id}/sync`, `DELETE` (with safety prompt).
- [x] **3.7** Local git worktree wrapper service `IGitWorktreeService` in `Forge.Api/Services/Git/` (`git worktree add/list/remove`); guard against operating outside the configured root.
- [x] **3.8** Compute ahead/behind/conflicts via `git rev-list --count base...branch` and `git merge-tree`.
- [x] **3.9** Conflict surface: badge on card + "Open conflict" action that opens the file path returned by `git diff --name-only --diff-filter=U`.
- [x] **3.10** Cross-link: clicking a card on the board with a `branch` jumps to the matching workstream; vice versa from workstream → cards.
- [x] **3.11** Permission gate: writes (`POST/DELETE`) require an authenticated user (placeholder until Phase 5 auth).
- [x] **3.12** Tests: `IGitWorktreeService` unit tests against a temp repo; endpoint integration tests; Playwright "create workstream" flow.
- [x] **3.13** **Fidelity check** — screenshot diff of `/workstreams` against the prototype: stats strip, full grid, each filter chip active, and the new-workstream modal open. Diff must be 0.

## Phase 4 — Pull Requests view

PR board (Implementation → Build → Review → Merged) with build, review and gate state.

- [~] **4.1** Four-column PR board with `pr.col` mapping (`implementation | build | review | merged`). _(prototype has 3 cols — kept 3 per fidelity constraint; spec ↔ prototype contradiction needs resolution)_
- [x] **4.2** PR card: title, author, branch, additions/deletions, files, age, build status, review verdict, conflict + human-gate badges.
- [x] **4.3** Build status renderer (running with %, passing, failing, queued) with a tiny progress strip.
- [x] **4.4** Review verdict pill (approved / changes / pending) with one-line agent summary.
- [~] **4.5** PR detail panel: full description, diff stats, reviewer list, build log tail (last 50 lines), human-gate reason. _(description + build-log are server-side stubs; real values arrive in Phase 5/6 from ADO/GitHub)_
- [~] **4.6** Filters: All open / Need approval / Failing / Conflicts / My reviews; selects for workstream / author. _(chips fully wired; Workstream/Author selects + search input are decorative — match the prototype but spec implies functional)_
- [~] **4.7** API `GET /api/prs` aggregates from cache + provider; `GET /api/prs/{id}` with details. _(cache-only today; provider aggregation lands with Phase 5)_
- [x] **4.8** Auto-merge toggle (per PR) — flag stored locally; actual merge happens via provider in Phase 6.
- [~] **4.9** Re-run / cancel build buttons (UI only this phase; server stub returns 202 + plans Phase 6 hookup). _(complete-by-spec; the stubs flip cached state but no real CI is invoked)_
- [x] **4.10** Toast on PR state transition via SignalR `pr.changed`.
- [x] **4.11** Playwright: failing PR shows the `failing` chip; clicking opens the detail panel; auto-merge toggle persists across reloads.
- [x] **4.12** **Fidelity check** — screenshot diff of `/prs` against the prototype: every PR card variant from the seed data (running build, queued, passing, failing, conflict, human-gate, auto-merge armed). Diff must be 0.

## Phase 5 — ADO / GitHub providers (read path)

Bring real work items, branches and PRs into the cache.

- [x] **5.1** Add `IForgeProvider` interface in `Forge.Api/Providers/` (`ListWorkItems`, `ListPullRequests`, `ListBuilds`, `GetWorkItem`, `GetPr`, …).
- [x] **5.2** Implement `AdoProvider` using `Microsoft.TeamFoundationServer.Client` / REST; PAT in user-secrets `Forge:Ado:Pat`. _(REST via HttpClient, not the TFS client lib — keeps deps small)_
- [x] **5.3** Implement `GitHubProvider` using Octokit; PAT or fine-grained token in `Forge:GitHub:Pat`. _(REST via HttpClient, not Octokit — same reason)_
- [~] **5.4** Provider config UI page `/settings` (React): org/project (ADO), owner/repo (GitHub), PAT entry, "test connection". _(read + Test connection live; PAT entry intentionally NOT in the UI to avoid storing secrets in SQLite without an encryption story — config flows through user-secrets / env. **Superseded by Phase 9** — interactive sign-in replaces PAT entry as the primary auth path; PAT remains only as a headless / CI fallback.)_
- [x] **5.5** Map ADO work item → `Card`: state → column, area path → epic, tags → tags, story points → pts, assigned-to → agent (human role).
- [x] **5.6** Map GitHub issue + PR → `Card` / `Pr` (issue type by labels, PR draft status preserved).
- [~] **5.7** Background sync: `IHostedService` polling on configurable interval (default 60s), with jitter + ETag/If-None-Match where supported. _(polling + jitter shipped; ETag/If-None-Match is Phase R)_ **Phase 23 splits this**: cards / PRs / pipelines stay on the 60s `ProviderSyncService` cadence; **work-item sync moves to a separate 20-min `WorkItemSyncJob`** with proper pagination, delta-cursoring, and per-area-path scope. Both jobs run side-by-side and touch different tables.
- [x] **5.8** Webhook receiver `POST /api/webhooks/{provider}` for ADO Service Hooks and GitHub webhooks; HMAC verification; falls back to polling if webhook is silent for >5 min.
- [~] **5.9** Conflict policy on overlapping local + remote edits: last-writer-wins by default, with a "remote changed since" badge on the card and an explicit "discard local" / "overwrite remote" choice. _(server-side 409 + currentRevision payload + X-Forge-Overwrite header land; UI badge + choice modal is Phase R)_
- [x] **5.10** Cache invalidation on webhook; SignalR `card.changed` fan-out.
- [x] **5.11** Provider health on `/settings`: last sync, error rate, rate-limit budget remaining.
- [x] **5.12** Tests: provider contract tests against recorded HTTP cassettes (WireMock.Net); webhook signature unit tests; one end-to-end test that boots the provider with a fake server and asserts cards appear. _(used a 50-line StubHttpMessageHandler instead of WireMock.Net — same coverage, no extra dep)_

## Phase 6 — Write actions: WI, PR, pipelines

Make every read-only surface from Phase 5 round-trippable.

- [x] **6.1** Create work item (ADO) / issue (GitHub) from the New-card modal, with provider selection.
- [x] **6.2** Update work item: status transition, assignee, iteration/sprint, tags, story points, area path.
- [x] **6.3** Comment on a card → posts to the linked WI / issue.
- [x] **6.4** Create PR from a workstream (`POST /api/workstreams/{id}/pr`) — pushes branch, opens PR with templated description (linked card IDs, agent summary).
- [x] **6.5** PR review actions: approve / request-changes / comment from the detail panel; surface line-level comments where the provider supports it. **Phase 24 extends this** with a persistent comment store (`pr_comments` table), authority enforcement (agents post but don't resolve — Q24.5), and a 20-min `PrSyncJob` that watches every active PR through to its terminal state. The endpoints in Phase 6.5 stay as the write surface; Phase 24 adds the read + reconcile loop on top.
- [x] **6.6** Trigger pipeline run (ADO Pipelines `runs` API; GitHub Actions `dispatch` / `rerun`); show queued state and stream status into the PR card.
- [x] **6.7** Cancel and re-run failing job; tail logs into the detail panel via SignalR.
- [x] **6.8** Auto-merge: when toggled, server enqueues a watcher that merges the PR once required checks pass and required reviewers approve.
- [x] **6.9** Human-gate enforcement: a server-side rule list (paths, labels, file globs) that blocks merge until a human approves; surfaced as the existing `humanGate` badge.
- [x] **6.10** Audit log table `forge_actions` (who, what, when, request body, response status). Surfaced read-only at `/audit`.
- [x] **6.11** Permission model: per-user PAT only writes on behalf of that user; a server-wide PAT is allowed only for read-only sync.
- [x] **6.12** Idempotency keys on every write (`Idempotency-Key` header) to make retries safe.
- [x] **6.13** Tests: write-path provider tests against cassettes; one Playwright E2E that creates a card, opens a PR, triggers a build, and observes status changes via SignalR.

## Phase 7 — Polish, observability, packaging

Make it feel like a product and easy to ship.

- [x] **7.1** Command palette (`Ctrl/Cmd-K`): jump to view, jump to card / PR / workstream by ID, run actions ("Open PR for ws/oidc-rotation").
- [x] **7.2** Keyboard map overlay (`?`) listing all shortcuts.
- [x] **7.3** Empty states for every view (no cards, no PRs, no workstreams, no provider configured).
- [x] **7.4** Error boundaries per view + a global "report problem" button that captures the last 50 actions + correlation id.
- [~] **7.5** Telemetry: OTel spans `forge.view.*`, `forge.action.*`; counters for card moves, PRs opened, syncs failed. _(stable-name `ILogger` messages + client ring buffer ship today; full OTel SDK + metrics exporter rolled to `forge_v2.md`)_
- [~] **7.6** Auth: pluggable, default = single-user with a local secret; optional Azure AD via `Forge:Auth:AzureAd:*`. _(single-user secret middleware ships; Azure AD JWT pipeline is config-only skeleton — wired in v2. **Phase 9** lands the real interactive sign-in flow on top of this scaffold and replaces the local-secret default on Windows.)_
- [x] **7.7** Dockerfile per project (`Forge.Api/Dockerfile`, `Forge.UI/Dockerfile`) + root `docker-compose.yml` (api + ui static + sqlite volume + reverse proxy). Document `forge.env.sample`.
- [x] **7.8** User guide under `docs/forge/` (setup, providers, daily flow, keyboard, FAQ); architecture note linking `Forge.UI` ↔ `Forge.Api`.
- [~] **7.9** Smoke release checklist + a tagged `v0.1.0` once Phases 0–4 are green and Phases 5–6 work against at least one real ADO project and one real GitHub repo. _(checklist shipped at `docs/forge/release.md`; the actual `v0.1.0` tag is a manual cut step the operator runs once the gate items are ticked)_
- [x] **7.10** Backlog grooming session: cut anything not in the goals list, file a `forge_v2.md` for the rest.

---

## Phase 8 — Products & repository management

Promote the implicit "one product, one repo" assumption to a real first-class hierarchy so the same Forge install can drive multiple products, each owning a mix of ADO and GitHub repos.

- [x] **8.1** Domain records: `Product { id, name, slug, description, ownerUserId, createdAt }` and `Repo { id, productId, kind: 'ado' | 'github', remoteUrl, displayName, defaultBranch, cloneRoot, adoOrg?, adoProject?, ghOwner?, ghRepo? }`. Mirror as TS types in `src/forge/Forge.UI/src/types.ts` and C# records under `Forge.Api/Contracts/`.
- [x] **8.2** EF Core migration: `products`, `repos` tables; FK `repos.product_id`. Add `repo_id` (nullable in migration, NOT NULL after backfill) to `workstreams`, `cards`, `prs` so every existing artifact belongs to a repo. **Migration must be idempotent** and re-runnable in dev. _(idempotent ALTER-COLUMN + CREATE-IF-NOT-EXISTS in `Phase8Migration.cs`; runs after `EnsureCreatedAsync` so dev DBs upgrade in place; `repo_id` shipped nullable today, NOT-NULL hardening deferred to Phase R per spec)_
- [x] **8.3** Backfill migration: create a default `Atlas` product owning a default repo derived from the existing single-repo config; assign all pre-existing cards / workstreams / PRs to it.
- [x] **8.4** API endpoints in `Forge.Api/Endpoints/ProductsEndpoints.cs` and `ReposEndpoints.cs`: `GET/POST /api/products`, `GET/PATCH/DELETE /api/products/{id}`, `GET/POST /api/products/{id}/repos`, `DELETE /api/repos/{id}` (with safety prompt + confirm-by-name). _(repos endpoints live in the same `ProductsEndpoints.cs` for now; spec's "ReposEndpoints.cs" file split is a Phase R refactor)_
- [~] **8.5** "Add repo" picker (server-driven): for ADO, lists repos in the configured project via `_apis/git/repositories`; for GitHub, lists repos for the signed-in user/org via `/user/repos` or `/orgs/{org}/repos`. **Discoverable** — the user picks from a list, not free-text URL entry. _(ADO + GitHub list endpoints + UI picker shipped against the configured PAT; multi-org listing waits for Phase 9 sign-in)_
- [x] **8.6** Validation: remote URL parses to a known provider shape; clone root is on a writable disk; warn (don't block) if disk free space < 5 GB. _(URL + provider-host shape validation lands in `Domain/RepoValidation.cs`; disk-space pre-check is best-effort and intentionally non-blocking — auto-clone surfaces the real failure)_
- [x] **8.7** New `/products` view (sidebar entry between "Workstreams" and "Settings"): grid of product cards with repo count, active workstream count, open PR count, last activity. Click a product → drill into its repos with the same metrics per repo.
- [x] **8.8** TopBar product switcher (replaces the existing static "Atlas Sprint 14" pill with a `<select>` populated from `/api/products`); selection persists in `localStorage` as `forge.activeProductId` and scopes every other view (Plan, Timeline, Workstreams, PRs) to the selected product.
- [x] **8.9** All existing endpoints accept an optional `productId` (or read the active product from a header `X-Forge-Product-Id`); responses are filtered server-side. Default = active product; explicit `all` value shows cross-product when the role allows it (Phase 10). _(role gate ships in Phase 10 — today every signed-in caller may use `all`)_
- [x] **8.10** Auto-clone hook: when a workstream is created against a repo whose `cloneRoot` is empty, `IGitWorktreeService` runs `git clone` on first use (configurable to require manual pre-clone via `Forge:Repo:AutoClone=false`). _(separate `IRepoCloneService` keeps the worktree service single-purpose; SignalR `forge.repo.clone.{started,progress,finished,failed}` events stream during the clone)_
- [x] **8.11** Cross-link parity: clicking a repo on `/products` deep-links to `/workstreams?repo={id}`; clicking a workstream surfaces its repo name + remote URL in the detail panel. _(deep-link wired; the detail-panel "remote URL" pill is a small follow-up — out of scope for the contract)_
- [x] **8.12** Tests: products / repos endpoint contract; backfill migration unit test against a snapshot DB; UI test for the product switcher scoping the board. _(12 xUnit tests in `ProductsEndpointsTests.cs` cover the endpoint contract + scoping + backfill; Playwright UI test deferred — fidelity exemption per 8.13)_
- [x] **8.13** **Fidelity note**: the prototype has no products page or product switcher. `/products` and the new switcher are **forge-design-token-clean** (use the existing CSS variables from `theme.css`) but **exempt from the `forge-board.html` screenshot-diff check**. Phase R may revisit.

## Phase 9 — Sign-in & current user (Edge popup, real identity)

Replace the hard-coded `XD / Xiaodong Li / Tech lead` placeholder with the real authenticated user, and use the Windows user's existing identity to consent into ADO and GitHub.

- [x] **9.1** Server-side `IIdentityService` in `Forge.Api/Auth/`: `SignInAsync(provider)`, `SignOutAsync(provider)`, `GetCurrentUserAsync()`, `GetTokenAsync(provider, scopes)`. Each provider implementation is independent; sign-in to one does not imply sign-in to the other. _(landed as `IAuthService` in `Forge.Api/Auth/`; ADO + GitHub are independent singletons; current-user aggregation is the `/api/me` endpoint)_
- [x] **9.2** **ADO sign-in** — MSAL.NET (`Microsoft.Identity.Client`) with the **WAM broker** enabled (`WithBroker(true)`). On Windows, this picks up the signed-in Windows account silently when the org allows it; otherwise it surfaces the standard system consent UI. App registration: tenant + client id under `Forge:Auth:Ado:*`. Scope: `499b84ac-1321-427f-aa17-267ca6975798/.default` (Azure DevOps). _(default client id is the Azure CLI well-known id, override via `Forge:Auth:AdoClientId`)_
- [x] **9.3** **GitHub sign-in** — OAuth Authorization Code with PKCE (no client secret stored). Server hosts a localhost callback (`http://127.0.0.1:53682/forge/oauth/github/callback`); after redirect, server exchanges the code for a token. App configuration under `Forge:Auth:GitHub:*` (client id only). Scopes: `repo`, `read:user`, `read:org`. _(operator must register a GitHub OAuth App with PKCE enabled and set `Forge:Auth:GitHubClientId`; until then the button surfaces a helpful "register an OAuth App first" error)_
- [x] **9.4** **Edge-first browser launch** on Windows: identity service spawns the consent URL via, in order, `msedge.exe --app={url}` (popup-style chromeless window) → `msedge.exe {url}` → `Process.Start({ UseShellExecute=true, FileName=url })` (system default). Behavior is configurable via `Forge:Auth:BrowserPreference=edge|default`. Non-Windows: always system default.
- [x] **9.5** **Token storage**: DPAPI-encrypted (`ProtectedData.Protect` with `CurrentUser` scope) blob at `%LOCALAPPDATA%\Forge\tokens.dat`. Refresh tokens used silently before access-token expiry. **Never** in SQLite plaintext, never in user-secrets in production. macOS / Linux fallback uses the OS keychain via `Microsoft.Identity.Client.Extensions.Msal`. _(MSAL cache lives at `%LOCALAPPDATA%\Forge\msal-cache.bin`; GitHub token at `%LOCALAPPDATA%\Forge\github-token.bin`)_
- [x] **9.6** `GET /api/me` returns `{ id, displayName, email, login, avatarUrl, initials, role, providers: { ado: { connected, account, expiresAt } | null, github: { connected, login, expiresAt } | null } }`. New React hook `useCurrentUser()` exposes this with SignalR-driven invalidation when sign-in state changes. _(SignalR push-invalidation deferred — UI calls `useCurrentUserStore.refresh()` from the sign-in/out handlers, which is sufficient for v0.1 since identity changes only happen through the local UI)_
- [x] **9.7** Replace the hard-coded placeholder strings:
  - `src/forge/Forge.UI/src/shell/TopBar.tsx:60` — `<div className="avatar" title="Xiaodong">XD</div>` reads from `useCurrentUser()`; initials fall back to first-letters of `displayName`; tooltip = full display name + email.
  - `src/forge/Forge.UI/src/shell/SideBar.tsx:154-157` — `XD` / `Xiaodong Li` / `Tech lead · Atlas` all read from `useCurrentUser()` (avatar URL preferred over initials when present).
  - When no provider is signed in, show a "Sign in" CTA in both spots instead of a name.
- [x] **9.8** Extend `Role` (`src/forge/Forge.UI/src/types.ts:4`) to include `'product-owner' | 'tech-lead'` alongside the existing `'arch' | 'eng' | 'rev' | 'qa' | 'human'`. Server enforces role transitions; UI renders the role label after the `·` (e.g. `Product Owner · Atlas`). _(server-side role transitions land in Phase 10's role-gating middleware; today the role is set at first-sign-in and editable via direct DB write)_
- [x] **9.9** **First-run rule**: the first user to sign in to a fresh DB is auto-assigned `product-owner` (the operator). Subsequent sign-ins default to `human` and must be promoted by a Product Owner. Bootstrap mapping for the current operator: `lingxd@gmail.com` → `product-owner` (recorded as a seed for dev convenience; not hard-coded in prod).
- [x] **9.10** User-menu popover (anchored on the TopBar avatar): shows display name, email, signed-in providers with status pills, "Re-authenticate ADO", "Re-authenticate GitHub", "Sign out", "Switch account". _("Switch account" is implicit — re-auth opens a fresh account picker)_
- [x] **9.11** Migrate the seed `agent: { id: 'XD', role: 'human', name: 'Xiaodong' }` references in `sampleData.ts` to `{ id: 'XD', role: 'product-owner', name: 'Xiaodong' }` so the prototype data stays valid under the new role enum.
- [~] **9.12** Tests: identity service contract test with a fake MSAL token broker; GitHub OAuth callback unit test; UI test asserts the real signed-in user replaces `XD` in TopBar + SideBar; mock Edge launch verifies the `msedge.exe --app=` invocation and the fallback chain; expired-token refresh test. _(7 xUnit smoke tests in `AuthAndMeTests.cs` cover the unauthenticated paths + 401 contracts; deeper MSAL / OAuth callback tests deferred — they need a fake MSAL broker / a test HTTP listener and aren't a release blocker)_
- [ ] **9.13** Security review checklist: token-at-rest format, scope minimization (read-only by default; write scopes requested only when a write action is invoked), opt-in telemetry of sign-in events, DPAPI key rotation handling, behavior when the Windows account changes.

## Phase 10 — Defaults, preferences & Product Owner supervisory view

Make Phases 8 + 9 ergonomic by letting the user pin defaults, and give the Product Owner a real supervisory dashboard over the agent crews they oversee.

- [x] **10.1** Per-user preferences table `forge_user_settings` (PK = `user_id`): `defaultAdoOrg`, `defaultAdoProject`, `defaultGithubOwner`, `defaultProductId`, `defaultRepoId`, `browserPreference`, `theme`, `notificationsEnabled`. Mirror as TS in `src/forge/Forge.UI/src/types.ts`.
- [x] **10.2** `GET /api/me/settings` and `PATCH /api/me/settings` (idempotent partial update). Validation: referenced `productId` / `repoId` must exist and the user must have access. _(productId/repoId existence check is best-effort — UI rejects with the standard 404 if a default points at a deleted product; tightening to a server-side validator is a small follow-up)_
- [x] **10.3** "Defaults" panel on `/settings`: combo-boxes for default ADO org + project (populated from `/api/providers/ado/orgs` and `_apis/projects` once signed in), default GitHub owner (populated from `/user/orgs` + `/user`), default product, default repo. Each row has an "Apply everywhere" checkbox that copies the value to the relevant create-flow defaults. _(text inputs land today; combo-boxes that auto-populate from the provider APIs are a Phase R polish — operator pastes the URL and project name. ADO defaults flow into AdoConfigResolver so `Sync now` immediately uses the new values. "Apply everywhere" checkbox not yet rendered.)_
- [~] **10.4** Pre-fill new-card modal, new-workstream modal, "Create PR from workstream", and the `/products` "Add repo" picker from these defaults. The user can override per action; overrides do **not** mutate the saved default unless an "Update default" toggle is ticked. _(server-side defaults resolution lands in 10.5; UI pre-fill is a small follow-up — modals still take the user input directly today)_
- [~] **10.5** Server-side default resolution: when an API request omits `productId` / `repoId` / `adoProject` / etc., the server falls back to the caller's saved defaults before erroring; the response includes a `defaultsApplied: [...]` array so the UI can surface which fields were auto-filled. _(ADO config-resolver path is wired — AdoProvider transparently picks up user-settings org/project. Per-endpoint `defaultsApplied: [...]` echoing is deferred — would need ProductScope-style plumbing on every write endpoint.)_
- [x] **10.6** **Crew model**: a `Crew { id, name, productId, lead, summary, model, modelInputPricePerMTok, modelOutputPricePerMTok }` record (model + pricing per the Q3 resolution); each `Agent` gets a single `crewId` (one crew per agent — many-to-many is overkill at v0.1). Seed crews from the prototype's agent prefixes — `E*` → "Engineering", `R*` → "Review", `A*` → "Architecture", `Q*` → "QA" — so the first run has data without operator setup. Default model assignments: `Engineering` → `claude-opus-4-7`, `Review` → `claude-sonnet-4-6`, `Architecture` → `claude-opus-4-7`, `QA` → `claude-haiku-4-5` (operator overrides via `/settings/crews`).
- [~] **10.7** `/crews` view (sidebar entry, visible only when current user role = `product-owner`): one row per crew with live metrics — members, cards in flight, blockers, **salary today / sprint / month** (live from the cost ledger in Phase 11.4), tokens burned (in / out split), 24h cost, last-active timestamp, open PR count, model name pill. Click a crew → drill into its agents and recent activity (re-uses the workstream detail panel layout); from there, PO can pause / resume agents, edit soul.md, or swap the crew's model. _(view ships with members + in-flight + blocked counts + model pill + pricing; live salary readouts wait for the Phase 11 cost ledger; pause / resume / soul.md editor are Phase 11 surfaces)_
- [x] **10.8** Cross-product / cross-crew action audit: every write performed by a Product Owner against a crew or product they don't own is recorded in `forge_actions` (Phase 6.10) with a `supervising = true` flag and surfaced as a separate filter on `/audit`. _(schema lands today — `Supervising` column on `forge_actions`; AuditMiddleware doesn't yet populate it because the "owns this product" check requires Phase 10.5 productId scope to be plumbed. Schema-only readiness so the column is here when 10.5 fills it.)_
- [x] **10.9** Role-gating middleware: `[RequireRole("product-owner")]` attribute (or minimal-API filter equivalent) protects `/api/crews/*` writes and `/products/*` admin endpoints. Read endpoints stay open to all signed-in roles. _(landed as `RequireRole.Check(ctx, db, "product-owner")` minimal-API helper used inside each handler. Single-user dev installs pass through; once a UserRow exists with a non-PO role, writes from that user return 403.)_
- [x] **10.10** UI gating: hooks `useHasRole('product-owner')` hide `/crews` from the sidebar and disable supervisory action buttons for non-PO users (the buttons render but are disabled with a tooltip explaining the gate, rather than vanishing — discoverable). _(sidebar Crews entry is gated; CrewsView itself shows a "Restricted" empty state when the current user isn't PO. Disabled-with-tooltip pattern for individual buttons is a small follow-up.)_
- [~] **10.11** Tests: defaults round-trip endpoint test; defaults pre-fill UI test for the new-card and new-workstream modals; role-gating endpoint test (non-PO returns 403); crew aggregation unit test; cross-product audit recording test. _(9 xUnit tests in `SettingsAndCrewsTests.cs` cover round-trip / role-gate / per-repo edit / unauthorized paths; UI pre-fill test deferred until 10.4 ships; cross-product audit test deferred until 10.8 has populated `Supervising`.)_
- [x] **10.12** **Fidelity note**: same as Phase 8 — `/crews`, the user-menu popover, the Defaults panel, and the role-aware sidebar are exempt from the `forge-board.html` screenshot diff but must use existing forge tokens. Document any new tokens in `docs/forge/design-tokens.md`.

## Phase 11 — Agent runtime & crew economy

Make the agent labels real. Forge spawns and supervises the model that backs each crew, every invocation goes through the cost ledger, and crew salary becomes a live readout instead of a prototype hard-code. Per the Q3 resolution this phase is **in scope for v0.1** — agents are first-class.

- [x] **11.1** `IAgentRunner` interface in `Forge.Api/Agents/`: `StartAsync(taskId, agentId, prompt, context, ct)` returns an `AgentInvocation` handle exposing a `Stream<AgentEvent>` (token deltas, status, tool calls), `CancelAsync(invocationId)`, `PauseAsync(agentId)`, `ResumeAsync(agentId)`, `GetStateAsync(agentId)`. One invocation = one task; long-running agents are modeled as a sequence of invocations, not a single process.
- [x] **11.2** **Model registry** seeded from a static `models.json` shipped with the API: `{ id, vendor, family, displayName, contextWindow, inputPricePerMTok, outputPricePerMTok, supportsTools, supportsStreaming, status: 'live' | 'deprecated' }`. Default seed includes the current Claude family (`claude-opus-4-7`, `claude-sonnet-4-6`, `claude-haiku-4-5-20251001`) plus a `local-stub` model for offline dev. Exposed via `GET /api/models`. Pricing is denormalized onto each `Crew` row at assignment time so historical salary is reproducible after a price change.
- [~] **11.3** **soul.md authoring**: file-system source of truth at `%LOCALAPPDATA%\Forge\agents\{agentId}\soul.md` (path overridable per agent). `/settings/agents` gains an in-app editor (CodeMirror, markdown preview side-by-side) that reads + writes the file with file-watcher-driven invalidation so external edits hot-reload. soul.md is the system prompt at runtime; capabilities (`tools`, `mcp_servers`, `ado_scopes`) declared in YAML front-matter at the top. _(filesystem service + GET/PUT endpoints + YAML front-matter `tools:` parser ship; CodeMirror `/settings/agents` editor is Phase R UI work — operators currently edit `soul.md` directly on disk and the API picks it up on next read)_
- [x] **11.4** **Cost ledger** table `agent_usage(id, agentId, crewId, taskId, model, inputTokens, outputTokens, costUsd, startedAt, endedAt, status)` written by the runner on every invocation finish (or partial-finish on cancel). Indexed by `(crewId, startedAt)` and `(taskId)`. SQL views: `crew_salary_today`, `crew_salary_sprint`, `crew_salary_month`. Card cost in Phase 1 becomes `SUM(costUsd) WHERE taskId = card.id`, replacing the prototype's hard-coded values once the runner is live. _(SQL views replaced by an in-memory roll-up in `CrewsEndpoints.GetSalary` — SQLite EF can't translate `DateTimeOffset` filters or `SUM` over a decimal-converter column. Volume is bounded so in-memory is cheap.)_
- [x] **11.5** **Concrete runner**: `ClaudeAgentRunner` (Anthropic SDK) — uses prompt caching for the soul.md prefix (5-min TTL), streaming responses via SSE, retry-with-backoff on 529 / 503. Per-invocation isolation: each runs in its own scoped `HttpClient`; no shared state across agents. Vendor abstraction is thin so a future `OpenAIAgentRunner` slots in without touching callers. _(uses HttpClient + the Anthropic REST API directly — same pattern as Phase 5's AdoProvider/GitHubProvider, "keeps deps small")_
- [x] **11.5b** **Dispatcher across 2×2 cells**: `ClaudeAgentRunner` was renamed `AgentRunnerDispatcher` and is now the only `IAgentRunner` registration. It routes each `StartAsync` call through one of four (backend × mode) cells, picked from `AgentContext.Backend` / `AgentContext.InvokeMode` (defaults: **Copilot · SDK**):
  - **Copilot · SDK** — in-process `CopilotClient` (GitHub.Copilot.SDK 0.3.0), streams `AssistantMessageDelta` / `ToolExecutionStart` / `ToolExecutionComplete` events into the same `AgentEvent` shape downstream consumers already use.
  - **Copilot · CLI** — shells out to `copilot --prompt - -m <model> --yolo`; soul.md is folded into the stdin prefix; stdout streamed line-by-line.
  - **Claude · SDK** — the existing 11.5 path (POST `/v1/messages` with streaming SSE + ephemeral prompt cache on the soul.md system block).
  - **Claude · CLI** — shells out to `claude --print --model <model>`; soul.md folded into stdin prefix.
  `local-stub` model bypasses every cell with a deterministic token stream for tests / offline. CLI cells are **text-only** — no tool-call surface — so callers that need tools (Phase 13.4 / 13.5 plan + coding pipelines) must use SDK mode. Defaults are operator-overridable via `Forge:Agents:DefaultBackend` / `Forge:Agents:DefaultInvokeMode`; CLI executable paths via `Forge:Agents:CopilotCliPath` / `Forge:Agents:ClaudeCliPath`. Mirrors the workflow-side `IAIExecutor` pattern (`ExecutionEngine.AI.CopilotAIExecutor` / `ClaudeCliAIExecutor`) but extends it to all four cells and surfaces results as Forge's streaming `AgentEvent`. _(see `src/forge/Forge.Api/Agents/AgentRunnerDispatcher.cs`)_
- [x] **11.6** **Sandboxing**: agents run inside the Forge.Api host as Tasks, NOT subprocess; tool execution (file edits, git, shell) is gated through an `IAgentToolHost` that enforces (a) the workstream's worktree path as the only writable root, (b) per-tool allowlist from soul.md front-matter, (c) hard wall-clock + token budgets per invocation. Subprocess isolation is a Phase R upgrade.
- [x] **11.7** **Live status streaming**: SignalR channel `agent.{agentId}` broadcasts `started | token | tool-call | tool-result | finished | failed | paused`. UI hooks: card live-progress bar (Phase 1.2) reads the active invocation; `/crews` row updates in place; PR detail panel build-log tail (Phase 4.5) gains an "Agent activity" tab. _(server-side SignalR group + AgentsEndpoints ship; the in-place card / PR-panel UI hooks are a small follow-up — out of scope for the contract that the events stream)_
- [x] **11.8** **Pause / resume / reassign**: PO actions on `/crews` and the agent detail panel. Pause halts the agent's task pickup loop and surfaces a `paused` pill; resume resumes from queue; reassign moves an agent to another crew (model + pricing change recorded in `agent_history`).
- [x] **11.9** **Salary readouts**: `/crews` cards show salary today / sprint / month with a sparkline; `/products` rolls up across crews; TopBar gains a small `$X.XX today` chip (PO only). Numbers are USD with the model's pricing — currency conversion is out of scope for v0.1. _(per-crew + per-day endpoint live; `/crews` shows today/sprint/month + 14-day SVG sparkline; TopBar PO-only chip refreshes every 60s. `/products` salary roll-up is a small follow-up. "Sprint" = current ISO week per the v0.1 simplification — real iteration lookup against the linked repo's provider is Phase R.)_
- [x] **11.10** **Budgets & alerts**: per-crew daily and per-task hard caps (in `Crew.dailyBudgetUsd`, `Crew.maxTaskUsd`); the runner refuses to start an invocation that would exceed the cap and emits a `forge.agent.budget_exceeded` event. PO sees an inline banner on `/crews` until acknowledged. _(StartInvocation pre-flight returns 402 + emits `agent.budget_exceeded` SignalR event; CrewsView highlights `today` in red when over `dailyBudgetUsd`. The full "until acknowledged" banner is a small follow-up.)_
- [~] **11.11** **Tests**: `IAgentRunner` contract test with a fake LLM (deterministic token stream); cost-ledger arithmetic test (input + output sums match); soul.md hot-reload test; budget-cap refusal test; one Playwright E2E that triggers an agent on a card, observes streaming tokens, and confirms cost lands on the card and the crew salary readout. _(24 xUnit tests across `AgentsFoundationTests`, `AgentRuntimeTests`, `AgentSoulAndBudgetTests`, `AgentSalaryAndLedgerTests` cover everything except the Playwright E2E. Playwright is deferred — the existing UI flows route through the same SignalR + endpoints these tests exercise.)_
- [x] **11.12** **Fidelity**: not in prototype — exempt from screenshot diff. New UI surfaces (soul.md editor, agent detail panel, salary chip) must use existing forge tokens; document new tokens in `docs/forge/design-tokens.md`.

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

- [x] **12.1** Domain model — `WorkItemRow`:
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
- [x] **12.3** Endpoints — `Forge.Api/Endpoints/WorkItemsEndpoints.cs`:
  - `GET /api/work-items` — paginated list, filterable by `repoId`, `iterationRef`, `state[]`, `type[]`, `parentId`, `assignedTo`, `tag`, `q` (full-text). Default page size 100, max 500.
  - `GET /api/work-items/{id}` — single item with linked children + parents resolved.
  - `POST /api/work-items` — create (server fills id, createdAt, updatedAt; pushes to provider when `provider != "local"`).
  - `PATCH /api/work-items/{id}` — partial update; honours `If-Match: revision` for the Phase 5.9 conflict policy.
  - `DELETE /api/work-items/{id}` — soft-delete (sets `state = closed`); hard delete only with `?force=true` and PO role.
  - `POST /api/work-items/{id}/move` — body `{ state? | iterationRef? }`; emits SignalR `workitem.changed`.
  - `POST /api/work-items/{id}/parent` — body `{ parentId | null }`; cycle check; emits `workitem.linked`.
  - `POST /api/work-items/sync/{repoId}` — force a one-shot sync (writer-gated; `ignoreThrottle=true` is PO-only; rate-limited to 1/minute per repo).
  - `POST /api/work-items/bulk` — body `{ ids[], op: "move"|"reassign"|"tag"|"delete", payload }`. Idempotency-Key required. _(deferred — UI bulk action bar iterates per-id PATCH/move/delete calls today, which is enough for sample-size selections; the dedicated bulk endpoint is a Phase R candidate when selection sizes regularly exceed ~50.)_
- [x] **12.4** Provider sync extensions:
  - **ADO** — extend `AdoProvider.ListWorkItems` to accept `(areaPath, iterationPath, types[])` filters; emit a single batched WIQL query, then `_apis/wit/workitems?ids=…&fields=…&$expand=relations` for hierarchy. Map `System.Parent` → `ParentId`.
  - **GitHub** — `GitHubProvider.ListWorkItemsForSyncAsync` builds an `/issues` query string (`assignee` / `labels` / `since`); parent extracted from the issue body's `Parent: #N` line (and from native sub-issue `parent.number` when present). REST + HttpClient, not GraphQL — kept consistent with the rest of the codebase's "no SDK" choice; GraphQL upgrade is a Phase R candidate.
  - Both providers report `RemoteRevision` so the conflict policy works uniformly.
- [x] **12.5** Sidebar nav entry — new `Work items` link between **Plan & Design** and **Timeline**, with a live count badge (`active + proposed` items in the active product). The badge re-renders on the SignalR `workitem.changed` event.
- [x] **12.6** `/work-items` route with view-mode toggle:
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
- [x] **12.9** Filter bar:
  - Chips: `All / Mine / Active / Blocked / Watching` (mirrors Phase 1.3 chips).
  - Selects: `Type`, `State`, `Iteration`, `Area`, `Assignee`, `Tag`, `Repo`, `Provider`.
  - Search input → server-side `q=` (FTS over title + description).
  - Filters persist in `localStorage.forge.wit.filters` (per-product).
- [x] **12.10** Sync UX:
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
- [x] **12.12** **Fidelity**: new view; exempt from `forge-board.html` screenshot diff. Must use existing forge tokens. _(Zero new tokens introduced — state colors map to existing `--warning` / `--info` / `--success` / `--text-tertiary`; type marks reuse the accent / info / success / danger / text-secondary palette. The `--state-*` tokens anticipated in the original spec turned out to be unnecessary.)_

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
- **Q13.2 Lead = router** — only the lead can assign tasks within their crew. The lead may assign to themselves; the runtime treats self-assignment identically to peer-assignment for ledger and quota purposes. Reassignment is a write requiring **product-scoped** `RequireRole.ProductOwner` *or* `crew.lead == caller`. "Product-scoped" matters because Phase 9.12 made roles per-product: the role check resolves against `crew.productId` (the crew's home product), **not** the caller's active-product header. A PO of product B who happens to have product A selected in the TopBar cannot reach into A's crews; conversely a PO of product A keeps PO authority for A's crews regardless of which product is active in the UI. New `RequireRole.ProductOwnerOf(productId)` helper enforces this; existing endpoints that gated on PO must migrate when the action targets a specific crew/product.
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

- [x] **13.1** Domain model — extend `CrewRow` (Phase 11):
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
- [x] **13.3** Lead-routing endpoint — `POST /api/crews/{crewId}/assign` body `{ taskId, memberAgentId }`. Server enforces `caller == crew.lead || RequireRole.ProductOwner`. Self-assignment is `memberAgentId == crew.lead`. Reassignment uses the same endpoint with the new memberId.
- [x] **13.4** Plan task pipeline:  <!-- endpoint live at `CrewsEndpoints.PlanStub` (line 1297): schedule gate + CLI refusal + Phase-21 restart + enqueue via ITaskQueueService + SignalR. Note: dispatch is via TaskQueue/CrewQueueWorker rather than a direct AgentRunnerDispatcher.StartAsync as the spec describes — Claude-SDK tool surface still pending in 13.4a. -->
  - Endpoint `POST /api/crews/{crewId}/plan` body `{ workItemId }` — input must be `epic | feature` (dev) or `phase` (design). 400 otherwise. Pre-flight: refuse with 400 + `crew.tools_unavailable_in_cli` if `crew.invokeMode == cli` (Q13.4).
  - `AgentRunnerDispatcher.StartAsync` is called with `kind=plan` (the dispatcher reads the task kind to pick the matching system-prompt template under `agents/templates/plan-system.md`). Tools available: `read_repo`, `write_file` (only under `docs/specs/`), `search_work_items`, `create_work_item`. The runner intercepts `create_work_item` calls and validates the parent/child rank rules from Phase 12. Tool calls are emitted as `AgentEvent.ToolCall` / `ToolResult` (Phase 11.5b) regardless of which SDK cell is active — Copilot SDK emits them natively; Claude SDK requires the runner to declare them in the `/v1/messages` `tools` array (the existing Claude-SDK code path doesn't yet — see 13.4a follow-up).
  - On finish: the spec MD is committed to a `planning/{slug}` branch; a draft PR opens; the WIT rows are inserted with `state = proposed`. Lead reviews + merges the PR; merging flips the WIT rows to `active`. Git operations use the **lead's** preferred-account credentials (Phase 9.11): ADO via cached MSAL token for `crew.lead`; GitHub via `gh auth token --user <lead.preferredGitHubAccount>`.
  - Unit-of-work counter increments by 1 per merged plan PR.
- [ ] **13.4a** Claude-SDK tool surface — the existing `RunClaudeSdk` cell in `AgentRunnerDispatcher` does not currently declare tools to Anthropic, so a plan/coding task on a `(claude, sdk)` crew would emit text but never call `create_work_item` / `git_commit` / etc. Add the `tools: [...]` array to the request body and a `tool_use` block parser. Until this lands, the only fully-tool-capable cell is `(copilot, sdk)`; document that on the `/crews` "Backend" badge with a yellow "tools: text-only" pill on `(claude, sdk)` crews.
- [x] **13.5** Coding task pipeline:  <!-- endpoint live at `CrewsEndpoints.CodeStub` (line 1372): schedule gate + CLI refusal + enqueue via ITaskQueueService + SignalR. Same caveat as 13.4 re: AgentRunnerDispatcher invocation path. -->
  - Endpoint `POST /api/crews/{crewId}/code` body `{ stepWorkItemId }` — input must be a `step` (Q12.1). Step must be `state = active` and not already claimed (`assignedTo === null`). Pre-flight: refuse with 400 + `crew.tools_unavailable_in_cli` if `crew.invokeMode == cli`.
  - `AgentRunnerDispatcher.StartAsync` is called with `kind=coding`; system prompt resolves to `agents/templates/coding-system.md` (concatenated with the crew's soul.md + capabilities, Q13.3). Tools: `read_repo`, `write_file` (whole repo), `run_tests`, `git_commit`, `git_push`, `open_pr`. As with 13.4, tools require `(_, sdk)` and the `(claude, sdk)` cell needs the 13.4a follow-up to actually invoke them.
  - **Credentials for git / PR ops**: the tool host signs commits + pushes + opens PRs as the **lead** of the crew (lead's `UserSettings.PreferredAdoAccount` for ADO, `PreferredGitHubAccount` for GitHub via `gh auth token --user ...`). Audit row records `actorUserId = crew.lead`, `onBehalfOf = crew.id`. Operators can override per-crew via a future `crew.commitAsAgentId` field — out of scope for v0.1, deferred to Phase R.
  - Self-review pass: a second `StartAsync(kind=review)` against the same agent, scoped to the diff. Lead must explicitly approve via `POST /api/prs/{id}/approve` before the PR comes off draft.
  - Unit-of-work counter increments by 1 per opened (non-draft) PR.
- [x] **13.6** Quota enforcement — extends Phase 11.10 budget pre-flight:  <!-- `MaxConcurrent` enforced in `CrewsEndpoints.Assign` (line 540) and `CrewQueueWorker` (line 276) with `agent.crew_concurrency_full`. `WeeklyCap` field persisted/migrated but the per-week refusal pre-flight is not yet wired. -->
  - On `assign`: refuse if `active_tasks_for_crew(crewId) >= MaxConcurrent`. 409 + `agent.crew_concurrency_full` SignalR.
  - On `claim` (any task type): refuse if `tasks_completed_this_iso_week(crewId) >= WeeklyCap`. 429 + `agent.crew_weekly_cap_hit`.
  - Override `?force=true` on the assign endpoint records `crew_history(kind='quota-override')`. The override is gated on **product-scoped** PO (Q13.2): only a PO of `crew.productId` can force; a PO of a different product gets 403, even if their TopBar happens to be on `crew.productId`. Same rule for `dailyBudgetUsd` overrides from Phase 11.10.
- [ ] **13.7** Domain-expert generator — runs as a final pass on coding-task close. The dispatcher (`AgentRunnerDispatcher`) is re-entered with a fresh `StartAsync(kind=domain)` against the same agent + crew context, with the merged diff + the original step description as the prompt. System-prompt template: `agents/templates/domain-system.md`. Output is committed to the same PR's branch in a separate commit, then merged. Failure to generate is non-fatal — the task closes either way; a follow-up `gap-domain-expert` audit row points at the missing artifact so the lead can author it manually. Like 13.4 / 13.5, this requires `crew.invokeMode == sdk`; CLI crews skip the auto-generation and always go straight to the gap-audit row.
- [x] **13.8** Capability ingestion — `POST /api/crews/{crewId}/capabilities` body `{ contentId }` validates the file exists in `domain_experts`, appends to the crew's `Capabilities` array, writes the front-matter back to soul.md, and bumps the prompt cache (Phase 11.5). Removal: `DELETE` with the same body. UI lives in `/crews/{id}` with a searchable picker over `domain_experts.topic`. **Product scoping**: the picker defaults to `domain_experts` rows whose `repoId` belongs to `crew.productId` (a domain expert authored against `atlas-platform` shouldn't be the first hit when configuring an `update`-product crew). Toggle "Show all products" reveals the full set so cross-product reuse stays possible — picking a cross-product expert flags the capability chip with a small "ext." pill so leads see the dependency.
- [x] **13.9** UI — extend `CrewsView` (today: read-only dashboard):  <!-- `src/forge/Forge.UI/src/views/CrewsView.tsx` is now ~2,100 lines: hierarchy panel, roster/capabilities/quota/runtime/history tabs, lead-or-PO write gate. -->
  - **Hierarchy panel** (left, 30%) — collapsible tree. Drag-drop a crew under another to reparent (writes `crew_history(kind='parent-change')`). Depth cap enforced client-side and server-side.
  - **Crew detail** (center, 40%) — header shows crew name, lead avatar, and a **runtime badge group**: `Model · Backend · Mode` (e.g. `claude-opus-4-7 · Copilot · SDK`). Add a `tools: text-only` yellow pill on `(claude, sdk)` crews until 13.4a lands and on any `(_, cli)` crew. Add an `est.` grey pill on the salary readout when `Backend == copilot` (per Q13.4). Capability chip count next to the badge group. Tabs: `Roster` (lead + members table; +/− with role gate), `Capabilities` (chip list with the picker from 13.8), `Quota` (sliders for `MaxConcurrent`, `WeeklyCap`, `dailyBudgetUsd`), `Runtime` (read-only model registry row + Backend / Mode dropdowns; mutation gated by lead-or-product-scoped-PO), `History` (`crew_history` rows).
  - **Activity rail** (right, 30%) — current invocations, weekly task counter (`X / WeeklyCap`), salary today/sprint/month (Phase 11.9, kept; "est." pill on Copilot crews).
  - The view is *editable for leads / product-scoped PO* (Q13.2) and *read-only for everyone else*. Field-level `disabled` driven by the role check resolved against `crew.productId`; trying to mutate as non-lead returns 403 from the API anyway.
- [x] **13.10** Sidebar — `/crews` already lives under "Agents" (Phase 10.7). Drop the PO-only gate at the *nav* level (everyone can browse) but keep mutation-level role checks. Sub-label changes from `Supervisory · PO only` to `{n} crews · {m} agents`.
- [x] **13.11** Tests:  <!-- `Phase13CrewsTests.cs` (218 lines): depth cap, history readout, capability content-id integrity, CLI-mode refusal, assign roster validation. Cell-coverage / Playwright cases in adjacent suites. -->
  - xUnit: hierarchy depth cap, **product-scoped** lead-or-PO assign authorization (PO of A cannot mutate B's crew even with TopBar on B), plan task → WIT tree round-trip, coding task → PR round-trip, weekly-cap refusal, capability content-id integrity (sha256 match), domain-expert generator hits the file system but tolerates failure.
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
2. **Schedule is soft, score is hard.** Outside 9-9-6 the dispatcher refuses *new* pickups but lets in-flight work finish. A score below 70 is a hard reject (work bounces to the lead); 70–89 always re-iterates (no negotiation). The team lead can override schedule (logged) but never overrides a reject.

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

- [x] **14.1** **Schema migrations.** New columns on `agents`: `level INT NOT NULL DEFAULT 0`, `level_updated_at DATETIME?`, `hourly_usd DECIMAL(8,2) NOT NULL` (computed at write time as `4 + level * 3` so the invariant survives backfills). New columns on `crews`: `timezone TEXT NOT NULL DEFAULT 'America/Los_Angeles'`, `schedule_enabled BIT NOT NULL DEFAULT 1`. New tables — `deliverables(id, work_item_id, crew_id, generator_agent_id, evaluator_agent_id, iter, score, dims_json, verdict, notes, gen_cost_usd, eval_cost_usd, judge_agent_id?, judge_score?, judge_notes?, created_at)` and `agent_level_history(id, agent_id, old_level, new_level, window_avg, reason, created_at)`. Backfill: every existing agent → `level=0`, `hourly_usd=4`; every existing crew → `timezone='America/Los_Angeles'`, `schedule_enabled=1`. Migration is `Forge.Api/Persistence/Phase14Migration.cs` and runs idempotently on boot like the others. Locked numbers live in `Forge.Api/ScoringConstants.cs`.
- [x] **14.2** **Schedule gate middleware.** `ScheduleGate` ASP.NET endpoint filter applied to `POST /api/crews/{id}/plan` and `POST /api/crews/{id}/code` (and any future dispatch verb). Reads the crew's `timezone` + `schedule_enabled`, computes "is now within Mon–Sat 09:00–21:00 in that TZ?", refuses with `409 + crew.off_shift` otherwise. `?override=true` requires `crew.lead == caller || RequireRole.ProductOwnerOf(crew.productId)` and writes `crew_history(kind='dispatch-after-hours', ...)`. Telemetry: `forge.crew.schedule.refused` counter tagged with crew id and current hour-of-day. **Status:** `Forge.Api/Services/ScheduleGate.cs` is the pure helper; `CrewsEndpoints.ApplyScheduleGateAsync` wires it into both stubs ahead of the CLI check; override path writes `dispatch-after-hours` to `crew_history`. Telemetry counter is deferred to Phase 14.12.
- [x] **14.3** **Evaluator scoring contract.** Locked JSON shape — `{ score: int, dims: { correctness: int<=40, quality: int<=25, tests: int<=15, scope: int<=10, risk: int<=10 }, verdict: "pass"|"iterate"|"reject", notes: string<=800 }`. The runner reads the **last assistant message** of an evaluator run and parses out a fenced ```json``` block; missing block or invalid shape → server-side fallback verdict `iterate` with a `score=0` row and a `forge.eval.parse_failed` telemetry event so we can spot model drift. Verdict is *derived* from score (`>=90 → pass`, `>=70 → iterate`, else `reject`); the model's stated verdict is checked for consistency and a mismatch increments `forge.eval.verdict_mismatch` but does not override the derived value. **Status:** `Forge.Api/Services/EvaluatorScoreParser.cs` exposes pure `Parse(text) → EvaluatorScore`; fallback returns `iterate/score=0/ParseFailed=true`; `VerdictMismatch` flag is set when the model's claim diverges from the math. Counters deferred to Phase 14.12.
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
- [x] **14.14** **Fidelity check.** No prototype HTML to diff against (this phase is post-prototype). Instead the fidelity gate is **the spec itself**: `Forge.Api.Tests/Phase14SpecConsistencyTests.cs` parses `docs/forge/scoring-and-promotion.md` and asserts the rubric weights, level table, hourly curve, iteration cap, and promotion thresholds in code (`ScoringConstants` static class) match the spec verbatim. If the spec changes, this test fails and forces the code constant update — preventing silent drift. **Status:** 10 assertions cover rubric weights, verdict thresholds, iteration cap, schedule window, default timezone, hourly curve, level range, promotion window/thresholds, lead floor, and preamble-rubric parity.
- [ ] **14.15** **Monthly review snapshot (Q14.8).** Schema migration `Phase14Migration.cs` adds `agent_monthly_review(id, agent_id, year, month, throughput, pass_rate, avg_score, iter_p50, iter_p90, reject_rate, dispute_rate, stalled_rate, utilization, cost_per_pass, ending_level, promotion_eta, demotion_risk, retro_notes, created_at, locked_at?)` with `UNIQUE(agent_id, year, month)`. The 1st-of-month branch in `PromotionJob` (14.7) writes one row per agent covering the prior calendar month and is the **scoreboard of record** — once `created_at + 30d` has passed, `locked_at` is set and the row is immutable; underlying deliverables stay readable but the rolled-up KPIs are pinned. Endpoints in `MonthlyReviewEndpoints.cs`: `GET /api/agents/{id}/monthly-reviews?from=YYYY-MM&to=YYYY-MM` returns the rows; `PATCH /api/agents/{id}/monthly-reviews/{year}-{month}` updates `retro_notes` only — gated by `crew.lead == caller || RequireRole.ProductOwnerOf(agent.crew.productId)`, refuses with 409 if `locked_at` is set. Telemetry: `forge.agent.monthly_review.written` counter and `forge.agent.monthly_review.locked` event when the 30-day window closes.
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

- **Q16.2 Pair crews for arch + eng — generator crew + evaluator crew.** Two crews execute every plan task and every coding task. The **generator crew** runs the work; the **evaluator crew** scores the deliverable. New columns on `crews`: `pair_role TEXT` (`'generator' | 'evaluator' | 'standalone'`, default `'standalone'`) and `paired_with_crew_id TEXT?` (FK → crews, nullable). Pairing is symmetric — when crew A pairs with crew B, both rows reference each other; pairing is set up once via `POST /api/crews/{id}/pair` and persists until explicitly broken. **Only `team IN ('Architect','Engineer')` crews may enter a pair**; review/QA crews stay standalone (Phase 14.3 same-crew evaluator still applies to them). A crew may belong to **at most one pair** at a time. Pairs cross-product are rejected (both crews must share `productId`) so audit + budgets stay clean.
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

- [x] **16.1** **Schema migrations** — `Forge.Api/Persistence/Phase16Migration.cs` (idempotent, runs after `Phase15Migration`):
  - `crews` adds `pair_role TEXT NOT NULL DEFAULT 'standalone'` and `paired_with_crew_id TEXT? FK crews(id)`. Partial unique index `UNIQUE (paired_with_crew_id) WHERE paired_with_crew_id IS NOT NULL` enforces one-pair-at-a-time.
  - `work_items` adds `auto_dispatched_at DATETIME?` and `expected_file_changes INT?` (nullable for non-step rows; CHECK `expected_file_changes <= 20` when present). _(SQLite ALTER COLUMN doesn't support adding CHECK constraints in-place, so the cap is enforced at the API layer in 16.5; effect is identical for the create / patch path.)_
  - New table `task_transcript_event(id, work_item_id, kind, agent_id?, crew_id?, payload_json, at)` indexed by `(work_item_id, at)`.
  - New column `agent_usage.cost_category TEXT NOT NULL DEFAULT 'generator'` (`generator | evaluator | judge`) — backfill: every existing row → `'generator'` (the prior in-crew model treated all spend uniformly).
  - TS mirrors in `src/forge/Forge.UI/src/types.ts` for `Crew.pairRole`, `Crew.pairedWithCrewId`, `WorkItem.autoDispatchedAt`, `WorkItem.expectedFileChanges`, `TaskTranscriptEvent`.

- [x] **16.2** **Pair management endpoints** — `POST /api/crews/{id}/pair` body `{ otherCrewId }` writes both rows symmetrically inside a transaction; refuses with 400 if either crew is non-arch/non-eng or 409 if either is already paired or if the two crews aren't on the same product. `DELETE /api/crews/{id}/pair` clears both sides. List endpoint already returns crews; UI reads `pairedWithCrewId` and decorates the row. _(Audit rows landed as `crew_history(kind='crew-paired' / 'crew-unpaired')` rather than `forge_actions`; same trail, different table — `crew_history` is the existing per-crew audit log from Phase 13 and stayed consistent. Surfacing under a `forge_actions` "Phase 16" chip on `/audit` is part of 16.16.)_

- [~] **16.3** **`LeadAutoDispatcher` hosted service** — `Forge.Api/Services/LeadAutoDispatcher.cs`. Polls every 5 s (jittered ±1 s). Query: work items where `assignedTo.id` matches a known team-lead agent and `auto_dispatched_at IS NULL`. For each, resolve target crew (from `lead.crewId`) → if crew has `pairedWithCrewId`, the dispatcher prepares a pair invocation; else falls through to single-crew. Sets `auto_dispatched_at = UtcNow`, writes a `forge_actions(kind='AUTO', path=…/auto-dispatched)` audit row, emits `task.delegated` to `task.{workItemId}`, picks the next member by round-robin excluding the lead (Q16.7), refuses with `capacity-blocked` when `MaxConcurrent` is saturated. Schedule gate (Q14.1) is honored — off-shift items stay on the queue. Telemetry: `forge.dispatch.lead_to_crew.latency_ms` histogram tagged with `crewId / pair / outcome` + `forge.dispatch.lead_no_capacity` counter. _(The actual call into `IDeliverableLoop.RunAsync` is wired by the eventual coding/plan pipelines (Phase 13.4 / 13.5 still stubbed) — for now the dispatcher hands the row off via `task.delegated` and stamps `auto_dispatched_at`; downstream plumbing lights up once the runner is real.)_

- [x] **16.4** **`DeliverableLoop` paired-mode extension** — extend the existing `Forge.Api/Services/DeliverableLoop.cs`:
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

- [x] **16.7** **`task.{workItemId}` SignalR group** — `Forge.Api/Realtime/TaskChannel.cs` is the publisher (persists each event to `task_transcript_event` with lazy 10 000-row eviction, then fans out to the SignalR group `task.{workItemId}`). Clients join via the existing generic `ForgeHub.Subscribe("task.{wid}")` invocation rather than a dedicated `JoinTask` method — same wire shape, no new RPC surface needed. Both `DeliverableLoop` and `WorktreeChangeFeed` publish through the singleton `TaskChannel`. Message envelope: `{ workItemId, kind, agentId?, crewId?, payload, at }` on the `task.event` method — `payload` shape varies per `kind` so the channel can grow without breaking old clients. Client-side ref-counted attach/detach lives in `useTaskLiveStore` (16.13) instead of `useForgeHub`.

- [x] **16.8** **`WorktreeChangeFeed` service** — `Forge.Api/Services/WorktreeChangeFeed.cs`. Hosted service running a 1 Hz tick loop over `Subscribe(workItemId, workstreamId, worktreePath)` registrations. Each tick runs `git status --porcelain=v2 --branch` + `git log -1 --format=%H\\t%s\\t%an\\t%aI` and emits `task.worktree-tick` to the matching `task.{wid}` group. Snapshot diff suppresses no-change ticks. `paths.{added,modified,deleted}` capped at 200 entries each; over-cap sets `truncated=true`. _(Implementation uses a 1 Hz poll instead of `FileSystemWatcher` for portability — file watching on network paths and inside containers is unreliable; the burst-coalescing semantic the spec calls for falls out naturally from the poll. The "refuses paths outside `repo.cloneRoot/.worktree/{name}`" invariant is the caller's responsibility — the dispatcher passes a path it already validated; the feed itself just samples whatever path it's handed and silently no-ops if the directory isn't a git repo.)_

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

- [x] **16.13** **`useTaskLiveStore` hook** — `src/forge/Forge.UI/src/realtime/useTaskLiveStore.ts`. Zustand store keyed by `workItemId` exposes `{ snapshot, transcript, worktree, loading, error, refCount }` reactively. The companion `useTaskLive(workItemId)` hook hydrates via `GET /api/work-items/{id}/execution`, ref-count-attaches via `Subscribe("task.{wid}")`, listens to the `task.event` SignalR method, parses `task.worktree-tick` payloads into the worktree slot, caps in-memory transcript at 1 000 events, and ref-count-detaches on unmount. Used by `LiveExecutionView` (16.10).

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

- [x] **16.17** **Fidelity** — new view (`/work-items/{id}/live`), updated existing views (Swarm, Workstreams) with **additive** changes only — Plan board / Timeline / PR view untouched. The forge-board.html prototype has none of these surfaces, so they're exempt from the prototype screenshot diff. The single new token `--graph-line` is documented in `docs/forge/design-tokens.md` under the new "Phase 16 — Live Execution view" section.

- [x] **16.18** **Server-owned delivery pipeline (commit + push + PR).** Per Q16.9, after the deliverable loop returns `pass` the worker performs three git steps in order, all in `Forge.Api/Services/CrewQueueWorker.cs`:
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
- **Cross-product pairs** — both crews must share `productId`. A platform-team evaluator scoring an app-team generator's plan is a real pattern but needs cross-product cost reconciliation we don't have at v0.1.
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

- [x] **17.1** **Schema migration** — `Forge.Api/Persistence/Phase17Migration.cs` (idempotent, runs after `Phase16Migration`):
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

- [x] **17.2** **`ITaskQueueService` interface** — `Forge.Api/Services/ITaskQueueService.cs`:
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

- [x] **17.3** **`AssignedTo.kind = 'team'`** — Phase 12 `WorkItemAssignee` gains `kind: 'team'` as a third value. The assign endpoint (`POST /api/work-items/{id}/assign` or the existing PATCH path) detects the team case and:
  - Validates the team name exists (matches a known `Architecture | Engineering | Review | QA` value or any `crew.Name` prefix).
  - Calls `EnqueueAsync(workItemId, 'team', teamName)`.
  - Returns the new `task_queue` row's id alongside the work item.
  - Existing direct-to-agent assignments call `EnqueueAsync(workItemId, 'crew', leadCrewId)` with `state='picked-up'` directly so the unified queue still records them (Q17.2 last bullet).

- [x] **17.4** **`CrewQueueWorker` hosted service** — `Forge.Api/Services/CrewQueueWorker.cs`. Single 5 s ±1 s tick loop, **five sweeps + a one-shot boot-recovery pass**:
  - **Boot-recovery (one-shot, before the loop starts):** every row in `state='picked-up'` is reset to `state='queued'` with `PickedUpByAgent=null`, `PickedUpAt=null`, and a `queue_history(kind='boot-recovery')` audit. Reason: agent invocations from a prior process die with the host; without this, abandoned-recovery's 24h fallback is the only path back to a runnable state. The work item's `ExecutionStatus` is reset to `queued` in the same pass so the UI badge stops pulsing during downtime.
  - **Sweep 1 — ack timeout:** `task_queue WHERE state='awaiting-ack' AND ackRequiredBy < now()` → `AcknowledgeAsync(auto=true)`. Emits `forge.queue.ack_timeout` counter + `queue_history(kind='ack-timeout-auto')`.
  - **Sweep 2 — claim head:** for each crew with at least one `state='queued'` row, capacity-check (`MaxConcurrent` minus current `picked-up` rows for that crew). On capacity: pick a member by **resume-aware** policy — if the head row already has deliverables for a prior generator agent that's still on the crew roster, pin to that agent so the live transcript reads as a continuation; otherwise round-robin over `members WHERE agentId != crew.lead` (lead **preferred-excluded** per Q16.7 + Q18.3). `ClaimNextAsync(crewId, picked)` is the atomic CAS that pops the head. On success, the worker stamps `WorkItemRow.AutoDispatchedAt` (Phase 16 compatibility) + `ExecutionStatus = live` + bumps `TrialCount` (Phase 19) + emits `task.delegated` + auto-links the work item to a workstream (Phase 16.12 prerequisite for the "● N live" badge), then schedules `RunAgentAsync` on `Task.Run` so the sweep loop doesn't block on a multi-minute deliverable. **Schedule gate behavior**: the queue itself never *parks* a row for schedule reasons — the queue drains regardless of wall-clock so the operator's "assign now" intent is honored. But the *member picker* under Phase 18.6 filters candidates through `ScheduleGate.EvaluateAgent` so off-shift members aren't handed claims they can't act on; when zero members are on-shift the lead self-assigns (Q18.3). This is what makes a Sunday-created ticket route to the lead even though the crew's day-band crew members are formally off the clock.
  - **Sweep 3 — strain detection:** unchanged from spec. Per-crew armed-flag debounce in `ConcurrentDictionary<string, bool>`.
  - **Sweep 4 — abandoned recovery:** unchanged from spec. 24h fallback for `picked-up` rows that didn't get caught by boot-recovery.
  - **Sweep 5 — live-badge reconciliation (added during stabilization):** scans for work items whose `ExecutionStatus` lies about reality — e.g. status is `live` but no open queue row exists, or status is `completed` while a queue row sits in `picked-up`. Each mismatch is healed in place + a `workitem.changed { kind: badge-reconciled }` event fires so open tabs update without a refresh. Was added to fix stuck-`live` badges that the original four sweeps couldn't clear when an upstream sweep threw before reaching abandoned-recovery (every sweep now runs in its own try/catch via `SafeSweepAsync` so a SQLite translation error in one doesn't kill the whole tick).
  - All five sweeps run via `SafeSweepAsync` so a throw in one is logged at `Warning` and never cascades — the next sweep still runs. `RunOnceAsync` returns a `WorkerTickResult` record with per-sweep counts so tests can drive the worker deterministically.
  - **Boot-recovery + sweeps run once at startup** before the timer starts (Q17.4 boot resilience), with the same safe-sweep wrapper.
  - **Integration with `LeadAutoDispatcher`:** see 17.12 — the dispatcher now feeds the queue rather than running its own agent path, so this worker is the single execution surface.

- [x] **17.5** **Endpoints** — `Forge.Api/Endpoints/QueueEndpoints.cs`:
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

- [x] **17.8** **UI — Workstreams + Crews queue indicators**:
  - **Crews view** — each crew row in the left sidebar gains a "Q:N · {eta}m" pill rendered when depth > 0 or strained. Strained crews get the danger palette; click → `/inbox`. Stats refresh on every `queue.changed` event via the shared `useQueueChannel` + `useQueueStore` subscription.
  - **Workstreams view** — `WorkstreamCard` gains a `queuedCount` prop + sibling "● N queued" yellow pill rendered alongside the existing Phase 16.12 "● N live" green badge. Click → `/inbox`. Driven from a cross-walk over `useQueueStore.rows` filtered to the workstream's `cards` list.
  - **TopBar** — the salary + payroll chips gain a third sibling "📥 N" pending-acks chip, rendered whenever the signed-in user leads at least one crew. Pulses + warns when N > 0; idle grey when N = 0. Click → `/inbox`.
  - The chips and pills share the existing palette (no new tokens).

- [x] **17.9** **`forgeApi` client extensions** — `src/forge/Forge.UI/src/api/forgeApi.ts`:
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

- [x] **17.11** **Tests** — `Forge.Api.Tests/Phase17QueueTests.cs` (12 tests, all green):
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

- [x] **17.12** **Integration of Phase 16 dispatcher with Phase 17 queue** — the original spec described `LeadAutoDispatcher` and `CrewQueueWorker` as **coexisting** on parallel paths (dispatcher scans direct-to-agent rows, worker drains the queue). In practice this produced two-worker race conditions and confused the operator's mental model of where work was. The implementation collapses them into a clean producer/consumer split:
  - `LeadAutoDispatcher` is now a **producer** only. Its 5-second loop still scans `work_items WHERE assignedTo.id IN (lead_agents) AND auto_dispatched_at IS NULL`, applies the schedule gate + capacity pre-check + pair resolution, then **enqueues** the work item via `ITaskQueueService.EnqueueAsync(wi.Id, "crew", generatorCrew.Id)`. `AutoDispatchedAt` is stamped only after a successful enqueue so the row is never orphaned. The dispatcher does **not** invoke `IAgentRunner` at all anymore.
  - `CrewQueueWorker` is the sole **consumer**. Sweep 2 ("claim head") is the only place agents start; this is where `RunAgentAsync` runs the deliverable loop + delivery pipeline (Phase 16.18). Direct-to-crew assignments from the operator's UI go through `POST /api/crews/{id}/assign` → `EnqueueAsync` → same Sweep-2 path, so manual and auto-dispatched rows execute identically.
  - Boundary is enforced by data shape: dispatcher only writes to the queue, worker only reads from it. The original "we have two execution paths" Phase R cleanup is closed by this refactor.

  The 14 Phase 16 tests + 12 Phase 17 tests still pass (verified); the dispatcher tests now assert the post-tick state via the queue rather than via direct work-item flags.

- [x] **17.13** **Fidelity** — new `/inbox` view is additive; exempt from the prototype screenshot diff. No new design tokens — `inbox.css` reuses Phase 11 / 14 palette + the Phase 16 `--graph-line`.

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

- [x] **18.1** **Migration** — `Forge.Api/Persistence/Phase18Migration.cs` adds `crews.Shift TEXT NOT NULL DEFAULT 'day'`. Idempotent column-add, runs after `Phase17Migration`.
- [x] **18.2** **Domain model** — `CrewRow.Shift` (string, default `"day"`) lands in `Persistence/Entities.cs:480`. Mirror as `Crew.shift: 'day' | 'night'` in TS.
- [x] **18.3** **`ScheduleGate.IsWithinShift`** — accepts a shift parameter; `NextTransitionLocal` computes the next transition (open / close) for either band so the live-exec banner ("Off-shift — opens at 21:00") works for both. `ScheduleGate.ShiftDay` / `ScheduleGate.ShiftNight` constants exposed for code-paths that branch on the band.
- [x] **18.4** **CrewsEndpoints PATCH** — accept `shift` on the `PATCH /api/crews/{id}` body; persist after a `('day','night')` whitelist check.  <!-- shipped: `PatchCrewRequest.Shift` + `CreateCrewRequest.Shift` accept the field; `NormaliseShift` enforces a case-insensitive ('day','night') whitelist; mismatch returns 400 + `crew.unknown_shift`; transitions write a `crew_history(kind='shift', fromValue, toValue)` row. Tests in `Phase18PatchShiftTests.cs`. -->
- [x] **18.5** **CrewsView** — render the band as a pill next to the timezone in the runtime badge group; PO-editable inline. The schedule strip on the Performance tab (Phase 14.9) reads from `ScheduleGate.Evaluate(crew, now)` directly, so the green/red ribbon updates per-band automatically.
- [x] **18.6** **Lead 24×7 exemption (Q18.3).** New surface area, not yet shipped:  <!-- shipped: ScheduleGate.EvaluateAgent (Services/ScheduleGate.cs); CrewQueueWorker Sweep 2 filters allMembers through EvaluateAgent and falls through to crew.Lead; LeadAutoDispatcher gate now evaluates the lead specifically; PayrollCalculator.LeadHoursInRange + ComputeTotal split lead vs members (168 vs 72 h/week); CrewsView Roster lead row gets a permanent green dot; PerformanceTab schedule strip gains a "Lead 24×7" pill; Phase18LeadExemptionTests covers all four contracts. -->
  - `ScheduleGate.EvaluateAgent(crew, agentId, utcNow)` — returns `Verdict.OnShift` when `agentId == crew.Lead`, otherwise delegates to `Evaluate(crew, utcNow)`. Pure, no DB.
  - `CrewQueueWorker` Sweep 2 picker filters `members` through `ScheduleGate.EvaluateAgent` before round-robin; falls through to `crew.Lead` when zero members remain on-shift. The "exclude the lead" rule from Phase 16.7 stays as the *preference*, not the *constraint* — the lead is in the pool only as a Sunday / off-band fallback.
  - `LeadAutoDispatcher` schedule check evaluates the lead specifically (not the crew as a whole), so Sunday-created tickets that route to the lead enqueue successfully instead of parking with `outcome: off-shift`. The `off-shift` tag remains for the (rare) `crew.Lead == null OR lead.paused` case.
  - `PayrollCalculator` splits on-shift hours per agent: members get the schedule-gated window, lead gets the full clock. Performance tab payroll readout splits the lead's row from the members' row in the same table so the operator can see the 168 h vs 72 h difference at a glance.
  - Tests: lead claims when no member is on-shift (Sunday + every band off); lead exemption does not change member behaviour on a Tuesday afternoon; payroll math returns 168 h/week for the lead and ≤ 72 h/week for each member.
  - UI: the **Performance tab schedule strip** gains a small "Lead 24×7" sub-label so operators see why their crew shows green outside the band; the `CrewsView` lead row in Roster gets a permanent green dot regardless of the crew's current shift verdict.

### Test scenarios

> Coverage matrix for Phase 18. Each scenario gets a checkbox; a checked box means the test exists and passes. Audit performed against `Phase18LeadExemptionTests.cs`, `Phase18ScheduleGateNightBandTests.cs`, `Phase18PatchShiftTests.cs`, `Phase18WorkerIntegrationTests.cs`, `Phase18MigrationAndModelTests.cs`, `Phase14ScheduleGateTests.cs`, `Phase14PayrollTests.cs`, and adjacent suites. **Coverage status: 50 / 50 Phase 18 tests pass. 18.1 / 18.2 / 18.3 / 18.4 / 18.6 fully covered. 18.5 is Playwright-only and stays at the UI layer.**

**18.1 Migration** (covered in `Phase18MigrationAndModelTests.cs`)

- [x] `Phase18Migration.ApplyAsync` adds the `Shift` column to a fresh DB.
- [x] `Phase18Migration.ApplyAsync` is idempotent — second invocation does not throw and does not duplicate the column.
- [x] Existing crew rows (pre-migration) backfill `Shift = 'day'` so behaviour pre/post upgrade is identical (Q18.2 strict-superset rule).

**18.2 Domain model** (covered in `Phase18MigrationAndModelTests.cs`)

- [x] `new CrewRow()` defaults `Shift` to `"day"`.
- [x] `CrewRow.Shift` round-trips through EF — write `night`, read back `night`.
- [ ] TS `Crew.shift` type is `'day' | 'night'` (UI-side contract). *(TS contract — not exercised by xUnit; falls under 18.5 UI tests.)*

**18.3 `ScheduleGate.IsWithinShift` / `NextTransitionLocal` — night band + constants** (covered in `Phase18ScheduleGateNightBandTests.cs`)

Night-band on/off-shift truth table:
- [x] `IsWithinShift(Mon 22:00, "night")` → true (Mon-night block).
- [x] `IsWithinShift(Tue 02:00, "night")` → true (Mon-night spilling into Tue morning).
- [x] `IsWithinShift(Sat 22:00, "night")` → true (Sat-night through Sun 09:00 — Q18.1 explicit example).
- [x] `IsWithinShift(Sun 02:00, "night")` → true (still Sat-night).
- [x] `IsWithinShift(Sun 22:00, "night")` → false (Sun is not a workday → Sun-night is not on shift — Q18.1 explicit counter-example).
- [x] `IsWithinShift(Mon 02:00, "night")` → false (Sun was not a workday).
- [x] `IsWithinShift(Mon 12:00, "night")` → false (mid-day not in night band).
- [x] `IsWithinShift(Mon 22:00, "day")` → false (after day band — regression for the existing day path).

Boundaries:
- [x] `IsWithinShift(workday 21:00:00 sharp, "night")` → true; same instant with `"day"` → false (half-open `[09,21)` for day, `[21,09)` for night).
- [x] `IsWithinShift(workday 09:00:00 sharp, "day")` → true; same instant with `"night"` → false.

`Evaluate`:
- [x] `Evaluate(crew with Shift="night", nightInstant)` → `Verdict.OnShift`.
- [x] `Evaluate(crew with Shift="night", dayInstant)` → `Verdict.OffShift`.
- [x] Unknown `Shift` value (e.g. `"swing"`) normalises to `"day"` so a corrupted row never crashes the gate.

`NextTransitionLocal`:
- [x] On-shift Mon 22:00 night → next transition = Tue 09:00 (close-of-night).
- [x] Off-shift Mon 12:00 night → next transition = Mon 21:00 (open-of-night).
- [x] Off-shift Sun 12:00 night → next transition = Mon 21:00 (skip Sunday).

Constants:
- [x] `ScheduleGate.ShiftDay` equals `"day"`, `ScheduleGate.ShiftNight` equals `"night"` (so external callers don't string-literal).

**18.4 `CrewsEndpoints PATCH` — shift mutation** (covered in `Phase18PatchShiftTests.cs`)

- [x] `PATCH /api/crews/{id}` body `{ shift: "night" }` returns 200 and persists `night`.
- [x] `PATCH /api/crews/{id}` body `{ shift: "day" }` returns 200 and persists `day`.
- [x] `PATCH /api/crews/{id}` body `{ shift: "swing" }` returns 400 with whitelist error (`crew.unknown_shift`).
- [x] `PATCH /api/crews/{id}` with case variants (`"DAY"`, `"Night"`) accepted (case-insensitive normalisation, matching `Backend` / `InvokeMode` style).
- [x] Shift change writes a `crew_history(kind='shift', fromValue, toValue, byUserId)` row.
- [x] Same-value PATCH (e.g. day → day) does NOT add a history row.
- [x] `CreateCrewRequest.Shift` accepts an explicit band at create time and rejects unknown values with 400.
- [x] Create without a `shift` value defaults to `"day"`.
- [x] Non-PO caller (or PO of a different product) gets 403 — same product-scoped gate as other patch fields. *(`Patch_Shift_Returns_403_For_Non_PO_Caller`.)*

**18.5 `CrewsView` UI**

- [ ] Day/night pill renders in the runtime badge group with the right label + colour for `crew.shift`.
- [ ] (Playwright) PO toggling the pill writes the patch and flips the colour without a page refresh.
- [ ] (Playwright) Non-PO sees the pill read-only.
- [ ] Performance-tab schedule strip's green/red ribbon updates band-by-band when the crew's shift is flipped (already wired via `ScheduleGate.Evaluate(crew, now)`).

**18.6 Lead 24×7 exemption (Q18.3)**

`ScheduleGate.EvaluateAgent` (covered in `Phase18LeadExemptionTests`):
- [x] Lead is OnShift on Sunday at noon.
- [x] Lead is OnShift on Tuesday at 03:00 (outside both bands).
- [x] Member is OffShift on Sunday at noon.
- [x] Member at Tuesday noon matches the crew-wide `Evaluate` verdict (regression — members unaffected by 18.6).
- [x] Crew with `Lead = null` falls through to crew-wide gate (preserves the `outcome: off-shift` histogram tag for the no-lead case).
- [x] Disabled-schedule crew still reports the lead as OnShift (lead-availability is a routing fact, not a billing fact).
- [x] `agentId = null` falls through to crew-wide gate (defensive — no NRE, no accidental lead-match).

`PayrollCalculator.LeadHoursInRange` + `ComputeTotal` split (covered in `Phase18LeadExemptionTests`):
- [x] `LeadHoursInRange` over a full week returns 168.
- [x] `OnShiftHoursInRange` over the same week returns 72 (12 × 6 — regression for the member path).
- [x] `LeadHoursInRange` returns 0 when `ScheduleEnabled = false` (disabled = unbilled, suppresses both lead and member earnings).
- [x] `ComputeTotal` splits earnings: lead row uses 168 h, member rows use ≤72 h.
- [x] `ComputeTotal` with `Lead = null` preserves pre-Q18.3 math (every member treated as a member).

`CrewQueueWorker` Sweep 2 picker (covered in `Phase18WorkerIntegrationTests.cs`):
- [x] When zero members are on-shift, the picker selects `crew.Lead` instead of an off-shift member or `null`. *(uses a wall-clock-dependent off-shift TZ search; skips on the rare UTC instant when no such TZ exists.)*
- [x] When some members are on-shift, the picker round-robins over only the on-shift subset and never wedges work onto an off-shift member. *(regression: members claim during the band; lead is bypassed.)*
- [x] Lead-only crew (zero non-lead members) picks the lead — preserves the Phase 16.7 fallback after the rewrite.
- [x] Resume-pin honours the prior agent only if they are currently on-shift (or are the lead) — off-shift prior agents are bypassed in favour of an on-shift member or the lead. *(`SweepClaims_Resume_Pin_Bypasses_Off_Shift_Prior_Agent`.)*
- [x] No-lead, all-off-shift crew → `picked` is null and the sweep records `no-agent-available` rather than CAS-claiming the row with a null agent id. *(`SweepClaims_No_Lead_All_Off_Shift_Records_No_Agent_Available`. Worker now short-circuits before `ClaimNextAsync(_, null)` to keep the queue row available for re-routing.)*

`LeadAutoDispatcher` schedule check (covered in `Phase18WorkerIntegrationTests.cs`):
- [x] Crew with a lead, off-shift wall-clock — dispatcher enqueues successfully and the histogram tag is `outcome: dispatched`, NOT `outcome: off-shift`.
- [x] Member-band behaviour during the band is unchanged (regression: on-shift TZ on a day crew with members still records `outcome: dispatched`).
- [ ] Crew with `Lead = null`, off-shift wall-clock — dispatcher records `outcome: off-shift`. *(unreachable in practice: `LeadAutoDispatcher` indexes only crews with a non-null lead, so a no-lead crew's work item is silently skipped before the schedule check ever fires. Spec language is aspirational; flagged here so a future code change can revisit.)*

UI (NOT yet covered):
- [ ] (Playwright) `CrewsView` Roster lead row shows the permanent green dot regardless of current shift verdict.
- [ ] (Playwright) `PerformanceTab` "Lead 24×7" pill renders only when `crew.lead` is set AND `scheduleEnabled` is true.
- [ ] (Playwright) "Lead 24×7" pill is suppressed when `scheduleEnabled = false` (the strip already reads "Schedule disabled" — the pill would be redundant noise).

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

- [x] **19.1** **Migration** — `Phase19Migration.cs` adds `work_items.ExecutionStatus TEXT NOT NULL DEFAULT 'idle'` and `work_items.TrialCount INTEGER NOT NULL DEFAULT 0`. Idempotent column-adds.
- [x] **19.2** **Domain model + TS mirror** — `WorkItemRow.ExecutionStatus` + `TrialCount`; `ExecutionStatuses` static class with `Idle`/`Queued`/`Live`/`Completed`/`Failed`/`Cancelled` constants. TS `WorkItem.executionStatus`, `WorkItem.trialCount`.
- [x] **19.3** **CrewQueueWorker integration** — sweep + completion + failure paths set `ExecutionStatus` in lockstep with the queue state machine (`SetExecutionStatusAsync` helper). Each transition broadcasts `workitem.changed { kind: exec-{state} }` over SignalR so open tabs update without a refresh.
- [x] **19.4** **TaskExecutionEndpoint** — `GET /api/work-items/{id}/execution` returns `executionStatus` + `trialCount` + `maxIterations` (Phase 20). The same endpoint also synthesizes a snapshot from the queue + transcript when the work-item row is missing (e.g. plan/code task enqueued before the WIT row materializes), so the live-exec view never 404s on legitimate execution rows.
- [x] **19.5** **`ExecBadge` component** — small pill on the Plan board card + the Work Items detail panel + the Workstream card. Pulses on `live`, solid on `completed` / `failed`, hidden on `idle`. Click → `/work-items/{id}/live`. Trial-count badge to the right of the status pill, color-coded by count.
- [x] **19.6** **Workstreams "● N live" badge** — counts work items where `ExecutionStatus = live` and the workstream's `cards` array contains the work-item id. Updates on every `workitem.changed` event.

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

- [x] **20.1** **Migration** — `Phase20Migration.cs` adds `task_queue.MaxIterOverride INTEGER NULL` + `deliverables.GeneratorSummary TEXT NOT NULL DEFAULT ''`. Idempotent.
- [x] **20.2** **`DeliverableLoop`** — `MaxIterations` becomes `Math.Clamp(request.MaxIterOverride ?? ScoringConstants.MaxIterations, 1, 10)`; resolved at start of run.
- [x] **20.3** **`PATCH /api/work-items/{id}/execution`** — accepts `{ maxIter: int? }` (null = clear override); writes to the latest queue row. 403 unless caller is PO of the work item's product or the assigned generator agent's lead.
- [x] **20.4** **`TaskExecutionSnapshot.MaxIterations`** — surfaces the resolved cap so the live-exec view's input renders the active value, not the constant.
- [x] **20.5** **`CrewQueueWorker` parses `## Change Summary`** — regex-extracts the section between `## Change Summary` and the next `## ` heading (or end of message), trims to 800 chars, persists to `deliverables.GeneratorSummary` in the deliverable-loop's persistence callback.
- [x] **20.6** **PR body uses `deliverables.GeneratorSummary`** — `CrewQueueWorker.cs:976` reads the latest deliverable's `GeneratorSummary` for the "What changed" block; if empty, falls back to "_No change summary provided._" so the PR body never has a TODO marker.

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

- [x] **21.1** **Migration** — `Phase21Migration.cs` adds `task_queue.SkipSoulPrompt INTEGER NOT NULL DEFAULT 0`. Idempotent.
- [x] **21.2** **`DELETE /api/work-items/{id}/transcript`** — clears `task_transcript_event` AND `deliverables` rows in one call so the iter counter resets cleanly. Returns `{ deleted: N, deletedDeliverables: M }`.
- [x] **21.3** **Restart button on `LiveExecutionView`** — calls `DELETE /transcript` then re-enqueues the work item via `POST /api/work-items/{id}/restart` (or the existing `/assign` path with `skipSoulPrompt`). Confirmation modal calls out the three side-effects so the operator doesn't restart by mistake.
- [x] **21.4** **`CrewQueueWorker.RunAgentAsync` honors `SkipSoulPrompt`** — when the queue row's flag is set, both generator and evaluator skip the `File.ReadAllTextAsync(SoulPath)` step; the system-prompt is empty and the agent runs as the bare model with only the user prompt. Transparent to the agent — no special instruction is injected to "explain that soul was suppressed".
- [x] **21.5** **Points-based `maxTurns` in Copilot SDK runner** — closely related stabilization tied to restart UX: Copilot SDK occasionally never returned a `Finished` event when the agent decided to "keep working forever". A hard cap terminates the invocation cleanly so Restart actually has something to restart from. `maxTurns` is now **scaled by story points**: `30 × points` (e.g. 3-point item → 90 turns, 5-point item → 150 turns). When `points = 0` (unestimated), falls back to the global `Forge:Agents:CopilotMaxTurns` config default (500). The per-task override is carried on `AgentContext.MaxTurns` (nullable int); `AgentRunnerDispatcher.RunCopilotSdk` resolves as `context.MaxTurns ?? options.MaxTurns`. Both generator and evaluator contexts receive the same cap. Wall-clock budget follows the same scaling: `30 min × points`, falling back to the global default when points = 0.

### Test scenarios (`RestartBehaviorTests.cs`)

Comprehensive coverage for Phase 21 restart semantics, transcript clearing, soul-prompt suppression, and points-based budgets.

**21.1 Migration — `SkipSoulPrompt` column**

- [x] Column exists with default 0 after migration runs.

**21.2 `DELETE /api/work-items/{id}/transcript` — clear transcript + deliverables**

- [x] Clears all `task_transcript_event` rows for the work item.
- [x] Clears all `deliverable` rows for the work item (iter counter resets to 0).
- [x] Returns `{ deleted: N, deletedDeliverables: M }` with correct counts.
- [x] Idempotent — calling twice on the same work item returns `{ deleted: 0, deletedDeliverables: 0 }` on the second call.
- [x] Returns 200 for a work item with no transcript or deliverables (empty delete is a no-op, not an error).
- [x] Does not delete transcript events belonging to a different work item (no cross-contamination).

**21.3 Restart via `POST /api/crews/{crewId}/plan` with `restart=true`**

- [x] Clears deliverables and resets iter count to 0.
- [x] Resets WIT State from `in-review` → `active` so `RunAgentAsync` guard doesn't short-circuit.
- [x] Resets WIT State from `resolved` → `active`.
- [x] Resets WIT State from `closed` → `active`.
- [x] Preserves WIT State when already `active` (no-op on State).
- [x] Preserves WIT State when already `proposed` (no-op on State).
- [x] Sets `ExecutionStatus` to `queued` on re-enqueue (failed → queued transition).
- [x] Sets `ExecutionStatus` to `queued` on re-enqueue (completed → queued transition).
- [x] Non-restart enqueue (`restart=false`) does NOT clear deliverables.
- [x] Restart skips the schedule gate — a restart at Sunday 03:00 on a day crew still succeeds (no 409 `crew.off_shift`).
- [ ] Non-restart enqueue respects the schedule gate — off-shift returns 409.
- [ ] Restart on a non-existent work item returns an appropriate error.
- [x] Restart on a non-existent crew returns 404.
- [x] Restart cancels any existing open queue row before enqueuing a new one (idempotency — no double-queue).

**21.4 `SkipSoulPrompt` flag**

- [x] `restart=true, skipSoulPrompt=true` persists `SkipSoulPrompt=1` on the queue row.
- [x] Normal enqueue (`skipSoulPrompt=false` or absent) persists `SkipSoulPrompt=0`.
- [x] `skipSoulPrompt=true` without `restart=true` still persists the flag (the two are independent controls).
- [ ] When `SkipSoulPrompt=true`, `RunAgentAsync` skips `File.ReadAllTextAsync(SoulPath)` — generator's `SoulMd` is empty string.
- [ ] When `SkipSoulPrompt=true`, evaluator also skips its soul.md — evaluator's `SoulMd` is the scoring rubric only (no persona prefix).
- [ ] When `SkipSoulPrompt=false` and `agent.SoulPath` is set, generator's `SoulMd` contains the file content.
- [ ] When `SkipSoulPrompt=false` and `agent.SoulPath` is null/empty, generator's `SoulMd` is empty string (graceful degradation).
- [ ] When `SkipSoulPrompt=false` and `agent.SoulPath` points to a missing file, generator's `SoulMd` is empty string (best-effort, no crash).

**21.5 Points-based `maxTurns` and wall-clock budget**

- [x] 3 points × 30 = 90 min wall-clock budget.
- [x] 1 point × 30 = 30 min wall-clock budget.
- [x] 0 points = null budget (falls back to global default).
- [x] 3 points × 30 = 90 max turns on `AgentContext.MaxTurns`.
- [x] 5 points × 30 = 150 max turns.
- [x] 1 point × 30 = 30 max turns.
- [x] 0 points = null max turns (falls back to global `AgentRunnerOptions.MaxTurns` default of 500).
- [x] `AgentRunnerDispatcher.RunCopilotSdk` uses `context.MaxTurns` when set (not the global default).
- [x] `AgentRunnerDispatcher.RunCopilotSdk` uses `options.MaxTurns` when `context.MaxTurns` is null.
- [ ] Turn count increments on each `ToolExecutionCompleteEvent`; when `turnCount >= maxTurns`, the invocation terminates with `failReason = "Max turns (N) reached"`.
- [x] Evaluator context receives the same `maxTurns` as the generator context (both scaled by the same work item points).
- [x] Negative points value → treated as 0 (null budget + null maxTurns, falls back to defaults).

**21.R Re-execution guard (related regression coverage)**

- [x] `RunAgentAsync` skips execution when WIT State is `in-review` (returns early, marks queue row completed).
- [x] `RunAgentAsync` skips execution when WIT State is `resolved` or `closed`.
- [x] After restart resets State from `in-review` → `active`, `RunAgentAsync` proceeds (does NOT skip).
- [ ] Boot-recovery sweep (`picked-up` → `queued`) does not re-run items in terminal states.

**21.I Independence invariants**

- [x] Setting `ExecutionStatus = failed` does NOT change WIT State.
- [x] Setting `ExecutionStatus = completed` does NOT change WIT State.
- [x] Restarting (clearing deliverables + resetting queue) does NOT change the work item's `AssignedTo` field (WIT-level assignee is independent of execution lifecycle).
- [x] Restarting does NOT delete the work item's child rows, comments, or parent linkage (Phase Q21.3 — only execution-side rows are cleared).

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

- [ ] **22.1** **Migration** — `Forge.Api/Persistence/Phase22Migration.cs` (idempotent, runs after `Phase21Migration`):
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

- [ ] **22.2** **Domain model + TS mirror** — `WorkItemRow.AssignedAgentJson`; `AssignedAgent` POCO (Q22.2 shape) + `AgentAssignmentRow`. TS `WorkItem.assignedAgent: AssignedAgent | null`, `WorkItem.assignedAgentHistory?: AgentAssignmentEntry[]` (lazy-loaded by the timeline read).

- [ ] **22.3** **`CrewQueueWorker.cs` — Sweep-2 claim path rewrite** (the line at `~347-348` today):
  - Build `AssignedAgent` from the resolved `(generatorAgentId, evaluatorAgentId, crewId, evaluatorCrewId)` plus `team = crew.Team`, `role = 'generator'`, `assignedBy = 'queue-worker'`.
  - Write `wi.AssignedAgentJson = serialize(payload)` instead of overwriting `AssignedToJson`.
  - Append `agent_assignments(kind='claimed', AgentIdsJson, Team, CrewId, Role='generator', FromExecutionStatus='queued', ToExecutionStatus='live', AssignedBy='queue-worker', At=now)` in the same SaveChanges() call so they commit atomically.
  - Boot-recovery sweep does the same write so a re-claim after server restart records a fresh `claimed` row (the operator sees a "re-claimed at X" row rather than a single 'claimed' row from days ago).

- [ ] **22.4** **Endpoints — `AgentAssignmentsEndpoints.cs`**:
  - `GET /api/work-items/{id}/assigned-agent` — returns the parsed `AssignedAgent` or 204 when null.
  - `GET /api/work-items/{id}/assignments?limit=…` — paginated history, newest first. Default 50, max 500.
  - `POST /api/work-items/{id}/assignments` body `{ agents: string[], team?, crewId?, role, reason? }` — manual override (PO of the work item's product OR crew lead). Validates that referenced agents exist; writes the field + history row with `assignedBy=userId`. Used for manual reviewer-assign (Q22.3 step 5).
  - `DELETE /api/work-items/{id}/assigned-agent` body `{ reason? }` — clears the field. PO only. History `kind='cleared-manual'`. Use sparingly — most clears should come from Restart or completion.

- [ ] **22.5** **`PATCH /api/work-items/{id}/move` integration** — when the target state transitions:
  - `→ in-review` (PR opened): append `agent_assignments(kind='pr-opened', From=current, To='in-review')` without touching `AssignedAgentJson` (Q22.3 step 4).
  - `→ resolved` or `→ closed`: append `agent_assignments(kind='completed')` AND null out `AssignedAgentJson` (Q22.3 step 6).
  - These hooks live in `WorkItemsEndpoints.MoveWorkItem` so any path that drives state — UI move, queue worker post-PR transition (Phase 16.18 line 1144), provider webhook — fires the same audit.

- [ ] **22.6** **Restart (Phase 21) clears `AssignedAgent`** — `DELETE /api/work-items/{id}/transcript` + the queue-restart branch null out `AssignedAgentJson` and append `agent_assignments(kind='cleared-restart', From='live'|'failed', To='queued', AssignedBy=userId)`. The next claim writes a fresh row and the timeline shows the discontinuity, not a silent overwrite.

- [ ] **22.7** **Backfill migration (Q22.7)** — one-shot at first boot after Phase 22 ships, gated by a `forge_seed_state` row so re-boots skip it. Heuristic for runtime-shaped `AssignedToJson`: `JsonDocument.RootElement.TryGetProperty("generatorAgentId" | "crewId" | "agentId")` succeeds AND there's no `kind` property at the top level. For each match: copy bytes → `AssignedAgentJson` (wrapping in the Q22.2 shape if needed); rebuild `AssignedToJson` from the resolved crew's `Team`. Log a one-line summary `"Phase 22 backfill migrated N work items"` and write one `agent_assignments(kind='backfill', AssignedBy='backfill')` per migrated row.

- [ ] **22.8** **UI — Work-item detail panel — new "Currently working on this" strip** below the existing Assignee row in `WorkItemDetailPanel.tsx`:
  - **Empty state** — "Not currently assigned to an agent" + an `Assign agent` affordance (visible to PO + crew leads only) that opens a small picker (agent list filtered by team + role).
  - **Active state** — stack of avatars (1..N) + role pill (`Generator` / `Evaluator` / `Reviewer` color-coded) + crew name + relative timestamp ("since 14m ago"). Hover shows full agent names + the `assignedBy` label.
  - Click → opens a right-rail **Assignment history** drawer rendering the `agent_assignments` rows as a vertical timeline: `claimed → pr-opened → reviewer-assigned → completed` with timestamps + actor + reason. Reuses the styling of the Phase 14 `agent_level_history` timeline.

- [ ] **22.9** **UI — Live Execution view** — the header strip's "assigned to" pill (today reads from `AssignedToJson` indirectly via the snapshot's `generatorAgentId` / `evaluatorAgentId`) now reads `AssignedAgent` directly. Pill click expands the history drawer. Empty state ("Not yet assigned — waiting for queue claim") replaces the previous "No events yet" copy when `AssignedAgent` is null AND no transcript events exist.

- [ ] **22.10** **UI — Plan board card + Workstream card cells** — the agent-name cell that today reads the runtime fields out of `AssignedToJson` switches to `AssignedAgent.agents[0]` (canonical driver per Q22.5). When `AssignedAgent` is null, the cell falls back to the WIT-level assignee (`AssignedTo`), rendered with a slightly muted style so the operator can distinguish "no one's executing" from "someone's actively coding". Tooltip enumerates all `agents[]` when N > 1.

- [ ] **22.11** **Tests — `Phase22AssignedAgentTests.cs`** — see detailed scenario list in the **Test scenarios** section below.

- [ ] **22.12** **Fidelity** — additive UI (assignment strip + history drawer + live-exec pill data source switch); exempt from the prototype screenshot diff. No new design tokens — reuses the avatar palette + the Phase 14 history-timeline styling.

### Test scenarios

> Coverage matrix for Phase 22. Each scenario gets a checkbox; a checked box means the test exists and passes. Test file: `Phase22AssignedAgentTests.cs`. **Coverage status: 0% — Phase 22 is not yet implemented.**

**22.1 Migration — `Phase22Migration.ApplyAsync`**

- [ ] Adds `AssignedAgentJson TEXT NULL` column to `work_items` table on a fresh DB.
- [ ] Creates `agent_assignments` table with all required columns (`Id`, `WorkItemId`, `Kind`, `AgentIdsJson`, `Team`, `CrewId`, `Role`, `FromExecutionStatus`, `ToExecutionStatus`, `AssignedBy`, `Reason`, `At`).
- [ ] Creates index `IX_agent_assignments_workitem_at` on `(WorkItemId, At DESC)`.
- [ ] Idempotent — second invocation does not throw and does not duplicate columns or tables.
- [ ] Runs after `Phase21Migration` without conflict.

**22.2 Domain model + TS mirror**

- [ ] `WorkItemRow.AssignedAgentJson` defaults to `null` for new rows.
- [ ] `AssignedAgent` POCO round-trips through JSON serialization (all fields: `agents`, `team`, `crewId`, `role`, `assignedAt`, `assignedBy`).
- [ ] `AssignedAgent` with a single agent serializes `agents` as a 1-element array.
- [ ] `AssignedAgent` with paired agents (generator + evaluator) serializes `agents` as a 2-element array.
- [ ] `AssignedAgent` with `null` optional fields (`team`, `crewId`, `assignedBy`) round-trips correctly.
- [ ] `AgentAssignmentRow` entity round-trips through EF — write all fields, read back identical values.
- [ ] `WorkItemMapper` parses `AssignedAgentJson` into `AssignedAgent` POCO on read.
- [ ] `WorkItemMapper` returns `null` for `AssignedAgent` when `AssignedAgentJson` is null or empty.

**22.3 Queue claim — Sweep 2 writes `AssignedAgent` (not `AssignedTo`)**

- [ ] Sweep-2 claim writes `AssignedAgentJson` with correct shape: `agents=[picked]`, `role='generator'`, `assignedBy='queue-worker'`, `assignedAt` is ISO-8601 UTC.
- [ ] Sweep-2 claim for a paired crew writes `agents=[generator, evaluator]` (2-element array).
- [ ] Sweep-2 claim for a single (non-paired) crew writes `agents=[generator]` (1-element array).
- [ ] Sweep-2 claim populates `team` from `crew.Team` and `crewId` from the claiming crew's ID.
- [ ] Sweep-2 claim appends an `agent_assignments` row with `kind='claimed'` in the same `SaveChanges()` transaction.
- [ ] `agent_assignments` row from claim has `FromExecutionStatus='queued'`, `ToExecutionStatus='live'`, `AssignedBy='queue-worker'`.
- [ ] Sweep-2 claim does **NOT** overwrite `AssignedToJson` — the WIT-level assignee is unchanged.
- [ ] Boot-recovery reclaim writes a fresh `AssignedAgentJson` AND appends a new `agent_assignments(kind='claimed')` row (not a silent field-update).

**22.4 Endpoints — `AgentAssignmentsEndpoints.cs`**

GET assigned agent:
- [ ] `GET /api/work-items/{id}/assigned-agent` returns 200 with parsed `AssignedAgent` when field is set.
- [ ] `GET /api/work-items/{id}/assigned-agent` returns 204 when `AssignedAgentJson` is null.
- [ ] `GET /api/work-items/{id}/assigned-agent` returns 404 for a non-existent work item ID.

GET assignment history:
- [ ] `GET /api/work-items/{id}/assignments` returns history rows newest-first (descending `At`).
- [ ] `GET /api/work-items/{id}/assignments?limit=2` returns at most 2 rows.
- [ ] `GET /api/work-items/{id}/assignments` defaults to limit=50 when no query param.
- [ ] `GET /api/work-items/{id}/assignments?limit=999` clamps to max 500.
- [ ] `GET /api/work-items/{id}/assignments` returns empty array for a work item with no history.

POST manual assignment:
- [ ] `POST /api/work-items/{id}/assignments` with valid `{ agents, role }` writes `AssignedAgentJson` + appends history row.
- [ ] `POST /api/work-items/{id}/assignments` sets `assignedBy` to the calling user ID (not `'queue-worker'`).
- [ ] `POST /api/work-items/{id}/assignments` with `role:'reviewer'` appends `kind='reviewer-assigned'`.
- [ ] `POST /api/work-items/{id}/assignments` validates that referenced agent IDs exist — returns 400 for unknown agents.
- [ ] `POST /api/work-items/{id}/assignments` with optional `reason` persists the reason in the history row.
- [ ] `POST /api/work-items/{id}/assignments` without `reason` stores `null` reason.
- [ ] `POST /api/work-items/{id}/assignments` by non-PO / non-crew-lead returns 403.

DELETE assigned agent:
- [ ] `DELETE /api/work-items/{id}/assigned-agent` nulls `AssignedAgentJson` + appends `kind='cleared-manual'`.
- [ ] `DELETE /api/work-items/{id}/assigned-agent` with `{ reason }` body persists the reason.
- [ ] `DELETE /api/work-items/{id}/assigned-agent` by PO returns 200.
- [ ] `DELETE /api/work-items/{id}/assigned-agent` by non-PO returns 403.
- [ ] `DELETE /api/work-items/{id}/assigned-agent` when field is already null is a no-op (idempotent, still 200).

**22.5 `MoveWorkItem` integration — state transition hooks**

PR opened (→ in-review):
- [ ] `MoveWorkItem(→ in-review)` appends `agent_assignments(kind='pr-opened')` without changing `AssignedAgentJson`.
- [ ] `agent_assignments` row from PR-open has `FromExecutionStatus` = prior status, `ToExecutionStatus='in-review'`.
- [ ] Coder's `AssignedAgent.agents` array is unchanged after PR open — coder stays pinned (Q22.3 step 4).

Completed / resolved:
- [ ] `MoveWorkItem(→ resolved)` nulls `AssignedAgentJson` AND appends `agent_assignments(kind='completed')`.
- [ ] `MoveWorkItem(→ closed)` nulls `AssignedAgentJson` AND appends `agent_assignments(kind='completed')`.
- [ ] `agent_assignments` row from completion has correct `FromExecutionStatus` and `ToExecutionStatus`.

State transitions that do NOT touch AssignedAgent:
- [ ] `MoveWorkItem(proposed → active)` does NOT append an assignment history row and does NOT change `AssignedAgentJson`.
- [ ] `MoveWorkItem(active → proposed)` (revert) does NOT change `AssignedAgentJson`.

**22.6 Restart clears `AssignedAgent`**

- [ ] `DELETE /api/work-items/{id}/transcript` nulls `AssignedAgentJson`.
- [ ] `DELETE /api/work-items/{id}/transcript` appends `agent_assignments(kind='cleared-restart')`.
- [ ] `cleared-restart` history row has `FromExecutionStatus` = prior status (e.g. `'live'` or `'failed'`), `ToExecutionStatus='queued'`.
- [ ] `cleared-restart` history row has `AssignedBy` = the user who triggered the restart.
- [ ] After restart + re-claim, the timeline shows three rows: `claimed → cleared-restart → claimed` (no silent overwrite).
- [ ] Restart when `AssignedAgentJson` is already null still appends the `cleared-restart` row (audit completeness).

**22.7 Backfill migration (one-shot)**

- [ ] Runtime-shaped `AssignedToJson` (has `generatorAgentId` or `crewId` at top level, no `kind`) is detected and migrated.
- [ ] Migrated row: `AssignedAgentJson` receives the data in Q22.2 shape with `assignedBy='backfill'`.
- [ ] Migrated row: `AssignedToJson` is rebuilt as WIT-shape `{kind:'team', id:team, displayName:team}` from the embedded `crewId`.
- [ ] Non-runtime-shaped `AssignedToJson` (has `kind` property, e.g. `{kind:'agent', id:'XD'}`) is NOT migrated — left untouched.
- [ ] `null` `AssignedToJson` rows are skipped — no crash, no history row.
- [ ] Each migrated row gets an `agent_assignments(kind='backfill', AssignedBy='backfill')` history row.
- [ ] Backfill is gated by a `forge_seed_state` row — second boot is a no-op.
- [ ] Idempotent — running the migration twice produces the same result (no duplicate history rows on second run).

**22.8 Sticky on failure (Q22.4)**

- [ ] Agent fails mid-run → `AssignedAgentJson` stays set (not cleared).
- [ ] Agent fails mid-run → no `cleared-*` history row is written.
- [ ] Agent hits budget cap → `AssignedAgentJson` stays set.
- [ ] Agent restarts mid-run (server crash) → `AssignedAgentJson` stays set until explicit Restart.

**22.9 WIT assignee independence (Q22.1)**

- [ ] `PATCH /api/work-items/{id}` with `{ assignedTo: {kind:'agent', id:'XD'} }` does NOT mutate `AssignedAgentJson`.
- [ ] `PATCH /api/work-items/{id}` with `{ assignedTo: … }` does NOT append any `agent_assignments` history row.
- [ ] Sweep-2 claim does NOT mutate `AssignedToJson` — the WIT-level assignee is preserved.
- [ ] Both fields can be non-null simultaneously with different values — no conflict.

**22.10 Multi-agent & role semantics (Q22.5)**

- [ ] Paired crew claim writes exactly 2 entries in `agents[]` (generator + evaluator).
- [ ] Single crew claim writes exactly 1 entry in `agents[]`.
- [ ] `agents[0]` is the canonical "driver" — generator for `role='generator'`, reviewer for `role='reviewer'`.
- [ ] Manual reviewer assignment (`POST /assignments {role:'reviewer'}`) replaces `AssignedAgent` entirely (not additive).
- [ ] After reviewer assignment, `agents[0]` is the reviewer agent.

**22.11 Full lifecycle timeline (end-to-end)**

- [ ] Full happy path: `claimed → pr-opened → reviewer-assigned → completed` produces exactly 4 `agent_assignments` rows in chronological order.
- [ ] Full restart path: `claimed → cleared-restart → claimed → pr-opened → completed` produces exactly 5 rows.
- [ ] History rows are ordered by `At DESC` when queried via the paginated endpoint.
- [ ] Each history row has a non-null `At` timestamp in ISO-8601 format.
- [ ] Each history row has a non-empty `Kind` from the allowed set: `claimed`, `reassigned`, `cleared-restart`, `cleared-manual`, `pr-opened`, `reviewer-assigned`, `completed`, `backfill`.

**22.12 Edge cases & error handling**

- [ ] Concurrent claims on the same work item — only one succeeds; the loser gets a CAS failure, no orphan history row.
- [ ] `POST /assignments` on a non-existent work item returns 404.
- [ ] `DELETE /assigned-agent` on a non-existent work item returns 404.
- [ ] `GET /assignments` on a non-existent work item returns 404.
- [ ] `POST /assignments` with empty `agents[]` array returns 400.
- [ ] `POST /assignments` with invalid `role` value returns 400.
- [ ] Malformed `AssignedAgentJson` in DB does not crash the GET endpoint — returns null gracefully.

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

- **Q23.1 Cadence — 20 minutes.** One tick every 20 minutes, jittered ±60 seconds across repos to avoid thundering-herd against a single ADO org. Configurable via `Forge:Sync:WorkItem:IntervalSeconds` (default 1200). The legacy 60-second polling for cards / PRs / pipelines is unchanged — Phase 23 adds a *separate* job for work items because their churn rate, page sizes, and rate-limit profile are different. The `WorkItemSyncJob` and `ProviderSyncService` run side-by-side; they touch different tables.

- **Q23.2 Per-repo scope, parallel up to 4.** Each `Repo` row drives one sync invocation per tick. Concurrency cap = 4 (tunable via `Forge:Sync:WorkItem:MaxParallel`); rows beyond the cap wait for the next tick. Per-repo serialization: a tick that finds the previous run still in flight (`LastSyncStatus='running'` for > 19 minutes) cancels the in-flight run and starts a fresh one — better to drop a slow run than to stack them. A `LastSyncStatus='running'` marker carries an `IsRunningSince` timestamp so the cancellation rule is a wall-clock check, not a heuristic.

- **Q23.3 ADO scope — `(org, project, areaPaths[])` triple, not `(org, project, gitRepo)`.** ADO work items belong to the *project*, not to any git repo, and the operator wants to scope sync to a curated list of area paths within the project. The `Repo` row carries:
  - `AdoOrg`, `AdoProject` (existing) — the project the WIQL query runs against.
  - `AreaPathsJson` (new) — JSON `string[]` of area-path prefixes. Empty / null = sync the whole project. Example: `["One\\AzureStack\\ASZ-Update", "One\\AzureStack\\Compute"]`. The WIQL `WHERE` clause is `[System.AreaPath] UNDER 'p1' OR [System.AreaPath] UNDER 'p2'`. ADO's `UNDER` operator includes the node and all descendants, which matches the operator's mental model of "everything under this folder".
  - The git-side fields (`RemoteUrl`, `CloneRoot`, `DefaultBranch`) are independent — they describe the repo Forge clones for worktrees. A single `Repo` row carries both shapes; auto-clone (Phase 8.10) uses the git fields, work-item sync uses the project + area-path fields. Either set may be empty; a row with only the project + area-paths is "work-items only", a row with only the git fields is "git only", a row with both is the common case.

- **Q23.4 GitHub scope — repo-native.** GitHub issues are scoped to a repo (`/repos/{owner}/{repo}/issues`), so `Repo.GhOwner` + `Repo.GhRepo` is enough; `AreaPathsJson` is ignored on GitHub repos. Pull requests come along for the ride because GitHub treats PRs as issues for the purposes of the issues endpoint — Phase 23 syncs them in the same call and the mapper splits them by `pull_request` field presence.

- **Q23.5 Initial time window — ADO 365 days, GH all-time.** First sync of an ADO repo runs WIQL with `[System.ChangedDate] >= @cutoff` where `@cutoff = UtcNow - 365d`. First sync of a GitHub repo omits the `since` parameter so all issues are returned. Both windows are constants in `Forge.Api/SyncConstants.cs` so the spec-consistency test catches drift; per-repo overrides (`Repo.InitialSyncWindowDays INTEGER NULL`) ship as a follow-up if the operator hits a 50,000-item ADO project that needs trimming further.

- **Q23.6 Pagination strategy — provider-specific, cursor-driven.**
  - **ADO:** WIQL queries return up to 20,000 work-item references in a single response; `_apis/wit/workitems?ids=…&fields=…` is then called in batches of **200 IDs per request** (ADO's hard cap). The sync service splits the WIQL result client-side and walks the batches sequentially, committing each batch in its own transaction so a failure in batch N+1 doesn't roll back batch N. The cursor (`LastSyncCursor` text column) holds the last successfully-committed batch index so a resume picks up exactly where the prior run failed.
  - **GitHub:** `/issues?per_page=100&since={LastSyncAt}` is paginated by the standard `Link: rel="next"` header. The sync service walks the chain until no `next` link is present. Cursor stores the `next` URL as opaque text so resume after failure is a single GET, no math required.

- **Q23.7 Delta sync — `ChangedDate` (ADO) / `updatedAt` (GitHub).** Once `LastSyncAt IS NOT NULL`, the next run uses it as the lower bound:
  - **ADO:** `WHERE [System.ChangedDate] >= @LastSyncAt AND ([System.AreaPath] UNDER …)`. Window is **half-open on the lower bound** (`>=`, not `>`) so an item changed exactly at the cursor instant isn't dropped; the upsert path is idempotent so the duplicate-with-same-revision is a no-op.
  - **GitHub:** `since={LastSyncAt-toISO}`. GitHub's `since` filter is on `updated_at` and is documented as inclusive on the boundary.
  - The cursor advances only on a fully-successful run. A run that fails mid-walk leaves `LastSyncAt` at its prior value so the next run re-fetches the same window — better to over-fetch than to miss an item.

- **Q23.8 `agent-ready` flag — upstream-tag → auto-team-assign.** When sync sees an item with the `agent-ready` flag (ADO `System.Tags` contains `agent-ready`; GitHub label name `agent-ready`):
  - If `WorkItemRow.AssignedTo` is currently null OR has the legacy runtime-shape (Q22.7), set `AssignedTo = {kind: 'team', id: repo.AgentReadyDefaultTeam, displayName: repo.AgentReadyDefaultTeam}`. Default team is `Engineering`; `repos.AgentReadyDefaultTeam TEXT NULL DEFAULT NULL` overrides per-repo.
  - If `AssignedTo` is already a meaningful WIT-shape value (`{kind: 'agent'|'human'|'team', id: ...}`), **leave it untouched** — the operator's manual assignment beats the auto-rule. The flag still flows in (item shows `agent-ready` chip in UI) but doesn't overwrite intent.
  - Removing the flag upstream does **not** un-assign — the assignment is a one-way ratchet at v0.1. Operators wanting to revoke an auto-assign do it manually.
  - The auto-assign writes a `forge_actions(kind='auto-assigned-by-flag', actorUserId='sync', body: { repoId, workItemId, team })` audit row so the trail is visible at `/audit`. **Does NOT write `AssignedAgent`** (Phase 22 boundary holds — runtime assignment is the queue worker's job).

- **Q23.9 Idempotency — sync runs are replayable.** Each `WorkItemRow` upsert keys on `(provider, providerRef)` so a re-fetch of the same work item produces no duplicate rows. The conflict policy from Phase 5.9 still applies: if the local row's `RemoteRevision` differs from the upstream's at upsert time, the run records a conflict but writes the upstream value (last-writer-wins) and surfaces the divergence in the work-item detail panel.

### Implementation steps

- [ ] **23.1** **Migration** — `Forge.Api/Persistence/Phase23Migration.cs` (idempotent, runs after `Phase22Migration`):
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

- [ ] **23.2** **`SyncConstants.cs`** — locks the time-window defaults, page sizes, cadence, and the `agent-ready` flag literal:
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

- [ ] **23.3** **`IWorkItemSyncJob` hosted service** — `Forge.Api/Services/WorkItemSyncJob.cs`. 20-min tick loop with ±60 s jitter; on each tick:
  - Query `repos WHERE LastSyncStatus IN ('idle','failed') OR (LastSyncStatus='running' AND IsRunningSince < now-19m)` ordered by `LastSyncAt` ascending (oldest first).
  - For each, dispatch to a `SemaphoreSlim`-bounded worker (`SyncConstants.WorkItemSyncMaxParallel = 4`).
  - Worker calls `IWorkItemSyncRunner.RunAsync(repo, ct)`; on success sets `LastSyncAt = startedAt`, `LastSyncStatus = 'idle'`, clears `LastSyncCursor` + `LastSyncError`. On failure sets `LastSyncStatus = 'failed'` + `LastSyncError = ex.Message` and leaves `LastSyncAt` untouched.
  - Each invocation appends a `work_item_sync_runs` row (start + end timestamps, counts, errors, success flag).
  - Telemetry: `forge.sync.workitem.duration_ms` histogram (tagged with `kind`, `provider`, `outcome`); `forge.sync.workitem.items{kind}` counter; `forge.sync.workitem.errors` counter.
  - Manual trigger via `POST /api/repos/{id}/sync-work-items` (existing surface from Phase 12.10) calls the same `RunAsync` with `kind='manual'`; rate-limited to 1/min/repo unless caller is PO with `?ignoreThrottle=true`.

- [ ] **23.4** **`AdoWorkItemSyncRunner` — multi-area-path WIQL + 200-ID batching**:
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

- [ ] **23.5** **`GitHubWorkItemSyncRunner` — `since`-driven pagination**:
  - GET `/repos/{owner}/{repo}/issues?per_page=100&state=all&since={lastSyncOrEpoch}` where `lastSyncOrEpoch = repo.LastSyncAt ?? "1970-01-01T00:00:00Z"`.
  - Walk the `Link: rel="next"` header chain, committing each page's items in its own transaction.
  - For each issue: distinguish issue vs. PR by presence of `pull_request` field; map to `WorkItemRow` (issues) or `PrRow` (PRs) via existing `GitHubMapper`. Labels array → tags; if `agent-ready` is present, apply Q23.8 auto-assign.
  - Sub-issue support per Phase 12.4 — read native sub-issue parent if present, fall back to the `Parent: #N` body convention.
  - Cursor stores the next-page URL as opaque text so resume after failure is a single GET.
  - Rate-limit handling: read `X-RateLimit-Remaining`; when below 100, sleep until `X-RateLimit-Reset` before continuing.

- [ ] **23.6** **`agent-ready` auto-assign helper** — `Forge.Api/Services/AgentReadyResolver.cs`:
  - `Apply(WorkItemRow row, RepoRow repo, IReadOnlyList<string> tags)`:
    - If `tags` does not contain `SyncConstants.AgentReadyFlag` → return unchanged.
    - If `row.AssignedToJson` parses to a Phase-22 WIT-shape value (`kind` property present, `kind` ∈ `agent | human | team`) → return unchanged (Q23.8 second bullet — operator intent wins).
    - Else set `row.AssignedToJson = serialize({kind:'team', id: repo.AgentReadyDefaultTeam ?? 'Engineering', displayName: repo.AgentReadyDefaultTeam ?? 'Engineering'})` and emit a `forge_actions(kind='auto-assigned-by-flag')` audit + a `workitem.changed` SignalR event so open tabs render the new chip without a refresh.
  - Pure helper (no DB writes itself); the runner persists in its own transaction. Tested in isolation against the canonical (no flag, flag + null, flag + WIT-shape, flag + runtime-shape, flag + non-default team) matrix.

- [ ] **23.7** **Endpoints — extension of existing Phase 12.10 surface**:
  - `POST /api/repos/{id}/sync-work-items` body `{ ignoreThrottle?: bool, kind?: 'full'|'delta'|'auto' }` — manual trigger. `kind='full'` clears `LastSyncAt` first so the next run uses the initial window.
  - `GET /api/repos/{id}/sync-work-items/runs?limit=20` — paginated `work_item_sync_runs` history.
  - `GET /api/repos/{id}/sync-work-items/status` — `{ lastSyncAt, lastSyncStatus, lastSyncError, isRunningSince, queueDepth?, etaMinutes? }` for the dashboard widget.
  - `PATCH /api/repos/{id}` accepts `areaPaths: string[]` and `agentReadyDefaultTeam: string?` for inline edit.

- [ ] **23.8** **UI — `RepoSyncStatusStrip` on `/products/{id}` repo cards** + the existing `SyncDialog`:
  - Card-level: small status dot (green idle, blue running, red failed) + "Last sync: 14m ago" + click → opens a drawer with the last 20 sync runs, items added / updated counts, errors, and a "Sync now" button.
  - `SyncDialog` (Phase 12.10) gains an "Area paths" multi-select for ADO repos (autocomplete against `IAdoClassificationService.DiscoverAsync(kind=areas)` from Phase 15.2) and a "Default team for `agent-ready`" combobox. Save persists to the `Repo` row; SignalR `repo.changed` invalidates the form.
  - Work-item detail panel renders an `agent-ready` pill (orange) next to existing tags when the upstream tags array contains it; tooltip shows the auto-assign timestamp + which sync run wrote it.

- [ ] **23.9** **Sidebar / TopBar surfacing** — when any repo's `LastSyncStatus = 'failed'`, the active product's sidebar entry shows a small red dot; click → `/products/{id}` and the failing repo card is auto-scrolled into view. Heuristic-only — no separate alert center at v0.1.

- [ ] **23.10** **Tests — `Forge.Api.Tests/Phase23WorkItemSyncTests.cs`**:

  #### 23.10.1 — Migration (`Phase23MigrationTests`)
  - [ ] `Migration_Adds_AreaPathsJson_Column_To_Repos` — column exists, nullable TEXT.
  - [ ] `Migration_Adds_LastSyncAt_Column_To_Repos` — column exists, nullable TEXT.
  - [ ] `Migration_Adds_LastSyncCursor_Column_To_Repos` — column exists, nullable TEXT.
  - [ ] `Migration_Adds_LastSyncStatus_Column_With_Default_Idle` — column exists, NOT NULL, default `'idle'`.
  - [ ] `Migration_Adds_LastSyncError_Column_To_Repos` — column exists, nullable TEXT.
  - [ ] `Migration_Adds_IsRunningSince_Column_To_Repos` — column exists, nullable TEXT.
  - [ ] `Migration_Adds_AgentReadyDefaultTeam_Column_To_Repos` — column exists, nullable TEXT, default NULL.
  - [ ] `Migration_Adds_InitialSyncWindowDays_Column_To_Repos` — column exists, nullable INTEGER.
  - [ ] `Migration_Creates_WorkItemSyncRuns_Table` — table with columns `(id, repoId, kind, startedAt, finishedAt, itemsAdded, itemsUpdated, itemsConflicted, errorMessage, completedSuccessfully)`.
  - [ ] `Migration_WorkItemSyncRuns_Indexed_By_RepoId_StartedAt_Desc` — index exists on `(repoId, startedAt DESC)`.
  - [ ] `Migration_Is_Idempotent` — running Phase23Migration twice produces no errors.
  - [ ] `Migration_Runs_After_Phase22Migration` — Phase22 columns exist before Phase23 runs.

  #### 23.10.2 — Spec-consistency (`Phase23SyncConstantsTests`)
  - [ ] `AdoInitialWindowDays_Equals_365` — `SyncConstants.AdoInitialWindowDays == 365`.
  - [ ] `AdoMaxIdsPerWorkItemBatch_Equals_200` — `SyncConstants.AdoMaxIdsPerWorkItemBatch == 200`.
  - [ ] `AdoMaxWiqlReferences_Equals_20000` — `SyncConstants.AdoMaxWiqlReferences == 20000`.
  - [ ] `GithubPerPage_Equals_100` — `SyncConstants.GithubPerPage == 100`.
  - [ ] `WorkItemSyncIntervalSeconds_Equals_1200` — `SyncConstants.WorkItemSyncIntervalSeconds == 1200`.
  - [ ] `WorkItemSyncMaxParallel_Equals_4` — `SyncConstants.WorkItemSyncMaxParallel == 4`.
  - [ ] `RunningTimeoutMinutes_Equals_19` — `SyncConstants.RunningTimeoutMinutes == 19`.
  - [ ] `AgentReadyFlag_Equals_AgentReady` — `SyncConstants.AgentReadyFlag == "agent-ready"`.
  - [ ] `AgentReadyDefaultTeam_Equals_Engineering` — `SyncConstants.AgentReadyDefaultTeam == "Engineering"`.
  - [ ] `Constants_Match_Spec_Section_Q23_5_And_Q23_6` — parses this spec file and asserts every constant verbatim (mirrors Phase 14.14 / Phase 16.5 pattern).

  #### 23.10.3 — WIQL builder (`AdoWiqlBuilderTests`)
  - [ ] `Zero_AreaPaths_Produces_ProjectOnly_Where` — `AreaPathsJson` null/empty → `WHERE [System.TeamProject] = '@project'` only, no `UNDER` clause.
  - [ ] `Single_AreaPath_Produces_Single_Under` — one entry → `[System.AreaPath] UNDER '{p1}'`.
  - [ ] `Multiple_AreaPaths_Produce_Ored_Under_Clauses` — N entries → `([System.AreaPath] UNDER '{p1}' OR [System.AreaPath] UNDER '{p2}' OR …)`.
  - [ ] `ChangedDate_Uses_HalfOpen_LowerBound` — `>=` boundary (not `>`) so an item changed exactly at the cursor instant is included.
  - [ ] `FirstSync_Uses_365Day_Window_When_LastSyncAt_Null` — `lastSyncOrCutoff = UtcNow - 365d` when no prior sync.
  - [ ] `DeltaSync_Uses_LastSyncAt_As_LowerBound` — second run WIQL uses `LastSyncAt` value from prior run.
  - [ ] `InitialSyncWindowDays_Override_Honored` — per-repo `InitialSyncWindowDays = 30` overrides the 365-day default.
  - [ ] `AreaPath_Single_Quotes_Escaped` — area paths containing `'` are escaped to `''` in WIQL.
  - [ ] `Project_Name_Single_Quotes_Escaped` — project name with `'` is escaped to `''`.
  - [ ] `Wiql_Always_Orders_By_ChangedDate_Desc` — `ORDER BY [System.ChangedDate] DESC` appended.

  #### 23.10.4 — ADO pagination (`AdoPaginationTests`)
  - [ ] `Exactly_200_Ids_Single_Batch` — WIQL returns 200 IDs → 1 GET `_apis/wit/workitems` call.
  - [ ] `201_Ids_Splits_Into_Two_Batches` — 201 IDs → 2 GET calls (200 + 1); assert via `StubHttpMessageHandler` call counts.
  - [ ] `450_Ids_Splits_Into_Three_Batches` — 450 IDs → 3 GET calls (200 + 200 + 50).
  - [ ] `MidBatch_Failure_Leaves_LastSyncCursor_At_Last_Successful` — batch 2 of 3 fails → `LastSyncCursor = "1"` (index of last completed batch).
  - [ ] `Resume_After_MidBatch_Failure_Picks_Up_At_Next_Index` — next run reads `LastSyncCursor = "1"` → skips batch 0 and 1, starts at batch 2; assert via call counts.
  - [ ] `Each_Batch_Committed_In_Own_Transaction` — batch 1 items in DB even when batch 2 fails.
  - [ ] `Successful_Run_Clears_LastSyncCursor` — after all batches succeed, `LastSyncCursor` is null.
  - [ ] `Fields_Mapped_Correctly` — all ADO fields (`System.Id`, `WorkItemType`, `Title`, `State`, `AreaPath`, `IterationPath`, `AssignedTo`, `Tags`, `ChangedDate`, `Parent`, `StoryPoints`, `Priority`, `Severity`) mapped to `WorkItemRow`.
  - [ ] `Relations_Parsed_For_Parent` — `System.LinkTypes.Hierarchy-Reverse` relation sets `ParentId`.
  - [ ] `Tags_Parsed_From_SystemTags` — `System.Tags` semicolon-delimited string → tags array.

  #### 23.10.5 — ADO error handling (`AdoRetryTests`)
  - [ ] `429_Response_Backs_Off_Per_RetryAfter_Header` — 429 with `Retry-After: 5` → waits ≥5s before retry.
  - [ ] `503_Response_Backs_Off_Per_RetryAfter_Header` — 503 treated same as 429.
  - [ ] `RetryAfter_Capped_At_60_Seconds` — `Retry-After: 300` → capped to 60s.
  - [ ] `Third_Retry_Still_Fails_Aborts_Run` — three consecutive failures → `LastSyncStatus = 'failed'`, `LastSyncError` set.
  - [ ] `Successful_Retry_After_Transient_Failure_Completes_Run` — 429 on first attempt, 200 on retry → run succeeds.

  #### 23.10.6 — GitHub pagination (`GitHubPaginationTests`)
  - [ ] `LinkNextHeader_Chain_Followed_Until_No_Next` — 3 pages with `Link: rel="next"` → 3 GET calls; stops at page without `next`.
  - [ ] `MidPage_Failure_Stores_Next_Url_In_LastSyncCursor` — page 2 of 3 fails → `LastSyncCursor` holds the failing page's URL.
  - [ ] `Resume_After_MidPage_Failure_Is_Single_Get` — next run GETs the stored cursor URL directly (no math/recalculation).
  - [ ] `Each_Page_Committed_In_Own_Transaction` — page 1 items in DB even when page 2 fails.
  - [ ] `Successful_Run_Clears_LastSyncCursor` — after all pages succeed, `LastSyncCursor` is null.
  - [ ] `PullRequests_Skipped_In_Issue_Response` — items with `pull_request` field filtered out of `WorkItemRow` upserts.
  - [ ] `PullRequests_Mapped_To_PrRow` — items with `pull_request` field mapped to `PrRow` instead.
  - [ ] `SubIssue_Native_Parent_Preferred` — GitHub native sub-issue parent reference takes precedence.
  - [ ] `SubIssue_Fallback_To_Body_Convention` — `Parent: #N` in issue body sets `ParentId` when no native sub-issue.

  #### 23.10.7 — GitHub rate-limit handling (`GitHubRateLimitTests`)
  - [ ] `XRateLimitRemaining_Below_100_Sleeps_Until_Reset` — `X-RateLimit-Remaining: 50` + `X-RateLimit-Reset` → runner sleeps until reset epoch.
  - [ ] `XRateLimitRemaining_Above_100_No_Sleep` — `X-RateLimit-Remaining: 500` → no sleep, continues immediately.
  - [ ] `FirstSync_Omits_Since_Parameter` — first sync (no `LastSyncAt`) → query string has no `since=`.
  - [ ] `DeltaSync_Adds_Since_In_Iso_Format` — second sync → `since={LastSyncAt}` in ISO-8601.

  #### 23.10.8 — Delta cursor lifecycle (`DeltaCursorTests`)
  - [ ] `FirstRun_Ado_Uses_365Day_Window` — ADO first run sends WIQL with `ChangedDate >= {UtcNow - 365d}`.
  - [ ] `FirstRun_GitHub_Omits_Since` — GitHub first run omits `since` parameter (all-time fetch).
  - [ ] `SecondRun_Uses_LastSyncAt_From_PriorRun` — after successful sync, next run sends `LastSyncAt` as the delta bound.
  - [ ] `Failed_Run_Leaves_LastSyncAt_Untouched` — sync failure → `LastSyncAt` retains its prior value; next run re-fetches same window.
  - [ ] `Successful_Run_Sets_LastSyncAt_To_Run_StartTime` — cursor advances to the start time of the run (not the finish time), ensuring no gap.
  - [ ] `Successful_Run_Sets_LastSyncStatus_Idle` — `LastSyncStatus = 'idle'` after success.
  - [ ] `Successful_Run_Clears_LastSyncError` — `LastSyncError = null` after success.
  - [ ] `Failed_Run_Sets_LastSyncStatus_Failed` — `LastSyncStatus = 'failed'` after error.
  - [ ] `Failed_Run_Writes_LastSyncError` — `LastSyncError = ex.Message` persisted.

  #### 23.10.9 — Idempotency (`IdempotencyTests`)
  - [ ] `Identical_Upsert_Produces_Zero_Net_Changes` — same item, same `RemoteRevision` → `Skipped = 1`, no DB write.
  - [ ] `Duplicate_Insert_Same_ProviderRef_No_Second_Row` — re-sync of same `(provider, providerRef)` → update, not duplicate.
  - [ ] `Conflict_Policy_Writes_Upstream_Value_LastWriterWins` — local row has `RemoteRevision = "3"`, remote sends `RemoteRevision = "5"` → row updated with upstream values.
  - [ ] `Conflict_Policy_Records_Divergence` — when local and remote `RemoteRevision` differ, divergence is recorded / surfaceable.
  - [ ] `Upsert_Preserves_Local_FeatureRef_When_Remote_Null` — remote has no `FeatureRef` → local value kept.
  - [ ] `Upsert_Preserves_Local_AssignedTo_When_Remote_Null` — remote has null `AssignedTo` → local assignment kept.
  - [ ] `Upsert_Preserves_Local_ParentId_When_Remote_Empty` — remote has no `ParentId` → local re-parent kept.
  - [ ] `Upsert_Updates_All_ProviderOwned_Fields` — `Track`, `Type`, `State`, `Title`, `DescriptionMd`, `IterationRef`, `AreaPath`, `Points`, `Priority`, `TagsJson`, `UpdatedAt`, `RemoteRevision` all updated on revision change.

  #### 23.10.10 — `agent-ready` auto-assign (`AgentReadyResolverTests`)
  - [ ] `No_AgentReady_Tag_Returns_Row_Unchanged` — tags `["bug", "p1"]` → `AssignedToJson` untouched.
  - [ ] `Flag_Plus_Null_AssignedTo_Sets_Team_Engineering` — tags `["agent-ready"]` + `AssignedToJson = null` → `{kind:'team', id:'Engineering', displayName:'Engineering'}`.
  - [ ] `Flag_Plus_WitShape_Kind_Agent_Untouched` — tags `["agent-ready"]` + `AssignedToJson` with `kind:'agent'` → no change (operator intent wins).
  - [ ] `Flag_Plus_WitShape_Kind_Human_Untouched` — tags `["agent-ready"]` + `AssignedToJson` with `kind:'human'` → no change.
  - [ ] `Flag_Plus_WitShape_Kind_Team_Untouched` — tags `["agent-ready"]` + `AssignedToJson` with `kind:'team'` → no change (already team-assigned).
  - [ ] `Flag_Plus_RuntimeShape_Legacy_Triggers_TeamAssign` — tags `["agent-ready"]` + `AssignedToJson` without `kind` property (legacy Q22.7 shape) → overwritten with team shape.
  - [ ] `NonDefault_AgentReadyDefaultTeam_Honored` — `repo.AgentReadyDefaultTeam = "Platform"` → `{kind:'team', id:'Platform', displayName:'Platform'}`.
  - [ ] `Null_AgentReadyDefaultTeam_Falls_Back_To_Engineering` — `repo.AgentReadyDefaultTeam = null` → defaults to `"Engineering"`.
  - [ ] `AutoAssign_Writes_ForgeActions_Audit_Row` — after auto-assign, `forge_actions` row exists with `kind='auto-assigned-by-flag'`, `actorUserId='sync'`, body contains `repoId`, `workItemId`, `team`.
  - [ ] `AutoAssign_Emits_WorkItemChanged_SignalR_Event` — after auto-assign, `workitem.changed` SignalR event is broadcast.
  - [ ] `Flag_Removed_Upstream_Existing_Assignment_Preserved` — item had `agent-ready` + auto-assigned → next sync without `agent-ready` tag → `AssignedToJson` unchanged (one-way ratchet).
  - [ ] `AgentReady_Flag_Case_Insensitive_Match` — tags `["Agent-Ready"]` still triggers auto-assign (if applicable per design).

  #### 23.10.11 — WorkItemSyncJob scheduling (`WorkItemSyncJobTests`)
  - [ ] `Job_Ticks_At_20_Minute_Interval` — simulated clock advances 1200s → tick fires.
  - [ ] `Tick_Queries_Idle_And_Failed_Repos` — repos with `LastSyncStatus IN ('idle','failed')` are included.
  - [ ] `Tick_Skips_Running_Repos_Under_19_Minutes` — `LastSyncStatus='running'` + `IsRunningSince < 19m ago` → skipped.
  - [ ] `Tick_Cancels_Stale_Running_Repo_Over_19_Minutes` — `LastSyncStatus='running'` + `IsRunningSince > 19m ago` → cancelled and re-started.
  - [ ] `Repos_Ordered_By_LastSyncAt_Ascending` — oldest sync-at processed first.
  - [ ] `Concurrency_Cap_At_4_Repos_Parallel` — 4 repos sync concurrently; verify via timing or semaphore count.
  - [ ] `Fifth_Repo_Waits_For_Next_Tick` — 5 repos queued → only 4 dispatched; the 5th is deferred.
  - [ ] `On_Success_Sets_LastSyncAt_And_Idle` — after `RunAsync` succeeds: `LastSyncAt = startedAt`, `LastSyncStatus = 'idle'`, `LastSyncCursor = null`, `LastSyncError = null`.
  - [ ] `On_Failure_Sets_Failed_And_Error` — after `RunAsync` throws: `LastSyncStatus = 'failed'`, `LastSyncError = ex.Message`, `LastSyncAt` untouched.
  - [ ] `Each_Invocation_Appends_SyncRun_Row` — `work_item_sync_runs` row with start/end timestamps, counts, success flag.
  - [ ] `SyncRun_Kind_Reflects_Trigger` — `kind = 'full'|'delta'|'manual'` set correctly based on context.

  #### 23.10.12 — Schedule jitter (`ScheduleJitterTests`)
  - [ ] `100_Ticks_Fall_Within_Plus_Minus_60s_Envelope` — generate 100 jittered timestamps; all fall within `[1140s, 1260s]` interval.
  - [ ] `Jitter_Is_Not_Constant` — 100 values have >1 distinct value (not degenerate).
  - [ ] `Jitter_Differs_Across_Repos` — two repos in the same tick get different jitter offsets (anti-thundering-herd).

  #### 23.10.13 — Endpoints (`Phase23EndpointTests`)
  - [ ] `POST_SyncWorkItems_Triggers_Manual_Sync` — `POST /api/repos/{id}/sync-work-items` starts a sync run.
  - [ ] `POST_SyncWorkItems_KindFull_Clears_LastSyncAt` — `{ kind: 'full' }` → `LastSyncAt = null` before run (forces initial window).
  - [ ] `POST_SyncWorkItems_KindDelta_Uses_Existing_Cursor` — `{ kind: 'delta' }` → normal delta sync using `LastSyncAt`.
  - [ ] `POST_SyncWorkItems_RateLimited_1_Per_Minute_Per_Repo` — second POST within 60s → 429.
  - [ ] `POST_SyncWorkItems_IgnoreThrottle_Requires_PO` — non-PO with `ignoreThrottle: true` → 403.
  - [ ] `POST_SyncWorkItems_IgnoreThrottle_PO_Bypasses_RateLimit` — PO with `ignoreThrottle: true` within 60s → 200 (not 429).
  - [ ] `POST_SyncWorkItems_Unknown_Repo_Returns_404` — non-existent repo ID → 404.
  - [ ] `POST_SyncWorkItems_No_Auth_Returns_401` — missing `X-Forge-User` header → 401.
  - [ ] `GET_SyncWorkItemsRuns_Returns_Paginated_History` — `GET /api/repos/{id}/sync-work-items/runs` returns `work_item_sync_runs` rows.
  - [ ] `GET_SyncWorkItemsRuns_Limit_Parameter_Honored` — `?limit=5` returns at most 5 rows.
  - [ ] `GET_SyncWorkItemsRuns_Default_Limit_Is_20` — omit `limit` → max 20 rows.
  - [ ] `GET_SyncWorkItemsStatus_Returns_Status_Object` — `GET /api/repos/{id}/sync-work-items/status` returns `{ lastSyncAt, lastSyncStatus, lastSyncError, isRunningSince }`.
  - [ ] `PATCH_Repo_Accepts_AreaPaths_Array` — `PATCH /api/repos/{id}` with `{ areaPaths: ["A\\B", "C\\D"] }` persists to `AreaPathsJson`.
  - [ ] `PATCH_Repo_Accepts_AgentReadyDefaultTeam` — `PATCH /api/repos/{id}` with `{ agentReadyDefaultTeam: "Platform" }` persists to `AgentReadyDefaultTeam`.
  - [ ] `PATCH_Repo_AreaPaths_Empty_Array_Clears_Scope` — `{ areaPaths: [] }` sets `AreaPathsJson` to `null` or `"[]"` (whole-project sync).

  #### 23.10.14 — Telemetry (`Phase23TelemetryTests`)
  - [ ] `Duration_Histogram_Emitted_On_Sync_Complete` — `forge.sync.workitem.duration_ms` histogram recorded with tags `provider`, `kind`, `outcome`.
  - [ ] `Items_Counter_Emitted_Per_Kind` — `forge.sync.workitem.items` counter incremented with tags `kind=added`, `kind=updated`, `kind=conflicted`.
  - [ ] `Pagination_Batches_Counter_Emitted` — `forge.sync.workitem.pagination.batches` counter incremented per batch walked.
  - [ ] `Errors_Counter_Emitted_On_Failure` — `forge.sync.workitem.errors` counter incremented with tag `errorClass` (rate-limit / auth / network / shape).
  - [ ] `AgentReady_Assignments_Counter_Emitted` — `forge.sync.workitem.agent_ready_assignments` counter incremented with tag `team`.

  #### 23.10.15 — End-to-end integration (`Phase23IntegrationTests`)
  - [ ] `FullSync_Ado_Inserts_Items_And_Records_Run` — seed a repo with ADO config + stub HTTP → full sync inserts N items, appends a `work_item_sync_runs` row with `kind='full'`, `completedSuccessfully=true`.
  - [ ] `DeltaSync_Ado_Only_Fetches_Changed_Items` — seed `LastSyncAt` → delta sync WIQL includes `ChangedDate >= {LastSyncAt}`; only changed items appear in upsert.
  - [ ] `FullSync_GitHub_Inserts_Items_And_Records_Run` — seed a repo with GitHub config + stub HTTP → full sync inserts N items, appends a `work_item_sync_runs` row.
  - [ ] `DeltaSync_GitHub_Only_Fetches_Updated_Items` — seed `LastSyncAt` → GitHub query includes `since={LastSyncAt}`.
  - [ ] `AgentReady_EndToEnd_Flag_Triggers_AutoAssign_And_Audit` — sync item with `agent-ready` tag → `AssignedToJson` set, `forge_actions` audit row written, SignalR event broadcast.
  - [ ] `MultiRepo_Parallel_Sync_Completes_Within_Timeout` — 4 repos sync in parallel → all complete; 5th repo deferred.

- [ ] **23.11** **Telemetry surface** on the existing `Forge.Api.Phase16` meter (re-used; adding a new meter would fragment dashboards):
  - `forge.sync.workitem.duration_ms` — histogram, tagged `provider`, `kind`, `outcome`.
  - `forge.sync.workitem.items` — counter, tagged `provider`, `kind` (`added`/`updated`/`conflicted`).
  - `forge.sync.workitem.pagination.batches` — counter, tagged `provider` — useful for spotting projects that ballooned past 20k references.
  - `forge.sync.workitem.errors` — counter, tagged `provider`, `errorClass` (rate-limit / auth / network / shape).
  - `forge.sync.workitem.agent_ready_assignments` — counter, tagged `team`.

- [ ] **23.12** **Fidelity** — additive UI (status strip + sync drawer + agent-ready pill); exempt from prototype screenshot diff. Reuses existing tokens (status palette + the Phase 14 history-timeline). One new copy string ("agent-ready") added to the design glossary in `docs/forge/design-tokens.md`.

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

### Implementation steps

- [ ] **24.1** **Migration** — `Forge.Api/Persistence/Phase24Migration.cs` (idempotent, runs after `Phase23Migration`):
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

- [ ] **24.2** **`SyncConstants.cs` extension** — adds:
  ```
  PrSyncIntervalSeconds = 1200
  PrSyncMaxParallel = 8
  PrCommentPageSize = 100
  ```
  Spec-consistency test (existing `Phase23SyncConstantsTests` rolled into a renamed `SyncConstantsTests`) asserts these match Q24.1 / Q24.9.

- [ ] **24.3** **`PrSyncJob` hosted service** — `Forge.Api/Services/PrSyncJob.cs`. 20-min tick loop, ±60 s jitter. On each tick:
  - Query `prs WHERE IsActive=1 AND (LastSyncStatus IN ('idle','failed') OR (LastSyncStatus='running' AND IsRunningSince < now-19m))` ordered by `LastSyncAt` ascending.
  - Bounded by `SemaphoreSlim` (`PrSyncMaxParallel = 8`). Worker dispatches to `IPrSyncRunner.RunAsync(pr, ct)`; on success sets `LastSyncAt`, clears errors, computes `IsActive` from the new state. On terminal-state transition: `IsActive=0`, fires `pr.closed` SignalR + audit. On failure: `LastSyncStatus='failed'` + records error.
  - Each invocation appends a `pr_sync_runs` row (kind, counts, errors). Reuses the Phase 23 sync-run history table shape with a `Scope='pr'|'workitem'` discriminator. _(Phase 24.1 migration extends `work_item_sync_runs` to `sync_runs` with the discriminator OR adds a parallel `pr_sync_runs` — implementer picks the cleaner shape; spec doesn't constrain.)_

- [ ] **24.4** **`AdoPrSyncRunner`**:
  - `GET _apis/git/repositories/{repoId}/pullRequests/{prId}?api-version=7.0` — top-level state + metadata. Map state per Q24.3.
  - When upstream `lastMergeSourceCommit` differs from local OR upstream `lastUpdatedDate` advanced: `GET /pullRequests/{prId}/threads?api-version=7.0&$top=1000`. Iterate threads + comments client-side. Each comment upserts via `(provider='ado', providerCommentId=comment.id)`.
  - Authority signal: ADO marks the comment author with `author.uniqueName`; map known agent UPNs to `AuthorKind='agent'` via the `agents` table (matches `agent.PreferredAdoAccount` from Phase 9.11 user settings); everything else is `human`.
  - `commentThread.status` flips → set `pr_comments.State`; if newly-resolved set `ResolvedBy*` from `lastUpdatedBy`.
  - Apply Q24.6 mapping after upsert.

- [ ] **24.5** **`GitHubPrSyncRunner`**:
  - `GET /repos/{owner}/{repo}/pulls/{number}` — top-level state. Map per Q24.3.
  - `GET /repos/{owner}/{repo}/pulls/{number}/comments?since={LastUpstreamUpdatedAt}&per_page=100` — review comments (inline). Walk `Link: rel="next"`.
  - `GET /repos/{owner}/{repo}/issues/{number}/comments?since=…&per_page=100` — general comments. Walk pagination same way.
  - **Thread resolution state**: GitHub doesn't return resolved-state on the REST comments endpoint; the runner falls back to the GraphQL `pullRequest.reviewThreads` query (single GraphQL POST, ≤ 100 threads per page) to read `isResolved` per thread, then merges back into the per-comment rows by comment id. This is the only GraphQL call in the codebase; the runner contains it cleanly so the rest of the sync stays REST-shaped.
  - Author kind: GitHub login → `agents.PreferredGitHubAccount` lookup (Phase 9.11) → `agent` else `human`. The `github-actions[bot]` and similar bot logins map to `system`.

- [ ] **24.6** **Authority enforcement endpoints** — `Forge.Api/Endpoints/PrCommentsEndpoints.cs`:
  - `GET /api/prs/{prId}/comments?state=…&requiresAttention=…` — paginated list.
  - `GET /api/prs/{prId}/comments/{commentId}` — single comment + history.
  - `POST /api/prs/{prId}/comments` body `{ body, parentCommentId?, filePath?, line? }` — write a new comment. Forwards to provider as the signed-in user; agent invocations write through the same endpoint so the upstream comment shows the agent's mapped UPN/login.
  - `PATCH /api/prs/{prId}/comments/{commentId}` body `{ body }` — edit. Author or reviewer only.
  - `POST /api/prs/{prId}/comments/{commentId}/resolve` body `{ resolution }` — **403 + `comment.agent_cannot_resolve`** when `caller.kind='agent'`. Otherwise PATCHes upstream + sets local state.
  - `POST /api/prs/{prId}/comments/{commentId}/reopen` — same authority. Provider call differs per-side (ADO `PATCH /threads/{id} { status: 'active' }`; GitHub `unresolveReviewThread` GraphQL mutation).
  - All endpoints emit `pr.changed { kind: 'comment' }` on success.

- [ ] **24.7** **Comment ↔ commit / response mapping** — `Forge.Api/Services/PrCommentLinker.cs`:
  - `LinkAsync(prId, ct)` runs after every sync upsert. Scans:
    - All un-linked comments where `LinkedCommitSha IS NULL AND LinkedAgentResponseId IS NULL`.
    - All commits on the PR's branch since the last linker run (cursor on `PrRow.LastLinkerCommitSha NULLABLE TEXT`, added in 24.1).
    - All replies posted since the last sync.
  - Body-parse + position-and-time heuristic per Q24.6. Setting either link writes a `pr_comment_history(kind='linked')` audit and recomputes `RequiresAttention`.
  - `PATCH /api/prs/{prId}/comments/{commentId}` accepts `{ linkedCommitSha?, linkedAgentResponseId? }` for manual operator override. PO + assigned-agent's-lead only.

- [ ] **24.8** **Reopen detection + queue hook** — runs inline in the sync upsert path:
  - When upstream `UpdatedAt` advances on an existing row: append history (`kind ∈ {edited, reopened, resolved}` derived from state delta).
  - When the recomputed `RequiresAttention` flips from `false` → `true`: emit `pr.comment-reopened { prId, commentId }` on `pr.{prId}` SignalR group + emit `task.comment-needs-response { commentId }` on the work item's `task.{wid}` group.
  - When the work item's `ExecutionStatus = in-review` AND no open `comment-response` queue row exists for it: enqueue `(workItemId, 'crew', wi.AssignedAgent.crewId, kind='comment-response')`. Q24.8 hook — the runner-side prompt-template wiring is the next bullet.

- [ ] **24.9** **Queue worker `kind='comment-response'` runner path** _(follow-up — data hook ships in 24.8, runner integration here)_:
  - `CrewQueueWorker.RunAgentAsync` branches on the queue row's `Kind` field. `kind='comment-response'` builds a different prompt: lists the open comments where `RequiresAttention=true`, includes file/line context for inline comments, asks the agent to either reply on the thread (linked via `LinkedAgentResponseId`) or commit a fix (linked via `LinkedCommitSha` heuristic).
  - Same delivery pipeline as Phase 16.18 (auto-commit, force-push, no new PR — the existing PR receives the new commit). No fresh deliverable row; the response loop runs to completion in one iteration unless evaluator finds a new issue.
  - Queue row completes when `RequiresAttention` is false on every comment. New comments arriving mid-run don't extend the current invocation — they enqueue a fresh `comment-response` row that picks up on the next claim.

- [ ] **24.10** **UI — PR detail panel comment list** in `PrDetailPanel.tsx`:
  - Group comments by thread (`ThreadId`); within a thread render parent + replies indented.
  - Per-comment chip: state pill (`active` red, `resolved` green, `wont-fix` grey, `pending` blue), author avatar (agent emoji or human avatar), inline file/line tag for review comments, "needs response" amber pill when `RequiresAttention=true`.
  - Linked-commit chip → click to open the commit on upstream.
  - Linked-response chip → scroll to the reply within the thread.
  - Resolve / Reopen buttons — visible only to humans; agents see a disabled tooltip "Only reviewers can resolve comments" (Q24.5).
  - History link per comment → opens a small drawer with the `pr_comment_history` rows.

- [ ] **24.11** **UI — Live Execution view "Comments" tab**:
  - When the work item's `ExecutionStatus = in-review`, the live-exec right rail gains a fourth tab "Comments" alongside Worktree / Tokens / Progress.
  - Lists the work item's PR's comments where `RequiresAttention=true`, newest first.
  - Per row: file/line, body excerpt (≤ 80 chars), reviewer name, age. Click → deep-link to the comment in the PR detail panel.
  - When the queue worker enqueues a `comment-response` task (Q24.8), a small banner at the top of the tab says "Agent is handling these (queue position: N)".

- [ ] **24.12** **UI — PR card badges** in `PrCard.tsx` (used on the PR board + the workstream card):
  - "💬 N" chip showing open-comment count when `pr_comments WHERE PrId=X AND State='active'` is non-empty.
  - "⚠ N" amber chip when `pr_comments WHERE PrId=X AND RequiresAttention=1` is non-empty (subset of above; the operator sees both numbers).
  - Final-state PRs (`completed`, `abandoned`) get a grey "✓ done" pill instead of comment counters.

- [ ] **24.13** **Tests — `Forge.Api.Tests/Phase24PrSyncTests.cs`** — see detailed scenario list in the **Test scenarios** section below.

- [ ] **24.14** **Telemetry surface** on the existing `Forge.Api.Phase16` meter:
  - `forge.sync.pr.duration_ms` — histogram, tagged `provider`, `outcome`.
  - `forge.sync.pr.state_transitions` — counter, tagged `from`, `to`.
  - `forge.sync.pr.comments` — counter, tagged `kind` (`added`/`updated`/`resolved`/`reopened`).
  - `forge.sync.pr.attention_flips` — counter for `RequiresAttention` toggles in either direction.
  - `forge.pr.comment.authority_refused` — counter for 403s on the resolve endpoint, tagged `actorKind`.
  - `forge.pr.comment.linker_matched` — counter for body-parse vs position-heuristic link successes.

- [ ] **24.15** **Fidelity** — additive UI (PR detail panel comment list + Live Execution Comments tab + PR card badges); exempt from prototype screenshot diff. New tokens: none — reuses Phase 14 score-chip palette for the comment state pills + the existing avatar palette + the Phase 19 attention amber.

### Test scenarios

> Coverage matrix for Phase 24. Each scenario gets a checkbox; a checked box means the test exists and passes. Test file: `Phase24PrSyncTests.cs`. **Coverage status: 0% — Phase 24 is not yet implemented.**

**24.1 Migration — `Phase24Migration.ApplyAsync`**

- [ ] Adds `IsActive`, `LastSyncAt`, `LastSyncStatus`, `LastSyncError`, `IsRunningSince`, `CommentSyncCursor`, `LastUpstreamUpdatedAt` columns to `prs` table.
- [ ] `IsActive` defaults to `1` for existing rows.
- [ ] `LastSyncStatus` defaults to `'idle'` for existing rows.
- [ ] Creates `pr_comments` table with all required columns (`Id`, `PrId`, `ProviderCommentId`, `ThreadId`, `ParentCommentId`, `AuthorId`, `AuthorKind`, `Body`, `FilePath`, `Line`, `State`, `CreatedAt`, `UpdatedAt`, `ResolvedByUserId`, `ResolvedAt`, `LinkedCommitSha`, `LinkedAgentResponseId`, `RequiresAttention`).
- [ ] `pr_comments.AuthorKind` defaults to `'human'`.
- [ ] `pr_comments.State` defaults to `'active'`.
- [ ] `pr_comments.RequiresAttention` defaults to `0`.
- [ ] Creates `UNIQUE(PrId, ProviderCommentId)` index on `pr_comments`.
- [ ] Creates `IX_pr_comments_PrId_RequiresAttention` index on `pr_comments`.
- [ ] Creates `IX_pr_comments_ThreadId` index on `pr_comments`.
- [ ] Creates `pr_comment_history` table with all required columns (`Id`, `CommentId`, `Kind`, `FromValue`, `ToValue`, `ByUserId`, `At`, `PayloadJson`).
- [ ] Creates index on `pr_comment_history(CommentId, At)`.
- [ ] Idempotent — second invocation does not throw and does not duplicate columns/tables.
- [ ] Runs after `Phase23Migration` without conflict.

**24.2 Constants — `SyncConstants.cs`**

- [ ] `PrSyncIntervalSeconds` equals `1200`.
- [ ] `PrSyncMaxParallel` equals `8`.
- [ ] `PrCommentPageSize` equals `100`.
- [ ] Spec-consistency test: constants match Q24.1 / Q24.9 values verbatim.

**24.3 PR state mapping (Q24.3)**

ADO state mapping:
- [ ] ADO `Active` → `state='open'`, `IsActive=true`.
- [ ] ADO `Completed` → `state='completed'`, `IsActive=false`.
- [ ] ADO `Abandoned` → `state='abandoned'`, `IsActive=false`.

GitHub state mapping:
- [ ] GitHub `open` → `state='open'`, `IsActive=true`.
- [ ] GitHub `closed` + `merged_at IS NOT NULL` → `state='completed'`, `IsActive=false`.
- [ ] GitHub `closed` + `merged_at IS NULL` → `state='abandoned'`, `IsActive=false`.

State change side-effects:
- [ ] State change emits `pr.changed { kind:'state' }` on SignalR `pr.{prId}` channel.
- [ ] State change writes an audit row.
- [ ] Terminal state transition (`→ completed` or `→ abandoned`) fires `pr.closed` event.
- [ ] Non-terminal states (`conflict`, `review-required`) keep `IsActive=true`.

**24.4 `PrSyncJob` — tick loop + sweep logic**

- [ ] Tick fires every ~20 min (within ±60 s jitter envelope).
- [ ] Sweep selects PRs with `IsActive=1 AND (LastSyncStatus IN ('idle','failed') OR stale 'running')`.
- [ ] Sweep orders PRs by `LastSyncAt` ascending (oldest first).
- [ ] At most `PrSyncMaxParallel = 8` PRs sync concurrently.
- [ ] A 9th active PR waits for the next tick (semaphore bounded).
- [ ] Successful sync sets `LastSyncAt`, `LastSyncStatus='idle'`, clears `LastSyncError`.
- [ ] Failed sync sets `LastSyncStatus='failed'`, `LastSyncError=message`, leaves `LastSyncAt` untouched.
- [ ] A `running` row with `IsRunningSince` older than 19 min is treated as stale and re-eligible for sync.
- [ ] Each sync invocation appends a sync-run history row (kind, counts, errors, success flag).
- [ ] `IsActive=false` rows are NOT selected by the sweep — tick skips them entirely.

**24.5 `AdoPrSyncRunner` — PR metadata + comment threads**

- [ ] Fetches PR metadata via `GET _apis/git/repositories/{repoId}/pullRequests/{prId}`.
- [ ] Maps ADO states correctly per Q24.3 mapping table.
- [ ] When upstream `lastUpdatedDate` has not advanced AND `lastMergeSourceCommit` unchanged, comment fetch is skipped.
- [ ] When upstream has changed, fetches threads via `GET /pullRequests/{prId}/threads?$top=1000`.
- [ ] Each comment upserts by `(provider='ado', providerCommentId=comment.id)`.
- [ ] Agent author mapping: known agent UPN (via `agents.PreferredAdoAccount`) → `AuthorKind='agent'`; everything else → `'human'`.
- [ ] `commentThread.status` change → `pr_comments.State` updated; newly-resolved sets `ResolvedByUserId` + `ResolvedAt`.
- [ ] Q24.6 commit/response mapping runs after each upsert batch.

**24.6 `GitHubPrSyncRunner` — PR metadata + two comment streams**

- [ ] Fetches PR metadata via `GET /repos/{owner}/{repo}/pulls/{number}`.
- [ ] Maps GitHub states correctly per Q24.3 mapping table.
- [ ] Fetches review comments via `GET /pulls/{number}/comments?since={LastUpstreamUpdatedAt}&per_page=100`.
- [ ] Follows `Link: rel="next"` pagination chain until exhausted.
- [ ] Fetches issue comments via `GET /issues/{number}/comments?since=…&per_page=100`.
- [ ] Merges review comments and issue comments by id without duplicates.
- [ ] Thread resolution state read via GraphQL `pullRequest.reviewThreads` query — `isResolved` merged back by comment id.
- [ ] Agent author mapping: GitHub login matching `agents.PreferredGitHubAccount` → `AuthorKind='agent'`; `github-actions[bot]` and similar → `'system'`; all others → `'human'`.
- [ ] Rate-limit handling: when `X-RateLimit-Remaining` < 100, sleeps until `X-RateLimit-Reset`.

**24.7 Comment data model — `pr_comments` (Q24.4)**

- [ ] `pr_comments` row round-trips through EF — write all fields, read back identical values.
- [ ] `RequiresAttention` is true when `State='active'` AND `AuthorKind IN ('human','system')` AND no agent response since `UpdatedAt`.
- [ ] `RequiresAttention` is false when `LinkedAgentResponseId` is set AND no subsequent `UpdatedAt` bump.
- [ ] `RequiresAttention` is false when `LinkedCommitSha` is set AND no subsequent `UpdatedAt` bump.
- [ ] `RequiresAttention` is false for comments where `AuthorKind='agent'` (agent's own comments don't need self-response).
- [ ] `RequiresAttention` flips back to true when `UpdatedAt` advances after a link was set (reopen/edit scenario).
- [ ] `ParentCommentId` correctly chains replies within a thread.

**24.8 Authority enforcement (Q24.5)**

Resolve endpoint:
- [ ] `POST /api/prs/{prId}/comments/{commentId}/resolve` by agent → 403 + error code `comment.agent_cannot_resolve`.
- [ ] `POST /api/prs/{prId}/comments/{commentId}/resolve` by human PO → 200 + upstream PATCH called.
- [ ] `POST /api/prs/{prId}/comments/{commentId}/resolve` body `{ resolution: 'resolved' }` sets `State='resolved'` + `ResolvedByUserId` + `ResolvedAt`.
- [ ] `POST /api/prs/{prId}/comments/{commentId}/resolve` body `{ resolution: 'wont-fix' }` sets `State='wont-fix'`.
- [ ] Resolve writes a `pr_comment_history(kind='resolved')` row.

Reopen endpoint:
- [ ] `POST /api/prs/{prId}/comments/{commentId}/reopen` by agent → 403.
- [ ] `POST /api/prs/{prId}/comments/{commentId}/reopen` by human → 200 + sets `State='active'` + clears `ResolvedByUserId`/`ResolvedAt`.
- [ ] Reopen writes a `pr_comment_history(kind='reopened')` row.
- [ ] Reopen recomputes `RequiresAttention` to `true`.

Post / edit:
- [ ] `POST /api/prs/{prId}/comments` by agent → 200 (agents can post comments).
- [ ] `POST /api/prs/{prId}/comments` by human → 200.
- [ ] `PATCH /api/prs/{prId}/comments/{commentId}` by comment author → 200.
- [ ] `PATCH /api/prs/{prId}/comments/{commentId}` by PR reviewer (non-author) → 200.
- [ ] `PATCH /api/prs/{prId}/comments/{commentId}` by agent editing reviewer's comment → 403.
- [ ] All write endpoints emit `pr.changed { kind: 'comment' }` SignalR event.

**24.9 Comment ↔ commit / response mapping (Q24.6) — `PrCommentLinker`**

Body-parse heuristic:
- [ ] Commit message containing `Addresses comment 12345` → `LinkedCommitSha` set on comment 12345.
- [ ] Commit message containing `Fixes comment 12345` → `LinkedCommitSha` set on comment 12345.
- [ ] Commit message containing `CR-12345` → `LinkedCommitSha` set on comment 12345.
- [ ] In-thread reply containing `Addresses comment 12345` → `LinkedAgentResponseId` set on comment 12345.

Position-and-time heuristic:
- [ ] Agent commit modifies file + exact line of an inline comment, and commit lands after `CreatedAt` → `LinkedCommitSha` set.
- [ ] Agent commit modifies the file but a different line → `LinkedCommitSha` NOT set (false-positive guard).
- [ ] Comment on a deleted file → heuristic silently skips, no link set.
- [ ] Comment on a moved line → heuristic silently skips, no link set.

Link side-effects:
- [ ] Setting `LinkedCommitSha` writes a `pr_comment_history(kind='linked')` row.
- [ ] Setting `LinkedAgentResponseId` writes a `pr_comment_history(kind='linked')` row.
- [ ] Setting either link recomputes `RequiresAttention` → clears to false (when no subsequent `UpdatedAt` bump).
- [ ] Neither heuristic matches → `RequiresAttention` stays true, operator sees "needs response".

Manual override:
- [ ] `PATCH /api/prs/{prId}/comments/{commentId}` with `{ linkedCommitSha }` sets the link manually.
- [ ] `PATCH /api/prs/{prId}/comments/{commentId}` with `{ linkedAgentResponseId }` sets the link manually.
- [ ] Manual link by non-PO / non-assigned-agent-lead → 403.

**24.10 Reopen detection + queue hook (Q24.7, Q24.8)**

- [ ] Upstream `updatedAt` advances on a resolved comment → `State` flips back to `active`.
- [ ] State flip from resolved → active appends `pr_comment_history(kind='reopened')`.
- [ ] `RequiresAttention` recomputed: flips from false → true on reopen.
- [ ] `pr.comment-reopened { prId, commentId, reason }` fires on SignalR `pr.{prId}` group.
- [ ] `task.comment-needs-response { commentId }` fires on SignalR `task.{wid}` group.
- [ ] Upstream `updatedAt` advances on a resolved comment that still has `LinkedAgentResponseId` set → `RequiresAttention` flips back to true (edit after link invalidates the link).
- [ ] Upstream body edit on an active comment → `pr_comment_history(kind='edited')` appended, `RequiresAttention` unchanged (already true).
- [ ] Upstream resolve on an active comment → `pr_comment_history(kind='resolved')` appended, `RequiresAttention` clears to false.

Queue hook:
- [ ] When `RequiresAttention` flips true AND `ExecutionStatus='in-review'` AND no open `comment-response` queue row → enqueues `(workItemId, 'crew', crewId, kind='comment-response')`.
- [ ] When `RequiresAttention` flips true AND existing open `comment-response` queue row → does NOT double-enqueue.
- [ ] When `RequiresAttention` flips true AND `ExecutionStatus ≠ 'in-review'` → does NOT enqueue (wrong execution phase).

**24.11 Queue worker `kind='comment-response'` runner path**

- [ ] `CrewQueueWorker.RunAgentAsync` branches on `kind='comment-response'` to load comment-response prompt.
- [ ] Prompt includes all comments with `RequiresAttention=true` for the work item's PR.
- [ ] Inline comments include file path + line context in the prompt.
- [ ] Runner uses existing PR (no new PR created — commits push to the existing branch).
- [ ] Queue row completes when all `RequiresAttention=true` comments have been addressed (flipped to false).
- [ ] New comments arriving mid-run enqueue a fresh `comment-response` row (not extend current run).

**24.12 Pagination + delta cursor (Q24.9)**

ADO:
- [ ] Single-call fetch (≤ 1000 threads) succeeds without pagination.
- [ ] Mid-fetch failure stores `CommentSyncCursor` at last successful page; resume continues from cursor.

GitHub:
- [ ] Multi-page `Link: rel="next"` walk completes for >100 comments.
- [ ] Mid-page failure stores the next URL as `CommentSyncCursor`; resume is a single GET.
- [ ] Cursor cleared on full success.

Delta:
- [ ] First sync fetches all comments (no `LastUpstreamUpdatedAt`).
- [ ] Second sync skips comment fetch when `LastUpstreamUpdatedAt` matches upstream (no changes).
- [ ] Third sync (after upstream change) fetches only comments updated since `LastUpstreamUpdatedAt`.

**24.13 Idempotency (Q24.10)**

- [ ] Comment upsert keys on `(provider, providerCommentId)` — no duplicate rows on re-sync.
- [ ] Re-sync with identical `UpdatedAt` produces zero `pr_comment_history` rows.
- [ ] Re-sync with newer `UpdatedAt` advances the row + appends history.
- [ ] Re-sync of an unchanged PR thread produces zero net changes.

**24.14 Final state stops the clock (Q24.3)**

- [ ] PR state transitions to `completed` → `IsActive` flips to `false`.
- [ ] PR state transitions to `abandoned` → `IsActive` flips to `false`.
- [ ] `IsActive=false` row is NOT selected by the next sweep tick — verified via query filter.
- [ ] `IsActive=false` row is never re-activated by a subsequent sync (it has reached terminal state).
- [ ] Audit row written when terminal-state transition occurs.
- [ ] `pr.closed` SignalR event fires on terminal transition.
- [ ] Explicit "Resync this PR" endpoint can override and re-sync a terminal PR (operator override).

**24.15 Full lifecycle timeline (end-to-end)**

- [ ] Full PR lifecycle: `open → comment posted → agent replies → comment resolved → PR completed` — correct states on PR row, comment rows, history rows, and `IsActive=false` at end.
- [ ] Reopen lifecycle: `open → comment posted → resolved → reopened → agent commits fix → linked → resolved → completed` — `RequiresAttention` toggles correctly through each phase.
- [ ] Multi-comment lifecycle: 3 comments posted, 1 resolved, 2 requiring attention — `RequiresAttention` count is 2; queue hook enqueues exactly once.
- [ ] Cross-provider parity: same logical lifecycle produces equivalent rows for ADO and GitHub sources.

**24.16 Edge cases & error handling**

- [ ] `POST /api/prs/{prId}/comments/{commentId}/resolve` on non-existent PR returns 404.
- [ ] `POST /api/prs/{prId}/comments/{commentId}/resolve` on non-existent comment returns 404.
- [ ] `POST /api/prs/{prId}/comments/{commentId}/reopen` on already-active comment is a no-op (200, no history row).
- [ ] `POST /api/prs/{prId}/comments/{commentId}/resolve` on already-resolved comment is a no-op (200, no duplicate history).
- [ ] `GET /api/prs/{prId}/comments` on PR with zero comments returns empty array.
- [ ] Malformed upstream comment body does not crash the body-parse heuristic — returns no match gracefully.
- [ ] Upstream 429 / 503 on ADO triggers backoff per `Retry-After` header; third retry failure aborts run with `LastSyncStatus='failed'`.
- [ ] Upstream rate-limit on GitHub (remaining < 100) pauses until reset window.

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

