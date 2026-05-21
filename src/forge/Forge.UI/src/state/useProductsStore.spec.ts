// Phase 80.G — the products store owns the active repo selection. A repo
// belongs to exactly one product, so switching products must clear the active
// repo (reset to all-repos) rather than carry a now-foreign repo across.
//
// vitest runs without a DOM, so we stub a minimal `window` (Map-backed
// localStorage + a reload spy) before importing the store, which reads/writes
// `forge.activeRepoId` through forgeApi.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

function fakeLocalStorage(): Storage {
  const m = new Map<string, string>();
  return {
    get length() { return m.size; },
    getItem: (k: string) => (m.has(k) ? m.get(k)! : null),
    setItem: (k: string, v: string) => { m.set(k, String(v)); },
    removeItem: (k: string) => { m.delete(k); },
    clear: () => m.clear(),
    key: (i: number) => [...m.keys()][i] ?? null,
  } as Storage;
}

describe('useProductsStore — active repo scope', () => {
  let reload: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    reload = vi.fn();
    vi.stubGlobal('window', {
      localStorage: fakeLocalStorage(),
      location: { reload },
    });
    vi.resetModules();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('clears the active repo when the product changes', async () => {
    const { useProductsStore } = await import('./useProductsStore');
    const { setActiveRepoId, getActiveRepoId } = await import('@/api/forgeApi');

    setActiveRepoId('repo-a');
    useProductsStore.setState({ activeProductId: 'p1', activeRepoId: 'repo-a' });

    useProductsStore.getState().setActive('p2');

    expect(useProductsStore.getState().activeProductId).toBe('p2');
    expect(useProductsStore.getState().activeRepoId).toBeNull();
    expect(getActiveRepoId()).toBeNull();
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('setActiveRepo persists the selection and reloads', async () => {
    const { useProductsStore } = await import('./useProductsStore');
    const { getActiveRepoId } = await import('@/api/forgeApi');

    useProductsStore.getState().setActiveRepo('repo-b');

    expect(useProductsStore.getState().activeRepoId).toBe('repo-b');
    expect(getActiveRepoId()).toBe('repo-b');
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('drops a stale repo when loaded repos no longer include it', async () => {
    const { useProductsStore } = await import('./useProductsStore');
    const { setActiveRepoId, getActiveRepoId } = await import('@/api/forgeApi');
    const { forgeApi } = await import('@/api/forgeApi');

    // Active product p1 with repo-a selected, but the product only has repo-x.
    setActiveRepoId('repo-a');
    useProductsStore.setState({ activeProductId: 'p1', activeRepoId: 'repo-a' });
    vi.spyOn(forgeApi, 'listProductRepos').mockResolvedValue([
      { id: 'repo-x', productId: 'p1', kind: 'ado', remoteUrl: '', displayName: 'repo-x', defaultBranch: 'main', cloneRoot: '' },
    ]);

    await useProductsStore.getState().loadReposFor('p1');

    expect(useProductsStore.getState().activeRepoId).toBeNull();
    expect(getActiveRepoId()).toBeNull();
  });

  it('keeps the active repo when it is still in the loaded repos', async () => {
    const { useProductsStore } = await import('./useProductsStore');
    const { setActiveRepoId } = await import('@/api/forgeApi');
    const { forgeApi } = await import('@/api/forgeApi');

    setActiveRepoId('repo-x');
    useProductsStore.setState({ activeProductId: 'p1', activeRepoId: 'repo-x' });
    vi.spyOn(forgeApi, 'listProductRepos').mockResolvedValue([
      { id: 'repo-x', productId: 'p1', kind: 'ado', remoteUrl: '', displayName: 'repo-x', defaultBranch: 'main', cloneRoot: '' },
    ]);

    await useProductsStore.getState().loadReposFor('p1');

    expect(useProductsStore.getState().activeRepoId).toBe('repo-x');
  });
});
