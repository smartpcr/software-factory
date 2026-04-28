// Phase 16.10 — Live Execution view.
//
// Header strip + transcript center column + right-rail with three tabs
// (Worktree, Tokens, Progress). Reads the snapshot from
// /api/work-items/{id}/execution and tails the SignalR `task.{id}` group
// for live updates via useTaskLive (Phase 16.13).
import { useCallback, useMemo, useState } from 'react';
import { Link, useParams, useNavigate } from 'react-router-dom';
import { useTaskLive, useTaskLiveStore, type TaskTranscriptEvent, type WorktreeSnapshot } from '@/realtime/useTaskLiveStore';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import { avatarFor, agentTooltip } from '@/agentAvatar';

type FilterKey = 'all' | 'generator' | 'evaluator' | 'tools' | 'commits';

const FILTERS: Array<{ key: FilterKey; label: string }> = [
  { key: 'all',       label: 'All' },
  { key: 'generator', label: 'Generator' },
  { key: 'evaluator', label: 'Evaluator' },
  { key: 'tools',     label: 'Tools' },
  { key: 'commits',   label: 'Commits' },
];

type RailTab = 'worktree' | 'tokens' | 'progress';

export function LiveExecutionView(): JSX.Element {
  const params = useParams<{ id: string }>();
  const workItemId = params.id ?? null;
  const entry = useTaskLive(workItemId);
  const [filter, setFilter] = useState<FilterKey>('all');
  const [tab, setTab]       = useState<RailTab>('worktree');
  const [busy, setBusy]     = useState(false);
  const navigate = useNavigate();

  const [copied, setCopied] = useState(false);
  const [promptsCollapsed, setPromptsCollapsed] = useState(false);
  const [promptRole, setPromptRole] = useState<'generator' | 'evaluator'>('generator');
  const [maxIter, setMaxIter] = useState<number | null>(null);

  // Sync local maxIter from server snapshot on first load.
  const effectiveMaxIter = maxIter ?? snapshot?.maxIterations ?? 5;

  const snapshot = entry?.snapshot ?? null;
  const transcript = entry?.transcript ?? [];
  const worktree = entry?.worktree ?? snapshot?.worktree ?? null;
  const genCrewId = snapshot?.generatorCrewId ?? null;
  const evalCrewId = snapshot?.evaluatorCrewId ?? null;
  const genAgentId = snapshot?.generatorAgentId ?? null;
  const evalAgentId = snapshot?.evaluatorAgentId ?? null;
  const filtered = useMemo(() => filterEvents(transcript, filter, genCrewId, evalCrewId, genAgentId, evalAgentId), [transcript, filter, genCrewId, evalCrewId, genAgentId, evalAgentId]);

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
      await forgeApi.requeueWorkItem(snapshot.generatorCrewId, workItemId);
      useToastStore.getState().show('Re-queued successfully', 'success');
    } catch (e) {
      useToastStore.getState().show(e instanceof Error ? e.message : 'Failed', 'danger');
    } finally {
      setBusy(false);
    }
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
      // 2. Clear transcript on server.
      await forgeApi.clearWorkItemTranscript(workItemId);
      // 3. Re-enqueue to the same crew.
      await forgeApi.requeueWorkItem(crewId, workItemId);
      // 4. Clear local store so the view reloads fresh.
      const store = useTaskLiveStore.getState();
      store.entries[workItemId] = {
        snapshot: null, transcript: [], worktree: null,
        loading: false, lastEventId: 0,
        refCount: store.entries[workItemId]?.refCount ?? 1,
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
            selected={promptRole === 'generator'}
            onClick={() => setPromptRole('generator')}
          />
          <span className="live-exec__pair-conn">↔ paired</span>
          <PairSide
            role="evaluator"
            crewName={snapshot?.evaluatorCrewName}
            agentName={snapshot?.evaluatorAgentName ?? snapshot?.evaluatorAgentId}
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
        </div>
        <div style={{ display: 'flex', gap: 6, marginLeft: 'auto' }}>
          <button
            className="btn btn-primary"
            style={{ fontSize: 11, padding: '4px 10px' }}
            disabled={busy}
            onClick={() => void forceRestart()}
            title="Cancel active run and restart the conversation from scratch"
          >
            ⟳ Restart
          </button>
          <button
            className="btn btn-secondary"
            style={{ fontSize: 11, padding: '4px 10px' }}
            disabled={busy}
            onClick={() => void requeue()}
            title="Cancel current queue entry and re-enqueue to the same crew"
          >
            ↻ Re-queue
          </button>
          <button
            className="btn btn-secondary"
            style={{ fontSize: 11, padding: '4px 10px', color: 'var(--danger, #ef4444)' }}
            disabled={busy}
            onClick={() => void cancelAndRequeue()}
            title="Cancel queue entry and clear agent assignment — returns to unassigned state"
          >
            ✕ Unassign
          </button>
        </div>
      </header>

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
        <div className="live-exec__transcript" style={{ userSelect: 'text' }}>
          {filtered.length === 0 ? (
            <div className="live-exec__empty">
              {entry?.loading ? 'Loading…' : 'No events yet.'}
              {snapshot?.autoDispatchedAt == null && !entry?.loading && (
                <div className="live-exec__empty-cta">Waiting for the lead to delegate…</div>
              )}
            </div>
          ) : (
            filtered.map((evt) => <TranscriptLine key={`${evt.id}:${evt.at}`} evt={evt} generatorCrewId={genCrewId} evaluatorCrewId={evalCrewId} />)
          )}
        </div>
      </main>

      <aside className="live-exec__rail">
        <div className="live-exec__tabs">
          <button type="button" className={`live-exec__tab${tab === 'worktree' ? ' active' : ''}`} onClick={() => setTab('worktree')}>Worktree</button>
          <button type="button" className={`live-exec__tab${tab === 'tokens'   ? ' active' : ''}`} onClick={() => setTab('tokens')}>Tokens</button>
          <button type="button" className={`live-exec__tab${tab === 'progress' ? ' active' : ''}`} onClick={() => setTab('progress')}>Progress</button>
        </div>
        <div className="live-exec__tab-body">
          {tab === 'worktree' && <WorktreeTab snap={worktree} transcript={transcript} />}
          {tab === 'tokens'   && <TokensTab snap={snapshot} />}
          {tab === 'progress' && <ProgressTab snap={snapshot} />}
        </div>
      </aside>
    </div>
  );
}

