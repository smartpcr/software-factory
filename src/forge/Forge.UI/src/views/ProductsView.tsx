import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useProductsStore } from '@/state/useProductsStore';
import { useToastStore } from '@/state/useToastStore';
import { NewProductModal } from '@/products/NewProductModal';
import { EditProductModal } from '@/products/EditProductModal';
import { AddRepoModal } from '@/products/AddRepoModal';
import { EditRepoModal } from '@/products/EditRepoModal';
import { RepoSyncPanel } from '@/products/RepoSyncPanel';
import { ProductIcon } from '@/products/ProductIcon';
import { RepoIcon } from '@/products/RepoIcon';
import { forgeApi } from '@/api/forgeApi';
import type { Product, Repo } from '@/types';
import { RepoSyncStatusStrip } from '@/views/RepoSyncStatusStrip';
import { ProductSprintStrip } from '@/sprints/ProductSprintStrip';
import { SprintManager } from '@/sprints/SprintManager';

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
  const [editingProduct, setEditingProduct] = useState<Product | null>(null);
  const [editingRepo, setEditingRepo] = useState<Repo | null>(null);
  // Phase 23/24 — sync management panel for a repo.
  const [syncRepo, setSyncRepo] = useState<Repo | null>(null);
  // Phase 15 — sprint manager modal scoped to a product.
  const [sprintMgr, setSprintMgr] = useState<{ productId: string; productName: string; tab?: 'iterations' | 'areas' | 'import' | 'prefixes' } | null>(null);

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
              onEdit={() => setEditingProduct(p)}
              onEditRepo={(r) => setEditingRepo(r)}
              onSyncRepo={(r) => setSyncRepo(r)}
              onManageSprints={(tab) => setSprintMgr({ productId: p.id, productName: p.name, tab })}
            />
          ))}
        </div>
      </div>

      {sprintMgr && (
        <SprintManager
          open={true}
          productId={sprintMgr.productId}
          productName={sprintMgr.productName}
          initialTab={sprintMgr.tab ?? 'iterations'}
          onClose={() => setSprintMgr(null)}
        />
      )}

      <NewProductModal
        open={newProductOpen}
        onClose={() => setNewProductOpen(false)}
        onCreated={() => { void load(); }}
      />

      {editingProduct && (
        <EditProductModal
          open={true}
          product={editingProduct}
          onClose={() => setEditingProduct(null)}
        />
      )}

      {editingRepo && (
        <EditRepoModal
          open={true}
          repo={editingRepo}
          onClose={() => setEditingRepo(null)}
          onSaved={(updated) => {
            void loadReposFor(updated.productId);
            void loadStatsFor(updated.productId);
          }}
        />
      )}

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

      {syncRepo && (
        <RepoSyncPanel
          open={true}
          repo={syncRepo}
          onClose={() => setSyncRepo(null)}
          onRepoUpdated={(updated) => {
            void loadReposFor(updated.productId);
            void loadStatsFor(updated.productId);
            setSyncRepo(updated);
          }}
        />
      )}
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
  onEdit: () => void;
  onEditRepo: (repo: Repo) => void;
  onSyncRepo: (repo: Repo) => void;
  onManageSprints: (tab?: 'iterations' | 'areas' | 'import' | 'prefixes') => void;
}

