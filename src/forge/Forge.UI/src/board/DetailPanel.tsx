import { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import type { Card as CardModel } from '@/types';
import { useBoardStore } from '@/state/useBoardStore';
import { useProductsStore } from '@/state/useProductsStore';
import { avatarFor, agentTooltip } from '@/agentAvatar';
import { useAgentEditorStore } from '@/state/useAgentEditorStore';
import { useWorkstreamsStore } from '@/state/useWorkstreamsStore';
import { useToastStore } from '@/state/useToastStore';
import { forgeApi } from '@/api/forgeApi';
import { DeliverableScorePanel } from '@/board/DeliverableScorePanel';
import { StoryPicker } from '@/board/StoryPicker';
import { useStateStore } from '@/state/useStateStore';
import { useWorkItemsStore } from '@/workitems/useWorkItemsStore';
import { useTaskLive } from '@/realtime/useTaskLiveStore';

// Verbatim port of the prototype's <aside class="detail-panel"> static
// markup (lines 2899-2934) and renderDetailBody / renderDetailActions
// (lines 3591-3668).


function DetailActions({ card }: { card: CardModel }) {
  const navigate = useNavigate();
  // The Plan & Design panel doesn't have a team / crew picker (legacy
  // prototype shape). Cards and work items share ids after Phase 12.2's
  // backfill, so navigating to /work-items focused on this id opens the
  // Phase 12 detail panel where the Phase 17 team picker lives.
  const openInWorkItems = () => navigate(`/work-items?focus=${encodeURIComponent(card.id)}`);
  const openLive = () => navigate(`/work-items/${encodeURIComponent(card.id)}/live`);

  if (card.humanGate) return (
    <>
      <button className="btn btn-primary" style={{ background: 'var(--success)' }}>
        <svg width="11" height="11" viewBox="0 0 11 11" fill="none">
          <path d="M2 5.5 L 4.5 8 L 9 3" stroke="currentColor" strokeWidth="1.6" fill="none" strokeLinecap="round" />
        </svg>
        Approve & merge
      </button>
      <button className="btn btn-secondary">Request changes</button>
      <button className="btn btn-ghost">View diff →</button>
    </>
  );
  if (card.live) return (
    <>
      <button className="btn btn-secondary">Pause agent</button>
      <button className="btn btn-secondary" onClick={openInWorkItems}>Reassign</button>
      <button className="btn btn-ghost" onClick={openLive}>View trace →</button>
    </>
  );
  if (card.blocked) return (
    <>
      <button className="btn btn-primary">Unblock</button>
      <button className="btn btn-secondary" onClick={openInWorkItems}>Reassign</button>
      <button className="btn btn-ghost">View blocker →</button>
    </>
  );
  if (card.col === 'backlog') return (
    <>
      {/* Phase 17 — opens the work-items detail panel where the team /
          crew picker lives. Routing to a team enqueues the work item;
          observe in /inbox. */}
      <button className="btn btn-primary" onClick={openInWorkItems}>Assign to agent or team</button>
      <button className="btn btn-secondary" onClick={openInWorkItems}>Edit</button>
      <button className="btn btn-ghost">Archive →</button>
    </>
  );
  return (
    <>
      <button className="btn btn-secondary" onClick={openInWorkItems}>Edit</button>
      <button className="btn btn-secondary" onClick={openInWorkItems}>Reassign</button>
      <button className="btn btn-ghost">Open in ADO →</button>
    </>
  );
}

export function DetailPanel() {
  const selectedId = useBoardStore((s) => s.selectedId);
  const card = useBoardStore((s) => s.cards.find((c) => c.id === s.selectedId)) ?? null;
  const setSelected = useBoardStore((s) => s.setSelected);
  const upsert = useBoardStore((s) => s.upsertFromRealtime);
  const workstreams = useWorkstreamsStore((s) => s.workstreams);
  const focusWs = useWorkstreamsStore((s) => s.setFocused);
  const navigate = useNavigate();
  const linkedWs = card?.branch ? workstreams.find((w) => w.branch === card.branch) : undefined;
  // Phase 17 — surface the WorkItem.assignedTo (team / crew / agent) so the
  // legacy Plan & Design panel doesn't show "unassigned" when a Phase 12
  // assignment has been made via /work-items.
  const workItem = useWorkItemsStore((s) => (selectedId ? s.items.find((w) => w.id === selectedId) : undefined));
  const wiAssignee = workItem?.assignedTo ?? null;
  // Phase 17 — pull the real per-work-item transcript so the "reasoning
  // trace" panel shows actual agent activity, not the prototype's static
  // fake. Hook is no-op when selectedId is null.
  const live = useTaskLive(selectedId ?? null);

  const [storyPickerOpen, setStoryPickerOpen] = useState(false);
  const reloadState = useStateStore((s) => s.load);

  async function assignStory(epicId: string) {
    if (!card) return;
    try {
      const updated = await forgeApi.patchCard(card.id, { epic: epicId });
      upsert(updated);
      useToastStore.getState().show(epicId ? `Assigned to ${epicId}` : 'Removed story', 'success');
      void reloadState();
    } catch {
      useToastStore.getState().show('Failed to update story', 'danger');
    }
  }

  const open = !!selectedId && !!card;
  const safe = card;

  return (
    <aside className={`detail-panel${open ? ' open' : ''}`} id="detail-panel">
      {safe && (
        <>
          <div className="detail-head">
            <div className="detail-head-row">
              <div className="detail-eyebrow">
                <span id="detail-id">{safe.id}</span>
                <span style={{ opacity: 0.4 }}>/</span>
                <span
                  id="detail-epic"
                  style={{ position: 'relative', cursor: 'pointer', textDecoration: 'underline dotted', textUnderlineOffset: 3 }}
                  onClick={() => setStoryPickerOpen((v) => !v)}
                  title="Click to change story"
                >
                  {safe.epic || 'No story'}
                  <StoryPicker
                    open={storyPickerOpen}
                    currentStory={safe.epic}
                    onSelect={(epicId) => void assignStory(epicId)}
                    onClose={() => setStoryPickerOpen(false)}
                  />
                </span>
                <span style={{ opacity: 0.4 }}>/</span>
                <span id="detail-type">{safe.type.charAt(0).toUpperCase() + safe.type.slice(1)}</span>
              </div>
              <button className="detail-close" id="detail-close" title="Close" onClick={() => setSelected(null)}>
                <svg width="14" height="14" viewBox="0 0 14 14" fill="none"><path d="M3 3 L 11 11 M 11 3 L 3 11" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" /></svg>
              </button>
            </div>
            <h2 className="detail-title" id="detail-title">{safe.title}</h2>
            <div className="detail-status-row">
              <span className="detail-status-pill">
                <span className="label">Column</span>
                <span id="detail-col">{safe.col}</span>
              </span>
              <span className="detail-status-pill">
                <span className="label">Agent</span>
                {safe.agent && (() => {
                  const agent = safe.agent;
                  const av = avatarFor(agent.id, agent.name);
                  const tip = agentTooltip(agent.id, agent.name, agent.role)
                    + (agent.id ? ' · click to open agent' : '');
                  // Phase 13f — agent chip on the work-item panel opens the
                  // global agent-edit popup so the operator can edit the
                  // assignee without leaving the board. When the provider
                  // didn't surface a stable id, the chip renders as a static
                  // badge (no click handler — there's no agent record to open).
                  const agentId = agent.id;
                  const open = agentId
                    ? () => useAgentEditorStore.getState().openAgent(agentId)
                    : undefined;
                  const fallbackLabel = agent.id ?? (agent.name ? agent.name.slice(0, 2).toUpperCase() : '?');
                  const sharedClick: React.HTMLAttributes<HTMLElement> = open
                    ? {
                        role: 'button',
                        tabIndex: 0,
                        onKeyDown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); } },
                      }
                    : {};
                  return av ? (
                    <img
                      src={av}
                      alt={agent.name || fallbackLabel}
                      className={`agent-chip-avatar ${agent.role}`}
                      style={{ marginRight: 2, cursor: open ? 'pointer' : 'default' }}
                      title={tip}
                      onClick={open}
                      {...sharedClick}
                    />
                  ) : (
                    <span
                      className={`agent-chip ${agent.role}`}
                      style={{ marginRight: 2, cursor: open ? 'pointer' : 'default' }}
                      title={tip}
                      onClick={open}
                      {...sharedClick}
                    >
                      {fallbackLabel}
                    </span>
                  );
                })()}
                <span
                  id="detail-agent"
                  style={safe.agent?.id ? { cursor: 'pointer', textDecoration: 'underline dotted', textUnderlineOffset: 2 } : undefined}
                  title={safe.agent?.id ? 'Click to open agent' : (wiAssignee ? `${wiAssignee.kind} — change in /work-items` : undefined)}
                  onClick={() => {
                    const aid = safe.agent?.id;
                    if (aid) useAgentEditorStore.getState().openAgent(aid);
                  }}
                >
                  {safe.agent
                    ? safe.agent.name
                    : wiAssignee
                      ? <>
                          {wiAssignee.displayName}
                          <span style={{
                            marginLeft: 6, fontSize: 9, padding: '1px 5px', borderRadius: 999,
                            border: `1px solid ${wiAssignee.kind === 'team' ? 'var(--warning)' : 'var(--accent)'}`,
                            color: wiAssignee.kind === 'team' ? 'var(--warning)' : 'var(--accent)',
                            fontFamily: 'var(--font-mono, monospace)',
                          }}>{wiAssignee.kind === 'team' ? 'queue' : wiAssignee.kind}</span>
                        </>
                      : 'unassigned'}
                </span>
              </span>
              <span className="detail-status-pill">
                <span className="label">Points</span>
                <span id="detail-pts">{safe.pts} pts</span>
              </span>
            </div>
          </div>

          <div className="detail-body" id="detail-body">
            <div className="detail-meta">
              <div className="detail-meta-cell">
                <div className="detail-meta-label">Branch</div>
                <div className="detail-meta-val mono">
                  {safe.branch ? (
                    linkedWs ? (
                      <a
                        href="#/workstreams"
                        onClick={(e) => {
                          e.preventDefault();
                          focusWs(linkedWs.id);
                          navigate('/workstreams');
                        }}
                        style={{ color: 'var(--accent)', textDecoration: 'none' }}
                        title="Jump to workstream"
                      >
                        {safe.branch} →
                      </a>
                    ) : safe.branch
                  ) : '—'}
                </div>
              </div>
              <div className="detail-meta-cell"><div className="detail-meta-label">Cost so far</div><div className="detail-meta-val mono">${safe.cost.toFixed(2)}</div></div>
              <div className="detail-meta-cell"><div className="detail-meta-label">Priority</div><div className="detail-meta-val">{safe.priority}</div></div>
              <div className="detail-meta-cell"><div className="detail-meta-label">Source</div><div className="detail-meta-val mono" style={{ color: 'var(--text-tertiary)' }}>ADO ↗</div></div>
            </div>

            {/* Phase 17 — show the REAL work-item description if one exists.
                Falls back to a "no description" message instead of the
                prototype's hard-coded "Token rotation" filler. */}
            <div className="detail-section" style={{ marginTop: 22 }}>
              <div className="detail-section-title">Description</div>
              <div className="detail-desc" style={{ whiteSpace: 'pre-wrap' }}>
                {workItem?.descriptionMd && workItem.descriptionMd.trim().length > 0
                  ? workItem.descriptionMd
                  : <span style={{ color: 'var(--text-tertiary)', fontStyle: 'italic' }}>No description.</span>}
              </div>
            </div>

            {/* Phase 14.10 — score panel: deliverables for this work item. */}
            <DeliverableScorePanel workItemId={safe.id} />

            {/* Phase 17 — REAL agent activity from task_transcript_event,
                replacing the prototype's hard-coded fake. Hidden until
                there's actual content. The full live execution view is at
                /work-items/{id}/live; this is the inline summary. */}
            {(() => {
              const transcript = live?.transcript ?? [];
              const isAutoDispatched = workItem?.autoDispatchedAt != null;
              const hasContent = transcript.length > 0 || isAutoDispatched;
              if (!hasContent) return null;
              // Surface the most recent meaningful events (skip pure tokens
              // since they're noisy at this summary level).
              const meaningful = transcript
                .filter((e) => e.kind !== 'task.token')
                .slice(-6);
              return (
                <div className="detail-section">
                  <div className="detail-section-title">
                    <span>Agent activity</span>
                    <span style={{ fontFamily: 'var(--font-mono)', color: 'var(--success)' }}>
                      <span style={{ display: 'inline-block', width: 5, height: 5, borderRadius: '50%', background: 'var(--success)', marginRight: 4, animation: 'pulse 1.4s ease-in-out infinite' }} />
                      live
                    </span>
                  </div>
                  {meaningful.length === 0 ? (
                    <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', fontStyle: 'italic' }}>
                      Routed to crew · waiting for the runner to start.
                    </div>
                  ) : (
                    <ul className="activity" style={{ marginBottom: 8 }}>
                      {meaningful.map((e, idx) => (
                        <li className="activity-item" key={`${e.id}-${idx}`}>
                          <span className="activity-dot eng" />
                          <div className="activity-body">
                            <div className="activity-line" style={{ fontSize: 12 }}>
                              <span style={{ fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-tertiary)', marginRight: 6 }}>
                                {e.kind.replace('task.', '')}
                              </span>
                              {summariseEvent(e)}
                            </div>
                            <div className="activity-time">{new Date(e.at).toLocaleTimeString()}</div>
                          </div>
                        </li>
                      ))}
                    </ul>
                  )}
                  <button
                    type="button"
                    className="btn btn-ghost"
                    onClick={() => navigate(`/work-items/${encodeURIComponent(safe.id)}/live`)}
                    style={{ fontSize: 11 }}
                  >
                    Open live execution view →
                  </button>
                </div>
              );
            })()}

            {/* Phase 17 — buildActivity() returns prototype-derived stub
                rows that have no relation to the real work item. Hide
                until we have a real provider-comments / audit feed. */}

            <CardCommentComposer cardId={safe.id} />
          </div>

          <div className="detail-actions" id="detail-actions">
            <DetailActions card={safe} />
          </div>
        </>
      )}
    </aside>
  );
}

