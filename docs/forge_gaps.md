# Forge — Implementation Gaps (Phases 0–24)

> Audit of `src/forge/` against `docs/forge_implementation.md`, **Phases 0–21 only** for the gap analysis. Phases 22–24 are forward-looking design (every checkbox `[ ]`); they're listed at the bottom for visibility but are not "gaps" — nothing is claimed yet. Phase R is out of scope by design.
>
> Audit date: **2026-04-29** (re-audit; previous pass 2026-04-26).
>
> **What changed since last audit**: 3 prior gaps filled, 6 new gaps surfaced from the new phases (12.3 / 14.7 / 14.8 + agent template files + Phase 22–24 design vs implementation diff). Priority list re-ordered — release-blocking items still on top, but the open list shrank from 10 → 8 net.

## How to read this

Findings are grouped by severity and tagged with the spec item number. Each entry cites the file path where the gap lives so it can be picked up directly.

- **Blocking** — spec item is marked `[x]` (claimed complete) but the feature is broken or missing entirely. Block the v0.1.0 cut.
- **Material** — spec item is marked `[~]` (partial) but the actual gap is wider than the parenthetical note suggests, OR claims `[x]` while violating its own contract.
- **Minor** — small drift, comment rot, undocumented config, or cosmetic spec / code inconsistency.

A separate **Honest partials** section lists `[~]` items where the parenthetical accurately describes what's missing — no action needed.

A separate **Design-only phases** section lists Phases 22–24 (the forward-looking phases the operator authored on 2026-04-28/29). Their `[ ]` checkboxes are honest — there's no code yet by design.

---

## ✅ Filled since 2026-04-26

These were on the prior audit and are now resolved. Listed so reviewers don't re-investigate.

- ~~**G-3** · Phases 2.13 / 3.13 fidelity tests~~ — `timeline-fidelity.spec.ts` and `workstreams-fidelity.spec.ts` now exist under `src/forge/Forge.UI.Tests/tests/` with snapshot directories. Release gate (`docs/forge/release.md:20`) can be evaluated.
- ~~**G-5** · Phase 6.10 `forge_actions.Supervising` column~~ — `AuditRow.Supervising` (bool) added at `src/forge/Forge.Api/Persistence/Entities.cs:285`. Phase 10.8 can populate without a churn cycle.
- ~~**G-10** · `ForgeHub.cs` stale header~~ — header at `src/forge/Forge.Api/Realtime/ForgeHub.cs:11-20` now correctly enumerates the channels each phase added (Phase 1 `card.changed`, Phase 4 `pr.changed`, Phase 6 `pr.log`, Phase 11.7 agent groups). No longer rotted.

---

## 🔴 Blocking

### G-1 · Phase 6.12 — UI never sends `Idempotency-Key` _(re-confirmed open)_
- **Claim**: `[x]` "Idempotency keys on every write (`Idempotency-Key` header) to make retries safe."
- **Reality**: Server middleware (`src/forge/Forge.Api/Hosting/IdempotencyMiddleware.cs`) honors the header correctly. **The UI never generates one.** `src/forge/Forge.UI/src/api/forgeApi.ts:225-253` `fetchJson` wrapper sets `Accept`, optional `Content-Type`, and `X-Forge-Product-Id` — but no `Idempotency-Key`. None of the ~10 mutating call sites (createCard, moveCard, mergePr, deleteCard, openWorkstreamPr, …) attach one either.
- **Why it matters**: Retries are not safe today; release-checklist gate "Idempotency-Key on a duplicate write returns the cached body" (`docs/forge/release.md:40`) cannot pass.
- **Fix**: in `fetchJson`, when `init.method` is `POST | PATCH | DELETE | PUT`, generate `crypto.randomUUID()` and set the header. ~5 lines.

