import { create } from 'zustand';

export interface LogEntry {
  timestamp: string;
  level: 'info' | 'success' | 'warn' | 'error';
  nodeId: string | null;
  msg: string;
}

interface LogState {
  entries: LogEntry[];
  addEntry: (entry: Omit<LogEntry, 'timestamp'>) => void;
  clear: () => void;
}

function formatTime(): string {
  const d = new Date();
  return d.toLocaleTimeString('en-US', { hour12: false });
}

export const useLogStore = create<LogState>()((set) => ({
  entries: [],
  addEntry: (entry) =>
    set((state) => ({
      entries: [
        ...state.entries.slice(-499),
        { ...entry, timestamp: formatTime() },
      ],
    })),
  clear: () => set({ entries: [] }),
}));
