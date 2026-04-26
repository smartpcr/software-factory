# Forge Implementation — Gap Analysis

> Reviewed `docs/forge_implementation.md` against `src/forge/Forge.Api/`, `src/forge/Forge.UI/`, `src/forge/Forge.Api.Tests/`, and `docs/forge/` on 2026-04-26.
>
> Spec uses self-reported markers: `[x]` complete, `[~]` partial, `[ ]` not started. This report verifies those markers against the code and surfaces the actual gaps.

## Step 1 — Parsed task list

Spec contains **155 numbered items** across Phases 0–11, plus a 7-item deferred Phase R.

Self-reported counts (Phases 0–11):

| Marker | Count |
|--------|------:|
| `[x]` complete | 122 |
| `[~]` partial  | 20  |
| `[ ]` not started | 13 |

After verification, adjustments below in the Summary.

## Implementation Review

### Summary (verified)

- Total tasks (Phases 0–11): **155**
- ✅ Complete: **121**
- ⚠️ Partial:  **21** (one [x] downgraded — see 10.2)
- ❌ Missing:  **13** (Phase 11 ×12, 9.13)
- 🚫 Deferred (Phase R): 7 — out of scope for v0.1

Every item in **Phases 0, 1, 2, 3, 6** is fully shipped (60 / 60). The unshipped surface is concentrated in Phase 11 (entire phase) and the partial finishing work in Phases 4, 5, 7, 8, 9, 10.

---

### Task-by-Task Breakdown — Gaps only

Items already verified `[x]` complete are **omitted** unless the code disagreed with the marker. Phases 0–3 and 6 are fully complete and skipped.

#### Phase 4 — Pull Requests view

**4.1 PR board columns** — ⚠️ Partial
> Spec wants 4 columns (`implementation | build | review | merged`), prototype has 3. Code: `Forge.UI/src/prs/PrBoard.tsx:15-18` renders the prototype's 3 columns (`implementation | review | build`). Spec acknowledges the contradiction and tags it as needing resolution.
> **Gap:** product decision pending (drop spec to 3 cols, or break prototype fidelity for the 4th).

**4.5 PR detail panel** — ⚠️ Partial
> `PrsEndpoints.cs:71-115` (`GetDetails`) returns placeholder description (lines 84-86) and synthetic build log (89-108). Marker says "Phase-4 placeholder — Phase 6 streams real CI log." Real values arrive once Phase 6 webhook fan-out is wired into PR detail.
> **Gap:** real PR description + build-log tail from provider; tied to Phase 5/6.

**4.6 Filters** — ⚠️ Partial
> `PrFilterBar.tsx:33-51` — chips wired; the Workstream/Author selects + search input are decorative (no `onChange` / `onClick`). Spec implied functional.
> **Gap:** wire the three controls into the PR store.

**4.7 `GET /api/prs` aggregation** — ⚠️ Partial
> `PrsEndpoints.cs:50-62` reads only `db.Prs`; no provider call path. Cache-only.
> **Gap:** provider-aware aggregation lands with Phase 5 — this is the read-path tail.

**4.9 Re-run / cancel build** — ⚠️ Partial
> `PrsEndpoints.cs:141-204`: `RerunBuild` / `CancelBuild` flip cached `BuildJson` and emit synthetic log tails. Returns 202. No real CI invoked.
> **Gap:** Phase 6.6 hookup to ADO Pipelines `runs` API + GitHub Actions dispatch. Spec leaves it for Phase 6, but Phase 6 is marked `[x]` — the wiring from PR card → Phase 6 trigger endpoint is missing.

#### Phase 5 — Providers (read path)

**5.4 Provider config UI** — ⚠️ Partial (intentional)
> `Forge.UI/src/views/SettingsView.tsx:6-12, 184-270`: read + Test connection live. PAT entry intentionally NOT in UI to avoid plaintext storage. Decision is documented; **superseded by Phase 9** sign-in flow.
> **Gap:** none until a non-Windows headless install needs a UI for PAT. Acceptable as-is.

