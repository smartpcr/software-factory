import { useEffect, useMemo, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useTaskLive, useTaskLiveStore, type TaskTranscriptEvent, type DeliverableShape } from '@/realtime/useTaskLiveStore';
import { useToastStore } from '@/state/useToastStore';
import { useConfirmStore } from '@/state/useConfirmStore';
import { useChromeCollapseStore } from '@/state/useChromeCollapseStore';
import {
  type FilterKey,
  FILTERS,
  filterEvents,
  buildPageWindow,
  groupByIteration,
  TranscriptLine,
  SummaryView,
  countActiveBuildFailures,
  availableTabs,
  IterKpiStrip,
  type IterKpi,
  ContextUsageBar,
  PromptView,
} from '@/views/conversation';
import { WorktreePane } from './WorktreePane';
import { BuildPane } from './BuildPane';
import { ToolsPane } from './ToolsPane';

interface Props {
  storyId: string;
  docId: string;
}

interface IterRow {
  id: number;
  iter: number;
  score: number;
  verdict: string;
  generatorAgentId: string;
  evaluatorAgentId: string;
  generatorSummary: string;
  notes: string;
  genCostUsd: number;
  evalCostUsd: number;
  judgeAgentId: string | null;
  judgeScore: number | null;
  judgeNotes: string | null;
  createdAt: string;
}

interface AgentInfo {
  agentId: string;
  name: string;
  model: string | null;
}

/**
 * Per-document conversation tab. Mirrors LiveExecutionView's center column
 * (filter tabs · iteration pager · paged transcript / summary cards) but
 * scoped to the synthetic plan-doc workItemId
 * (`plan-doc:{spawn.Id}:{filename}`). The architect crew emits the same
 * `task.{wid}` channel shape as work-item runs, so the same shared
 * filterEvents / groupByIteration / TranscriptLine / SummaryView helpers
 * drive both views.
 */
