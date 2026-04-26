import { create } from 'zustand';

export type Theme = 'light' | 'dark';

const STORAGE_KEY = 'forge.theme';

function readInitial(): Theme {
  if (typeof window === 'undefined') return 'dark';
  const stored = window.localStorage.getItem(STORAGE_KEY);
  if (stored === 'light' || stored === 'dark') return stored;
  // Prototype defaults to dark (data-theme="dark" on <html>).
  return 'dark';
}

function applyToDom(theme: Theme): void {
  if (typeof document === 'undefined') return;
  document.documentElement.setAttribute('data-theme', theme);
}

interface ThemeState {
  theme: Theme;
  setTheme: (next: Theme) => void;
  toggle: () => void;
}

export const useThemeStore = create<ThemeState>((set, get) => {
  const initial = readInitial();
  applyToDom(initial);
  return {
    theme: initial,
    setTheme: (next) => {
      applyToDom(next);
      try { window.localStorage.setItem(STORAGE_KEY, next); } catch { /* private mode */ }
      set({ theme: next });
    },
    toggle: () => get().setTheme(get().theme === 'dark' ? 'light' : 'dark'),
  };
});
