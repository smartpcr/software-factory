import { describe, expect, it } from 'vitest';
import { useConfirmStore } from './useConfirmStore';

// askText replaces window.prompt (unthemed native dialog) with the app's
// confirm dialog plus a text field.
describe('useConfirmStore', () => {
  it('askText resolves to the entered text on confirm', async () => {
    const answer = useConfirmStore.getState().askText({ title: 'Pin', input: { rows: 5 } });
    expect(useConfirmStore.getState().pending?.input?.rows).toBe(5);
    useConfirmStore.getState().confirm('stop flipping');
    await expect(answer).resolves.toBe('stop flipping');
    expect(useConfirmStore.getState().pending).toBeNull();
  });

  it('askText resolves to null on cancel', async () => {
    const answer = useConfirmStore.getState().askText({ title: 'Pin' });
    useConfirmStore.getState().cancel();
    await expect(answer).resolves.toBeNull();
  });

  it('ask still resolves to a boolean and shows no text field', async () => {
    const answer = useConfirmStore.getState().ask({ title: 'Restart?' });
    expect(useConfirmStore.getState().pending?.input).toBeUndefined();
    useConfirmStore.getState().confirm();
    await expect(answer).resolves.toBe(true);
  });
});