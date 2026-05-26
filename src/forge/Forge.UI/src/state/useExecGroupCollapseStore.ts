// Persistent per-group collapse state for the exec (agent status) board
// on /work-items. Each group section (one per story / epic / phase / agent /
// priority value depending on the active GROUP BY) gets an independently
// togglable chevron in its header so the operator can fold large stories
// out of the way. Keyed by `${groupBy}:${groupKey}` so toggles don't bleed
// across the GROUP BY modes (the same id string can mean different things
// — e.g. a story id vs an agent id). Persists across reloads via
// localStorage. Mirrors the pattern in useChromeCollapseStore.
import { create } from 'zustand';

const STORAGE_KEY = 'forge.execGroup.collapsed';

function readInitial(): Record<string, boolean> {
  if (typeof window === 'undefined') return {};
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed == null) return {};
    const out: Record<string, boolean> = {};
    for (const [k, v] of Object.entries(parsed)) {
      if (typeof v === 'boolean') out[k] = v;
    }
    return out;
  } catch {
    return {};
  }
}

interface ExecGroupCollapseState {
  collapsed: Record<string, boolean>;
  isCollapsed: (groupBy: string, key: string) => boolean;
  toggle: (groupBy: string, key: string) => void;
  setCollapsed: (groupBy: string, key: string, next: boolean) => void;
}

function persist(snapshot: Record<string, boolean>) {
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(snapshot));
  } catch { /* ignore */ }
}

export const useExecGroupCollapseStore = create<ExecGroupCollapseState>((set, get) => ({
  collapsed: readInitial(),
  isCollapsed: (groupBy, key) => get().collapsed[`${groupBy}:${key}`] === true,
  toggle: (groupBy, key) => {
    const composite = `${groupBy}:${key}`;
    const next = !get().collapsed[composite];
    const snapshot = { ...get().collapsed, [composite]: next };
    if (!next) delete snapshot[composite];
    set({ collapsed: snapshot });
    persist(snapshot);
  },
  setCollapsed: (groupBy, key, next) => {
    const composite = `${groupBy}:${key}`;
    const snapshot = { ...get().collapsed };
    if (next) snapshot[composite] = true;
    else delete snapshot[composite];
    set({ collapsed: snapshot });
    persist(snapshot);
  },
}));
