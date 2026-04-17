/**
 * Zustand store for live telemetry state.
 * Batches overlay updates to at most one paint per requestAnimationFrame.
 */
import { create } from 'zustand';
import type { RunEvent, NodeOverlayState, EdgeOverlayState } from '../../telemetry/types';
import { projectNodeState, projectEdgeState } from './overlayProjector';

export interface TelemetryStoreState {
  /** Currently attached run ID. */
  attachedRunId: string | null;
  /** All received events for the current run. */
  events: RunEvent[];
  /** Node visual states projected from events. */
  nodeStates: Map<string, NodeOverlayState>;
  /** Edge visual states projected from events. */
  edgeStates: Map<string, EdgeOverlayState>;
  /** Filter state for the event log. */
  filters: EventLogFilters;

  // Actions
  attachRun: (runId: string) => void;
  detachRun: () => void;
  addEvent: (event: RunEvent) => void;
  addEvents: (events: RunEvent[]) => void;
  setFilters: (filters: Partial<EventLogFilters>) => void;
  reset: () => void;
}

export interface EventLogFilters {
  kinds: Set<string>;
  nodeId: string | null;
  messageType: string | null;
}

const defaultFilters: EventLogFilters = {
  kinds: new Set(),
  nodeId: null,
  messageType: null,
};

export const useTelemetryStore = create<TelemetryStoreState>((set) => ({
  attachedRunId: null,
  events: [],
  nodeStates: new Map(),
  edgeStates: new Map(),
  filters: { ...defaultFilters, kinds: new Set() },

  attachRun: (runId: string) =>
    set({
      attachedRunId: runId,
      events: [],
      nodeStates: new Map(),
      edgeStates: new Map(),
    }),

  detachRun: () =>
    set({
      attachedRunId: null,
      events: [],
      nodeStates: new Map(),
      edgeStates: new Map(),
    }),

  addEvent: (event: RunEvent) =>
    set((state) => ({
      events: [...state.events, event],
      nodeStates: projectNodeState(state.nodeStates, event),
      edgeStates: projectEdgeState(state.edgeStates, event),
    })),

  addEvents: (events: RunEvent[]) =>
    set((state) => {
      let nodeStates = state.nodeStates;
      let edgeStates = state.edgeStates;
      for (const event of events) {
        nodeStates = projectNodeState(nodeStates, event);
        edgeStates = projectEdgeState(edgeStates, event);
      }
      return {
        events: [...state.events, ...events],
        nodeStates,
        edgeStates,
      };
    }),

  setFilters: (partial: Partial<EventLogFilters>) =>
    set((state) => ({
      filters: { ...state.filters, ...partial },
    })),

  reset: () =>
    set({
      attachedRunId: null,
      events: [],
      nodeStates: new Map(),
      edgeStates: new Map(),
      filters: { ...defaultFilters, kinds: new Set() },
    }),
}));

/**
 * Filters events based on the current filter state.
 */
export function filterEvents(
  events: RunEvent[],
  filters: EventLogFilters,
): RunEvent[] {
  return events.filter((event) => {
    // Kind filter: if any kinds selected, only show those
    if (filters.kinds.size > 0 && !filters.kinds.has(event.kind)) {
      return false;
    }

    // NodeId filter: exact match (engine-provided IDs)
    if (filters.nodeId && event.nodeId !== filters.nodeId) {
      return false;
    }

    // MessageType filter: applies only to EdgeActivated rows
    if (filters.messageType) {
      if (event.kind === 'EdgeActivated') {
        const msgType = String(event.payload?.triggerMessageType ?? '');
        if (msgType !== filters.messageType) {
          return false;
        }
      }
      // Non-edge rows always visible when only messageType filter is set
    }

    return true;
  });
}