### G-2 · Phase 6.11 — Per-user PAT headers not parsed _(re-confirmed open)_
- **Claim**: `[x]` "Permission model: per-user PAT only writes on behalf of that user; a server-wide PAT is allowed only for read-only sync."
- **Reality**: `X-Forge-Ado-Pat` / `X-Forge-GitHub-Pat` documented in `docs/forge/providers.md` but never read. Grep of `src/forge/Forge.Api/Providers/*.cs` returns zero matches for either header. All writes route through server-wide `Forge:Ado:Pat` / `Forge:GitHub:Pat`.
- **Why it matters**: writes do not respect per-user attribution — the audit log shows the wrong actor. Phase 6.11 contract is unenforced. **Phase 9 sign-in (which now ships) is the proper successor**, so this gap may be best closed by *demoting* 6.11 to `[~]` and pointing to Phase 9.6 / Phase 9.10 as the per-user path.
- **Fix (option A — small)**: parse the headers in the provider HttpClient pipeline; on read calls fall through to server PAT.
- **Fix (option B — preferred)**: downgrade 6.11 to `[~]` with a parenthetical "superseded by Phase 9 interactive sign-in"; the per-user identity now flows from `IUserContext` → MSAL token cache (Phase 9.5), not from headers.

### G-4 · Phase 5.8 — Webhook HMAC silently bypassed when no secret configured _(re-confirmed open)_
- **Claim**: `[x]` "Webhook receiver `POST /api/webhooks/{provider}` … HMAC verification".
- **Reality**: `src/forge/Forge.Api/Endpoints/WebhooksEndpoints.cs:49-54` (GitHub) and `:81-86` (ADO) wrap signature checks in `if (!string.IsNullOrEmpty(secret))`. With no secret configured the endpoint accepts any request.
- **Why it matters**: a dev install that forgot to set a secret is wide-open to spoofed events. Spec claims unconditional HMAC.
- **Fix**: refuse to start the endpoint when running outside `Development` and the secret is absent (fail-fast on boot). In `Development`, log a Warning on every accepted request when no secret is set.

---

## 🟡 Material

### G-11 · Phase 12.3 — `POST /api/work-items/bulk` claimed `[x]` but is deferred _(NEW)_
- **Claim**: `[x]` "POST /api/work-items/bulk — body `{ ids[], op, payload }`. Idempotency-Key required."
- **Reality**: the parenthetical on the same bullet says "deferred — UI bulk action bar iterates per-id PATCH/move/delete calls today, which is enough for sample-size selections; the dedicated bulk endpoint is a Phase R candidate when selection sizes regularly exceed ~50". The endpoint **does not exist** in `src/forge/Forge.Api/Endpoints/WorkItemsEndpoints.cs` — only the per-id verbs are mapped.
- **Why it matters**: a `[x]` next to a "deferred" parenthetical is a contradiction inside a single bullet. Reviewers reading the spec see the box ticked and assume the endpoint exists; UI authors discover the absence the hard way.
- **Fix**: downgrade to `[~]` (or `[ ]`) and lift the deferred note out of the parenthetical into the body so it's unambiguous. Code change is **not** wanted at v0.1.

### G-12 · Phase 14.7 — Promotion job has no nightly scheduler _(NEW)_
- **Claim**: `[~]` with a "Remaining: the nightly hosted-service scheduler (one timer per TZ), SignalR `agent.level.changed` broadcast, and the monthly-review snapshot writer".
- **Reality**: `src/forge/Forge.Api/Services/PromotionRunner.cs` exists and works correctly when invoked via `POST /api/admin/promotion/run`, but **no `IHostedService` timer drives it on a schedule**. Operator must hit the admin endpoint manually for promotions to land.
- **Why it matters**: trailing-20 promotions / demotions never fire automatically — the level + hourly fields stay stuck at whatever they were when an operator last clicked the admin endpoint. The Phase 14 economic loop is functionally inert in normal operation.
- **Fix**: register `PromotionJob : IHostedService` that wakes at 02:00 in each distinct crew TZ and calls the existing `PromotionRunner.RunAsync`. Single-timer-per-TZ implementation is in spec 14.7. Once shipped, flip 14.7 to `[x]`.

