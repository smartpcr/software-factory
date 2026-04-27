import type { Card as CardModel, Epic, Pr, Workstream } from '@/types';
import { formatRel } from './formatRel';
import { avatarFor, agentTooltip } from '@/agentAvatar';

interface Props {
  ws: Workstream;
  epic: Epic | undefined;
  prs: Pr[];
  cards: CardModel[];
  onOpen: (ws: Workstream) => void;
  onSync: (ws: Workstream) => void;
  onIde?: (ws: Workstream) => void;
  onOpenPr?: (ws: Workstream) => void;
}

function roleFor(id: string): string {
  if (id.startsWith('A')) return 'arch';
  if (id.startsWith('E')) return 'eng';
  if (id.startsWith('R')) return 'rev';
  if (id.startsWith('Q')) return 'qa';
  return 'human';
}

// Verbatim port of the prototype's ws-card block (lines 3818-3909).
export function WorkstreamCard({ ws, epic, prs, cards, onOpen, onSync, onIde, onOpenPr }: Props) {
  const colorVar = epic ? `var(--${epic.color})` : 'var(--text-tertiary)';

  return (
    <div className="ws-card" style={{ '--ws-color': colorVar } as React.CSSProperties} data-ws-id={ws.id}>
      <div className="ws-card-head">
        <div className="ws-card-head-left">
          <div className="ws-card-eyebrow">
            <span className="epic-badge">{ws.epic}</span>
            <span style={{ opacity: 0.4 }}>·</span>
            <span>{epic?.name ?? ws.epic}</span>
          </div>
          <div className="ws-card-name">{ws.name}</div>
          <div className="ws-card-branch">
            <svg width="11" height="11" viewBox="0 0 11 11" fill="none">
              <circle cx="3" cy="2.5" r="1.2" stroke="currentColor" strokeWidth="1.2" fill="none" />
              <circle cx="3" cy="8.5" r="1.2" stroke="currentColor" strokeWidth="1.2" fill="none" />
              <circle cx="8" cy="5.5" r="1.2" stroke="currentColor" strokeWidth="1.2" fill="none" />
              <line x1="3" y1="3.5" x2="3" y2="7.5" stroke="currentColor" strokeWidth="1.2" />
              <path d="M8 4.5 V 4 a 2 2 0 0 0 -2 -2 H 4" stroke="currentColor" strokeWidth="1.2" fill="none" />
            </svg>
            {ws.branch} <span className="arrow">→</span> {ws.base}
          </div>
        </div>
        <span className={`ws-card-status-pill ${ws.status}`}><span className="dot" />{ws.status}</span>
      </div>

      <div style={{ fontSize: 11.5, lineHeight: 1.5, color: 'var(--text-secondary)', marginBottom: 12 }}>
        {ws.summary}
      </div>

      <div className="ws-card-worktree">
        <div className="ws-card-worktree-path">
          {ws.worktree.length === 1 ? (
            <span style={{ color: 'var(--text-tertiary)', fontStyle: 'italic' }}>{ws.worktree[0]}</span>
          ) : (
            <>{ws.worktree[0]}\<span className="seg-active">{ws.worktree[1]}</span></>
          )}
        </div>
      </div>

      <div className="ws-card-stats">
        <div className="ws-stat">
          <div className="ws-stat-label">Commits</div>
          <div className="ws-stat-val">{ws.commits}</div>
        </div>
        <div className="ws-stat">
          <div className="ws-stat-label">Ahead / behind</div>
          <div className={`ws-stat-val ${ws.behind ? 'behind' : 'ahead'}`}>
            {ws.ahead}
            <span style={{ fontSize: 10, color: 'var(--text-tertiary)' }}> / </span>
            <span style={{ fontSize: 12, color: ws.behind ? 'var(--danger)' : 'var(--text-tertiary)' }}>{ws.behind}</span>
          </div>
        </div>
        <div className="ws-stat">
          <div className="ws-stat-label">Conflicts</div>
          <div className={`ws-stat-val ${ws.conflicts ? 'conflicts' : ''}`}>{ws.conflicts}</div>
        </div>
      </div>

      <div className="ws-card-section">
        <div className="ws-card-section-title">
          <span>Pull requests</span>
          <span style={{ fontFamily: 'var(--font-mono)', color: 'var(--text-tertiary)', fontWeight: 500 }}>
            {ws.prs.length}
          </span>
        </div>
        {ws.prs.length === 0 ? (
          <div style={{ fontSize: 11, color: 'var(--text-tertiary)', padding: '4px 8px', fontStyle: 'italic' }}>
            No PRs yet
          </div>
        ) : (
          ws.prs.map((prId) => {
            const pr = prs.find((p) => p.id === `PR ${prId}` || p.id === prId);
            if (!pr) return null;
            const stage =
              pr.col === 'implementation' ? 'impl'
              : pr.col === 'review' ? 'review'
              : pr.col === 'build' ? 'build'
              : 'merge';
            return (
              <div className="ws-pr-row" key={pr.id}>
                <span className="id">{pr.id}</span>
                <span className="title">{pr.title}</span>
                <span className={`stage ${stage}`}>{pr.col}</span>
              </div>
            );
          })
        )}
      </div>

      <div className="ws-card-section">
        <div className="ws-card-section-title"><span>Crew · cards</span></div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', padding: '4px 0' }}>
          <div className="agent-stack" style={{ display: 'flex' }}>
            {ws.agents.map((a) => {
              const role = roleFor(a);
              const av = avatarFor(a);
              const tip = agentTooltip(a, undefined, role);
              return av ? (
                <img
                  key={a}
                  src={av}
                  alt={a}
                  className={`agent-chip-avatar ${role}`}
                  style={{ borderColor: 'var(--bg-surface)' }}
                  title={tip}
                />
              ) : (
                <span
                  key={a}
                  className={`agent-chip ${role}`}
                  style={{ borderColor: 'var(--bg-surface)' }}
                  title={tip}
                >
                  {a}
                </span>
              );
            })}
          </div>
          <span style={{ opacity: 0.4 }}>·</span>
          <div style={{ display: 'flex', flexWrap: 'wrap' }}>
            {ws.cards.map((cid) => {
              const c = cards.find((x) => x.id === cid);
              if (!c) return null;
              return (
                <span
                  key={cid}
                  className="agent-chip"
                  style={{
                    background: 'var(--bg-sunken)',
                    color: 'var(--text-secondary)',
                    fontSize: 8,
                    width: 'auto',
                    height: 'auto',
                    padding: '2px 5px',
                    borderRadius: 3,
                    marginRight: 3,
                    borderStyle: 'solid',
                  }}
                >
                  {c.id}
                </span>
              );
            })}
          </div>
        </div>
      </div>

      <div className="ws-card-foot">
        <div style={{ fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-tertiary)' }}>
          {formatRel(ws.lastActive)}
        </div>
        <div className="ws-card-actions">
          <button className="ws-action primary" onClick={() => onOpen(ws)}>Open</button>
          <button className="ws-action" onClick={() => onSync(ws)}>Sync</button>
          {onOpenPr && (
            <button className="ws-action" onClick={() => onOpenPr(ws)} title="Open PR (Phase 6.4)">PR</button>
          )}
          <button className="ws-action" onClick={() => onIde?.(ws)}>IDE</button>
        </div>
      </div>
    </div>
  );
}
