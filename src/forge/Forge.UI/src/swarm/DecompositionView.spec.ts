// Phase 46 — pure-logic vitest covering DecompositionView's helpers.
// The component itself is mostly conditional JSX driven by these pure
// functions; locking them in here means we don't need a DOM to assert
// the grouping / floating / YAML-emit contracts.

import { describe, expect, it } from 'vitest';
import type { StorySpawnDecomposeWorkstream } from '../api/forgeApi';
import { __test__ } from './DecompositionView';

const { groupByPhase, detectFloating, toYaml, phaseLabelFromAnchor, DEFAULT_PHASE_ANCHOR } = __test__;

function ws(
  title: string,
  overrides: Partial<StorySpawnDecomposeWorkstream> = {},
): StorySpawnDecomposeWorkstream & { ix: number } {
  return {
    title,
    description: undefined,
    deps: [],
    ...overrides,
    ix: 0,
  } as StorySpawnDecomposeWorkstream & { ix: number };
}

describe('groupByPhase', () => {
  it('buckets cards by phaseAnchor preserving first-seen order', () => {
    const items = [
      ws('A', { phaseAnchor: 'phase-foundation' }),
      ws('B', { phaseAnchor: 'phase-build' }),
      ws('C', { phaseAnchor: 'phase-foundation' }),
    ].map((w, ix) => ({ ...w, ix }));

    const groups = groupByPhase(items);

    expect(groups.map((g) => g.anchor)).toEqual(['phase-foundation', 'phase-build']);
    expect(groups[0].items).toHaveLength(2);
    expect(groups[1].items).toHaveLength(1);
  });

  it('falls back to the default anchor when phaseAnchor is missing (legacy spawns)', () => {
    const items = [ws('A'), ws('B')].map((w, ix) => ({ ...w, ix }));

    const groups = groupByPhase(items);

    expect(groups).toHaveLength(1);
    expect(groups[0].anchor).toBe(DEFAULT_PHASE_ANCHOR);
    expect(groups[0].label).toBe('Workstreams');
  });
});

describe('phaseLabelFromAnchor', () => {
  it('strips the phase- prefix and Title-cases kebab segments', () => {
    expect(phaseLabelFromAnchor('phase-memory-schema-and-foundation'))
      .toBe('Memory Schema And Foundation');
  });

  it('returns "Workstreams" for the default ungrouped anchor', () => {
    expect(phaseLabelFromAnchor(DEFAULT_PHASE_ANCHOR)).toBe('Workstreams');
  });

  it('handles anchors without the phase- prefix gracefully', () => {
    expect(phaseLabelFromAnchor('foo-bar')).toBe('Foo Bar');
  });
});

describe('detectFloating', () => {
  it('flags a card with no upstream and no downstream', () => {
    const items = [ws('A'), ws('B')].map((w, ix) => ({ ...w, ix }));
    expect(detectFloating(items[0], items)).toBe(true);
  });

  it('does not flag a card with an upstream', () => {
    const items = [
      ws('A'),
      ws('B', { deps: ['A'] }),
    ].map((w, ix) => ({ ...w, ix }));
    expect(detectFloating(items[1], items)).toBe(false);
  });

  it('does not flag a card with a downstream (someone depends on it)', () => {
    const items = [
      ws('A'),
      ws('B', { deps: ['A'] }),
    ].map((w, ix) => ({ ...w, ix }));
    expect(detectFloating(items[0], items)).toBe(false);
  });
});

describe('toYaml', () => {
  it('emits the canonical workstreams.yaml shape', () => {
    const items: StorySpawnDecomposeWorkstream[] = [
      {
        title: 'Setup',
        description: 'kick-off',
        acceptanceCriteria: ['ok'],
        deps: [],
        deliverables: ['foo.cs'],
        timeline: 'day 1',
        estimatedEffort: 'S',
        phaseAnchor: 'phase-foundation',
      },
    ];

    const yaml = toYaml(items);

    expect(yaml).toContain('- title: Setup');
    expect(yaml).toContain('  description: kick-off');
    expect(yaml).toContain('  acceptanceCriteria:');
    expect(yaml).toContain('    - ok');
    expect(yaml).toContain('  deliverables:');
    expect(yaml).toContain('    - foo.cs');
    expect(yaml).toContain('  timeline: day 1');
    expect(yaml).toContain('  estimatedEffort: S');
    expect(yaml).toContain('  phaseAnchor: phase-foundation');
  });

  it('omits empty / missing fields so the output is clean', () => {
    const items: StorySpawnDecomposeWorkstream[] = [
      { title: 'Tiny', deps: [] },
    ];

    const yaml = toYaml(items);

    expect(yaml).toContain('- title: Tiny');
    expect(yaml).not.toContain('description:');
    expect(yaml).not.toContain('acceptanceCriteria:');
    expect(yaml).not.toContain('deps:');
  });

  it('quotes scalars containing colons or starting with a digit', () => {
    const items: StorySpawnDecomposeWorkstream[] = [
      { title: 'foo: bar', deps: [] },
      { title: '12 stages', deps: [] },
    ];

    const yaml = toYaml(items);

    expect(yaml).toContain('- title: "foo: bar"');
    expect(yaml).toContain('- title: "12 stages"');
  });

  it('emits unresolvedDeps so the operator sees them in the YAML view', () => {
    const items: StorySpawnDecomposeWorkstream[] = [
      { title: 'Wire', deps: [], unresolvedDeps: ['phase-typo'] },
    ];

    const yaml = toYaml(items);

    expect(yaml).toContain('  unresolvedDeps:');
    expect(yaml).toContain('    - phase-typo');
  });
});
