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
  const response = await fetch(`${baseUrl}/api/operator/status`, { credentials: 'include', cache: 'no-store' });
  if (!response.ok) throw new Error('Could not connect to the local Forge host.');
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

let localSignInRequest: Promise<void> | null = null;
let automaticSignInAttempted = false;
const signedOutKey = 'forge.operator.signed-out';

function setSignedOut(value: boolean): void {
  try { if (value) window.localStorage.setItem(signedOutKey, 'true'); else window.localStorage.removeItem(signedOutKey); }
  catch { /* Storage can be unavailable in restricted browser profiles. */ }
}

export async function signInLocalOperator(): Promise<void> {
  if (localSignInRequest) return localSignInRequest;
  localSignInRequest = (async () => {
    const url = new URL(baseUrl || window.location.origin);
    if (!['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)) throw new Error('Local sign-in requires a localhost connection.');
    if (!baseUrl && url.port === '5181') url.port = '5180';
    // Bypass Vite: the host verifies the browser's TCP connection owner.
    url.pathname = '/api/operator/local-sign-in';
    url.search = ''; url.hash = '';
    const response = await operatorFetch(url, { method: 'POST' });
    if (!response.ok) throw new Error('Automatic sign-in is unavailable. Use a pairing code, or retry from the Windows account running Forge.');
    csrfToken = ((await response.json()) as { token: string }).token;
    setSignedOut(false);
    window.dispatchEvent(new Event(operatorSessionChanged));
  })().finally(() => { localSignInRequest = null; });
  return localSignInRequest;
}

export async function restoreLocalOperator(status: OperatorStatus): Promise<OperatorStatus> {
  if (status.state === 'Paired' || !status.localSignInAvailable) return status;
  // Explicit logout must remain logged out, including after a reload or in another tab.
  try { if (window.localStorage.getItem(signedOutKey) === 'true') return status; } catch { /* Optional preference. */ }
  if (localSignInRequest) { await localSignInRequest; return getOperatorStatus(); }
  if (automaticSignInAttempted) return status;
  automaticSignInAttempted = true;
  await signInLocalOperator();
  return getOperatorStatus();
}

export async function unpairOperator(): Promise<void> {
  const response = await operatorFetch(`${baseUrl}/api/operator/logout`, { method: 'POST' });
  if (!response.ok) throw new Error('Could not end this session.');
  automaticSignInAttempted = true;
  setSignedOut(true);
  clearOperatorSession();
}
