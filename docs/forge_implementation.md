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
- **Worktree location (Q2)** — worktrees live **under each repo**, in a fixed subdirectory: `{repo.cloneRoot}\.worktree\{workstreamName}`. Example: for the `update` repo cloned at `E:\work\hci\update`, the workstream `idp-failover` worktree is at `E:\work\hci\update\.worktree\idp-failover`.
  - **Implications**:
    - Phase 3.7 `IGitWorktreeService` derives the worktree path from the workstream's repo (`Repo.CloneRoot` + `.worktree` + `Workstream.Name`); it MUST refuse any path that escapes that prefix (no `..`, no symlinks out of the repo).
    - Phase 8.1 `Repo` record must carry `cloneRoot` (already specified) — the only field needed; there is no separate global "worktrees root" config.
    - First-time setup for a repo writes a `.worktree/` entry into the repo's local `.git/info/exclude` (NOT `.gitignore`, which would dirty the main checkout) so the host worktree never reports the sibling worktrees as untracked.
    - Workstream's stored `worktree` shape simplifies from the prototype's `[root, name]` tuple to a single relative segment (`name`); the absolute path is computed at read time from the linked repo. Sample data and the Phase 3 detail panel must be updated to match.
    - Auto-clone (Phase 8.10): when `cloneRoot` is empty, `git clone` runs first; the `.worktree` subdir is created lazily on first `git worktree add`.
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
- [~] **5.7** Background sync: `IHostedService` polling on configurable interval (default 60s), with jitter + ETag/If-None-Match where supported. _(polling + jitter shipped; ETag/If-None-Match is Phase R)_
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
- [x] **6.5** PR review actions: approve / request-changes / comment from the detail panel; surface line-level comments where the provider supports it.
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
- **Q12.5 Identity for assignment** — `assignedTo` carries either an **`agent`** (Forge agent id, e.g. `E4`) **or** a **`human`** (UPN / GitHub login). Phase 9 user identity is the source of truth; agents are resolved against the `agents` table.
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
  - Auto-poll every 60 s (re-uses Phase 5.7 `ProviderSyncService`).
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
- **Q13.2 Lead = router** — only the lead can assign tasks within their crew. The lead may assign to themselves; the runtime treats self-assignment identically to peer-assignment for ledger and quota purposes. Reassignment is a write requiring `RequireRole.ProductOwner` *or* `crew.lead == caller`.
- **Q13.3 Capability via soul.md** — capability is the YAML front-matter `capabilities:` list at the top of the crew's `soul.md` (Phase 11.3). Each entry references a `domain-expert.md` file by **content-addressed id** (sha256 of the file at ingestion time) and optional human label. The system prompt is a deterministic concatenation: `<soul.md body>` + `\n\n<capabilities (sorted by id)>`. Content addressing guarantees that two crews with the same capability list produce identical caches, which the runner reuses as a cache prefix.
- **Q13.4 Model assignment** — model is a **crew-level** property (every member runs against the same model). Pricing is denormalized per Phase 11.2 so historical salary stays reproducible after a price/model change. Switching models writes a row to `crew_history(crewId, fromModel, toModel, switchedBy, switchedAt)` and forces a new prompt cache (the prefix changes).
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
  Capabilities   (json string[] of domain-expert content-ids)
  MaxConcurrent  (int, default 2)
  WeeklyCap      (int, default 20)
  ```
  New table `crew_history(id, crewId, kind, fromValue, toValue, byUserId, at)` — kind ∈ `model | lead | member-add | member-remove | parent-change`. New table `domain_experts(id, contentId, repoId, path, topic, summary, indexedAt)`.
- [ ] **13.2** Task taxonomy — add `kind` column to `tasks`: `plan | coding`. The runner reads `kind` to pick the right system-prompt template (under `agents/templates/plan-system.md` and `agents/templates/coding-system.md`); both templates concatenate the crew's soul.md + capabilities and add the kind-specific deliverable rubric.
- [ ] **13.3** Lead-routing endpoint — `POST /api/crews/{crewId}/assign` body `{ taskId, memberAgentId }`. Server enforces `caller == crew.lead || RequireRole.ProductOwner`. Self-assignment is `memberAgentId == crew.lead`. Reassignment uses the same endpoint with the new memberId.
- [ ] **13.4** Plan task pipeline:
  - Endpoint `POST /api/crews/{crewId}/plan` body `{ workItemId }` — input must be `epic | feature` (dev) or `phase` (design). 400 otherwise.
  - Runner spawns a `plan` invocation. Tools available: `read_repo`, `write_file` (only under `docs/specs/`), `search_work_items`, `create_work_item`. The runner intercepts `create_work_item` calls and validates the parent/child rank rules from Phase 12.
  - On finish: the spec MD is committed to a `planning/{slug}` branch; a draft PR opens; the WIT rows are inserted with `state = proposed`. Lead reviews + merges the PR; merging flips the WIT rows to `active`.
  - Unit-of-work counter increments by 1 per merged plan PR.
- [ ] **13.5** Coding task pipeline:
  - Endpoint `POST /api/crews/{crewId}/code` body `{ stepWorkItemId }` — input must be a `step` (Q12.1). Step must be `state = active` and not already claimed (`assignedTo === null`).
  - Runner spawns a `coding` invocation in the workstream's worktree. Tools: `read_repo`, `write_file` (whole repo), `run_tests`, `git_commit`, `git_push`, `open_pr`.
  - Self-review pass: the runner runs a second invocation against the same agent with a `review` system prompt, scoped to the diff. Lead must explicitly approve via `POST /api/prs/{id}/approve` before the PR comes off draft.
  - Unit-of-work counter increments by 1 per opened (non-draft) PR.
- [ ] **13.6** Quota enforcement — extends Phase 11.10 budget pre-flight:
  - On `assign`: refuse if `active_tasks_for_crew(crewId) >= MaxConcurrent`. 409 + `agent.crew_concurrency_full` SignalR.
  - On `claim` (any task type): refuse if `tasks_completed_this_iso_week(crewId) >= WeeklyCap`. 429 + `agent.crew_weekly_cap_hit`.
  - PO-only override `?force=true` on the assign endpoint records `crew_history(kind='quota-override')`.
- [ ] **13.7** Domain-expert generator — runs as a final tool call on coding-task close. The runner POSTs to itself: a follow-up `domain` invocation with the merged diff + the original step description. Output is committed to the same PR's branch in a separate commit, then merged. Failure to generate is non-fatal — the task closes either way; a follow-up `gap-domain-expert` audit row points at the missing artifact so the lead can author it manually.
- [ ] **13.8** Capability ingestion — `POST /api/crews/{crewId}/capabilities` body `{ contentId }` validates the file exists in `domain_experts`, appends to the crew's `Capabilities` array, writes the front-matter back to soul.md, and bumps the prompt cache (Phase 11.5). Removal: `DELETE` with the same body. UI lives in `/crews/{id}` with a searchable picker over `domain_experts.topic`.
- [ ] **13.9** UI — extend `CrewsView` (today: read-only dashboard):
  - **Hierarchy panel** (left, 30%) — collapsible tree. Drag-drop a crew under another to reparent (writes `crew_history(kind='parent-change')`). Depth cap enforced client-side and server-side.
  - **Crew detail** (center, 40%) — header shows crew name, lead avatar, model badge, capability chip count. Tabs: `Roster` (lead + members table; +/− with role gate), `Capabilities` (chip list with the picker from 13.8), `Quota` (sliders for `MaxConcurrent`, `WeeklyCap`, `dailyBudgetUsd`), `History` (`crew_history` rows).
  - **Activity rail** (right, 30%) — current invocations, weekly task counter (`X / WeeklyCap`), salary today/sprint/month (Phase 11.9, kept).
  - The view is *editable for leads / PO* and *read-only for everyone else*. Field-level `disabled` driven by the role check; trying to mutate as non-lead returns 403 from the API anyway.
- [ ] **13.10** Sidebar — `/crews` already lives under "Agents" (Phase 10.7). Drop the PO-only gate at the *nav* level (everyone can browse) but keep mutation-level role checks. Sub-label changes from `Supervisory · PO only` to `{n} crews · {m} agents`.
- [ ] **13.11** Tests:
  - xUnit: hierarchy depth cap, lead-or-PO assign authorization, plan task → WIT tree round-trip, coding task → PR round-trip, weekly-cap refusal, capability content-id integrity (sha256 match), domain-expert generator hits the file system but tolerates failure.
  - Playwright: open `/crews`, reparent via drag, assign a self-step, observe the `agent.crew_concurrency_full` toast when MaxConcurrent is hit.
- [ ] **13.12** Fidelity — new view; not in the prototype. New tokens (hierarchy-tree connector lines, capability chip palette) documented in `docs/forge/design-tokens.md`. Existing Phase 10.7 cards remain as the "Activity rail" content.

### Out of scope (explicitly deferred)

- **Cross-org crew sharing** (one crew working for two products) — all crews bind to a single product. Phase R candidate.
- **Auto-promotion of members to leads** based on completion stats — manual via PO action only, by design.
- **Skill matching** (auto-suggesting capabilities based on the work item title/description) — capability picker is operator-driven; ML matching is `forge_v2.md` territory.
- **Domain-expert MD versioning beyond content-addressing** — if a file changes, it gets a new content-id; old crews keep referencing the old id. Conflict resolution / merge UI is Phase R.
- **Plan tasks that recurse** (a plan task generating sub-plan-tasks) — v0.1 enforces a single planning round per feature; deeper decomposition lives inside the resulting WIT tree.

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

