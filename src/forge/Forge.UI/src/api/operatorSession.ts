const baseUrl = import.meta.env.VITE_FORGE_API_URL ?? '';
export const operatorSessionChanged = 'forge:operator-session-changed';
let csrfToken: string | null = null;
let csrfRequest: Promise<string> | null = null;

export interface OperatorStatus {
  state: 'Paired' | 'Pair browser' | 'Needs operator enrollment';
  userId: string | null;
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
  return response.json() as Promise<OperatorStatus>;
}

export async function pairOperator(challenge: string): Promise<void> {
  const response = await operatorFetch(`${baseUrl}/api/operator/pair`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ challenge }),
  });
  if (!response.ok) throw new Error('This pairing code is invalid, expired, or already used. Run Pair browser again.');
  csrfToken = ((await response.json()) as { token: string }).token;
  if (typeof window !== 'undefined') window.dispatchEvent(new Event(operatorSessionChanged));
}

export async function unpairOperator(): Promise<void> {
  const response = await operatorFetch(`${baseUrl}/api/operator/logout`, { method: 'POST' });
  if (!response.ok) throw new Error('Could not end this session.');
  clearOperatorSession();
}
