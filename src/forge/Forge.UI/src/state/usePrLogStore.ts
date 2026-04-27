import { create } from 'zustand';

// Phase 6.7 — buffer of live build-log lines per PR id, fed by SignalR `pr.log`.
// The PrDetailPanel reads this and appends after the static buildLogTail.
interface PrLogState {
  lines: Record<string, string[]>;
  append: (id: string, line: string) => void;
  clear: (id: string) => void;
}

export const usePrLogStore = create<PrLogState>((set, get) => ({
  lines: {},
  append: (id, line) => {
    const prev = get().lines[id] ?? [];
    const next = prev.length > 200 ? [...prev.slice(-200), line] : [...prev, line];
    set({ lines: { ...get().lines, [id]: next } });
  },
  clear: (id) => {
    if (!(id in get().lines)) return;
    const next = { ...get().lines };
    delete next[id];
    set({ lines: next });
  },
}));
