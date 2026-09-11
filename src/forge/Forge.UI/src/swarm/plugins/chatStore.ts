import { useCallback, useEffect, useSyncExternalStore } from 'react';
import { operatorSessionChanged } from '@/api/operatorSession';
import { chatApi, type ChatSummary, type ChatSummaryItem } from './chatApi';

type ProductState = { rows: Map<string, ChatSummaryItem>; error: boolean };
const data = new Map<string, ProductState>();
const listeners = new Map<string, Set<() => void>>();
const flights = new Map<string, Promise<void>>();
const timers = new Map<string, ReturnType<typeof setInterval>>();
const empty: ProductState = { rows: new Map(), error: false };
let sessionGeneration = 0;
function emit(product: string) { listeners.get(product)?.forEach(f => f()); }

export function getChatSummary(product: string, spawn: string) { return data.get(product)?.rows.get(spawn)?.summary; }

export function mergeChatSummaries(product: string, rows: ChatSummaryItem[]) {
  const next = new Map(data.get(product)?.rows);
  for (const row of rows) {
    if ((next.get(row.spawnId)?.summary.revision ?? -1) <= row.summary.revision) next.set(row.spawnId, row);
  }
  data.set(product, { rows: next, error: false }); emit(product);
}

export function refreshChatSummaries(product: string): Promise<void> {
  const existing = flights.get(product);
  if (existing) return existing;
  const generation = sessionGeneration;
  const request = chatApi.summaries(product).then(rows => {
    if (generation === sessionGeneration) mergeChatSummaries(product, rows);
  }).catch(() => {
    if (generation === sessionGeneration) { data.set(product, { rows: data.get(product)?.rows ?? new Map(), error: true }); emit(product); }
  }).finally(() => { if (flights.get(product) === request) flights.delete(product); });
  flights.set(product, request); return request;
}

export async function reloadChatSummaries(product: string) {
  // A save may finish while an older list request is in flight. Always fetch once after it completes.
  await flights.get(product); await refreshChatSummaries(product);
}

export function useChatSummary(product: string, spawn: string, initial?: ChatSummary, title = spawn) {
  const state = useSyncExternalStore(useCallback(callback => {
    let group = listeners.get(product);
    if (!group) { group = new Set(); listeners.set(product, group); }
    group.add(callback);
    if (!timers.has(product)) {
      void refreshChatSummaries(product);
      timers.set(product, setInterval(() => { if (!document.hidden) void refreshChatSummaries(product); }, 10000));
    }
    return () => {
      group!.delete(callback);
      if (!group!.size) { clearInterval(timers.get(product)); timers.delete(product); }
    };
  }, [product]), () => data.get(product) ?? empty);
  useEffect(() => {
    const cached = getChatSummary(product, spawn);
    // List/detail seeds must not overwrite fresher health or display names at the same configuration revision.
    if (initial && (!cached || cached.revision < initial.revision)) mergeChatSummaries(product, [{ spawnId: spawn, title, summary: initial }]);
  }, [product, spawn, initial, title]);
  return { summary: state.rows.get(spawn)?.summary ?? initial, error: state.error, rows: [...state.rows.values()] };
}

if (typeof window !== 'undefined') window.addEventListener(operatorSessionChanged, () => {
  sessionGeneration++; data.clear(); flights.clear(); listeners.forEach((_, product) => emit(product));
});