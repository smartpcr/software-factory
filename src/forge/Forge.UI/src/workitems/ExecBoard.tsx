// Phase 25 — Agent execution status board. Read-only board with six columns
// corresponding to the execution lifecycle: idle, queued, live, completed,
// failed, cancelled. Each card shows type mark, ID, title, assignee, exec
// badge, and a PR status badge when a linked PR exists.

import { useMemo } from 'react';
import type { WorkItem } from './workItemTypes';
import { EXEC_STATUSES, type ExecStatus } from './workItemTypes';
import { TypeMark } from './TypeMark';
import { ExecBadge } from './ExecBadge';
import { PrStatusBadge } from './PrStatusBadge';
import { useWitStore } from './useWitStore';
import { useBoardStore, type BoardGroupBy } from '@/state/useBoardStore';
import { useExecGroupCollapseStore } from '@/state/useExecGroupCollapseStore';

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

const GROUP_LABEL: Record<BoardGroupBy, string> = {
  story: 'Story', epic: 'Epic', phase: 'Phase', agent: 'Agent', priority: 'Priority',
};

const NONE_KEY = '__none__';

interface ExecGroup { key: string; label: string; items: WorkItem[]; }

// Group the work items by the active GROUP BY field (shared with the legacy
// Board via useBoardStore). story/epic/phase key off `featureRef` (which holds
// the feature/story id, e.g. `local:UPDATE-HISTORY-IN-CL`, and is resolved to
// that feature work item's title when present); agent keys off the WIT
// assignee; priority off the priority field. Items with no key collapse into a
// trailing "No <group>" lane.
function computeExecGroups(items: WorkItem[], groupBy: BoardGroupBy): ExecGroup[] {
  const titleById = new Map(items.map((i) => [i.id, i.title]));
  const noneLabel =
    groupBy === 'agent' ? 'Unassigned'
    : groupBy === 'priority' ? 'No priority'
    : `No ${GROUP_LABEL[groupBy].toLowerCase()}`;

  const keyOf = (i: WorkItem): { key: string; label: string } => {
    switch (groupBy) {
      case 'agent': {
        const id = i.assignedTo?.id ?? null;
        return id ? { key: id, label: i.assignedTo!.displayName } : { key: NONE_KEY, label: noneLabel };
      }
      case 'priority':
        return { key: String(i.priority), label: String(i.priority) };
      case 'story':
      case 'epic':
      case 'phase':
      default: {
        const ref = i.featureRef ?? '';
        return ref ? { key: ref, label: titleById.get(ref) ?? ref } : { key: NONE_KEY, label: noneLabel };
      }
    }
  };

  const map = new Map<string, ExecGroup>();
  for (const i of items) {
    const { key, label } = keyOf(i);
    let g = map.get(key);
    if (!g) { g = { key, label, items: [] }; map.set(key, g); }
    g.items.push(i);
  }
  // Named groups alpha-sorted; the catch-all "None" lane always sinks last.
  return [...map.values()].sort((a, b) => {
    if (a.key === NONE_KEY) return 1;
    if (b.key === NONE_KEY) return -1;
    return a.label.localeCompare(b.label);
  });
}

export function ExecBoard({ items, onOpen }: Props) {
  const groupBy = useBoardStore((s) => s.groupBy);
  const groups = useMemo(() => computeExecGroups(items, groupBy), [items, groupBy]);
  const collapsedMap = useExecGroupCollapseStore((s) => s.collapsed);
  const toggleCollapsed = useExecGroupCollapseStore((s) => s.toggle);

  return (
    <div className="wit-exec-groups">
      {groups.map((g) => {
        const pts = g.items.reduce((n, i) => n + i.points, 0);
        const collapsed = collapsedMap[`${groupBy}:${g.key}`] === true;
        const onToggle = () => toggleCollapsed(groupBy, g.key);
        return (
          <section
            className={`wit-exec-group${collapsed ? ' collapsed' : ''}`}
            key={g.key}
          >
            <header
              className="wit-exec-group-head"
              role="button"
              tabIndex={0}
              aria-expanded={!collapsed}
              aria-label={`${collapsed ? 'Expand' : 'Collapse'} ${GROUP_LABEL[groupBy]} ${g.label}`}
              onClick={onToggle}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault();
                  onToggle();
                }
              }}
            >
              <span className="wit-exec-group-chevron" aria-hidden>{collapsed ? '▸' : '▾'}</span>
              <span className="wit-exec-group-kind">{GROUP_LABEL[groupBy]}</span>
              <h3 className="wit-exec-group-name">{g.label}</h3>
              <span className="wit-exec-group-meta">
                <span className="num">{g.items.length}</span> items · <span className="num">{pts}</span> pts
              </span>
            </header>
            {!collapsed && <ExecBoardGrid items={g.items} onOpen={onOpen} />}
          </section>
        );
      })}
    </div>
  );
}

// The flat six-column execution-status grid, scoped to one group's items.
function ExecBoardGrid({ items, onOpen }: Props) {
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
