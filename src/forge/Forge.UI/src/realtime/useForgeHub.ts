import { useEffect } from 'react';
import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr';
import type { Card, Pr, Workstream } from '@/types';
import { useBoardStore } from '@/state/useBoardStore';
import { usePrsStore } from '@/state/usePrsStore';
import { usePrLogStore } from '@/state/usePrLogStore';
import { useToastStore } from '@/state/useToastStore';
import { useSprintsStore } from '@/state/useSprintsStore';
import { useSpawnStore } from '@/state/useSpawnStore';
import { useWorkstreamsStore } from '@/state/useWorkstreamsStore';
import { useWorkItemsStore } from '@/workitems/useWorkItemsStore';
import { useTaskLiveStore } from '@/realtime/useTaskLiveStore';
import type { WorkItem } from '@/workitems/workItemTypes';

const baseUrl = import.meta.env.VITE_FORGE_API_URL ?? '';
const hubUrl = `${baseUrl}/hubs/forge`;

let connection: HubConnection | null = null;
let starting: Promise<void> | null = null;

interface CardChangedPayload {
  kind: 'created' | 'updated' | 'moved';
  card: Card;
}

interface PrChangedPayload {
  kind: 'autoMerge' | 'build.rerun' | 'build.cancel' | 'updated';
  pr: Pr;
}

interface WorkstreamChangedPayload {
  kind?: 'created' | 'synced' | 'deleted' | 'restarted' | 'retried' | 'cancelled' | 'accepted' | 'updated';
  id?: string;
  workstream?: Workstream;
}

async function ensureConnected() {
  if (connection?.state === 'Connected') return;
  if (starting) return starting;
  if (!connection) {
    connection = new HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    connection.on('card.changed', (payload: CardChangedPayload) => {
      if (payload?.card) {
        useBoardStore.getState().upsertFromRealtime(payload.card);
      }
    });
    connection.on('pr.changed', (payload: PrChangedPayload) => {
      if (payload?.pr) {
        usePrsStore.getState().upsertFromRealtime(payload.pr);
        if (payload.kind === 'build.rerun') {
          useToastStore.getState().show(`${payload.pr.id} build re-queued`, 'info');
        } else if (payload.kind === 'build.cancel') {
          useToastStore.getState().show(`${payload.pr.id} build cancelled`, 'info');
        }
      }
    });
    // Phase 6.7 — stream build-log lines into the PR log store.
    connection.on('pr.log', (payload: { id?: string; line?: string }) => {
      if (payload?.id && typeof payload.line === 'string') {
        usePrLogStore.getState().append(payload.id, payload.line);
      }
    });
    connection.on('workstream.changed', (payload: WorkstreamChangedPayload) => {
      const store = useWorkstreamsStore.getState();
      if (payload?.kind === 'deleted' && payload.id) {
        store.removeFromRealtime(payload.id);
        return;
      }
      if (payload?.kind === 'restarted' && payload.id) {
        useTaskLiveStore.getState().resetEntry(payload.id);
      }
      if (payload?.workstream) {
        store.upsertFromRealtime(payload.workstream);
      } else {
        void store.load();
      }
    });
    // Phase 15 — sprint registry changes invalidate the per-product cache.
    connection.on('sprint.changed', (payload: { productId?: string }) => {
      if (payload?.productId) {
        void useSprintsStore.getState().invalidate(payload.productId);
      }
    });
    // Phase 12 — work-item changes fan out to the WIT store.
    connection.on('workitem.changed', (payload: { kind?: string; workItem?: WorkItem; id?: string }) => {
      const store = useWorkItemsStore.getState();
      if (payload?.kind === 'deleted' && payload?.id) {
        store.removeFromRealtime(payload.id);
        return;
      }
      if (payload?.kind === 'synced') {
        // Bulk pull — re-fetch the full slice once.
        void store.refresh();
        return;
      }
      if (payload?.workItem) {
        store.upsertFromRealtime(payload.workItem);
      }
    });
    // forge_implementation.md Phase 28 — story-spawn lifecycle events. The
    // workstreams view subscribes to these so its story cards reflect phase
    // advances + auto-orchestration in real time without polling.
    connection.on('story-spawn.changed', (payload: { kind?: string; storyId?: string; phase?: string }) => {
      // Phase 51 — always bump the GLOBAL list tick so SwarmIndexView's
      // list/details refetch immediately. The `created` and `deleted`
      // kinds carry no per-storyId association on every event (the
      // server's `kind=created` payload nests storyId inside `spawn`),
      // so the list-level tick is the only reliable signal for them.
      useSpawnStore.getState().bumpList();

      if (!payload?.storyId) return;
      // Bump a tick on the spawn store so per-story subscribed components
      // re-fetch detail (SwarmStoryRow, SwarmStoryView, StoryCard).
      useSpawnStore.getState().bump(payload.storyId);
      // Re-pull the workstreams slice on the kinds that mutate the
      // workstream table — `decomposed` adds new rows, the orchestrator
      // PR/merge transitions flip status (planning → active → review →
      // complete). The sidebar badge subscribes to this store so the
      // count stays live without polling. Other kinds (plan-pr-opened,
      // qa-pr-merged, spawn-error, decomposition-approved) don't touch
      // the workstreams table; skip the reload to avoid extra fetches.
      if (
        payload.kind === 'decomposed' ||
        payload.kind === 'ws-pr-opened' ||
        payload.kind === 'ws-complete' ||
        payload.kind === 'done' ||
        payload.kind === 'story-closed'
      ) {
        void useWorkstreamsStore.getState().load();
      }
      if (payload.kind === 'plan-pr-opened') {
        useToastStore.getState().show(`Story ${payload.storyId}: plan PR opened`, 'info');
      } else if (payload.kind === 'decomposed') {
        useToastStore.getState().show(`Story ${payload.storyId}: decomposed`, 'info');
      } else if (payload.kind === 'qa-pr-merged') {
        useToastStore.getState().show(`Story ${payload.storyId}: QA features merged`, 'success');
      } else if (payload.kind === 'done' || payload.kind === 'story-closed') {
        useToastStore.getState().show(`Story ${payload.storyId}: complete ✓`, 'success');
      }
    });

    // Phase 29 — build validation completion toast (global, not modal-scoped).
    connection.on('repo.validation-complete', (payload: {
      runId?: string; repoId?: string; status?: string;
      allPassed?: boolean; ecosystem?: string; errorMessage?: string;
      steps?: Array<{ step: string; passed: boolean }>;
    }) => {
      if (!payload?.repoId) return;
      const stepSummary = payload.steps
        ?.map((s) => `${s.step}:${s.passed ? 'ok' : 'fail'}`)
        .join(' → ') ?? '';
      if (payload.allPassed) {
        useToastStore.getState().show(
          `Build validation passed ✓ ${stepSummary}`,
          'success',
        );
      } else {
        useToastStore.getState().show(
          payload.errorMessage
            ? `Validation failed: ${payload.errorMessage}`
            : `Validation failed: ${stepSummary}`,
          'danger',
        );
      }
    });
  }
  starting = connection.start().catch((err) => {
    if (import.meta.env.DEV) {
      console.warn('[forge] SignalR connect failed (likely API offline)', err);
    }
  }).finally(() => {
    starting = null;
  });
  return starting;
}

/** Mount-once hook that opens a SignalR connection and dispatches into the board store. */
export function useForgeHub(): void {
  useEffect(() => {
    void ensureConnected();
    // Connection is intentionally process-lifetime; the hook does not stop it
    // on unmount because StrictMode in dev would tear it down repeatedly.
  }, []);
}
