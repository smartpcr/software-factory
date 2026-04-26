import { useMemo } from 'react';
import { useBoardStore, type BoardFilter } from '@/state/useBoardStore';

// Verbatim port of the prototype's filter-bar markup inside #view-plan
// (lines 2698-2717). Phase 1 wires the chips to the board store; the
// epic/agent/priority/group-by selects + search input are stubs for Phase R.

const CHIPS: Array<{ key: BoardFilter; label: string; pulse?: boolean }> = [
  { key: 'all',            label: 'All' },
  { key: 'needs-approval', label: 'Needs my approval', pulse: true },
  { key: 'ai-working',     label: 'AI working' },
  { key: 'blocked',        label: 'Blocked' },
  { key: 'my-watch',       label: 'Watching' },
];

export function FilterBar() {
  const cards = useBoardStore((s) => s.cards);
  const active = useBoardStore((s) => s.activeFilter);
  const setFilter = useBoardStore((s) => s.setFilter);

  const counts = useMemo(() => ({
    all: cards.length,
    'needs-approval': cards.filter((c) => c.humanGate).length,
    'ai-working': cards.filter((c) => c.live && c.agent && c.agent.role !== 'human').length,
    blocked: cards.filter((c) => c.blocked).length,
    'my-watch': 5,
  }), [cards]);

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
      <button className="filter-select"><span className="label">Epic</span><span>All 5</span><Chevron /></button>
      <button className="filter-select"><span className="label">Agent</span><span>All</span><Chevron /></button>
      <button className="filter-select"><span className="label">Priority</span><span>Any</span><Chevron /></button>
      <button className="filter-select"><span className="label">Group by</span><span>Epic</span><Chevron /></button>
      <div className="filter-search">
        <svg width="12" height="12" viewBox="0 0 12 12" fill="none" style={{ color: 'var(--text-tertiary)' }}>
          <circle cx="5.5" cy="5.5" r="3.5" stroke="currentColor" strokeWidth="1.3" />
          <line x1="8.4" y1="8.4" x2="10.5" y2="10.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
        </svg>
        <input type="text" placeholder="Search tickets, branches…" />
      </div>
    </div>
  );
}

function Chevron() {
  return (
    <svg width="9" height="9" viewBox="0 0 9 9">
      <path d="M2 3.5 L4.5 6 L7 3.5" stroke="currentColor" fill="none" strokeWidth="1.3" strokeLinecap="round" />
    </svg>
  );
}
