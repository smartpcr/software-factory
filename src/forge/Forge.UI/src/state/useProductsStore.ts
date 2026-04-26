import { create } from 'zustand';
import type { Product, ProductStats, Repo } from '@/types';
import { forgeApi, getActiveProductId, setActiveProductId } from '@/api/forgeApi';

interface ProductsState {
  products: Product[];
  reposByProduct: Record<string, Repo[]>;
  statsByProduct: Record<string, ProductStats>;
  activeProductId: string | null;
  loaded: boolean;

  load: () => Promise<void>;
  loadReposFor: (productId: string) => Promise<void>;
  loadStatsFor: (productId: string) => Promise<void>;
  setActive: (id: string | null) => void;
}

export const useProductsStore = create<ProductsState>((set, get) => ({
  products: [],
  reposByProduct: {},
  statsByProduct: {},
  activeProductId: getActiveProductId(),
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
      set((s) => ({ reposByProduct: { ...s.reposByProduct, [productId]: repos } }));
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
    set({ activeProductId: id });
    // Hard reload so every store re-fetches under the new scope. Cheap and
    // avoids subtle bugs where one cache is from product A and another from B.
    if (typeof window !== 'undefined') window.location.reload();
  },
}));
