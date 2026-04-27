// Phase 15 — compact area-path filter chip used in the Plan & Design page-head.

import { useEffect } from 'react';
import { useSprintsStore, selectAreas, selectDefaultArea } from '@/state/useSprintsStore';

interface AreaPathFilterProps {
  productId: string;
  activePath: string | null;
  onChange: (path: string | null) => void;
  onManage: () => void;
}

export function AreaPathFilter({ productId, activePath, onChange, onManage }: AreaPathFilterProps) {
  const ensure = useSprintsStore((s) => s.ensure);
  useEffect(() => { void ensure(productId); }, [ensure, productId]);
  const dflt = useSprintsStore(selectDefaultArea(productId));
  const areas = useSprintsStore(selectAreas(productId));
  const path = activePath ?? dflt?.path ?? null;

  if (!path) {
    return (
      <button
        type="button"
        className="area-chip"
        onClick={onManage}
        style={{ cursor: 'pointer', borderStyle: 'solid' }}
      >
        <span className="label">area</span>
        <span style={{ color: 'var(--text-tertiary)' }}>set default…</span>
      </button>
    );
  }

  const segs = path.split('\\');
  return (
    <span className="area-chip" title={`${areas.length} imported · click × to clear`}>
      <span className="label">area</span>
      <span className="path">
        {segs.map((s, i) => (
          <span key={i}>
            {i > 0 && <span className="sep">\</span>}
            <span className={i === segs.length - 1 ? 'seg' : ''} style={i < segs.length - 1 ? { color: 'var(--text-tertiary)' } : undefined}>
              {s}
            </span>
          </span>
        ))}
      </span>
      <button onClick={() => onChange(null)} aria-label="Clear area path filter" type="button">×</button>
    </span>
  );
}
