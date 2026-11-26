// Phases 108–109 — Rules & guardrails: the product policies, rules and tags.
//
// A rule is one directive the agent follows on every iteration of the work
// items it reaches. A policy groups rules (a rule can be in several). A
// policy applies to the whole product, to whole stories, wherever one of its
// tags matches, or to single work items picked by hand; a rule also applies
// on its own wherever one of its own tags matches.
//
// Layout: a filter bar (products, stories, tags, text; each a multi-select),
// a tree on the left (product → policies → rules, rules in no policy, tags),
// and the detail panel on the right for whatever is selected.
// Data: /api/products/{id}/rule-catalog.

import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { forgeApi, type RuleCatalog, type RuleReach, type StoryTarget } from '@/api/forgeApi';
import { useConfirmStore } from '@/state/useConfirmStore';
import { useProductsStore } from '@/state/useProductsStore';
import { MultiSelect, PolicyPanel, RulePanel, TagPanel, type PanelContext } from './policyPanels';
import {
  buildTree, emptyFilter, isSelected, storiesFor, storyKey, visibleKeys,
  type ProductCatalog, type Selection, type TreeFilter, type TreeNode,
} from './policyTree';

export function PoliciesView() {
  const products = useProductsStore((s) => s.products);
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const [productIds, setProductIds] = useState<string[]>([]);
  const [catalogs, setCatalogs] = useState<Record<string, RuleCatalog>>({});
  const [stories, setStories] = useState<Record<string, StoryTarget[]>>({});
  const [reach, setReach] = useState<Record<string, RuleReach>>({});
  const [reachVersion, setReachVersion] = useState(0);
  const [filter, setFilter] = useState<TreeFilter>(emptyFilter);
  const [selection, setSelection] = useState<Selection | null>(null);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Start on the active product (or every product on "All products"), and
  // follow it when it changes in the top bar; a store refresh keeps the choice.
  const startedOn = useRef<string | null | undefined>(undefined);
  useEffect(() => {
    if (products.length === 0 || startedOn.current === activeProductId) return;
    startedOn.current = activeProductId;
    const active = activeProductId && activeProductId !== 'all' && products.some((p) => p.id === activeProductId);
    setProductIds(active ? [activeProductId!] : products.map((p) => p.id));
  }, [activeProductId, products]);

  const loadCatalog = useCallback(async (pid: string) => {
    const c = await forgeApi.getRuleCatalog(pid);
    setCatalogs((all) => ({ ...all, [pid]: c }));
  }, []);

  useEffect(() => {
    let live = true;
    void (async () => {
      const missing = productIds.filter((pid) => !catalogs[pid]);
      try {
        await Promise.all(missing.map(async (pid) => {
          // The story list only feeds the filter and the story picker; without it the catalog still loads.
          const [c, t] = await Promise.all([
            forgeApi.getRuleCatalog(pid),
            forgeApi.listRuleTargets(pid).catch(() => ({ stories: [], workItems: [] })),
          ]);
          if (!live) return;
          setCatalogs((all) => ({ ...all, [pid]: c }));
          setStories((all) => ({ ...all, [pid]: t.stories }));
        }));
        if (live) setError(null);
      } catch (e) {
        if (live) setError(e instanceof Error ? e.message : 'Loading the policies failed');
      }
    })();
    return () => { live = false; };
    // Only fetch what is missing when the chosen products change.
  }, [productIds]);

  // How far each policy and rule reaches into the chosen stories.
  useEffect(() => {
    if (filter.storyKeys.length === 0) { setReach({}); return undefined; }
    let live = true;
    void Promise.all(productIds.map(async (pid) => {
      const spawnIds = storiesFor(filter, pid);
      if (spawnIds.length === 0) return [pid, undefined] as const;
      return [pid, await forgeApi.getRuleReach(pid, spawnIds)] as const;
    })).then((rows) => {
      if (!live) return;
      const next: Record<string, RuleReach> = {};
      for (const [pid, r] of rows) if (r) next[pid] = r;
      setReach(next);
    }).catch((e) => { if (live) setError(e instanceof Error ? e.message : 'Checking the stories failed'); });
    return () => { live = false; };
  }, [filter, productIds, reachVersion]);

  const nameOf = useCallback((pid: string) => products.find((p) => p.id === pid)?.name ?? pid, [products]);
  const shown: ProductCatalog[] = useMemo(
    () => productIds.filter((pid) => catalogs[pid]).map((pid) => ({ productId: pid, productName: nameOf(pid), catalog: catalogs[pid] })),
    [productIds, catalogs, nameOf],
  );
  const tree = useMemo(() => buildTree(shown, filter, reach), [shown, filter, reach]);

  const run = useCallback(async <T,>(action: () => Promise<T>): Promise<T | undefined> => {
    const pid = selection?.productId;
    setBusy(true);
    setError(null);
    try {
      const result = await action();
      if (pid) await loadCatalog(pid);
      setReachVersion((v) => v + 1);
      return result;
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The change failed');
      return undefined;
    } finally {
      setBusy(false);
    }
  }, [selection?.productId, loadCatalog]);

  const confirm = useCallback((title: string, body: string) =>
    useConfirmStore.getState().ask({ title, body, tone: 'warn', confirmLabel: 'Delete', cancelLabel: 'Keep' }), []);

  const select = useCallback((sel: Selection | null) => { setSelection(sel); setError(null); }, []);
  const newIn = selection?.productId ?? productIds[0] ?? null;

  const tagOptions = useMemo(() => {
    const names = new Map<string, Set<string>>();
    for (const { productName, catalog } of shown) {
      for (const t of catalog.tags) {
        const key = t.name.toLowerCase();
        if (!names.has(key)) names.set(key, new Set());
        names.get(key)!.add(productName);
      }
    }
    return [...names.entries()].sort(([a], [b]) => a.localeCompare(b))
      .map(([name, in_]) => ({ value: name, label: name, hint: shown.length > 1 ? [...in_].join(', ') : undefined }));
  }, [shown]);
  const storyOptions = useMemo(() => productIds.flatMap((pid) => (stories[pid] ?? []).map((s) => ({
    value: storyKey(pid, s.spawnId),
    label: `${s.storyId} — ${s.title}`,
    hint: `${s.workstreams} ws · ${s.phase}${productIds.length > 1 ? ` · ${nameOf(pid)}` : ''}`,
  }))), [productIds, stories, nameOf]);

  function exportCatalog() {
    const data = shown.map(({ productId, catalog }) => ({
      productId, policies: catalog.policies, rules: catalog.rules, tags: catalog.tags, policyScopes: catalog.policyScopes,
    }));
    const blob = new Blob([JSON.stringify(data.length === 1 ? data[0] : data, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `policies-${productIds.join('-') || 'none'}.json`;
    a.click();
    URL.revokeObjectURL(url);
  }

  const filtering = filter.tagNames.length > 0 || filter.storyKeys.length > 0 || filter.text.trim().length > 0;
  const counts = useMemo(() => {
    const all = visibleKeys(tree, new Set());
    return {
      policies: all.filter((n) => n.kind === 'policy').length,
      rules: new Set(all.filter((n) => n.kind === 'rule').map((n) => `${n.productId}:${n.id}`)).size,
      tags: all.filter((n) => n.kind === 'tag').length,
    };
  }, [tree]);

  const selCatalog = selection ? catalogs[selection.productId] : undefined;
  const ctx: PanelContext | null = selection && selCatalog ? {
    productId: selection.productId,
    productName: nameOf(selection.productId),
    catalog: selCatalog,
    stories: stories[selection.productId] ?? [],
    busy,
    run,
    select,
    confirm,
  } : null;

  return (
    <section className="view active" id="view-policies">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">08</span>&nbsp;Policies</span>
            <span className="sep">/</span>
            <span>{counts.policies} policies · {counts.rules} rules · {counts.tags} tags{filtering ? ' shown' : ''}</span>
            <span className="sep">/</span>
            <span>{productIds.length === 1 ? `product ${nameOf(productIds[0])}` : `${productIds.length} products`}</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Rules &amp; guardrails
          </h1>
        </div>
        <div className="page-head-right">
          <button type="button" className="btn btn-secondary" disabled={shown.length === 0} onClick={exportCatalog}>Export JSON</button>
          <button type="button" className="btn btn-secondary" disabled={!newIn}
            onClick={() => newIn && select({ productId: newIn, kind: 'tag', id: null })}>New tag</button>
          <button type="button" className="btn btn-secondary" disabled={!newIn}
            onClick={() => newIn && select({ productId: newIn, kind: 'rule', id: null })}>New rule</button>
          <button type="button" className="btn btn-primary" disabled={!newIn}
            onClick={() => newIn && select({ productId: newIn, kind: 'policy', id: null })}>
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none" aria-hidden="true">
              <path d="M6 2v8M2 6h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
            </svg>
            New policy
          </button>
        </div>
      </div>

      <div className="px-filters rx" role="search" aria-label="Filter policies and rules">
        <MultiSelect label="Products" allLabel="None" values={productIds}
          options={products.map((p) => ({ value: p.id, label: p.name }))}
          onChange={(v) => { setProductIds(v); setFilter((f) => ({ ...f, storyKeys: f.storyKeys.filter((k) => v.some((pid) => k.startsWith(`${pid}|`))) })); }} />
        <MultiSelect label="Stories" allLabel="Any story" values={filter.storyKeys} options={storyOptions}
          onChange={(v) => setFilter((f) => ({ ...f, storyKeys: v }))} />
        <MultiSelect label="Tags" allLabel="Any tag" values={filter.tagNames} options={tagOptions}
          onChange={(v) => setFilter((f) => ({ ...f, tagNames: v }))} />
        <input className="rx-input px-filter-text" type="search" value={filter.text} aria-label="Filter by words"
          placeholder="Words in a name or rule…" onChange={(e) => setFilter((f) => ({ ...f, text: e.target.value }))} />
        {filtering && <button type="button" className="rx-small-btn" onClick={() => setFilter(emptyFilter)}>Clear filters</button>}
      </div>

      {error && <p className="policies-error" role="alert">{error}</p>}
      {productIds.length === 0 && products.length > 0 && <p className="policies-note">Choose at least one product.</p>}

      <div className="px-split">
        <PolicyTree nodes={tree} collapsed={collapsed} selection={selection}
          loading={productIds.some((pid) => !catalogs[pid])}
          filtering={filtering}
          onToggle={(key) => setCollapsed((c) => { const n = new Set(c); if (n.has(key)) n.delete(key); else n.add(key); return n; })}
          onSelect={(n) => select({ productId: n.productId, kind: n.kind as Selection['kind'], id: n.id })} />
        <div className="px-detail rx" aria-live="polite">
          {ctx && selection ? <Detail ctx={ctx} selection={selection} /> : <Help />}
        </div>
      </div>
    </section>
  );
}

function Detail({ ctx, selection }: { ctx: PanelContext; selection: Selection }) {
  const key = `${selection.productId}:${selection.kind}:${selection.id ?? `new-${selection.policyId ?? ''}`}`;
  switch (selection.kind) {
    case 'policy': {
      const p = selection.id ? ctx.catalog.policies.find((x) => x.id === selection.id) : null;
      if (selection.id && !p) return <Gone />;
      return <PolicyPanel key={key} ctx={ctx} policy={p ?? null} />;
    }
    case 'rule': {
      const r = selection.id ? ctx.catalog.rules.find((x) => x.id === selection.id) : null;
      if (selection.id && !r) return <Gone />;
      return <RulePanel key={key} ctx={ctx} rule={r ?? null} startPolicyId={selection.policyId} />;
    }
    default: {
      const t = selection.id ? ctx.catalog.tags.find((x) => x.id === selection.id) : null;
      if (selection.id && !t) return <Gone />;
      return <TagPanel key={key} ctx={ctx} tag={t ?? null} />;
    }
  }
}

function Gone() {
  return <div className="px-help"><p>This item no longer exists. Pick another in the tree.</p></div>;
}

function Help() {
  return (
    <div className="px-help">
      <h2 className="px-help-title">Pick a policy, rule or tag to see and change it</h2>
      <ol>
        <li><strong>Rules</strong> are directives the agent follows on every iteration of the work items they reach.</li>
        <li><strong>Policies</strong> group rules. A policy applies to the whole product, to whole stories, wherever one of its tags matches, or to single workstreams and work items you pick.</li>
        <li><strong>Tags</strong> match work items by repo and text. A tag on a policy applies the policy; a tag on a rule applies that rule on its own.</li>
      </ol>
      <p className="rx-hint">On one work item&apos;s execution page or a workstream&apos;s Overview you can still pick or remove a single policy or rule; that choice wins.</p>
    </div>
  );
}

const KIND_LABEL: Record<TreeNode['kind'], string> = { product: 'Product', group: '', policy: 'Policy', rule: 'Rule', tag: 'Tag' };

/** The tree: arrow keys move, Right/Left open and close, Enter or Space selects. */
export function PolicyTree({ nodes, collapsed, selection, loading, filtering, onToggle, onSelect }: {
  nodes: TreeNode[];
  collapsed: Set<string>;
  selection: Selection | null;
  loading?: boolean;
  filtering?: boolean;
  onToggle: (key: string) => void;
  onSelect: (node: TreeNode) => void;
}) {
  const [focusKey, setFocusKey] = useState<string | null>(null);
  const box = useRef<HTMLDivElement>(null);
  const visible = useMemo(() => visibleKeys(nodes, collapsed), [nodes, collapsed]);
  const current = visible.find((n) => n.key === focusKey) ?? visible.find((n) => isSelected(n, selection)) ?? visible[0];

  const focus = (n: TreeNode | undefined) => {
    if (!n) return;
    setFocusKey(n.key);
    box.current?.querySelector<HTMLElement>(`[data-key="${CSS.escape(n.key)}"]`)?.focus();
  };
  const parentOf = (key: string): TreeNode | undefined => {
    const find = (list: TreeNode[], parent?: TreeNode): TreeNode | undefined => {
      for (const n of list) {
        if (n.key === key) return parent;
        const hit = find(n.children, n);
        if (hit) return hit;
      }
      return undefined;
    };
    return find(nodes);
  };
  const activate = (n: TreeNode) => {
    if (n.kind === 'product' || n.kind === 'group') onToggle(n.key);
    else onSelect(n);
  };

  function onKeyDown(e: KeyboardEvent<HTMLUListElement>) {
    if (!current) return;
    const i = visible.indexOf(current);
    const open = current.children.length > 0 && !collapsed.has(current.key);
    switch (e.key) {
      case 'ArrowDown': focus(visible[i + 1]); break;
      case 'ArrowUp': focus(visible[i - 1]); break;
      case 'Home': focus(visible[0]); break;
      case 'End': focus(visible[visible.length - 1]); break;
      case 'ArrowRight':
        if (current.children.length === 0) return;
        if (open) focus(current.children[0]); else onToggle(current.key);
        break;
      case 'ArrowLeft':
        if (open) onToggle(current.key); else focus(parentOf(current.key));
        break;
      case 'Enter':
      case ' ':
        activate(current);
        break;
      default:
        return;
    }
    e.preventDefault();
  }

  const render = (list: TreeNode[], level: number) => list.map((n) => {
    const hasKids = n.children.length > 0 || n.kind === 'product' || n.kind === 'group';
    const open = hasKids && !collapsed.has(n.key);
    const sel = isSelected(n, selection);
    return (
      <li key={n.key} role="none">
        <div
          role="treeitem"
          data-key={n.key}
          data-kind={n.kind}
          aria-level={level}
          aria-expanded={hasKids ? open : undefined}
          aria-selected={n.kind === 'product' || n.kind === 'group' ? undefined : sel}
          tabIndex={current?.key === n.key ? 0 : -1}
          className={`px-node${sel ? ' is-selected' : ''}${n.off ? ' is-off' : ''}`}
          style={{ paddingLeft: 8 + (level - 1) * 16 }}
          onClick={() => { setFocusKey(n.key); activate(n); }}
          onFocus={() => setFocusKey(n.key)}
        >
          <span className="px-caret" aria-hidden="true">{hasKids ? (open ? '▾' : '▸') : ''}</span>
          {KIND_LABEL[n.kind] && <span className="px-kind">{KIND_LABEL[n.kind]}</span>}
          <span className="px-node-label">{n.label}</span>
          {n.off && <span className="px-off">off</span>}
          <span className="px-node-meta">{n.meta.join(' · ')}</span>
          {n.reachShort && (
            <span className="px-reach" title={n.reach}>
              <span aria-hidden="true">{n.reachShort}</span>
              <span className="px-sr">{n.reach}</span>
            </span>
          )}
        </div>
        {hasKids && open && n.children.length > 0 && <ul role="group">{render(n.children, level + 1)}</ul>}
        {hasKids && open && n.children.length === 0 && n.kind === 'group' && (
          <p className="px-node-empty" style={{ paddingLeft: 24 + level * 16 }}>{n.id === 'tags' ? 'No tags yet.' : 'No policies yet.'}</p>
        )}
      </li>
    );
  });

  return (
    <div className="px-tree rx" ref={box}>
      {loading && nodes.length === 0 && <p className="rx-empty">Loading…</p>}
      {!loading && nodes.length === 0 && <p className="rx-empty">{filtering ? 'Nothing matches these filters.' : 'No products chosen.'}</p>}
      {nodes.length > 0 && (
        <ul role="tree" aria-label="Policies, rules and tags" onKeyDown={onKeyDown}>{render(nodes, 1)}</ul>
      )}
    </div>
  );
}