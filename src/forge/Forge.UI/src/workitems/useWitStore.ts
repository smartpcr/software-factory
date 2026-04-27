import { create } from 'zustand';
import type { WorkItemPriority, WorkItemState, WorkItemType } from './workItemTypes';

export type WitMode = 'board' | 'timeline' | 'tree' | 'list';

export type WitChip = 'all' | 'mine' | 'active' | 'blocked' | 'watching';

export interface WitFilters {
  chip: WitChip;
  /** Multi-select — empty array means "any". */
  type: WorkItemType[];
  /** Multi-select — empty array means "any". */
  state: WorkItemState[];
  /** Multi-select — empty array means "any". `'__unassigned__'` is a valid sentinel inside `assignee`. */
  iterationRef: string[];
  assignee: string[];
  repoId: string[];
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
  type: [],
  state: [],
  iterationRef: [],
  assignee: [],
  repoId: [],
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
    return { ...DEFAULT_FILTERS, ...parsed, ...migrateLegacy(parsed) };
  } catch {
    return DEFAULT_FILTERS;
  }
}

// Pre-multiselect builds stored filters as scalars with 'any' as the
// "no filter" sentinel. Convert those to arrays so older sessions don't
// crash when reading their saved state.
function migrateLegacy(parsed: Record<string, unknown>): Partial<WitFilters> {
  const out: Partial<WitFilters> = {};
  const keys: Array<keyof WitFilters> = ['type', 'state', 'iterationRef', 'assignee', 'repoId'];
  for (const k of keys) {
    const v = parsed[k];
    if (Array.isArray(v)) continue;
    if (typeof v === 'string') {
      // 'any' was the legacy "no filter" sentinel.
      (out as Record<string, unknown>)[k] = v === 'any' ? [] : [v];
    }
  }
  return out;
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
