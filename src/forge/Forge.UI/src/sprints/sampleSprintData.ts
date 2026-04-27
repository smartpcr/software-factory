// Phase 15 — mocked iterations / area paths / ADO discovery preview.
// Wholly client-side; no API. The wire types live in `@/types` (mirroring
// `Forge.Api/Contracts/ForgeContracts.cs` Phase 15 records) so the mock
// and the API stay in lockstep — swap a fetch for the array imports
// below with no other code change.

import type {
  IterationState,
  ProviderKind,
  IterationBindingRole as BindingRole,
  ProductIteration,
  ProductAreaPath,
  SavedPrefix,
  DiscoveryNode,
} from '@/types';

export type {
  IterationState,
  ProviderKind,
  BindingRole,
  ProductIteration,
  ProductAreaPath,
  SavedPrefix,
  DiscoveryNode,
};

// ── Active product context ────────────────────────────────────────────
export const ACTIVE_PRODUCT_ID = 'atlas';

// ── Iterations imported into Atlas ────────────────────────────────────
export const PRODUCT_ITERATIONS: ProductIteration[] = [
  {
    id: 'pi-12', productId: 'atlas',
    providerKind: 'ado', providerOrg: 'contoso', providerProject: 'Atlas',
    providerNodeId: 'ado:iter:512',
    path: 'Atlas\\Sprint 12', name: 'Sprint 12',
    startDate: '2026-02-02', finishDate: '2026-02-13',
    state: 'past',
    bindings: [{ repoId: 'r-update', repoName: 'update', role: 'reference' }],
    pointsTotal: 142, pointsDone: 138,
    importedAt: '2025-12-04T20:18:00Z', refreshedAt: '2026-04-25T09:02:00Z',
  },
  {
    id: 'pi-13', productId: 'atlas',
    providerKind: 'ado', providerOrg: 'contoso', providerProject: 'Atlas',
    providerNodeId: 'ado:iter:513',
    path: 'Atlas\\Sprint 13', name: 'Sprint 13',
    startDate: '2026-02-16', finishDate: '2026-02-27',
    state: 'past',
    bindings: [{ repoId: 'r-update', repoName: 'update', role: 'reference' }],
    pointsTotal: 138, pointsDone: 134,
    importedAt: '2025-12-04T20:18:00Z', refreshedAt: '2026-04-25T09:02:00Z',
  },
  {
    id: 'pi-14', productId: 'atlas',
    providerKind: 'ado', providerOrg: 'contoso', providerProject: 'Atlas',
    providerNodeId: 'ado:iter:514',
    path: 'Atlas\\Sprint 14', name: 'Sprint 14',
    startDate: '2026-04-13', finishDate: '2026-04-24',
    state: 'current',
    bindings: [
      { repoId: 'r-update',  repoName: 'update',  role: 'current' },
      { repoId: 'r-portal',  repoName: 'portal',  role: 'current' },
      { repoId: 'r-runtime', repoName: 'runtime', role: 'reference' },
    ],
    pointsTotal: 156, pointsDone: 96,
    importedAt: '2025-12-04T20:18:00Z', refreshedAt: '2026-04-26T02:02:00Z',
  },
  {
    id: 'pi-15', productId: 'atlas',
    providerKind: 'ado', providerOrg: 'contoso', providerProject: 'Atlas',
    providerNodeId: 'ado:iter:515',
    path: 'Atlas\\Sprint 15', name: 'Sprint 15',
    startDate: '2026-04-27', finishDate: '2026-05-08',
    state: 'future',
    bindings: [{ repoId: 'r-update', repoName: 'update', role: 'next' }],
    pointsTotal: 134, pointsDone: 0,
    importedAt: '2026-04-19T03:02:00Z', refreshedAt: '2026-04-26T02:02:00Z',
  },
  {
    id: 'pi-16', productId: 'atlas',
    providerKind: 'ado', providerOrg: 'contoso', providerProject: 'Atlas',
    providerNodeId: 'ado:iter:516',
    path: 'Atlas\\Sprint 16', name: 'Sprint 16',
    startDate: '2026-05-11', finishDate: '2026-05-22',
    state: 'future',
    bindings: [],
    pointsTotal: 0, pointsDone: 0,
    importedAt: '2026-04-19T03:02:00Z', refreshedAt: '2026-04-26T02:02:00Z',
  },
  // GitHub milestone — flat path
  {
    id: 'pi-rel-105', productId: 'atlas',
    providerKind: 'github', providerOrg: 'atlas-corp', providerProject: 'portal',
    providerNodeId: 'gh:milestone:105',
    path: '#105 v2.4 release', name: 'v2.4 release',
    startDate: null, finishDate: '2026-05-15',
    state: 'future',
    bindings: [{ repoId: 'r-portal', repoName: 'portal', role: 'reference' }],
    pointsTotal: 22, pointsDone: 4,
    importedAt: '2026-03-12T17:18:00Z', refreshedAt: '2026-04-26T02:02:00Z',
  },
  // Provider-orphan example — node was deleted upstream
  {
    id: 'pi-old', productId: 'atlas',
    providerKind: 'ado', providerOrg: 'contoso', providerProject: 'Atlas',
    providerNodeId: 'ado:iter:411',
    path: 'Atlas\\Hotfix 2025-Q4', name: 'Hotfix 2025-Q4',
    startDate: '2025-11-03', finishDate: '2025-11-07',
    state: 'provider-orphan',
    bindings: [{ repoId: 'r-update', repoName: 'update', role: 'reference' }],
    pointsTotal: 18, pointsDone: 18,
    importedAt: '2025-11-04T08:00:00Z', refreshedAt: '2026-04-26T02:02:00Z',
  },
];

