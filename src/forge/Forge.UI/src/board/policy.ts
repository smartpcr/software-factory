// Verbatim port of `checkDragPolicy` from docs/examples/forge-board.html.
// Mirrored byte-for-byte in Forge.Api/Domain/DragPolicy.cs — keep them in
// sync; `Forge.Api.Tests/DragPolicyParityTests` runs the same scenarios on
// both implementations to prove they agree.

import type { Card, ColumnId } from '@/types';

export const colOrder: ColumnId[] = ['backlog', 'design', 'build', 'review', 'done'];

export interface DragPolicyResult {
  allow: boolean;
  /** Toast message that should fire after a move that does not require confirm. */
  toast?: string;
  /** Confirmation modal title. */
  title?: string;
  /** Confirmation modal body (may include `<strong>` markup). */
  text?: string;
  /** Soft warning that surfaces while hovering the drop target. */
  warn?: string;
  /** When true, callers must show a confirmation modal before performing the move. */
  confirm?: boolean;
}

export function checkDragPolicy(fromCol: ColumnId, toCol: ColumnId, card: Card): DragPolicyResult {
  if (fromCol === toCol) return { allow: true };
  const fromIdx = colOrder.indexOf(fromCol);
  const toIdx = colOrder.indexOf(toCol);
  const distance = toIdx - fromIdx;

  if (distance < 0) {
    if (fromCol === 'done') {
      return {
        allow: true,
        warn: 'Re-open shipped item?',
        toast: 'Item re-opened — agents will re-investigate',
      };
    }
    return { allow: true, toast: 'Card moved back · agent reassigned' };
  }

  if (distance > 1) {
    if (toCol === 'done' && fromCol !== 'review') {
      return {
        allow: true,
        confirm: true,
        title: 'Bypass review?',
        text: `You're marking <strong>${card.id}</strong> as done without going through review. Code will not be reviewed by an agent and CI gates are skipped.`,
      };
    }
    const skipped = distance - 1;
    return {
      allow: true,
      confirm: true,
      title: 'Skip phase?',
      text: `You're moving <strong>${card.id}</strong> directly from <strong>${fromCol}</strong> to <strong>${toCol}</strong>. This bypasses the ${skipped} intermediate phase${skipped > 1 ? 's' : ''}.`,
    };
  }

  if (toCol === 'done' && card.humanGate) {
    return {
      allow: true,
      confirm: true,
      title: 'Approve human-gated change?',
      text: `<strong>${card.id}</strong> has been escalated for your review because: ${card.gateReason}. Approving will merge to main.`,
    };
  }

  return { allow: true, toast: `Moved to ${toCol}` };
}
