import { encodePath } from './workflows';

const FILES_BASE = '/api/files';

export interface TreeNode {
  name: string;
  isFolder: boolean;
  workflowId?: string;
  lastModifiedUtc?: string;
  sizeBytes?: number;
  children: TreeNode[];
}

export async function getWorkspaceTree(): Promise<TreeNode> {
  const res = await fetch(FILES_BASE);
  if (!res.ok) throw new Error(`GET ${FILES_BASE} failed: ${res.status}`);
  return res.json() as Promise<TreeNode>;
}

export async function createWorkflow(id: string): Promise<void> {
  const res = await fetch(FILES_BASE, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ id }),
  });
  if (!res.ok) throw new Error(`POST ${FILES_BASE} failed: ${res.status}`);
}

export async function renameWorkflow(
  id: string,
  newId: string,
): Promise<void> {
  const url = `${FILES_BASE}/${encodePath(id)}`;
  const res = await fetch(url, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ newId }),
  });
  if (!res.ok) throw new Error(`PATCH ${url} failed: ${res.status}`);
}

export async function deleteWorkflow(id: string): Promise<void> {
  const url = `${FILES_BASE}/${encodePath(id)}`;
  const res = await fetch(url, { method: 'DELETE' });
  if (!res.ok) throw new Error(`DELETE ${url} failed: ${res.status}`);
}

export async function duplicateWorkflow(
  sourceId: string,
  targetId?: string,
): Promise<{ id: string }> {
  const url = `${FILES_BASE}/duplicate`;
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sourceId, targetId: targetId ?? null }),
  });
  if (!res.ok) throw new Error(`POST ${url} failed: ${res.status}`);
  return res.json() as Promise<{ id: string }>;
}

export async function exportWorkflows(ids: string[]): Promise<Blob> {
  const res = await fetch(`${FILES_BASE}/export`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ids }),
  });
  if (!res.ok)
    throw new Error(`POST ${FILES_BASE}/export failed: ${res.status}`);
  return res.blob();
}

export async function importWorkflows(
  zipBlob: Blob,
): Promise<{ imported: string[] }> {
  const res = await fetch(`${FILES_BASE}/import`, {
    method: 'POST',
    body: zipBlob,
  });
  if (!res.ok)
    throw new Error(`POST ${FILES_BASE}/import failed: ${res.status}`);
  return res.json() as Promise<{ imported: string[] }>;
}
