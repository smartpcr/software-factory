import { useEffect, useMemo, useRef, useState } from 'react';
import { forgeApi, type StorySpawnArtifacts, type StorySpawnDetail, type StorySpawnWorkstreamSummary } from '@/api/forgeApi';
import type { SwarmMode } from './SwarmPipelineStrip';
import { avatarFor } from '@/agentAvatar';
import {
  useWorkstreamsStore,
  PHASE_STATUS_FILTER_KEYS,
  type PhaseStatusFilterKey,
} from '@/state/useWorkstreamsStore';
import { isQaE2e, qaE2eByImplId } from './qaE2eSelector';
import { computeStageNumbers } from './stageNumbering';
import { dismiss, isDismissed, loadDismissals, saveDismissals, type Dismissals } from './attentionDismissals';

export type SelectionKind = 'overview' | 'phase' | 'doc' | 'gate';
export interface Selection {
  kind: SelectionKind;
  id: string | null;
}

interface Props {
  mode: SwarmMode;
  detail: StorySpawnDetail;
  artifacts: StorySpawnArtifacts | null;
  selection: Selection;
  onSelect: (sel: Selection) => void;
  collapsed: boolean;
  onToggleCollapsed: () => void;
}

function CollapseBtn({ collapsed, onToggle }: { collapsed: boolean; onToggle: () => void }) {
  return (
    <button
      type="button"
      className="panel-collapse-btn"
      onClick={(e) => { e.stopPropagation(); onToggle(); }}
      title={collapsed ? 'Expand master panel' : 'Collapse master panel'}
      aria-label={collapsed ? 'Expand master panel' : 'Collapse master panel'}
    >
      {collapsed ? '›' : '‹'}
    </button>
  );
}

/**
 * Phase 49 follow-up — operator can collapse the NEEDS ATTENTION block per
 * story. Persisted to localStorage so the choice survives a refresh /
 * cross-product switch. Stored as a boolean string under
 * `forge.attention.collapsed.{storyId}`; absent key means expanded (the
 * default — operators usually want to see the failures up front when a
 * spawn first lights up).
 */
function loadAttentionCollapsed(storyId: string): boolean {
  if (typeof window === 'undefined' || !storyId) return false;
  try {
    return window.localStorage.getItem(`forge.attention.collapsed.${storyId}`) === '1';
  } catch { return false; }
}

function persistAttentionCollapsed(storyId: string, collapsed: boolean) {
  if (typeof window === 'undefined' || !storyId) return;
  try {
    if (collapsed) {
      window.localStorage.setItem(`forge.attention.collapsed.${storyId}`, '1');
    } else {
      window.localStorage.removeItem(`forge.attention.collapsed.${storyId}`);
    }
  } catch { /* quota / insecure context — silent */ }
}

const STATUS_TO_PHASE_CLASS: Record<string, string> = {
  planning: 'queued',
  active:   'active',
  review:   'review',
  merging:  'active',
  complete: 'done',
  // Phase 42 `historic` shipped under a previous plan version (PR
  // merged); operator-facing lifecycle reads as `complete` with an
  // "obsolete" audit pill rendered separately below.
  historic: 'done',
  blocked:  'blocked',
  stalled:  'stalled',
  // Phase 63 operator-induced pause — gray neutral, mirrors `planning`.
  paused:   'queued',
};

const STATUS_TO_BADGE: Record<string, string> = {
  planning: 'blocked',
  active:   'active',
  review:   'review',
  merging:  'active',
  complete: 'done',
  historic: 'done',
  blocked:  'blocked',
  stalled:  'stalled',
  paused:   'blocked',
};

const STATUS_PROGRESS: Record<string, number> = {
  planning: 5,
  active:   45,
  review:   70,
  merging:  90,
  complete: 100,
  historic: 100,
  blocked:  0,
  stalled:  35,
  paused:   50,
};

export function SwarmMasterPanel(props: Props) {
  // Phase 50 — when the operator clicks a rect on the story-row strip we
  // navigate to `/workstreams/{storyId}?mode=…&sel=…&selId=…` which
  // restores both the mode and the per-card selection. Scroll the
  // matching card into the viewport centre so the operator doesn't have
  // to hunt for it inside a 21-row phase board. Retry on rAF for ~500 ms
  // because the cards only render after `detail` resolves; the
  // ref-guard ensures a refetch tick doesn't repeatedly re-scroll.
  useScrollSelectedIntoView(props.mode, props.selection, props.detail);

  if (props.mode === 'design') return <DesignMaster {...props} />;
  if (props.mode === 'release') return <ReleaseMaster {...props} />;
  return <ImplMaster {...props} />;
}

