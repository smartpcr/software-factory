// -----------------------------------------------------------------------
// <copyright file="PrFilterBar.tsx" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Pr } from '@/types';
import { usePrsStore, type PrFilter } from '@/state/usePrsStore';
import { useProductsStore } from '@/state/useProductsStore';

interface Props {
  prs: Pr[];
}

const CHIPS: Array<{ key: PrFilter; label: string; pulse?: boolean }> = [
  { key: 'all',            label: 'All open' },
  { key: 'need-approval',  label: 'Need approval', pulse: true },
  { key: 'failed-build',   label: 'Failed build' },
  { key: 'conflicts',      label: 'Conflicts' },
  { key: 'my-reviews',     label: 'My reviews' },
  { key: 'open-comments',  label: 'Open comments' },
];

export function PrFilterBar({ prs }: Props) {
  const filter = usePrsStore((s) => s.filter);
  const setFilter = usePrsStore((s) => s.setFilter);
  const authorFilter = usePrsStore((s) => s.authorFilter);
  const setAuthorFilter = usePrsStore((s) => s.setAuthorFilter);
  const workstreamFilter = usePrsStore((s) => s.workstreamFilter);
  const setWorkstreamFilter = usePrsStore((s) => s.setWorkstreamFilter);
  const searchQuery = usePrsStore((s) => s.searchQuery);
  const setSearchQuery = usePrsStore((s) => s.setSearchQuery);
  const dateFrom = usePrsStore((s) => s.dateFrom);
  const dateTo = usePrsStore((s) => s.dateTo);
  const setDateRange = usePrsStore((s) => s.setDateRange);

  const counts = useMemo(() => ({
    all: prs.length,
    'need-approval': prs.filter((p) => p.humanGate).length,
    'failed-build': prs.filter((p) => p.build?.status === 'failing').length,
    conflicts: prs.filter((p) => p.conflicts).length,
    'my-reviews': prs.filter((p) => p.review?.verdict === 'pending' || p.review?.verdict === 'changes').length,
    'open-comments': prs.filter((p) => (p.openCommentCount ?? 0) > 0).length,
  }), [prs]);

  const authors = useMemo(() => [...new Set(prs.map((p) => p.author.name))].sort(), [prs]);

  // Build a repoId → displayName map from the products store.
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const repoNameMap = useMemo(() => {
    const m: Record<string, string> = {};
    for (const repos of Object.values(reposByProduct)) {
      for (const r of repos) m[r.id] = r.displayName;
    }
    return m;
  }, [reposByProduct]);
  const repoIds = useMemo(() => [...new Set(prs.map((p) => p.repoId).filter(Boolean) as string[])].sort(), [prs]);

  // Debounced search
  const [localSearch, setLocalSearch] = useState(searchQuery);
  const timerRef = useRef<ReturnType<typeof setTimeout>>();
  const handleSearch = useCallback((value: string) => {
    setLocalSearch(value);
    clearTimeout(timerRef.current);
    timerRef.current = setTimeout(() => setSearchQuery(value), 300);
  }, [setSearchQuery]);

  useEffect(() => () => clearTimeout(timerRef.current), []);

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

      <FilterDropdown
        label="Repo"
        options={repoIds}
        displayNames={repoNameMap}
        selected={workstreamFilter}
        onToggle={(id) => {
          const next = workstreamFilter.includes(id)
            ? workstreamFilter.filter((w) => w !== id)
            : [...workstreamFilter, id];
          setWorkstreamFilter(next);
        }}
      />

      <FilterDropdown
        label="Author"
        options={authors}
        selected={authorFilter}
        onToggle={(author) => {
          const next = authorFilter.includes(author)
            ? authorFilter.filter((a) => a !== author)
            : [...authorFilter, author];
          setAuthorFilter(next);
        }}
      />

      <DateRangePicker
        from={dateFrom}
        to={dateTo}
        onChange={(from, to) => setDateRange(from, to)}
      />

      <div className="filter-search">
        <svg width="12" height="12" viewBox="0 0 12 12" fill="none" style={{ color: 'var(--text-tertiary)' }}>
          <circle cx="5.5" cy="5.5" r="3.5" stroke="currentColor" strokeWidth="1.3" />
          <line x1="8.4" y1="8.4" x2="10.5" y2="10.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
        </svg>
        <input
          type="text"
          placeholder="Search PRs…"
          value={localSearch}
          onChange={(e) => handleSearch(e.target.value)}
        />
      </div>
    </div>
  );
}

