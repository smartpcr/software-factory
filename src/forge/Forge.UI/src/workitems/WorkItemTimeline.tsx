import { useMemo } from 'react';
import type { WorkItem, WorkItemState } from './workItemTypes';
import { useWitStore } from './useWitStore';
import { TypeMark } from './TypeMark';
import { SAMPLE_ITERATIONS } from './sampleWorkItems';

interface Props {
  items: WorkItem[];
}

// Phase 12 — Timeline mode. Gantt-style strips, one row per item, x-axis is
// the union of iterations the items belong to. Diagonal hatching denotes
// proposed (planning) spans; solid for active; outlined for resolved/closed.
// The visual is deliberately rule-and-stripe rather than chart-library
// candy — keeps the editorial register of the rest of the registry.

export function WorkItemTimeline({ items }: Props) {
  // Group items by epic (or by iteration for orphans).
  const groups = useMemo(() => groupByEpic(items), [items]);

  // Iteration columns: keep the original SAMPLE_ITERATIONS order so the chart
  // axis is stable regardless of which subset of items is filtered.
  const cols = SAMPLE_ITERATIONS;
  const totalCols = cols.length;

  return (
    <div className="wit-timeline">
      <header className="wit-timeline-axis">
        <div className="wit-timeline-axis-spine" />
        <div className="wit-timeline-axis-cols" style={{ '--cols': totalCols } as React.CSSProperties}>
          {cols.map((it) => (
            <div key={it.ref} className="wit-timeline-axis-col">
              <span className="wit-timeline-axis-label">{it.label}</span>
              <span className="wit-timeline-axis-dates">
                {fmtRange(it.startsAt, it.endsAt)}
              </span>
            </div>
          ))}
        </div>
      </header>

      <div className="wit-timeline-body">
        {groups.length === 0 && (
          <div className="wit-timeline-empty">No items in scope for this filter.</div>
        )}
        {groups.map((g) => (
          <div key={g.id} className="wit-timeline-group">
            <div className="wit-timeline-group-head">
              <span className="wit-timeline-group-label">{g.label}</span>
              <span className="wit-timeline-group-count">{g.items.length}</span>
            </div>
            {g.items.map((i) => (
              <TimelineRow key={i.id} item={i} cols={cols} />
            ))}
          </div>
        ))}
      </div>

      <Legend />
    </div>
  );
}

interface Col { ref: string; label: string; startsAt: string; endsAt: string; }

function TimelineRow({ item, cols }: { item: WorkItem; cols: Col[] }) {
  const colIndex = Math.max(0, cols.findIndex((c) => c.ref === item.iterationRef));
  const focus = useWitStore((s) => s.focus);
  const focusedId = useWitStore((s) => s.focusedId);
  const isFocused = focusedId === item.id;
  return (
    <div
      className={`wit-timeline-row${isFocused ? ' focused' : ''}`}
      onClick={() => focus(item.id)}
      tabIndex={0}
      onKeyDown={(e) => { if (e.key === 'Enter') focus(item.id); }}
    >
      <div className="wit-timeline-row-label">
        <TypeMark type={item.type} size={16} />
        <span className="wit-timeline-row-id">{item.id}</span>
        <span className="wit-timeline-row-title">{item.title}</span>
      </div>
      <div className="wit-timeline-row-track" style={{ '--cols': cols.length } as React.CSSProperties}>
        {cols.map((_, i) => (
          <span key={i} className={`wit-timeline-row-cell${i === colIndex ? ' here' : ''}`} aria-hidden />
        ))}
        <Bar colIndex={colIndex} totalCols={cols.length} state={item.state} />
      </div>
    </div>
  );
}

function Bar({ colIndex, totalCols, state }: { colIndex: number; totalCols: number; state: WorkItemState }) {
  const widthPct = 100 / totalCols;
  const leftPct = widthPct * colIndex;
  const fill = barStyleForState(state);
  return (
    <span
      className={`wit-timeline-bar wit-timeline-bar-${state}`}
      style={{
        left: `calc(${leftPct}% + 6px)`,
        width: `calc(${widthPct}% - 12px)`,
        ...fill,
      }}
    />
  );
}

function barStyleForState(state: WorkItemState): React.CSSProperties {
  switch (state) {
    case 'proposed':
      return { background: 'repeating-linear-gradient(135deg, var(--warning-bg) 0 8px, transparent 8px 14px)', border: '1px solid var(--warning-border)' };
    case 'active':
      return { background: 'var(--info)', border: '1px solid var(--info-border)' };
    case 'resolved':
      return { background: 'var(--success-bg)', border: '1px solid var(--success-border)' };
    case 'closed':
      return { background: 'transparent', border: '1px dashed var(--border-strong)' };
  }
}

function Legend() {
  return (
    <div className="wit-timeline-legend">
      <span className="wit-timeline-legend-key">
        <span className="wit-timeline-legend-swatch" style={barStyleForState('proposed') as React.CSSProperties} />
        proposed
      </span>
      <span className="wit-timeline-legend-key">
        <span className="wit-timeline-legend-swatch" style={barStyleForState('active') as React.CSSProperties} />
        active
      </span>
      <span className="wit-timeline-legend-key">
        <span className="wit-timeline-legend-swatch" style={barStyleForState('resolved') as React.CSSProperties} />
        resolved
      </span>
      <span className="wit-timeline-legend-key">
        <span className="wit-timeline-legend-swatch" style={barStyleForState('closed') as React.CSSProperties} />
        closed
      </span>
    </div>
  );
}

interface Group { id: string; label: string; items: WorkItem[]; }

function groupByEpic(items: WorkItem[]): Group[] {
  const byEpic = new Map<string, WorkItem>();
  const byId = new Map<string, WorkItem>();
  for (const i of items) {
    byId.set(i.id, i);
    if (i.type === 'epic') byEpic.set(i.id, i);
  }
  const find = (id: string | null): WorkItem | null => {
    let cur = id ? byId.get(id) ?? null : null;
    while (cur && cur.parentId && cur.type !== 'epic') {
      cur = byId.get(cur.parentId) ?? null;
    }
    return cur && cur.type === 'epic' ? cur : null;
  };
  const groups = new Map<string, Group>();
  for (const i of items) {
    let key = i.id;
    let label: string;
    if (i.type === 'epic') {
      key = i.id;
      label = `${i.id} · ${i.title}`;
    } else {
      const e = find(i.id);
      if (e) {
        key = e.id;
        label = `${e.id} · ${e.title}`;
      } else {
        key = '__orphans__';
        label = 'Unparented';
      }
    }
    if (!groups.has(key)) groups.set(key, { id: key, label, items: [] });
    if (i.type !== 'epic') groups.get(key)!.items.push(i);
  }
  return [...groups.values()].filter((g) => g.items.length > 0);
}

function fmtRange(startsAt: string, endsAt: string): string {
  const s = new Date(startsAt);
  const e = new Date(endsAt);
  const fmt = (d: Date) => `${d.getDate()} ${d.toLocaleString('en-US', { month: 'short' })}`;
  return `${fmt(s)} – ${fmt(e)}`;
}
