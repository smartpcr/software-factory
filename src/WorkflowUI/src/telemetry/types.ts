/**
 * Transport-neutral telemetry types mirroring the C# RunEvent DTO.
 */

export type RunEventKind =
  | 'WorkflowStarted'
  | 'WorkflowCompleted'
  | 'WorkflowFailed'
  | 'WorkflowCancelled'
  | 'NodeStarted'
  | 'NodeCompleted'
  | 'NodeFailed'
  | 'NodeCancelled'
  | 'NodeProgress'
  | 'EdgeActivated'
  | 'NodeSkipped';

export interface RunEvent {
  runId: string;
  seq: number;
  kind: RunEventKind;
  synthetic: boolean;
  timestampUtc: string;
  nodeId?: string;
  nodeName?: string;
  payload?: Record<string, unknown>;
}

export type NodeStatus =
  | 'pending'
  | 'running'
  | 'completed'
  | 'failed'
  | 'cancelled'
  | 'skipped';

export type EdgeStatus = 'idle' | 'activated';

export interface NodeOverlayState {
  nodeId: string;
  status: NodeStatus;
  progressPercent?: number;
  progressStatus?: string;
}

export interface EdgeOverlayState {
  sourceNodeId: string;
  targetNodeId: string;
  status: EdgeStatus;
  triggerMessageType?: string;
}

export interface ReplayResponse {
  notFound: boolean;
  truncated: boolean;
  oldestAvailableSeq: number;
  events: RunEvent[];
}
