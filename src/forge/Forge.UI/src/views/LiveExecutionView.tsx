// Phase 16.10 — Live Execution view.
//
// Header strip + transcript center column + right-rail with three tabs
// (Worktree, Tokens, Progress). Reads the snapshot from
// /api/work-items/{id}/execution and tails the SignalR `task.{id}` group
// for live updates via useTaskLive (Phase 16.13).
import { useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useTaskLive, type TaskTranscriptEvent, type WorktreeSnapshot } from '@/realtime/useTaskLiveStore';

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

  if (!workItemId) {
    return <div className="live-exec__empty">Pick a work item to watch.</div>;
  }

  const snapshot = entry?.snapshot ?? null;
  const transcript = entry?.transcript ?? [];
  const worktree = entry?.worktree ?? snapshot?.worktree ?? null;

  const filtered = useMemo(() => filterEvents(transcript, filter), [transcript, filter]);

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
            crewId={snapshot?.generatorCrewId}
            agentId={snapshot?.generatorAgentId}
          />
          <span className="live-exec__pair-conn">↔ paired</span>
          <PairSide
            role="evaluator"
            crewId={snapshot?.evaluatorCrewId}
            agentId={snapshot?.evaluatorAgentId}
          />
        </div>
        <div className="live-exec__iter">
          <strong>iter {snapshot?.currentIteration ?? 0}</strong> of 5
        </div>
        <div className="live-exec__budget">
          <BudgetBar
            label="task"
            spend={snapshot?.totalCostUsd ?? 0}
            cap={snapshot?.maxTaskBudgetUsd ?? null}
          />
        </div>
      </header>

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
        </nav>
        <div className="live-exec__transcript">
          {filtered.length === 0 ? (
            <div className="live-exec__empty">
              {entry?.loading ? 'Loading…' : 'No events yet.'}
              {snapshot?.autoDispatchedAt == null && !entry?.loading && (
                <div className="live-exec__empty-cta">Waiting for the lead to delegate…</div>
              )}
            </div>
          ) : (
            filtered.map((evt) => <TranscriptLine key={`${evt.id}:${evt.at}`} evt={evt} />)
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

function PairSide(props: { role: 'generator' | 'evaluator'; crewId?: string | null; agentId?: string | null }): JSX.Element {
  const { role, crewId, agentId } = props;
  return (
    <div className="live-exec__pair-side" data-role={role}>
      <span style={{ textTransform: 'capitalize', color: 'var(--text-tertiary)' }}>{role}</span>
      <strong>{crewId ?? '—'}</strong>
      {agentId ? <span style={{ color: 'var(--text-tertiary)', fontFamily: 'monospace' }}>{agentId}</span> : null}
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

function TranscriptLine(props: { evt: TaskTranscriptEvent }): JSX.Element {
  const { evt } = props;
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
  const side = evt.crewId
    ? null // pair side comes from the crew association — not directly known here
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

function renderPayload(evt: TaskTranscriptEvent): string {
  const p = evt.payload as Record<string, unknown> | null;
  if (!p) return '';
  if (typeof p === 'string') return p;
  if ('delta' in p && typeof p.delta === 'string') return p.delta;
  if ('text'  in p && typeof p.text  === 'string') return p.text;
  if (evt.kind === 'task.delegated') {
    return `→ crew ${(p.generatorCrewId as string) ?? '?'}${p.evaluatorCrewId ? ` ↔ ${p.evaluatorCrewId}` : ''} (lead: ${(p.leadId as string) ?? '?'})`;
  }
  if (evt.kind === 'task.tool-call' && typeof p.tool === 'string') {
    return `tool: ${p.tool}`;
  }
  if (evt.kind === 'task.budget-warning') {
    return `over budget: ${(p.actual as number) ?? '?'} / ${(p.expected as number) ?? '?'}`;
  }
  // Fallback — compact JSON.
  try { return JSON.stringify(p); } catch { return ''; }
}

function filterEvents(events: TaskTranscriptEvent[], f: FilterKey): TaskTranscriptEvent[] {
  if (f === 'all') return events;
  if (f === 'commits') return events.filter((e) => e.kind === 'task.commit' || e.kind === 'task.pr-opened');
  if (f === 'tools')   return events.filter((e) => e.kind === 'task.tool-call' || e.kind === 'task.tool-result');
  if (f === 'generator' || f === 'evaluator') {
    // We don't (yet) flag side directly — heuristic: token / tool events are
    // generator unless the agentId matches the evaluator slot. Iteration
    // boundaries always render.
    return events.filter((e) => e.kind.startsWith('task.iteration') || e.crewId !== null);
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
  return (
    <div>
      <div className="live-exec__iter-list">
        {snap.deliverables.map((d) => (
          <div key={d.id} className="live-exec__iter-item">
            <span className={`live-exec__score-chip ${d.verdict}`}>iter {d.iter}: {d.score}</span>
            <span style={{ color: 'var(--text-secondary)', flex: 1, textAlign: 'right' }}>{d.verdict}</span>
            {d.judgeScore != null && <span className="live-exec__score-chip pass" style={{ outline: '1px dashed currentColor' }}>judge {d.judgeScore}</span>}
          </div>
        ))}
        {snap.deliverables.length === 0 && (
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