// ── Sub-components ────────────────────────────────────────────────────────

function PairSide(props: {
  role: 'generator' | 'evaluator';
  crewName?: string | null;
  agentName?: string | null;
  selected?: boolean;
  onClick?: () => void;
}): JSX.Element {
  const { role, crewName, agentName, selected, onClick } = props;
  const portrait = agentName ? avatarFor(agentName, agentName) : null;
  const tip = agentName ? agentTooltip(agentName, agentName, role) : '';
  return (
    <div
      className="live-exec__pair-side"
      data-role={role}
      onClick={onClick}
      role={onClick ? 'button' : undefined}
      tabIndex={onClick ? 0 : undefined}
      style={{
        cursor: onClick ? 'pointer' : undefined,
        borderBottom: selected ? '2px solid var(--accent, #3b82f6)' : '2px solid transparent',
        paddingBottom: 2,
      }}
    >
      {portrait ? (
        <img
          src={portrait}
          alt={agentName ?? role}
          className={`live-exec__pair-avatar ${role === 'generator' ? 'eng' : 'rev'}`}
          title={tip}
          onError={(e) => { (e.target as HTMLImageElement).style.display = 'none'; }}
        />
      ) : (
        <span className="live-exec__pair-mono" title={tip}>
          {(role === 'generator' ? 'G' : 'E')}
        </span>
      )}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 1, minWidth: 0 }}>
        <strong style={{ whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {agentName ?? '—'}
        </strong>
        {crewName && (
          <span style={{ fontSize: 10, color: 'var(--text-tertiary)', whiteSpace: 'nowrap' }}>
            {crewName}
          </span>
        )}
      </div>
    </div>
  );
}

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

function TranscriptLine(props: { evt: TaskTranscriptEvent; generatorCrewId?: string | null; evaluatorCrewId?: string | null }): JSX.Element {
  const { evt, generatorCrewId, evaluatorCrewId } = props;
  if (evt.kind === 'task.iteration.scored' || evt.kind === 'task.iteration.passed' || evt.kind === 'task.iteration.rejected' || evt.kind === 'task.iteration.iterated' || evt.kind === 'task.iteration.stalled') {
    const payload = evt.payload as { iter?: number; score?: number; verdict?: string };
    const verdict = payload.verdict ?? evt.kind.replace('task.iteration.', '');
    return (
      <div className="live-exec__iter-rule">
        <span>iter {payload.iter ?? '?'}</span>
        <span className={`live-exec__score-chip ${verdict}`}>
          {payload.score ?? 0} · {verdict}
        </span>
      </div>
    );
  }
  if (evt.kind === 'task.prompt') {
    return <PromptLine evt={evt} />;
  }
  const side = evt.crewId === generatorCrewId ? 'generator'
             : evt.crewId === evaluatorCrewId ? 'evaluator'
             : null;
  const meta = new Date(evt.at).toLocaleTimeString();
  return (
    <div className="live-exec__line" data-kind={evt.kind} data-side={side ?? ''}>
      <span className="live-exec__line-meta">{meta}</span>
      <span className="live-exec__line-kind">{evt.kind.replace('task.', '')}</span>
      <span className="live-exec__line-text">{renderPayload(evt)}</span>
    </div>
  );
}

