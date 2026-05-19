// docs/forge/e2e.md Stage 8.2 — pin the edge-dataset shape the
// workstream DAG view consumes. The spec's headline contract is:
//
//   "Edge dataset for a 2-stage story has exactly N edges where
//    N = (deps between impls) + (impl→qa-e2e per qa-e2e stage)."
//
// A typical 2-stage story with 1 inter-impl dep + both stages
// carrying a qa-e2e sibling resolves to N = 1 + 2 = 3.

import { describe, expect, it } from 'vitest';
import type { StorySpawnWorkstreamSummary } from '../api/forgeApi';
import { buildEdgeDataset, edgesOfKind } from './dagEdges';
import { QA_E2E_CATEGORY } from './qaE2eSelector';

function impl(
  id: string,
  planAnchor: string,
  deps: string[] = [],
): StorySpawnWorkstreamSummary {
  return {
    id,
    name: id,
    branch: `ws/${id}`,
    status: 'planning',
    cards: [],
    prs: [],
    deps,
    category: 'impl',
    planAnchor,
    phaseAnchor: planAnchor.split('/')[0],
  };
}

function qaE2e(
  id: string,
  implSiblingAnchor: string,
  extraDeps: string[] = [],
  implSiblingId?: string,
): StorySpawnWorkstreamSummary {
  return {
    id,
    name: id,
    branch: `ws/${id}`,
    status: 'planning',
    cards: [],
    prs: [],
    // First dep is the impl sibling's id (matches Stage 3.1 +
    // PersistDecompositionAsync's title→id resolution); subsequent
    // deps are cross-stage upstreams from the Stage 2.1 H3 parser.
    deps: implSiblingId ? [implSiblingId, ...extraDeps] : [...extraDeps],
    category: QA_E2E_CATEGORY,
    planAnchor: implSiblingAnchor + '/e2e',
    phaseAnchor: implSiblingAnchor.split('/')[0],
  };
}