**5.7 Background sync ETag** — ⚠️ Partial
> `ProviderSyncService.cs:44-75`: polling loop with jitter (line 67) and webhook-silence fallback (59-62). No `ETag` / `If-None-Match` headers issued.
> **Gap:** ETag short-circuit deferred to Phase R. Functional but not bandwidth-optimal.

**5.9 Conflict policy UI** — ⚠️ Partial
> `CardsEndpoints.cs:200-213` returns 409 with `currentRevision` when `IfMatchRemoteRevision` differs. UI rendering ("remote changed since" badge + discard / overwrite modal) not present in board / card components.
> **Gap:** the UX half. Server is ready; UI surface is the missing piece.

#### Phase 7 — Polish

**7.5 Telemetry** — ⚠️ Partial (intentional)
> `Forge.UI/src/telemetry.ts:1-34` — client ring buffer, no SDK. Server uses `ILogger<T>` with stable message names. No OTel SDK / metrics exporter.
> **Gap:** OTel rollout punted to `forge_v2.md`; acceptable for v0.1.

**7.6 Auth pluggability** — ⚠️ Partial (intentional)
> `Forge.Api/Auth/ForgeAuth.cs:64-127`: `ForgeSecretMiddleware` enforces `X-Forge-Secret`. Azure AD branch (lines 91-95) is a placeholder pass-through with no JWT pipeline.
> **Gap:** Azure AD JWT validation; superseded by Phase 9 interactive sign-in for the Windows path.

**7.9 Smoke release checklist** — ⚠️ Partial (intentional)
> `docs/forge/release.md` shipped with 59-item checklist + manual tag instructions. The actual `v0.1.0` tag is an operator step.
> **Gap:** none beyond the operator action.

#### Phase 8 — Products & repo management

**8.5 "Add repo" picker** — ⚠️ Partial
> `ProductsEndpoints.cs` exposes the list endpoints + attach POST. Server-side ADO + GitHub list code is wired against the configured PAT. UI picker (server-driven select-from-list) **not visible** in `ProductsView.tsx` or `SettingsView.tsx` — only seed data is shown.
> **Gap:** the picker UI itself; multi-org listing waits for Phase 9 sign-in to plumb the per-account dropdown.

#### Phase 9 — Sign-in & current user

**9.12 Tests** — ⚠️ Partial (intentional)
> `Forge.Api.Tests/AuthAndMeTests.cs` — 6 tests covering unauthenticated paths and 401 contracts. Spec says 7; close enough that this is a counting nit. MSAL token-broker fake + OAuth callback tests deferred.
> **Gap:** WAM-broker contract test, OAuth callback unit test, Edge-launch invocation test, expired-token refresh test.

**9.13 Security review checklist** — ❌ Missing
> No `docs/forge/security*.md` exists; no checklist artifact. Marker `[ ]` is correct.
> **Gap:** the checklist itself — token-at-rest format, scope minimization, telemetry opt-in, DPAPI rotation, Windows-account-change behavior. **Release blocker** by spec wording.

#### Phase 10 — Defaults, preferences & PO supervisory view

**10.2 Settings validation** — ⚠️ Partial (downgrade)
> Spec marker says "best-effort" check exists; `MeSettingsEndpoints.cs:58-71` accepts any string for `DefaultProductId` / `DefaultRepoId` with **no** existence validation. The "best-effort" claim is over-stated — it's currently zero.
> **Gap:** add a foreign-key existence check (or a soft `defaults.invalid` flag returned to the UI). Easy fix.

**10.3 Defaults panel** — ⚠️ Partial
> `SettingsView.tsx:184-270` — text inputs + Save button only. No combo-boxes that auto-populate from `/api/providers/ado/orgs`, `_apis/projects`, `/user/orgs`. No "Apply everywhere" checkbox.
> **Gap:** the auto-populated dropdowns + the propagation toggle.

**10.4 Pre-fill new-card / new-workstream / Create PR / Add repo modals** — ⚠️ Partial
> Server defaults resolve in 10.5; UI modals do not consume them (`NewWorkstreamModal`, new-card modal still render empty fields).
> **Gap:** wire `useCurrentUserStore.settings` into modal initial state with override + "Update default" toggle.