function useScrollSelectedIntoView(
  mode: SwarmMode,
  selection: Selection,
  detail: StorySpawnDetail | null,
) {
  const lastScrolled = useRef<string>('');
  useEffect(() => {
    if (selection.kind === 'overview' || !selection.id) {
      lastScrolled.current = '';
      return;
    }
    const key = `${selection.kind}:${selection.id}`;
    const trackKey = `${mode}|${key}`;
    if (lastScrolled.current === trackKey) return;

    let cancelled = false;
    let raf = 0;
    let attempts = 0;
    const tryScroll = () => {
      if (cancelled) return;
      // CSS.escape covers colons / slashes inside the selection id
      // (e.g. `phase:ws-failover-cluster-XRAFT:phase-1/stage-1`).
      const sel = `[data-sel-key="${CSS.escape(key)}"]`;
      const el = document.querySelector(sel);
      if (el && 'scrollIntoView' in el) {
        (el as HTMLElement).scrollIntoView({ behavior: 'smooth', block: 'center' });
        lastScrolled.current = trackKey;
        return;
      }
      // Card hasn't rendered yet (detail still loading, mode just
      // switched, …). Retry on the next frame, but cap so a missing
      // card doesn't spin the rAF forever.
      if (attempts++ < 30) raf = requestAnimationFrame(tryScroll);
    };
    raf = requestAnimationFrame(tryScroll);
    return () => { cancelled = true; if (raf) cancelAnimationFrame(raf); };
  }, [mode, selection.kind, selection.id, detail]);
}

/* ─── IMPL MODE ── workstreams as phase cards ────────────────────────── */

