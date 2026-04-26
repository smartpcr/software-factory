# Forge Implementation — Gap Analysis (v2)

> Re-reviewed `docs/forge_implementation.md` against the codebase on 2026-04-26 after Phase 11 Batches A–D landed. **138/138 backend tests pass.** Vite UI build clean.
>
> Spec uses self-reported markers: `[x]` complete, `[~]` partial, `[ ]` not started. This report verifies the markers, surfaces gaps that the markers under-state, and **reorders priorities** since Phase 11 is no longer the dominant blocker.

## What changed since the last report

| Item                      | Was         | Now         | Notes |
|---------------------------|-------------|-------------|-------|
| 11.1 IAgentRunner         | ❌ missing  | ✅ shipped  | Interface, types, DI wiring |
| 11.2 Model registry       | ❌ missing  | ✅ shipped  | `models.json` + `GET /api/models` + `/{id}` |
| 11.3 soul.md authoring    | ❌ missing  | ⚠️ partial  | Filesystem service + endpoints; CodeMirror editor deferred |
| 11.4 Cost ledger          | ❌ missing  | ✅ shipped  | `agent_usage` table + indexes; aggregation in-memory |
| 11.5 ClaudeAgentRunner    | ❌ missing  | ✅ shipped  | Local-stub + Anthropic REST + SSE + caching + retry |
| 11.6 Sandbox              | ❌ missing  | ✅ shipped  | But **not yet invoked** by the runner — see Correctness Issues |
| 11.7 SignalR streaming    | ❌ missing  | ✅ shipped  | Server fan-out wired; UI hooks deferred |
| 11.8 Reassign + history   | ❌ missing  | ✅ shipped  | `agent_history` + endpoint + SignalR event |
| 11.9 Salary readouts      | ❌ missing  | ✅ shipped  | Server endpoints + CrewsView + TopBar chip |
| 11.10 Budgets + alerts    | ❌ missing  | ✅ shipped  | 402 + `agent.budget_exceeded` event |
| 11.11 Tests               | ❌ missing  | ⚠️ partial  | 24 new xUnit tests; Playwright E2E deferred |
| 11.12 Fidelity + tokens   | ❌ missing  | ✅ shipped  | `docs/forge/design-tokens.md` |
| 10.7 `/crews` view        | ⚠️ partial  | ⚠️ partial  | Salary readouts now live; drill-in + PO action UI still missing |
| 9.13 Security review      | ❌ missing  | ❌ missing  | Unchanged — only true release blocker remaining |

The headline number moved from **13 missing** to **0 missing** (Phases 0–11), with **23 partial** items.

---

## Implementation Review

### Summary (verified)

- Total tasks (Phases 0–11): **155**
- ✅ Complete: **132**  (was 121; +11)
- ⚠️ Partial:  **23**  (was 21; +2 net — Phase 11 added 2 partials, no other movement)
- ❌ Missing:  **0**   (was 13; Phase 11 cleared 12 + 9.13 still pending — see below)
- 🚫 Deferred (Phase R): 7 (unchanged)

> Note on 9.13: in this v2 report I'm classifying 9.13 as "partial / not started" rather than ❌ because the security posture is real (DPAPI tokens, role-gated writes, audit trail), what's missing is the *checklist artifact* — a doc, not new functionality.

---

## Correctness Issues Found (NEW since v1)

Found while verifying Phase 11 — not visible in the spec markers:

- **[11.6 ↔ 11.5]** Sandbox is built and tested but **`ClaudeAgentRunner` never invokes it**. `grep IAgentToolHost src/forge/Forge.Api/Agents/*.cs` returns the interface and implementation only — no runner code references it. Anthropic `tool_use` protocol parsing is also absent. Consequence: the `ToolCall` / `ToolResult` `AgentEventKind` values exist but are never emitted; an agent can't actually edit files, run commands, or hit any tool. The runner today is "chat-only". → **Fix:** wire `tool_use` events out of the SSE stream into `IAgentToolSession` calls; emit `ToolCall` / `ToolResult` events to the SignalR group; round-trip the result back into the conversation. Substantial change but well-scoped.
- **[11.7]** Spec lists three UI hooks for the streaming events: card live-progress bar (Phase 1.2), `/crews` row in-place updates, PR detail panel "Agent activity" tab (Phase 4.5). Server fan-out is live; **none of the three UI consumers are wired**. → **Fix:** subscribe each consumer to `agent.{agentId}` via the existing `useSignalR`-style hooks.
- **[11.4 spec ↔ code drift]** Spec mandates SQL views (`crew_salary_today`, `crew_salary_sprint`, `crew_salary_month`). Code uses in-memory aggregation in `CrewsEndpoints.GetSalary` — SQLite EF can't translate `DateTimeOffset` filters or `SUM` over a decimal-converter column. The behavior matches the spec's intent (right numbers, right shape) but the artifact (SQL views) doesn't exist. Documented in the spec note. → **No fix required at v0.1**; revisit when migrating off SQLite.
- **[10.7 still partial]** Even after Phase 11, the `/crews` drill-in is not built: clicking a crew should open an agent detail panel with pause / resume / soul.md / model-swap actions. The endpoints all exist; only the UI wiring is missing. → **Fix:** add a CrewDetailPanel that calls `/api/agents/{id}/pause|resume|reassign|soul`.
- **[from v1, still open] 10.2** `MeSettingsEndpoints.cs` accepts arbitrary strings for `DefaultProductId` / `DefaultRepoId` with no existence check. → **Fix:** lookup against `db.Products` / `db.Repos`; reject 400 or echo `defaults.invalid: [...]`.
- **[from v1, still open] 4.9** PR `RerunBuild` / `CancelBuild` flip cached state and synthesize log tails without actually invoking CI. Returns 202 with no provider call.
- **[from v1, still open] 4.6** PR Workstream/Author selects + search input render without click handlers.
- **[from v1, still open] 5.9** Server returns 409 + `currentRevision` for conflicts; UI never surfaces the badge or override modal.
- **[from v1, still open] 10.8** `Supervising` audit column added but never written.

