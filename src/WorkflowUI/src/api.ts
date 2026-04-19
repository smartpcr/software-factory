// -----------------------------------------------------------------------
// Server API client
// -----------------------------------------------------------------------

import type { WorkflowData } from './types';

function encodePath(p: string): string {
  return p.split('/').map(seg => encodeURIComponent(seg)).join('/');
}

const API_BASE = '/api/workflows';
const FILES_BASE = '/api/files';

// ---- Workflow CRUD ----

export async function listWorkflows(): Promise<unknown[]> {
  const res = await fetch(API_BASE);
  if (!res.ok) throw new Error(`GET ${API_BASE} failed: ${res.status}`);
  return res.json();
}

export async function getWorkflow(id: string): Promise<unknown> {
  const url = `${API_BASE}/${encodePath(id)}`;
  const res = await fetch(url);
  if (!res.ok) throw new Error(`GET ${url} failed: ${res.status}`);
  return res.json();
}

export async function saveWorkflow(id: string, data: WorkflowData): Promise<void> {
  const url = `${API_BASE}/${encodePath(id)}`;
  // Store as YAML-compatible JSON with sidecar for layout
  const yaml = JSON.stringify({
    id: data.id,
    name: data.name,
    nodes: data.nodes,
    edges: data.edges,
  });
  const sidecarJson = JSON.stringify({
    createdAt: data.createdAt,
    updatedAt: data.updatedAt,
  });
  const res = await fetch(url, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ yaml, sidecarJson }),
  });
  if (!res.ok) throw new Error(`PUT ${url} failed: ${res.status}`);
}

export async function deleteWorkflowApi(id: string): Promise<boolean> {
  const url = `${API_BASE}/${encodePath(id)}`;
  // First get etag
  const getRes = await fetch(url);
  if (!getRes.ok) return false;
  const record = await getRes.json();
  const etag = record?.etag || record?.eTag || '*';
  const res = await fetch(url, {
    method: 'DELETE',
    headers: { 'If-Match': etag },
  });
  return res.ok || res.status === 204;
}

// ---- File operations ----

export async function createWorkflowApi(id: string): Promise<void> {
  const res = await fetch(FILES_BASE, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ id }),
  });
  if (!res.ok) throw new Error(`POST ${FILES_BASE} failed: ${res.status}`);
}

export async function exportWorkflowsApi(ids: string[]): Promise<Blob> {
  const res = await fetch(`${FILES_BASE}/export`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ids }),
  });
  if (!res.ok) throw new Error(`POST ${FILES_BASE}/export failed: ${res.status}`);
  return res.blob();
}

export async function importWorkflowsApi(zipBlob: Blob): Promise<{ imported: string[] }> {
  const res = await fetch(`${FILES_BASE}/import`, {
    method: 'POST',
    body: zipBlob,
  });
  if (!res.ok) throw new Error(`POST ${FILES_BASE}/import failed: ${res.status}`);
  return res.json() as Promise<{ imported: string[] }>;
}

// ---- Execution ----

export async function runWorkflowApi(id: string): Promise<{ runId: string }> {
  const res = await fetch(`${API_BASE}/${encodePath(id)}/run`, {
    method: 'POST',
  });
  if (!res.ok) throw new Error(`POST run failed: ${res.status}`);
  return res.json() as Promise<{ runId: string }>;
}

export async function cancelWorkflowApi(id: string): Promise<void> {
  const res = await fetch(`${API_BASE}/${encodePath(id)}/cancel`, {
    method: 'POST',
  });
  if (!res.ok) throw new Error(`POST cancel failed: ${res.status}`);
}

/** Check if server is available */
export async function checkServerHealth(): Promise<boolean> {
  try {
    const res = await fetch('/api/health', { signal: AbortSignal.timeout(2000) });
    return res.ok;
  } catch {
    return false;
  }
}
