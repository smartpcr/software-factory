// Phase 15 — sprint summary strip rendered inside each ProductCard
// on the /products view. Click "Manage sprints" to open the SprintManager
// modal scoped to the product.

import { useEffect, useMemo } from 'react';
import {
  useSprintsStore,
  selectIterations,
  selectAreas,
  selectPrefixes,
} from '@/state/useSprintsStore';

interface ProductSprintStripProps {
  productId: string;
  onManage: () => void;
}

export function ProductSprintStrip({ productId, onManage }: ProductSprintStripProps) {
  const ensure = useSprintsStore((s) => s.ensure);
  useEffect(() => { void ensure(productId); }, [ensure, productId]);

  const iters = useSprintsStore(selectIterations(productId));
  const areas = useSprintsStore(selectAreas(productId));
  const prefixes = useSprintsStore(selectPrefixes(productId));

  const current = useMemo(
    () => iters.find((i) => i.state === 'current' && i.bindings.some((b) => b.role === 'current')),
    [iters],
  );
  const orphans = iters.filter((i) => i.state === 'provider-orphan').length;

  // Provider coverage — split bar by provider kind.
  const adoCount = iters.filter((i) => i.providerKind === 'ado').length;
  const ghCount = iters.filter((i) => i.providerKind === 'github').length;
  const total = adoCount + ghCount || 1;
  const adoPct = (adoCount / total) * 100;
  const ghPct = (ghCount / total) * 100;

  return (
    <div className="product-sprint-strip">
      <div className="left">
        <div className="eyebrow">current sprint</div>
        <div className="current-name">
          {current ? (
            <>
              <span className="pulse" />
              <span>{current.name}</span>
              <span style={{ fontFamily: 'var(--font-mono)', fontSize: 10.5, color: 'var(--text-secondary)', fontWeight: 400 }}>
                · {current.pointsDone}/{current.pointsTotal} pts
              </span>
            </>
          ) : (
            <span style={{ color: 'var(--text-tertiary)', fontWeight: 400 }}>no active iteration bound</span>
          )}
        </div>
        <div className="stats">
          <em>{iters.length}</em> iteration{iters.length === 1 ? '' : 's'} · <em>{areas.length}</em> area path{areas.length === 1 ? '' : 's'} · <em>{prefixes.length}</em> saved prefix{prefixes.length === 1 ? '' : 'es'}
          {orphans > 0 && <span style={{ color: 'var(--warning)', marginLeft: 6 }}>· {orphans} orphan</span>}
        </div>
        {(adoCount > 0 || ghCount > 0) && (
          <div className="provider-coverage">
            <div className="bar" aria-hidden>
              <div className="ado" style={{ width: `${adoPct}%` }} />
              <div className="gh"  style={{ width: `${ghPct}%` }} />
            </div>
            <span>{adoCount} ADO · {ghCount} GH</span>
          </div>
        )}
      </div>
      <button type="button" className="manage" onClick={onManage}>
        Manage sprints
      </button>
    </div>
  );
}
