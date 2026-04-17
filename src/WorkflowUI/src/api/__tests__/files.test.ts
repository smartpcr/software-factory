import { describe, it, expect, vi, beforeEach } from 'vitest';
import {
  getWorkspaceTree,
  createWorkflow,
  renameWorkflow,
  deleteWorkflow,
  duplicateWorkflow,
  exportWorkflows,
  importWorkflows,
} from '../files';

describe('files API client', () => {
  let capturedUrl: string;
  let capturedMethod: string;
  let capturedBody: string | undefined;

  beforeEach(() => {
    capturedUrl = '';
    capturedMethod = '';
    capturedBody = undefined;

    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        capturedUrl = url;
        capturedMethod = init?.method ?? 'GET';
        capturedBody =
          typeof init?.body === 'string' ? init.body : undefined;
        return {
          ok: true,
          json: async () => ({ name: '', isFolder: true, children: [] }),
          blob: async () => new Blob(['zip-data']),
        };
      }),
    );
  });

  it('getWorkspaceTree calls GET /api/files', async () => {
    await getWorkspaceTree();
    expect(capturedUrl).toBe('/api/files');
    expect(capturedMethod).toBe('GET');
  });

  it('createWorkflow calls POST /api/files with id', async () => {
    await createWorkflow('my-workflow');
    expect(capturedUrl).toBe('/api/files');
    expect(capturedMethod).toBe('POST');
    expect(capturedBody).toContain('"id":"my-workflow"');
  });

  it('renameWorkflow calls PATCH /api/files/{id}', async () => {
    await renameWorkflow('old-id', 'new-id');
    expect(capturedUrl).toBe('/api/files/old-id');
    expect(capturedMethod).toBe('PATCH');
    expect(capturedBody).toContain('"newId":"new-id"');
  });

  it('deleteWorkflow calls DELETE /api/files/{id}', async () => {
    await deleteWorkflow('wf-1');
    expect(capturedUrl).toBe('/api/files/wf-1');
    expect(capturedMethod).toBe('DELETE');
  });

  it('duplicateWorkflow calls POST /api/files/duplicate', async () => {
    await duplicateWorkflow('source');
    expect(capturedUrl).toBe('/api/files/duplicate');
    expect(capturedMethod).toBe('POST');
    expect(capturedBody).toContain('"sourceId":"source"');
  });

  it('duplicateWorkflow with targetId includes targetId', async () => {
    await duplicateWorkflow('source', 'my-target');
    expect(capturedBody).toContain('"targetId":"my-target"');
    expect(capturedBody).toContain('"sourceId":"source"');
  });

  it('exportWorkflows calls POST /api/files/export', async () => {
    const blob = await exportWorkflows(['wf1', 'wf2']);
    expect(capturedUrl).toBe('/api/files/export');
    expect(capturedMethod).toBe('POST');
    expect(blob).toBeDefined();
  });

  it('importWorkflows calls POST /api/files/import', async () => {
    const zipBlob = new Blob(['data']);
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        capturedUrl = url;
        capturedMethod = init?.method ?? 'GET';
        return { ok: true, json: async () => ({ imported: ['wf1'] }) };
      }),
    );

    const result = await importWorkflows(zipBlob);
    expect(capturedUrl).toBe('/api/files/import');
    expect(capturedMethod).toBe('POST');
    expect(result.imported).toEqual(['wf1']);
  });

  it('renameWorkflow encodes special characters in path', async () => {
    await renameWorkflow('my folder/wf', 'new-id');
    expect(capturedUrl).toBe('/api/files/my%20folder/wf');
  });
});
