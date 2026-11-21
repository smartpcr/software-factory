const baseUrl = import.meta.env.VITE_FORGE_API_URL ?? '';
export const operatorSessionChanged = 'forge:operator-session-changed';
let csrfToken: string | null = null;
let csrfRequest: Promise<string> | null = null;
let browserHandoff: Promise<void> | null = null;

/** Consume the local launcher's one-use handoff once, including React StrictMode remounts. */
export function completeBrowserHandoff(): Promise<void> {
  if (browserHandoff) return browserHandoff;
  const fragment = new URLSearchParams(window.location.hash.slice(1));
  const challenge = fragment.get('forge-pair');
  if (challenge === null) return Promise.resolve();
  // Remove the secret before any network request, rendering, or error reporting.
  window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search);
  browserHandoff = /^[0-9a-f]{64}$/i.test(challenge)
    ? pairOperator(challenge)
    : Promise.reject(new Error('This browser sign-in link is invalid. Open Forge from the launcher again.'));
  return browserHandoff;
}

export interface OperatorStatus {
  state: 'Paired' | 'Pair browser' | 'Needs operator enrollment';
  userId: string | null;
  localSignInAvailable?: boolean;
  codeDeliveryAvailable?: boolean;
}

function apiUnavailableMessage(): string {
  if (typeof window !== 'undefined' && ['localhost', '127.0.0.1', '[::1]'].includes(window.location.hostname)) {
    return 'Forge.Api is not running on http://localhost:5180. Start it with scripts\\run-forge-ui.ps1 -ServerOnly.';
  }
  return 'Forge.Api is unavailable on the Forge host. Ask the host administrator to start it.';
}

export function clearOperatorSession(): void {
  csrfToken = null;
  csrfRequest = null;
  if (typeof window !== 'undefined') window.dispatchEvent(new Event(operatorSessionChanged));
}

export async function operatorCsrfHeaders(): Promise<Record<string, string>> {
  if (!csrfToken) {
    csrfRequest ??= fetch(`${baseUrl}/api/operator/csrf`, { credentials: 'include', cache: 'no-store' })
      .then(async (response) => {
        if (!response.ok) throw new Error('Could not establish request protection. Reload Forge.');
        const data = await response.json() as { token: string };
        csrfToken = data.token;
        return data.token;
      }).finally(() => { csrfRequest = null; });
    await csrfRequest;
  }
  return { 'X-Forge-CSRF': csrfToken! };
}

/** All browser requests use the paired cookie; every mutation also carries a CSRF token. */
export async function operatorFetch(input: RequestInfo | URL, init: RequestInit = {}): Promise<Response> {
  const request = input instanceof Request ? input : undefined;
  const headers = new Headers(init.headers ?? request?.headers);
  headers.delete('X-Forge-User');
  if (!['GET', 'HEAD', 'OPTIONS'].includes((init.method ?? request?.method ?? 'GET').toUpperCase())) {
    const csrf = await operatorCsrfHeaders();
    Object.entries(csrf).forEach(([name, value]) => headers.set(name, value));
  }
  const response = await fetch(input, { ...init, headers, credentials: 'include' });
  if (response.status === 401) clearOperatorSession();
  return response;
}

export async function getOperatorStatus(): Promise<OperatorStatus> {
  let response: Response;
  try {
    response = await fetch(`${baseUrl}/api/operator/status`, { credentials: 'include', cache: 'no-store' });
  } catch {
    throw new Error(apiUnavailableMessage());
  }
  if (!response.ok) throw new Error(apiUnavailableMessage());
  const status = await response.json() as OperatorStatus;
  if (!['localhost', '127.0.0.1', '[::1]'].includes(window.location.hostname)) status.localSignInAvailable = false;
  return status;
}

export async function pairOperator(challenge: string): Promise<void> {
  const response = await operatorFetch(`${baseUrl}/api/operator/pair`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ challenge }),
  });
  if (!response.ok) throw new Error('This pairing code is invalid, expired, or already used. Run Pair browser again.');
  csrfToken = ((await response.json()) as { token: string }).token;
  setSignedOut(false);
  if (typeof window !== 'undefined') window.dispatchEvent(new Event(operatorSessionChanged));
}

/**
 * Asks the host to send a pairing code to the operator's own chat. The code never comes
 * back in this response, so reaching this endpoint from anywhere on the network reveals
 * nothing; holding the chat account is what actually grants access.
 */
export async function requestPairingCode(): Promise<void> {
  const response = await operatorFetch(`${baseUrl}/api/operator/request-pairing-code`, { method: 'POST' });
  if (response.status === 429) throw new Error('A code was just sent. Use that one, or wait a minute before asking again.');
  if (!response.ok) throw new Error('Could not send a pairing code. Check that the chat bridge is configured on the host.');
}

