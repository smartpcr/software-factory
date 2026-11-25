// Phase 108 — the Rules & guardrails page: policy cards list their rules and
// how they apply; rule cards say which policies contain them; tag cards say
// what they match and which policies they apply.

import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import type { RuleCatalog } from '@/api/forgeApi';
import { PolicyCard, RuleCard, TagCard } from './PoliciesView';

const at = '2026-11-25T12:00:00Z';
const meta = { createdAt: at, createdBy: 'u', updatedAt: at };
const catalog: RuleCatalog = {
  productId: 'harness',
  tags: [{
    id: 't-gate', name: 'full harness gate', description: 'Needs the full gate or a lane run',
    repoIds: ['agent-plugin'], categories: [], phrases: ['both lanes'], patterns: ['lanes/(win|wsl)'], ...meta,
  }],
  rules: [
    { id: 'r-long', title: 'Long-running checks', text: 'Never run the full gate in an iteration.', enabled: true, ...meta },
    { id: 'r-vet', title: 'Scoped vet', text: 'Vet only changed packages.', enabled: false, ...meta },
    { id: 'r-fake', title: 'Use the fake spawner', text: 'x', enabled: true, ...meta },
  ],
  policies: [
    { id: 'p-gate', name: 'Harness gate', description: 'Gate work', ruleIds: ['r-long', 'r-vet'], tagIds: ['t-gate'], enabled: true, ...meta },
    { id: 'p-lab', name: 'Lab E2E', description: '', ruleIds: ['r-long'], tagIds: [], enabled: false, ...meta },
  ],
  ruleUsage: { 'r-fake': { attached: 3, detached: 0 } },
  policyUsage: { 'p-gate': { attached: 1, detached: 2 } },
};

describe('PolicyCard', () => {
  it('lists the policy rules in order and how it applies', () => {
    const out = renderToStaticMarkup(<PolicyCard policy={catalog.policies[0]} catalog={catalog} onToggle={() => undefined} />);
    expect(out).toContain('Harness gate');
    expect(out).toContain('<li>Long-running checks</li>');
    expect(out).toContain('Scoped vet<span class="policy-card-off"> (off)</span>');
    expect(out).toContain('where tag full harness gate matches, or where picked');
    expect(out).toContain('picked on <span class="num">1</span> work item');
    expect(out).toContain('removed on <span class="num">2</span>');
    expect(out).toContain('Harness gate: on, click to turn off');
  });

  it('shows a hand-only policy that is off', () => {
    const out = renderToStaticMarkup(<PolicyCard policy={catalog.policies[1]} catalog={catalog} onToggle={() => undefined} />);
    expect(out).toContain('by hand');
    expect(out).toContain('where picked on a work item');
    expect(out).toContain('aria-pressed="false"');
  });
});

describe('RuleCard', () => {
  it('names the policies that contain the rule', () => {
    expect(renderToStaticMarkup(<RuleCard rule={catalog.rules[0]} catalog={catalog} />)).toContain('in Harness gate, Lab E2E');
  });

  it('shows a rule in no policy and where it is picked on its own', () => {
    const out = renderToStaticMarkup(<RuleCard rule={catalog.rules[2]} catalog={catalog} />);
    expect(out).toContain('in no policy');
    expect(out).toContain('picked on <span class="num">3</span> work items');
  });
});

describe('TagCard', () => {
  it('shows scope, matchers and the policies it applies', () => {
    const out = renderToStaticMarkup(<TagCard tag={catalog.tags[0]} catalog={catalog} onCheck={() => undefined} />);
    expect(out).toContain('agent-plugin · every workstream');
    expect(out).toContain('text has &quot;both lanes&quot; or /lanes/(win|wsl)/');
    expect(out).toContain('applies Harness gate');
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