### G-13 · Phase 14.8 — `/api/crews/{id}/salary` deprecation alias has no log line or counter _(NEW)_
- **Claim**: `[~]` Q14.7 documents a 90-day deprecation alias on the renamed `/spend` endpoint that "emits `forge.api.deprecated`"; spec note acknowledges "Remaining: explicit deprecation log line + counter on the legacy `/salary` route".
- **Reality**: `src/forge/Forge.Api/Endpoints/CrewsEndpoints.cs` keeps the legacy `/salary` route as a passthrough returning the spend payload — no `forge.api.deprecated` counter increment, no Warning log line. Operators using the legacy URL get no signal that they should migrate.
- **Why it matters**: the 90-day deprecation window is meaningless without observability. When the cut-over date arrives there's no data to confirm "no one's still hitting /salary".
- **Fix**: in the legacy `/salary` handler, emit a one-line `ILogger.LogWarning` with the caller's user-id + an `OpenTelemetry.Counter` increment tagged `route=/api/crews/{id}/salary`. ~10 lines.

### G-6 · Phase 3.7 — Worktree guard does not resolve symlinks _(re-confirmed open)_
- **Claim**: `[x]` "guard against operating outside the configured root … (no `..`, no symlinks out of the repo)".
- **Reality**: `src/forge/Forge.Api/Services/Git/GitWorktreeService.cs:151-155` uses `Path.GetFullPath()` then `pathFull.StartsWith(rootWithSep)` — a string prefix check after path canonicalization that resolves `..` but **not** symlinks. A symlink inside the worktree pointing outside the repo passes the check.
- **Why it matters**: low real-world risk (operator has to plant the symlink themselves) but the spec's stated contract is violated. Note that the **production agent path now bypasses `GitWorktreeService` entirely** (Phase 16.18 runs `git worktree add` directly in `CrewQueueWorker.cs`), so this gap shrinks to "ad-hoc UI worktree creation only". Worth fixing for correctness even at low blast radius.
- **Fix**: after `Path.GetFullPath`, call `File.ResolveLinkTarget(path, returnFinalTarget: true)` if the file exists, then re-prefix-check. Same shape as the equivalent in `RepoCloneService`.

---

## 🟢 Minor

### G-7 · Phase 4.1 — Spec prose says 4 columns; code + prototype use 3 _(re-confirmed open — spec edit not made)_
- **Claim**: `[~] 4.1` — "Four-column PR board with `pr.col` mapping (`implementation | build | review | merged`)" with parenthetical "prototype has 3 cols — kept 3 per fidelity constraint; spec ↔ prototype contradiction needs resolution".
- **Reality**: `src/forge/Forge.UI/src/prs/PrBoard.tsx:15` defines `PCOLS = ['implementation', 'review', 'build']` (3 cols, prototype-correct). The spec's parenthetical correctly identifies the contradiction but leaves the 4-column wording in the body.
- **Fix**: edit the spec prose down to 3 columns and drop the parenthetical. Code is correct (prototype is the source of truth per the hard-fidelity constraint).

### G-8 · Phase 2.11 — Timeline telemetry only console-logged _(re-confirmed: partially fixed)_
- **Claim**: `[x]` "Telemetry: count timeline opens, time-on-view; structured logs `forge.timeline.*`".
- **Reality**: `src/forge/Forge.UI/src/views/TimelineView.tsx:43-46` now calls `console.info('forge.timeline.opened' / '.closed')` with a duration, but the calls don't route through `logAction()` (the structured client ring buffer at `src/forge/Forge.UI/src/telemetry.ts:29`). They land in browser devtools only and are invisible to the "report problem" capture flow (Phase 7.4).
- **Fix**: replace the two `console.info` calls with `logAction({ event: 'forge.timeline.opened', durationMs: ... })` so they're captured in the ring buffer alongside the rest of the action stream.

### G-9 · Phase 7.6 — Azure AD config keys not discoverable _(re-confirmed open)_
- **Claim**: `[~] 7.6` parenthetical: "Azure AD JWT pipeline is config-only skeleton".
- **Reality**: `src/forge/Forge.Api/Auth/ForgeAuth.cs:37-42` defines `AzureAdOptions { TenantId, ClientId, Audience }`, but `docs/forge/setup.md:44` only documents `Forge:Auth:Mode` and `Forge:Auth:Secret`. No `forge.env.sample` exists.
- **Why it matters**: Phase 9 (interactive sign-in) **supersedes** the AAD-JWT path on Windows. The skeleton is dead-code-adjacent.
- **Fix**: drop the `AzureAdOptions` skeleton (delete the unused class + the `Forge:Auth:Mode='azure-ad'` branch) and update 7.6's parenthetical to "single-user secret middleware ships; Phase 9 replaced the AAD-JWT plan with WAM + OAuth-PKCE". Cleaner than documenting config keys for a path nothing uses.

