import { useEffect, useMemo, useRef, useState } from 'react';
import type { WorkItem, WorkItemState, WorkItemType } from './workItemTypes';
import type { WitChip } from './useWitStore';
import { useWitStore } from './useWitStore';
import { SAMPLE_ITERATIONS } from './sampleWorkItems';
import { typeLabel, WORK_ITEM_TYPES } from './workItemTypes';
import { useProductsStore } from '@/state/useProductsStore';
import { useSprintsStore, selectIterations } from '@/state/useSprintsStore';
import { fmtDateRange } from '@/sprints/sampleSprintData';

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

  // Phase 8.9 — repo dropdown follows the active product. Lazy-load the
  // repos for that product if the SideBar hasn't already.
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const loadReposFor = useProductsStore((s) => s.loadReposFor);
  useEffect(() => {
    if (activeProductId && !reposByProduct[activeProductId]) {
      void loadReposFor(activeProductId);
    }
  }, [activeProductId, reposByProduct, loadReposFor]);
  const productRepos = useMemo(
    () => (activeProductId ? reposByProduct[activeProductId] ?? [] : []),
    [activeProductId, reposByProduct],
  );

  const counts = useMemo(() => ({
    all:      items.length,
    mine:     items.filter((i) => i.assignedTo?.id === filters.meId).length,
    active:   items.filter((i) => i.state === 'active').length,
    blocked:  items.filter((i) => i.blocked).length,
    watching: items.filter((i) => i.watching).length,
  }), [items, filters.meId]);

  // Items that belong to the active product — used to scope the secondary
  // dropdowns. When the user further filters by Repo, the assignee /
  // iteration option lists honor that selection too.
  const productRepoIdSet = useMemo(() => new Set(productRepos.map((r) => r.id)), [productRepos]);
  const repoFilterSet = useMemo(
    () => (filters.repoId.length ? new Set(filters.repoId) : null),
    [filters.repoId],
  );
  const productItems = useMemo(() => {
    return items.filter((i) => {
      // Items missing a repoId fall back to "in scope" so existing seed
      // rows without backfill don't disappear from the dropdowns.
      if (!i.repoId) return true;
      if (!productRepoIdSet.has(i.repoId)) return false;
      if (repoFilterSet && !repoFilterSet.has(i.repoId)) return false;
      return true;
    });
  }, [items, productRepoIdSet, repoFilterSet]);

  // Distinct assignees on items that fall inside the active product (and
  // optional repo) scope. Defensive: backfill can leave displayName/id
  // null on legacy rows; coalesce so the sort doesn't trip.
  const assignees = useMemo(() => {
    const map = new Map<string, string>();
    for (const i of productItems) {
      const a = i.assignedTo;
      if (!a) continue;
      const id = a.id ?? '';
      if (!id) continue;
      const display = a.displayName || a.id || '—';
      map.set(id, display);
    }
    return [...map.entries()].sort((a, b) => a[1].localeCompare(b[1]));
  }, [productItems]);

  // Repo options come from the active product's repo list. We don't fold
  // in repo ids from `items` — once the store is product-scoped, surfacing
  // a repo that doesn't belong to this product would be confusing.
  const repoOptions = useMemo(
    () => productRepos.map((r) => ({
      value: r.id,
      label: `${r.displayName} · ${r.kind === 'ado' ? 'ADO' : 'GH'}`,
    })),
    [productRepos],
  );

  // Story/feature options from items in scope.
  const storyOptions = useMemo(() => {
    const map = new Map<string, string>();
    for (const i of productItems) {
      const ref = i.featureRef;
      if (!ref) continue;
      if (!map.has(ref)) map.set(ref, ref);
    }
    return [...map.entries()]
      .sort((a, b) => a[1].localeCompare(b[1]))
      .map(([id, label]) => ({ value: id, label }));
  }, [productItems]);

  // When the active product changes, drop any repo selections that no
  // longer belong to it so the filter doesn't silently match nothing.
  useEffect(() => {
    if (!filters.repoId.length) return;
    const valid = new Set(productRepos.map((r) => r.id));
    const next = filters.repoId.filter((id) => valid.has(id));
    if (next.length !== filters.repoId.length) {
      setFilters({ repoId: next });
    }
  }, [productRepos, filters.repoId, setFilters]);

  // Iterations: drive the dropdown from the Phase 15 product-scoped
  // registry (canonical source, fed by the API), then top up with raw
  // refs that appear on items in the same scope but haven't been imported
  // yet. SAMPLE_ITERATIONS stays as a fallback when neither source has data.
  const ensureSprints = useSprintsStore((s) => s.ensure);
  useEffect(() => {
    if (activeProductId) void ensureSprints(activeProductId);
  }, [activeProductId, ensureSprints]);
  const productIters = useSprintsStore(selectIterations(activeProductId ?? ''));
  const iterationOptions = useMemo(() => {
    const map = new Map<string, string>();
    for (const it of productIters) {
      const range = fmtDateRange(it.startDate, it.finishDate);
      map.set(it.providerNodeId, `${it.name} · ${range}`);
    }
    // Items inside the product scope may reference iterations that
    // weren't imported through Phase 15 yet — surface them so the
    // operator can still filter.
    for (const i of productItems) {
      if (i.iterationRef && !map.has(i.iterationRef)) {
        map.set(i.iterationRef, i.iterationRef);
      }
    }
    if (map.size === 0) {
      for (const it of SAMPLE_ITERATIONS) map.set(it.ref, it.label);
    }
    return [...map.entries()].map(([value, label]) => ({ value, label }));
  }, [productIters, productItems]);

  // Drop iteration / assignee selections that no longer match the new
  // product (or repo) scope so a stale selection doesn't filter to zero.
  useEffect(() => {
    if (!filters.iterationRef.length) return;
    const valid = new Set(iterationOptions.map((o) => o.value));
    const next = filters.iterationRef.filter((v) => valid.has(v));
    if (next.length !== filters.iterationRef.length) setFilters({ iterationRef: next });
  }, [iterationOptions, filters.iterationRef, setFilters]);
  useEffect(() => {
    if (!filters.assignee.length) return;
    const valid = new Set(['__unassigned__', ...assignees.map(([id]) => id)]);
    const next = filters.assignee.filter((v) => valid.has(v));
    if (next.length !== filters.assignee.length) setFilters({ assignee: next });
  }, [assignees, filters.assignee, setFilters]);

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
        <MultiSelect
          label="Type"
          values={filters.type}
          onChange={(v) => setFilters({ type: v as WorkItemType[] })}
          options={WORK_ITEM_TYPES.map((t) => ({ value: t, label: typeLabel(t) }))}
          allLabel="Any type"
        />
        <MultiSelect
          label="State"
          values={filters.state}
          onChange={(v) => setFilters({ state: v as WorkItemState[] })}
          options={[
            { value: 'proposed', label: 'Proposed' },
            { value: 'active',   label: 'Active' },
            { value: 'resolved', label: 'Resolved' },
            { value: 'closed',   label: 'Closed' },
          ]}
          allLabel="Any state"
        />
        <MultiSelect
          label="Iteration"
          values={filters.iterationRef}
          onChange={(v) => setFilters({ iterationRef: v })}
          options={iterationOptions}
          allLabel="Any iteration"
        />
        <MultiSelect
          label="Assignee"
          values={filters.assignee}
          onChange={(v) => setFilters({ assignee: v })}
          options={[
            { value: '__unassigned__', label: '— unassigned' },
            ...assignees.map(([id, name]) => ({ value: id, label: `${id} · ${name}` })),
          ]}
          allLabel="Any assignee"
        />
        <MultiSelect
          label="Repo"
          values={filters.repoId}
          onChange={(v) => setFilters({ repoId: v })}
          options={repoOptions}
          allLabel="Any repo"
        />
        <MultiSelect
          label="Story"
          values={filters.featureRef}
          onChange={(v) => setFilters({ featureRef: v })}
          options={storyOptions}
          allLabel="Any story"
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

