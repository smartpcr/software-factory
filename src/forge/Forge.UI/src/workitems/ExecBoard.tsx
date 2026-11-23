// Phase 25 — Agent execution status board. Drag-to-reconcile (added
// 2026-05-30): each card carries a draggable handle; columns are drop
// targets. On drag-end the UI POSTs the target column to
// /api/work-items/{id}/execution-status, which validates the request
// against the authoritative status computed by
// WorkItemExecutionStatusInspector (live runner + open queue rows +
// linked workstream + latest deliverable). On match the WI's stored
// execution_status is reconciled to the truth value; on mismatch the
// server returns 409 with the authoritative value + a short reason and
// the UI surfaces an informative toast. This is drift-correction —
// dragging never cancels live agents or enqueues work.

import { useMemo, useRef, useState } from 'react';
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
import type { WorkItem } from './workItemTypes';
import { EXEC_STATUSES, type ExecStatus } from './workItemTypes';
import { TypeMark } from './TypeMark';
import { ExecBadge } from './ExecBadge';
import { PrStatusBadge } from './PrStatusBadge';
import { useWitStore } from './useWitStore';
import { useBoardStore, type BoardGroupBy } from '@/state/useBoardStore';
import { useExecGroupCollapseStore } from '@/state/useExecGroupCollapseStore';
import { forgeApi, ForgeApiError } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';

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

  // A spawned story also has a work item of its own (id = story id, no
  // featureRef). It names the story lane above; drawing it as a card too put
  // a duplicate of the story under "No story".
  const storyGrouping = groupBy === 'story' || groupBy === 'epic' || groupBy === 'phase';
  const laneIds = new Set(items.map((i) => i.featureRef).filter((r): r is string => !!r));

  const map = new Map<string, ExecGroup>();
  for (const i of items) {
    if (storyGrouping && !i.featureRef && laneIds.has(i.id)) continue;
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

// Drag context. The droppable id pattern is `${groupKey}::${status}` so we
// can reject cross-group drops cheaply in onDragEnd by comparing the
// `over.id` prefix to the `active.id`'s group prefix.
const DRAG_SEPARATOR = '::';

interface DraggableData { groupKey: string; itemId: string; sourceStatus: ExecStatus; }

function dragId(groupKey: string, itemId: string): string {
  return `card${DRAG_SEPARATOR}${groupKey}${DRAG_SEPARATOR}${itemId}`;
}

function dropId(groupKey: string, status: ExecStatus): string {
  return `col${DRAG_SEPARATOR}${groupKey}${DRAG_SEPARATOR}${status}`;
}

function parseDropId(id: string): { groupKey: string; status: ExecStatus } | null {
  const parts = id.split(DRAG_SEPARATOR);
  if (parts.length < 3 || parts[0] !== 'col') return null;
  // groupKey may itself contain '::' so rejoin the middle parts; status is last.
  const status = parts[parts.length - 1] as ExecStatus;
  const groupKey = parts.slice(1, -1).join(DRAG_SEPARATOR);
  if (!EXEC_STATUSES.includes(status)) return null;
  return { groupKey, status };
}

// Test-only exports. The leading double-underscore matches the convention
// used by useTaskLiveStore for unit-testable internals that aren't part of
// the public component surface.
export const __testing = {
  computeExecGroups,
  dragId,
  dropId,
  parseDropId,
  reconcileExecutionStatusFromDrag,
};

export function ExecBoard({ items, onOpen }: Props) {
  const groupBy = useBoardStore((s) => s.groupBy);
  const groups = useMemo(() => computeExecGroups(items, groupBy), [items, groupBy]);
  const collapsedMap = useExecGroupCollapseStore((s) => s.collapsed);
  const toggleCollapsed = useExecGroupCollapseStore((s) => s.toggle);

  // 8-pixel activation distance matches WorkItemBoard — far enough to
  // avoid accidental drags on a sticky-trackpad click (so card click still
  // opens the detail panel), close enough that a deliberate move starts
  // the drag immediately.
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 8 } }));

  const [, setDraggingId] = useState<string | null>(null);
  const [draggingItem, setDraggingItem] = useState<WorkItem | null>(null);
  const [draggingGroupKey, setDraggingGroupKey] = useState<string | null>(null);

  // Suppresses the post-drag onClick that would otherwise fire on the card
  // and open the detail panel (synthetic click follows pointerup even after
  // a successful drag). Cleared on the next pointerdown by useDraggable.
  const justDraggedRef = useRef(false);

  function onDragStart(e: DragStartEvent) {
    const data = e.active.data.current as DraggableData | undefined;
    if (!data) return;
    setDraggingId(String(e.active.id));
    setDraggingItem(items.find((i) => i.id === data.itemId) ?? null);
    setDraggingGroupKey(data.groupKey);
  }

  function clearDragState() {
    setDraggingId(null);
    setDraggingItem(null);
    setDraggingGroupKey(null);
  }

  function onDragCancel() {
    clearDragState();
  }

  function onDragEnd(e: DragEndEvent) {
    const data = e.active.data.current as DraggableData | undefined;
    const item = data ? items.find((i) => i.id === data.itemId) ?? null : null;
    clearDragState();
    justDraggedRef.current = true;
    // Clear the click-suppression on the next tick so a fresh click after
    // the drag still works as expected.
    window.setTimeout(() => { justDraggedRef.current = false; }, 0);

    if (!data || !item || !e.over) return;
    const drop = parseDropId(String(e.over.id));
    if (!drop) return;
    // Cross-group drag rejected — exec status is per-item but the operator's
    // mental model groups by story/epic/phase; allowing cross-group drops
    // would silently mis-route an unrelated card.
    if (drop.groupKey !== data.groupKey) return;
    // Same-column drop — no-op, no API call, no toast.
    if (drop.status === data.sourceStatus) return;

    void reconcileExecutionStatusFromDrag(item, drop.status);
  }

  return (
    <DndContext
      sensors={sensors}
      onDragStart={onDragStart}
      onDragEnd={onDragEnd}
      onDragCancel={onDragCancel}
    >
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
              {!collapsed && (
                <ExecBoardGrid
                  items={g.items}
                  onOpen={onOpen}
                  groupKey={g.key}
                  draggingGroupKey={draggingGroupKey}
                  draggingSourceStatus={draggingItem?.executionStatus as ExecStatus | undefined}
                  justDraggedRef={justDraggedRef}
                />
              )}
            </section>
          );
        })}
      </div>
      <DragOverlay dropAnimation={null}>
        {draggingItem ? <ExecCard item={draggingItem} dragging /> : null}
      </DragOverlay>
    </DndContext>
  );
}

