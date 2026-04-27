// -----------------------------------------------------------------------
// Global application state
// -----------------------------------------------------------------------

import type { FlowNode, FlowEdge, Selection, CanvasState, LogEntry, WorkflowData } from './types';

export const State = {
  workflows: {} as Record<string, WorkflowData>,
  /** Lightweight index of all server workflows: workflowId → workflowName */
  workflowIndex: {} as Record<string, string>,
  currentWfId: null as string | null,
  openTabs: [] as string[],
  nodes: new Map<string, FlowNode>(),
  edges: new Map<string, FlowEdge>(),
  selection: { kind: null, id: null } as Selection,
  canvas: { x: 200, y: 120, zoom: 1 } as CanvasState,
  isDirty: false,
  /** Per-workflow dirty tracking */
  dirtyWorkflows: new Set<string>(),
  isLocked: false,
  lastSaved: null as number | null,
  logs: [] as LogEntry[],
  activeRun: null as { runId: string; status: string; startTime?: string; endTime?: string; duration?: string; globalVariables?: Record<string, unknown> } | null,
};
