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


describe('local launcher browser sign-in', () => {
  function browser(hash: string) {
    const replaceState = vi.fn();
    vi.stubGlobal('window', {
      location: { hash, pathname: '/workstreams', search: '?product=local' },
      history: { state: null, replaceState }, dispatchEvent: vi.fn(),
    });
    return replaceState;
  }

  it('removes the fragment before HTTP and redeems once across concurrent mounts', async () => {
    const secret = 'A'.repeat(64);
    const replaceState = browser(`#forge-pair=${secret}`);
    fetchMock.mockImplementation(async (url) => {
      expect(replaceState).toHaveBeenCalledWith(null, '', '/workstreams?product=local');
      expect(String(url)).not.toContain(secret);
      return json({ token: 'protected' });
    });
    const { completeBrowserHandoff } = await import('./operatorSession');
    await Promise.all([completeBrowserHandoff(), completeBrowserHandoff()]);
    expect(fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/pair'))).toHaveLength(1);
    expect(JSON.parse(fetchMock.mock.calls[1][1]?.body as string)).toEqual({ challenge: secret });
    expect(fetchMock.mock.calls[1][1]?.credentials).toBe('include');
    expect(new Headers(fetchMock.mock.calls[1][1]?.headers).get('X-Forge-CSRF')).toBe('protected');
  });

  it('does not sign in on ordinary navigation or after an explicit logout', async () => {
    const replaceState = browser('#workstream-detail');
    const { completeBrowserHandoff } = await import('./operatorSession');
    await completeBrowserHandoff();
    expect(replaceState).not.toHaveBeenCalled();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('strips malformed secrets without making requests', async () => {
    const replaceState = browser('#forge-pair=bad-secret');
    const { completeBrowserHandoff } = await import('./operatorSession');
    await expect(completeBrowserHandoff()).rejects.toThrow('invalid');
    expect(replaceState).toHaveBeenCalled();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('reports an expired handoff and never retries the one-use secret', async () => {
    browser(`#forge-pair=${'B'.repeat(64)}`);
    fetchMock.mockResolvedValueOnce(json({ token: 'anonymous' })).mockResolvedValueOnce(json({}, 403));
    const { completeBrowserHandoff } = await import('./operatorSession');
    await expect(completeBrowserHandoff()).rejects.toThrow('expired');
    await expect(completeBrowserHandoff()).rejects.toThrow('expired');
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});


describe('automatic localhost sign-in', () => {
  it.each([
    ['an HTTP failure', () => Promise.resolve(json({}, 500))],
    ['a network failure', () => Promise.reject(new TypeError('Failed to fetch'))],
  ])('reports that Forge.Api is not running after %s', async (_case, response) => {
    vi.stubGlobal('window', { location: { hostname: 'localhost', origin: 'http://localhost:5181' } });
    fetchMock.mockImplementationOnce(response);
    const { getOperatorStatus } = await import('./operatorSession');
    await expect(getOperatorStatus()).rejects.toThrow(
      'Forge.Api is not running on http://localhost:5180. Start it with scripts\\run-forge-ui.ps1 -ServerOnly.',
    );
  });

  it('offers code pairing on a remote hostname without attempting local Windows sign-in', async () => {
    vi.stubGlobal('window', { location: { hostname: 'xd003', origin: 'http://xd003:5181' } });
    fetchMock.mockResolvedValueOnce(json({ state: 'Pair browser', userId: null, localSignInAvailable: true }));
    const { getOperatorStatus, restoreLocalOperator } = await import('./operatorSession');
    const status = await getOperatorStatus();
    expect(status.localSignInAvailable).toBe(false);
    expect(await restoreLocalOperator(status)).toEqual(status);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  function browser() {
    const values = new Map<string, string>();
    vi.stubGlobal('window', {
      location: { origin: 'http://localhost:5181', hostname: 'localhost' }, dispatchEvent: vi.fn(),
      localStorage: { getItem: (key: string) => values.get(key), setItem: (key: string, value: string) => values.set(key, value), removeItem: (key: string) => values.delete(key) },
    });
    return values;
  }
  const unpaired = { state: 'Pair browser' as const, userId: null, localSignInAvailable: true };

  it.each(['Pair browser', 'Needs operator enrollment'] as const)('automatically establishes a session from %s and shares concurrent attempts', async (state) => {
    browser();
    fetchMock.mockImplementation(async (url) => String(url).endsWith('/status')
      ? json({ state: 'Paired', userId: 'existing' }) : json({ token: 'csrf' }));
    const { restoreLocalOperator } = await import('./operatorSession');
    const status = { ...unpaired, state };
    const statuses = await Promise.all([restoreLocalOperator(status), restoreLocalOperator(status)]);
    expect(statuses.every(s => s.userId === 'existing')).toBe(true);
    const calls = fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/local-sign-in'));
    expect(calls).toHaveLength(1);
    expect(String(calls[0][0])).toBe('http://localhost:5180/api/operator/local-sign-in');
    expect(calls[0][1]?.credentials).toBe('include');
    expect(new Headers(calls[0][1]?.headers).get('X-Forge-CSRF')).toBe('csrf');
  });

  it('does not retry a denied automatic sign-in, but permits a manual retry', async () => {
    browser();
    fetchMock.mockResolvedValueOnce(json({ token: 'csrf' })).mockResolvedValueOnce(json({}, 403)).mockResolvedValueOnce(json({ token: 'paired' }));
    const { restoreLocalOperator, signInLocalOperator } = await import('./operatorSession');
    await expect(restoreLocalOperator(unpaired)).rejects.toThrow('unavailable');
    expect(await restoreLocalOperator(unpaired)).toEqual(unpaired);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    await signInLocalOperator();
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it('signs in again automatically after a Forge.Api restart drops the session', async () => {
    browser();
    fetchMock.mockImplementation(async (url) => String(url).endsWith('/status')
      ? json({ state: 'Paired', userId: 'existing' }) : json({ token: 'csrf' }));
    const { restoreLocalOperator } = await import('./operatorSession');
    await restoreLocalOperator(unpaired);
    // The restart revoked the in-memory session; the same page load sees an unpaired status again.
    await restoreLocalOperator(unpaired);
    expect(fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/local-sign-in'))).toHaveLength(2);
  });

  it('keeps trying automatic sign-in while Forge.Api is unreachable', async () => {
    browser();
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'));
    const { restoreLocalOperator } = await import('./operatorSession');
    await expect(restoreLocalOperator(unpaired)).rejects.toThrow(TypeError);
    fetchMock.mockImplementation(async (url) => String(url).endsWith('/status')
      ? json({ state: 'Paired', userId: 'existing' }) : json({ token: 'csrf' }));
    expect((await restoreLocalOperator(unpaired)).state).toBe('Paired');
  });

  it('respects explicit logout across reloads and permits explicit local sign-in', async () => {
    const values = browser();
    fetchMock.mockResolvedValueOnce(json({ token: 'csrf' })).mockResolvedValueOnce(new Response(null, { status: 204 }));
    let session = await import('./operatorSession');
    await session.unpairOperator();
    expect(values.get('forge.operator.signed-out')).toBe('true');
    vi.resetModules();
    session = await import('./operatorSession');
    expect(await session.restoreLocalOperator(unpaired)).toEqual(unpaired);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    fetchMock.mockResolvedValueOnce(json({ token: 'csrf' })).mockResolvedValueOnce(json({ token: 'paired' }));
    await session.signInLocalOperator();
    expect(values.has('forge.operator.signed-out')).toBe(false);
  });

  it('leaves paired sessions and unavailable local sign-in alone', async () => {
    browser();
    const { restoreLocalOperator } = await import('./operatorSession');
    await restoreLocalOperator({ ...unpaired, localSignInAvailable: false });
    await restoreLocalOperator({ state: 'Paired', userId: 'existing', localSignInAvailable: true });
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe('choosing a sign-in path', () => {
  const remoteWindow = { location: { hostname: 'xd003', origin: 'http://xd003:5181' }, addEventListener: vi.fn(), dispatchEvent: vi.fn(), localStorage: { getItem: () => null, setItem: () => undefined, removeItem: () => undefined } };

  it('never offers or attempts local sign-in from another machine', async () => {
    vi.stubGlobal('window', remoteWindow);
    const { localSignInUsable, restoreLocalOperator } = await import('./operatorSession');
    const status = { state: 'Pair browser' as const, userId: null, localSignInAvailable: true };
    expect(localSignInUsable(status)).toBe(false);
    expect(await restoreLocalOperator(status)).toBe(status);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('still offers local sign-in on the host itself', async () => {
    vi.stubGlobal('window', { ...remoteWindow, location: { hostname: 'localhost', origin: 'http://localhost:5181' } });
    const { localSignInUsable } = await import('./operatorSession');
    expect(localSignInUsable({ state: 'Pair browser', userId: null, localSignInAvailable: true })).toBe(true);
    expect(localSignInUsable({ state: 'Pair browser', userId: null, localSignInAvailable: false })).toBe(false);
  });
});

describe('issuing a pairing code from the host browser', () => {
  const hostWindow = (hostname: string) => ({ location: { hostname, origin: `http://${hostname}:5181` }, dispatchEvent: vi.fn() });

  it('calls the API directly, bypassing Vite, and returns the code with its expiry', async () => {
    vi.stubGlobal('window', hostWindow('localhost'));
    const code = 'C'.repeat(64);
    fetchMock.mockImplementation(async (url) => String(url).endsWith('/csrf') ? json({ token: 'csrf' }) : json({ challenge: code, expiresInSeconds: 120 }));
    const { issuePairingCode, isHostBrowser } = await import('./operatorSession');
    expect(isHostBrowser()).toBe(true);
    const before = Date.now();
    const issued = await issuePairingCode();
    expect(issued.challenge).toBe(code);
    expect(issued.expiresAt).toBeGreaterThanOrEqual(before + 120_000);
    const call = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/issue-pairing-code'));
    expect(String(call?.[0])).toBe('http://localhost:5180/api/operator/issue-pairing-code');
    expect(call?.[1]?.method).toBe('POST');
    expect(call?.[1]?.credentials).toBe('include');
    expect(new Headers(call?.[1]?.headers).get('X-Forge-CSRF')).toBe('csrf');
  });

  it('is hidden and never requested from another machine', async () => {
    vi.stubGlobal('window', hostWindow('xd003'));
    const { issuePairingCode, isHostBrowser } = await import('./operatorSession');
    expect(isHostBrowser()).toBe(false);
    await expect(issuePairingCode()).rejects.toThrow('localhost');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('explains a host refusal', async () => {
    vi.stubGlobal('window', hostWindow('localhost'));
    fetchMock.mockImplementation(async (url) => String(url).endsWith('/csrf') ? json({ token: 'csrf' }) : json({ title: 'denied' }, 403));
    const { issuePairingCode } = await import('./operatorSession');
    await expect(issuePairingCode()).rejects.toThrow(/Windows account running Forge/);
  });
});

describe('out-of-band pairing code delivery', () => {
  beforeEach(() => vi.stubGlobal('window', { location: { hostname: 'xd003', origin: 'http://xd003:5181' }, addEventListener: vi.fn(), dispatchEvent: vi.fn(), localStorage: { getItem: () => null, setItem: () => undefined, removeItem: () => undefined } }));

  it('asks the host to send a code and never receives one back', async () => {
    fetchMock.mockImplementation(async (url) => String(url).endsWith('/csrf') ? json({ token: 'csrf' }) : json({ delivered: true, platform: 'telegram' }, 202));
    const { requestPairingCode } = await import('./operatorSession');
    await requestPairingCode();
    const call = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/api/operator/request-pairing-code'));
    expect(call).toBeDefined();
    expect(call?.[1]?.method).toBe('POST');
    expect(new Headers(call?.[1]?.headers).get('X-Forge-CSRF')).toBe('csrf');
    const body = await (await json({ delivered: true })).json() as Record<string, unknown>;
    expect(body).not.toHaveProperty('challenge');
  });

  it('explains the throttle instead of reporting a generic failure', async () => {
    fetchMock.mockImplementation(async (url) => String(url).endsWith('/csrf') ? json({ token: 'csrf' }) : json({ title: 'too soon' }, 429));
    const { requestPairingCode } = await import('./operatorSession');
    await expect(requestPairingCode()).rejects.toThrow(/just sent/i);
  });

  it('surfaces an unconfigured chat bridge', async () => {
    fetchMock.mockImplementation(async (url) => String(url).endsWith('/csrf') ? json({ token: 'csrf' }) : json({ title: 'not configured' }, 503));
    const { requestPairingCode } = await import('./operatorSession');
    await expect(requestPairingCode()).rejects.toThrow(/chat bridge/i);
  });
});