async function reconcileExecutionStatusFromDrag(item: WorkItem, target: ExecStatus): Promise<void> {
  const toast = useToastStore.getState();
  try {
    const result = await forgeApi.reconcileExecutionStatus(item.id, target);
    if (result.previous === result.current) {
      // Server treated it as a no-op (stored value already matched target).
      toast.show(`${item.id} already ${target}`, 'success');
    } else {
      toast.show(`${item.id} reconciled: ${result.previous} → ${result.current}`, 'success');
    }
  } catch (err) {
    if (err instanceof ForgeApiError && err.status === 409) {
      const problem = (err.problem ?? {}) as {
        authoritative?: string;
        reason?: string;
        requested?: string;
      };
      const actual = problem.authoritative ?? 'unknown';
      const reason = problem.reason ?? 'state does not match';
      toast.show(
        `Can't move ${item.id} to ${target}: actual status is ${actual} — ${reason}`,
        'danger',
      );
    } else {
      const msg = err instanceof Error ? err.message : 'Reconcile failed';
      toast.show(`Reconcile failed: ${msg}`, 'danger');
    }
  }
}

interface ExecBoardGridProps {
  items: WorkItem[];
  onOpen?: (id: string) => void;
  groupKey: string;
  draggingGroupKey: string | null;
  draggingSourceStatus: ExecStatus | undefined;
  justDraggedRef: React.MutableRefObject<boolean>;
}

