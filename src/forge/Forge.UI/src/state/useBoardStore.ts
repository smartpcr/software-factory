import { create } from 'zustand';
import type { Card, ColumnId } from '@/types';
import { checkDragPolicy } from '@/board/policy';
import { sampleState } from '@/sampleData';
import { forgeApi, ForgeApiError } from '@/api/forgeApi';
import { useToastStore } from './useToastStore';

export type BoardFilter = 'all' | 'needs-approval' | 'ai-working' | 'blocked' | 'my-watch';

interface BoardState {
  cards: Card[];
  selectedId: string | null;
  draggingId: string | null;
  activeFilter: BoardFilter;
  /** Card id whose move is awaiting confirmation (the modal is open). */
  pendingMove: { cardId: string; toCol: ColumnId } | null;

  hydrate: (cards: Card[]) => void;
  setSelected: (id: string | null) => void;
  setDragging: (id: string | null) => void;
  setFilter: (f: BoardFilter) => void;
  /** Apply a card update from the SignalR hub. */
  upsertFromRealtime: (card: Card) => void;
  /**
   * Move a card. Returns:
   *   - { kind: 'moved' }       if the move applied (optimistic + server agreed)
   *   - { kind: 'confirm', ... } if the policy needs a confirmation modal
   *   - { kind: 'blocked', ... } if the policy/server rejected the move
   *   - { kind: 'noop' }         if from === to
   */
  move: (cardId: string, toCol: ColumnId, force?: boolean) => Promise<MoveOutcome>;
  /** Resolve the pending modal with confirm/cancel. */
  confirmPendingMove: () => Promise<void>;
  cancelPendingMove: () => void;
}

export type MoveOutcome =
  | { kind: 'moved' }
  | { kind: 'noop' }
  | { kind: 'confirm'; title: string; text: string }
  | { kind: 'blocked'; reason: string };

export const useBoardStore = create<BoardState>((set, get) => ({
  cards: sampleState.cards,
  selectedId: null,
  draggingId: null,
  activeFilter: 'all',
  pendingMove: null,

  hydrate: (cards) => set({ cards }),
  setSelected: (id) => set({ selectedId: id }),
  setDragging: (id) => set({ draggingId: id }),
  setFilter: (f) => set({ activeFilter: f }),

  upsertFromRealtime: (card) => {
    const cards = get().cards;
    const idx = cards.findIndex((c) => c.id === card.id);
    if (idx === -1) {
      set({ cards: [...cards, card] });
    } else {
      const next = cards.slice();
      next[idx] = card;
      set({ cards: next });
    }
  },

  move: async (cardId, toCol, force = false) => {
    const cards = get().cards;
    const idx = cards.findIndex((c) => c.id === cardId);
    if (idx === -1) return { kind: 'blocked', reason: `Card ${cardId} not found` };
    const card = cards[idx];
    if (card.col === toCol) return { kind: 'noop' };

    const policy = checkDragPolicy(card.col, toCol, card);
    if (!policy.allow) {
      useToastStore.getState().show('Move blocked by policy', 'danger');
      return { kind: 'blocked', reason: 'Policy disallows the move' };
    }
    if (policy.confirm && !force) {
      set({ pendingMove: { cardId, toCol } });
      return { kind: 'confirm', title: policy.title ?? 'Confirm move', text: policy.text ?? '' };
    }

    // Optimistic: snapshot, then patch the local store immediately.
    const snapshot = cards;
    const optimistic = cards.slice();
    optimistic[idx] = { ...card, col: toCol };
    set({ cards: optimistic });

    try {
      await forgeApi.moveCard(cardId, toCol, force);
      useToastStore.getState().show(policy.toast ?? `${cardId} → ${toCol}`, 'success');
      return { kind: 'moved' };
    } catch (err) {
      // A real server response (4xx/5xx) is a hard reject — roll back.
      if (err instanceof ForgeApiError) {
        set({ cards: snapshot });
        useToastStore.getState().show(`Move rejected (${err.status})`, 'danger');
        return { kind: 'blocked', reason: `Move rejected (${err.status})` };
      }
      // Network failure (API offline) — keep the optimistic move so the board
      // stays usable in dev / sample-only mode. The same fallback the read
      // path uses when /api/state is unreachable.
      useToastStore.getState().show(`${policy.toast ?? `${cardId} → ${toCol}`} (offline)`, 'info');
      return { kind: 'moved' };
    }
  },

  confirmPendingMove: async () => {
    const pm = get().pendingMove;
    set({ pendingMove: null });
    if (!pm) return;
    await get().move(pm.cardId, pm.toCol, true);
  },

  cancelPendingMove: () => set({ pendingMove: null }),
}));

/**
 * Pure helpers (called inside `useMemo` from components). They MUST NOT be
 * passed directly to `useBoardStore(...)` — Zustand v5's `useSyncExternalStore`
 * runs the selector on every render and a new array/object reference would
 * trigger an infinite update loop.
 */
export function filterCards(cards: Card[], filter: BoardFilter): Card[] {
  switch (filter) {
    case 'needs-approval': return cards.filter((c) => c.humanGate);
    case 'ai-working':     return cards.filter((c) => c.live && c.agent && c.agent.role !== 'human');
    case 'blocked':        return cards.filter((c) => c.blocked);
    case 'my-watch': {
      const watch = new Set(['#234', '#812', '#241', 'PR #240', 'PR #229']);
      return cards.filter((c) => watch.has(c.id));
    }
    default: return cards;
  }
}

export function computeBurndown(cards: Card[]): { done: number; total: number; pct: number } {
  let done = 0;
  let total = 0;
  for (const c of cards) {
    total += c.pts;
    if (c.col === 'done') done += c.pts;
  }
  const pct = total === 0 ? 0 : Math.round((done / total) * 100);
  return { done, total, pct };
}