// ── Area paths imported into Atlas (tree shape) ───────────────────────
export const PRODUCT_AREA_PATHS: ProductAreaPath[] = [
  {
    id: 'ap-root', productId: 'atlas',
    providerKind: 'ado', providerOrg: 'contoso', providerProject: 'Atlas',
    providerNodeId: 'ado:area:1', path: 'Atlas', parentPath: null,
    isDefaultFilter: false, workItemCount: 312,
    importedAt: '2025-12-04T20:18:00Z',
  },
  {
    id: 'ap-updates', productId: 'atlas',
    providerKind: 'ado', providerOrg: 'contoso', providerProject: 'Atlas',
    providerNodeId: 'ado:area:14', path: 'Atlas\\Updates', parentPath: 'Atlas',
    isDefaultFilter: true, workItemCount: 142,
    importedAt: '2025-12-04T20:18:00Z',
  },
  {
    id: 'ap-updates-idp', productId: 'atlas',
    providerKind: 'ado', providerOrg: 'contoso', providerProject: 'Atlas',
    providerNodeId: 'ado:area:21', path: 'Atlas\\Updates\\IDP', parentPath: 'Atlas\\Updates',
    isDefaultFilter: false, workItemCount: 68,
    importedAt: '2025-12-04T20:18:00Z',
  },
  {
    id: 'ap-updates-failover', productId: 'atlas',
    providerKind: 'ado', providerOrg: 'contoso', providerProject: 'Atlas',
    providerNodeId: 'ado:area:22', path: 'Atlas\\Updates\\Failover', parentPath: 'Atlas\\Updates',
    isDefaultFilter: false, workItemCount: 41,
    importedAt: '2025-12-04T20:18:00Z',
  },
  {
    id: 'ap-portal', productId: 'atlas',
    providerKind: 'ado', providerOrg: 'contoso', providerProject: 'Atlas',
    providerNodeId: 'ado:area:18', path: 'Atlas\\Portal', parentPath: 'Atlas',
    isDefaultFilter: false, workItemCount: 96,
    importedAt: '2025-12-04T20:18:00Z',
  },
  {
    id: 'ap-portal-onboarding', productId: 'atlas',
    providerKind: 'ado', providerOrg: 'contoso', providerProject: 'Atlas',
    providerNodeId: 'ado:area:34', path: 'Atlas\\Portal\\Onboarding', parentPath: 'Atlas\\Portal',
    isDefaultFilter: false, workItemCount: 28,
    importedAt: '2026-03-04T15:32:00Z',
  },
  {
    id: 'ap-runtime', productId: 'atlas',
    providerKind: 'ado', providerOrg: 'contoso', providerProject: 'Atlas',
    providerNodeId: 'ado:area:7', path: 'Atlas\\Runtime', parentPath: 'Atlas',
    isDefaultFilter: false, workItemCount: 74,
    importedAt: '2025-12-04T20:18:00Z',
  },
];

