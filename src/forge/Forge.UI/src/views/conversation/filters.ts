// Phase 28 — shared paged-conversation filters used by both
// LiveExecutionView (work-item runs) and DocConversationPane (plan-doc
// runs from the architect crew). Both views consume the same task.{wid}
// transcript stream, so the filter / grouping / paging logic moved here.
import type { TaskTranscriptEvent } from '@/realtime/useTaskLiveStore';

/**
 * Filter keys for the paged-conversation view's tab strip.
 *
 * `worktree` (formerly `commits`) renders a different SHAPE than the other
 * filters: instead of filtering the transcript event list, the conversation
 * panes branch when this key is active and render the
 * <c>&lt;WorktreePane&gt;</c> inline. That shows the actual git history of
 * the work-item's branch (commits + per-commit file changes), which is what
 * operators want when they ask "what did the agent change?". The old
 * `commits` filter scanned for `task.branch-pushed` / `task.pr-opened`
 * events that almost never fired for the architect crew, so the tab was
 * always empty for plan tasks. Coding-task panes get the same treatment so
 * the view is consistent everywhere.
 *
 * `build` (Phase 49 follow-up) surfaces build + test gate runs alongside
 * the agent's own `cargo build` / `cargo test` / `dotnet build` /
 * `dotnet test` invocations from its tool-call stream. The dedicated
 * <c>&lt;BuildPane&gt;</c> renders each event as a status card (green
 * tick / red cross / gray skipped) with the exit code, duration, and
 * an expandable error summary so the operator can see why a workstream
 * stalled without trawling the raw transcript. Same "render-special"
 * pattern as `worktree`.
 *
 * `review` (Phase 49 follow-up) surfaces events emitted by the Review
 * crew during the PR review-comment + engineer-fix loop. Was previously
 * invisible because the orchestrator emitted those events under the
 * spawn-level transcript stream; Phase 49 re-tagged them with the
 * workstream's id so this filter can pick them up. Matches events whose
 * crewId is "crew-rev" (any reviewer) OR whose payload `phase` starts
 * with "review:" / "review-fix:" (covers fix-loop transcripts that
 * carry the engineer's crewId but belong semantically to the review
 * subtab). Hidden by the UI tab strip until the workstream actually
 * has review activity, so unstarted / pre-review workstreams don't
 * surface an empty tab.
 */
export type FilterKey = 'all' | 'generator' | 'evaluator' | 'tools' | 'worktree' | 'build' | 'review' | 'summary';

export const FILTERS: Array<{ key: FilterKey; label: string }> = [
  { key: 'all',       label: 'All' },
  { key: 'generator', label: 'Generator' },
  { key: 'evaluator', label: 'Evaluator' },
  { key: 'tools',     label: 'Tools' },
  { key: 'worktree',  label: 'Worktree' },
  { key: 'build',     label: 'Build' },
  { key: 'review',    label: 'Review' },
  { key: 'summary',   label: 'Summary' },
];

// Emission isn't uniform across the two crew strategies:
//   - CrewQueueWorker (engineer/comment-response/build-fix) emits
//     `task.tool-call` and `task.tool-result` (hyphenated).
//   - CrewStoryArtifactStrategy (architect plan + workstream docs)
//     emits `task.{AgentEventKind.ToString().ToLowerInvariant()}` which
//     produces `task.toolcall` and `task.toolresult` (no hyphen).
// Filters and renderers must accept both forms.
const TOOL_CALL_KINDS = new Set(['task.tool-call', 'task.toolcall']);
const TOOL_RESULT_KINDS = new Set(['task.tool-result', 'task.toolresult']);

// Phase 49 follow-up — Forge-emitted build/test gate events. Their
// kinds map 1:1 to the gate trip points in `CrewStoryArtifactStrategy`
// (build / quality / test, each with passed/failed/skipped variants)
// plus the per-iter detector that warns before the 3-strike abort.
const FORGE_BUILD_EVENT_KINDS = new Set<string>([
  'task.build-gate.passed',
  'task.build-gate.failed',
  'task.build-gate.skipped',
  'task.build-failed.detected',
  'task.build-failed.aborted',
  'task.test-gate.passed',
  'task.test-gate.failed',
  'task.test-gate.skipped',
]);

