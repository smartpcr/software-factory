// -----------------------------------------------------------------------
// <copyright file="usePrsStore.ts" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

import { create } from 'zustand';
import type { Pr } from '@/types';
import { forgeApi } from '@/api/forgeApi';
import { sampleState } from '@/sampleData';
import { useToastStore } from './useToastStore';

export type PrFilter = 'all' | 'need-approval' | 'failed-build' | 'conflicts' | 'my-reviews' | 'open-comments';

interface PrsState {
  prs: Pr[];
  loading: boolean;
  error: string | null;
  filter: PrFilter;
  selectedId: string | null;
  authorFilter: string[];
  workstreamFilter: string[];
  searchQuery: string;
  dateFrom: string | null;
  dateTo: string | null;

  load: () => Promise<void>;
  setFilter: (f: PrFilter) => void;
  setSelected: (id: string | null) => void;
  setAuthorFilter: (authors: string[]) => void;
  setWorkstreamFilter: (ws: string[]) => void;
  setSearchQuery: (q: string) => void;
  setDateRange: (from: string | null, to: string | null) => void;
  upsertFromRealtime: (pr: Pr) => void;

  toggleAutoMerge: (id: string, next?: boolean) => Promise<void>;
  rerun: (id: string) => Promise<void>;
  cancel: (id: string) => Promise<void>;
  approve: (id: string) => Promise<void>;
  requestChanges: (id: string, body: string) => Promise<void>;
  comment: (id: string, body: string) => Promise<void>;
  merge: (id: string) => Promise<void>;
}

export const usePrsStore = create<PrsState>((set, get) => ({
  prs: sampleState.prs,
  loading: false,
  error: null,
  filter: 'all',
  selectedId: null,
  authorFilter: [],
  workstreamFilter: [],
  searchQuery: '',
  dateFrom: null,
  dateTo: null,

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
  setAuthorFilter: (authors) => set({ authorFilter: authors }),
  setWorkstreamFilter: (ws) => set({ workstreamFilter: ws }),
  setSearchQuery: (q) => set({ searchQuery: q }),
  setDateRange: (from, to) => set({ dateFrom: from, dateTo: to }),

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

  approve: async (id) => {
    try {
      const pr = await forgeApi.approvePr(id);
      get().upsertFromRealtime(pr);
      useToastStore.getState().show(`${id} approved`, 'success');
    } catch (err) {
      useToastStore.getState().show(err instanceof Error ? err.message : 'Approve failed', 'danger');
    }
  },

  requestChanges: async (id, body) => {
    try {
      const pr = await forgeApi.requestChangesOnPr(id, body);
      get().upsertFromRealtime(pr);
      useToastStore.getState().show(`${id} changes requested`, 'info');
    } catch (err) {
      useToastStore.getState().show(err instanceof Error ? err.message : 'Request-changes failed', 'danger');
    }
  },

  comment: async (id, body) => {
    try {
      await forgeApi.commentOnPr(id, body);
      useToastStore.getState().show(`Commented on ${id}`, 'success');
    } catch (err) {
      useToastStore.getState().show(err instanceof Error ? err.message : 'Comment failed', 'danger');
    }
  },

  merge: async (id) => {
    try {
      const pr = await forgeApi.mergePr(id);
      get().upsertFromRealtime(pr);
      useToastStore.getState().show(`${id} merged`, 'success');
    } catch (err) {
      useToastStore.getState().show(err instanceof Error ? err.message : 'Merge failed', 'danger');
    }
  },
}));

/** Pure selector helper — call inside `useMemo`. */
export function filterPrs(
  list: Pr[],
  filter: PrFilter,
  authorFilter: string[] = [],
  workstreamFilter: string[] = [],
  searchQuery = '',
  dateFrom: string | null = null,
  dateTo: string | null = null,
): Pr[] {
  let result = list;

  // Chip filter
  switch (filter) {
    case 'need-approval': result = result.filter((p) => p.humanGate); break;
    case 'failed-build':  result = result.filter((p) => p.build?.status === 'failing'); break;
    case 'conflicts':     result = result.filter((p) => p.conflicts); break;
    case 'my-reviews':    result = result.filter((p) => p.review?.verdict === 'pending' || p.review?.verdict === 'changes'); break;
    case 'open-comments': result = result.filter((p) => (p.openCommentCount ?? 0) > 0); break;
    default: break;
  }

  // Author filter
  if (authorFilter.length > 0) {
    result = result.filter((p) => authorFilter.includes(p.author.name));
  }

  // Repo filter (by repoId)
  if (workstreamFilter.length > 0) {
    result = result.filter((p) => p.repoId && workstreamFilter.includes(p.repoId));
  }

  // Search filter (case-insensitive substring on title)
  if (searchQuery.trim()) {
    const q = searchQuery.trim().toLowerCase();
    result = result.filter((p) => p.title.toLowerCase().includes(q));
  }

  // Date range filter
  if (dateFrom) {
    const from = new Date(dateFrom);
    result = result.filter((p) => {
      if (!p.createdAt) return false;
      return new Date(p.createdAt) >= from;
    });
  }
  if (dateTo) {
    const to = new Date(dateTo);
    to.setHours(23, 59, 59, 999);
    result = result.filter((p) => {
      if (!p.createdAt) return false;
      return new Date(p.createdAt) <= to;
    });
  }

  return result;
}
