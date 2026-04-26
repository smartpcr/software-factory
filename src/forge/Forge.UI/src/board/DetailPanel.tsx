import { useNavigate } from 'react-router-dom';
import type { Card as CardModel } from '@/types';
import { useBoardStore } from '@/state/useBoardStore';
import { useWorkstreamsStore } from '@/state/useWorkstreamsStore';

// Verbatim port of the prototype's <aside class="detail-panel"> static
// markup (lines 2899-2934) and renderDetailBody / renderDetailActions
// (lines 3591-3668).

interface ActivityRow {
  dot: string;
  line: React.ReactNode;
  time: string;
}

function buildActivity(card: CardModel): ActivityRow[] {
  const items: ActivityRow[] = [];
  if (card.col === 'done') {
    items.push({ dot: 'system', line: <><span className="who">CI</span> all checks passed · merged to main</>, time: card.doneAt ?? 'recently' });
    items.push({ dot: 'rev',    line: <><span className="who">R-03 Cortex</span> approved PR</>, time: '12m before merge' });
  }
  if (['review', 'done'].includes(card.col)) {
    items.push({
      dot: card.agent ? card.agent.role : 'eng',
      line: <><span className="who">{card.agent ? `${card.agent.id} ${card.agent.name}` : 'agent'}</span> opened pull request</>,
      time: '2h ago',
    });
  }
  if (['build', 'review', 'done'].includes(card.col)) {
    items.push({ dot: 'eng', line: <><span className="who">E-04 Forge</span> pushed 3 commits</>, time: '3h ago' });
    items.push({ dot: 'eng', line: <><span className="who">E-04 Forge</span> created branch ws/oidc-rotation off main</>, time: '3h ago' });
  }
  if (['design', 'build', 'review', 'done'].includes(card.col)) {
    items.push({ dot: 'arch', line: <><span className="who">A-01 Atlas</span> approved design spec</>, time: '5h ago' });
    items.push({ dot: 'arch', line: <><span className="who">A-01 Atlas</span> drafted ADR-019 · key rotation strategy</>, time: '6h ago' });
  }
  items.push({ dot: 'human',  line: <><span className="who">Xiaodong</span> created from Epic-024 decomposition</>, time: '1d ago' });
  items.push({ dot: 'system', line: <><span className="who">ADO sync</span> linked to Epic-024 work item</>, time: '1d ago' });
  return items;
}

function DetailActions({ card }: { card: CardModel }) {
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
      <button className="btn btn-secondary">Reassign</button>
      <button className="btn btn-ghost">View trace →</button>
    </>
  );
  if (card.blocked) return (
    <>
      <button className="btn btn-primary">Unblock</button>
      <button className="btn btn-secondary">Reassign</button>
      <button className="btn btn-ghost">View blocker →</button>
    </>
  );
  if (card.col === 'backlog') return (
    <>
      <button className="btn btn-primary">Assign to agent</button>
      <button className="btn btn-secondary">Edit</button>
      <button className="btn btn-ghost">Archive →</button>
    </>
  );
  return (
    <>
      <button className="btn btn-secondary">Edit</button>
      <button className="btn btn-secondary">Reassign</button>
      <button className="btn btn-ghost">Open in ADO →</button>
    </>
  );
}

const STATIC_DESC =
  'Implement automatic OIDC token rotation with key rollover. The handler should rotate signing keys on a 24-hour cadence while maintaining a 60-second grace period for in-flight tokens signed with the previous key. All rotation events must emit audit logs and OTel counters for observability.';

