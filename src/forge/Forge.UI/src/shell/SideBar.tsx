import { useEffect, useMemo, useRef, useState } from 'react';
import { NavLink } from 'react-router-dom';
import { useSidebarStore } from '@/state/useSidebarStore';
import { useProductsStore } from '@/state/useProductsStore';
import { useCurrentUser } from '@/state/useCurrentUserStore';
import { ProductIcon } from '@/products/ProductIcon';
import { UserMenu } from '@/shell/UserMenu';
import { useWorkItemsStore } from '@/workitems/useWorkItemsStore';

// Verbatim port of the prototype's <aside class="sidebar">. The only
// React-required changes: NavLink in place of <a data-view>, className
// replacing class, and onClick handlers on the collapse and add buttons.
export function SideBar() {
  const toggle = useSidebarStore((s) => s.toggle);
  const products = useProductsStore((s) => s.products);
  const activeProductId = useProductsStore((s) => s.activeProductId);
  const reposByProduct = useProductsStore((s) => s.reposByProduct);
  const loadProducts = useProductsStore((s) => s.load);
  const loadRepos = useProductsStore((s) => s.loadReposFor);
  const me = useCurrentUser();

  useEffect(() => { void loadProducts(); }, [loadProducts]);

  const activeProduct = useMemo(
    () => products.find((p) => p.id === activeProductId) ?? products[0] ?? null,
    [products, activeProductId],
  );

  useEffect(() => {
    if (activeProduct) void loadRepos(activeProduct.id);
  }, [activeProduct, loadRepos]);

  const activeProductRepos = useMemo(
    () => (activeProduct ? reposByProduct[activeProduct.id] ?? [] : []),
    [activeProduct, reposByProduct],
  );
  // Phase 23.9 — when any repo in the active product has lastSyncStatus
  // 'failed', the sidebar entry surfaces a small red dot. No separate
  // alert center at v0.1; click → /products/{id} (the entry already
  // navigates there) and the failing repo card is auto-scrolled into view.
  const hasFailedSync = useMemo(
    () => activeProductRepos.some((r) => r.lastSyncStatus === 'failed'),
    [activeProductRepos],
  );
  const repoCount = activeProductRepos.length;
  const roleLabel = formatRole(me.role);

  // Phase 12 — sidebar badge counts active+proposed work items. Reads live
  // from the WIT store (which falls back to the sample registry when the
  // API is unreachable). Updates via SignalR `workitem.changed`.
  const workItems = useWorkItemsStore((s) => s.items);
  const loadWorkItems = useWorkItemsStore((s) => s.load);
  useEffect(() => { void loadWorkItems(); }, [loadWorkItems]);
  const workItemActiveCount = useMemo(
    () => workItems.filter((i) => i.state === 'active' || i.state === 'proposed').length,
    [workItems],
  );

  // Phase 9 — sidebar foot user widget opens UserMenu (same popover the TopBar
  // avatar uses). Important when no provider is connected — the placeholder
  // "Sign in" label was previously a non-interactive string.
  const userRef = useRef<HTMLButtonElement | null>(null);
  const [menuOpen, setMenuOpen] = useState(false);
  const [menuAnchor, setMenuAnchor] = useState<DOMRect | null>(null);
  const isUnsigned = !me.id;
  function openMenu() {
    if (userRef.current) setMenuAnchor(userRef.current.getBoundingClientRect());
    setMenuOpen(true);
  }

  return (
    <aside className="sidebar">
      <div className="sidebar-head">
        <span className="sidebar-head-label">Workspace</span>
        <button className="sidebar-collapse-btn" id="sidebar-toggle" title="Toggle sidebar" onClick={toggle}>
          <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
            <path d="M9 3 L 5 7 L 9 11" stroke="currentColor" strokeWidth="1.4" fill="none" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        </button>
      </div>

      <div className="sidebar-body">

        <NavLink
          to={hasFailedSync && activeProduct ? `/products?focus=${encodeURIComponent(activeProduct.id)}#sync-failed` : '/products'}
          className="repo-card"
          title={
            hasFailedSync
              ? `${activeProduct?.name ?? 'Product'} — sync failed on at least one repo`
              : (activeProduct ? `${activeProduct.name} — switch product` : 'Manage products')
          }
          data-testid="sidebar-product-link"
          data-sync-failed={hasFailedSync ? 'true' : 'false'}
          style={{ textDecoration: 'none', color: 'inherit', display: 'block' }}
        >
          <div className="repo-card-row" style={{ alignItems: 'center', gap: 8 }}>
            {activeProduct
              ? <ProductIcon product={activeProduct} size={22} />
              : <span className="repo-card-dot" />}
            <span className="repo-card-name" style={{ flex: 1, minWidth: 0 }}>
              {activeProduct?.name ?? 'No product'}
            </span>
            {hasFailedSync && (
              <span
                aria-hidden
                data-testid="sidebar-sync-failed-dot"
                style={{
                  width: 8,
                  height: 8,
                  borderRadius: 999,
                  background: 'rgb(220, 38, 38)',
                  boxShadow: '0 0 0 3px color-mix(in srgb, rgb(220, 38, 38) 30%, transparent)',
                }}
              />
            )}
            <span className="repo-card-source">{products.length > 1 ? 'Switch' : 'Product'}</span>
          </div>
          <div className="repo-card-meta">
            <span>{repoCount} repo{repoCount === 1 ? '' : 's'}</span>
            <span className="sync">{products.length} product{products.length === 1 ? '' : 's'}</span>
          </div>
        </NavLink>
        <div className="repo-collapsed-icon" title={activeProduct?.name ?? 'product'}>
          {activeProduct ? <ProductIcon product={activeProduct} size={20} /> : 'A'}
        </div>

        <div className="nav-section">
          <div className="nav-section-title"><span>Work</span></div>
          <NavLink className={({ isActive }) => `nav-item${isActive ? ' active' : ''}`} to="/" end>
            <svg className="icon" width="16" height="16" viewBox="0 0 16 16" fill="none"><rect x="2" y="2" width="5" height="5" stroke="currentColor" strokeWidth="1.4" /><rect x="9" y="2" width="5" height="5" stroke="currentColor" strokeWidth="1.4" /><rect x="2" y="9" width="5" height="5" stroke="currentColor" strokeWidth="1.4" /><rect x="9" y="9" width="5" height="5" stroke="currentColor" strokeWidth="1.4" /></svg>
            <span className="label">Overview</span>
            <span className="tooltip">Overview</span>
          </NavLink>
          <NavLink className={({ isActive }) => `nav-item has-sub${isActive ? ' active' : ''}`} to="/plan">
            <svg className="icon" width="16" height="16" viewBox="0 0 16 16" fill="none"><rect x="2" y="2" width="3" height="12" stroke="currentColor" strokeWidth="1.4" /><rect x="6.5" y="2" width="3" height="7" stroke="currentColor" strokeWidth="1.4" /><rect x="11" y="2" width="3" height="9" stroke="currentColor" strokeWidth="1.4" /></svg>
            <div className="label-block">
              <span className="label">Plan & Design</span>
              <span className="sub">Sprint 14 · day 6/10</span>
            </div>
            <span className="badge">38</span>
            <span className="tooltip">Plan & Design</span>
          </NavLink>
          <NavLink className={({ isActive }) => `nav-item has-sub${isActive ? ' active' : ''}`} to="/work-items">
            <svg className="icon" width="16" height="16" viewBox="0 0 16 16" fill="none"><rect x="2" y="2.5" width="9" height="2.2" rx="0.6" stroke="currentColor" strokeWidth="1.3" /><rect x="2" y="6.9" width="12" height="2.2" rx="0.6" stroke="currentColor" strokeWidth="1.3" /><rect x="2" y="11.3" width="6.5" height="2.2" rx="0.6" stroke="currentColor" strokeWidth="1.3" /></svg>
            <div className="label-block">
              <span className="label">Work items</span>
              <span className="sub">2 epics · 4 features · 17 stories/tasks</span>
            </div>
            <span className="badge">{workItemActiveCount}</span>
            <span className="tooltip">Work items</span>
          </NavLink>
          <NavLink className={({ isActive }) => `nav-item has-sub${isActive ? ' active' : ''}`} to="/inbox">
            <svg className="icon" width="16" height="16" viewBox="0 0 16 16" fill="none">
              <path d="M2 9 L 2 13 a 1 1 0 0 0 1 1 H 13 a 1 1 0 0 0 1 -1 V 9 H 10.5 L 9.5 11 H 6.5 L 5.5 9 Z" stroke="currentColor" strokeWidth="1.3" fill="none" />
              <path d="M3 9 L 4.5 3 a 1 1 0 0 1 1 -0.8 H 10.5 a 1 1 0 0 1 1 0.8 L 13 9" stroke="currentColor" strokeWidth="1.3" fill="none" />
            </svg>
            <div className="label-block">
              <span className="label">Inbox</span>
              <span className="sub">Pending acks · queue</span>
            </div>
            <span className="tooltip">Inbox</span>
          </NavLink>
          <NavLink className={({ isActive }) => `nav-item has-sub${isActive ? ' active' : ''}`} to="/timeline">
            <svg className="icon" width="16" height="16" viewBox="0 0 16 16" fill="none"><line x1="2" y1="4" x2="11" y2="4" stroke="currentColor" strokeWidth="1.4" /><line x1="2" y1="8" x2="14" y2="8" stroke="currentColor" strokeWidth="1.4" /><line x1="2" y1="12" x2="9" y2="12" stroke="currentColor" strokeWidth="1.4" /><circle cx="11" cy="4" r="1.3" fill="currentColor" /><circle cx="14" cy="8" r="1.3" fill="currentColor" /><circle cx="9" cy="12" r="1.3" fill="currentColor" /></svg>
            <div className="label-block">
              <span className="label">Timeline</span>
              <span className="sub">14 days · 8 branches</span>
            </div>
            <span className="tooltip">Timeline</span>
          </NavLink>
          <NavLink className={({ isActive }) => `nav-item has-sub${isActive ? ' active' : ''}`} to="/workstreams">
            <svg className="icon" width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="4" cy="4" r="1.5" stroke="currentColor" strokeWidth="1.3" /><circle cx="4" cy="12" r="1.5" stroke="currentColor" strokeWidth="1.3" /><circle cx="12" cy="8" r="1.5" stroke="currentColor" strokeWidth="1.3" /><path d="M4 5.5 V 10.5 M 5.5 4 Q 12 4 12 6.5 M 5.5 12 Q 12 12 12 9.5" stroke="currentColor" strokeWidth="1.1" fill="none" /></svg>
            <div className="label-block">
              <span className="label">Workstreams</span>
              <span className="sub">5 active · 1 conflict</span>
            </div>
            <span className="badge">8</span>
            <span className="tooltip">Workstreams</span>
          </NavLink>
          <NavLink className={({ isActive }) => `nav-item has-sub${isActive ? ' active' : ''}`} to="/prs">
            <svg className="icon" width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="4" cy="3.5" r="1.5" stroke="currentColor" strokeWidth="1.3" /><circle cx="4" cy="12.5" r="1.5" stroke="currentColor" strokeWidth="1.3" /><circle cx="12" cy="12.5" r="1.5" stroke="currentColor" strokeWidth="1.3" /><line x1="4" y1="5" x2="4" y2="11" stroke="currentColor" strokeWidth="1.3" /><path d="M12 11 V 7 a 3 3 0 0 0 -3 -3 H 5.5" stroke="currentColor" strokeWidth="1.3" fill="none" /></svg>
            <div className="label-block">
              <span className="label">Pull Requests</span>
              <span className="sub">2 need approval</span>
            </div>
            <span className="badge alert">2</span>
            <span className="tooltip">Pull Requests</span>
          </NavLink>
          <NavLink className={({ isActive }) => `nav-item${isActive ? ' active' : ''}`} to="/audit">
            <svg className="icon" width="16" height="16" viewBox="0 0 16 16" fill="none">
              <rect x="2.5" y="2.5" width="11" height="11" stroke="currentColor" strokeWidth="1.3" fill="none" />
              <line x1="5" y1="6" x2="11" y2="6" stroke="currentColor" strokeWidth="1.2" />
              <line x1="5" y1="9" x2="11" y2="9" stroke="currentColor" strokeWidth="1.2" />
              <line x1="5" y1="12" x2="9" y2="12" stroke="currentColor" strokeWidth="1.2" />
            </svg>
            <span className="label">Audit</span>
            <span className="tooltip">Audit log</span>
          </NavLink>
        </div>

        <div className="nav-section">
          <div className="nav-section-title"><span>Agents</span><button className="add-btn" title="New agent">+</button></div>
          <NavLink className={({ isActive }) => `nav-item has-sub${isActive ? ' active' : ''}`} to="/crews">
            <svg className="icon" width="16" height="16" viewBox="0 0 16 16" fill="none">
              <circle cx="5" cy="5" r="2" stroke="currentColor" strokeWidth="1.3" />
              <circle cx="11" cy="5" r="2" stroke="currentColor" strokeWidth="1.3" />
              <path d="M2 13c0-2 1.5-3 3-3s3 1 3 3" stroke="currentColor" strokeWidth="1.3" fill="none" />
              <path d="M8 13c0-2 1.5-3 3-3s3 1 3 3" stroke="currentColor" strokeWidth="1.3" fill="none" />
            </svg>
            <div className="label-block">
              <span className="label">Crews</span>
              <span className="sub">Hierarchy · capability · quota</span>
            </div>
            <span className="tooltip">Crews</span>
          </NavLink>
          {/* Phase 13.10 — nav is open to all; the view itself stays read-only
              for non-lead / non-PO users, mutation endpoints enforce role. */}
          <NavLink className={({ isActive }) => `nav-item has-sub${isActive ? ' active' : ''}`} to="/swarm">
            <svg className="icon" width="16" height="16" viewBox="0 0 16 16" fill="none"><circle cx="8" cy="8" r="1.4" fill="currentColor" /><circle cx="3.5" cy="4.5" r="1.1" fill="currentColor" /><circle cx="12.5" cy="4.5" r="1.1" fill="currentColor" /><circle cx="3.5" cy="11.5" r="1.1" fill="currentColor" /><circle cx="12.5" cy="11.5" r="1.1" fill="currentColor" /></svg>
            <div className="label-block">
              <span className="label">Swarm</span>
              <span className="sub">6 working · 3 idle</span>
            </div>
            <span className="tooltip">Swarm</span>
          </NavLink>
          <NavLink className={({ isActive }) => `nav-item has-sub${isActive ? ' active' : ''}`} to="/policies">
            <svg className="icon" width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M8 1 L13 3 V 8 C 13 11 11 13 8 14 C 5 13 3 11 3 8 V 3 Z" stroke="currentColor" strokeWidth="1.3" fill="none" /></svg>
            <div className="label-block">
              <span className="label">Policies</span>
              <span className="sub">12 rules · 2 gates</span>
            </div>
            <span className="tooltip">Policies</span>
          </NavLink>
          <NavLink className={({ isActive }) => `nav-item has-sub${isActive ? ' active' : ''}`} to="/cost">
            <svg className="icon" width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M8 2 V 14 M 5 5 H 10 a 2 2 0 0 1 0 4 H 5 a 2 2 0 0 0 0 4 H 11" stroke="currentColor" strokeWidth="1.3" fill="none" strokeLinecap="round" /></svg>
            <div className="label-block">
              <span className="label">Costs</span>
              <span className="sub">$4.81 today · $32 wk</span>
            </div>
            <span className="tooltip">Costs</span>
          </NavLink>
          <NavLink className={({ isActive }) => `nav-item has-sub${isActive ? ' active' : ''}`} to="/performance">
            <svg className="icon" width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M2 13 L 6 8 L 9 11 L 14 4" stroke="currentColor" strokeWidth="1.4" fill="none" strokeLinecap="round" strokeLinejoin="round" /><circle cx="14" cy="4" r="1.4" fill="currentColor" /></svg>
            <div className="label-block">
              <span className="label">Performance</span>
              <span className="sub">2 promo ready · 1 demo risk</span>
            </div>
            <span className="badge">2</span>
            <span className="tooltip">Scorecard · KPI · promotion</span>
          </NavLink>
        </div>

        <div className="nav-section">
          <div className="nav-section-title">
            <span>Repositories{activeProduct ? ` · ${activeProduct.name}` : ''}</span>
            <NavLink to="/products" className="add-btn" title="Connect repo">+</NavLink>
          </div>
          {activeProductRepos.length === 0 ? (
            <div className="repo-row" style={{ color: 'var(--text-tertiary)', cursor: 'default' }}>
              <span className="name" style={{ fontStyle: 'italic' }}>No repos yet</span>
            </div>
          ) : (
            activeProductRepos.map((r, i) => (
              <div key={r.id} className={`repo-row${i === 0 ? ' active' : ''}`} title={r.remoteUrl}>
                <span className="dot"></span>
                <span className="name">{r.displayName}</span>
                <span className="src">{r.kind === 'ado' ? 'ADO' : 'GH'}</span>
              </div>
            ))
          )}
        </div>

        <div style={{ padding: '0 12px' }}>
          <div className="cost-meter">
            <div className="cost-meter-row">
              <span className="label">Today's spend</span>
              <span className="val">$4.81 / $7.50</span>
            </div>
            <div className="cost-meter-bar">
              <div className="cost-meter-fill"></div>
            </div>
          </div>
        </div>

      </div>

      <div className="sidebar-foot">
        <div className="sidebar-foot-row">
          <button
            ref={userRef}
            type="button"
            className="sidebar-user"
            onClick={openMenu}
            title={isUnsigned ? 'Click to sign in' : (me.email ?? me.displayName)}
            style={{
              flex: 1,
              display: 'flex',
              alignItems: 'center',
              gap: 10,
              minWidth: 0,
              border: 'none',
              background: 'transparent',
              padding: 0,
              cursor: 'pointer',
              color: 'inherit',
              textAlign: 'left',
            }}
          >
            <div
              className="avatar"
              style={
                me.avatarUrl
                  ? { margin: 0, backgroundImage: `url(${me.avatarUrl})`, backgroundSize: 'cover' }
                  : { margin: 0 }
              }
            >
              {me.avatarUrl ? '' : me.initials}
            </div>
            <div style={{ minWidth: 0, flex: 1 }}>
              <div className="name">{me.displayName}</div>
              <div className="role">{roleLabel}{activeProduct ? ` · ${activeProduct.name}` : ''}</div>
            </div>
          </button>
          <NavLink to="/settings" className="icon-btn" title="Settings" style={{ width: 26, height: 26, display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}>
            <svg width="14" height="14" viewBox="0 0 14 14" fill="none"><circle cx="7" cy="7" r="1.6" stroke="currentColor" strokeWidth="1.2" /><path d="M7 1 v 1.6 M 7 11.4 v 1.6 M 1 7 h 1.6 M 11.4 7 h 1.6" stroke="currentColor" strokeWidth="1.1" strokeLinecap="round" /></svg>
          </NavLink>
        </div>
      </div>
      <UserMenu open={menuOpen} onClose={() => setMenuOpen(false)} anchor={menuAnchor} />
    </aside>
  );
}

function formatRole(role: string): string {
  switch (role) {
    case 'product-owner': return 'Product Owner';
    case 'tech-lead': return 'Tech lead';
    case 'arch': return 'Architect';
    case 'eng': return 'Engineer';
    case 'rev': return 'Reviewer';
    case 'qa': return 'QA';
    default: return role;
  }
}
