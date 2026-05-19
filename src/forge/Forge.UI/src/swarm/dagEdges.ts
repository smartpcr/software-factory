// docs/forge/e2e.md Stage 8.2 — pure helper that projects the workstream
// list into a flat edge dataset for the workstream DAG view. Used by:
//
//   • The vitest lock-in (`dagEdges.spec.ts`) to assert exact edge counts
//     for canonical 2-stage / N-stage fixtures.
//   • Future consumers that need the edge contract independent of the
//     DOM-position pipeline (the current SVG overlay still iterates
//     `ws.deps` inline; this helper is the canonical recipe and a future
//     follow-up can wire the overlay through it).
//
// Edge taxonomy (kept stable so test assertions can filter by kind):
//
//   1. `impl-dep`          — an impl workstream depends on another impl
//                            workstream (the only edge kind before Stage
//                            8.1 / 8.2 — declared by the architect's
//                            workstreams.yaml `deps:` list, surfaced via
//                            the `WorkstreamDeps` table).
//   2. `impl-to-qae2e`     — a qa-e2e workstream depends on its impl
//                            sibling. Added by Stage 3.1's
//                            `BuildDecompositionFromPlan`, surfaced via
//                            the same dep table. The DAG view renders
//                            this as a short downward arrow from the
//                            impl card to its stacked qa-e2e child.
//   3. `cross-stage-e2e`   — a qa-e2e workstream depends on an upstream
//                            stage's impl row that is NOT its own impl
//                            sibling. Sourced from the Stage 2.1 H3
//                            `### Cross-Stage Dependencies` parser via
//                            `DecompositionDepsResolver`. Routed by the
//                            existing edge router as a long edge.
//
// Pairing rule for `impl-to-qae2e`: identical to the Stage 8.1 selector
// (`qaE2eByImplId`) — strip the `/e2e` suffix from the qa-e2e
// workstream's `planAnchor` and look up an impl workstream whose
// `planAnchor` matches. A qa-e2e whose impl sibling can't be resolved
// is treated as `cross-stage-e2e` for every dep (defensive — the
// reconciler should have caught this).

import type { StorySpawnWorkstreamSummary } from '@/api/forgeApi';
import { isQaE2e, QA_E2E_ANCHOR_SUFFIX } from './qaE2eSelector';

/** docs/forge/e2e.md Stage 8.2 — edge discriminator. */
export type EdgeKind = 'impl-dep' | 'impl-to-qae2e' | 'cross-stage-e2e';

/** docs/forge/e2e.md Stage 8.2 — one entry per dependency edge. */
export interface DagEdge {
  /** Upstream workstream id (the dep target — drawn at the arrow's
   *  source end visually since arrows point from dep → dependent). */
  from: string;
  /** Downstream workstream id (the row that declared the dep). */
  to: string;
  /** Discriminator — see file-level taxonomy. */
  kind: EdgeKind;
}

/**
 * docs/forge/e2e.md Stage 8.2 — build the edge dataset for a story
 * spawn. Iterates each workstream's `deps` array (already FK-resolved
 * by `GetSpawn` from the WorkstreamDeps table) and classifies each
 * edge by the source/target pair's category + anchor relationship.
 *
 * <strong>Edges with unresolved upstream ids are skipped silently</strong>
 * (the cross-phase chip rendering on the DAG card surfaces those as
 * `↥ deps` chips already).
 */
export function buildEdgeDataset(
  workstreams: ReadonlyArray<StorySpawnWorkstreamSummary>,
): DagEdge[] {
  const byId = new Map<string, StorySpawnWorkstreamSummary>();
  for (const w of workstreams) byId.set(w.id, w);

  // Pre-compute impl sibling lookup so each qa-e2e edge can be
  // classified in O(1): given a qa-e2e workstream + a dep id, decide
  // whether the dep is "my impl sibling" (→ `impl-to-qae2e`) or
  // anything else (→ `cross-stage-e2e`).
  const implSiblingIdByQaId = new Map<string, string>();
  const implByAnchor = new Map<string, StorySpawnWorkstreamSummary>();
  for (const w of workstreams) {
    if (isQaE2e(w)) continue;
    if (w.planAnchor) implByAnchor.set(w.planAnchor, w);
  }
  for (const w of workstreams) {
    if (!isQaE2e(w)) continue;
    if (!w.planAnchor || !w.planAnchor.endsWith(QA_E2E_ANCHOR_SUFFIX)) continue;
    const implAnchor = w.planAnchor.slice(0, -QA_E2E_ANCHOR_SUFFIX.length);
    const impl = implByAnchor.get(implAnchor);
    if (impl) implSiblingIdByQaId.set(w.id, impl.id);
  }

  const edges: DagEdge[] = [];
  for (const w of workstreams) {
    const myDeps = w.deps ?? [];
    for (const depId of myDeps) {
      // Skip deps that can't be resolved against the spawn's
      // workstream list (stale row referenced by id alone).
      if (!byId.has(depId)) continue;

      if (isQaE2e(w)) {
        const implSiblingId = implSiblingIdByQaId.get(w.id);
        if (implSiblingId && depId === implSiblingId) {
          edges.push({ from: depId, to: w.id, kind: 'impl-to-qae2e' });
        } else {
          edges.push({ from: depId, to: w.id, kind: 'cross-stage-e2e' });
        }
      } else {
        // Two impl-side cases: dep on another impl row (`impl-dep`) or
        // an impl row whose dep edge points at a qa-e2e (impossible
        // per the reconciler's invariants, but classify defensively
        // as `cross-stage-e2e` so a stale row doesn't crash the
        // consumer).
        const dep = byId.get(depId)!;
        if (isQaE2e(dep)) {
          edges.push({ from: depId, to: w.id, kind: 'cross-stage-e2e' });
        } else {
          edges.push({ from: depId, to: w.id, kind: 'impl-dep' });
        }
      }
    }
  }
  return edges;
}

/** docs/forge/e2e.md Stage 8.2 — convenience predicate filters. */
export function edgesOfKind(edges: ReadonlyArray<DagEdge>, kind: EdgeKind): DagEdge[] {
  return edges.filter((e) => e.kind === kind);
}
