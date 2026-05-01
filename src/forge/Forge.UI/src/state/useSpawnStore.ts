// forge_implementation.md Phase 28 — UI store for story-spawn realtime updates.
//
// The store keeps a per-storyId monotonic tick. SignalR's
// `story-spawn.changed` handler calls `bump(storyId)` to advance the tick;
// `<StoryCard>` subscribes to its tick and re-fetches `/api/stories/{id}/spawn`
// + `/artifacts` whenever it changes. This decouples the UI re-render from
// any network polling.

import { create } from 'zustand';

interface SpawnStore {
  /** monotonic tick per storyId — bumped on every realtime change event. */
  ticks: Record<string, number>;
  /** Increment the tick for a story id (called from useForgeHub). */
  bump: (storyId: string) => void;
}

export const useSpawnStore = create<SpawnStore>((set) => ({
  ticks: {},
  bump(storyId: string) {
    set((s) => ({
      ticks: { ...s.ticks, [storyId]: (s.ticks[storyId] ?? 0) + 1 },
    }));
  },
}));