// Phase 6.3 — comment composer that posts to the linked WI/issue.
function CardCommentComposer({ cardId }: { cardId: string }) {
  // Provider dropdown is scoped to the kinds present on the active
  // product's repos — a single-provider product only sees one option.
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const productKinds = useMemo(() => {
    const repos = activeProductId ? reposByProduct[activeProductId] ?? [] : [];
    const kinds = new Set(repos.map((r) => r.kind));
    return {
      ado: kinds.has('ado'),
      github: kinds.has('github'),
      any: kinds.size > 0,
    };
  }, [activeProductId, reposByProduct]);

  const [body, setBody] = useState('');
  const [provider, setProvider] = useState<'github' | 'ado'>(
    productKinds.github ? 'github' : 'ado',
  );
  // Snap to a valid kind whenever the product (and therefore the option set) changes.
  useEffect(() => {
    if (provider === 'github' && !productKinds.github && productKinds.ado) setProvider('ado');
    if (provider === 'ado' && !productKinds.ado && productKinds.github) setProvider('github');
  }, [provider, productKinds.ado, productKinds.github]);
  const [busy, setBusy] = useState(false);

  async function send() {
    if (!body.trim()) return;
    setBusy(true);
    try {
      await forgeApi.commentOnCard(cardId, body, provider);
      useToastStore.getState().show(`Comment posted to ${provider}`, 'success');
      setBody('');
    } catch (err) {
      useToastStore.getState().show(err instanceof Error ? err.message : 'Comment failed', 'danger');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="detail-section">
      <div className="detail-section-title">Comment</div>
      <textarea
        value={body}
        onChange={(e) => setBody(e.target.value)}
        rows={3}
        placeholder="Reply to the linked work item / issue…"
        style={{
          width: '100%',
          padding: 8,
          fontFamily: 'inherit',
          fontSize: 11.5,
          background: 'var(--bg-surface)',
          color: 'var(--text-primary)',
          border: '1px solid var(--border-strong)',
          borderRadius: 'var(--radius-sm)',
          resize: 'vertical',
        }}
      />
      <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginTop: 6 }}>
        <select
          value={provider}
          onChange={(e) => setProvider(e.target.value as 'github' | 'ado')}
          style={{ fontSize: 11, padding: '4px 6px' }}
          title="Provider"
          disabled={!productKinds.any}
        >
          {productKinds.github && <option value="github">GitHub</option>}
          {productKinds.ado    && <option value="ado">Azure DevOps</option>}
          {!productKinds.any   && <option value="github">No repos in product</option>}
        </select>
        <button
          className="btn btn-secondary"
          onClick={() => void send()}
          disabled={busy || !body.trim() || !productKinds.any}
        >
          {busy ? 'Posting…' : 'Comment'}
        </button>
      </div>
    </div>
  );
}

