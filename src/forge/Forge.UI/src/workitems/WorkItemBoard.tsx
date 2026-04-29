import { useState } from 'react';
import {
  DndContext,
  DragOverlay,
  PointerSensor,
  useDraggable,
  useDroppable,
  useSensor,
  useSensors,
  type DragEndEvent,
  type DragStartEvent,
} from '@dnd-kit/core';
import type { WorkItem, WorkItemState } from './workItemTypes';
import { useWitStore } from './useWitStore';
import { useWorkItemsStore } from './useWorkItemsStore';
import { TypeMark } from './TypeMark';
import { StatePill } from './StatePill';
import { ExecBadge } from './ExecBadge';
import { RepoIcon } from '@/products/RepoIcon';
import { SAMPLE_REPOS } from './sampleWorkItems';

interface Props {
  items: WorkItem[];
}

const STATE_ORDER: WorkItemState[] = ['proposed', 'active', 'in-review', 'resolved', 'closed'];

const STATE_COPY: Record<WorkItemState, { label: string; sub: string }> = {
  proposed:    { label: 'Proposed',   sub: 'Awaiting triage' },
  active:      { label: 'Active',     sub: 'In flight' },
  'in-review': { label: 'In Review',  sub: 'PR under review' },
  resolved:    { label: 'Resolved',   sub: 'Awaiting close' },
  closed:      { label: 'Closed',     sub: 'Done or removed' },
};

// Phase 12 — board mode with drag-drop between state columns. Uses dnd-kit
// pointer sensor; release over a column commits the new state via the
// witStore overrides map (Phase 12.6 implementation step plumbs this to
// /api/work-items/{id}/move).
export function WorkItemBoard({ items }: Props) {
  const setItemStateApi = useWorkItemsStore((s) => s.setState);
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 4 } }));
  const [draggingId, setDraggingId] = useState<string | null>(null);

  const buckets: Record<WorkItemState, WorkItem[]> = {
    proposed: [], active: [], 'in-review': [], resolved: [], closed: [],
  };
  for (const i of items) {
    if (buckets[i.state]) buckets[i.state].push(i);
    else buckets.active.push(i); // unknown states fall into active
  }
  for (const k of STATE_ORDER) {
    buckets[k].sort((a, b) => a.title.localeCompare(b.title));
  }

  function onDragStart(e: DragStartEvent) {
    setDraggingId(String(e.active.id));
  }

  function onDragEnd(e: DragEndEvent) {
    setDraggingId(null);
    const id = String(e.active.id);
    const overId = e.over?.id;
    if (!overId) return;
    const target = String(overId);
    if (STATE_ORDER.includes(target as WorkItemState)) {
      const dropped = items.find((i) => i.id === id);
      if (dropped && dropped.state !== target) {
        void setItemStateApi(id, target as WorkItemState);
      }
    }
  }

  const draggingItem = draggingId ? items.find((i) => i.id === draggingId) ?? null : null;

  return (
    <DndContext sensors={sensors} onDragStart={onDragStart} onDragEnd={onDragEnd} onDragCancel={() => setDraggingId(null)}>
      <div className="wit-board">
        {STATE_ORDER.map((state) => (
          <BoardColumn
            key={state}
            state={state}
            items={buckets[state]}
            total={items.length}
            isDropTarget={!!draggingId && draggingItem?.state !== state}
          />
        ))}
      </div>
      <DragOverlay dropAnimation={null}>
        {draggingItem ? <BoardCard item={draggingItem} dragging /> : null}
      </DragOverlay>
    </DndContext>
  );
}

function BoardColumn({
  state,
  items,
  total,
  isDropTarget,
}: {
  state: WorkItemState;
  items: WorkItem[];
  total: number;
  isDropTarget: boolean;
}) {
  const copy = STATE_COPY[state];
  const points = items.reduce((n, i) => n + i.points, 0);
  const { isOver, setNodeRef } = useDroppable({ id: state });

  const className = [
    'wit-board-col',
    isOver && 'over',
    isDropTarget && 'drop-target',
  ].filter(Boolean).join(' ');

  return (
    <section ref={setNodeRef} className={className} aria-label={copy.label}>
      <header className="wit-board-col-head">
        <div className="wit-board-col-title">
          <StatePill state={state} size="sm" />
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
        {items.length === 0 && <EmptyColumn />}
        {items.map((i) => <DraggableBoardCard key={i.id} item={i} />)}
      </div>
      {isDropTarget && (
        <div className={`wit-board-col-dropzone${isOver ? ' over' : ''}`} aria-hidden>
          <span>drop to set <strong>{copy.label.toLowerCase()}</strong></span>
        </div>
      )}
    </section>
  );
}

function EmptyColumn() {
  return (
    <div className="wit-board-empty">
      <div className="line" />
      <span>—</span>
    </div>
  );
}

function DraggableBoardCard({ item }: { item: WorkItem }) {
  const { attributes, listeners, setNodeRef, transform, isDragging } = useDraggable({ id: item.id });
  const style: React.CSSProperties = transform
    ? { transform: `translate3d(${transform.x}px, ${transform.y}px, 0)`, opacity: isDragging ? 0.4 : 1 }
    : { opacity: isDragging ? 0.4 : 1 };
  return (
    <div ref={setNodeRef} style={style} {...attributes} {...listeners}>
      <BoardCard item={item} />
    </div>
  );
}

function BoardCard({ item, dragging }: { item: WorkItem; dragging?: boolean }) {
  const focus = useWitStore((s) => s.focus);
  const focusedId = useWitStore((s) => s.focusedId);
  const selection = useWitStore((s) => s.selection);
  const toggleSelect = useWitStore((s) => s.toggleSelect);

  const repo = SAMPLE_REPOS.find((r) => r.id === item.repoId);
  const selected = selection.has(item.id);
  const focused = focusedId === item.id;

  function onClick(e: React.MouseEvent) {
    if (e.shiftKey || e.metaKey || e.ctrlKey) {
      toggleSelect(item.id, true);
    } else {
      focus(item.id);
    }
  }

  const tagSlice = item.tags.slice(0, 2);
  const overflow = item.tags.length - tagSlice.length;

  const className = [
    'wit-board-card',
    selected && 'selected',
    focused && 'focused',
    item.blocked && 'blocked',
    dragging && 'dragging',
  ].filter(Boolean).join(' ');

  return (
    <article
      className={className}
      onClick={onClick}
      tabIndex={0}
      onKeyDown={(e) => { if (e.key === 'Enter') focus(item.id); }}
    >
      <div className="wit-board-card-row">
        <TypeMark type={item.type} size={20} />
        <span className="wit-board-card-id" title={item.id}>{item.id}</span>
        <StatePill state={item.state} />
        <ExecBadge status={item.executionStatus} trialCount={item.trialCount} size={item.executionStatus === 'idle' ? 'sm' : 'md'} />
        {repo && <RepoIcon kind={repo.kind} size={12} title={repo.displayName} />}
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
