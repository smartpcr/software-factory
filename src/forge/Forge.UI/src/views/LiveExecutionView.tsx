// Phase 16.10 — Live Execution view.
//
// Header strip + transcript center column + right-rail with three tabs
// (Worktree, Tokens, Progress). Reads the snapshot from
// /api/work-items/{id}/execution and tails the SignalR `task.{id}` group
// for live updates via useTaskLive (Phase 16.13).
import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useParams, useNavigate } from 'react-router-dom';
import { useTaskLive, useTaskLiveStore, type TaskTranscriptEvent, type WorktreeSnapshot, type DeliverableShape } from '@/realtime/useTaskLiveStore';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { BuildPane } from '@/swarm/BuildPane';
import { WorkItemRulesSection } from '@/swarm/WorkItemRulesSection';
import { useProductsStore } from '@/state/useProductsStore';
import {
  type FilterKey,
  FILTERS,
  filterEvents,
  buildPageWindow,
  groupByIteration,
  TranscriptLine,
  renderPayload,
  SummaryView,
  PairSide,
} from './conversation';

type RailTab = 'worktree' | 'tokens' | 'progress' | 'comments' | 'builds';

export function LiveExecutionView(): JSX.Element {
  const params = useParams<{ id: string }>();
  const workItemId = params.id ?? null;
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const rulesProductId = activeProductId && activeProductId !== 'all' ? activeProductId : null;
  const entry = useTaskLive(workItemId);
  const [filter, setFilter] = useState<FilterKey>('all');
  const [tab, setTab]       = useState<RailTab>('worktree');
  const [busy, setBusy]     = useState(false);
  const navigate = useNavigate();

  const [copied, setCopied] = useState(false);
  const [promptsCollapsed, setPromptsCollapsed] = useState(true);
  const [promptRole, setPromptRole] = useState<'generator' | 'evaluator'>('generator');
  const [maxIter, setMaxIter] = useState<number | null>(null);

  const snapshot = entry?.snapshot ?? null;
  const transcript = entry?.transcript ?? [];
  const worktree = entry?.worktree ?? snapshot?.worktree ?? null;

  // Sync local maxIter from server snapshot on first load.
  const effectiveMaxIter = maxIter ?? snapshot?.maxIterations ?? 5;

  const genCrewId = snapshot?.generatorCrewId ?? null;
  const evalCrewId = snapshot?.evaluatorCrewId ?? null;
  const genAgentId = snapshot?.generatorAgentId ?? null;
  const evalAgentId = snapshot?.evaluatorAgentId ?? null;
  const filtered = useMemo(() => filterEvents(transcript, filter, genCrewId, evalCrewId, genAgentId, evalAgentId), [transcript, filter, genCrewId, evalCrewId, genAgentId, evalAgentId]);

  // Phase 19 — group filtered events by iteration for paged display.
  const iterations = useMemo(() => groupByIteration(filtered), [filtered]);

  // Summary-mode iteration list — derived from deliverables (one per iter,
  // dedup latest-wins for restarts). The pager works the same; just the
  // source switches.
  const summaryIters = useMemo<DeliverableShape[]>(() => {
    const ds = snapshot?.deliverables ?? [];
    const byIter = new Map<number, DeliverableShape>();
    for (const d of ds) byIter.set(d.iter, d);
    return Array.from(byIter.values()).sort((a, b) => a.iter - b.iter);
  }, [snapshot?.deliverables]);

  // Resume eligibility + mode — single button that branches on the last
  // deliverable's verdict (pass → delivery replay; iterate/stalled → continue
  // iterating; reject/none → not eligible). The button stays *visible* but
  // is disabled when a run is in flight, so the operator always sees the
  // affordance and understands why it can't fire right now.
  const lastDeliverable = useMemo<DeliverableShape | null>(() => {
    const ds = snapshot?.deliverables ?? [];
    return ds.length > 0 ? ds[ds.length - 1] : null;
  }, [snapshot?.deliverables]);
  const isRunActive = snapshot?.executionStatus === 'live'
    || snapshot?.executionStatus === 'queued';
  // Once execution is completed, all retry-style actions are off — the work
  // is done. The work item lifecycle owns re-opening; from this view, the
  // only forward action is closing the panel.
  const isCompleted = snapshot?.executionStatus === 'completed';
  const resumeMode: 'delivery' | 'iter' | null = (() => {
    if (!lastDeliverable) return null;
    if (lastDeliverable.verdict === 'pass') return 'delivery';
    if (lastDeliverable.verdict === 'iterate'
        || lastDeliverable.verdict === 'stalled'
        || lastDeliverable.verdict === 'stalled-budget') return 'iter';
    return null;
  })();
  const canResume = resumeMode !== null;
  const resumeDisabled = isRunActive || busy || isCompleted;
  const [resumeNote, setResumeNote] = useState('');
  const [resumeComposerOpen, setResumeComposerOpen] = useState(false);

  // Auto-close the composer when a run kicks off — the action it was
  // staging just became inapplicable, no need to keep the panel open.
  useEffect(() => {
    if (isRunActive && resumeComposerOpen) {
      setResumeComposerOpen(false);
      setResumeNote('');
    }
  }, [isRunActive, resumeComposerOpen]);

  const isSummaryMode = filter === 'summary';
  const isBuildMode = filter === 'build';
  const pagerLength = isSummaryMode ? summaryIters.length : iterations.length;
  const totalIters = pagerLength;
  const [selectedIter, setSelectedIter] = useState<number | null>(null);
  // Reset selection when filter changes so we never index out of bounds and
  // the user lands on the latest entry of the new view.
  useEffect(() => { setSelectedIter(null); }, [filter]);
  // Default to the latest iteration; selectedIter===null means "follow latest".
  const activeIter = selectedIter ?? (totalIters > 0 ? totalIters - 1 : 0);
  const visibleEvents = !isSummaryMode && !isBuildMode && totalIters > 0
    ? iterations[activeIter]?.events ?? []
    : (!isSummaryMode && !isBuildMode ? filtered : []);
  const activeSummary = isSummaryMode && totalIters > 0 ? summaryIters[activeIter] ?? null : null;

  // Reverse-chronological: newest events on top.
  const reversedEvents = useMemo(() => [...visibleEvents].reverse(), [visibleEvents]);
  const transcriptTotalCount = entry?.transcriptTotalCount ?? 0;
  const loadingOlder = entry?.loadingOlder ?? false;
  const hasOlderEvents = transcript.length < transcriptTotalCount;

  // Derive the per-role model from the latest task.prompt event when the
  // snapshot doesn't carry it yet (older API build, or task hasn't been
  // re-fetched after the field was added). Snapshot value wins when present
  // — it's resolved server-side with full agent/crew override precedence.
  const promptModels = useMemo(() => {
    let gen: string | null = null;
    let ev: string | null = null;
    for (let i = transcript.length - 1; i >= 0 && (gen === null || ev === null); i--) {
      if (transcript[i].kind !== 'task.prompt') continue;
      const p = transcript[i].payload as Record<string, unknown> | null;
      if (!p || typeof p.model !== 'string' || !p.model) continue;
      const isEvaluator = p.role === 'evaluator';
      if (isEvaluator && ev === null) ev = p.model;
      if (!isEvaluator && gen === null) gen = p.model;
    }
    return { gen, ev };
  }, [transcript]);
  const genModel = snapshot?.generatorModel ?? promptModels.gen ?? null;
  const evalModel = snapshot?.evaluatorModel ?? promptModels.ev ?? null;

  // Extract latest system + user prompt from the most recent task.prompt event, per role.
  const latestPrompt = useMemo(() => {
    for (let i = transcript.length - 1; i >= 0; i--) {
      if (transcript[i].kind === 'task.prompt') {
        const p = transcript[i].payload as Record<string, unknown> | null;
        if (!p) continue;
        const evtRole = (p.role as string) ?? undefined;
        const isEvaluator = evtRole === 'evaluator';
        if (promptRole === 'evaluator' && !isEvaluator) continue;
        if (promptRole === 'generator' && isEvaluator) continue;
        return {
          systemPrompt: (p.systemPrompt as string) ?? '',
          userPrompt: (p.userPrompt as string) ?? '',
          model: (p.model as string) ?? '',
          backend: (p.backend as string) ?? '',
          mode: (p.invokeMode as string) ?? '',
        };
      }
    }
    return null;
  }, [transcript, promptRole]);

  function copyLogs() {
    const lines = filtered.map((evt) => {
      const time = new Date(evt.at).toLocaleTimeString();
      const kind = evt.kind.replace('task.', '');
      const text = renderPayload(evt);
      return `${time}  ${kind}  ${text}`;
    });
    void navigator.clipboard.writeText(lines.join('\n')).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    });
  }

  const handleMaxIterChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    const v = parseInt(e.target.value, 10);
    if (Number.isNaN(v) || v < 1 || v > 50) return;
    setMaxIter(v);
    if (workItemId) {
      void forgeApi.patchExecution(workItemId, { maxIterations: v }).catch(() => {
        useToastStore.getState().show('Failed to update max iterations', 'danger');
      });
    }
  }, [workItemId]);

  if (!workItemId) {
    return <div className="live-exec__empty">Pick a work item to watch.</div>;
  }

  // Surface API-offline / load-failure state instead of showing an empty
  // shell forever. The store keeps `entry.error` populated when the GET
  // /execution call fails or when SignalR negotiation 500s.
  const apiOfflineBanner = entry?.error ? (
    <div
      role="alert"
      data-testid="live-exec-api-offline"
      style={{
        background: 'color-mix(in srgb, rgb(220, 38, 38) 14%, transparent)',
        border: '1px solid rgb(220, 38, 38)',
        color: 'rgb(220, 38, 38)',
        padding: '8px 14px',
        fontSize: 12,
        margin: '8px 0',
        borderRadius: 'var(--radius-sm)',
        display: 'flex',
        alignItems: 'center',
        gap: 12,
      }}
    >
      <span>⚠ Forge API is unreachable.</span>
      <span style={{ flex: 1, color: 'var(--text-secondary)', fontFamily: 'var(--font-mono)', fontSize: 11 }}>
        {entry.error}
      </span>
      <button
        type="button"
        className="btn btn-secondary"
        onClick={() => { void useTaskLiveStore.getState().ensureLoaded(workItemId); }}
      >
        Retry
      </button>
    </div>
  ) : null;

  async function cancelAndRequeue() {
    if (!workItemId) return;
    setBusy(true);
    try {
      // Find the queue entry for this work item.
      const queue: Array<{ id: number; workItemId: string; state: string }> = await forgeApi.listQueue();
      const entry = queue.find((q) => q.workItemId === workItemId);
      if (entry) {
        await forgeApi.cancelQueue(entry.id, 're-queue from live exec');
      }
      // Clear assignment on the work item.
      await forgeApi.patchWorkItem(workItemId, { clearAssignedTo: true });
      useToastStore.getState().show('Cancelled and cleared assignment. Ready to re-dispatch.', 'success');
      navigate('/work-items');
    } catch (e) {
      useToastStore.getState().show(e instanceof Error ? e.message : 'Failed', 'danger');
    } finally {
      setBusy(false);
    }
  }

  async function requeue() {
    if (!workItemId || !snapshot?.generatorCrewId) return;
    setBusy(true);
    try {
      const queue: Array<{ id: number; workItemId: string; state: string }> = await forgeApi.listQueue();
      const queueEntry = queue.find((q) => q.workItemId === workItemId);
      if (queueEntry) {
        await forgeApi.cancelQueue(queueEntry.id, 're-queue');
      }
      await forgeApi.requeueWorkItem(snapshot.generatorCrewId, workItemId, { restart: true });
      useToastStore.getState().show('Re-queued successfully', 'success');
    } catch (e) {
      useToastStore.getState().show(e instanceof Error ? e.message : 'Failed', 'danger');
    } finally {
      setBusy(false);
    }
  }

  async function resume(noteOverride?: string) {
    if (!workItemId) return;
    setBusy(true);
    try {
      const r = await forgeApi.resume(workItemId, noteOverride);
      const msg = r.mode === 'delivery-replay'
        ? `Resumed — replaying delivery from prior pass (iter ${r.priorIter}, score ${r.priorScore}).`
        : `Resumed — continuing iteration from iter ${r.priorIter} (verdict ${r.priorVerdict}, score ${r.priorScore})${r.noteApplied ? ' with steering note' : ''}.`;
      useToastStore.getState().show(msg, 'success');
      setResumeComposerOpen(false);
      setResumeNote('');
    } catch (e) {
      useToastStore.getState().show(
        e instanceof Error ? e.message : 'Resume failed',
        'danger',
      );
    } finally {
      setBusy(false);
    }
  }

  function onResumeClick() {
    // Always open the composer so the operator gets a clear hint about
    // what's about to run before the API call fires. For the pass path
    // the composer is preview-only (no note input). For the iter path
    // it includes the steering-note textarea.
    setResumeComposerOpen(true);
  }

  async function forceRestart() {
    if (!workItemId) return;
    setBusy(true);
    try {
      // 1. Cancel any active queue entry.
      const queue = await forgeApi.listQueue();
      const queueEntry = queue.find((q) => q.workItemId === workItemId && q.state !== 'cancelled' && q.state !== 'completed');
      const crewId = snapshot?.generatorCrewId || queueEntry?.routedToCrewId;
      if (queueEntry) {
        await forgeApi.cancelQueue(queueEntry.id, 'force-restart');
      }
      if (!crewId) {
        useToastStore.getState().show('No crew to restart with — assign a crew first.', 'danger');
        return;
      }
      // 2. Clear transcript + deliverables on server.
      await forgeApi.clearWorkItemTranscript(workItemId);
      // 3. Re-enqueue with restart semantics: reset state, clear iters, skip soul prompt.
      await forgeApi.requeueWorkItem(crewId, workItemId, { restart: true });
      // 4. Clear local store so the view reloads fresh.
      const store = useTaskLiveStore.getState();
      store.entries[workItemId] = {
        snapshot: null, transcript: [], worktree: null,
        loading: false, lastEventId: 0,
        refCount: store.entries[workItemId]?.refCount ?? 1,
        transcriptTotalCount: 0, loadingOlder: false,
      };
      // 5. Re-hydrate from server.
      await store.ensureLoaded(workItemId);
      useToastStore.getState().show('Conversation restarted', 'success');
    } catch (e) {
      useToastStore.getState().show(e instanceof Error ? e.message : 'Restart failed', 'danger');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="live-exec">
      {apiOfflineBanner}
      <header className="live-exec__header">
        <div className="live-exec__title">
          <Link to="/work-items" style={{ color: 'inherit', textDecoration: 'none' }}>
            ← Live Execution
          </Link>
          <small>{workItemId}</small>
        </div>
        <div className="live-exec__pair">
          <PairSide
            role="generator"
            crewName={snapshot?.generatorCrewName}
            agentName={snapshot?.generatorAgentName ?? snapshot?.generatorAgentId}
            model={genModel}
            selected={promptRole === 'generator'}
            onClick={() => setPromptRole('generator')}
          />
          <span className="live-exec__pair-conn">↔ paired</span>
          <PairSide
            role="evaluator"
            crewName={snapshot?.evaluatorCrewName}
            agentName={snapshot?.evaluatorAgentName ?? snapshot?.evaluatorAgentId}
            model={evalModel}
            sameAsOther={!!genModel && !!evalModel && genModel === evalModel}
            selected={promptRole === 'evaluator'}
            onClick={() => setPromptRole('evaluator')}
          />
        </div>
        <div className="live-exec__iter">
          <strong>iter {snapshot?.currentIteration ?? 0}</strong> of{' '}
          <input
            type="number"
            min={1}
            max={50}
            value={effectiveMaxIter}
            onChange={handleMaxIterChange}
            style={{ width: 48, textAlign: 'center', fontSize: 'inherit', fontWeight: 'bold', border: '1px solid var(--border, #ccc)', borderRadius: 3, padding: '1px 4px' }}
          />
          {snapshot?.executionStatus && snapshot.executionStatus !== 'live' && snapshot.executionStatus !== 'idle' && (
            <span className={`live-exec__status-badge live-exec__status-badge--${snapshot.executionStatus}`}>
              {snapshot.executionStatus === 'failed' ? '✗ failed' :
               snapshot.executionStatus === 'completed' ? '✓ completed' :
               snapshot.executionStatus === 'queued' ? '◌ queued' :
               snapshot.executionStatus}
            </span>
          )}
        </div>
        <div style={{ display: 'flex', gap: 6, marginLeft: 'auto' }}>
          {canResume && (
            <button
              className="btn btn-primary"
              style={{
                fontSize: 11,
                padding: '4px 10px',
                background: resumeDisabled ? 'var(--bg-surface-2)' : 'var(--success, #10b981)',
                borderColor: resumeDisabled ? 'var(--border)' : 'var(--success, #10b981)',
                color: resumeDisabled ? 'var(--text-tertiary)' : undefined,
                cursor: resumeDisabled ? 'not-allowed' : undefined,
              }}
              disabled={resumeDisabled}
              onClick={onResumeClick}
              title={
                isCompleted
                  ? 'Resume disabled — execution is completed. Re-open the work item to retry.'
                  : isRunActive
                    ? `Resume disabled — a run is in flight (status: ${snapshot?.executionStatus}). Wait for it to finish or cancel it first.`
                    : resumeMode === 'delivery'
                      ? `Replay build + push + PR. Iter ${lastDeliverable?.iter} already passed (score ${lastDeliverable?.score}); no agent re-run.`
                      : `Continue iterating from iter ${lastDeliverable?.iter} (verdict ${lastDeliverable?.verdict}, score ${lastDeliverable?.score}). Opens a note composer first.`
              }
            >
              ▶ Resume
            </button>
          )}
          <button
            className="btn btn-primary"
            style={{ fontSize: 11, padding: '4px 10px' }}
            disabled={busy || isCompleted}
            onClick={() => void forceRestart()}
            title={
              isCompleted
                ? 'Restart disabled — execution is completed.'
                : 'Cancel active run and restart the conversation from scratch'
            }
          >
            ⟳ Restart
          </button>
          <button
            className="btn btn-secondary"
            style={{ fontSize: 11, padding: '4px 10px' }}
            disabled={busy || isCompleted}
            onClick={() => void requeue()}
            title={
              isCompleted
                ? 'Re-queue disabled — execution is completed.'
                : 'Cancel current queue entry and re-enqueue to the same crew'
            }
          >
            ↻ Re-queue
          </button>
          <button
            className="btn btn-secondary"
            style={{ fontSize: 11, padding: '4px 10px', color: 'var(--danger, #ef4444)' }}
            disabled={busy || isCompleted}
            onClick={() => void cancelAndRequeue()}
            title={
              isCompleted
                ? 'Unassign disabled — execution is completed.'
                : 'Cancel queue entry and clear agent assignment — returns to unassigned state'
            }
          >
            ✕ Unassign
          </button>
        </div>
      </header>

      {resumeComposerOpen && canResume && (
        <section
          aria-label="Resume preview"
          style={{
            border: '1px solid var(--success, #10b981)',
            background: 'color-mix(in srgb, var(--success, #10b981) 6%, transparent)',
            borderRadius: 8,
            padding: '10px 14px',
            margin: '8px 0',
            display: 'flex',
            flexDirection: 'column',
            gap: 8,
          }}
        >
          {resumeMode === 'delivery' ? (
            <>
              <div style={{ fontSize: 12, color: 'var(--text-secondary)' }}>
                <strong style={{ color: 'var(--text-primary)' }}>Resume — replay delivery</strong>
                {' — '}
                iter {lastDeliverable?.iter} already passed (score {lastDeliverable?.score}).
                {' '}Skips the gen+eval loop; no agent runs, no payroll.
              </div>
              <ul style={{ fontSize: 11, color: 'var(--text-tertiary)', margin: 0, paddingLeft: 18 }}>
                <li>Run the build gate (<code>dotnet build Workflow.sln</code>)</li>
                <li>Run the test gate if configured</li>
                <li>Push <code>ws/{workItemId}</code> with <code>--force-with-lease</code></li>
                <li>Open the PR (or update the existing one)</li>
              </ul>
            </>
          ) : (
            <>
              <div style={{ fontSize: 12, color: 'var(--text-secondary)' }}>
                <strong style={{ color: 'var(--text-primary)' }}>Resume — continue iterating</strong>
                {' — '}
                from iter {lastDeliverable?.iter}
                {' '}(verdict <code>{lastDeliverable?.verdict}</code>, score {lastDeliverable?.score}).
                {' '}Optional note steers the next iteration; leave blank to just iterate again.
              </div>
              <textarea
                value={resumeNote}
                onChange={(e) => setResumeNote(e.target.value)}
                placeholder="e.g. The build was failing because of an upstream issue that has now been fixed. Verify your changes still apply cleanly and update the change summary."
                rows={4}
                style={{
                  fontFamily: 'var(--font-mono, ui-monospace)',
                  fontSize: 12,
                  padding: 8,
                  background: 'var(--bg-surface)',
                  color: 'var(--text-primary)',
                  border: '1px solid var(--border)',
                  borderRadius: 4,
                  resize: 'vertical',
                }}
                disabled={busy}
              />
            </>
          )}
          <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end', alignItems: 'center' }}>
            {busy && (
              <span style={{ fontSize: 11, color: 'var(--text-tertiary)', marginRight: 'auto' }}>
                Enqueuing…
              </span>
            )}
            <button
              type="button"
              className="btn btn-secondary"
              style={{ fontSize: 11, padding: '4px 10px' }}
              disabled={busy}
              onClick={() => { setResumeComposerOpen(false); setResumeNote(''); }}
            >
              Cancel
            </button>
            <button
              type="button"
              className="btn btn-primary"
              style={{ fontSize: 11, padding: '4px 10px', background: 'var(--success, #10b981)', borderColor: 'var(--success, #10b981)' }}
              disabled={busy}
              onClick={() => void resume(
                resumeMode === 'iter' ? (resumeNote.trim() || undefined) : undefined,
              )}
            >
              {busy
                ? '▶ Resuming…'
                : resumeMode === 'delivery'
                  ? '▶ Replay delivery'
                  : `▶ Resume ${resumeNote.trim() ? 'with note' : 'without note'}`}
            </button>
          </div>
        </section>
      )}

      {/* Phase 108 — pick the catalog rules this execution's prompt carries. */}
      {rulesProductId && workItemId && (
        <WorkItemRulesSection productId={rulesProductId} workItemId={workItemId} />
      )}

      {latestPrompt && (latestPrompt.systemPrompt || latestPrompt.userPrompt) && (
        <section className="live-exec__prompts" aria-label="Prompts">
          <div className="live-exec__prompts-header" onClick={() => setPromptsCollapsed(!promptsCollapsed)} role="button" tabIndex={0}>
            <span className="live-exec__prompts-title">
              {promptsCollapsed ? '▸' : '▾'} {promptRole === 'generator' ? 'Generator' : 'Evaluator'} Prompts
            </span>
            <span className="live-exec__prompts-meta">
              {latestPrompt.backend}/{latestPrompt.mode} · {latestPrompt.model}
            </span>
          </div>
          {!promptsCollapsed && (
            <div className="live-exec__prompts-body">
              {latestPrompt.systemPrompt && (
                <div className="live-exec__prompt-standalone">
                  <div className="live-exec__prompt-label">System prompt</div>
                  <pre className="live-exec__prompt-pre">{latestPrompt.systemPrompt}</pre>
                </div>
              )}
              {latestPrompt.userPrompt && (
                <div className="live-exec__prompt-standalone">
                  <div className="live-exec__prompt-label">User prompt</div>
                  <pre className="live-exec__prompt-pre">{latestPrompt.userPrompt}</pre>
                </div>
              )}
            </div>
          )}
        </section>
      )}

      <main className="live-exec__center">
        <nav className="live-exec__filters" role="tablist">
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
          <button
            type="button"
            className="live-exec__copy-btn"
            onClick={copyLogs}
            disabled={filtered.length === 0}
            title="Copy visible logs to clipboard"
          >
            {copied ? '✓ Copied' : '⎘ Copy logs'}
          </button>
        </nav>
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
                    <span className={`live-exec__iter-pager-score ${slotVerdict ?? ''}`}>
                      {slotScore}
                    </span>
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
            <span className="live-exec__iter-pager-label">
              Iteration {activeIter + 1} of {totalIters}
              {selectedIter === null && ' (latest)'}
            </span>
          </nav>
        )}
        <div className="live-exec__transcript" style={{ userSelect: 'text' }}>
          {isSummaryMode ? (
            <SummaryView deliverable={activeSummary} loading={!!entry?.loading} dispatched={snapshot?.autoDispatchedAt != null} />
          ) : isBuildMode ? (
            <BuildPane workItemId={workItemId} />
          ) : (
            <>
              {transcriptTotalCount > 0 && (
                <div className="live-exec__transcript-count" style={{ fontSize: 11, color: 'var(--text-tertiary, #888)', padding: '4px 8px', borderBottom: '1px solid var(--border, #e5e7eb)' }}>
                  Showing 1–{transcript.length.toLocaleString()} most recent of {transcriptTotalCount.toLocaleString()} total events
                </div>
              )}
              {reversedEvents.length === 0 ? (
                <div className="live-exec__empty">
                  {entry?.loading ? 'Loading…' : 'No events yet.'}
                  {snapshot?.autoDispatchedAt == null && !entry?.loading && (
                    <div className="live-exec__empty-cta">Waiting for the lead to delegate…</div>
                  )}
                </div>
              ) : (
                reversedEvents.map((evt) => <TranscriptLine key={`${evt.id}:${evt.at}`} evt={evt} generatorCrewId={genCrewId} evaluatorCrewId={evalCrewId} />)
              )}
              {hasOlderEvents && (
                <div style={{ textAlign: 'center', padding: '8px 0' }}>
                  <button
                    type="button"
                    className="btn btn-secondary"
                    style={{ fontSize: 11, padding: '4px 12px' }}
                    disabled={loadingOlder}
                    onClick={() => workItemId && void useTaskLiveStore.getState().loadOlderEvents(workItemId)}
                  >
                    {loadingOlder ? 'Loading…' : '↑ Load older events'}
                  </button>
                </div>
              )}
            </>
          )}
        </div>
      </main>

      <aside className="live-exec__rail">
        <div className="live-exec__tabs">
          <button type="button" className={`live-exec__tab${tab === 'worktree' ? ' active' : ''}`} onClick={() => setTab('worktree')}>Worktree</button>
          <button type="button" className={`live-exec__tab${tab === 'tokens'   ? ' active' : ''}`} onClick={() => setTab('tokens')}>Tokens</button>
          <button type="button" className={`live-exec__tab${tab === 'progress' ? ' active' : ''}`} onClick={() => setTab('progress')}>Progress</button>
          <button type="button" className={`live-exec__tab${tab === 'comments' ? ' active' : ''}`} onClick={() => setTab('comments')}>Comments</button>
          <button type="button" className={`live-exec__tab${tab === 'builds'   ? ' active' : ''}`} onClick={() => setTab('builds')}>Builds</button>
        </div>
        <div className="live-exec__tab-body">
          {tab === 'worktree' && <WorktreeTab snap={worktree} transcript={transcript} workItemId={workItemId} />}
          {tab === 'tokens'   && <TokensTab snap={snapshot} />}
          {tab === 'progress' && <ProgressTab snap={snapshot} />}
          {tab === 'comments' && <CommentsTab workItemId={workItemId} />}
          {tab === 'builds'   && <BuildsTab workItemId={workItemId} />}
        </div>
      </aside>
    </div>
  );
}

