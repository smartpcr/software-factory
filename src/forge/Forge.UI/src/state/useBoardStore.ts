import { create } from 'zustand';
import type { Card, ColumnId } from '@/types';
import { checkDragPolicy } from '@/board/policy';
import { sampleState } from '@/sampleData';
import { forgeApi, ForgeApiError } from '@/api/forgeApi';
import { logAction } from '@/telemetry';
import { useToastStore } from './useToastStore';

export type BoardFilter = 'all' | 'needs-approval' | 'ai-working' | 'blocked' | 'my-watch';

export type BoardGroupBy = 'epic' | 'story' | 'phase' | 'agent' | 'priority';

interface BoardState {
  cards: Card[];
  selectedId: string | null;
  /** When set, the right-rail renders the story detail instead of card / WI detail. Mutually exclusive with selectedId. */
  selectedStoryId: string | null;
  draggingId: string | null;
  activeFilter: BoardFilter;
  /** Phase-1 select dropdowns (epic / agent / priority). null = "all". */
  epicFilter: string | null;
  agentFilter: string | null;
  priorityFilter: string | null;
  /** Multi-select story filter. Empty = show all. */
  storyFilter: string[];
  /** Phase-1 search input (substring match against title/branch/id). */
  searchTerm: string;
  /** Group by — currently visual-only (board still groups by epic). */
  groupBy: BoardGroupBy;
  /** Card id whose move is awaiting confirmation (the modal is open). */
  pendingMove: { cardId: string; toCol: ColumnId } | null;

  hydrate: (cards: Card[]) => void;
  setSelected: (id: string | null) => void;
  setSelectedStory: (id: string | null) => void;
  setDragging: (id: string | null) => void;
  setFilter: (f: BoardFilter) => void;
  setEpicFilter: (e: string | null) => void;
  setAgentFilter: (a: string | null) => void;
  setPriorityFilter: (p: string | null) => void;
  setStoryFilter: (s: string[]) => void;
  setSearchTerm: (s: string) => void;
  setGroupBy: (g: BoardGroupBy) => void;
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
  selectedStoryId: null,
  draggingId: null,
  activeFilter: 'all',
  epicFilter: null,
  agentFilter: null,
  priorityFilter: null,
  storyFilter: [],
  searchTerm: '',
  groupBy: 'story',
  pendingMove: null,

  hydrate: (cards) => set({ cards }),
  setSelected: (id) => set({ selectedId: id, selectedStoryId: id ? null : get().selectedStoryId }),
  setSelectedStory: (id) => set({ selectedStoryId: id, selectedId: id ? null : get().selectedId }),
  setDragging: (id) => set({ draggingId: id }),
  setFilter: (f) => set({ activeFilter: f }),
  setEpicFilter: (e) => set({ epicFilter: e }),
  setAgentFilter: (a) => set({ agentFilter: a }),
  setPriorityFilter: (p) => set({ priorityFilter: p }),
  setStoryFilter: (s) => set({ storyFilter: s }),
  setSearchTerm: (s) => set({ searchTerm: s }),
  setGroupBy: (g) => set({ groupBy: g }),

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
      logAction('forge.action.card.move', { cardId, fromCol: card.col, toCol });
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
/**
 * Pure card filter. The chip filter (`activeFilter`) narrows by lifecycle
 * state; the select-dropdown filters narrow by epic/agent/priority; the
 * search term substring-matches across id / title / branch. All filters
 * AND together — a card must satisfy every one to be visible.
 */
export interface BoardFilterState {
  activeFilter: BoardFilter;
  epicFilter: string | null;
  agentFilter: string | null;
  priorityFilter: string | null;
  storyFilter?: string[];
  searchTerm: string;
}

export function filterCards(cards: Card[], filter: BoardFilter | BoardFilterState): Card[] {
  const f: BoardFilterState = typeof filter === 'string'
    ? { activeFilter: filter, epicFilter: null, agentFilter: null, priorityFilter: null, storyFilter: [], searchTerm: '' }
    : filter;

  let out = cards;

  switch (f.activeFilter) {
    case 'needs-approval': out = out.filter((c) => c.humanGate); break;
    case 'ai-working':     out = out.filter((c) => c.live && c.agent && c.agent.role !== 'human'); break;
    case 'blocked':        out = out.filter((c) => c.blocked); break;
    case 'my-watch': {
      const watch = new Set(['#234', '#812', '#241', 'PR #240', 'PR #229']);
      out = out.filter((c) => watch.has(c.id));
      break;
    }
    default: break;
  }

  if (f.epicFilter) out = out.filter((c) => c.epic === f.epicFilter);
  if (f.agentFilter) out = out.filter((c) => c.agent?.id === f.agentFilter);
  if (f.priorityFilter) out = out.filter((c) => c.priority === f.priorityFilter);
  if (f.storyFilter && f.storyFilter.length > 0) {
    const set = new Set(f.storyFilter);
    out = out.filter((c) => set.has(c.epic));
  }

  const q = f.searchTerm.trim().toLowerCase();
  if (q) {
    out = out.filter((c) =>
      c.id.toLowerCase().includes(q)
      || c.title.toLowerCase().includes(q)
      || (c.branch ?? '').toLowerCase().includes(q),
    );
  }

  return out;
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