// Phase 17 — terse single-line summary of a transcript event for the
// inline activity list. The full payload is shown on /work-items/{id}/live.
function summariseEvent(e: { kind: string; payload: unknown; agentId?: string | null; crewId?: string | null }): string {
  const p = (e.payload && typeof e.payload === 'object' ? e.payload : {}) as Record<string, unknown>;
  switch (e.kind) {
    case 'task.delegated':
      return `→ crew ${(p.generatorCrewId as string) ?? '?'} (member ${(p.memberAgentId as string) ?? '?'})`;
    case 'task.iteration.started':
      return `iter ${(p.iter as number) ?? '?'} started`;
    case 'task.iteration.scored':
      return `iter ${(p.iter as number) ?? '?'} → score ${(p.score as number) ?? '?'} (${(p.verdict as string) ?? '?'})`;
    case 'task.iteration.passed':
      return `passed at iter ${(p.iter as number) ?? '?'} · score ${(p.score as number) ?? '?'}`;
    case 'task.iteration.rejected':
      return `rejected at iter ${(p.iter as number) ?? '?'}`;
    case 'task.iteration.iterated':
      return `iter ${(p.iter as number) ?? '?'} → re-iterating (notes: ${((p.notes as string) ?? '').slice(0, 60)})`;
    case 'task.tool-call':
      return `tool ${(p.tool as string) ?? '?'}`;
    case 'task.tool-result':
      return `tool ${(p.tool as string) ?? '?'} done`;
    case 'task.commit':
      return `commit ${(p.sha as string)?.slice(0, 8) ?? '?'} — ${(p.message as string) ?? ''}`;
    case 'task.pr-opened':
      return `PR opened: ${(p.prId as string) ?? ''}`;
    case 'task.budget-warning':
      return `over budget: ${(p.actual as number) ?? '?'} / ${(p.expected as number) ?? '?'}`;
    case 'queue.acked':
    case 'queue.routed':
    case 'queue.picked-up':
    case 'queue.completed':
      return `queue ${e.kind.replace('queue.', '')}${p.crewId ? ` → ${p.crewId}` : ''}`;
    default:
      try { return JSON.stringify(p).slice(0, 80); } catch { return ''; }
  }
}
