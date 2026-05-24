// Zustand store backing the three "collapse this header" chevrons added
// to save vertical real estate on the Swarm story page: the global
// top-bar (FORGE logo + product/repo pickers + spend/payroll badges),
// the spawn slim-header (story title + phase + nav breadcrumb), and the
// conversation KPI strip (iter cards + sparkline + generator/evaluator
// progress bars). Each section is independently togglable and the
// preference persists across reloads via localStorage. Mirrors the
// existing `useSidebarStore` pattern.
import { create } from 'zustand';

type Section = 'topbar' | 'spawnHeader' | 'conversationKpi';

const STORAGE_KEY = 'forge.chrome.collapsed';

interface PersistedShape {
  topbar?: boolean;
  spawnHeader?: boolean;
  conversationKpi?: boolean;
}

function readInitial(): PersistedShape {
  if (typeof window === 'undefined') return {};
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw);
    return typeof parsed === 'object' && parsed != null ? parsed : {};
  } catch {
    return {};
  }
}

interface ChromeCollapseState {
  topbar: boolean;
  spawnHeader: boolean;
  conversationKpi: boolean;
  toggle: (section: Section) => void;
  set: (section: Section, next: boolean) => void;
}

const init = readInitial();

export const useChromeCollapseStore = create<ChromeCollapseState>((set, get) => ({
  topbar: init.topbar === true,
  spawnHeader: init.spawnHeader === true,
  conversationKpi: init.conversationKpi === true,
  toggle: (section) => get().set(section, !get()[section]),
  set: (section, next) => {
    set({ [section]: next } as Partial<ChromeCollapseState>);
    try {
      const snapshot: PersistedShape = {
        topbar: get().topbar,
        spawnHeader: get().spawnHeader,
        conversationKpi: get().conversationKpi,
      };
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(snapshot));
    } catch { /* ignore */ }
  },
}));
