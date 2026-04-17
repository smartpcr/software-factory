import type { NodeExecutionStatus } from '../../engine-contract';

export interface ExecutionOverlayState {
  statuses: Map<string, NodeExecutionStatus>;
  isLive: boolean;
}

export function createEmptyOverlay(): ExecutionOverlayState {
  return { statuses: new Map(), isLive: false };
}

export function mergeExecutionStatuses(
  current: Map<string, NodeExecutionStatus>,
  updates: Record<string, NodeExecutionStatus>,
): Map<string, NodeExecutionStatus> {
  const merged = new Map(current);
  for (const [nodeId, status] of Object.entries(updates)) {
    merged.set(nodeId, status);
  }
  return merged;
}