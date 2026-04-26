import { create } from 'zustand';
import type { ForgeState } from '@/types';
import { forgeApi } from '@/api/forgeApi';

interface StateStore {
  state: ForgeState | null;
  loading: boolean;
  error: string | null;
  load: () => Promise<void>;
}

export const useStateStore = create<StateStore>((set) => ({
  state: null,
  loading: false,
  error: null,
  load: async () => {
    set({ loading: true, error: null });
    try {
      const state = await forgeApi.getState();
      set({ state, loading: false });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), loading: false });
    }
  },
}));
