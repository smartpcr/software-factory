---
name: forge
description: Prime session context for the Forge subsystem (src/forge/Forge.Api, src/forge/Forge.UI) from docs/forge_implementation.md. Use when the user says "forge", "prime forge", "load forge context", "what phase are we on", or starts work on anything under src/forge/. Establishes phase-by-phase memory of the implementation plan, identifies completed vs in-progress vs upcoming work, and surfaces operational constraints (do-not-restart Forge.Api, attachment propagation, build/test gates, etc.) so subsequent edits land in the right phase with the right conventions.
---

# Forge — Implementation Plan Primer

Load deep session context for the **Forge** software-factory subsystem of this repo (`smartpcr/workflow`) from `docs/forge_implementation.md`. That plan is the **single source of truth** for what Forge is, which phases ship in v0.1, and which phases are done / in-flight / deferred.

## When to invoke

Run this skill at the **start of any session** that touches:

- `src/forge/Forge.Api/**` — ASP.NET Core 8 minimal-API backend, SQLite persistence, SignalR hub, agent runtime, crew economy, story spawn orchestrator, plan-doc loop, deliverable loop, sync workers.
- `src/forge/Forge.UI/**` — React + TypeScript + Vite frontend, ported byte-for-byte from `docs/examples/forge-board.html`.
- `src/forge/Forge.Api.Tests/**` or `src/forge/Forge.UI.Tests/**`.
- Operator-facing flows: stories, spawns, workstreams, pull requests, build/test gates, agent cost ledger, ADO/GitHub providers, sign-in.

Trigger phrases: *"prime forge"*, *"forge context"*, *"what phase are we on"*, *"is X done in forge"*, *"load the forge plan"*, *"forge implementation status"*, or any prompt that opens with `/forge`.

## File location

`docs/forge_implementation.md` (absolute on this machine: `E:\work\github\crp\workflow\docs\forge_implementation.md`).

Size: ~750 KB / ~6000 lines. **Do not read end-to-end in one call** — it will be truncated at the 50 KB view limit. Use the workflow below.

## Workflow

### Step 1: Extract the phase index (do not load the body)

Use the **grep** tool (not powershell) to extract the phase table-of-contents:

- pattern: `^## (Phase \d+|Phase R|Phase legend|Resolved decisions|Goals|Hard constraint|Stack|Open questions)`
- output_mode: `content`
- paths: `docs/forge_implementation.md`

That returns the full ordered list of phases (Phase 0 through Phase 56+ as of writing, plus Phase R "deferred refactor") without loading the body.

### Step 2: Read the prologue (lines 1–~250)

Use **view** with `view_range: [1, 250]` to load:

- **Goals** — 10 numbered items from the operator.
- **Hard fidelity constraint** vs `docs/examples/forge-board.html` (no deviation in layout, CSS variables, icons, seed data, or copy until Phase R).
- **Stack & layout** — `Forge.Api` on port 5180, `Forge.UI` on port 5181, namespace `Forge.Api.*`, package `@forge/ui`, CSS scope `.forge-*`, SQLite under `src/forge/Forge.Api/Persistence/`.
- **Resolved decisions (Q1–Q5)** — these are **binding** for every subsequent phase:
  - Q1 — ADO and GitHub are equal first-class providers.
  - Q2 — Worktrees live under `{repo.cloneRoot}\.worktree\ws-{workItemId}` (per work item, not per workstream); branch `ws/{workItemId}`; `.gitignore` (not `.git/info/exclude`) is auto-appended; multiple Forge products on the same `cloneRoot` are forbidden.
  - Q3 — Agents are real, supervised crew members with a `soul.md` persona and a cost ledger; each crew is pinned to a single model with denormalized prices.
  - Q4 — A `Repo` belongs to exactly one provider (ADO **or** GitHub). All sprint / iteration concepts come from the repo's provider — no local Forge sprint.
  - Q5 — Product↔Repo is strictly 1:N. A `Repo` belongs to exactly one `Product`; shared infra is modeled as its own product.

### Step 3: Compute phase status

The plan uses checkbox states defined in the "Phase legend" section:

- `[ ]` not started
- `[~]` in progress
- `[x]` completed (merged + verified — not just "code written")

Use **grep** for a fast rollup. To find every currently-active step across the plan:

- pattern: `^- \[~\]`
- output_mode: `content`
- paths: `docs/forge_implementation.md`

To count completion within a phase, run **grep** with pattern `^- \[(x| |~)\] \*\*N\.` (substitute `N` for the phase number) and tally states.

A phase is **landed** only when its **last** step is `[x]`. Any `[~]` or `[ ]` inside means the phase is still in flight.

