import { useEffect, useMemo, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useTaskLive, useTaskLiveStore, type TaskTranscriptEvent } from '@/realtime/useTaskLiveStore';
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
  countActiveBuildFailures,
  availableTabs,
  IterKpiStrip,
  type IterKpi,
  ContextUsageBar,
  PromptView,
} from '@/views/conversation';
import { WorktreePane } from './WorktreePane';
import { BuildPane } from './BuildPane';

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
    const ok = await useConfirmStore.getState().ask({
      title: 'Restart workstream?',
      body: 'This nukes the gen↔eval state for this workstream and re-enters the loop from iter 1.',
      tone: 'warn',
      confirmLabel: '↻ Restart',
      cancelLabel: 'Cancel',
      checklist: [
        'Clear all deliverables for this workstream',
        'Clear transcript events',
        'Reset workstream status to "active"',
        'Reset PRs / commits / ahead-behind counters',
        'Wipe the impl workspace on disk',
        'Reset the branch worktree to origin/main',
        'Re-drive the orchestrator (next tick re-enters gen↔eval at iter 1)',
      ],
    });
    if (!ok) return;
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
  const isWorktreeMode = filter === 'worktree';
  const isBuildMode = filter === 'build';
  const isPromptMode = filter === 'prompt';
  const totalIters = isSummaryMode ? summaryIters.length : iterations.length;
  const activeIter = selectedIter ?? (totalIters > 0 ? totalIters - 1 : 0);
  const visibleEvents = !isSummaryMode && !isWorktreeMode && !isBuildMode && !isPromptMode && totalIters > 0
    ? iterations[activeIter]?.events ?? []
    : (!isSummaryMode && !isWorktreeMode && !isBuildMode && !isPromptMode ? filtered : []);
  const activeSummary = isSummaryMode && totalIters > 0 ? summaryIters[activeIter] ?? null : null;
  const reversedEvents = useMemo(() => [...visibleEvents].reverse(), [visibleEvents]);

  // Phase 49 follow-up — count active build/test failures so the
  // Build tab can render a red ✗N badge. Recomputed from the full
  // transcript (not the filtered slice) so failures stay visible even
  // when the operator has another filter active.
  const buildFailures = useMemo(() => countActiveBuildFailures(transcript), [transcript]);

  // Insight I.1 (docs/forge/workstream-insights.md) — only render a
  // tab when its underlying filter has at least one matching event
  // (or, for `summary`, at least one deliverable). `all` is always
  // available so the operator never lands on an empty strip.
  const visibleTabs = useMemo(
    () => availableTabs(transcript, summaryIters.length, generatorCrewId, evaluatorCrewId, generatorAgentId, evaluatorAgentId),
    [transcript, summaryIters.length, generatorCrewId, evaluatorCrewId, generatorAgentId, evaluatorAgentId],
  );
  const filtersToRender = useMemo(() => FILTERS.filter((f) => visibleTabs.has(f.key)), [visibleTabs]);

  // When the active filter becomes hidden (e.g. operator was on Build,
  // then Restart cleared the transcript) fall back to the first
  // available tab so the view doesn't render blank.
  useEffect(() => {
    if (!visibleTabs.has(filter) && filtersToRender.length > 0) {
      setFilter(filtersToRender[0].key);
    }
  }, [visibleTabs, filter, filtersToRender]);

  // Insight I.2 — fetch the agent's per-iter working notes archive
  // (`.forge/notes/iter-*.md`). Refreshed whenever the iter count
  // changes so a newly-completed iter's notes appear in the Summary
  // tab without a manual reload. Best-effort: a 404 / fetch failure
  // just leaves the section absent.
  const [notesByIter, setNotesByIter] = useState<Record<number, { content: string; truncated?: boolean }>>({});
  useEffect(() => {
    if (summaryIters.length === 0) {
      setNotesByIter({});
      return;
    }
    let cancelled = false;
    void forgeApi.fetchWorkstreamIterNotes(workstreamId)
      .then((r) => {
        if (cancelled) return;
        const map: Record<number, { content: string; truncated?: boolean }> = {};
        for (const row of r.iters) {
          map[row.iter] = { content: row.content, truncated: row.truncated };
        }
        setNotesByIter(map);
      })
      .catch(() => {
        if (!cancelled) setNotesByIter({});
      });
    return () => { cancelled = true; };
  }, [workstreamId, summaryIters.length]);

  // Insight I.3 — per-iter KPI rollup (score / tokens / cost / files
  // / duration). Refetched when iter count changes so every newly-
  // completed iter populates a chip; rendered as a strip above the
  // tab nav. Click a chip → setSelectedIter scrolls the active iter.
  const [iterKpis, setIterKpis] = useState<IterKpi[]>([]);
  useEffect(() => {
    if (summaryIters.length === 0) {
      setIterKpis([]);
      return;
    }
    let cancelled = false;
    void forgeApi.fetchWorkstreamIterKpis(workstreamId)
      .then((r) => {
        if (cancelled) return;
        setIterKpis(r.iters);
      })
      .catch(() => {
        if (!cancelled) setIterKpis([]);
      });
    return () => { cancelled = true; };
  }, [workstreamId, summaryIters.length]);

  // Insight I.4 — context-usage bar source. Same refresh trigger as
  // iter-kpis so an operator who watches the loop sees the bar tick
  // up alongside the chips. Empty roles → ContextUsageBar returns
  // null and the row collapses.
  const [memoryRoles, setMemoryRoles] = useState<Parameters<typeof ContextUsageBar>[0]['roles']>({});
  useEffect(() => {
    if (summaryIters.length === 0) {
      setMemoryRoles({});
      return;
    }
    let cancelled = false;
    void forgeApi.fetchWorkstreamMemory(workstreamId)
      .then((r) => {
        if (cancelled) return;
        setMemoryRoles(r.roles);
      })
      .catch(() => {
        if (!cancelled) setMemoryRoles({});
      });
    return () => { cancelled = true; };
  }, [workstreamId, summaryIters.length]);

  // When the KPI strip is clicked, jump the active iter pointer to
  // the chosen iter. selectedIter is 0-indexed; iter chip values are
  // 1-indexed, so subtract one. Falls through cleanly to the
  // existing pager / Summary logic.
  const handleSelectKpiIter = (iter: number) => {
    setSelectedIter(iter - 1);
  };
  const activeIterNumber = (isSummaryMode ? summaryIters[activeIter]?.iter : iterKpis[activeIter]?.iter)
    ?? (iterKpis.length > 0 ? iterKpis[iterKpis.length - 1].iter : null);

  const currentIteration = snapshot?.currentIteration ?? (summaryIters.length > 0 ? summaryIters[summaryIters.length - 1].iter : 0);
  const maxIterations = snapshot?.maxIterations ?? 0;
  const budgetSeconds = snapshot?.budgetSeconds ?? 0;

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

  const verdictLooksStalled = lastDeliverable !== null
    && (lastDeliverable.verdict === 'stalled' || lastDeliverable.verdict === 'reject');
  const stalledNotes = lastDeliverable?.notes?.trim() ?? '';

  return (
    <div className="doc-conv">
      {verdictLooksStalled && !isLive && (
        <div
          role="alert"
          style={{
            border: '1px solid var(--sc-red, #ff6b80)',
            background: 'rgba(255,107,128,0.06)',
            color: 'var(--sc-red, #ff6b80)',
            padding: '6px 10px',
            fontSize: 11,
            fontFamily: 'var(--sc-font-mono, monospace)',
            marginBottom: 8,
            borderRadius: 3,
            lineHeight: 1.5,
          }}
        >
          <strong style={{ letterSpacing: '0.12em' }}>⚠ STALLED</strong>
          {' — iter '}<strong>{lastDeliverable!.iter}</strong>
          {maxIterations > 0 ? ` of ${maxIterations}` : ''}
          {' reached without pass · last score '}<strong>{lastDeliverable!.score}</strong>
          {stalledNotes && (
            <div
              style={{
                marginTop: 4,
                fontSize: 10.5,
                color: 'var(--sc-text-dim, #8d94a3)',
                whiteSpace: 'pre-wrap',
              }}
            >
              {stalledNotes.length > 280 ? stalledNotes.slice(0, 280) + '…' : stalledNotes}
            </div>
          )}
        </div>
      )}
      {iterKpis.length > 0 && (
        <IterKpiStrip
          iters={iterKpis}
          activeIter={activeIterNumber}
          onSelectIter={handleSelectKpiIter}
        />
      )}
      <ContextUsageBar roles={memoryRoles} />
      <nav className="live-exec__filters" role="tablist" style={{ display: 'flex', alignItems: 'center' }}>
        {filtersToRender.map((f) => {
          // Phase 49 follow-up — show a red "✗N" badge on the Build
          // tab when at least one build / test / quality gate is
          // currently in a failed terminal state. Helps the operator
          // spot a stalled workstream without having to open the
          // tab. Only computed for the Build tab; everything else
          // gets the unchanged plain label.
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
              {budgetSeconds > 0 && (
                <span style={{ marginLeft: 8, color: 'var(--sc-text-dim, #8d94a3)' }}>
                  · timeout <strong style={{ color: 'var(--sc-text, #e9e6dc)' }}>{budgetSeconds >= 60 ? `${Math.floor(budgetSeconds / 60)}m` : `${budgetSeconds}s`}</strong>
                </span>
              )}
            </span>
          )}
        </nav>
      )}

      <div className="live-exec__transcript" style={{ userSelect: 'text' }}>
        {isWorktreeMode ? (
          <WorktreePane workItemId={workstreamId} />
        ) : isBuildMode ? (
          <BuildPane workItemId={workstreamId} />
        ) : isPromptMode ? (
          <PromptView
            events={transcript}
            activeIter={activeIterNumber}
          />
        ) : isSummaryMode ? (
          <SummaryView
            deliverable={activeSummary}
            loading={live?.loading === true}
            dispatched={transcript.length > 0 || summaryIters.length > 0}
            notesByIter={notesByIter}
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
