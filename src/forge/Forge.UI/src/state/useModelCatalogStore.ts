import { useEffect } from 'react';
import { create } from 'zustand';
import { forgeApi } from '@/api/forgeApi';
import type { ModelInfo } from '@/types';

/**
 * 2026-09-10 — one shared, self-refreshing copy of `GET /api/models`.
 *
 * The catalog is NOT static: the API's daily model-catalog enrichment job
 * fetches the live Copilot catalog, auto-adds newly released models and retires
 * ones it stopped serving. Every consumer used to call `listModels()` once on
 * mount, so a long-lived tab kept showing whatever the catalog looked like when
 * the page was opened — including the boot-time snapshot taken BEFORE the first
 * enrichment run (~25s after the API starts). Sharing one store and revalidating
 * on an interval, on tab focus and on reconnect keeps every picker current
 * without a page reload.
 */

/**
 * How long a fetched catalog is considered fresh. Deliberately SHORTER than
 * {@link POLL_INTERVAL_MS} so a poll tick always passes the freshness check
 * (timer drift must not silently halve the refresh rate) while still collapsing
 * the near-simultaneous ticks of several mounted consumers into one fetch.
 */
const STALE_AFTER_MS = 4 * 60 * 1000;

/** Background revalidation cadence while a consumer is mounted. */
const POLL_INTERVAL_MS = 5 * 60 * 1000;

interface ModelCatalogState {
  models: ModelInfo[];
  loaded: boolean;
  loading: boolean;
  /** Epoch ms of the last successful fetch; 0 when never loaded. */
  fetchedAt: number;
  error: string | null;

  /** Fetch unless a fresh copy is already in hand (or a fetch is in flight). */
  ensureLoaded: () => Promise<void>;
  /** Unconditional refetch of the catalog the API currently serves. */
  refresh: () => Promise<void>;
  /**
   * Ask the API to re-run enrichment against the live Copilot catalog, then
   * adopt the result. Slower than {@link refresh} (it hits the Copilot server),
   * so it is operator-triggered rather than automatic.
   */
  forceServerRefresh: () => Promise<void>;
}

export const useModelCatalogStore = create<ModelCatalogState>((set, get) => ({
  models: [],
  loaded: false,
  loading: false,
  fetchedAt: 0,
  error: null,

  async ensureLoaded() {
    const { loading, fetchedAt } = get();
    if (loading) return;
    if (fetchedAt > 0 && Date.now() - fetchedAt < STALE_AFTER_MS) return;
    await get().refresh();
  },

  async refresh() {
    if (get().loading) return;
    set({ loading: true });
    try {
      const models = await forgeApi.listModels();
      set({ models, loaded: true, loading: false, fetchedAt: Date.now(), error: null });
    } catch (err) {
      // Keep the last good catalog: an offline blip must not empty every picker.
      set({ loading: false, loaded: true, error: err instanceof Error ? err.message : String(err) });
    }
  },

  async forceServerRefresh() {
    if (get().loading) return;
    set({ loading: true });
    try {
      const models = await forgeApi.refreshModels();
      set({ models, loaded: true, loading: false, fetchedAt: Date.now(), error: null });
    } catch (err) {
      set({ loading: false, error: err instanceof Error ? err.message : String(err) });
    }
  },
}));

/**
 * Subscribe to the shared catalog and keep it fresh for as long as the calling
 * component is mounted: load on mount, revalidate every {@link POLL_INTERVAL_MS},
 * and revalidate immediately when the tab regains focus or the browser comes
 * back online (the common case — a laptop resumed after the nightly job ran).
 */
export function useModelCatalog(): ModelInfo[] {
  const models = useModelCatalogStore((s) => s.models);

  useEffect(() => {
    const { ensureLoaded } = useModelCatalogStore.getState();
    void ensureLoaded();

    // ensureLoaded (not refresh) so several mounted consumers — CrewsView plus
    // a modal that stays mounted while closed — share one fetch per staleness
    // window instead of each running its own.
    const timer = window.setInterval(() => { void useModelCatalogStore.getState().ensureLoaded(); }, POLL_INTERVAL_MS);
    const revalidate = () => {
      if (document.visibilityState === 'visible') void useModelCatalogStore.getState().ensureLoaded();
    };
    document.addEventListener('visibilitychange', revalidate);
    window.addEventListener('focus', revalidate);
    window.addEventListener('online', revalidate);
    return () => {
      window.clearInterval(timer);
      document.removeEventListener('visibilitychange', revalidate);
      window.removeEventListener('focus', revalidate);
      window.removeEventListener('online', revalidate);
    };
  }, []);

  return models;
}

/**
 * Options to show in a model `<select>`. Retired models and ones the live
 * Copilot catalog no longer serves are hidden — they only cluttered the list as
 * disabled "— unsupported" rows. `current` is always kept so an already-assigned
 * (now stale) model stays visible and the picker can warn about it.
 */
export function selectableModels(models: ModelInfo[], current?: string | null): ModelInfo[] {
  return models.filter(
    (m) => (m.available !== false && m.status !== 'retired') || (!!current && m.id === current),
  );
}
