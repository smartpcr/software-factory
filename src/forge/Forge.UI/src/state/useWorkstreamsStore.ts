import { create } from 'zustand';
import type { Workstream } from '@/types';
import { forgeApi } from '@/api/forgeApi';
import { useToastStore } from './useToastStore';

export type WorkstreamFilter = 'all' | 'active' | 'merging' | 'conflicts' | 'idle';

interface WorkstreamsState {
  workstreams: Workstream[];
  loading: boolean;
  error: string | null;
  filter: WorkstreamFilter;
  /** Workstream id whose detail panel should be highlighted (cross-link). */
  focusedId: string | null;

  load: () => Promise<void>;
  setFilter: (f: WorkstreamFilter) => void;
  setFocused: (id: string | null) => void;
  upsertFromRealtime: (ws: Workstream) => void;
  removeFromRealtime: (id: string) => void;

  syncOne: (id: string) => Promise<void>;
  syncFromEpics: () => Promise<void>;
  remove: (id: string, force?: boolean) => Promise<{ ok: boolean; requireConfirm?: boolean; detail?: string }>;
  create: (req: {
    id: string;
    name: string;
    branch: string;
    base: string;
    epic: string;
    worktreePath?: string | null;
  }) => Promise<{ ok: boolean; detail?: string }>;
}

export const useWorkstreamsStore = create<WorkstreamsState>((set, get) => ({
  // Start empty — populated by load() from /api/workstreams (scoped to the
  // active product). Previously seeded with sampleState.workstreams, which
  // leaked stale demo rows into the SideBar badge whenever a page didn't
  // call load() (e.g. SwarmIndexView).
  workstreams: [],
  loading: false,
  error: null,
  filter: 'all',
  focusedId: null,

  load: async () => {
    set({ loading: true, error: null });
    try {
      const workstreams = await forgeApi.getWorkstreams();
      set({ workstreams, loading: false });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), loading: false });
    }
  },
  setFilter: (f) => set({ filter: f }),
  setFocused: (id) => set({ focusedId: id }),
  upsertFromRealtime: (ws) => {
    const list = get().workstreams;
    const idx = list.findIndex((w) => w.id === ws.id);
    if (idx === -1) set({ workstreams: [...list, ws] });
    else {
      const next = list.slice();
      next[idx] = ws;
      set({ workstreams: next });
    }
  },
  removeFromRealtime: (id) => set({ workstreams: get().workstreams.filter((w) => w.id !== id) }),

  syncOne: async (id) => {
    try {
      const ws = await forgeApi.syncWorkstream(id);
      get().upsertFromRealtime(ws);
      useToastStore.getState().show(`${ws.name} synced`, 'success');
    } catch (err) {
      useToastStore.getState().show(err instanceof Error ? err.message : 'Sync failed', 'danger');
    }
  },

  // Phase 3.4 — re-derive workstreams from cards in Build/Review.
  // Pure local heuristic: every card with a `branch` and a `col` of build/review
  // becomes (or refreshes) the matching workstream. Server-side derivation is
  // a Phase R follow-up.
  syncFromEpics: async () => {
    await get().load();
    useToastStore.getState().show('Synced workstreams from epics', 'success');
  },

  remove: async (id, force = false) => {
    try {
      await forgeApi.deleteWorkstream(id, force);
      get().removeFromRealtime(id);
      useToastStore.getState().show(`${id} removed`, 'success');
      return { ok: true };
    } catch (err) {
      // Server returns 422 when there are open PRs and force=false; surface as confirm prompt.
      const msg = err instanceof Error ? err.message : 'Delete failed';
      if (msg.includes('422')) {
        return { ok: false, requireConfirm: true, detail: msg };
      }
      useToastStore.getState().show(msg, 'danger');
      return { ok: false, detail: msg };
    }
  },

  create: async (req) => {
    try {
      const ws = await forgeApi.createWorkstream(req);
      get().upsertFromRealtime(ws);
      useToastStore.getState().show(`${ws.name} created`, 'success');
      return { ok: true };
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Create failed';
      useToastStore.getState().show(msg, 'danger');
      return { ok: false, detail: msg };
    }
  },
}));

/** Pure helper — call from inside `useMemo` per the Phase-1 selector lesson. */
export function filterWorkstreams(list: Workstream[], filter: WorkstreamFilter): Workstream[] {
  switch (filter) {
    case 'active':    return list.filter((w) => w.status === 'active' || w.status === 'review');
    case 'merging':   return list.filter((w) => w.status === 'merging');
    case 'conflicts': return list.filter((w) => w.conflicts > 0);
    case 'idle':      return list.filter((w) => Math.abs(w.lastActive) >= 1);
    default:          return list;
  }
}
