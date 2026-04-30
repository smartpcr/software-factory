// Phase 25 — Agent execution status board. Read-only board with six columns
// corresponding to the execution lifecycle: idle, queued, live, completed,
// failed, cancelled. Each card shows type mark, ID, title, assignee, exec
// badge, and a PR status badge when a linked PR exists.

import type { WorkItem } from './workItemTypes';
import { EXEC_STATUSES, type ExecStatus } from './workItemTypes';
import { TypeMark } from './TypeMark';
import { ExecBadge } from './ExecBadge';
import { PrStatusBadge } from './PrStatusBadge';
import { useWitStore } from './useWitStore';

interface Props {
  items: WorkItem[];
  onOpen?: (id: string) => void;
}

const EXEC_COPY: Record<ExecStatus, { label: string; sub: string }> = {
  idle:      { label: 'Idle',       sub: 'Not started' },
  queued:    { label: 'Queued',     sub: 'Awaiting agent' },
  live:      { label: 'Live',       sub: 'Agent working' },
  completed: { label: 'Completed',  sub: 'Done' },
  failed:    { label: 'Failed',     sub: 'Needs attention' },
  cancelled: { label: 'Cancelled',  sub: 'Stopped' },
};

export function ExecBoard({ items, onOpen }: Props) {
  const buckets: Record<ExecStatus, WorkItem[]> = {
    idle: [], queued: [], live: [], completed: [], failed: [], cancelled: [],
  };
  for (const item of items) {
    const status = (item.executionStatus as ExecStatus) || 'idle';
    if (buckets[status]) buckets[status].push(item);
    else buckets.idle.push(item);
  }
  for (const k of EXEC_STATUSES) {
    buckets[k].sort((a, b) => a.title.localeCompare(b.title));
  }

  return (
    <div className="wit-exec-board">
      {EXEC_STATUSES.map((status) => (
        <ExecColumn
          key={status}
          status={status}
          items={buckets[status]}
          total={items.length}
          onOpen={onOpen}
        />
      ))}
    </div>
  );
}

function ExecColumn({
  status,
  items,
  total,
  onOpen,
}: {
  status: ExecStatus;
  items: WorkItem[];
  total: number;
  onOpen?: (id: string) => void;
}) {
  const copy = EXEC_COPY[status];
  const points = items.reduce((n, i) => n + i.points, 0);

  return (
    <section className={`wit-board-col wit-exec-col-${status}`} aria-label={copy.label}>
      <header className="wit-board-col-head">
        <div className="wit-board-col-title">
          <ExecBadge status={status} size="sm" />
          <h3>{copy.label}</h3>
        </div>
        <div className="wit-board-col-meta">
          <span><span className="num">{items.length}</span><span className="lbl">items</span></span>
          <span><span className="num">{points}</span><span className="lbl">pts</span></span>
          <span className="wit-board-col-pct">
            {total === 0 ? '0' : Math.round((items.length / total) * 100)}%
          </span>
        </div>
      </header>
      <div className="wit-board-col-body">
        {items.length === 0 && <ExecEmptyColumn />}
        {items.map((item) => (
          <ExecCard key={item.id} item={item} onOpen={onOpen} />
        ))}
      </div>
    </section>
  );
}

function ExecEmptyColumn() {
  return (
    <div className="wit-board-empty">
      <div className="line" />
      <span>—</span>
    </div>
  );
}

function ExecCard({ item, onOpen }: { item: WorkItem; onOpen?: (id: string) => void }) {
  const focusedId = useWitStore((s) => s.focusedId);
  const selection = useWitStore((s) => s.selection);
  const tagSlice = item.tags.slice(0, 2);
  const overflow = item.tags.length - tagSlice.length;

  const focused = focusedId === item.id;
  const selected = selection.has(item.id);

  const className = [
    'wit-board-card',
    'wit-exec-card',
    focused && 'focused',
    selected && 'selected',
    item.blocked && 'blocked',
  ].filter(Boolean).join(' ');

  return (
    <article
      className={className}
      onClick={() => onOpen?.(item.id)}
      tabIndex={0}
      onKeyDown={(e) => { if (e.key === 'Enter') onOpen?.(item.id); }}
      style={{ cursor: 'pointer' }}
    >
      <div className="wit-board-card-row">
        <TypeMark type={item.type} size={20} />
        <span className="wit-board-card-id" title={item.id}>{item.id}</span>
        <ExecBadge status={item.executionStatus} trialCount={item.trialCount} size="sm" />
        {item.primaryPrStatus && (
          <PrStatusBadge status={item.primaryPrStatus} prId={item.primaryPrId} size="sm" />
        )}
        <span className="wit-board-card-spacer" />
        {item.points > 0 && <span className="wit-board-card-pts">{item.points}</span>}
      </div>
      <h4 className="wit-board-card-title">{item.title}</h4>
      <div className="wit-board-card-foot">
        {item.assignedTo && (
          <span className="wit-board-card-assignee">
            <span className="wit-board-card-avatar">{item.assignedTo.initials}</span>
            <span className="wit-board-card-assignee-name">{item.assignedTo.displayName}</span>
          </span>
        )}
        <span className="wit-board-card-tags">
          {tagSlice.map((t) => <span key={t} className="wit-tag">{t}</span>)}
          {overflow > 0 && <span className="wit-tag wit-tag-overflow">+{overflow}</span>}
        </span>
      </div>
      {item.blocked && (
        <div className="wit-board-card-blocked">
          <span className="dot" /> blocked
        </div>
      )}
    </article>
  );
}