### Step 4: Load only the phases relevant to the current task

Do **not** load all phases. Instead:

1. Identify which phase the user's task belongs to. Ask if ambiguous.
2. Find that phase's start line with **grep** for `^## Phase N —`.
3. Find the next phase's start line.
4. Use **view** with `view_range: [phase_start, next_phase_start - 1]` to load only that phase plus 1–2 adjacent phases for context.
5. If the task touches cross-cutting concerns, also load:
   - **Phase 9** — Sign-in & current user (Edge popup / WAM broker / WebView2 fallback).
   - **Phase 11** — Agent runtime & crew economy (binding spec; agents are real, `soul.md`, cost ledger).
   - **Phase 23 + 29** — Per-repo paginated delta sync (`agent-ready` auto-assign) refined by 2-hour cadence, upstream-changedate cursor, per-repo overrides, UI knob.
   - **Phase 26 + 27** — Build/quality/test gate before PR creation + build sync & failure-driven repair loop.
   - **Phase 28** — Story-driven spawn: multi-team orchestrated delivery (root spec for stories/spawns).
   - **Phase 30** — Agent Communication Protocol (ACP): cross-stream context, silo detection, parallelism guardrails.
   - **Phase 43** — Story attachments & operator accept.
   - **Phase 45** — Operator-chosen base branch on a story spawn.
   - **Phase 46** — Plan-doc layout v2: phase-as-root, two-level deps, stage-keyed execution, YAML decomposition.
   - **Phase 47** — Pair diversity for parallel deliverables.
   - **Phase 48** — Plan-doc completion gate (regression fix).
   - **Phase 51** — Per-task budget-cap auto-recovery.
   - **Phase 52** — Sloppy-iteration guard & pair reassignment (token-cap + score-plateau triggers currently disabled).
   - **Phase 53** — Sequential plan-doc tasks (one worktree + one PR per doc; canonical slug `plan-{docKey}`).
   - **Phase 54** — Per-iteration prompt-size growth metric.
   - **Phase 56** — Gate-failure recovery loop.

### Step 5: Surface the operational guardrails

These are **non-negotiable** rules from this repo's `CLAUDE.md` and `.github/copilot-instructions.md`, reflected in the plan. After loading context, explicitly remind the user (and yourself) of:

1. **Never restart Forge.Api without explicit per-restart approval.** This includes `scripts\run-forge-ui.ps1` (any flag), `dotnet run --project src/forge/Forge.Api/...`, `Stop-Process` against `Forge.Api` / `dotnet`, and any IDE launch profile. `dotnet build` is fine (file-lock copy errors are expected and harmless). Front-end Vite changes ride HMR — no restart needed. Approval is **per-restart**, not carried forward.
2. **After C# backend changes, the server must be rebuilt + restarted before the change is live** — state that, list the changes that require it, then **wait for the operator** to say "go ahead" / "approve" / equivalent.
3. **Forge.Api tests require the running server to be stopped first** (the test build copies `Forge.Api.exe` which is file-locked by the running process). Always offer to restart afterwards.
4. **Story attachments** must flow through every producer (plan-doc, workstream, QA, step-defs, review, review-fix) via `LoadFreshAttachmentsJsonAsync` + `CopyAttachmentsToWorktreeAsync` + `BuildAttachmentPromptSection`. Missing any one path silently drops operator uploads downstream.
5. **Worktree paths** live under `{repo.cloneRoot}\.worktree\ws-{workItemId}` (per work item). Sequential plan-doc worktrees use canonical slug `plan-{docKey}` (Phase 53). **`TryFindExistingWorktreeAsync` MUST skip the main worktree** — callers `Directory.Delete` its return value and on `main`-branch input would otherwise wipe `.git`.
6. **Destructive deletes** (RemoveWorktreeAsync + EnsureWorktreeAsync) must refuse `Directory.Delete` when target == cloneRoot (`IsSamePath` or `IsMainWorktreeDir`). Never restrict `TryFindExistingWorktreeAsync` for MergeAsync / RebaseBaseBranchAsync — those intentionally reuse the main worktree.
7. **Git subprocess hangs**: `RunGitQuietAsync` / `RunGitCaptureAsync` must respect `FORGE_GIT_PROMPT_POLICY` (default `FailFast` suppresses GCM prompts; `Interactive` enables them). `scripts\run-forge-ui.ps1` sets `Interactive` for desktop runs.
8. **Cost ledger**: spawn-lifecycle deletes (DeleteSpawn synthetic sweep, `PurgeResidualSpawnDataAsync`, `CascadeDeleteWorkstreamAsync`) MUST purge `agent_usage` rows alongside `agent_pair_assignment`. TaskIds are deterministic (`spawn-{safeStoryId}`, `plan-doc:{spawn.Id}:{file}`, `ws-{slug}`) so old rows leak onto new spawns otherwise.
9. **Workstream restart** wipes `agent_pair_assignment` + `agent_usage` rows AND resets `WorkItem.PairAttemptCount = 1`, `PriorPairsJson` / `LastPairAgentIdsJson` / `FailureReason = null`, `BudgetWindowStartUtc = now`.
10. **Build / quality / test gates** must pass before a workstream opens a PR (`BuildFailedException` stalls the row). UI surfaces all three gates in the Conversation `'build'` tab via `BuildPane`.
11. **Memory compactor threshold** is configurable via `Forge:Compaction:ContextWindowRatio` (default `0.5`). Never hardcode `0.3` / 30%.
12. **Sloppy triggers**: Phase 52 token-cap + score-plateau triggers are currently **disabled** in `SloppyDetector.CheckAsync` (always returns `IsSloppy=false`). Only the 5-iter cap and Phase 51 $-cap remain active.
13. **Evaluator diversity**: `PickEvaluator` gives `defaultEvalAgentId` unconditional first-dibs. Call sites must pass ONLY operator-configured `crew.DefaultEvaluatorAgentId ?? evaluatorCrew.DefaultEvaluatorAgentId` and NOT promote `evaluatorCrew?.Lead` into that slot.
14. **Worktree change feed** must anchor `branchDiff` at `merge-base origin/{spawn.BaseBranch} HEAD` (not literal `main..HEAD`) and fold uncommitted + untracked in to match `WorktreeCommitsScanner.BranchTotals`.
15. **Git excludes in worktrees** — use `.git/info/exclude` (resolved via `git rev-parse --git-dir`), NOT a tracked `.gitignore`. `RealStoryGitOps.CommitFilesAsync` uses `git add -A` which would commit any `.gitignore` change into the workstream PR diff.
16. **Skip-soul-prompt** is operator-controllable (Phase 21).