function ProductCard({ product, repos, stats, isActive, onActivate, onAttach, onEdit, onEditRepo, onSyncRepo, onManageSprints }: ProductCardProps) {
  const navigate = useNavigate();
  const deleteProduct = useProductsStore((s) => s.deleteProduct);
  const loadReposFor = useProductsStore((s) => s.loadReposFor);
  const loadStatsFor = useProductsStore((s) => s.loadStatsFor);
  const toast = useToastStore((s) => s.show);

  const [busy, setBusy] = useState(false);

  async function confirmDelete() {
    const repoCount = repos.length;
    const prompt = repoCount > 0
      ? `Delete "${product.name}" and detach its ${repoCount} repo${repoCount === 1 ? '' : 's'}? This cannot be undone.`
      : `Delete "${product.name}"? This cannot be undone.`;
    if (!window.confirm(prompt)) return;
    setBusy(true);
    try {
      await deleteProduct(product.id, repoCount > 0);
      toast(`Deleted ${product.name}`, 'info');
    } catch (err) {
      toast(err instanceof Error ? err.message : 'Delete failed', 'danger');
    } finally {
      setBusy(false);
    }
  }

  async function detachRepo(r: Repo) {
    if (!window.confirm(`Detach "${r.displayName}" from ${product.name}?`)) return;
    try {
      await forgeApi.deleteRepo(r.id, true);
      toast(`Detached ${r.displayName}`, 'info');
      void loadReposFor(product.id);
      void loadStatsFor(product.id);
    } catch (err) {
      toast(err instanceof Error ? err.message : 'Detach failed', 'danger');
    }
  }

  return (
    <article
      className="ws-card"
      style={isActive ? { outline: '2px solid var(--accent)', borderRadius: 'var(--radius-lg)' } : undefined}
    >
      <header className="ws-card-head" style={{ alignItems: 'flex-start', gap: 12 }}>
        <ProductIcon product={product} size={44} />
        <div style={{ flex: 1, minWidth: 0 }}>
          <div className="ws-card-title">{product.name}</div>
          {product.description ? (
            <div style={{
              fontSize: 12,
              color: 'var(--text-tertiary)',
              lineHeight: 1.4,
              marginTop: 2,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              display: '-webkit-box',
              WebkitLineClamp: 2,
              WebkitBoxOrient: 'vertical',
            }}>
              {product.description}
            </div>
          ) : (
            <div className="ws-card-sub">Slug: {product.slug}</div>
          )}
        </div>
        <div style={{ display: 'flex', gap: 6, flexShrink: 0 }}>
          {!isActive && (
            <button className="btn btn-secondary" onClick={onActivate} title="Set active product" disabled={busy}>
              Activate
            </button>
          )}
          <button className="btn btn-ghost" onClick={onEdit} title="Edit product" disabled={busy}>
            Edit
          </button>
          <button className="btn btn-ghost" onClick={() => void confirmDelete()} title="Delete product" disabled={busy} style={{ color: 'var(--danger, #ef4444)' }}>
            Delete
          </button>
        </div>
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
          <div key={r.id} className="repo-row" style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <button
              type="button"
              onClick={() => navigate(`/workstreams?repo=${encodeURIComponent(r.id)}`)}
              title={`Open workstreams for ${r.displayName} (${r.kind === 'ado' ? 'Azure DevOps' : 'GitHub'})`}
              style={{
                flex: 1,
                display: 'flex',
                alignItems: 'center',
                gap: 8,
                background: 'transparent',
                border: 'none',
                color: 'inherit',
                font: 'inherit',
                textAlign: 'left',
                cursor: 'pointer',
                padding: 0,
                minWidth: 0,
              }}
            >
              <RepoIcon kind={r.kind} size={16} />
              <span className="name" style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {r.displayName}
              </span>
              <span className="src" style={{ marginLeft: 'auto' }}>
                {r.kind === 'ado' ? 'ADO' : 'GH'}
              </span>
            </button>
            {/* Phase 23.8 — sync status strip + drawer. Click → shows the
                last 20 sync runs with errors and a "Sync now" button. */}
            <RepoSyncStatusStrip
              repo={r}
              onSyncNow={async (repoId) => {
                try { await forgeApi.syncRepoWorkItems(repoId); }
                catch { /* surfaced inside the drawer */ }
              }}
            />

            <button
              type="button"
              className="icon-btn"
              onClick={() => onSyncRepo(r)}
              title={`Manage sync for ${r.displayName}`}
              aria-label={`Manage sync for ${r.displayName}`}
              style={{ width: 22, height: 22, color: 'var(--accent)' }}
            >
              <svg width="12" height="12" viewBox="0 0 16 16" fill="none">
                <path d="M2 8a6 6 0 0 1 10.2-4.3M14 8a6 6 0 0 1-10.2 4.3" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
                <path d="M12 1v3.5h-3.5M4 15v-3.5h3.5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/>
              </svg>
            </button>

            <button
              type="button"
              className="icon-btn"
              onClick={() => onEditRepo(r)}
              title={`Edit ${r.displayName} (path, branch, name)`}
              aria-label={`Edit ${r.displayName}`}
              style={{ width: 22, height: 22, color: 'var(--text-tertiary)' }}
            >
              <svg width="11" height="11" viewBox="0 0 12 12" fill="none">
                <path d="M8 1.5 L10.5 4 L4 10.5 L1.5 10.5 L1.5 8 Z" stroke="currentColor" strokeWidth="1.2" strokeLinejoin="round" />
              </svg>
            </button>
            <button
              type="button"
              className="icon-btn"
              onClick={() => void detachRepo(r)}
              title={`Detach ${r.displayName}`}
              aria-label={`Detach ${r.displayName}`}
              style={{ width: 22, height: 22, color: 'var(--text-tertiary)' }}
            >
              <svg width="11" height="11" viewBox="0 0 12 12" fill="none">
                <path d="M3 3 L 9 9 M 9 3 L 3 9" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
              </svg>
            </button>
          </div>
        ))}
      </div>

      <ProductSprintStrip productId={product.id} onManage={() => onManageSprints('iterations')} />

      <footer className="ws-card-foot" style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <button className="btn btn-secondary" onClick={onAttach}>+ Attach repo</button>
        <button className="btn btn-secondary" onClick={() => onManageSprints('import')}>Import from ADO</button>
      </footer>
    </article>
  );
}
