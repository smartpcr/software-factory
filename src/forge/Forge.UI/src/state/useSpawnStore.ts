// forge_implementation.md Phase 28 — UI store for story-spawn realtime updates.
//
// The store keeps a per-storyId monotonic tick. SignalR's
// `story-spawn.changed` handler calls `bump(storyId)` to advance the tick;
// `<StoryCard>` subscribes to its tick and re-fetches `/api/stories/{id}/spawn`
// + `/artifacts` whenever it changes. This decouples the UI re-render from
// any network polling.
//
// Phase 51 — list-level tick. `SwarmIndexView` polls the spawn list every
// 15 s; without a signal it can't react to `created`/`deleted`/phase
// transitions from other tabs or the orchestrator until the next poll
// fires. `bumpList()` is fired from the SignalR handler on every
// `story-spawn.changed` event so the index view re-fetches the list
// + per-spawn details immediately. The per-storyId `bump` still
// drives single-row refresh in `<SwarmStoryRow>` / `<SwarmStoryView>` /
// `<StoryCard>`.

import { create } from 'zustand';

interface SpawnStore {
  /** monotonic tick per storyId — bumped on every realtime change event. */
  ticks: Record<string, number>;
  /** Increment the tick for a story id (called from useForgeHub). */
  bump: (storyId: string) => void;
  /**
   * Phase 51 — global list-level tick. Bumped on EVERY story-spawn.changed
   * event so list-rendering views (SwarmIndexView) refresh the spawn list
   * and detail map immediately instead of waiting for their 15 s poll tick.
   */
  listTick: number;
  /** Increment the global list tick (called from useForgeHub). */
  bumpList: () => void;
}

export const useSpawnStore = create<SpawnStore>((set) => ({
  ticks: {},
  bump(storyId: string) {
    set((s) => ({
      ticks: { ...s.ticks, [storyId]: (s.ticks[storyId] ?? 0) + 1 },
    }));
  },
  listTick: 0,
  bumpList() {
    set((s) => ({ listTick: s.listTick + 1 }));
  },
}));
