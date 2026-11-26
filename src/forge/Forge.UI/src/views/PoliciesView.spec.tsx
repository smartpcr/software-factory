// Phase 109 — the Rules & guardrails page: the policy → rule tree and its
// filters (products, stories, tags, words), and the detail panels that open
// for a policy, a rule or a tag.

import { describe, expect, it } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import type { RuleCatalog, RuleReach } from '@/api/forgeApi';
import { PolicyTree } from './PoliciesView';
import { PolicyPanel, RulePanel, TagPanel, type PanelContext } from './policyPanels';
import { buildTree, emptyFilter, storyKey, visibleKeys, type ProductCatalog, type TreeNode } from './policyTree';

const at = '2026-11-25T12:00:00Z';
const meta = { createdAt: at, createdBy: 'u', updatedAt: at };
const catalog: RuleCatalog = {
  productId: 'harness',
  tags: [
    { id: 't-gate', name: 'full harness gate', description: 'Needs a lane run', repoIds: ['agent-plugin'], categories: [], phrases: ['both lanes'], patterns: [], ...meta },
    { id: 't-all', name: 'every work item', description: '', repoIds: [], categories: [], phrases: [], patterns: [], ...meta },
  ],
  rules: [
    { id: 'r-long', title: 'Never run the full gate', text: 'Never run a lane in an iteration.', enabled: true, tagIds: [], ...meta },
    { id: 'r-vet', title: 'Scoped vet', text: 'Vet only changed packages.', enabled: false, tagIds: [], ...meta },
    { id: 'r-fake', title: 'Use the fake spawner', text: 'Prefer the fake spawner.', enabled: true, tagIds: ['t-gate'], ...meta },
  ],
  policies: [
    { id: 'p-gate', name: 'Full harness gate', description: 'Gate work', ruleIds: ['r-long', 'r-vet'], tagIds: ['t-gate'], enabled: true, ...meta },
    { id: 'p-merge', name: 'Merge readiness', description: 'Before merge', ruleIds: ['r-long'], tagIds: [], enabled: false, ...meta },
  ],
  ruleUsage: { 'r-fake': { attached: 3, detached: 1 } },
  policyUsage: { 'p-gate': { attached: 2, detached: 0 } },
  policyScopes: {
    'p-merge': [
      { kind: 'product', scopeId: 'harness', label: 'the whole product' },
      { kind: 'story', scopeId: 'spawn-m2', label: 'harness:DSH-M2 — dsh M2' },
    ],
  },
};
const other: RuleCatalog = {
  productId: 'local', policies: [], tags: [], ruleUsage: {}, policyUsage: {}, policyScopes: {},
  rules: [{ id: 'r-lab', title: 'One mutating run per lab', text: 'Respect the 409.', enabled: true, tagIds: [], ...meta }],
};
const products: ProductCatalog[] = [
  { productId: 'harness', productName: 'harness', catalog },
  { productId: 'local', productName: 'local', catalog: other },
];

const labels = (nodes: TreeNode[]) => visibleKeys(nodes, new Set()).map((n) => `${n.kind}:${n.label}`);

describe('buildTree', () => {
  it('nests rules under each policy, with rules in no policy and the tags after', () => {
    expect(labels(buildTree(products.slice(0, 1), emptyFilter))).toEqual([
      'product:harness',
      'group:Policies',
      'policy:Full harness gate', 'rule:Never run the full gate', 'rule:Scoped vet',
      'policy:Merge readiness', 'rule:Never run the full gate',
      'group:Rules in no policy', 'rule:Use the fake spawner',
      'group:Tags', 'tag:full harness gate', 'tag:every work item',
    ]);
  });

  it('describes how each policy applies and marks what is off', () => {
    const nodes = visibleKeys(buildTree(products.slice(0, 1), emptyFilter), new Set());
    const gate = nodes.find((n) => n.id === 'p-gate')!;
    const merge = nodes.find((n) => n.id === 'p-merge')!;
    expect(gate.meta).toEqual(['2 rules', '1 tag', '2 picked']);
    expect(merge.meta).toEqual(['1 rule', 'whole product', '1 story']);
    expect(merge.off).toBe(true);
    expect(nodes.find((n) => n.kind === 'tag' && n.id === 't-gate')!.meta).toEqual(['1 policy', '1 rule']);
  });

  it('filters by tag: policies with the tag show all their rules, and rules with the tag show on their own', () => {
    const tree = buildTree(products, { ...emptyFilter, tagNames: ['Full Harness Gate'] });
    expect(labels(tree)).toEqual([
      'product:harness',
      'group:Policies', 'policy:Full harness gate', 'rule:Never run the full gate', 'rule:Scoped vet',
      'group:Rules in no policy', 'rule:Use the fake spawner',
      'group:Tags', 'tag:full harness gate',
    ]);
  });

  it('filters by words, keeping each matching rule under its policy', () => {
    expect(labels(buildTree(products, { ...emptyFilter, text: 'lane iteration' }))).toEqual([
      'product:harness',
      'group:Policies', 'policy:Full harness gate', 'rule:Never run the full gate', 'policy:Merge readiness', 'rule:Never run the full gate',
    ]);
    expect(labels(buildTree(products, { ...emptyFilter, text: '409' }))).toEqual(['product:local', 'group:Rules in no policy', 'rule:One mutating run per lab']);
  });

  it('filters by story using how far each policy and rule reaches, and leaves out products without a chosen story', () => {
    const reach: Record<string, RuleReach> = { harness: { workstreams: 12, policies: { 'p-gate': 5 }, rules: { 'r-long': 5, 'r-fake': 2 } } };
    const tree = buildTree(products, { ...emptyFilter, storyKeys: [storyKey('harness', 'spawn-m2')] }, reach);
    expect(labels(tree)).toEqual([
      'product:harness',
      'group:Policies',
      'policy:Full harness gate', 'rule:Never run the full gate', 'rule:Scoped vet',
      'policy:Merge readiness', 'rule:Never run the full gate',
      'group:Rules in no policy', 'rule:Use the fake spawner',
      'group:Tags', 'tag:full harness gate',
    ]);
    const nodes = visibleKeys(tree, new Set());
    expect(nodes.find((n) => n.id === 'p-gate')!.reach).toBe('reaches 5 of 12 workstreams');
    expect(nodes.find((n) => n.id === 'p-gate')!.reachShort).toBe('5/12');
    expect(nodes.find((n) => n.id === 'p-merge')!.reach).toBe('reaches 0 of 12 workstreams');
    expect(tree[0].reach).toBe('12 workstreams in the chosen stories');
  });
});

