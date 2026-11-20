import { describe, expect, it } from 'vitest';
import { dismiss, isDismissed } from './attentionDismissals';

const blocked = { id: 'ws-a', status: 'blocked' };
const other = { id: 'ws-b', status: 'blocked' };

describe('attention dismissals', () => {
  it('hides a dismissed item while its situation is unchanged', () => {
    const d = dismiss([blocked], [blocked, other], {});
    expect(isDismissed(blocked, d)).toBe(true);
    expect(isDismissed(other, d)).toBe(false);
  });

  it('shows the item again when it changes (blocked → stalled, or a new stall reason)', () => {
    const d = dismiss([blocked], [blocked], {});
    expect(isDismissed({ ...blocked, status: 'stalled', stallReason: 'gate failed' }, d)).toBe(false);
    const stalled = { id: 'ws-a', status: 'stalled', stallReason: 'gate failed' };
    const d2 = dismiss([stalled], [stalled], {});
    expect(isDismissed({ ...stalled, stallReason: 'merge conflict' }, d2)).toBe(false);
  });

  it('clear-all dismisses every item and drops entries that no longer need attention', () => {
    const d = dismiss([blocked, other], [blocked, other], { 'ws-gone': 'blocked|' });
    expect(Object.keys(d).sort()).toEqual(['ws-a', 'ws-b']);
  });
});