// The flat six-column execution-status grid, scoped to one group's items.
function ExecBoardGrid({
  items,
  onOpen,
  groupKey,
  draggingGroupKey,
  draggingSourceStatus,
  justDraggedRef,
}: ExecBoardGridProps) {
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

  // Only the dragging-source group's columns should glow as drop targets,
  // and only for statuses that aren't the source column itself.
  const isThisGroupDragging = draggingGroupKey === groupKey;

  return (
    <div className="wit-exec-board">
      {EXEC_STATUSES.map((status) => (
        <ExecColumn
          key={status}
          status={status}
          items={buckets[status]}
          total={items.length}
          onOpen={onOpen}
          groupKey={groupKey}
          isDropTarget={isThisGroupDragging && draggingSourceStatus !== status}
          justDraggedRef={justDraggedRef}
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
  groupKey,
  isDropTarget,
  justDraggedRef,
}: {
  status: ExecStatus;
  items: WorkItem[];
  total: number;
  onOpen?: (id: string) => void;
  groupKey: string;
  isDropTarget: boolean;
  justDraggedRef: React.MutableRefObject<boolean>;
}) {
  const copy = EXEC_COPY[status];
  const points = items.reduce((n, i) => n + i.points, 0);
  const { isOver, setNodeRef } = useDroppable({ id: dropId(groupKey, status) });

  const className = [
    'wit-board-col',
    `wit-exec-col-${status}`,
    isOver && 'over',
    isDropTarget && 'drop-target',
  ].filter(Boolean).join(' ');

  return (
    <section ref={setNodeRef} className={className} aria-label={copy.label}>
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
          <DraggableExecCard
            key={item.id}
            item={item}
            groupKey={groupKey}
            onOpen={onOpen}
            justDraggedRef={justDraggedRef}
          />
        ))}
      </div>
      {isDropTarget && (
        <div className={`wit-board-col-dropzone${isOver ? ' over' : ''}`} aria-hidden>
          <span>drop to mark <strong>{copy.label.toLowerCase()}</strong></span>
        </div>
      )}
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

function DraggableExecCard({
  item,
  groupKey,
  onOpen,
  justDraggedRef,
}: {
  item: WorkItem;
  groupKey: string;
  onOpen?: (id: string) => void;
  justDraggedRef: React.MutableRefObject<boolean>;
}) {
  const sourceStatus = ((item.executionStatus as ExecStatus) || 'idle');
  const data: DraggableData = { groupKey, itemId: item.id, sourceStatus };
  const { attributes, listeners, setNodeRef, transform, isDragging } = useDraggable({
    id: dragId(groupKey, item.id),
    data,
  });

  const style: React.CSSProperties = transform
    ? { transform: `translate3d(${transform.x}px, ${transform.y}px, 0)`, opacity: isDragging ? 0.4 : 1 }
    : { opacity: isDragging ? 0.4 : 1 };

  return (
    <div ref={setNodeRef} style={style} {...attributes} {...listeners}>
      <ExecCard item={item} onOpen={onOpen} justDraggedRef={justDraggedRef} />
    </div>
  );
}

function ExecCard({
  item,
  onOpen,
  dragging,
  justDraggedRef,
}: {
  item: WorkItem;
  onOpen?: (id: string) => void;
  dragging?: boolean;
  justDraggedRef?: React.MutableRefObject<boolean>;
}) {
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
    dragging && 'dragging',
  ].filter(Boolean).join(' ');

  const handleClick = () => {
    // Suppress the synthetic click that follows a successful drag — without
    // this, every drop would also open the detail panel.
    if (justDraggedRef?.current) return;
    onOpen?.(item.id);
  };

  return (
    <article
      className={className}
      onClick={handleClick}
      tabIndex={0}
      onKeyDown={(e) => { if (e.key === 'Enter') onOpen?.(item.id); }}
      style={{ cursor: dragging ? 'grabbing' : 'pointer' }}
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