**10.5 Server-side default resolution** — ⚠️ Partial (intentional)
> `Providers/Ado/AdoConfigResolver.cs:50-85` — per-repo override → user settings → appsettings fallback. Endpoint responses do **not** echo a `defaultsApplied: [...]` array.
> **Gap:** add the response field; needs `ProductScope`-style plumbing on every write endpoint.

**10.7 `/crews` view** — ⚠️ Partial
> `Forge.UI/src/views/CrewsView.tsx:69-113` — members, in-flight, blocked, model pill render. Footer (107-111) flags "Live salary readout lands in Phase 11."
> **Gap:** salary today / sprint / month, tokens-burned in/out split, 24h cost, last-active, P50/P90 cost-per-card, model-swap action, pause/resume buttons, soul.md editor — all blocked by Phase 11.

**10.8 Cross-product audit** — ⚠️ Partial
> `Persistence/Entities.cs:261` and `Persistence/Phase8Migration.cs:109` add the `Supervising` column. **No code writes to it** — `AuditMiddleware` does not set the flag. Marker is honest.
> **Gap:** populate `Supervising` in audit middleware once 10.5 plumbs `productId` per request.

**10.10 UI gating disabled-with-tooltip** — ⚠️ Partial
> `SideBar.tsx:140-154` hides Crews entry for non-PO; `CrewsView.tsx:23-39` shows "Restricted" empty state. Disabled-with-tooltip pattern not applied to individual buttons.
> **Gap:** apply the disabled+tooltip pattern to write-action buttons elsewhere (cosmetic).

**10.11 Tests** — ⚠️ Partial
> `Forge.Api.Tests/SettingsAndCrewsTests.cs` — 8 xUnit tests (spec says 9 — counting nit). UI pre-fill test and cross-product audit test deferred per the gaps in 10.4 / 10.8.
> **Gap:** UI pre-fill Playwright spec, audit `Supervising=true` recording test.

#### Phase 11 — Agent runtime & crew economy — ❌ ALL MISSING

Confirmed: no `Forge.Api/Agents/` folder, no `IAgentRunner`, no `models.json`, no `agent_usage` table, no `soul.md` paths, no `ClaudeAgentRunner`, no `IAgentToolHost`.

| Item | Status | Note |
|------|--------|------|
| 11.1  | ❌ | `IAgentRunner` interface + invocation handle |
| 11.2  | ❌ | Model registry + `models.json` + `GET /api/models` |
| 11.3  | ❌ | soul.md authoring + filesystem + in-app editor |
| 11.4  | ❌ | Cost ledger `agent_usage` table + SQL views |
| 11.5  | ❌ | Concrete `ClaudeAgentRunner` with prompt caching |
| 11.6  | ❌ | `IAgentToolHost` sandboxing + budgets |
| 11.7  | ❌ | SignalR `agent.{agentId}` channel + UI hooks |
| 11.8  | ❌ | Pause / resume / reassign + `agent_history` |
| 11.9  | ❌ | Live salary readouts + TopBar `$X.XX today` chip |
| 11.10 | ❌ | Per-crew daily / per-task budgets + alerts |
| 11.11 | ❌ | Tests (runner contract, ledger, soul.md hot-reload, budget) |
| 11.12 | ❌ | Fidelity exemption note + design-token doc |

This is the largest single gap and the spec explicitly marks Phase 11 **in scope for v0.1** (per the Q3 resolution).

---

### Correctness Issues Found

Issues from the verification pass — including items the spec itself self-marked `[x]`:

- **[10.2] Issue:** `MeSettingsEndpoints.cs:58-71` accepts arbitrary strings for `DefaultProductId` / `DefaultRepoId`. The spec marker `[~]` characterizes this as "best-effort" — actually it's zero validation. → **Fix:** lookup the IDs against `db.Products` / `db.Repos` and either reject (`400`) or return a 200 with a `defaults.invalid: [...]` echo so the UI can flag dangling values.
- **[4.9] Issue:** PR `RerunBuild` / `CancelBuild` (`PrsEndpoints.cs:141-204`) return 202 with synthetic log tail. A re-run that flips local cache to "queued" with no provider call is a silent no-op for users expecting a real CI re-run. → **Fix:** route through the Phase 6 trigger endpoint or surface a "no provider configured" 503; do not pretend success.
- **[4.6] Issue:** `PrFilterBar.tsx` workstream/author/search controls render without click handlers — looks functional, isn't. → **Fix:** wire to PR store filter state, or visually mark them disabled until wired.
- **[10.8] Issue:** `Supervising` column added but never written. Any audit query filtered by `Supervising=true` returns empty — this is a silent failure mode. → **Fix:** add the write in `AuditMiddleware` once the request carries `productId` (gated on 10.5); add a unit test that fails until populated.
- **[5.9] Issue:** Server returns 409 with `currentRevision` but UI does not surface a remote-changed badge or override modal. Conflict resolution becomes invisible to users. → **Fix:** render the badge on `<Card/>` and add the discard / overwrite modal; small UI change since the contract is in place.
- **[9.7 verification]** No `XD` / `Xiaodong Li` strings remain in `TopBar.tsx` or `SideBar.tsx` — fully replaced with `useCurrentUser()`. ✅
- **[9.9 verification]** First-run rule **is** wired despite an earlier suspicion: `MeEndpoints.cs:77-83` checks `anyExisting` + `anyPo` and the bootstrap email; assigns `product-owner` to the first user. ✅

---

### Recommendations

Prioritized by what unblocks the most.

**Blocking (release gate per spec wording):**
1. **9.13 Security review checklist** — write `docs/forge/security.md`. Cheap, blocks v0.1 ship.
2. **Phase 11 (12 items)** — agent runtime is in-scope per Q3. The biggest piece of remaining work; everything else in 10.7 (live salary, pause/resume, soul.md editor) is downstream of this. Suggest landing in this order: 11.2 (model registry) → 11.4 (cost ledger schema) → 11.1 + 11.5 (runner interface + Claude implementation) → 11.7 (SignalR streaming) → 11.6 (sandbox) → 11.9 (salary readouts) → 11.8 + 11.10 (controls) → 11.3 (soul.md editor) → 11.11 (tests) → 11.12 (docs).

**Important (visible polish gaps):**
3. **4.6** Wire the PR filter selects + search.
4. **4.9** Decide: route Re-run/Cancel through Phase 6, or label clearly as cache-only.
5. **5.9** UI surface for the conflict-policy 409 — server is already paying for it.
6. **8.5** Ship the server-driven Add-repo picker UI.
7. **10.2** Validate `DefaultProductId` / `DefaultRepoId` references.
8. **10.3 / 10.4** Defaults dropdowns + new-card / new-workstream modal pre-fill (a single sprint of UI work covers both).
9. **10.8** Populate `Supervising` once `productId` plumbing lands in 10.5.

**Minor (cleanup / verifications):**
10. **4.1** Reconcile spec ↔ prototype: drop spec to 3 cols or accept the fidelity break for a 4th.
11. **5.7** ETag headers on poll loop (Phase R candidate; performance only).
12. **7.5** OTel SDK rollout — already booked for `forge_v2.md`.
13. **9.12** Add MSAL broker fake + OAuth callback unit test + Edge-launch test.
14. **10.10** Apply disabled-with-tooltip pattern to non-PO write buttons (UX consistency).
15. **10.11** Backfill the missing pre-fill UI test + audit-supervising test once their features ship.

**Out of scope (acceptable as-is):**
- Phase R items (R.1–R.7) — design debt; spec explicitly defers.
- 5.4 (PAT entry UI), 7.6 (Azure AD JWT) — superseded by Phase 9.
- 7.9 manual tag step — operator action.

---

## Methodology notes

- Self-reported `[~]` markers in the spec almost always matched reality — the operator's own status notes are reliable.
- Two minor counting discrepancies surfaced: 9.12 says "7 tests" — code shows 6; 10.11 says "9 tests" — code shows 8. Cosmetic.
- One spec status was over-stated (10.2 — "best-effort validation" is actually no validation).
- One spec status was nearly missed by the explorer pass (9.9) — the promotion logic is in `MeEndpoints.cs`, not `ForgeAuth.cs`. Verified directly.
- Phases 0, 1, 2, 3, 6 (60 items) verified complete by directory presence + spot-checks; no individual file walk performed.