// ── Saved prefix shorthands (Q15.10) ──────────────────────────────────
export const SAVED_PREFIXES: SavedPrefix[] = [
  {
    id: 'sp-1', productId: 'atlas', prefix: 'Atlas\\Sprint', kind: 'iterations',
    repoId: 'r-update', repoName: 'update',
    addedBy: 'XD', lastRefreshedAt: '2026-04-26T02:02:00Z',
    delta: { added: 1, removed: 0, renamed: 0 }, // sprint 16 was added
  },
  {
    id: 'sp-2', productId: 'atlas', prefix: 'Atlas\\Releases', kind: 'iterations',
    repoId: 'r-update', repoName: 'update',
    addedBy: 'XD', lastRefreshedAt: '2026-04-26T02:02:00Z',
    delta: { added: 0, removed: 0, renamed: 0 },
  },
  {
    id: 'sp-3', productId: 'atlas', prefix: 'Atlas\\Updates', kind: 'areas',
    repoId: 'r-update', repoName: 'update',
    addedBy: 'XD', lastRefreshedAt: '2026-04-22T02:02:00Z',
    delta: { added: 0, removed: 0, renamed: 1 }, // a child was renamed
  },
];

// ── ADO discovery preview ─────────────────────────────────────────────
// Mocked response from `/api/products/atlas/sprint-discovery`. The component
// filters this in-memory by prefix to simulate the live preview.