export function DocConversationPane({ storyId, docId }: Props) {
  const key = useMemo(() => (docId.startsWith('doc:') ? docId.slice(4) : docId), [docId]);
  // Persisted collapse toggle for the conversation KPI band (iter strip
  // + memory roles). Shared key with WorkstreamConversationPane so the
  // operator's preference applies regardless of pane variant.
  const kpiCollapsed = useChromeCollapseStore((s) => s.conversationKpi);
  const toggleChrome = useChromeCollapseStore((s) => s.toggle);

  // Iterations + workItemId from the per-doc deliverables endpoint. Used as
  // the source for Summary mode AND to discover the workItemId so we can
  // attach the live-task store for All / Generator / Evaluator / Tools /
  // Commits modes.
  const [rows, setRows] = useState<IterRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [, setTotalCost] = useState(0);
  const [workItemId, setWorkItemId] = useState<string | null>(null);
  const [generator, setGenerator] = useState<AgentInfo | null>(null);
  const [evaluator, setEvaluator] = useState<AgentInfo | null>(null);
  const [status, setStatus] = useState<'live' | 'iterating' | 'stalled' | 'awaiting-pr-review' | 'completed' | 'pending'>('pending');
  const [maxIterations, setMaxIterations] = useState(0);
  const [budgetSeconds, setBudgetSeconds] = useState(0);
  const [filter, setFilter] = useState<FilterKey>('summary');
  const [selectedIter, setSelectedIter] = useState<number | null>(null);
  const [resuming, setResuming] = useState(false);
  const [retrying, setRetrying] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [restarting, setRestarting] = useState(false);
  const [accepting, setAccepting] = useState(false);
  const [pinning, setPinning] = useState(false);
  const showToast = useToastStore((s) => s.show);

  const isLive = status === 'live';
  const isCompleted = status === 'completed';
  const hasIterations = rows.length > 0;
  // Phase 56 — derive the last verdict directly from deliverables so
  // the toolbar reflects the actual loop terminal state instead of
  // relying on the synthetic `status` field (which is influenced by
  // WorkItem.ExecutionStatus, often left stuck at "live" after a
  // terminal sloppy / stalled verdict because the architect path
  // doesn't clear it). Operator-reported 2026-05-13: architecture.md
  // hit verdict=sloppy at iter 10/10 with ExecutionStatus="live"
  // still on the row, so the Retry button stayed disabled even
  // though Retry is exactly what the situation needed.
  const lastVerdict = rows.length > 0 ? rows[rows.length - 1].verdict : null;
  const lastVerdictIsTerminalBad = lastVerdict === 'sloppy'
    || lastVerdict === 'stalled'
    || lastVerdict === 'stalled-budget'
    || lastVerdict === 'reject';
  const canResume = !isLive && !isCompleted && hasIterations;
  // Phase 56 -- Retry is the soft-recovery affordance for docs that
  // hit a terminal verdict (sloppy at iter-cap is the dominant case
  // for plan-docs; the orchestrator banner explicitly tells the
  // operator "bump max-iterations or restart"). Enabled whenever:
  //   1. There's at least one prior iteration, AND
  //   2. The doc isn't terminally complete, AND
  //   3. Either the run isn't live, OR the last verdict is terminal-
  //      bad (allows Retry on a row whose ExecutionStatus is stuck
  //      at "live"/"queued" but whose loop has actually halted).
  const canRetry = !isCompleted
    && hasIterations
    && (!isLive || lastVerdictIsTerminalBad);
  const busy = resuming || retrying || cancelling || restarting || accepting || pinning;

  async function handleResume() {
    setResuming(true);
    try {
      await forgeApi.resumeStoryArtifact(storyId, key);
      showToast(`Resuming ${key}.md — gen↔eval loop re-entered.`, 'success');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Resume failed', 'danger');
    } finally { setResuming(false); }
  }
  async function handleRetry() {
    const ok = await useConfirmStore.getState().ask({
      title: `Retry ${key}.md?`,
      body: 'Soft recovery — demote the last iteration verdict to "iterate" AND bump the iter cap by +5 so the architect agent gets another swing with the prior critique chain intact.',
      tone: 'info',
      confirmLabel: '↺ Retry',
      cancelLabel: 'Cancel',
      checklist: [
        `Demote last verdict (sloppy/stalled/pass → iterate) for ${key}.md`,
        'Bump iter cap by +5 (allows the loop to continue beyond the original cap)',
        'Reset doc execution status to "queued"',
        'Clear the orchestrator-halted error banner',
        'PRESERVE the doc on disk, deliverables, transcript, costs, prior chain',
        'Re-fire the architect loop (continues at iter+1 with prior critique)',
      ],
    });
    if (!ok) return;
    setRetrying(true);
    try {
      const r = await forgeApi.retryStoryArtifact(storyId, key);
      showToast(
        r.demoted
          ? `Retry queued — verdict ${r.priorVerdict} → ${r.newVerdict}, cap ${r.priorMaxIter} → ${r.newMaxIter}; continuing from iter ${r.priorIter + 1}.`
          : `Retry queued — cap ${r.priorMaxIter} → ${r.newMaxIter}; continuing from iter ${r.priorIter + 1}.`,
        'success');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Retry failed', 'danger');
    } finally { setRetrying(false); }
  }
  async function handleCancel() {
    setCancelling(true);
    try {
      const r = await forgeApi.cancelStoryArtifact(storyId, key);
      showToast(`Cancelled ${key}.md — ${r.invocationsCancelled} invocation(s) stopped.`, 'info');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Cancel failed', 'danger');
    } finally { setCancelling(false); }
  }
  async function handleRestart() {
    const ok = await useConfirmStore.getState().ask({
      title: `Restart ${key}.md?`,
      body: 'This nukes the gen↔eval state for this document and re-enters the loop from iter 1.',
      tone: 'warn',
      confirmLabel: '↻ Restart',
      cancelLabel: 'Cancel',
      checklist: [
        `Clear all iterations for ${key}.md`,
        'Clear transcript events',
        'Reset doc execution status to idle',
        'Wipe the architect workspace on disk',
        'Re-enter the gen↔eval loop at iter 1',
      ],
    });
    if (!ok) return;
    setRestarting(true);
    try {
      const r = await forgeApi.restartStoryArtifact(storyId, key);
      showToast(`Restarted ${key}.md — ${r.deliverablesCleaned} iteration(s) cleared.`, 'success');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Restart failed', 'danger');
    } finally { setRestarting(false); }
  }
  async function handleAccept() {
    const ok = await useConfirmStore.getState().ask({
      title: `Accept ${key}.md as-is?`,
      body: 'This overrides the evaluator verdict and marks the document as passed at its current score. The gen↔eval loop will stop.',
      tone: 'warn',
      confirmLabel: '✓ Accept',
      cancelLabel: 'Cancel',
    });
    if (!ok) return;
    setAccepting(true);
    try {
      const r = await forgeApi.acceptStoryArtifact(storyId, key);
      showToast(`Accepted ${key}.md (was ${r.previousVerdict} @ ${r.previousScore}).`, 'success');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Accept failed', 'danger');
    } finally { setAccepting(false); }
  }

  /**
   * Phase 50 — pin an operator decision that the agent must treat as
   * binding on every future iter. Used to break cross-doc convergence
   * loops where the agent keeps re-litigating the same decision (e.g.
   * "DefaultAction lives in sidecar — STOP flipping. tech-spec HC-3 is
   * authoritative."). The text is collected via window.prompt() — the
   * existing ConfirmDialog only renders single-line bodies and a
   * full-fledged textarea modal is out of scope for this quick win.
   * The pin scopes to this specific doc (workItemId set) so it only
   * affects this artifact, not unrelated sibling docs.
   */
  async function handlePin() {
    const text = window.prompt(
      `Pin a binding decision for ${key}.md:\n\n`
      + `The text below will be prepended to every future iter prompt as an `
      + `OPERATOR DECISION PIN that the agent must follow. Use this when the `
      + `same critique keeps appearing across iters (e.g. "DefaultAction lives `
      + `in sidecar — stop flipping. tech-spec HC-3 is authoritative.").`,
      '',
    );
    if (text === null) return; // cancelled
    const trimmed = text.trim();
    if (!trimmed) {
      showToast('Pin text is empty — nothing to pin.', 'info');
      return;
    }
    setPinning(true);
    try {
      const wid = workItemId ?? `plan-doc:spawn-${storyId.replace(/[^a-zA-Z0-9._-]+/g, '-').replace(/^-+|-+$/g, '')}:${key}.md`;
      await forgeApi.addStoryDecisionPin(storyId, trimmed, wid);
      showToast(`Pinned decision for ${key}.md. Next iter will see it.`, 'success');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Pin failed', 'danger');
    } finally { setPinning(false); }
  }

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const r = await forgeApi.getStoryArtifactIterations(storyId, key);
        if (cancelled) return;
        setRows(r.iterations);
        setTotalCost(r.totalCostUsd);
        setWorkItemId(r.workItemId);
        setGenerator(r.generator);
        setEvaluator(r.evaluator);
        setStatus(r.status);
        setMaxIterations(r.maxIterations);
        setBudgetSeconds(r.budgetSeconds ?? 0);
        setLoading(false);
      } catch {
        if (!cancelled) setLoading(false);
      }
    }
    setLoading(true);
    void load();
    const id = setInterval(() => { void load(); }, 8_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [storyId, key]);

  const liveEntry = useTaskLive(workItemId);
  const transcript = liveEntry?.transcript ?? [];
  const transcriptTotalCount = liveEntry?.transcriptTotalCount ?? 0;
  const loadingOlder = liveEntry?.loadingOlder ?? false;
  const hasOlderEvents = transcript.length < transcriptTotalCount;

  // Insight I.2 — per-iter working-notes archive
  // (`.forge/notes/iter-{N}.md`). Refetched when the iter count changes
  // so a newly-completed iter's notes appear in the Summary tab without
  // a manual reload.
  const [notesByIter, setNotesByIter] = useState<Record<number, { content: string; truncated?: boolean }>>({});
  useEffect(() => {
    if (rows.length === 0) {
      setNotesByIter({});
      return;
    }
    let cancelled = false;
    void forgeApi.fetchStoryArtifactIterNotes(storyId, key)
      .then((r) => {
        if (cancelled) return;
        const map: Record<number, { content: string; truncated?: boolean }> = {};
        for (const row of r.iters) {
          map[row.iter] = { content: row.content, truncated: row.truncated };
        }
        setNotesByIter(map);
      })
      .catch(() => { if (!cancelled) setNotesByIter({}); });
    return () => { cancelled = true; };
  }, [storyId, key, rows.length]);

  // Insight I.3 — per-iter KPI rollup. Click a chip to filter the
  // transcript / summary to that iter.
  const [iterKpis, setIterKpis] = useState<IterKpi[]>([]);
  useEffect(() => {
    if (rows.length === 0) {
      setIterKpis([]);
      return;
    }
    let cancelled = false;
    void forgeApi.fetchStoryArtifactIterKpis(storyId, key)
      .then((r) => { if (!cancelled) setIterKpis(r.iters); })
      .catch(() => { if (!cancelled) setIterKpis([]); });
    return () => { cancelled = true; };
  }, [storyId, key, rows.length]);

  // Insight I.4 — ContextUsageBar source. Empty roles → bar collapses.
  const [memoryRoles, setMemoryRoles] = useState<Parameters<typeof ContextUsageBar>[0]['roles']>({});
  useEffect(() => {
    if (rows.length === 0) {
      setMemoryRoles({});
      return;
    }
    let cancelled = false;
    void forgeApi.fetchStoryArtifactMemory(storyId, key)
      .then((r) => { if (!cancelled) setMemoryRoles(r.roles); })
      .catch(() => { if (!cancelled) setMemoryRoles({}); });
    return () => { cancelled = true; };
  }, [storyId, key, rows.length]);

  // Reset selection when filter or doc changes so the latest page is sticky.
  useEffect(() => { setSelectedIter(null); }, [filter, key]);

  const filtered = useMemo(
    () => filterEvents(transcript, filter, null, null, generator?.agentId ?? null, evaluator?.agentId ?? null),
    [transcript, filter, generator?.agentId, evaluator?.agentId],
  );
  const iterations = useMemo(() => groupByIteration(filtered), [filtered]);

  // Summary mode iteration source. Map IterRow → DeliverableShape so the
  // shared SummaryView component renders the same way live-exec does.
  const summaryIters = useMemo<DeliverableShape[]>(() => {
    return rows.map<DeliverableShape>((r) => ({
      id: r.id,
      iter: r.iter,
      score: r.score,
      verdict: r.verdict,
      generatorSummary: r.generatorSummary,
      notes: r.notes,
      dims: {},
      genCostUsd: r.genCostUsd,
      evalCostUsd: r.evalCostUsd,
      judgeAgentId: r.judgeAgentId,
      judgeScore: r.judgeScore,
      judgeNotes: r.judgeNotes,
      createdAt: r.createdAt,
    }));
  }, [rows]);

  const isSummaryMode = filter === 'summary';
  const isWorktreeMode = filter === 'worktree';
  const isBuildMode = filter === 'build';
  const isPromptMode = filter === 'prompt';
  const isToolsMode = filter === 'tools';
  const totalIters = isSummaryMode ? summaryIters.length : iterations.length;
  const activeIter = selectedIter ?? (totalIters > 0 ? totalIters - 1 : 0);
  const visibleEvents = !isSummaryMode && !isWorktreeMode && !isBuildMode && !isPromptMode && !isToolsMode && totalIters > 0
    ? iterations[activeIter]?.events ?? []
    : (!isSummaryMode && !isWorktreeMode && !isBuildMode && !isPromptMode && !isToolsMode ? filtered : []);
  const activeSummary = isSummaryMode && totalIters > 0 ? summaryIters[activeIter] ?? null : null;

  // KPI strip click jumps the iter pointer. KPI iter values are 1-based;
  // selectedIter is 0-based, so subtract one.
  const handleSelectKpiIter = (iter: number) => { setSelectedIter(iter - 1); };
  // Active iter mapping:
  //   • summary mode → activeIter indexes summaryIters (deliverables)
  //   • everything else → activeIter indexes `iterations` (groupByIteration
  //     of the filtered transcript). Using iterKpis[activeIter] here is
  //     wrong because the transcript can extend past the last persisted
  //     Deliverable row (live iter emitted task.iteration.started before
  //     a deliverable row was written), and the two arrays don't align.
  const activeIterNumber = (isSummaryMode
      ? summaryIters[activeIter]?.iter
      : iterations[activeIter]?.iter)
    ?? (iterations.length > 0 ? iterations[iterations.length - 1].iter : null)
    ?? (iterKpis.length > 0 ? iterKpis[iterKpis.length - 1].iter : null);

  // Surface the higher of deliverable-derived iter and transcript-derived
  // iter so the pager doesn't lag when the loop has emitted
  // task.iteration.started/scored past the last persisted deliverable
  // (e.g. stuck in mid-iter when a sloppy-cap fires).
  const deliverableIter = rows.length > 0 ? rows[rows.length - 1].iter : 0;
  const transcriptIter = useMemo(() => {
    let max = 0;
    for (const e of transcript) {
      if (e.kind !== 'task.iteration.started' && e.kind !== 'task.iteration.scored') continue;
      const p = (e.payload ?? {}) as { iter?: number };
      if (typeof p.iter === 'number' && p.iter > max) max = p.iter;
    }
    return max;
  }, [transcript]);
  const currentIteration = Math.max(deliverableIter, transcriptIter);

  // Reverse-chronological: newest events on top within the active iteration.
  const reversedEvents = useMemo(() => [...visibleEvents].reverse(), [visibleEvents]);

  // Phase 49 follow-up — count active build/test failures so the
  // Build tab gets a red ✗N badge even when the operator hasn't
  // opened it yet. Same shape as WorkstreamConversationPane.
  const buildFailures = useMemo(() => countActiveBuildFailures(transcript), [transcript]);

  // Insight I.1 (docs/forge/workstream-insights.md) — strip tabs that
  // have no content for this doc's current state. Plan-doc spawns
  // don't carry crewId (architect plan-doc workItemIds emit untagged
  // events), so the gen/eval columns fall back to agent-id matching.
  const visibleTabs = useMemo(
    () => availableTabs(transcript, rows.length, null, null, generator?.agentId ?? null, evaluator?.agentId ?? null),
    [transcript, rows.length, generator?.agentId, evaluator?.agentId],
  );
  const filtersToRender = useMemo(() => FILTERS.filter((f) => visibleTabs.has(f.key)), [visibleTabs]);

  useEffect(() => {
    if (!visibleTabs.has(filter) && filtersToRender.length > 0) {
      setFilter(filtersToRender[0].key);
    }
  }, [visibleTabs, filter, filtersToRender]);

  // Empty / loading guard. The pane is also reachable when the doc hasn't
  // produced any iterations yet — show the same prompt-y empty state as
  // before in that case.
  if (loading && rows.length === 0 && transcript.length === 0) {
    return <div className="tab-placeholder"><em>loading conversation…</em></div>;
  }
  if (rows.length === 0 && transcript.length === 0) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ NO ITERATIONS YET</div>
        <div className="sub">
          The architect crew hasn't iterated on this document yet. Once the
          gen↔eval loop starts, each iteration appears here with the generator's
          change summary and the evaluator's score-with-rationale.
        </div>
      </div>
    );
  }

  return (
    <div className="doc-conv doc-conv--quiet">
      {kpiCollapsed ? (
        <div className="conv-kpi-collapsed">
          <span className="conv-kpi-collapsed-label">CONVERSATION KPI</span>
          {activeIterNumber !== null && (
            <span className="conv-kpi-collapsed-iter">iter {activeIterNumber}</span>
          )}
          {iterKpis.length > 0 && (() => {
            const best = iterKpis.reduce((m, k) => (k.score ?? -1) > (m.score ?? -1) ? k : m, iterKpis[0]);
            return best.score !== null && best.score !== undefined ? (
              <span className="conv-kpi-collapsed-score">best {best.score}</span>
            ) : null;
          })()}
          <div className="conv-kpi-collapsed-spacer" />
          <button
            type="button"
            className="chrome-collapse-btn"
            title="Expand conversation KPI strip"
            aria-label="Expand conversation KPI strip"
            onClick={() => toggleChrome('conversationKpi')}
          >
            ▾
          </button>
        </div>
      ) : (
        <div className="conv-kpi-band">
          <IterKpiStrip
            iters={iterKpis}
            activeIter={activeIterNumber}
            onSelectIter={handleSelectKpiIter}
          />
          <ContextUsageBar roles={memoryRoles} />
          <button
            type="button"
            className="chrome-collapse-btn chrome-collapse-btn--abs"
            title="Collapse conversation KPI strip"
            aria-label="Collapse conversation KPI strip"
            onClick={() => toggleChrome('conversationKpi')}
          >
            ▴
          </button>
        </div>
      )}
      <nav className="live-exec__filters" role="tablist" style={{ display: 'flex', alignItems: 'center' }}>
        {filtersToRender.map((f) => {
          const buildFailCount = f.key === 'build' ? buildFailures : 0;
          return (
            <button
              key={f.key}
              type="button"
              role="tab"
              aria-selected={filter === f.key}
              className={`live-exec__filter${filter === f.key ? ' active' : ''}`}
              onClick={() => setFilter(f.key)}
            >
              {f.label}
              {buildFailCount > 0 && (
                <span
                  aria-label={`${buildFailCount} build/test failure${buildFailCount === 1 ? '' : 's'}`}
                  title={`${buildFailCount} active build/test failure${buildFailCount === 1 ? '' : 's'}`}
                  style={{
                    marginLeft: 6,
                    padding: '0 5px',
                    fontSize: 9,
                    fontWeight: 700,
                    letterSpacing: '0.04em',
                    color: '#fff',
                    background: 'var(--sc-red, #f87171)',
                    borderRadius: 8,
                    lineHeight: '14px',
                    display: 'inline-block',
                    minWidth: 14,
                    textAlign: 'center',
                  }}
                >
                  ✗{buildFailCount}
                </span>
              )}
            </button>
          );
        })}
        <span style={{ flex: 1 }} />
        <button type="button" className="doc-conv-resume-btn" disabled={!canResume || busy}
          title={isLive ? 'Running.' : isCompleted ? 'Shipped.' : !hasIterations ? 'No iterations.' : 'Resume loop.'}
          onClick={() => void handleResume()} style={{ margin: '0 2px', padding: '4px 10px' }}>
          {resuming ? '⏳…' : isLive ? (
            <span style={{ color: 'var(--sc-cyan, #5eead4)', fontWeight: 700 }}>● LIVE</span>
          ) : '↻ RESUME'}
        </button>
        <button type="button" className="doc-conv-resume-btn" disabled={!canRetry || busy}
          title={isCompleted ? 'Shipped.' : !canRetry ? 'No iterations.' : (isLive && lastVerdictIsTerminalBad ? `Loop halted (verdict=${lastVerdict}) but ExecutionStatus is stuck — soft retry will unstick it.` : 'Soft retry — demote verdict + bump iter cap +5 so the loop continues (vs Restart which wipes to iter 1).')}
          onClick={() => void handleRetry()}
          style={{ margin: '0 2px', padding: '4px 10px', borderColor: 'var(--sc-cyan, #5eead4)' }}>
          {retrying ? '⏳…' : '↺ RETRY'}
        </button>
        <button type="button" className="doc-conv-resume-btn" disabled={isCompleted || busy}
          title={isCompleted ? 'Shipped.' : 'Cancel running invocations for this doc.'}
          onClick={() => void handleCancel()}
          style={{ margin: '0 2px', padding: '4px 10px', borderColor: 'var(--sc-red, #ff6b80)' }}>
          {cancelling ? '⏳…' : '✕ CANCEL'}
        </button>
        <button type="button" className="doc-conv-resume-btn" disabled={!hasIterations || busy}
          title={!hasIterations ? 'No iterations.' : 'Accept the doc at its current score and merge to main.'}
          onClick={() => void handleAccept()}
          style={{ margin: '0 2px', padding: '4px 10px', borderColor: 'var(--sc-green, #4ade80)' }}>
          {accepting ? '⏳…' : '✓ ACCEPT'}
        </button>
        <button type="button" className="doc-conv-resume-btn" disabled={busy}
          title="Wipe iterations and restart from iter 1."
          onClick={() => void handleRestart()}
          style={{ margin: '0 2px', padding: '4px 10px', borderColor: 'var(--sc-amber, #ffb347)' }}>
          {restarting ? '⏳…' : '⟲ RESTART'}
        </button>
        <button type="button" className="doc-conv-resume-btn" disabled={busy}
          title="Pin a binding operator decision the agent must follow on every future iteration. Use when the same critique keeps recurring across iters."
          onClick={() => void handlePin()}
          style={{ margin: '0 2px', padding: '4px 10px', borderColor: 'var(--sc-cyan, #5eead4)' }}>
          {pinning ? '⏳…' : '📌 PIN'}
        </button>
      </nav>

      {/* Iteration pager — windowed page list with score chips. Hidden
          in Prompt sub-tab mode because PromptView renders its own
          pager chipped on actual persisted-prompt iters (avoids
          double-pager and outer/inner mismatch). */}
      {totalIters > 0 && !isPromptMode && (
        <nav className="live-exec__iter-pager" aria-label="Iteration pager">
          <button
            type="button"
            disabled={activeIter === 0}
            onClick={() => setSelectedIter(Math.max(0, activeIter - 1))}
            title="Previous iteration"
          >
            ◂
          </button>
          {buildPageWindow(totalIters, activeIter).map((slot, i) => {
            if (slot === 'ellipsis') return <span key={`e${i}`} className="live-exec__iter-pager-ellipsis">…</span>;
            const slotLabel  = isSummaryMode ? `Iteration ${summaryIters[slot].iter}` : iterations[slot].label;
            const slotScore  = isSummaryMode ? summaryIters[slot].score   : iterations[slot].score;
            const slotVerdict = isSummaryMode ? summaryIters[slot].verdict : iterations[slot].verdict;
            return (
              <button
                key={slot}
                type="button"
                className={slot === activeIter ? 'active' : ''}
                onClick={() => setSelectedIter(slot === totalIters - 1 ? null : slot)}
                title={slotLabel}
              >
                {slot + 1}
                {slotScore != null && (
                  <span className={`live-exec__iter-pager-score ${slotVerdict ?? ''}`}>{slotScore}</span>
                )}
              </button>
            );
          })}
          <button
            type="button"
            disabled={activeIter >= totalIters - 1}
            onClick={() => {
              const next = activeIter + 1;
              setSelectedIter(next >= totalIters - 1 ? null : next);
            }}
            title="Next iteration"
          >
            ▸
          </button>
          {maxIterations > 0 && (
            <span style={{ marginLeft: 'auto', fontFamily: 'var(--sc-font-display, monospace)', fontSize: 9, letterSpacing: '0.14em', color: 'var(--sc-text-dim, #8d94a3)' }}>
              iter <strong style={{ color: 'var(--sc-text, #e9e6dc)' }}>{currentIteration}</strong> of <strong style={{ color: 'var(--sc-text, #e9e6dc)' }}>{maxIterations}</strong>
              {budgetSeconds > 0 && (
                <span style={{ marginLeft: 8, color: 'var(--sc-text-dim, #8d94a3)' }}>
                  · timeout <strong style={{ color: 'var(--sc-text, #e9e6dc)' }}>{budgetSeconds >= 60 ? `${Math.floor(budgetSeconds / 60)}m` : `${budgetSeconds}s`}</strong>
                </span>
              )}
            </span>
          )}
        </nav>
      )}

      {/* Body — Summary card | Worktree (commits + file changes) | Build (gates + agent build/test) | Prompt (verbatim system + user prompt per role) | transcript list */}
      <div className="live-exec__transcript" style={{ userSelect: 'text' }}>
        {isWorktreeMode ? (
          <WorktreePane workItemId={workItemId} />
        ) : isBuildMode ? (
          <BuildPane workItemId={workItemId} />
        ) : isToolsMode ? (
          <ToolsPane workItemId={workItemId} />
        ) : isPromptMode ? (
          <PromptView
            events={transcript}
            activeIter={activeIterNumber}
            workItemId={workItemId ?? ''}
          />
        ) : isSummaryMode ? (
          <SummaryView
            deliverable={activeSummary}
            loading={loading}
            dispatched={rows.length > 0 || transcript.length > 0}
            notesByIter={notesByIter}
            reasoningTokens={activeSummary
              ? iterKpis.find((k) => k.iter === activeSummary.iter)?.reasoningTokens ?? null
              : null}
          />
        ) : (
          <>
            {transcriptTotalCount > 0 && (
              <div
                className="live-exec__transcript-count"
                style={{ fontSize: 11, color: 'var(--text-tertiary, #888)', padding: '4px 8px', borderBottom: '1px solid var(--border, #e5e7eb)' }}
              >
                Showing 1–{transcript.length.toLocaleString()} most recent of {transcriptTotalCount.toLocaleString()} total events
              </div>
            )}
            {reversedEvents.length === 0 ? (
              <div className="live-exec__empty">
                {!liveEntry || liveEntry.loading ? 'Loading…' : 'No events yet.'}
              </div>
            ) : (
              reversedEvents.map((evt: TaskTranscriptEvent) => (
                <TranscriptLine
                  key={`${evt.id}:${evt.at}`}
                  evt={evt}
                  generatorCrewId={null}
                  evaluatorCrewId={null}
                />
              ))
            )}
            {hasOlderEvents && workItemId && (
              <div style={{ textAlign: 'center', padding: '8px 0' }}>
                <button
                  type="button"
                  className="btn btn-secondary"
                  style={{ fontSize: 11, padding: '4px 12px' }}
                  disabled={loadingOlder}
                  onClick={() => void useTaskLiveStore.getState().loadOlderEvents(workItemId)}
                >
                  {loadingOlder ? 'Loading…' : '↑ Load older events'}
                </button>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
