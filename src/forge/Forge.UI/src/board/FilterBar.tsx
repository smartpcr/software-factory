import { useEffect, useMemo, useRef, useState } from 'react';
import { useBoardStore, type BoardFilter, type BoardGroupBy } from '@/state/useBoardStore';
import { useStateStore } from '@/state/useStateStore';
import { useProductsStore } from '@/state/useProductsStore';
import { sampleState } from '@/sampleData';
import { forgeApi } from '@/api/forgeApi';
import type { ProductStory } from '@/types';

// Verbatim port of the prototype's filter-bar markup. Phase-1 chips live
// on the left; the four select buttons + the search input are wired to
// the board store so they actually filter cards.

const CHIPS: Array<{ key: BoardFilter; label: string; pulse?: boolean }> = [
  { key: 'all',            label: 'All' },
  { key: 'needs-approval', label: 'Needs my approval', pulse: true },
  { key: 'ai-working',     label: 'AI working' },
  { key: 'blocked',        label: 'Blocked' },
  { key: 'my-watch',       label: 'Watching' },
];

const PRIORITIES: Array<{ key: string; label: string }> = [
  { key: 'high', label: 'High' },
  { key: 'med',  label: 'Med' },
  { key: 'low',  label: 'Low' },
];

const GROUP_BY_OPTIONS: Array<{ key: BoardGroupBy; label: string }> = [
  { key: 'story',    label: 'Story' },
  { key: 'epic',     label: 'Epic' },
  { key: 'phase',    label: 'Phase' },
  { key: 'agent',    label: 'Agent' },
  { key: 'priority', label: 'Priority' },
];

