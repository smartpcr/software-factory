# Agent memory persistence across gen-eval iterations

## Background

Each call to `IAgentRunner.StartAsync` opens a fresh provider session
(Claude/CLI spawns a one-shot `claude --print`, Claude/SDK posts a single
`/v1/messages` body, Copilot/SDK creates a new `CopilotSession`). The only
continuity between gen-eval iterations is the rebuilt text prompt
assembled by `CrewStoryArtifactStrategy.GeneratorDelegate`
(decision pins + prior build feedback + prior evaluator feedback + the
original brief). The agent walks into iter N+1 blind — it cannot see the
files it already opened in iter N, the greps it already ran, the dead
ends it already discarded, or the partial reasoning that was in its
context at end of iter N. Every iter re-primes the same 30–50 tool-round
discovery dance.

This document is the plan for adding cross-iter memory, covering all four
mechanisms identified in the root-cause review:

1. Per-workstream **scratch notes** (universal, works for every backend).
2. **Claude/CLI `--resume`** (provider-native session resume).
3. **Claude/SDK conversation history** (replay prior turns in `messages`).
4. **Copilot/SDK long-lived session** (reuse one `CopilotSession`).

## Pre-condition

Token-cap sloppy detection (`SloppyDetector` → `IsSloppy`) is already
disabled — the running Forge.Api binary must be rebuilt and restarted
before any of the memory work below produces observable effects.

## Design

Two-layer model:

- **Layer 1 — universal: worktree scratch notes.** Works across all four
  (backend × mode) cells. No runner changes. Always-on. The agent
  writes `.forge/iter-notes.md` at end of its turn; the next iter's
  `GeneratorDelegate` reads that file and prepends a "Notes from prior
  iteration" section into the iter prompt.
- **Layer 2 — backend-specific: native session continuity.** Opt-in per
  crew via `CrewRow.EnableAgentMemory`. Three implementations:
  Claude/CLI session resume, Claude/SDK message-history replay,
  Copilot/SDK pooled session. Copilot/CLI has no equivalent and falls
  back to Layer 1 alone.

**Memory key everywhere**: `(workItemId, agentId, role)`. A reassigned
pair (Phase 52 path, when re-enabled) starts with fresh memory.

## Locked decisions

| Decision | Value |
|---|---|
| Default state for Phases C / D / E | **Opt-out per crew** (operator decision 2026-05-13; `CrewRow.EnableAgentMemory` defaults `true`). Every supported crew gets memory; flip to `0` per row to disable. `AgentRow.AgentMemoryOverride` can also pin a single agent on/off. Phase A is always-on regardless. |
| Memory scope | **Per `(workItemId, agentId, role)`** — fresh agent gets fresh memory. |
| Compaction trigger | **300K input tokens** by default (fits the 1M-context Opus cells in use; capped to `model.ContextWindowTokens × Forge:Compaction:ContextWindowRatio` so smaller-window evaluators are also safe). The ratio defaults to `0.5` and is operator-tunable via `appsettings.json` — see `Services/CompactionOptions.cs`. |
| Evaluator opt-in | **Yes — both generator and evaluator opt in.** The evaluator's prior-iter "Still needs improvement" checkbox list lives in its own conversation history; without memory the evaluator re-scores from scratch each iter and can't verify whether the agent actually addressed each numbered item. With memory the evaluator sees its own prior critique alongside the ongoing file state — which is the whole point of opting it into Layer 2. The single `CrewRow.EnableAgentMemory` flag covers both sides; paired evaluator crews can opt in or out independently because they have their own row. |

## Phases

### Phase A — Worktree scratch notes (1 day, low risk)

Always-on. Cheapest continuity, useful even without Layer 2.

**Status — Phase A.1 shipped:** single-file scratch notes. Each iter
overwrites `.forge/iter-notes.md`, the next iter sees ONLY the most
recent iter's reflection. That works but throws away earlier iters'
decisions and dead-end records once we get past iter 2.

**Phase A.2 — amended history (revision the operator asked for
2026-05-13):** treat workstream notes as an append-style log that
accumulates across iterations so the agent sees the full reasoning
arc, not just the last iter. The agent keeps writing one file (no
extra discipline required) and Forge owns the versioning so the
agent can't accidentally erase prior iters' notes:

1. **Agent contract — unchanged from A.1.** The engineer prompt still
   instructs the agent to write `.forge/iter-notes.md` capturing
   THIS iter's reflection (files touched, decisions, dead ends,
   open questions, what's left). The agent does not need to know
   anything about the iter number or prior content; it just records
   what's new this turn. The prompt explicitly tells the agent to
   focus on what changed THIS iter — Forge stitches the history
   together.
2. **Forge archive — after each iter.** When `GeneratorDelegate`
   finishes the agent invocation, copy
   `.forge/iter-notes.md` → `.forge/notes/iter-{N}.md` (where N is
   the current iter number). The original file stays in place so a
   debugging operator can read the latest reflection at the
   well-known path; the archive is what the next iter consumes.
3. **Prompt prepend — concatenated history.** Replace
   `ReadPriorIterNotesAsync` with a version that scans
   `.forge/notes/iter-*.md`, sorts by iter descending (newest
   first), and concatenates with `### Iteration N` headers. Hard
   cap the total at 16 KB; when the cap is reached, include the
   newest M iters in full and add a one-line truncation marker
   citing how many older iters were dropped. The block is still
   prepended between decision-pins and build/eval feedback, same
   slot as A.1.
4. **Worktree exclude — same as A.1.** `.forge/` registered in
   `.git/info/exclude` covers both `iter-notes.md` and the
   `notes/iter-*.md` archive.

**Why the cap is 16 KB and not unbounded.** The history is in the
prompt every iter; without a cap, a 30-iter workstream pays for
30× the notes-bytes on every subsequent iter even if iter 1's
notes haven't been relevant since iter 5. The cap keeps the
trailing window meaningful (~last 4-6 iters at 50 lines each) and
older iters get dropped with an explicit marker so the agent
knows the trail truncated and can re-derive context from code if
needed. Phase F's compactor takes over once Layer 2 lands and
gives a smarter summarisation path.

**Why Forge owns the archive (not the agent).** Agents are
unreliable at "append, don't overwrite" — they reach for
`Write file` and clobber prior content. Putting the versioning
behind the agent (Forge does the `cp` post-invocation) makes the
history robust regardless of agent discipline.

Risk: none. Additive. No runner changes. The migration from A.1
to A.2 is also additive — workstreams currently mid-flight with
only an `iter-notes.md` and no `.forge/notes/` archive will have
their next iter's archive land at `iter-{N}.md` and the prompt
will simply show one iter of history until the archive builds up.

### Phase B — Memory persistence layer (2 days)

Storage that Layer 2 consumes. Cold-restart safe.

- `Persistence/Entities.cs`: new `AgentMemoryRow`:
  - `Id`, `WorkItemId`, `AgentId`, `Role`, `Backend`, `InvokeMode`,
    `SessionId` (nullable — CLI + Copilot/SDK), `MessagesJson`
    (nullable — Claude/SDK), `LastIter`, `ApproxInputTokens`,
    `CreatedAt`, `UpdatedAt`.
- `ForgeDbContext`: register `DbSet<AgentMemoryRow> AgentMemory` with a
  unique index on `(WorkItemId, AgentId, Role)`.
- `Phase53Migration.cs`: create-table migration.
- `Services/AgentMemoryStore.cs`: `GetAsync`, `UpsertAsync`,
  `DeleteForWorkItemAsync`. Pure DB.
- `AgentContext`: add `AgentMemoryHandle? Memory { get; init; }`
  carrying `SessionId` and `PriorTurns`.

### Phase C — Claude/CLI `--resume` (2 days, medium risk)

Continues the same Claude Code session across iters.

- `AgentRunnerDispatcher.RunClaudeCli`: accept `ctx.Memory?.SessionId`;
  when set, switch to `--resume <sid>`. Switch CLI invocation to
  `--print --output-format=stream-json --include-partial-messages` so
  we can parse the session id from the final SDK message.
- Surface the returned session id back to the caller via a new
  `AgentEventKind.SessionInfo`.
- `CrewStoryArtifactStrategy.GeneratorDelegate`: pre-call fetch
  `AgentMemoryStore.GetAsync(workItemId, genAgentId, "generator")` →
  `ctx.Memory`; post-call upsert the new session id.

Caveats:
- Claude Code CLI stores sessions under
  `~/.claude/projects/<worktree>/...`. Worktree path MUST be stable
  across iters (already true — `EnsureWorktreeAsync` is idempotent on
  the branch). If a session file is gone, fail soft: clear memory,
  start fresh, emit `task.memory.recovered`.
- Verify the CLI version supports `--resume` with `--print`; lock it
  in `AgentRunnerOptions` if needed.