describe('buildEdgeDataset', () => {
  // ── 1. Headline assertion: 2-stage story → exactly 3 edges ──────────

  it('a 2-stage story (1 impl-dep + 2 qa-e2e siblings) yields exactly 3 edges', () => {
    // Stage A (impl) — no upstream deps; carries a qa-e2e sibling.
    // Stage B (impl) — depends on Stage A; carries a qa-e2e sibling.
    const implA = impl('ws-a', 'phase-foundation/stage-a');
    const qaA = qaE2e('ws-a-e2e', 'phase-foundation/stage-a', [], 'ws-a');
    const implB = impl('ws-b', 'phase-foundation/stage-b', ['ws-a']);
    const qaB = qaE2e('ws-b-e2e', 'phase-foundation/stage-b', [], 'ws-b');

    const edges = buildEdgeDataset([implA, qaA, implB, qaB]);

    // The headline assertion the Stage 8.2 spec hardcodes.
    expect(edges).toHaveLength(3);

    // Each edge classified correctly.
    expect(edgesOfKind(edges, 'impl-dep')).toHaveLength(1);
    expect(edgesOfKind(edges, 'impl-to-qae2e')).toHaveLength(2);
    expect(edgesOfKind(edges, 'cross-stage-e2e')).toHaveLength(0);

    // Edge endpoints — direction is `from = dep target` (upstream),
    // `to = the row that declared the dep` (downstream).
    const implDep = edgesOfKind(edges, 'impl-dep')[0];
    expect(implDep).toMatchObject({ from: 'ws-a', to: 'ws-b' });

    const qaEdges = edgesOfKind(edges, 'impl-to-qae2e');
    const qaPairs = qaEdges.map((e) => `${e.from}->${e.to}`).sort();
    expect(qaPairs).toEqual(['ws-a->ws-a-e2e', 'ws-b->ws-b-e2e']);
  });

  // ── 2. Cross-stage e2e deps classify as `cross-stage-e2e`, not `impl-to-qae2e` ──

  it('a qa-e2e with an extra cross-stage upstream dep emits one impl-to-qae2e + one cross-stage-e2e edge', () => {
    // Mirrors a Stage 2.1 H3 declaration: `### Cross-Stage
    // Dependencies` lists `phase-foundation/stage-a`, so the qa-e2e
    // for stage B depends on BOTH its impl sibling (ws-b) AND an
    // upstream impl (ws-a).
    const implA = impl('ws-a', 'phase-foundation/stage-a');
    const implB = impl('ws-b', 'phase-foundation/stage-b');
    const qaB = qaE2e('ws-b-e2e', 'phase-foundation/stage-b', ['ws-a'], 'ws-b');

    const edges = buildEdgeDataset([implA, implB, qaB]);

    // Total = (impl-to-qae2e, ws-b → ws-b-e2e) + (cross-stage-e2e, ws-a → ws-b-e2e) = 2.
    expect(edges).toHaveLength(2);
    expect(edgesOfKind(edges, 'impl-to-qae2e')).toHaveLength(1);
    expect(edgesOfKind(edges, 'cross-stage-e2e')).toHaveLength(1);

    const crossStage = edgesOfKind(edges, 'cross-stage-e2e')[0];
    expect(crossStage).toMatchObject({ from: 'ws-a', to: 'ws-b-e2e' });
    const implToQa = edgesOfKind(edges, 'impl-to-qae2e')[0];
    expect(implToQa).toMatchObject({ from: 'ws-b', to: 'ws-b-e2e' });
  });

  // ── 3. Lone-impl stage (no qa-e2e sibling) contributes no impl→qae2e edge ──

  it('a 3-stage story (2 with qa-e2e, 1 without) emits exactly (impl-deps) + 2 impl-to-qae2e edges', () => {
    // Mirrors the Stage 8.1 Playwright fixture: stages A + C have
    // qa-e2e siblings, stage B doesn't. Sequential impl deps A → B → C.
    const implA = impl('ws-a', 'phase-foundation/stage-a');
    const qaA = qaE2e('ws-a-e2e', 'phase-foundation/stage-a', [], 'ws-a');
    const implB = impl('ws-b', 'phase-foundation/stage-b', ['ws-a']);
    const implC = impl('ws-c', 'phase-ingest/stage-c', ['ws-b']);
    const qaC = qaE2e('ws-c-e2e', 'phase-ingest/stage-c', [], 'ws-c');

    const edges = buildEdgeDataset([implA, qaA, implB, implC, qaC]);

    // 2 impl-deps (A→B, B→C) + 2 impl-to-qae2e (A→qaA, C→qaC) = 4.
    expect(edges).toHaveLength(4);
    expect(edgesOfKind(edges, 'impl-dep')).toHaveLength(2);
    expect(edgesOfKind(edges, 'impl-to-qae2e')).toHaveLength(2);
    expect(edgesOfKind(edges, 'cross-stage-e2e')).toHaveLength(0);

    // Stage B has no qa-e2e sibling, so no edge terminates at a
    // ws-b-e2e id (that row doesn't exist in the spawn).
    expect(edges.some((e) => e.to === 'ws-b-e2e')).toBe(false);
  });

  // ── 4. Edges with unresolved deps are skipped (defensive) ──────────

  it('skips edges whose upstream dep id is not in the spawn workstream list', () => {
    // A typo / stale row id in the WorkstreamDeps table — the edge
    // must NOT appear in the dataset (no rendering target). The
    // resolved deps still surface chip-only, but the SVG overlay
    // cannot draw an arrow to a non-existent box.
    const implA = impl('ws-a', 'phase-foundation/stage-a', ['ws-MISSING']);
    const edges = buildEdgeDataset([implA]);
    expect(edges).toHaveLength(0);
  });

  // ── 5. Empty spawn → empty dataset ─────────────────────────────────

  it('returns an empty list for an empty spawn', () => {
    expect(buildEdgeDataset([])).toEqual([]);
  });
});