function ImplMaster({ detail, selection, onSelect, collapsed, onToggleCollapsed }: Props) {
  const ws = detail.workstreams;
  const stalled = ws.filter((w) => w.status === 'stalled');
  const blocked = ws.filter((w) => w.status === 'blocked');
  const allAttention = [...stalled, ...blocked];

  // Phase 49 follow-up — collapsible NEEDS ATTENTION block. Persisted per
  // story so the operator's choice survives reloads + product switches.
  const storyId = detail.spawn.storyId;

  // Operator can dismiss items one by one or all at once. A dismissed item
  // returns when its status / stall reason changes (see attentionDismissals).
  const [dismissals, setDismissals] = useState<Dismissals>(() => loadDismissals(storyId));
  useEffect(() => { setDismissals(loadDismissals(storyId)); }, [storyId]);
  const [showDismissed, setShowDismissed] = useState(false);
  const attention = showDismissed ? allAttention : allAttention.filter((w) => !isDismissed(w, dismissals));
  const dismissedCount = allAttention.length - allAttention.filter((w) => !isDismissed(w, dismissals)).length;
  const dismissItems = (items: typeof allAttention) => {
    const next = dismiss(items, allAttention, dismissals);
    setDismissals(next); saveDismissals(storyId, next); setShowDismissed(false);
  };
  const restoreDismissed = () => { setDismissals({}); saveDismissals(storyId, {}); setShowDismissed(false); };
  const [attentionCollapsed, setAttentionCollapsed] = useState<boolean>(
    () => loadAttentionCollapsed(storyId),
  );
  useEffect(() => { setAttentionCollapsed(loadAttentionCollapsed(storyId)); }, [storyId]);
  const toggleAttentionCollapsed = () => {
    setAttentionCollapsed((prev) => {
      const next = !prev;
      persistAttentionCollapsed(storyId, next);
      return next;
    });
  };

  // Phase 49 — canonical `phaseNum.stageNum` label for every workstream.
  // Drives both the phase-card title ("1.1 Cargo Workspace and Crate
  // Layout" instead of "PHASE 01") and the deps row's chip list. Shared
  // with the workstream DAG view (right pane, SwarmDetailPanel) via
  // `computeStageNumbers` so both panels stay numerically identical.
  // qa-e2e rows are labelled `{implNum}-e2e` and do NOT consume a stage
  // slot (docs/forge/e2e.md Stage 8.1).
  const stageNumOf = useMemo(() => computeStageNumbers(ws), [ws]);

  const wsNameById = useMemo(() => {
    const m = new Map<string, string>();
    for (const w of ws) m.set(w.id, w.name);
    return m;
  }, [ws]);

  // docs/forge/e2e.md Stage 8.1 — pair qa-e2e workstreams with their
  // impl siblings so the phase-board can stack the qa-e2e row directly
  // beneath the impl row (instead of rendering each qa-e2e as a
  // standalone top-level phase card). The selector is exported so a
  // vitest unit lock-in covers the pairing contract.
  const qaE2eByImpl = useMemo(() => qaE2eByImplId(ws), [ws]);

  // Phase-board status filter — operator narrows which phase rows render.
  // Counts shown on chips are derived from the unfiltered list so the
  // operator always sees the full picture even when the filter is on.
  //
  // The `complete` chip folds in `historic` rows: from a lifecycle
  // standpoint historic IS complete (PR merged + code shipped), only the
  // plan version it was scoped against has been superseded. Operator-
  // facing model treats them as the same bucket; the per-row "obsolete"
  // pill below keeps the audit dimension visible. Backend status string
  // stays `historic` so all `Status != WorkstreamStatuses.Historic`
  // filters in the orchestrator continue to scope live work correctly.
  const phaseStatusFilter = useWorkstreamsStore((s) => s.phaseStatusFilter);
  const togglePhaseStatusFilter = useWorkstreamsStore((s) => s.togglePhaseStatusFilter);
  const setPhaseStatusFilter = useWorkstreamsStore((s) => s.setPhaseStatusFilter);
  const wsCountByStatus = useMemo(() => {
    const counts: Record<string, number> = {};
    for (const w of ws) {
      const bucket = w.status === 'historic' ? 'complete' : w.status;
      counts[bucket] = (counts[bucket] ?? 0) + 1;
    }
    return counts;
  }, [ws]);
  const effectiveFilter = phaseStatusFilter.length === 0
    ? PHASE_STATUS_FILTER_KEYS
    : phaseStatusFilter;
  // Phase numbers are positional within the *unfiltered* workstream list
  // so the title stays consistent across filter changes (e.g., a row that
  // is "PHASE 12" under `all` must still read "PHASE 12" under `active`).
  //
  // docs/forge/e2e.md Stage 8.1 — qa-e2e rows are removed from the
  // top-level loop: they render stacked beneath their impl sibling
  // via the qaE2eByImpl selector below. Without this filter every
  // qa-e2e row would also appear as a standalone phase card, doubling
  // the visible card count for stages that have one.
  const visibleWs = useMemo(
    () => ws
      .map((w, originalIndex) => ({ w, originalIndex }))
      .filter(({ w }) => !isQaE2e(w))
      .filter(({ w }) => {
        const bucket = w.status === 'historic' ? 'complete' : w.status;
        return (effectiveFilter as readonly string[]).includes(bucket);
      })
      .sort((a, b) => {
        const sa = stageNumOf.get(a.w.id) ?? '999.999';
        const sb = stageNumOf.get(b.w.id) ?? '999.999';
        const [aMaj, aMin] = sa.split('.').map(Number);
        const [bMaj, bMin] = sb.split('.').map(Number);
        return aMaj !== bMaj ? aMaj - bMaj : aMin - bMin;
      }),
    [ws, effectiveFilter, stageNumOf],
  );
  const isAllSelected = phaseStatusFilter.length === 0
    || phaseStatusFilter.length === PHASE_STATUS_FILTER_KEYS.length;

  return (
    <section className="panel">
      <div className="panel-hd">
        <CollapseBtn collapsed={collapsed} onToggle={onToggleCollapsed} />
        <span>▌ PHASE BOARD · WORKSTREAMS</span>
        <span className="ix">
          {visibleWs.length === ws.length
            ? `${ws.length} ROWS · CLICK TO DRILL`
            : `${visibleWs.length} / ${ws.length} ROWS · FILTERED`}
        </span>
      </div>
      <div className="phase-status-filter" role="toolbar" aria-label="Filter phases by status">
        <button
          type="button"
          className={`phase-status-chip${isAllSelected ? ' active' : ''}`}
          onClick={() => setPhaseStatusFilter([...PHASE_STATUS_FILTER_KEYS])}
          title="Show all statuses"
        >
          <span className="lbl">all</span>
          <span className="count">{ws.length}</span>
        </button>
        {PHASE_STATUS_FILTER_KEYS.map((status) => {
          const count = wsCountByStatus[status] ?? 0;
          const checked = !isAllSelected && phaseStatusFilter.includes(status);
          return (
            <button
              key={status}
              type="button"
              className={`phase-status-chip status-${status}${checked ? ' active' : ''}`}
              disabled={count === 0}
              onClick={() => togglePhaseStatusFilter(status as PhaseStatusFilterKey)}
              title={`${count} workstream${count === 1 ? '' : 's'} in '${status}'`}
            >
              <span className="lbl">{status}</span>
              <span className="count">{count}</span>
            </button>
          );
        })}
      </div>
      <div className="panel-bd">
        {attention.length === 0 && dismissedCount > 0 && (
          <div className="attention-dismissed-note">
            {dismissedCount} attention {dismissedCount === 1 ? 'item' : 'items'} dismissed ·{' '}
            <button type="button" className="attention-link" onClick={() => setShowDismissed(true)}>show</button>{' '}·{' '}
            <button type="button" className="attention-link" onClick={restoreDismissed}>restore</button>
          </div>
        )}
        {attention.length > 0 && (
          <div className={`attention${attentionCollapsed ? ' is-collapsed' : ''}`}>
            <div className="attention-bar">
              <button
                type="button"
                className="attention-hd"
                onClick={toggleAttentionCollapsed}
                aria-expanded={!attentionCollapsed}
                aria-controls={`attention-bd-${storyId}`}
                title={attentionCollapsed ? 'Expand needs-attention list' : 'Collapse needs-attention list'}
              >
                <span className="attention-hd__left">
                  <span className="attention-chev" aria-hidden="true">{attentionCollapsed ? '▸' : '▾'}</span>
                  <span>NEEDS ATTENTION</span>
                </span>
                <span>{attention.length}</span>
              </button>
              {showDismissed
                ? <button type="button" className="attention-clear" onClick={() => setShowDismissed(false)} title="Hide dismissed items again">Hide dismissed</button>
                : <button type="button" className="attention-clear" onClick={() => dismissItems(attention)}
                    title="Dismiss every item listed. An item comes back if its status or stall reason changes.">Clear all</button>}
            </div>
            {!attentionCollapsed && (
              <div className="attention-bd" id={`attention-bd-${storyId}`}>
                {attention.map((w) => (
                  <div key={w.id} className="att-item" onClick={() => onSelect({ kind: 'phase', id: w.id })}>
                    {/* Stage number + name, as on the phase cards; the full id stays in the tooltip. */}
                    <span className="id" title={w.id}>
                      {[stageNumOf.get(w.id), w.name || shortId(w.id)].filter(Boolean).join(' · ')}
                    </span>
                    <span
                      className="reason"
                      title={w.status === 'stalled' && w.stallReason ? w.stallReason : undefined}
                    >
                      {w.status === 'stalled'
                        ? `stalled — ${w.stallReason ?? 'escalate to lead'}`
                        : `blocked on ${w.deps.length} upstream`}
                    </span>
                    {isDismissed(w, dismissals)
                      ? <span className="att-dismissed" title="Dismissed">dismissed</span>
                      : <button
                          type="button"
                          className="att-dismiss"
                          aria-label={`Dismiss ${shortId(w.id)}`}
                          title="Dismiss. It comes back if its status or stall reason changes."
                          onClick={(e) => { e.stopPropagation(); dismissItems([w]); }}
                        >×</button>}
                  </div>
                ))}
                {dismissedCount > 0 && !showDismissed && (
                  <div className="attention-dismissed-note">
                    {dismissedCount} dismissed ·{' '}
                    <button type="button" className="attention-link" onClick={() => setShowDismissed(true)}>show</button>{' '}·{' '}
                    <button type="button" className="attention-link" onClick={restoreDismissed}>restore</button>
                  </div>
                )}
              </div>
            )}
          </div>
        )}

        {ws.length === 0 ? (
          <div className="mode-stub">
            <div className="lbl">▌ NO WORKSTREAMS</div>
            <div className="sub">
              The architect crew has not yet decomposed this story into workstreams.
              Once decomposition lands, each workstream renders here as one phase card.
            </div>
          </div>
        ) : visibleWs.length === 0 ? (
          <div className="mode-stub">
            <div className="lbl">▌ NO MATCHES</div>
            <div className="sub">
              No workstreams match the current status filter. Click <b>all</b> above
              to clear it.
            </div>
          </div>
        ) : (
          <div className="phase-board">
            {visibleWs.map(({ w, originalIndex }) => {
              // docs/forge/e2e.md Stage 8.1 — render the impl card,
              // then (when present) stack the qa-e2e sibling directly
              // beneath it inside a `.phase-stack` wrapper. When the
              // impl row has no qa-e2e companion (selector returns
              // undefined), render the impl card alone — no empty
              // placeholder slot.
              const qa = qaE2eByImpl.get(w.id);
              const stageNum = stageNumOf.get(w.id) ?? `${originalIndex + 1}`;
              const implCard = (
                <PhaseCard
                  key={w.id}
                  ws={w}
                  index={originalIndex}
                  stageNum={stageNum}
                  stageNumOf={stageNumOf}
                  wsNameById={wsNameById}
                  selected={selection.kind === 'phase' && selection.id === w.id}
                  onSelect={() => onSelect({ kind: 'phase', id: w.id })}
                />
              );
              if (!qa) return implCard;
              return (
                <div key={w.id} className="phase-stack" data-impl-id={w.id}>
                  {implCard}
                  <PhaseCard
                    ws={qa}
                    index={originalIndex}
                    stageNum={`${stageNum}-e2e`}
                    stageNumOf={stageNumOf}
                    wsNameById={wsNameById}
                    selected={selection.kind === 'phase' && selection.id === qa.id}
                    onSelect={() => onSelect({ kind: 'phase', id: qa.id })}
                    variant="qae2e"
                  />
                </div>
              );
            })}
          </div>
        )}
      </div>
    </section>
  );
}

