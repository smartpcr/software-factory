import { create } from 'zustand';

/**
 * Phase 13f — global "edit this agent" popup.
 *
 * The agent edit modal used to live inside <CrewsView/> and only opened
 * from the member catalog. It now lives at the AppShell level so any
 * agent avatar / id / name across the app (Roster, Pair tab, Overview
 * crew rail, Plan-view card detail panel, work-item panel) can call
 * `openAgent(id)` to surface it.
 *
 * The store is intentionally minimal — `agentId` is the only piece of
 * state the popup needs; the modal fetches its own data on open
 * (crews / models / agent detail / soul.md) so callers don't have to
 * thread reference data through every click handler.
 *
 * Pattern outside React: `useAgentEditorStore.getState().openAgent('E4')`.
 * Inside components: `const open = useAgentEditorStore((s) => s.openAgent);`.
 */
interface AgentEditorState {
  agentId: string | null;
  openAgent: (id: string) => void;
  close: () => void;
}

export const useAgentEditorStore = create<AgentEditorState>((set) => ({
  agentId: null,
  openAgent: (id) => set({ agentId: id }),
  close: () => set({ agentId: null }),
}));
