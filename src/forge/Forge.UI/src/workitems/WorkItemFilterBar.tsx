import { useMemo } from 'react';
import type { WorkItem, WorkItemState, WorkItemType } from './workItemTypes';
import type { WitChip } from './useWitStore';
import { useWitStore } from './useWitStore';
import { SAMPLE_ITERATIONS, SAMPLE_REPOS } from './sampleWorkItems';
import { typeLabel } from './workItemTypes';

interface Props {
  items: WorkItem[];
  /** Items after chip filter — used to size the secondary selects. */
  visibleItems: WorkItem[];
}

const CHIPS: Array<{ id: WitChip; label: string }> = [
  { id: 'all',      label: 'All' },
  { id: 'mine',     label: 'Mine' },
  { id: 'active',   label: 'Active' },
  { id: 'blocked',  label: 'Blocked' },
  { id: 'watching', label: 'Watching' },
];

export function WorkItemFilterBar({ items, visibleItems }: Props) {
  const filters = useWitStore((s) => s.filters);
  const setFilters = useWitStore((s) => s.setFilters);

  // Counts for chip badges. Use the unfiltered registry so the badge tells
  // the operator what's *available* to filter into, not what the current
  // chip already shows.
  const counts = useMemo(() => ({
    all:      items.length,
    mine:     items.filter((i) => i.assignedTo?.id === filters.meId).length,
    active:   items.filter((i) => i.state === 'active').length,
    blocked:  items.filter((i) => i.blocked).length,
    watching: items.filter((i) => i.watching).length,
  }), [items, filters.meId]);

  // Distinct assignees for the assignee select.
  const assignees = useMemo(() => {
    const map = new Map<string, string>();
    for (const i of items) {
      if (i.assignedTo) map.set(i.assignedTo.id, i.assignedTo.displayName);
    }
    return [...map.entries()].sort((a, b) => a[1].localeCompare(b[1]));
  }, [items]);

  return (
    <div className="filter-bar wit-filter-bar">
      <div className="filter-chips">
        {CHIPS.map((c) => (
          <button
            key={c.id}
            className={`filter-chip ${filters.chip === c.id ? 'active' : ''}`}
            onClick={() => setFilters({ chip: c.id })}
            type="button"
          >
            {c.label}
            <span className="count">{counts[c.id]}</span>
          </button>
        ))}
      </div>

      <div className="wit-filter-selects">
        <Select
          label="Type"
          value={filters.type}
          onChange={(v) => setFilters({ type: v as WorkItemType | 'any' })}
          options={[
            { value: 'any', label: 'All' },
            ...(['epic', 'feature', 'user-story', 'task', 'bug'] as WorkItemType[]).map((t) => ({
              value: t,
              label: typeLabel(t),
            })),
          ]}
        />
        <Select
          label="State"
          value={filters.state}
          onChange={(v) => setFilters({ state: v as WorkItemState | 'any' })}
          options={[
            { value: 'any', label: 'Any' },
            { value: 'proposed', label: 'Proposed' },
            { value: 'active',   label: 'Active' },
            { value: 'resolved', label: 'Resolved' },
            { value: 'closed',   label: 'Closed' },
          ]}
        />
        <Select
          label="Iteration"
          value={filters.iterationRef}
          onChange={(v) => setFilters({ iterationRef: v })}
          options={[
            { value: 'any', label: 'Any iteration' },
            ...SAMPLE_ITERATIONS.map((it) => ({ value: it.ref, label: it.label })),
          ]}
        />
        <Select
          label="Assignee"
          value={filters.assignee}
          onChange={(v) => setFilters({ assignee: v })}
          options={[
            { value: 'any', label: 'Any assignee' },
            { value: '__unassigned__', label: '— unassigned' },
            ...assignees.map(([id, name]) => ({ value: id, label: `${id} · ${name}` })),
          ]}
        />
        <Select
          label="Repo"
          value={filters.repoId}
          onChange={(v) => setFilters({ repoId: v })}
          options={[
            { value: 'any', label: 'Any repo' },
            ...SAMPLE_REPOS.map((r) => ({ value: r.id, label: `${r.displayName} · ${r.kind === 'ado' ? 'ADO' : 'GH'}` })),
          ]}
        />
      </div>

      <div className="wit-filter-search">
        <svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden>
          <circle cx="5.5" cy="5.5" r="3.5" stroke="currentColor" strokeWidth="1.3" />
          <line x1="8.4" y1="8.4" x2="10.5" y2="10.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
        </svg>
        <input
          value={filters.q}
          onChange={(e) => setFilters({ q: e.target.value })}
          placeholder="Search work items…"
          aria-label="Search work items"
        />
        <span className="wit-filter-result-count">
          {visibleItems.length} / {items.length}
        </span>
      </div>
    </div>
  );
}

interface SelectProps {
  label: string;
  value: string;
  onChange: (v: string) => void;
  options: Array<{ value: string; label: string }>;
}

function Select({ label, value, onChange, options }: SelectProps) {
  return (
    <label className="wit-select">
      <span className="wit-select-lbl">{label}</span>
      <select value={value} onChange={(e) => onChange(e.target.value)}>
        {options.map((o) => (
          <option key={o.value} value={o.value}>{o.label}</option>
        ))}
      </select>
    </label>
  );
}