### G-14 · Phase 13 / 14 — Several `agents/templates/*-system.md` files referenced by spec are absent _(NEW)_
- **Claim**: Spec Phase 13.2 references `agents/templates/{plan,coding}-system.md`; Phase 13.7 references `domain-system.md`; Phase 14.6 references `judge-system.md` + `eval-system.md`.
- **Reality**: only `agents/templates/coding-system.md` and `agents/templates/plan-system.md` exist on disk. `judge-system.md`, `eval-system.md`, `domain-system.md` are absent.
- **Why it matters**: Phase 13 is entirely `[ ]` so the missing `domain-system.md` is honest. Phase 14.6 is `[~]` and explicitly lists "judge/eval templates remain" — also honest. **The eval template's absence is hidden** because `CrewQueueWorker.cs:758-770` inlines the rubric directly into the evaluator prompt instead of loading the template, so the runtime works but the template-driven design from spec 14.6 is bypassed.
- **Fix**: extract the inline evaluator rubric in `CrewQueueWorker.cs:758-770` into `agents/templates/eval-system.md` and load it from disk like soul.md. Same shape Phase 14.6 already specifies. ~30 lines of code change + one new file.

---

## ✅ Honest partials (no action needed)

These items are correctly marked `[~]` with parentheticals that match reality. Listed so the next auditor doesn't re-investigate them.

| Item  | Partial scope (parenthetical)                                       | Verified                                    |
|-------|---------------------------------------------------------------------|---------------------------------------------|
| 4.5   | PR description + build-log are server-side stubs                    | `PrsEndpoints.cs:77-88`                     |
| 4.9   | Re-run / cancel return 202, no real CI                              | `PrsEndpoints.cs:123-141`                   |
| 5.7   | Polling + jitter; no ETag/If-None-Match                             | `ProviderSyncService.cs:67`                 |
| 7.5   | `ILogger` + 50-entry client ring buffer; no OTel SDK                | `telemetry.ts:8`                            |
| 7.6   | Single-user secret middleware ships; AAD is skeleton                | `ForgeAuth.cs:37-42` _(see G-9 — kill it)_  |
| 10.4  | Defaults pre-fill UI deferred; resolver path live                   | `AdoConfigResolver.cs`                      |
| 10.5  | Per-endpoint `defaultsApplied` echoing deferred                     | spec 10.5 parenthetical                     |
| 11.3  | `/settings/agents` editor deferred; filesystem is source of truth   | `SoulService.cs`                            |
| 14.4  | DeliverableLoop time-cap (`paused-eod`) deferred                    | `DeliverableLoop.cs`                        |
| 14.9  | Performance tab nine-KPI columns + window toggle deferred           | `CrewsView.tsx:59`                          |
| 14.10 | DeliverableScorePanel reuse + dispute affordance restriction        | `DeliverableScorePanel.tsx`                 |
| 14.11 | TopBar Spend + Payroll chip popovers (today tooltip-only)           | `TopBar.tsx:42-60`                          |
| 16.3  | LeadAutoDispatcher hands off via queue (correct now per Phase 17.12) | `LeadAutoDispatcher.cs:193`                |
| 16.10 | LiveExecutionView countdown + git-graph + diff split deferred       | `LiveExecutionView.tsx`                     |
| 17.6  | `useQueueChannel` joins on mount; auto-subscribe-from-store deferred | `useQueueStore.ts` + `InboxView.tsx:27`    |
| 17.7  | InboxView sidebar entry not yet lead-only-gated                     | `SideBar.tsx`                               |
| 18.6  | Lead 24×7 exemption (Q18.3) is fully designed; not yet shipped      | spec 18.6 — known `[ ]`                     |

---

## 🟦 Design-only phases (Phases 22–24)

These phases are forward-looking — every checkbox is `[ ]` and every described table / endpoint / hosted-service is intentionally absent from the code. Listed here so reviewers know the absence is **by design**, not drift. When implementation lands, audit the new code against the spec contract.

