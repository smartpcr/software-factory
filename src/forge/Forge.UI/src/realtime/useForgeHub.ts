import { useEffect } from 'react';
import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr';
import type { Card, Pr } from '@/types';
import { useBoardStore } from '@/state/useBoardStore';
import { usePrsStore } from '@/state/usePrsStore';
import { usePrLogStore } from '@/state/usePrLogStore';
import { useToastStore } from '@/state/useToastStore';

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
