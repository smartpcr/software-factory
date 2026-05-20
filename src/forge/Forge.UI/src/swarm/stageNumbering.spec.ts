// Pure-logic vitest for stage numbering. Locks in the docs/forge/e2e.md
// Stage 8.1 fix: qa-e2e companion rows must NOT consume a positional stage
// slot (the bug that rendered `stage 1.1 → stage 1.1-e2e → stage 1.3`,
// skipping 1.2). Both the phase board and the workstream DAG read this map,
// so a regression here is operator-visible in two places.

import { describe, expect, it } from 'vitest';
import type { StorySpawnWorkstreamSummary } from '../api/forgeApi';
import { computeStageNumbers } from './stageNumbering';

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

function impl(id: string, phase: string, anchor: string): StorySpawnWorkstreamSummary {
  return ws(id, { phaseAnchor: phase, planAnchor: anchor, category: 'impl' });
}

function e2e(id: string, phase: string, implAnchor: string): StorySpawnWorkstreamSummary {
  return ws(id, { phaseAnchor: phase, planAnchor: `${implAnchor}/e2e`, category: 'qa-e2e' });
}

describe('computeStageNumbers', () => {
  it('numbers impl stages sequentially even when qa-e2e rows are interleaved', () => {
    // The exact operator-reported shape: impl, its e2e, impl, its e2e, impl.
    const list = [
      impl('a', 'phase-1', 'phase-1/stage-1.1'),
      e2e('a-e2e', 'phase-1', 'phase-1/stage-1.1'),
      impl('b', 'phase-1', 'phase-1/stage-1.2'),
      e2e('b-e2e', 'phase-1', 'phase-1/stage-1.2'),
      impl('c', 'phase-1', 'phase-1/stage-1.3'),
    ];
    const n = computeStageNumbers(list);
    expect(n.get('a')).toBe('1.1');
    expect(n.get('b')).toBe('1.2'); // was '1.3' before the fix
    expect(n.get('c')).toBe('1.3');
  });

  it('labels qa-e2e rows as {implNum}-e2e', () => {
    const list = [
      impl('a', 'phase-1', 'phase-1/stage-1.1'),
      e2e('a-e2e', 'phase-1', 'phase-1/stage-1.1'),
    ];
    const n = computeStageNumbers(list);
    expect(n.get('a-e2e')).toBe('1.1-e2e');
  });

  it('increments the phase number per distinct phaseAnchor (first-seen order)', () => {
    const list = [
      impl('a', 'phase-1', 'phase-1/stage-1.1'),
      impl('b', 'phase-2', 'phase-2/stage-2.1'),
      impl('c', 'phase-2', 'phase-2/stage-2.2'),
    ];
    const n = computeStageNumbers(list);
    expect(n.get('a')).toBe('1.1');
    expect(n.get('b')).toBe('2.1');
    expect(n.get('c')).toBe('2.2');
  });

  it('numbers impl stages independent of where the e2e rows sit in the array', () => {
    // e2e rows trailing at the end (the legacy "rightmost column" layout)
    // must still leave impl numbering contiguous.
    const list = [
      impl('a', 'phase-1', 'phase-1/stage-1.1'),
      impl('b', 'phase-1', 'phase-1/stage-1.2'),
      e2e('a-e2e', 'phase-1', 'phase-1/stage-1.1'),
      e2e('b-e2e', 'phase-1', 'phase-1/stage-1.2'),
    ];
    const n = computeStageNumbers(list);
    expect(n.get('a')).toBe('1.1');
    expect(n.get('b')).toBe('1.2');
    expect(n.get('a-e2e')).toBe('1.1-e2e');
    expect(n.get('b-e2e')).toBe('1.2-e2e');
  });

  it('buckets workstreams without a phaseAnchor under a single phase', () => {
    const list = [ws('a'), ws('b')];
    const n = computeStageNumbers(list);
    expect(n.get('a')).toBe('1.1');
    expect(n.get('b')).toBe('1.2');
  });

  it('leaves an orphan qa-e2e row (no resolvable impl sibling) out of the map', () => {
    const list = [e2e('orphan', 'phase-1', 'phase-1/stage-9.9')];
    const n = computeStageNumbers(list);
    expect(n.has('orphan')).toBe(false);
  });
});
