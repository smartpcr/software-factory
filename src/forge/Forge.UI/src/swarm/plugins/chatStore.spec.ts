import { describe, expect, it, vi } from 'vitest';
import { chatApi, type ChatSummaryItem } from './chatApi';
import { getChatSummary, mergeChatSummaries, refreshChatSummaries, reloadChatSummaries } from './chatStore';

vi.mock('./chatApi', () => ({ chatApi: { summaries: vi.fn() } }));
const item = (revision: number, state = 'off'): ChatSummaryItem => ({ spawnId: 'shared', title: 'Same ID',
  summary: { revision, state, destinations: [], unroutedCount: 0, unroutedReasons: {}, pendingCount: 0, failedCount: 0, sentCount: 0 } });

describe('chat summary cache', () => {
  it('keeps product ownership and rejects older saved revisions', () => {
    mergeChatSummaries('product-a', [item(2, 'ready')]); mergeChatSummaries('product-b', [item(0)]);
    mergeChatSummaries('product-a', [item(1)]);
    expect(getChatSummary('product-a', 'shared')?.state).toBe('ready');
    expect(getChatSummary('product-b', 'shared')?.state).toBe('off');
  });
  it('coalesces row readers into one product request', async () => {
    let resolve!: (rows: ChatSummaryItem[]) => void;
    vi.mocked(chatApi.summaries).mockImplementationOnce(() => new Promise(r => { resolve = r; }));
    const start = vi.mocked(chatApi.summaries).mock.calls.length;
    const requests = Array.from({ length: 32 }, () => refreshChatSummaries('readers'));
    expect(vi.mocked(chatApi.summaries).mock.calls.length - start).toBe(1);
    resolve([item(1, 'ready')]); await Promise.all(requests); expect(getChatSummary('readers', 'shared')?.state).toBe('ready');
  });
  it('waits for an old poll and fetches saved state again after a mutation', async () => {
    let resolve!: (rows: ChatSummaryItem[]) => void;
    vi.mocked(chatApi.summaries).mockImplementationOnce(() => new Promise(r => { resolve = r; })).mockResolvedValueOnce([item(2, 'ready')]);
    const old = refreshChatSummaries('mutation'); const saved = reloadChatSummaries('mutation');
    resolve([item(1)]); await old; await saved; expect(getChatSummary('mutation', 'shared')?.revision).toBe(2);
  });
});