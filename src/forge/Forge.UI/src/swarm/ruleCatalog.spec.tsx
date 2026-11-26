// Phase 108 — the per-work-item "Policies & rules" checklist and its helpers:
// what ticking a box asks the server for, how each standing reads, and that
// every policy and rule is listed with its state.

import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { MemoryRouter } from 'react-router-dom';
import type { PolicyChoice, RuleChoice, WorkItemRules } from '@/api/forgeApi';
import { choiceAction, choiceStatus, items, lines, tagChoice, tagMatchers, tagScope } from './ruleCatalogText';
import { WorkItemRulesView } from './WorkItemRulesSection';

const policy = (id: string, source: PolicyChoice['source'], via: string[] = [], applied = false): PolicyChoice =>
  ({ id, name: `Policy ${id}`, enabled: source !== 'disabled', applied, source, via, ruleCount: 2 });

const rule = (id: string, source: RuleChoice['source'], via: string[] = [], applied = false, policies: string[] = via, viaTags: string[] = []): RuleChoice =>
  ({ id, title: `Rule ${id}`, text: `text ${id}`, enabled: source !== 'disabled', applied, source, via, policies, viaTags });

describe('ruleCatalogText', () => {
  it('splits phrases by line and repos by comma or line', () => {
    expect(lines(' both lanes \n\ntools/harness-gate, analyzers\r\n')).toEqual(['both lanes', 'tools/harness-gate, analyzers']);
    expect(items('agent-plugin, forge\nother')).toEqual(['agent-plugin', 'forge', 'other']);
  });

  it('describes a tag in words', () => {
    expect(tagScope({ repoIds: [], categories: [] })).toBe('every repo · every workstream');
    expect(tagMatchers({ phrases: [], patterns: [] })).toBe('every work item in scope');
    expect(tagMatchers({ phrases: ['both lanes'], patterns: ['lanes/(win|wsl)'] })).toBe('text has "both lanes" or /lanes/(win|wsl)/');
  });

  it('reads the operator choice on a tag', () => {
    const tag = { tagId: 't', name: 'n', applied: true, reason: '' };
    expect(tagChoice({ ...tag, source: 'matched' })).toBe('auto');
    expect(tagChoice({ ...tag, source: 'operator-added' })).toBe('on');
    expect(tagChoice({ ...tag, source: 'operator-removed', applied: false })).toBe('off');
  });

  it('picks a box by hand only when nothing automatic would', () => {
    expect(choiceAction(policy('a', 'none'), true)).toBe('on');
    expect(choiceAction(policy('a', 'tag', ['gate'], true), false)).toBe('off');
    expect(choiceAction(policy('a', 'detached', ['gate']), true)).toBe('clear');
    expect(choiceAction(rule('r', 'attached', [], true), false)).toBe('clear');
    expect(choiceAction(rule('r', 'tag', [], true, [], ['gate']), false)).toBe('off');
    expect(choiceAction(policy('a', 'product', ['the whole product'], true), false)).toBe('off');
  });

  it('says why a policy or rule is or is not applied', () => {
    expect(choiceStatus(policy('a', 'tag', ['tag full harness gate'], true))).toBe('added by tag full harness gate');
    expect(choiceStatus(policy('a', 'story', ['story harness:DSH-M2', 'tag gate'], true))).toBe('added by story harness:DSH-M2, tag gate');
    expect(choiceStatus(policy('a', 'detached', ['the whole product']))).toBe('removed here by hand (would come from the whole product)');
    expect(choiceStatus(rule('r', 'policy', ['Harness gate'], true))).toBe('in policy Harness gate');
    expect(choiceStatus(rule('r', 'policy', ['Harness gate'], true, ['Harness gate'], ['gate']))).toBe('in policy Harness gate and by its tag gate');
    expect(choiceStatus(rule('r', 'tag', [], true, [], ['gate']))).toBe('added by its tag gate');
    expect(choiceStatus(rule('r', 'detached', ['Harness gate']))).toBe('removed here by hand (would come from policy Harness gate)');
    expect(choiceStatus(rule('r', 'attached', [], true))).toBe('added here by hand');
    expect(choiceStatus(rule('r', 'disabled'))).toBe('disabled on the Policies page');
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
    policies: [policy('gate', 'tag', ['tag full harness gate'], true), policy('lab', 'attached', [], true), policy('docs', 'none')],
    choices: [
      rule('long', 'policy', ['Policy gate'], true),
      rule('vet', 'detached', ['Policy gate']),
      rule('fake', 'attached', [], true, []),
      rule('old', 'disabled'),
    ],
    rules: [],
  };

  const render = (r: WorkItemRules) => renderToStaticMarkup(
    <MemoryRouter>
      <WorkItemRulesView rules={r} onChoose={() => undefined} onReset={() => undefined} onTag={() => undefined} />
    </MemoryRouter>,
  );

  it('lists every policy and rule as a checkbox, ticked where it applies', () => {
    const out = render(rules);
    expect(out.match(/type="checkbox"/g)).toHaveLength(7);
    expect(out.match(/checked=""/g)).toHaveLength(4);
    expect(out).toContain('Policy gate · 2 rules');
    expect(out).toContain('added by tag full harness gate');
    expect(out).toContain('in policy Policy gate');
    expect(out).toContain('removed here by hand');
    expect(out).toContain('disabled on the Policies page');
    expect(out.match(/>Reset</g)).toHaveLength(3);
  });

  it('folds the tags with why each matches', () => {
    const out = render(rules);
    expect(out).toContain('Tags · 1 of 2 match this work item');
    expect(out).toContain('matched &quot;both lanes&quot;');
    expect(out.match(/aria-pressed="true"/g)).toHaveLength(2);
  });

  it('points to the Policies page when the product has no rules', () => {
    expect(render({ ...rules, policies: [], choices: [], tags: [] })).toContain('href="/policies"');
  });
});