interface PhaseCardProps {
  ws: StorySpawnWorkstreamSummary;
  index: number;
  stageNum: string;
  stageNumOf: Map<string, string>;
  wsNameById: Map<string, string>;
  selected: boolean;
  onSelect: () => void;
  /** docs/forge/e2e.md Stage 8.1 — `'qae2e'` swaps the card's accent
   *  to the green QA team colour (`--sc-t-qa`) and adds the
   *  `phase--qae2e` CSS modifier so it visually reads as a stacked
   *  child of its impl sibling. Defaults to `'impl'`. */
  variant?: 'impl' | 'qae2e';
}

function PhaseCard({ ws, stageNum, stageNumOf, wsNameById, selected, onSelect, variant = 'impl' }: PhaseCardProps) {
  const phaseCls = STATUS_TO_PHASE_CLASS[ws.status] ?? 'queued';
  const badgeCls = STATUS_TO_BADGE[ws.status] ?? 'blocked';
  const pct = STATUS_PROGRESS[ws.status] ?? 0;
  const unanswered = ws.unansweredQuestionCount ?? 0;
  // Operator-facing lifecycle label: a `historic` row reads as
  // `complete` (it shipped, PR merged) with an "obsolete" audit pill
  // rendered next to the badge to surface that the plan version it
  // targeted has been superseded. Tooltip mirrors the same model.
  const isObsolete = ws.status === 'historic';
  const lifecycleLabel = isObsolete ? 'complete' : ws.status;
  const variantCls = variant === 'qae2e' ? ' phase--qae2e' : '';
  // Phase 28+ runtime metrics (mirrors the doc-card metrics row): show the
  // gen↔eval avatars + iter / cost / score on a second line so the long stage
  // title keeps the first line to itself. Only when the workstream has run.
  const iter = ws.iter ?? 0;
  const cost = ws.costUsd ?? 0;
  const hasRun = iter > 0 || cost > 0 || !!ws.latestGenAgentId || !!ws.latestEvalAgentId;
  const scoreTone = verdictTone(ws.latestVerdict ?? '');

  return (
    <div
      className={`phase ${phaseCls}${selected ? ' selected' : ''}${variantCls}`}
      data-sel-key={`phase:${ws.id}`}
      data-variant={variant}
      onClick={onSelect}
      data-sc-tooltip={
        ws.status === 'stalled' && ws.stallReason
          ? `${ws.name}\nstalled — ${ws.stallReason}${ws.branch ? `\n${ws.branch}` : ''}`
          : `${ws.name}\n${lifecycleLabel}${isObsolete ? ' (obsolete plan version)' : ''}${ws.branch ? ` · ${ws.branch}` : ''}`
      }
      data-sc-tooltip-pos="bottom"
    >
      <div className="phase-hd">
        <span className="phase-no"><b>{stageNum}</b></span>
        <span className="phase-title">{ws.name}</span>
        <span
          className={`badge ${badgeCls}`}
          title={ws.status === 'stalled' && ws.stallReason ? ws.stallReason : undefined}
        >
          {lifecycleLabel}
        </span>
        {isObsolete && (
          <span
            className="badge"
            style={{
              marginLeft: 4,
              background: 'rgba(121,224,138,0.10)',
              color: 'var(--sc-green-d, #4f8a59)',
              border: '1px solid rgba(121,224,138,0.35)',
              fontSize: 10,
              padding: '1px 6px',
              borderRadius: 8,
              fontWeight: 600,
            }}
            title="Shipped + merged under a previous version of the plan; the architect has since re-decomposed and this anchor was either renamed or removed. The merged code is still on the base branch — only the plan attribution moved."
          >
            obsolete
          </span>
        )}
        {unanswered > 0 && (
          <span
            className="badge stalled"
            style={{
              marginLeft: 4,
              background: 'rgba(244,71,71,0.15)',
              color: '#f44747',
              border: '1px solid rgba(244,71,71,0.3)',
              fontSize: 10,
              padding: '1px 6px',
              borderRadius: 8,
              fontWeight: 600,
            }}
            title={`${unanswered} unanswered question${unanswered === 1 ? '' : 's'} blocking pass`}
          >
            ⚠ {unanswered} Q
          </span>
        )}
      </div>
      {hasRun && (
        <div className="phase-metrics">
          {/* Line 1: gen↔eval avatars + run start time. */}
          <div className="phase-metrics-row">
            <div className="doc-agents">
              {(ws.agentPairs && ws.agentPairs.length > 1) ? (
                <span
                  style={{ display: 'inline-flex', alignItems: 'center' }}
                  title={`${ws.agentPairs.length} agent pairs assigned (oldest → newest): ${ws.agentPairs.map((p) => `${p.gen ?? '?'}/${p.eval ?? '?'}`).join(' → ')}`}
                >
                  {ws.agentPairs.map((p, i) => {
                    const isLatest = i === ws.agentPairs!.length - 1;
                    return (
                      <span key={`${p.gen}-${p.eval}-${i}`} style={{ display: 'inline-flex', alignItems: 'center' }}>
                        {i > 0 && <span style={{ opacity: 0.4, fontSize: 9, margin: '0 2px' }}>→</span>}
                        <span
                          style={{
                            display: 'inline-flex',
                            alignItems: 'center',
                            opacity: isLatest ? 1 : 0.45,
                            transform: isLatest ? 'none' : 'scale(0.82)',
                            transformOrigin: 'center',
                          }}
                        >
                          <PhaseAgentAvatar id={p.gen} role="generator" />
                          <span className="doc-agents-link">⇄</span>
                          <PhaseAgentAvatar id={p.eval} role="evaluator" />
                        </span>
                      </span>
                    );
                  })}
                </span>
              ) : (
                <>
                  <PhaseAgentAvatar id={ws.latestGenAgentId} role="generator" />
                  <span className="doc-agents-link">⇄</span>
                  <PhaseAgentAvatar id={ws.latestEvalAgentId} role="evaluator" />
                </>
              )}
            </div>
            {ws.startedAt && (
              <span className="m-cell"><span className="k">START</span><span className="v">{new Date(ws.startedAt).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })}</span></span>
            )}
          </div>
          {/* Line 2: run metrics — iter / score / cost. */}
          <div className="phase-metrics-row">
            {iter > 0 && (
              <span className="m-cell"><span className="k">ITER</span><span className="v">{iter}</span></span>
            )}
            {ws.latestScore != null && (
              <span className={`m-cell score ${scoreTone}`}><span className="k">SCORE</span><span className="v">{ws.latestScore}</span></span>
            )}
            {cost > 0 && (
              <span className="m-cell cost"><span className="k">COST</span><span className="v">${cost.toFixed(2)}</span></span>
            )}
            {ws.buildGate && gateTone(ws.buildGate) !== 'neutral' && (
              <span className={`m-cell build ${gateTone(ws.buildGate)}`} title={`build gate: ${ws.buildGate}`}>
                <span className="k">BUILD</span><span className="v">{gateGlyph(ws.buildGate)}</span>
              </span>
            )}
            {ws.testGate && gateTone(ws.testGate) !== 'neutral' && (
              <span className={`m-cell build ${gateTone(ws.testGate)}`} title={`test gate: ${ws.testGate}`}>
                <span className="k">TEST</span><span className="v">{gateGlyph(ws.testGate)}</span>
              </span>
            )}
            {(ws.questionCount ?? 0) > 0 && (
              <span className="m-cell q" title={`${ws.questionCount} question${ws.questionCount === 1 ? '' : 's'} raised on this workstream`}>
                <span className="k">Q</span><span className="v">{ws.questionCount}</span>
              </span>
            )}
          </div>
        </div>
      )}
      <div className="phase-bd">
        <div className={`progress ${phaseCls === 'done' ? 'done' : phaseCls === 'review' ? 'review' : ''}`}>
          <div className="bar" style={{ width: `${pct}%` }} />
        </div>
        <div className="stages">
          <div className="stage-row queued">
            <span className="id">BR</span>
            <span className="name">{ws.branch}</span>
            <span className="pts">—</span>
            <span className="stat">branch</span>
          </div>
          {ws.prs.length > 0 && (
            ws.prs.map((pr) => (
              <div key={pr} className="stage-row review">
                <span className="id">PR</span>
                <span className="name">{pr}</span>
                <span className="pts">—</span>
                <span className="stat">{ws.status}</span>
              </div>
            ))
          )}
          {ws.deps.length > 0 && (
            <div className="stage-row blocked">
              <span className="id">DEPS</span>
              <span className="name phase-card__deps">
                {ws.deps.map((depId) => {
                  const num = stageNumOf.get(depId) ?? '?';
                  const upName = wsNameById.get(depId) ?? depId;
                  return (
                    <span key={depId} className="phase-card__dep-chip" title={`${num} — ${upName}`}>
                      {num}
                    </span>
                  );
                })}
              </span>
              <span className="pts">—</span>
              <span className="stat">{ws.status === 'blocked' ? 'waiting' : 'cleared'}</span>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

/* ─── DESIGN MODE ── four planning artifacts as doc cards ────────────── */

const PLAN_DOCS: { no: string; key: 'architecture' | 'tech-spec' | 'implementation-plan' | 'e2e-scenarios'; title: string; }[] = [
  { no: '01', key: 'architecture',         title: 'architecture.md — system + components + scenarios' },
  { no: '02', key: 'tech-spec',            title: 'tech-spec.md — libs, patterns, practices' },
  { no: '03', key: 'implementation-plan',  title: 'implementation-plan.md — Phase → Stage → Step' },
  { no: '04', key: 'e2e-scenarios',        title: 'e2e-scenarios.md — Gherkin BDD per phase' },
];

function DesignMaster({ detail, artifacts, selection, onSelect, collapsed, onToggleCollapsed }: Props) {
  const planMerged = !!artifacts?.plan.pr && detail.spawn.phase !== 'plan';
  const planOpen   = !!artifacts?.plan.pr && detail.spawn.phase === 'plan';
  const decomposed = !!detail.spawn.decompositionJson;
  const docStats = useDocStats(detail.spawn.storyId, PLAN_DOCS.map((d) => d.key));

  return (
    <section className="panel">
      <div className="panel-hd">
        <CollapseBtn collapsed={collapsed} onToggle={onToggleCollapsed} />
        <span>▌ DESIGN DOCS · ARCHITECT CREW</span>
        <span className="ix">{PLAN_DOCS.length} ARTIFACTS</span>
      </div>
      <div className="panel-bd">
        <div className="doc-list">
          {PLAN_DOCS.map((doc) => {
            const prState = planMerged ? 'merged' : planOpen ? 'review' : 'draft';
            const id = `doc:${doc.key}`;
            const stats = docStats[doc.key];
            const loopStatus = stats?.status ?? 'pending';
            const fileName = `${doc.key}.md`;
            const subtitle = doc.title.split('—').slice(1).join('—').trim();
            const tone = verdictTone(stats?.verdict ?? '');
            // When the doc is stalled, show the stall reason in the tooltip.
            const statusLabel = loopStatus;
            const statusTitle = loopStatus === 'stalled' && stats?.stallReason
              ? `stalled — ${stats.stallReason}`
              : undefined;
            return (
              <div
                key={doc.key}
                className={`doc-card ${prState}${selection.kind === 'doc' && selection.id === id ? ' selected' : ''}`}
                data-sel-key={`doc:${id}`}
                onClick={() => onSelect({ kind: 'doc', id })}
                title={`plan PR ${artifacts?.plan.pr ?? '—'} · branch ${artifacts?.plan.branch ?? '—'}`}
              >
                <div className="doc-card__top">
                  <span className="doc-no">DOC {doc.no}</span>
                  <span
                    className={`doc-status doc-status--${loopStatus}`}
                    title={statusTitle}
                  >
                    {statusLabel}
                  </span>
                </div>
                <div className="doc-card__name">{fileName}</div>
                {subtitle && <div className="doc-card__sub">{subtitle}</div>}
                <div className="doc-card__metrics">
                  {(stats?.generator || stats?.evaluator) && (
                    <DocAgentStrip generator={stats.generator} evaluator={stats.evaluator} />
                  )}
                  {stats && stats.iter > 0 && (
                    <span className="m-cell">
                      <span className="k">ITER</span>
                      <span className="v">{stats.iter}</span>
                    </span>
                  )}
                  {stats && stats.score != null && (
                    <span className={`m-cell score ${tone}`}>
                      <span className="k">SCORE</span>
                      <span className="v">{stats.score}</span>
                    </span>
                  )}
                  {stats && stats.cost > 0 && (
                    <span className="m-cell cost">
                      <span className="k">COST</span>
                      <span className="v">${stats.cost.toFixed(2)}</span>
                    </span>
                  )}
                </div>
              </div>
            );
          })}

          <div
            className={`doc-card ${decomposed ? 'merged' : 'draft'}${selection.kind === 'doc' && selection.id === 'doc:decomposition' ? ' selected' : ''}`}
            data-sel-key="doc:doc:decomposition"
            onClick={() => onSelect({ kind: 'doc', id: 'doc:decomposition' })}
          >
            <div className="doc-card__top">
              <span className="doc-no">DECOMP</span>
              <span className="doc-status">{decomposed ? 'merged' : 'draft'}</span>
            </div>
            <div className="doc-card__name">workstreams.yaml</div>
            <div className="doc-card__sub">N parallel workstreams + dependency graph</div>
            <div className="doc-card__metrics">
              <span className="m-cell">
                <span className="k">STREAMS</span>
                <span className="v">{detail.workstreams.length}</span>
              </span>
              <span className={`m-cell ${detail.spawn.decompositionApproved ? 'score pass' : ''}`}>
                <span className="k">APPROVED</span>
                <span className="v">{detail.spawn.decompositionApproved ? 'yes' : 'no'}</span>
              </span>
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}

/* ─── RELEASE MODE ── completion gates ───────────────────────────────── */

function ReleaseMaster({ detail, artifacts, selection, onSelect, collapsed, onToggleCollapsed }: Props) {
  const ws = detail.workstreams;
  const total = ws.length || 1;
  const merged = ws.filter((w) => w.status === 'complete' || w.status === 'merging').length;
  const stalled = ws.filter((w) => w.status === 'stalled').length;
  const planMerged = !!artifacts?.plan.pr && detail.spawn.phase !== 'plan';
  const qaMerged   = !!detail.spawn.qaPrId;

  const gates = [
    {
      id: 'plan',
      name: 'PLAN ARTIFACTS MERGED',
      pct: planMerged ? 100 : 0,
      status: planMerged ? 'pass' as const : 'pending' as const,
      summary: planMerged ? `${artifacts?.plan.pr} on main` : 'awaiting plan PR merge',
    },
    {
      id: 'impl',
      name: 'WORKSTREAM PRS MERGED',
      pct: Math.round((merged / total) * 100),
      status: merged === total ? 'pass' as const : merged === 0 ? 'pending' as const : 'warn' as const,
      summary: `${merged}/${total} workstream PRs merged`,
    },
    {
      id: 'qa',
      name: 'QA FEATURE FILES MERGED',
      pct: qaMerged ? 100 : 0,
      status: qaMerged ? 'pass' as const : 'pending' as const,
      summary: qaMerged ? `${detail.spawn.qaPrId} on main` : 'awaiting QA PR',
    },
    {
      id: 'tests',
      name: 'PER-PHASE UNIT + INT + E2E GREEN',
      pct: 0,
      status: 'pending' as const,
      summary: 'gate runs once step-defs land per workstream',
    },
    {
      id: 'stalled',
      name: 'NO STALLED WORK ITEMS',
      pct: stalled === 0 ? 100 : 0,
      status: stalled === 0 ? 'pass' as const : 'fail' as const,
      summary: stalled === 0 ? 'no stalled items' : `${stalled} item(s) stalled — escalate`,
    },
  ];

  return (
    <section className="panel">
      <div className="panel-hd">
        <CollapseBtn collapsed={collapsed} onToggle={onToggleCollapsed} />
        <span>▌ RELEASE GATES</span>
        <span className="ix">{gates.length} GATES</span>
      </div>
      <div className="panel-bd">
        <div className="gate-list">
          {gates.map((g) => (
            <div
              key={g.id}
              className={`gate-card ${g.status}${selection.kind === 'gate' && selection.id === g.id ? ' selected' : ''}`}
              data-sel-key={`gate:${g.id}`}
              onClick={() => onSelect({ kind: 'gate', id: g.id })}
            >
              <div className="gate-hd">
                <span className="gate-name">{g.name}</span>
                <span className="gate-pct">{g.pct}%</span>
              </div>
              <div className="gate-summary">{g.summary}</div>
              <div className="gate-bar"><div className="fill" style={{ width: `${g.pct}%` }} /></div>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

function shortId(id: string): string {
  // ws-foo-bar-baz-very-long  →  …baz-very-long
  if (id.length <= 14) return id.toUpperCase();
  return ('…' + id.slice(-13)).toUpperCase();
}

interface DocAgentInfo {
  agentId: string;
  name: string;
  model: string | null;
}

interface DocStats {
  iter: number;
  score: number | null;
  verdict: string;
  cost: number;
  /** Loop lifecycle — `live` while events flow, `iterating` between iters
   *  (sub-pass), `stalled` when max-iters hit / rejected / blocked on
   *  unanswered questions, `awaiting-pr-review` after a passing verdict
   *  but before the plan PR merges, `completed` after the PR merges,
   *  `pending` before the first run. */
  status: 'live' | 'iterating' | 'stalled' | 'awaiting-pr-review' | 'completed' | 'pending';
  /** Reason the doc is stalled — e.g. the last deliverable verdict. */
  stallReason: string | null;
  /** Count of unanswered operator questions blocking the pass gate.
   *  Surfaced next to the stalled badge so the operator knows the
   *  loop isn't making meaningful progress until they answer. */
  unansweredQuestions: number;
  generator: DocAgentInfo | null;
  evaluator: DocAgentInfo | null;
}

/**
 * Polls the per-doc iteration endpoint for each known plan-doc key. The
 * 12s cadence is slower than the DocumentPane's own poll because the master
 * panel is "always visible" — we don't need sub-10s freshness for a badge.
 */
function useDocStats(storyId: string, keys: string[]): Record<string, DocStats | null> {
  const [stats, setStats] = useState<Record<string, DocStats | null>>({});
  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const results = await Promise.all(keys.map((k) => forgeApi.getStoryArtifactIterations(storyId, k)));
        if (cancelled) return;
        const next: Record<string, DocStats | null> = {};
        results.forEach((r, i) => {
          const last = r.iterations[r.iterations.length - 1];
          next[keys[i]] = {
            iter: r.iterations.length,
            score: last?.score ?? null,
            verdict: last?.verdict ?? '',
            cost: r.totalCostUsd,
            status: r.status,
            stallReason: r.stallReason ?? null,
            unansweredQuestions: r.unansweredQuestions ?? 0,
            generator: r.generator,
            evaluator: r.evaluator,
          };
        });
        setStats(next);
      } catch {
        // silent
      }
    }
    void load();
    // Tighter cadence than the original 12s — `live` should turn over within
    // an iteration boundary, and the iterations endpoint is cheap.
    const id = setInterval(() => { void load(); }, 6_000);
    return () => { cancelled = true; clearInterval(id); };
  }, [storyId, keys.join('|')]);
  return stats;
}

function verdictTone(verdict: string): 'pass' | 'iterate' | 'reject' | 'neutral' {
  const v = (verdict || '').toLowerCase();
  if (v.startsWith('pass')) return 'pass';
  if (v.startsWith('reject') || v.startsWith('stalled')) return 'reject';
  if (!v) return 'neutral';
  return 'iterate';
}

// Local build/test gate outcome (passed | failed | skipped | resolving |
// resolved) → the shared green/amber/red tone palette the score cell uses.
// `skipped` maps to neutral so the badge is hidden (the gate didn't run).
function gateTone(outcome: string): 'pass' | 'iterate' | 'reject' | 'neutral' {
  switch ((outcome || '').toLowerCase()) {
    case 'passed':
    case 'resolved': return 'pass';
    case 'failed': return 'reject';
    case 'resolving': return 'iterate';
    default: return 'neutral'; // skipped / unknown — badge hidden
  }
}

function gateGlyph(outcome: string): string {
  switch ((outcome || '').toLowerCase()) {
    case 'passed':
    case 'resolved': return '✓';
    case 'failed': return '✗';
    case 'resolving': return '⟳';
    default: return '–';
  }
}

interface DocAgentStripProps {
  generator: DocAgentInfo | null;
  evaluator: DocAgentInfo | null;
}

function DocAgentStrip({ generator, evaluator }: DocAgentStripProps) {
  return (
    <div className="doc-agents">
      <DocAgentAvatar info={generator} role="generator" />
      <span className="doc-agents-link">⇄</span>
      <DocAgentAvatar info={evaluator} role="evaluator" />
    </div>
  );
}

// Phase-card variant — the workstream summary only carries agent ids (no
// name/model), so this is a slimmer twin of DocAgentAvatar driven by id alone.
function PhaseAgentAvatar({ id, role }: { id: string | null | undefined; role: 'generator' | 'evaluator' }) {
  if (!id) {
    return <span className={`doc-agent doc-agent--${role} doc-agent--empty`} title={`no ${role} yet`}>—</span>;
  }
  const url = avatarFor(id);
  return (
    <span className={`doc-agent doc-agent--${role}`} title={`${id} · ${role}`}>
      {url ? (
        <img src={url} alt={id} onError={(e) => { (e.target as HTMLImageElement).style.display = 'none'; }} />
      ) : (
        <span className="doc-agent-mono">{role === 'generator' ? 'G' : 'E'}</span>
      )}
    </span>
  );
}

function DocAgentAvatar({ info, role }: { info: DocAgentInfo | null; role: 'generator' | 'evaluator' }) {
  if (!info) {
    return <span className={`doc-agent doc-agent--${role} doc-agent--empty`} title={`no ${role} yet`}>—</span>;
  }
  const url = avatarFor(info.agentId, info.name);
  // Compact tooltip: line 1 = agent id + display name, line 2 = role + model.
  const tip = `${info.agentId} · ${info.name}\n${role} · ${info.model ?? 'unknown model'}`;
  return (
    <span className={`doc-agent doc-agent--${role}`} title={tip}>
      {url ? (
        <img src={url} alt={info.name} onError={(e) => { (e.target as HTMLImageElement).style.display = 'none'; }} />
      ) : (
        <span className="doc-agent-mono">{role === 'generator' ? 'G' : 'E'}</span>
      )}
    </span>
  );
}
