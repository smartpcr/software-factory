import { useMemo } from 'react';
import type { Pr } from '@/types';
import { usePrsStore, type PrFilter } from '@/state/usePrsStore';

interface Props {
  prs: Pr[];
}

const CHIPS: Array<{ key: PrFilter; label: string; pulse?: boolean }> = [
  { key: 'all',           label: 'All open' },
  { key: 'need-approval', label: 'Need approval', pulse: true },
  { key: 'failing',       label: 'Failing' },
  { key: 'conflicts',     label: 'Conflicts' },
  { key: 'my-reviews',    label: 'My reviews' },
];

// Verbatim port of the PR filter-bar markup (lines 2871-2888).
export function PrFilterBar({ prs }: Props) {
  const filter = usePrsStore((s) => s.filter);
  const setFilter = usePrsStore((s) => s.setFilter);

  const counts = useMemo(() => ({
    all: prs.length,
    'need-approval': prs.filter((p) => p.humanGate).length,
    failing: prs.filter((p) => p.build?.status === 'failing').length,
    conflicts: prs.filter((p) => p.conflicts).length,
    'my-reviews': prs.filter((p) => p.review?.verdict === 'pending' || p.review?.verdict === 'changes').length,
  }), [prs]);

  return (
    <div className="filter-bar" style={{ paddingBottom: 14 }}>
      <div className="filter-chips">
        {CHIPS.map((chip) => (
          <button
            key={chip.key}
            className={`filter-chip${filter === chip.key ? ' active' : ''}`}
            onClick={() => setFilter(chip.key)}
          >
            {chip.pulse && <span className="pulse-dot" />}
            {chip.label} <span className="count">{counts[chip.key]}</span>
          </button>
        ))}
      </div>
      <button className="filter-select"><span className="label">Workstream</span><span>All</span><Chevron /></button>
      <button className="filter-select"><span className="label">Author</span><span>Any</span><Chevron /></button>
      <div className="filter-search">
        <svg width="12" height="12" viewBox="0 0 12 12" fill="none" style={{ color: 'var(--text-tertiary)' }}>
          <circle cx="5.5" cy="5.5" r="3.5" stroke="currentColor" strokeWidth="1.3" />
          <line x1="8.4" y1="8.4" x2="10.5" y2="10.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
        </svg>
        <input type="text" placeholder="Search PRs…" />
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
