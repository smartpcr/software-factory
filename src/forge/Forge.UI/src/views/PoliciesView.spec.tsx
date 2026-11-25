// Phase 108 — the Rules & guardrails page renders the real rule catalog:
// rule cards say how a rule is attached and where it is ticked by hand; tag
// cards say what they match and which rules ride on them.

import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import type { RuleCatalog } from '@/api/forgeApi';
import { RuleCard, TagCard } from './PoliciesView';

const at = '2026-11-25T12:00:00Z';
const catalog: RuleCatalog = {
  productId: 'harness',
  tags: [{
    id: 't-gate', name: 'full harness gate', description: 'Needs the full gate or a lane run',
    repoIds: ['agent-plugin'], categories: [], phrases: ['both lanes'], patterns: ['lanes/(win|wsl)'],
    createdAt: at, createdBy: 'u', updatedAt: at,
  }],
  rules: [
    { id: 'r1', title: 'Long-running checks', text: 'Never run the full gate in an iteration.', tagIds: ['t-gate'], enabled: true, createdAt: at, createdBy: 'u', updatedAt: at },
    { id: 'r2', title: 'Use the fake spawner', text: 'x', tagIds: [], enabled: false, createdAt: at, createdBy: 'u', updatedAt: at },
  ],
  usage: { r2: { attached: 3, detached: 0 }, r1: { attached: 0, detached: 2 } },
};

describe('RuleCard', () => {
  it('shows a tagged rule as automatic, with its tag and hand overrides', () => {
    const out = renderToStaticMarkup(<RuleCard rule={catalog.rules[0]} catalog={catalog} onToggle={() => undefined} onEdit={() => undefined} />);
    expect(out).toContain('data-kind="tagged"');
    expect(out).toContain('auto');
    expect(out).toContain('tag full harness gate matches');
    expect(out).toContain('unticked on <span class="num">2</span>');
    expect(out).toContain('aria-pressed="true"');
    expect(out).toContain('Long-running checks: enabled, click to disable');
  });

  it('shows a hand-picked rule, disabled, with where it is ticked', () => {
    const out = renderToStaticMarkup(<RuleCard rule={catalog.rules[1]} catalog={catalog} onToggle={() => undefined} />);
    expect(out).toContain('data-kind="manual"');
    expect(out).toContain('by hand');
    expect(out).toContain('ticked on a work item');
    expect(out).toContain('ticked on <span class="num">3</span>');
    expect(out).toContain('disabled');
    expect(out).toContain('aria-pressed="false"');
  });
});

describe('TagCard', () => {
  it('shows scope, matchers and the rules on the tag', () => {
    const out = renderToStaticMarkup(<TagCard tag={catalog.tags[0]} catalog={catalog} onCheck={() => undefined} />);
    expect(out).toContain('agent-plugin · every workstream');
    expect(out).toContain('text has &quot;both lanes&quot; | /lanes/(win|wsl)/');
    expect(out).toContain('1 rule: Long-running checks');
    expect(out).toContain('Check matches');
  });

  it('lists the workstreams a checked tag matches', () => {
    const out = renderToStaticMarkup(
      <TagCard tag={catalog.tags[0]} catalog={catalog} matches={[
        { workstreamId: 'ws-1', name: 'Launch Plan', category: 'impl', status: 'active', applied: true, source: 'matched', reason: 'matched "both lanes"' },
        { workstreamId: 'ws-2', name: 'Docs', category: 'impl', status: 'complete', applied: false, source: 'no-match', reason: 'no phrase' },
      ]} />,
    );
    expect(out).toContain('Matches 1 of 2 workstreams in this product');
    expect(out).toContain('Launch Plan');
    expect(out).not.toContain('>Docs<');
  });
});