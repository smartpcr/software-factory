import { create } from 'zustand';
import type { Product, ProductStats, Repo } from '@/types';
import {
  forgeApi,
  getActiveProductId,
  setActiveProductId,
  getActiveRepoId,
  setActiveRepoId,
} from '@/api/forgeApi';

interface ProductsState {
  products: Product[];
  reposByProduct: Record<string, Repo[]>;
  statsByProduct: Record<string, ProductStats>;
  activeProductId: string | null;
  /** Phase 80.G — active repo within the product. `null` = all repos
   *  (product-wide view; new stories bind to the product's default repo). */
  activeRepoId: string | null;
  loaded: boolean;

  load: () => Promise<void>;
  loadReposFor: (productId: string) => Promise<void>;
  loadStatsFor: (productId: string) => Promise<void>;
  setActive: (id: string | null) => void;
  setActiveRepo: (id: string | null) => void;
  patchProduct: (id: string, req: { name?: string; description?: string }) => Promise<void>;
  deleteProduct: (id: string, force?: boolean) => Promise<void>;
}

export const useProductsStore = create<ProductsState>((set, get) => ({
  products: [],
  reposByProduct: {},
  statsByProduct: {},
  activeProductId: getActiveProductId(),
  activeRepoId: getActiveRepoId(),
  loaded: false,

  async load() {
    try {
      const products = await forgeApi.listProducts();
      // Default the active product if nothing is selected yet.
      const current = get().activeProductId;
      const next = current && products.some((p) => p.id === current)
        ? current
        : products[0]?.id ?? null;
      if (next !== current) {
        setActiveProductId(next);
      }
      set({ products, activeProductId: next, loaded: true });
    } catch {
      // Best-effort. /products view degrades gracefully when API is offline.
      set({ loaded: true });
    }
  },

  async loadReposFor(productId: string) {
    try {
      const repos = await forgeApi.listProductRepos(productId);
      set((s) => {
        const next: Partial<ProductsState> = {
          reposByProduct: { ...s.reposByProduct, [productId]: repos },
        };
        // Phase 80.G — drop a stale repo selection that no longer belongs to
        // the active product (e.g. repo deleted, or carried across a reload).
        if (
          productId === s.activeProductId &&
          s.activeRepoId &&
          !repos.some((r) => r.id === s.activeRepoId)
        ) {
          setActiveRepoId(null);
          next.activeRepoId = null;
        }
        return next as ProductsState;
      });
    } catch {
      // ignore
    }
  },

  async loadStatsFor(productId: string) {
    try {
      const stats = await forgeApi.getProductStats(productId);
      set((s) => ({ statsByProduct: { ...s.statsByProduct, [productId]: stats } }));
    } catch {
      // ignore
    }
  },

  setActive(id) {
    setActiveProductId(id);
    // Phase 80.G — a repo belongs to one product, so a product switch must
    // clear the active repo (reset to all-repos) rather than carry it over.
    setActiveRepoId(null);
    set({ activeProductId: id, activeRepoId: null });
    // Hard reload so every store re-fetches under the new scope. Cheap and
    // avoids subtle bugs where one cache is from product A and another from B.
    if (typeof window !== 'undefined') window.location.reload();
  },

  setActiveRepo(id) {
    setActiveRepoId(id);
    set({ activeRepoId: id });
    // Phase 80.G — like the product switch, reload so every repo-scoped store
    // (board / workstreams / prs / live-exec) re-fetches under the new repo.
    if (typeof window !== 'undefined') window.location.reload();
  },

  async patchProduct(id, req) {
    const updated = await forgeApi.patchProduct(id, req);
    set((s) => ({
      products: s.products.map((p) => (p.id === id ? updated : p)),
    }));
  },

  async deleteProduct(id, force = false) {
    await forgeApi.deleteProduct(id, force);
    set((s) => {
      const products = s.products.filter((p) => p.id !== id);
      const next = { ...s.reposByProduct };
      delete next[id];
      const stats = { ...s.statsByProduct };
      delete stats[id];
      const activeProductId = s.activeProductId === id
        ? products[0]?.id ?? null
        : s.activeProductId;
      // Phase 80.G — deleting the active product also clears its repo selection.
      let activeRepoId = s.activeRepoId;
      if (s.activeProductId === id) {
        setActiveProductId(activeProductId);
        setActiveRepoId(null);
        activeRepoId = null;
      }
      return { products, reposByProduct: next, statsByProduct: stats, activeProductId, activeRepoId };
    });
  },
}));
