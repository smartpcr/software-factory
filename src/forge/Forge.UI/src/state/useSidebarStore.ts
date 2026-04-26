import { create } from 'zustand';

const STORAGE_KEY = 'forge.sidebar.collapsed';

function readInitial(): boolean {
  if (typeof window === 'undefined') return false;
  return window.localStorage.getItem(STORAGE_KEY) === '1';
}

interface SidebarState {
  collapsed: boolean;
  toggle: () => void;
  setCollapsed: (next: boolean) => void;
}

export const useSidebarStore = create<SidebarState>((set, get) => ({
  collapsed: readInitial(),
  toggle: () => get().setCollapsed(!get().collapsed),
  setCollapsed: (next) => {
    try { window.localStorage.setItem(STORAGE_KEY, next ? '1' : '0'); } catch { /* ignore */ }
    set({ collapsed: next });
  },
}));
