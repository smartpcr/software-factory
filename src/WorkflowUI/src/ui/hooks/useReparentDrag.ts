import { useCallback } from 'react';

export interface ReparentTarget {
  parentId: string;
  slot: string;
}

/**
 * Finds a branch-port or container-body slot element at the given screen coordinates.
 * Returns the parentId and slot name if found, or null if no valid target.
 */
function findSlotAtPoint(
  clientX: number,
  clientY: number,
  excludeNodeId: string,
  isDescendantOf: (maybeDesc: string, ancestor: string) => boolean,
): ReparentTarget | null {
  const target = document.elementFromPoint(clientX, clientY);
  if (!target) return null;

  // Check for branch port
  const branchEl = target.closest<HTMLElement>('.branch-port[data-parent-id]');
  if (branchEl) {
    const parentEl = branchEl.closest<HTMLElement>('.node-chrome');
    const parentId = parentEl?.dataset.testid?.replace('node-chrome-', '');
    if (parentId && parentId !== excludeNodeId && !isDescendantOf(parentId, excludeNodeId)) {
      return { parentId, slot: branchEl.dataset.branch ?? '' };
    }
  }

  // Check for container body
  const bodyEl = target.closest<HTMLElement>('.container-body');
  if (bodyEl) {
    const parentEl = bodyEl.closest<HTMLElement>('.node-chrome.container');
    const parentId = parentEl?.dataset.testid?.replace('node-chrome-', '');
    if (parentId && parentId !== excludeNodeId && !isDescendantOf(parentId, excludeNodeId)) {
      return { parentId, slot: '_body' };
    }
  }

  return null;
}

/**
 * Hook providing utilities for re-parenting nodes via drag.
 * Returns a function that checks if a screen point is over a valid slot.
 */
export function useReparentDrag() {
  const findSlot = useCallback(
    (
      clientX: number,
      clientY: number,
      excludeNodeId: string,
      isDescendantOf: (maybeDesc: string, ancestor: string) => boolean,
    ): ReparentTarget | null => {
      return findSlotAtPoint(clientX, clientY, excludeNodeId, isDescendantOf);
    },
    [],
  );

  return { findSlot };
}
