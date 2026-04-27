import { create } from 'zustand';
import { forgeApi, type WorkItemPatchBody } from '@/api/forgeApi';
import { SAMPLE_WORK_ITEMS } from './sampleWorkItems';
import type { WorkItem, WorkItemState } from './workItemTypes';

interface WorkItemsState {
  items: WorkItem[];
  /** True after the first /api/work-items round-trip resolves. */
  loaded: boolean;
  /** True while the initial load is in flight. */
  loading: boolean;
  /** Error message from the last load attempt; null on success. */
  error: string | null;
  /** True once the API returned 200 — used to decide whether mutations target the API or the local sample. */
  isLive: boolean;

  load: () => Promise<void>;
  refresh: () => Promise<void>;

  /** Optimistic move (drag-drop / detail edit). Calls /move with state. */
  setState: (id: string, state: WorkItemState) => Promise<void>;
  /** Optimistic patch — server reply replaces the local row. */
  patch: (id: string, body: WorkItemPatchBody) => Promise<WorkItem | null>;
  /** Soft delete — flips state to closed. */
  softDelete: (id: string) => Promise<void>;

  /** SignalR fan-out hook. */
  upsertFromRealtime: (item: WorkItem) => void;
  removeFromRealtime: (id: string) => void;
}

export const useWorkItemsStore = create<WorkItemsState>((set, get) => ({
  items: [],
  loaded: false,
  loading: false,
  error: null,
  isLive: false,

  async load() {
    if (get().loading || get().loaded) return;
    set({ loading: true, error: null });
    try {
      const items = await forgeApi.listWorkItems({ pageSize: 500 });
      set({ items, loaded: true, loading: false, isLive: true, error: null });
    } catch (err) {
      // API unreachable — fall back to the prototype sample so the view
      // stays usable in offline / dev-only modes. isLive=false signals the
      // mutators to target the local store only.
      const msg = err instanceof Error ? err.message : 'load failed';
      set({
        items: SAMPLE_WORK_ITEMS,
        loaded: true,
        loading: false,
        isLive: false,
        error: msg,
      });
    }
  },

  async refresh() {
    if (!get().isLive) return;
    try {
      const items = await forgeApi.listWorkItems({ pageSize: 500 });
      set({ items, error: null });
    } catch {
      // keep last good state
    }
  },

  async setState(id, state) {
    const prev = get().items.find((w) => w.id === id);
    if (!prev || prev.state === state) return;
    // Optimistic: flip locally first.
    set((s) => ({
      items: s.items.map((w) => (w.id === id ? { ...w, state } : w)),
    }));
    if (!get().isLive) return;
    try {
      const updated = await forgeApi.moveWorkItem(id, { state });
      set((s) => ({
        items: s.items.map((w) => (w.id === id ? updated : w)),
      }));
    } catch (err) {
      // Roll back.
      set((s) => ({
        items: s.items.map((w) => (w.id === id ? prev : w)),
        error: err instanceof Error ? err.message : 'move failed',
      }));
    }
  },

  async patch(id, body) {
    if (!get().isLive) {
      // Sample-only path: shallow merge so the UI behaves the same.
      set((s) => ({
        items: s.items.map((w) => (w.id === id ? applyPatchLocal(w, body) : w)),
      }));
      return get().items.find((w) => w.id === id) ?? null;
    }
    try {
      const updated = await forgeApi.patchWorkItem(id, body);
      set((s) => ({
        items: s.items.map((w) => (w.id === id ? updated : w)),
      }));
      return updated;
    } catch (err) {
      set({ error: err instanceof Error ? err.message : 'patch failed' });
      return null;
    }
  },

  async softDelete(id) {
    const prev = get().items.find((w) => w.id === id);
    if (!prev) return;
    if (!get().isLive) {
      set((s) => ({
        items: s.items.map((w) => (w.id === id ? { ...w, state: 'closed' as WorkItemState } : w)),
      }));
      return;
    }
    try {
      const updated = await forgeApi.deleteWorkItem(id, false);
      if (updated) {
        set((s) => ({
          items: s.items.map((w) => (w.id === id ? updated : w)),
        }));
      }
    } catch (err) {
      set({ error: err instanceof Error ? err.message : 'delete failed' });
    }
  },

  upsertFromRealtime(item) {
    set((s) => {
      const idx = s.items.findIndex((w) => w.id === item.id);
      if (idx < 0) return { items: [item, ...s.items] };
      const next = [...s.items];
      next[idx] = item;
      return { items: next };
    });
  },

  removeFromRealtime(id) {
    set((s) => ({ items: s.items.filter((w) => w.id !== id) }));
  },
}));

function applyPatchLocal(w: WorkItem, body: WorkItemPatchBody): WorkItem {
  return {
    ...w,
    title: body.title ?? w.title,
    descriptionMd: body.descriptionMd ?? w.descriptionMd,
    state: body.state ?? w.state,
    iterationRef: body.iterationRef ?? w.iterationRef,
    areaPath: body.areaPath ?? w.areaPath,
    assignedTo: body.clearAssignedTo ? null : (body.assignedTo ?? w.assignedTo),
    points: body.points ?? w.points,
    priority: body.priority ?? w.priority,
    tags: body.tags ?? w.tags,
    featureRef: body.clearFeatureRef ? null : (body.featureRef ?? w.featureRef),
    updatedAt: new Date().toISOString(),
  };
}