export const DISCOVERY_ITERATIONS: DiscoveryNode[] = [
  // The Atlas root and a few peers
  { providerNodeId: 'ado:iter:500', path: 'Atlas', name: 'Atlas', hasChildren: true, depth: 0, attributes: {}, alreadyImported: false },

  // Atlas\Sprint subtree
  { providerNodeId: 'ado:iter:512', path: 'Atlas\\Sprint 12', name: 'Sprint 12', hasChildren: false, depth: 1,
    attributes: { startDate: '2026-02-02', finishDate: '2026-02-13', timeFrame: 'past' }, alreadyImported: true },
  { providerNodeId: 'ado:iter:513', path: 'Atlas\\Sprint 13', name: 'Sprint 13', hasChildren: false, depth: 1,
    attributes: { startDate: '2026-02-16', finishDate: '2026-02-27', timeFrame: 'past' }, alreadyImported: true },
  { providerNodeId: 'ado:iter:514', path: 'Atlas\\Sprint 14', name: 'Sprint 14', hasChildren: false, depth: 1,
    attributes: { startDate: '2026-04-13', finishDate: '2026-04-24', timeFrame: 'current' }, alreadyImported: true },
  { providerNodeId: 'ado:iter:515', path: 'Atlas\\Sprint 15', name: 'Sprint 15', hasChildren: false, depth: 1,
    attributes: { startDate: '2026-04-27', finishDate: '2026-05-08', timeFrame: 'future' }, alreadyImported: true },
  { providerNodeId: 'ado:iter:516', path: 'Atlas\\Sprint 16', name: 'Sprint 16', hasChildren: false, depth: 1,
    attributes: { startDate: '2026-05-11', finishDate: '2026-05-22', timeFrame: 'future' }, alreadyImported: true },
  { providerNodeId: 'ado:iter:517', path: 'Atlas\\Sprint 17', name: 'Sprint 17', hasChildren: false, depth: 1,
    attributes: { startDate: '2026-05-25', finishDate: '2026-06-05', timeFrame: 'future' }, alreadyImported: false },
  { providerNodeId: 'ado:iter:518', path: 'Atlas\\Sprint 18', name: 'Sprint 18', hasChildren: false, depth: 1,
    attributes: { startDate: '2026-06-08', finishDate: '2026-06-19', timeFrame: 'future' }, alreadyImported: false },
  { providerNodeId: 'ado:iter:519', path: 'Atlas\\Sprint 19', name: 'Sprint 19', hasChildren: false, depth: 1,
    attributes: { startDate: '2026-06-22', finishDate: '2026-07-03', timeFrame: 'future' }, alreadyImported: false },

  // Atlas\Releases subtree (with nested children)
  { providerNodeId: 'ado:iter:600', path: 'Atlas\\Releases', name: 'Releases', hasChildren: true, depth: 1, attributes: {}, alreadyImported: false },
  { providerNodeId: 'ado:iter:610', path: 'Atlas\\Releases\\v1.8', name: 'v1.8', hasChildren: false, depth: 2,
    attributes: { startDate: '2026-03-01', finishDate: '2026-03-15', timeFrame: 'past' }, alreadyImported: false },
  { providerNodeId: 'ado:iter:611', path: 'Atlas\\Releases\\v1.9', name: 'v1.9', hasChildren: false, depth: 2,
    attributes: { startDate: '2026-04-01', finishDate: '2026-04-15', timeFrame: 'past' }, alreadyImported: false },
  { providerNodeId: 'ado:iter:612', path: 'Atlas\\Releases\\v2.0', name: 'v2.0', hasChildren: false, depth: 2,
    attributes: { startDate: '2026-05-01', finishDate: '2026-05-15', timeFrame: 'future' }, alreadyImported: false },
  { providerNodeId: 'ado:iter:613', path: 'Atlas\\Releases\\v2.1', name: 'v2.1', hasChildren: false, depth: 2,
    attributes: { startDate: '2026-06-01', finishDate: '2026-06-15', timeFrame: 'future' }, alreadyImported: false },
  // Hotfix sub-tree
  { providerNodeId: 'ado:iter:700', path: 'Atlas\\Hotfix', name: 'Hotfix', hasChildren: true, depth: 1, attributes: {}, alreadyImported: false },
  { providerNodeId: 'ado:iter:711', path: 'Atlas\\Hotfix\\2026-Q1', name: '2026-Q1', hasChildren: false, depth: 2,
    attributes: { startDate: '2026-01-15', finishDate: '2026-01-22', timeFrame: 'past' }, alreadyImported: false },
  { providerNodeId: 'ado:iter:712', path: 'Atlas\\Hotfix\\2026-Q2', name: '2026-Q2', hasChildren: false, depth: 2,
    attributes: { startDate: '2026-04-08', finishDate: '2026-04-12', timeFrame: 'past' }, alreadyImported: false },
];

