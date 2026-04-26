import { create } from 'zustand';
import type { WorkItemPriority, WorkItemState, WorkItemType } from './workItemTypes';

export type WitMode = 'board' | 'timeline' | 'tree' | 'list';

export type WitChip = 'all' | 'mine' | 'active' | 'blocked' | 'watching';

export interface WitFilters {
  chip: WitChip;
  type: WorkItemType | 'any';
  state: WorkItemState | 'any';
  iterationRef: string | 'any';
  assignee: string | 'any';
  repoId: string | 'any';
  /** Free-text search across title + tags. */
  q: string;
  /** Phase 12 — owner identifier used by the "Mine" chip. Defaults to current PO seed. */
  meId: string;
}

interface WitState {
  mode: WitMode;
  filters: WitFilters;
  /** Multi-select for Board / List modes — set of work item ids. */
  selection: Set<string>;
  /** Item id whose detail panel is open in the right rail. */
  focusedId: string | null;
  /** Mockup-only: per-item state overrides written by drag-drop / inline edit. */
  stateOverrides: Record<string, WorkItemState>;
  /** Detail-panel layout: full-screen overlay vs. side-rail. */
  panelExpanded: boolean;

  setMode: (mode: WitMode) => void;
  setFilters: (patch: Partial<WitFilters>) => void;
  resetFilters: () => void;
  toggleSelect: (id: string, additive: boolean) => void;
  clearSelection: () => void;
  selectMany: (ids: string[]) => void;
  focus: (id: string | null) => void;
  /** Sample-only: shift an item to a different state column. */
  setItemState: (id: string, state: WorkItemState) => void;
  togglePanelExpanded: () => void;
}

const MODE_KEY = 'forge.wit.mode';
const FILTERS_KEY = 'forge.wit.filters';

const DEFAULT_FILTERS: WitFilters = {
  chip: 'all',
  type: 'any',
  state: 'any',
  iterationRef: 'any',
  assignee: 'any',
  repoId: 'any',
  q: '',
  meId: 'XD',
};

function loadMode(): WitMode {
  if (typeof window === 'undefined') return 'board';
  const raw = window.localStorage.getItem(MODE_KEY);
  return raw === 'timeline' || raw === 'tree' || raw === 'list' || raw === 'board' ? raw : 'board';
}

function loadFilters(): WitFilters {
  if (typeof window === 'undefined') return DEFAULT_FILTERS;
  try {
    const raw = window.localStorage.getItem(FILTERS_KEY);
    if (!raw) return DEFAULT_FILTERS;
    const parsed = JSON.parse(raw);
    return { ...DEFAULT_FILTERS, ...parsed };
  } catch {
    return DEFAULT_FILTERS;
  }
}

function saveMode(mode: WitMode) {
  if (typeof window !== 'undefined') window.localStorage.setItem(MODE_KEY, mode);
}

function saveFilters(filters: WitFilters) {
  if (typeof window !== 'undefined') window.localStorage.setItem(FILTERS_KEY, JSON.stringify(filters));
}

export const useWitStore = create<WitState>((set, get) => ({
  mode: loadMode(),
  filters: loadFilters(),
  selection: new Set(),
  focusedId: null,
  stateOverrides: {},
  panelExpanded: false,

  setMode(mode) {
    saveMode(mode);
    set({ mode });
  },

  setItemState(id, state) {
    set((s) => ({ stateOverrides: { ...s.stateOverrides, [id]: state } }));
  },

  togglePanelExpanded() {
    set((s) => ({ panelExpanded: !s.panelExpanded }));
  },

  setFilters(patch) {
    const next = { ...get().filters, ...patch };
    saveFilters(next);
    set({ filters: next });
  },

  resetFilters() {
    saveFilters(DEFAULT_FILTERS);
    set({ filters: DEFAULT_FILTERS });
  },

  toggleSelect(id, additive) {
    const sel = new Set(additive ? get().selection : []);
    if (sel.has(id)) sel.delete(id);
    else sel.add(id);
    set({ selection: sel });
  },

  selectMany(ids) {
    set({ selection: new Set(ids) });
  },

  clearSelection() {
    set({ selection: new Set() });
  },

  focus(id) {
    // Closing the panel also collapses any expanded overlay so reopening
    // a different item starts in the default side-rail layout.
    set((s) => ({ focusedId: id, panelExpanded: id ? s.panelExpanded : false }));
  },
}));

/** Helper: priority weight for sorting. */
export const PRIORITY_WEIGHT: Record<WorkItemPriority, number> = { high: 0, med: 1, low: 2 };