interface MultiSelectProps {
  label: string;
  values: string[];
  onChange: (v: string[]) => void;
  options: Array<{ value: string; label: string }>;
  /** Summary text shown in the trigger when no value is selected. */
  allLabel: string;
}

function MultiSelect({ label, values, onChange, options, allLabel }: MultiSelectProps) {
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState('');
  const ref = useRef<HTMLDivElement | null>(null);
  const selected = new Set(values);

  // Close on click-outside / Esc so the popover doesn't trap focus.
  useEffect(() => {
    if (!open) { setSearch(''); return; }
    function onMouseDown(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') setOpen(false); }
    window.addEventListener('mousedown', onMouseDown);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onMouseDown);
      window.removeEventListener('keydown', onKey);
    };
  }, [open]);

  function toggle(value: string) {
    const next = new Set(selected);
    if (next.has(value)) next.delete(value);
    else next.add(value);
    onChange([...next]);
  }

  const needle = search.trim().toLowerCase();
  const filtered = needle
    ? options.filter((o) => o.label.toLowerCase().includes(needle) || o.value.toLowerCase().includes(needle))
    : options;

  const summary = (() => {
    if (selected.size === 0) return allLabel;
    if (selected.size === 1) {
      return options.find((o) => o.value === [...selected][0])?.label ?? allLabel;
    }
    return `${selected.size} selected`;
  })();

  return (
    <div className={`wit-multi${open ? ' open' : ''}`} ref={ref}>
      <button
        type="button"
        className="wit-multi-trigger"
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
      >
        <span className="wit-select-lbl">{label}</span>
        <span className="wit-multi-summary">{summary}</span>
        {selected.size > 0 && (
          <span className="wit-multi-badge" aria-hidden>{selected.size}</span>
        )}
        <svg className="wit-multi-caret" width="10" height="10" viewBox="0 0 10 10" aria-hidden>
          <path d="M2 4 L5 7 L8 4" stroke="currentColor" strokeWidth="1.3" fill="none" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>
      {open && (
        <div className="wit-multi-pop" role="listbox" aria-multiselectable>
          <div className="wit-multi-pop-head">
            <span className="wit-select-lbl">{label}</span>
            <button
              type="button"
              className="wit-multi-clear"
              onClick={() => onChange([])}
              disabled={selected.size === 0}
            >
              Clear
            </button>
          </div>
          {options.length > 5 && (
            <div style={{ padding: '4px 6px', borderBottom: '1px solid var(--border)' }}>
              <input
                type="text"
                autoFocus
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder={`Search ${label.toLowerCase()}…`}
                style={{
                  width: '100%', padding: '3px 6px', fontSize: 11,
                  background: 'var(--bg-base, transparent)', color: 'var(--text-primary)',
                  border: '1px solid var(--border-strong)', borderRadius: 3,
                }}
              />
            </div>
          )}
          <div className="wit-multi-pop-list">
            {filtered.length === 0 && (
              <div className="wit-multi-empty">{search ? 'No matches' : 'No options'}</div>
            )}
            {filtered.map((o) => {
              const checked = selected.has(o.value);
              return (
                <label
                  key={o.value}
                  className={`wit-multi-opt${checked ? ' checked' : ''}`}
                  role="option"
                  aria-selected={checked}
                >
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={() => toggle(o.value)}
                  />
                  <span className="wit-multi-opt-label">{o.label}</span>
                </label>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}
