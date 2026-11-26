// Phase 109 — the Policies page tree: product → policies → rules, plus the
// rules in no policy and the tags. Pure, so the filters can be unit-tested.
//
// Filters combine with AND, and each is a multi-select (any value matches):
//   tags    — a policy matches when it has one of the tags; a rule when it
//             has one of the tags itself;
//   stories — a policy or rule matches when it reaches at least one
//             workstream of the chosen stories (resolved by the server
//             exactly as the prompt is), or the policy is applied to one of
//             those stories;
//   text    — name, title, description or rule text contains the words.
// A policy that matches shows all its rules; a policy that does not, but
// holds matching rules, shows just those rules, so every match keeps its
// place in the tree.

import type { CatalogRule, RuleCatalog, RulePolicy, RuleReach, RuleTag } from '@/api/forgeApi';

export interface ProductCatalog {
  productId: string;
  productName: string;
  catalog: RuleCatalog;
}

export interface TreeFilter {
  /** Tag names, compared case-insensitively across products. */
  tagNames: string[];
  /** `${productId}|${spawnId}` for each chosen story. */
  storyKeys: string[];
  text: string;
}

export const emptyFilter: TreeFilter = { tagNames: [], storyKeys: [], text: '' };

export type NodeKind = 'product' | 'group' | 'policy' | 'rule' | 'tag';

export interface TreeNode {
  /** Unique within the tree; a rule in two policies has two nodes. */
  key: string;
  kind: NodeKind;
  productId: string;
  /** Id of the policy, rule or tag; the group name for a group. */
  id: string;
  label: string;
  /** Short facts shown after the label. */
  meta: string[];
  off?: boolean;
  /** "reaches 3 of 12 workstreams" while a story filter is on. */
  reach?: string;
  /** The same, short: "3/12". */
  reachShort?: string;
  children: TreeNode[];
}

/** What a node opens in the detail panel. */
export interface Selection {
  productId: string;
  kind: 'policy' | 'rule' | 'tag';
  /** Null while creating a new one. */
  id: string | null;
  /** For a new rule: the policy it starts in. */
  policyId?: string;
}

export const storyKey = (productId: string, spawnId: string) => `${productId}|${spawnId}`;

function words(text: string): string[] {
  return text.toLowerCase().split(/\s+/).filter(Boolean);
}

function hasWords(haystack: string, needles: string[]): boolean {
  const h = haystack.toLowerCase();
  return needles.every((w) => h.includes(w));
}

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

/** The spawn ids chosen for one product. */
export function storiesFor(filter: TreeFilter, productId: string): string[] {
  return filter.storyKeys.filter((k) => k.startsWith(`${productId}|`)).map((k) => k.slice(productId.length + 1));
}

function reachText(count: number | undefined, reach: RuleReach): string {
  return `reaches ${count ?? 0} of ${plural(reach.workstreams, 'workstream')}`;
}

function policyMeta(catalog: RuleCatalog, p: RulePolicy): string[] {
  const scopes = catalog.policyScopes[p.id] ?? [];
  const meta = [plural(p.ruleIds.length, 'rule')];
  if (scopes.some((s) => s.kind === 'product')) meta.push('whole product');
  const stories = scopes.filter((s) => s.kind === 'story').length;
  if (stories > 0) meta.push(plural(stories, 'story', 'stories'));
  if (p.tagIds.length > 0) meta.push(plural(p.tagIds.length, 'tag'));
  const picked = catalog.policyUsage[p.id]?.attached ?? 0;
  if (picked > 0) meta.push(`${picked} picked`);
  return meta;
}

function ruleMeta(catalog: RuleCatalog, r: CatalogRule): string[] {
  const meta: string[] = [];
  if (r.tagIds.length > 0) meta.push(plural(r.tagIds.length, 'tag'));
  const picked = catalog.ruleUsage[r.id]?.attached ?? 0;
  if (picked > 0) meta.push(`${picked} picked`);
  return meta;
}

/**
 * The tree for the chosen products. `reach` holds, per product, how far each
 * policy and rule reaches into the chosen stories; a product with chosen
 * stories elsewhere but none of its own is left out while a story filter is on.
 */