// Phase 49 follow-up — detect agent-issued build/test commands so the
// Build tab shows the engineer's `cargo build` / `cargo test` /
// `dotnet build` / etc invocations alongside Forge's own gate runs.
// Each entry catches both the canonical form (`cargo test`) and common
// variants (`cargo nextest run`, `cargo test --workspace`).
const AGENT_BUILD_CMD_REGEX = /\b(cargo\s+(?:build|test|check|clippy|fmt|nextest)|dotnet\s+(?:build|test|publish|run)|npm\s+(?:test|run\s+(?:build|test|lint))|pnpm\s+(?:test|run\s+(?:build|test|lint))|yarn\s+(?:test|build|lint)|go\s+(?:build|test|vet)|mvn\s+(?:test|compile|verify)|gradle(?:w)?\s+(?:build|test|check)|pytest\b|tox\b)/i;

/**
 * Phase 49 follow-up — true when an event should appear in the Build
 * tab. Either a Forge-emitted gate event, OR a tool-call / tool-result
 * whose payload references a known build/test command. Exported so the
 * tab-availability check (`availableTabs`) can hide the Build tab
 * before any build activity has happened for the workstream.
 */
export function isBuildEvent(e: TaskTranscriptEvent): boolean {
  if (FORGE_BUILD_EVENT_KINDS.has(e.kind)) return true;
  if (TOOL_CALL_KINDS.has(e.kind) || TOOL_RESULT_KINDS.has(e.kind)) {
    const p = e.payload as Record<string, unknown> | null;
    // The CrewQueueWorker shape stores the command in `text`; the
    // CrewStoryArtifactStrategy shape stores it under `toolPayload`.
    // toolName is e.g. "bash" / "shell" / "run_command" / "execute" —
    // we don't restrict by name because different agent backends use
    // different tool names and we don't want to miss any.
    const text = (p?.text as string) ?? '';
    const toolPayload = typeof p?.toolPayload === 'string'
      ? (p.toolPayload as string)
      : (p?.toolPayload != null ? JSON.stringify(p.toolPayload) : '');
    const haystack = `${text} ${toolPayload}`;
    return AGENT_BUILD_CMD_REGEX.test(haystack);
  }
  return false;
}

/**
 * Phase 49 follow-up — Forge-emitted build / test event kinds that
 * indicate a HARD failure (gate refused the PR). The tab badge uses
 * this so a stale `per-iter build (warning)` warning that the agent
 * already recovered from doesn't keep the badge red.
 *
 * <para>Phase 49 follow-up² — <c>build-failed.detected</c> is also
 * included now that per-iter passed events are emitted. The terminal
 * state of any (stage, kind) pair is the LATEST emission for that
 * stage; if the agent's most recent per-iter build broke, the badge
 * should reflect that. The next passing iter emits
 * <c>build-gate.passed (stage=per-iter)</c> which overwrites the
 * stage's state back to 'pass' and clears the badge.</para>
 */
const FORGE_BUILD_FAILURE_KINDS = new Set<string>([
  'task.build-gate.failed',
  'task.test-gate.failed',
  'task.build-failed.detected',
  'task.build-failed.aborted',
]);

/**
 * Phase 49 follow-up — count active build/test failures in the
 * transcript. Used by the conversation tab strip to render a red
 * "✗ N" badge on the Build tab so the operator sees a stalled
 * workstream's build state without clicking into the tab.
 *
 * <para>"Active" here means "the last terminal state for that gate
 * is failure". A workstream that had a build-gate.failed earlier
 * but later passed (e.g. operator hit Restart) shows 0 — we walk the
 * transcript forward and clear the failure on any subsequent passed
 * event for the same stage. The set is small (~3 stages: post-pass
 * build, post-pass-quality, post-pass test, plus per-iter aborted)
 * so an O(N) walk is fine.</para>
 */
