import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useProductsStore } from '@/state/useProductsStore';
import { NewProductModal } from '@/products/NewProductModal';
import { AddRepoModal } from '@/products/AddRepoModal';
import type { Product, Repo } from '@/types';

// Phase 8.7 — products page. New first-class surface; exempt from the
// forge-board.html screenshot diff (Phase 8.13) but uses the existing forge
// design tokens (page-head / filter-bar / workstream-grid classes).
export function ProductsView() {
  const products = useProductsStore((s) => s.products);
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const statsByProduct = useProductsStore((s) => s.statsByProduct);
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const load = useProductsStore((s) => s.load);
  const loadReposFor = useProductsStore((s) => s.loadReposFor);
  const loadStatsFor = useProductsStore((s) => s.loadStatsFor);
  const setActive = useProductsStore((s) => s.setActive);

  const [pickerOpen, setPickerOpen] = useState<string | null>(null);
  const [newProductOpen, setNewProductOpen] = useState(false);

  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    products.forEach((p) => {
      void loadReposFor(p.id);
      void loadStatsFor(p.id);
    });
  }, [products, loadReposFor, loadStatsFor]);

  return (
    <section className="view active" id="view-products">
      <div className="page-head">
        <div className="page-head-left">
          <div className="page-eyebrow">
            <span><span className="num">07</span>&nbsp;Products</span>
            <span className="sep">/</span>
            <span>{products.length} product{products.length === 1 ? '' : 's'}</span>
          </div>
          <h1 className="page-title">
            <span className="mark" />
            Products &amp; repos
          </h1>
        </div>
        <div className="page-head-right">
          <button className="btn btn-primary" onClick={() => setNewProductOpen(true)}>
            <svg width="12" height="12" viewBox="0 0 12 12" fill="none">
              <path d="M6 2v8M2 6h8" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
            </svg>
            New product
          </button>
        </div>
      </div>

      <div className="workstream-grid-wrap">
        {products.length === 0 && (
          <div className="empty-state">
            <div className="title">No products yet</div>
            <div className="sub">
              A product groups one or more repos. Click "New product" to start, then
              attach repos from ADO or GitHub.
            </div>
            <div className="actions">
              <button className="btn btn-primary" onClick={() => setNewProductOpen(true)}>
                New product
              </button>
            </div>
          </div>
        )}

        <div className="workstream-grid">
          {products.map((p) => (
            <ProductCard
              key={p.id}
              product={p}
              repos={reposByProduct[p.id] ?? []}
              stats={statsByProduct[p.id]}
              isActive={activeProductId === p.id}
              onActivate={() => setActive(p.id)}
              onAttach={() => setPickerOpen(p.id)}
            />
          ))}
        </div>
      </div>

      <NewProductModal
        open={newProductOpen}
        onClose={() => setNewProductOpen(false)}
        onCreated={() => { void load(); }}
      />

      <AddRepoModal
        open={pickerOpen !== null}
        productId={pickerOpen ?? ''}
        onClose={() => setPickerOpen(null)}
        onAttached={() => {
          if (pickerOpen) {
            void loadReposFor(pickerOpen);
            void loadStatsFor(pickerOpen);
          }
        }}
      />
    </section>
  );
}

interface ProductCardProps {
  product: Product;
  repos: Repo[];
  stats?: { repoCount: number; activeWorkstreams: number; openPrs: number; lastActivity: string | null };
  isActive: boolean;
  onActivate: () => void;
  onAttach: () => void;
}

function ProductCard({ product, repos, stats, isActive, onActivate, onAttach }: ProductCardProps) {
  const navigate = useNavigate();
  return (
    <article
      className="ws-card"
      style={isActive ? { outline: '2px solid var(--accent)', borderRadius: 'var(--radius-lg)' } : undefined}
    >
      <header className="ws-card-head">
        <div>
          <div className="ws-card-title">{product.name}</div>
          <div className="ws-card-sub">{product.description || `Slug: ${product.slug}`}</div>
        </div>
        {!isActive && (
          <button className="btn btn-secondary" onClick={onActivate} title="Set active product">
            Activate
          </button>
        )}
      </header>

      <div className="ws-card-stats">
        <div><span className="num">{repos.length}</span><span className="lbl">repo{repos.length === 1 ? '' : 's'}</span></div>
        <div><span className="num">{stats?.activeWorkstreams ?? 0}</span><span className="lbl">workstreams</span></div>
        <div><span className="num">{stats?.openPrs ?? 0}</span><span className="lbl">open PRs</span></div>
      </div>

      <div className="ws-card-body">
        {repos.length === 0 && (
          <div className="ws-card-empty">No repos attached. <button className="link" onClick={onAttach}>Attach a repo</button></div>
        )}
        {repos.map((r) => (
          <button
            key={r.id}
            className="repo-row"
            style={{ width: '100%', textAlign: 'left', cursor: 'pointer' }}
            onClick={() => navigate(`/workstreams?repo=${encodeURIComponent(r.id)}`)}
            title={`Click to open workstreams for ${r.displayName}`}
          >
            <span className="dot"></span>
            <span className="name">{r.displayName}</span>
            <span className="src">{r.kind === 'ado' ? 'ADO' : 'GH'}</span>
          </button>
        ))}
      </div>

      <footer className="ws-card-foot">
        <button className="btn btn-secondary" onClick={onAttach}>+ Attach repo</button>
      </footer>
    </article>
  );
}
