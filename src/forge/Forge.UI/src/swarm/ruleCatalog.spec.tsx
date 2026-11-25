// Phase 108 — the product rule catalog UI: tag scope and matcher wording, the
// editor's list parsing, the catalog lists, and a workstream's tags and rules.

import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { MemoryRouter } from 'react-router-dom';
import type { RuleCatalog, RuleChoice, WorkItemRules } from '@/api/forgeApi';
import { items, lines, ruleAction, ruleStatus, tagChoice, tagMatchers, tagScope } from './ruleCatalogText';
import { RuleList, TagList } from './RuleCatalogView';
import { WorkItemRulesView } from './WorkItemRulesSection';

const at = '2026-11-25T12:00:00Z';
const catalog: RuleCatalog = {
  productId: 'harness',
  tags: [{
    id: 't-gate', name: 'full harness gate', description: 'Needs the full gate or a lane run',
    repoIds: ['agent-plugin'], categories: [], phrases: ['both lanes', 'tools/harness-gate'], patterns: ['lanes/(win|wsl)'],
    createdAt: at, createdBy: 'u', updatedAt: at,
  }],
  rules: [
    { id: 'r1', title: 'Long-running checks', text: 'Never run the full gate in an iteration.', tagIds: ['t-gate'], enabled: true, createdAt: at, createdBy: 'u', updatedAt: at },
    { id: 'r2', title: 'Old rule', text: 'x', tagIds: ['t-gate'], enabled: false, createdAt: at, createdBy: 'u', updatedAt: at },
  ],
};

describe('ruleCatalogText', () => {
  it('splits phrases by line and repos by comma or line', () => {
    expect(lines(' both lanes \n\ntools/harness-gate, analyzers\r\n')).toEqual(['both lanes', 'tools/harness-gate, analyzers']);
    expect(items('agent-plugin, forge\nother')).toEqual(['agent-plugin', 'forge', 'other']);
  });

  it('describes scope and matchers in words', () => {
    expect(tagScope({ repoIds: [], categories: [] })).toBe('every repo · every workstream');
    expect(tagScope({ repoIds: ['agent-plugin'], categories: ['qa-e2e'] })).toBe('agent-plugin · qa-e2e');
    expect(tagMatchers({ phrases: [], patterns: [] })).toBe('applies to every workstream in scope');
    expect(tagMatchers({ phrases: ['a', 'b'], patterns: ['c'] })).toBe('stage text has any of 2 phrases or 1 pattern');
  });

  it('reads the operator choice from the tag source', () => {
    const tag = { tagId: 't', name: 'n', applied: true, reason: '' };
    expect(tagChoice({ ...tag, source: 'matched' })).toBe('auto');
    expect(tagChoice({ ...tag, source: 'no-match', applied: false })).toBe('auto');
    expect(tagChoice({ ...tag, source: 'operator-added' })).toBe('on');
    expect(tagChoice({ ...tag, source: 'operator-removed', applied: false })).toBe('off');
  });
});

describe('TagList', () => {
  it('shows each tag with its scope, matchers and rule count', () => {
    const out = renderToStaticMarkup(<TagList catalog={catalog} />);
    expect(out).toContain('full harness gate');
    expect(out).toContain('agent-plugin · every workstream');
    expect(out).toContain('2 rules');
    expect(out).toContain('both lanes');
    expect(out).toContain('/lanes/(win|wsl)/');
  });

  it('lists the workstreams a checked tag applies to', () => {
    const out = renderToStaticMarkup(
      <TagList
        catalog={catalog}
        onCheck={() => undefined}
        matches={{
          tagId: 't-gate',
          rows: [
            { workstreamId: 'ws-1', name: 'Launch Plan', category: 'impl', status: 'active', applied: true, source: 'matched', reason: 'matched "both lanes"' },
            { workstreamId: 'ws-2', name: 'Docs only', category: 'impl', status: 'blocked', applied: false, source: 'no-match', reason: 'no phrase' },
          ],
        }}
      />,
    );
    expect(out).toContain('Applies to 1 of 2 workstreams in this story');
    expect(out).toContain('Check this story');
    expect(out).toContain('Launch Plan');
    expect(out).not.toContain('Docs only');
  });

  it('explains an empty catalog', () => {
    expect(renderToStaticMarkup(<TagList catalog={{ ...catalog, tags: [] }} />)).toContain('No tags yet');
  });
});

