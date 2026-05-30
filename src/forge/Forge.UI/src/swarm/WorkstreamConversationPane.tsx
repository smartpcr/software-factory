import { useEffect, useMemo, useState } from 'react';
import { forgeApi } from '@/api/forgeApi';
import { useTaskLive, useTaskLiveStore, type TaskTranscriptEvent } from '@/realtime/useTaskLiveStore';
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
  hasLogAttachment,
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
  /** Workstream id (also the workItemId emitted on the task.{wid} channel by
   *  the engineer crew's gen↔eval loop). */
  workstreamId: string;
  /** DB status of the workstream row — when this is "stalled" or "cancelled"
   *  but executionStatus is still "live"/"queued", the loop has actually
   *  halted and the buttons should reflect that. */
  wsStatus?: string;
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
export function WorkstreamConversationPane({ workstreamId, wsStatus }: Props) {
  const live = useTaskLive(workstreamId);
  const snapshot = live?.snapshot ?? null;
  const transcript = live?.transcript ?? [];
  const transcriptTotalCount = live?.transcriptTotalCount ?? 0;
  const loadingOlder = live?.loadingOlder ?? false;
  const hasOlderEvents = transcript.length < transcriptTotalCount;
  // Vertical-space saver: lets the operator collapse the KPI strip +
  // context-usage bar (the tallest band in the conversation pane) so
  // more transcript fits in the viewport. Persisted in localStorage.
  // Toggle button lives in SwarmDetailPanel's tab-header row (Phase 25).
  const kpiCollapsed = useChromeCollapseStore((s) => s.conversationKpi);

  const [filter, setFilter] = useState<FilterKey>('summary');
  // Phase 59 Stage B — "Has logs" overlay toggle. Applies on top of
  // whichever tab is active, hiding events without a `logUrl` payload
  // field. Local-only state — operators flip it ad-hoc when triaging
  // a long transcript for the failing gate's log file.
  const [logsOnly, setLogsOnly] = useState(false);
  const [selectedIter, setSelectedIter] = useState<number | null>(null);
  const [resuming, setResuming] = useState(false);
  const [restarting, setRestarting] = useState(false);
  const [retrying, setRetrying] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [accepting, setAccepting] = useState(false);
  const showToast = useToastStore((s) => s.show);

  useEffect(() => { setSelectedIter(null); }, [filter, workstreamId]);

  const executionStatus = snapshot?.executionStatus ?? null;
  const lastDeliverable = snapshot?.deliverables?.[snapshot.deliverables.length - 1] ?? null;
  // Button-gating keys off the authoritative workstream lifecycle Status,
  // NOT the gen↔eval loop's ExecutionStatus snapshot. The two diverge once a
  // phase AFTER the loop runs: the impl loop passes (ExecutionStatus stays
  // 'completed') and then review/merge can fail, leaving Status='stalled'.
  // Gating "complete" on the snapshot wrongly treated such a stalled row as
  // done and disabled Retry/Accept/Resume/Cancel. A workstream is complete
  // only when its Status says so (shipped: 'complete'/'historic'); 'stalled'
  // is a recovery state, so the recovery buttons must light up.
  const isComplete = wsStatus === 'complete' || wsStatus === 'historic';
  // A halted workstream (terminal Status) is never "live", regardless of a
  // stale ExecutionStatus snapshot the orchestrator never updated.
  const wsActuallyHalted = isComplete || wsStatus === 'stalled' || wsStatus === 'cancelled';
  const isLive = (executionStatus === 'live' || executionStatus === 'queued') && !wsActuallyHalted;
  // Phase 56 -- terminal-bad verdicts must enable Retry even when
  // ExecutionStatus is stuck at "live"/"queued" (architect and
  // engineer paths can both leave this dangling after a terminal
  // sloppy/stalled verdict; cf. DocConversationPane.tsx). Without
  // this guard the operator sees "● LIVE" on the disabled Retry
  // button even though the loop has actually halted.
  const lastVerdictIsTerminalBad = lastDeliverable !== null && (
    lastDeliverable.verdict === 'sloppy'
    || lastDeliverable.verdict === 'stalled'
    || lastDeliverable.verdict === 'stalled-budget'
    || lastDeliverable.verdict === 'reject');
  const canResume = !isComplete && !isLive && lastDeliverable !== null;
  // Phase 56 -- Retry is the soft recovery affordance for stalled
  // workstreams that hit a gate-exhausted state but have salvageable
  // work. Demotes verdict to "iterate" and reactivates the row WITHOUT
  // wiping deliverables/worktree/PRs (vs Restart's full reset).
  // Enabled when: has deliverable AND not terminal-complete AND
  // (not live OR last verdict is terminal-bad).
  const canRetry = !isComplete
    && lastDeliverable !== null
    && (!isLive || lastVerdictIsTerminalBad);

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
        'Clear prompt, build, and test history',
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
      useTaskLiveStore.getState().resetEntry(workstreamId);
      void useTaskLiveStore.getState().ensureLoaded(workstreamId);
      showToast(`Restarted — ${r.deliverablesCleaned} deliverables cleared.`, 'success');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Restart failed', 'danger');
    } finally { setRestarting(false); }
  }
  async function handleRetry() {
    const ok = await useConfirmStore.getState().ask({
      title: 'Retry workstream?',
      body: 'Soft recovery — demote the last deliverable verdict to "iterate" so the orchestrator re-enters the gen↔eval loop at iter+1. The agent gets another swing with all prior context intact.',
      tone: 'info',
      confirmLabel: '↻ Retry',
      cancelLabel: 'Cancel',
      checklist: [
        'Demote last deliverable verdict (pass / stalled → iterate)',
        'Reset workstream status to "active"',
        'Clear stall reason',
        'Cancel any in-flight runner work',
        'PRESERVE deliverables, transcript, agent_usage, costs, PRs',
        'PRESERVE the worktree on disk and the branch',
        'Re-drive the orchestrator (next tick continues at iter+1 with prior chain)',
      ],
    });
    if (!ok) return;
    setRetrying(true);
    try {
      const r = await forgeApi.retryWorkstream(workstreamId);
      showToast(
        r.demoted
          ? `Retry queued — verdict ${r.priorVerdict} → ${r.newVerdict} (resuming after iter ${r.priorIter}, score ${r.priorScore}).`
          : `Retry queued — verdict already ${r.newVerdict}; just reactivated.`,
        'success');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Retry failed', 'danger');
    } finally { setRetrying(false); }
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
  async function handleAccept() {
    const ok = await useConfirmStore.getState().ask({
      title: 'Accept workstream?',
      body: 'Operator override — flip the latest deliverable verdict to "pass" so the orchestrator commits the worktree files and opens the PR on its next tick. Use when the loop keeps demoting pass→iterate on a false positive but the diff is good.',
      tone: 'info',
      confirmLabel: '✓ Accept',
      cancelLabel: 'Cancel',
      checklist: [
        'Flip last deliverable verdict → pass',
        'Append OPERATOR ACCEPT audit note',
        'Cancel any in-flight runner work + queued task rows',
        'Reset workstream status to "active"',
        'PRESERVE deliverables, transcript, agent_usage, costs, branch, worktree',
        'Orchestrator next tick commits worktree files and opens the PR',
      ],
    });
    if (!ok) return;
    setAccepting(true);
    try {
      const r = await forgeApi.acceptWorkstream(workstreamId);
      showToast(
        r.alreadyPassed
          ? `Already passed (iter ${r.iter}, score ${r.score}); orchestrator nudged.`
          : `Accepted — verdict ${r.priorVerdict} → ${r.newVerdict} (iter ${r.iter}, score ${r.score}). PR opens on next tick.`,
        'success');
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Accept failed', 'danger');
    } finally { setAccepting(false); }
  }

  const generatorAgentId = snapshot?.generatorAgentId ?? null;
  const evaluatorAgentId = snapshot?.evaluatorAgentId ?? null;
  const generatorCrewId = snapshot?.generatorCrewId ?? null;
  const evaluatorCrewId = snapshot?.evaluatorCrewId ?? null;

  const filtered = useMemo(
    () => {
      const base = filterEvents(transcript, filter, generatorCrewId, evaluatorCrewId, generatorAgentId, evaluatorAgentId);
      if (!logsOnly) return base;
      return base.filter(hasLogAttachment);
    },
    [transcript, filter, generatorCrewId, evaluatorCrewId, generatorAgentId, evaluatorAgentId, logsOnly],
  );
  const iterations = useMemo(() => groupByIteration(filtered), [filtered]);

  // Summary mode iteration source — straight from the snapshot's deliverables.
  const summaryIters = snapshot?.deliverables ?? [];

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
  // Active iter mapping — see DocConversationPane.tsx for the rationale.
  // Non-summary modes index `iterations` (grouped transcript), not
  // `iterKpis` (deliverables), so the cursor stays in lock-step with what
  // the operator is actually looking at when the live transcript extends
  // past the last persisted Deliverable row.
  const activeIterNumber = (isSummaryMode
      ? summaryIters[activeIter]?.iter
      : iterations[activeIter]?.iter)
    ?? (iterations.length > 0 ? iterations[iterations.length - 1].iter : null)
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
      {/* Always render KPI strip — shows placeholder row when no
       * iterations exist yet, so the visual structure matches the
       * active-workstream layout from day one. Toggle lives in
       * SwarmDetailPanel's tab-header row (Phase 25). */}
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
        </div>
      ) : (
        <div className="conv-kpi-band">
          <IterKpiStrip
            iters={iterKpis}
            activeIter={activeIterNumber}
            onSelectIter={handleSelectKpiIter}
          />
          <ContextUsageBar roles={memoryRoles} />
        </div>
      )}
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
        {/* Phase 59 Stage B — "Has logs" overlay toggle. Hides any event
            whose payload doesn't carry a logUrl (i.e. anything that isn't
            a gate event with persisted stdout/stderr). Useful when
            triaging a failing run across a long transcript — flip on,
            see only events with a 📎 view-log chip. */}
        <button
          type="button"
          className={`doc-conv-resume-btn${logsOnly ? ' active' : ''}`}
          title="Show only events with a persisted log file (📎 view-log)."
          aria-pressed={logsOnly}
          onClick={() => setLogsOnly((v) => !v)}
          data-testid="transcript-logs-only-toggle"
          style={{
            margin: '0 6px 0 2px',
            padding: '4px 10px',
            borderColor: logsOnly ? 'var(--info, rgb(56,189,248))' : undefined,
            color: logsOnly ? 'var(--info, rgb(56,189,248))' : undefined,
          }}
        >
          {logsOnly ? '📎 Has logs ✓' : '📎 Has logs'}
        </button>
        <button type="button" className="doc-conv-resume-btn" disabled={!canResume || resuming || cancelling || restarting || retrying}
          title={isComplete ? 'Completed.' : isLive ? 'Running.' : !canResume ? 'No iterations yet.' : 'Resume loop.'}
          onClick={() => void handleResume()} style={{ margin: '0 2px', padding: '4px 10px' }}>
          {resuming ? '⏳…' : isLive ? (
            <span style={{ color: 'var(--sc-cyan, #5eead4)', fontWeight: 700 }}>● LIVE</span>
          ) : '↻ RESUME'}
        </button>
        <button type="button" className="doc-conv-resume-btn" disabled={!canRetry || retrying || resuming || cancelling || restarting}
          title={isComplete ? 'Completed.' : isLive ? 'Running.' : !canRetry ? 'No iterations yet.' : 'Soft retry — demote verdict to iterate and resume at iter+1 (keeps worktree, deliverables, PRs).'}
          onClick={() => void handleRetry()}
          style={{ margin: '0 2px', padding: '4px 10px', borderColor: 'var(--sc-cyan, #5eead4)' }}>
          {retrying ? '⏳…' : '↺ RETRY'}
        </button>
        <button type="button" className="doc-conv-resume-btn" disabled={isComplete || cancelling || restarting || resuming || retrying}
          title="Cancel queued tasks." onClick={() => void handleCancel()}
          style={{ margin: '0 2px', padding: '4px 10px', borderColor: 'var(--sc-red, #ff6b80)' }}>
          {cancelling ? '⏳…' : '✕ CANCEL'}
        </button>
        <button type="button" className="doc-conv-resume-btn" disabled={cancelling || restarting || resuming || retrying || accepting}
          title="Restart from iter 1 (wipes worktree, deliverables, PRs)." onClick={() => void handleRestart()}
          style={{ margin: '0 2px', padding: '4px 10px', borderColor: 'var(--sc-amber, #ffb347)' }}>
          {restarting ? '⏳…' : '⟲ RESTART'}
        </button>
        {/* Phase 70 — ACCEPT mirrors plan-doc Phase 43 ACCEPT for workstreams.
            Enabled when: at least one deliverable AND workstream not in terminal
            status. Bypasses Phase 50 / 56 / 66 false-positive demotions when
            the operator has independently verified the diff is good. */}
        <button type="button" className="doc-conv-resume-btn"
          disabled={isComplete || lastDeliverable === null || accepting || cancelling || restarting || resuming || retrying}
          title={isComplete ? 'Completed.' : lastDeliverable === null ? 'No iterations yet — wait for at least one deliverable.' : 'Accept the workstream at its current score — flip verdict to pass and commit + open PR on next tick.'}
          onClick={() => void handleAccept()}
          style={{ margin: '0 2px', padding: '4px 10px', borderColor: 'var(--sc-green, #4ade80)' }}>
          {accepting ? '⏳…' : '✓ ACCEPT'}
        </button>
      </nav>

      {totalIters > 0 && !isPromptMode && (
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
        ) : isToolsMode ? (
          <ToolsPane workItemId={workstreamId} />
        ) : isPromptMode ? (
          <PromptView
            events={transcript}
            activeIter={activeIterNumber}
            workItemId={workstreamId}
          />
        ) : isSummaryMode ? (
          <SummaryView
            deliverable={activeSummary}
            loading={live?.loading === true}
            dispatched={transcript.length > 0 || summaryIters.length > 0}
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
