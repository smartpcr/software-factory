import { useEffect, useMemo, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useTaskLive, useTaskLiveStore, type TaskTranscriptEvent } from '@/realtime/useTaskLiveStore';
import { useToastStore } from '@/state/useToastStore';
import {
  type FilterKey,
  FILTERS,
  filterEvents,
  buildPageWindow,
  groupByIteration,
  TranscriptLine,
  SummaryView,
} from '@/views/conversation';

interface Props {
  /** Workstream id (also the workItemId emitted on the task.{wid} channel by
   *  the engineer crew's gen↔eval loop). */
  workstreamId: string;
}

/**
 * Per-workstream conversation tab. Sibling of DocConversationPane — same UX
 * (filter tabs · iteration pager · paged transcript / summary cards) but
 * driven entirely by the live execution snapshot rather than the per-doc
 * iterations API. The engineer crew runs against the workstream id as
 * workItemId, so useTaskLive(workstreamId) returns:
 *   - transcript events (for All / Generator / Evaluator / Tools / Commits)
 *   - deliverables[] (one per gen↔eval iteration; drives Summary mode)
 *   - generator / evaluator agent + crew + model fields (drives the pair bar)
 */
export function WorkstreamConversationPane({ workstreamId }: Props) {
  const live = useTaskLive(workstreamId);
  const snapshot = live?.snapshot ?? null;
  const transcript = live?.transcript ?? [];
  const transcriptTotalCount = live?.transcriptTotalCount ?? 0;
  const loadingOlder = live?.loadingOlder ?? false;
  const hasOlderEvents = transcript.length < transcriptTotalCount;

  const [filter, setFilter] = useState<FilterKey>('summary');
  const [selectedIter, setSelectedIter] = useState<number | null>(null);
  const [resuming, setResuming] = useState(false);
  const [restarting, setRestarting] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const showToast = useToastStore((s) => s.show);

  useEffect(() => { setSelectedIter(null); }, [filter, workstreamId]);

  const executionStatus = snapshot?.executionStatus ?? null;
  const lastDeliverable = snapshot?.deliverables?.[snapshot.deliverables.length - 1] ?? null;
  const isComplete = executionStatus === 'completed';
  const isLive = executionStatus === 'live' || executionStatus === 'queued';
  const canResume = !isComplete && !isLive && lastDeliverable !== null;

  async function handleResume() {
    setResuming(true);
    try {
      const r = await forgeApi.resume(workstreamId);
      showToast(`Resumed (prior iter ${r.priorIter}, score ${r.priorScore}).`, 'success');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Resume failed', 'danger');
    } finally { setResuming(false); }
  }
  async function handleRestart() {
    if (!window.confirm('Restart this workstream? Clears deliverables, resets branch.')) return;
    setRestarting(true);
    try {
      const r = await forgeApi.restartWorkstream(workstreamId);
      showToast(`Restarted — ${r.deliverablesCleaned} deliverables cleared.`, 'success');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Restart failed', 'danger');
    } finally { setRestarting(false); }
  }
  async function handleCancel() {
    setCancelling(true);
    try {
      const r = await forgeApi.cancelWorkstream(workstreamId);
      showToast(`Cancelled — ${r.queueRowsCancelled} task(s) stopped.`, 'info');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Cancel failed', 'danger');
    } finally { setCancelling(false); }
  }

  const generatorAgentId = snapshot?.generatorAgentId ?? null;
  const evaluatorAgentId = snapshot?.evaluatorAgentId ?? null;
  const generatorCrewId = snapshot?.generatorCrewId ?? null;
  const evaluatorCrewId = snapshot?.evaluatorCrewId ?? null;

  const filtered = useMemo(
    () => filterEvents(transcript, filter, generatorCrewId, evaluatorCrewId, generatorAgentId, evaluatorAgentId),
    [transcript, filter, generatorCrewId, evaluatorCrewId, generatorAgentId, evaluatorAgentId],
  );
  const iterations = useMemo(() => groupByIteration(filtered), [filtered]);

  // Summary mode iteration source — straight from the snapshot's deliverables.
  const summaryIters = snapshot?.deliverables ?? [];

  const isSummaryMode = filter === 'summary';
  const totalIters = isSummaryMode ? summaryIters.length : iterations.length;
  const activeIter = selectedIter ?? (totalIters > 0 ? totalIters - 1 : 0);
  const visibleEvents = !isSummaryMode && totalIters > 0
    ? iterations[activeIter]?.events ?? []
    : (!isSummaryMode ? filtered : []);
  const activeSummary = isSummaryMode && totalIters > 0 ? summaryIters[activeIter] ?? null : null;
  const reversedEvents = useMemo(() => [...visibleEvents].reverse(), [visibleEvents]);

  const currentIteration = snapshot?.currentIteration ?? (summaryIters.length > 0 ? summaryIters[summaryIters.length - 1].iter : 0);
  const maxIterations = snapshot?.maxIterations ?? 0;

  // Empty state — workstream hasn't started, or live store is still loading.
  if ((!snapshot && live?.loading !== false) && transcript.length === 0) {
    return <div className="tab-placeholder"><em>loading conversation…</em></div>;
  }
  if (transcript.length === 0 && summaryIters.length === 0) {
    return (
      <div className="tab-placeholder">
        <div className="lbl">▌ NO ACTIVITY YET</div>
        <div className="sub">
          The engineer crew hasn't claimed this workstream yet. Once the
          gen↔eval loop starts, each iteration appears here with the
          generator's change summary and the evaluator's score-with-rationale.
        </div>
      </div>
    );
  }

  return (
    <div className="doc-conv">
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
        <button type="button" className="doc-conv-resume-btn" disabled={!canResume || resuming || cancelling || restarting}
          title={isComplete ? 'Completed.' : isLive ? 'Running.' : !canResume ? 'No iterations yet.' : 'Resume loop.'}
          onClick={() => void handleResume()} style={{ margin: '0 2px', padding: '4px 10px' }}>
          {resuming ? '⏳…' : isLive ? (
            <span style={{ color: 'var(--sc-cyan, #5eead4)', fontWeight: 700 }}>● LIVE</span>
          ) : '↻ RESUME'}
        </button>
        <button type="button" className="doc-conv-resume-btn" disabled={isComplete || cancelling || restarting || resuming}
          title="Cancel queued tasks." onClick={() => void handleCancel()}
          style={{ margin: '0 2px', padding: '4px 10px', borderColor: 'var(--sc-red, #ff6b80)' }}>
          {cancelling ? '⏳…' : '✕ CANCEL'}
        </button>
        <button type="button" className="doc-conv-resume-btn" disabled={cancelling || restarting || resuming}
          title="Restart from iter 1." onClick={() => void handleRestart()}
          style={{ margin: '0 2px', padding: '4px 10px', borderColor: 'var(--sc-amber, #ffb347)' }}>
          {restarting ? '⏳…' : '⟲ RESTART'}
        </button>
      </nav>

      {totalIters > 1 && (
        <nav className="live-exec__iter-pager" aria-label="Iteration pager">
          <button
            type="button"
            disabled={activeIter === 0}
            onClick={() => setSelectedIter(Math.max(0, activeIter - 1))}
            title="Previous iteration"
          >◂</button>
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
          >▸</button>
          {maxIterations > 0 && (
            <span style={{ marginLeft: 'auto', fontFamily: 'var(--sc-font-display, monospace)', fontSize: 9, letterSpacing: '0.14em', color: 'var(--sc-text-dim, #8d94a3)' }}>
              iter <strong style={{ color: 'var(--sc-text, #e9e6dc)' }}>{currentIteration}</strong> of <strong style={{ color: 'var(--sc-text, #e9e6dc)' }}>{maxIterations}</strong>
            </span>
          )}
        </nav>
      )}

      <div className="live-exec__transcript" style={{ userSelect: 'text' }}>
        {isSummaryMode ? (
          <SummaryView
            deliverable={activeSummary}
            loading={live?.loading === true}
            dispatched={transcript.length > 0 || summaryIters.length > 0}
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
                {live?.loading ? 'Loading…' : 'No events match this filter.'}
              </div>
            ) : (
              reversedEvents.map((evt: TaskTranscriptEvent) => (
                <TranscriptLine
                  key={`${evt.id}:${evt.at}`}
                  evt={evt}
                  generatorCrewId={generatorCrewId}
                  evaluatorCrewId={evaluatorCrewId}
                />
              ))
            )}
            {hasOlderEvents && (
              <div style={{ textAlign: 'center', padding: '8px 0' }}>
                <button
                  type="button"
                  className="btn btn-secondary"
                  style={{ fontSize: 11, padding: '4px 12px' }}
                  disabled={loadingOlder}
                  onClick={() => void useTaskLiveStore.getState().loadOlderEvents(workstreamId)}
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