- **Phase 22 — `AssignedAgent` field & assignment history** (spec lines 1286–1404)
  - `WorkItemRow` lacks `AssignedAgentJson`; `agent_assignments` table absent. ✓ matches spec.
  - `CrewQueueWorker.cs:347-348` still writes the runtime payload (crewId/agentId/generatorAgentId/...) onto `AssignedToJson` — the legacy behavior the spec calls out as needing to migrate to `AssignedAgent`.

- **Phase 23 — Scheduled work-item sync** (spec lines 1410–1559)
  - No `Phase23Migration.cs`; `RepoRow` lacks `AreaPathsJson`, `LastSyncAt`, `LastSyncStatus`, `LastSyncCursor`, `IsRunningSince`, `AgentReadyDefaultTeam`, `InitialSyncWindowDays`. ✓ matches spec.
  - `ProviderSyncService` still on the legacy 60-s polling cadence; no `WorkItemSyncJob` registered.
  - `SyncConstants.cs` does not exist (referenced by Phase 23 + 24).

- **Phase 24 — PR state & comment-lifecycle sync** (spec lines 1562–1771)
  - No `Phase24Migration.cs`; `PrRow` lacks `IsActive`, `LastSyncAt`, `CommentSyncCursor`, `LastUpstreamUpdatedAt`. ✓ matches spec.
  - `pr_comments` + `pr_comment_history` tables absent; `PrSyncJob` not registered; `PrCommentsEndpoints.cs` does not exist.

---

## Cross-cutting unknowns

The audit was static-only. Before the v0.1.0 cut these need a live check:

- **`dotnet test src/forge/Forge.Api.Tests` green?** Not verified.
- **`npx playwright test` green?** Not verified. (G-3 closed the missing-spec gap, so all suites can now be evaluated.)
- **`__snapshots__/` baselines committed to git?** Filesystem listing showed `*-snapshots/` dirs alongside the spec files; confirm via `git ls-files` against those paths.
- **`dev.ps1` (item 0.17) boots both servers cleanly on a fresh checkout?** Not exercised in this audit.
- **Migration ordering on a fresh DB**: `EnsureCreatedAsync` + Phase8 → Phase21 in `Program.cs:179-201` runs in order; a smoke run on a fresh `forge.db` is not part of this audit.

---

## Recommended fix order (priority re-ordered for this pass)

1. **G-1 (Idempotency-Key on UI)** — small change, unblocks release gate #40. ~5 LOC.
2. **G-4 (webhook HMAC required outside Development)** — security hardening; small, removes a silent-failure mode.
3. **G-12 (Phase 14.7 nightly promotion job)** — without it, the Phase 14 economic loop never fires. Single timer-per-TZ wiring per the spec; cleanly closes a `[~]` to `[x]`.
4. **G-2 (per-user PAT) — option B (downgrade + cite Phase 9)** — converts a wrong claim into honest spec text. Zero code change; Phase 9 already covers the per-user case.
5. **G-11 (Phase 12.3 bulk endpoint claim)** — spec correction, `[x]` → `[~]`. Zero code change.
6. **G-14 (`eval-system.md` extraction)** — small refactor that closes a divergence between Phase 14.6's design and `CrewQueueWorker.cs`'s inline rubric.
7. **G-13 (Phase 14.8 deprecation log)** — observability hygiene. ~10 LOC.
8. **G-7, G-8, G-9 (spec/docs hygiene)** — batch into a single PR.
9. **G-6 (symlink resolution)** — low-risk security hardening; can wait for Phase R or a security pass since the prod path bypasses `GitWorktreeService`.

After each fix, re-run this audit and either tick the entry off (move to the **Filled** section at the top) or update the file path / line numbers cited above.

---

## Audit method

- **Static read** of `src/forge/Forge.Api/`, `src/forge/Forge.UI/`, `src/forge/Forge.Api.Tests/`, `src/forge/Forge.UI.Tests/`.
- **Spec contract** = `docs/forge_implementation.md` Phases 0–24, with `[x]` claims taken as the test target and `[~]` parentheticals taken as the expected partial-scope.
- **No live runs** of `dotnet test` / `playwright test` / a clean-DB boot. See **Cross-cutting unknowns**.
- **No git history mining** — the "filled since 2026-04-26" section identifies fixes by present-day code shape, not by commit log.
