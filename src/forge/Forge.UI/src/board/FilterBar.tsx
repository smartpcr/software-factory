import { useEffect, useMemo, useRef, useState } from 'react';
import { useBoardStore, type BoardFilter, type BoardGroupBy } from '@/state/useBoardStore';
import { useStateStore } from '@/state/useStateStore';
import { sampleState } from '@/sampleData';

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

  const cards = useBoardStore((s) => s.cards);
  const active = useBoardStore((s) => s.activeFilter);
  const setFilter = useBoardStore((s) => s.setFilter);
  const epicFilter = useBoardStore((s) => s.epicFilter);
  const setEpicFilter = useBoardStore((s) => s.setEpicFilter);
  const agentFilter = useBoardStore((s) => s.agentFilter);
  const setAgentFilter = useBoardStore((s) => s.setAgentFilter);
  const priorityFilter = useBoardStore((s) => s.priorityFilter);
  const setPriorityFilter = useBoardStore((s) => s.setPriorityFilter);
  const searchTerm = useBoardStore((s) => s.searchTerm);
  const setSearchTerm = useBoardStore((s) => s.setSearchTerm);
  const groupBy = useBoardStore((s) => s.groupBy);
  const setGroupBy = useBoardStore((s) => s.setGroupBy);

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