export function DetailPanel() {
  const selectedId = useBoardStore((s) => s.selectedId);
  const card = useBoardStore((s) => s.cards.find((c) => c.id === s.selectedId)) ?? null;
  const setSelected = useBoardStore((s) => s.setSelected);
  const workstreams = useWorkstreamsStore((s) => s.workstreams);
  const focusWs = useWorkstreamsStore((s) => s.setFocused);
  const navigate = useNavigate();
  const linkedWs = card?.branch ? workstreams.find((w) => w.branch === card.branch) : undefined;

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
                <span id="detail-epic">{safe.epic}</span>
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
                {safe.agent && (
                  <span className={`agent-chip ${safe.agent.role}`} style={{ marginRight: 2 }}>
                    {safe.agent.id}
                  </span>
                )}
                <span id="detail-agent">{safe.agent ? safe.agent.name : 'unassigned'}</span>
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

            <div className="detail-section" style={{ marginTop: 22 }}>
              <div className="detail-section-title">Description</div>
              <div className="detail-desc">{STATIC_DESC}</div>
            </div>

            <div className="detail-section">
              <div className="detail-section-title">Acceptance criteria <span style={{ fontFamily: 'var(--font-mono)', color: 'var(--text-tertiary)', fontWeight: 400 }}>2 / 4</span></div>
              <ul className="detail-criteria">
                <li><span className="check done"><svg width="12" height="12" viewBox="0 0 12 12"><path d="M2 6 L 5 9 L 10 3" stroke="currentColor" strokeWidth="1.6" fill="none" strokeLinecap="round" /></svg></span> Token rotation triggers automatically every 24h</li>
                <li><span className="check done"><svg width="12" height="12" viewBox="0 0 12 12"><path d="M2 6 L 5 9 L 10 3" stroke="currentColor" strokeWidth="1.6" fill="none" strokeLinecap="round" /></svg></span> Old keys remain valid for 60s grace period</li>
                <li><span className="check"><svg width="12" height="12" viewBox="0 0 12 12"><circle cx="6" cy="6" r="4" stroke="currentColor" strokeWidth="1.3" fill="none" /></svg></span> Audit log entry on every rotation</li>
                <li><span className="check"><svg width="12" height="12" viewBox="0 0 12 12"><circle cx="6" cy="6" r="4" stroke="currentColor" strokeWidth="1.3" fill="none" /></svg></span> Telemetry counters expose rotation latency</li>
              </ul>
            </div>

            <div className="detail-section">
              <div className="detail-section-title">
                <span>Agent reasoning trace</span>
                {safe.agent && safe.agent.role !== 'human' && (
                  <span style={{ fontFamily: 'var(--font-mono)', color: 'var(--success)' }}>
                    <span style={{ display: 'inline-block', width: 5, height: 5, borderRadius: '50%', background: 'var(--success)', marginRight: 4, animation: 'pulse 1.4s ease-in-out infinite' }} />
                    live
                  </span>
                )}
              </div>
              {safe.agent && safe.agent.role !== 'human' ? (
                <div className="reasoning-trace">
                  <div className="reasoning-step"><span className="reasoning-step-num">01</span><div className="reasoning-step-body">Read AC + parent epic. Identified 4 sub-concerns: rotation timer, key retention window, audit emission, telemetry.</div></div>
                  <div className="reasoning-step"><span className="reasoning-step-num">02</span><div className="reasoning-step-body">Searched repo for existing rotation code in /auth/. Found related code in auth/keys.go which I'm extending rather than replacing.</div></div>
                  <div className="reasoning-step"><span className="reasoning-step-num">03</span><div className="reasoning-step-body">Drafting implementation. Using ticker.NewTicker for rotation cadence; key store gets new RotateAt field; old keys held in rotating window keyed on issued-at.</div></div>
                  <div className="reasoning-step" style={{ opacity: 0.6 }}><span className="reasoning-step-num">04</span><div className="reasoning-step-body" style={{ fontStyle: 'italic' }}>→ Will write unit tests next, then open PR.</div></div>
                </div>
              ) : (
                <div style={{ fontSize: 11.5, color: 'var(--text-secondary)', fontStyle: 'italic' }}>
                  No agent assigned · awaiting pickup from backlog or human action.
                </div>
              )}
            </div>

            <div className="detail-section">
              <div className="detail-section-title">Activity</div>
              <ul className="activity">
                {buildActivity(safe).map((i, idx) => (
                  <li className="activity-item" key={idx}>
                    <span className={`activity-dot ${i.dot}`} />
                    <div className="activity-body">
                      <div className="activity-line">{i.line}</div>
                      <div className="activity-time">{i.time}</div>
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          </div>

          <div className="detail-actions" id="detail-actions">
            <DetailActions card={safe} />
          </div>
        </>
      )}
    </aside>
  );
}
