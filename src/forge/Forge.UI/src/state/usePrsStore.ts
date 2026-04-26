import { create } from 'zustand';
import type { Pr } from '@/types';
import { forgeApi } from '@/api/forgeApi';
import { sampleState } from '@/sampleData';
import { useToastStore } from './useToastStore';

export type PrFilter = 'all' | 'need-approval' | 'failing' | 'conflicts' | 'my-reviews';

interface PrsState {
  prs: Pr[];
  loading: boolean;
  error: string | null;
  filter: PrFilter;
  selectedId: string | null;

  load: () => Promise<void>;
  setFilter: (f: PrFilter) => void;
  setSelected: (id: string | null) => void;
  upsertFromRealtime: (pr: Pr) => void;

  toggleAutoMerge: (id: string, next?: boolean) => Promise<void>;
  rerun: (id: string) => Promise<void>;
  cancel: (id: string) => Promise<void>;
}

export const usePrsStore = create<PrsState>((set, get) => ({
  prs: sampleState.prs,
  loading: false,
  error: null,
  filter: 'all',
  selectedId: null,

  load: async () => {
    set({ loading: true, error: null });
    try {
      const prs = await forgeApi.getPrs();
      set({ prs, loading: false });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), loading: false });
    }
  },

  setFilter: (f) => set({ filter: f }),
  setSelected: (id) => set({ selectedId: id }),

  upsertFromRealtime: (pr) => {
    const list = get().prs;
    const idx = list.findIndex((p) => p.id === pr.id);
    if (idx === -1) set({ prs: [...list, pr] });
    else {
      const next = list.slice();
      next[idx] = pr;
      set({ prs: next });
    }
  },

  toggleAutoMerge: async (id, next) => {
    const prev = get().prs.find((p) => p.id === id);
    if (!prev) return;
    const target = next ?? !prev.autoMerge;
    // Optimistic.
    get().upsertFromRealtime({ ...prev, autoMerge: target });
    try {
      const updated = await forgeApi.setAutoMerge(id, target);
      get().upsertFromRealtime(updated);
      useToastStore.getState().show(
        target ? `${id} auto-merge armed` : `${id} auto-merge disarmed`,
        'success',
      );
    } catch (err) {
      // Rollback.
      get().upsertFromRealtime(prev);
      useToastStore.getState().show(err instanceof Error ? err.message : 'Toggle failed', 'danger');
    }
  },

  rerun: async (id) => {
    try {
      const pr = await forgeApi.rerunBuild(id);
      get().upsertFromRealtime(pr);
      useToastStore.getState().show(`${id} build re-queued`, 'success');
    } catch (err) {
      useToastStore.getState().show(err instanceof Error ? err.message : 'Re-run failed', 'danger');
    }
  },

  cancel: async (id) => {
    try {
      const pr = await forgeApi.cancelBuild(id);
      get().upsertFromRealtime(pr);
      useToastStore.getState().show(`${id} build cancelled`, 'success');
    } catch (err) {
      useToastStore.getState().show(err instanceof Error ? err.message : 'Cancel failed', 'danger');
    }
  },
}));

/** Pure selector helper — call inside `useMemo`. */
export function filterPrs(list: Pr[], filter: PrFilter): Pr[] {
  switch (filter) {
    case 'need-approval': return list.filter((p) => p.humanGate);
    case 'failing':       return list.filter((p) => p.build?.status === 'failing');
    case 'conflicts':     return list.filter((p) => p.conflicts);
    case 'my-reviews':    return list.filter((p) => p.review?.verdict === 'pending' || p.review?.verdict === 'changes');
    default:              return list;
  }
}