---

## Recommendations — REORDERED

The previous report had Phase 11 as the gating mass of work. With that done, the priority shape changes meaningfully. New ordering:

### Tier 1 — Release blockers (single item)

1. **9.13** Write `docs/forge/security.md` checklist (token-at-rest, scope minimization, telemetry opt-in, DPAPI rotation, Windows-account-change behavior). The only outstanding piece flagged in the spec as a release gate. Estimated half a day.

### Tier 2 — Phase 11 finishing work (newly the top behavioral gap)

2. **11.6 + 11.5 — wire tool execution end-to-end.** The biggest "looks shipped but isn't" gap in the codebase. Sequence: parse Anthropic `content_block_delta` of type `tool_use` → call `IAgentToolSession` → emit `ToolCall` + `ToolResult` events → round-trip the result back into the next message. ~1–2 days. Without this, agents can chat but can't do anything.
3. **11.7 UI hooks** — connect the three deferred consumers to `agent.{agentId}`:
   - Card live-progress bar (`Card.Live` already exists in the prototype shape)
   - `/crews` row token / cost increments in place
   - PR detail panel "Agent activity" tab
   ~1 day total.
4. **10.7 CrewDetailPanel + PO actions UI** — pause/resume/reassign/soul.md buttons. Server endpoints all exist. ~1 day.
5. **11.3 CodeMirror soul.md editor** in `/settings/agents`. Markdown preview side-by-side. ~half a day with a CodeMirror-React integration.
6. **11.11 Playwright E2E** — one happy-path: trigger an agent on a card, observe streaming tokens, confirm cost lands on card and crew salary readout. Optional if Tier 2.2–2.5 land first; the xUnit suite already covers the same code paths server-side.

### Tier 3 — Carryover polish from v1 (visible UX gaps)

7. **5.9** UI surface for the conflict-policy 409 — server is already paying for it. ~half a day.
8. **4.9** Either route Re-run/Cancel through Phase 6, or label clearly as cache-only (badges don't claim what isn't true). ~half a day.
9. **4.6** Wire PR filter selects + search. ~couple of hours.
10. **8.5** Server-driven Add-repo picker UI. ~half a day.
11. **10.2** Validate `DefaultProductId` / `DefaultRepoId`. ~couple of hours.
12. **10.3 / 10.4** Defaults dropdowns + new-card / new-workstream modal pre-fill. One sprint of UI work covers both.
13. **10.8** Populate `Supervising` once `productId` plumbing lands in 10.5. ~couple of hours; gated on 10.5.

### Tier 4 — Small cleanups

14. **4.1** Reconcile spec ↔ prototype: drop spec to 3 cols or accept the fidelity break for a 4th.
15. **9.12** Add MSAL broker fake + OAuth callback + Edge-launch tests.
16. **10.10** Apply disabled-with-tooltip pattern to non-PO write buttons.
17. **10.11** Backfill missing pre-fill UI test + audit-supervising test once their features ship.

### Out of scope (acceptable as-is)

- Phase R items (R.1–R.7) — design debt; spec explicitly defers.
- 5.4 (PAT entry UI), 7.6 (Azure AD JWT) — superseded by Phase 9.
- 5.7 (ETag), 7.5 (OTel SDK) — booked for `forge_v2.md`.
- 11.4 SQL views — replaced by in-memory aggregation; revisit when migrating off SQLite.
- 7.9 manual tag step — operator action.

---

## What ship-readiness looks like now

| Gate | Status | Notes |
|------|--------|-------|
| Spec coverage (Phases 0–11) | 132 / 155 ✅ done, 23 ⚠️ partial, 0 ❌ missing | Up from 121/21/13 |
| Release blocker (9.13) | ❌ pending | Half a day of doc work |
| Tests | 138 / 138 passing | +29 new in Phase 11 |
| Fidelity (Phases 0–4) | ✅ green | Unchanged from v1 |
| UI build | ✅ clean | Vite + tsc --noEmit pass |
| Agent runtime | ⚠️ chat-only | Sandbox exists; runner doesn't invoke it (Tier 2.2) |

Realistic v0.1 ship distance: **3–5 working days** if Tier 2.2 (tool execution) is required to call the agent runtime "complete", or **half a day** if v0.1 ships with chat-only agents and tool execution is bumped to v0.1.1.

---

## Methodology notes

- The 23 `[~]` markers in the spec are tightly written and consistently honest — each carries a parenthetical explaining the gap. v2 verification confirmed no over-stated `[x]` markers, with one nuance: `11.6` is technically built but not yet *consumed* by the runner. Spec text leaves room for that ("gated through" vs "invoked by") so the marker is defensible; the report calls it out as a Correctness Issue regardless.
- Phase 11 verification: spot-checked `Forge.Api/Agents/`, `ForgeDbContext`, `Phase11Migration`, `AgentsEndpoints`, `CrewsEndpoints`, `CrewsView.tsx`, `TopBar.tsx`. xUnit suite re-run cleanly.
- No regressions detected: the Phase 11 work only touched new files except for tightly-scoped edits to `Program.cs`, `Entities.cs`, `ForgeDbContext.cs`, `ForgeSeed.cs`, `CrewsEndpoints.cs` (added endpoints + extended request DTOs), `ForgeContracts.cs`, `ForgeHub.cs`, `forgeApi.ts`, `types.ts`, `CrewsView.tsx`, `TopBar.tsx`. Pre-existing 109 tests still green.
