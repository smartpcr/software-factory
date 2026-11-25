// Phase 108 — the Rules page: manage a product's rule catalog (rules and the
// optional tags that attach them automatically). Rules are attached to one
// work item or workstream from that item's "Rules for this work item" list,
// on the work item execution page or the workstream's Overview tab.

import { useEffect, useId } from 'react';
import { useProductsStore } from '@/state/useProductsStore';
import { RuleCatalogView } from '@/swarm/RuleCatalogView';

export function RulesView(): JSX.Element {
  const id = useId();
  const products = useProductsStore((s) => s.products);
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const setActive = useProductsStore((s) => s.setActive);
  const load = useProductsStore((s) => s.load);

  useEffect(() => {
    if (products.length === 0) void load();
  }, [products.length, load]);

  const productId = activeProductId && activeProductId !== 'all' ? activeProductId : null;
  return (
    <div className="swarm-ctrl rules-page">
      <div className="rules-page__inner">
        <header className="rules-page__head">
          <h1 className="rules-page__title">Rules</h1>
          <label className="rules-page__product" htmlFor={`${id}-product`}>
            Product
            <select
              id={`${id}-product`}
              value={productId ?? ''}
              onChange={(e) => setActive(e.target.value || null)}
            >
              <option value="" disabled>Choose a product</option>
              {products.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </label>
        </header>
        <ol className="rules-page__steps">
          <li><strong>Write a rule</strong> once for the product: what the agent must do.</li>
          <li><strong>Attach it</strong> where it matters: tick it under <em>Rules for this work item</em> on a work item&apos;s execution page or a workstream&apos;s Overview.</li>
          <li><strong>Or tag it</strong> to attach it automatically to every work item whose repo and text a tag matches.</li>
        </ol>
        {productId
          ? <RuleCatalogView key={productId} productId={productId} />
          : <p className="sc-rules__empty">Choose a product to see its rules.</p>}
      </div>
    </div>
  );
}