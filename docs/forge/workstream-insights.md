# Workstream-execution insights

## Goal

Make agent performance legible at a glance. Today the Conversation view
shows raw transcript events plus a Summary tab that only renders the
generator/evaluator markdown; an operator has to scroll text to know
whether iter 4 was better than iter 3, what the prompt looked like,
whether the agent burned tokens reading vs writing, and which tabs
have content worth opening. The fixes below land three improvements:

1. **Per-iter KPI strip + sparkline** above the tab nav: score, total
   tokens, cost USD, file changes, wall-clock duration. Sparkline for
   each KPI across iters. Click an iter chip → filter the view to that
   iter.
2. **Iteration scratch notes in the Summary tab** — the agent's own
   `.forge/notes/iter-{N}.md` (Phase A.2 archive) rendered alongside
   the generator / evaluator markdown sections.
3. **Hide empty tabs.** Tabs surface in the strip only when the
   underlying event/data exists (Build only when build events fired;
   Review only when review/review-fix events fired; Worktree only
   when file changes landed; new Prompt tab only when `task.prompt`
   events exist). Reduces "tab graveyard" noise and signals workstream
   progress structurally.

## Design

### Insight A — Per-iter KPI strip + sparkline

A new compact header row above `live-exec__filters`. Each iter renders
one chip:

```
┌────────────────────────────────────────────────────────────────────────┐
│  iter 1   iter 2   iter 3   iter 4 (active)        cumulative          │
│  ┌────┐   ┌────┐   ┌────┐   ┌────┐                  ┌──────────┐      │
│  │ 68 │   │ 75 │   │ 82 │   │ 83 │   score          │ ▁▃▅▆▇    │      │
│  │ 87K│   │ 142K│  │ 198K│  │ 224K│   in-tok        │ ─────▆── │      │
│  │$0.4│   │$1.2│   │$2.6│   │$3.1│   cost           │ ▁▃▅▆     │      │
│  │ 5f │   │ 8f │   │ 11f│   │ 12f│   files changed  │ ▁▃▅▆     │      │
│  │ 47s│   │ 1m12│  │ 2m4│   │ 1m48│  duration       │ ▁▅█▆     │      │
│  └────┘   └────┘   └────┘   └────┘                  └──────────┘      │
└────────────────────────────────────────────────────────────────────────┘
```

- **Click** an iter chip → `setSelectedIter(N - 1)` so the transcript
  list and Summary view jump to that iter. Active iter highlighted.
- **Sparkline column** to the right of the chip strip — one mini-line
  per KPI (score, tokens, cost, files, duration). Inline SVG, no
  charting library; ~80 px wide each. Useful when the chip strip is
  truncated past iter ~10.
- **Source of truth:**
  - `score` → `DeliverableRow.Score` (snapshot.deliverables[i].score).
  - `inputTokens` / `outputTokens` → sum of `agent_usage` rows whose
    `taskId == workstreamId` AND `startedAt` between iter-start and
    iter-end. New backend helper `GET /api/workstreams/{id}/iter-kpis`.
  - `costUsd` → same query, `SUM(CostUsd)` partitioned by iter.
  - `filesChanged` → walk transcript for `task.worktree-changed`
    events bracketed by iter boundaries (`task.iteration.started`
    through `task.iteration.scored`). Cheap client-side compute; no
    new endpoint needed.
  - `duration` → `iteration.scored.timestamp - iteration.started.timestamp`.
- **Component**: new `IterKpiStrip` in
  `src/forge/Forge.UI/src/views/conversation/IterKpiStrip.tsx`.
  Renders the chip array + sparkline column. Pulls live data via the
  existing `useTaskLive` hook plus the new `useIterKpis` query for the
  agent-usage / cost roll-up. Falls back to client-derived numbers
  when the endpoint is missing (e.g. during transition / older
  Forge.Api versions).

### Insight A.b — Context-usage indicator

Adjacent to the KPI strip, a small horizontal bar showing
`approxInputTokens / effectiveThreshold` for the active iter's
memory row. Pulled from `GET /api/workstreams/{id}/memory` (Phase G
endpoint, already shipped). Green under 70%, amber 70-90%, red ≥90%.
Tooltip: "224K / 300K — compactor fires at threshold; next iter
will trigger a fold."

When memory is off for the crew, hide this bar entirely (no signal
to show).

### Insight A.c — Prompt visibility per iter

Add a new tab **Prompt** that surfaces the iter's user prompt + system
prompt verbatim. The data is already emitted by `GeneratorDelegate`
as `task.prompt` events. New tab renderer reads the active iter's
matching event and shows:

```
[ generator ]                                          model: claude-opus-4-7
─────────────────────────────────────────────────────────────────────────
system prompt (soul.md)  [▾ expand]
─────────────────────────────────────────────────────────────────────────
user prompt                                                       8,432 chars
> [decision pins]
> [notes from prior iterations]
> [build feedback]
> [evaluator feedback]
> [original brief]
─────────────────────────────────────────────────────────────────────────
[ evaluator ]                                          model: gpt-5.5
... same shape ...
```

Operator can verify what context the agent actually saw, which is the
core "is the prompt growing in a way that explains the cost?" question.

### Insight B — Iteration scratch notes in Summary tab

Extend `SummaryView` to render a third section between Generator and
Evaluator:

