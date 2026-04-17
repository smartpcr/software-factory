/**
 * Projects RunEvents into node/edge overlay states for the canvas.
 * Implements a monotonic FSM: once a node reaches a terminal state
 * (completed/failed/cancelled/skipped), it cannot go backwards.
 */
import type {
  RunEvent,
  NodeStatus,
  NodeOverlayState,
  EdgeOverlayState,
} from '../../telemetry/types';

const TERMINAL_STATES: Set<NodeStatus> = new Set([
  'completed',
  'failed',
  'cancelled',
  'skipped',
]);

/**
 * Given the current node states, applies a RunEvent and returns updated state.
 * Idempotent on replay — applying the same event twice produces the same result.
 */
export function projectNodeState(
  current: Map<string, NodeOverlayState>,
  event: RunEvent,
): Map<string, NodeOverlayState> {
  const next = new Map(current);

  if (!event.nodeId) return next;

  const existing = next.get(event.nodeId);

  // Monotonic FSM guard: don't go backwards from terminal states
  if (existing && TERMINAL_STATES.has(existing.status)) {
    // Allow progress updates on running nodes but not on terminal
    return next;
  }

  switch (event.kind) {
    case 'NodeStarted':
      next.set(event.nodeId, {
        nodeId: event.nodeId,
        status: 'running',
      });
      break;

    case 'NodeCompleted':
      next.set(event.nodeId, {
        nodeId: event.nodeId,
        status: 'completed',
      });
      break;

    case 'NodeFailed':
      next.set(event.nodeId, {
        nodeId: event.nodeId,
        status: 'failed',
      });
      break;

    case 'NodeCancelled':
      next.set(event.nodeId, {
        nodeId: event.nodeId,
        status: 'cancelled',
      });
      break;

    case 'NodeSkipped':
      next.set(event.nodeId, {
        nodeId: event.nodeId,
        status: 'skipped',
      });
      break;

    case 'NodeProgress': {
      const payload = event.payload;
      next.set(event.nodeId, {
        nodeId: event.nodeId,
        status: 'running',
        progressPercent:
          typeof payload?.progressPercent === 'number'
            ? payload.progressPercent
            : undefined,
        progressStatus:
          typeof payload?.status === 'string' ? payload.status : undefined,
      });
      break;
    }
  }

  return next;
}

/**
 * Projects an EdgeActivated event into edge overlay states.
 */
export function projectEdgeState(
  current: Map<string, EdgeOverlayState>,
  event: RunEvent,
): Map<string, EdgeOverlayState> {
  const next = new Map(current);

  if (event.kind !== 'EdgeActivated' || !event.payload) return next;

  const sourceNodeId = String(event.payload.sourceNodeId ?? '');
  const targetNodeId = String(event.payload.targetNodeId ?? '');
  const triggerMessageType = String(
    event.payload.triggerMessageType ?? 'Complete',
  );
  const key = `${sourceNodeId}->${targetNodeId}`;

  next.set(key, {
    sourceNodeId,
    targetNodeId,
    status: 'activated',
    triggerMessageType,
  });

  return next;
}
