import { useEffect, useMemo, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useTaskLive, useTaskLiveStore, type TaskTranscriptEvent, type DeliverableShape } from '@/realtime/useTaskLiveStore';
import { useToastStore } from '@/state/useToastStore';
import { useConfirmStore } from '@/state/useConfirmStore';
import {
  type FilterKey,
  FILTERS,
  filterEvents,
  buildPageWindow,
  groupByIteration,
  TranscriptLine,
  SummaryView,
} from '@/views/conversation';
import { WorktreePane } from './WorktreePane';

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
  const [filter, setFilter] = useState<FilterKey>('summary');
  const [selectedIter, setSelectedIter] = useState<number | null>(null);
  const [resuming, setResuming] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [restarting, setRestarting] = useState(false);
  const [accepting, setAccepting] = useState(false);
  const showToast = useToastStore((s) => s.show);

  const isLive = status === 'live';
  const isCompleted = status === 'completed';
  const hasIterations = rows.length > 0;
  const canResume = !isLive && !isCompleted && hasIterations;
  const busy = resuming || cancelling || restarting || accepting;

  async function handleResume() {
    setResuming(true);
    try {
      await forgeApi.resumeStoryArtifact(storyId, key);
      showToast(`Resuming ${key}.md — gen↔eval loop re-entered.`, 'success');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Resume failed', 'danger');
    } finally { setResuming(false); }
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
  const totalIters = isSummaryMode ? summaryIters.length : iterations.length;
  const activeIter = selectedIter ?? (totalIters > 0 ? totalIters - 1 : 0);
  const visibleEvents = !isSummaryMode && !isWorktreeMode && totalIters > 0
    ? iterations[activeIter]?.events ?? []
    : (!isSummaryMode && !isWorktreeMode ? filtered : []);
  const activeSummary = isSummaryMode && totalIters > 0 ? summaryIters[activeIter] ?? null : null;

  const currentIteration = rows.length > 0 ? rows[rows.length - 1].iter : 0;

  // Reverse-chronological: newest events on top within the active iteration.
  const reversedEvents = useMemo(() => [...visibleEvents].reverse(), [visibleEvents]);

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
      <nav className="live-exec__filters" role="tablist" style={{ display: 'flex', alignItems: 'center' }}>
        {FILTERS.map((f) => (
          <button
            key={f.key}
            type="button"
            role="tab"
            aria-selected={filter === f.key}
            className={`live-exec__filter${filter === f.key ? ' active' : ''}`}
            onClick={() => setFilter(f.key)}
          >
            {f.label}
          </button>
        ))}
        <span style={{ flex: 1 }} />
        <button type="button" className="doc-conv-resume-btn" disabled={!canResume || busy}
          title={isLive ? 'Running.' : isCompleted ? 'Shipped.' : !hasIterations ? 'No iterations.' : 'Resume loop.'}
          onClick={() => void handleResume()} style={{ margin: '0 2px', padding: '4px 10px' }}>
          {resuming ? '⏳…' : isLive ? (
            <span style={{ color: 'var(--sc-cyan, #5eead4)', fontWeight: 700 }}>● LIVE</span>
          ) : '↻ RESUME'}
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
      </nav>

      {/* Iteration pager — windowed page list with score chips */}
      {totalIters > 1 && (
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
            </span>
          )}
        </nav>
      )}

      {/* Body — Summary card | Worktree (commits + file changes) | transcript list */}
      <div className="live-exec__transcript" style={{ userSelect: 'text' }}>
        {isWorktreeMode ? (
          <WorktreePane workItemId={workItemId} />
        ) : isSummaryMode ? (
          <SummaryView
            deliverable={activeSummary}
            loading={loading}
            dispatched={rows.length > 0 || transcript.length > 0}
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
