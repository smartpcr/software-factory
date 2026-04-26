# Forge — Implementation Gaps (Phases 0–7)

> Audit of `src/forge/` against `docs/forge_implementation.md`, Phases 0–7 only. Phases 8–11 and Phase R are out of scope here (unimplemented by design).
>
> Audit date: **2026-04-26**. Re-run before each release cut and tick / strike-through items as they are resolved.

## How to read this

Findings are grouped by severity and tagged with the spec item number. Each entry cites the file path where the gap lives so it can be picked up directly.

- **Blocking** — spec item is marked `[x]` (claimed complete) but the feature is broken or missing entirely. Block the v0.1.0 cut.
- **Material** — spec item is marked `[~]` (partial) but the actual gap is wider than the parenthetical note suggests, OR claims `[x]` while violating its own contract.
- **Minor** — small drift, comment rot, undocumented config, or cosmetic spec / code inconsistency.

A separate **Honest partials** section lists `[~]` items where the parenthetical accurately describes what's missing — no action needed.

---

## 🔴 Blocking

### G-1 · Phase 6.12 — UI never sends `Idempotency-Key`
- **Claim**: `[x]` "Idempotency keys on every write (`Idempotency-Key` header) to make retries safe."
- **Reality**: Server middleware (`src/forge/Forge.Api/Middleware/IdempotencyMiddleware.cs:47-98`) honors the header correctly. **The UI does not generate one.** Grep of `forgeApi.ts`, `useBoardStore.ts`, `usePrsStore.ts` finds zero call sites that set the header on `POST` / `PATCH` / `DELETE`.
- **Why it matters**: Retries are not safe today; release-checklist gate "Idempotency-Key on a duplicate write returns the cached body" (`docs/forge/release.md:40`) cannot pass.
- **Fix**: generate a UUID per write in `src/forge/Forge.UI/src/api/forgeApi.ts` and attach it on every mutating fetch.

### G-2 · Phase 6.11 — Per-user PAT headers not parsed
- **Claim**: `[x]` "Permission model: per-user PAT only writes on behalf of that user; a server-wide PAT is allowed only for read-only sync."
- **Reality**: `X-Forge-Ado-Pat` / `X-Forge-GitHub-Pat` headers are documented in `docs/forge/providers.md:52-54` but never read by `AdoProvider.cs` / `GitHubProvider.cs`. All writes route through the server-wide `Forge:Ado:Pat` and `Forge:GitHub:Pat`.
- **Why it matters**: Writes do not respect per-user attribution — the audit log shows the wrong actor. Phase 6.11 contract is unenforced.
- **Fix**: parse the headers in the provider HttpClient pipeline; if absent, fall through to the server PAT only when the request is a read.

### G-3 · Phases 2.13 / 3.13 — Fidelity tests missing for `/timeline` and `/workstreams`
- **Claim**: `[x]` on both items: "screenshot diff … must be 0".
- **Reality**: `src/forge/Forge.UI.Tests/` contains `shell-fidelity.spec.ts`, `plan-fidelity.spec.ts`, `prs-fidelity.spec.ts` — but **no `timeline-fidelity.spec.ts` or `workstreams-fidelity.spec.ts`**.
- **Why it matters**: `release.md:20` requires the timeline diff to be 0 as a pre-cut gate. The gate cannot be evaluated.
- **Fix**: add the two specs, capture baselines from `docs/examples/forge-board.html` once, and commit the snapshots under `__snapshots__/`.

---

## 🟡 Material

### G-4 · Phase 5.8 — Webhook HMAC verification is silently bypassed when no secret is configured
- **Claim**: `[x]` "Webhook receiver `POST /api/webhooks/{provider}` … HMAC verification".
- **Reality**: `WebhooksEndpoints.cs:49-54` (GitHub) and `:81-86` (ADO) wrap signature checks in `if (!string.IsNullOrEmpty(secret))`. With no secret configured the endpoint accepts any request.
- **Why it matters**: Spec claims unconditional HMAC. A dev install that forgot to set a secret is wide-open to spoofed events.
- **Fix**: refuse to start the endpoint when running outside `Development` and the secret is absent; fail loud, not silent.

### G-5 · Phase 6.10 — Audit table has no `supervising` column (pre-Phase-10 blocker)
- **Claim**: `[x]` "Audit log table `forge_actions` … Surfaced read-only at `/audit`".
- **Reality**: Schema in `Forge.Api/Persistence/Entities.cs` (the `AuditRow`) lacks the `supervising` boolean that Phase 10.8 will require ("every write performed by a Product Owner against a crew or product they don't own is recorded with a `supervising = true` flag").
- **Why it matters**: today this is correct for Phase 6's narrower contract, but Phase 10 lands a feature that demands the column. Adding it after `/audit` ships is a churn cycle.
- **Fix**: add the column now (nullable in this migration, default `false`); writers can no-op it until Phase 10 starts populating.

