// Phase 15 — sprint registry store. Per-product cache for iterations,
// area paths, and saved prefixes, with API loaders + mutation helpers
// + a SignalR-driven invalidation hook (`invalidate(productId)`).
//
// All loaders are best-effort: if the API isn't reachable, the store
// keeps the last successful payload (or an empty array) and surfaces
// the error via `errorByProduct` so callers can decide whether to
// degrade gracefully.

import { create } from 'zustand';
import type {
  ImportNodesRequest,
  IterationBindingRequest,
  ProductAreaPath,
  ProductIteration,
  SavePrefixRequest,
  SavedPrefix,
  SprintDiscoveryRequest,
  SprintImportKind,
} from '@/types';
import { forgeApi } from '@/api/forgeApi';

interface PerProduct {
  iterations: ProductIteration[];
  areas: ProductAreaPath[];
  prefixes: SavedPrefix[];
  loadedAt: number | null;
  error: string | null;
}

interface SprintsState {
  byProduct: Record<string, PerProduct>;
  inflight: Record<string, Promise<void> | undefined>;

  /** Lazy-load (idempotent within ~5 s) all three lists for the product. */
  ensure: (productId: string) => Promise<void>;
  /** Force a refresh — used after mutations + on `sprint.changed` events. */
  invalidate: (productId: string) => Promise<void>;

  // Mutations — refresh the relevant slice on success.
  importIterations: (productId: string, req: ImportNodesRequest) => Promise<ProductIteration[]>;
  importAreaPaths:  (productId: string, req: ImportNodesRequest) => Promise<ProductAreaPath[]>;
  toggleDefaultArea:(productId: string, areaPathId: string) => Promise<void>;
  savePrefix:       (productId: string, req: SavePrefixRequest) => Promise<SavedPrefix>;
  deletePrefix:     (productId: string, prefixId: string) => Promise<void>;
  refreshPrefix:    (productId: string, prefixId: string) => Promise<SavedPrefix>;
  addBinding:       (productId: string, repoId: string, req: IterationBindingRequest) => Promise<void>;
  removeBinding:    (productId: string, repoId: string, bindingId: string | number) => Promise<void>;
  setActiveIteration:(productId: string, iterationId: string | null) => Promise<void>;

  /** Discovery is read-only; the result is not cached because the prefix
   * varies per call — the SprintManager memoises in-component. */
  discover: (productId: string, req: SprintDiscoveryRequest) => ReturnType<typeof forgeApi.discoverSprintNodes>;
}

const EMPTY: PerProduct = { iterations: [], areas: [], prefixes: [], loadedAt: null, error: null };
const STALE_MS = 5_000;

export const useSprintsStore = create<SprintsState>((set, get) => ({
  byProduct: {},
  inflight: {},

  async ensure(productId) {
    const cached = get().byProduct[productId];
    if (cached?.loadedAt && Date.now() - cached.loadedAt < STALE_MS) return;
    const inflight = get().inflight[productId];
    if (inflight) return inflight;
    const p = doLoad(productId, set);
    set((s) => ({ inflight: { ...s.inflight, [productId]: p } }));
    try { await p; }
    finally {
      set((s) => {
        const next = { ...s.inflight };
        delete next[productId];
        return { inflight: next };
      });
    }
  },

  async invalidate(productId) {
    await doLoad(productId, set);
  },

  async importIterations(productId, req) {
    const next = await forgeApi.importIterations(productId, req);
    set((s) => ({
      byProduct: {
        ...s.byProduct,
        [productId]: { ...(s.byProduct[productId] ?? EMPTY), iterations: next, loadedAt: Date.now(), error: null },
      },
    }));
    return next;
  },

  async importAreaPaths(productId, req) {
    const next = await forgeApi.importAreaPaths(productId, req);
    set((s) => ({
      byProduct: {
        ...s.byProduct,
        [productId]: { ...(s.byProduct[productId] ?? EMPTY), areas: next, loadedAt: Date.now(), error: null },
      },
    }));
    return next;
  },

  async toggleDefaultArea(productId, areaPathId) {
    const next = await forgeApi.toggleDefaultAreaPath(productId, areaPathId);
    set((s) => ({
      byProduct: {
        ...s.byProduct,
        [productId]: { ...(s.byProduct[productId] ?? EMPTY), areas: next, loadedAt: Date.now(), error: null },
      },
    }));
  },

  async savePrefix(productId, req) {
    const created = await forgeApi.savePrefix(productId, req);
    await get().invalidate(productId);
    return created;
  },

  async deletePrefix(productId, prefixId) {
    await forgeApi.deleteSavedPrefix(productId, prefixId);
    await get().invalidate(productId);
  },

  async refreshPrefix(productId, prefixId) {
    const updated = await forgeApi.refreshSavedPrefix(productId, prefixId);
    // Refresh both iterations and area paths because either could have changed.
    await get().invalidate(productId);
    return updated;
  },

  async addBinding(productId, repoId, req) {
    await forgeApi.addIterationBinding(repoId, req);
    await get().invalidate(productId);
  },

  async removeBinding(productId, repoId, bindingId) {
    await forgeApi.removeIterationBinding(repoId, bindingId);
    await get().invalidate(productId);
  },

  async setActiveIteration(productId, iterationId) {
    // Empty string clears the override server-side; null leaves it.
    await forgeApi.patchProduct(productId, { activeIterationId: iterationId ?? '' });
  },

  discover(productId, req) {
    return forgeApi.discoverSprintNodes(productId, req);
  },
}));

