// docs/forge/e2e.md Stage 8.1 — pair an impl workstream with its trailing
// qa-e2e companion. The reconciler-side contract:
//
//   • Impl workstream's `planAnchor` is `phase-{phaseSlug}/stage-{stageSlug}`.
//   • qa-e2e workstream's `planAnchor` is the same plus the
//     `/e2e` suffix (see `CrewStoryArtifactStrategy.QaE2eAnchorSuffix`
//     on the backend).
//   • qa-e2e workstreams carry `category === 'qa-e2e'` (set by
//     `WorkstreamCategories.QaE2e` server-side; added by Stage 1.1
//     via Phase79Migration).
//
// The selector returns a Map keyed on the IMPL workstream's id so the
// phase-board render can call `qaE2eByImplId.get(impl.id)` per impl row
// to discover its qa-e2e sibling (or undefined when there is none —
// the impl renders standalone in that case, with NO empty placeholder
// per the Stage 8.1 empty-state rule).
//
// Pure function so a vitest unit lock-in covers the pairing contract
// without standing up a DOM; exported as a module-scope function
// (instead of inside `useSpawnStore`) because the workstream data
// flows through props from `<StoryCard>` — `useSpawnStore` only owns
// the realtime tick.

import type { StorySpawnWorkstreamSummary } from '@/api/forgeApi';

/** docs/forge/e2e.md Stage 8.1 — anchor suffix the backend appends to
 *  pair a qa-e2e workstream with its impl sibling. Mirrors
 *  `CrewStoryArtifactStrategy.QaE2eAnchorSuffix` on the server. */
export const QA_E2E_ANCHOR_SUFFIX = '/e2e';

/** docs/forge/e2e.md Stage 8.1 — discriminator value for qa-e2e rows. */
export const QA_E2E_CATEGORY = 'qa-e2e';

/**
 * docs/forge/e2e.md Stage 8.1 — pair every qa-e2e workstream with its
 * impl sibling by stripping the `/e2e` suffix from the qa-e2e row's
 * `planAnchor` and looking up an impl row whose `planAnchor` matches.
 *
 * Returns a `Map<implWorkstreamId, qaE2eWorkstream>`. Impl workstreams
 * without a qa-e2e sibling are absent from the map (the phase-board
 * skips the stacked row in that case — no empty placeholder).
 *
 * Edge cases:
 *   • qa-e2e row without `planAnchor` → skipped (no anchor to pair).
 *   • qa-e2e row whose stripped anchor doesn't match any impl row →
 *     skipped (orphan; reconciler should have caught this).
 *   • Multiple qa-e2e rows pointing at the same impl anchor → last
 *     one wins (impossible in practice — reconciler enforces 1:1).
 */
export function qaE2eByImplId(
  workstreams: ReadonlyArray<StorySpawnWorkstreamSummary>,
): Map<string, StorySpawnWorkstreamSummary> {
  const result = new Map<string, StorySpawnWorkstreamSummary>();
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
    if (impl) result.set(impl.id, w);
  }
  return result;
}

/** docs/forge/e2e.md Stage 8.1 — predicate. Treats `null` /
 *  `undefined` as "impl" so legacy rows without the Category column
 *  populated default to the impl bucket. */
export function isQaE2e(w: StorySpawnWorkstreamSummary): boolean {
  return w.category === QA_E2E_CATEGORY;
}
