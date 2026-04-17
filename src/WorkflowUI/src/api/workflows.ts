/**
 * Encode a workflow path preserving `/` separators but encoding each segment.
 */
export function encodePath(p: string): string {
  return p
    .split('/')
    .map((seg) => encodeURIComponent(seg))
    .join('/');
}

const API_BASE = '/api/workflows';

export async function getWorkflow(path: string): Promise<unknown> {
  const url = `${API_BASE}/${encodePath(path)}`;
  const res = await fetch(url);
  if (!res.ok) throw new Error(`GET ${url} failed: ${res.status}`);
  return res.json();
}

export async function putWorkflow(
  path: string,
  body: unknown,
): Promise<unknown> {
  const url = `${API_BASE}/${encodePath(path)}`;
  const res = await fetch(url, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`PUT ${url} failed: ${res.status}`);
  return res.json();
}
