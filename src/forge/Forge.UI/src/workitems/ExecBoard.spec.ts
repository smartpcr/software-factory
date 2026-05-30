// Drag-to-reconcile contract for the Exec board (Phase 25 + 2026-05-30
// drag-move). Covers the pure helpers and the dragEnd-side reconcile call
// — not the full DnD wiring (which would need a DOM + dnd-kit simulator).

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { __testing } from './ExecBoard';
import { ForgeApiError, forgeApi } from '@/api/forgeApi';
import { useToastStore } from '@/state/useToastStore';
import type { WorkItem } from './workItemTypes';

const { parseDropId, dropId, dragId, reconcileExecutionStatusFromDrag } = __testing;

function makeItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: 'wi-test',
    repoId: null,
    provider: 'local',
    providerRef: null,
    track: 'dev',
    type: 'task',
    state: 'active',
    title: 'Test work item',
    descriptionMd: '',
    parentId: null,
    iterationRef: '',
    areaPath: '',
    assignedTo: null,
    points: 0,
    priority: 'med',
    tags: [],
    createdAt: '2026-05-30T00:00:00Z',
    updatedAt: '2026-05-30T00:00:00Z',
    executionStatus: 'queued',
    ...overrides,
  };
}

describe('ExecBoard.parseDropId', () => {
  it('parses a simple group key', () => {
    expect(parseDropId(dropId('story-A', 'queued'))).toEqual({
      groupKey: 'story-A',
      status: 'queued',
    });
  });

  it('handles a group key that itself contains the separator (e.g. namespaced story id)', () => {
    const id = dropId('local:UPDATE-HISTORY', 'completed');
    expect(parseDropId(id)).toEqual({
      groupKey: 'local:UPDATE-HISTORY',
      status: 'completed',
    });
  });

  it('round-trips every valid exec status', () => {
    for (const s of ['idle', 'queued', 'live', 'completed', 'failed', 'cancelled'] as const) {
      expect(parseDropId(dropId('g', s))?.status).toBe(s);
    }
  });

  it('returns null for card ids (drop ids only)', () => {
    expect(parseDropId(dragId('g', 'wi-1'))).toBeNull();
  });

  it('returns null for an unknown status', () => {
    expect(parseDropId('col::g::cooldown-budget')).toBeNull();
    expect(parseDropId('col::g::definitely-not-a-status')).toBeNull();
  });

  it('returns null for malformed ids', () => {
    expect(parseDropId('col::g')).toBeNull();
    expect(parseDropId('something-else')).toBeNull();
    expect(parseDropId('')).toBeNull();
  });
});

describe('ExecBoard.reconcileExecutionStatusFromDrag', () => {
  let toastSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    toastSpy = vi.fn();
    useToastStore.setState({ toasts: [], show: toastSpy, dismiss: vi.fn() });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('shows a success toast when the server reconciles to the target', async () => {
    const apiSpy = vi.spyOn(forgeApi, 'reconcileExecutionStatus').mockResolvedValue({
      id: 'wi-x',
      previous: 'queued',
      current: 'completed',
      reason: 'Linked workstream is complete; deliverable passed.',
    });
    await reconcileExecutionStatusFromDrag(makeItem({ id: 'wi-x' }), 'completed');
    expect(apiSpy).toHaveBeenCalledWith('wi-x', 'completed');
    expect(toastSpy).toHaveBeenCalledTimes(1);
    const [msg, kind] = toastSpy.mock.calls[0];
    expect(msg).toContain('wi-x');
    expect(msg).toContain('queued');
    expect(msg).toContain('completed');
    expect(kind).toBe('success');
  });

  it('shows an already-X toast when previous === current (no-op write)', async () => {
    vi.spyOn(forgeApi, 'reconcileExecutionStatus').mockResolvedValue({
      id: 'wi-noop',
      previous: 'idle',
      current: 'idle',
      reason: 'No active runner, queue row, or terminal workstream signal.',
    });
    await reconcileExecutionStatusFromDrag(makeItem({ id: 'wi-noop' }), 'idle');
    expect(toastSpy).toHaveBeenCalledTimes(1);
    const [msg, kind] = toastSpy.mock.calls[0];
    expect(msg).toContain('already idle');
    expect(kind).toBe('success');
  });

  it('shows a mismatch error toast on 409 with the authoritative status and reason', async () => {
    vi.spyOn(forgeApi, 'reconcileExecutionStatus').mockRejectedValue(
      new ForgeApiError(
        409,
        {
          error: 'execution_status_mismatch',
          requested: 'failed',
          authoritative: 'completed',
          reason: 'Linked workstream is complete; deliverable passed.',
          current: 'queued',
        },
        '/api/work-items/wi-misread/execution-status: 409 mismatch',
      ),
    );
    await reconcileExecutionStatusFromDrag(makeItem({ id: 'wi-misread' }), 'failed');
    expect(toastSpy).toHaveBeenCalledTimes(1);
    const [msg, kind] = toastSpy.mock.calls[0];
    expect(msg).toContain('wi-misread');
    expect(msg).toContain('failed');         // requested
    expect(msg).toContain('completed');      // authoritative
    expect(msg).toContain('Linked workstream is complete');
    expect(kind).toBe('danger');
  });

  it('shows a generic error toast on non-API errors', async () => {
    vi.spyOn(forgeApi, 'reconcileExecutionStatus').mockRejectedValue(
      new Error('network down'),
    );
    await reconcileExecutionStatusFromDrag(makeItem(), 'queued');
    expect(toastSpy).toHaveBeenCalledTimes(1);
    const [msg, kind] = toastSpy.mock.calls[0];
    expect(msg).toContain('network down');
    expect(kind).toBe('danger');
  });

  it('falls back gracefully when a 409 response has no problem body', async () => {
    vi.spyOn(forgeApi, 'reconcileExecutionStatus').mockRejectedValue(
      new ForgeApiError(409, null, '409 with no body'),
    );
    await reconcileExecutionStatusFromDrag(makeItem({ id: 'wi-empty' }), 'queued');
    const [msg, kind] = toastSpy.mock.calls[0];
    expect(msg).toContain('wi-empty');
    expect(msg).toContain('unknown');     // missing authoritative
    expect(kind).toBe('danger');
  });
});