// ── Sub-components ────────────────────────────────────────────────────────

// @ts-expect-error — unused but kept for future budget display
function BudgetBar(props: { label: string; spend: number; cap: number | null }): JSX.Element {
  const { label, spend, cap } = props;
  const ratio = cap && cap > 0 ? Math.min(1, spend / cap) : 0;
  const over = !!cap && spend > cap;
  return (
    <>
      <span>
        {label}: <strong>${spend.toFixed(2)}</strong>
        {cap ? <span style={{ color: 'var(--text-tertiary)' }}> / ${cap.toFixed(2)}</span> : null}
      </span>
      <div className="live-exec__budget-bar">
        <div
          className={`live-exec__budget-fill${over ? ' over' : ''}`}
          style={{ width: `${(ratio * 100).toFixed(0)}%` }}
        />
      </div>
    </>
  );
}

type CommitsResponse = Awaited<ReturnType<typeof forgeApi.getWorkItemCommits>>;

function WorktreeTab(props: {
  snap: WorktreeSnapshot | null;
  transcript: TaskTranscriptEvent[];
  workItemId: string | null;
}): JSX.Element {
  const { snap, transcript, workItemId } = props;

  // Fetch commits from the authoritative `git log` endpoint when the tab
  // opens, and re-fetch whenever the latest worktree-tick reports a new
  // HEAD sha (agent committed). The transcript-replay reconstruction was
  // lossy — commits faster than the tick interval lost their file list.
  const [data, setData] = useState<CommitsResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Derive a "stable signal" from transcript so we refetch only when a new
  // commit actually lands, not on every tick.
  const latestTickSha = useMemo(() => {
    for (let i = transcript.length - 1; i >= 0; i--) {
      const e = transcript[i];
      if (e.kind !== 'task.worktree-tick') continue;
      const head = (e.payload as { head?: { sha?: string } } | null)?.head;
      if (head?.sha) return head.sha;
    }
    return null;
  }, [transcript]);

  useEffect(() => {
    if (!workItemId) return;
    let cancelled = false;
    setLoading(true);
    setError(null);
    forgeApi.getWorkItemCommits(workItemId)
      .then((r) => { if (!cancelled) setData(r); })
      .catch((e: unknown) => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [workItemId, latestTickSha]);

  if (!snap && !data) {
    return <div className="live-exec__empty">No worktree activity yet.</div>;
  }

  const commits = data?.commits ?? [];
  const uncommitted = data?.uncommitted ?? { added: [], modified: [], deleted: [] };
  // Phase 49 follow-up — branch totals (committed + uncommitted vs
  // origin/baseBranch) are the right scope for the +N ~N -N pills.
  // `uncommitted` alone underreports the moment the agent commits.
  const branchTotals = data?.branchTotals ?? null;
  const hasDirty = uncommitted.added.length + uncommitted.modified.length + uncommitted.deleted.length > 0;

  // Leaf directory name — the project convention is `ws-{workItemId}` under
  // a fixed `.worktree/` parent, so the leaf is the most useful "name" the
  // operator recognises (it matches the agent's branch suffix). Prefer the
  // path the API resolved; fall back to the snap or the workstream id.
  const wtPath = data?.worktreePath ?? snap?.worktreePath ?? null;
  const wtLeaf = wtPath
    ? (wtPath.split(/[\\/]/).filter(Boolean).pop() ?? wtPath)
    : (snap?.workstreamId || null);

  // Pills: prefer branchTotals (committed + uncommitted), fall back to
  // uncommitted-only when branchTotals isn't available (older server),
  // then to the live snap when no API response has loaded.
  // Phase 49 follow-up — when only `snap` is available, prefer
  // `snap.branchDiff` array lengths over `snap.added/.modified/.deleted`
  // scalars. The scalars are git-status uncommitted counts (always 0
  // once the agent commits); the branchDiff arrays carry the cumulative
  // committed+uncommitted scope. Without this fallback the pre-API-loaded
  // window misreports +0 ~0 -0 for workstreams that have finished
  // committing.
  const aheadCount = data?.ahead ?? snap?.ahead ?? 0;
  const behindCount = data?.behind ?? snap?.behind ?? 0;
  const addedCount = branchTotals
    ? branchTotals.added.length
    : (data ? uncommitted.added.length : (snap?.branchDiff?.added.length ?? snap?.added ?? 0));
  const modifiedCount = branchTotals
    ? branchTotals.modified.length
    : (data ? uncommitted.modified.length : (snap?.branchDiff?.modified.length ?? snap?.modified ?? 0));
  const deletedCount = branchTotals
    ? branchTotals.deleted.length
    : (data ? uncommitted.deleted.length : (snap?.branchDiff?.deleted.length ?? snap?.deleted ?? 0));

  return (
    <div>
      {(wtLeaf || wtPath) && (
        <div className="live-exec__wt-name" title={wtPath ?? undefined}>
          {wtLeaf && <div className="live-exec__wt-name__leaf">{wtLeaf}</div>}
          {wtPath && <div className="live-exec__wt-name__path">{wtPath}</div>}
        </div>
      )}
      <div className="live-exec__wt-summary">
        <span className="live-exec__wt-pill added">+{addedCount}</span>
        <span className="live-exec__wt-pill modified">~{modifiedCount}</span>
        <span className="live-exec__wt-pill deleted">−{deletedCount}</span>
        {aheadCount > 0  && <span className="live-exec__wt-pill ahead">↑{aheadCount}</span>}
        {behindCount > 0 && <span className="live-exec__wt-pill ahead">↓{behindCount}</span>}
        {loading && <span style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>loading commits…</span>}
      </div>

      {error && (
        <div style={{
          fontSize: 11,
          color: 'rgb(220, 38, 38)',
          background: 'color-mix(in srgb, rgb(220, 38, 38) 10%, transparent)',
          border: '1px solid rgb(220, 38, 38)',
          padding: '6px 10px', borderRadius: 4, margin: '6px 0',
        }}>
          ⚠ commits load failed: {error}
        </div>
      )}

      {commits.length > 0 && (
        <div className="live-exec__commits">
          {commits.map((c) => (
            <div key={c.sha} className="live-exec__commit-group">
              <div className="live-exec__wt-head">
                <div className="live-exec__wt-sha">{c.sha.slice(0, 8)}</div>
                <div className="live-exec__wt-msg">{c.message}</div>
                <div className="live-exec__wt-author">{c.author} · {c.at ? new Date(c.at).toLocaleString() : ''}</div>
              </div>
              <div className="live-exec__file-list">
                {c.added.map((p) => <div key={`a${p}`} className="live-exec__file-row added">{p}</div>)}
                {c.modified.map((p) => <div key={`m${p}`} className="live-exec__file-row modified">{p}</div>)}
                {c.deleted.map((p) => <div key={`d${p}`} className="live-exec__file-row deleted">{p}</div>)}
                {c.other.map((o) => (
                  <div key={`o${o.path}`} className="live-exec__file-row modified" title={o.oldPath ? `${o.status} from ${o.oldPath}` : o.status}>
                    {o.status} {o.path}
                  </div>
                ))}
              </div>
            </div>
          ))}
        </div>
      )}

      {commits.length === 0 && !loading && data && (
        <div style={{ fontSize: 11, color: 'var(--text-tertiary)', padding: '8px 0' }}>
          No commits yet on <code>{data.branch}</code>.
        </div>
      )}

      {commits.length === 0 && !data && snap?.headSha && (
        <div className="live-exec__wt-head">
          <div className="live-exec__wt-sha">{snap.headSha.slice(0, 8)}</div>
          <div className="live-exec__wt-msg">{snap.headMessage ?? ''}</div>
          <div className="live-exec__wt-author">{snap.headAuthor ?? ''} · {snap.headAt ? new Date(snap.headAt).toLocaleString() : ''}</div>
        </div>
      )}

      {hasDirty && (
        <div className="live-exec__commit-group" style={{ opacity: 0.7 }}>
          <div className="live-exec__wt-msg" style={{ fontStyle: 'italic', fontSize: 11 }}>Uncommitted changes</div>
          <div className="live-exec__file-list">
            {uncommitted.added.map((p) => <div key={`ua${p}`} className="live-exec__file-row added">{p}</div>)}
            {uncommitted.modified.map((p) => <div key={`um${p}`} className="live-exec__file-row modified">{p}</div>)}
            {uncommitted.deleted.map((p) => <div key={`ud${p}`} className="live-exec__file-row deleted">{p}</div>)}
          </div>
        </div>
      )}

      {snap?.truncated && <div style={{ fontSize: 11, color: 'var(--text-tertiary)', marginTop: 6 }}>list truncated at 200 paths.</div>}
    </div>
  );
}

function TokensTab(props: { snap: TaskTranscriptEvent extends never ? never : import('@/realtime/useTaskLiveStore').TaskExecutionSnapshot | null }): JSX.Element {
  const { snap } = props;
  if (!snap) return <div className="live-exec__empty">No invocations yet.</div>;
  const total = snap.generatorCostUsd + snap.evaluatorCostUsd;
  const genShare  = total > 0 ? (snap.generatorCostUsd / total) * 100 : 0;
  const evalShare = total > 0 ? (snap.evaluatorCostUsd / total) * 100 : 0;
  const genTokens = snap.generatorInputTokens + snap.generatorOutputTokens;
  const evalTokens = snap.evaluatorInputTokens + snap.evaluatorOutputTokens;
  const totalTokens = genTokens + evalTokens;

  return (
    <div>
      <div className="live-exec__cost-row">
        <span>Generator</span>
        <span className="live-exec__cost-tokens">{genTokens.toLocaleString()} tok</span>
        <strong>${snap.generatorCostUsd.toFixed(4)}</strong>
      </div>
      <div className="live-exec__cost-detail">
        <span>↓ {snap.generatorInputTokens.toLocaleString()} in</span>
        <span>↑ {snap.generatorOutputTokens.toLocaleString()} out</span>
      </div>
      <div className="live-exec__cost-row">
        <span>Evaluator</span>
        <span className="live-exec__cost-tokens">{evalTokens.toLocaleString()} tok</span>
        <strong>${snap.evaluatorCostUsd.toFixed(4)}</strong>
      </div>
      <div className="live-exec__cost-detail">
        <span>↓ {snap.evaluatorInputTokens.toLocaleString()} in</span>
        <span>↑ {snap.evaluatorOutputTokens.toLocaleString()} out</span>
      </div>
      <div className="live-exec__tokens-bar" aria-hidden="true">
        <span className="gen"  style={{ width: `${genShare}%` }} />
        <span className="eval" style={{ width: `${evalShare}%` }} />
      </div>
      <div className="live-exec__cost-row live-exec__cost-total">
        <span>Total</span>
        <span className="live-exec__cost-tokens">{totalTokens.toLocaleString()} tok</span>
        <strong>${total.toFixed(4)}</strong>
      </div>
      {snap.crewDailyBudgetUsd != null && (
        <div style={{ marginTop: 12 }}>
          <div className="live-exec__budget-bar">
            <div
              className={`live-exec__budget-fill${snap.crewSpendTodayUsd && snap.crewSpendTodayUsd > snap.crewDailyBudgetUsd ? ' over' : ''}`}
              style={{ width: `${Math.min(100, ((snap.crewSpendTodayUsd ?? 0) / snap.crewDailyBudgetUsd) * 100).toFixed(0)}%` }}
            />
          </div>
          <div style={{ fontSize: 11, color: 'var(--text-tertiary)', marginTop: 4 }}>
            crew today: ${snap.crewSpendTodayUsd?.toFixed(2) ?? '0.00'} / ${snap.crewDailyBudgetUsd.toFixed(2)}
          </div>
        </div>
      )}
      <PromptGrowthPanel snap={snap} />
    </div>
  );
}

/**
 * Phase 54 — prompt-size growth across iterations. Renders two inline
 * SVG sparklines (generator + evaluator) plotting `inputTokens` per
 * iteration. Numeric readout shows the latest absolute value and the
 * delta vs. the previous iteration; the delta is coloured amber when
 * |delta| > snap.promptBloatGrowthThreshold OR when a `task.prompt-bloat`
 * warning fired for that iteration (carries through `promptBloatRole`).
 */
function PromptGrowthPanel(props: { snap: import('@/realtime/useTaskLiveStore').TaskExecutionSnapshot }): JSX.Element | null {
  const { snap } = props;
  const series = snap.promptGrowth ?? [];
  if (series.length === 0) return null;
  const threshold = snap.promptBloatGrowthThreshold ?? 30_000;
  return (
    <div className="live-exec__prompt-growth" style={{ marginTop: 16, paddingTop: 12, borderTop: '1px solid var(--border-subtle)' }}>
      <div className="live-exec__prompt-growth-title" style={{ fontSize: 11, color: 'var(--text-tertiary)', textTransform: 'uppercase', letterSpacing: 0.5, marginBottom: 8 }}>
        Prompt size across iterations
      </div>
      <PromptGrowthRow
        label="Generator"
        series={series}
        accessor={(g) => g.generatorInputTokens}
        threshold={threshold}
        warnRole="generator"
      />
      <PromptGrowthRow
        label="Evaluator"
        series={series}
        accessor={(g) => g.evaluatorInputTokens}
        threshold={threshold}
        warnRole="evaluator"
      />
      <div style={{ fontSize: 10, color: 'var(--text-tertiary)', marginTop: 6 }}>
        amber Δ ≥ {threshold.toLocaleString()} input tokens (iteration-over-iteration)
      </div>
    </div>
  );
}

function PromptGrowthRow(props: {
  label: string;
  series: import('@/realtime/useTaskLiveStore').IterationPromptGrowth[];
  accessor: (g: import('@/realtime/useTaskLiveStore').IterationPromptGrowth) => number;
  threshold: number;
  warnRole: 'generator' | 'evaluator';
}): JSX.Element {
  const { label, series, accessor, threshold, warnRole } = props;
  const values = series.map(accessor);
  const latest = values[values.length - 1] ?? 0;
  const prev = values.length >= 2 ? values[values.length - 2] : 0;
  const delta = latest - prev;
  const latestIter = series[series.length - 1];
  const hadBloat = latestIter?.promptBloatRole === warnRole;
  const amber = (values.length >= 2 && Math.abs(delta) >= threshold) || hadBloat;
  const max = Math.max(1, ...values);
  const W = 200;
  const H = 36;
  const points = values.map((v, i) => {
    const x = values.length === 1 ? W / 2 : (i / (values.length - 1)) * W;
    const y = H - (v / max) * (H - 4) - 2;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 4 }}>
      <span style={{ width: 70, fontSize: 11, color: 'var(--text-secondary)' }}>{label}</span>
      <svg width={W} height={H} aria-hidden="true" style={{ flexShrink: 0 }}>
        {values.length === 1 ? (
          <circle cx={W / 2} cy={H - (values[0] / max) * (H - 4) - 2} r={2} fill="var(--graph-line, #6aa3ff)" />
        ) : (
          <polyline
            points={points}
            fill="none"
            stroke={amber ? '#d8a232' : 'var(--graph-line, #6aa3ff)'}
            strokeWidth={1.5}
          />
        )}
      </svg>
      <span style={{ fontSize: 11, fontVariantNumeric: 'tabular-nums', color: 'var(--text-primary)', minWidth: 80, textAlign: 'right' }}>
        {latest.toLocaleString()}
      </span>
      <span
        title={hadBloat ? 'task.prompt-bloat fired for this iteration' : undefined}
        style={{ fontSize: 11, fontVariantNumeric: 'tabular-nums', color: amber ? '#d8a232' : 'var(--text-tertiary)', minWidth: 60, textAlign: 'right' }}
      >
        {values.length >= 2 ? `${delta >= 0 ? '+' : ''}${delta.toLocaleString()}` : '—'}
      </span>
    </div>
  );
}

function ProgressTab(props: { snap: import('@/realtime/useTaskLiveStore').TaskExecutionSnapshot | null }): JSX.Element {
  const { snap } = props;
  if (!snap) return <div className="live-exec__empty">No deliverables yet.</div>;

  // Deduplicate deliverables by iter — keep only the latest entry per iteration
  // (restarts produce multiple rows with the same iter number).
  const byIter = new Map<number, (typeof snap.deliverables)[number]>();
  for (const d of snap.deliverables) {
    byIter.set(d.iter, d);
  }
  const deduped = Array.from(byIter.values()).sort((a, b) => a.iter - b.iter);

  return (
    <div>
      <div className="live-exec__iter-list">
        {deduped.map((d) => (
          <div key={d.id} className="live-exec__iter-item">
            <div className="live-exec__iter-item__header">
              <span className={`live-exec__score-chip ${d.verdict}`}>iter {d.iter}: {d.score}</span>
              <span style={{ color: 'var(--text-secondary)', flex: 1, textAlign: 'right' }}>{d.verdict}</span>
              {d.judgeScore != null && <span className="live-exec__score-chip pass" style={{ outline: '1px dashed currentColor' }}>judge {d.judgeScore}</span>}
            </div>
            {d.notes && (
              <div className="live-exec__iter-item__notes">{d.notes}</div>
            )}
            {d.judgeNotes && (
              <div className="live-exec__iter-item__notes" data-role="judge">
                <strong>Judge:</strong> {d.judgeNotes}
              </div>
            )}
          </div>
        ))}
        {deduped.length === 0 && (
          <div style={{ color: 'var(--text-tertiary)', fontSize: 12 }}>No deliverables yet.</div>
        )}
      </div>

      {snap.expectedFileChanges != null && (
        <div className={`live-exec__file-budget`}>
          <div className="live-exec__file-budget__label">file budget</div>
          <div className="live-exec__file-budget__value">≤ {snap.expectedFileChanges} files</div>
        </div>
      )}
    </div>
  );
}

// Phase 24.11 — Comments tab on the Live Execution right rail.
// Lists the work item's PR comments needing response, newest first. When a
// `comment-response` queue row exists for this work item, a banner at the
// top says "Agent is handling these (queue position: N)".
function CommentsTab({ workItemId }: { workItemId: string | null }): JSX.Element {
  const [comments, setComments] = useState<Array<{
    id: number;
    prId: string;
    body: string;
    filePath?: string | null;
    line?: number | null;
    authorId?: string | null;
    updatedAt: string;
    requiresAttention: boolean;
  }>>([]);
  const [prId, setPrId] = useState<string | null>(null);
  const [queuePos, setQueuePos] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!workItemId) return;
    let cancelled = false;
    (async () => {
      try {
        // Resolve the PR linked to this work item via /api/state.
        const state = await forgeApi.getState();
        const linkedPr = state.prs.find((p: { id?: string }) => (p as { workItemId?: string | null }).workItemId === workItemId)
          ?? state.prs.find((p) => p.workstream && p.workstream.endsWith(workItemId));
        if (cancelled) return;
        if (!linkedPr) { setComments([]); setPrId(null); return; }
        setPrId(linkedPr.id);
        const resp = await forgeApi.listPrComments(linkedPr.id, { requiresAttention: true, take: 100 });
        if (cancelled) return;
        setComments(resp.items.map((c) => ({
          id: c.id,
          prId: c.prId,
          body: c.body,
          filePath: c.filePath,
          line: c.line,
          authorId: c.authorId,
          updatedAt: c.updatedAt,
          requiresAttention: c.requiresAttention,
        })));

        // Resolve queue position (best-effort).
        const queue = await forgeApi.listQueue();
        const my = queue.find((q: { workItemId?: string; kind?: string; state?: string }) =>
          q.workItemId === workItemId && q.kind === 'comment-response' && q.state !== 'completed' && q.state !== 'cancelled');
        if (cancelled) return;
        setQueuePos(my ? queue.indexOf(my) + 1 : null);
      } catch (e: unknown) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load');
      }
    })();
    return () => { cancelled = true; };
  }, [workItemId]);

  return (
    <div className="live-exec__pane live-exec__pane--comments">
      {error && <div style={{ color: 'var(--danger)', fontSize: 11.5 }}>Error: {error}</div>}
      {queuePos !== null && (
        <div
          style={{
            background: 'var(--info-bg)',
            border: '1px solid var(--info-border)',
            color: 'var(--info)',
            padding: '6px 10px',
            borderRadius: 'var(--radius-sm)',
            fontSize: 11.5,
            marginBottom: 8,
          }}
        >
          Agent is handling these (queue position: {queuePos})
        </div>
      )}
      {!prId ? (
        <div style={{ color: 'var(--text-tertiary)', fontSize: 11.5, fontStyle: 'italic' }}>
          No PR linked to this work item yet.
        </div>
      ) : comments.length === 0 ? (
        <div style={{ color: 'var(--text-tertiary)', fontSize: 11.5, fontStyle: 'italic' }}>
          No comments need a response.
        </div>
      ) : (
        <div data-testid="live-exec-comments">
          {[...comments]
            .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
            .map((c) => (
              <div
                key={c.id}
                style={{
                  borderTop: '1px solid var(--border-subtle)',
                  padding: '6px 0',
                  fontSize: 11.5,
                }}
              >
                <div style={{ display: 'flex', gap: 6, alignItems: 'center', marginBottom: 2 }}>
                  {c.filePath && (
                    <span style={{ fontFamily: 'var(--font-mono)', color: 'var(--text-tertiary)', fontSize: 10 }}>
                      {c.filePath}{c.line != null ? `:${c.line}` : ''}
                    </span>
                  )}
                  <span style={{ color: 'var(--text-secondary)', fontSize: 10 }}>{c.authorId}</span>
                </div>
                <div style={{ color: 'var(--text-primary)', whiteSpace: 'pre-wrap' }}>
                  {c.body.length > 120 ? `${c.body.slice(0, 120)}…` : c.body}
                </div>
              </div>
            ))}
        </div>
      )}
    </div>
  );
}