### G-6 · Phase 3.7 — Worktree guard does not resolve symlinks
- **Claim**: `[x]` "guard against operating outside the configured root … (no `..`, no symlinks out of the repo)".
- **Reality**: `Forge.Api/Services/Git/GitWorktreeService.cs:155` uses `pathFull.StartsWith(rootWithSep)` — a string prefix check. A symlink inside the worktree pointing outside the repo passes the check.
- **Why it matters**: low real-world risk (operator has to plant the symlink themselves) but the spec's stated contract is violated.
- **Fix**: resolve to canonical path with `Path.GetFullPath` after `File.ResolveLinkTarget` (or `realpath` equivalent), then prefix-check the resolved form.

---

## 🟢 Minor

### G-7 · Phase 4.1 — Spec prose says 4 columns; code + prototype use 3
- **Claim**: `[~] 4.1` — "Four-column PR board with `pr.col` mapping (`implementation | build | review | merged`)" with parenthetical "prototype has 3 cols — kept 3 per fidelity constraint; spec ↔ prototype contradiction needs resolution".
- **Reality**: `PrBoard.tsx:15` defines `PCOLS = ['implementation', 'review', 'build']` (3 cols); the prototype matches. Code is correct (prototype is the source of truth per the hard-fidelity constraint).
- **Fix**: edit the spec prose down to 3 columns and drop the parenthetical contradiction. Code change is **not** wanted.

### G-8 · Phase 2.11 — Timeline telemetry counters not wired
- **Claim**: `[x]` "Telemetry: count timeline opens, time-on-view; structured logs `forge.timeline.*`".
- **Reality**: `src/forge/Forge.UI/src/telemetry/telemetry.ts:29` emits a generic `forge.view` log; no `forge.timeline.opens` counter, no `time-on-view` measurement.
- **Fix**: either add the two counters (cheap), or downgrade 2.11 to `[~]` with a parenthetical pointing at Phase 7.5 / `forge_v2.md`.

### G-9 · Phase 7.6 — Azure AD config keys not discoverable
- **Claim**: `[~] 7.6` parenthetical: "Azure AD JWT pipeline is config-only skeleton".
- **Reality**: `ForgeAuth.cs:37-42` defines `AzureAdOptions { TenantId, ClientId, Audience }`, but `docs/forge/setup.md:44` only documents `Forge:Auth:Mode` and `Forge:Auth:Secret`. No `forge.env.sample` exists.
- **Fix**: add the three keys to setup docs and to a sample env file (or explicitly defer to Phase 9 in setup.md, since Phase 9 will replace this anyway).

### G-10 · `ForgeHub.cs` header comment is stale
- **Reality**: comment at top of `Forge.Api/Hubs/ForgeHub.cs` says "Phase 1 emits one event"; Phases 4 (`pr.changed`) and 6 (`pr.log`, build streaming) added more. Comment rot.
- **Fix**: drop the per-phase enumeration; one short sentence on what the hub broadcasts.

---

## ✅ Honest partials (no action needed)

These items are correctly marked `[~]` with parentheticals that match reality. Listed here so the next auditor doesn't re-investigate them.

| Item | Partial scope (parenthetical) | Verified |
|------|--------------------------------|----------|
| 4.5 | PR description + build-log are server-side stubs | `PrsEndpoints.cs:77-88` |
| 4.9 | Re-run / cancel return 202, no real CI | `PrsEndpoints.cs:123-141` |
| 5.7 | Polling + jitter; no ETag/If-None-Match | `ProviderSyncService.cs:67` |
| 7.5 | `ILogger` + 50-entry client ring buffer; no OTel SDK | `telemetry.ts:8` |
| 7.6 | Single-user secret middleware ships; AAD is skeleton | `ForgeAuth.cs:37-42` |

---

## Cross-cutting unknowns

The audit was static-only. Before the v0.1.0 cut these need a live check:

- **`dotnet test src/forge/Forge.Api.Tests` green?** Not verified.
- **`npx playwright test` green?** Not verified, and missing-spec gap (G-3) means at least two suites can't be evaluated.
- **`__snapshots__/` baselines committed to git?** Assumed yes; confirm with `git ls-files src/forge/Forge.UI.Tests/**/__snapshots__/`.
- **`dev.ps1` (item 0.17) boots both servers cleanly on a fresh checkout?** Not exercised in this audit.

---

## Recommended fix order

1. **G-1 (Idempotency-Key on UI)** — small change, unblocks release gate #40.
2. **G-3 (timeline + workstreams fidelity tests)** — blocks the release-checklist screenshot gates.
3. **G-2 (per-user PAT parsing)** — either implement, or downgrade 6.11 to `[~]` with an honest parenthetical and capture the gap in `forge_v2.md`.
4. **G-4 (webhook HMAC required outside Development)** — small, removes a silent-failure mode.
5. **G-5 (`supervising` column)** — cheap migration now; avoids a Phase 10 churn cycle.
6. **G-7, G-8, G-9, G-10** — spec / docs hygiene, batch into a single PR.
7. **G-6 (symlink resolution)** — low-risk security hardening; can wait for Phase R or a security pass.

After each fix, re-run this audit and either tick the entry off or update the file path / line numbers cited above.