/* ---------- Reusable filter dropdown with popover + checkboxes ---------- */

function FilterDropdown({ label, options, selected, onToggle, displayNames }: {
  label: string;
  options: string[];
  selected: string[];
  onToggle: (value: string) => void;
  displayNames?: Record<string, string>;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  // Close on outside click
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [open]);

  // Close on Escape
  useEffect(() => {
    if (!open) return;
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [open]);

  const summary = selected.length === 0
    ? 'All'
    : `${selected.length} selected`;

  return (
    <div className="filter-dropdown-wrap" ref={ref} style={{ position: 'relative' }}>
      <button className="filter-select" onClick={() => setOpen((o) => !o)}>
        <span className="label">{label}</span>
        <span>{summary}</span>
        <Chevron />
      </button>

      {open && (
        <div className="filter-dropdown-popover">
          {options.length === 0 ? (
            <div className="filter-dropdown-empty">No options</div>
          ) : (
            options.map((opt) => (
              <label key={opt} className="filter-dropdown-option">
                <input
                  type="checkbox"
                  checked={selected.includes(opt)}
                  onChange={() => onToggle(opt)}
                />
                <span className="filter-dropdown-option-text">{displayNames?.[opt] ?? opt}</span>
              </label>
            ))
          )}
        </div>
      )}
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

/* ---------- Date range picker with calendar popover ---------- */

const MONTH_NAMES = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const DAY_NAMES = ['Su', 'Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa'];

function toDateStr(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

function parseDate(s: string | null): Date | null {
  if (!s) return null;
  const d = new Date(s + 'T00:00:00');
  return isNaN(d.getTime()) ? null : d;
}

function formatShort(s: string | null): string {
  const d = parseDate(s);
  if (!d) return '—';
  return `${MONTH_NAMES[d.getMonth()]} ${d.getDate()}`;
}

function DateRangePicker({ from, to, onChange }: {
  from: string | null;
  to: string | null;
  onChange: (from: string | null, to: string | null) => void;
}) {
  const [open, setOpen] = useState(false);
  const [picking, setPicking] = useState<'from' | 'to'>('from');
  const [viewDate, setViewDate] = useState(() => {
    const d = parseDate(from) ?? parseDate(to) ?? new Date();
    return { year: d.getFullYear(), month: d.getMonth() };
  });
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [open]);

  const hasFilter = from || to;

  const daysInMonth = new Date(viewDate.year, viewDate.month + 1, 0).getDate();
  const startDay = new Date(viewDate.year, viewDate.month, 1).getDay();
  const cells: (number | null)[] = [];
  for (let i = 0; i < startDay; i++) cells.push(null);
  for (let d = 1; d <= daysInMonth; d++) cells.push(d);

  function prevMonth() {
    setViewDate((v) => v.month === 0 ? { year: v.year - 1, month: 11 } : { ...v, month: v.month - 1 });
  }
  function nextMonth() {
    setViewDate((v) => v.month === 11 ? { year: v.year + 1, month: 0 } : { ...v, month: v.month + 1 });
  }

  function selectDay(day: number) {
    const ds = toDateStr(new Date(viewDate.year, viewDate.month, day));
    if (picking === 'from') {
      onChange(ds, to && ds > to ? null : to);
      setPicking('to');
    } else {
      onChange(from && ds < from ? ds : from, ds);
      setOpen(false);
    }
  }

  function isInRange(day: number): boolean {
    const ds = toDateStr(new Date(viewDate.year, viewDate.month, day));
    return !!from && !!to && ds >= from && ds <= to;
  }
  function isEndpoint(day: number): boolean {
    const ds = toDateStr(new Date(viewDate.year, viewDate.month, day));
    return ds === from || ds === to;
  }

  const S: Record<string, React.CSSProperties> = {
    trigger: {
      display: 'flex', alignItems: 'center', gap: 6,
      background: 'none', border: '1px solid var(--border-strong)',
      borderRadius: 'var(--radius-sm)', padding: '4px 10px',
      color: hasFilter ? 'var(--accent)' : 'var(--text-tertiary)',
      cursor: 'pointer', font: 'inherit', fontSize: 11, fontWeight: 600,
      transition: 'border-color 120ms',
    },
    popover: {
      position: 'absolute', top: '100%', right: 0, marginTop: 6,
      width: 260, background: 'var(--bg-surface)', border: '1px solid var(--border-strong)',
      borderRadius: 'var(--radius-sm)', boxShadow: '0 8px 24px rgba(0,0,0,0.5)',
      zIndex: 1000, padding: '10px',
    },
    nav: {
      display: 'flex', alignItems: 'center', justifyContent: 'space-between',
      marginBottom: 8,
    },
    navBtn: {
      background: 'none', border: 'none', cursor: 'pointer',
      color: 'var(--text-secondary)', fontSize: 14, padding: '2px 6px',
    },
    grid: {
      display: 'grid', gridTemplateColumns: 'repeat(7, 1fr)',
      gap: 1, textAlign: 'center' as const,
    },
    dayHeader: {
      fontSize: 9, fontWeight: 700, color: 'var(--text-tertiary)',
      padding: '2px 0', textTransform: 'uppercase' as const, letterSpacing: 0.3,
    },
    day: {
      fontSize: 11, padding: '4px 0', borderRadius: 3,
      cursor: 'pointer', border: 'none', background: 'none',
      color: 'var(--text-primary)',
    },
    footer: {
      display: 'flex', gap: 6, alignItems: 'center', justifyContent: 'space-between',
      marginTop: 8, paddingTop: 8, borderTop: '1px solid var(--border-subtle)',
    },
  };

  return (
    <div ref={ref} style={{ position: 'relative', display: 'inline-flex' }}>
      <button type="button" onClick={() => setOpen(!open)} style={S.trigger}>
        <svg width="12" height="12" viewBox="0 0 16 16" fill="none">
          <rect x="2" y="3" width="12" height="11" rx="1.5" stroke="currentColor" strokeWidth="1.3" />
          <path d="M2 6.5h12M5.5 1.5v3M10.5 1.5v3" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" />
        </svg>
        <span>{formatShort(from)}</span>
        <span style={{ opacity: 0.4 }}>→</span>
        <span>{formatShort(to)}</span>
      </button>

      {open && (
        <div style={S.popover}>
          {/* Picking indicator */}
          <div style={{ fontSize: 10, color: 'var(--accent)', fontWeight: 600, marginBottom: 6, textTransform: 'uppercase', letterSpacing: 0.5 }}>
            Select {picking === 'from' ? 'start' : 'end'} date
          </div>

          {/* Month navigation */}
          <div style={S.nav}>
            <button type="button" onClick={prevMonth} style={S.navBtn}>‹</button>
            <span style={{ fontSize: 12, fontWeight: 700, color: 'var(--text-primary)' }}>
              {MONTH_NAMES[viewDate.month]} {viewDate.year}
            </span>
            <button type="button" onClick={nextMonth} style={S.navBtn}>›</button>
          </div>

          {/* Day headers */}
          <div style={S.grid}>
            {DAY_NAMES.map((d) => <span key={d} style={S.dayHeader}>{d}</span>)}
            {cells.map((day, i) =>
              day === null
                ? <span key={`e${i}`} />
                : (
                  <button
                    key={day}
                    type="button"
                    onClick={() => selectDay(day)}
                    style={{
                      ...S.day,
                      background: isEndpoint(day) ? 'var(--accent)' : isInRange(day) ? 'color-mix(in srgb, var(--accent) 20%, transparent)' : 'none',
                      color: isEndpoint(day) ? '#000' : isInRange(day) ? 'var(--accent)' : 'var(--text-primary)',
                      fontWeight: isEndpoint(day) ? 700 : 400,
                    }}
                  >
                    {day}
                  </button>
                ),
            )}
          </div>

          {/* Footer: current range + clear */}
          <div style={S.footer}>
            <span style={{ fontSize: 10, color: 'var(--text-tertiary)' }}>
              {from && to ? `${formatShort(from)} → ${formatShort(to)}` : from ? `From ${formatShort(from)}` : to ? `To ${formatShort(to)}` : 'No date filter'}
            </span>
            {hasFilter && (
              <button type="button" onClick={() => { onChange(null, null); setOpen(false); }} style={{
                fontSize: 10, fontWeight: 600, padding: '2px 8px',
                background: 'none', color: 'var(--text-tertiary)',
                border: '1px solid var(--border-subtle)', borderRadius: 3, cursor: 'pointer',
              }}>Clear</button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
