# Forge — scoring, iteration, and promotion (spec)

Locks the rubric and rules for how every crew is graded, iterated, and
promoted. Written before any code so we agree on the math.

Everything below is **proposed**. Items prefixed `OPEN —` are forks that
need your call before we implement; the rest are defaults we move with
unless you push back.

---

## 1. Schedule — "9-9-6"

Each crew runs **9:00 → 21:00 (12h), Monday–Saturday**. Sundays off.

- Times are local to a per-crew timezone (`Crew.timezone`, IANA, default
  `America/Los_Angeles`).
- "On shift" is a soft gate: outside the window the dispatcher refuses
  new work for that crew with a 409, but in-flight tasks finish.
- A new crew starts in the schedule the first Monday after creation; an
  override flag lets the team lead push work outside hours (logged).

> OPEN — should "off shift" be a hard refusal or a backlog enqueue that
> drains at 9:00? Default: hard refusal, lead override.

---

## 2. Scoring rubric — `0–100`

Every **deliverable** (one PR, one WIT-tree commit, or one plan doc) gets
a single integer score `0..100` from the evaluator agent.

Rubric dimensions (weights sum to 100):

| Dim | Weight | What's checked |
|-----|-------:|----------------|
| Correctness | 40 | Does it solve the work item? Tests pass? Acceptance criteria met? |
| Quality | 25 | Idiomatic style, readability, naming, no dead code, no TODOs. |
| Tests | 15 | New tests cover the change; negative paths included; no skipped. |
| Scope discipline | 10 | Diff matches the work item — no drive-by edits or unrelated refactors. |
| Risk / safety | 10 | No regressions, migrations safe, no leaked secrets, no fragile mocks. |

The evaluator returns a JSON blob:

```json
{ "score": 0..100, "dims": { "correctness": 0..40, "quality": 0..25, ... },
  "verdict": "pass" | "iterate" | "reject", "notes": "<≤ 800 chars>" }
```

`verdict` follows the score:
- **score ≥ 90** → `pass` → mark deliverable green, merge gates open.
- **70 ≤ score < 90** → `iterate` (see §3).
- **score < 70** → `reject` — the work item bounces back to the team
  lead with the evaluator's notes; no auto-iteration.

> OPEN — do you want a separate **judge** agent (third party) to break
> ties or audit the evaluator, or is the evaluator's score authoritative?
> Default: evaluator authoritative; judge agent only triggered if the
> generator disputes via `POST /api/deliverables/{id}/dispute`.

---

## 3. Iteration loop — generator ↔ evaluator until `≥ 90`

When the verdict is `iterate`:

1. The evaluator's notes are appended to the work-item context.
2. The same generator re-attempts with the notes as a fresh user turn
   (not a system-prompt edit).
3. Evaluator re-scores. Repeat.

Caps:
- **Hard cap: 5 iterations.** After 5 fails the deliverable is marked
  `stalled` and surfaces to the team lead with all 5 score traces.
- **Budget cap:** if the deliverable's cumulative spend exceeds
  `Crew.maxTaskBudgetUsd`, stop early with `stalled-budget`.
- **Time cap:** if the deliverable runs past 21:00, pause and resume at
  09:00 the next workday (idempotent — counter persists).

Each iteration is logged as a `deliverable-iteration` history row with
`{ iter, score, dims, verdict, gen_cost, eval_cost }`.

> OPEN — should iteration 2+ swap the evaluator to a different agent /
> different model to avoid the same agent always passing its own
> rework? Default: same evaluator, same model — the rubric is supposed
> to be objective and a swap would slow the loop. Revisit if scores
> stop being reproducible.

---

## 4. Levels & promotion

Every agent has a `level` field, integer `0..10`:

| Level | Title | Median model |
|------:|-------|--------------|
| 0 | Trainee | haiku |
| 1–3 | Engineer I–III | haiku → sonnet |
| 4–6 | Senior I–III | sonnet |
| 7–8 | Staff / Principal | opus |
| 9–10 | Distinguished / Fellow | opus + judge slot |

Promotion rule (default):

- Window: trailing **20 deliverables** as generator (or evaluator).
- Threshold: **rolling-avg score ≥ 92** AND **no `reject` in the window**.
- When the threshold is hit, level increases by `+1` and the window
  resets. Cooldown: at least 30 calendar days between promotions.
- Demotion: rolling avg `< 75` for 20 deliverables → `level − 1`. Floor
  at 0.

The promotion job runs nightly at 02:00 per timezone, writes
`agent-promoted` / `agent-demoted` rows, and emits SignalR
`agent.level.changed`.

> OPEN — do you want demotion at all, or only "no promotion"? Default:
> demotion enabled. A non-improving agent should leak budget less.

