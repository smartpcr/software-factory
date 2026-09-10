import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const fetchMock = vi.fn<typeof fetch>();
beforeEach(() => { vi.resetModules(); vi.stubGlobal('fetch', fetchMock); fetchMock.mockReset(); });
afterEach(() => vi.unstubAllGlobals());
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });

describe('paired operator requests', () => {
  it('includes cookies on reads and never uses a writer header as identity', async () => {
    fetchMock.mockResolvedValueOnce(json([]));
    const { operatorFetch } = await import('./operatorSession');
    await operatorFetch('/api/products', { headers: { 'X-Forge-User': 'forged' } });
    const [, options] = fetchMock.mock.calls[0];
    expect(options?.credentials).toBe('include');
    expect(new Headers(options?.headers).has('X-Forge-User')).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('shares CSRF acquisition across concurrent writes and protects Request objects', async () => {
    fetchMock.mockImplementation(async (url) => String(url).endsWith('/csrf') ? json({ token: 'csrf-one' }) : json({}));
    const { operatorFetch } = await import('./operatorSession');
    await Promise.all([
      operatorFetch('/api/workstreams/a/cancel', { method: 'POST' }),
      operatorFetch(new Request('http://localhost:5180/api/me/avatar', { method: 'PUT', headers: { 'X-Forge-User': 'forged' } })),
    ]);
    expect(fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/csrf'))).toHaveLength(1);
    for (const [, options] of fetchMock.mock.calls.slice(1)) {
      expect(new Headers(options?.headers).get('X-Forge-CSRF')).toBe('csrf-one');
      expect(new Headers(options?.headers).has('X-Forge-User')).toBe(false);
      expect(options?.credentials).toBe('include');
    }
  });

  it('sends pairing secrets only in the body and replaces the anonymous CSRF token', async () => {
    fetchMock.mockResolvedValueOnce(json({ token: 'anonymous' })).mockResolvedValueOnce(json({ token: 'paired' })).mockResolvedValueOnce(json({}));
    const { pairOperator, operatorFetch } = await import('./operatorSession');
    await pairOperator('SECRET');
    await operatorFetch('/api/workstreams/a/cancel', { method: 'POST' });
    expect(String(fetchMock.mock.calls[1][0])).not.toContain('SECRET');
    expect(JSON.parse(fetchMock.mock.calls[1][1]?.body as string)).toEqual({ challenge: 'SECRET' });
    expect(new Headers(fetchMock.mock.calls[2][1]?.headers).get('X-Forge-CSRF')).toBe('paired');
  });

  it('clears cached protection after session loss without replaying a mutation', async () => {
    fetchMock.mockResolvedValueOnce(json({ token: 'old' })).mockResolvedValueOnce(json({}, 401))
      .mockResolvedValueOnce(json({ token: 'new' })).mockResolvedValueOnce(json({}));
    const { operatorFetch } = await import('./operatorSession');
    expect((await operatorFetch('/api/action', { method: 'POST' })).status).toBe(401);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    await operatorFetch('/api/next', { method: 'POST' });
    expect(new Headers(fetchMock.mock.calls[3][1]?.headers).get('X-Forge-CSRF')).toBe('new');
  });

  it('does not send a mutation when CSRF acquisition fails', async () => {
    fetchMock.mockResolvedValueOnce(json({}, 403));
    const { operatorFetch } = await import('./operatorSession');
    await expect(operatorFetch('/api/action', { method: 'POST' })).rejects.toThrow('request protection');
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