// Phase 27 Q27.11 — Builds tab on the Live Execution right rail.
// Lists the work item's PR builds newest-first. When a `build-fix` queue row
// exists for this work item, a banner at the top says
// "Agent is fixing this (queue position: N · iter M of 5)".
function BuildsTab({ workItemId }: { workItemId: string | null }): JSX.Element {
  const [builds, setBuilds] = useState<import('@/types').Build[]>([]);
  const [prId, setPrId] = useState<string | null>(null);
  const [queuePos, setQueuePos] = useState<number | null>(null);
  const [activeIter, setActiveIter] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [expandedId, setExpandedId] = useState<number | null>(null);
  const [details, setDetails] = useState<Record<number, {
    stages: import('@/types').BuildStage[];
    failurePoints: import('@/types').BuildFailurePoint[];
  }>>({});

  useEffect(() => {
    if (!workItemId) return;
    let cancelled = false;
    (async () => {
      try {
        const state = await forgeApi.getState();
        const linkedPr = state.prs.find((p: { id?: string }) => (p as { workItemId?: string | null }).workItemId === workItemId)
          ?? state.prs.find((p) => p.workstream && p.workstream.endsWith(workItemId));
        if (cancelled) return;
        if (!linkedPr) { setBuilds([]); setPrId(null); return; }
        setPrId(linkedPr.id);
        const resp = await forgeApi.listPrBuilds(linkedPr.id, { limit: 50 });
        if (cancelled) return;
        setBuilds(resp.items);

        const queue = await forgeApi.listQueue();
        const my = queue.find((q: { workItemId?: string; kind?: string; state?: string }) =>
          q.workItemId === workItemId && q.kind === 'build-fix' && q.state !== 'completed' && q.state !== 'cancelled');
        if (cancelled) return;
        setQueuePos(my ? queue.indexOf(my) + 1 : null);
      } catch (e: unknown) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load');
      }
    })();
    return () => { cancelled = true; };
  }, [workItemId]);

  // Subscribe to live iteration progress for the build-fix banner.
  useEffect(() => {
    const off = useTaskLiveStore.subscribe((s) => {
      if (!workItemId) return;
      const entry = s.entries[workItemId];
      if (!entry || !entry.snapshot) return;
      setActiveIter(entry.snapshot.currentIteration ?? null);
    });
    return off;
  }, [workItemId]);

  async function toggleExpand(id: number): Promise<void> {
    if (expandedId === id) { setExpandedId(null); return; }
    setExpandedId(id);
    if (!details[id]) {
      try {
        const d = await forgeApi.getBuild(id);
        setDetails((prev) => ({ ...prev, [id]: { stages: d.stages, failurePoints: d.failurePoints } }));
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Failed to load build');
      }
    }
  }

  return (
    <div className="live-exec__pane live-exec__pane--builds">
      {error && <div style={{ color: 'var(--danger)', fontSize: 11.5 }}>Error: {error}</div>}
      {queuePos !== null && (
        <div
          style={{
            background: 'var(--info-bg)',
            border: '1px solid var(--info-border)',
            color: 'var(--info)',
            padding: '6px 10px',
            borderRadius: 'var(--radius-sm)',
            fontSize: 11.5,
            marginBottom: 8,
          }}
          data-testid="build-fix-banner"
        >
          Agent is fixing this (queue position: {queuePos}{activeIter !== null ? ` · iter ${activeIter} of 5` : ''})
        </div>
      )}
      {!prId ? (
        <div style={{ color: 'var(--text-tertiary)', fontSize: 11.5, fontStyle: 'italic' }}>
          No PR linked to this work item yet.
        </div>
      ) : builds.length === 0 ? (
        <div style={{ color: 'var(--text-tertiary)', fontSize: 11.5, fontStyle: 'italic' }}>
          No builds for this PR yet.
        </div>
      ) : (
        <div data-testid="live-exec-builds">
          {builds.map((b) => {
            const det = details[b.id];
            const expanded = expandedId === b.id;
            return (
              <div
                key={b.id}
                style={{
                  borderTop: '1px solid var(--border-subtle)',
                  padding: '6px 0',
                  fontSize: 11.5,
                }}
              >
                <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                  <span
                    title={b.state}
                    style={{
                      display: 'inline-block',
                      padding: '1px 6px',
                      borderRadius: 'var(--radius-sm)',
                      fontSize: 10,
                      fontWeight: 600,
                      background: b.state === 'failed' ? 'var(--danger-bg)' :
                                  b.state === 'succeeded' ? 'var(--success-bg)' :
                                  b.state === 'running' ? 'var(--info-bg)' : 'var(--surface-subtle)',
                      color: b.state === 'failed' ? 'var(--danger)' :
                             b.state === 'succeeded' ? 'var(--success)' :
                             b.state === 'running' ? 'var(--info)' : 'var(--text-secondary)',
                    }}
                  >
                    #{b.id} {b.state}
                  </span>
                  <span style={{ flex: 1, color: 'var(--text-primary)', cursor: 'pointer' }} onClick={() => toggleExpand(b.id)}>
                    {b.pipelineName ?? b.providerBuildId}
                  </span>
                  {b.webUrl && (
                    <a
                      href={b.webUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                      style={{ color: 'var(--info)', fontSize: 10 }}
                    >
                      Open in {b.provider}
                    </a>
                  )}
                </div>
                <div style={{ display: 'flex', gap: 6, color: 'var(--text-tertiary)', fontSize: 10, marginTop: 2 }}>
                  {b.startedAt && <span>started {new Date(b.startedAt).toLocaleString()}</span>}
                  {b.triggeringCommit && <span>commit {b.triggeringCommit.slice(0, 7)}</span>}
                </div>
                {expanded && det && (
                  <div style={{ marginTop: 6, paddingLeft: 8, borderLeft: '2px solid var(--border-subtle)' }}>
                    {det.stages.map((s) => (
                      <div key={s.id} style={{ marginBottom: 6 }}>
                        <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                          <span
                            style={{
                              display: 'inline-block',
                              padding: '1px 4px',
                              borderRadius: 'var(--radius-sm)',
                              fontSize: 9,
                              background: s.result === 'failed' || s.result === 'failure'
                                ? 'var(--danger-bg)' : 'var(--surface-subtle)',
                              color: s.result === 'failed' || s.result === 'failure'
                                ? 'var(--danger)' : 'var(--text-secondary)',
                            }}
                          >
                            {s.state}
                          </span>
                          <span>{s.stageName}</span>
                        </div>
                        {det.failurePoints.filter((f) => f.stageId === s.id).map((f) => (
                          <div key={f.id} style={{ marginLeft: 12, fontSize: 10, color: 'var(--text-secondary)' }}>
                            <span style={{ fontFamily: 'var(--font-mono)' }}>
                              [{f.kind}] {f.filePath}{f.line != null ? `:${f.line}` : ''}
                            </span>
                            <div style={{ marginLeft: 4 }}>{f.message}</div>
                          </div>
                        ))}
                      </div>
                    ))}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

