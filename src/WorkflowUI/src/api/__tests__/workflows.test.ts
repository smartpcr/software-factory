import { describe, it, expect, vi, beforeEach } from 'vitest';
import { encodePath, getWorkflow, putWorkflow } from '../workflows';

describe('encodePath', () => {
  it('preserves simple path', () => {
    expect(encodePath('workflow.yaml')).toBe('workflow.yaml');
  });

  it('preserves nested path separators', () => {
    expect(encodePath('folder/sub/workflow.yaml')).toBe(
      'folder/sub/workflow.yaml',
    );
  });

  it('encodes spaces per segment', () => {
    const result = encodePath('my folder/my file.yaml');
    expect(result).toBe('my%20folder/my%20file.yaml');
  });

  it('does not double-encode', () => {
    const result = encodePath('a/b/c.yaml');
    expect(result).not.toContain('%252F');
  });

  it('handles deeply nested paths', () => {
    const result = encodePath('a/b/c/d/e/f.yaml');
    expect(result).toBe('a/b/c/d/e/f.yaml');
  });
});

describe('getWorkflow / putWorkflow URL construction', () => {
  let capturedUrl: string;

  beforeEach(() => {
    capturedUrl = '';
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        capturedUrl = url;
        return { ok: true, json: async () => ({}) };
      }),
    );
  });

  it('getWorkflow uses correct URL for simple path', async () => {
    await getWorkflow('simple.yaml');
    expect(capturedUrl).toBe('/api/workflows/simple.yaml');
  });

  it('getWorkflow preserves nested path separators', async () => {
    await getWorkflow('folder/sub/workflow.yaml');
    expect(capturedUrl).toBe('/api/workflows/folder/sub/workflow.yaml');
  });

  it('getWorkflow encodes spaces', async () => {
    await getWorkflow('my folder/my file.yaml');
    expect(capturedUrl).toBe('/api/workflows/my%20folder/my%20file.yaml');
  });

  it('putWorkflow uses correct URL', async () => {
    await putWorkflow('folder/sub/workflow.yaml', {
      workflowId: 'test',
      nodes: [],
      connections: [],
    });
    expect(capturedUrl).toBe('/api/workflows/folder/sub/workflow.yaml');
  });
});