export function FilterBar() {
  const apiState = useStateStore((s) => s.state);
  const epics = apiState?.epics ?? sampleState.epics;
  const activeProductId = useProductsStore((s) => s.activeProductId);

  const cards = useBoardStore((s) => s.cards);
  const active = useBoardStore((s) => s.activeFilter);
  const setFilter = useBoardStore((s) => s.setFilter);
  const epicFilter = useBoardStore((s) => s.epicFilter);
  const setEpicFilter = useBoardStore((s) => s.setEpicFilter);
  const agentFilter = useBoardStore((s) => s.agentFilter);
  const setAgentFilter = useBoardStore((s) => s.setAgentFilter);
  const priorityFilter = useBoardStore((s) => s.priorityFilter);
  const setPriorityFilter = useBoardStore((s) => s.setPriorityFilter);
  const storyFilter = useBoardStore((s) => s.storyFilter);
  const setStoryFilter = useBoardStore((s) => s.setStoryFilter);
  const searchTerm = useBoardStore((s) => s.searchTerm);
  const setSearchTerm = useBoardStore((s) => s.setSearchTerm);
  const groupBy = useBoardStore((s) => s.groupBy);
  const setGroupBy = useBoardStore((s) => s.setGroupBy);

  // Load product stories for the multi-select filter.
  const [productStories, setProductStories] = useState<ProductStory[]>([]);
  useEffect(() => {
    if (!activeProductId) return;
    let cancelled = false;
    forgeApi.listProductStories(activeProductId)
      .then((s) => { if (!cancelled) setProductStories(s); })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [activeProductId]);
  const storyOptions = useMemo(
    () => productStories.map((s) => ({ key: s.id, label: s.name })),
    [productStories],
  );

  const counts = useMemo(() => ({
    all: cards.length,
    'needs-approval': cards.filter((c) => c.humanGate).length,
    'ai-working': cards.filter((c) => c.live && c.agent && c.agent.role !== 'human').length,
    blocked: cards.filter((c) => c.blocked).length,
    'my-watch': 5,
  }), [cards]);

  // Distinct agents seen on cards (board-scoped, so the dropdown stays
  // relevant to what's actually rendered).
  const agentOptions = useMemo(() => {
    const seen = new Map<string, string>();
    for (const c of cards) if (c.agent) seen.set(c.agent.id, c.agent.name || c.agent.id);
    return Array.from(seen.entries()).map(([key, label]) => ({ key, label }));
  }, [cards]);

  // Scope the Epic dropdown to epics that actually appear on cards loaded
  // for the active product. Epics with no cards in the current scope would
  // produce empty filters, so dropping them keeps the menu honest.
  const epicOptions = useMemo(() => {
    const seen = new Set<string>();
    for (const c of cards) seen.add(c.epic);
    return epics
      .filter((e) => seen.has(e.id))
      .map((e) => ({ key: e.id, label: `${e.id} · ${e.name}` }));
  }, [cards, epics]);

  return (
    <div className="filter-bar">
      <div className="filter-chips">
        {CHIPS.map((chip) => (
          <button
            key={chip.key}
            className={`filter-chip${active === chip.key ? ' active' : ''}`}
            data-filter={chip.key}
            onClick={() => setFilter(chip.key)}
          >
            {chip.pulse && <span className="pulse-dot" />}
            {chip.label} <span className="count">{counts[chip.key]}</span>
          </button>
        ))}
      </div>
      <FilterSelect
        label="Epic"
        valueLabel={epicFilter
          ? (epicOptions.find((o) => o.key === epicFilter)?.label ?? epicFilter)
          : `All ${epicOptions.length}`}
        options={epicOptions}
        active={epicFilter}
        onPick={setEpicFilter}
      />
      <FilterSelect
        label="Agent"
        valueLabel={agentFilter
          ? (agentOptions.find((o) => o.key === agentFilter)?.label ?? agentFilter)
          : 'All'}
        options={agentOptions}
        active={agentFilter}
        onPick={setAgentFilter}
      />
      <FilterSelect
        label="Priority"
        valueLabel={priorityFilter
          ? (PRIORITIES.find((p) => p.key === priorityFilter)?.label ?? priorityFilter)
          : 'Any'}
        options={PRIORITIES}
        active={priorityFilter}
        onPick={setPriorityFilter}
      />
      <MultiFilterSelect
        label="Story"
        options={storyOptions}
        selected={storyFilter}
        onChange={setStoryFilter}
      />
      <FilterSelect
        label="Group by"
        valueLabel={GROUP_BY_OPTIONS.find((o) => o.key === groupBy)?.label ?? 'Epic'}
        options={GROUP_BY_OPTIONS}
        active={groupBy}
        onPick={(v) => {
          const next = (v ?? 'story') as BoardGroupBy;
          setGroupBy(next);
        }}
        clearable={false}
      />
      <div className="filter-search">
        <svg width="12" height="12" viewBox="0 0 12 12" fill="none" style={{ color: 'var(--text-tertiary)' }}>
          <circle cx="5.5" cy="5.5" r="3.5" stroke="currentColor" strokeWidth="1.3" />
          <line x1="8.4" y1="8.4" x2="10.5" y2="10.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
        </svg>
        <input
          type="text"
          value={searchTerm}
          onChange={(e) => setSearchTerm(e.target.value)}
          placeholder="Search tickets, branches…"
        />
        {searchTerm && (
          <button
            type="button"
            onClick={() => setSearchTerm('')}
            title="Clear search"
            style={{ background: 'transparent', border: 'none', color: 'var(--text-tertiary)', cursor: 'pointer', padding: '0 4px', fontSize: 14, lineHeight: 1 }}
          >×</button>
        )}
      </div>
    </div>
  );
}

interface FilterSelectProps {
  label: string;
  valueLabel: string;
  options: Array<{ key: string; label: string }>;
  active: string | null;
  onPick: (value: string | null) => void;
  /** Default true — when true, an "All" reset entry sits at the top. */
  clearable?: boolean;
}

function FilterSelect({ label, valueLabel, options, active, onPick, clearable = true }: FilterSelectProps) {
  const [open, setOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const popoverRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    function onDown(e: MouseEvent) {
      const t = e.target as Node;
      if (triggerRef.current?.contains(t)) return;
      if (popoverRef.current?.contains(t)) return;
      setOpen(false);
    }
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') setOpen(false); }
    window.addEventListener('mousedown', onDown);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onDown);
      window.removeEventListener('keydown', onKey);
    };
  }, [open]);

  function pick(value: string | null) {
    onPick(value);
    setOpen(false);
  }

  return (
    <div style={{ position: 'relative' }}>
      <button
        ref={triggerRef}
        type="button"
        className={`filter-select${active ? ' active' : ''}`}
        onClick={() => setOpen((v) => !v)}
        title={`Filter by ${label}`}
      >
        <span className="label">{label}</span>
        <span>{valueLabel}</span>
        <Chevron />
      </button>
      {open && (
        <div
          ref={popoverRef}
          role="listbox"
          style={{
            position: 'absolute',
            top: 'calc(100% + 4px)',
            left: 0,
            zIndex: 30,
            minWidth: 200,
            maxWidth: 320,
            maxHeight: 320,
            overflow: 'auto',
            background: 'var(--bg-elevated, #1c1c1c)',
            border: '1px solid var(--border)',
            borderRadius: 6,
            boxShadow: '0 6px 20px rgba(0,0,0,0.35)',
            padding: 4,
          }}
        >
          {clearable && (
            <button
              type="button"
              onClick={() => pick(null)}
              style={popoverRowStyle(active === null)}
            >
              <span style={{ color: 'var(--text-tertiary)', fontStyle: 'italic' }}>All / clear</span>
            </button>
          )}
          {options.length === 0 ? (
            <div style={{ padding: '6px 10px', fontSize: 11, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>
              No options.
            </div>
          ) : options.map((o) => (
            <button
              key={o.key}
              type="button"
              onClick={() => pick(o.key)}
              style={popoverRowStyle(active === o.key)}
            >
              {o.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

function popoverRowStyle(selected: boolean): React.CSSProperties {
  return {
    display: 'block',
    width: '100%',
    textAlign: 'left',
    padding: '6px 10px',
    border: 'none',
    background: selected ? 'color-mix(in srgb, var(--accent) 14%, transparent)' : 'transparent',
    color: 'inherit',
    cursor: 'pointer',
    fontSize: 12,
    borderRadius: 4,
  };
}

function Chevron() {
  return (
    <svg width="9" height="9" viewBox="0 0 9 9">
      <path d="M2 3.5 L4.5 6 L7 3.5" stroke="currentColor" fill="none" strokeWidth="1.3" strokeLinecap="round" />
    </svg>
  );
}

// ── Multi-select filter (checkboxes) ─────────────────────────────────────

interface MultiFilterSelectProps {
  label: string;
  options: Array<{ key: string; label: string }>;
  selected: string[];
  onChange: (next: string[]) => void;
}

function MultiFilterSelect({ label, options, selected, onChange }: MultiFilterSelectProps) {
  const [open, setOpen] = useState(false);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const popoverRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    function onDown(e: MouseEvent) {
      const t = e.target as Node;
      if (triggerRef.current?.contains(t)) return;
      if (popoverRef.current?.contains(t)) return;
      setOpen(false);
    }
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') setOpen(false); }
    window.addEventListener('mousedown', onDown);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onDown);
      window.removeEventListener('keydown', onKey);
    };
  }, [open]);

  function toggle(key: string) {
    const set = new Set(selected);
    if (set.has(key)) set.delete(key); else set.add(key);
    onChange([...set]);
  }

  const valueLabel = selected.length === 0
    ? `All ${options.length}`
    : selected.length === 1
      ? (options.find((o) => o.key === selected[0])?.label ?? selected[0])
      : `${selected.length} selected`;

  return (
    <div style={{ position: 'relative' }}>
      <button
        ref={triggerRef}
        type="button"
        className={`filter-select${selected.length > 0 ? ' active' : ''}`}
        onClick={() => setOpen((v) => !v)}
        title={`Filter by ${label}`}
      >
        <span className="label">{label}</span>
        <span>{valueLabel}</span>
        <Chevron />
      </button>
      {open && (
        <div
          ref={popoverRef}
          role="listbox"
          style={{
            position: 'absolute',
            top: 'calc(100% + 4px)',
            left: 0,
            zIndex: 30,
            minWidth: 220,
            maxWidth: 320,
            maxHeight: 320,
            overflow: 'auto',
            background: 'var(--bg-elevated, #1c1c1c)',
            border: '1px solid var(--border)',
            borderRadius: 6,
            boxShadow: '0 6px 20px rgba(0,0,0,0.35)',
            padding: 4,
          }}
        >
          {selected.length > 0 && (
            <button
              type="button"
              onClick={() => onChange([])}
              style={popoverRowStyle(false)}
            >
              <span style={{ color: 'var(--text-tertiary)', fontStyle: 'italic' }}>Clear all</span>
            </button>
          )}
          {options.length === 0 ? (
            <div style={{ padding: '6px 10px', fontSize: 11, color: 'var(--text-tertiary)', fontStyle: 'italic' }}>
              No stories — create one first.
            </div>
          ) : options.map((o) => (
            <button
              key={o.key}
              type="button"
              onClick={() => toggle(o.key)}
              style={{ ...popoverRowStyle(selected.includes(o.key)), display: 'flex', alignItems: 'center', gap: 6 }}
            >
              <span style={{
                width: 14, height: 14, borderRadius: 3, flexShrink: 0,
                border: selected.includes(o.key) ? '2px solid var(--accent)' : '2px solid var(--border)',
                background: selected.includes(o.key) ? 'var(--accent)' : 'transparent',
                display: 'flex', alignItems: 'center', justifyContent: 'center',
                fontSize: 10, color: '#fff',
              }}>
                {selected.includes(o.key) && '✓'}
              </span>
              {o.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