```
┌─ Generator summary ────────────────────────────────────────────────┐
│  (markdown)                                                        │
└────────────────────────────────────────────────────────────────────┘
┌─ Working notes (iter N)  ──────────────────────────────────────────┐
│  (contents of .forge/notes/iter-N.md)                              │
└────────────────────────────────────────────────────────────────────┘
┌─ Evaluator summary ────────────────────────────────────────────────┐
│  (markdown)                                                        │
└────────────────────────────────────────────────────────────────────┘
```

Data source: new backend endpoint
`GET /api/workstreams/{id}/iter-notes` →
`{ iters: [{ iter, content }] }`. Reads `.forge/notes/iter-*.md` from
the workstream's worktree, sorts ascending, caps content at ~16 KB
per iter. Endpoint mirrors the existing memory endpoint pattern in
`WorkstreamsEndpoints` — no auth gate, read-only.

`SummaryView` accepts an optional `notesByIter: Record<number, string>`
prop. When the iter's note exists, render the section; otherwise
omit it cleanly (no "no notes" placeholder — the section just isn't
there).

### Insight C — Hide empty tabs

A new helper `availableTabs(transcript, deliverables, hasMemory)` in
`views/conversation/filters.ts` that returns the set of `FilterKey`
values with content. Both `WorkstreamConversationPane` and
`DocConversationPane` filter `FILTERS` against this set before
rendering the tab strip.

Per-tab rules:

| Tab | Show when |
|---|---|
| `all` | always (catch-all) |
| `summary` | `deliverables.length > 0` |
| `generator` | any event whose effective agentId matches generator |
| `evaluator` | any iteration-scored / iteration-iterated event OR any event matching the evaluator's agent/crew |
| `tools` | any `task.tool-call` / `task.toolcall` / `task.tool-result` / `task.toolresult` |
| `worktree` | any file-change event (`task.worktree-changed` or git events) |
| `build` | `isBuildEvent(e)` returns true for any event (already exists) |
| `review` | event's phase starts `review:` / `review-fix:` OR effective crew is `crew-rev` |
| `prompt` *(new)* | any `task.prompt` event |

Tabs are computed once per render via `useMemo` on `transcript` +
`deliverables`. The default `filter` state defaults to `'summary'`
when present, else falls through to `'all'`.

Edge case: when the active filter becomes hidden (e.g. operator was
on Build, then Restart cleared the transcript), reset to the first
available tab via an effect.

## Backend additions

| Endpoint | Returns | Source |
|---|---|---|
| `GET /api/workstreams/{id}/iter-kpis` | `{ iters: [{ iter, score, inputTokens, outputTokens, costUsd, durationMs, filesChanged }] }` | Joins `agent_usage`, `deliverables`, `task_transcript_event` (for file changes) |
| `GET /api/workstreams/{id}/iter-notes` | `{ iters: [{ iter, content }] }` | Reads `.forge/notes/iter-*.md` from the workstream's worktree, capped per iter |

Both endpoints follow the `GET /api/workstreams/{id}/memory` pattern
shipped in Phase G — no auth, read-only, return 404 when workstream
absent.

## Frontend additions

- `src/forge/Forge.UI/src/views/conversation/IterKpiStrip.tsx` —
  iter-chip strip + KPI sparklines.
- `src/forge/Forge.UI/src/views/conversation/IterSparkline.tsx` —
  reusable inline-SVG sparkline (1×80 px). Accepts `values: number[]`
  + optional `format(v: number): string` for axis labels.
- `src/forge/Forge.UI/src/views/conversation/PromptView.tsx` — renders
  the active iter's `task.prompt` events for both roles.
- `src/forge/Forge.UI/src/views/conversation/ContextUsageBar.tsx` —
  green/amber/red horizontal bar with tokens-used / cap label.
- `SummaryView` extended with `notesByIter` prop + new section.
- `filters.ts` gains `availableTabs(...)` + `'prompt'` added to
  `FilterKey`.
- `WorkstreamConversationPane` and `DocConversationPane` thread the
  new endpoints / props through.

## Phases (suggested order)

| # | Phase | Days | Outcome |
|---|---|---|---|
| 1 | **I.1** — `availableTabs` + hide empty | 0.5 | Tab strip cleans up immediately; no backend work. |
| 2 | **I.2** — iter-notes endpoint + SummaryView extension | 1 | Operator sees agent's own reflection alongside the generator/evaluator markdown. |
| 3 | **I.3** — iter-kpis endpoint + `IterKpiStrip` + `IterSparkline` | 2 | Per-iter chip strip with score/tokens/cost/files/duration + sparklines. |
| 4 | **I.4** — `ContextUsageBar` + `PromptView` (new Prompt tab) | 1 | Memory-on workstreams show context-headroom bar; operator can audit per-iter prompt verbatim. |

Total ~4.5 days focused. **I.1** ships in isolation as a quick UX win
(no backend). **I.2** depends on the Phase A.2 archive directory
landing — already live.

## Out of scope (deferred)

- Per-agent rollup view (sparklines across multiple workstreams the
  same agent worked on). Phase G's agent scorecard surfaces some of
  this; a per-agent insight view is a separate task.
- Diff-aware file changes (line counts, not just file counts).
  `filesChanged` here is a count from `git status --porcelain`; line
  counts need a follow-up via `git diff --shortstat`.
- Real-time live update of the KPI strip during a streaming iter.
  v1 updates only on `task.iteration.scored` (end of iter); a
  rolling running-totals chip is `forge_v2`.
