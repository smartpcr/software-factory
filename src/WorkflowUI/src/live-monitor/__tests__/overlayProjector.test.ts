import { describe, it, expect } from 'vitest';
import { projectNodeState, projectEdgeState } from '../store/overlayProjector';
import type { RunEvent, NodeOverlayState, EdgeOverlayState } from '../../telemetry/types';

function makeEvent(
  kind: RunEvent['kind'],
  nodeId: string,
  payload?: Record<string, unknown>,
): RunEvent {
  return {
    runId: 'run-1',
    seq: 1,
    kind,
    synthetic: kind === 'EdgeActivated' || kind === 'NodeSkipped',
    timestampUtc: new Date().toISOString(),
    nodeId,
    payload,
  };
}

describe('overlayProjector', () => {
  describe('projectNodeState', () => {
    it('NodeStarted sets running', () => {
      const state = new Map<string, NodeOverlayState>();
      const next = projectNodeState(state, makeEvent('NodeStarted', 'n1'));
      expect(next.get('n1')?.status).toBe('running');
    });

    it('NodeCompleted sets completed', () => {
      const state = new Map<string, NodeOverlayState>();
      const next = projectNodeState(state, makeEvent('NodeCompleted', 'n1'));
      expect(next.get('n1')?.status).toBe('completed');
    });

    it('NodeFailed sets failed', () => {
      const state = new Map<string, NodeOverlayState>();
      const next = projectNodeState(state, makeEvent('NodeFailed', 'n1'));
      expect(next.get('n1')?.status).toBe('failed');
    });

    it('NodeCancelled sets cancelled', () => {
      const state = new Map<string, NodeOverlayState>();
      const next = projectNodeState(state, makeEvent('NodeCancelled', 'n1'));
      expect(next.get('n1')?.status).toBe('cancelled');
    });

    it('NodeSkipped (synthetic) sets skipped', () => {
      const state = new Map<string, NodeOverlayState>();
      const next = projectNodeState(state, makeEvent('NodeSkipped', 'n1'));
      expect(next.get('n1')?.status).toBe('skipped');
    });

    it('NodeProgress sets running with progress', () => {
      const state = new Map<string, NodeOverlayState>();
      const next = projectNodeState(
        state,
        makeEvent('NodeProgress', 'n1', { progressPercent: 50, status: 'loading' }),
      );
      expect(next.get('n1')?.status).toBe('running');
      expect(next.get('n1')?.progressPercent).toBe(50);
      expect(next.get('n1')?.progressStatus).toBe('loading');
    });

    it('late NodeStarted for already-completed node is ignored (monotonic FSM)', () => {
      let state = new Map<string, NodeOverlayState>();
      state = projectNodeState(state, makeEvent('NodeCompleted', 'n1'));
      expect(state.get('n1')?.status).toBe('completed');

      // Late NodeStarted should NOT revert to running
      state = projectNodeState(state, makeEvent('NodeStarted', 'n1'));
      expect(state.get('n1')?.status).toBe('completed');
    });

    it('WorkflowStarted does not modify node state', () => {
      const state = new Map<string, NodeOverlayState>();
      const next = projectNodeState(state, makeEvent('WorkflowStarted', 'n1'));
      expect(next.has('n1')).toBe(false);
    });

    it('idempotent on replay', () => {
      let state = new Map<string, NodeOverlayState>();
      const event = makeEvent('NodeCompleted', 'n1');
      state = projectNodeState(state, event);
      const first = state.get('n1');
      state = projectNodeState(state, event);
      expect(state.get('n1')).toEqual(first);
    });
  });

  describe('projectEdgeState', () => {
    it('EdgeActivated sets edge to activated', () => {
      const state = new Map<string, EdgeOverlayState>();
      const event = makeEvent('EdgeActivated', 'n1', {
        sourceNodeId: 'n1',
        targetNodeId: 'n2',
        triggerMessageType: 'Complete',
      });
      const next = projectEdgeState(state, event);
      const edge = next.get('n1->n2');
      expect(edge?.status).toBe('activated');
      expect(edge?.triggerMessageType).toBe('Complete');
    });

    it('non-EdgeActivated events do not modify edge state', () => {
      const state = new Map<string, EdgeOverlayState>();
      const next = projectEdgeState(state, makeEvent('NodeCompleted', 'n1'));
      expect(next.size).toBe(0);
    });
  });
});
