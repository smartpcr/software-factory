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
export type FilterKey = 'all' | 'generator' | 'evaluator' | 'tools' | 'worktree' | 'review' | 'summary';

export const FILTERS: Array<{ key: FilterKey; label: string }> = [
  { key: 'all',       label: 'All' },
  { key: 'generator', label: 'Generator' },
  { key: 'evaluator', label: 'Evaluator' },
  { key: 'tools',     label: 'Tools' },
  { key: 'worktree',  label: 'Worktree' },
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
