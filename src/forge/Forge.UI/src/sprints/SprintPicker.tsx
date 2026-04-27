// Phase 15 — compact sprint picker chip used in the Plan & Design page-head.
// Replaces the prototype's static "Sprint 14 board" title (Q15.7).

import { useEffect, useMemo, useRef, useState } from 'react';
import { useSprintsStore, selectIterations, iterationStateRank } from '@/state/useSprintsStore';
import { fmtDateRange } from '@/sprints/sampleSprintData';
import type { ProductIteration } from '@/types';

interface SprintPickerProps {
  productId: string;
  selectedId: string;
  burndown: { done: number; total: number; pct: number };
  onSelect: (iterationId: string) => void;
  onManage: (initialPrefix?: string) => void;
}

export function SprintPicker({ productId, selectedId, burndown, onSelect, onManage }: SprintPickerProps) {
  const ensure = useSprintsStore((s) => s.ensure);
  useEffect(() => { void ensure(productId); }, [ensure, productId]);
  const iterations = useSprintsStore(selectIterations(productId));
  // Sort in the consumer so the selector can return a stable reference.
  const all = useMemo(
    () => [...iterations].sort((a, b) => iterationStateRank(a) - iterationStateRank(b)
      || (a.startDate ?? '').localeCompare(b.startDate ?? '')),
    [iterations],
  );

  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    function onDoc(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') setOpen(false); }
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const current = all.find((i) => i.id === selectedId)
    ?? all.find((i) => i.state === 'current')
    ?? all[0];
  if (!current) {
    return (
      <button
        type="button"
        className="sprint-picker-chip"
        onClick={() => onManage()}
        title="No iterations imported yet"
      >
        <span className="pulse" style={{ background: 'var(--text-tertiary)', boxShadow: 'none', animation: 'none' }} />
        <span className="name">No sprint</span>
        <span className="range" style={{ color: 'var(--text-tertiary)' }}>import iterations →</span>
      </button>
    );
  }
  const isOrphan = current.state === 'provider-orphan';

  return (
    <div className="sprint-picker-anchor" ref={ref}>
      <button
        type="button"
        className="sprint-picker-chip"
        data-open={open}
        data-state={current.state}
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        aria-haspopup="listbox"
      >
        <span className="pulse" />
        <span className="name">{current.name}</span>
        <span className="range">{fmtDateRange(current.startDate, current.finishDate)}</span>
        <span style={{ color: 'var(--text-tertiary)', fontSize: 10 }}>·</span>
        <span style={{ color: 'var(--text-primary)', fontFamily: 'var(--font-mono)', fontSize: 11 }}>
          {burndown.done} / {burndown.total} pts
        </span>
        <span className="caret">▾</span>
      </button>

      {open && (
        <div className="sprint-picker-pop" role="listbox">
          <div className="head">
            <span>iterations · <span className="product">{productId}</span></span>
            <span>{all.length} imported</span>
          </div>
          <div className="list">
            <button
              key="__all__"
              className="row"
              type="button"
              data-state="all"
              data-current={!selectedId}
              onClick={() => { onSelect(''); setOpen(false); }}
            >
              <span className="dot" style={{ background: 'var(--text-tertiary)' }} />
              <span className="meta">
                <span className="name">All sprints</span>
                <span className="path">show cards from every iteration</span>
              </span>
            </button>
            {all.map((i: ProductIteration) => (
              <button
                key={i.id}
                className="row"
                type="button"
                data-state={i.state}
                data-current={i.id === current.id}
                onClick={() => { onSelect(i.id); setOpen(false); }}
              >
                <span className="dot" />
                <span className="meta">
                  <span className="name">{i.name}</span>
                  <span className="path">{i.path}</span>
                </span>
                <span className="right">
                  {fmtDateRange(i.startDate, i.finishDate)}
                </span>
              </button>
            ))}
          </div>
          <div className="foot">
            <span style={{ fontFamily: 'var(--font-mono)', fontSize: 10, color: 'var(--text-tertiary)', letterSpacing: 0.04 }}>
              {isOrphan ? '⚠ provider-orphan' : 'live · ADO + GH'}
            </span>
            <button className="manage" onClick={() => { setOpen(false); onManage(); }}>
              Manage&hellip;
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