> OPEN — does **lead** count separately? E.g. only promotions ≥ L7 can
> become a crew lead. Default: yes, lead requires `level ≥ 7`.

---

## 5. Salary — tied to level

`agent.hourlyUsd` is derived (read-only):

```
hourlyUsd = base + level * stepUsd      // base = $4, step = $3
```

| Level | $/hr | $/day (12h) | $/month (26 days) |
|------:|-----:|------------:|------------------:|
| 0 |  4 |  48 | 1,248 |
| 3 | 13 | 156 | 4,056 |
| 5 | 19 | 228 | 5,928 |
| 7 | 25 | 300 | 7,800 |
| 10 | 34 | 408 | 10,608 |

This is **HR salary** — distinct from the existing `CrewSalary` rollup
which tracks **token spend** (cost of inference). The TopBar will show
both: spend (today) and HR payroll (today). Renaming the existing
`CrewSalary` to `CrewSpend` is a follow-up — out of scope here.

Pay is accrued per **on-shift hour** the agent's crew was in service,
not per task. Off-shift hours don't accrue. Sundays don't accrue.

> OPEN — do you want a per-deliverable bonus (e.g. +$2 for any
> score ≥ 95) on top of hourly? Default: no — keep it simple.

---

## 6. System-prompt preamble

Every agent's `soul.md` is rendered with a shared preamble injected
ahead of the agent-specific content. The preamble is one file —
`agents/_preamble.md` — and the runner concatenates it as
`<preamble>\n\n---\n\n<soul.md body>` before sending to the model.

Preamble content (locked unless you change it here):

```md
You are an agent in the Forge crew system. Your work is graded.

# Rubric (0–100)
- Correctness (40): solves the work item; tests pass; AC met.
- Quality (25): idiomatic, readable, no dead code or TODOs.
- Tests (15): new tests cover the change including negative paths.
- Scope discipline (10): diff matches the work item — no drive-bys.
- Risk / safety (10): no regressions, safe migrations, no secret leaks.

# Verdict
- ≥ 90 → pass.
- 70..89 → iterate (you'll get evaluator notes; address them precisely).
- < 70 → reject (the work item returns to the team lead).

# Iteration
- You get up to 5 iterations per deliverable. Use the evaluator's notes
  literally — don't re-argue the rubric.

# Promotion
- Your level (0–10) and salary are derived from your trailing-20
  rolling-average score. ≥ 92 with no rejects → promotion. < 75 → demotion.
- Optimize for honest, reproducible passes — not for clever appeals.

# Schedule
- Crew works 09:00–21:00 local, Mon–Sat. Outside hours, defer.
```

The preamble is versioned (`agents/_preamble.md` checked into git;
`agent.preambleSha` recorded on each deliverable for auditing).

---

## 7. Data model deltas

**`Agent`** (new fields):
- `level: int` (0..10, default 0)
- `levelUpdatedAt: DateTime?`
- `hourlyUsd: decimal` (computed; persisted for invariant audit)

**`Crew`** (new fields):
- `timezone: string` (IANA, default `America/Los_Angeles`)
- `scheduleEnabled: bool` (default true)

**`Deliverable`** (new entity — one row per work item × iteration):
- `id`, `workItemId`, `crewId`, `generatorAgentId`, `evaluatorAgentId`
- `iter: int` (1..5)
- `score: int` (0..100)
- `dims: jsonb`
- `verdict: 'pass' | 'iterate' | 'reject' | 'stalled' | 'stalled-budget'`
- `notes: string`
- `genCostUsd`, `evalCostUsd`
- `createdAt`

**`AgentLevelHistory`** (new entity):
- `id`, `agentId`, `oldLevel`, `newLevel`, `windowAvg`, `reason`,
  `createdAt`

---

## 8. Surfaces (UI)

Per-crew **Pair tab** stays as is.

New per-crew **Performance tab**:
- Rolling-20 score histogram for each member.
- Level + last-promotion / next-eligible-at.
- HR salary today / sprint / month (parallel to the existing spend
  meter).
- Schedule strip — green/red ribbon showing on/off shift now.

New per-deliverable **Score panel** on the work-item detail:
- Score, dims breakdown, verdict, evaluator notes.
- Iteration timeline (1..N) showing the rebound trajectory.

---

## 9. Open questions — recap

These are the only items that block implementation:

1. Off-shift work — hard refusal vs. backlog drain at 09:00? *(default: refuse)*
2. Tie-breaker / dispute path — judge agent on demand? *(default: yes, on dispute only)*
3. Iteration evaluator — same agent every iter? *(default: same)*
4. Demotion enabled vs promotion-only? *(default: demotion enabled)*
5. Lead floor — `level ≥ 7` to be a crew lead? *(default: yes)*
6. Per-deliverable bonus on top of hourly? *(default: no)*

Reply with which defaults to keep / flip and we'll start building.