export function countActiveBuildFailures(events: TaskTranscriptEvent[]): number {
  // Map of "stage" → most recent terminal state ('pass' | 'fail').
  // Stage is taken from payload.stage (post-pass / post-pass-quality /
  // post-pass-test) for gate events; the per-iter aborted event
  // doesn't have a stage so we use the literal kind as the key.
  const stageState = new Map<string, 'pass' | 'fail'>();
  for (const e of events) {
    if (!FORGE_BUILD_EVENT_KINDS.has(e.kind)) continue;
    const p = (e.payload as Record<string, unknown> | null) ?? {};
    const stage = (p.stage as string) ?? e.kind;
    if (FORGE_BUILD_FAILURE_KINDS.has(e.kind)) {
      stageState.set(stage, 'fail');
    } else if (e.kind === 'task.build-gate.passed' || e.kind === 'task.test-gate.passed') {
      stageState.set(stage, 'pass');
    }
    // skipped / detected (warning) don't change terminal state
  }
  let n = 0;
  for (const v of stageState.values()) if (v === 'fail') n += 1;
  return n;
}

export function filterEvents(
  events: TaskTranscriptEvent[],
  f: FilterKey,
  genCrewId?: string | null,
  evalCrewId?: string | null,
  genAgentId?: string | null,
  evalAgentId?: string | null,
): TaskTranscriptEvent[] {
  if (f === 'all') return events;
  // `worktree` is rendered specially by the calling pane (WorktreePane);
  // returning [] here prevents the transcript list from rendering anything
  // when the caller forgets to branch.
  if (f === 'worktree') return [];
  // `build` is rendered specially by BuildPane (Phase 49 follow-up).
  // The pane needs the events to render its status cards, so unlike
  // `worktree` we DO return the filtered list here — and BuildPane
  // imports `isBuildEvent` directly when it needs to compute the same
  // set without piping through this function (e.g. for tab visibility).
  if (f === 'build') return events.filter(isBuildEvent);
  if (f === 'tools')   return events.filter((e) => TOOL_CALL_KINDS.has(e.kind) || TOOL_RESULT_KINDS.has(e.kind));

  const EVAL_KINDS = new Set([
    'task.iteration.scored', 'task.iteration.passed', 'task.iteration.rejected',
    'task.iteration.iterated', 'task.iteration.stalled',
  ]);

  // Resolve effective agentId/crewId: top-level fields are often null,
  // the real values live inside payload.
  const effectiveAgent = (e: TaskTranscriptEvent): string | null | undefined => {
    const p = e.payload as Record<string, unknown> | null;
    return e.agentId || (p?.agentId as string) || null;
  };
  const effectiveCrew = (e: TaskTranscriptEvent): string | null | undefined => {
    const p = e.payload as Record<string, unknown> | null;
    return e.crewId || (p?.crewId as string) || null;
  };
  const effectivePhase = (e: TaskTranscriptEvent): string | null | undefined => {
    const p = e.payload as Record<string, unknown> | null;
    return (p?.phase as string) ?? null;
  };

  // Phase 49 follow-up — `review` matches the PR review-comment +
  // engineer-fix loop driven by StorySpawnOrchestrator.DriveReviewAsync.
  // Two kinds of events show up here:
  //   (a) `story.agent-*` from the reviewer (SG) producing comments.json.
  //   (b) `story.agent-*` from the engineer addressing each comment via
  //       ProduceReviewFixAsync (phaseTag = "review-fix:{wsId}").
  // Match by crewId (crew-rev) OR by phase prefix so both reviewer-side
  // and engineer-fix-side events surface in the same subtab — that's
  // the operator's mental model: "what happened on the PR review".
  if (f === 'review') {
    return events.filter((e) => {
      const crew = effectiveCrew(e);
      if (crew === 'crew-rev') return true;
      const phase = effectivePhase(e);
      if (phase && (phase.startsWith('review:') || phase.startsWith('review-fix:'))) return true;
      return false;
    });
  }

  if (f === 'generator') {
    return events.filter((e) => {
      if (e.kind.startsWith('task.iteration')) return true;
      const agent = effectiveAgent(e);
      const crew = effectiveCrew(e);
      if (agent === evalAgentId && agent !== genAgentId) return false;
      if (EVAL_KINDS.has(e.kind)) return false;
      // Phase 49 follow-up — review-loop events (crew-rev OR phase
      // review:/review-fix:) belong to the Review subtab, not Generator.
      // Without this they'd pollute "Generator" the moment the engineer's
      // fix-loop ran with the engineer crew id (which matches genCrewId).
      if (crew === 'crew-rev') return false;
      const phase = effectivePhase(e);
      if (phase && (phase.startsWith('review:') || phase.startsWith('review-fix:'))) return false;
      if (genCrewId && crew === genCrewId) return true;
      if (genAgentId && agent === genAgentId) return true;
      if (!crew && !agent) return true; // untagged events go to generator
      return false;
    });
  }
  if (f === 'evaluator') {
    return events.filter((e) => {
      if (e.kind.startsWith('task.iteration')) return true;
      if (EVAL_KINDS.has(e.kind)) return true;
      const agent = effectiveAgent(e);
      const crew = effectiveCrew(e);
      // Review-loop events do NOT belong to the evaluator subtab either.
      if (crew === 'crew-rev') return false;
      const phase = effectivePhase(e);
      if (phase && (phase.startsWith('review:') || phase.startsWith('review-fix:'))) return false;
      if (evalAgentId && agent === evalAgentId) return true;
      if (evalCrewId && crew === evalCrewId) return true;
      return false;
    });
  }
  return events;
}

