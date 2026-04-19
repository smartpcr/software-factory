import { useCallback, useSyncExternalStore } from 'react';

const STORAGE_KEY = 'flow-studio:theme';
type Theme = 'dark' | 'light';

function getSystemTheme(): Theme {
  if (typeof window === 'undefined') return 'dark';
  return window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
}

function getStoredTheme(): Theme {
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    if (saved === 'dark' || saved === 'light') return saved;
  } catch {
    /* storage unavailable */
  }
  return getSystemTheme();
}

function applyTheme(theme: Theme): void {
  document.documentElement.setAttribute('data-theme', theme);
}

function subscribe(onStoreChange: () => void): () => void {
  const handler = (e: StorageEvent) => {
    if (e.key === STORAGE_KEY) onStoreChange();
  };
  window.addEventListener('storage', handler);

  const mq = window.matchMedia('(prefers-color-scheme: light)');
  const mqHandler = () => {
    if (!localStorage.getItem(STORAGE_KEY)) onStoreChange();
  };
  mq.addEventListener('change', mqHandler);

  return () => {
    window.removeEventListener('storage', handler);
    mq.removeEventListener('change', mqHandler);
  };
}

function getSnapshot(): Theme {
  return (document.documentElement.getAttribute('data-theme') as Theme) ?? getStoredTheme();
}

export function useTheme() {
  const theme = useSyncExternalStore(subscribe, getSnapshot, () => 'dark' as Theme);

  const toggleTheme = useCallback(() => {
    const current = document.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark';
    const next: Theme = current === 'light' ? 'dark' : 'light';
    applyTheme(next);
    try {
      localStorage.setItem(STORAGE_KEY, next);
    } catch {
      /* storage unavailable */
    }
  }, []);

  const setTheme = useCallback((t: Theme) => {
    applyTheme(t);
    try {
      localStorage.setItem(STORAGE_KEY, t);
    } catch {
      /* storage unavailable */
    }
  }, []);

  return { theme, toggleTheme, setTheme } as const;
}
