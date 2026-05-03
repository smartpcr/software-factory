import { useDraggable } from '@dnd-kit/core';
import type { Card as CardModel } from '@/types';
import { useBoardStore } from '@/state/useBoardStore';
import { useWorkItemsStore } from '@/workitems/useWorkItemsStore';
import { StatePill } from '@/workitems/StatePill';
import { ExecBadge } from '@/workitems/ExecBadge';
import { avatarFor, agentTooltip } from '@/agentAvatar';
import {
  BlockIcon,
  BoltIcon,
  CardMoreIcon,
  CardOpenIcon,
  ShieldIcon,
  TypeIcon,
} from './icons';

function AgentChip({ id, name, role }: { id: string | null; name?: string | null; role?: string | null }) {
  const avatar = avatarFor(id, name);
  const tooltip = agentTooltip(id, name, role);
  const fallbackLabel = id ?? (name ? name.slice(0, 2).toUpperCase() : '?');
  if (avatar) {
    return (
      <img
        src={avatar}
        alt={name || fallbackLabel}
        className={`agent-chip-avatar ${role ?? ''}`}
        title={tooltip}
      />
    );
  }
  // No portrait — fall back to the text chip but keep the rich tooltip so
  // the operator still gets id · role · model · capabilities on hover.
  return <span className={`agent-chip ${role ?? ''}`} title={tooltip}>{fallbackLabel}</span>;
}

interface CardProps {
  card: CardModel;
}

// Verbatim port of the prototype's renderCard (lines 3384-3440).
export function Card({ card }: CardProps) {
  const selected = useBoardStore((s) => s.selectedId === card.id);
  const setSelected = useBoardStore((s) => s.setSelected);
  const wiState = useWorkItemsStore((s) => s.items.find((w) => w.id === card.id)?.state ?? null);
  const wiExecStatus = useWorkItemsStore((s) => s.items.find((w) => w.id === card.id)?.executionStatus ?? null);
  const wiTrialCount = useWorkItemsStore((s) => s.items.find((w) => w.id === card.id)?.trialCount ?? 0);

  const { attributes, listeners, setNodeRef, isDragging } = useDraggable({
    id: card.id,
    data: { fromCol: card.col, fromEpic: card.epic },
  });

  const cls = [
    'card',
    `type-${card.type}`,
    card.humanGate ? 'human-gate' : '',
    card.blocked ? 'blocked' : '',
    selected ? 'selected' : '',
    isDragging ? 'dragging' : '',
  ].filter(Boolean).join(' ');

  return (
    <div
      ref={setNodeRef}
      className={cls}
      data-id={card.id}
      data-epic={card.epic}
      data-col={card.col}
      {...listeners}
      {...attributes}
      onPointerUp={(e) => {
        if (isDragging) return;
        if ((e.target as HTMLElement).closest('.card-action')) return;
        setSelected(card.id);
      }}
    >
      <div className="card-row1">
        <div className="card-id-group">
          <span className={`card-type-icon ${card.type}`}><TypeIcon type={card.type} /></span>
          <span className="card-id">{card.id}</span>
          {card.priority !== 'low' && <span className={`card-priority ${card.priority}`} />}
          {wiState && <StatePill state={wiState} size="sm" />}
          <ExecBadge status={wiExecStatus ?? undefined} trialCount={wiTrialCount} size={wiExecStatus && wiExecStatus !== 'idle' ? 'md' : 'sm'} />
        </div>
        <div className="card-actions">
          <button className="card-action" title="Open" data-action="open"><CardOpenIcon /></button>
          <button className="card-action" title="More" data-action="more"><CardMoreIcon /></button>
        </div>
      </div>
      <div className="card-title">{card.title}</div>
      {card.tags && card.tags.length > 0 && (
        <div className="card-tags">
          {card.tags.map((t) => (
            <span key={t} className={`tag tag-${t}`}>{t}</span>
          ))}
        </div>
      )}
      {card.live && card.progress != null && (
        <div className="card-progress">
          <div className="card-progress-row"><span className="live">working</span><span>{card.progress}%</span></div>
          <div className="card-progress-bar"><div className="card-progress-fill shimmer" style={{ width: `${card.progress}%` }} /></div>
        </div>
      )}
      {card.humanGate ? (
        <div className="card-human-gate"><ShieldIcon /> Human approval · {card.gateReason}</div>
      ) : card.blocked ? (
        <div className="card-blocked-reason"><BlockIcon /> {card.blockedReason}</div>
      ) : card.autoNext ? (
        <div className="card-auto"><BoltIcon /> {card.autoNext}</div>
      ) : null}
      <div className="card-foot">
        <div className="card-foot-left">
          {card.agent ? (
            <>
              <AgentChip id={card.agent.id} name={card.agent.name} role={card.agent.role} />
              <span>{card.agent.name}</span>
            </>
          ) : (
            <>
              <span className="agent-chip unassigned">·</span>
              <span style={{ color: 'var(--text-tertiary)' }}>unassigned</span>
            </>
          )}
        </div>
        <div className="card-foot-right">
          {card.cost > 0 && <span className="card-cost">${card.cost.toFixed(2)}</span>}
          <span className="card-pts">{card.pts}pt</span>
        </div>
      </div>
    </div>
  );
}