export const DISCOVERY_AREAS: DiscoveryNode[] = [
  { providerNodeId: 'ado:area:1', path: 'Atlas', name: 'Atlas', hasChildren: true, depth: 0, attributes: {}, alreadyImported: true, workItemCount: 312 },
  { providerNodeId: 'ado:area:14', path: 'Atlas\\Updates', name: 'Updates', hasChildren: true, depth: 1, attributes: {}, alreadyImported: true, workItemCount: 142 },
  { providerNodeId: 'ado:area:21', path: 'Atlas\\Updates\\IDP', name: 'IDP', hasChildren: false, depth: 2, attributes: {}, alreadyImported: true, workItemCount: 68 },
  { providerNodeId: 'ado:area:22', path: 'Atlas\\Updates\\Failover', name: 'Failover', hasChildren: false, depth: 2, attributes: {}, alreadyImported: true, workItemCount: 41 },
  { providerNodeId: 'ado:area:23', path: 'Atlas\\Updates\\Telemetry', name: 'Telemetry', hasChildren: false, depth: 2, attributes: {}, alreadyImported: false, workItemCount: 33 },
  { providerNodeId: 'ado:area:24', path: 'Atlas\\Updates\\Migration', name: 'Migration', hasChildren: false, depth: 2, attributes: {}, alreadyImported: false, workItemCount: 27 },
  { providerNodeId: 'ado:area:18', path: 'Atlas\\Portal', name: 'Portal', hasChildren: true, depth: 1, attributes: {}, alreadyImported: true, workItemCount: 96 },
  { providerNodeId: 'ado:area:34', path: 'Atlas\\Portal\\Onboarding', name: 'Onboarding', hasChildren: false, depth: 2, attributes: {}, alreadyImported: true, workItemCount: 28 },
  { providerNodeId: 'ado:area:35', path: 'Atlas\\Portal\\Billing', name: 'Billing', hasChildren: false, depth: 2, attributes: {}, alreadyImported: false, workItemCount: 41 },
  { providerNodeId: 'ado:area:36', path: 'Atlas\\Portal\\Reports', name: 'Reports', hasChildren: false, depth: 2, attributes: {}, alreadyImported: false, workItemCount: 27 },
  { providerNodeId: 'ado:area:7', path: 'Atlas\\Runtime', name: 'Runtime', hasChildren: true, depth: 1, attributes: {}, alreadyImported: true, workItemCount: 74 },
  { providerNodeId: 'ado:area:42', path: 'Atlas\\Runtime\\Scheduler', name: 'Scheduler', hasChildren: false, depth: 2, attributes: {}, alreadyImported: false, workItemCount: 19 },
  { providerNodeId: 'ado:area:43', path: 'Atlas\\Runtime\\Networking', name: 'Networking', hasChildren: false, depth: 2, attributes: {}, alreadyImported: false, workItemCount: 24 },
];

// ── Repo registry (matches forge sample data shape) ───────────────────
// Each repo carries productId so the import-from-ADO dropdown can scope
// to the product the SprintManager is currently editing (Q15.1, Q5).
export interface SprintRepoOption {
  id: string;
  productId: string;
  name: string;
  kind: ProviderKind;
  org: string;
  project: string;
}
export const REPOS: SprintRepoOption[] = [
  { id: 'r-update',  productId: 'atlas',       name: 'update',  kind: 'ado',    org: 'contoso',    project: 'Atlas' },
  { id: 'r-portal',  productId: 'atlas',       name: 'portal',  kind: 'github', org: 'atlas-corp', project: 'portal' },
  { id: 'r-runtime', productId: 'atlas',       name: 'runtime', kind: 'ado',    org: 'contoso',    project: 'Atlas' },
  { id: 'r-az-local',productId: 'azure-local', name: 'azure-local', kind: 'ado', org: 'contoso',   project: 'AzureLocal' },
];

/** Repos that belong to the supplied product. Used by every dropdown that
 * needs to honor product scope (iteration import, sprint manager, etc.). */
export function reposForProduct(productId: string): SprintRepoOption[] {
  return REPOS.filter((r) => r.productId === productId);
}

// ── Lookup helpers ────────────────────────────────────────────────────
export function activeIteration(): ProductIteration | null {
  return PRODUCT_ITERATIONS.find((i) => i.state === 'current' && i.bindings.some((b) => b.role === 'current'))
    ?? null;
}
export function defaultAreaPath(): ProductAreaPath | null {
  return PRODUCT_AREA_PATHS.find((a) => a.isDefaultFilter) ?? null;
}

// ── Date formatters ───────────────────────────────────────────────────
export function fmtDate(s: string | null | undefined): string {
  if (!s) return '—';
  const d = new Date(s);
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}
export function fmtDateRange(start: string | null, finish: string | null): string {
  if (!start && !finish) return '—';
  if (start && finish) return `${fmtDate(start)} – ${fmtDate(finish)}`;
  return start ? `from ${fmtDate(start)}` : `until ${fmtDate(finish)}`;
}