function PromptLine(props: { evt: TaskTranscriptEvent }): JSX.Element {
  const { evt } = props;
  const p = evt.payload as Record<string, unknown> | null;
  const model = (p?.model as string) ?? '';
  const backend = (p?.backend as string) ?? '';
  const mode = (p?.invokeMode as string) ?? '';
  const meta = new Date(evt.at).toLocaleTimeString();

  return (
    <div className="live-exec__line" data-kind="task.prompt">
      <span className="live-exec__line-meta">{meta}</span>
      <span className="live-exec__line-kind">prompt</span>
      <span className="live-exec__line-text">
        [{backend}/{mode} · {model}] — see Prompts section above
      </span>
    </div>
  );
}

function renderPayload(evt: TaskTranscriptEvent): string {
  const p = evt.payload as Record<string, unknown> | null;
  if (!p) return '';
  if (typeof p === 'string') return p;
  if ('delta' in p && typeof p.delta === 'string') return p.delta;
  if ('text'  in p && typeof p.text  === 'string') return p.text;
  if (evt.kind === 'task.delegated') {
    return `→ crew ${(p.generatorCrewId as string) ?? '?'}${p.evaluatorCrewId ? ` ↔ ${p.evaluatorCrewId}` : ''} (lead: ${(p.leadId as string) ?? '?'})`;
  }
  if (evt.kind === 'task.prompt') {
    const model = (p.model as string) ?? '';
    const backend = (p.backend as string) ?? '';
    const mode = (p.invokeMode as string) ?? '';
    return `[${backend}/${mode} · ${model}]`;
  }
  if (evt.kind === 'task.tool-call') {
    const name = (p.toolName as string) ?? (p.tool as string) ?? '?';
    const payload = typeof p.toolPayload === 'string' ? p.toolPayload : '';
    // Show tool name + truncated args for context.
    return payload ? `${name}: ${payload.substring(0, 120)}${payload.length > 120 ? '…' : ''}` : name;
  }
  if (evt.kind === 'task.tool-result') {
    const name = (p.toolName as string) ?? '';
    const payload = typeof p.toolPayload === 'string' ? p.toolPayload : '';
    const err = typeof p.error === 'string' ? p.error : '';
    if (err) return `✗ ${name || 'tool'}: ${err.substring(0, 120)}`;
    return payload ? `✓ ${name || 'result'}: ${payload.substring(0, 120)}${payload.length > 120 ? '…' : ''}` : `✓ ${name || 'done'}`;
  }
  if (evt.kind === 'task.budget-warning') {
    return `over budget: ${(p.actual as number) ?? '?'} / ${(p.expected as number) ?? '?'}`;
  }
  if (evt.kind === 'task.completed') return '✓ completed';
  if (evt.kind === 'task.failed') {
    const err = typeof p.error === 'string' ? p.error : '';
    return err ? `✗ ${err.substring(0, 150)}` : '✗ failed';
  }
  // Fallback — compact JSON.
  try { return JSON.stringify(p); } catch { return ''; }
}

function filterEvents(
  events: TaskTranscriptEvent[],
  f: FilterKey,
  genCrewId?: string | null,
  evalCrewId?: string | null,
  genAgentId?: string | null,
  evalAgentId?: string | null,
): TaskTranscriptEvent[] {
  if (f === 'all') return events;
  if (f === 'commits') return events.filter((e) => e.kind === 'task.commit' || e.kind === 'task.pr-opened');
  if (f === 'tools')   return events.filter((e) => e.kind === 'task.tool-call' || e.kind === 'task.tool-result');

  // When generator and evaluator share the same crewId, fall back to
  // event kind heuristics to split the transcript.
  const sameCrews = genCrewId && evalCrewId && genCrewId === evalCrewId;

  const EVAL_KINDS = new Set([
    'task.iteration.scored', 'task.iteration.passed', 'task.iteration.rejected',
    'task.iteration.iterated', 'task.iteration.stalled',
  ]);

  if (f === 'generator') {
    return events.filter((e) => {
      if (e.kind.startsWith('task.iteration')) return true; // boundaries shown in both
      if (sameCrews) {
        // Exclude evaluator-specific events; include everything else.
        if (e.agentId === evalAgentId && e.agentId !== genAgentId) return false;
        if (e.crewId && e.crewId !== genCrewId) return false;
        return !EVAL_KINDS.has(e.kind);
      }
      return (genCrewId && e.crewId === genCrewId) || (!e.crewId && !evalCrewId);
    });
  }
  if (f === 'evaluator') {
    return events.filter((e) => {
      if (e.kind.startsWith('task.iteration')) return true; // boundaries shown in both
      if (sameCrews) {
        // Only evaluator-specific events: scoring verdicts + events
        // tagged with the evaluator agent.
        if (EVAL_KINDS.has(e.kind)) return true;
        if (e.agentId === evalAgentId && e.agentId !== genAgentId) return true;
        // Also include events from the paired (different) crew.
        return false;
      }
      return evalCrewId && e.crewId === evalCrewId;
    });
  }
  return events;
}

