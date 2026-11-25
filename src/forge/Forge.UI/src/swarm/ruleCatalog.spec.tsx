// Phase 108 — the product rule catalog UI: tag scope and matcher wording, the
// editor's list parsing, the catalog lists, and a workstream's tags and rules.

import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import type { RuleCatalog, WorkstreamRules } from '@/api/forgeApi';
import { items, lines, tagChoice, tagMatchers, tagScope } from './ruleCatalogText';
import { RuleList, TagList } from './RuleCatalogView';
import { WorkstreamRulesView } from './WorkstreamRulesSection';

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

describe('WorkstreamRulesView', () => {
  const rules: WorkstreamRules = {
    workstreamId: 'ws-1',
    productId: 'harness',
    tags: [
      { tagId: 't-gate', name: 'full harness gate', applied: true, source: 'matched', reason: 'matched "both lanes"' },
      { tagId: 't-ui', name: 'ui', applied: false, source: 'operator-removed', reason: 'removed by the operator' },
    ],
    rules: [{ id: 'r1', title: 'Long-running checks', text: 'Never run the full gate.', via: ['full harness gate'] }],
  };

  it('shows each tag, why, and the rules that reach the workstream', () => {
    const out = renderToStaticMarkup(<WorkstreamRulesView rules={rules} onChoose={() => undefined} />);
    expect(out).toContain('matched &quot;both lanes&quot;');
    expect(out).toContain('removed by the operator');
    expect(out).toContain('Long-running checks');
    expect(out).toContain('via full harness gate');
    expect(out.match(/aria-pressed="true"/g)).toHaveLength(2);
  });

  it('says when the catalog is empty or no rule applies', () => {
    expect(renderToStaticMarkup(<WorkstreamRulesView rules={{ ...rules, tags: [], rules: [] }} />)).toContain('no tags yet');
    expect(renderToStaticMarkup(<WorkstreamRulesView rules={{ ...rules, rules: [] }} />)).toContain('No catalog rule reaches this workstream.');
  });
});