/// Build a windowed page list: first, last, and a window around the
/// active page, with 'ellipsis' markers for gaps.  E.g. for 33 pages
/// and active=16: [0, 'ellipsis', 14, 15, 16, 17, 18, 'ellipsis', 32]
export function buildPageWindow(total: number, active: number, wing = 2): (number | 'ellipsis')[] {
  if (total <= wing * 2 + 5) {
    return Array.from({ length: total }, (_, i) => i);
  }
  const pages = new Set<number>();
  pages.add(0);
  pages.add(total - 1);
  for (let i = Math.max(0, active - wing); i <= Math.min(total - 1, active + wing); i++) {
    pages.add(i);
  }
  const sorted = [...pages].sort((a, b) => a - b);
  const result: (number | 'ellipsis')[] = [];
  for (let i = 0; i < sorted.length; i++) {
    if (i > 0 && sorted[i] - sorted[i - 1] > 1) result.push('ellipsis');
    result.push(sorted[i]);
  }
  return result;
}

export interface IterationGroup {
  label: string;
  score: number | null;
  verdict: string | null;
  events: TaskTranscriptEvent[];
}

const ITER_BOUNDARY_KINDS = new Set([
  'task.iteration.started',
  'task.iteration.scored',
  'task.iteration.passed',
  'task.iteration.rejected',
  'task.iteration.iterated',
  'task.iteration.stalled',
]);

export function groupByIteration(events: TaskTranscriptEvent[]): IterationGroup[] {
  if (events.length === 0) return [];

  const groups: IterationGroup[] = [];
  let current: IterationGroup = { label: 'Iteration 1', score: null, verdict: null, events: [] };

  for (const evt of events) {
    if (evt.kind === 'task.iteration.started') {
      const p = evt.payload as Record<string, unknown> | null;
      const iter = (p?.iter as number) ?? groups.length + 2;
      if (current.events.length > 0) groups.push(current);
      current = { label: `Iteration ${iter}`, score: null, verdict: null, events: [evt] };
      continue;
    }

    if (ITER_BOUNDARY_KINDS.has(evt.kind)) {
      const p = evt.payload as Record<string, unknown> | null;
      if (p?.score != null) current.score = p.score as number;
      if (p?.verdict != null) current.verdict = p.verdict as string;
    }

    current.events.push(evt);
  }

  if (current.events.length > 0) groups.push(current);

  // Drop early iterations that only have boundary markers (no body — happens
  // when the server pinned boundaries but the body events fell outside the
  // initial window). Always keep the last group so paging degrades gracefully.
  const hasContent = (g: IterationGroup) =>
    g.events.some((e) => !ITER_BOUNDARY_KINDS.has(e.kind));
  return groups.filter((g, i) => i === groups.length - 1 || hasContent(g));
}
