import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useProductsStore } from '@/state/useProductsStore';
import { useToastStore } from '@/state/useToastStore';
import { useConfirmStore } from '@/state/useConfirmStore';
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
import { RepoDetailPanel } from '@/products/RepoDetailPanel';
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
  const [expandedRepoId, setExpandedRepoId] = useState<string | null>(null);

  async function confirmDelete() {
    const repoCount = repos.length;
    const ok = await useConfirmStore.getState().ask({
      title: `Delete "${product.name}"?`,
      body: repoCount > 0
        ? `This deletes the product and detaches its ${repoCount} repo${repoCount === 1 ? '' : 's'}. The repo records remain — only the product binding is removed.`
        : 'This deletes the product. There are no attached repos.',
      tone: 'danger',
      confirmLabel: '✕ Delete product',
      cancelLabel: 'Cancel',
    });
    if (!ok) return;
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
    const ok = await useConfirmStore.getState().ask({
      title: `Detach "${r.displayName}"?`,
      body: `Removes the repo from ${product.name}. The repo record stays in the database; only the product binding is dropped.`,
      tone: 'warn',
      confirmLabel: 'Detach',
      cancelLabel: 'Cancel',
    });
    if (!ok) return;
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
        <div className="ws-stat">
          <div className="ws-stat-label">Repos</div>
          <div className="ws-stat-val">{repos.length}</div>
        </div>
        <div className="ws-stat">
          <div className="ws-stat-label">Workstreams</div>
          <div className="ws-stat-val">{stats?.activeWorkstreams ?? 0}</div>
        </div>
        <div className="ws-stat">
          <div className="ws-stat-label">Open PRs</div>
          <div className="ws-stat-val">{stats?.openPrs ?? 0}</div>
        </div>
      </div>

      <div className="ws-card-body">
        {repos.length === 0 && (
          <div className="ws-card-empty">No repos attached. <button className="link" onClick={onAttach}>Attach a repo</button></div>
        )}
        {repos.map((r) => (
          <div key={r.id}>
            <div className="repo-row" style={{ display: 'grid', gap: 4, padding: '6px 0' }}>
              {/* Row 1: Repo name + badges */}
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <button
                  type="button"
                  onClick={() => navigate(`/workstreams?repo=${encodeURIComponent(r.id)}`)}
                  title={`Open workstreams for ${r.displayName}`}
                  style={{
                    display: 'flex', alignItems: 'center', gap: 8,
                    background: 'transparent', border: 'none', color: 'inherit',
                    font: 'inherit', textAlign: 'left', cursor: 'pointer', padding: 0,
                    minWidth: 0, flex: 1,
                  }}
                >
                  <RepoIcon kind={r.kind} size={18} />
                  <span style={{ fontWeight: 600, fontSize: 13, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {r.displayName}
                  </span>
                </button>
                <span style={{ fontSize: 9, fontWeight: 600, padding: '1px 5px', borderRadius: 3, background: 'color-mix(in srgb, var(--text-tertiary) 12%, transparent)', color: 'var(--text-secondary)', whiteSpace: 'nowrap' }}>
                  {r.kind === 'ado' ? 'ADO' : 'GH'}
                </span>
                <RepoSyncStatusStrip
                  repo={r}
                  onSyncNow={async (repoId) => {
                    try { await forgeApi.syncRepoWorkItems(repoId); }
                    catch { /* surfaced inside the drawer */ }
                  }}
                  onRepoUpdated={(updated) => { void loadReposFor(updated.productId); }}
                />
                {(r.initCommand || r.restoreCommand || r.buildCommand || r.testCommand) ? (
                  <span
                    title={[
                      r.initCommand ? `Init: ${r.initCommand}` : null,
                      r.restoreCommand ? `Restore: ${r.restoreCommand}` : null,
                      r.buildCommand ? `Build: ${r.buildCommand}` : null,
                      r.testCommand ? `Test: ${r.testCommand}` : null,
                    ].filter(Boolean).join('\n')}
                    style={{ fontSize: 9, fontWeight: 600, padding: '1px 5px', borderRadius: 3, background: 'color-mix(in srgb, var(--accent) 15%, transparent)', color: 'var(--accent)', whiteSpace: 'nowrap', letterSpacing: 0.3 }}
                  >🔨 gated</span>
                ) : (
                  <span title="No build pipeline configured" style={{ fontSize: 9, fontWeight: 500, padding: '1px 5px', borderRadius: 3, background: 'color-mix(in srgb, var(--text-tertiary) 10%, transparent)', color: 'var(--text-tertiary)', whiteSpace: 'nowrap', letterSpacing: 0.3 }}>no gate</span>
                )}
              </div>

              {/* Row 2: Action buttons — icon + label both clickable */}
              <div style={{ display: 'flex', alignItems: 'center', gap: 2, paddingLeft: 26 }}>
                <button type="button" className="icon-btn" onClick={() => setExpandedRepoId(expandedRepoId === r.id ? null : r.id)} title={`${expandedRepoId === r.id ? 'Hide' : 'Show'} build & sync history for ${r.displayName}`} style={{ display: 'flex', alignItems: 'center', gap: 4, width: 'auto', height: 22, padding: '0 6px', color: expandedRepoId === r.id ? 'var(--accent)' : 'var(--text-primary)' }}>
                  <svg width="12" height="12" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="6.5" stroke="currentColor" strokeWidth="1.3" /><path d="M8 4.5V8l2.5 1.5" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round" /></svg>
                  <span style={{ fontSize: 9 }}>History</span>
                </button>

                <span style={{ width: 1, height: 12, background: 'var(--border-subtle)', margin: '0 2px' }} />

                <button type="button" className="icon-btn" onClick={() => onSyncRepo(r)} title={`Manage sync settings for ${r.displayName}`} style={{ display: 'flex', alignItems: 'center', gap: 4, width: 'auto', height: 22, padding: '0 6px', color: 'var(--accent)' }}>
                  <svg width="12" height="12" viewBox="0 0 16 16" fill="none"><path d="M2 8a6 6 0 0 1 10.2-4.3M14 8a6 6 0 0 1-10.2 4.3" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/><path d="M12 1v3.5h-3.5M4 15v-3.5h3.5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/></svg>
                  <span style={{ fontSize: 9 }}>Sync</span>
                </button>

                <span style={{ width: 1, height: 12, background: 'var(--border-subtle)', margin: '0 2px' }} />

                <button type="button" className="icon-btn" onClick={() => onEditRepo(r)} title={`Edit ${r.displayName} (path, branch, pipeline)`} style={{ display: 'flex', alignItems: 'center', gap: 4, width: 'auto', height: 22, padding: '0 6px', color: 'var(--text-primary)' }}>
                  <svg width="11" height="11" viewBox="0 0 12 12" fill="none"><path d="M8 1.5 L10.5 4 L4 10.5 L1.5 10.5 L1.5 8 Z" stroke="currentColor" strokeWidth="1.2" strokeLinejoin="round" /></svg>
                  <span style={{ fontSize: 9 }}>Edit</span>
                </button>

                <span style={{ width: 1, height: 12, background: 'var(--border-subtle)', margin: '0 2px' }} />

                <button type="button" className="icon-btn" onClick={() => void detachRepo(r)} title={`Detach ${r.displayName} from this product`} style={{ display: 'flex', alignItems: 'center', gap: 4, width: 'auto', height: 22, padding: '0 6px', color: 'var(--text-primary)' }}>
                  <svg width="11" height="11" viewBox="0 0 12 12" fill="none"><path d="M3 3 L 9 9 M 9 3 L 3 9" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" /></svg>
                  <span style={{ fontSize: 9 }}>Detach</span>
                </button>
              </div>
            </div>

          {/* Inline detail panel — full width, slides open below the repo row */}
          {expandedRepoId === r.id && <RepoDetailPanel repo={r} />}
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