describe('RuleList', () => {
  it('shows rules with their tag names and marks disabled ones', () => {
    const out = renderToStaticMarkup(<RuleList catalog={catalog} onToggle={() => undefined} />);
    expect(out).toContain('Long-running checks');
    expect(out).toContain('full harness gate');
    expect(out).toContain('disabled');
    expect(out).toContain('Enable');
    expect(out).toContain('Disable');
  });
});

const choice = (source: RuleChoice['source'], via: string[] = [], applied = false): RuleChoice =>
  ({ id: `r-${source}`, title: `Rule ${source}`, text: `text ${source}`, enabled: source !== 'disabled', applied, source, via });

describe('ruleAction', () => {
  it('attaches a rule no tag brings, and detaches one a tag brings', () => {
    expect(ruleAction(choice('none'), true)).toBe('on');
    expect(ruleAction(choice('tag', ['gate'], true), false)).toBe('off');
  });

  it('drops the override when the tags already give the wanted answer', () => {
    expect(ruleAction(choice('detached', ['gate']), true)).toBe('clear');
    expect(ruleAction(choice('attached', [], true), false)).toBe('clear');
  });

  it('describes each standing', () => {
    expect(ruleStatus(choice('tag', ['full harness gate'], true))).toBe('via tag full harness gate');
    expect(ruleStatus(choice('detached', ['full harness gate']))).toBe('detached from this work item (tag full harness gate applies)');
    expect(ruleStatus(choice('none'))).toBe('not attached');
  });
});

describe('WorkItemRulesView', () => {
  const rules: WorkItemRules = {
    workItemId: 'ws-1',
    productId: 'harness',
    tags: [
      { tagId: 't-gate', name: 'full harness gate', applied: true, source: 'matched', reason: 'matched "both lanes"' },
      { tagId: 't-ui', name: 'ui', applied: false, source: 'operator-removed', reason: 'removed by the operator' },
    ],
    rules: [{ id: 'r-tag', title: 'Rule tag', text: 'text tag', via: ['full harness gate'], attached: false }],
    choices: [
      choice('tag', ['full harness gate'], true),
      choice('attached', [], true),
      choice('none'),
      choice('disabled'),
    ],
  };

  const render = (r: WorkItemRules) => renderToStaticMarkup(
    <MemoryRouter>
      <WorkItemRulesView rules={r} onRule={() => undefined} onResetRule={() => undefined} onTag={() => undefined} />
    </MemoryRouter>,
  );

  it('lists every catalog rule as a checkbox, ticked where it reaches the prompt', () => {
    const out = render(rules);
    expect(out.match(/type="checkbox"/g)).toHaveLength(4);
    expect(out.match(/checked=""/g)).toHaveLength(2);
    expect(out).toContain('via tag full harness gate');
    expect(out).toContain('attached to this work item');
    expect(out).toContain('disabled in the catalog');
    expect(out.match(/>Reset</g)).toHaveLength(1);
  });

  it('folds the tags with why each applies', () => {
    const out = render(rules);
    expect(out).toContain('Tags · 1 of 2 apply');
    expect(out).toContain('matched &quot;both lanes&quot;');
    expect(out.match(/aria-pressed="true"/g)).toHaveLength(2);
  });

  it('points to the Rules page when the catalog is empty', () => {
    expect(render({ ...rules, tags: [], rules: [], choices: [] })).toContain('href="/policies"');
  });
});