export function buildTree(
  products: ProductCatalog[],
  filter: TreeFilter,
  reach: Record<string, RuleReach | undefined> = {},
): TreeNode[] {
  const tagSet = new Set(filter.tagNames.map((t) => t.toLowerCase()));
  const needles = words(filter.text);
  const storyFilter = filter.storyKeys.length > 0;
  const out: TreeNode[] = [];

  for (const { productId, productName, catalog } of products) {
    const spawnIds = storiesFor(filter, productId);
    if (storyFilter && spawnIds.length === 0) continue;
    const r = storyFilter ? reach[productId] : undefined;
    const tagIds = new Set(catalog.tags.filter((t) => tagSet.has(t.name.toLowerCase())).map((t) => t.id));
    if (tagSet.size > 0 && tagIds.size === 0) continue;

    const policyMatches = (p: RulePolicy) =>
      (tagSet.size === 0 || p.tagIds.some((id) => tagIds.has(id)))
      && (!storyFilter || (r?.policies[p.id] ?? 0) > 0
        || (catalog.policyScopes[p.id] ?? []).some((s) => s.kind === 'story' && spawnIds.includes(s.scopeId)))
      && (needles.length === 0 || hasWords(`${p.name}\n${p.description}`, needles));
    const ruleMatches = (x: CatalogRule) =>
      (tagSet.size === 0 || x.tagIds.some((id) => tagIds.has(id)))
      && (!storyFilter || (r?.rules[x.id] ?? 0) > 0)
      && (needles.length === 0 || hasWords(`${x.title}\n${x.text}`, needles));

    const byId = new Map(catalog.rules.map((x) => [x.id, x]));
    const ruleNode = (x: CatalogRule, parent: string): TreeNode => ({
      key: `${productId}:rule:${x.id}@${parent}`,
      kind: 'rule',
      productId,
      id: x.id,
      label: x.title,
      meta: ruleMeta(catalog, x),
      off: !x.enabled,
      reach: r ? reachText(r.rules[x.id], r) : undefined,
      reachShort: r ? `${r.rules[x.id] ?? 0}/${r.workstreams}` : undefined,
      children: [],
    });

    const policyNodes: TreeNode[] = [];
    for (const p of catalog.policies) {
      const rules = p.ruleIds.map((id) => byId.get(id)).filter((x): x is CatalogRule => !!x);
      const whole = policyMatches(p);
      const shown = whole ? rules : rules.filter(ruleMatches);
      if (!whole && shown.length === 0) continue;
      policyNodes.push({
        key: `${productId}:policy:${p.id}`,
        kind: 'policy',
        productId,
        id: p.id,
        label: p.name,
        meta: policyMeta(catalog, p),
        off: !p.enabled,
        reach: r ? reachText(r.policies[p.id], r) : undefined,
        reachShort: r ? `${r.policies[p.id] ?? 0}/${r.workstreams}` : undefined,
        children: shown.map((x) => ruleNode(x, p.id)),
      });
    }

    const inSome = new Set(catalog.policies.flatMap((p) => p.ruleIds));
    const loose = catalog.rules.filter((x) => !inSome.has(x.id) && ruleMatches(x)).map((x) => ruleNode(x, 'none'));

    const used = new Set([
      ...policyNodes.flatMap((n) => catalog.policies.find((p) => p.id === n.id)?.tagIds ?? []),
      ...[...policyNodes.flatMap((n) => n.children), ...loose].flatMap((n) => byId.get(n.id)?.tagIds ?? []),
    ]);
    const tagMatches = (t: RuleTag) =>
      (tagSet.size === 0 || tagIds.has(t.id))
      && (!storyFilter || used.has(t.id))
      && (needles.length === 0 || hasWords(`${t.name}\n${t.description}`, needles));
    const tags = catalog.tags.filter(tagMatches).map<TreeNode>((t) => ({
      key: `${productId}:tag:${t.id}`,
      kind: 'tag',
      productId,
      id: t.id,
      label: t.name,
      meta: tagUse(catalog, t),
      children: [],
    }));

    const filtering = tagSet.size > 0 || storyFilter || needles.length > 0;
    const groups: TreeNode[] = [];
    if (policyNodes.length > 0 || !filtering) {
      groups.push(group(productId, 'policies', 'Policies', policyNodes));
    }
    if (loose.length > 0) groups.push(group(productId, 'loose', 'Rules in no policy', loose));
    if (tags.length > 0 || !filtering) groups.push(group(productId, 'tags', 'Tags', tags));
    if (filtering && groups.length === 0) continue;

    out.push({
      key: `${productId}:product`,
      kind: 'product',
      productId,
      id: productId,
      label: productName,
      meta: [plural(catalog.policies.length, 'policy', 'policies'), plural(catalog.rules.length, 'rule')],
      reach: r ? `${plural(r.workstreams, 'workstream')} in the chosen stories` : undefined,
      reachShort: r ? `${r.workstreams} ws` : undefined,
      children: groups,
    });
  }

  return out;
}

function group(productId: string, id: string, label: string, children: TreeNode[]): TreeNode {
  return { key: `${productId}:group:${id}`, kind: 'group', productId, id, label, meta: [String(children.length)], children };
}

function tagUse(catalog: RuleCatalog, t: RuleTag): string[] {
  const policies = catalog.policies.filter((p) => p.tagIds.includes(t.id)).length;
  const rules = catalog.rules.filter((r) => r.tagIds.includes(t.id)).length;
  if (policies + rules === 0) return ['unused'];
  return [policies > 0 ? plural(policies, 'policy', 'policies') : '', rules > 0 ? plural(rules, 'rule') : ''].filter(Boolean);
}

/** Keys of every node, depth-first, skipping the children of collapsed nodes. */
export function visibleKeys(nodes: TreeNode[], collapsed: Set<string>): TreeNode[] {
  const out: TreeNode[] = [];
  const walk = (list: TreeNode[]) => {
    for (const n of list) {
      out.push(n);
      if (n.children.length > 0 && !collapsed.has(n.key)) walk(n.children);
    }
  };
  walk(nodes);
  return out;
}

/** Whether a node shows the current selection. */
export function isSelected(node: TreeNode, sel: Selection | null): boolean {
  return !!sel && sel.id !== null && node.productId === sel.productId && node.kind === sel.kind && node.id === sel.id;
}