function WorktreeTab(props: { snap: WorktreeSnapshot | null; transcript: TaskTranscriptEvent[] }): JSX.Element {
  const { snap, transcript } = props;
  if (!snap) {
    return <div className="live-exec__empty">No worktree activity yet.</div>;
  }
  // Pull the most recent path arrays from the latest worktree-tick payload.
  const lastTick = [...transcript].reverse().find((e) => e.kind === 'task.worktree-tick');
  const paths = (lastTick?.payload as { paths?: { added?: string[]; modified?: string[]; deleted?: string[] } } | null)?.paths ?? null;
  return (
    <div>
      <div className="live-exec__wt-summary">
        <span className="live-exec__wt-pill added">+{snap.added}</span>
        <span className="live-exec__wt-pill modified">~{snap.modified}</span>
        <span className="live-exec__wt-pill deleted">−{snap.deleted}</span>
        {snap.ahead > 0  && <span className="live-exec__wt-pill ahead">↑{snap.ahead}</span>}
        {snap.behind > 0 && <span className="live-exec__wt-pill ahead">↓{snap.behind}</span>}
      </div>

      {snap.headSha && (
        <div className="live-exec__wt-head">
          <div className="live-exec__wt-sha">{snap.headSha.slice(0, 8)}</div>
          <div className="live-exec__wt-msg">{snap.headMessage ?? ''}</div>
          <div className="live-exec__wt-author">{snap.headAuthor ?? ''} · {snap.headAt ? new Date(snap.headAt).toLocaleString() : ''}</div>
        </div>
      )}

      <div className="live-exec__file-list">
        {paths?.added?.map((p) => <div key={`a${p}`} className="live-exec__file-row added">{p}</div>)}
        {paths?.modified?.map((p) => <div key={`m${p}`} className="live-exec__file-row modified">{p}</div>)}
        {paths?.deleted?.map((p) => <div key={`d${p}`} className="live-exec__file-row deleted">{p}</div>)}
      </div>
      {snap.truncated && <div style={{ fontSize: 11, color: 'var(--text-tertiary)', marginTop: 6 }}>list truncated at 200 paths.</div>}
    </div>
  );
}

function TokensTab(props: { snap: TaskTranscriptEvent extends never ? never : import('@/realtime/useTaskLiveStore').TaskExecutionSnapshot | null }): JSX.Element {
  const { snap } = props;
  if (!snap) return <div className="live-exec__empty">No invocations yet.</div>;
  const total = snap.generatorCostUsd + snap.evaluatorCostUsd;
  const genShare  = total > 0 ? (snap.generatorCostUsd / total) * 100 : 0;
  const evalShare = total > 0 ? (snap.evaluatorCostUsd / total) * 100 : 0;
  return (
    <div>
      <div className="live-exec__cost-row"><span>Generator</span><strong>${snap.generatorCostUsd.toFixed(4)}</strong></div>
      <div className="live-exec__cost-row"><span>Evaluator</span><strong>${snap.evaluatorCostUsd.toFixed(4)}</strong></div>
      <div className="live-exec__tokens-bar" aria-hidden="true">
        <span className="gen"  style={{ width: `${genShare}%` }} />
        <span className="eval" style={{ width: `${evalShare}%` }} />
      </div>
      <div className="live-exec__cost-row"><span>Total</span><strong>${total.toFixed(4)}</strong></div>
      <div style={{ marginTop: 12, fontSize: 12, color: 'var(--text-tertiary)' }}>
        in: {snap.generatorInputTokens.toLocaleString()} / {snap.evaluatorInputTokens.toLocaleString()} ·{' '}
        out: {snap.generatorOutputTokens.toLocaleString()} / {snap.evaluatorOutputTokens.toLocaleString()}
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
            <span className={`live-exec__score-chip ${d.verdict}`}>iter {d.iter}: {d.score}</span>
            <span style={{ color: 'var(--text-secondary)', flex: 1, textAlign: 'right' }}>{d.verdict}</span>
            {d.judgeScore != null && <span className="live-exec__score-chip pass" style={{ outline: '1px dashed currentColor' }}>judge {d.judgeScore}</span>}
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