### Phase D — Claude/SDK conversation history (3 days, medium-high risk)

Replays prior turns in `messages` so the API itself has context.

- `AgentRunnerDispatcher.RunClaudeSdk`: build `messages` from
  `ctx.Memory?.PriorTurns ?? []` + new `{role:"user", content:prompt}`.
  Accumulate streamed assistant text; on stream close persist
  `{role:"assistant", content:finalText}` back to memory.
- Extend prompt caching: add `cache_control: {type:"ephemeral"}` to the
  last stable user turn (second-to-last message). This is the lever
  that prevents Phase D from doubling cost.
- `ConversationTurn` record: `Role`, `Content`, `Iter`, `Tokens`.

Caveats:
- Without caching tuning each iter pays for the full history. Phase F
  (compaction) + aggressive caching are the mitigations.

### Phase E — Copilot/SDK long-lived session (3 days, high risk)

Reuse one `CopilotSession` across multiple `SendAsync` calls per
`(workstream, agent)`.

- New `Agents/CopilotSessionPool.cs`: `AcquireAsync` /
  `ReleaseAsync` / `DisposeForWorkItemAsync`. Cache keyed by
  `(workItemId, agentId, role)`.
- `AgentRunnerDispatcher.RunCopilotSdk`: replace
  `client.CreateSessionAsync` with `pool.AcquireAsync`. Skip system
  message + worktree config wiring after the first acquire.
- Inactivity watchdog redesign: today it fires on 5-min silence within
  one invocation; with a long-lived session, idle gaps between iters
  are normal. Track activity only within an active `SendAsync` window.
- Lifecycle: `StorySpawnOrchestrator.DriveOneWorkstreamImplAsync`
  calls `DisposeForWorkItemAsync` on terminal verdicts and on cancel.

Caveats:
- The Copilot SDK isn't documented for cross-task reuse. If `SendAsync`
  on a reused session throws, drop, recreate, emit
  `task.memory.recovered`. Behind a per-crew opt-in flag.

### Phase F — Compaction (2 days, low risk)

Bound the conversation length before it eats the context window or the
wallet.

- `Services/MemoryCompactor.cs`: invoked after each iter's upsert when
  `ApproxInputTokens > effectiveThreshold`. Calls a cheap model
  (Haiku) with all but the most recent 3 turns: "summarize this
  conversation as bullet points; preserve decisions, file edits, dead
  ends, open questions". Replaces summarized turns with one synthetic
  `{role:"user", content:"<summary>"}` + the most recent 3 verbatim.
- `effectiveThreshold = min(crew.CompactionTokenThreshold ?? 300_000,
  model.ContextWindowTokens × Forge:Compaction:ContextWindowRatio)`
  (ratio defaults to 0.5; tunable in `appsettings.json`).
- Telemetry: `task.memory.compacted { compactedTurns, priorTokens,
  newTokens, summaryCostUsd, effectiveThreshold }`.

### Phase G — Configuration + telemetry (threaded across phases)

- `CrewRow.EnableAgentMemory bool` (default `false` for C/D/E; A is
  universal).
- `AgentRow.AgentMemoryOverride bool?` for per-agent toggle.
- `CrewRow.CompactionTokenThreshold long?` (nullable, default 300K).
- `GET /api/workstreams/{id}/memory` → `{ sessionId, turnCount,
  approxInputTokens, lastCompactedAt }` per role.
- UI Conversation tab: small "Memory" chip — "resumed · 3 turns · 47K
  tok" when active, "fresh" when not.
- Bus events: `task.memory.resumed`, `task.memory.fresh`,
  `task.memory.compacted`, `task.memory.recovered`.

## Suggested order

| # | Phase | Days |
|---|---|---|
| 1 | A — Worktree scratch notes | 1 |
| 2 | B — Persistence layer | 2 |
| 3 | C — Claude/CLI `--resume` | 2 |
| 4 | F — Compaction (must precede D / E in production) | 2 |
| 5 | D — Claude/SDK history replay | 3 |
| 6 | E — Copilot/SDK pool | 3 |
| 7 | G — Config + telemetry | threaded |

Total ~14 focused days. Phase A is shippable in isolation for immediate
relief.

## Open questions to revisit during implementation

- Compaction quality on Haiku vs Sonnet — measure on the first few
  long-running workstreams once D lands.
- Cleanup cadence: when do we delete `AgentMemoryRow` for workstreams
  that have been complete for > N days? Add to a reaper job in
  Phase G.
