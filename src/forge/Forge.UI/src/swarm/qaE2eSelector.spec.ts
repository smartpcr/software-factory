// docs/forge/e2e.md Stage 8.1 — pure-logic vitest covering the
// impl ↔ qa-e2e pairing selector. The phase-board's stacked-row
// rendering reads directly from this Map, so a regression in the
// pairing rules would break the operator-visible layout. Pure
// function means no DOM / no React; the visual close-the-loop
// happens in the sibling Playwright spec.

import { describe, expect, it } from 'vitest';
import type { StorySpawnWorkstreamSummary } from '../api/forgeApi';
import { qaE2eByImplId, QA_E2E_ANCHOR_SUFFIX, QA_E2E_CATEGORY, isQaE2e } from './qaE2eSelector';

function ws(
  id: string,
  overrides: Partial<StorySpawnWorkstreamSummary> = {},
): StorySpawnWorkstreamSummary {
  return {
    id,
    name: id,
    branch: `ws/${id}`,
    status: 'planning',
    cards: [],
    prs: [],
    deps: [],
    ...overrides,
  };
}

describe('qaE2eByImplId', () => {
  // ── 1. [ui-unit] Selector returns the qa-e2e ws for an impl ws when one exists ──

  it('pairs a qa-e2e ws to its impl sibling via the /e2e anchor suffix', () => {
    const impl = ws('ws-foundation-setup', {
      category: 'impl',
      planAnchor: 'phase-foundation/stage-setup',
    });
    const qa = ws('ws-foundation-setup-e2e', {
      category: QA_E2E_CATEGORY,
      planAnchor: 'phase-foundation/stage-setup' + QA_E2E_ANCHOR_SUFFIX,
    });

    const map = qaE2eByImplId([impl, qa]);

    expect(map.size).toBe(1);
    expect(map.get(impl.id)).toBe(qa);
  });

  it('pairs a delta-split impl (anchor carries `@{hash}`) with its bare-anchor qa-e2e', () => {
    // Regression: editing a SHIPPED stage's plan content makes
    // PlanReconciler "split for delta" — it re-anchors the merged impl
    // row to `{anchor}@{hash}` while the qa-e2e keeps the bare anchor.
    // Exact-equality matching orphaned the e2e (rendered neither
    // standalone nor stacked → vanished from the phase board). The
    // selector must match on the base anchor so the pair survives.
    const impl = ws('ws-identity-worker-adopts', {
      category: 'impl',
      planAnchor: 'phase-identity/stage-worker-adopts@005e8f66',
    });
    const qa = ws('ws-identity-worker-adopts-e2e', {
      category: QA_E2E_CATEGORY,
      planAnchor: 'phase-identity/stage-worker-adopts' + QA_E2E_ANCHOR_SUFFIX,
    });

    const map = qaE2eByImplId([impl, qa]);

    expect(map.size).toBe(1);
    expect(map.get(impl.id)).toBe(qa);
  });

  it('skips qa-e2e rows whose planAnchor is missing OR lacks the /e2e suffix (orphan guard)', () => {
    // Reconciler-side bug or in-flight legacy data — the selector
    // refuses to pair, leaving the impl row unaccompanied (avoids a
    // silently wrong stacking). Both rows still surface in the
    // workstream list (the consumer just won't get a Map entry).
    const impl = ws('ws-foundation-setup', {
      category: 'impl',
      planAnchor: 'phase-foundation/stage-setup',
    });
    const orphanNoAnchor = ws('ws-orphan-no-anchor-e2e', {
      category: QA_E2E_CATEGORY,
      planAnchor: null,
    });
    const orphanWrongSuffix = ws('ws-orphan-wrong-suffix-e2e', {
      category: QA_E2E_CATEGORY,
      planAnchor: 'phase-foundation/stage-other-thing',
    });

    const map = qaE2eByImplId([impl, orphanNoAnchor, orphanWrongSuffix]);

    expect(map.size).toBe(0);
    expect(map.get(impl.id)).toBeUndefined();
  });

  it('pairs three stages: two with qa-e2e, one without — and the lone impl returns undefined', () => {
    // Mirrors the Stage 8.1 Playwright scenario: a 3-stage plan
    // where stage A + stage C have qa-e2e siblings but stage B
    // doesn't. The selector returns a Map with 2 entries (A → qaA,
    // C → qaC); `.get(implB.id)` returns undefined and the
    // phase-board's empty-state rule renders no placeholder.
    const implA = ws('ws-impl-a', { category: 'impl', planAnchor: 'phase-foundation/stage-a' });
    const qaA = ws('ws-impl-a-e2e', { category: QA_E2E_CATEGORY, planAnchor: 'phase-foundation/stage-a/e2e' });
    const implB = ws('ws-impl-b', { category: 'impl', planAnchor: 'phase-foundation/stage-b' });
    const implC = ws('ws-impl-c', { category: 'impl', planAnchor: 'phase-ingest/stage-c' });
    const qaC = ws('ws-impl-c-e2e', { category: QA_E2E_CATEGORY, planAnchor: 'phase-ingest/stage-c/e2e' });

    const map = qaE2eByImplId([implA, qaA, implB, implC, qaC]);

    expect(map.size).toBe(2);
    expect(map.get(implA.id)?.id).toBe('ws-impl-a-e2e');
    expect(map.get(implC.id)?.id).toBe('ws-impl-c-e2e');
    // Stage B has no qa-e2e sibling.
    expect(map.get(implB.id)).toBeUndefined();
  });

  // ── 2. [ui-unit] Selector returns undefined for an impl ws with no qa-e2e sibling ──

  it('returns an empty map when no qa-e2e workstreams are present', () => {
    // A spawn whose plan has zero `### Test Scenarios` sections won't
    // get any qa-e2e rows from the decomposer (Stage 3.1's contract).
    // The selector must safely return an empty map; the phase-board
    // then renders only impl cards with no stacked siblings.
    const impl1 = ws('ws-only-impl-1', { category: 'impl', planAnchor: 'phase-x/stage-1' });
    const impl2 = ws('ws-only-impl-2', { category: 'impl', planAnchor: 'phase-x/stage-2' });

    const map = qaE2eByImplId([impl1, impl2]);

    expect(map.size).toBe(0);
    expect(map.get(impl1.id)).toBeUndefined();
    expect(map.get(impl2.id)).toBeUndefined();
  });

  it('treats null / undefined category as impl (legacy-row fallback)', () => {
    // Pre-Phase79Migration rows have no Category column populated;
    // the backend defaults to "impl" via the entity property but
    // the predicate must also tolerate explicit null/undefined so
    // a partially-deserialised payload doesn't crash the selector.
    const legacyNullCategory = ws('ws-legacy-null', {
      category: null,
      planAnchor: 'phase-x/stage-y',
    });
    const legacyUndefinedCategory = ws('ws-legacy-undef', {
      planAnchor: 'phase-x/stage-z',
    });
    // Neither is qa-e2e; selector returns empty map.
    expect(qaE2eByImplId([legacyNullCategory, legacyUndefinedCategory]).size).toBe(0);

    expect(isQaE2e(legacyNullCategory)).toBe(false);
    expect(isQaE2e(legacyUndefinedCategory)).toBe(false);
    expect(isQaE2e(ws('q', { category: QA_E2E_CATEGORY }))).toBe(true);
  });
});
