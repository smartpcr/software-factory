import { useMemo } from 'react';
import { useProductsStore } from '@/state/useProductsStore';
import { SAMPLE_REPOS } from './sampleWorkItems';
import type { RepoLite } from './workItemTypes';

/**
 * Phase 80 — resolve a work item's `repoId` to a displayable repo. Prefers the
 * live per-product repos (loaded by the TopBar repo switcher into
 * `useProductsStore.reposByProduct`) so real products' repos render, and falls
 * back to the sample registry for offline / demo mode. Live entries win over a
 * sample entry sharing the same id.
 *
 * Returns a stable resolver `(repoId) => RepoLite | undefined`.
 */
export function useRepoLookup(): (repoId: string | null | undefined) => RepoLite | undefined {
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const activeProductId = useProductsStore((s) => s.activeProductId);

  return useMemo(() => {
    const byId = new Map<string, RepoLite>();
    for (const r of SAMPLE_REPOS) byId.set(r.id, r);
    const live = activeProductId ? reposByProduct[activeProductId] ?? [] : [];
    for (const r of live) {
      byId.set(r.id, { id: r.id, kind: r.kind, displayName: r.displayName });
    }
    return (repoId: string | null | undefined) => (repoId ? byId.get(repoId) : undefined);
  }, [reposByProduct, activeProductId]);
}