async function doLoad(
  productId: string,
  set: (fn: (s: SprintsState) => Partial<SprintsState>) => void,
): Promise<void> {
  try {
    const [iterations, areas, prefixes] = await Promise.all([
      forgeApi.listIterations(productId),
      forgeApi.listAreaPaths(productId),
      forgeApi.listSavedPrefixes(productId),
    ]);
    set((s) => ({
      byProduct: {
        ...s.byProduct,
        [productId]: { iterations, areas, prefixes, loadedAt: Date.now(), error: null },
      },
    }));
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    set((s) => ({
      byProduct: {
        ...s.byProduct,
        [productId]: { ...(s.byProduct[productId] ?? EMPTY), error: msg, loadedAt: Date.now() },
      },
    }));
  }
}

// ── Selectors ────────────────────────────────────────────────────────
//
// Stable empty references are critical: returning a fresh `[]` from a
// selector every render means Zustand sees a new identity on every store
// update and re-renders the consumer, which can drive the consumer's
// useEffects into a "Maximum update depth exceeded" loop.

// Object.freeze provides runtime protection against accidental mutation;
// the type stays as the regular mutable array shape so consumers + child
// components don't need a readonly cascade through their prop types.
const EMPTY_ITERS    = Object.freeze([] as ProductIteration[]) as ProductIteration[];
const EMPTY_AREAS    = Object.freeze([] as ProductAreaPath[])  as ProductAreaPath[];
const EMPTY_PREFIXES = Object.freeze([] as SavedPrefix[])      as SavedPrefix[];

export function selectIterations(productId: string) {
  return (s: SprintsState): ProductIteration[] =>
    s.byProduct[productId]?.iterations ?? EMPTY_ITERS;
}
export function selectAreas(productId: string) {
  return (s: SprintsState): ProductAreaPath[] =>
    s.byProduct[productId]?.areas ?? EMPTY_AREAS;
}
export function selectPrefixes(productId: string) {
  return (s: SprintsState): SavedPrefix[] =>
    s.byProduct[productId]?.prefixes ?? EMPTY_PREFIXES;
}

/**
 * `find()` over a stable array returns a stable element reference; safe
 * to call on every render. When the area set changes, the element ref
 * naturally changes too, which is exactly what consumers expect.
 */
export function selectDefaultArea(productId: string) {
  return (s: SprintsState): ProductAreaPath | null => {
    const areas = s.byProduct[productId]?.areas;
    if (!areas) return null;
    return areas.find((a) => a.isDefaultFilter) ?? null;
  };
}

/** Stable iteration-state rank — exposed so consumers can sort with useMemo. */
export function iterationStateRank(i: ProductIteration): number {
  switch (i.state) {
    case 'current': return 0;
    case 'future':  return 1;
    case 'past':    return 2;
    case 'provider-orphan': return 3;
  }
}

/** Tiny helper for the SprintImportKind enum. */
export const SPRINT_IMPORT_KINDS: SprintImportKind[] = ['iterations', 'areas'];
