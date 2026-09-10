import { operatorCsrfHeaders } from '@/api/operatorSession';
// Phase 17 — Zustand store + SignalR plumbing for the task queue.
//
// Subscribes to the existing /hubs/forge connection (re-uses the singleton
// from useForgeHub.ts). Two channels:
//   * `queue.changed` — every state transition, broadcast to all clients.
//   * `queue.strained` — fired on the lead's user channel when a crew
//     queue crosses the depth-or-ETA threshold.
import { useEffect } from 'react';
import { create } from 'zustand';
import { HubConnectionBuilder, LogLevel, HubConnectionState, type HubConnection } from '@microsoft/signalr';
import { forgeApi } from '@/api/forgeApi';
import type { CrewQueueStats, TaskQueueEntry } from '@/types';

interface QueueChangedPayload {
  kind: string;
  row: TaskQueueEntry;
}

interface QueueStore {
  /** All open queue rows we've seen, keyed by id. */
  rows: Record<number, TaskQueueEntry>;
  /** Strain banner state per crew id. */
  strain: Record<string, CrewQueueStats>;
  /** Per-crew queue stats cache (depth, ETA). */
  stats: Record<string, CrewQueueStats>;
  upsertRow: (row: TaskQueueEntry) => void;
  applyChanged: (msg: QueueChangedPayload) => void;
  applyStrain: (snap: CrewQueueStats) => void;
  dismissStrain: (crewId: string) => void;
  refresh: (target?: { kind: string; id: string }) => Promise<void>;
  loadCrewStats: (crewId: string) => Promise<CrewQueueStats | null>;
}

export const useQueueStore = create<QueueStore>((set, get) => ({
  rows: {},
  strain: {},
  stats: {},
  upsertRow: (row) => set({ rows: { ...get().rows, [row.id]: row } }),
  applyChanged: (msg) => {
    const next = { ...get().rows };
    if (msg.row) {
      // Closed states drop out of the local cache.
      if (msg.row.state === 'completed' || msg.row.state === 'cancelled' || msg.row.state === 'reassigned') {
        delete next[msg.row.id];
      } else {
        next[msg.row.id] = msg.row;
      }
    }
    set({ rows: next });
  },
  applyStrain: (snap) => set({ strain: { ...get().strain, [snap.crewId]: snap } }),
  dismissStrain: (crewId) => {
    const next = { ...get().strain };
    delete next[crewId];
    set({ strain: next });
  },
  refresh: async (target) => {
    try {
      const list = target
        ? (target.kind === 'team'
            ? await forgeApi.listTeamQueue(target.id)
            : await forgeApi.listCrewQueue(target.id))
        : await forgeApi.listQueue();
      const map: Record<number, TaskQueueEntry> = {};
      for (const r of list) map[r.id] = r;
      set({ rows: map });
    } catch {
      // network / auth errors fall through; the SignalR feed will catch up.
    }
  },
  loadCrewStats: async (crewId) => {
    try {
      const s = await forgeApi.getCrewQueueStats(crewId);
      set({ stats: { ...get().stats, [crewId]: s } });
      return s;
    } catch {
      return null;
    }
  },
}));

const baseUrl = (typeof import.meta !== 'undefined' && import.meta.env?.VITE_FORGE_API_URL) || '';
const hubUrl = `${baseUrl}/hubs/forge`;

let queueConnection: HubConnection | null = null;
let queueStarting: Promise<void> | null = null;

async function ensureQueueConnection(): Promise<HubConnection> {
  if (queueConnection && queueConnection.state === HubConnectionState.Connected) return queueConnection;
  if (queueStarting) { await queueStarting; return queueConnection!; }
  if (!queueConnection) {
    queueConnection = new HubConnectionBuilder()
      .withUrl(hubUrl, { withCredentials: true, headers: await operatorCsrfHeaders() })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    queueConnection.on('queue.changed', (msg: QueueChangedPayload) => {
      useQueueStore.getState().applyChanged(msg);
    });
    queueConnection.on('queue.strained', (snap: CrewQueueStats) => {
      useQueueStore.getState().applyStrain(snap);
    });
  }
  queueStarting = queueConnection.start().catch((err) => {
    if (typeof import.meta !== 'undefined' && import.meta.env?.DEV) {
      console.warn('[forge] queue SignalR connect failed', err);
    }
  }).finally(() => { queueStarting = null; });
  await queueStarting;
  return queueConnection;
}

/**
 * Phase 17 — connect once and keep the connection alive for the session.
 * Joins per-user/per-crew groups when the caller passes them.
 */
export function useQueueChannel(opts?: { userGroup?: string | null; crewGroup?: string | null }): void {
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const conn = await ensureQueueConnection();
      if (cancelled) return;
      try {
        if (opts?.userGroup) await conn.invoke('Subscribe', `user.${opts.userGroup}`);
        if (opts?.crewGroup) await conn.invoke('Subscribe', `crew.${opts.crewGroup}`);
      } catch { /* ignore — auto-reconnect re-issues these */ }
    })();
    return () => { cancelled = true; };
  }, [opts?.userGroup, opts?.crewGroup]);
}