let localSignInRequest: Promise<void> | null = null;
/**
 * Set only when the host actually refused this browser's Windows identity (or the operator
 * signed out). An unreachable API, e.g. during a Forge.Api restart, must not block it: the
 * restart drops every in-memory session, and a localhost tab has to sign itself back in.
 */
let automaticSignInBlocked = false;
const signedOutKey = 'forge.operator.signed-out';

function setSignedOut(value: boolean): void {
  try { if (value) window.localStorage.setItem(signedOutKey, 'true'); else window.localStorage.removeItem(signedOutKey); }
  catch { /* Storage can be unavailable in restricted browser profiles. */ }
}

/**
 * The host reports local sign-in without knowing who is asking, because it verifies the
 * caller's socket only when the request arrives. A browser on another machine can never
 * satisfy that, so it must not be offered the local path or auto-attempt it.
 */
export function localSignInUsable(status: OperatorStatus | null): boolean {
  return !!status?.localSignInAvailable && isHostBrowser();
}

/** True when this page reaches the API on localhost, so host-socket proofs can succeed. */
export function isHostBrowser(): boolean {
  if (typeof window === 'undefined') return false;
  try { return ['localhost', '127.0.0.1', '[::1]'].includes(new URL(baseUrl || window.location.origin).hostname); }
  catch { return false; }
}

/**
 * Host-only operator calls go straight to the API, bypassing Vite: the host verifies the
 * browser's own TCP connection owner, which a proxied request cannot prove.
 */
function directHostApiUrl(path: string, purpose: string): URL {
  const url = new URL(baseUrl || window.location.origin);
  if (!['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)) throw new Error(`${purpose} requires a localhost connection.`);
  if (!baseUrl && url.port === '5181') url.port = '5180';
  url.pathname = path;
  url.search = ''; url.hash = '';
  return url;
}

export interface IssuedPairingCode { challenge: string; expiresAt: number }

/**
 * Mints a one-use code that pairs another browser as the signed-in user. The host refuses
 * unless this request comes directly from the host Windows user's browser.
 */
export async function issuePairingCode(): Promise<IssuedPairingCode> {
  const response = await operatorFetch(directHostApiUrl('/api/operator/issue-pairing-code', 'Issuing a pairing code'), { method: 'POST' });
  if (response.status === 403) throw new Error('Only the Windows account running Forge can issue a pairing code, from a browser on the host.');
  if (!response.ok) throw new Error('Could not issue a pairing code.');
  const data = await response.json() as { challenge: string; expiresInSeconds: number };
  return { challenge: data.challenge, expiresAt: Date.now() + data.expiresInSeconds * 1000 };
}

export async function signInLocalOperator(): Promise<void> {
  if (localSignInRequest) return localSignInRequest;
  localSignInRequest = (async () => {
    const url = directHostApiUrl('/api/operator/local-sign-in', 'Local sign-in');
    const response = await operatorFetch(url, { method: 'POST' });
    if (!response.ok) throw new Error('Automatic sign-in is unavailable. Use a pairing code, or retry from the Windows account running Forge.');
    csrfToken = ((await response.json()) as { token: string }).token;
    automaticSignInBlocked = false;
    setSignedOut(false);
    window.dispatchEvent(new Event(operatorSessionChanged));
  })().finally(() => { localSignInRequest = null; });
  return localSignInRequest;
}

export async function restoreLocalOperator(status: OperatorStatus): Promise<OperatorStatus> {
  if (status.state === 'Paired' || !localSignInUsable(status)) return status;
  // Explicit logout must remain logged out, including after a reload or in another tab.
  try { if (window.localStorage.getItem(signedOutKey) === 'true') return status; } catch { /* Optional preference. */ }
  if (localSignInRequest) { await localSignInRequest; return getOperatorStatus(); }
  if (automaticSignInBlocked) return status;
  try { await signInLocalOperator(); }
  catch (reason) {
    // fetch rejects with TypeError only when the API is unreachable; anything else is a refusal.
    if (!(reason instanceof TypeError)) automaticSignInBlocked = true;
    throw reason;
  }
  return getOperatorStatus();
}

export async function unpairOperator(): Promise<void> {
  const response = await operatorFetch(`${baseUrl}/api/operator/logout`, { method: 'POST' });
  if (!response.ok) throw new Error('Could not end this session.');
  automaticSignInBlocked = true;
  setSignedOut(true);
  clearOperatorSession();
}
