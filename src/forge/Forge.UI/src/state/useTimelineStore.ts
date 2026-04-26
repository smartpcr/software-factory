import { create } from 'zustand';
import type { TimelineFilter, TimelineLane, TimelineResponse, TimelineZoom } from '@/types';
import { forgeApi } from '@/api/forgeApi';

/** Day-pixel widths per zoom level. Day = prototype default (60px). */
export const ZOOM_DAY_W: Record<TimelineZoom, number> = {
  day: 90,
  week: 60,
  sprint: 28,
};

interface TimelineState {
  data: TimelineResponse | null;
  loading: boolean;
  error: string | null;

  zoom: TimelineZoom;
  forecast: boolean;
  lane: TimelineLane;
  filter: TimelineFilter;
  collapsedEpics: Set<string>;

  load: () => Promise<void>;
  setZoom: (z: TimelineZoom) => void;
  toggleForecast: () => void;
  setLane: (l: TimelineLane) => void;
  setFilter: (f: TimelineFilter) => void;
  toggleEpic: (epicId: string) => void;
}

export const useTimelineStore = create<TimelineState>((set, get) => ({
  data: null,
  loading: false,
  error: null,

  zoom: 'week',
  forecast: false,
  lane: 'workstream',
  filter: 'all',
  collapsedEpics: new Set(),

  load: async () => {
    set({ loading: true, error: null });
    try {
      const data = await forgeApi.getTimeline(-10, 5);
      set({ data, loading: false });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), loading: false });
    }
  },
  setZoom: (z) => set({ zoom: z }),
  toggleForecast: () => set({ forecast: !get().forecast }),
  setLane: (l) => set({ lane: l }),
  setFilter: (f) => set({ filter: f }),
  toggleEpic: (epicId) => {
    const next = new Set(get().collapsedEpics);
    if (next.has(epicId)) next.delete(epicId);
    else next.add(epicId);
    set({ collapsedEpics: next });
  },
}));
