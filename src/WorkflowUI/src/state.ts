// -----------------------------------------------------------------------
// Global application state
// -----------------------------------------------------------------------

import type { FlowNode, FlowEdge, Selection, CanvasState, LogEntry, WorkflowData } from './types';

export const State = {
  workflows: {} as Record<string, WorkflowData>,
  currentWfId: null as string | null,
  nodes: new Map<string, FlowNode>(),
  edges: new Map<string, FlowEdge>(),
  selection: { kind: null, id: null } as Selection,
  canvas: { x: 200, y: 120, zoom: 1 } as CanvasState,
  isDirty: false,
  lastSaved: null as number | null,
  logs: [] as LogEntry[],
};