describe('PolicyTree', () => {
  it('renders an accessible tree with the selection marked', () => {
    const out = renderToStaticMarkup(
      <PolicyTree nodes={buildTree(products.slice(0, 1), emptyFilter)} collapsed={new Set(['harness:group:tags'])}
        selection={{ productId: 'harness', kind: 'rule', id: 'r-fake' }} onToggle={() => undefined} onSelect={() => undefined} />,
    );
    expect(out).toContain('role="tree"');
    expect(out.match(/role="treeitem"/g)).toHaveLength(10);
    expect(out).toContain('aria-selected="true"');
    expect(out).toContain('aria-expanded="false"');
    expect(out).not.toContain('every work item');
  });

  it('says when nothing matches', () => {
    const out = renderToStaticMarkup(<PolicyTree nodes={[]} collapsed={new Set()} selection={null} filtering onToggle={() => undefined} onSelect={() => undefined} />);
    expect(out).toContain('Nothing matches these filters.');
  });
});

const ctx: PanelContext = {
  productId: 'harness',
  productName: 'harness',
  catalog,
  stories: [
    { spawnId: 'spawn-m2', storyId: 'harness:DSH-M2', title: 'dsh M2', phase: 'implement', workstreams: 40 },
    { spawnId: 'spawn-m1', storyId: 'harness:DSH-M1', title: 'dsh M1', phase: 'done', workstreams: 20 },
  ],
  busy: false,
  run: async () => undefined,
  select: () => undefined,
  confirm: async () => false,
};

describe('PolicyPanel', () => {
  it('lists its rules in order with controls, and where it applies', () => {
    const out = renderToStaticMarkup(<PolicyPanel ctx={ctx} policy={catalog.policies[1]} />);
    expect(out).toContain('Merge readiness');
    expect(out).toContain('Rules (1)');
    expect(out).toContain('Move Never run the full gate up');
    expect(out).toContain('Remove Never run the full gate from this policy');
    expect(out).toMatch(/type="checkbox" checked=""[^>]*\/><span><strong>The whole product<\/strong>/);
    expect(out).toContain('harness:DSH-M2 — dsh M2');
    expect(out).toContain('Add a story…');
    expect(out).toContain('harness:DSH-M1 — dsh M1 (20 workstreams)');
    expect(out).not.toContain('harness:DSH-M2 — dsh M2 (40 workstreams)');
    expect(out).toContain('Find a workstream or work item');
    expect(out).toContain('Merge readiness: off, click to turn on');
  });

  it('starts a new policy with just its name and purpose', () => {
    const out = renderToStaticMarkup(<PolicyPanel ctx={ctx} policy={null} />);
    expect(out).toContain('Create policy');
    expect(out).not.toContain('Applies to');
  });
});

describe('RulePanel', () => {
  it('shows the directive, the policies that contain it, and its own tags', () => {
    const out = renderToStaticMarkup(<RulePanel ctx={ctx} rule={catalog.rules[2]} />);
    expect(out).toContain('Prefer the fake spawner.');
    expect(out).toContain('In no policy.');
    expect(out).toContain('Tags (1)');
    expect(out).toContain('Remove full harness gate');
    expect(out).toContain('Picked on its own on 3 work items; kept out on 1');
  });

  it('starts a new rule inside the policy it was created from', () => {
    expect(renderToStaticMarkup(<RulePanel ctx={ctx} rule={null} startPolicyId="p-gate" />)).toContain('It starts in the policy Full harness gate.');
  });
});

describe('TagPanel', () => {
  it('names the policies and rules the tag applies', () => {
    const out = renderToStaticMarkup(<TagPanel ctx={ctx} tag={catalog.tags[0]} />);
    expect(out).toContain('Policy: Full harness gate');
    expect(out).toContain('Rule: Use the fake spawner');
    expect(out).toContain('agent-plugin · every workstream; text has &quot;both lanes&quot;.');
  });
});