### Step 6: Establish phase memory and report

Summarize for the operator:

- **Plan frontier** — latest `## Phase N` heading in the file (the current edge of work).
- **Phases landed** — count of phases whose every step is `[x]`.
- **Phases in flight** — list of phase numbers + titles with any `[~]` or `[ ]`.
- **Resolved decisions Q1–Q5** in one line each.
- **Task-specific phase pointer** — "your task touches Phase N (title); here are the relevant adjacent phases…"
- **Operational guardrails** the operator must keep in mind for this session (subset of Step 5 relevant to the task).

Then offer to:

- Drill into a specific phase.
- Run `dotnet build` to verify baseline.
- Search the codebase for a specific symbol mentioned in a phase.

## Constraints

- **Do not** modify `docs/forge_implementation.md` unless the user explicitly asks. The plan is operator-authored.
- **Do not** load the whole file at once. It is far above the 50 KB view truncation limit; use targeted `view_range` reads scoped to the phase(s) needed.
- **Do not** restart Forge.Api as part of priming. Priming is read-only.
- **Do not** invent phase numbers, decisions, or guardrails. If the plan disagrees with this skill's summary, the **plan wins** — and after verifying, propose an update to this skill (with the user's permission).
- **Do not** treat the prototype file `docs/examples/forge-board.html` as obsolete. Phase R defers all refactor; until then, the React port matches the prototype byte-for-byte (Hard fidelity constraint).
- **Do not** confuse `Forge.Api` (this subsystem) with `WorkflowUi.Server` — they share nothing beyond conventions.
- **Do not** add markdown files for planning, notes, or tracking elsewhere. Stay inside the session workspace (`~/.copilot/session-state/<id>/`) for transient artifacts.

## Notes

- This repo's `CLAUDE.md` and `.github/copilot-instructions.md` already encode most of the guardrails in Step 5; this skill is the authoritative loader for the **phase plan** specifically.
- For agent runtime work, the binding spec is **Phase 11** — agents are real, supervised crew members with `soul.md` personas and a cost ledger, NOT labels surfaced from work-item metadata. Crews own a single model + denormalized price so historical salary lookups are stable.
- For story spawn / multi-team orchestration, the binding spec is **Phase 28** plus the follow-ups listed in Step 4.
- For sync work, the binding specs are **Phase 23** (per-repo paginated delta sync) and **Phase 29** (cadence + upstream-changedate cursor refinement).
- For agent communication, the binding spec is **Phase 30** (ACP: cross-stream context, silo detection, parallelism guardrails).
- Default ports: API `5180`, UI dev server `5181`. Auth via `IForgeProvider` abstraction with PATs in user-secrets (dev) → DPAPI / Key Vault (later).
